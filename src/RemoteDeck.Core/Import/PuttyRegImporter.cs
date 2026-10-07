using System.Globalization;
using System.Text.RegularExpressions;

namespace RemoteDeck.Core.Import;

/// <summary>Reads saved sessions from a PuTTY registry export (<c>reg export HKCU\Software\SimonTatham\PuTTY\Sessions putty.reg</c>).</summary>
public static partial class PuttyRegImporter
{
    [GeneratedRegex(@"^\[HKEY_[^\\]+\\Software\\SimonTatham\\PuTTY\\Sessions\\(?<name>[^\]\\]+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex SessionHeader();

    [GeneratedRegex("^\"(?<key>[^\"]+)\"=(?<value>.*)$")]
    private static partial Regex ValueLine();

    public static ImportResult Parse(string text, string folderName = "PuTTY")
    {
        var builder = new ImportBuilder();
        string? folderId = null;
        string? name = null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (name is null)
            {
                return;
            }

            var protocol = values.GetValueOrDefault("Protocol") ?? "ssh";
            var host = values.GetValueOrDefault("HostName") ?? string.Empty;
            string? user = values.GetValueOrDefault("UserName");

            // PuTTY accepts "user@host" in the host box.
            var at = host.LastIndexOf('@');
            if (at > 0)
            {
                user = string.IsNullOrEmpty(user) ? host[..at] : user;
                host = host[(at + 1)..];
            }

            if (!protocol.Equals("ssh", StringComparison.OrdinalIgnoreCase))
            {
                builder.Warn($"Skipped '{name}': only SSH sessions are imported (it uses {protocol}).");
            }
            else
            {
                int? port = values.TryGetValue("PortNumber", out var p) && int.TryParse(p, out var n) ? n : null;
                var options = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(user))
                {
                    options["username"] = user;
                }

                if (values.GetValueOrDefault("PublicKeyFile") is { Length: > 0 } key)
                {
                    options["privateKeyPath"] = key;
                }

                folderId ??= builder.AddFolder(folderName, null);
                builder.AddConnection(name, "ssh", host, port, folderId, options);
            }

            name = null;
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.StartsWith('['))
            {
                Flush();
                var header = SessionHeader().Match(line);
                var decoded = header.Success ? Uri.UnescapeDataString(header.Groups["name"].Value) : null;
                name = decoded is null || decoded == "Default Settings" ? null : decoded;
                continue;
            }

            if (name is null)
            {
                continue;
            }

            var match = ValueLine().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var value = match.Groups["value"].Value;
            if (value.StartsWith("dword:", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(value.AsSpan(6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number))
            {
                values[match.Groups["key"].Value] = number.ToString(CultureInfo.InvariantCulture);
            }
            else if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                values[match.Groups["key"].Value] = value[1..^1].Replace("\\\\", "\\").Replace("\\\"", "\"");
            }
        }

        Flush();
        return builder.Build();
    }
}
