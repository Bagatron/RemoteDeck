using RemoteDeck.Plugin;

namespace RemoteDeck.Core.Launchers;

/// <summary>A problem with an editor connection. The message is safe to show to the user.</summary>
public sealed class EditorLaunchException : Exception
{
    public EditorLaunchException(string message)
        : base(message)
    {
    }

    public EditorLaunchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The settings of an editor connection: the folder or file to open (the connection's <c>Host</c>) and the options
/// <c>editor</c> (<c>notepad</c>, <c>notepad++</c>, <c>sublime</c>, <c>vscode</c>, <c>builtin</c> or <c>custom</c>; default notepad),
/// <c>program</c> (the program's full path: required for <c>custom</c>, and used instead of the automatic lookup when
/// set for the others) and <c>arguments</c> (extra command line text; <c>{target}</c> marks where the path goes, and it
/// is added at the end when missing).
/// </summary>
public sealed record EditorSettings(string Target, string Editor, string? Program, string? Arguments)
{
    public static readonly IReadOnlyList<string> KnownEditors = new[] { "notepad", "notepad++", "sublime", "vscode", "builtin", "custom" };

    public static EditorSettings From(ConnectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return From(definition.Host, definition.Options);
    }

    public static EditorSettings From(string target, IReadOnlyDictionary<string, string>? options)
    {
        var path = (target ?? string.Empty).Trim().Trim('"');
        if (path.Length == 0)
        {
            throw new EditorLaunchException("Enter the folder or file to open, for example C:\\Projects\\RemoteDeck.");
        }

        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            throw new EditorLaunchException(
                "That is a program, not something to open. Put the folder or file here and choose the editor in the Editor box (use \"Other program...\" for one that is not listed).");
        }

        string Get(string key) =>
            options is not null && options.TryGetValue(key, out var value) ? value.Trim() : string.Empty;

        var chosen = Get("editor");
        if (chosen.Length == 0)
        {
            chosen = "notepad";
        }

        var editor = KnownEditors.FirstOrDefault(k => string.Equals(k, chosen, StringComparison.OrdinalIgnoreCase))
            ?? throw new EditorLaunchException(
                $"The editor \"{chosen}\" is not known. Use notepad, notepad++, sublime, vscode, builtin or custom.");

        var program = Get("program").Trim('"');
        if (editor == "custom" && program.Length == 0)
        {
            throw new EditorLaunchException("Choose the program to run for a custom editor.");
        }

        var arguments = Get("arguments");
        if (arguments.Contains('\r') || arguments.Contains('\n'))
        {
            throw new EditorLaunchException("The arguments must be a single line.");
        }

        return new EditorSettings(path, editor, program.Length == 0 ? null : program, arguments.Length == 0 ? null : arguments);
    }
}

/// <summary>What to start: the program, its command line, and the folder to start in.</summary>
public sealed record EditorLaunch(string FileName, string Arguments, string WorkingDirectory, string Description);

/// <summary>
/// Finds the editor on this computer and builds the command line. Everything it looks at is passed in, so it can be
/// tested without a real computer.
/// </summary>
public sealed class EditorLauncher
{
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, string?> _environment;
    private readonly IReadOnlyList<string> _pathFolders;

    public EditorLauncher(
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Func<string, string?> environment,
        IReadOnlyList<string> pathFolders)
    {
        _fileExists = fileExists;
        _directoryExists = directoryExists;
        _environment = environment;
        _pathFolders = pathFolders;
    }

