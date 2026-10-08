using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RemoteDeck.Plugin;

namespace RemoteDeck.Core.Connections;

internal sealed record CatalogDocument(int Version, List<FolderEntry>? Folders, List<ConnectionEntry>? Connections);

/// <summary>
/// The user's saved connections and the folders they sit in. Holds credential <i>references</i> only; the
/// secrets live in the vault, so this file is safe to sync or commit and stays searchable while the vault is locked.
///
/// Thread-safe. Everything handed out is an immutable snapshot.
/// </summary>
public sealed class ConnectionStore
{
    public const int FormatVersion = 1;

    private static readonly Regex ColorPattern = new("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly object _gate = new();
    private readonly Dictionary<string, FolderEntry> _folders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConnectionEntry> _connections = new(StringComparer.Ordinal);

    /// <summary>Raised after any change, so the host can save the file and refresh the tree.</summary>
    public event EventHandler? Changed;

    public static string NewId() => Guid.NewGuid().ToString("N");

    public IReadOnlyList<FolderEntry> Folders
    {
        get
        {
            lock (_gate)
            {
                return _folders.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
    }

    public IReadOnlyList<ConnectionEntry> Connections
    {
        get
        {
            lock (_gate)
            {
                return _connections.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
    }

    public IReadOnlyList<ConnectionEntry> Favorites
    {
        get
        {
            lock (_gate)
            {
                return _connections.Values
                    .Where(c => c.Favorite)
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }

    // ---- folders ----

    public void AddFolder(FolderEntry folder)
    {
        var entry = NormalizeFolder(folder);
        lock (_gate)
        {
            if (_folders.ContainsKey(entry.Id))
            {
                throw new CatalogException($"A folder with id '{entry.Id}' already exists.");
            }

            ValidateParent(entry);
            _folders[entry.Id] = entry;
        }

        RaiseChanged();
    }

    /// <summary>Renames, recolors or moves a folder. Moving it inside itself is refused.</summary>
    public void UpdateFolder(FolderEntry folder)
    {
        var entry = NormalizeFolder(folder);
        lock (_gate)
        {
            if (!_folders.ContainsKey(entry.Id))
            {
                throw new CatalogException($"No folder with id '{entry.Id}'.");
            }

            ValidateParent(entry);
            _folders[entry.Id] = entry;
        }

        RaiseChanged();
    }

    /// <summary>
    /// Moves a folder under another folder, or to the top level when <paramref name="newParentId"/> is null.
    /// Moving a folder into itself or one of its own subfolders is refused.
    /// </summary>
    public void MoveFolder(string id, string? newParentId)
    {
        lock (_gate)
        {
            if (!_folders.TryGetValue(id, out var folder))
            {
                throw new CatalogException($"No folder with id '{id}'.");
            }

            if (folder.ParentId == newParentId)
            {
                return;
            }

            var moved = folder with { ParentId = newParentId };
            ValidateParent(moved);
            _folders[id] = moved;
        }

        RaiseChanged();
    }

    /// <summary>Whether <see cref="MoveFolder"/> would accept this move (the target exists and is not inside the folder).</summary>
    public bool CanMoveFolder(string id, string? newParentId)
    {
        lock (_gate)
        {
            if (!_folders.ContainsKey(id))
            {
                return false;
            }

            var current = newParentId;
            var steps = 0;
            while (current is not null)
            {
                if (current == id || !_folders.TryGetValue(current, out var parent) || ++steps > _folders.Count)
                {
                    return false;
                }

                current = parent.ParentId;
            }

            return true;
        }
    }

    /// <summary>
    /// Removes a folder. With <paramref name="deleteContents"/> everything inside goes too; otherwise the
    /// contents move up to the folder's parent.
    /// </summary>
    public bool RemoveFolder(string id, bool deleteContents = false)
    {
        lock (_gate)
        {
            if (!_folders.TryGetValue(id, out var folder))
            {
                return false;
            }

            if (deleteContents)
            {
                var doomed = new HashSet<string>(StringComparer.Ordinal) { id };
                var queue = new Queue<string>();
                queue.Enqueue(id);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    foreach (var child in _folders.Values.Where(f => f.ParentId == current).ToArray())
                    {
                        if (doomed.Add(child.Id))
                        {
                            queue.Enqueue(child.Id);
                        }
                    }
                }

                foreach (var doomedId in doomed)
                {
                    _folders.Remove(doomedId);
                }

                foreach (var connection in _connections.Values
                             .Where(c => c.FolderId is not null && doomed.Contains(c.FolderId))
                             .ToArray())
                {
                    _connections.Remove(connection.Id);
                }
            }
            else
            {
                foreach (var child in _folders.Values.Where(f => f.ParentId == id).ToArray())
                {
                    _folders[child.Id] = child with { ParentId = folder.ParentId };
                }

                foreach (var connection in _connections.Values.Where(c => c.FolderId == id).ToArray())
                {
                    _connections[connection.Id] = connection with { FolderId = folder.ParentId };
                }

                _folders.Remove(id);
            }
        }

        RaiseChanged();
        return true;
    }

    public FolderEntry? FindFolder(string id)
    {
        lock (_gate)
        {
            return _folders.GetValueOrDefault(id);
        }
    }

    /// <summary>Display path such as "Prod / Web". Empty for a top-level item.</summary>
    public string FolderPath(string? folderId)
    {
        lock (_gate)
        {
            return FolderPathLocked(folderId);
        }
    }

    // ---- connections ----

    public void AddConnection(ConnectionEntry connection)
    {
        var entry = NormalizeConnection(connection);
        lock (_gate)
        {
            if (_connections.ContainsKey(entry.Id))
            {
                throw new CatalogException($"A connection with id '{entry.Id}' already exists.");
            }

            RequireFolder(entry.FolderId);
            _connections[entry.Id] = entry;
        }

        RaiseChanged();
    }

    public void UpdateConnection(ConnectionEntry connection)
    {
        var entry = NormalizeConnection(connection);
        lock (_gate)
        {
            if (!_connections.ContainsKey(entry.Id))
            {
                throw new CatalogException($"No connection with id '{entry.Id}'.");
            }

            RequireFolder(entry.FolderId);
            _connections[entry.Id] = entry;
        }

        RaiseChanged();
    }

    /// <summary>Moves a connection into a folder, or to the top level when <paramref name="folderId"/> is null.</summary>
    public void MoveConnection(string id, string? folderId)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(id, out var connection))
            {
                throw new CatalogException($"No connection with id '{id}'.");
            }

            if (connection.FolderId == folderId)
            {
                return;
            }

            RequireFolder(folderId);
            _connections[id] = connection with { FolderId = folderId };
        }

        RaiseChanged();
    }

    public bool RemoveConnection(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _connections.Remove(id);
        }

        if (removed)
        {
            RaiseChanged();
        }

        return removed;
    }

    public ConnectionEntry? FindConnection(string id)
    {
        lock (_gate)
        {
            return _connections.GetValueOrDefault(id);
        }
    }

    // ---- credentials ----

    /// <summary>
    /// The credential a connection actually uses: its own, otherwise the nearest folder above it that has one.
    /// Matches the signature the vault's credential broker expects.
    /// </summary>
    public string? CredentialIdFor(string connectionId)
    {
        lock (_gate)
        {
            return _connections.TryGetValue(connectionId, out var connection)
                ? CredentialIdForLocked(connection)
                : null;
        }
    }

    /// <summary>The plugin-facing definition of a connection, with the inherited credential filled in.</summary>
    public ConnectionDefinition? ResolveDefinition(string connectionId)
    {
        lock (_gate)
        {
            return _connections.TryGetValue(connectionId, out var connection)
                ? connection.ToDefinition(CredentialIdForLocked(connection))
                : null;
        }
    }

    /// <summary>Every credential id in use, so the UI can spot vault entries nothing refers to.</summary>
    public IReadOnlyCollection<string> ReferencedCredentialIds()
    {
        lock (_gate)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var folder in _folders.Values)
            {
                if (folder.CredentialId is not null)
                {
                    ids.Add(folder.CredentialId);
                }
            }

            foreach (var connection in _connections.Values)
            {
                if (connection.CredentialId is not null)
                {
                    ids.Add(connection.CredentialId);
                }
            }

            return ids;
        }
    }

