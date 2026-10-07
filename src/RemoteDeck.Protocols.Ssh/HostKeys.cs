using System.Security.Cryptography;

namespace RemoteDeck.Protocols.Ssh;

/// <summary>A server's host key as presented (or as remembered).</summary>
/// <param name="Algorithm">For example "ssh-ed25519" or "rsa-sha2-512".</param>
/// <param name="Fingerprint">OpenSSH style: "SHA256:" followed by unpadded base64.</param>
public sealed record HostKeyInfo(string Host, int Port, string Algorithm, string Fingerprint);

public enum HostKeyVerdict
{
    /// <summary>First time this host is seen.</summary>
    NewHost,

    /// <summary>The host is known, but never with this kind of key.</summary>
    NewKeyType,

    /// <summary>The host is known with this kind of key, and the key is different. Possible man-in-the-middle.</summary>
    Changed,
}

/// <summary>Remembers which host keys the user has trusted (the equivalent of OpenSSH's known_hosts).</summary>
public interface IHostKeyStore
{
    /// <summary>All remembered keys for a host and port, one per algorithm.</summary>
    IReadOnlyList<HostKeyInfo> FindAll(string host, int port);

    /// <summary>Remembers a key, replacing any earlier key of the same algorithm for that host and port.</summary>
    void Save(HostKeyInfo key);
}

/// <summary>Asks the user whether to trust a key. The UI shows the fingerprint and, for a change, a prominent warning.</summary>
public interface IHostKeyPrompt
{
    /// <param name="knownKeys">What is already remembered for this host (empty for a new host).</param>
    ValueTask<bool> ConfirmAsync(
        HostKeyVerdict verdict,
        HostKeyInfo presented,
        IReadOnlyList<HostKeyInfo> knownKeys,
        CancellationToken cancellationToken);
}

/// <summary>
/// Trust on first use. A remembered key passes silently; anything else needs the user's say-so.
/// With no prompt available, everything unknown or changed is refused.
/// </summary>
public sealed class HostKeyVerifier
{
    private readonly IHostKeyStore _store;
    private readonly IHostKeyPrompt? _prompt;

    public HostKeyVerifier(IHostKeyStore store, IHostKeyPrompt? prompt)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _prompt = prompt;
    }

    public async ValueTask<bool> VerifyAsync(HostKeyInfo presented, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presented);

        var known = _store.FindAll(presented.Host, presented.Port);
        var sameAlgorithm = known.FirstOrDefault(
            k => string.Equals(k.Algorithm, presented.Algorithm, StringComparison.OrdinalIgnoreCase));

        if (sameAlgorithm is not null
            && string.Equals(sameAlgorithm.Fingerprint, presented.Fingerprint, StringComparison.Ordinal))
        {
            return true;
        }

        var verdict = sameAlgorithm is not null
            ? HostKeyVerdict.Changed
            : known.Count > 0 ? HostKeyVerdict.NewKeyType : HostKeyVerdict.NewHost;

        // Fail closed: nobody to ask means no connection.
        if (_prompt is null)
        {
            return false;
        }

        if (!await _prompt.ConfirmAsync(verdict, presented, known, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _store.Save(presented);
        return true;
    }
}

public static class HostKeyFingerprint
{
    /// <summary>The same text <c>ssh-keygen -lf</c> prints: "SHA256:" and unpadded base64 of the SHA-256 of the key blob.</summary>
    public static string Sha256(ReadOnlySpan<byte> hostKey)
    {
        return "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');
    }
}
