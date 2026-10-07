namespace RemoteDeck.Core.Themes;

public enum ThemeBase
{
    Dark,
    Light,
}

/// <summary>Interface colors. All are "#RRGGBB" or "#RRGGBBAA" in upper case.</summary>
public sealed record ThemeColors(
    string Background,
    string Surface,
    string SurfaceAlt,
    string Foreground,
    string MutedForeground,
    string Accent,
    string AccentForeground,
    string Border,
    string Success,
    string Warning,
    string Danger,
    string Broadcast);

/// <param name="Ansi">Always exactly 16 colors: black, red, green, yellow, blue, magenta, cyan, white, then the bright ones.</param>
public sealed record TerminalColors(
    string Background,
    string Foreground,
    string Cursor,
    string Selection,
    IReadOnlyList<string> Ansi);

public sealed record ThemeShape(double CornerRadius, double BorderWidth, string Density);

public sealed record ThemeFonts(string Ui, string Mono, double Size);

/// <summary>A fully resolved theme: anything the file left out has been filled in from the built-in theme of its base.</summary>
public sealed record Theme(
    string Name,
    string? Author,
    ThemeBase Base,
    string Backdrop,
    ThemeColors Colors,
    TerminalColors Terminal,
    ThemeShape Shape,
    ThemeFonts Font);

/// <summary>A theme file that is not valid. The message says which field is wrong.</summary>
public sealed class ThemeException : Exception
{
    public ThemeException(string message)
        : base(message)
    {
    }

    public ThemeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
