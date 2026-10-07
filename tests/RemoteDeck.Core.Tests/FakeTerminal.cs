using System.Text;
using RemoteDeck.Plugin;

namespace RemoteDeck.Core.Tests;

/// <summary>A terminal that records what it was sent, and can be told to fail.</summary>
internal sealed class FakeTerminal : ITerminalConnection
{
    private readonly TerminalOutput _output = new();

    public FakeTerminal(string id)
    {
        Id = id;
    }

    public string Id { get; }

    public ConnectionState State { get; } = ConnectionState.Connected;

    public event EventHandler<ConnectionState>? StateChanged
    {
        add { }
        remove { }
    }

    public IObservable<ReadOnlyMemory<byte>> Output => _output;

    public bool ThrowOnWrite { get; set; }

    public List<string> Received { get; } = new();

    public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
    {
        if (ThrowOnWrite)
        {
            throw new IOException("connection lost");
        }

        Received.Add(Encoding.UTF8.GetString(input.Span));
        return ValueTask.CompletedTask;
    }

    public void Resize(int columns, int rows)
    {
    }

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
