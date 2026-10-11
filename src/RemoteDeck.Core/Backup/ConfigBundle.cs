using System.IO.Compression;
using System.Text;
using System.Text.Json;
using RemoteDeck.Core.Connections;

namespace RemoteDeck.Core.Backup;

/// <summary>A saved login or connection password that travels in a backup (only inside an encrypted one).</summary>
public sealed record BundleCredential(string Id, string? Username, string Password);

/// <summary>What a backup says about itself, readable without importing it.</summary>
public sealed record BundleInfo(int Format, string AppVersion, DateTimeOffset CreatedUtc, string? Machine, bool HasCredentials);

/// <param name="SettingsJson">The backup's settings.json; only given when the import overwrites, otherwise null.</param>
/// <param name="Credentials">Passwords the backup carries for the connections and logins it brought in. The caller puts them in the vault.</param>
public sealed record BundleResult(
    int Folders,
    int Connections,
    int Logins,
    int Files,
    int Skipped,
    string? SettingsJson,
    IReadOnlyList<BundleCredential> Credentials,
    IReadOnlyList<string> Notes);

public sealed class BundleException : Exception
{
    public BundleException(string message)
        : base(message)
    {
    }

    public BundleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A backup of RemoteDeck's data as one zip: the connection list, settings, workspaces, notes, saved chats and themes,
/// and optionally the passwords. The zip holds passwords in plain text, so the caller must encrypt it whenever it has
/// them (see <c>PassphraseBox</c> in the vault project). Importing adds what is missing and leaves what is there alone,
/// unless asked to overwrite.
/// </summary>
public static class ConfigBundle
{
    public const int Format = 1;

    public const string Extension = ".rdbackup";

    private const string ManifestName = "manifest.json";
    private const string CredentialsName = "credentials.json";
    private const string ConnectionsName = "connections.json";
    private const string SettingsName = "settings.json";
    private const long MaxFileBytes = 256L * 1024 * 1024;
    private const long MaxTotalBytes = 1024L * 1024 * 1024;
    private const int MaxEntries = 50_000;

    /// <summary>Folders of the data folder that are copied whole.</summary>
    private static readonly string[] DataFolders = { "workspaces", "themes", "Notes", "chats", "logos" };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed record Manifest(int Format, string? AppVersion, DateTimeOffset CreatedUtc, string? Machine, bool Credentials);

    /// <summary>Zips the data folder. Pass the credentials to include them, or null to leave every password out.</summary>
    public static byte[] Create(string dataFolder, IReadOnlyList<BundleCredential>? credentials, string appVersion, DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);

        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = new Manifest(Format, appVersion, now ?? DateTimeOffset.UtcNow, Environment.MachineName, credentials is { Count: > 0 });
            AddText(zip, ManifestName, JsonSerializer.Serialize(manifest, Json));

            foreach (var name in new[] { ConnectionsName, SettingsName })
            {
                var path = Path.Combine(dataFolder, name);
                if (File.Exists(path))
                {
                    AddBytes(zip, name, ReadShared(path));
                }
            }

            foreach (var folder in DataFolders)
            {
                var root = Path.Combine(dataFolder, folder);
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var extension = Path.GetExtension(file);
                    if (extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bak", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var relative = Path.GetRelativePath(dataFolder, file).Replace('\\', '/');
                    AddBytes(zip, relative, ReadShared(file));
                }
            }

            if (credentials is { Count: > 0 })
            {
                AddText(zip, CredentialsName, JsonSerializer.Serialize(credentials, Json));
            }
        }

