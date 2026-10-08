namespace RemoteDeck.Protocols.Git.Tests;

public class ShellLocatorTests
{
    private static ShellLocator With(HashSet<string> files, Dictionary<string, string>? env = null, params string[] path)
    {
        env ??= new Dictionary<string, string>();
        return new ShellLocator(files.Contains, k => env.GetValueOrDefault(k), path);
    }

    private static readonly string GitBash = Path.Combine("PF", "Git", "bin", "bash.exe");
    private static readonly string Pwsh = Path.Combine("tools", "pwsh.exe");
    private static readonly string WinPs = Path.Combine("SR", "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

    private static Dictionary<string, string> Env() => new()
    {
        ["ProgramFiles"] = "PF",
        ["SystemRoot"] = "SR",
        ["ComSpec"] = "cmd.exe",
    };

    [Fact]
    public void Auto_prefers_git_bash()
    {
        var locator = With(new() { GitBash, Pwsh, WinPs, "cmd.exe" }, Env(), "tools");
        var choice = locator.Locate("auto");
        Assert.Equal(GitBash, choice.FileName);
        Assert.Equal("--login -i", choice.Arguments);
    }

    [Fact]
    public void Auto_falls_back_to_pwsh_then_windows_powershell_then_cmd()
    {
        Assert.Equal(Pwsh, With(new() { Pwsh, WinPs, "cmd.exe" }, Env(), "tools").Locate("auto").FileName);
        Assert.Equal(WinPs, With(new() { WinPs, "cmd.exe" }, Env()).Locate("auto").FileName);
        Assert.Equal("cmd.exe", With(new() { "cmd.exe" }, Env()).Locate("auto").FileName);
    }

    [Fact]
    public void Auto_with_nothing_installed_explains_what_to_do()
    {
        var ex = Assert.Throws<GitShellException>(() => With(new(), Env()).Locate("auto"));
        Assert.Contains("Git for Windows", ex.Message);
    }

    [Fact]
    public void Git_bash_is_found_in_the_per_user_install_folder()
    {
        var env = Env();
        env["LOCALAPPDATA"] = "LA";
        var path = Path.Combine("LA", "Programs", "Git", "bin", "bash.exe");
        Assert.Equal(path, With(new() { path }, env).Locate("bash").FileName);
    }

    [Fact]
    public void A_named_shell_that_is_missing_fails_instead_of_picking_another()
    {
        var ex = Assert.Throws<GitShellException>(() => With(new() { "cmd.exe" }, Env()).Locate("bash"));
        Assert.Contains("Git Bash", ex.Message);
    }

    [Fact]
    public void A_full_path_must_exist()
    {
        var locator = With(new() { @"D:\Tools\zsh.exe" }, Env());
        Assert.Equal("zsh.exe", locator.Locate(@"D:\Tools\zsh.exe").Description);
        Assert.Throws<GitShellException>(() => locator.Locate(@"D:\Tools\nope.exe"));
    }
}
