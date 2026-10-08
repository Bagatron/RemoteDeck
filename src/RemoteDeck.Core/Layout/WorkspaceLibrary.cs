namespace RemoteDeck.Core.Layout;

/// <summary>A workspace file found in the library. Exactly one of <see cref="Workspace"/> and <see cref="Error"/> is set.</summary>
public sealed record WorkspaceFile(string Path, Workspace? Workspace, string? Error);

/// <summary>A folder of workspace JSON files, one file per workspace.</summary>
public sealed class WorkspaceLibrary
{
    private readonly string _folder;

    public WorkspaceLibrary(string folder)
    {
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));
    }

    public string Folder => _folder;

    /// <summary>Every workspace file, sorted by name. A file that cannot be read is listed with its error instead of being skipped silently.</summary>
    public IReadOnlyList<WorkspaceFile> LoadAll()
    {
        if (!Directory.Exists(_folder))
        {
            return Array.Empty<WorkspaceFile>();
        }

        var files = new List<WorkspaceFile>();
        foreach (var path in Directory.EnumerateFiles(_folder, "*.json"))
        {
            try
            {
                files.Add(new WorkspaceFile(path, LayoutSerializer.DeserializeWorkspace(File.ReadAllText(path)), null));
            }
            catch (Exception ex) when (ex is LayoutException or IOException or UnauthorizedAccessException)
            {
                files.Add(new WorkspaceFile(path, null, ex.Message));
            }
        }

        return files
            .OrderBy(f => f.Workspace?.Name ?? System.IO.Path.GetFileNameWithoutExtension(f.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Saves a workspace, replacing any file already holding one of the same name. Returns the file path.</summary>
    public string Save(Workspace workspace)
    {
        var json = LayoutSerializer.SerializeWorkspace(workspace);
        Directory.CreateDirectory(_folder);

        var existing = LoadAll().FirstOrDefault(f => string.Equals(f.Workspace?.Name, workspace.Name, StringComparison.OrdinalIgnoreCase));
        var path = existing?.Path ?? System.IO.Path.Combine(_folder, FileNameFor(workspace.Name));

        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>Deletes the workspace with this name. Returns false when there is none.</summary>
    public bool Delete(string name)
    {
        var match = LoadAll().FirstOrDefault(f => string.Equals(f.Workspace?.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return false;
        }

        File.Delete(match.Path);
        return true;
    }

    /// <summary>A safe file name for a workspace name, unique within the folder.</summary>
    internal string FileNameFor(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim('.', ' ');
        if (cleaned.Length == 0)
        {
            cleaned = "workspace";
        }

        var candidate = cleaned + ".json";
        for (var i = 2; File.Exists(System.IO.Path.Combine(_folder, candidate)); i++)
        {
            candidate = $"{cleaned} ({i}).json";
        }

        return candidate;
    }
}
