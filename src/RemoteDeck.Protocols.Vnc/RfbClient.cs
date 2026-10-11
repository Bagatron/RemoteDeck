using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteDeck.Protocols.Vnc;

/// <summary>A rectangle of the remote screen, in pixels.</summary>
public readonly record struct RfbRect(int X, int Y, int Width, int Height);

/// <summary>
/// A VNC (RFB) client over any stream. <see cref="Handshake"/> signs in, then <see cref="Run"/> reads the server's
/// messages until the connection ends; both block, so call them from a worker thread and close the client to stop
/// them. The screen is kept as 32-bit pixels in the order blue, green, red, unused. It understands the Raw,
/// CopyRect, RRE and Hextile encodings (every server offers at least Raw and Hextile) and signs in with no
/// password or the classic VNC password.
/// </summary>
public sealed class RfbClient : IDisposable
{
    private const int EncodingRaw = 0;
    private const int EncodingCopyRect = 1;
    private const int EncodingRre = 2;
    private const int EncodingHextile = 5;
    private const int EncodingDesktopSize = -223;

    private readonly Stream _stream;
    private readonly RfbReader _reader;
    private readonly object _sendGate = new();
    private readonly object _frameGate = new();

    private byte[] _back = Array.Empty<byte>();
    private byte[] _front = Array.Empty<byte>();
    private int _dirtyLeft;
    private int _dirtyTop;
    private int _dirtyRight;
    private int _dirtyBottom;
    private bool _disposed;

    public RfbClient(Stream stream)
    {
        _stream = stream;
        _reader = new RfbReader(stream);
    }

    /// <summary>Width of the remote screen; known after <see cref="Handshake"/>.</summary>
    public int Width { get; private set; }

    /// <summary>Height of the remote screen; known after <see cref="Handshake"/>.</summary>
    public int Height { get; private set; }

    /// <summary>The name the server gives its desktop, often the computer name.</summary>
    public string DesktopName { get; private set; } = string.Empty;

    /// <summary>Raised (on the reading thread) when part of the screen has changed. Read it with <see cref="CopyRegion"/>.</summary>
    public event Action<RfbRect>? Updated;

    /// <summary>Raised when the remote screen changed size; <see cref="Width"/> and <see cref="Height"/> hold the new size.</summary>
    public event Action? Resized;

    /// <summary>Raised when the server copied text to its clipboard.</summary>
    public event Action<string>? ClipboardReceived;

    /// <summary>Raised when the server rings its bell.</summary>
    public event Action? Bell;

