using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteDeck.Plugin;

namespace RemoteDeck.Vault;

internal sealed record KdfSection(string Algorithm, string Salt, int MemoryKiB, int Iterations, int Parallelism);

internal sealed record VaultDocument(int Version, KdfSection Kdf, string WrappedKey, Dictionary<string, string> Entries);

/// <summary>
/// An encrypted store of credentials.
///
/// Design:
/// * A random 256-bit data key encrypts every credential (AES-256-GCM, fresh nonce each time).
/// * The data key is itself stored wrapped by a key derived from the master password with Argon2id.
///   Changing the master password re-wraps one small blob; no credential is re-encrypted, and a second
///   unlock method (Windows Hello, a recovery key) can later wrap the same data key.
/// * Each credential is bound to its id and padded, so blobs cannot be swapped and lengths are hidden.
/// * Credentials stay encrypted in memory; one is decrypted only inside <see cref="UseAsync{T}"/> and wiped afterwards.
///
/// Only credentials live here. Hosts, names and tags are kept elsewhere so they stay searchable without unlocking.
/// </summary>
public sealed class CredentialVault : IDisposable
{
    public const int FormatVersion = 1;

    private const int KeySize = 32;
    private const int SaltSize = 16;
    private const int MinSaltSize = 16;
    private const int MaxSaltSize = 64;

    private static readonly byte[] WrapAad = Encoding.ASCII.GetBytes("remotedeck-vault-v1|wrap");

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private readonly IKeyDerivation _kdf;
    private readonly KdfParameters _parameters;
    private readonly Dictionary<string, byte[]> _entries;
    private byte[] _salt;
    private byte[] _wrappedKey;
    private byte[]? _dataKey;

    private CredentialVault(
        IKeyDerivation kdf,
        KdfParameters parameters,
        byte[] salt,
        byte[] wrappedKey,
        Dictionary<string, byte[]> entries,
        byte[]? dataKey)
    {
        _kdf = kdf;
        _parameters = parameters;
        _salt = salt;
        _wrappedKey = wrappedKey;
        _entries = entries;
        _dataKey = dataKey;
    }

    /// <summary>Raised after the contents or the master password change, so the host can save the file.</summary>
    public event EventHandler? Changed;

    public bool IsLocked
    {
        get
        {
            lock (_gate)
            {
                return _dataKey is null;
            }
        }
    }

    public IReadOnlyList<string> Ids
    {
        get
        {
            lock (_gate)
            {
                return _entries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            }
        }
    }

