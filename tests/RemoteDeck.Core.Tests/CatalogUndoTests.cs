using RemoteDeck.Core.Connections;

namespace RemoteDeck.Core.Tests;

public class CatalogUndoTests
{
    private static ConnectionStore Store()
    {
        var store = new ConnectionStore();
        store.AddFolder(new FolderEntry("f1", "Prod"));
        store.AddConnection(new ConnectionEntry("c1", "web-1", "ssh", "10.0.0.1", FolderId: "f1", CredentialId: "cred-c1"));
        store.AddConnection(new ConnectionEntry("c2", "web-2", "ssh", "10.0.0.2", FolderId: "f1"));
        store.AddConnection(new ConnectionEntry("c3", "lab", "ssh", "10.0.0.3"));
        return store;
    }

    [Fact]
    public void Undo_PutsBackADeletedConnection()
    {
        var store = Store();
        using var undo = new CatalogUndo(store);

        undo.Run("Delete web-1", () => store.RemoveConnection("c1"));
        Assert.Null(store.FindConnection("c1"));

        Assert.Equal("Delete web-1", undo.Undo());

        var back = store.FindConnection("c1");
        Assert.NotNull(back);
        Assert.Equal("cred-c1", back!.CredentialId);
        Assert.Equal("f1", back.FolderId);
    }

    [Fact]
    public void Undo_PutsBackAFolderWithEverythingInIt()
    {
        var store = Store();
        using var undo = new CatalogUndo(store);

        undo.Run("Delete Prod", () => store.RemoveFolder("f1", deleteContents: true));
        Assert.Null(store.FindFolder("f1"));
        Assert.Single(store.Connections);

        undo.Undo();

        Assert.NotNull(store.FindFolder("f1"));
        Assert.Equal(3, store.Connections.Count);
    }

    [Fact]
    public void Undo_UndoesMovesAndTheirOrder()
    {
        var store = Store();
        using var undo = new CatalogUndo(store);

        undo.Run("Move one", () => store.MoveConnection("c3", "f1"));
        undo.Run("Move two", () => store.MoveConnection("c3", null));

        Assert.Equal("Move two", undo.NextLabel);
        undo.Undo();
        Assert.Equal("f1", store.FindConnection("c3")!.FolderId);
        undo.Undo();
        Assert.Null(store.FindConnection("c3")!.FolderId);
        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void Undo_WithNothingToUndoReturnsNull()
    {
        using var undo = new CatalogUndo(Store());

        Assert.Null(undo.Undo());
        Assert.Null(undo.NextLabel);
    }

    [Fact]
    public void AStepThatChangesNothingIsNotKept()
    {
        var store = Store();
        using var undo = new CatalogUndo(store);

        undo.Run("Move nowhere", () => store.MoveConnection("c3", null));

        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void AStepThatFailsHalfWayCanStillBeUndone()
    {
        var store = Store();
        using var undo = new CatalogUndo(store);

        Assert.Throws<CatalogException>(() => undo.Run("Messy", () =>
        {
            store.RemoveConnection("c3");
            store.MoveConnection("missing", null);
        }));

        Assert.Null(store.FindConnection("c3"));
        undo.Undo();
        Assert.NotNull(store.FindConnection("c3"));
    }

    [Fact]
    public void AChangeMadeAnyOtherWay_StartsAFreshHistory()
    {
        var store = Store();
        using var undo = new CatalogUndo(store);
        undo.Run("Delete lab", () => store.RemoveConnection("c3"));

        store.AddConnection(new ConnectionEntry("c4", "new", "ssh", "10.0.0.4"));

        Assert.False(undo.CanUndo);
        Assert.Null(undo.Undo());
        Assert.NotNull(store.FindConnection("c4"));
    }

    [Fact]
    public void Credentials_AreReleasedOnlyWhenTheStepCanNoLongerBeUndone()
    {
        var store = Store();
        using var undo = new CatalogUndo(store);
        var released = new List<string>();
        undo.CredentialsReleased += ids => released.AddRange(ids);

        undo.Run("Delete web-1", () => store.RemoveConnection("c1"), new[] { "cred-c1" });
        Assert.Empty(released);

        // Undoing keeps them.
        undo.Undo();
        Assert.Empty(released);

        // A step that is left behind by an outside change releases them.
        undo.Run("Delete web-1", () => store.RemoveConnection("c1"), new[] { "cred-c1" });
        store.AddFolder(new FolderEntry("f2", "Other"));
        Assert.Equal(new[] { "cred-c1" }, released);
    }

    [Fact]
    public void Credentials_AreReleasedWhenTheOldestStepFallsOffTheEnd()
    {
        var store = Store();
        using var undo = new CatalogUndo(store, limit: 2);
        var released = new List<string>();
        undo.CredentialsReleased += ids => released.AddRange(ids);

        undo.Run("one", () => store.RemoveConnection("c1"), new[] { "cred-c1" });
        undo.Run("two", () => store.RemoveConnection("c2"));
        Assert.Empty(released);
        undo.Run("three", () => store.RemoveConnection("c3"));

        Assert.Equal(new[] { "cred-c1" }, released);
        Assert.Equal(2, undo.Count);
    }

    [Fact]
    public void Dispose_ReleasesWhatIsLeft()
    {
        var store = Store();
        var undo = new CatalogUndo(store);
        var released = new List<string>();
        undo.CredentialsReleased += ids => released.AddRange(ids);
        undo.Run("Delete web-1", () => store.RemoveConnection("c1"), new[] { "cred-c1", "cred-c1" });

        undo.Dispose();

        Assert.Equal(new[] { "cred-c1" }, released);
    }
}

public class ConnectionStoreRestoreTests
{
    [Fact]
    public void Restore_ReplacesEverything_AndRaisesChanged()
    {
        var original = new ConnectionStore();
        original.AddFolder(new FolderEntry("f1", "One"));
        original.AddConnection(new ConnectionEntry("c1", "a", "ssh", "h1", FolderId: "f1"));
        original.AddLogin(new LoginEntry("l1", "Admin", "root"));
        var json = original.Serialize();

        var store = new ConnectionStore();
        store.AddConnection(new ConnectionEntry("x", "other", "ssh", "h9"));
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.Restore(json);

        Assert.Equal(1, raised);
        Assert.Null(store.FindConnection("x"));
        Assert.NotNull(store.FindConnection("c1"));
        Assert.NotNull(store.FindFolder("f1"));
        Assert.NotNull(store.FindLogin("l1"));
    }

    [Fact]
    public void Restore_WithBadText_LeavesTheStoreAlone()
    {
        var store = new ConnectionStore();
        store.AddConnection(new ConnectionEntry("x", "other", "ssh", "h9"));

        Assert.Throws<CatalogException>(() => store.Restore("not json"));

        Assert.NotNull(store.FindConnection("x"));
    }
}
