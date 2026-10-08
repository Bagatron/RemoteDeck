using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RemoteDeck.App.Dialogs;

/// <summary>One line in the command palette.</summary>
public sealed record PaletteItem(string Title, string Hint, Action Run);

/// <summary>Type to search saved connections and actions, Enter to run, Esc to close.</summary>
public sealed partial class CommandPalette : Window
{
    private readonly Func<string, IReadOnlyList<PaletteItem>> _source;
    private bool _closing;

    public CommandPalette(Window owner, Func<string, IReadOnlyList<PaletteItem>> source)
    {
        InitializeComponent();
        Owner = owner;
        _source = source;
        Left = owner.Left + ((owner.ActualWidth - Width) / 2);
        Top = owner.Top + 90;
        Loaded += (_, _) =>
        {
            Refresh();
            QueryBox.Focus();
        };
    }

    /// <summary>What to run once the palette has closed (so any dialog it opens is not stacked on top of it).</summary>
    public Action? Chosen { get; private set; }

    private void Refresh()
    {
        Results.ItemsSource = _source(QueryBox.Text.Trim());
        if (Results.Items.Count > 0)
        {
            Results.SelectedIndex = 0;
        }
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void Choose()
    {
        if (Results.SelectedItem is PaletteItem item)
        {
            Chosen = item.Run;
            Finish();
        }
    }

    private void Finish()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Close();
    }

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Choose();

    private void Window_Deactivated(object? sender, EventArgs e) => Finish();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Finish();
                e.Handled = true;
                break;
            case Key.Enter:
                Choose();
                e.Handled = true;
                break;
            case Key.Down:
            case Key.Up:
                var step = e.Key == Key.Down ? 1 : -1;
                if (Results.Items.Count > 0)
                {
                    Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + step, 0, Results.Items.Count - 1);
                    Results.ScrollIntoView(Results.SelectedItem);
                }

                e.Handled = true;
                break;
        }
    }
}
