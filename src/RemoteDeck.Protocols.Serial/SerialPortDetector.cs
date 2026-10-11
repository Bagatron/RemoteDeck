using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RemoteDeck.Protocols.Serial;

/// <summary>A serial port that is present right now, with the name Windows gives the device behind it.</summary>
/// <param name="Name">The port name, for example COM3.</param>
/// <param name="Description">What it is, for example "USB Serial Port" or "Prolific USB-to-Serial Comm Port". Empty when Windows has no name for it.</param>
public sealed record SerialPortInfo(string Name, string Description)
{
    /// <summary>What to show in a list: "COM3 - USB Serial Port".</summary>
    public string Label => Description.Length == 0 ? Name : $"{Name} - {Description}";
}

/// <summary>Finds the serial ports that are plugged in and says what each one is.</summary>
public static class SerialPortDetector
{
    private static readonly Regex PortInBrackets = new(@"\s*\((COM\d+)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The ports present now, in order (COM2 before COM10), each with its device name when Windows knows one.</summary>
    public static IReadOnlyList<SerialPortInfo> Detect() =>
        Combine(SerialConnectionFactory.AvailablePorts(), ReadDescriptions());

    /// <summary>Puts port names in a natural order: COM2 before COM10, anything that is not COMn last.</summary>
    internal static IEnumerable<string> InOrder(IEnumerable<string> names) =>
        names
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(PortNumber)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<SerialPortInfo> Combine(IEnumerable<string> names, IReadOnlyDictionary<string, string> descriptions) =>
        InOrder(names)
            .Select(name => new SerialPortInfo(name, descriptions.TryGetValue(name, out var text) ? text : string.Empty))
            .ToList();

    private static int PortNumber(string name) =>
        name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(3), out var number)
            ? number
            : int.MaxValue;

    /// <summary>The port named in a device name such as "USB Serial Port (COM3)", or null when it names none.</summary>
    internal static string? PortOf(string friendlyName)
    {
        var match = PortInBrackets.Match(friendlyName);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>A device name without the port in brackets: "USB Serial Port (COM3)" becomes "USB Serial Port".</summary>
    internal static string Describe(string friendlyName) => PortInBrackets.Replace(friendlyName, string.Empty).Trim();

    /// <summary>
    /// Windows stores a driver's text as "@oem12.inf,%name%;USB Serial Converter"; the readable part follows the last semicolon.
    /// </summary>
    internal static string ReadableDeviceText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var semicolon = text.LastIndexOf(';');
        return (semicolon >= 0 ? text[(semicolon + 1)..] : text).Trim();
    }

    /// <summary>
    /// Reads device names from the registry (HKLM\SYSTEM\CurrentControlSet\Enum), where Windows keeps one entry per
    /// device it has ever seen. When an old, unplugged device used the same COM number, the one that is running now wins.
    /// </summary>
    private static Dictionary<string, string> ReadDescriptions()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
        {
            return result;
        }

        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
            if (root is null)
            {
                return result;
            }

            foreach (var bus in root.GetSubKeyNames())
            {
                using var busKey = root.OpenSubKey(bus);
                foreach (var device in busKey?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var deviceKey = busKey!.OpenSubKey(device);
                    foreach (var instance in deviceKey?.GetSubKeyNames() ?? Array.Empty<string>())
                    {
                        using var instanceKey = deviceKey!.OpenSubKey(instance);
                        if (instanceKey is null)
                        {
                            continue;
                        }

                        var friendly = instanceKey.GetValue("FriendlyName") as string;
                        var port = friendly is null ? null : PortOf(friendly);
                        var text = friendly is null ? string.Empty : Describe(friendly);
                        if (port is null)
                        {
                            using var parameters = instanceKey.OpenSubKey("Device Parameters");
                            var named = parameters?.GetValue("PortName") as string;
                            if (named is null || !named.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            port = named.ToUpperInvariant();
                            text = friendly ?? ReadableDeviceText(instanceKey.GetValue("DeviceDesc") as string);
                        }

                        // A device that is running has a "Control" key; one that was unplugged long ago does not.
                        var running = instanceKey.GetSubKeyNames().Contains("Control", StringComparer.OrdinalIgnoreCase);
                        if (text.Length == 0 || (result.ContainsKey(port) && (live.Contains(port) || !running)))
                        {
                            continue;
                        }

                        result[port] = text;
                        if (running)
                        {
                            live.Add(port);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Without the names the ports are still listed.
        }

        return result;
    }
}
