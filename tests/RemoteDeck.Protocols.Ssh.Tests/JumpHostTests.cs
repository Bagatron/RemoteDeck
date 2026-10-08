using System.Text;
using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Ssh.Tests;

public class JumpHostTests
{
    private sealed class Setup
    {
        public FakeSessionFactory Sessions { get; } = new();

        public FakeBroker Broker { get; } = new();

        public Dictionary<string, ConnectionDefinition> Saved { get; } = new();

        public byte[]? JumpSecretAtConnectTime { get; set; }

        public Setup()
        {
            Sessions.OnConnect = request => JumpSecretAtConnectTime = request.Jump?.Secret?.ToArray();
        }

        public SshConnection Create(ConnectionDefinition definition, bool withResolver = true) =>
            new(
                definition,
                Broker,
                Sessions,
                new HostKeyVerifier(new MemoryHostKeyStore(), null),
                withResolver ? id => Saved.GetValueOrDefault(id) : null);

        public ConnectionDefinition Add(string id, string host, string? jump = null, string type = "ssh", string? credentialId = "cred", string? password = null)
        {
            var options = new Dictionary<string, string>();
            if (jump is not null)
            {
                options["proxyJump"] = jump;
            }

            var definition = new ConnectionDefinition(id, id, type, host, null, credentialId, options);
            Saved[id] = definition;
            if (credentialId is not null)
            {
                Broker.ByConnection[id] = (id + "-user", password ?? id + "-pw");
            }

            return definition;
        }
    }

    [Fact]
    public async Task WithoutAJumpHost_TheRequestIsDirect()
    {
        var setup = new Setup();
        await using var connection = setup.Create(setup.Add("web", "10.0.0.5"));

        await connection.ConnectAsync();

        Assert.Null(setup.Sessions.Request!.Jump);
    }

    [Fact]
    public async Task AJumpHost_IsConnectedFirst_WithItsOwnCredential()
    {
        var setup = new Setup();
        setup.Add("bastion", "bastion.example.com");
        await using var connection = setup.Create(setup.Add("web", "10.0.0.5", jump: "bastion"));

        await connection.ConnectAsync();

        var request = setup.Sessions.Request!;
        Assert.Equal("10.0.0.5", request.Host);
        Assert.Equal("web-user", request.Username);
        Assert.NotNull(request.Jump);
        Assert.Equal("bastion.example.com", request.Jump!.Host);
        Assert.Equal("bastion-user", request.Jump.Username);
        Assert.Equal("bastion-pw", Encoding.UTF8.GetString(setup.JumpSecretAtConnectTime!));
        Assert.Null(request.Jump.Jump);
    }

