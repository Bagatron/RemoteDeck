using RemoteDeck.Core.Connections;

namespace RemoteDeck.Core.Tests;

public class SavedLoginTests
{
    private static ConnectionEntry Conn(string id, string? credential = null) =>
        new(id, id, "ssh", "10.0.0.1", CredentialId: credential);

    [Fact]
    public void AddUpdateAndRemove()
    {
        var store = new ConnectionStore();
        store.AddLogin(new LoginEntry("login-1", "Lab admin", "admin"));

        Assert.Equal("admin", store.FindLogin("login-1")!.Username);

        store.UpdateLogin(new LoginEntry("login-1", "Lab admin", "root"));
        Assert.Equal("root", store.FindLogin("login-1")!.Username);

        Assert.True(store.RemoveLogin("login-1"));
        Assert.False(store.RemoveLogin("login-1"));
        Assert.Empty(store.Logins);
    }

    [Fact]
    public void NamesMustBeUnique_IgnoringCase()
    {
        var store = new ConnectionStore();
        store.AddLogin(new LoginEntry("a", "Lab admin"));

        Assert.Throws<CatalogException>(() => store.AddLogin(new LoginEntry("b", "lab ADMIN")));
        store.AddLogin(new LoginEntry("b", "Other"));
        Assert.Throws<CatalogException>(() => store.UpdateLogin(new LoginEntry("b", "Lab Admin")));
    }

    [Fact]
    public void ANameAndIdAreRequired()
    {
        var store = new ConnectionStore();

        Assert.Throws<CatalogException>(() => store.AddLogin(new LoginEntry("a", "  ")));
        Assert.Throws<CatalogException>(() => store.AddLogin(new LoginEntry(" ", "Name")));
        Assert.Throws<CatalogException>(() => store.UpdateLogin(new LoginEntry("missing", "Name")));
    }

    [Fact]
    public void UniqueLoginName_AddsANumberWhenTaken()
    {
        var store = new ConnectionStore();
        store.AddLogin(new LoginEntry("a", "Lab"));
        store.AddLogin(new LoginEntry("b", "Lab (2)"));

        Assert.Equal("lab (3)", store.UniqueLoginName("lab"));
        Assert.Equal("Fresh", store.UniqueLoginName("Fresh"));
        Assert.Equal("Login", store.UniqueLoginName("  "));
    }

    [Fact]
    public void ALoginCountsAsReferenced_SoItsVaultEntryIsKept()
    {
        var store = new ConnectionStore();
        store.AddLogin(new LoginEntry("login-1", "Lab"));
        store.AddConnection(Conn("c1", "login-1"));

        Assert.Contains("login-1", store.ReferencedCredentialIds());

        store.RemoveConnection("c1");
        Assert.Contains("login-1", store.ReferencedCredentialIds());

        store.RemoveLogin("login-1");
        Assert.DoesNotContain("login-1", store.ReferencedCredentialIds());
    }

    [Fact]
    public void AConnectionThatUsedALoginKeepsItAfterTheLoginIsRemoved()
    {
        var store = new ConnectionStore();
        store.AddLogin(new LoginEntry("login-1", "Lab"));
        store.AddConnection(Conn("c1", "login-1"));

        store.RemoveLogin("login-1");

        Assert.Equal("login-1", store.CredentialIdFor("c1"));
        Assert.Contains("login-1", store.ReferencedCredentialIds());
        Assert.Equal(1, store.UsesOfCredential("login-1"));
    }

    [Fact]
    public void UsesOfCredential_CountsConnectionsAndFolders()
    {
        var store = new ConnectionStore();
        store.AddFolder(new FolderEntry("f", "Folder", CredentialId: "shared"));
        store.AddConnection(Conn("a", "shared"));
        store.AddConnection(Conn("b", "shared"));
        store.AddConnection(Conn("c"));

        Assert.Equal(3, store.UsesOfCredential("shared"));
        Assert.Equal(0, store.UsesOfCredential("nobody"));
    }

    [Fact]
    public void LoginsSurviveSerialization_AndHoldNoSecrets()
    {
        var store = new ConnectionStore();
        store.AddLogin(new LoginEntry("login-1", "Lab", "admin"));

        var json = store.Serialize();
        var copy = ConnectionStore.Deserialize(json);

        Assert.Equal("Lab", copy.FindLogin("login-1")!.Name);
        Assert.Equal("admin", copy.FindLogin("login-1")!.Username);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnOlderConnectionFileWithoutLoginsStillLoads()
    {
        var store = ConnectionStore.Deserialize("{\"version\":1,\"folders\":[],\"connections\":[]}");

        Assert.Empty(store.Logins);
    }

    [Fact]
    public void AddingALoginRaisesChanged()
    {
        var store = new ConnectionStore();
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.AddLogin(new LoginEntry("a", "Lab"));
        store.UpdateLogin(new LoginEntry("a", "Lab", "x"));
        store.RemoveLogin("a");

        Assert.Equal(3, raised);
    }
}
