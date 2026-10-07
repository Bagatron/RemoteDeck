namespace RemoteDeck.Plugin;

/// <summary>Receives a secret for the duration of the call only. Do not store the span.</summary>
public delegate void SecretReader(ReadOnlySpan<char> secret);

/// <summary>
/// A credential handed out by the host for one operation. The password is only
/// readable inside a <see cref="SecretReader"/> callback, and the host wipes it afterwards.
/// </summary>
public interface ICredential
{
    string? Username { get; }

    void ReadPassword(SecretReader reader);
}

/// <summary>
/// The only way plugins touch stored credentials. Plugins never see the vault: they ask for
/// the credential of one connection, use it inside the callback, and the host takes it back.
/// Requires the <c>useCredentials</c> permission in plugin.json.
/// </summary>
public interface ICredentialBroker
{
    ValueTask<T> UseAsync<T>(
        string connectionId,
        Func<ICredential, ValueTask<T>> use,
        CancellationToken cancellationToken = default);
}
