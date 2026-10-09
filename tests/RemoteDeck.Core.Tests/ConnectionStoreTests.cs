using RemoteDeck.Core.Connections;

namespace RemoteDeck.Core.Tests;

public class ConnectionStoreTests
{
    private static ConnectionEntry Conn(
        string id,
        string? name = null,
        string host = "10.0.0.1",
        string type = "ssh",
        string? folder = null,
        string? credential = null,
        string[]? tags = null,
        bool favorite = false) =>
        new(id, name ?? id, type, host, FolderId: folder, CredentialId: credential, Tags: tags, Favorite: favorite);

    private static ConnectionStore Store(params FolderEntry[] folders)
    {
        var store = new ConnectionStore();
        foreach (var folder in folders)
        {
            store.AddFolder(folder);
        }

        return store;
    }

    // ---- adding and validating ----

    [Fact]
    public void AddConnection_CleansUpTheEntry()
    {
        var store = new ConnectionStore();

        store.AddConnection(Conn("c1", "  Web 01 ", " 10.0.0.9 ", "SSH", tags: new[] { " prod", "Prod", "web", "" }));

        var saved = store.FindConnection("c1")!;
        Assert.Equal("Web 01", saved.Name);
        Assert.Equal("10.0.0.9", saved.Host);
        Assert.Equal("ssh", saved.Type);
        Assert.Equal(new[] { "prod", "web" }, saved.Tags);
    }

    [Fact]
    public void AddConnection_WithOnlyBlankTags_StoresNoTags()
    {
        var store = new ConnectionStore();

        store.AddConnection(Conn("c1", tags: new[] { " ", "" }));

        Assert.Null(store.FindConnection("c1")!.Tags);
    }

    [Fact]
    public void AddConnection_DuplicateId_Throws()
    {
        var store = new ConnectionStore();
        store.AddConnection(Conn("c1"));

        Assert.Throws<CatalogException>(() => store.AddConnection(Conn("c1", "other")));
    }

    [Fact]
    public void AddConnection_RejectsInvalidFields()
    {
        var store = new ConnectionStore();
        var good = Conn("c1");

        Assert.Throws<CatalogException>(() => store.AddConnection(good with { Id = " " }));
        Assert.Throws<CatalogException>(() => store.AddConnection(good with { Name = "" }));
        Assert.Throws<CatalogException>(() => store.AddConnection(good with { Host = " " }));
        Assert.Throws<CatalogException>(() => store.AddConnection(good with { Type = "" }));
        Assert.Throws<CatalogException>(() => store.AddConnection(good with { Port = 0 }));
        Assert.Throws<CatalogException>(() => store.AddConnection(good with { Port = 70000 }));
        Assert.Throws<CatalogException>(() => store.AddConnection(good with { Color = "red" }));
        Assert.Throws<CatalogException>(() => store.AddConnection(good with { FolderId = "missing" }));
        Assert.Empty(store.Connections);
    }

    [Fact]
    public void AddConnection_AcceptsValidPortAndColor()
    {
        var store = new ConnectionStore();

        store.AddConnection(Conn("c1") with { Port = 2222, Color = "#E5484D" });

        var saved = store.FindConnection("c1")!;
        Assert.Equal(2222, saved.Port);
        Assert.Equal("#E5484D", saved.Color);
    }

    [Fact]
    public void UpdateConnection_ReplacesTheEntry_AndRequiresItToExist()
    {
        var store = new ConnectionStore();
        store.AddConnection(Conn("c1"));

        store.UpdateConnection(Conn("c1", "Renamed", favorite: true));

        Assert.Equal("Renamed", store.FindConnection("c1")!.Name);
        Assert.True(store.FindConnection("c1")!.Favorite);
        Assert.Throws<CatalogException>(() => store.UpdateConnection(Conn("nope")));
    }

    [Fact]
    public void RemoveConnection_Works()
    {
        var store = new ConnectionStore();
        store.AddConnection(Conn("c1"));

        Assert.True(store.RemoveConnection("c1"));
        Assert.False(store.RemoveConnection("c1"));
        Assert.Null(store.FindConnection("c1"));
    }

    // ---- folders ----

