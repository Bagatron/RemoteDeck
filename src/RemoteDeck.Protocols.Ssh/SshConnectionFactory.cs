using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Ssh;

/// <summary>
/// Creates SSH sessions. Register one with the host's connection types; connections of type "ssh" then
/// open as terminals.
/// </summary>
public sealed class SshConnectionFactory : IConnectionFactory
{
    private readonly HostKeyVerifier _hostKeys;

    /// <param name="hostKeys">Where trusted host keys are remembered.</param>
    /// <param name="prompt">Asks the user about new or changed keys. Without it, unknown keys are refused.</param>
    public SshConnectionFactory(IHostKeyStore hostKeys, IHostKeyPrompt? prompt = null)
    {
        ArgumentNullException.ThrowIfNull(hostKeys);
        _hostKeys = new HostKeyVerifier(hostKeys, prompt);
    }

    /// <summary>Opens an SFTP file session to the same server, using the same credential and host-key rules as a terminal.</summary>
    public Task<SftpSession> OpenSftpAsync(
        ConnectionDefinition definition,
        ICredentialBroker credentials,
        CancellationToken cancellationToken = default) =>
        SftpSession.ConnectAsync(definition, credentials, new SshNetSftpFactory(), _hostKeys, cancellationToken);

    public string Type => "ssh";

    public string DisplayName => "SSH";

    /// <summary>Returns an <see cref="ITerminalConnection"/> that has not connected yet.</summary>
    public IConnection Create(ConnectionDefinition definition, ICredentialBroker credentials)
    {
        return new SshConnection(definition, credentials, new SshNetSessionFactory(), _hostKeys);
    }
}
