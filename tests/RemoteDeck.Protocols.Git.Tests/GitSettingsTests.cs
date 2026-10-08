namespace RemoteDeck.Protocols.Git.Tests;

public class GitSettingsTests
{
    private static Dictionary<string, string> Opts(params (string, string)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public void Defaults_to_auto_with_no_startup()
    {
        var s = GitSettings.From(@"C:\Projects\App", null);
        Assert.Equal(@"C:\Projects\App", s.Folder);
        Assert.Equal("auto", s.Shell);
        Assert.Null(s.Startup);
    }

    [Fact]
    public void Trims_the_folder_and_removes_quotes()
    {
        Assert.Equal(@"C:\My Repo", GitSettings.From("  \"C:\\My Repo\"  ", null).Folder);
    }

    [Fact]
    public void Requires_a_folder()
    {
        Assert.Throws<GitShellException>(() => GitSettings.From("  ", null));
    }

    [Theory]
    [InlineData("BASH", "bash")]
    [InlineData("PwSh", "pwsh")]
    [InlineData("cmd", "cmd")]
    public void Known_shells_are_case_insensitive(string given, string expected)
    {
        Assert.Equal(expected, GitSettings.From("C:\\r", Opts(("shell", given))).Shell);
    }

    [Fact]
    public void Accepts_a_full_path_and_keeps_it_as_given()
    {
        var s = GitSettings.From("C:\\r", Opts(("shell", @"D:\Tools\Zsh.exe")));
        Assert.Equal(@"D:\Tools\Zsh.exe", s.Shell);
    }

    [Fact]
    public void Rejects_an_unknown_shell_name()
    {
        var ex = Assert.Throws<GitShellException>(() => GitSettings.From("C:\\r", Opts(("shell", "fish"))));
        Assert.Contains("fish", ex.Message);
    }

    [Fact]
    public void Startup_is_kept_and_must_be_one_line()
    {
        Assert.Equal("git status", GitSettings.From("C:\\r", Opts(("startup", " git status "))).Startup);
        Assert.Throws<GitShellException>(() => GitSettings.From("C:\\r", Opts(("startup", "a\nb"))));
    }
}
