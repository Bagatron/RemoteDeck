using RemoteDeck.Plugin;

namespace RemoteDeck.Vault.Tests;

public class VaultStorageTests : IDisposable
{
    private const string Master = "storage test password";
    private static readonly KdfParameters Cheap = new(1024, 1, 1);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "remotedeck-vault-tests-" + Guid.NewGuid().ToString("N"));

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
    public async Task SaveThenLoad_RoundTrips_AndCreatesMissingFolders()
    {
        var path = Path.Combine(_directory, "nested", "vault.json");
        using var vault = CredentialVault.Create(Master, Cheap, new FastKeyDerivation());
        vault.Set("c1", "admin", "secret");

        VaultStorage.Save(vault, path);
        using var loaded = VaultStorage.Load(path, Master, new FastKeyDerivation());

        string? password = null;
        await loaded.UseAsync<object?>("c1", credential =>
        {
            credential.ReadPassword(span => password = span.ToString());
            return ValueTask.FromResult<object?>(null);
        });
        Assert.Equal("secret", password);
    }

    [Fact]
    public void Saving_LeavesNoTemporaryFile_AndKeepsTheOldVersionAsBackup()
    {
        var path = Path.Combine(_directory, "vault.json");
        using var vault = CredentialVault.Create(Master, Cheap, new FastKeyDerivation());

        VaultStorage.Save(vault, path);
        vault.Set("c1", "admin", "secret");
        VaultStorage.Save(vault, path);

        Assert.False(File.Exists(path + ".tmp"));
        Assert.True(File.Exists(path + ".bak"));
        using var backup = VaultStorage.Load(path + ".bak", Master, new FastKeyDerivation());
        Assert.False(backup.Contains("c1"));
        using var current = VaultStorage.Load(path, Master, new FastKeyDerivation());
        Assert.True(current.Contains("c1"));
    }

    [Fact]
    public void Load_OfAMissingFile_Throws()
    {
        var path = Path.Combine(_directory, "nope.json");

        // The folder does not exist either, so this is a DirectoryNotFoundException; both are IOExceptions.
        Assert.ThrowsAny<IOException>(() => VaultStorage.Load(path, Master, new FastKeyDerivation()));
    }
}

public class CredentialBrokerTests
{
    private static CredentialVault NewVault()
    {
        var vault = CredentialVault.Create("broker test password", new KdfParameters(1024, 1, 1), new FastKeyDerivation());
        vault.Set("cred-1", "admin", "secret");
        return vault;
    }

    private static async Task<string?> ReadUsername(ICredentialBroker broker, string connectionId)
    {
        string? user = null;
        await broker.UseAsync<object?>(connectionId, credential =>
        {
            user = credential.Username;
            return ValueTask.FromResult<object?>(null);
        });
        return user;
    }

    [Fact]
    public async Task Broker_ResolvesAConnection_ToItsCredential()
    {
        using var vault = NewVault();
        var broker = new VaultCredentialBroker(vault, id => id == "web-01" ? "cred-1" : null);

        Assert.Equal("admin", await ReadUsername(broker, "web-01"));
    }

    [Fact]
    public async Task Broker_ForAConnectionWithoutACredential_Throws()
    {
        using var vault = NewVault();
        var broker = new VaultCredentialBroker(vault, _ => null);

        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await ReadUsername(broker, "web-01"));
    }

    [Fact]
    public async Task GuardedBroker_RefusesPluginsWithoutThePermission()
    {
        using var vault = NewVault();
        var inner = new VaultCredentialBroker(vault, _ => "cred-1");
        var guarded = new GuardedCredentialBroker(inner, "acme.proxmox", useCredentialsPermitted: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await ReadUsername(guarded, "web-01"));
    }

    [Fact]
    public async Task GuardedBroker_AllowsPluginsWithThePermission()
    {
        using var vault = NewVault();
        var inner = new VaultCredentialBroker(vault, _ => "cred-1");
        var guarded = new GuardedCredentialBroker(inner, "acme.proxmox", useCredentialsPermitted: true);

        Assert.Equal("admin", await ReadUsername(guarded, "web-01"));
    }
}
