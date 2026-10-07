namespace RemoteDeck.Core.Import;

/// <summary>Reads a Microsoft <c>.rdp</c> file. Only the address, port and user name are used.</summary>
public static class RdpFileImporter
{
    public static ImportResult Parse(string text, string name)
    {
        var builder = new ImportBuilder();
        string? address = null;
        int? port = null;
        string? user = null;
        string? domain = null;

        foreach (var raw in text.Split('\n'))
        {
            // Lines look like "key:type:value"; the value may itself contain colons.
            var line = raw.Trim().TrimStart('﻿');
            var first = line.IndexOf(':');
            var second = first < 0 ? -1 : line.IndexOf(':', first + 1);
            if (second < 0)
            {
                continue;
            }

            var key = line[..first].Trim();
            var value = line[(second + 1)..].Trim();

            switch (key.ToLowerInvariant())
            {
                case "full address":
                    address = value;
                    break;
                case "server port":
                    port = int.TryParse(value, out var p) ? p : port;
                    break;
                case "username":
                    user = value;
                    break;
                case "domain":
                    domain = value;
                    break;
            }
        }

        if (address is not null && !address.StartsWith('[') && address.Count(c => c == ':') == 1)
        {
            var colon = address.IndexOf(':');
            if (int.TryParse(address[(colon + 1)..], out var embedded))
            {
                port ??= embedded;
                address = address[..colon];
            }
        }

        var options = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(user))
        {
            options["username"] = user;
        }

        if (!string.IsNullOrEmpty(domain))
        {
            options["domain"] = domain;
        }

        builder.AddConnection(name, "rdp", address ?? string.Empty, port == 3389 ? null : port, null, options);
        return builder.Build();
    }
}
