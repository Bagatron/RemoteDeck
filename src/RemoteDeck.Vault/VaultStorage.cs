using System.Text;

namespace RemoteDeck.Vault;

/// <summary>Saves and loads the vault file without ever leaving a half-written file behind.</summary>
public static class VaultStorage
{
    /// <summary>
    /// Writes to a temporary file, flushes it to disk, then swaps it into place.
    /// The previous version is kept next to it as <c>.bak</c>.
    /// </summary>
    public static void Save(CredentialVault vault, string path)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var temporary = fullPath + ".tmp";
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(vault.Serialize());
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(fullPath))
        {
            File.Replace(temporary, fullPath, fullPath + ".bak");
        }
        else
        {
            File.Move(temporary, fullPath);
        }
    }

    public static CredentialVault Load(string path, ReadOnlySpan<char> masterPassword, IKeyDerivation? kdf = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return CredentialVault.Open(File.ReadAllText(path), masterPassword, kdf);
    }
}
