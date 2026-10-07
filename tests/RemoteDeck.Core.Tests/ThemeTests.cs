using System.Text.RegularExpressions;
using RemoteDeck.Core.Themes;

namespace RemoteDeck.Core.Tests;

public class ThemeTests : IDisposable
{
    private const string Minimal = """
        {
          "name": "Tiny",
          "base": "dark",
          "colors": {
            "background": "#101010",
            "surface": "#202020",
            "foreground": "#f0f0f0",
            "mutedForeground": "#c0c0c0",
            "accent": "#00aaff",
            "accentForeground": "#000000",
            "border": "#303030",
            "success": "#00ff00",
            "warning": "#ffff00",
            "danger": "#ff0000",
            "broadcast": "#ff8800"
          }
        }
        """;

    private static readonly Regex Hex = new("^#[0-9A-F]{6}([0-9A-F]{2})?$");

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "remotedeck-theme-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void MinimalTheme_GetsDefaultsForEverythingElse()
    {
        var theme = ThemeLoader.Parse(Minimal);

        Assert.Equal("Tiny", theme.Name);
        Assert.Equal(ThemeBase.Dark, theme.Base);
        Assert.Equal("mica", theme.Backdrop);
        Assert.Equal("#101010".ToUpperInvariant(), theme.Colors.Background);
        Assert.Equal(theme.Colors.Background, theme.Terminal.Background);
        Assert.Equal(theme.Colors.Foreground, theme.Terminal.Foreground);
        Assert.Equal(theme.Colors.Foreground, theme.Terminal.Cursor);
        Assert.Equal(16, theme.Terminal.Ansi.Count);
        Assert.Equal(BuiltInThemes.DarkAnsi, theme.Terminal.Ansi);
        Assert.Equal(8, theme.Shape.CornerRadius);
        Assert.Equal("comfortable", theme.Shape.Density);
        Assert.Equal("Cascadia Code", theme.Font.Mono);
        Assert.Equal(13, theme.Font.Size);
    }

    [Fact]
    public void LightBase_UsesTheLightAnsiPalette()
    {
        var theme = ThemeLoader.Parse(Minimal.Replace("\"dark\"", "\"light\""));

        Assert.Equal(BuiltInThemes.LightAnsi, theme.Terminal.Ansi);
    }

    [Fact]
    public void Colors_AreNormalizedToUpperCase()
    {
        var theme = ThemeLoader.Parse(Minimal);

        Assert.Equal("#00AAFF", theme.Colors.Accent);
        Assert.Equal("#F0F0F0", theme.Colors.Foreground);
    }

    [Fact]
    public void MissingSurfaceAlt_IsBlendedFromSurfaceAndForeground()
    {
        var theme = ThemeLoader.Parse(Minimal);

        // 12% of the way from #202020 (32) to #F0F0F0 (240): 32 + 0.12 * 208 = 56.96 -> 0x39.
        Assert.Equal("#393939", theme.Colors.SurfaceAlt);
    }

    [Fact]
    public void GivenSurfaceAlt_IsKept()
    {
        var theme = ThemeLoader.Parse(Minimal.Replace("\"surface\": \"#202020\",", "\"surface\": \"#202020\", \"surfaceAlt\": \"#123456\","));

        Assert.Equal("#123456", theme.Colors.SurfaceAlt);
    }

    [Fact]
    public void AlphaColors_AreAccepted()
    {
        var theme = ThemeLoader.Parse(Minimal.Replace("#00aaff", "#00aaff80"));

        Assert.Equal("#00AAFF80", theme.Colors.Accent);
    }

    [Fact]
    public void CommentsAndTrailingCommas_AreAllowed()
    {
        var json = Minimal.Replace("\"base\": \"dark\",", "// a comment\n  \"base\": \"dark\",").Replace("\"broadcast\": \"#ff8800\"", "\"broadcast\": \"#ff8800\",");

        Assert.Equal("Tiny", ThemeLoader.Parse(json).Name);
    }

    [Fact]
    public void ExplicitTerminalShapeAndFont_AreRead()
    {
        var json = Minimal.TrimEnd().TrimEnd('}') + """
            ,
            "backdrop": "none",
            "terminal": { "cursor": "#ffffff", "ansi": ["#000000","#111111","#222222","#333333","#444444","#555555","#666666","#777777","#888888","#999999","#aaaaaa","#bbbbbb","#cccccc","#dddddd","#eeeeee","#ffffff"] },
            "shape": { "cornerRadius": 0, "borderWidth": 2, "density": "compact" },
            "font": { "ui": "Arial", "mono": "Consolas", "size": 16 }
          }
        """;

        var theme = ThemeLoader.Parse(json);

        Assert.Equal("none", theme.Backdrop);
        Assert.Equal("#FFFFFF", theme.Terminal.Cursor);
        Assert.Equal("#000000", theme.Terminal.Ansi[0]);
        Assert.Equal("#FFFFFF", theme.Terminal.Ansi[15]);
        Assert.Equal(0, theme.Shape.CornerRadius);
        Assert.Equal(2, theme.Shape.BorderWidth);
        Assert.Equal("compact", theme.Shape.Density);
        Assert.Equal("Arial", theme.Font.Ui);
        Assert.Equal("Consolas", theme.Font.Mono);
        Assert.Equal(16, theme.Font.Size);
    }

