using System.IO;
using System.Text.Json;

namespace RemoteDeck.App.Services;

/// <summary>Small per-user preferences, kept in <c>settings.json</c>. A missing or damaged file just means defaults.</summary>
internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Name of the chosen theme.</summary>
    public string? Theme { get; set; }

    public const int DefaultScrollback = 10000;

    /// <summary>How many lines of output each terminal keeps to scroll back through.</summary>
    public int ScrollbackLines { get; set; } = DefaultScrollback;

    /// <summary>Minutes of inactivity before the vault locks and the unlock prompt appears; 0 means never.</summary>
    public int AutoLockMinutes { get; set; } = RemoteDeck.Core.Security.AutoLock.DefaultMinutes;

    /// <summary>Plugins the user turned on, with the permissions they approved for each (plugin id to permission names).</summary>
    public Dictionary<string, List<string>> Plugins { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings)) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall through to defaults.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Folder);
            File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a preference is not worth interrupting the user.
        }
    }
}
