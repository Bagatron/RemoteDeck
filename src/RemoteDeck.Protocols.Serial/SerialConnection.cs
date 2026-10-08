using System.IO.Ports;
using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Serial;

/// <summary>An open serial port: the bytes going each way, and a way to close it.</summary>
internal interface ISerialLink : IDisposable
{
    Stream Stream { get; }
}

/// <summary>Opens serial ports. The real one uses the system's COM ports; tests use a fake.</summary>
internal interface ISerialLinkFactory
{
    ISerialLink Open(SerialSettings settings);
}

internal sealed class SystemSerialLinkFactory : ISerialLinkFactory
{
    public ISerialLink Open(SerialSettings settings)
    {
        var port = new SerialPort(settings.PortName, settings.BaudRate, settings.Parity, settings.DataBits, settings.StopBits)
        {
            Handshake = settings.Handshake,
            DtrEnable = true,
            RtsEnable = settings.Handshake == Handshake.None,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 3000,
        };

        try
        {
            port.Open();
        }
        catch (UnauthorizedAccessException ex)
        {
            port.Dispose();
            throw new SerialConnectionException(
                $"{settings.PortName} is in use by another program (or you do not have access to it). Close the other program and try again.",
                ex);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or PlatformNotSupportedException)
        {
            port.Dispose();
            throw new SerialConnectionException(
                $"Could not open {settings.PortName}: {ex.Message} Check that the device is plugged in and the port name is right.",
                ex);
        }

        return new Link(port);
    }

    private sealed class Link : ISerialLink
    {
        private readonly SerialPort _port;

        public Link(SerialPort port)
        {
            _port = port;
        }

        public Stream Stream => _port.BaseStream;

        public void Dispose()
        {
            try
            {
                _port.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // The device was already unplugged.
            }
        }
    }
}

/// <summary>
/// One serial terminal session (a COM port, for example a switch's console cable or a microcontroller). Like SSH it is
/// an <see cref="ITerminalConnection"/>, so it gets split panes and broadcast input for free. Nothing is saved except
/// the port settings. See <see cref="SerialSettings"/> for the options.
/// </summary>
public sealed class SerialConnection : ITerminalConnection
{
    private readonly ConnectionDefinition _definition;
    private readonly ISerialLinkFactory _links;
    private readonly TerminalOutput _output = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _writeGate = new();

    private ConnectionState _state = ConnectionState.Disconnected;
    private ISerialLink? _link;
    private CancellationTokenSource? _readCancel;
    private bool _lastWasCr;

    internal SerialConnection(ConnectionDefinition definition, ISerialLinkFactory links)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(links);
        _definition = definition;
        _links = links;
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
            try
            {
                var settings = SerialSettings.From(_definition);
                var link = await Task.Run(() => _links.Open(settings), cancellationToken).ConfigureAwait(false);
                var cancel = new CancellationTokenSource();
                _link = link;
                _readCancel = cancel;
                _lastWasCr = false;
                SetState(ConnectionState.Connected);
                _ = Task.Run(() => ReadLoopAsync(link, settings.TranslateLf, cancel.Token));
            }
            catch (OperationCanceledException)
            {
                SetState(ConnectionState.Disconnected);
                throw;
            }
            catch (Exception)
            {
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
        var link = Volatile.Read(ref _link) ?? throw new InvalidOperationException("The serial port is not open.");
        try
        {
            lock (_writeGate)
            {
                link.Stream.Write(input.Span);
                link.Stream.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException or InvalidOperationException)
        {
            // The read loop notices a port that went away and reports it.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>A serial line has no window size to report.</summary>
    public void Resize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
    }

    public ValueTask DisposeAsync()
    {
        Close();
        SetState(ConnectionState.Disconnected);
        _output.Complete();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Devices usually end lines with a bare line feed, which a terminal shows as a step down without returning to the
    /// left edge. This puts a carriage return in front of every line feed that does not already have one.
    /// </summary>
    internal static byte[] AddCarriageReturns(ReadOnlySpan<byte> data, ref bool lastWasCr)
    {
        var result = new List<byte>(data.Length + 8);
        foreach (var b in data)
        {
            if (b == 10 && !lastWasCr)
            {
                result.Add(13);
            }

            result.Add(b);
            lastWasCr = b == 13;
        }

        return result.ToArray();
    }

    private async Task ReadLoopAsync(ISerialLink link, bool translateLf, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await link.Stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    // A serial stream normally never ends; this is a port that was closed or unplugged.
                    break;
                }

                var data = translateLf
                    ? AddCarriageReturns(buffer.AsSpan(0, read), ref _lastWasCr)
                    : buffer.AsSpan(0, read).ToArray();
                _output.Publish(data);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            // The device was unplugged or the port was closed; handled below.
        }

        if (ReferenceEquals(Volatile.Read(ref _link), link))
        {
            Close();
            TrySetState(ConnectionState.Connected, ConnectionState.Disconnected);
        }
    }

    private void Close()
    {
        var cancel = Interlocked.Exchange(ref _readCancel, null);
        var link = Interlocked.Exchange(ref _link, null);

        cancel?.Cancel();
        link?.Dispose();
        cancel?.Dispose();
    }

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
