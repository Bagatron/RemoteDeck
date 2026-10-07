using System.Security.Cryptography;
using System.Text;
using RemoteDeck.Plugin;

namespace RemoteDeck.Vault;

/// <summary>A decrypted credential that wipes itself. Handed to plugins only inside a broker callback.</summary>
internal sealed class VaultCredential : ICredential, IDisposable
{
    private byte[]? _password;

    public VaultCredential(string? username, byte[] password)
    {
        Username = username;
        _password = password;
    }

    public string? Username { get; }

    public void ReadPassword(SecretReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var bytes = _password
            ?? throw new ObjectDisposedException(nameof(VaultCredential), "The credential can only be used inside the callback it was given to.");

        var chars = new char[Encoding.UTF8.GetCharCount(bytes)];
        try
        {
            Encoding.UTF8.GetChars(bytes, chars);
            reader(chars);
        }
        finally
        {
            Array.Clear(chars);
        }
    }

    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref _password, null);
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

/// <summary>
/// The host's <see cref="ICredentialBroker"/>: looks up which credential a connection uses and lends it out
/// for one operation. The mapping comes from the connection store, which this library knows nothing about.
/// </summary>
public sealed class VaultCredentialBroker : ICredentialBroker
{
    private readonly CredentialVault _vault;
    private readonly Func<string, string?> _credentialIdForConnection;

    public VaultCredentialBroker(CredentialVault vault, Func<string, string?> credentialIdForConnection)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(credentialIdForConnection);
        _vault = vault;
        _credentialIdForConnection = credentialIdForConnection;
    }

    public ValueTask<T> UseAsync<T>(
        string connectionId,
        Func<ICredential, ValueTask<T>> use,
        CancellationToken cancellationToken = default)
    {
        var credentialId = _credentialIdForConnection(connectionId)
            ?? throw new KeyNotFoundException($"Connection '{connectionId}' has no saved credential.");

        return _vault.UseAsync(credentialId, use, cancellationToken);
    }
}

/// <summary>
/// Wraps a broker for one plugin. Without the <c>useCredentials</c> permission in its manifest the plugin
/// gets this guard, which refuses every request.
/// </summary>
public sealed class GuardedCredentialBroker : ICredentialBroker
{
    private readonly ICredentialBroker _inner;
    private readonly string _pluginId;
    private readonly bool _permitted;

    public GuardedCredentialBroker(ICredentialBroker inner, string pluginId, bool useCredentialsPermitted)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        _inner = inner;
        _pluginId = pluginId;
        _permitted = useCredentialsPermitted;
    }

    public ValueTask<T> UseAsync<T>(
        string connectionId,
        Func<ICredential, ValueTask<T>> use,
        CancellationToken cancellationToken = default)
    {
        if (!_permitted)
        {
            throw new UnauthorizedAccessException(
                $"Plugin '{_pluginId}' did not request the 'useCredentials' permission.");
        }

        return _inner.UseAsync(connectionId, use, cancellationToken);
    }
}
