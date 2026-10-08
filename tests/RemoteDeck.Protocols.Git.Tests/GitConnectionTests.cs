using System.Text;
using System.Threading.Channels;
using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Git.Tests;

public class GitConnectionTests
{
    private sealed class PipeStream : Stream
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private byte[] _pending = Array.Empty<byte>();
        private int _offset;

        public List<byte> Written { get; } = new();

        public void Feed(string text) => _incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

        public void End() => _incoming.Writer.TryComplete();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

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
            lock (Written)
            {
                Written.AddRange(buffer.AsSpan(offset, count).ToArray());
            }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class FakeLink : IShellLink
    {
        public PipeStream In { get; } = new();
        public PipeStream Out { get; } = new();
        public (int, int)? LastResize { get; private set; }
        public bool Disposed { get; private set; }
        public Stream Input => In;
        public Stream Output => Out;
        public void Resize(int columns, int rows) => LastResize = (columns, rows);
        public void Dispose() { Disposed = true; Out.End(); }
    }

    private sealed class FakeFactory : IShellLinkFactory
    {
        public FakeLink Link { get; } = new();
        public ShellLaunch? Launch { get; private set; }

        public IShellLink Start(ShellLaunch launch)
        {
            Launch = launch;
            return Link;
        }
    }

    private sealed class Collector : IObserver<ReadOnlyMemory<byte>>
    {
        private readonly StringBuilder _text = new();
        public string Text { get { lock (_text) { return _text.ToString(); } } }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(ReadOnlyMemory<byte> value) { lock (_text) { _text.Append(Encoding.UTF8.GetString(value.Span)); } }
    }

    private static ShellLocator BashLocator() => new(
        f => f.EndsWith("bash.exe", StringComparison.Ordinal),
        k => k == "ProgramFiles" ? "PF" : null,
        Array.Empty<string>());

    private static GitConnection Make(FakeFactory factory, Dictionary<string, string>? options = null, bool folderExists = true) =>
        new(new ConnectionDefinition("1", "repo", "git", @"C:\r", Options: options), factory, BashLocator(), _ => folderExists);

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task Connect_starts_the_shell_in_the_folder()
    {
        var factory = new FakeFactory();
        await using var connection = Make(factory);
        await connection.ConnectAsync();

        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Equal(@"C:\r", factory.Launch!.Folder);
        Assert.Equal("Git Bash", factory.Launch.Shell.Description);
    }

    [Fact]
    public async Task Output_from_the_shell_reaches_subscribers_unchanged()
    {
        var factory = new FakeFactory();
        await using var connection = Make(factory);
        var collector = new Collector();
        using var _ = connection.Output.Subscribe(collector);
        await connection.ConnectAsync();

        factory.Link.Out.Feed("\u001b[32mmain\u001b[0m $ ");
        await Until(() => collector.Text.Contains("main"));
        Assert.Equal("\u001b[32mmain\u001b[0m $ ", collector.Text);
    }

    [Fact]
    public async Task Input_is_written_to_the_shell()
    {
        var factory = new FakeFactory();
        await using var connection = Make(factory);
        await connection.ConnectAsync();

        await connection.WriteAsync(Encoding.UTF8.GetBytes("ls\r"));
        Assert.Equal("ls\r", Encoding.UTF8.GetString(factory.Link.In.Written.ToArray()));
    }

    [Fact]
    public async Task The_startup_command_is_typed_with_a_return()
    {
        var factory = new FakeFactory();
        await using var connection = Make(factory, new() { ["startup"] = "git status" });
        await connection.ConnectAsync();

        Assert.Equal("git status\r", Encoding.UTF8.GetString(factory.Link.In.Written.ToArray()));
    }

    [Fact]
    public async Task A_missing_folder_fails_with_a_clear_message()
    {
        var factory = new FakeFactory();
        await using var connection = Make(factory, folderExists: false);

        var ex = await Assert.ThrowsAsync<GitShellException>(async () => await connection.ConnectAsync());
        Assert.Contains(@"C:\r", ex.Message);
        Assert.Equal(ConnectionState.Failed, connection.State);
        Assert.Null(factory.Launch);
    }

    [Fact]
    public async Task The_size_set_before_connecting_is_used_to_start()
    {
        var factory = new FakeFactory();
        await using var connection = Make(factory);
        connection.Resize(100, 40);
        await connection.ConnectAsync();

        Assert.Equal(100, factory.Launch!.Columns);
        Assert.Equal(40, factory.Launch.Rows);
    }

    [Fact]
    public async Task Resize_while_running_reaches_the_shell()
    {
        var factory = new FakeFactory();
        await using var connection = Make(factory);
        await connection.ConnectAsync();
        connection.Resize(80, 24);

        Assert.Equal((80, 24), factory.Link.LastResize);
    }

    [Fact]
    public async Task When_the_shell_exits_the_connection_becomes_disconnected()
    {
        var factory = new FakeFactory();
        await using var connection = Make(factory);
        await connection.ConnectAsync();

        factory.Link.Out.End();
        await Until(() => connection.State == ConnectionState.Disconnected);
        Assert.True(factory.Link.Disposed);
    }

    [Fact]
    public async Task Disconnect_closes_the_shell()
    {
        var factory = new FakeFactory();
        await using var connection = Make(factory);
        await connection.ConnectAsync();

        await connection.DisconnectAsync();
        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.True(factory.Link.Disposed);
    }

    [Fact]
    public async Task Writing_before_connecting_is_an_error()
    {
        await using var connection = Make(new FakeFactory());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await connection.WriteAsync(new byte[] { 1 }));
    }
}
