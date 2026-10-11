using System.Text;

namespace RemoteDeck.Protocols.Vnc.Tests;

/// <summary>A stream that plays back what a server would send and records what the client writes.</summary>
internal sealed class ScriptedStream : Stream
{
    private readonly MemoryStream _server;

    public ScriptedStream(byte[] serverBytes) => _server = new MemoryStream(serverBytes);

    public MemoryStream Written { get; } = new();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _server.Read(buffer, offset, count);

    public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>Builds the bytes of a server's side of the conversation.</summary>
internal sealed class ServerScript
{
    private readonly List<byte> _bytes = new();

    public ServerScript Bytes(params byte[] bytes)
    {
        _bytes.AddRange(bytes);
        return this;
    }

    public ServerScript Text(string text) => Bytes(Encoding.ASCII.GetBytes(text));

    public ServerScript U16(int value) => Bytes((byte)(value >> 8), (byte)value);

    public ServerScript U32(uint value) => Bytes((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);

    public ServerScript I32(int value) => U32(unchecked((uint)value));

    /// <summary>A pixel as the client asked for it: blue, green, red, unused.</summary>
    public ServerScript Pixel(byte red, byte green, byte blue) => Bytes(blue, green, red, 0);

    public ServerScript ServerInit(int width, int height, string name) =>
        U16(width).U16(height).Bytes(new byte[16]).U32((uint)name.Length).Text(name);

    public ServerScript Update(int rectangles) => Bytes(0, 0).U16(rectangles);

    public ServerScript Rect(int x, int y, int width, int height, int encoding) =>
        U16(x).U16(y).U16(width).U16(height).I32(encoding);

    public byte[] ToArray() => _bytes.ToArray();
}

public sealed class RfbClientTests
{
    private static RfbClient Connect(ServerScript script, out ScriptedStream stream, string? password = null)
    {
        stream = new ScriptedStream(script.ToArray());
        var client = new RfbClient(stream);
        client.Handshake(password);
        return client;
    }

    /// <summary>A 3.8 server with no password and a screen of the given size.</summary>
    private static ServerScript Open(int width, int height) =>
        new ServerScript().Text("RFB 003.008\n").Bytes(1, 1).U32(0).ServerInit(width, height, "lab");

    private static (RfbClient Client, RfbRect Area, byte[] Pixels) RunToEnd(ServerScript script)
    {
        var client = Connect(script, out _);
        RfbRect area = default;
        client.Updated += a => area = a;
        Assert.Throws<EndOfStreamException>(client.Run);
        var pixels = new byte[client.Width * client.Height * 4];
        client.CopyRegion(new RfbRect(0, 0, client.Width, client.Height), pixels);
        return (client, area, pixels);
    }

    private static byte[] PixelAt(byte[] pixels, int width, int x, int y) => pixels[(((y * width) + x) * 4)..(((y * width) + x) * 4 + 3)];

    [Fact]
    public void Handshake_WithNoPassword_ReadsTheScreen()
    {
        var client = Connect(Open(640, 480), out var stream);

        Assert.Equal(640, client.Width);
        Assert.Equal(480, client.Height);
        Assert.Equal("lab", client.DesktopName);

        var sent = stream.Written.ToArray();
        Assert.Equal("RFB 003.008\n", Encoding.ASCII.GetString(sent, 0, 12));
        Assert.Equal(1, sent[12]); // chose "none"
        Assert.Equal(1, sent[13]); // shared desktop
    }

    [Fact]
    public void Handshake_AsksForOurPixelFormatAndEncodings()
    {
        Connect(Open(10, 10), out var stream);

        var sent = stream.Written.ToArray();
        var setFormat = sent.AsSpan(14, 20);
        Assert.Equal(0, setFormat[0]);
        Assert.Equal(32, setFormat[4]); // bits per pixel
        Assert.Equal(1, setFormat[7]);  // true colour
        Assert.Equal(16, setFormat[14]); // red shift
        Assert.Equal(0, setFormat[16]);  // blue shift
        Assert.Equal(2, sent[34]);       // SetEncodings follows
        Assert.Equal(3, sent[34 + 4 + 20]); // then a request for the whole screen
        Assert.Equal(0, sent[34 + 4 + 20 + 1]);
    }

    [Fact]
    public void Handshake_UsesTheOldProtocolForOldServers()
    {
        var script = new ServerScript().Text("RFB 003.003\n").U32(1).ServerInit(4, 4, "old");

        var client = Connect(script, out var stream);

        Assert.Equal("RFB 003.003\n", Encoding.ASCII.GetString(stream.Written.ToArray(), 0, 12));
        Assert.Equal(4, client.Width);
    }

    [Fact]
    public void Handshake_TreatsAnAppleStyleVersionAs38()
    {
        var script = new ServerScript().Text("RFB 003.889\n").Bytes(1, 1).U32(0).ServerInit(4, 4, "mac");

        Connect(script, out var stream);

        Assert.Equal("RFB 003.008\n", Encoding.ASCII.GetString(stream.Written.ToArray(), 0, 12));
    }

