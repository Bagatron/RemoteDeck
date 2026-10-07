using System.Xml.Linq;
using RemoteDeck.Core.Connections;

namespace RemoteDeck.Core.Import;

/// <summary>Reads an mRemoteNG <c>confCons.xml</c>. Stored passwords are not imported, and an encrypted file is refused.</summary>
public static class MRemoteNgImporter
{
    public static ImportResult Parse(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new CatalogException("This is not a valid mRemoteNG file: " + ex.Message, ex);
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "Connections")
        {
            throw new CatalogException("This is not an mRemoteNG connections file.");
        }

        if (string.Equals((string?)root.Attribute("FullFileEncryption"), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new CatalogException("This mRemoteNG file is fully encrypted. Save a copy without file encryption and import that.");
        }

        var builder = new ImportBuilder();
        Walk(root, null, builder);
        return builder.Build();
    }

    private static void Walk(XElement parent, string? folderId, ImportBuilder builder)
    {
        foreach (var node in parent.Elements().Where(e => e.Name.LocalName == "Node"))
        {
            var name = (string?)node.Attribute("Name") ?? string.Empty;
            if (string.Equals((string?)node.Attribute("Type"), "Container", StringComparison.OrdinalIgnoreCase))
            {
                Walk(node, builder.AddFolder(name, folderId), builder);
                continue;
            }

            var protocol = ((string?)node.Attribute("Protocol") ?? string.Empty).ToUpperInvariant();
            var type = protocol switch
            {
                "SSH1" or "SSH2" => "ssh",
                "RDP" => "rdp",
                "VNC" => "vnc",
                "HTTP" or "HTTPS" => "web",
                "TELNET" => "telnet",
                _ => null,
            };

            if (type is null)
            {
                builder.Warn($"Skipped '{name}': the {protocol} protocol is not supported.");
                continue;
            }

            var host = (string?)node.Attribute("Hostname") ?? string.Empty;
            if (type == "web" && !host.Contains("://", StringComparison.Ordinal))
            {
                host = protocol.ToLowerInvariant() + "://" + host;
            }

            int? port = int.TryParse((string?)node.Attribute("Port"), out var p) && p > 0 && !IsDefault(type, p) ? p : null;
            var options = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty((string?)node.Attribute("Username")))
            {
                options["username"] = (string)node.Attribute("Username")!;
            }

            if (!string.IsNullOrEmpty((string?)node.Attribute("Domain")))
            {
                options["domain"] = (string)node.Attribute("Domain")!;
            }

            builder.AddConnection(name, type, host, port, folderId, options, (string?)node.Attribute("Descr"));
        }
    }

    private static bool IsDefault(string type, int port) =>
        (type, port) is ("ssh", 22) or ("rdp", 3389) or ("vnc", 5900) or ("telnet", 23);
}

/// <summary>Reads a Remote Desktop Connection Manager <c>.rdg</c> file. Stored passwords are not imported.</summary>
public static class RdcManImporter
{
    public static ImportResult Parse(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new CatalogException("This is not a valid RDCMan file: " + ex.Message, ex);
        }

        var file = document.Root?.Element("file");
        if (document.Root?.Name.LocalName != "RDCMan" || file is null)
        {
            throw new CatalogException("This is not an RDCMan .rdg file.");
        }

        var builder = new ImportBuilder();
        var rootName = file.Element("properties")?.Element("name")?.Value;
        var rootFolder = builder.AddFolder(string.IsNullOrWhiteSpace(rootName) ? "RDCMan" : rootName, null);
        Walk(file, rootFolder, builder);
        return builder.Build();
    }

    private static void Walk(XElement parent, string folderId, ImportBuilder builder)
    {
        foreach (var group in parent.Elements("group"))
        {
            Walk(group, builder.AddFolder(group.Element("properties")?.Element("name")?.Value ?? "Group", folderId), builder);
        }

        foreach (var server in parent.Elements("server"))
        {
            var props = server.Element("properties");
            var host = props?.Element("name")?.Value ?? string.Empty;
            var display = props?.Element("displayName")?.Value;
            var options = new Dictionary<string, string>();

            var credentials = server.Element("logonCredentials");
            if (!string.IsNullOrWhiteSpace(credentials?.Element("userName")?.Value))
            {
                options["username"] = credentials!.Element("userName")!.Value;
            }

            if (!string.IsNullOrWhiteSpace(credentials?.Element("domain")?.Value))
            {
                options["domain"] = credentials!.Element("domain")!.Value;
            }

            int? port = int.TryParse(server.Element("connectionSettings")?.Element("port")?.Value, out var p) && p != 3389 ? p : null;
            builder.AddConnection(string.IsNullOrWhiteSpace(display) ? host : display, "rdp", host, port, folderId, options, props?.Element("comment")?.Value);
        }
    }
}
