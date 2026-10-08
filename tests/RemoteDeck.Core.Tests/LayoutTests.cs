using RemoteDeck.Core.Layout;

namespace RemoteDeck.Core.Tests;

public class LayoutTests
{
    public static TheoryData<LayoutPreset, int> PresetPaneCounts => new()
    {
        { LayoutPreset.Single, 1 },
        { LayoutPreset.TwoColumns, 2 },
        { LayoutPreset.TwoRows, 2 },
        { LayoutPreset.Grid2x2, 4 },
        { LayoutPreset.OneBigTwoSmall, 3 },
        { LayoutPreset.ThreeColumns, 3 },
    };

    [Theory]
    [MemberData(nameof(PresetPaneCounts))]
    public void Presets_HaveExpectedNumberOfPanes_WithUniqueIds(LayoutPreset preset, int expected)
    {
        var root = LayoutPresets.Create(preset);

        var ids = LayoutTree.Panes(root).Select(p => p.Id).ToList();

        Assert.Equal(expected, ids.Count);
        Assert.Equal(expected, ids.Distinct().Count());
        LayoutTree.Validate(root);
    }

    [Fact]
    public void Grid2x2_ListsPanesInVisualOrder()
    {
        var root = LayoutPresets.Create(LayoutPreset.Grid2x2);

        Assert.Equal(new[] { "p1", "p2", "p3", "p4" }, LayoutTree.Panes(root).Select(p => p.Id));
    }

    [Fact]
    public void SplitPane_ReplacesLeafWithSplit_AndKeepsExistingSession()
    {
        var root = LayoutTree.AssignConnection(LayoutPresets.Create(LayoutPreset.Single), "p1", "web-01");

        var result = LayoutTree.SplitPane(root, "p1", SplitOrientation.Rows, "p2", 0.3);

        var split = Assert.IsType<SplitNode>(result);
        Assert.Equal(SplitOrientation.Rows, split.Orientation);
        Assert.Equal(0.3, split.Ratio);
        Assert.Equal(new PaneNode("p1", "web-01"), split.First);
        Assert.Equal(new PaneNode("p2"), split.Second);
    }

    [Fact]
    public void SplitPane_DuplicateId_Throws()
    {
        var root = LayoutPresets.Create(LayoutPreset.TwoColumns);

        Assert.Throws<ArgumentException>(() => LayoutTree.SplitPane(root, "p1", SplitOrientation.Columns, "p2"));
    }

    [Fact]
    public void SplitPane_UnknownPane_Throws()
    {
        var root = LayoutPresets.Create(LayoutPreset.Single);

        Assert.Throws<KeyNotFoundException>(() => LayoutTree.SplitPane(root, "nope", SplitOrientation.Columns, "p2"));
    }

    [Fact]
    public void ClosePane_LetsSiblingTakeOverTheSpace()
    {
        var root = LayoutPresets.Create(LayoutPreset.Grid2x2);

        var result = LayoutTree.ClosePane(root, "p2");

        Assert.NotNull(result);
        Assert.Equal(new[] { "p1", "p3", "p4" }, LayoutTree.Panes(result!).Select(p => p.Id));
        var top = Assert.IsType<SplitNode>(result);
        Assert.Equal(new PaneNode("p1"), top.First);
    }

    [Fact]
    public void ClosePane_LastPane_ReturnsNull()
    {
        var root = LayoutPresets.Create(LayoutPreset.Single);

        Assert.Null(LayoutTree.ClosePane(root, "p1"));
    }

    [Fact]
    public void SetRatio_MovesTheRootSplitter_AndClamps()
    {
        var root = LayoutPresets.Create(LayoutPreset.TwoColumns);

        var moved = Assert.IsType<SplitNode>(LayoutTree.SetRatio(root, Array.Empty<int>(), 0.7));
        var clamped = Assert.IsType<SplitNode>(LayoutTree.SetRatio(root, Array.Empty<int>(), 5));

        Assert.Equal(0.7, moved.Ratio);
        Assert.Equal(LayoutTree.MaxRatio, clamped.Ratio);
    }

    [Fact]
    public void SetRatio_FollowsPathToNestedSplit()
    {
        var root = LayoutPresets.Create(LayoutPreset.Grid2x2);

        var result = Assert.IsType<SplitNode>(LayoutTree.SetRatio(root, new[] { 1 }, 0.25));

        Assert.Equal(0.5, result.Ratio);
        Assert.Equal(0.25, Assert.IsType<SplitNode>(result.Second).Ratio);
        Assert.Equal(0.5, Assert.IsType<SplitNode>(result.First).Ratio);
    }

    [Fact]
    public void SetRatio_PathThroughAPane_Throws()
    {
        var root = LayoutPresets.Create(LayoutPreset.TwoColumns);

        Assert.Throws<ArgumentException>(() => LayoutTree.SetRatio(root, new[] { 0 }, 0.4));
    }