    /// <summary>
    /// Agrees a protocol version, signs in and learns the screen size. Throws <see cref="VncPasswordRequiredException"/>
    /// when the server wants a password and <paramref name="password"/> is empty, and
    /// <see cref="VncAuthenticationException"/> when it refuses the one given.
    /// </summary>
    public void Handshake(string? password)
    {
        var banner = Encoding.ASCII.GetString(_reader.ReadBytes(12));
        if (!banner.StartsWith("RFB ", StringComparison.Ordinal) || banner[7] != '.' || banner[11] != '\n'
            || !int.TryParse(banner.AsSpan(4, 3), out var major) || !int.TryParse(banner.AsSpan(8, 3), out var minor))
        {
            throw new RfbException("That is not a VNC server (it did not start with the RFB greeting).");
        }

        if (major < 3)
        {
            throw new RfbException("This VNC server is too old.");
        }

        // Speak the newest version we know that the server also knows: 3.8, 3.7 or 3.3.
        var version = major > 3 || minor >= 8 ? 8 : minor == 7 ? 7 : 3;
        Send(Encoding.ASCII.GetBytes($"RFB 003.00{version}\n"));

        byte security;
        if (version == 3)
        {
            var offered = _reader.ReadU32();
            if (offered == 0)
            {
                throw new RfbException("The VNC server refused the connection: " + ReadReason());
            }

            security = (byte)Math.Min(offered, 255);
        }
        else
        {
            var count = _reader.ReadByte();
            if (count == 0)
            {
                throw new RfbException("The VNC server refused the connection: " + ReadReason());
            }

            var types = _reader.ReadBytes(count);
            if (types.Contains((byte)1))
            {
                security = 1;
            }
            else if (types.Contains((byte)2))
            {
                security = 2;
            }
            else
            {
                throw new VncUnsupportedException(
                    "This VNC server only offers sign-in methods this viewer does not have (" + string.Join(", ", types) + "). Tick \"Open in an external VNC viewer\" on the connection to use another viewer.");
            }

            Send(new[] { security });
        }

        if (security == 2)
        {
            if (string.IsNullOrEmpty(password))
            {
                throw new VncPasswordRequiredException();
            }

            Send(VncAuth.Response(_reader.ReadBytes(16), password));
        }
        else if (security != 1)
        {
            throw new VncUnsupportedException(
                $"This VNC server asks for a sign-in method this viewer does not have (type {security}). Tick \"Open in an external VNC viewer\" on the connection to use another viewer.");
        }

        // None needs no answer before version 3.8; the password check always has one.
        if (security == 2 || version == 8)
        {
            if (_reader.ReadU32() != 0)
            {
                throw new VncAuthenticationException(version == 8 ? ReadReason() : "The VNC server did not accept the password.");
            }
        }

        Send(new byte[] { 1 }); // ClientInit: share the desktop with any other viewer.

        var width = _reader.ReadU16();
        var height = _reader.ReadU16();
        _reader.Skip(16); // The server's pixel format; we ask for our own below.
        var nameLength = _reader.ReadU32();
        if (nameLength > 4096)
        {
            throw new RfbException("The VNC server sent a desktop name that is too long.");
        }

        DesktopName = Encoding.UTF8.GetString(_reader.ReadBytes((int)nameLength));
        Allocate(width, height);

        // 32 bits per pixel, true colour, little endian, red in bits 16-23, green in 8-15, blue in 0-7.
        var format = new byte[20];
        format[0] = 0; // SetPixelFormat
        format[4] = 32;
        format[5] = 24;
        format[6] = 0;
        format[7] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(format.AsSpan(8), 255);
        BinaryPrimitives.WriteUInt16BigEndian(format.AsSpan(10), 255);
        BinaryPrimitives.WriteUInt16BigEndian(format.AsSpan(12), 255);
        format[14] = 16;
        format[15] = 8;
        format[16] = 0;
        Send(format);

        int[] encodings = { EncodingHextile, EncodingCopyRect, EncodingRre, EncodingRaw, EncodingDesktopSize };
        var request = new byte[4 + (encodings.Length * 4)];
        request[0] = 2; // SetEncodings
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), (ushort)encodings.Length);
        for (var i = 0; i < encodings.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(4 + (i * 4)), encodings[i]);
        }

        Send(request);
        RequestUpdate(incremental: false);
    }

    /// <summary>Reads and applies the server's messages until the connection ends, which throws.</summary>
    public void Run()
    {
        while (true)
        {
            var type = _reader.ReadByte();
            switch (type)
            {
                case 0:
                    ReadFramebufferUpdate();
                    break;
                case 1:
                    ReadColourMap();
                    break;
                case 2:
                    Bell?.Invoke();
                    break;
                case 3:
                    ReadServerCutText();
                    break;
                default:
                    throw new RfbException($"The VNC server sent a message this viewer does not know (type {type}).");
            }
        }
    }

    /// <summary>Asks the server for the screen (all of it, or only what changed since the last time).</summary>
    public void RequestUpdate(bool incremental)
    {
        var message = new byte[10];
        message[0] = 3;
        message[1] = incremental ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(6), (ushort)Width);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(8), (ushort)Height);
        Send(message);
    }

    /// <summary>Moves the mouse and sets which buttons are down (bit 0 left, 1 middle, 2 right, 3 wheel up, 4 wheel down).</summary>
    public void SendPointer(int x, int y, int buttons)
    {
        var message = new byte[6];
        message[0] = 5;
        message[1] = (byte)buttons;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)Math.Clamp(x, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), (ushort)Math.Clamp(y, 0, ushort.MaxValue));
        Send(message);
    }

    /// <summary>Presses or releases a key, named by its X11 key symbol (see <see cref="KeySyms"/>).</summary>
    public void SendKey(uint keySym, bool down)
    {
        var message = new byte[8];
        message[0] = 4;
        message[1] = down ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), keySym);
        Send(message);
    }

    /// <summary>Puts text on the server's clipboard. The protocol carries Latin-1 only, so other characters become '?'.</summary>
    public void SendClipboard(string text)
    {
        var data = Encoding.Latin1.GetBytes(text);
        var message = new byte[8 + data.Length];
        message[0] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), (uint)data.Length);
        data.CopyTo(message, 8);
        Send(message);
    }

    /// <summary>
    /// Copies part of the screen into <paramref name="destination"/> as tightly packed rows (width x 4 bytes each).
    /// The area is clipped to the current screen size. Returns the area actually copied.
    /// </summary>
    public RfbRect CopyRegion(RfbRect area, byte[] destination)
    {
        lock (_frameGate)
        {
            var left = Math.Clamp(area.X, 0, Width);
            var top = Math.Clamp(area.Y, 0, Height);
            var right = Math.Clamp(area.X + area.Width, 0, Width);
            var bottom = Math.Clamp(area.Y + area.Height, 0, Height);
            var width = right - left;
            var height = bottom - top;
            if (width <= 0 || height <= 0 || destination.Length < width * height * 4)
            {
                return new RfbRect(left, top, 0, 0);
            }

            var stride = Width * 4;
            for (var row = 0; row < height; row++)
            {
                Buffer.BlockCopy(_front, ((top + row) * stride) + (left * 4), destination, row * width * 4, width * 4);
            }

            return new RfbRect(left, top, width, height);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _stream.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Already gone.
        }
    }

    private void Send(byte[] message)
    {
        lock (_sendGate)
        {
            if (_disposed)
            {
                return;
            }

            _stream.Write(message, 0, message.Length);
            _stream.Flush();
        }
    }

    private string ReadReason()
    {
        var length = _reader.ReadU32();
        return length > 4096 ? "(no reason given)" : Encoding.UTF8.GetString(_reader.ReadBytes((int)length));
    }

    private void Allocate(int width, int height)
    {
        lock (_frameGate)
        {
            Width = width;
            Height = height;
            _back = new byte[width * height * 4];
            _front = new byte[width * height * 4];
        }

        ResetDirty();
    }

    private void ResetDirty()
    {
        _dirtyLeft = int.MaxValue;
        _dirtyTop = int.MaxValue;
        _dirtyRight = 0;
        _dirtyBottom = 0;
    }

    private void MarkDirty(int x, int y, int width, int height)
    {
        _dirtyLeft = Math.Min(_dirtyLeft, x);
        _dirtyTop = Math.Min(_dirtyTop, y);
        _dirtyRight = Math.Max(_dirtyRight, x + width);
        _dirtyBottom = Math.Max(_dirtyBottom, y + height);
    }

    private void ReadFramebufferUpdate()
    {
        _reader.Skip(1);
        var count = _reader.ReadU16();
        var resized = false;
        for (var i = 0; i < count; i++)
        {
            var x = _reader.ReadU16();
            var y = _reader.ReadU16();
            var width = _reader.ReadU16();
            var height = _reader.ReadU16();
            var encoding = _reader.ReadI32();

            if (encoding == EncodingDesktopSize)
            {
                Allocate(width, height);
                resized = true;
                continue;
            }

            if (x + width > Width || y + height > Height)
            {
                throw new RfbException("The VNC server drew outside its own screen.");
            }

            switch (encoding)
            {
                case EncodingRaw:
                    ReadRaw(x, y, width, height);
                    break;
                case EncodingCopyRect:
                    ReadCopyRect(x, y, width, height);
                    break;
                case EncodingRre:
                    ReadRre(x, y, width, height);
                    break;
                case EncodingHextile:
                    ReadHextile(x, y, width, height);
                    break;
                default:
                    throw new VncUnsupportedException($"The VNC server used a way of sending the screen this viewer does not have (encoding {encoding}).");
            }

            MarkDirty(x, y, width, height);
        }

        if (resized)
        {
            Resized?.Invoke();
            RequestUpdate(incremental: false);
        }

        Publish();
        RequestUpdate(incremental: true);
    }

    /// <summary>Copies what changed from the working screen to the one the display reads, and tells the display.</summary>
    private void Publish()
    {
        if (_dirtyRight <= _dirtyLeft || _dirtyBottom <= _dirtyTop)
        {
            return;
        }

        var area = new RfbRect(_dirtyLeft, _dirtyTop, _dirtyRight - _dirtyLeft, _dirtyBottom - _dirtyTop);
        lock (_frameGate)
        {
            var stride = Width * 4;
            for (var row = area.Y; row < area.Y + area.Height; row++)
            {
                Buffer.BlockCopy(_back, (row * stride) + (area.X * 4), _front, (row * stride) + (area.X * 4), area.Width * 4);
            }
        }

        ResetDirty();
        Updated?.Invoke(area);
    }

    private void ReadRaw(int x, int y, int width, int height)
    {
        var stride = Width * 4;
        for (var row = 0; row < height; row++)
        {
            _reader.ReadInto(_back, ((y + row) * stride) + (x * 4), width * 4);
        }
    }

    private void ReadCopyRect(int x, int y, int width, int height)
    {
        var sourceX = _reader.ReadU16();
        var sourceY = _reader.ReadU16();
        if (sourceX + width > Width || sourceY + height > Height)
        {
            throw new RfbException("The VNC server copied from outside its own screen.");
        }

        // Through a copy, because the two areas may overlap.
        var stride = Width * 4;
        var temporary = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(_back, ((sourceY + row) * stride) + (sourceX * 4), temporary, row * width * 4, width * 4);
        }

        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(temporary, row * width * 4, _back, ((y + row) * stride) + (x * 4), width * 4);
        }
    }

    private void ReadRre(int x, int y, int width, int height)
    {
        var subRectangles = _reader.ReadU32();
        var background = ReadPixel();
        Fill(x, y, width, height, background);
        for (long i = 0; i < subRectangles; i++)
        {
            var color = ReadPixel();
            var subX = _reader.ReadU16();
            var subY = _reader.ReadU16();
            var subWidth = _reader.ReadU16();
            var subHeight = _reader.ReadU16();
            if (subX + subWidth > width || subY + subHeight > height)
            {
                throw new RfbException("The VNC server drew outside a rectangle it sent.");
            }

            Fill(x + subX, y + subY, subWidth, subHeight, color);
        }
    }

    private void ReadHextile(int x, int y, int width, int height)
    {
        const int raw = 1;
        const int backgroundSpecified = 2;
        const int foregroundSpecified = 4;
        const int anySubRectangles = 8;
        const int subRectanglesColoured = 16;

        uint background = 0;
        uint foreground = 0;
        for (var tileY = 0; tileY < height; tileY += 16)
        {
            var tileHeight = Math.Min(16, height - tileY);
            for (var tileX = 0; tileX < width; tileX += 16)
            {
                var tileWidth = Math.Min(16, width - tileX);
                var left = x + tileX;
                var top = y + tileY;
                var flags = _reader.ReadByte();

                if ((flags & raw) != 0)
                {
                    ReadRaw(left, top, tileWidth, tileHeight);
                    continue;
                }

                if ((flags & backgroundSpecified) != 0)
                {
                    background = ReadPixel();
                }

                if ((flags & foregroundSpecified) != 0)
                {
                    foreground = ReadPixel();
                }

                Fill(left, top, tileWidth, tileHeight, background);
                if ((flags & anySubRectangles) == 0)
                {
                    continue;
                }

                var count = _reader.ReadByte();
                for (var i = 0; i < count; i++)
                {
                    var color = (flags & subRectanglesColoured) != 0 ? ReadPixel() : foreground;
                    var position = _reader.ReadByte();
                    var size = _reader.ReadByte();
                    var subX = position >> 4;
                    var subY = position & 15;
                    var subWidth = (size >> 4) + 1;
                    var subHeight = (size & 15) + 1;
                    if (subX + subWidth > tileWidth || subY + subHeight > tileHeight)
                    {
                        throw new RfbException("The VNC server drew outside a tile it sent.");
                    }

                    Fill(left + subX, top + subY, subWidth, subHeight, color);
                }
            }
        }
    }

    private uint ReadPixel()
    {
        // The four bytes arrive as blue, green, red, unused, which is the order we store them in.
        var low = _reader.ReadByte();
        var mid = _reader.ReadByte();
        var high = _reader.ReadByte();
        var top = _reader.ReadByte();
        return low | ((uint)mid << 8) | ((uint)high << 16) | ((uint)top << 24);
    }

    private void Fill(int x, int y, int width, int height, uint color)
    {
        var stride = Width * 4;
        for (var row = 0; row < height; row++)
        {
            MemoryMarshal.Cast<byte, uint>(_back.AsSpan(((y + row) * stride) + (x * 4), width * 4)).Fill(color);
        }
    }

    private void ReadColourMap()
    {
        _reader.Skip(1);
        _reader.Skip(2);
        var count = _reader.ReadU16();
        _reader.Skip(count * 6);
    }

    private void ReadServerCutText()
    {
        _reader.Skip(3);
        var length = _reader.ReadU32();
        if (length > 1024 * 1024)
        {
            _reader.Skip((int)Math.Min(length, int.MaxValue));
            return;
        }

        var text = Encoding.Latin1.GetString(_reader.ReadBytes((int)length));
        ClipboardReceived?.Invoke(text);
    }
}
