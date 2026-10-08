using System.Globalization;

namespace RemoteDeck.Core.Import;

/// <summary>Reads an OpenSSH client config (<c>~/.ssh/config</c>). Wildcard hosts and Match blocks are skipped.</summary>
public static class SshConfigImporter
{
    private sealed class Block
    {
        public List<string> Aliases { get; } = new();

        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>LocalForward, RemoteForward and DynamicForward may repeat, so every one is kept.</summary>
        public List<(string Keyword, string Value)> Forwards { get; } = new();
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
            else if (!ignoring && current is not null
                     && (keyword.Equals("LocalForward", StringComparison.OrdinalIgnoreCase)
                         || keyword.Equals("RemoteForward", StringComparison.OrdinalIgnoreCase)
                         || keyword.Equals("DynamicForward", StringComparison.OrdinalIgnoreCase)))
            {
                current.Forwards.Add((keyword, Unquote(value)));
            }
            else if (!ignoring && current is not null && !current.Values.ContainsKey(keyword))
            {
                // OpenSSH uses the first value it sees for each keyword.
                current.Values[keyword] = Unquote(value);
            }
        }

        string? folderId = null;
        var idsByAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var jumps = new List<(string Id, string Alias, string Jump)>();
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

                var forwards = new List<string>();
                foreach (var (keyword, value) in block.Forwards)
                {
                    if (TryConvertForward(keyword, value, out var spec))
                    {
                        forwards.Add(spec);
                    }
                    else
                    {
                        builder.Warn($"'{alias}': {keyword} '{value}' could not be read, so it was skipped.");
                    }
                }

                if (forwards.Count > 0)
                {
                    options["forwards"] = string.Join("\n", forwards);
                }

                if (values.ContainsKey("ProxyCommand"))
                {
                    builder.Warn($"'{alias}': ProxyCommand is not supported, so it will connect directly.");
                }

                var id = builder.AddConnection(alias, "ssh", host, port, folderId, options);
                if (id is not null)
                {
                    idsByAlias.TryAdd(alias, id);
                    if (values.GetValueOrDefault("ProxyJump") is { Length: > 0 } jump
                        && !values.ContainsKey("ProxyCommand"))
                    {
                        jumps.Add((id, alias, jump));
                    }
                }
            }
        }

        // A jump host can only be linked once every host in the file exists.
        foreach (var (id, alias, jump) in jumps)
        {
            var name = jump.Trim();
            if (name.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.Contains(','))
            {
                builder.Warn($"'{alias}': a chain of ProxyJump hosts is not imported. Set each host's own jump host instead; it will connect directly for now.");
            }
            else if (idsByAlias.TryGetValue(name, out var jumpId) && jumpId != id)
            {
                builder.SetOption(id, "proxyJump", jumpId);
            }
            else
            {
                builder.Warn($"'{alias}': ProxyJump '{name}' is not a host in this file, so it will connect directly. Import or add that host, then choose it as the jump host.");
            }
        }

        return builder.Build();
    }

    /// <summary>Converts "8080 host:80" (or "127.0.0.1:8080 host:80", or "1080" for DynamicForward) to RemoteDeck's L:/R:/D: form.</summary>
    private static bool TryConvertForward(string keyword, string value, out string spec)
    {
        spec = string.Empty;
        var words = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        static bool Port(string text, out string port)
        {
            var ok = int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
                     && n is >= 1 and <= 65535;
            port = ok ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            return ok;
        }

        // The listening side may carry a bind address ("127.0.0.1:8080"); only its port matters here.
        static string LastPart(string text) => text[(text.LastIndexOf(':') + 1)..];

        if (keyword.Equals("DynamicForward", StringComparison.OrdinalIgnoreCase))
        {
            if (words.Length == 1 && Port(LastPart(words[0]), out var dynamicPort))
            {
                spec = $"D:{dynamicPort}";
                return true;
            }

            return false;
        }

        if (words.Length != 2 || !Port(LastPart(words[0]), out var bind))
        {
            return false;
        }

        var colon = words[1].LastIndexOf(':');
        if (colon <= 0 || !Port(words[1][(colon + 1)..], out var targetPort))
        {
            return false;
        }

        var prefix = keyword.Equals("LocalForward", StringComparison.OrdinalIgnoreCase) ? "L" : "R";
        spec = $"{prefix}:{bind}:{words[1][..colon]}:{targetPort}";
        return true;
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