    // ---- search and tree ----

    /// <summary>
    /// Finds connections matching every word in <paramref name="query"/> (name, host, tags, folder, type).
    /// An empty query lists favorites first, then everything else by name.
    /// </summary>
    public IReadOnlyList<SearchResult> Search(string? query, int maxResults = 50)
    {
        var words = (query ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lock (_gate)
        {
            var results = new List<SearchResult>();
            foreach (var connection in _connections.Values)
            {
                var path = FolderPathLocked(connection.FolderId);
                var score = words.Length == 0
                    ? (connection.Favorite ? 1 : 0)
                    : ConnectionSearch.Score(connection, path, words);

                if (words.Length == 0 || score > 0)
                {
                    results.Add(new SearchResult(connection, path, score));
                }
            }

            return results
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.Connection.Name, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, maxResults))
                .ToArray();
        }
    }

    /// <summary>The sidebar tree: in each folder, subfolders first, then connections, both by name.</summary>
    public IReadOnlyList<CatalogNode> BuildTree()
    {
        lock (_gate)
        {
            var foldersByParent = _folders.Values
                .GroupBy(f => f.ParentId ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var connectionsByFolder = _connections.Values
                .GroupBy(c => c.FolderId ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            return Build(string.Empty);

            IReadOnlyList<CatalogNode> Build(string key)
            {
                var nodes = new List<CatalogNode>();
                if (foldersByParent.TryGetValue(key, out var folders))
                {
                    foreach (var folder in folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        nodes.Add(new FolderNode(folder, Build(folder.Id)));
                    }
                }

                if (connectionsByFolder.TryGetValue(key, out var connections))
                {
                    foreach (var connection in connections.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        nodes.Add(new ConnectionNode(connection));
                    }
                }

                return nodes;
            }
        }
    }

    // ---- persistence ----

    public string Serialize()
    {
        CatalogDocument document;
        lock (_gate)
        {
            document = new CatalogDocument(
                FormatVersion,
                _folders.Values
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.Id, StringComparer.Ordinal)
                    .ToList(),
                _connections.Values
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(c => c.Id, StringComparer.Ordinal)
                    .ToList());
        }

        return JsonSerializer.Serialize(document, JsonOptions);
    }

    /// <summary>Reads a connection file. Throws <see cref="CatalogException"/> with a readable reason if it is invalid.</summary>
    public static ConnectionStore Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        CatalogDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<CatalogDocument>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new CatalogException($"The connection file is not valid: {ex.Message}", ex);
        }

        if (document is null)
        {
            throw new CatalogException("The connection file is empty.");
        }

        if (document.Version != FormatVersion)
        {
            throw new CatalogException($"Unsupported connection file version {document.Version}.");
        }

        var store = new ConnectionStore();

        foreach (var folder in document.Folders ?? new List<FolderEntry>())
        {
            var entry = NormalizeFolder(folder);
            if (!store._folders.TryAdd(entry.Id, entry))
            {
                throw new CatalogException($"Duplicate folder id '{entry.Id}'.");
            }
        }

        foreach (var folder in store._folders.Values)
        {
            store.ValidateParent(folder);
        }

        foreach (var connection in document.Connections ?? new List<ConnectionEntry>())
        {
            var entry = NormalizeConnection(connection);
            if (!store._connections.TryAdd(entry.Id, entry))
            {
                throw new CatalogException($"Duplicate connection id '{entry.Id}'.");
            }

            store.RequireFolder(entry.FolderId);
        }

        return store;
    }

    // ---- helpers (call with the lock held where they touch the dictionaries) ----

    private string? CredentialIdForLocked(ConnectionEntry connection)
    {
        if (connection.CredentialId is not null)
        {
            return connection.CredentialId;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var folderId = connection.FolderId;
        while (folderId is not null && visited.Add(folderId) && _folders.TryGetValue(folderId, out var folder))
        {
            if (folder.CredentialId is not null)
            {
                return folder.CredentialId;
            }

            folderId = folder.ParentId;
        }

        return null;
    }

    private string FolderPathLocked(string? folderId)
    {
        var names = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (folderId is not null && visited.Add(folderId) && _folders.TryGetValue(folderId, out var folder))
        {
            names.Add(folder.Name);
            folderId = folder.ParentId;
        }

        names.Reverse();
        return string.Join(" / ", names);
    }

    private void RequireFolder(string? folderId)
    {
        if (folderId is not null && !_folders.ContainsKey(folderId))
        {
            throw new CatalogException($"Folder '{folderId}' does not exist.");
        }
    }

    /// <summary>The parent must exist, and walking up from it must never reach the folder itself.</summary>
    private void ValidateParent(FolderEntry folder)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { folder.Id };
        var current = folder.ParentId;
        while (current is not null)
        {
            if (!visited.Add(current))
            {
                throw new CatalogException($"Folder '{folder.Name}' cannot be placed inside itself.");
            }

            if (!_folders.TryGetValue(current, out var parent))
            {
                throw new CatalogException($"Parent folder '{current}' does not exist.");
            }

            current = parent.ParentId;
        }
    }

    private void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static FolderEntry NormalizeFolder(FolderEntry? folder)
    {
        if (folder is null)
        {
            throw new CatalogException("A folder entry is missing.");
        }

        return folder with
        {
            Id = RequireText(folder.Id, "folder id"),
            Name = RequireText(folder.Name, "folder name"),
            ParentId = Blank(folder.ParentId),
            CredentialId = Blank(folder.CredentialId),
            Color = CheckColor(folder.Color)
        };
    }

    private static ConnectionEntry NormalizeConnection(ConnectionEntry? connection)
    {
        if (connection is null)
        {
            throw new CatalogException("A connection entry is missing.");
        }

        if (connection.Port is < 1 or > 65535)
        {
            throw new CatalogException($"Port {connection.Port} is out of range (1-65535).");
        }

        return connection with
        {
            Id = RequireText(connection.Id, "connection id"),
            Name = RequireText(connection.Name, "connection name"),
            Type = RequireText(connection.Type, "connection type").ToLowerInvariant(),
            Host = RequireText(connection.Host, "host"),
            FolderId = Blank(connection.FolderId),
            CredentialId = Blank(connection.CredentialId),
            Tags = NormalizeTags(connection.Tags),
            Color = CheckColor(connection.Color),
            Icon = Blank(connection.Icon),
            Notes = string.IsNullOrWhiteSpace(connection.Notes) ? null : connection.Notes
        };
    }

    private static IReadOnlyList<string>? NormalizeTags(IReadOnlyList<string>? tags)
    {
        if (tags is null)
        {
            return null;
        }

        var cleaned = tags
            .Select(t => t?.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string RequireText(string? value, string what)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new CatalogException($"A {what} is required.");
        }

        return text;
    }

    private static string? Blank(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string? CheckColor(string? color)
    {
        var text = Blank(color);
        if (text is not null && !ColorPattern.IsMatch(text))
        {
            throw new CatalogException($"Color '{text}' must look like #RRGGBB.");
        }

        return text;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }
}
