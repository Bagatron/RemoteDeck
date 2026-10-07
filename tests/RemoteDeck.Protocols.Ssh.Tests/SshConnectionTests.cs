using System.Text;
using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Ssh.Tests;

public class SshConnectionTests
{
    private static readonly HostKeyInfo SomeKey = new("10.0.0.5", 22, "ssh-ed25519", "SHA256:abc");

    private sealed class Setup
    {
        public FakeSessionFactory Sessions { get; } = new();

        public FakeBroker Broker { get; } = new();

        public MemoryHostKeyStore Store { get; } = new();

        public ScriptedPrompt? Prompt { get; init; }

        public SshConnection Create(ConnectionDefinition definition) =>
            new(definition, Broker, Sessions, new HostKeyVerifier(Store, Prompt));
    }

    private static ConnectionDefinition Definition(
        string? credentialId = "cred-1",
        int? port = null,
        Dictionary<string, string>? options = null) =>
        new("web-01", "Web 01", "ssh", "10.0.0.5", port, credentialId, options);

    private static Setup WithCredential(string? username = "admin", string password = "hunter2", ScriptedPrompt? prompt = null)
    {
        var setup = new Setup { Prompt = prompt };
        setup.Broker.ByConnection["web-01"] = (username, password);
        return setup;
    }

    // ---- connecting ----

