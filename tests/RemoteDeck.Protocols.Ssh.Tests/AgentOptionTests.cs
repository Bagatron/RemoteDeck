using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Ssh.Tests;

public class AgentOptionTests
{
    private static ConnectionDefinition Definition(bool useAgent, string? credentialId = null, string? jump = null)
    {
        var options = new Dictionary<string, string> { ["username"] = "ops" };
        if (useAgent)
        {
            options["useAgent"] = "true";
        }

        if (jump is not null)
        {
            options["proxyJump"] = jump;
        }

        return new ConnectionDefinition("web", "Web", "ssh", "10.0.0.5", null, credentialId, options);
    }

    private static SshConnection Create(ConnectionDefinition definition, FakeSessionFactory sessions, Func<string, ConnectionDefinition?>? resolve = null) =>
        new(definition, new FakeBroker(), sessions, new HostKeyVerifier(new MemoryHostKeyStore(), null), resolve);

    [Fact]
    public void TheOptionIsRead()
    {
        Assert.True(SshOptions.From(Definition(useAgent: true)).UseAgent);
        Assert.False(SshOptions.From(Definition(useAgent: false)).UseAgent);
    }

    [Fact]
    public async Task WithTheAgent_NoPasswordOrKeyFileIsNeeded()
    {
        var sessions = new FakeSessionFactory();
        await using var connection = Create(Definition(useAgent: true), sessions);

        await connection.ConnectAsync();

        Assert.True(sessions.Request!.UseAgent);
        Assert.Equal("ops", sessions.Request.Username);
        Assert.Null(sessions.Request.Secret);
        Assert.Null(sessions.Request.PrivateKeyPath);
    }

    [Fact]
    public async Task WithoutTheAgent_AMissingSecretIsStillRefused()
    {
        var sessions = new FakeSessionFactory();
        await using var connection = Create(Definition(useAgent: false), sessions);

        var error = await Assert.ThrowsAsync<SshConnectionException>(() => connection.ConnectAsync().AsTask());

        Assert.Contains("agent", error.Message);
        Assert.Equal(0, sessions.ConnectCount);
    }

    [Fact]
    public async Task AJumpHost_CanUseTheAgentToo()
    {
        var sessions = new FakeSessionFactory();
        var bastion = new ConnectionDefinition(
            "bastion", "Bastion", "ssh", "bastion.example.com", null, null,
            new Dictionary<string, string> { ["username"] = "jump", ["useAgent"] = "true" });
        await using var connection = Create(
            Definition(useAgent: true, jump: "bastion"),
            sessions,
            id => id == "bastion" ? bastion : null);

        await connection.ConnectAsync();

        Assert.True(sessions.Request!.Jump!.UseAgent);
        Assert.Equal("jump", sessions.Request.Jump.Username);
    }
}
