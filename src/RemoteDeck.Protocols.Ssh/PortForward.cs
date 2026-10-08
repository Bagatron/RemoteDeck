using System.Globalization;

namespace RemoteDeck.Protocols.Ssh;

internal enum PortForwardKind
{
    /// <summary>A port on this computer that leads to a host the server can reach (OpenSSH -L).</summary>
    Local,

    /// <summary>A port on the server that leads back to a host this computer can reach (OpenSSH -R).</summary>
    Remote,

    /// <summary>A SOCKS proxy on this computer that sends everything through the server (OpenSSH -D).</summary>
    Dynamic,
}

/// <summary>
/// One port forward from the <c>forwards</c> option. Listeners are always bound to 127.0.0.1, so a forward is
/// never exposed to the rest of the network by accident.
/// </summary>
internal sealed record PortForward(PortForwardKind Kind, int BindPort, string? Host, int? Port)
{
    /// <summary>
    /// Parses forwards separated by new lines or semicolons: <c>L:8080:db.internal:5432</c>,
    /// <c>R:9000:localhost:3000</c> and <c>D:1080</c>.
    /// </summary>
    public static IReadOnlyList<PortForward> ParseList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<PortForward>();
        }

        var forwards = new List<PortForward>();
        foreach (var spec in text.Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            forwards.Add(Parse(spec));
        }

        var duplicate = forwards
            .Where(f => f.Kind != PortForwardKind.Remote)
            .GroupBy(f => f.BindPort)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new SshConnectionException($"Two port forwards listen on local port {duplicate.Key}.");
        }

        return forwards;
    }

    public static PortForward Parse(string spec)
    {
        var parts = spec.Split(':', StringSplitOptions.TrimEntries);
        var kind = parts[0].ToUpperInvariant() switch
        {
            "L" => PortForwardKind.Local,
            "R" => PortForwardKind.Remote,
            "D" => PortForwardKind.Dynamic,
            _ => throw Bad(spec),
        };

        if (kind == PortForwardKind.Dynamic)
        {
            return parts.Length == 2 ? new PortForward(kind, ParsePort(parts[1], spec), null, null) : throw Bad(spec);
        }

        if (parts.Length != 4 || parts[2].Length == 0)
        {
            throw Bad(spec);
        }

        return new PortForward(kind, ParsePort(parts[1], spec), parts[2], ParsePort(parts[3], spec));
    }

    public override string ToString() => Kind switch
    {
        PortForwardKind.Local => $"L:{BindPort}:{Host}:{Port}",
        PortForwardKind.Remote => $"R:{BindPort}:{Host}:{Port}",
        _ => $"D:{BindPort}",
    };

    private static int ParsePort(string text, string spec) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? port
            : throw Bad(spec);

    private static SshConnectionException Bad(string spec) =>
        new($"'{spec}' is not a valid port forward. Use L:8080:host:80, R:9000:host:3000 or D:1080, one per line.");
}
