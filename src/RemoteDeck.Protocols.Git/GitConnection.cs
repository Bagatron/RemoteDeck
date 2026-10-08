using System.Text;
using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Git;

/// <summary>
/// A local terminal opened in a git repository folder. It is an <see cref="ITerminalConnection"/>, so it gets split
/// panes and broadcast input like SSH does. Nothing is saved except the folder and the options; see
/// <see cref="GitSettings"/>.
/// </summary>
public sealed class GitConnection : ITerminalConnection
{
    private readonly ConnectionDefinition _definition;
    private readonly IShellLinkFactory _links;
    private readonly ShellLocator _locator;
    private readonly Func<string, bool> _directoryExists;
    private readonly TerminalOutput _output = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _writeGate = new();

    private ConnectionState _state = ConnectionState.Disconnected;
    private IShellLink? _link;
    private CancellationTokenSource? _readCancel;
    private int _columns = 120;
    private int _rows = 30;

    internal GitConnection(
        ConnectionDefinition definition,
        IShellLinkFactory links,
        ShellLocator locator,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(directoryExists);
        _definition = definition;
        _links = links;
        _locator = locator;
        _directoryExists = directoryExists;
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
                var settings = GitSettings.From(_definition);
                if (!_directoryExists(settings.Folder))
                {
                    throw new GitShellException($"The folder \"{settings.Folder}\" does not exist.");
                }

                var shell = _locator.Locate(settings.Shell);
                int columns, rows;
                lock (_writeGate)
                {
                    columns = _columns;
                    rows = _rows;
                }

                var link = await Task.Run(
                    () => _links.Start(new ShellLaunch(shell, settings.Folder, columns, rows)),
                    cancellationToken).ConfigureAwait(false);
                var cancel = new CancellationTokenSource();
                _link = link;
                _readCancel = cancel;
                SetState(ConnectionState.Connected);
                _ = Task.Run(() => ReadLoopAsync(link, cancel.Token));

                if (settings.Startup is { } startup)
                {
                    // The shell reads this once it is ready, so there is no need to wait for the prompt.
                    await WriteAsync(Encoding.UTF8.GetBytes(startup + "\r"), cancellationToken).ConfigureAwait(false);
                }
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
        var link = Volatile.Read(ref _link) ?? throw new InvalidOperationException("The terminal is not running.");
        try
        {
            lock (_writeGate)
            {
                link.Input.Write(input.Span);
                link.Input.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The program ended; the read loop reports it.
        }

        return ValueTask.CompletedTask;
    }

    public void Resize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        IShellLink? link;
        lock (_writeGate)
        {
            _columns = columns;
            _rows = rows;
            link = _link;
        }

        try
        {
            link?.Resize(columns, rows);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Closing at the same moment.
        }
    }

    public ValueTask DisposeAsync()
    {
        Close();
        SetState(ConnectionState.Disconnected);
        _output.Complete();
        return ValueTask.CompletedTask;
    }

    private async Task ReadLoopAsync(IShellLink link, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await link.Output.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                _output.Publish(buffer.AsSpan(0, read).ToArray());
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            // The program ended or the tab was closed; handled below.
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
        IShellLink? link;
        lock (_writeGate)
        {
            link = Interlocked.Exchange(ref _link, null);
        }

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
