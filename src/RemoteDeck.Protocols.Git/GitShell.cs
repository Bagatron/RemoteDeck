using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Git;

/// <summary>A problem starting a git shell. The message is safe to show to the user.</summary>
public sealed class GitShellException : Exception
{
    public GitShellException(string message)
        : base(message)
    {
    }

    public GitShellException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A program to run in the terminal: the file, its arguments, and a name for messages.</summary>
internal sealed record ShellChoice(string FileName, string Arguments, string Description);

/// <summary>
/// The settings of a git connection: the repository folder (the connection's <c>Host</c>) and the options
/// <c>shell</c> ("auto", "bash", "pwsh", "powershell", "cmd", or the full path of a program; default auto) and
/// <c>startup</c> (a command typed for you once the shell is up, for example "git status").
/// </summary>
internal sealed record GitSettings(string Folder, string Shell, string? Startup)
{
    public static readonly IReadOnlyList<string> KnownShells = new[] { "auto", "bash", "pwsh", "powershell", "cmd" };

    public static GitSettings From(ConnectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return From(definition.Host, definition.Options);
    }

    public static GitSettings From(string folder, IReadOnlyDictionary<string, string>? options)
    {
        var path = (folder ?? string.Empty).Trim().Trim('"');
        if (path.Length == 0)
        {
            throw new GitShellException("Enter the folder of the repository, for example C:\\Projects\\RemoteDeck.");
        }

        string Get(string key) =>
            options is not null && options.TryGetValue(key, out var value) ? value.Trim() : string.Empty;

        var shell = Get("shell");
        if (shell.Length == 0)
        {
            shell = "auto";
        }
        else if (!KnownShells.Contains(shell, StringComparer.OrdinalIgnoreCase) && !LooksLikePath(shell))
        {
            throw new GitShellException(
                $"The shell \"{shell}\" is not known. Use auto, bash, pwsh, powershell, cmd, or the full path of a program.");
        }

        var startup = Get("startup");
        if (startup.Contains('\r') || startup.Contains('\n'))
        {
            throw new GitShellException("The startup command must be a single line.");
        }

        var known = KnownShells.FirstOrDefault(k => string.Equals(k, shell, StringComparison.OrdinalIgnoreCase));
        return new GitSettings(path, known ?? shell, startup.Length == 0 ? null : startup);
    }

    private static bool LooksLikePath(string value) => value.Contains('\\') || value.Contains('/');
}

/// <summary>
/// Finds the program to run. Git Bash is preferred (it is what most people mean by a git terminal), then PowerShell 7,
/// Windows PowerShell and cmd. Everything it looks at is passed in, so it can be tested without a real computer.
/// </summary>
internal sealed class ShellLocator
{
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string?> _environment;
    private readonly IReadOnlyList<string> _pathFolders;

    public ShellLocator(Func<string, bool> fileExists, Func<string, string?> environment, IReadOnlyList<string> pathFolders)
    {
        _fileExists = fileExists;
        _environment = environment;
        _pathFolders = pathFolders;
    }

    public static ShellLocator ForThisComputer() => new(
        File.Exists,
        Environment.GetEnvironmentVariable,
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public ShellChoice Locate(string shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        switch (shell.ToLowerInvariant())
        {
            case "auto":
                return FindBash() ?? FindPwsh() ?? FindWindowsPowerShell() ?? FindCmd()
                    ?? throw new GitShellException("No shell was found. Install Git for Windows (https://git-scm.com) or choose a shell in the connection.");
            case "bash":
                return FindBash()
                    ?? throw new GitShellException("Git Bash was not found. Install Git for Windows (https://git-scm.com), or pick another shell.");
            case "pwsh":
                return FindPwsh()
                    ?? throw new GitShellException("PowerShell 7 (pwsh) was not found. Install it, or pick another shell.");
            case "powershell":
                return FindWindowsPowerShell()
                    ?? throw new GitShellException("Windows PowerShell was not found. Pick another shell.");
            case "cmd":
                return FindCmd()
                    ?? throw new GitShellException("cmd.exe was not found. Pick another shell.");
            default:
                if (!_fileExists(shell))
                {
                    throw new GitShellException($"The program \"{shell}\" does not exist.");
                }

                return new ShellChoice(shell, string.Empty, Path.GetFileName(shell));
        }
    }

    private ShellChoice? FindBash()
    {
        var roots = new[]
        {
            _environment("ProgramFiles"),
            _environment("ProgramW6432"),
            _environment("ProgramFiles(x86)"),
            Combine(_environment("LOCALAPPDATA"), "Programs"),
        };

        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(root!, "Git", "bin", "bash.exe");
            if (_fileExists(candidate))
            {
                return new ShellChoice(candidate, "--login -i", "Git Bash");
            }
        }

        return null;
    }

    private ShellChoice? FindPwsh()
    {
        var found = FindOnPath("pwsh.exe");
        if (found is null)
        {
            var programFiles = _environment("ProgramFiles");
            if (!string.IsNullOrEmpty(programFiles))
            {
                var candidate = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
                found = _fileExists(candidate) ? candidate : null;
            }
        }

        return found is null ? null : new ShellChoice(found, "-NoLogo", "PowerShell 7");
    }

    private ShellChoice? FindWindowsPowerShell()
    {
        var root = _environment("SystemRoot") ?? _environment("windir");
        if (!string.IsNullOrEmpty(root))
        {
            var candidate = Path.Combine(root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (_fileExists(candidate))
            {
                return new ShellChoice(candidate, "-NoLogo", "Windows PowerShell");
            }
        }

        var onPath = FindOnPath("powershell.exe");
        return onPath is null ? null : new ShellChoice(onPath, "-NoLogo", "Windows PowerShell");
    }

    private ShellChoice? FindCmd()
    {
        var comspec = _environment("ComSpec");
        if (!string.IsNullOrEmpty(comspec) && _fileExists(comspec))
        {
            return new ShellChoice(comspec, string.Empty, "Command Prompt");
        }

        var onPath = FindOnPath("cmd.exe");
        return onPath is null ? null : new ShellChoice(onPath, string.Empty, "Command Prompt");
    }

    private string? FindOnPath(string fileName)
    {
        foreach (var folder in _pathFolders)
        {
            var candidate = Path.Combine(folder, fileName);
            if (_fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? Combine(string? a, string b) => string.IsNullOrEmpty(a) ? null : Path.Combine(a, b);
}

/// <summary>What to start: the program, where, and how big the window is.</summary>
internal sealed record ShellLaunch(ShellChoice Shell, string Folder, int Columns, int Rows);

/// <summary>A running shell: the bytes in and out, a resize, and a way to know it ended.</summary>
internal interface IShellLink : IDisposable
{
    Stream Input { get; }

    Stream Output { get; }

    void Resize(int columns, int rows);
}

/// <summary>Starts shells. The real one uses a Windows pseudo console; tests use a fake.</summary>
internal interface IShellLinkFactory
{
    IShellLink Start(ShellLaunch launch);
}
