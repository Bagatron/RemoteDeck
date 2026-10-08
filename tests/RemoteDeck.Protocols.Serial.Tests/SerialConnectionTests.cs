using System.Text;
using System.Threading.Channels;
using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Serial.Tests;

public class SerialConnectionTests
{
    private sealed class FakeStream : Stream
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private readonly object _gate = new();
        private byte[] _pending = Array.Empty<byte>();
        private int _offset;

        public List<byte> Written { get; } = new();

        public void Feed(string text) => _incoming.Writer.TryWrite(Encoding.Latin1.GetBytes(text));

        public void Unplug() => _incoming.Writer.TryComplete();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= _pending.Length)
            {
                try
                {
                    _pending = await _incoming.Reader.ReadAsync(cancellationToken);
                    _offset = 0;
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }
            }

            var count = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                Written.AddRange(buffer.AsSpan(offset, count).ToArray());
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class FakeLink : ISerialLink
    {
        public FakeStream Fake { get; } = new();

        public bool Disposed { get; private set; }

        public Stream Stream => Fake;

        public void Dispose()
        {
            Disposed = true;
            Fake.Unplug();
        }
    }

    private sealed class FakeLinks : ISerialLinkFactory
    {
        public FakeLink Link { get; } = new();

        public SerialSettings? Opened { get; private set; }

        public Exception? FailWith { get; init; }

        public ISerialLink Open(SerialSettings settings)
        {
            Opened = settings;
            if (FailWith is not null)
            {
                throw FailWith;
            }

            return Link;
        }
    }

    private sealed class Collector : IObserver<ReadOnlyMemory<byte>>
    {
        private readonly object _gate = new();
        private readonly List<byte> _bytes = new();

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return Encoding.Latin1.GetString(_bytes.ToArray());
                }
            }
        }

        public void OnNext(ReadOnlyMemory<byte> value)
        {
            lock (_gate)
            {
                _bytes.AddRange(value.ToArray());
            }
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    private static ConnectionDefinition Definition(Dictionary<string, string>? options = null) =>
        new("s1", "Console", "serial", "COM7", Options: options);

    private static async Task<bool> WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    [Fact]
    public async Task Opens_the_port_with_the_saved_settings()
    {
        var links = new FakeLinks();
        await using var connection = new SerialConnection(
            Definition(new() { ["baud"] = "115200", ["format"] = "7E2", ["flow"] = "rtscts" }),
            links);

        await connection.ConnectAsync();

        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Equal("COM7", links.Opened!.PortName);
        Assert.Equal(115200, links.Opened.BaudRate);
        Assert.Equal(7, links.Opened.DataBits);
    }

    [Fact]
    public async Task Bare_line_feeds_start_a_new_line_at_the_left_edge()
    {
        var links = new FakeLinks();
        await using var connection = new SerialConnection(Definition(), links);
        var seen = new Collector();
        connection.Output.Subscribe(seen);
        await connection.ConnectAsync();

        links.Link.Fake.Feed("one\ntwo\r\nthree\n");

        Assert.True(await WaitFor(() => seen.Text == "one\r\ntwo\r\nthree\r\n"));
    }

    [Fact]
    public async Task A_cr_lf_split_across_reads_is_not_doubled()
    {
        var links = new FakeLinks();
        await using var connection = new SerialConnection(Definition(), links);
        var seen = new Collector();
        connection.Output.Subscribe(seen);
        await connection.ConnectAsync();

        links.Link.Fake.Feed("a\r");
        Assert.True(await WaitFor(() => seen.Text == "a\r"));
        links.Link.Fake.Feed("\nb");

        Assert.True(await WaitFor(() => seen.Text == "a\r\nb"));
    }

    [Fact]
    public async Task Line_feed_translation_can_be_turned_off()
    {
        var links = new FakeLinks();
        await using var connection = new SerialConnection(Definition(new() { ["translateLf"] = "false" }), links);
        var seen = new Collector();
        connection.Output.Subscribe(seen);
        await connection.ConnectAsync();

        links.Link.Fake.Feed("x\ny");

        Assert.True(await WaitFor(() => seen.Text == "x\ny"));
    }

    [Fact]
    public async Task Keystrokes_are_written_to_the_port()
    {
        var links = new FakeLinks();
        await using var connection = new SerialConnection(Definition(), links);
        await connection.ConnectAsync();

        await connection.WriteAsync(Encoding.ASCII.GetBytes("ls\r"));

        Assert.Equal(new byte[] { 108, 115, 13 }, links.Link.Fake.Written.ToArray());
    }

    [Fact]
    public async Task Unplugging_the_device_ends_the_session()
    {
        var links = new FakeLinks();
        await using var connection = new SerialConnection(Definition(), links);
        await connection.ConnectAsync();

        links.Link.Fake.Unplug();

        Assert.True(await WaitFor(() => connection.State == ConnectionState.Disconnected));
        Assert.True(links.Link.Disposed);
    }

    [Fact]
    public async Task Disconnecting_closes_the_port()
    {
        var links = new FakeLinks();
        await using var connection = new SerialConnection(Definition(), links);
        await connection.ConnectAsync();

        await connection.DisconnectAsync();

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.True(links.Link.Disposed);
    }

    [Fact]
    public async Task A_port_that_cannot_be_opened_fails_with_the_reason()
    {
        var links = new FakeLinks { FailWith = new SerialConnectionException("COM7 is in use by another program.") };
        await using var connection = new SerialConnection(Definition(), links);

        var error = await Assert.ThrowsAsync<SerialConnectionException>(async () => await connection.ConnectAsync());

        Assert.Contains("in use", error.Message);
        Assert.Equal(ConnectionState.Failed, connection.State);
    }

    [Fact]
    public async Task Bad_settings_fail_before_opening()
    {
        var links = new FakeLinks();
        await using var connection = new SerialConnection(Definition(new() { ["baud"] = "fast" }), links);

        await Assert.ThrowsAsync<SerialConnectionException>(async () => await connection.ConnectAsync());

        Assert.Null(links.Opened);
        Assert.Equal(ConnectionState.Failed, connection.State);
    }

    [Fact]
    public async Task Writing_before_connecting_is_an_error()
    {
        await using var connection = new SerialConnection(Definition(), new FakeLinks());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await connection.WriteAsync(new byte[] { 1 }));
    }

    [Fact]
    public void Factory_describes_itself()
    {
        var factory = new SerialConnectionFactory();

        Assert.Equal("serial", factory.Type);
        Assert.Equal("Serial port", factory.DisplayName);
    }
}
