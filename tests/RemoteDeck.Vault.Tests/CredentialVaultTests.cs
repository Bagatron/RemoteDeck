using System.Text;
using System.Text.Json.Nodes;
using RemoteDeck.Plugin;

namespace RemoteDeck.Vault.Tests;

public class CredentialVaultTests
{
    private const string Master = "correct horse battery staple";
    private static readonly KdfParameters Cheap = new(1024, 1, 1);

    private static CredentialVault NewVault() => CredentialVault.Create(Master, Cheap, new FastKeyDerivation());

    private static CredentialVault Reopen(CredentialVault vault, string password = Master) =>
        CredentialVault.Open(vault.Serialize(), password, new FastKeyDerivation());

    private static async Task<(string? User, string Password)> Read(CredentialVault vault, string id)
    {
        string? user = null;
        string? password = null;
        await vault.UseAsync<object?>(id, credential =>
        {
            user = credential.Username;
            credential.ReadPassword(span => password = span.ToString());
            return ValueTask.FromResult<object?>(null);
        });
        return (user, password!);
    }

    [Fact]
    public async Task StoredCredential_ComesBackUnchanged()
    {
        using var vault = NewVault();
        vault.Set("c1", "admin", "p@ss w0rd ünïcode ✓");

        var (user, password) = await Read(vault, "c1");

        Assert.Equal("admin", user);
        Assert.Equal("p@ss w0rd ünïcode ✓", password);
    }

    [Fact]
    public async Task CredentialWithoutUsername_ComesBackWithNullUsername()
    {
        using var vault = NewVault();
        vault.Set("c1", null, "secret");

        var (user, password) = await Read(vault, "c1");

        Assert.Null(user);
        Assert.Equal("secret", password);
    }

    [Fact]
    public async Task Credentials_SurviveSerializeAndReopen()
    {
        using var vault = NewVault();
        vault.Set("c1", "admin", "secret-1");
        vault.Set("c2", "root", "secret-2");

        using var reopened = Reopen(vault);

        Assert.Equal(new[] { "c1", "c2" }, reopened.Ids);
        var (user1, password1) = await Read(reopened, "c1");
        var (user2, password2) = await Read(reopened, "c2");
        Assert.Equal(("admin", "secret-1"), (user1, password1));
        Assert.Equal(("root", "secret-2"), (user2, password2));
    }

    [Fact]
    public void Open_WithWrongPassword_Throws()
    {
        using var vault = NewVault();
        vault.Set("c1", "admin", "secret");
        var json = vault.Serialize();

        Assert.Throws<InvalidMasterPasswordException>(
            () => CredentialVault.Open(json, "not the password", new FastKeyDerivation()));
    }

