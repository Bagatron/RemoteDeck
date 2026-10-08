using System.IO.Ports;
using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Serial;

/// <summary>Creates serial sessions. Connections of type "serial" open as terminals; the host is the port name, for example COM3.</summary>
public sealed class SerialConnectionFactory : IConnectionFactory
{
    public string Type => "serial";

    public string DisplayName => "Serial port";

    /// <summary>Returns an <see cref="ITerminalConnection"/> that has not connected yet. Serial uses no saved credentials.</summary>
    public IConnection Create(ConnectionDefinition definition, ICredentialBroker credentials) =>
        new SerialConnection(definition, new SystemSerialLinkFactory());

    /// <summary>The serial ports this computer has right now (COM3, COM4, ...), in a natural order.</summary>
    public static IReadOnlyList<string> AvailablePorts()
    {
        try
        {
            return SerialPort.GetPortNames()
                .OrderBy(name => name.Length)
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return Array.Empty<string>();
        }
    }
}
