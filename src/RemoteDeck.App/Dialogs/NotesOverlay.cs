using System.Windows;
using System.Windows.Media;

namespace RemoteDeck.App.Dialogs;

/// <summary>
/// The built-in editor for a pane of a split layout. The terminal page is one native browser window, and WPF content
/// cannot be drawn over it, so the editor lives in a borderless window owned by the main window that is moved over the
/// pane (and hidden when the pane is not on screen).
/// </summary>
internal sealed class NotesOverlay : Window
{
    private bool _closed;

    public NotesOverlay(NotesView view, Window owner)
    {
        View = view;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Editor";
        Width = 100;
        Height = 100;
        Background = Application.Current.TryFindResource("Back") as Brush ?? Brushes.Black;
        Content = view;
        Closed += (_, _) => _closed = true;
    }

    public NotesView View { get; }

    /// <summary>Moves the editor over a pane (all values in device-independent pixels, screen coordinates) and shows it.</summary>
    public void Place(double left, double top, double width, double height)
    {
        if (_closed)
        {
            return;
        }

        Left = left;
        Top = top;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        if (!IsVisible)
        {
            Show();
        }
    }

    public void HideOverlay()
    {
        if (!_closed && IsVisible)
        {
            Hide();
        }
    }

    /// <summary>Saves everything and removes the editor.</summary>
    public void Shutdown()
    {
        View.Dispose();
        if (_closed)
        {
            return;
        }

        try
        {
            Content = null;
            Close();
        }
        catch (InvalidOperationException)
        {
            // Already closing with the main window.
        }
    }
}
