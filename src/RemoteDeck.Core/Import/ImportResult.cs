using RemoteDeck.Core.Connections;

namespace RemoteDeck.Core.Import;

/// <summary>What an importer found. Folders are listed parents first. Passwords are never imported.</summary>
public sealed record ImportResult(
    IReadOnlyList<FolderEntry> Folders,
    IReadOnlyList<ConnectionEntry> Connections,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Adds everything to the store. Entries that the store refuses are reported rather than aborting the rest.</summary>
    public IReadOnlyList<string> AddTo(ConnectionStore store)
    {
        var problems = new List<string>();
        var rejected = new HashSet<string>();

        foreach (var folder in Folders)
        {
            if (folder.ParentId is { } parent && rejected.Contains(parent))
            {
                rejected.Add(folder.Id);
                continue;
            }

            try
            {
                store.AddFolder(folder);
            }
            catch (CatalogException ex)
            {
                rejected.Add(folder.Id);
                problems.Add($"Folder '{folder.Name}': {ex.Message}");
            }
        }

        foreach (var connection in Connections)
        {
            try
            {
                store.AddConnection(
                    connection.FolderId is { } folderId && rejected.Contains(folderId)
                        ? connection with { FolderId = null }
                        : connection);
            }
            catch (CatalogException ex)
            {
                problems.Add($"'{connection.Name}': {ex.Message}");
            }
        }

        return problems;
    }
}

/// <summary>Collects folders and connections while an importer walks a file.</summary>
internal sealed class ImportBuilder
{
    private readonly List<FolderEntry> _folders = new();
    private readonly List<ConnectionEntry> _connections = new();
    private readonly List<string> _warnings = new();

    public string AddFolder(string name, string? parentId)
    {
        var id = ConnectionStore.NewId();
        _folders.Add(new FolderEntry(id, string.IsNullOrWhiteSpace(name) ? "Folder" : name.Trim(), parentId));
        return id;
    }

    public string? AddConnection(
        string name,
        string type,
        string host,
        int? port,
        string? folderId,
        Dictionary<string, string>? options = null,
        string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            Warn($"Skipped '{name}': it has no host.");
            return null;
        }

        if (port is < 1 or > 65535)
        {
            Warn($"'{name}': port {port} is out of range, using the default.");
            port = null;
        }

        options = options is { Count: > 0 } ? options : null;
        var id = ConnectionStore.NewId();
        _connections.Add(new ConnectionEntry(
            id,
            string.IsNullOrWhiteSpace(name) ? host.Trim() : name.Trim(),
            type,
            host.Trim(),
            port,
            folderId,
            Options: options,
            Notes: string.IsNullOrWhiteSpace(notes) ? null : notes));
        return id;
    }

    /// <summary>Sets one option on a connection added earlier.</summary>
    public void SetOption(string connectionId, string key, string value)
    {
        var index = _connections.FindIndex(c => c.Id == connectionId);
        if (index < 0)
        {
            return;
        }

        var existing = _connections[index];
        var options = existing.Options is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(existing.Options);
        options[key] = value;
        _connections[index] = existing with { Options = options };
    }

    public void Warn(string message) => _warnings.Add(message);

    public ImportResult Build() => new(_folders, _connections, _warnings);
}
