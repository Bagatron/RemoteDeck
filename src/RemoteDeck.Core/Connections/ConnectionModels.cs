using RemoteDeck.Plugin;

namespace RemoteDeck.Core.Connections;

public sealed class CatalogException : Exception
{
    public CatalogException(string message)
        : base(message)
    {
    }

    public CatalogException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A folder in the connection tree. A credential set on a folder is inherited by everything below it.</summary>
/// <param name="ParentId">Null for a top-level folder.</param>
/// <param name="CredentialId">A credential id in the vault. Never a password.</param>
/// <param name="Color">Optional #RRGGBB accent shown on the folder.</param>
public sealed record FolderEntry(
    string Id,
    string Name,
    string? ParentId = null,
    string? CredentialId = null,
    string? Color = null);

/// <summary>
/// A saved connection. It holds a reference to a credential, never the secret itself.
/// </summary>
/// <param name="Type">Connection type id such as "ssh" or "rdp"; stored in lower case.</param>
/// <param name="FolderId">Null for a top-level connection.</param>
/// <param name="CredentialId">Overrides the credential inherited from the folder.</param>
/// <param name="Options">Type-specific settings, e.g. the terminal font or RDP color depth.</param>
/// <param name="Color">Optional #RRGGBB accent, e.g. red for production.</param>
public sealed record ConnectionEntry(
    string Id,
    string Name,
    string Type,
    string Host,
    int? Port = null,
    string? FolderId = null,
    string? CredentialId = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyDictionary<string, string>? Options = null,
    bool Favorite = false,
    string? Color = null,
    string? Icon = null,
    string? Notes = null)
{
    /// <summary>The plugin-facing view of this connection, with the credential that actually applies.</summary>
    public ConnectionDefinition ToDefinition(string? effectiveCredentialId) =>
        new(Id, Name, Type, Host, Port, effectiveCredentialId, Options, Tags);
}

/// <summary>A node of the folder tree shown in the sidebar.</summary>
public abstract record CatalogNode;

public sealed record FolderNode(FolderEntry Folder, IReadOnlyList<CatalogNode> Children) : CatalogNode;

public sealed record ConnectionNode(ConnectionEntry Connection) : CatalogNode;

/// <param name="FolderPath">For display, e.g. "Prod / Web". Empty for top-level connections.</param>
/// <param name="Score">Higher is a better match.</param>
public sealed record SearchResult(ConnectionEntry Connection, string FolderPath, int Score);
