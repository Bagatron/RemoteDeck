namespace RemoteDeck.Core.Layout;

public enum LayoutPreset
{
    Single,
    TwoColumns,
    TwoRows,
    Grid2x2,
    OneBigTwoSmall,
    ThreeColumns,
}

/// <summary>One-click layouts. Panes are named p1, p2, ... in visual order.</summary>
public static class LayoutPresets
{
    public static LayoutNode Create(LayoutPreset preset) => preset switch
    {
        LayoutPreset.Single => Pane(1),
        LayoutPreset.TwoColumns => new SplitNode(SplitOrientation.Columns, 0.5, Pane(1), Pane(2)),
        LayoutPreset.TwoRows => new SplitNode(SplitOrientation.Rows, 0.5, Pane(1), Pane(2)),
        LayoutPreset.Grid2x2 => new SplitNode(
            SplitOrientation.Rows,
            0.5,
            new SplitNode(SplitOrientation.Columns, 0.5, Pane(1), Pane(2)),
            new SplitNode(SplitOrientation.Columns, 0.5, Pane(3), Pane(4))),
        LayoutPreset.OneBigTwoSmall => new SplitNode(
            SplitOrientation.Columns,
            0.6,
            Pane(1),
            new SplitNode(SplitOrientation.Rows, 0.5, Pane(2), Pane(3))),
        LayoutPreset.ThreeColumns => new SplitNode(
            SplitOrientation.Columns,
            1.0 / 3.0,
            Pane(1),
            new SplitNode(SplitOrientation.Columns, 0.5, Pane(2), Pane(3))),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown layout preset."),
    };

    private static PaneNode Pane(int number) => new($"p{number}");
}
