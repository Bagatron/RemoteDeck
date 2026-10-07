using System.Text;

namespace RemoteDeck.Vault.Tests;

/// <summary>Runs the real Argon2id with small settings, so these stay quick.</summary>
public class Argon2Tests
{
    private static readonly KdfParameters Small = new(1024, 1, 1);

    private static byte[] Salt(byte first)
    {
        var salt = new byte[16];
        salt[0] = first;
        return salt;
    }

    [Fact]
    public void Argon2id_IsDeterministic_AndDependsOnPasswordAndSalt()
    {
        var kdf = new Argon2idKeyDerivation();
        var password = Encoding.UTF8.GetBytes("password");
        var other = Encoding.UTF8.GetBytes("Password");

        var a = kdf.DeriveKey(password, Salt(1), Small);
        var b = kdf.DeriveKey(password, Salt(1), Small);
        var differentSalt = kdf.DeriveKey(password, Salt(2), Small);
        var differentPassword = kdf.DeriveKey(other, Salt(1), Small);

        Assert.Equal(32, a.Length);
        Assert.Equal(a, b);
        Assert.NotEqual(a, differentSalt);
        Assert.NotEqual(a, differentPassword);
    }

    [Fact]
    public void Argon2id_DependsOnTheCostSettings()
    {
        var kdf = new Argon2idKeyDerivation();
        var password = Encoding.UTF8.GetBytes("password");

        var a = kdf.DeriveKey(password, Salt(1), new KdfParameters(1024, 1, 1));
        var b = kdf.DeriveKey(password, Salt(1), new KdfParameters(2048, 1, 1));
        var c = kdf.DeriveKey(password, Salt(1), new KdfParameters(1024, 2, 1));

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public async Task FullVault_RoundTrips_WithTheRealKeyDerivation()
    {
        using var vault = CredentialVault.Create("master password", Small);
        vault.Set("c1", "admin", "secret");

        using var reopened = CredentialVault.Open(vault.Serialize(), "master password");

        string? password = null;
        await reopened.UseAsync<object?>("c1", credential =>
        {
            credential.ReadPassword(span => password = span.ToString());
            return ValueTask.FromResult<object?>(null);
        });
        Assert.Equal("secret", password);
        Assert.Throws<InvalidMasterPasswordException>(() => CredentialVault.Open(vault.Serialize(), "wrong"));
    }

    [Fact]
    public void DefaultSettings_AreValid_AndMatchTheDocumentedCost()
    {
        Assert.Null(KdfParameters.Default.Problem());
        Assert.Equal(65536, KdfParameters.Default.MemoryKiB);
        Assert.Equal(3, KdfParameters.Default.Iterations);
        Assert.Equal(4, KdfParameters.Default.Parallelism);
    }

    [Theory]
    [InlineData(1024, 1, 0)]
    [InlineData(1024, 1, 17)]
    [InlineData(1024, 0, 1)]
    [InlineData(1024, 65, 1)]
    [InlineData(7, 1, 1)]
    [InlineData(2_000_000, 1, 1)]
    public void BadSettings_AreReported(int memory, int iterations, int parallelism)
    {
        Assert.NotNull(new KdfParameters(memory, iterations, parallelism).Problem());
    }
}