    [Fact]
    public void Handshake_SignsInWithAPassword()
    {
        var challenge = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var script = new ServerScript().Text("RFB 003.008\n").Bytes(1, 2).Bytes(challenge).U32(0).ServerInit(8, 8, "pw");

        Connect(script, out var stream, "password");

        var sent = stream.Written.ToArray();
        Assert.Equal(2, sent[12]); // chose the password check
        Assert.Equal("b866924125c8eebb9debc1db61c538e2", Convert.ToHexString(sent, 13, 16).ToLowerInvariant());
    }

    [Fact]
    public void Handshake_WithoutAPasswordAskingForOne_Throws()
    {
        var script = new ServerScript().Text("RFB 003.008\n").Bytes(1, 2).Bytes(new byte[16]);

        var stream = new ScriptedStream(script.ToArray());
        var client = new RfbClient(stream);

        Assert.Throws<VncPasswordRequiredException>(() => client.Handshake(null));
    }

    [Fact]
    public void Handshake_WithAWrongPassword_ReportsTheServersReason()
    {
        var script = new ServerScript().Text("RFB 003.008\n").Bytes(1, 2).Bytes(new byte[16]).U32(1).U32(12).Text("Bad password");

        var client = new RfbClient(new ScriptedStream(script.ToArray()));

        var error = Assert.Throws<VncAuthenticationException>(() => client.Handshake("nope"));
        Assert.Equal("Bad password", error.Message);
    }

    [Fact]
    public void Handshake_WithOnlyUnsupportedSignIn_SaysSo()
    {
        var script = new ServerScript().Text("RFB 003.008\n").Bytes(1, 30);

        var client = new RfbClient(new ScriptedStream(script.ToArray()));

        Assert.Throws<VncUnsupportedException>(() => client.Handshake("x"));
    }

    [Fact]
    public void Handshake_WhenTheServerRefuses_ShowsTheReason()
    {
        var script = new ServerScript().Text("RFB 003.008\n").Bytes(0).U32(4).Text("full");

        var client = new RfbClient(new ScriptedStream(script.ToArray()));

        var error = Assert.Throws<RfbException>(() => client.Handshake(null));
        Assert.Contains("full", error.Message);
    }

    [Fact]
    public void Handshake_RejectsSomethingThatIsNotVnc()
    {
        var client = new RfbClient(new ScriptedStream(Encoding.ASCII.GetBytes("HTTP/1.1 400 ")));

        Assert.Throws<RfbException>(() => client.Handshake(null));
    }

    [Fact]
    public void Raw_DrawsPixels()
    {
        var script = Open(4, 2)
            .Update(1).Rect(1, 0, 2, 2, 0)
            .Pixel(255, 0, 0).Pixel(0, 255, 0)
            .Pixel(0, 0, 255).Pixel(10, 20, 30);

        var (_, area, pixels) = RunToEnd(script);

        Assert.Equal(new RfbRect(1, 0, 2, 2), area);
        Assert.Equal(new byte[] { 0, 0, 255 }, PixelAt(pixels, 4, 1, 0)); // blue, green, red of a red pixel
        Assert.Equal(new byte[] { 0, 255, 0 }, PixelAt(pixels, 4, 2, 0));
        Assert.Equal(new byte[] { 255, 0, 0 }, PixelAt(pixels, 4, 1, 1));
        Assert.Equal(new byte[] { 30, 20, 10 }, PixelAt(pixels, 4, 2, 1));
        Assert.Equal(new byte[] { 0, 0, 0 }, PixelAt(pixels, 4, 0, 0)); // outside the rectangle
    }

    [Fact]
    public void CopyRect_MovesPixelsAroundOverlaps()
    {
        // Row of four distinct pixels, then copy the left three one place to the right.
        var script = Open(4, 1)
            .Update(2)
            .Rect(0, 0, 4, 1, 0).Pixel(1, 0, 0).Pixel(2, 0, 0).Pixel(3, 0, 0).Pixel(4, 0, 0)
            .Rect(1, 0, 3, 1, 1).U16(0).U16(0);

        var (_, _, pixels) = RunToEnd(script);

        Assert.Equal(1, PixelAt(pixels, 4, 0, 0)[2]);
        Assert.Equal(1, PixelAt(pixels, 4, 1, 0)[2]);
        Assert.Equal(2, PixelAt(pixels, 4, 2, 0)[2]);
        Assert.Equal(3, PixelAt(pixels, 4, 3, 0)[2]);
    }

    [Fact]
    public void Rre_FillsABackgroundAndSubRectangles()
    {
        var script = Open(4, 4)
            .Update(1).Rect(0, 0, 4, 4, 2)
            .U32(1).Pixel(0, 0, 100)
            .Pixel(200, 0, 0).U16(1).U16(1).U16(2).U16(2);

        var (_, _, pixels) = RunToEnd(script);

        Assert.Equal(new byte[] { 100, 0, 0 }, PixelAt(pixels, 4, 0, 0));
        Assert.Equal(new byte[] { 0, 0, 200 }, PixelAt(pixels, 4, 1, 1));
        Assert.Equal(new byte[] { 0, 0, 200 }, PixelAt(pixels, 4, 2, 2));
        Assert.Equal(new byte[] { 100, 0, 0 }, PixelAt(pixels, 4, 3, 3));
    }

