namespace RemoteDeck.Core.Themes;

/// <summary>The themes that always exist, and the defaults that fill the gaps in theme files.</summary>
public static class BuiltInThemes
{
    public static IReadOnlyList<string> DarkAnsi { get; } = new[]
    {
        "#3B4252", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#88C0D0", "#E5E9F0",
        "#4C566A", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#8FBCBB", "#ECEFF4",
    };

    public static IReadOnlyList<string> LightAnsi { get; } = new[]
    {
        "#24292F", "#CF222E", "#116329", "#4D2D00", "#0969DA", "#8250DF", "#1B7C83", "#6E7781",
        "#57606A", "#A40E26", "#1A7F37", "#633C01", "#218BFF", "#A475F9", "#3192AA", "#8C959F",
    };

    public static Theme Dark { get; } = new(
        "RemoteDeck Dark",
        "RemoteDeck contributors",
        ThemeBase.Dark,
        "mica",
        new ThemeColors(
            Background: "#2E3440",
            Surface: "#3B4252",
            SurfaceAlt: "#434C5E",
            Foreground: "#ECEFF4",
            MutedForeground: "#8F9BB3",
            Accent: "#88C0D0",
            AccentForeground: "#2E3440",
            Border: "#4C566A",
            Success: "#A3BE8C",
            Warning: "#EBCB8B",
            Danger: "#BF616A",
            Broadcast: "#D08770"),
        new TerminalColors("#2E3440", "#D8DEE9", "#D8DEE9", "#434C5E", DarkAnsi),
        DefaultShape,
        DefaultFonts);

    public static Theme Light { get; } = new(
        "RemoteDeck Light",
        "RemoteDeck contributors",
        ThemeBase.Light,
        "mica",
        new ThemeColors(
            Background: "#FAFAFB",
            Surface: "#EDEEF1",
            SurfaceAlt: "#E1E3E8",
            Foreground: "#1F2430",
            MutedForeground: "#5B6272",
            Accent: "#2F6FDE",
            AccentForeground: "#FFFFFF",
            Border: "#C9CCD4",
            Success: "#2E8B57",
            Warning: "#B7791F",
            Danger: "#C0392B",
            Broadcast: "#D9480F"),
        new TerminalColors("#FFFFFF", "#24292F", "#24292F", "#BBD6FB", LightAnsi),
        DefaultShape,
        DefaultFonts);

    public static IReadOnlyList<Theme> All { get; } = new[] { Dark, Light };

    public static ThemeShape DefaultShape => new(8, 1, "comfortable");

    public static ThemeFonts DefaultFonts => new("Segoe UI Variable", "Cascadia Code", 13);

    public static IReadOnlyList<string> AnsiFor(ThemeBase themeBase) =>
        themeBase == ThemeBase.Light ? LightAnsi : DarkAnsi;
}
