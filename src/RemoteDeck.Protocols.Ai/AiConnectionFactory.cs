using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Ai;

/// <summary>Creates AI chat sessions. Connections of type "ai" open as terminals.</summary>
public sealed class AiConnectionFactory : IConnectionFactory
{
    public string Type => "ai";

    public string DisplayName => "AI chat";

    /// <summary>Checks the server address typed in the connection dialog.</summary>
    public static bool TryValidate(string host, int? port, out string error) =>
        ChatProtocol.TryBuildBase(host, port, out _, out error);

    public IConnection Create(ConnectionDefinition definition, ICredentialBroker credentials) =>
        new AiConnection(definition, credentials, null);
}
