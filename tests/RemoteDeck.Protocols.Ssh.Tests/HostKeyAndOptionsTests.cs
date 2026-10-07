using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Ssh.Tests;

public class HostKeyVerifierTests
{
    private static HostKeyInfo Key(
        string host = "web-01",
        int port = 22,
        string algorithm = "ssh-ed25519",
        string fingerprint = "SHA256:aaa") => new(host, port, algorithm, fingerprint);

    [Fact]
    public async Task RememberedKey_IsTrustedWithoutAsking()
    {
        var store = new MemoryHostKeyStore();
        store.Save(Key());
        var prompt = new ScriptedPrompt(false);

        var trusted = await new HostKeyVerifier(store, prompt).VerifyAsync(Key());

        Assert.True(trusted);
        Assert.Empty(prompt.Asked);
    }

    [Fact]
    public async Task NewHost_AcceptedByTheUser_IsTrustedAndRemembered()
    {
        var store = new MemoryHostKeyStore();
        var prompt = new ScriptedPrompt(true);

        var trusted = await new HostKeyVerifier(store, prompt).VerifyAsync(Key());

        Assert.True(trusted);
        Assert.Equal(HostKeyVerdict.NewHost, Assert.Single(prompt.Asked).Verdict);
        Assert.Equal(Key(), Assert.Single(store.Keys));
    }

    [Fact]
    public async Task NewHost_DeclinedByTheUser_IsRefusedAndNotRemembered()
    {
        var store = new MemoryHostKeyStore();

        var trusted = await new HostKeyVerifier(store, new ScriptedPrompt(false)).VerifyAsync(Key());

        Assert.False(trusted);
        Assert.Empty(store.Keys);
    }

    [Fact]
    public async Task ChangedKey_IsFlagged_AndOnlyReplacedWhenTheUserAccepts()
    {
        var store = new MemoryHostKeyStore();
        store.Save(Key(fingerprint: "SHA256:old"));
        var declined = new ScriptedPrompt(false);

        var refused = await new HostKeyVerifier(store, declined).VerifyAsync(Key(fingerprint: "SHA256:new"));

        Assert.False(refused);
        Assert.Equal(HostKeyVerdict.Changed, Assert.Single(declined.Asked).Verdict);
        Assert.Equal("SHA256:old", Assert.Single(store.Keys).Fingerprint);

        var accepted = new ScriptedPrompt(true);

        var trusted = await new HostKeyVerifier(store, accepted).VerifyAsync(Key(fingerprint: "SHA256:new"));

        Assert.True(trusted);
        Assert.Equal("SHA256:new", Assert.Single(store.Keys).Fingerprint);
    }

    [Fact]
    public async Task ADifferentKeyType_ForAKnownHost_IsAskedAboutAsNewKeyType()
    {
        var store = new MemoryHostKeyStore();
        store.Save(Key(algorithm: "ssh-ed25519"));
        var prompt = new ScriptedPrompt(true);

        var trusted = await new HostKeyVerifier(store, prompt).VerifyAsync(Key(algorithm: "rsa-sha2-512", fingerprint: "SHA256:rsa"));

        Assert.True(trusted);
        var asked = Assert.Single(prompt.Asked);
        Assert.Equal(HostKeyVerdict.NewKeyType, asked.Verdict);
        Assert.Equal(1, asked.KnownCount);
        Assert.Equal(2, store.Keys.Count);
    }

    [Fact]
    public async Task WithoutAPrompt_AnythingUnknownOrChangedIsRefused()
    {
        var store = new MemoryHostKeyStore();
        store.Save(Key(fingerprint: "SHA256:old"));
        var verifier = new HostKeyVerifier(store, prompt: null);

        Assert.False(await verifier.VerifyAsync(Key(host: "other")));
        Assert.False(await verifier.VerifyAsync(Key(fingerprint: "SHA256:new")));
        Assert.True(await verifier.VerifyAsync(Key(fingerprint: "SHA256:old")));
    }

    [Fact]
    public async Task APortIsADifferentServer()
    {
        var store = new MemoryHostKeyStore();
        store.Save(Key(port: 22));
        var prompt = new ScriptedPrompt(false);

        var trusted = await new HostKeyVerifier(store, prompt).VerifyAsync(Key(port: 2222));

        Assert.False(trusted);
        Assert.Equal(HostKeyVerdict.NewHost, Assert.Single(prompt.Asked).Verdict);
    }

    [Fact]
    public void Fingerprints_MatchTheOpenSshFormat()
    {
        Assert.Equal("SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU", HostKeyFingerprint.Sha256(Array.Empty<byte>()));
        Assert.Equal("SHA256:A5BYxvLAy0ksUzsKTRTvd8wPeKvMztUofYShogEc+4E", HostKeyFingerprint.Sha256(new byte[] { 1, 2, 3 }));
        Assert.Equal("SHA256:ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0", HostKeyFingerprint.Sha256("abc"u8));
    }
}

