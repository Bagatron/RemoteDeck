using System.Text.RegularExpressions;

namespace RemoteDeck.Core.Broadcast;

public enum BroadcastRiskKind
{
    None,
    MultiLinePaste,
    RiskyCommand,
}

public sealed record BroadcastRisk(BroadcastRiskKind Kind, string? Detail = null)
{
    public static BroadcastRisk NoRisk { get; } = new(BroadcastRiskKind.None);

    public bool IsRisky => Kind != BroadcastRiskKind.None;
}

/// <summary>
/// Spots commands that deserve a second look before they go to several machines at once.
/// This is a safety net, not a security boundary: it only ever asks for confirmation.
/// </summary>
public static class RiskyCommandDetector
{
    private const RegexOptions Flags = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly (Regex Pattern, string Reason)[] Rules =
    {
        (new Regex(@"\brm\b[^\n;&|]*\s(?:-[a-z]*r[a-z]*|--recursive)\b", Flags), "recursive delete (rm -r)"),
        (new Regex(@"\b(?:shutdown|reboot|poweroff|halt)\b", Flags), "shuts down or restarts the machine"),
        (new Regex(@"\bmkfs(?:\.\w+)?\b", Flags), "formats a filesystem"),
        (new Regex(@"\bdd\s+[^\n]*\bof=/dev/", Flags), "writes directly to a device (dd)"),
        (new Regex(@">\s*/dev/(?:sd|nvme|hd)", Flags), "overwrites a disk device"),
        (new Regex(@"\bchmod\s+-R\s+[0-7]{3,4}\s+/(?:\s|$)", Flags), "recursive chmod on /"),
        (new Regex(@":\(\)\s*\{\s*:\s*\|\s*:\s*&\s*\}\s*;\s*:", Flags), "fork bomb"),
        (new Regex(@"\bdrop\s+(?:table|database)\b", Flags), "drops a database object"),
        (new Regex(@"\bRemove-Item\b[^\n]*-Recurse", Flags), "recursive delete (Remove-Item -Recurse)"),
        (new Regex(@"\bformat\s+[a-z]:", Flags), "formats a drive"),
    };

    /// <summary>Returns a short human-readable reason, or null if nothing looks risky.</summary>
    public static string? Detect(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (var (pattern, reason) in Rules)
        {
            if (pattern.IsMatch(text))
            {
                return reason;
            }
        }

        return null;
    }
}

/// <summary>What the host double-checks before text is sent to more than one session.</summary>
public sealed class BroadcastPolicy
{
    public bool ConfirmMultiLine { get; init; } = true;

    public bool ConfirmRiskyCommands { get; init; } = true;

    public BroadcastRisk Evaluate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (ConfirmRiskyCommands && RiskyCommandDetector.Detect(text) is { } reason)
        {
            return new BroadcastRisk(BroadcastRiskKind.RiskyCommand, reason);
        }

        if (ConfirmMultiLine && IsMultiLine(text))
        {
            return new BroadcastRisk(BroadcastRiskKind.MultiLinePaste, "The text contains more than one line.");
        }

        return BroadcastRisk.NoRisk;
    }

    // A single trailing newline is just "press Enter", not a multi-line paste.
    private static bool IsMultiLine(string text)
    {
        var trimmed = text.TrimEnd('\r', '\n');
        return trimmed.AsSpan().IndexOfAny('\r', '\n') >= 0;
    }
}
