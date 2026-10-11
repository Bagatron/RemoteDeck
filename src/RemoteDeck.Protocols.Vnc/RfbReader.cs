namespace RemoteDeck.Protocols.Vnc;

/// <summary>Reads the big-endian values of the RFB protocol from a stream, with a buffer so many small reads stay cheap.</summary>
internal sealed class RfbReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _position;
    private int _length;

    public RfbReader(Stream stream) => _stream = stream;

    public byte ReadByte()
    {
        if (_position == _length)
        {
            Fill();
        }

        return _buffer[_position++];
    }

    public int ReadU16() => (ReadByte() << 8) | ReadByte();

    public int ReadI32() => unchecked((int)ReadU32());

    public uint ReadU32() =>
        ((uint)ReadByte() << 24) | ((uint)ReadByte() << 16) | ((uint)ReadByte() << 8) | ReadByte();

    public byte[] ReadBytes(int count)
    {
        var result = new byte[count];
        ReadInto(result, 0, count);
        return result;
    }

    public void ReadInto(byte[] destination, int offset, int count)
    {
        while (count > 0)
        {
            if (_position == _length)
            {
                Fill();
            }

            var take = Math.Min(count, _length - _position);
            Buffer.BlockCopy(_buffer, _position, destination, offset, take);
            _position += take;
            offset += take;
            count -= take;
        }
    }

    public void Skip(int count)
    {
        while (count > 0)
        {
            if (_position == _length)
            {
                Fill();
            }

            var take = Math.Min(count, _length - _position);
            _position += take;
            count -= take;
        }
    }

    private void Fill()
    {
        var read = _stream.Read(_buffer, 0, _buffer.Length);
        if (read <= 0)
        {
            throw new EndOfStreamException("The VNC server closed the connection.");
        }

        _position = 0;
        _length = read;
    }
}
