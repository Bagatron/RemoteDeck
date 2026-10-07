using System.Text;
using RemoteDeck.Plugin;

namespace RemoteDeck.Samples.Hello;

/// <summary>A pretend terminal that prints back whatever you type. Handy for trying split panes and broadcast.</summary>
internal sealed class EchoConnectionFactory : IConnectionFactory
{
    public string Type => "echo";

    public string DisplayName => "Echo (demo)";

    public IConnection Create(ConnectionDefinition definition, ICredentialBroker credentials)
    {
        return new EchoConnection(definition.Id);
    }
}

internal sealed class EchoConnection : ITerminalConnection
{
    private readonly TerminalOutput _output = new();
    private ConnectionState _state = ConnectionState.Disconnected;

    public EchoConnection(string id)
    {
        Id = id;
    }

    public string Id { get; }

    public ConnectionState State => _state;

    public event EventHandler<ConnectionState>? StateChanged;

    public IObservable<ReadOnlyMemory<byte>> Output => _output;

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(ConnectionState.Connected);
        _output.Publish(Encoding.UTF8.GetBytes("Echo session ready. Type something.\r\n"));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(ConnectionState.Disconnected);
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
    {
        if (_state != ConnectionState.Connected)
        {
            throw new InvalidOperationException("The echo session is not connected.");
        }

        // A real terminal turns Enter (\r) into a new line; do the same here.
        var text = Encoding.UTF8.GetString(input.Span).Replace("\r", "\r\n", StringComparison.Ordinal);
        _output.Publish(Encoding.UTF8.GetBytes(text));
        return ValueTask.CompletedTask;
    }

    public void Resize(int columns, int rows)
    {
    }

    public ValueTask DisposeAsync()
    {
        _output.Complete();
        return ValueTask.CompletedTask;
    }

    private void SetState(ConnectionState state)
    {
        _state = state;
        StateChanged?.Invoke(this, state);
    }
}