    [Theory]
    [InlineData(LayoutPreset.Single)]
    [InlineData(LayoutPreset.Grid2x2)]
    [InlineData(LayoutPreset.OneBigTwoSmall)]
    public void Serialization_RoundTrips(LayoutPreset preset)
    {
        var original = LayoutTree.AssignConnection(LayoutPresets.Create(preset), "p1", "web-01");

        var json = LayoutSerializer.Serialize(original);
        var roundTripped = LayoutSerializer.Deserialize(json);

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void Serialization_WritesTypeDiscriminatorAndCamelCaseEnums()
    {
        var json = LayoutSerializer.Serialize(LayoutPresets.Create(LayoutPreset.TwoColumns));

        Assert.Contains("\"type\": \"split\"", json);
        Assert.Contains("\"type\": \"pane\"", json);
        Assert.Contains("\"orientation\": \"columns\"", json);
    }

    [Fact]
    public void Deserialize_AcceptsHandWrittenJson_WithDiscriminatorAnywhere()
    {
        const string json = """
            {
              "orientation": "rows",
              "ratio": 0.4,
              "first": { "id": "a", "connectionId": "db-01", "type": "pane" },
              "second": { "id": "b", "type": "pane" },
              "type": "split"
            }
            """;

        var root = LayoutSerializer.Deserialize(json);

        var split = Assert.IsType<SplitNode>(root);
        Assert.Equal(SplitOrientation.Rows, split.Orientation);
        Assert.Equal(new PaneNode("a", "db-01"), split.First);
    }

    [Fact]
    public void Deserialize_DuplicatePaneIds_Throws()
    {
        const string json = """
            {
              "type": "split", "orientation": "columns", "ratio": 0.5,
              "first": { "type": "pane", "id": "a" },
              "second": { "type": "pane", "id": "a" }
            }
            """;

        Assert.Throws<LayoutException>(() => LayoutSerializer.Deserialize(json));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-0.2")]
    public void Deserialize_RatioOutsideZeroToOne_Throws(string ratio)
    {
        var json = $$"""
            {
              "type": "split", "orientation": "columns", "ratio": {{ratio}},
              "first": { "type": "pane", "id": "a" },
              "second": { "type": "pane", "id": "b" }
            }
            """;

        Assert.Throws<LayoutException>(() => LayoutSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_BrokenJson_ThrowsLayoutException()
    {
        Assert.Throws<LayoutException>(() => LayoutSerializer.Deserialize("{ not json"));
    }

    [Fact]
    public void Workspace_RoundTrips()
    {
        var layout = LayoutTree.AssignConnection(LayoutPresets.Create(LayoutPreset.TwoColumns), "p1", "web-01");
        var workspace = new Workspace("Web pair", layout, "Two web hosts", true, new[] { "p1", "p2" });

        var parsed = LayoutSerializer.DeserializeWorkspace(LayoutSerializer.SerializeWorkspace(workspace));

        Assert.Equal(workspace.Name, parsed.Name);
        Assert.Equal(workspace.Layout, parsed.Layout);
        Assert.True(parsed.AutoConnect);
        Assert.Equal(new[] { "p1", "p2" }, parsed.BroadcastMembers);
    }

    [Fact]
    public void Workspace_BroadcastMemberThatIsNotAPane_Throws()
    {
        var workspace = new Workspace("Bad", LayoutPresets.Create(LayoutPreset.TwoColumns), null, false, new[] { "p9" });

        Assert.Throws<LayoutException>(() => LayoutSerializer.SerializeWorkspace(workspace));
    }

    [Fact]
    public void SampleWorkspaceFiles_ParseAndValidate()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "workspaces");
        var files = Directory.GetFiles(folder, "*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var workspace = LayoutSerializer.DeserializeWorkspace(File.ReadAllText(file));
            Assert.False(string.IsNullOrWhiteSpace(workspace.Name));
        }
    }
}

public class WorkspaceLibraryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rd-ws-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Workspace Sample(string name, string? connection = "web-01") =>
        new(name, new PaneNode("p1", connection), "d", true);

    [Fact]
    public void SavedWorkspaces_AreFoundAgain()
    {
        var library = new WorkspaceLibrary(_folder);
        library.Save(Sample("Prod web"));
        library.Save(Sample("Alpha"));

        var all = library.LoadAll();

        Assert.Equal(new[] { "Alpha", "Prod web" }, all.Select(f => f.Workspace!.Name));
        Assert.Equal("web-01", ((PaneNode)all[1].Workspace!.Layout).ConnectionId);
    }

    [Fact]
    public void SavingTheSameNameAgain_ReplacesTheFile()
    {
        var library = new WorkspaceLibrary(_folder);
        var first = library.Save(Sample("Prod", "a"));
        var second = library.Save(Sample("prod", "b"));

        Assert.Equal(first, second);
        var only = Assert.Single(library.LoadAll());
        Assert.Equal("b", ((PaneNode)only.Workspace!.Layout).ConnectionId);
    }

    [Fact]
    public void UnsafeNames_BecomeSafeFileNames_AndDifferentNamesNeverShareAFile()
    {
        var library = new WorkspaceLibrary(_folder);
        var a = library.Save(Sample("a/b:c"));
        var b = library.Save(Sample("a-b-c"));

        Assert.NotEqual(a, b);
        Assert.Equal(_folder, Path.GetDirectoryName(a));
        Assert.Equal(2, library.LoadAll().Count);
    }

    [Fact]
    public void ADamagedFile_IsListedWithItsError()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "bad.json"), "{ nope");
        var library = new WorkspaceLibrary(_folder);
        library.Save(Sample("Good"));

        var all = library.LoadAll();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, f => f.Error is not null && f.Path.EndsWith("bad.json"));
        Assert.Contains(all, f => f.Workspace?.Name == "Good");
    }

    [Fact]
    public void Delete_RemovesTheFile_AndReportsAMissingOne()
    {
        var library = new WorkspaceLibrary(_folder);
        library.Save(Sample("Gone"));

        Assert.True(library.Delete("gone"));
        Assert.False(library.Delete("gone"));
        Assert.Empty(library.LoadAll());
    }

    [Fact]
    public void ALibraryFolderThatDoesNotExistYet_IsJustEmpty() =>
        Assert.Empty(new WorkspaceLibrary(_folder).LoadAll());
}
