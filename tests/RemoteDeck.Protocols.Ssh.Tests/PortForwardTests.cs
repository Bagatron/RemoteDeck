using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Ssh.Tests;

public class PortForwardTests
{
    [Fact]
    public void ParsesAllThreeKinds()
    {
        var list = PortForward.ParseList("L:8080:db.internal:5432\nR:9000:localhost:3000;D:1080");

        Assert.Equal(3, list.Count);
        Assert.Equal(new PortForward(PortForwardKind.Local, 8080, "db.internal", 5432), list[0]);
        Assert.Equal(new PortForward(PortForwardKind.Remote, 9000, "localhost", 3000), list[1]);
        Assert.Equal(new PortForward(PortForwardKind.Dynamic, 1080, null, null), list[2]);
    }

    [Fact]
    public void NothingOrBlank_IsEmpty()
    {
        Assert.Empty(PortForward.ParseList(null));
        Assert.Empty(PortForward.ParseList("  \n ; "));
    }

    [Theory]
    [InlineData("X:1:h:2")]
    [InlineData("L:8080:db")]
    [InlineData("L:0:h:80")]
    [InlineData("L:8080:h:70000")]
    [InlineData("L:abc:h:80")]
    [InlineData("L:8080::80")]
    [InlineData("D:1080:extra")]
    [InlineData("D")]
    [InlineData("hello")]
    public void BadSpecs_AreRejectedWithAHelpfulMessage(string spec)
    {
        var error = Assert.Throws<SshConnectionException>(() => PortForward.ParseList(spec));

        Assert.Contains("not a valid port forward", error.Message);
    }

    [Fact]
    public void TwoForwardsOnTheSameLocalPort_AreRejected()
    {
        var error = Assert.Throws<SshConnectionException>(() => PortForward.ParseList("L:8080:a:1\nD:8080"));

        Assert.Contains("8080", error.Message);
    }

    [Fact]
    public void ARemoteForward_MayShareANumberWithALocalOne()
    {
        Assert.Equal(2, PortForward.ParseList("L:8080:a:1\nR:8080:b:2").Count);
    }

    [Fact]
    public void ToString_RoundTrips()
    {
        foreach (var text in new[] { "L:1:h:2", "R:3:h:4", "D:5" })
        {
            Assert.Equal(text, PortForward.Parse(text).ToString());
        }
    }

    [Fact]
    public async Task Connecting_PassesTheForwardsToTheSession()
    {
        var sessions = new FakeSessionFactory();
        var broker = new FakeBroker();
        broker.ByConnection["c"] = ("u", "p");
        var definition = new ConnectionDefinition(
            "c", "C", "ssh", "10.0.0.5", null, "cred",
            new Dictionary<string, string> { ["forwards"] = "L:8080:db:5432\nD:1080" });
        await using var connection = new SshConnection(definition, broker, sessions, new HostKeyVerifier(new MemoryHostKeyStore(), null));

        await connection.ConnectAsync();

        Assert.Equal(2, sessions.Request!.Forwards.Count);
        Assert.Equal(PortForwardKind.Dynamic, sessions.Request.Forwards[1].Kind);
    }

    [Fact]
    public async Task ABadForwardsOption_FailsBeforeConnecting()
    {
        var sessions = new FakeSessionFactory();
        var broker = new FakeBroker();
        broker.ByConnection["c"] = ("u", "p");
        var definition = new ConnectionDefinition(
            "c", "C", "ssh", "10.0.0.5", null, "cred",
            new Dictionary<string, string> { ["forwards"] = "L:oops" });
        await using var connection = new SshConnection(definition, broker, sessions, new HostKeyVerifier(new MemoryHostKeyStore(), null));

        await Assert.ThrowsAsync<SshConnectionException>(() => connection.ConnectAsync().AsTask());

        Assert.Equal(0, sessions.ConnectCount);
    }
}
