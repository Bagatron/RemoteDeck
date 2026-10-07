namespace RemoteDeck.Protocols.Ssh;

/// <summary>
/// What the SSH library is asked to do. Keeping the real library (SSH.NET) behind these small interfaces
/// means the connection logic can be tested without a server, and the library can be replaced later.
/// </summary>
internal sealed class SshConnectRequest
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    public required string Username { get; init; }

    /// <summary>The password, or the key's passphrase when <see cref="PrivateKeyPath"/> is set. Wiped by the caller after connecting.</summary>
    public byte[]? Secret { get; init; }

    public string? PrivateKeyPath { get; init; }

    public required TimeSpan ConnectTimeout { get; init; }

    public required TimeSpan KeepAlive { get; init; }

    /// <summary>
    /// Called when the server presents its host key; return false to abort. It may block while the user decides,
    /// so implementations call it from a background thread, never the UI thread.
    /// </summary>
    public required Func<HostKeyInfo, bool> VerifyHostKey { get; init; }
}

internal interface ISshSessionFactory
{
    /// <summary>Connects and authenticates. Throws <see cref="SshConnectionException"/> with a readable message on failure.</summary>
    Task<ISshSession> ConnectAsync(SshConnectRequest request, CancellationToken cancellationToken);
}

/// <summary>One connected, authenticated SSH session.</summary>
internal interface ISshSession : IDisposable
{
    ISshShell OpenShell(string terminal, int columns, int rows);
}

/// <summary>An interactive shell channel. Nothing is delivered until <see cref="Start"/> is called, so no output is missed.</summary>
internal interface ISshShell : IDisposable
{
    event EventHandler<byte[]>? DataReceived;

    /// <summary>Raised when the remote side closes the shell or the connection drops. May be raised more than once.</summary>
    event EventHandler? Closed;

    void Start();

    void Write(ReadOnlySpan<byte> data);

    void Resize(int columns, int rows);
}
