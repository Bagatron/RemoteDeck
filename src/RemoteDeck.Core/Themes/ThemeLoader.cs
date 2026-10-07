using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemoteDeck.Core.Themes;

/// <summary>A theme file on disk: either a usable theme or the reason it could not be read.</summary>
public sealed record ThemeFile(string Path, Theme? Theme, string? Error);

/// <summary>
/// Reads theme files (see schemas/theme.schema.json). Strict on purpose: an unknown or misspelled field is an
/// error with its name in the message, so a typo does not silently do nothing while you hot-reload.
/// </summary>
public static class ThemeLoader
{
    private static readonly Regex HexColor = new("^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$", RegexOptions.CultureInvariant);

    private static readonly string[] RequiredColors =
    {
        "background", "surface", "foreground", "mutedForeground", "accent", "accentForeground",
        "border", "success", "warning", "danger", "broadcast",
    };

    private static readonly string[] Backdrops = { "none", "mica", "acrylic", "tabbed" };
    private static readonly string[] Densities = { "compact", "comfortable", "spacious" };

    public static Theme Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            throw new ThemeException("The file is not valid JSON: " + ex.Message, ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ThemeException("A theme must be a JSON object.");
            }

            RejectUnknown(root, string.Empty, "$schema", "name", "author", "base", "backdrop", "colors", "terminal", "shape", "font");

            var name = Text(root, "name", required: true)!;
            if (name.Length > 64)
            {
                throw new ThemeException("name must be at most 64 characters.");
            }

            var author = Text(root, "author", required: false);
            var themeBase = ReadBase(root);
            var backdrop = OneOf(Text(root, "backdrop", required: false) ?? "mica", "backdrop", Backdrops);

            var colors = ReadColors(Section(root, "colors", required: true)!.Value);
            var terminal = ReadTerminal(Section(root, "terminal", required: false), colors, themeBase);
            var shape = ReadShape(Section(root, "shape", required: false));
            var font = ReadFont(Section(root, "font", required: false));

