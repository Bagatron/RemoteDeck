using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RemoteDeck.Vault;

/// <summary>
/// AES-256-GCM with a fresh random nonce per call. The result is one blob: nonce | tag | ciphertext.
/// The additional data is authenticated but not stored; it ties a blob to its purpose (and entry id),
/// so blobs cannot be swapped between entries without being noticed.
/// </summary>
internal static class SealedBox
{
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int Overhead = NonceSize + TagSize;

    public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        var blob = new byte[Overhead + plaintext.Length];
        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var ciphertext = blob.AsSpan(Overhead);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return blob;
    }

    /// <summary>Throws <see cref="CryptographicException"/> when the key is wrong or anything was changed.</summary>
    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blob, ReadOnlySpan<byte> associatedData)
    {
        if (blob.Length < Overhead)
        {
            throw new CryptographicException("The sealed data is too short.");
        }

        var plaintext = new byte[blob.Length - Overhead];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                blob[..NonceSize],
                blob[Overhead..],
                blob.Slice(NonceSize, TagSize),
                plaintext,
                associatedData);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }
}

/// <summary>
/// Binary layout of one credential before it is sealed:
/// int32 usernameLength | username (UTF-8) | int32 passwordLength | password (UTF-8) | zero padding.
/// The total is padded to a multiple of 256 bytes so the ciphertext length does not reveal the password length.
/// </summary>
internal static class EntryCodec
{
    private const int Block = 256;

    public static byte[] Encode(string? username, ReadOnlySpan<char> password)
    {
        var userBytes = Encoding.UTF8.GetBytes(username ?? string.Empty);
        var passwordLength = Encoding.UTF8.GetByteCount(password);
        var used = 4 + userBytes.Length + 4 + passwordLength;
        var buffer = new byte[(used + Block - 1) / Block * Block];

        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(0, 4), userBytes.Length);
        userBytes.CopyTo(buffer.AsSpan(4));

        var passwordOffset = 4 + userBytes.Length;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(passwordOffset, 4), passwordLength);
        Encoding.UTF8.GetBytes(password, buffer.AsSpan(passwordOffset + 4, passwordLength));
        return buffer;
    }

    /// <summary>The caller owns the returned password bytes and must wipe them.</summary>
    public static (string? Username, byte[] Password) Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            throw new VaultFormatException("A stored credential is too short to be valid.");
        }

        var userLength = BinaryPrimitives.ReadInt32BigEndian(data);
        if (userLength < 0 || userLength > data.Length - 8)
        {
            throw new VaultFormatException("A stored credential has an invalid username length.");
        }

        var passwordOffset = 4 + userLength;
        var passwordLength = BinaryPrimitives.ReadInt32BigEndian(data[passwordOffset..]);
        passwordOffset += 4;
        if (passwordLength < 0 || passwordLength > data.Length - passwordOffset)
        {
            throw new VaultFormatException("A stored credential has an invalid password length.");
        }

        var username = userLength == 0 ? null : Encoding.UTF8.GetString(data.Slice(4, userLength));
        return (username, data.Slice(passwordOffset, passwordLength).ToArray());
    }
}
