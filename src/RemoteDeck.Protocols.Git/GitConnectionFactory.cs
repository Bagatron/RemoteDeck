using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Git;

/// <summary>Creates git terminals. Connections of type "git" open a local shell in the repository folder given as the host.</summary>
public sealed class GitConnectionFactory : IConnectionFactory
{
    public string Type => "git";

    public string DisplayName => "Git terminal";

    /// <summary>Returns an <see cref="ITerminalConnection"/> that has not started yet. Git terminals use no saved credentials.</summary>
    public IConnection Create(ConnectionDefinition definition, ICredentialBroker credentials) =>
        new GitConnection(definition, new ConPtyShellLinkFactory(), ShellLocator.ForThisComputer(), Directory.Exists);

    /// <summary>The shell names a connection's <c>shell</c> option accepts besides a full program path.</summary>
    public static IReadOnlyList<string> KnownShells => GitSettings.KnownShells;

    /// <summary>Checks the saved settings without starting anything; throws <see cref="GitShellException"/> when they are wrong.</summary>
    public static void Validate(string folder, IReadOnlyDictionary<string, string>? options) =>
        _ = GitSettings.From(folder, options);
}
