using RemoteDeck.Core.Launchers;

namespace RemoteDeck.Core.Tests;

public class EditorLauncherTests
{
    private static Dictionary<string, string> Opts(params (string, string)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2);

    private static readonly string Pf = "PF";
    private static readonly string Local = "LA";
    private static readonly string Notepad = Path.Combine("SR", "System32", "notepad.exe");
    private static readonly string NotepadPp = Path.Combine(Pf, "Notepad++", "notepad++.exe");
    private static readonly string Sublime = Path.Combine(Pf, "Sublime Text", "sublime_text.exe");
    private static readonly string Code = Path.Combine(Local, "Programs", "Microsoft VS Code", "Code.exe");

    private static EditorLauncher With(IEnumerable<string> files, IEnumerable<string>? folders = null, params string[] path)
    {
        var fileSet = files.ToHashSet();
        var folderSet = (folders ?? Array.Empty<string>()).ToHashSet();
        var env = new Dictionary<string, string>
        {
            ["ProgramFiles"] = Pf,
            ["LOCALAPPDATA"] = Local,
            ["SystemRoot"] = "SR",
        };
        return new EditorLauncher(fileSet.Contains, folderSet.Contains, k => env.GetValueOrDefault(k), path);
    }

    // ---- settings ----

    [Fact]
    public void Settings_DefaultToNotepadWithNoArguments()
    {
        var s = EditorSettings.From(@"C:\notes\todo.txt", null);
        Assert.Equal("notepad", s.Editor);
        Assert.Null(s.Program);
        Assert.Null(s.Arguments);
    }

    [Fact]
    public void Settings_RequireATarget()
    {
        Assert.Throws<EditorLaunchException>(() => EditorSettings.From("  ", null));
    }

    [Theory]
    [InlineData("VSCode", "vscode")]
    [InlineData("Notepad++", "notepad++")]
    [InlineData("SUBLIME", "sublime")]
    public void Settings_EditorNamesAreCaseInsensitive(string given, string expected)
    {
        Assert.Equal(expected, EditorSettings.From("C:\\x", Opts(("editor", given))).Editor);
    }

    [Fact]
    public void Settings_RejectAnUnknownEditor()
    {
        var ex = Assert.Throws<EditorLaunchException>(() => EditorSettings.From("C:\\x", Opts(("editor", "emacs"))));
        Assert.Contains("emacs", ex.Message);
    }

    [Fact]
    public void Settings_CustomNeedsAProgram()
    {
        Assert.Throws<EditorLaunchException>(() => EditorSettings.From("C:\\x", Opts(("editor", "custom"))));
        var ok = EditorSettings.From("C:\\x", Opts(("editor", "custom"), ("program", "\"D:\\Tools\\ed.exe\"")));
        Assert.Equal(@"D:\Tools\ed.exe", ok.Program);
    }

    [Fact]
    public void Settings_ArgumentsMustBeOneLine()
    {
        Assert.Throws<EditorLaunchException>(() => EditorSettings.From("C:\\x", Opts(("arguments", "a\nb"))));
    }

    // ---- finding editors ----

    [Fact]
    public void Installed_ListsOnlyWhatIsThere()
    {
        var launcher = With(new[] { Notepad, Sublime, Code });
        Assert.Equal(new[] { "notepad", "sublime", "vscode" }, launcher.Installed());
    }

    [Fact]
    public void Installed_FindsOnesOnThePathToo()
    {
        var launcher = With(new[] { Path.Combine("tools", "subl.exe") }, null, "tools");
        Assert.Equal(new[] { "sublime" }, launcher.Installed());
    }

    // ---- planning ----

