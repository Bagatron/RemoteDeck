using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RemoteDeck.Vault;

/// <summary>The passphrase did not open the data, or the data was changed.</summary>
public sealed class InvalidPassphraseException : VaultException
{
    public InvalidPassphraseException()
        : base("Wrong passphrase, or the file is damaged.")
    {
    }
}

/// <summary>
/// Encrypts a block of bytes under a passphrase, for files that leave the machine (such as a backup).
/// The passphrase is stretched with Argon2id into an AES-256-GCM key. Layout: "RDBX" | version | memory | iterations |
/// parallelism | salt | nonce | tag | ciphertext. The header is authenticated, so changing any part of it fails the open.
/// </summary>
public static class PassphraseBox
{
    private const byte Version = 1;
    private const int SaltSize = 16;
    private const int HeaderSize = 4 + 1 + 4 + 4 + 4 + SaltSize;
    private static readonly byte[] Magic = { (byte)'R', (byte)'D', (byte)'B', (byte)'X' };

    /// <summary>True when <paramref name="data"/> starts like something <see cref="Seal"/> produced.</summary>
    public static bool IsSealed(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderSize + SealedBox.Overhead && data[..4].SequenceEqual(Magic.AsSpan());

    public static byte[] Seal(ReadOnlySpan<char> passphrase, ReadOnlySpan<byte> data, KdfParameters? parameters = null, IKeyDerivation? kdf = null)
    {
        if (passphrase.IsEmpty)
        {
            throw new ArgumentException("A passphrase is required.", nameof(passphrase));
        }

        parameters ??= KdfParameters.Default;
        if (parameters.Problem() is { } problem)
        {
            throw new ArgumentException(problem, nameof(parameters));
        }

        kdf ??= new Argon2idKeyDerivation();
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        header[4] = Version;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(5), parameters.MemoryKiB);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(9), parameters.Iterations);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(13), parameters.Parallelism);
        RandomNumberGenerator.Fill(header.AsSpan(17, SaltSize));

        var key = Derive(kdf, passphrase, header.AsSpan(17, SaltSize), parameters);
        try
        {
            var sealedData = SealedBox.Seal(key, data, header);
            var result = new byte[HeaderSize + sealedData.Length];
            header.CopyTo(result, 0);
            sealedData.CopyTo(result, HeaderSize);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static byte[] Open(ReadOnlySpan<char> passphrase, ReadOnlySpan<byte> blob, IKeyDerivation? kdf = null)
    {
        if (!IsSealed(blob))
        {
            throw new VaultFormatException("This is not an encrypted RemoteDeck file.");
        }

        if (blob[4] != Version)
        {
            throw new VaultFormatException($"This file was made by a newer version of RemoteDeck (format {blob[4]}).");
        }

        var parameters = new KdfParameters(
            BinaryPrimitives.ReadInt32BigEndian(blob[5..]),
            BinaryPrimitives.ReadInt32BigEndian(blob[9..]),
            BinaryPrimitives.ReadInt32BigEndian(blob[13..]));
        if (parameters.Problem() is { } problem)
        {
            throw new VaultFormatException("The file has unusable encryption settings: " + problem);
        }

        kdf ??= new Argon2idKeyDerivation();
        var header = blob[..HeaderSize];
        var key = Derive(kdf, passphrase, blob.Slice(17, SaltSize), parameters);
        try
        {
            return SealedBox.Open(key, blob[HeaderSize..], header);
        }
        catch (CryptographicException)
        {
            throw new InvalidPassphraseException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Derive(IKeyDerivation kdf, ReadOnlySpan<char> passphrase, ReadOnlySpan<byte> salt, KdfParameters parameters)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(passphrase)];
        Encoding.UTF8.GetBytes(passphrase, bytes);
        try
        {
            return kdf.DeriveKey(bytes, salt, parameters);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