    [Fact]
    public void Folders_CanBeNested_AndHaveDisplayPaths()
    {
        var store = Store(
            new FolderEntry("prod", "Prod"),
            new FolderEntry("web", "Web", "prod"));

        Assert.Equal("Prod / Web", store.FolderPath("web"));
        Assert.Equal("Prod", store.FolderPath("prod"));
        Assert.Equal(string.Empty, store.FolderPath(null));
    }

    [Fact]
    public void AddFolder_WithUnknownParent_Throws()
    {
        var store = new ConnectionStore();

        Assert.Throws<CatalogException>(() => store.AddFolder(new FolderEntry("a", "A", "missing")));
    }

    [Fact]
    public void AddFolder_DuplicateId_Throws()
    {
        var store = Store(new FolderEntry("a", "A"));

        Assert.Throws<CatalogException>(() => store.AddFolder(new FolderEntry("a", "Again")));
    }

    [Fact]
    public void UpdateFolder_CannotMoveAFolderInsideItselfOrItsDescendants()
    {
        var store = Store(
            new FolderEntry("a", "A"),
            new FolderEntry("b", "B", "a"),
            new FolderEntry("c", "C", "b"));

        Assert.Throws<CatalogException>(() => store.UpdateFolder(new FolderEntry("a", "A", "a")));
        Assert.Throws<CatalogException>(() => store.UpdateFolder(new FolderEntry("a", "A", "c")));
        Assert.Null(store.FindFolder("a")!.ParentId);
    }

    [Fact]
    public void UpdateFolder_CanMoveAFolderElsewhere()
    {
        var store = Store(
            new FolderEntry("a", "A"),
            new FolderEntry("b", "B"),
            new FolderEntry("c", "C", "a"));

        store.UpdateFolder(new FolderEntry("c", "C", "b"));

        Assert.Equal("B / C", store.FolderPath("c"));
    }

    [Fact]
    public void RemoveFolder_WithoutDeletingContents_MovesThemUp()
    {
        var store = Store(
            new FolderEntry("prod", "Prod"),
            new FolderEntry("web", "Web", "prod"),
            new FolderEntry("deep", "Deep", "web"));
        store.AddConnection(Conn("c1", folder: "web"));
        store.AddConnection(Conn("c2", folder: "deep"));

        Assert.True(store.RemoveFolder("web"));

        Assert.Null(store.FindFolder("web"));
        Assert.Equal("prod", store.FindFolder("deep")!.ParentId);
        Assert.Equal("prod", store.FindConnection("c1")!.FolderId);
        Assert.Equal("deep", store.FindConnection("c2")!.FolderId);
    }

    [Fact]
    public void RemoveFolder_WithContents_DeletesEverythingInside()
    {
        var store = Store(
            new FolderEntry("prod", "Prod"),
            new FolderEntry("web", "Web", "prod"),
            new FolderEntry("other", "Other"));
        store.AddConnection(Conn("c1", folder: "web"));
        store.AddConnection(Conn("c2", folder: "other"));

        Assert.True(store.RemoveFolder("prod", deleteContents: true));

        Assert.Equal(new[] { "other" }, store.Folders.Select(f => f.Id));
        Assert.Equal(new[] { "c2" }, store.Connections.Select(c => c.Id));
    }

    [Fact]
    public void ContentsOf_ListsEverythingInsideAtAnyDepth()
    {
        var store = Store(
            new FolderEntry("prod", "Prod"),
            new FolderEntry("web", "Web", "prod"),
            new FolderEntry("deep", "Deep", "web"),
            new FolderEntry("other", "Other"));
        store.AddConnection(Conn("a", folder: "prod"));
        store.AddConnection(Conn("b", folder: "deep"));
        store.AddConnection(Conn("c", folder: "other"));

        var (folders, connections) = store.ContentsOf("prod");

        Assert.Equal(new[] { "deep", "web" }, folders.Select(f => f.Id).OrderBy(x => x));
        Assert.Equal(new[] { "a", "b" }, connections.Select(c => c.Id).OrderBy(x => x));
        Assert.Empty(store.ContentsOf("other").Folders);
        Assert.Empty(store.ContentsOf("nope").Connections);
    }

    [Fact]
    public void RemoveFolder_Unknown_ReturnsFalse()
    {
        Assert.False(new ConnectionStore().RemoveFolder("nope"));
    }