            return new Theme(name, author, themeBase, backdrop, colors, terminal, shape, font);
        }
    }

    public static Theme Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Reads every <c>*.json</c> in a folder. A broken file does not hide the good ones; it comes back with its
    /// error. A missing folder is simply empty.
    /// </summary>
    public static IReadOnlyList<ThemeFile> LoadFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        if (!Directory.Exists(folder))
        {
            return Array.Empty<ThemeFile>();
        }

        var results = new List<ThemeFile>();
        foreach (var path in Directory.GetFiles(folder, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                results.Add(new ThemeFile(path, Load(path), null));
            }
            catch (Exception ex) when (ex is ThemeException or IOException or UnauthorizedAccessException)
            {
                results.Add(new ThemeFile(path, null, ex.Message));
            }
        }

        return results;
    }

    private static ThemeBase ReadBase(JsonElement root)
    {
        var text = Text(root, "base", required: true)!;
        return OneOf(text, "base", "dark", "light") == "light" ? ThemeBase.Light : ThemeBase.Dark;
    }

    private static ThemeColors ReadColors(JsonElement element)
    {
        var known = new List<string>(RequiredColors) { "surfaceAlt" };
        RejectUnknown(element, "colors.", known.ToArray());

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            map[property.Name] = Color("colors." + property.Name, property.Value);
        }

        foreach (var key in RequiredColors)
        {
            if (!map.ContainsKey(key))
            {
                throw new ThemeException($"colors.{key} is missing.");
            }
        }

        var surfaceAlt = map.TryGetValue("surfaceAlt", out var alt)
            ? alt
            : ColorMath.Mix(map["surface"], map["foreground"], 0.12);

        return new ThemeColors(
            map["background"],
            map["surface"],
            surfaceAlt,
            map["foreground"],
            map["mutedForeground"],
            map["accent"],
            map["accentForeground"],
            map["border"],
            map["success"],
            map["warning"],
            map["danger"],
            map["broadcast"]);
    }

    private static TerminalColors ReadTerminal(JsonElement? element, ThemeColors colors, ThemeBase themeBase)
    {
        var background = colors.Background;
        var foreground = colors.Foreground;
        var cursor = colors.Foreground;
        var selection = colors.SurfaceAlt;
        IReadOnlyList<string> ansi = BuiltInThemes.AnsiFor(themeBase);

        if (element is { } terminal)
        {
            RejectUnknown(terminal, "terminal.", "background", "foreground", "cursor", "selection", "ansi");
            background = OptionalColor(terminal, "terminal.background", "background") ?? background;
            foreground = OptionalColor(terminal, "terminal.foreground", "foreground") ?? foreground;
            cursor = OptionalColor(terminal, "terminal.cursor", "cursor") ?? cursor;
            selection = OptionalColor(terminal, "terminal.selection", "selection") ?? selection;

            if (terminal.TryGetProperty("ansi", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() != 16)
                {
                    throw new ThemeException("terminal.ansi must list exactly 16 colors.");
                }

                var parsed = new List<string>(16);
                var index = 0;
                foreach (var item in list.EnumerateArray())
                {
                    parsed.Add(Color($"terminal.ansi[{index++}]", item));
                }

                ansi = parsed;
            }
        }

        return new TerminalColors(background, foreground, cursor, selection, ansi);
    }

    private static ThemeShape ReadShape(JsonElement? element)
    {
        var defaults = BuiltInThemes.DefaultShape;
        if (element is not { } shape)
        {
            return defaults;
        }

        RejectUnknown(shape, "shape.", "cornerRadius", "borderWidth", "density");
        return new ThemeShape(
            Number(shape, "shape.cornerRadius", "cornerRadius", 0, 24) ?? defaults.CornerRadius,
            Number(shape, "shape.borderWidth", "borderWidth", 0, 4) ?? defaults.BorderWidth,
            OneOf(Text(shape, "density", required: false, "shape.density") ?? defaults.Density, "shape.density", Densities));
    }

    private static ThemeFonts ReadFont(JsonElement? element)
    {
        var defaults = BuiltInThemes.DefaultFonts;
        if (element is not { } font)
        {
            return defaults;
        }

        RejectUnknown(font, "font.", "ui", "mono", "size");
        return new ThemeFonts(
            Text(font, "ui", required: false, "font.ui") ?? defaults.Ui,
            Text(font, "mono", required: false, "font.mono") ?? defaults.Mono,
            Number(font, "font.size", "size", 8, 24) ?? defaults.Size);
    }

    // ---- small helpers ----

    private static void RejectUnknown(JsonElement element, string prefix, params string[] known)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!known.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new ThemeException($"Unknown field '{prefix}{property.Name}'. Check the spelling against the theme schema.");
            }
        }
    }

    private static JsonElement? Section(JsonElement parent, string key, bool required)
    {
        if (!parent.TryGetProperty(key, out var value))
        {
            return required ? throw new ThemeException($"'{key}' is missing.") : null;
        }

        return value.ValueKind == JsonValueKind.Object
            ? value
            : throw new ThemeException($"'{key}' must be an object.");
    }

    private static string? Text(JsonElement parent, string key, bool required, string? path = null)
    {
        path ??= key;
        if (!parent.TryGetProperty(key, out var value))
        {
            return required ? throw new ThemeException($"'{path}' is missing.") : null;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ThemeException($"'{path}' must be a non-empty string.");
        }

        return value.GetString()!.Trim();
    }

    private static string OneOf(string value, string path, params string[] allowed)
    {
        var match = allowed.FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new ThemeException($"'{path}' must be one of: {string.Join(", ", allowed)}.");
    }

    private static string Color(string path, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || !HexColor.IsMatch(text))
        {
            throw new ThemeException($"'{path}' must be a hex color like #RRGGBB or #RRGGBBAA.");
        }

        return text.ToUpperInvariant();
    }

    private static string? OptionalColor(JsonElement parent, string path, string key) =>
        parent.TryGetProperty(key, out var value) ? Color(path, value) : null;

    private static double? Number(JsonElement parent, string path, string key, double min, double max)
    {
        if (!parent.TryGetProperty(key, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || number < min || number > max)
        {
            throw new ThemeException(string.Create(CultureInfo.InvariantCulture, $"'{path}' must be a number from {min} to {max}."));
        }

        return number;
    }
}

internal static class ColorMath
{
    /// <summary>Blends two colors (alpha ignored). <paramref name="amount"/> 0 gives <paramref name="from"/>, 1 gives <paramref name="to"/>.</summary>
    public static string Mix(string from, string to, double amount)
    {
        static int Channel(string color, int index) =>
            int.Parse(color.AsSpan(1 + (index * 2), 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        var parts = Enumerable.Range(0, 3)
            .Select(i => (int)Math.Round(Channel(from, i) + ((Channel(to, i) - Channel(from, i)) * amount)))
            .ToArray();

        return $"#{parts[0]:X2}{parts[1]:X2}{parts[2]:X2}";
    }
}
