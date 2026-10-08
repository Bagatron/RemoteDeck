using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;

namespace RemoteDeck.Protocols.Ssh;

// This file is the only place that touches the SSH.NET library. Its exception types share names with ours,
// so they are always written out in full below.

internal sealed class SshNetSessionFactory : ISshSessionFactory
{
    public async Task<ISshSession> ConnectAsync(SshConnectRequest request, CancellationToken cancellationToken)
    {
        var wipe = new List<byte[]>();
        SshClient? client = null;
        SshTunnel? tunnel = null;
        try
        {
            var (info, opened) = await SshTunnel.PrepareAsync(request, wipe, cancellationToken).ConfigureAwait(false);
            tunnel = opened;
            var created = new SshClient(info);
            client = created;
            Configure(created, request);

            // Task.Run keeps the host-key callback (which may wait for the user) off the caller's thread.
            await Task.Run(() => created.ConnectAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            StartForwards(created, request.Forwards);
            return new SshNetSession(created, tunnel);
        }
        catch (Exception ex)
        {
            client?.Dispose();
            tunnel?.Dispose();
            throw Translate(ex, request);
        }
        finally
        {
            foreach (var secret in wipe)
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }
    }

    /// <param name="host">Connect here instead of the request's host (the local end of a tunnel).</param>
    /// <param name="port">Connect to this port instead of the request's.</param>
    private static void StartForwards(SshClient client, IReadOnlyList<PortForward> forwards)
    {
        foreach (var forward in forwards)
        {
            ForwardedPort port = forward.Kind switch
            {
                PortForwardKind.Local => new ForwardedPortLocal("127.0.0.1", (uint)forward.BindPort, forward.Host!, (uint)forward.Port!.Value),
                PortForwardKind.Remote => new ForwardedPortRemote("127.0.0.1", (uint)forward.BindPort, forward.Host!, (uint)forward.Port!.Value),
                _ => new ForwardedPortDynamic("127.0.0.1", (uint)forward.BindPort),
            };

            try
            {
                client.AddForwardedPort(port);
                port.Start();
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or Renci.SshNet.Common.SshException or InvalidOperationException)
            {
                throw new SshConnectionException(
                    $"Could not start the port forward {forward}. Is the port already in use? ({ex.Message})",
                    ex);
            }
        }
    }

    internal static ConnectionInfo BuildInfo(SshConnectRequest request, List<byte[]> wipe, string? host = null, int? port = null)
    {
        var methods = new List<AuthenticationMethod>();

        if (request.PrivateKeyPath is not null)
        {
            PrivateKeyFile key;
            try
            {
                key = request.Secret is { Length: > 0 }
                    ? new PrivateKeyFile(request.PrivateKeyPath, Encoding.UTF8.GetString(request.Secret))
                    : new PrivateKeyFile(request.PrivateKeyPath);
            }
            catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or IOException or UnauthorizedAccessException)
            {
                throw new SshConnectionException(
                    $"Could not read the private key '{request.PrivateKeyPath}'. Check the path and the passphrase. ({ex.Message})",
                    ex);
            }

            methods.Add(new PrivateKeyAuthenticationMethod(request.Username, key));
        }
        else if (request.Secret is { Length: > 0 })
        {
            var password = (byte[])request.Secret.Clone();
            wipe.Add(password);

            methods.Add(new PasswordAuthenticationMethod(request.Username, password));

            // Many Linux servers only offer "keyboard-interactive" for password logins.
            var interactive = new KeyboardInteractiveAuthenticationMethod(request.Username);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    if (prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase))
                    {
                        prompt.Response = Encoding.UTF8.GetString(password);
                    }
                }
            };
            methods.Add(interactive);
        }

        var info = new ConnectionInfo(host ?? request.Host, port ?? request.Port, request.Username, methods.ToArray())
        {
            Timeout = request.ConnectTimeout,
        };

        return info;
    }

    /// <summary>Applies keep-alive and the host-key check to a client (SSH or SFTP).</summary>
    internal static void Configure(BaseClient client, SshConnectRequest request)
    {
        if (request.KeepAlive > TimeSpan.Zero)
        {
            client.KeepAliveInterval = request.KeepAlive;
        }

        client.HostKeyReceived += (_, e) =>
        {
            var presented = new HostKeyInfo(
                request.Host,
                request.Port,
                e.HostKeyName,
                HostKeyFingerprint.Sha256(e.HostKey));
            e.CanTrust = request.VerifyHostKey(presented);
        };
    }

    internal static Exception Translate(Exception ex, SshConnectRequest request)
    {
        var where = request.Jump is null
            ? $"{request.Host}:{request.Port}"
            : $"{request.Host}:{request.Port} through the jump host {request.Jump.Host}";

        return ex switch
        {
            OperationCanceledException => ex,
            SshConnectionException => ex,
            Renci.SshNet.Common.SshAuthenticationException => new SshConnectionException(
                $"Authentication failed for '{request.Username}'. Check the password or key.", ex),
            Renci.SshNet.Common.SshOperationTimeoutException => new SshConnectionException(
                $"Timed out connecting to {where}.", ex) { Transient = true },
            System.Net.Sockets.SocketException => new SshConnectionException(
                $"Could not reach {where}: {ex.Message}", ex) { Transient = true },
            Renci.SshNet.Common.SshException => new SshConnectionException(
                $"The SSH connection to {where} failed: {ex.Message}", ex) { Transient = true },
            _ => ex,
        };
    }
}

