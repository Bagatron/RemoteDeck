namespace RemoteDeck.Core.Import;

/// <summary>
/// Reads a MobaXterm sessions export (<c>.mxtsessions</c>) or the <c>[Bookmarks]</c> sections of <c>MobaXterm.ini</c>.
/// SSH, Telnet and RDP sessions are imported with their folders. Other session types are skipped with a note,
/// and passwords are never imported.
/// </summary>
public static class MobaXtermImporter
{
    private const string RootFolder = "MobaXterm";

    public static bool LooksLikeMobaXterm(string text) =>
        text.Contains("[Bookmarks", StringComparison.OrdinalIgnoreCase);

    public static ImportResult Parse(string text)
    {
        var builder = new ImportBuilder();
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? rootId = null;
        var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var labels = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        string FolderFor(string path)
        {
            rootId ??= builder.AddFolder(RootFolder, null);
            var parent = rootId;
            var built = string.Empty;
            foreach (var part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                built = built.Length == 0 ? part : built + "\\" + part;
                if (!folders.TryGetValue(built, out var id))
                {
                    id = builder.AddFolder(part, parent);
                    folders[built] = id;
                }

                parent = id;
            }

            return parent;
        }

        var inBookmarks = false;
        var subRep = string.Empty;
        var pending = new List<(string Name, string Value)>();

        void Flush()
        {
            if (pending.Count == 0)
            {
                return;
            }

            var folderId = FolderFor(subRep);
            foreach (var (name, value) in pending)
            {
                AddSession(builder, name, value, folderId, skipped, labels);
            }

            pending.Clear();
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line[0] == ';')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                Flush();
                inBookmarks = line.StartsWith("[Bookmarks", StringComparison.OrdinalIgnoreCase);
                subRep = string.Empty;
                continue;
            }

            if (!inBookmarks)
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..];
            if (key.Equals("SubRep", StringComparison.OrdinalIgnoreCase))
            {
                subRep = value.Trim();
            }
            else if (key.Equals("ImgNum", StringComparison.OrdinalIgnoreCase))
            {
                // The folder icon number; nothing to import.
            }
            else
            {
                pending.Add((key, value));
            }
        }

        Flush();

        if (labels.Count > 0)
        {
            builder.Warn(
                "MobaXterm keeps these logins in its password manager, so only their names are known: "
                + string.Join(", ", labels)
                + ". They were used as the user names; check them if a login fails.");
        }

        foreach (var (code, count) in skipped)
        {
            builder.Warn($"{count} MobaXterm session(s) of type {code} were skipped: only SSH, Telnet and RDP are imported.");
        }

        return builder.Build();
    }

    private static void AddSession(ImportBuilder builder, string name, string value, string folderId, SortedDictionary<string, int> skipped, SortedSet<string> labels)
    {
        // A session looks like "#109#0%host%22%user%...": a type marker, then fields separated by '%'.
        var fields = value.Split('%');
        var marker = fields[0].Split('#', StringSplitOptions.RemoveEmptyEntries);
        if (marker.Length == 0 || !value.TrimStart().StartsWith('#'))
        {
            builder.Warn($"Skipped '{name}': not a session line.");
            return;
        }

        var code = marker[0];
        var (type, defaultPort) = code switch
        {
            "109" => ("ssh", 22),
            "98" => ("telnet", 23),
            "91" => ("rdp", 3389),
            _ => (string.Empty, 0)
        };

        if (type.Length == 0)
        {
            skipped[code] = skipped.GetValueOrDefault(code) + 1;
            return;
        }

        var host = fields.Length > 1 ? fields[1].Trim() : string.Empty;
        int? port = fields.Length > 2 && int.TryParse(fields[2], out var parsed) ? parsed : null;
        if (port == defaultPort)
        {
            port = null;
        }

        var user = fields.Length > 3 ? fields[3].Trim() : string.Empty;
        var options = new Dictionary<string, string>();

        // "[name]" is the name of an entry in MobaXterm's password manager, not a user name. "<default>" means none.
        if (user.Length > 2 && user[0] == '[' && user[^1] == ']')
        {
            user = user[1..^1].Trim();
            if (user.Length > 0)
            {
                labels.Add(user);
            }
        }

        if (user.Length > 0 && !user.StartsWith('<'))
        {
            options["username"] = user;
        }

        builder.AddConnection(name, type, host, port, folderId, options);
    }
}
