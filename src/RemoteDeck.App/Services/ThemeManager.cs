using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using RemoteDeck.Core.Themes;

namespace RemoteDeck.App.Services;

/// <summary>Converts the theme file's "#RRGGBB" / "#RRGGBBAA" text to a WPF color (note WPF's own parser expects the alpha first).</summary>
internal static class ThemeColor
{
    public static Color ToColor(string hex)
    {
        byte Part(int index) => byte.Parse(hex.AsSpan(1 + (index * 2), 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        var alpha = hex.Length == 9 ? Part(3) : (byte)255;
        return Color.FromArgb(alpha, Part(0), Part(1), Part(2));
    }
}

/// <summary>
/// Knows the available themes (built in, shipped, and the user's own), applies the chosen one to the window
/// resources, and reloads whenever a theme file changes on disk.
/// </summary>
internal sealed class ThemeManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly string _shippedFolder = Path.Combine(AppContext.BaseDirectory, "themes");
    private readonly DispatcherTimer _debounce;
    private readonly List<FileSystemWatcher> _watchers = new();

    public ThemeManager(AppSettings settings)
    {
        _settings = settings;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Reload();
        };
    }

    public IReadOnlyList<Theme> Themes { get; private set; } = BuiltInThemes.All;

    public Theme Current { get; private set; } = BuiltInThemes.Dark;

    /// <summary>One line per theme file that could not be read, naming the file and the field.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = Array.Empty<string>();

    public string UserFolder => AppPaths.UserThemes;

    /// <summary>Raised after every reload, once the new theme is applied to the windows.</summary>
    public event Action<Theme>? Applied;

    public void Start()
    {
        Directory.CreateDirectory(UserFolder);
        Reload();

        foreach (var folder in new[] { _shippedFolder, UserFolder })
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            var watcher = new FileSystemWatcher(folder, "*.json") { EnableRaisingEvents = true };
            FileSystemEventHandler changed = (_, _) => Dispatcher().InvokeAsync(Restart);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (_, _) => Dispatcher().InvokeAsync(Restart);
            _watchers.Add(watcher);
        }
    }

    public void Select(string name)
    {
        _settings.Theme = name;
        _settings.Save();
        Reload();
    }

    public void Reload()
    {
        var files = ThemeLoader.LoadFolder(_shippedFolder).Concat(ThemeLoader.LoadFolder(UserFolder)).ToList();

        // Later sources win, so a file in your own folder can replace a shipped theme of the same name.
        var byName = new Dictionary<string, Theme>(StringComparer.OrdinalIgnoreCase);
        foreach (var theme in BuiltInThemes.All)
        {
            byName[theme.Name] = theme;
        }

        foreach (var file in files)
        {
            if (file.Theme is { } theme)
            {
                byName[theme.Name] = theme;
            }
        }

        Themes = byName.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Problems = files
            .Where(f => f.Error is not null)
            .Select(f => $"{Path.GetFileName(f.Path)}: {f.Error}")
            .ToList();

        Current = _settings.Theme is { } wanted && byName.TryGetValue(wanted, out var found)
            ? found
            : BuiltInThemes.Dark;

        Apply(Current);
    }

    public void StyleWindow(Window window)
    {
        window.FontFamily = new FontFamily(Current.Font.Ui);
        window.FontSize = Current.Font.Size;
    }

    public void Dispose()
    {
        _debounce.Stop();
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
    }

    private static Dispatcher Dispatcher() => Application.Current.Dispatcher;

    private void Restart()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void Apply(Theme theme)
    {
        var app = Application.Current;
        var c = theme.Colors;

        // The brushes are shared by every window, so changing their color restyles everything at once.
        Set("Back", c.Background);
        Set("Panel", c.Surface);
        Set("PanelHover", c.SurfaceAlt);
        Set("Line", c.Border);
        Set("Text", c.Foreground);
        Set("TextDim", c.MutedForeground);
        Set("Accent", c.Accent);
        Set("Danger", c.Danger);
        Set("Warning", c.Warning);
        Set("Success", c.Success);
        Set("Broadcast", c.Broadcast);

        app.ThemeMode = theme.Base == ThemeBase.Light ? ThemeMode.Light : ThemeMode.Dark;
        foreach (Window window in app.Windows)
        {
            StyleWindow(window);
        }

        Applied?.Invoke(theme);

        void Set(string key, string hex)
        {
            var color = ThemeColor.ToColor(hex);
            if (app.Resources[key] is SolidColorBrush { IsFrozen: false } brush)
            {
                brush.Color = color;
            }
            else
            {
                app.Resources[key] = new SolidColorBrush(color);
            }
        }
    }
}
