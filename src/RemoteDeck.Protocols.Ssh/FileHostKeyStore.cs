using System.Text;
using System.Text.Json;

namespace RemoteDeck.Protocols.Ssh;

/// <summary>
/// Keeps trusted host keys in a small JSON file. A damaged file is reported rather than ignored: treating it
/// as empty would quietly turn every known server back into an unknown one.
/// </summary>
public sealed class FileHostKeyStore : IHostKeyStore
{
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, List<StoredKey>>? _hosts;

    public FileHostKeyStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public IReadOnlyList<HostKeyInfo> FindAll(string host, int port)
    {
        lock (_gate)
        {
            var hosts = LoadLocked();
            if (!hosts.TryGetValue(Identify(host, port), out var keys))
            {
                return Array.Empty<HostKeyInfo>();
            }

            return keys.Select(k => new HostKeyInfo(host, port, k.Algorithm, k.Fingerprint)).ToArray();
        }
    }

    public void Save(HostKeyInfo key)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            var hosts = LoadLocked();
            var id = Identify(key.Host, key.Port);
            if (!hosts.TryGetValue(id, out var keys))
            {
                keys = new List<StoredKey>();
                hosts[id] = keys;
            }

            keys.RemoveAll(k => string.Equals(k.Algorithm, key.Algorithm, StringComparison.OrdinalIgnoreCase));
            keys.Add(new StoredKey(key.Algorithm, key.Fingerprint));

            WriteLocked(hosts);
        }
    }

    /// <summary>Host names are case-insensitive, and the same name on another port is a different server.</summary>
    internal static string Identify(string host, int port)
    {
        var name = host.Trim().ToLowerInvariant();
        return name.Contains(':') ? $"[{name}]:{port}" : $"{name}:{port}";
    }

    private Dictionary<string, List<StoredKey>> LoadLocked()
    {
        if (_hosts is not null)
        {
            return _hosts;
        }

        if (!File.Exists(_path))
        {
            _hosts = new Dictionary<string, List<StoredKey>>(StringComparer.Ordinal);
            return _hosts;
        }

        try
        {
            var file = JsonSerializer.Deserialize<HostKeyFile>(File.ReadAllText(_path), JsonOptions);
            if (file is null || file.Version != FormatVersion || file.Hosts is null)
            {
                throw new InvalidDataException("unsupported or empty content");
            }

            _hosts = new Dictionary<string, List<StoredKey>>(file.Hosts, StringComparer.Ordinal);
            return _hosts;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new InvalidDataException(
                $"The known-hosts file '{_path}' is damaged ({ex.Message}). Fix or delete it, then reconnect and check the fingerprints again.",
                ex);
        }
    }

    private void WriteLocked(Dictionary<string, List<StoredKey>> hosts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var json = JsonSerializer.Serialize(new HostKeyFile(FormatVersion, hosts), JsonOptions);
        var temporary = _path + ".tmp";
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, _path, overwrite: true);
    }

    private sealed record StoredKey(string Algorithm, string Fingerprint);

    private sealed record HostKeyFile(int Version, Dictionary<string, List<StoredKey>> Hosts);
}