    /// <summary>Creates a new, empty, unlocked vault.</summary>
    public static CredentialVault Create(
        ReadOnlySpan<char> masterPassword,
        KdfParameters? parameters = null,
        IKeyDerivation? kdf = null)
    {
        if (masterPassword.IsEmpty)
        {
            throw new ArgumentException("The master password must not be empty.", nameof(masterPassword));
        }

        kdf ??= new Argon2idKeyDerivation();
        parameters ??= KdfParameters.Default;
        if (parameters.Problem() is { } problem)
        {
            throw new ArgumentException(problem, nameof(parameters));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var dataKey = RandomNumberGenerator.GetBytes(KeySize);
        var kek = DeriveKek(kdf, masterPassword, salt, parameters);
        try
        {
            var wrapped = SealedBox.Seal(kek, dataKey, WrapAad);
            return new CredentialVault(
                kdf,
                parameters,
                salt,
                wrapped,
                new Dictionary<string, byte[]>(StringComparer.Ordinal),
                dataKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>Reads a vault file's contents and unlocks it. Throws <see cref="InvalidMasterPasswordException"/> on a wrong password.</summary>
    public static CredentialVault Open(string json, ReadOnlySpan<char> masterPassword, IKeyDerivation? kdf = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        kdf ??= new Argon2idKeyDerivation();

        var document = ParseDocument(json);

        if (!string.Equals(document.Kdf.Algorithm, kdf.Algorithm, StringComparison.Ordinal))
        {
            throw new VaultFormatException(
                $"This vault uses the '{document.Kdf.Algorithm}' key derivation, which is not supported here.");
        }

        var parameters = new KdfParameters(document.Kdf.MemoryKiB, document.Kdf.Iterations, document.Kdf.Parallelism);
        if (parameters.Problem() is { } problem)
        {
            throw new VaultFormatException($"The vault has unusable key-derivation settings. {problem}");
        }

        var salt = FromBase64(document.Kdf.Salt, "salt");
        if (salt.Length is < MinSaltSize or > MaxSaltSize)
        {
            throw new VaultFormatException("The vault has an invalid salt.");
        }

        var wrapped = FromBase64(document.WrappedKey, "wrapped key");
        if (wrapped.Length != SealedBox.Overhead + KeySize)
        {
            throw new VaultFormatException("The vault's key block has the wrong size.");
        }

        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (id, value) in document.Entries)
        {
            var blob = FromBase64(value, $"entry '{id}'");
            if (blob.Length < SealedBox.Overhead)
            {
                throw new VaultFormatException($"Entry '{id}' is too short to be valid.");
            }

            entries[id] = blob;
        }

        var vault = new CredentialVault(kdf, parameters, salt, wrapped, entries, dataKey: null);
        vault.Unlock(masterPassword);
        return vault;
    }

    /// <summary>Unlocks a locked vault. Throws <see cref="InvalidMasterPasswordException"/> when the password is wrong.</summary>
    public void Unlock(ReadOnlySpan<char> masterPassword)
    {
        var key = UnwrapDataKey(masterPassword);
        lock (_gate)
        {
            if (_dataKey is not null)
            {
                CryptographicOperations.ZeroMemory(_dataKey);
            }

            _dataKey = key;
        }
    }

    /// <summary>Wipes the data key from memory. Credentials stay encrypted and can be unlocked again.</summary>
    public void Lock()
    {
        lock (_gate)
        {
            if (_dataKey is not null)
            {
                CryptographicOperations.ZeroMemory(_dataKey);
                _dataKey = null;
            }
        }
    }

    public void Dispose()
    {
        Lock();
    }

    public bool Contains(string id)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(id);
        }
    }

    /// <summary>Adds or replaces a credential. An empty username is stored as no username.</summary>
    public void Set(string id, string? username, ReadOnlySpan<char> password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var plaintext = EntryCodec.Encode(username, password);
        try
        {
            lock (_gate)
            {
                var key = RequireKey();
                _entries[id] = SealedBox.Seal(key, plaintext, EntryAad(id));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_gate)
        {
            RequireKey();
            removed = _entries.Remove(id);
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>
    /// Decrypts one credential for the duration of <paramref name="use"/>. The credential is wiped when the
    /// callback finishes, so it must not be kept or used afterwards.
    /// </summary>
    public async ValueTask<T> UseAsync<T>(
        string id,
        Func<ICredential, ValueTask<T>> use,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(use);
        cancellationToken.ThrowIfCancellationRequested();

        using var credential = Decrypt(id);
        return await use(credential).ConfigureAwait(false);
    }

    /// <summary>
    /// Changes the master password. The current password must be supplied again, even though the vault is unlocked.
    /// Credentials are not re-encrypted; only the key block is rewritten, under a new salt.
    /// </summary>
    public void ChangeMasterPassword(ReadOnlySpan<char> currentPassword, ReadOnlySpan<char> newPassword)
    {
        if (newPassword.IsEmpty)
        {
            throw new ArgumentException("The new master password must not be empty.", nameof(newPassword));
        }

        // Proves the caller knows the current password; throws InvalidMasterPasswordException otherwise.
        CryptographicOperations.ZeroMemory(UnwrapDataKey(currentPassword));

        var newSalt = RandomNumberGenerator.GetBytes(SaltSize);
        var kek = DeriveKek(_kdf, newPassword, newSalt, _parameters);
        try
        {
            lock (_gate)
            {
                var key = RequireKey();
                _wrappedKey = SealedBox.Seal(kek, key, WrapAad);
                _salt = newSalt;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The encrypted vault file contents. Safe to call while locked; it contains no plaintext.</summary>
    public string Serialize()
    {
        VaultDocument document;
        lock (_gate)
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in _entries.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                entries[id] = Convert.ToBase64String(_entries[id]);
            }

            document = new VaultDocument(
                FormatVersion,
                new KdfSection(
                    _kdf.Algorithm,
                    Convert.ToBase64String(_salt),
                    _parameters.MemoryKiB,
                    _parameters.Iterations,
                    _parameters.Parallelism),
                Convert.ToBase64String(_wrappedKey),
                entries);
        }

        return JsonSerializer.Serialize(document, JsonOptions);
    }

    private VaultCredential Decrypt(string id)
    {
        byte[] plaintext;
        lock (_gate)
        {
            var key = RequireKey();
            if (!_entries.TryGetValue(id, out var blob))
            {
                throw new KeyNotFoundException($"No credential '{id}' in the vault.");
            }

            try
            {
                plaintext = SealedBox.Open(key, blob, EntryAad(id));
            }
            catch (CryptographicException ex)
            {
                throw new VaultFormatException($"Credential '{id}' is damaged or was modified.", ex);
            }
        }

        try
        {
            var (username, password) = EntryCodec.Decode(plaintext);
            return new VaultCredential(username, password);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private byte[] UnwrapDataKey(ReadOnlySpan<char> masterPassword)
    {
        byte[] salt;
        byte[] wrapped;
        lock (_gate)
        {
            salt = _salt;
            wrapped = _wrappedKey;
        }

        // The slow part runs outside the lock.
        var kek = DeriveKek(_kdf, masterPassword, salt, _parameters);
        try
        {
            return SealedBox.Open(kek, wrapped, WrapAad);
        }
        catch (CryptographicException)
        {
            throw new InvalidMasterPasswordException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private byte[] RequireKey()
    {
        return _dataKey ?? throw new VaultLockedException();
    }

    private static byte[] EntryAad(string id)
    {
        return Encoding.UTF8.GetBytes("remotedeck-vault-v1|entry|" + id);
    }

    private static byte[] DeriveKek(
        IKeyDerivation kdf,
        ReadOnlySpan<char> password,
        byte[] salt,
        KdfParameters parameters)
    {
        var passwordBytes = new byte[Encoding.UTF8.GetByteCount(password)];
        try
        {
            Encoding.UTF8.GetBytes(password, passwordBytes);
            return kdf.DeriveKey(passwordBytes, salt, parameters);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static VaultDocument ParseDocument(string json)
    {
        VaultDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<VaultDocument>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new VaultFormatException("The vault file is not valid JSON.", ex);
        }

        if (document is null || document.Kdf is null || document.WrappedKey is null || document.Entries is null)
        {
            throw new VaultFormatException("The vault file is incomplete.");
        }

        if (document.Version != FormatVersion)
        {
            throw new VaultFormatException($"Unsupported vault version {document.Version}.");
        }

        return document;
    }

    private static byte[] FromBase64(string? value, string what)
    {
        try
        {
            return Convert.FromBase64String(value ?? string.Empty);
        }
        catch (FormatException ex)
        {
            throw new VaultFormatException($"The vault's {what} is not valid base64.", ex);
        }
    }
}