    [Fact]
    public void Serialized_File_ContainsNoPlaintext()
    {
        using var vault = NewVault();
        vault.Set("c1", "very-unique-user", "very-unique-password-9183");

        var json = vault.Serialize();

        Assert.DoesNotContain("very-unique-user", json);
        Assert.DoesNotContain("very-unique-password-9183", json);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("very-unique-password-9183")), json);
        Assert.DoesNotContain(Master, json);
    }

    [Fact]
    public void SettingTheSameCredentialTwice_ProducesDifferentCiphertext()
    {
        using var vault = NewVault();
        vault.Set("c1", "admin", "secret");
        var first = JsonNode.Parse(vault.Serialize())!["entries"]!["c1"]!.GetValue<string>();

        vault.Set("c1", "admin", "secret");
        var second = JsonNode.Parse(vault.Serialize())!["entries"]!["c1"]!.GetValue<string>();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CiphertextLength_DoesNotRevealPasswordLength()
    {
        using var vault = NewVault();
        vault.Set("short", "u", "a");
        vault.Set("longer", "u", "a-considerably-longer-password-value");

        var entries = JsonNode.Parse(vault.Serialize())!["entries"]!.AsObject();

        Assert.Equal(
            entries["short"]!.GetValue<string>().Length,
            entries["longer"]!.GetValue<string>().Length);
    }

    [Fact]
    public async Task LockedVault_RefusesUse_AndUnlocksAgain()
    {
        using var vault = NewVault();
        vault.Set("c1", "admin", "secret");

        vault.Lock();

        Assert.True(vault.IsLocked);
        await Assert.ThrowsAsync<VaultLockedException>(async () => await Read(vault, "c1"));
        Assert.Throws<VaultLockedException>(() => vault.Set("c2", "u", "p"));
        Assert.Throws<VaultLockedException>(() => vault.Remove("c1"));

        vault.Unlock(Master);

        Assert.False(vault.IsLocked);
        var (_, password) = await Read(vault, "c1");
        Assert.Equal("secret", password);
    }

    [Fact]
    public void Unlock_WithWrongPassword_Throws_AndStaysLocked()
    {
        using var vault = NewVault();
        vault.Lock();

        Assert.Throws<InvalidMasterPasswordException>(() => vault.Unlock("wrong"));
        Assert.True(vault.IsLocked);
    }

    [Fact]
    public void Serialize_WorksWhileLocked()
    {
        using var vault = NewVault();
        vault.Set("c1", "admin", "secret");
        vault.Lock();

        var json = vault.Serialize();

        using var reopened = CredentialVault.Open(json, Master, new FastKeyDerivation());
        Assert.True(reopened.Contains("c1"));
    }

    [Fact]
    public async Task ChangeMasterPassword_KeepsCredentials_AndReplacesThePassword()
    {
        using var vault = NewVault();
        vault.Set("c1", "admin", "secret-1");

        vault.ChangeMasterPassword(Master, "a brand new password");
        var json = vault.Serialize();

        Assert.Throws<InvalidMasterPasswordException>(
            () => CredentialVault.Open(json, Master, new FastKeyDerivation()));
        using var reopened = CredentialVault.Open(json, "a brand new password", new FastKeyDerivation());
        var (user, password) = await Read(reopened, "c1");
        Assert.Equal("admin", user);
        Assert.Equal("secret-1", password);
    }

    [Fact]
    public void ChangeMasterPassword_WithWrongCurrentPassword_Throws_AndChangesNothing()
    {
        using var vault = NewVault();
        vault.Set("c1", "admin", "secret");

        Assert.Throws<InvalidMasterPasswordException>(() => vault.ChangeMasterPassword("nope", "new password"));

        using var reopened = Reopen(vault);
        Assert.True(reopened.Contains("c1"));
    }

    [Fact]
    public void ChangeMasterPassword_ToEmpty_Throws()
    {
        using var vault = NewVault();

        Assert.Throws<ArgumentException>(() => vault.ChangeMasterPassword(Master, ""));
    }

    [Fact]
    public async Task SwappingTwoEntriesInTheFile_IsDetected()
    {
        using var vault = NewVault();
        vault.Set("c1", "admin", "secret-1");
        vault.Set("c2", "root", "secret-2");
        var node = JsonNode.Parse(vault.Serialize())!;
        var entries = node["entries"]!.AsObject();
        var first = entries["c1"]!.GetValue<string>();
        var second = entries["c2"]!.GetValue<string>();
        entries["c1"] = second;
        entries["c2"] = first;

        using var tampered = CredentialVault.Open(node.ToJsonString(), Master, new FastKeyDerivation());

        await Assert.ThrowsAnyAsync<VaultException>(async () => await Read(tampered, "c1"));
        await Assert.ThrowsAnyAsync<VaultException>(async () => await Read(tampered, "c2"));
    }

    [Fact]
    public async Task FlippingABitInAnEntry_IsDetected()
    {
        using var vault = NewVault();
        vault.Set("c1", "admin", "secret-1");
        var node = JsonNode.Parse(vault.Serialize())!;
        var entries = node["entries"]!.AsObject();
        var blob = Convert.FromBase64String(entries["c1"]!.GetValue<string>());
        blob[^1] ^= 0x01;
        entries["c1"] = Convert.ToBase64String(blob);

        using var tampered = CredentialVault.Open(node.ToJsonString(), Master, new FastKeyDerivation());

        await Assert.ThrowsAnyAsync<VaultException>(async () => await Read(tampered, "c1"));
    }

    [Fact]
    public void ModifiedKeyBlock_LooksLikeAWrongPassword()
    {
        using var vault = NewVault();
        var node = JsonNode.Parse(vault.Serialize())!;
        var blob = Convert.FromBase64String(node["wrappedKey"]!.GetValue<string>());
        blob[^1] ^= 0x01;
        node["wrappedKey"] = Convert.ToBase64String(blob);
        var json = node.ToJsonString();

        Assert.Throws<InvalidMasterPasswordException>(
            () => CredentialVault.Open(json, Master, new FastKeyDerivation()));
    }

    [Fact]
    public void Open_RejectsAbsurdKeyDerivationCost()
    {
        using var vault = NewVault();
        var node = JsonNode.Parse(vault.Serialize())!;
        node["kdf"]!["memoryKiB"] = 2_000_000_000;
        var json = node.ToJsonString();

        Assert.Throws<VaultFormatException>(() => CredentialVault.Open(json, Master, new FastKeyDerivation()));
    }

    [Fact]
    public void Open_RejectsUnknownVersion()
    {
        using var vault = NewVault();
        var node = JsonNode.Parse(vault.Serialize())!;
        node["version"] = 99;
        var json = node.ToJsonString();

        Assert.Throws<VaultFormatException>(() => CredentialVault.Open(json, Master, new FastKeyDerivation()));
    }

    [Fact]
    public void Open_RejectsAVaultMadeWithADifferentKeyDerivation()
    {
        using var vault = NewVault();
        var json = vault.Serialize();

        Assert.Throws<VaultFormatException>(() => CredentialVault.Open(json, Master));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{ "version": 1 }""")]
    public void Open_RejectsMalformedFiles(string json)
    {
        Assert.Throws<VaultFormatException>(() => CredentialVault.Open(json, Master, new FastKeyDerivation()));
    }

    [Fact]
    public async Task UnknownCredential_Throws()
    {
        using var vault = NewVault();

        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await Read(vault, "missing"));
    }

    [Fact]
    public void Set_WithABlankId_Throws()
    {
        using var vault = NewVault();

        Assert.Throws<ArgumentException>(() => vault.Set("", "u", "p"));
        Assert.Throws<ArgumentException>(() => vault.Set("   ", "u", "p"));
    }

    [Fact]
    public void Remove_And_Contains_Work()
    {
        using var vault = NewVault();
        vault.Set("c1", "admin", "secret");

        Assert.True(vault.Contains("c1"));
        Assert.True(vault.Remove("c1"));
        Assert.False(vault.Contains("c1"));
        Assert.False(vault.Remove("c1"));
    }

    [Fact]
    public async Task Credential_CannotBeUsedAfterTheCallbackReturns()
    {
        using var vault = NewVault();
        vault.Set("c1", "admin", "secret");
        ICredential? captured = null;

        await vault.UseAsync<object?>("c1", credential =>
        {
            captured = credential;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.Throws<ObjectDisposedException>(() => captured!.ReadPassword(_ => { }));
    }

    [Fact]
    public void Changed_IsRaisedForEveryModification()
    {
        using var vault = NewVault();
        var changes = 0;
        vault.Changed += (_, _) => changes++;

        vault.Set("c1", "admin", "secret");
        vault.Remove("c1");
        vault.Remove("c1");
        vault.ChangeMasterPassword(Master, "another password");

        Assert.Equal(3, changes);
    }

    [Fact]
    public void Create_RejectsAnEmptyPassword_AndBadSettings()
    {
        Assert.Throws<ArgumentException>(() => CredentialVault.Create("", Cheap, new FastKeyDerivation()));
        Assert.Throws<ArgumentException>(
            () => CredentialVault.Create(Master, new KdfParameters(1, 1, 1), new FastKeyDerivation()));
    }

    [Fact]
    public void TwoVaultsWithTheSamePassword_HaveDifferentSaltsAndKeyBlocks()
    {
        using var a = NewVault();
        using var b = NewVault();
        var nodeA = JsonNode.Parse(a.Serialize())!;
        var nodeB = JsonNode.Parse(b.Serialize())!;

        Assert.NotEqual(nodeA["kdf"]!["salt"]!.GetValue<string>(), nodeB["kdf"]!["salt"]!.GetValue<string>());
        Assert.NotEqual(nodeA["wrappedKey"]!.GetValue<string>(), nodeB["wrappedKey"]!.GetValue<string>());
    }
}
