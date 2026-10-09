namespace RemoteDeck.Core.Layout;

/// <summary>Matching what is typed in the "save workspace" box against the workspaces that already exist.</summary>
public static class WorkspaceNames
{
    /// <summary>
    /// The existing names that match the typed text (anywhere in the name, ignoring case), names that start with it
    /// first, then alphabetical. Nothing typed gives them all.
    /// </summary>
    public static IReadOnlyList<string> Filter(IEnumerable<string> existing, string? typed)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var text = (typed ?? string.Empty).Trim();
        return existing
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => text.Length == 0 || n.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => text.Length > 0 && n.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The existing workspace with exactly this name (ignoring case), or null: saving would replace it.</summary>
    public static string? FindExact(IEnumerable<string> existing, string? typed)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var text = (typed ?? string.Empty).Trim();
        return text.Length == 0 ? null : existing.FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
    }
}
