using System.Security.Cryptography;
using Konscious.Security.Cryptography;

namespace RemoteDeck.Vault;

/// <summary>Argon2id cost settings. They are stored in the vault file so they can be raised later.</summary>
/// <param name="MemoryKiB">Memory in KiB. The default is 64 MiB.</param>
/// <param name="Iterations">Passes over that memory.</param>
/// <param name="Parallelism">Lanes (threads).</param>
public sealed record KdfParameters(int MemoryKiB = 65536, int Iterations = 3, int Parallelism = 4)
{
    /// <summary>Upper bound so a hostile vault file cannot make the app allocate gigabytes.</summary>
    public const int MaxMemoryKiB = 1_048_576;

    public static KdfParameters Default { get; } = new();

    /// <summary>Returns a readable reason when the settings are unusable, otherwise null.</summary>
    public string? Problem()
    {
        if (Parallelism is < 1 or > 16)
        {
            return "Parallelism must be between 1 and 16.";
        }

        if (Iterations is < 1 or > 64)
        {
            return "Iterations must be between 1 and 64.";
        }

        if (MemoryKiB < 8 * Parallelism || MemoryKiB > MaxMemoryKiB)
        {
            return $"Memory must be between {8 * Parallelism} and {MaxMemoryKiB} KiB.";
        }

        return null;
    }
}

/// <summary>Turns a password into a 32-byte key. Kept behind an interface so it can be swapped or replaced in tests.</summary>
public interface IKeyDerivation
{
    /// <summary>Name written into the vault file, e.g. "argon2id".</summary>
    string Algorithm { get; }

    byte[] DeriveKey(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, KdfParameters parameters);
}

public sealed class Argon2idKeyDerivation : IKeyDerivation
{
    public const int KeySize = 32;

    public string Algorithm => "argon2id";

    public byte[] DeriveKey(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, KdfParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        // The library wants arrays it can hold on to; wipe our copy as soon as we are done.
        var passwordCopy = password.ToArray();
        var argon2 = new Argon2id(passwordCopy)
        {
            Salt = salt.ToArray(),
            DegreeOfParallelism = parameters.Parallelism,
            MemorySize = parameters.MemoryKiB,
            Iterations = parameters.Iterations,
        };

        try
        {
            return argon2.GetBytes(KeySize);
        }
        finally
        {
            if ((object)argon2 is IDisposable disposable)
            {
                disposable.Dispose();
            }

            CryptographicOperations.ZeroMemory(passwordCopy);
        }
    }
}