internal sealed class SshNetSftpFactory : ISftpBackendFactory
{
    public async Task<ISftpBackend> ConnectAsync(SshConnectRequest request, CancellationToken cancellationToken)
    {
        var wipe = new List<byte[]>();
        SftpClient? client = null;
        SshTunnel? tunnel = null;
        try
        {
            var (info, opened) = await SshTunnel.PrepareAsync(request, wipe, cancellationToken).ConfigureAwait(false);
            tunnel = opened;
            var created = new SftpClient(info);
            client = created;
            SshNetSessionFactory.Configure(created, request);

            await Task.Run(() => created.ConnectAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            return new SshNetSftp(created, tunnel);
        }
        catch (Exception ex)
        {
            client?.Dispose();
            tunnel?.Dispose();
            throw SshNetSessionFactory.Translate(ex, request);
        }
        finally
        {
            foreach (var secret in wipe)
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }
    }
}

internal sealed class SshNetSftp : ISftpBackend
{
    private readonly SftpClient _client;
    private readonly IDisposable? _tunnel;

    public SshNetSftp(SftpClient client, IDisposable? tunnel = null)
    {
        _client = client;
        _tunnel = tunnel;
    }

    public string WorkingDirectory => _client.WorkingDirectory;

    public IReadOnlyList<SftpEntry> List(string path) =>
        _client.ListDirectory(path)
            .Select(f => new SftpEntry(f.Name, f.FullName, f.IsDirectory, f.IsDirectory ? 0 : f.Length, f.LastWriteTime))
            .ToList();

    public void Download(string path, Stream destination, Action<long> progress) =>
        _client.DownloadFile(path, destination, done => progress((long)done));

    public void Upload(Stream source, string path, Action<long> progress) =>
        _client.UploadFile(source, path, true, done => progress((long)done));

    public void DeleteFile(string path) => _client.DeleteFile(path);

    public void DeleteDirectory(string path) => _client.DeleteDirectory(path);

    public void CreateDirectory(string path) => _client.CreateDirectory(path);

    public void Rename(string from, string to) => _client.RenameFile(from, to);

    public bool Exists(string path) => _client.Exists(path);

    public void Dispose()
    {
        try
        {
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ObjectDisposedException or IOException)
        {
            // Already gone.
        }

        _client.Dispose();
        _tunnel?.Dispose();
    }
}

internal sealed class SshNetSession : ISshSession
{
    private readonly SshClient _client;
    private readonly IDisposable? _tunnel;

    public SshNetSession(SshClient client, IDisposable? tunnel = null)
    {
        _client = client;
        _tunnel = tunnel;
    }

    public ISshShell OpenShell(string terminal, int columns, int rows)
    {
        var stream = _client.CreateShellStream(terminal, (uint)columns, (uint)rows, 0, 0, 8192);
        return new SshNetShell(_client, stream);
    }

    public void Dispose()
    {
        try
        {
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ObjectDisposedException or IOException)
        {
            // Already gone; nothing more to do.
        }

        _client.Dispose();
        _tunnel?.Dispose();
    }
}

internal sealed class SshNetShell : ISshShell
{
    private readonly SshClient _client;
    private readonly ShellStream _stream;
    private readonly Thread _reader;
    private volatile bool _disposed;
    private volatile bool _errored;

    public SshNetShell(SshClient client, ShellStream stream)
    {
        _client = client;
        _stream = stream;
        _client.ErrorOccurred += (_, _) => _errored = true;
        _stream.Closed += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ssh-shell-reader" };
    }

    public event EventHandler<byte[]>? DataReceived;

    public event EventHandler? Closed;

    public bool Faulted => _errored || !_client.IsConnected;

    public void Start()
    {
        _reader.Start();
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        var copy = data.ToArray();
        _stream.Write(copy, 0, copy.Length);
        _stream.Flush();
    }

    public void Resize(int columns, int rows)
    {
        _stream.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
    }

    public void Dispose()
    {
        _disposed = true;
        _stream.Dispose();
    }

    // Reading in our own loop (instead of relying on the stream's event) means the library's internal
    // buffer is drained continuously, so a long session cannot make it grow without bound.
    private void ReadLoop()
    {
        var buffer = new byte[8192];
        try
        {
            while (!_disposed)
            {
                var read = _stream.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    DataReceived?.Invoke(this, buffer.AsSpan(0, read).ToArray());
                }
                else if (!_client.IsConnected)
                {
                    break;
                }
                else
                {
                    // Some versions return 0 when nothing is waiting rather than blocking.
                    Thread.Sleep(15);
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or InvalidOperationException
                                       or Renci.SshNet.Common.SshException)
        {
            // The session ended underneath us; reported through Closed below.
        }
        finally
        {
            if (!_disposed)
            {
                Closed?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
