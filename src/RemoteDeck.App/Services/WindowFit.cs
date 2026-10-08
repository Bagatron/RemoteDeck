using System.Windows;

namespace RemoteDeck.App.Services;

/// <summary>
/// Keeps a window from being larger than the screen it opens on, so dialogs and the main window stay usable on small
/// displays and at high zoom levels (a 1920x1080 screen at 150% only has 1280x720 units of room). Call it once after
/// InitializeComponent.
/// </summary>
/// <remarks>
/// Windows the user can resize and maximize (the main window, file browser, plugin list) only get their starting size
/// capped: a maximum size would stop them filling the screen when maximized.
/// </remarks>
internal static class WindowFit
{
    private const double Margin = 24;

    public static void ToScreen(Window window, bool capMaximum = true)
    {
        var area = SystemParameters.WorkArea;
        var maxWidth = Math.Max(320, area.Width - Margin);
        var maxHeight = Math.Max(240, area.Height - Margin);

        if (capMaximum)
        {
            window.MaxWidth = double.IsInfinity(window.MaxWidth) ? maxWidth : Math.Min(window.MaxWidth, maxWidth);
            window.MaxHeight = double.IsInfinity(window.MaxHeight) ? maxHeight : Math.Min(window.MaxHeight, maxHeight);
        }

        if (!double.IsNaN(window.Width))
        {
            window.Width = Math.Min(window.Width, maxWidth);
        }

        if (!double.IsNaN(window.Height))
        {
            window.Height = Math.Min(window.Height, maxHeight);
        }

        window.MinWidth = Math.Min(window.MinWidth, maxWidth);
        window.MinHeight = Math.Min(window.MinHeight, maxHeight);
    }
}
