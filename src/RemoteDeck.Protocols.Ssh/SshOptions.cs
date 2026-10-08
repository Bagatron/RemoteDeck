using System.Globalization;
using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Ssh;

/// <summary>A problem with how a connection is set up, or why it could not be established. The message is safe to show to the user.</summary>
public sealed class SshConnectionException : Exception
{
    public SshConnectionException(string message)
        : base(message)
    {
    }

    public SshConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// True when trying again later could succeed (the network or server was unreachable). False for problems
    /// that will not fix themselves: wrong password, a rejected host key, or a mistake in the connection's settings.
    /// </summary>
    public bool Transient { get; init; }
}

/// <summary>
/// SSH settings read from a connection's <c>Options</c>. All are optional:
/// <c>username</c> (used when no credential supplies one), <c>privateKeyPath</c>, <c>term</c>,
/// <c>keepAliveSeconds</c> (0 turns keep-alive off), <c>connectTimeoutSeconds</c> and <c>proxyJump</c>
/// (the id of another saved SSH connection to tunnel through) <c>forwards</c> (port forwards, see <see cref="PortForward"/>) and <c>useAgent</c> ("true" to sign in with keys from the SSH agent).
/// </summary>
internal sealed record SshOptions(
    string Host,
    int Port,
    string? Username,
    string? PrivateKeyPath,
    string Terminal,
    TimeSpan KeepAlive,
    TimeSpan ConnectTimeout,
    string? ProxyJump = null,
    IReadOnlyList<PortForward>? Forwards = null,
    bool UseAgent = false)
{
    public const int DefaultPort = 22;
    public const string DefaultTerminal = "xterm-256color";

    public static SshOptions From(ConnectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var host = definition.Host?.Trim();
        if (string.IsNullOrEmpty(host))
        {
            throw new SshConnectionException("This connection has no host.");
        }

        var port = definition.Port ?? DefaultPort;
        if (port is < 1 or > 65535)
        {
            throw new SshConnectionException($"Port {port} is out of range (1-65535).");
        }

        var options = definition.Options;

        string? Text(string key) =>
            options is not null && options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;

        int Whole(string key, int fallback, int min, int max)
        {
            var text = Text(key);
            if (text is null)
            {
                return fallback;
            }

            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || number < min
                || number > max)
            {
                throw new SshConnectionException($"The option '{key}' must be a whole number from {min} to {max}.");
            }

            return number;
        }

        return new SshOptions(
            host,
            port,
            Text("username"),
            Text("privateKeyPath"),
            Text("term") ?? DefaultTerminal,
            TimeSpan.FromSeconds(Whole("keepAliveSeconds", 30, 0, 3600)),
            TimeSpan.FromSeconds(Whole("connectTimeoutSeconds", 15, 1, 300)),
            Text("proxyJump"),
            PortForward.ParseList(options is not null && options.TryGetValue("forwards", out var forwards) ? forwards : null),
            string.Equals(Text("useAgent"), "true", StringComparison.OrdinalIgnoreCase));
    }
}
