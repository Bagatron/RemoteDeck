using System.Security.Cryptography;
using System.Text;
using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Ssh;

/// <summary>
/// One SSH terminal session. It gets split panes and broadcast input from the host for free, because it is an
/// <see cref="ITerminalConnection"/>: bytes in, bytes out.
///
/// The password is borrowed from the credential broker only while connecting and wiped straight afterwards.
/// </summary>
public sealed class SshConnection : ITerminalConnection
{
    private readonly ConnectionDefinition _definition;
    private readonly ICredentialBroker _credentials;
    private readonly ISshSessionFactory _sessions;
    private readonly HostKeyVerifier _hostKeys;
    private readonly Func<string, ConnectionDefinition?>? _resolveJump;
    private readonly TerminalOutput _output = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _stateGate = new();

    private ConnectionState _state = ConnectionState.Disconnected;
    private ISshSession? _session;
    private ISshShell? _shell;
    private int _columns = 80;
    private int _rows = 24;
    private volatile bool _hostKeyRejected;

    internal SshConnection(
        ConnectionDefinition definition,
        ICredentialBroker credentials,
        ISshSessionFactory sessions,
        HostKeyVerifier hostKeys,
        Func<string, ConnectionDefinition?>? resolveJump = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(hostKeys);

        _definition = definition;
        _credentials = credentials;
        _sessions = sessions;
        _hostKeys = hostKeys;
        _resolveJump = resolveJump;
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

    /// <summary>
    /// True when the last session ended because the connection was lost (network down, server gone), false when the
    /// shell simply ended, you closed it, or it never connected. Used to decide whether to reconnect.
    /// </summary>
    public bool DroppedUnexpectedly { get; private set; }

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

            _hostKeyRejected = false;
            DroppedUnexpectedly = false;
            SetState(ConnectionState.Connecting);
            try
            {
                await OpenAsync(cancellationToken).ConfigureAwait(false);
                SetState(ConnectionState.Connected);
            }
            catch (OperationCanceledException)
            {
                Cleanup();
                SetState(ConnectionState.Disconnected);
                throw;
            }
            catch (Exception ex)
            {
                Cleanup();
                SetState(ConnectionState.Failed);

                if (_hostKeyRejected)
                {
                    throw new SshConnectionException(
                        $"The host key of {_definition.Host} was not trusted, so the connection was cancelled.",
                        ex);
                }

                if (ex is SshConnectionException)
                {
                    throw;
                }

                throw new SshConnectionException($"Could not connect to {_definition.Host}: {ex.Message}", ex);
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
            Cleanup();
            SetState(ConnectionState.Disconnected);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Sends keystrokes to the remote shell exactly as typed. Throws when not connected.</summary>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
    {
        var shell = Volatile.Read(ref _shell)
            ?? throw new InvalidOperationException("The SSH session is not connected.");

        shell.Write(input.Span);
        return ValueTask.CompletedTask;
    }

    /// <summary>Tells the remote side the terminal size. Before connecting it sets the size the shell starts with.</summary>
    public void Resize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        _columns = columns;
        _rows = rows;
        Volatile.Read(ref _shell)?.Resize(columns, rows);
    }

    public ValueTask DisposeAsync()
    {
        Cleanup();
        SetState(ConnectionState.Disconnected);
        _output.Complete();
        return ValueTask.CompletedTask;
    }

    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        var options = SshOptions.From(_definition);

        if (_definition.CredentialId is null)
        {
            // No saved credential: only key-based login with a 'username' option can work.
            await OpenWithAsync(options, options.Username, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _credentials.UseAsync<object?>(
            _definition.Id,
            async credential =>
            {
                var (username, secret) = ReadCredential(credential);
                try
                {
                    await OpenWithAsync(options, username ?? options.Username, secret, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(secret);
                }

                return null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task OpenWithAsync(SshOptions options, string? username, byte[]? secret, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new SshConnectionException(
                "No username is set for this connection. Save a credential for it, or add a 'username' option.");
        }

        var hasSecret = secret is { Length: > 0 };
        if (options.PrivateKeyPath is null && !hasSecret && !options.UseAgent)
        {
            throw new SshConnectionException(
                "No password, private key or agent is set for this connection. Save a credential, set 'privateKeyPath', or turn on the SSH agent.");
        }

        var session = await JumpHosts.WithJumpAsync<ISshSession>(
            options,
            _definition.Id,
            _resolveJump,
            _credentials,
            VerifyHostKey,
            jump => _sessions.ConnectAsync(
                new SshConnectRequest
                {
                    Host = options.Host,
                    Port = options.Port,
                    Username = username,
                    Secret = hasSecret ? secret : null,
                    PrivateKeyPath = options.PrivateKeyPath,
                    UseAgent = options.UseAgent,
                    ConnectTimeout = options.ConnectTimeout,
                    KeepAlive = options.KeepAlive,
                    VerifyHostKey = VerifyHostKey,
                    Jump = jump,
                    Forwards = options.Forwards ?? Array.Empty<PortForward>(),
                },
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
        _session = session;

        var shell = session.OpenShell(options.Terminal, _columns, _rows);
        shell.DataReceived += OnDataReceived;
        shell.Closed += OnShellClosed;
        _shell = shell;

        // Only now, so the login banner is not lost.
        shell.Start();
    }

    /// <summary>
    /// Runs on the SSH library's connecting thread while the key exchange waits, so blocking here for the
    /// user's answer is safe as long as the connect itself was not started on the UI thread.
    /// </summary>
    private bool VerifyHostKey(HostKeyInfo key)
    {
        var trusted = _hostKeys.VerifyAsync(key, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        if (!trusted)
        {
            _hostKeyRejected = true;
        }

        return trusted;
    }

    private static (string? Username, byte[] Secret) ReadCredential(ICredential credential)
    {
        var secret = Array.Empty<byte>();
        credential.ReadPassword(password =>
        {
            secret = new byte[Encoding.UTF8.GetByteCount(password)];
            Encoding.UTF8.GetBytes(password, secret);
        });

        return (credential.Username, secret);
    }

    private void OnDataReceived(object? sender, byte[] data)
    {
        _output.Publish(data);
    }

    private void OnShellClosed(object? sender, EventArgs e)
    {
        DroppedUnexpectedly = (sender as ISshShell)?.Faulted ?? false;
        Cleanup();
        TrySetState(ConnectionState.Connected, ConnectionState.Disconnected);
    }

    private void Cleanup()
    {
        var shell = Interlocked.Exchange(ref _shell, null);
        var session = Interlocked.Exchange(ref _session, null);

        if (shell is not null)
        {
            shell.DataReceived -= OnDataReceived;
            shell.Closed -= OnShellClosed;
            shell.Dispose();
        }

        session?.Dispose();
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
