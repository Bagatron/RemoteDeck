using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteDeck.Core.Notes;

/// <summary>The sticky-note colours a tab can have, the muted variants for dark themes, and the text colours.</summary>
public static class NoteColors
{
    public static readonly IReadOnlyList<string> Names = new[]
    {
        "yellow", "pink", "mint", "blue", "lavender", "peach", "lemon", "coral", "lime", "aqua", "orchid", "orange",
    };

    private static readonly Dictionary<string, (string Light, string Dark)> Shades = new(StringComparer.OrdinalIgnoreCase)
    {
        ["yellow"] = ("#FFF6B8", "#5A5532"),
        ["pink"] = ("#FFD9E4", "#5A3643"),
        ["mint"] = ("#D4F5E2", "#2F5446"),
        ["blue"] = ("#D6E8FF", "#2F4560"),
        ["lavender"] = ("#E6DCFF", "#443B66"),
        ["peach"] = ("#FFE0C7", "#5C4130"),
        ["lemon"] = ("#FFF176", "#665F1F"),
        ["coral"] = ("#FFA8A0", "#6B3330"),
        ["lime"] = ("#CCF27A", "#455B1F"),
        ["aqua"] = ("#8DE9F2", "#1F5860"),
        ["orchid"] = ("#E6B0FF", "#59316B"),
        ["orange"] = ("#FFC46B", "#664218"),
    };

    /// <summary>The text colours a tab can use besides "auto".</summary>
    public static readonly IReadOnlyList<string> InkNames = new[] { "auto", "black", "gray", "navy", "green", "maroon", "purple", "white" };

    private static readonly Dictionary<string, string> Inks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "#111111",
        ["gray"] = "#4A4A4A",
        ["navy"] = "#1B2A6B",
        ["green"] = "#14532D",
        ["maroon"] = "#7F1D1D",
        ["purple"] = "#4C1D95",
        ["white"] = "#FFFFFF",
    };

    /// <summary>The colour for the n-th new tab; they cycle.</summary>
    public static string ForIndex(int index) => Names[((index % Names.Count) + Names.Count) % Names.Count];

    /// <summary>A known colour name in lower case; anything else becomes the first colour.</summary>
    public static string Normalize(string? name) =>
        name is not null && Shades.ContainsKey(name) ? name.ToLowerInvariant() : Names[0];

    /// <summary>The bright pastel (light) or the muted version that suits a dark theme.</summary>
    public static string Hex(string? name, bool dark)
    {
        var shade = Shades[Normalize(name)];
        return dark ? shade.Dark : shade.Light;
    }

    /// <summary>
    /// The background of a tab. Bright colours are used everywhere; with them off, a dark theme gets the muted version.
    /// </summary>
    public static string Background(string? name, bool bright, bool darkTheme) => Hex(name, darkTheme && !bright);

    /// <summary>True when <see cref="Background"/> is the light pastel (so dark text is the readable choice).</summary>
    public static bool IsLightBackground(bool bright, bool darkTheme) => bright || !darkTheme;

    /// <summary>A known text colour name in lower case; anything else is "auto".</summary>
    public static string NormalizeInk(string? name) =>
        name is not null && (string.Equals(name, "auto", StringComparison.OrdinalIgnoreCase) || Inks.ContainsKey(name))
            ? name.ToLowerInvariant()
            : "auto";

    /// <summary>
    /// The text colour: the chosen one, or for "auto" near-black on a light background and the theme's light text on a
    /// dark one.
    /// </summary>
    public static string InkHex(string? ink, bool lightBackground, string themeText)
    {
        var name = NormalizeInk(ink);
        return name == "auto" ? (lightBackground ? "#1E1E1E" : themeText) : Inks[name];
    }
}

/// <summary>One open tab of the built-in editor: the file it shows and its colour.</summary>
public sealed record NoteTab(string Path, string Color, string Ink = "auto");

/// <summary>The open tabs of a notes folder and which one was showing.</summary>
public sealed class NoteSession
{
    public List<NoteTab> Tabs { get; set; } = new();

    public int Active { get; set; }

    /// <summary>True (the default) for the bright pastels everywhere; false mutes them on a dark theme.</summary>
    public bool Bright { get; set; } = true;

    /// <summary>Files the user closed; they are not brought back by loading the whole folder.</summary>
    public List<string> Closed { get; set; } = new();
}

/// <summary>
/// A folder of plain text notes. New notes are numbered files in the folder; which tabs are open, their colours and
/// the active one are remembered in a small hidden file next to them.
/// </summary>
public sealed class NoteLibrary
{
    public const string SessionFileName = ".remotedeck-notes.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public NoteLibrary(string folder)
    {
        Folder = folder ?? throw new ArgumentNullException(nameof(folder));
    }

