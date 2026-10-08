using System.Text.RegularExpressions;

namespace RemoteDeck.Core.Connections;

/// <summary>Turns what a person types for a web connection ("pve.lan", "pve.lan:8006", "http://router/admin") into a full address.</summary>
public static class WebAddress
{
    private static readonly Regex ExplicitPort = new(@"^[A-Za-z][A-Za-z0-9+.\-]*://[^/?#]*:\d+(?:[/?#]|$)", RegexOptions.CultureInvariant);

    /// <summary>
    /// Adds https:// when no scheme is given and applies <paramref name="port"/> unless the address already has one.
    /// Only http and https are accepted. Returns false, with a reason, otherwise.
    /// </summary>
    public static bool TryNormalize(string? text, int? port, out Uri address, out string error)
    {
        address = null!;
        error = string.Empty;

        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            error = "This web connection has no address.";
            return false;
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            error = $"'{text}' is not a valid web address.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = $"Only http and https addresses can be opened, not '{uri.Scheme}'.";
            return false;
        }

        if (port is { } p && !ExplicitPort.IsMatch(trimmed))
        {
            if (p is < 1 or > 65535)
            {
                error = $"Port {p} is out of range (1-65535).";
                return false;
            }

            uri = new UriBuilder(uri) { Port = p }.Uri;
        }

        address = uri;
        return true;
    }
}
