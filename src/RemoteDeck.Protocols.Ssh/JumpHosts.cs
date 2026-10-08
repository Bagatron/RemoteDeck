using System.Security.Cryptography;
using System.Text;
using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Ssh;

/// <summary>
/// Turns a connection's <c>proxyJump</c> option (the id of another saved SSH connection) into a chain of
/// connect requests, borrowing each jump host's credential only while the connection is being made.
/// </summary>
internal static class JumpHosts
{
    public const int MaxHops = 5;

    /// <summary>
    /// Runs <paramref name="body"/> with the request for the jump host (null when the target is reached directly).
    /// The jump credentials stay available until <paramref name="body"/> finishes, then are wiped.
    /// </summary>
    public static Task<T> WithJumpAsync<T>(
        SshOptions target,
        string targetId,
        Func<string, ConnectionDefinition?>? resolve,
        ICredentialBroker credentials,
        Func<HostKeyInfo, bool> verify,
        Func<SshConnectRequest?, Task<T>> body,
        CancellationToken cancellationToken) =>
        Next(target, new[] { targetId }, resolve, credentials, verify, body, cancellationToken);

    private static async Task<T> Next<T>(
        SshOptions options,
        IReadOnlyList<string> chain,
        Func<string, ConnectionDefinition?>? resolve,
        ICredentialBroker credentials,
        Func<HostKeyInfo, bool> verify,
        Func<SshConnectRequest?, Task<T>> body,
        CancellationToken cancellationToken)
    {
        if (options.ProxyJump is not { } jumpId)
        {
            return await body(null).ConfigureAwait(false);
        }

        if (chain.Contains(jumpId, StringComparer.Ordinal))
        {
            throw new SshConnectionException("The jump hosts for this connection loop back on themselves.");
        }

        if (chain.Count > MaxHops)
        {
            throw new SshConnectionException($"Too many jump hosts in a row (the limit is {MaxHops}).");
        }

        var definition = resolve?.Invoke(jumpId)
            ?? throw new SshConnectionException("The jump host for this connection no longer exists.");

        if (!string.Equals(definition.Type, "ssh", StringComparison.OrdinalIgnoreCase))
        {
            throw new SshConnectionException($"The jump host '{definition.Name}' is not an SSH connection.");
        }

        var jumpOptions = SshOptions.From(definition);
        var nextChain = chain.Append(jumpId).ToArray();

        return await Next(
            jumpOptions,
            nextChain,
            resolve,
            credentials,
            verify,
            inner => WithCredentialAsync(definition, jumpOptions, credentials, cancellationToken, (username, secret) =>
            {
                if (string.IsNullOrWhiteSpace(username))
                {
                    throw new SshConnectionException($"No username is set for the jump host '{definition.Name}'.");
                }

                var hasSecret = secret is { Length: > 0 };
                if (jumpOptions.PrivateKeyPath is null && !hasSecret)
                {
                    throw new SshConnectionException(
                        $"No password or private key is set for the jump host '{definition.Name}'.");
                }

                return body(new SshConnectRequest
                {
                    Host = jumpOptions.Host,
                    Port = jumpOptions.Port,
                    Username = username,
                    Secret = hasSecret ? secret : null,
                    PrivateKeyPath = jumpOptions.PrivateKeyPath,
                    ConnectTimeout = jumpOptions.ConnectTimeout,
                    KeepAlive = jumpOptions.KeepAlive,
                    VerifyHostKey = verify,
                    Jump = inner,
                });
            }),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> WithCredentialAsync<T>(
        ConnectionDefinition definition,
        SshOptions options,
        ICredentialBroker credentials,
        CancellationToken cancellationToken,
        Func<string?, byte[]?, Task<T>> use)
    {
        if (definition.CredentialId is null)
        {
            return await use(options.Username, null).ConfigureAwait(false);
        }

        return await credentials.UseAsync<T>(
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
                    return await use(credential.Username ?? options.Username, secret).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(secret);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }
}
