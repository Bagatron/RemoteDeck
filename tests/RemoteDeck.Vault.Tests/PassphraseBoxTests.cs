using System.Text;

namespace RemoteDeck.Vault.Tests;

public class PassphraseBoxTests
{
    private static readonly byte[] Data = Encoding.UTF8.GetBytes("a backup full of secrets");
    private static readonly FastKeyDerivation Fast = new();

    [Fact]
    public void RoundTrip()
    {
        var sealedData = PassphraseBox.Seal("correct horse", Data, kdf: Fast);

        Assert.True(PassphraseBox.IsSealed(sealedData));
        Assert.Equal(Data, PassphraseBox.Open("correct horse", sealedData, Fast));
    }

    [Fact]
    public void TheCiphertextHidesTheData()
    {
        var sealedData = PassphraseBox.Seal("correct horse", Data, kdf: Fast);

        Assert.DoesNotContain("secrets", Encoding.UTF8.GetString(sealedData));
    }

    [Fact]
    public void AWrongPassphraseIsRefused()
    {
        var sealedData = PassphraseBox.Seal("correct horse", Data, kdf: Fast);

        Assert.Throws<InvalidPassphraseException>(() => PassphraseBox.Open("battery staple", sealedData, Fast));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(40)]
    [InlineData(60)]
    public void AnyChangeToTheFileIsNoticed(int index)
    {
        var sealedData = PassphraseBox.Seal("correct horse", Data, kdf: Fast);
        sealedData[index] ^= 0x01;

        Assert.ThrowsAny<VaultException>(() => PassphraseBox.Open("correct horse", sealedData, Fast));
    }

    [Fact]
    public void TwoSealsOfTheSameDataDiffer()
    {
        Assert.NotEqual(
            PassphraseBox.Seal("correct horse", Data, kdf: Fast),
            PassphraseBox.Seal("correct horse", Data, kdf: Fast));
    }

    [Fact]
    public void ThingsThatAreNotSealedAreRecognised()
    {
        Assert.False(PassphraseBox.IsSealed(Encoding.UTF8.GetBytes("PK zip bytes")));
        Assert.False(PassphraseBox.IsSealed(Array.Empty<byte>()));
        Assert.Throws<VaultFormatException>(() => PassphraseBox.Open("x", Encoding.UTF8.GetBytes("not sealed at all, just text"), Fast));
    }

    [Fact]
    public void AnEmptyPassphraseIsRefused()
    {
        Assert.Throws<ArgumentException>(() => PassphraseBox.Seal(string.Empty, Data, kdf: Fast));
    }

    [Fact]
    public void WorksWithTheRealArgon2id()
    {
        var cheap = new KdfParameters(MemoryKiB: 256, Iterations: 1, Parallelism: 1);
        var sealedData = PassphraseBox.Seal("correct horse", Data, cheap);

        Assert.Equal(Data, PassphraseBox.Open("correct horse", sealedData));
    }
}
