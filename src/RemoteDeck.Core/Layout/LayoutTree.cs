namespace RemoteDeck.Core.Layout;

/// <summary>Pure functions for reading and editing a layout tree.</summary>
public static class LayoutTree
{
    public const double MinRatio = 0.05;
    public const double MaxRatio = 0.95;

    /// <summary>All panes, in visual order (left to right, top to bottom).</summary>
    public static IEnumerable<PaneNode> Panes(LayoutNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        switch (root)
        {
            case PaneNode pane:
                yield return pane;
                break;
            case SplitNode split:
                foreach (var p in Panes(split.First))
                {
                    yield return p;
                }

                foreach (var p in Panes(split.Second))
                {
                    yield return p;
                }

                break;
        }
    }

    public static PaneNode? FindPane(LayoutNode root, string paneId)
    {
        return Panes(root).FirstOrDefault(p => p.Id == paneId);
    }

    /// <summary>Splits a pane in two. The existing pane keeps its session; the new pane starts empty.</summary>
    public static LayoutNode SplitPane(
        LayoutNode root,
        string paneId,
        SplitOrientation orientation,
        string newPaneId,
        double ratio = 0.5)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPaneId);

        if (FindPane(root, newPaneId) is not null)
        {
            throw new ArgumentException($"Pane id '{newPaneId}' already exists.", nameof(newPaneId));
        }

        if (FindPane(root, paneId) is null)
        {
            throw new KeyNotFoundException($"No pane '{paneId}' in this layout.");
        }

        return Rewrite(root);

        LayoutNode Rewrite(LayoutNode node) => node switch
        {
            PaneNode pane when pane.Id == paneId =>
                new SplitNode(orientation, ClampRatio(ratio), pane, new PaneNode(newPaneId)),
            SplitNode split => split with { First = Rewrite(split.First), Second = Rewrite(split.Second) },
            _ => node,
        };
    }

    /// <summary>
    /// Closes a pane. Its sibling takes over the space the split used to occupy.
    /// Returns null when the last pane is closed.
    /// </summary>
    public static LayoutNode? ClosePane(LayoutNode root, string paneId)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (FindPane(root, paneId) is null)
        {
            throw new KeyNotFoundException($"No pane '{paneId}' in this layout.");
        }

        return Remove(root);

        LayoutNode? Remove(LayoutNode node)
        {
            switch (node)
            {
                case PaneNode pane:
                    return pane.Id == paneId ? null : pane;
                case SplitNode split:
                    var first = Remove(split.First);
                    var second = Remove(split.Second);
                    if (first is null)
                    {
                        return second;
                    }

                    if (second is null)
                    {
                        return first;
                    }

                    return split with { First = first, Second = second };
                default:
                    return node;
            }
        }
    }

    /// <summary>Shows a saved connection in a pane.</summary>
    public static LayoutNode AssignConnection(LayoutNode root, string paneId, string? connectionId)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (FindPane(root, paneId) is null)
        {
            throw new KeyNotFoundException($"No pane '{paneId}' in this layout.");
        }

        return Rewrite(root);

        LayoutNode Rewrite(LayoutNode node) => node switch
        {
            PaneNode pane when pane.Id == paneId => pane with { ConnectionId = connectionId },
            SplitNode split => split with { First = Rewrite(split.First), Second = Rewrite(split.Second) },
            _ => node,
        };
    }

    /// <summary>
    /// Moves a splitter. The path walks from the root: 0 = first child, 1 = second child,
    /// and an empty path is the root split itself.
    /// </summary>
    public static LayoutNode SetRatio(LayoutNode root, IReadOnlyList<int> path, double ratio)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);

        return Walk(root, 0);

        LayoutNode Walk(LayoutNode node, int depth)
        {
            if (node is not SplitNode split)
            {
                throw new ArgumentException("Path does not lead to a split.", nameof(path));
            }

            if (depth == path.Count)
            {
                return split with { Ratio = ClampRatio(ratio) };
            }

            return path[depth] switch
            {
                0 => split with { First = Walk(split.First, depth + 1) },
                1 => split with { Second = Walk(split.Second, depth + 1) },
                _ => throw new ArgumentException("Path entries must be 0 or 1.", nameof(path)),
            };
        }
    }

    /// <summary>Throws <see cref="LayoutException"/> if the tree is malformed.</summary>
    public static void Validate(LayoutNode root)
    {
        if (root is null)
        {
            throw new LayoutException("Layout is missing.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        Visit(root);

        void Visit(LayoutNode node)
        {
            switch (node)
            {
                case PaneNode pane:
                    if (string.IsNullOrWhiteSpace(pane.Id))
                    {
                        throw new LayoutException("A pane has an empty id.");
                    }

                    if (!seen.Add(pane.Id))
                    {
                        throw new LayoutException($"Duplicate pane id '{pane.Id}'.");
                    }

                    break;
                case SplitNode split:
                    if (!(split.Ratio > 0 && split.Ratio < 1))
                    {
                        throw new LayoutException($"Split ratio must be between 0 and 1 (got {split.Ratio}).");
                    }

                    if (split.First is null || split.Second is null)
                    {
                        throw new LayoutException("A split needs two children.");
                    }

                    Visit(split.First);
                    Visit(split.Second);
                    break;
                default:
                    throw new LayoutException("Unknown layout node.");
            }
        }
    }

    public static double ClampRatio(double ratio)
    {
        if (double.IsNaN(ratio))
        {
            return 0.5;
        }

        return Math.Clamp(ratio, MinRatio, MaxRatio);
    }
}
