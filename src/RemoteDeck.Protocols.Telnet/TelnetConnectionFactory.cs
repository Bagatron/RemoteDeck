using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Telnet;

/// <summary>Creates Telnet sessions. Connections of type "telnet" open as terminals.</summary>
public sealed class TelnetConnectionFactory : IConnectionFactory
{
    public string Type => "telnet";

    public string DisplayName => "Telnet";

    /// <summary>Returns an <see cref="ITerminalConnection"/> that has not connected yet. Telnet uses no saved credentials.</summary>
    public IConnection Create(ConnectionDefinition definition, ICredentialBroker credentials) =>
        new TelnetConnection(definition);
}
