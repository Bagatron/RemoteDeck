namespace RemoteDeck.Core;

/// <summary>Formats the version shown next to the app name.</summary>
public static class AppVersion
{
    /// <summary>"0.5.0+abc123" becomes "v0.5.0"; a pre-release suffix is kept ("v0.5.0-beta.1"); nothing gives an empty string.</summary>
    public static string Label(string? informationalVersion)
    {
        var text = (informationalVersion ?? string.Empty).Trim();
        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        text = text.TrimStart('v', 'V');
        return text.Length == 0 ? string.Empty : "v" + text;
    }
}