    [Fact]
    public void Plan_QuotesTheTargetAndStartsBesideAFile()
    {
        var file = Path.Combine(@"C:\", "My Notes", "todo.txt");
        var launcher = With(new[] { Notepad, file });
        var plan = launcher.Plan(EditorSettings.From(file, null));

        Assert.Equal(Notepad, plan.FileName);
        Assert.Equal("\"" + file + "\"", plan.Arguments);
        Assert.Equal("Notepad", plan.Description);
        Assert.EndsWith("My Notes", plan.WorkingDirectory);
    }

    [Fact]
    public void Plan_OpensAFolderInAnEditorThatSupportsIt()
    {
        var folder = Path.Combine(@"C:\", "Projects", "App");
        var launcher = With(new[] { Code }, new[] { folder });
        var plan = launcher.Plan(EditorSettings.From(folder, Opts(("editor", "vscode"))));

        Assert.Equal(Code, plan.FileName);
        Assert.Equal("\"" + folder + "\"", plan.Arguments);
        Assert.Equal(folder, plan.WorkingDirectory);
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("notepad++")]
    public void Plan_RefusesAFolderForEditorsThatOnlyOpenFiles(string editor)
    {
        var folder = Path.Combine(@"C:\", "Projects");
        var launcher = With(new[] { Notepad, NotepadPp }, new[] { folder });

        var ex = Assert.Throws<EditorLaunchException>(() => launcher.Plan(EditorSettings.From(folder, Opts(("editor", editor)))));
        Assert.Contains("opens files", ex.Message);
    }

    [Fact]
    public void Plan_ArgumentsCanPlaceTheTargetOrGetItAppended()
    {
        var folder = Path.Combine(@"C:\", "Proj");
        var launcher = With(new[] { Sublime }, new[] { folder });

        var placed = launcher.Plan(EditorSettings.From(folder, Opts(("editor", "sublime"), ("arguments", "--new-window {target} --wait"))));
        Assert.Equal("--new-window \"" + folder + "\" --wait", placed.Arguments);

        var appended = launcher.Plan(EditorSettings.From(folder, Opts(("editor", "sublime"), ("arguments", "--new-window"))));
        Assert.Equal("--new-window \"" + folder + "\"", appended.Arguments);
    }

    [Fact]
    public void Plan_ACustomProgramIsUsedAsGiven()
    {
        var program = Path.Combine(@"D:\", "Tools", "ed.exe");
        var file = Path.Combine(@"C:\", "a.txt");
        var launcher = With(new[] { program, file });

        var plan = launcher.Plan(EditorSettings.From(file, Opts(("editor", "custom"), ("program", program))));
        Assert.Equal(program, plan.FileName);
        Assert.Equal("ed", plan.Description);
    }

    [Fact]
    public void Plan_AMissingProgramOrTargetIsReported()
    {
        var file = Path.Combine(@"C:\", "a.txt");

        var noTarget = With(new[] { Notepad });
        Assert.Contains("does not exist", Assert.Throws<EditorLaunchException>(() => noTarget.Plan(EditorSettings.From(file, null))).Message);

        var noEditor = With(new[] { file });
        Assert.Contains("was not found", Assert.Throws<EditorLaunchException>(() => noEditor.Plan(EditorSettings.From(file, Opts(("editor", "vscode"))))).Message);

        var noCustom = With(new[] { file });
        Assert.Contains("does not exist", Assert.Throws<EditorLaunchException>(() =>
            noCustom.Plan(EditorSettings.From(file, Opts(("editor", "custom"), ("program", @"D:\nope.exe"))))).Message);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Sublime Text\sublime_text.exe")]
    [InlineData(@"C:\Users\me\Desktop\Sublime.LNK")]
    public void Settings_RefusesAProgramAsTheThingToOpen(string target)
    {
        Assert.Contains("Editor box", Assert.Throws<EditorLaunchException>(() => EditorSettings.From(target, null)).Message);
    }

    [Fact]
    public void BuiltInEditorIsAKnownChoiceWithNoProgramToStart()
    {
        var settings = EditorSettings.From(@"C:\Notes", Opts(("editor", "builtin")));
        Assert.Equal("builtin", settings.Editor);
        Assert.Throws<EditorLaunchException>(() => With(Array.Empty<string>()).Plan(settings));
        Assert.DoesNotContain("builtin", With(new[] { Notepad }).Installed());
    }
}
