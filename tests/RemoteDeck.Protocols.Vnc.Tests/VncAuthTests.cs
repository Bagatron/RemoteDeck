namespace RemoteDeck.Protocols.Vnc.Tests;

public sealed class VncAuthTests
{
    [Fact]
    public void Des_MatchesTheStandardTestVector()
    {
        // The worked example that accompanies the DES specification.
        var key = Convert.FromHexString("133457799BBCDFF1");
        var block = Convert.FromHexString("0123456789ABCDEF");

        Assert.Equal("85E813540F0AB405", Convert.ToHexString(Des.EncryptBlock(key, block)));
    }

    [Theory]
    [InlineData("password", "b866924125c8eebb9debc1db61c538e2")]
    [InlineData("secret12longer", "adcd997f8e16fee575e973f93c2b62b4")]
    [InlineData("", "491e890de9ace932838a49792f2213f3")]
    [InlineData("a", "449407fb6f71bd517aca76866568a4e2")]
    public void Response_MatchesAnIndependentImplementation(string password, string expected)
    {
        var challenge = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

        Assert.Equal(expected, Convert.ToHexString(VncAuth.Response(challenge, password)).ToLowerInvariant());
    }

    [Fact]
    public void Response_OnlyUsesTheFirstEightCharacters()
    {
        var challenge = Enumerable.Range(0, 16).Select(i => (byte)(i * 3)).ToArray();

        Assert.Equal(VncAuth.Response(challenge, "12345678"), VncAuth.Response(challenge, "12345678and more"));
    }

    [Fact]
    public void Response_NeedsASixteenByteChallenge()
    {
        Assert.Throws<ArgumentException>(() => VncAuth.Response(new byte[8], "x"));
    }
}

public sealed class KeySymTests
{
    [Theory]
    [InlineData('a', 0x61u)]
    [InlineData(' ', 0x20u)]
    [InlineData('~', 0x7Eu)]
    [InlineData('é', 0xE9u)]
    public void FromCodePoint_UsesTheCharacterCodeForLatin1(char c, uint expected)
    {
        Assert.Equal(expected, KeySyms.FromCodePoint(c));
    }

    [Fact]
    public void FromCodePoint_OffsetsOtherCharacters()
    {
        Assert.Equal(0x010020ACu, KeySyms.FromCodePoint(0x20AC)); // the euro sign
    }

    [Fact]
    public void Function_CountsFromF1()
    {
        Assert.Equal(0xFFBEu, KeySyms.Function(1));
        Assert.Equal(0xFFC9u, KeySyms.Function(12));
    }
}
