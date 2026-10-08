using System.Text;
using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Ssh.Tests;

internal sealed class FakeSftpBackend : ISftpBackend
{
    private readonly Dictionary<string, SftpEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _contents = new(StringComparer.Ordinal);

    public FakeSftpBackend()
    {
        _entries["/"] = new SftpEntry("/", "/", true, 0, DateTime.UnixEpoch);
    }

    public List<string> Log { get; } = new();

    public bool Disposed { get; private set; }

    public string WorkingDirectory => "/home/me";

    public void AddDirectory(string path) =>
        _entries[path] = new SftpEntry(path.Split('/').Last(), path, true, 0, DateTime.UnixEpoch);

    public void AddFile(string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _contents[path] = bytes;
        _entries[path] = new SftpEntry(path.Split('/').Last(), path, false, bytes.Length, DateTime.UnixEpoch);
    }

    public string? ReadFile(string path) => _contents.TryGetValue(path, out var b) ? Encoding.UTF8.GetString(b) : null;

    public bool Has(string path) => _entries.ContainsKey(path);

    public IReadOnlyList<SftpEntry> List(string path)
    {
        var prefix = path.TrimEnd('/') + "/";
        var children = _entries.Values
            .Where(e => e.FullPath != path && e.FullPath.StartsWith(prefix, StringComparison.Ordinal)
                        && !e.FullPath[prefix.Length..].Contains('/'))
            .ToList();
        children.Add(new SftpEntry(".", path, true, 0, DateTime.UnixEpoch));
        children.Add(new SftpEntry("..", RemotePath.Parent(path), true, 0, DateTime.UnixEpoch));
        return children;
    }

    public void Download(string path, Stream destination, Action<long> progress)
    {
        var bytes = _contents[path];
        destination.Write(bytes);
        progress(bytes.Length);
    }

    public void Upload(Stream source, string path, Action<long> progress)
    {
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        AddFile(path, Encoding.UTF8.GetString(buffer.ToArray()));
        progress(buffer.Length);
    }

    public void DeleteFile(string path)
    {
        Log.Add("rm " + path);
        _entries.Remove(path);
        _contents.Remove(path);
    }

    public void DeleteDirectory(string path)
    {
        Log.Add("rmdir " + path);
        _entries.Remove(path);
    }

    public void CreateDirectory(string path) => AddDirectory(path);

    public void Rename(string from, string to)
    {
        var entry = _entries[from];
        _entries.Remove(from);
        _entries[to] = entry with { FullPath = to, Name = to.Split('/').Last() };
        if (_contents.Remove(from, out var bytes))
        {
            _contents[to] = bytes;
        }
    }

    public bool Exists(string path) => _entries.ContainsKey(path);

    public void Dispose() => Disposed = true;
}

internal sealed class FakeSftpFactory : ISftpBackendFactory
{
    public FakeSftpBackend Backend { get; } = new();

    public SshConnectRequest? Request { get; private set; }

    public byte[]? SecretAtConnectTime { get; private set; }

    public Task<ISftpBackend> ConnectAsync(SshConnectRequest request, CancellationToken cancellationToken)
    {
        Request = request;
        SecretAtConnectTime = request.Secret?.ToArray();
        if (!request.VerifyHostKey(new HostKeyInfo(request.Host, request.Port, "ssh-ed25519", "SHA256:abc")))
        {
            throw new InvalidOperationException("library aborted the key exchange");
        }

        return Task.FromResult<ISftpBackend>(Backend);
    }
}

internal sealed class CountingProgress : IProgress<long>
{
    public long Total { get; private set; }

    public void Report(long value) => Total += value;
}

public class RemotePathTests
{
    [Theory]
    [InlineData("/home/me", "docs", "/home/me/docs")]
    [InlineData("/", "etc", "/etc")]
    public void Combine_JoinsWithOneSlash(string directory, string name, string expected) =>
        Assert.Equal(expected, RemotePath.Combine(directory, name));

    [Theory]
    [InlineData("/home/me/docs", "/home/me")]
    [InlineData("/home/me/docs/", "/home/me")]
    [InlineData("/home", "/")]
    [InlineData("/", "/")]
    public void Parent_GoesUpOneFolder_AndStopsAtTheRoot(string path, string expected) =>
        Assert.Equal(expected, RemotePath.Parent(path));
}

