using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RemoteDeck.Core.Plugins;

/// <summary>What a plugin asks to be allowed to do. Shown to the user before the plugin is enabled.</summary>
public enum PluginPermission
{
    Network,
    FileSystem,
    UseCredentials,
    LaunchProcess,
}

/// <summary>The contents of a plugin's plugin.json.</summary>
public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    string MinHostApiVersion,
    string EntryPoint,
    string? Author = null,
    string? Description = null,
    string? License = null,
    IReadOnlyList<PluginPermission>? Permissions = null);

public sealed class PluginManifestException : Exception
{
    public PluginManifestException(string message)
        : base(message)
    {
    }

    public PluginManifestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static partial class PluginManifestLoader
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.\-]{1,62}[a-z0-9]$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)(?:-[0-9A-Za-z.\-]+)?$")]
    private static partial Regex VersionPattern();

    /// <summary>Parses and validates plugin.json. Throws <see cref="PluginManifestException"/> with a readable reason.</summary>
    public static PluginManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new PluginManifestException($"plugin.json is invalid: {ex.Message}", ex);
        }

        if (manifest is null)
        {
            throw new PluginManifestException("plugin.json is empty.");
        }

        Validate(manifest);
        return manifest;
    }

    /// <summary>
    /// A plugin can load when it was built for the same major API version and the host is at least
    /// as new as the plugin's minimum.
    /// </summary>
    public static bool IsCompatible(PluginManifest manifest, Version hostApiVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(hostApiVersion);

        var minimum = ParseCoreVersion(manifest.MinHostApiVersion);
        var host = new Version(hostApiVersion.Major, hostApiVersion.Minor, Math.Max(hostApiVersion.Build, 0));
        return host.Major == minimum.Major && host >= minimum;
    }

    private static void Validate(PluginManifest manifest)
    {
        Require(manifest.Id, "id");
        Require(manifest.Name, "name");
        Require(manifest.Version, "version");
        Require(manifest.MinHostApiVersion, "minHostApiVersion");
        Require(manifest.EntryPoint, "entryPoint");

        if (!IdPattern().IsMatch(manifest.Id))
        {
            throw new PluginManifestException(
                $"Plugin id '{manifest.Id}' must be 3-64 characters: lowercase letters, digits, '.' or '-'.");
        }

        if (!VersionPattern().IsMatch(manifest.Version))
        {
            throw new PluginManifestException($"Plugin version '{manifest.Version}' must look like 1.2.3.");
        }

        if (!VersionPattern().IsMatch(manifest.MinHostApiVersion))
        {
            throw new PluginManifestException(
                $"minHostApiVersion '{manifest.MinHostApiVersion}' must look like 1.0.0.");
        }

        var entry = manifest.EntryPoint;
        var hasPathPart = entry.Contains('/') || entry.Contains('\\') || entry.Contains("..", StringComparison.Ordinal);
        if (hasPathPart || !entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginManifestException(
                $"entryPoint '{entry}' must be a .dll file name inside the plugin folder.");
        }

        if (manifest.Permissions is { Count: > 0 } permissions && permissions.Distinct().Count() != permissions.Count)
        {
            throw new PluginManifestException("permissions must not contain duplicates.");
        }
    }

    private static void Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PluginManifestException($"plugin.json is missing '{field}'.");
        }
    }

    private static Version ParseCoreVersion(string text)
    {
        var match = VersionPattern().Match(text);
        if (!match.Success)
        {
            throw new PluginManifestException($"'{text}' is not a valid version.");
        }

        return new Version(
            int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
