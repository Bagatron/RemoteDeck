using System.Text.Json.Serialization;

namespace RemoteDeck.Core.Layout;

/// <summary>How a split arranges its two children.</summary>
public enum SplitOrientation
{
    /// <summary>Children sit side by side (left | right).</summary>
    Columns,

    /// <summary>Children are stacked (top / bottom).</summary>
    Rows,
}

/// <summary>
/// A node in the split tree: either a pane that holds one session, or a split of two nodes.
/// The tree is immutable; every edit returns a new tree.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PaneNode), "pane")]
[JsonDerivedType(typeof(SplitNode), "split")]
public abstract record LayoutNode;

/// <param name="Id">Unique within one layout, e.g. "p1".</param>
/// <param name="ConnectionId">The saved connection shown in this pane, if any.</param>
public sealed record PaneNode(string Id, string? ConnectionId = null) : LayoutNode;

/// <param name="Orientation">Columns put the children side by side, rows stack them.</param>
/// <param name="Ratio">Share of the space given to <paramref name="First"/>, strictly between 0 and 1.</param>
public sealed record SplitNode(
    SplitOrientation Orientation,
    double Ratio,
    LayoutNode First,
    LayoutNode Second) : LayoutNode;

public sealed class LayoutException : Exception
{
    public LayoutException(string message)
        : base(message)
    {
    }

    public LayoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