    [Theory]
    [InlineData("not json at all", "valid JSON")]
    [InlineData("[]", "JSON object")]
    [InlineData("{}", "'name' is missing")]
    public void BrokenFiles_AreRejectedWithAReason(string json, string expected)
    {
        var error = Assert.Throws<ThemeException>(() => ThemeLoader.Parse(json));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void MissingRequiredColor_NamesIt()
    {
        var error = Assert.Throws<ThemeException>(() => ThemeLoader.Parse(Minimal.Replace("\"danger\": \"#ff0000\",", string.Empty)));

        Assert.Contains("colors.danger", error.Message);
    }

    [Theory]
    [InlineData("#00aaff", "red")]
    [InlineData("#00aaff", "#0af")]
    [InlineData("#00aaff", "#gggggg")]
    [InlineData("#00aaff", "#00aaff1")]
    public void BadColors_NameTheField(string from, string to)
    {
        var error = Assert.Throws<ThemeException>(() => ThemeLoader.Parse(Minimal.Replace(from, to)));

        Assert.Contains("colors.accent", error.Message);
    }

    [Fact]
    public void MisspelledField_IsReportedInsteadOfIgnored()
    {
        var error = Assert.Throws<ThemeException>(() => ThemeLoader.Parse(Minimal.Replace("\"colors\"", "\"colours\"")));

        Assert.Contains("colours", error.Message);
    }

    [Fact]
    public void UnknownColorKey_IsReported()
    {
        var error = Assert.Throws<ThemeException>(() => ThemeLoader.Parse(Minimal.Replace("\"accent\"", "\"acent\"")));

        Assert.Contains("colors.acent", error.Message);
    }

    [Theory]
    [InlineData("\"base\": \"dark\"", "\"base\": \"blue\"", "base")]
    [InlineData("\"base\": \"dark\"", "\"base\": \"dark\", \"backdrop\": \"glass\"", "backdrop")]
    public void BadChoices_NameTheField(string from, string to, string field)
    {
        var error = Assert.Throws<ThemeException>(() => ThemeLoader.Parse(Minimal.Replace(from, to)));

        Assert.Contains(field, error.Message);
    }

    [Theory]
    [InlineData("\"shape\": { \"cornerRadius\": 99 }", "shape.cornerRadius")]
    [InlineData("\"shape\": { \"borderWidth\": -1 }", "shape.borderWidth")]
    [InlineData("\"shape\": { \"density\": \"tight\" }", "shape.density")]
    [InlineData("\"font\": { \"size\": 2 }", "font.size")]
    [InlineData("\"font\": { \"size\": \"big\" }", "font.size")]
    [InlineData("\"terminal\": { \"ansi\": [\"#000000\"] }", "terminal.ansi")]
    [InlineData("\"terminal\": { \"ansi\": 3 }", "terminal.ansi")]
    public void BadSections_NameTheField(string section, string field)
    {
        var json = Minimal.TrimEnd().TrimEnd('}') + ", " + section + " }";

        var error = Assert.Throws<ThemeException>(() => ThemeLoader.Parse(json));

        Assert.Contains(field, error.Message);
    }

    [Fact]
    public void BuiltInThemes_AreWellFormed()
    {
        foreach (var theme in BuiltInThemes.All)
        {
            var colors = new[]
            {
                theme.Colors.Background, theme.Colors.Surface, theme.Colors.SurfaceAlt, theme.Colors.Foreground,
                theme.Colors.MutedForeground, theme.Colors.Accent, theme.Colors.AccentForeground, theme.Colors.Border,
                theme.Colors.Success, theme.Colors.Warning, theme.Colors.Danger, theme.Colors.Broadcast,
                theme.Terminal.Background, theme.Terminal.Foreground, theme.Terminal.Cursor, theme.Terminal.Selection,
            }.Concat(theme.Terminal.Ansi);

            Assert.All(colors, c => Assert.Matches(Hex, c));
            Assert.Equal(16, theme.Terminal.Ansi.Count);
        }

        Assert.Equal(2, BuiltInThemes.All.Select(t => t.Name).Distinct().Count());
    }

    [Fact]
    public void ShippedThemeFiles_AllLoad()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "themes");

        var files = ThemeLoader.LoadFolder(folder);

        Assert.NotEmpty(files);
        Assert.All(files, f => Assert.True(f.Theme is not null, $"{f.Path}: {f.Error}"));
        Assert.Contains(files, f => f.Theme!.Name == "Nord");
        Assert.Contains(files, f => f.Theme!.Name == "Dracula");
    }

    [Fact]
    public void LoadFolder_KeepsGoodFiles_AndReportsBrokenOnes()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "a-good.json"), Minimal);
        File.WriteAllText(Path.Combine(_folder, "b-broken.json"), "{ nope");
        File.WriteAllText(Path.Combine(_folder, "c-ignored.txt"), "not a theme");

        var files = ThemeLoader.LoadFolder(_folder);

        Assert.Equal(2, files.Count);
        Assert.Equal("Tiny", files[0].Theme!.Name);
        Assert.Null(files[0].Error);
        Assert.Null(files[1].Theme);
        Assert.Contains("valid JSON", files[1].Error);
    }

    [Fact]
    public void LoadFolder_OfAMissingFolder_IsEmpty()
    {
        Assert.Empty(ThemeLoader.LoadFolder(_folder));
    }
}
