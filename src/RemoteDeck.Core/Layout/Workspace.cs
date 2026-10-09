using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteDeck.Core.Layout;

/// <summary>
/// A saved arrangement: which connections sit in which panes. Shared as a plain JSON file.
/// Broadcast is never saved as "on": a workspace only remembers which panes are group members,
/// so opening one can never start typing into several hosts by surprise.
/// </summary>
public sealed record Workspace(
    string Name,
    LayoutNode Layout,
    string? Description = null,
    bool AutoConnect = false,
    IReadOnlyList<string>? BroadcastMembers = null,
    IReadOnlyDictionary<string, string>? NoteFolders = null);

/// <summary>JSON reading and writing for layouts and workspaces (camelCase, string enums, "type" discriminator).</summary>
public static class LayoutSerializer
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(LayoutNode root)
    {
        LayoutTree.Validate(root);
        return JsonSerializer.Serialize<LayoutNode>(root, Options);
    }

    public static LayoutNode Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        LayoutNode? root;
        try
        {
            root = JsonSerializer.Deserialize<LayoutNode>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new LayoutException($"Layout JSON is invalid: {ex.Message}", ex);
        }

        if (root is null)
        {
            throw new LayoutException("Layout JSON was empty.");
        }

        LayoutTree.Validate(root);
        return root;
    }

    public static string SerializeWorkspace(Workspace workspace)
    {
        ValidateWorkspace(workspace);
        return JsonSerializer.Serialize(workspace, Options);
    }

    public static Workspace DeserializeWorkspace(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        Workspace? workspace;
        try
        {
            workspace = JsonSerializer.Deserialize<Workspace>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new LayoutException($"Workspace JSON is invalid: {ex.Message}", ex);
        }

        if (workspace is null)
        {
            throw new LayoutException("Workspace JSON was empty.");
        }

        ValidateWorkspace(workspace);
        return workspace;
    }

    private static void ValidateWorkspace(Workspace workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace.Name))
        {
            throw new LayoutException("A workspace needs a name.");
        }

        LayoutTree.Validate(workspace.Layout);

        var paneIds = LayoutTree.Panes(workspace.Layout).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var paneId in workspace.NoteFolders?.Keys ?? Enumerable.Empty<string>())
        {
            if (!paneIds.Contains(paneId))
            {
                throw new LayoutException($"Note folder pane '{paneId}' is not a pane in this workspace.");
            }
        }

        if (workspace.BroadcastMembers is null)
        {
            return;
        }

        foreach (var member in workspace.BroadcastMembers)
        {
            if (!paneIds.Contains(member))
            {
                throw new LayoutException($"Broadcast member '{member}' is not a pane in this workspace.");
            }
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            AllowOutOfOrderMetadataProperties = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