    public static EditorLauncher ForThisComputer() => new(
        File.Exists,
        Directory.Exists,
        Environment.GetEnvironmentVariable,
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>The known editors that are installed here (never includes "custom").</summary>
    public IReadOnlyList<string> Installed() =>
        EditorSettings.KnownEditors.Where(e => e is not "custom" and not "builtin" && Locate(e) is not null).ToList();

    /// <summary>The friendly name of a known editor, for messages.</summary>
    public static string DisplayName(string editor) => editor switch
    {
        "notepad" => "Notepad",
        "notepad++" => "Notepad++",
        "sublime" => "Sublime Text",
        "vscode" => "Visual Studio Code",
        "builtin" => "the built-in editor",
        _ => "the editor",
    };

    public EditorLaunch Plan(EditorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Editor == "builtin")
        {
            throw new EditorLaunchException("The built-in editor runs inside RemoteDeck; there is no program to start.");
        }

        var isFolder = _directoryExists(settings.Target);
        if (!isFolder && !_fileExists(settings.Target))
        {
            throw new EditorLaunchException($"\"{settings.Target}\" does not exist.");
        }

        if (isFolder && settings.Editor is "notepad" or "notepad++")
        {
            throw new EditorLaunchException(
                $"{DisplayName(settings.Editor)} opens files, not folders. Point the connection at a file, or choose an editor that opens folders (Sublime Text or Visual Studio Code).");
        }

        string program;
        if (settings.Program is { } given)
        {
            if (!_fileExists(given))
            {
                throw new EditorLaunchException($"The program \"{given}\" does not exist.");
            }

            program = given;
        }
        else
        {
            program = Locate(settings.Editor)
                ?? throw new EditorLaunchException(
                    $"{DisplayName(settings.Editor)} was not found on this computer. Install it, or choose \"Other program\" and pick its .exe.");
        }

        var quoted = "\"" + settings.Target.Replace("\"", string.Empty) + "\"";
        var arguments = settings.Arguments is null
            ? quoted
            : settings.Arguments.Contains("{target}", StringComparison.Ordinal)
                ? settings.Arguments.Replace("{target}", quoted, StringComparison.Ordinal)
                : settings.Arguments + " " + quoted;


        var workingDirectory = isFolder
            ? settings.Target
            : Path.GetDirectoryName(Path.GetFullPath(settings.Target)) ?? settings.Target;

        var description = settings.Editor == "custom" ? Path.GetFileNameWithoutExtension(program) : DisplayName(settings.Editor);
        return new EditorLaunch(program, arguments, workingDirectory, description);
    }

    private string? Locate(string editor)
    {
        var programFiles = new[]
        {
            _environment("ProgramFiles"),
            _environment("ProgramW6432"),
            _environment("ProgramFiles(x86)"),
            Under(_environment("LOCALAPPDATA"), "Programs"),
        };

        IEnumerable<string> Candidates(params string[] relative) =>
            programFiles
                .Where(root => !string.IsNullOrEmpty(root))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(root => relative.Select(r => Path.Combine(root!, r)));

        switch (editor)
        {
            case "notepad":
                var systemRoot = _environment("SystemRoot") ?? _environment("windir");
                var candidates = string.IsNullOrEmpty(systemRoot)
                    ? Enumerable.Empty<string>()
                    : new[] { Path.Combine(systemRoot, "System32", "notepad.exe") };
                return candidates.FirstOrDefault(_fileExists) ?? OnPath("notepad.exe");

            case "notepad++":
                return Candidates(Path.Combine("Notepad++", "notepad++.exe")).FirstOrDefault(_fileExists) ?? OnPath("notepad++.exe");

            case "sublime":
                return Candidates(
                        Path.Combine("Sublime Text", "sublime_text.exe"),
                        Path.Combine("Sublime Text 3", "sublime_text.exe"))
                    .FirstOrDefault(_fileExists)
                    ?? OnPath("subl.exe")
                    ?? OnPath("sublime_text.exe");

            case "vscode":
                return Candidates(Path.Combine("Microsoft VS Code", "Code.exe")).FirstOrDefault(_fileExists);

            default:
                return null;
        }
    }

    private string? OnPath(string fileName) =>
        _pathFolders.Select(folder => Path.Combine(folder, fileName)).FirstOrDefault(_fileExists);

    private static string? Under(string? root, string child) => string.IsNullOrEmpty(root) ? null : Path.Combine(root, child);
}