    public string Folder { get; }

    /// <summary>The folder used when none is chosen: %LOCALAPPDATA%\RemoteDeck\Notes.</summary>
    public static string DefaultFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDeck", "Notes");

    /// <summary>Creates an empty "Note N.txt" with the first free number and returns its path.</summary>
    public string CreateNote()
    {
        Directory.CreateDirectory(Folder);
        for (var n = 1; ; n++)
        {
            var path = Path.Combine(Folder, $"Note {n}.txt");
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
                // Taken; try the next number.
            }
        }
    }

    /// <summary>File types that are loaded as notes when a whole folder is opened.</summary>
    public static readonly IReadOnlySet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".json", ".yaml", ".yml", ".toml", ".ini", ".conf", ".cfg", ".sh", ".ps1", ".psm1",
        ".bat", ".cmd", ".py", ".js", ".ts", ".cs", ".xml", ".html", ".htm", ".css", ".sql", ".csv", ".tsv", ".promql", ".rules",
    };

    /// <summary>The most files loaded from a folder at once.</summary>
    public const int MaxFiles = 100;

    private const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The tabs to open: the remembered ones first (files that no longer exist are dropped), then every other text file
    /// in the folder that the user has not closed. A damaged or missing session file just means nothing is remembered.
    /// </summary>
    public NoteSession Load()
    {
        var session = new NoteSession();
        var file = Path.Combine(Folder, SessionFileName);
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<NoteSession>(File.ReadAllText(file), Json) is { } loaded)
            {
                session.Tabs = loaded.Tabs
                    .Where(t => !string.IsNullOrWhiteSpace(t.Path) && File.Exists(t.Path))
                    .Select(t => new NoteTab(t.Path, NoteColors.Normalize(t.Color), NoteColors.NormalizeInk(t.Ink)))
                    .ToList();
                session.Active = loaded.Active;
                session.Bright = loaded.Bright;
                session.Closed = (loaded.Closed ?? new List<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            session.Tabs.Clear();
            session.Closed.Clear();
        }

        AddFolderFiles(session);
        session.Active = session.Tabs.Count == 0 ? 0 : Math.Clamp(session.Active, 0, session.Tabs.Count - 1);
        return session;
    }

    private void AddFolderFiles(NoteSession session)
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        var have = session.Tabs.Select(t => Path.GetFullPath(t.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var closed = session.Closed.Select(p => Path.GetFullPath(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in Directory.EnumerateFiles(Folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (session.Tabs.Count >= MaxFiles)
                {
                    break;
                }

                var name = Path.GetFileName(path);
                var full = Path.GetFullPath(path);
                if (name.StartsWith('.') || !TextExtensions.Contains(Path.GetExtension(name))
                    || have.Contains(full) || closed.Contains(full) || new FileInfo(path).Length > MaxBytes)
                {
                    continue;
                }

                session.Tabs.Add(new NoteTab(path, NoteColors.ForIndex(session.Tabs.Count)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Whatever was found so far is enough.
        }
    }

    /// <summary>The folder that holds a workspace's own notes: Notes\Workspaces\&lt;name&gt;.</summary>
    public static string WorkspaceFolder(string workspaceName) =>
        Path.Combine(DefaultFolder(), "Workspaces", Slug(workspaceName));

    /// <summary>True for a folder inside Notes\Workspaces (one that belongs to a saved workspace).</summary>
    public static bool IsWorkspaceFolder(string folder)
    {
        var root = Path.GetFullPath(Path.Combine(DefaultFolder(), "Workspaces")).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return Path.GetFullPath(folder).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the folder is the shared notes folder that is not part of any workspace.</summary>
    public static bool IsSharedFolder(string folder) =>
        string.Equals(Path.GetFullPath(folder).TrimEnd('\\', '/'), Path.GetFullPath(DefaultFolder()).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>A name that is safe as a folder or file name.</summary>
    public static string Slug(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var cleaned = new string((name ?? string.Empty).Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().Trim('.');
        if (cleaned.Length > 60)
        {
            cleaned = cleaned[..60].TrimEnd();
        }

        return cleaned.Length == 0 ? "workspace" : cleaned;
    }

    /// <summary>A path in <paramref name="folder"/> for a file called <paramref name="fileName"/> that does not exist yet.</summary>
    public static string UniquePath(string folder, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var path = Path.Combine(folder, fileName);
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{stem} ({n}){extension}");
        }

        return path;
    }

    public void Save(NoteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, SessionFileName), JsonSerializer.Serialize(session, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Remembering the tabs is a convenience; the notes themselves are already on disk.
        }
    }
}