public class SftpSessionTests : IDisposable
{
    private readonly string _local = Path.Combine(Path.GetTempPath(), "rd-sftp-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSftpBackend _backend = new();

    public SftpSessionTests()
    {
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_local, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private SftpSession Session() => new(_backend);

    [Fact]
    public async Task Listing_ShowsFoldersFirst_AlphabeticallyAndWithoutDotEntries()
    {
        _backend.AddDirectory("/home");
        _backend.AddFile("/home/b.txt", "b");
        _backend.AddDirectory("/home/Zeta");
        _backend.AddFile("/home/A.txt", "a");
        _backend.AddDirectory("/home/alpha");

        var names = (await Session().ListAsync("/home")).Select(e => e.Name).ToArray();

        Assert.Equal(new[] { "alpha", "Zeta", "A.txt", "b.txt" }, names);
    }

    [Fact]
    public async Task DeletingAFolder_RemovesItsContentsFirst()
    {
        _backend.AddDirectory("/d");
        _backend.AddDirectory("/d/sub");
        _backend.AddFile("/d/sub/x", "x");
        _backend.AddFile("/d/y", "y");

        await Session().DeleteAsync(new SftpEntry("d", "/d", true, 0, DateTime.UnixEpoch));

        Assert.False(_backend.Has("/d"));
        Assert.False(_backend.Has("/d/sub/x"));
        Assert.Equal("rmdir /d", _backend.Log[^1]);
        Assert.True(_backend.Log.IndexOf("rm /d/sub/x") < _backend.Log.IndexOf("rmdir /d/sub"));
    }

    [Fact]
    public async Task UploadingAFolder_RecreatesItRemotely()
    {
        var folder = Path.Combine(_local, "site");
        Directory.CreateDirectory(Path.Combine(folder, "css"));
        File.WriteAllText(Path.Combine(folder, "index.html"), "hello");
        File.WriteAllText(Path.Combine(folder, "css", "a.css"), "body{}");
        _backend.AddDirectory("/www");
        var progress = new CountingProgress();

        await Session().UploadAsync(folder, "/www", progress, CancellationToken.None);

        Assert.Equal("hello", _backend.ReadFile("/www/site/index.html"));
        Assert.Equal("body{}", _backend.ReadFile("/www/site/css/a.css"));
        Assert.Equal(11, progress.Total);
    }

    [Fact]
    public async Task DownloadingAFolder_WritesItLocally()
    {
        _backend.AddDirectory("/logs");
        _backend.AddFile("/logs/app.log", "line");
        var target = Path.Combine(_local, "out");
        Directory.CreateDirectory(target);

        await Session().DownloadAsync(new SftpEntry("logs", "/logs", true, 0, DateTime.UnixEpoch), target, null, CancellationToken.None);

        Assert.Equal("line", File.ReadAllText(Path.Combine(target, "logs", "app.log")));
    }

    [Fact]
    public async Task ADownloadNamedWithDotDot_StaysInsideTheChosenFolder()
    {
        _backend.AddFile("/evil", "x");
        var target = Path.Combine(_local, "safe");
        Directory.CreateDirectory(target);

        await Session().DownloadAsync(new SftpEntry("../evil", "/evil", false, 1, DateTime.UnixEpoch), target, null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(target, "evil")));
        Assert.False(File.Exists(Path.Combine(_local, "evil")));
    }

    [Fact]
    public async Task Renaming_MovesTheEntry()
    {
        _backend.AddFile("/a", "1");

        await Session().RenameAsync("/a", "/b");

        Assert.False(_backend.Has("/a"));
        Assert.Equal("1", _backend.ReadFile("/b"));
    }
}

public class SftpConnectTests
{
    private static ConnectionDefinition Definition(string? credentialId = "cred") =>
        new("c1", "Test", "ssh", "10.0.0.5", null, credentialId, new Dictionary<string, string> { ["username"] = "opt-user" });

    [Fact]
    public async Task Connecting_UsesTheBrokersCredential_AndWipesTheSecretAfterwards()
    {
        var broker = new FakeBroker();
        broker.ByConnection["c1"] = ("deploy", "hunter2");
        var factory = new FakeSftpFactory();

        using var session = await SftpSession.ConnectAsync(
            Definition(), broker, factory, new HostKeyVerifier(new MemoryHostKeyStore(), new ScriptedPrompt(true)), CancellationToken.None);

        Assert.Equal("deploy", factory.Request!.Username);
        Assert.Equal("hunter2", Encoding.UTF8.GetString(factory.SecretAtConnectTime!));
        Assert.All(factory.Request.Secret!, b => Assert.Equal(0, b));
        Assert.Equal("/home/me", session.HomeDirectory);
    }

    [Fact]
    public async Task ARefusedHostKey_ProducesAClearError()
    {
        var broker = new FakeBroker();
        broker.ByConnection["c1"] = ("deploy", "pw");

        var error = await Assert.ThrowsAsync<SshConnectionException>(() => SftpSession.ConnectAsync(
            Definition(), broker, new FakeSftpFactory(), new HostKeyVerifier(new MemoryHostKeyStore(), new ScriptedPrompt(false)), CancellationToken.None));

        Assert.Contains("not trusted", error.Message);
    }

    [Fact]
    public async Task WithoutASecretOrKey_ConnectingIsRefused()
    {
        await Assert.ThrowsAsync<SshConnectionException>(() => SftpSession.ConnectAsync(
            Definition(credentialId: null), new FakeBroker(), new FakeSftpFactory(),
            new HostKeyVerifier(new MemoryHostKeyStore(), null), CancellationToken.None));
    }
}
