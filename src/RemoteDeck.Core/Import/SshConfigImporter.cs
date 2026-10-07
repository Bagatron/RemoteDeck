using System.Globalization;

namespace RemoteDeck.Core.Import;

/// <summary>Reads an OpenSSH client config (<c>~/.ssh/config</c>). Wildcard hosts and Match blocks are skipped.</summary>
public static class SshConfigImporter
{
    private sealed class Block
    {
        public List<string> Aliases { get; } = new();

        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <param name="homeDirectory">Used to expand a leading "~" in IdentityFile.</param>
    public static ImportResult Parse(string text, string homeDirectory, string folderName = "SSH config")
    {
        var builder = new ImportBuilder();
        var blocks = new List<Block>();
        Block? current = null;
        var ignoring = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var split = line.IndexOfAny(new[] { ' ', '\t', '=' });
            var keyword = split < 0 ? line : line[..split];
            var value = split < 0 ? string.Empty : line[(split + 1)..].Trim().TrimStart('=').Trim();

            if (keyword.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                ignoring = false;
                current = new Block();
                foreach (var pattern in SplitWords(value))
                {
                    if (pattern.IndexOfAny(new[] { '*', '?', '!' }) >= 0)
                    {
                        continue;
                    }

                    current.Aliases.Add(pattern);
                }

                blocks.Add(current);
            }
            else if (keyword.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                ignoring = true;
                current = null;
                builder.Warn("A Match block was skipped.");
            }
            else if (!ignoring && current is not null && !current.Values.ContainsKey(keyword))
            {
                // OpenSSH uses the first value it sees for each keyword.
                current.Values[keyword] = Unquote(value);
            }
        }

        string? folderId = null;
        foreach (var block in blocks)
        {
            foreach (var alias in block.Aliases)
            {
                folderId ??= builder.AddFolder(folderName, null);
                var values = block.Values;
                var host = values.GetValueOrDefault("HostName") is { Length: > 0 } hostName
                    ? hostName.Replace("%h", alias, StringComparison.Ordinal)
                    : alias;

                int? port = null;
                if (values.TryGetValue("Port", out var portText))
                {
                    if (int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    {
                        port = parsed;
                    }
                    else
                    {
                        builder.Warn($"'{alias}': port '{portText}' is not a number, using the default.");
                    }
                }

                var options = new Dictionary<string, string>();
                if (values.GetValueOrDefault("User") is { Length: > 0 } user)
                {
                    options["username"] = user;
                }

                if (values.GetValueOrDefault("IdentityFile") is { Length: > 0 } key)
                {
                    options["privateKeyPath"] = ExpandHome(key, homeDirectory);
                }

                if (values.ContainsKey("ProxyJump") || values.ContainsKey("ProxyCommand"))
                {
                    builder.Warn($"'{alias}': ProxyJump/ProxyCommand is not supported yet, so it will connect directly.");
                }

                builder.AddConnection(alias, "ssh", host, port, folderId, options);
            }
        }

        return builder.Build();
    }

    private static string ExpandHome(string path, string home) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? home.TrimEnd('/', '\\') + path[1..]
            : path;

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    private static IEnumerable<string> SplitWords(string value) =>
        value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(Unquote);
}
