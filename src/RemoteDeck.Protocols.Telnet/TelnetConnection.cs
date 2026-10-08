using System.Net.Sockets;
using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Telnet;

/// <summary>A problem opening a Telnet connection. The message is safe to show to the user.</summary>
public sealed class TelnetConnectionException : Exception
{
    public TelnetConnectionException(string message)
        : base(message)
    {
    }

    public TelnetConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// One Telnet terminal session. Like SSH it is an <see cref="ITerminalConnection"/>, so it gets split panes and
/// broadcast input for free. Telnet is not encrypted and has no passwords of its own: you type the login at the
/// remote prompt, and nothing is saved.
///
/// Options on the connection: <c>term</c> (terminal type, default xterm-256color), <c>connectTimeoutSeconds</c>
/// (default 10) and <c>localEcho</c> ("true" to show what you type yourself, for raw devices that do not echo).
/// </summary>
public sealed class TelnetConnection : ITerminalConnection
{
    public const int DefaultPort = 23;

    private readonly ConnectionDefinition _definition;
    private readonly TerminalOutput _output = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _writeGate = new();
    private readonly TelnetParser _parser;
    private readonly bool _localEcho;
    private readonly TimeSpan _connectTimeout;

    private ConnectionState _state = ConnectionState.Disconnected;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readCancel;

    internal TelnetConnection(ConnectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _definition = definition;
        _parser = new TelnetParser(Option("term"));
        _localEcho = string.Equals(Option("localEcho"), "true", StringComparison.OrdinalIgnoreCase);
        _connectTimeout = TimeSpan.FromSeconds(
            int.TryParse(Option("connectTimeoutSeconds"), out var seconds) && seconds is >= 1 and <= 300 ? seconds : 10);
    }

    public string Id => _definition.Id;

    public ConnectionState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    public event EventHandler<ConnectionState>? StateChanged;

    public IObservable<ReadOnlyMemory<byte>> Output => _output;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == ConnectionState.Connected)
            {
                return;
            }

            SetState(ConnectionState.Connecting);
            var client = new TcpClient { NoDelay = true };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_connectTimeout);
                var port = _definition.Port ?? DefaultPort;
                try
                {
                    await client.ConnectAsync(_definition.Host, port, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TelnetConnectionException(
                        $"Could not connect to {_definition.Host}:{port}: no answer within {_connectTimeout.TotalSeconds:0} seconds.");
                }
                catch (SocketException ex)
                {
                    throw new TelnetConnectionException($"Could not connect to {_definition.Host}:{port}: {ex.Message}", ex);
                }

                var stream = client.GetStream();
                var cancel = new CancellationTokenSource();
                _client = client;
                _stream = stream;
                _readCancel = cancel;
                SetState(ConnectionState.Connected);
                _ = Task.Run(() => ReadLoopAsync(client, stream, cancel.Token));
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                SetState(ConnectionState.Disconnected);
                throw;
            }
            catch (Exception)
            {
                client.Dispose();
                SetState(ConnectionState.Failed);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Close();
            SetState(ConnectionState.Disconnected);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _stream) is null)
        {
            throw new InvalidOperationException("The Telnet session is not connected.");
        }

        if (_localEcho)
        {
            _output.Publish(Echo(input.Span));
        }

        Send(TelnetParser.EncodeInput(input.Span));
        return ValueTask.CompletedTask;
    }

    public void Resize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        byte[] frame;
        lock (_writeGate)
        {
            frame = _parser.SetSize(columns, rows);
        }

        if (frame.Length > 0)
        {
            Send(frame);
        }
    }

    public ValueTask DisposeAsync()
    {
        Close();
        SetState(ConnectionState.Disconnected);
        _output.Complete();
        return ValueTask.CompletedTask;
    }

    /// <summary>What to show when the session echoes locally: the keys, with Return as a new line.</summary>
    private static byte[] Echo(ReadOnlySpan<byte> input)
    {
        var shown = new List<byte>(input.Length + 2);
        for (var i = 0; i < input.Length; i++)
        {
            var b = input[i];
            if (b == 13)
            {
                shown.Add(13);
                shown.Add(10);
                if (i + 1 < input.Length && input[i + 1] == 10)
                {
                    i++;
                }
            }
            else if (b == 127)
            {
                shown.AddRange(new byte[] { 8, 32, 8 });
            }
            else
            {
                shown.Add(b);
            }
        }

        return shown.ToArray();
    }

    private async Task ReadLoopAsync(TcpClient client, NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                byte[] output;
                byte[] reply;
                lock (_writeGate)
                {
                    (output, reply) = _parser.Feed(buffer.AsSpan(0, read));
                }

                if (reply.Length > 0)
                {
                    Send(reply);
                }

                if (output.Length > 0)
                {
                    _output.Publish(output);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The connection ended; handled below.
        }

        // Only report the end if this session is still the current one (not closed by us).
        if (ReferenceEquals(Volatile.Read(ref _client), client))
        {
            Close();
            TrySetState(ConnectionState.Connected, ConnectionState.Disconnected);
        }
    }

    private void Send(byte[] data)
    {
        var stream = Volatile.Read(ref _stream);
        if (stream is null)
        {
            return;
        }

        try
        {
            lock (_writeGate)
            {
                stream.Write(data, 0, data.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // The read loop notices the closed connection and reports it.
        }
    }

    private void Close()
    {
        var cancel = Interlocked.Exchange(ref _readCancel, null);
        var stream = Interlocked.Exchange(ref _stream, null);
        var client = Interlocked.Exchange(ref _client, null);

        cancel?.Cancel();
        stream?.Dispose();
        client?.Dispose();
        cancel?.Dispose();
    }

    private string Option(string key) =>
        _definition.Options is not null && _definition.Options.TryGetValue(key, out var value) ? value : string.Empty;

    private void SetState(ConnectionState state)
    {
        lock (_stateGate)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        StateChanged?.Invoke(this, state);
    }

    private void TrySetState(ConnectionState expected, ConnectionState state)
    {
        lock (_stateGate)
        {
            if (_state != expected)
            {
                return;
            }

            _state = state;
        }

        StateChanged?.Invoke(this, state);
    }
}
