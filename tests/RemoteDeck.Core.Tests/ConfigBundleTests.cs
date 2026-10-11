using System.IO.Compression;
using System.Text;
using RemoteDeck.Core.Backup;
using RemoteDeck.Core.Connections;

namespace RemoteDeck.Core.Tests;

public sealed class ConfigBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rd-bundle-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Write(string folder, string relative, string text)
    {
        var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>A data folder like the app's, with one folder, a login, two connections and some files.</summary>
    private string SourceFolder(out ConnectionStore store)
    {
        var folder = Folder("src");
        store = new ConnectionStore();
        store.AddFolder(new FolderEntry("f1", "Prod"));
        store.AddFolder(new FolderEntry("f2", "Web", "f1"));
        store.AddLogin(new LoginEntry("login-1", "Lab admin", "admin"));
        store.AddConnection(new ConnectionEntry("c1", "web01", "ssh", "10.0.0.1", FolderId: "f2", CredentialId: "login-1"));
        store.AddConnection(new ConnectionEntry("c2", "desk", "rdp", "10.0.0.2", CredentialId: "cred-c2"));
        File.WriteAllText(Path.Combine(folder, "connections.json"), store.Serialize());
        Write(folder, "settings.json", "{\"Theme\":\"Dark\"}");
        Write(folder, "workspaces/grid.json", "{}");
        Write(folder, "themes/mine.json", "{}");
        Write(folder, "Notes/Note 1.txt", "remember this");
        Write(folder, "chats/workspaces/grid/p1.json", "{}");
        Write(folder, "logos/eagle.png", "png");
        Write(folder, "logs/session.log", "never exported");
        Write(folder, "vault.json", "never exported");
        Write(folder, "workspaces/old.json.bak", "skipped");
        return folder;
    }

    private static List<BundleCredential> Credentials() => new()
    {
        new("login-1", "admin", "s3cret"),
        new("cred-c2", "dave", "hunter2"),
        new("unrelated", "x", "y"),
    };

    [Fact]
    public void RoundTrip_BringsEverythingToAnEmptyMachine()
    {
        var source = SourceFolder(out _);
        var bundle = ConfigBundle.Create(source, Credentials(), "1.2.3");

        var destination = Folder("dst");
        var target = new ConnectionStore();
        var result = ConfigBundle.Import(bundle, destination, target, overwrite: false);

        Assert.Equal(2, result.Folders);
        Assert.Equal(2, result.Connections);
        Assert.Equal(1, result.Logins);
        Assert.Equal(5, result.Files);
        Assert.True(File.Exists(Path.Combine(destination, "logos", "eagle.png")));
        Assert.Equal("f1", target.FindFolder("f2")!.ParentId);
        Assert.Equal("login-1", target.FindConnection("c1")!.CredentialId);
        Assert.Equal("remember this", File.ReadAllText(Path.Combine(destination, "Notes", "Note 1.txt")));
        Assert.True(File.Exists(Path.Combine(destination, "chats", "workspaces", "grid", "p1.json")));

        // Only the passwords the imported items refer to come back; the vault, logs and backups stay behind.
        Assert.Equal(new[] { "cred-c2", "login-1" }, result.Credentials.Select(c => c.Id).OrderBy(i => i, StringComparer.Ordinal));
        Assert.False(File.Exists(Path.Combine(destination, "vault.json")));
        Assert.False(File.Exists(Path.Combine(destination, "logs", "session.log")));
        Assert.False(File.Exists(Path.Combine(destination, "workspaces", "old.json.bak")));
    }

    [Fact]
    public void Inspect_ReportsTheManifest()
    {
        var source = SourceFolder(out _);
        var when = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

        var withPasswords = ConfigBundle.Inspect(ConfigBundle.Create(source, Credentials(), "1.2.3", when));
        var without = ConfigBundle.Inspect(ConfigBundle.Create(source, null, "1.2.3", when));

        Assert.Equal(ConfigBundle.Format, withPasswords.Format);
        Assert.Equal("1.2.3", withPasswords.AppVersion);
        Assert.Equal(when, withPasswords.CreatedUtc);
        Assert.True(withPasswords.HasCredentials);
        Assert.False(without.HasCredentials);
    }

    [Fact]
    public void WithoutPasswords_NothingSecretIsInTheFile()
    {
        var source = SourceFolder(out _);
        var bundle = ConfigBundle.Create(source, null, "1.0");

        using var zip = new ZipArchive(new MemoryStream(bundle));
        Assert.Null(zip.GetEntry("credentials.json"));
        Assert.DoesNotContain("s3cret", Encoding.UTF8.GetString(bundle));
    }

    [Fact]
    public void WithoutPasswords_ReferencesAreDroppedAndTheUserNameKept()
    {
        var source = SourceFolder(out _);
        var bundle = ConfigBundle.Create(source, null, "1.0");

        var target = new ConnectionStore();
        var result = ConfigBundle.Import(bundle, Folder("dst"), target, overwrite: false);

        var web = target.FindConnection("c1")!;
        Assert.Null(web.CredentialId);
        Assert.Equal("admin", web.Options!["username"]);
        Assert.Null(target.FindConnection("c2")!.CredentialId);
        Assert.Empty(target.Logins);
        Assert.Empty(result.Credentials);
        Assert.Contains(result.Notes, n => n.Contains("login"));
    }

    [Fact]
    public void MergeKeepsWhatIsAlreadyHere()
    {
        var source = SourceFolder(out _);
        var bundle = ConfigBundle.Create(source, Credentials(), "1.0");

        var destination = Folder("dst");
        Write(destination, "Notes/Note 1.txt", "mine");
        var target = new ConnectionStore();
        target.AddConnection(new ConnectionEntry("c1", "my own name", "ssh", "192.168.0.9"));

        var result = ConfigBundle.Import(bundle, destination, target, overwrite: false);

        Assert.Equal("my own name", target.FindConnection("c1")!.Name);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(destination, "Notes", "Note 1.txt")));
        Assert.Equal(1, result.Connections);
        Assert.True(result.Skipped >= 2);
        Assert.Null(result.SettingsJson);
    }

    [Fact]
    public void OverwriteReplacesAndReturnsTheSettings()
    {
        var source = SourceFolder(out _);
        var bundle = ConfigBundle.Create(source, Credentials(), "1.0");

        var destination = Folder("dst");
        Write(destination, "Notes/Note 1.txt", "mine");
        var target = new ConnectionStore();
        target.AddConnection(new ConnectionEntry("c1", "my own name", "ssh", "192.168.0.9"));

        var result = ConfigBundle.Import(bundle, destination, target, overwrite: true);

        Assert.Equal("web01", target.FindConnection("c1")!.Name);
        Assert.Equal("remember this", File.ReadAllText(Path.Combine(destination, "Notes", "Note 1.txt")));
        Assert.Contains("Dark", result.SettingsJson);
    }

    [Fact]
    public void ALoginNameAlreadyTakenGetsANumber()
    {
        var source = SourceFolder(out _);
        var bundle = ConfigBundle.Create(source, Credentials(), "1.0");

        var target = new ConnectionStore();
        target.AddLogin(new LoginEntry("other", "Lab admin", "someone"));

        ConfigBundle.Import(bundle, Folder("dst"), target, overwrite: false);

        Assert.Equal("Lab admin (2)", target.FindLogin("login-1")!.Name);
        Assert.Equal("Lab admin", target.FindLogin("other")!.Name);
    }

    [Fact]
    public void ARepeatedImportAddsNothing()
    {
        var source = SourceFolder(out _);
        var bundle = ConfigBundle.Create(source, Credentials(), "1.0");
        var destination = Folder("dst");
        var target = new ConnectionStore();
        ConfigBundle.Import(bundle, destination, target, overwrite: false);

        var again = ConfigBundle.Import(bundle, destination, target, overwrite: false);

        Assert.Equal(0, again.Folders + again.Connections + again.Logins + again.Files);
        Assert.Equal(2, target.Connections.Count);
    }

    [Fact]
    public void NotABackup_IsRefused()
    {
        Assert.Throws<BundleException>(() => ConfigBundle.Inspect(Encoding.UTF8.GetBytes("hello")));

        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("something.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("hi");
        }

        Assert.Throws<BundleException>(() => ConfigBundle.Inspect(memory.ToArray()));
    }

    [Fact]
    public void ANewerFormat_IsRefused()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write("{\"format\":99,\"createdUtc\":\"2030-01-01T00:00:00Z\",\"credentials\":false}");
        }

        var ex = Assert.Throws<BundleException>(() => ConfigBundle.Inspect(memory.ToArray()));
        Assert.Contains("newer", ex.Message);
    }

    [Theory]
    [InlineData("workspaces/../../evil.txt")]
    [InlineData("../evil.txt")]
    [InlineData("/etc/evil.txt")]
    [InlineData("Notes/C:/evil.txt")]
    [InlineData("vault.json")]
    [InlineData("logs/a.log")]
    public void FilesOutsideTheDataFolders_AreNeverWritten(string entryName)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            {
                writer.Write("{\"format\":1,\"createdUtc\":\"2030-01-01T00:00:00Z\",\"credentials\":false}");
            }

            using (var writer = new StreamWriter(zip.CreateEntry(entryName).Open()))
            {
                writer.Write("bad");
            }
        }

        var destination = Folder("dst");
        var result = ConfigBundle.Import(memory.ToArray(), destination, new ConnectionStore(), overwrite: true);

        Assert.Equal(0, result.Files);
        Assert.Empty(Directory.EnumerateFiles(_root, "evil.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories));
    }
}
