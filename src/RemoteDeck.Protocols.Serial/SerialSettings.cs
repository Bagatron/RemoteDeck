using System.IO.Ports;
using System.Text.RegularExpressions;
using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Serial;

/// <summary>A problem with a serial connection's settings, or opening the port. The message is safe to show to the user.</summary>
public sealed class SerialConnectionException : Exception
{
    public SerialConnectionException(string message)
        : base(message)
    {
    }

    public SerialConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The settings of a serial connection, read from the connection's <c>Host</c> (the port name, for example COM3) and
/// <c>Options</c>: <c>baud</c> (default 9600), <c>format</c> (data bits, parity and stop bits as in "8N1", the default),
/// <c>flow</c> ("none", "xonxoff" or "rtscts"; default none) and <c>translateLf</c> ("false" to show line feeds as they
/// are; by default a bare line feed from the device starts a new line at the left edge).
/// </summary>
internal sealed record SerialSettings(
    string PortName,
    int BaudRate,
    int DataBits,
    Parity Parity,
    StopBits StopBits,
    Handshake Handshake,
    bool TranslateLf)
{
    private static readonly Regex FormatPattern = new("^([5-8])([NEOMS])(1|1\\.5|2)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static SerialSettings From(ConnectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return From(definition.Host, definition.Options);
    }

    public static SerialSettings From(string portName, IReadOnlyDictionary<string, string>? options)
    {
        var name = (portName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new SerialConnectionException("Enter the name of the serial port, for example COM3.");
        }

        string Get(string key) =>
            options is not null && options.TryGetValue(key, out var value) ? value.Trim() : string.Empty;

        var baud = 9600;
        var baudText = Get("baud");
        if (baudText.Length > 0 && (!int.TryParse(baudText, out baud) || baud is < 50 or > 4_000_000))
        {
            throw new SerialConnectionException($"The baud rate \"{baudText}\" is not valid. Use a number such as 9600 or 115200.");
        }

        var formatText = Get("format");
        var dataBits = 8;
        var parity = Parity.None;
        var stopBits = StopBits.One;
        if (formatText.Length > 0)
        {
            var match = FormatPattern.Match(formatText);
            if (!match.Success)
            {
                throw new SerialConnectionException(
                    $"The format \"{formatText}\" is not valid. Use data bits, parity and stop bits such as 8N1, 7E1 or 8N2.");
            }

            dataBits = int.Parse(match.Groups[1].Value);
            parity = char.ToUpperInvariant(match.Groups[2].Value[0]) switch
            {
                'E' => Parity.Even,
                'O' => Parity.Odd,
                'M' => Parity.Mark,
                'S' => Parity.Space,
                _ => Parity.None,
            };
            stopBits = match.Groups[3].Value switch
            {
                "2" => StopBits.Two,
                "1.5" => StopBits.OnePointFive,
                _ => StopBits.One,
            };
        }

        var flowText = Get("flow").ToLowerInvariant();
        var handshake = flowText switch
        {
            "" or "none" => Handshake.None,
            "xonxoff" => Handshake.XOnXOff,
            "rtscts" => Handshake.RequestToSend,
            _ => throw new SerialConnectionException($"The flow control \"{flowText}\" is not valid. Use none, xonxoff or rtscts."),
        };

        var translate = !string.Equals(Get("translateLf"), "false", StringComparison.OrdinalIgnoreCase);
        return new SerialSettings(name, baud, dataBits, parity, stopBits, handshake, translate);
    }
}

/// <summary>Checks serial settings so mistakes show up when saving, with the same rules the connection applies later.</summary>
public static class SerialSettingsCheck
{
    /// <summary>Throws <see cref="SerialConnectionException"/> with a readable message when the port name or options are not usable.</summary>
    public static void Validate(string portName, IReadOnlyDictionary<string, string>? options) =>
        SerialSettings.From(portName, options);
}
