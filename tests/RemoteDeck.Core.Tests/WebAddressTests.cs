using RemoteDeck.Core.Connections;

namespace RemoteDeck.Core.Tests;

public class WebAddressTests
{
    private static string Ok(string text, int? port = null)
    {
        Assert.True(WebAddress.TryNormalize(text, port, out var uri, out var error), error);
        return uri.AbsoluteUri;
    }

    [Theory]
    [InlineData("pve.lan", null, "https://pve.lan/")]
    [InlineData("  pve.lan  ", null, "https://pve.lan/")]
    [InlineData("pve.lan", 8006, "https://pve.lan:8006/")]
    [InlineData("http://router", null, "http://router/")]
    [InlineData("http://router", 8080, "http://router:8080/")]
    [InlineData("https://grafana.lan/d/abc?x=1", null, "https://grafana.lan/d/abc?x=1")]
    [InlineData("https://grafana.lan:3000/d/abc", 9999, "https://grafana.lan:3000/d/abc")]
    [InlineData("https://a.lan:443", 9999, "https://a.lan/")]
    [InlineData("10.0.0.5", 8443, "https://10.0.0.5:8443/")]
    public void BuildsTheAddress(string text, int? port, string expected)
    {
        Assert.Equal(expected, Ok(text, port));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingIsRefused(string? text)
    {
        Assert.False(WebAddress.TryNormalize(text, null, out _, out var error));
        Assert.Contains("no address", error);
    }

    [Theory]
    [InlineData("ftp://files.lan")]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("javascript:alert(1)")]
    public void OnlyHttpAndHttpsAreAllowed(string text)
    {
        Assert.False(WebAddress.TryNormalize(text, null, out _, out _));
    }

    [Fact]
    public void ABadPort_IsRefused()
    {
        Assert.False(WebAddress.TryNormalize("pve.lan", 70000, out _, out var error));
        Assert.Contains("out of range", error);
    }
}
