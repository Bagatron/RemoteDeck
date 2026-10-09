using System.Windows;
using System.Windows.Media;

namespace RemoteDeck.App.Dialogs;

/// <summary>
/// A Remote Desktop session for a pane of a split layout. Both the terminal page and the Remote Desktop control are
/// native windows, and WPF content cannot be drawn over either, so the session lives in a borderless window owned by
/// the main window that is moved over the pane (and hidden when the pane is not on screen), like the editor's.
/// </summary>
internal sealed class RdpOverlay : Window
{
    private bool _closed;

    public RdpOverlay(RdpSessionView view, Window owner)
    {
        View = view;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Remote Desktop";
        Width = 100;
        Height = 100;
        Background = Application.Current.TryFindResource("Back") as Brush ?? Brushes.Black;
        Content = view;
        Closed += (_, _) => _closed = true;
    }

    public RdpSessionView View { get; }

    /// <summary>Moves the session over a pane (device-independent pixels, screen coordinates) and shows it.</summary>
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

    /// <summary>Disconnects and removes the session.</summary>
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
