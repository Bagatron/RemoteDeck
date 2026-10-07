namespace RemoteDeck.Plugin;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Failed,
}

/// <summary>
/// A single live session (an SSH shell, an RDP window, a web tab, ...).
/// Implementations are created by an <see cref="IConnectionFactory"/>.
/// </summary>
public interface IConnection : IAsyncDisposable
{
    string Id { get; }

    ConnectionState State { get; }

    event EventHandler<ConnectionState>? StateChanged;

    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A connection that behaves like a terminal: bytes in, bytes out.
/// Anything implementing this gets split panes and broadcast input from the host for free.
/// </summary>
public interface ITerminalConnection : IConnection
{
    /// <summary>Raw bytes produced by the remote side (including escape sequences).</summary>
    IObservable<ReadOnlyMemory<byte>> Output { get; }

    /// <summary>Send bytes to the remote side, exactly as a keyboard would.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default);

    void Resize(int columns, int rows);
}

/// <summary>What the user saved about a connection. Secrets are never stored here.</summary>
public sealed record ConnectionDefinition(
    string Id,
    string Name,
    string Type,
    string Host,
    int? Port = null,
    string? CredentialId = null,
    IReadOnlyDictionary<string, string>? Options = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>Creates connections of one type (for example "ssh" or "rdp").</summary>
public interface IConnectionFactory
{
    /// <summary>Stable identifier stored in saved connections, e.g. "ssh".</summary>
    string Type { get; }

    string DisplayName { get; }

    IConnection Create(ConnectionDefinition definition, ICredentialBroker credentials);
}

public interface IConnectionTypeRegistry
{
    void Register(IConnectionFactory factory);
}

/// <summary>
/// Small helper so plugin authors do not need System.Reactive just to expose <see cref="ITerminalConnection.Output"/>.
/// </summary>
public sealed class TerminalOutput : IObservable<ReadOnlyMemory<byte>>
{
    private readonly object _gate = new();
    private List<IObserver<ReadOnlyMemory<byte>>> _observers = new();
    private bool _completed;

    public IDisposable Subscribe(IObserver<ReadOnlyMemory<byte>> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        bool alreadyCompleted;
        lock (_gate)
        {
            alreadyCompleted = _completed;
            if (!alreadyCompleted)
            {
                _observers = new List<IObserver<ReadOnlyMemory<byte>>>(_observers) { observer };
            }
        }

        if (alreadyCompleted)
        {
            observer.OnCompleted();
            return EmptyDisposable.Instance;
        }

        return new Subscription(this, observer);
    }

    public void Publish(ReadOnlyMemory<byte> data)
    {
        List<IObserver<ReadOnlyMemory<byte>>> snapshot;
        lock (_gate)
        {
            snapshot = _observers;
        }

        foreach (var observer in snapshot)
        {
            observer.OnNext(data);
        }
    }

    public void Complete()
    {
        List<IObserver<ReadOnlyMemory<byte>>> snapshot;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            snapshot = _observers;
            _observers = new List<IObserver<ReadOnlyMemory<byte>>>();
        }

        foreach (var observer in snapshot)
        {
            observer.OnCompleted();
        }
    }

    private void Unsubscribe(IObserver<ReadOnlyMemory<byte>> observer)
    {
        lock (_gate)
        {
            var copy = new List<IObserver<ReadOnlyMemory<byte>>>(_observers);
            copy.Remove(observer);
            _observers = copy;
        }
    }

    private sealed class Subscription : IDisposable
    {
        private TerminalOutput? _owner;
        private readonly IObserver<ReadOnlyMemory<byte>> _observer;

        public Subscription(TerminalOutput owner, IObserver<ReadOnlyMemory<byte>> observer)
        {
            _owner = owner;
            _observer = observer;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Unsubscribe(_observer);
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
