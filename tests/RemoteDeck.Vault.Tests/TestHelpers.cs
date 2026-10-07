using System.Security.Cryptography;

namespace RemoteDeck.Vault.Tests;

/// <summary>
/// A near-instant stand-in for Argon2id so the many vault tests stay fast.
/// The real Argon2id is covered separately in <see cref="Argon2Tests"/>.
/// </summary>
internal sealed class FastKeyDerivation : IKeyDerivation
{
    public string Algorithm => "test-fast";

    public byte[] DeriveKey(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, KdfParameters parameters)
    {
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, 1, HashAlgorithmName.SHA256, 32);
    }
}