    // ---- credentials ----

    [Fact]
    public void Credential_IsInheritedFromTheNearestFolder()
    {
        var store = Store(
            new FolderEntry("prod", "Prod", CredentialId: "cred-prod"),
            new FolderEntry("web", "Web", "prod"),
            new FolderEntry("db", "DB", "prod", CredentialId: "cred-db"));
        store.AddConnection(Conn("direct", folder: "web", credential: "cred-own"));
        store.AddConnection(Conn("inherits-parent", folder: "web"));
        store.AddConnection(Conn("inherits-near", folder: "db"));
        store.AddConnection(Conn("none"));

        Assert.Equal("cred-own", store.CredentialIdFor("direct"));
        Assert.Equal("cred-prod", store.CredentialIdFor("inherits-parent"));
        Assert.Equal("cred-db", store.CredentialIdFor("inherits-near"));
        Assert.Null(store.CredentialIdFor("none"));
        Assert.Null(store.CredentialIdFor("unknown"));
    }

    [Fact]
    public void ResolveDefinition_FillsInTheInheritedCredential()
    {
        var store = Store(new FolderEntry("prod", "Prod", CredentialId: "cred-prod"));
        store.AddConnection(Conn("c1", "Web", "10.0.0.5", "ssh", "prod", tags: new[] { "web" }) with { Port = 2222 });

        var definition = store.ResolveDefinition("c1")!;

        Assert.Equal("c1", definition.Id);
        Assert.Equal("Web", definition.Name);
        Assert.Equal("ssh", definition.Type);
        Assert.Equal("10.0.0.5", definition.Host);
        Assert.Equal(2222, definition.Port);
        Assert.Equal("cred-prod", definition.CredentialId);
        Assert.Equal(new[] { "web" }, definition.Tags);
        Assert.Null(store.ResolveDefinition("unknown"));
    }

    [Fact]
    public void ReferencedCredentialIds_ListsFolderAndConnectionCredentials()
    {
        var store = Store(new FolderEntry("prod", "Prod", CredentialId: "cred-prod"));
        store.AddConnection(Conn("c1", credential: "cred-own"));
        store.AddConnection(Conn("c2"));

        var ids = store.ReferencedCredentialIds();

        Assert.Equal(2, ids.Count);
        Assert.Contains("cred-prod", ids);
        Assert.Contains("cred-own", ids);
    }

    // ---- tree ----

    [Fact]
    public void BuildTree_PutsFoldersFirst_ThenConnections_EachByName()
    {
        var store = Store(
            new FolderEntry("z", "Zeta"),
            new FolderEntry("a", "alpha"),
            new FolderEntry("sub", "Sub", "a"));
        store.AddConnection(Conn("c-top-b", "banana"));
        store.AddConnection(Conn("c-top-a", "Apple"));
        store.AddConnection(Conn("c-in-a", "inside", folder: "a"));

        var tree = store.BuildTree();

        Assert.Equal(4, tree.Count);
        var alpha = Assert.IsType<FolderNode>(tree[0]);
        Assert.Equal("alpha", alpha.Folder.Name);
        Assert.Equal("Zeta", Assert.IsType<FolderNode>(tree[1]).Folder.Name);
        Assert.Equal("Apple", Assert.IsType<ConnectionNode>(tree[2]).Connection.Name);
        Assert.Equal("banana", Assert.IsType<ConnectionNode>(tree[3]).Connection.Name);

        Assert.Equal(2, alpha.Children.Count);
        Assert.Equal("Sub", Assert.IsType<FolderNode>(alpha.Children[0]).Folder.Name);
        Assert.Equal("inside", Assert.IsType<ConnectionNode>(alpha.Children[1]).Connection.Name);
    }

    [Fact]
    public void BuildTree_OfAnEmptyStore_IsEmpty()
    {
        Assert.Empty(new ConnectionStore().BuildTree());
    }

    // ---- search ----

    private static string[] Names(IEnumerable<SearchResult> results) =>
        results.Select(r => r.Connection.Id).ToArray();