    [Fact]
    public void Hextile_HandlesBackgroundForegroundAndSubRectangles()
    {
        // One 20 x 4 rectangle is two tiles: 16 wide and 4 wide.
        var script = Open(20, 4)
            .Update(1).Rect(0, 0, 20, 4, 5)
            // Tile 1: background and foreground given, one plain sub-rectangle at (2,1) of size 3 x 2.
            .Bytes(2 | 4 | 8).Pixel(0, 0, 50).Pixel(0, 90, 0).Bytes(1).Bytes(0x21, 0x21)
            // Tile 2: nothing given, so it reuses the background.
            .Bytes(0);

        var (_, _, pixels) = RunToEnd(script);

        Assert.Equal(new byte[] { 50, 0, 0 }, PixelAt(pixels, 20, 0, 0));
        Assert.Equal(new byte[] { 0, 90, 0 }, PixelAt(pixels, 20, 2, 1));
        Assert.Equal(new byte[] { 0, 90, 0 }, PixelAt(pixels, 20, 4, 2));
        Assert.Equal(new byte[] { 50, 0, 0 }, PixelAt(pixels, 20, 5, 1));
        Assert.Equal(new byte[] { 50, 0, 0 }, PixelAt(pixels, 20, 18, 3));
    }

    [Fact]
    public void Hextile_ColouredSubRectanglesAndRawTiles()
    {
        var script = Open(32, 2)
            .Update(1).Rect(0, 0, 32, 2, 5)
            // Tile 1: coloured sub-rectangles (flag 16) on a given background.
            .Bytes(2 | 8 | 16).Pixel(0, 0, 10).Bytes(1).Pixel(0, 0, 250).Bytes(0x00, 0x00)
            // Tile 2: raw, 16 x 2 pixels, all the same colour.
            .Bytes(1);
        for (var i = 0; i < 32; i++)
        {
            script.Pixel(7, 8, 9);
        }

        var (_, _, pixels) = RunToEnd(script);

        Assert.Equal(new byte[] { 250, 0, 0 }, PixelAt(pixels, 32, 0, 0));
        Assert.Equal(new byte[] { 10, 0, 0 }, PixelAt(pixels, 32, 1, 0));
        Assert.Equal(new byte[] { 9, 8, 7 }, PixelAt(pixels, 32, 20, 1));
    }

    [Fact]
    public void DesktopSize_ResizesTheScreen()
    {
        var script = Open(4, 4).Update(1).Rect(0, 0, 8, 6, -223);
        var client = Connect(script, out _);
        var resized = false;
        client.Resized += () => resized = true;

        Assert.Throws<EndOfStreamException>(client.Run);

        Assert.True(resized);
        Assert.Equal(8, client.Width);
        Assert.Equal(6, client.Height);
    }

    [Fact]
    public void ARectangleOutsideTheScreen_IsRefused()
    {
        var script = Open(4, 4).Update(1).Rect(3, 3, 4, 4, 0);
        var client = Connect(script, out _);

        Assert.Throws<RfbException>(client.Run);
    }

    [Fact]
    public void AnEncodingWeDidNotAskFor_IsReported()
    {
        var script = Open(4, 4).Update(1).Rect(0, 0, 1, 1, 16);
        var client = Connect(script, out _);

        Assert.Throws<VncUnsupportedException>(client.Run);
    }

    [Fact]
    public void ServerClipboardAndBell_AreReported()
    {
        var script = Open(4, 4).Bytes(2).Bytes(3, 0, 0, 0).U32(5).Text("hello");
        var client = Connect(script, out _);
        var bells = 0;
        string? text = null;
        client.Bell += () => bells++;
        client.ClipboardReceived += t => text = t;

        Assert.Throws<EndOfStreamException>(client.Run);

        Assert.Equal(1, bells);
        Assert.Equal("hello", text);
    }

    [Fact]
    public void Input_IsEncodedAsTheProtocolSays()
    {
        var client = Connect(Open(4, 4), out var stream);
        var before = stream.Written.Length;

        client.SendPointer(300, 5, 1);
        client.SendKey(0xFF0D, down: true);
        client.SendClipboard("hi");

        var sent = stream.Written.ToArray()[(int)before..];
        Assert.Equal(new byte[] { 5, 1, 1, 44, 0, 5 }, sent[..6]);
        Assert.Equal(new byte[] { 4, 1, 0, 0, 0, 0, 0xFF, 0x0D }, sent[6..14]);
        Assert.Equal(new byte[] { 6, 0, 0, 0, 0, 0, 0, 2, (byte)'h', (byte)'i' }, sent[14..]);
    }

    [Fact]
    public void CopyRegion_ClipsToTheScreen()
    {
        var client = Connect(Open(4, 4), out _);
        var buffer = new byte[4 * 4 * 4];

        var copied = client.CopyRegion(new RfbRect(2, 2, 10, 10), buffer);

        Assert.Equal(new RfbRect(2, 2, 2, 2), copied);
    }
}
