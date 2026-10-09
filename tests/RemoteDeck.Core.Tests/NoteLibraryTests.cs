using RemoteDeck.Core.Notes;

namespace RemoteDeck.Core.Tests;

public sealed class NoteLibraryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rd-notes-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    [Fact]
    public void Colors_CycleAndFallBack()
    {
        Assert.Equal("yellow", NoteColors.ForIndex(0));
        Assert.Equal("yellow", NoteColors.ForIndex(NoteColors.Names.Count));
        Assert.Equal("pink", NoteColors.ForIndex(1));
        Assert.Equal(NoteColors.Names[^1], NoteColors.ForIndex(-1));
        Assert.Equal("mint", NoteColors.Normalize("MINT"));
        Assert.Equal("yellow", NoteColors.Normalize("purple"));
        Assert.Equal("yellow", NoteColors.Normalize(null));
    }

    [Fact]
    public void Colors_HaveALightAndADarkShadeEach()
    {
        foreach (var name in NoteColors.Names)
        {
            Assert.Matches("^#[0-9A-F]{6}$", NoteColors.Hex(name, dark: false));
            Assert.Matches("^#[0-9A-F]{6}$", NoteColors.Hex(name, dark: true));
            Assert.NotEqual(NoteColors.Hex(name, false), NoteColors.Hex(name, true));
        }
    }

    [Fact]
    public void Colors_BackgroundFollowsTheBrightSetting()
    {
        Assert.Equal(NoteColors.Hex("aqua", false), NoteColors.Background("aqua", bright: true, darkTheme: true));
        Assert.Equal(NoteColors.Hex("aqua", true), NoteColors.Background("aqua", bright: false, darkTheme: true));
        Assert.Equal(NoteColors.Hex("aqua", false), NoteColors.Background("aqua", bright: false, darkTheme: false));
        Assert.True(NoteColors.IsLightBackground(true, true));
        Assert.False(NoteColors.IsLightBackground(false, true));
        Assert.True(NoteColors.IsLightBackground(false, false));
    }

    [Fact]
    public void Ink_AutoPicksReadableTextAndNamedOnesWin()
    {
        Assert.Equal("#1E1E1E", NoteColors.InkHex("auto", lightBackground: true, themeText: "#ECEFF4"));
        Assert.Equal("#ECEFF4", NoteColors.InkHex(null, lightBackground: false, themeText: "#ECEFF4"));
        Assert.Equal("#1B2A6B", NoteColors.InkHex("NAVY", true, "#ECEFF4"));
        Assert.Equal("auto", NoteColors.NormalizeInk("rainbow"));
        Assert.Equal("maroon", NoteColors.NormalizeInk("Maroon"));
        foreach (var ink in NoteColors.InkNames)
        {
            Assert.Matches("^#[0-9A-F]{6}$", NoteColors.InkHex(ink, true, "#ECEFF4"));
        }
    }

    [Fact]
    public void CreateNote_UsesTheFirstFreeNumber()
    {
        var library = new NoteLibrary(_folder);

        var first = library.CreateNote();
        var second = library.CreateNote();
        File.Delete(first);
        var third = library.CreateNote();

        Assert.Equal("Note 1.txt", Path.GetFileName(first));
        Assert.Equal("Note 2.txt", Path.GetFileName(second));
        Assert.Equal("Note 1.txt", Path.GetFileName(third));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void Session_RoundTripsAndDropsMissingFiles()
    {
        var library = new NoteLibrary(_folder);
        var a = library.CreateNote();
        var b = library.CreateNote();
        var gone = Path.Combine(_folder, "gone.txt");

        library.Save(new NoteSession
        {
            Tabs = { new NoteTab(a, "pink", "navy"), new NoteTab(gone, "blue"), new NoteTab(b, "nonsense", "rainbow") },
            Active = 2,
            Bright = false,
        });

        var loaded = library.Load();
        Assert.Equal(new[] { a, b }, loaded.Tabs.Select(t => t.Path));
        Assert.Equal(new[] { "pink", "yellow" }, loaded.Tabs.Select(t => t.Color));
        Assert.Equal(new[] { "navy", "auto" }, loaded.Tabs.Select(t => t.Ink));
        Assert.Equal(1, loaded.Active);
        Assert.False(loaded.Bright);
    }

    [Fact]
    public void Load_OfAnOldSessionWithoutNewFieldsUsesDefaults()
    {
        var library = new NoteLibrary(_folder);
        var a = library.CreateNote();
        File.WriteAllText(Path.Combine(_folder, NoteLibrary.SessionFileName),
            "{\"Tabs\":[{\"Path\":" + System.Text.Json.JsonSerializer.Serialize(a) + ",\"Color\":\"mint\"}],\"Active\":0}");

        var loaded = library.Load();
        Assert.Equal("auto", loaded.Tabs[0].Ink);
        Assert.True(loaded.Bright);
    }

    [Fact]
    public void Load_BringsInEveryTextFileInTheFolder()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "b.md"), "b");
        File.WriteAllText(Path.Combine(_folder, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_folder, "c.LOG"), "c");
        File.WriteAllText(Path.Combine(_folder, "tool.exe"), "x");
        File.WriteAllText(Path.Combine(_folder, ".hidden.txt"), "x");
        File.WriteAllText(Path.Combine(_folder, "closed.txt"), "x");

        var library = new NoteLibrary(_folder);
        library.Save(new NoteSession { Tabs = { new NoteTab(Path.Combine(_folder, "b.md"), "blue", "navy") }, Closed = { Path.Combine(_folder, "closed.txt") } });

        var loaded = library.Load();
        Assert.Equal(new[] { "b.md", "a.txt", "c.LOG" }, loaded.Tabs.Select(t => Path.GetFileName(t.Path)));
        Assert.Equal("navy", loaded.Tabs[0].Ink);
        Assert.Equal(NoteColors.ForIndex(1), loaded.Tabs[1].Color);
        Assert.Equal(NoteColors.ForIndex(2), loaded.Tabs[2].Color);
    }

    [Fact]
    public void Load_WithoutASessionStillLoadsTheFolder()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "x.txt"), "x");
        Assert.Single(new NoteLibrary(_folder).Load().Tabs);
    }

    [Fact]
    public void Slug_AndUniquePath_AreSafe()
    {
        Assert.Equal("a_b_c", NoteLibrary.Slug("a/b:c"));
        Assert.Equal("workspace", NoteLibrary.Slug("  .. "));
        Assert.True(NoteLibrary.Slug(new string('x', 200)).Length <= 60);

        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "n.txt"), "1");
        Assert.Equal(Path.Combine(_folder, "n (2).txt"), NoteLibrary.UniquePath(_folder, "n.txt"));
        Assert.Equal(Path.Combine(_folder, "z.txt"), NoteLibrary.UniquePath(_folder, "z.txt"));
    }

    [Fact]
    public void WorkspaceFolders_AreRecognisedAndSeparateFromTheSharedOne()
    {
        var shared = NoteLibrary.DefaultFolder();
        var own = NoteLibrary.WorkspaceFolder("Prod: k8s");
        Assert.True(NoteLibrary.IsSharedFolder(shared));
        Assert.False(NoteLibrary.IsSharedFolder(own));
        Assert.True(NoteLibrary.IsWorkspaceFolder(own));
        Assert.False(NoteLibrary.IsWorkspaceFolder(shared));
        Assert.EndsWith("Prod_ k8s", own);
    }

    [Fact]
    public void Load_WithNothingOrGarbageGivesAnEmptySession()
    {
        var library = new NoteLibrary(_folder);
        Assert.Empty(library.Load().Tabs);

        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, NoteLibrary.SessionFileName), "{ not json");
        var loaded = library.Load();
        Assert.Empty(loaded.Tabs);
        Assert.Equal(0, loaded.Active);
    }
}