public class FileHostKeyStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "remotedeck-hostkeys-tests-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "known_hosts.json");

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
    public void SavedKeys_AreFoundAgain_AfterReopeningTheFile()
    {
        new FileHostKeyStore(FilePath).Save(new HostKeyInfo("web-01", 22, "ssh-ed25519", "SHA256:aaa"));

        var found = new FileHostKeyStore(FilePath).FindAll("web-01", 22);

        var key = Assert.Single(found);
        Assert.Equal("ssh-ed25519", key.Algorithm);
        Assert.Equal("SHA256:aaa", key.Fingerprint);
    }

    [Fact]
    public void HostNames_AreCaseInsensitive_AndPortsAreSeparate()
    {
        var store = new FileHostKeyStore(FilePath);
        store.Save(new HostKeyInfo("Web-01.Example.com", 22, "ssh-ed25519", "SHA256:aaa"));

        Assert.Single(store.FindAll("web-01.example.com", 22));
        Assert.Empty(store.FindAll("web-01.example.com", 2222));
        Assert.Empty(store.FindAll("web-02.example.com", 22));
    }

    [Fact]
    public void SavingTheSameAlgorithmReplaces_AndOtherAlgorithmsAreKept()
    {
        var store = new FileHostKeyStore(FilePath);
        store.Save(new HostKeyInfo("web-01", 22, "ssh-ed25519", "SHA256:old"));
        store.Save(new HostKeyInfo("web-01", 22, "rsa-sha2-512", "SHA256:rsa"));

        store.Save(new HostKeyInfo("web-01", 22, "ssh-ed25519", "SHA256:new"));

        var found = new FileHostKeyStore(FilePath).FindAll("web-01", 22);
        Assert.Equal(2, found.Count);
        Assert.Equal("SHA256:new", found.Single(k => k.Algorithm == "ssh-ed25519").Fingerprint);
        Assert.Equal("SHA256:rsa", found.Single(k => k.Algorithm == "rsa-sha2-512").Fingerprint);
    }

    [Fact]
    public void IPv6Addresses_AreKeptApartFromHostnames()
    {
        var store = new FileHostKeyStore(FilePath);
        store.Save(new HostKeyInfo("::1", 22, "ssh-ed25519", "SHA256:v6"));

        Assert.Single(store.FindAll("::1", 22));
        Assert.Empty(store.FindAll("1", 22));
        Assert.Equal("[::1]:22", FileHostKeyStore.Identify("::1", 22));
    }

    [Fact]
    public void Saving_LeavesNoTemporaryFile()
    {
        var store = new FileHostKeyStore(FilePath);

        store.Save(new HostKeyInfo("web-01", 22, "ssh-ed25519", "SHA256:aaa"));
        store.Save(new HostKeyInfo("web-02", 22, "ssh-ed25519", "SHA256:bbb"));

        Assert.True(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void AMissingFile_MeansNothingIsKnown()
    {
        Assert.Empty(new FileHostKeyStore(FilePath).FindAll("web-01", 22));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "version": 7, "hosts": {} }""")]
    [InlineData("null")]
    public void ADamagedFile_IsReported_NotTreatedAsEmpty(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, content);
        var store = new FileHostKeyStore(FilePath);

        Assert.Throws<InvalidDataException>(() => store.FindAll("web-01", 22));
    }
}

public class SshOptionsTests
{
    private static ConnectionDefinition Definition(
        string host = "10.0.0.5",
        int? port = null,
        Dictionary<string, string>? options = null) =>
        new("c1", "Test", "ssh", host, port, null, options);

    [Fact]
    public void Defaults_AreSensible()
    {
        var options = SshOptions.From(Definition());

        Assert.Equal("10.0.0.5", options.Host);
        Assert.Equal(22, options.Port);
        Assert.Null(options.Username);
        Assert.Null(options.PrivateKeyPath);
        Assert.Equal("xterm-256color", options.Terminal);
        Assert.Equal(TimeSpan.FromSeconds(30), options.KeepAlive);
        Assert.Equal(TimeSpan.FromSeconds(15), options.ConnectTimeout);
    }

    [Fact]
    public void AllOptions_AreRead_AndTrimmed()
    {
        var options = SshOptions.From(Definition(port: 2222, options: new Dictionary<string, string>
        {
            ["username"] = " deploy ",
            ["privateKeyPath"] = " C:\\keys\\id ",
            ["term"] = "vt100",
            ["keepAliveSeconds"] = "0",
            ["connectTimeoutSeconds"] = "60",
        }));

        Assert.Equal(2222, options.Port);
        Assert.Equal("deploy", options.Username);
        Assert.Equal("C:\\keys\\id", options.PrivateKeyPath);
        Assert.Equal("vt100", options.Terminal);
        Assert.Equal(TimeSpan.Zero, options.KeepAlive);
        Assert.Equal(TimeSpan.FromSeconds(60), options.ConnectTimeout);
    }

    [Fact]
    public void BlankOptions_AreIgnored()
    {
        var options = SshOptions.From(Definition(options: new Dictionary<string, string>
        {
            ["username"] = "  ",
            ["term"] = "",
        }));

        Assert.Null(options.Username);
        Assert.Equal("xterm-256color", options.Terminal);
    }

    [Theory]
    [InlineData("keepAliveSeconds", "-1")]
    [InlineData("keepAliveSeconds", "3601")]
    [InlineData("keepAliveSeconds", "often")]
    [InlineData("keepAliveSeconds", "1.5")]
    [InlineData("connectTimeoutSeconds", "0")]
    [InlineData("connectTimeoutSeconds", "301")]
    public void BadNumbers_AreRejected_WithTheOptionNameInTheMessage(string key, string value)
    {
        var definition = Definition(options: new Dictionary<string, string> { [key] = value });

        var error = Assert.Throws<SshConnectionException>(() => SshOptions.From(definition));

        Assert.Contains(key, error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-5)]
    public void BadPorts_AreRejected(int port)
    {
        Assert.Throws<SshConnectionException>(() => SshOptions.From(Definition(port: port)));
    }

    [Fact]
    public void ABlankHost_IsRejected()
    {
        Assert.Throws<SshConnectionException>(() => SshOptions.From(Definition(host: "  ")));
    }
}
