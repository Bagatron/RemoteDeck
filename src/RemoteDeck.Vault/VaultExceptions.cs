namespace RemoteDeck.Vault;

/// <summary>Base type for everything the vault can refuse to do.</summary>
public class VaultException : Exception
{
    public VaultException(string message)
        : base(message)
    {
    }

    public VaultException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The master password did not unlock the vault. A wrong password and a damaged key block look
/// identical on purpose, so the vault never reveals which one it was.
/// </summary>
public sealed class InvalidMasterPasswordException : VaultException
{
    public InvalidMasterPasswordException()
        : base("Wrong master password, or the vault file is damaged.")
    {
    }
}

public sealed class VaultLockedException : VaultException
{
    public VaultLockedException()
        : base("The vault is locked. Unlock it with the master password first.")
    {
    }
}

/// <summary>The vault file is malformed, unsupported, or has been modified.</summary>
public sealed class VaultFormatException : VaultException
{
    public VaultFormatException(string message)
        : base(message)
    {
    }

    public VaultFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
