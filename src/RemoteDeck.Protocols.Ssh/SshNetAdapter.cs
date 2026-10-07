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
        try
        {
            var created = BuildClient(request, wipe);
            client = created;

            // Task.Run keeps the host-key callback (which may wait for the user) off the caller's thread.
            await Task.Run(() => created.ConnectAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            return new SshNetSession(created);
        }
        catch (Exception ex)
        {
            client?.Dispose();
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

    private static SshClient BuildClient(SshConnectRequest request, List<byte[]> wipe)
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

        var info = new ConnectionInfo(request.Host, request.Port, request.Username, methods.ToArray())
        {
            Timeout = request.ConnectTimeout,
        };

        var client = new SshClient(info);
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

        return client;
    }

    private static Exception Translate(Exception ex, SshConnectRequest request)
    {
        return ex switch
        {
            OperationCanceledException => ex,
            SshConnectionException => ex,
            Renci.SshNet.Common.SshAuthenticationException => new SshConnectionException(
                $"Authentication failed for '{request.Username}'. Check the password or key.", ex),
            Renci.SshNet.Common.SshOperationTimeoutException => new SshConnectionException(
                $"Timed out connecting to {request.Host}:{request.Port}.", ex),
            System.Net.Sockets.SocketException => new SshConnectionException(
                $"Could not reach {request.Host}:{request.Port}: {ex.Message}", ex),
            Renci.SshNet.Common.SshException => new SshConnectionException(
                $"The SSH connection to {request.Host}:{request.Port} failed: {ex.Message}", ex),
            _ => ex,
        };
    }
}

internal sealed class SshNetSession : ISshSession
{
    private readonly SshClient _client;

    public SshNetSession(SshClient client)
    {
        _client = client;
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
    }
}

internal sealed class SshNetShell : ISshShell
{
    private readonly SshClient _client;
    private readonly ShellStream _stream;
    private readonly Thread _reader;
    private volatile bool _disposed;

    public SshNetShell(SshClient client, ShellStream stream)
    {
        _client = client;
        _stream = stream;
        _stream.Closed += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ssh-shell-reader" };
    }

    public event EventHandler<byte[]>? DataReceived;

    public event EventHandler? Closed;

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
