using System.Text;

namespace RemoteDeck.Core.Connections;

/// <summary>Saves and loads the connection file without ever leaving a half-written file behind.</summary>
public static class CatalogStorage
{
    /// <summary>Writes a temp file, flushes it, then swaps it into place. The previous version is kept as <c>.bak</c>.</summary>
    public static void Save(ConnectionStore store, string path)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var temporary = fullPath + ".tmp";
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(store.Serialize());
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

    public static ConnectionStore Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ConnectionStore.Deserialize(File.ReadAllText(path));
    }

    /// <summary>First run: a missing file gives an empty store instead of an error.</summary>
    public static ConnectionStore LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(path) ? Load(path) : new ConnectionStore();
    }
}