    [Fact]
    public async Task TheJumpHostsPassword_IsWipedAfterConnecting()
    {
        var setup = new Setup();
        setup.Add("bastion", "bastion.example.com");
        await using var connection = setup.Create(setup.Add("web", "10.0.0.5", jump: "bastion"));

        await connection.ConnectAsync();

        Assert.All(setup.Sessions.Request!.Jump!.Secret!, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task JumpHosts_CanBeChained()
    {
        var setup = new Setup();
        setup.Add("edge", "edge.example.com");
        setup.Add("inner", "inner.example.com", jump: "edge");
        await using var connection = setup.Create(setup.Add("web", "10.0.0.5", jump: "inner"));

        await connection.ConnectAsync();

        var jump = setup.Sessions.Request!.Jump!;
        Assert.Equal("inner.example.com", jump.Host);
        Assert.Equal("edge.example.com", jump.Jump!.Host);
        Assert.Null(jump.Jump.Jump);
    }

    [Fact]
    public async Task AJumpHostThatUsesAKeyFile_NeedsNoSavedCredential()
    {
        var setup = new Setup();
        setup.Saved["bastion"] = new ConnectionDefinition(
            "bastion", "bastion", "ssh", "bastion.example.com", null, null,
            new Dictionary<string, string> { ["username"] = "ops", ["privateKeyPath"] = "C:\\keys\\id" });
        await using var connection = setup.Create(setup.Add("web", "10.0.0.5", jump: "bastion"));

        await connection.ConnectAsync();

        var jump = setup.Sessions.Request!.Jump!;
        Assert.Equal("ops", jump.Username);
        Assert.Equal("C:\\keys\\id", jump.PrivateKeyPath);
        Assert.Null(jump.Secret);
    }

    [Fact]
    public async Task JumpHostsThatLoop_AreRefused()
    {
        var setup = new Setup();
        setup.Add("a", "a.example.com", jump: "b");
        setup.Add("b", "b.example.com", jump: "a");
        await using var connection = setup.Create(setup.Saved["a"]);

        var error = await Assert.ThrowsAsync<SshConnectionException>(() => connection.ConnectAsync().AsTask());

        Assert.Contains("loop", error.Message);
        Assert.Equal(0, setup.Sessions.ConnectCount);
    }

    [Fact]
    public async Task AConnectionCannotJumpThroughItself()
    {
        var setup = new Setup();
        await using var connection = setup.Create(setup.Add("web", "10.0.0.5", jump: "web"));

        var error = await Assert.ThrowsAsync<SshConnectionException>(() => connection.ConnectAsync().AsTask());

        Assert.Contains("loop", error.Message);
    }

    [Fact]
    public async Task ADeletedJumpHost_GivesAClearError()
    {
        var setup = new Setup();
        await using var connection = setup.Create(setup.Add("web", "10.0.0.5", jump: "gone"));

        var error = await Assert.ThrowsAsync<SshConnectionException>(() => connection.ConnectAsync().AsTask());

        Assert.Contains("no longer exists", error.Message);
        Assert.Equal(0, setup.Sessions.ConnectCount);
    }

    [Fact]
    public async Task AJumpHostThatIsNotSsh_IsRefused()
    {
        var setup = new Setup();
        setup.Add("desk", "desk.example.com", type: "rdp");
        await using var connection = setup.Create(setup.Add("web", "10.0.0.5", jump: "desk"));

        var error = await Assert.ThrowsAsync<SshConnectionException>(() => connection.ConnectAsync().AsTask());

        Assert.Contains("not an SSH connection", error.Message);
    }

    [Fact]
    public async Task WithoutAResolver_JumpHostsAreRefused()
    {
        var setup = new Setup();
        setup.Add("bastion", "bastion.example.com");
        await using var connection = setup.Create(setup.Add("web", "10.0.0.5", jump: "bastion"), withResolver: false);

        await Assert.ThrowsAsync<SshConnectionException>(() => connection.ConnectAsync().AsTask());
    }

    [Fact]
    public async Task TooManyHops_AreRefused()
    {
        var setup = new Setup();
        setup.Add("h0", "h0.example.com");
        for (var i = 1; i <= JumpHosts.MaxHops + 1; i++)
        {
            setup.Add("h" + i, $"h{i}.example.com", jump: "h" + (i - 1));
        }

        await using var connection = setup.Create(setup.Add("web", "10.0.0.5", jump: "h" + (JumpHosts.MaxHops + 1)));

        var error = await Assert.ThrowsAsync<SshConnectionException>(() => connection.ConnectAsync().AsTask());

        Assert.Contains("Too many", error.Message);
    }

    [Fact]
    public async Task SftpSessions_AlsoGoThroughTheJumpHost()
    {
        var setup = new Setup();
        setup.Add("bastion", "bastion.example.com");
        var target = setup.Add("web", "10.0.0.5", jump: "bastion");
        var factory = new FakeSftpFactory();

        using var session = await SftpSession.ConnectAsync(
            target,
            setup.Broker,
            factory,
            new HostKeyVerifier(new MemoryHostKeyStore(), new ScriptedPrompt(true)),
            CancellationToken.None,
            id => setup.Saved.GetValueOrDefault(id));

        Assert.Equal("bastion.example.com", factory.Request!.Jump!.Host);
        Assert.Equal("web-user", factory.Request.Username);
    }
}