    [Fact]
    public void Search_RanksNamePrefixAboveHostMatch()
    {
        var store = new ConnectionStore();
        store.AddConnection(Conn("db", "db-01", "web-gateway.local"));
        store.AddConnection(Conn("web", "web-01", "10.0.0.5"));

        Assert.Equal(new[] { "web", "db" }, Names(store.Search("web")));
    }

    [Fact]
    public void Search_EveryWordMustMatch()
    {
        var store = new ConnectionStore();
        store.AddConnection(Conn("a", "web-01", tags: new[] { "prod" }));
        store.AddConnection(Conn("b", "web-02", tags: new[] { "dev" }));

        Assert.Equal(new[] { "a" }, Names(store.Search("web prod")));
        Assert.Empty(store.Search("web staging"));
    }

    [Fact]
    public void Search_IsCaseInsensitive_AndFindsTagsHostsAndTypes()
    {
        var store = new ConnectionStore();
        store.AddConnection(Conn("a", "alpha", "files.example.com", "sftp", tags: new[] { "Backup" }));
        store.AddConnection(Conn("b", "beta", "10.0.0.2", "rdp"));

        Assert.Equal(new[] { "a" }, Names(store.Search("BACKUP")));
        Assert.Equal(new[] { "a" }, Names(store.Search("example")));
        Assert.Equal(new[] { "b" }, Names(store.Search("RDP")));
    }

    [Fact]
    public void Search_MatchesTheFolderPath()
    {
        var store = Store(new FolderEntry("prod", "Prod"));
        store.AddConnection(Conn("alpha", folder: "prod"));
        store.AddConnection(Conn("beta"));

        var results = store.Search("prod");

        Assert.Equal(new[] { "alpha" }, Names(results));
        Assert.Equal("Prod", results[0].FolderPath);
    }

    [Fact]
    public void Search_FavoritesWinTies()
    {
        var store = new ConnectionStore();
        store.AddConnection(Conn("web-a", favorite: false));
        store.AddConnection(Conn("web-b", favorite: true));

        Assert.Equal(new[] { "web-b", "web-a" }, Names(store.Search("web")));
    }

    [Fact]
    public void Search_FindsAbbreviationsOfTheName()
    {
        var store = new ConnectionStore();
        store.AddConnection(Conn("a", "prod-web", "10.0.0.1"));
        store.AddConnection(Conn("b", "dev-box", "10.0.0.2"));

        Assert.Equal(new[] { "a" }, Names(store.Search("prd")));
    }

    [Fact]
    public void Search_SingleLetterIsNotAnAbbreviation()
    {
        var store = new ConnectionStore();
        store.AddConnection(Conn("a", "alpha", "10.0.0.1"));

        Assert.Empty(store.Search("q"));
    }

    [Fact]
    public void Search_WithNoMatch_ReturnsNothing()
    {
        var store = new ConnectionStore();
        store.AddConnection(Conn("a", "alpha"));

        Assert.Empty(store.Search("zzz"));
    }

    [Fact]
    public void Search_EmptyQuery_ListsFavoritesFirst_ThenByName()
    {
        var store = new ConnectionStore();
        store.AddConnection(Conn("c", "charlie"));
        store.AddConnection(Conn("b", "bravo", favorite: true));
        store.AddConnection(Conn("a", "alpha"));

        Assert.Equal(new[] { "b", "a", "c" }, Names(store.Search("")));
        Assert.Equal(new[] { "b", "a", "c" }, Names(store.Search(null)));
        Assert.Equal(new[] { "b", "a", "c" }, Names(store.Search("   ")));
    }

    [Fact]
    public void Search_HonorsTheResultLimit()
    {
        var store = new ConnectionStore();
        for (var i = 0; i < 5; i++)
        {
            store.AddConnection(Conn("web-" + i));
        }

        Assert.Equal(2, store.Search("web", 2).Count);
        Assert.Empty(store.Search("web", 0));
    }

    // ---- persistence ----

