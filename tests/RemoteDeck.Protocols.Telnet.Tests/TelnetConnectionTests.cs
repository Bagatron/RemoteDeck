using System.Net;
using System.Net.Sockets;
using System.Text;
using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Telnet.Tests;

public class TelnetConnectionTests
{
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

    private static ConnectionDefinition Definition(int port, Dictionary<string, string>? options = null) =>
        new("t1", "Switch", "telnet", "127.0.0.1", port, Options: options);

    private static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

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
    public async Task Text_from_the_server_reaches_the_terminal()
    {
        using var listener = Listen();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var factory = new TelnetConnectionFactory();
        await using var connection = (ITerminalConnection)factory.Create(Definition(port), null!);
        var seen = new Collector();
        connection.Output.Subscribe(seen);

        await connection.ConnectAsync();
        using var server = await listener.AcceptTcpClientAsync();
        await server.GetStream().WriteAsync(Encoding.ASCII.GetBytes("Welcome\r\nlogin: "));

        Assert.True(await WaitFor(() => seen.Text == "Welcome\r\nlogin: "));
        Assert.Equal(ConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task Keystrokes_reach_the_server()
    {
        using var listener = Listen();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var connection = (ITerminalConnection)new TelnetConnectionFactory().Create(Definition(port), null!);

        await connection.ConnectAsync();
        using var server = await listener.AcceptTcpClientAsync();
        await connection.WriteAsync(Encoding.ASCII.GetBytes("admin\r"));

        var buffer = new byte[16];
        var total = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (total < 7)
        {
            total += await server.GetStream().ReadAsync(buffer.AsMemory(total), timeout.Token);
        }

        Assert.Equal(new byte[] { 97, 100, 109, 105, 110, 13, 0 }, buffer[..7]);
    }

    [Fact]
    public async Task Negotiation_is_answered_and_not_shown()
    {
        using var listener = Listen();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var connection = (ITerminalConnection)new TelnetConnectionFactory().Create(Definition(port), null!);
        var seen = new Collector();
        connection.Output.Subscribe(seen);
        connection.Resize(100, 30);

        await connection.ConnectAsync();
        using var server = await listener.AcceptTcpClientAsync();
        await server.GetStream().WriteAsync(new byte[] { 255, 251, 1, 255, 253, 31, 72, 105 });

        // Expect: IAC DO ECHO, IAC WILL NAWS, then the size frame.
        var expected = new byte[] { 255, 253, 1, 255, 251, 31, 255, 250, 31, 0, 100, 0, 30, 255, 240 };
        var reply = new byte[expected.Length];
        var total = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (total < reply.Length)
        {
            total += await server.GetStream().ReadAsync(reply.AsMemory(total), timeout.Token);
        }

        Assert.Equal(expected, reply);
        Assert.True(await WaitFor(() => seen.Text == "Hi"));
    }

    [Fact]
    public async Task Local_echo_shows_what_is_typed()
    {
        using var listener = Listen();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var connection = (ITerminalConnection)new TelnetConnectionFactory()
            .Create(Definition(port, new() { ["localEcho"] = "true" }), null!);
        var seen = new Collector();
        connection.Output.Subscribe(seen);

        await connection.ConnectAsync();
        using var server = await listener.AcceptTcpClientAsync();
        await connection.WriteAsync(Encoding.ASCII.GetBytes("ls\r"));

        Assert.Equal("ls\r\n", seen.Text);
    }

    [Fact]
    public async Task Closing_from_the_server_ends_the_session()
    {
        using var listener = Listen();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var connection = (ITerminalConnection)new TelnetConnectionFactory().Create(Definition(port), null!);

        await connection.ConnectAsync();
        var server = await listener.AcceptTcpClientAsync();
        server.Dispose();

        Assert.True(await WaitFor(() => connection.State == ConnectionState.Disconnected));
    }

    [Fact]
    public async Task An_unreachable_port_gives_a_clear_error()
    {
        var listener = Listen();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var connection = (ITerminalConnection)new TelnetConnectionFactory().Create(Definition(port), null!);

        var error = await Assert.ThrowsAsync<TelnetConnectionException>(async () => await connection.ConnectAsync());

        Assert.Contains("Could not connect", error.Message);
        Assert.Equal(ConnectionState.Failed, connection.State);
    }

    [Fact]
    public async Task Writing_before_connecting_is_an_error()
    {
        await using var connection = (ITerminalConnection)new TelnetConnectionFactory().Create(Definition(23), null!);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await connection.WriteAsync(new byte[] { 1 }));
    }

    [Fact]
    public void Factory_describes_itself()
    {
        var factory = new TelnetConnectionFactory();

        Assert.Equal("telnet", factory.Type);
        Assert.Equal("Telnet", factory.DisplayName);
    }
}
