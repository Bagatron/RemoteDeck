using RemoteDeck.Core.Connections;

namespace RemoteDeck.Core.Import;

/// <summary>Picks the right importer for a file by its name and content.</summary>
public static class ConnectionImport
{
    public const string FileDialogFilter =
        "Connection files|config;*.reg;*.rdp;*.rdg;*.xml;*.mxtsessions;*.ini|OpenSSH config|config|PuTTY export (.reg)|*.reg|Remote Desktop (.rdp)|*.rdp|RDCMan (.rdg)|*.rdg|mRemoteNG (.xml)|*.xml|MobaXterm (.mxtsessions or .ini)|*.mxtsessions;*.ini|All files|*.*";

    public static ImportResult FromFile(string path, string homeDirectory)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CatalogException($"Could not read '{path}': {ex.Message}", ex);
        }

        var file = Path.GetFileName(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".reg" => PuttyRegImporter.Parse(text),
            ".rdp" => RdpFileImporter.Parse(text, Path.GetFileNameWithoutExtension(path)),
            ".rdg" => RdcManImporter.Parse(text),
            ".xml" => MRemoteNgImporter.Parse(text),
            ".mxtsessions" => MobaXtermImporter.Parse(text),
            ".ini" when MobaXtermImporter.LooksLikeMobaXterm(text) => MobaXtermImporter.Parse(text),
            _ when file.Equals("config", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)
                => SshConfigImporter.Parse(text, homeDirectory),
            _ => throw new CatalogException("Unrecognised file. Use an OpenSSH config, a PuTTY .reg export, an .rdp, an .rdg, an mRemoteNG .xml or a MobaXterm .mxtsessions."),
        };
    }
}
