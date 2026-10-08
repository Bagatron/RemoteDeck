using System.Security.Cryptography;
using System.Text;
using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Ssh;

/// <summary>Helpers for the forward-slash paths used on SFTP servers.</summary>
public static class RemotePath
{
    public static string Combine(string directory, string name) =>
        directory.EndsWith('/') ? directory + name : directory + "/" + name;

    /// <summary>The folder above <paramref name="path"/>; the root is its own parent.</summary>
    public static string Parent(string path)
    {
        var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        var slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
    }
}

/// <summary>
/// A connected SFTP session: browse folders and move files. The password is borrowed from the credential
/// broker while connecting and wiped straight afterwards, exactly like a terminal session.
/// </summary>
public sealed class SftpSession : IDisposable
{
    private readonly ISftpBackend _backend;

    internal SftpSession(ISftpBackend backend)
    {
        _backend = backend;
    }

    /// <summary>The folder the server starts in (usually the home folder).</summary>
    public string HomeDirectory => _backend.WorkingDirectory;

    internal static async Task<SftpSession> ConnectAsync(
        ConnectionDefinition definition,
        ICredentialBroker credentials,
        ISftpBackendFactory backends,
        HostKeyVerifier hostKeys,
        CancellationToken cancellationToken,
        Func<string, ConnectionDefinition?>? resolveJump = null)
    {
        var options = SshOptions.From(definition);

        if (definition.CredentialId is null)
        {
            return await OpenAsync(options, options.Username, null).ConfigureAwait(false);
        }

        return await credentials.UseAsync<SftpSession>(
            definition.Id,
            async credential =>
            {
                var secret = Array.Empty<byte>();
                credential.ReadPassword(password =>
                {
                    secret = new byte[Encoding.UTF8.GetByteCount(password)];
                    Encoding.UTF8.GetBytes(password, secret);
                });

                try
                {
                    return await OpenAsync(options, credential.Username ?? options.Username, secret).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(secret);
                }
            },
            cancellationToken).ConfigureAwait(false);

        async Task<SftpSession> OpenAsync(SshOptions opts, string? username, byte[]? secret)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                throw new SshConnectionException("No username is set for this connection.");
            }

            var hasSecret = secret is { Length: > 0 };
            if (opts.PrivateKeyPath is null && !hasSecret)
            {
                throw new SshConnectionException("No password or private key is set for this connection.");
            }

            var rejected = false;
            bool Verify(HostKeyInfo key)
            {
                var trusted = hostKeys.VerifyAsync(key, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                rejected |= !trusted;
                return trusted;
            }

            try
            {
                return await JumpHosts.WithJumpAsync<SftpSession>(
                    opts,
                    definition.Id,
                    resolveJump,
                    credentials,
                    Verify,
                    async jump => new SftpSession(await backends.ConnectAsync(
                        new SshConnectRequest
                        {
                            Host = opts.Host,
                            Port = opts.Port,
                            Username = username,
                            Secret = hasSecret ? secret : null,
                            PrivateKeyPath = opts.PrivateKeyPath,
                            ConnectTimeout = opts.ConnectTimeout,
                            KeepAlive = opts.KeepAlive,
                            VerifyHostKey = Verify,
                            Jump = jump,
                        },
                        cancellationToken).ConfigureAwait(false)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (rejected)
            {
                throw new SshConnectionException($"The host key of {opts.Host} was not trusted, so the connection was cancelled.");
            }
        }
    }

    /// <summary>Folders first, then files, each alphabetical. "." and ".." are left out.</summary>
    public Task<IReadOnlyList<SftpEntry>> ListAsync(string path) =>
        Task.Run<IReadOnlyList<SftpEntry>>(() =>
            _backend.List(path)
                .Where(e => e.Name is not ("." or ".."))
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());

    public Task CreateDirectoryAsync(string path) => Task.Run(() => _backend.CreateDirectory(path));

    public Task RenameAsync(string from, string to) => Task.Run(() => _backend.Rename(from, to));

    /// <summary>Deletes a file, or a folder with everything inside it.</summary>
    public Task DeleteAsync(SftpEntry entry) => Task.Run(() => Delete(entry));

    /// <summary>Uploads a file, or a whole local folder, into <paramref name="remoteDirectory"/>. Progress counts bytes sent.</summary>
    public Task UploadAsync(string localPath, string remoteDirectory, IProgress<long>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Upload(localPath, remoteDirectory, progress, cancellationToken), cancellationToken);

    /// <summary>Downloads a file, or a whole remote folder, into <paramref name="localDirectory"/>.</summary>
    public Task DownloadAsync(SftpEntry entry, string localDirectory, IProgress<long>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Download(entry, localDirectory, progress, cancellationToken), cancellationToken);

    public void Dispose() => _backend.Dispose();

    private void Delete(SftpEntry entry)
    {
        if (!entry.IsDirectory)
        {
            _backend.DeleteFile(entry.FullPath);
            return;
        }

        foreach (var child in _backend.List(entry.FullPath).Where(e => e.Name is not ("." or "..")))
        {
            Delete(child);
        }

        _backend.DeleteDirectory(entry.FullPath);
    }

    private void Upload(string localPath, string remoteDirectory, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var target = RemotePath.Combine(remoteDirectory, name);

        if (Directory.Exists(localPath))
        {
            if (!_backend.Exists(target))
            {
                _backend.CreateDirectory(target);
            }

            foreach (var child in Directory.EnumerateFileSystemEntries(localPath))
            {
                Upload(child, target, progress, cancellationToken);
            }

            return;
        }

        long reported = 0;
        using var stream = File.OpenRead(localPath);
        _backend.Upload(stream, target, done =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(done - reported);
            reported = done;
        });
    }

    private void Download(SftpEntry entry, string localDirectory, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A server could send a name like "../x"; only ever write inside the chosen folder.
        var safeName = Path.GetFileName(entry.Name);
        if (safeName.Length == 0)
        {
            return;
        }

        var target = Path.Combine(localDirectory, safeName);
        if (entry.IsDirectory)
        {
            Directory.CreateDirectory(target);
            foreach (var child in _backend.List(entry.FullPath).Where(e => e.Name is not ("." or "..")))
            {
                Download(child, target, progress, cancellationToken);
            }

            return;
        }

        long reported = 0;
        using var stream = File.Create(target);
        _backend.Download(entry.FullPath, stream, done =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(done - reported);
            reported = done;
        });
    }
}