    [Fact]
    public void Serialize_ThenDeserialize_KeepsEverything()
    {
        var store = Store(
            new FolderEntry("prod", "Prod", CredentialId: "cred-prod", Color: "#E5484D"),
            new FolderEntry("web", "Web", "prod"));
        store.AddConnection(new ConnectionEntry(
            "c1",
            "Web 01",
            "ssh",
            "10.0.0.5",
            Port: 2222,
            FolderId: "web",
            CredentialId: "cred-own",
            Tags: new[] { "prod", "web" },
            Options: new Dictionary<string, string> { ["font"] = "Cascadia Code" },
            Favorite: true,
            Color: "#112233",
            Icon: "server",
            Notes: "Primary web host"));

        var copy = ConnectionStore.Deserialize(store.Serialize());

        var folder = copy.FindFolder("prod")!;
        Assert.Equal("cred-prod", folder.CredentialId);
        Assert.Equal("#E5484D", folder.Color);
        Assert.Equal("prod", copy.FindFolder("web")!.ParentId);

        var c = copy.FindConnection("c1")!;
        Assert.Equal("Web 01", c.Name);
        Assert.Equal("ssh", c.Type);
        Assert.Equal("10.0.0.5", c.Host);
        Assert.Equal(2222, c.Port);
        Assert.Equal("web", c.FolderId);
        Assert.Equal("cred-own", c.CredentialId);
        Assert.Equal(new[] { "prod", "web" }, c.Tags);
        Assert.Equal("Cascadia Code", c.Options!["font"]);
        Assert.True(c.Favorite);
        Assert.Equal("#112233", c.Color);
        Assert.Equal("server", c.Icon);
        Assert.Equal("Primary web host", c.Notes);
    }

