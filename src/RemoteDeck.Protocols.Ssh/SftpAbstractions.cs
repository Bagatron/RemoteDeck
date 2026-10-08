namespace RemoteDeck.Protocols.Ssh;

/// <summary>One file or folder in a remote directory listing.</summary>
public sealed record SftpEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTime Modified);

/// <summary>What the SFTP code needs from the SSH library, kept small so it can be tested with a fake.</summary>
internal interface ISftpBackend : IDisposable
{
    string WorkingDirectory { get; }

    IReadOnlyList<SftpEntry> List(string path);

    void Download(string path, Stream destination, Action<long> progress);

    void Upload(Stream source, string path, Action<long> progress);

    void DeleteFile(string path);

    void DeleteDirectory(string path);

    void CreateDirectory(string path);

    void Rename(string from, string to);

    bool Exists(string path);
}

internal interface ISftpBackendFactory
{
    Task<ISftpBackend> ConnectAsync(SshConnectRequest request, CancellationToken cancellationToken);
}