    [Fact]
    public async Task Connect_UsesTheCredential_AndOpensAShell()
    {
        var setup = WithCredential();
        await using var connection = setup.Create(Definition(port: 2222));

        await connection.ConnectAsync();

        var request = setup.Sessions.Request!;
        Assert.Equal("10.0.0.5", request.Host);
        Assert.Equal(2222, request.Port);
        Assert.Equal("admin", request.Username);
        Assert.Equal(Encoding.UTF8.GetBytes("hunter2"), setup.Sessions.SecretAtConnectTime);
        Assert.Null(request.PrivateKeyPath);
        Assert.Equal("xterm-256color", setup.Sessions.Session.Terminal);
        Assert.Equal(1, setup.Sessions.Session.Shell.StartCount);
        Assert.Equal(ConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task Connect_DefaultsToPort22_AndSensibleTimeouts()
    {
        var setup = WithCredential();
        await using var connection = setup.Create(Definition());

        await connection.ConnectAsync();

        Assert.Equal(22, setup.Sessions.Request!.Port);
        Assert.Equal(TimeSpan.FromSeconds(15), setup.Sessions.Request.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), setup.Sessions.Request.KeepAlive);
    }

    [Fact]
    public async Task Connect_WipesThePasswordAfterwards()
    {
        var setup = WithCredential();
        await using var connection = setup.Create(Definition());

        await connection.ConnectAsync();

        Assert.NotNull(setup.Sessions.Request!.Secret);
        Assert.All(setup.Sessions.Request.Secret!, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Connect_WipesThePasswordEvenWhenItFails()
    {
        var setup = WithCredential();
        setup.Sessions.Failure = new IOException("boom");
        await using var connection = setup.Create(Definition());

        await Assert.ThrowsAsync<SshConnectionException>(async () => await connection.ConnectAsync());

        Assert.All(setup.Sessions.Request!.Secret!, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Connect_ReportsStateChanges()
    {
        var setup = WithCredential();
        await using var connection = setup.Create(Definition());
        var states = new List<ConnectionState>();
        connection.StateChanged += (_, state) => states.Add(state);

        await connection.ConnectAsync();
        await connection.DisconnectAsync();

        Assert.Equal(
            new[] { ConnectionState.Connecting, ConnectionState.Connected, ConnectionState.Disconnected },
            states);
    }

    [Fact]
    public async Task Connect_WithAPrivateKey_UsesThePasswordAsThePassphrase()
    {
        var setup = WithCredential("deploy", "key-passphrase");
        var options = new Dictionary<string, string> { ["privateKeyPath"] = @"C:\keys\id_ed25519", ["term"] = "vt100" };
        await using var connection = setup.Create(Definition(options: options));

        await connection.ConnectAsync();

        var request = setup.Sessions.Request!;
        Assert.Equal("deploy", request.Username);
        Assert.Equal(@"C:\keys\id_ed25519", request.PrivateKeyPath);
        Assert.Equal(Encoding.UTF8.GetBytes("key-passphrase"), setup.Sessions.SecretAtConnectTime);
        Assert.Equal("vt100", setup.Sessions.Session.Terminal);
    }

    [Fact]
    public async Task Connect_WithAKeyAndNoCredential_UsesTheUsernameOption()
    {
        var setup = new Setup();
        var options = new Dictionary<string, string> { ["username"] = "deploy", ["privateKeyPath"] = "key.pem" };
        await using var connection = setup.Create(Definition(credentialId: null, options: options));

        await connection.ConnectAsync();

        Assert.Equal("deploy", setup.Sessions.Request!.Username);
        Assert.Null(setup.Sessions.Request.Secret);
        Assert.Equal("key.pem", setup.Sessions.Request.PrivateKeyPath);
    }

    [Fact]
    public async Task Connect_TheCredentialUsernameWinsOverTheOption()
    {
        var setup = WithCredential("from-credential", "pw");
        var options = new Dictionary<string, string> { ["username"] = "from-option" };
        await using var connection = setup.Create(Definition(options: options));

        await connection.ConnectAsync();

        Assert.Equal("from-credential", setup.Sessions.Request!.Username);
    }

    [Fact]
    public async Task Connect_WithNoCredentialAndNoKey_Fails()
    {
        var setup = new Setup();
        var options = new Dictionary<string, string> { ["username"] = "deploy" };
        await using var connection = setup.Create(Definition(credentialId: null, options: options));

        var error = await Assert.ThrowsAsync<SshConnectionException>(async () => await connection.ConnectAsync());

        Assert.Contains("password or private key", error.Message);
        Assert.Equal(ConnectionState.Failed, connection.State);
        Assert.Equal(0, setup.Sessions.ConnectCount);
    }

    [Fact]
    public async Task Connect_WithNoUsername_Fails()
    {
        var setup = WithCredential(username: null);
        await using var connection = setup.Create(Definition());

        var error = await Assert.ThrowsAsync<SshConnectionException>(async () => await connection.ConnectAsync());

        Assert.Contains("username", error.Message);
        Assert.Equal(0, setup.Sessions.ConnectCount);
    }

    [Fact]
    public async Task Connect_FailureIsReported_AndRetryingWorks()
    {
        var setup = WithCredential();
        setup.Sessions.Failure = new IOException("network down");
        await using var connection = setup.Create(Definition());

        var error = await Assert.ThrowsAsync<SshConnectionException>(async () => await connection.ConnectAsync());

        Assert.Contains("10.0.0.5", error.Message);
        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal(ConnectionState.Failed, connection.State);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await connection.WriteAsync(new byte[] { 1 }));

        setup.Sessions.Failure = null;
        await connection.ConnectAsync();

        Assert.Equal(ConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task Connect_WhenAlreadyConnected_DoesNothing()
    {
        var setup = WithCredential();
        await using var connection = setup.Create(Definition());

        await connection.ConnectAsync();
        await connection.ConnectAsync();

        Assert.Equal(1, setup.Sessions.ConnectCount);
    }

    [Fact]
    public async Task Connect_WhenCancelled_EndsDisconnected()
    {
        var setup = WithCredential();
        await using var connection = setup.Create(Definition());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await connection.ConnectAsync(cts.Token));

        Assert.Equal(ConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task Connect_WithBadOptions_FailsWithAReadableMessage()
    {
        var setup = WithCredential();
        var options = new Dictionary<string, string> { ["keepAliveSeconds"] = "soon" };
        await using var connection = setup.Create(Definition(options: options));

        var error = await Assert.ThrowsAsync<SshConnectionException>(async () => await connection.ConnectAsync());

        Assert.Contains("keepAliveSeconds", error.Message);
    }

    // ---- host keys ----

    [Fact]
    public async Task UnknownHostKey_IsRememberedWhenTheUserAccepts()
    {
        var prompt = new ScriptedPrompt(true);
        var setup = WithCredential(prompt: prompt);
        setup.Sessions.OnConnect = request => Assert.True(request.VerifyHostKey(SomeKey));
        await using var connection = setup.Create(Definition());

        await connection.ConnectAsync();

        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Equal(HostKeyVerdict.NewHost, Assert.Single(prompt.Asked).Verdict);
        Assert.Equal(SomeKey, Assert.Single(setup.Store.Keys));
    }

    [Fact]
    public async Task RejectedHostKey_StopsTheConnection_WithAClearMessage()
    {
        var prompt = new ScriptedPrompt(false);
        var setup = WithCredential(prompt: prompt);
        setup.Sessions.OnConnect = request =>
        {
            Assert.False(request.VerifyHostKey(SomeKey));

            // What the real library does when the callback refuses the key.
            throw new IOException("key exchange failed");
        };
        await using var connection = setup.Create(Definition());

        var error = await Assert.ThrowsAsync<SshConnectionException>(async () => await connection.ConnectAsync());

        Assert.Contains("host key", error.Message);
        Assert.Empty(setup.Store.Keys);
        Assert.Equal(ConnectionState.Failed, connection.State);
    }

    [Fact]
    public async Task HostKey_WithNoPromptAvailable_IsRefused()
    {
        var setup = WithCredential(prompt: null);
        setup.Sessions.OnConnect = request =>
        {
            Assert.False(request.VerifyHostKey(SomeKey));
            throw new IOException("key exchange failed");
        };
        await using var connection = setup.Create(Definition());

        var error = await Assert.ThrowsAsync<SshConnectionException>(async () => await connection.ConnectAsync());

        Assert.Contains("host key", error.Message);
    }

    [Fact]
    public async Task KnownHostKey_ConnectsWithoutAsking()
    {
        var prompt = new ScriptedPrompt(false);
        var setup = WithCredential(prompt: prompt);
        setup.Store.Save(SomeKey);
        setup.Sessions.OnConnect = request => Assert.True(request.VerifyHostKey(SomeKey));
        await using var connection = setup.Create(Definition());

        await connection.ConnectAsync();

        Assert.Empty(prompt.Asked);
    }

    // ---- using the session ----

    [Fact]
    public async Task Output_IsPublishedToSubscribers()
    {
        var setup = WithCredential();
        await using var connection = setup.Create(Definition());
        var collector = new Collector();
        using var subscription = connection.Output.Subscribe(collector);

        await connection.ConnectAsync();
        setup.Sessions.Session.Shell.Receive("Welcome\r\n");
        setup.Sessions.Session.Shell.Receive("$ ");

        Assert.Equal(new[] { "Welcome\r\n", "$ " }, collector.Chunks);
    }

    [Fact]
    public async Task Write_SendsKeystrokesToTheShell()
    {
        var setup = WithCredential();
        await using var connection = setup.Create(Definition());
        await connection.ConnectAsync();

        await connection.WriteAsync(Encoding.UTF8.GetBytes("ls\r"));

        Assert.Equal(new[] { "ls\r" }, setup.Sessions.Session.Shell.Written.Select(Encoding.UTF8.GetString));
    }

    [Fact]
    public async Task Write_BeforeConnecting_Throws()
    {
        var setup = WithCredential();
        await using var connection = setup.Create(Definition());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await connection.WriteAsync(new byte[] { 1 }));
    }

    [Fact]
    public async Task Resize_BeforeConnecting_SetsTheStartingSize_AndLaterResizesAreForwarded()
    {
        var setup = WithCredential();
        await using var connection = setup.Create(Definition());

        connection.Resize(120, 40);
        await connection.ConnectAsync();

        Assert.Equal((120, 40), (setup.Sessions.Session.Columns, setup.Sessions.Session.Rows));

        connection.Resize(100, 30);

        Assert.Equal((100, 30), setup.Sessions.Session.Shell.LastResize);
    }

    [Fact]
    public async Task Resize_RejectsNonPositiveSizes()
    {
        var setup = WithCredential();
        await using var connection = setup.Create(Definition());

        Assert.Throws<ArgumentOutOfRangeException>(() => connection.Resize(0, 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => connection.Resize(80, -1));
    }

    [Fact]
    public async Task Disconnect_ReleasesTheShellAndSession_AndIsRepeatable()
    {
        var setup = WithCredential();
        await using var connection = setup.Create(Definition());
        await connection.ConnectAsync();
        var session = setup.Sessions.Session;

        await connection.DisconnectAsync();
        await connection.DisconnectAsync();

        Assert.True(session.Shell.Disposed);
        Assert.True(session.Disposed);
        Assert.Equal(ConnectionState.Disconnected, connection.State);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await connection.WriteAsync(new byte[] { 1 }));
    }

    [Fact]
    public async Task RemoteClose_MovesToDisconnected_AndReleasesEverything()
    {
        var setup = WithCredential();
        await using var connection = setup.Create(Definition());
        await connection.ConnectAsync();
        var session = setup.Sessions.Session;

        session.Shell.RemoteClose();
        session.Shell.RemoteClose();

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.True(session.Shell.Disposed);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task Reconnecting_AfterADisconnect_OpensANewSession()
    {
        var setup = WithCredential();
        await using var connection = setup.Create(Definition());
        await connection.ConnectAsync();
        await connection.DisconnectAsync();
        setup.Sessions.NextSession();

        await connection.ConnectAsync();

        Assert.Equal(2, setup.Sessions.ConnectCount);
        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Equal(1, setup.Sessions.Session.Shell.StartCount);
    }

    [Fact]
    public async Task DisposingTheConnection_CompletesTheOutput()
    {
        var setup = WithCredential();
        var connection = setup.Create(Definition());
        var collector = new Collector();
        using var subscription = connection.Output.Subscribe(collector);
        await connection.ConnectAsync();

        await connection.DisposeAsync();

        Assert.True(collector.Completed);
        Assert.Equal(ConnectionState.Disconnected, connection.State);
    }
}