    [Fact]
    public void Serialize_WritesNoNullFields_AndNoSecrets()
    {
        var store = new ConnectionStore();
        store.AddConnection(Conn("c1", credential: "cred-1"));

        var json = store.Serialize();

        Assert.DoesNotContain("null", json);
        Assert.Contains("\"credentialId\": \"cred-1\"", json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_AcceptsAHandWrittenFile_WithOnlyTheRequiredFields()
    {
        const string json = """
            {
              "version": 1,
              "connections": [
                { "id": "c1", "name": "Router", "type": "SSH", "host": "192.168.1.1" }
              ]
            }
            """;

        var store = ConnectionStore.Deserialize(json);

        var c = store.FindConnection("c1")!;
        Assert.Equal("ssh", c.Type);
        Assert.False(c.Favorite);
        Assert.Null(c.FolderId);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{ "version": 2 }""")]
    [InlineData("""{ "version": 1, "connections": [ { "id": "c1", "name": "x", "type": "ssh", "host": "h", "folderId": "missing" } ] }""")]
    [InlineData("""{ "version": 1, "connections": [ { "id": "c1", "name": "x", "type": "ssh", "host": "h" }, { "id": "c1", "name": "y", "type": "ssh", "host": "h" } ] }""")]
    [InlineData("""{ "version": 1, "folders": [ { "id": "a", "name": "A" }, { "id": "a", "name": "B" } ] }""")]
    [InlineData("""{ "version": 1, "folders": [ { "id": "a", "name": "A", "parentId": "b" }, { "id": "b", "name": "B", "parentId": "a" } ] }""")]
    [InlineData("""{ "version": 1, "folders": [ { "id": "a", "name": "A", "parentId": "missing" } ] }""")]
    [InlineData("""{ "version": 1, "connections": [ { "id": "c1", "name": "x", "type": "ssh", "host": "h", "port": 99999 } ] }""")]
    public void Deserialize_RejectsInvalidFiles(string json)
    {
        Assert.Throws<CatalogException>(() => ConnectionStore.Deserialize(json));
    }

    // ---- events ----

    [Fact]
    public void Changed_IsRaisedOnlyWhenSomethingChanges()
    {
        var store = new ConnectionStore();
        var changes = 0;
        store.Changed += (_, _) => changes++;

        store.AddFolder(new FolderEntry("f", "F"));
        store.AddConnection(Conn("c1"));
        store.UpdateConnection(Conn("c1", "renamed"));
        store.RemoveConnection("c1");
        store.RemoveConnection("c1");
        store.RemoveFolder("missing");
        store.RemoveFolder("f");

        Assert.Equal(5, changes);
    }

    [Fact]
    public void NewId_ProducesDistinctNonBlankIds()
    {
        var first = ConnectionStore.NewId();
        var second = ConnectionStore.NewId();

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.NotEqual(first, second);
    }

    // ---- moving ----

    [Fact]
    public void MoveConnection_PutsItInTheFolderAndBackToTheTop()
    {
        var store = Store(new FolderEntry("f1", "Prod"));
        store.AddConnection(Conn("c1"));

        store.MoveConnection("c1", "f1");
        Assert.Equal("f1", store.FindConnection("c1")!.FolderId);

        store.MoveConnection("c1", null);
        Assert.Null(store.FindConnection("c1")!.FolderId);
    }

    [Fact]
    public void MoveConnection_ToAMissingFolderIsRefused()
    {
        var store = new ConnectionStore();
        store.AddConnection(Conn("c1"));

        Assert.Throws<CatalogException>(() => store.MoveConnection("c1", "nope"));
        Assert.Null(store.FindConnection("c1")!.FolderId);
    }

    [Fact]
    public void MoveConnection_RaisesChangedOnlyWhenSomethingMoved()
    {
        var store = Store(new FolderEntry("f1", "Prod"));
        store.AddConnection(Conn("c1"));
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.MoveConnection("c1", null);
        Assert.Equal(0, raised);

        store.MoveConnection("c1", "f1");
        Assert.Equal(1, raised);
    }

    [Fact]
    public void MoveFolder_NestsAndUnnests()
    {
        var store = Store(new FolderEntry("a", "A"), new FolderEntry("b", "B"));

        store.MoveFolder("b", "a");
        Assert.Equal("a", store.FindFolder("b")!.ParentId);

        store.MoveFolder("b", null);
        Assert.Null(store.FindFolder("b")!.ParentId);
    }

    [Fact]
    public void MoveFolder_IntoItselfOrItsOwnSubfolderIsRefused()
    {
        var store = Store(new FolderEntry("a", "A"), new FolderEntry("b", "B", "a"), new FolderEntry("c", "C", "b"));

        Assert.Throws<CatalogException>(() => store.MoveFolder("a", "a"));
        Assert.Throws<CatalogException>(() => store.MoveFolder("a", "c"));
        Assert.Null(store.FindFolder("a")!.ParentId);
    }

    [Fact]
    public void CanMoveFolder_MatchesWhatMoveFolderAccepts()
    {
        var store = Store(new FolderEntry("a", "A"), new FolderEntry("b", "B", "a"), new FolderEntry("x", "X"));

        Assert.True(store.CanMoveFolder("a", null));
        Assert.True(store.CanMoveFolder("a", "x"));
        Assert.True(store.CanMoveFolder("b", "x"));
        Assert.False(store.CanMoveFolder("a", "a"));
        Assert.False(store.CanMoveFolder("a", "b"));
        Assert.False(store.CanMoveFolder("a", "missing"));
        Assert.False(store.CanMoveFolder("missing", null));
    }
}

public class CatalogStorageTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "remotedeck-catalog-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_AndCreatesMissingFolders()
    {
        var path = Path.Combine(_directory, "nested", "connections.json");
        var store = new ConnectionStore();
        store.AddConnection(new ConnectionEntry("c1", "Router", "ssh", "192.168.1.1"));

        CatalogStorage.Save(store, path);
        var loaded = CatalogStorage.Load(path);

        Assert.Equal("Router", loaded.FindConnection("c1")!.Name);
    }

    [Fact]
    public void Saving_LeavesNoTemporaryFile_AndKeepsTheOldVersionAsBackup()
    {
        var path = Path.Combine(_directory, "connections.json");
        var store = new ConnectionStore();

        CatalogStorage.Save(store, path);
        store.AddConnection(new ConnectionEntry("c1", "Router", "ssh", "192.168.1.1"));
        CatalogStorage.Save(store, path);

        Assert.False(File.Exists(path + ".tmp"));
        Assert.True(File.Exists(path + ".bak"));
        Assert.Null(CatalogStorage.Load(path + ".bak").FindConnection("c1"));
        Assert.NotNull(CatalogStorage.Load(path).FindConnection("c1"));
    }

    [Fact]
    public void LoadOrCreate_OfAMissingFile_GivesAnEmptyStore()
    {
        var store = CatalogStorage.LoadOrCreate(Path.Combine(_directory, "missing.json"));

        Assert.Empty(store.Connections);
        Assert.Empty(store.Folders);
    }
}