        return memory.ToArray();
    }

    /// <summary>True when <paramref name="data"/> looks like a zip (an unencrypted backup).</summary>
    public static bool IsZip(ReadOnlySpan<byte> data) => data.Length > 4 && data[0] == (byte)'P' && data[1] == (byte)'K';

    /// <summary>Reads the backup's manifest. Throws <see cref="BundleException"/> when this is not a RemoteDeck backup.</summary>
    public static BundleInfo Inspect(byte[] bundle)
    {
        using var zip = OpenZip(bundle);
        var manifest = ReadManifest(zip);
        return new BundleInfo(manifest.Format, manifest.AppVersion ?? string.Empty, manifest.CreatedUtc, manifest.Machine, manifest.Credentials);
    }

    /// <summary>
    /// Brings a backup into this installation. Connections, folders and logins are merged into <paramref name="target"/>
    /// (which saves itself); files are written under <paramref name="dataFolder"/>. Things that already exist here (same
    /// id, same file) are kept unless <paramref name="overwrite"/> is set.
    /// </summary>
    public static BundleResult Import(byte[] bundle, string dataFolder, ConnectionStore target, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        ArgumentNullException.ThrowIfNull(target);

        using var zip = OpenZip(bundle);
        var manifest = ReadManifest(zip);
        CheckEntries(zip);

        var notes = new List<string>();
        var skipped = 0;

        var credentials = new List<BundleCredential>();
        if (manifest.Credentials && zip.GetEntry(CredentialsName) is { } credentialEntry)
        {
            try
            {
                credentials = JsonSerializer.Deserialize<List<BundleCredential>>(ReadText(credentialEntry), Json) ?? new List<BundleCredential>();
            }
            catch (JsonException ex)
            {
                throw new BundleException("The backup's passwords could not be read.", ex);
            }
        }

        var folders = 0;
        var connections = 0;
        var logins = 0;
        IReadOnlyList<string> referenced = Array.Empty<string>();
        if (zip.GetEntry(ConnectionsName) is { } catalogEntry)
        {
            ConnectionStore source;
            try
            {
                source = ConnectionStore.Deserialize(ReadText(catalogEntry));
            }
            catch (CatalogException ex)
            {
                throw new BundleException("The backup's connection list could not be read: " + ex.Message, ex);
            }

            var keepCredentials = manifest.Credentials;
            (folders, connections, logins, var catalogSkipped) = Merge(target, source, overwrite, keepCredentials);
            skipped += catalogSkipped;
            referenced = keepCredentials ? source.ReferencedCredentialIds().ToList() : Array.Empty<string>();

            if (!keepCredentials && source.Logins.Count > 0)
            {
                notes.Add($"{source.Logins.Count} saved login(s) were not imported because the backup has no passwords. Add them again under Logins.");
            }
        }

        var root = Path.GetFullPath(dataFolder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var files = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || !IsDataFile(entry.FullName))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(dataFolder, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new BundleException($"The backup holds a file outside the data folder ({entry.FullName}).");
            }

            if (File.Exists(destination) && !overwrite)
            {
                skipped++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + ".tmp";
            using (var input = entry.Open())
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
            }

            File.Move(temporary, destination, overwrite: true);
            files++;
        }

        string? settings = null;
        if (overwrite && zip.GetEntry(SettingsName) is { } settingsEntry)
        {
            settings = ReadText(settingsEntry);
        }

        var wanted = new HashSet<string>(referenced, StringComparer.Ordinal);
        var carried = credentials.Where(c => wanted.Contains(c.Id)).ToList();
        return new BundleResult(folders, connections, logins, files, skipped, settings, carried, notes);
    }

    // ---- merging the connection list ----

    private static (int Folders, int Connections, int Logins, int Skipped) Merge(ConnectionStore target, ConnectionStore source, bool overwrite, bool keepCredentials)
    {
        var added = (Folders: 0, Connections: 0, Logins: 0, Skipped: 0);
        var sourceLogins = source.Logins.ToDictionary(l => l.Id, StringComparer.Ordinal);

        // Folders: parents first, so each one has somewhere to go.
        var pending = source.Folders.ToList();
        var done = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var ready = pending.Where(f => f.ParentId is null || done.Contains(f.ParentId) || target.FindFolder(f.ParentId) is not null).ToList();
            if (ready.Count == 0)
            {
                added.Skipped += pending.Count;
                break;
            }

            foreach (var folder in ready)
            {
                pending.Remove(folder);
                done.Add(folder.Id);
                var entry = keepCredentials ? folder : folder with { CredentialId = null };
                try
                {
                    if (target.FindFolder(folder.Id) is null)
                    {
                        target.AddFolder(entry);
                        added.Folders++;
                    }
                    else if (overwrite)
                    {
                        target.UpdateFolder(entry);
                        added.Folders++;
                    }
                    else
                    {
                        added.Skipped++;
                    }
                }
                catch (CatalogException)
                {
                    added.Skipped++;
                }
            }
        }

        if (keepCredentials)
        {
            foreach (var login in source.Logins)
            {
                try
                {
                    if (target.FindLogin(login.Id) is null)
                    {
                        target.AddLogin(login with { Name = target.UniqueLoginName(login.Name) });
                        added.Logins++;
                    }
                    else if (overwrite)
                    {
                        target.UpdateLogin(login);
                        added.Logins++;
                    }
                    else
                    {
                        added.Skipped++;
                    }
                }
                catch (CatalogException)
                {
                    added.Skipped++;
                }
            }
        }

        foreach (var connection in source.Connections)
        {
            var entry = connection;
            if (!keepCredentials && entry.CredentialId is { } credentialId)
            {
                // No password travels, so the reference would point at nothing. Keep the user name where it is useful.
                var options = entry.Options is null ? new Dictionary<string, string>() : new Dictionary<string, string>(entry.Options);
                if (sourceLogins.TryGetValue(credentialId, out var login) && !string.IsNullOrEmpty(login.Username) && !options.ContainsKey("username"))
                {
                    options["username"] = login.Username;
                }

                entry = entry with { CredentialId = null, Options = options.Count > 0 ? options : null };
            }

            try
            {
                if (target.FindConnection(entry.Id) is null)
                {
                    target.AddConnection(entry);
                    added.Connections++;
                }
                else if (overwrite)
                {
                    target.UpdateConnection(entry);
                    added.Connections++;
                }
                else
                {
                    added.Skipped++;
                }
            }
            catch (CatalogException)
            {
                added.Skipped++;
            }
        }

        return added;
    }

    // ---- zip helpers ----

    private static bool IsDataFile(string fullName)
    {
        var name = fullName.Replace('\\', '/');
        if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part is ".." or "."))
        {
            return false;
        }

        var top = name.Split('/')[0];
        return DataFolders.Contains(top, StringComparer.Ordinal) && name.Contains('/');
    }

    private static ZipArchive OpenZip(byte[] bundle)
    {
        try
        {
            return new ZipArchive(new MemoryStream(bundle, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new BundleException("This is not a RemoteDeck backup.", ex);
        }
    }

    private static Manifest ReadManifest(ZipArchive zip)
    {
        if (zip.GetEntry(ManifestName) is not { } entry)
        {
            throw new BundleException("This is not a RemoteDeck backup.");
        }

        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(ReadText(entry), Json);
        }
        catch (JsonException ex)
        {
            throw new BundleException("This is not a RemoteDeck backup.", ex);
        }

        if (manifest is null)
        {
            throw new BundleException("This is not a RemoteDeck backup.");
        }

        if (manifest.Format > Format)
        {
            throw new BundleException($"This backup was made by a newer RemoteDeck (format {manifest.Format}). Update RemoteDeck to restore it.");
        }

        return manifest;
    }

    /// <summary>Refuses a backup that is absurdly large (a zip bomb) before anything is written.</summary>
    private static void CheckEntries(ZipArchive zip)
    {
        if (zip.Entries.Count > MaxEntries)
        {
            throw new BundleException("The backup holds too many files.");
        }

        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.Length > MaxFileBytes)
            {
                throw new BundleException($"The backup holds a file that is too large ({entry.FullName}).");
            }

            total += entry.Length;
            if (total > MaxTotalBytes)
            {
                throw new BundleException("The backup is too large.");
            }
        }
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxFileBytes)
        {
            throw new BundleException($"The backup holds a file that is too large ({entry.FullName}).");
        }

        using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false));
        return reader.ReadToEnd();
    }

    private static void AddText(ZipArchive zip, string name, string text) =>
        AddBytes(zip, name, new UTF8Encoding(false).GetBytes(text));

    private static void AddBytes(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    /// <summary>Reads a file another part of the app may have open (a note being edited, a settings file being saved).</summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
