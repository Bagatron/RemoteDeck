using RemoteDeck.App.Services;
using RemoteDeck.Core.Layout;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RemoteDeck.App.Dialogs;

/// <summary>
/// Asks for one line of text, e.g. a folder name. With a list of existing names it also shows the ones that match what
/// is typed; picking one fills the box (and says that saving will replace it).
/// </summary>
public partial class NameDialog : Window
{
    private readonly IReadOnlyList<string>? _existing;
    private bool _filling;

    public NameDialog(string title, string prompt, string initial = "", IReadOnlyList<string>? existing = null)
    {
        InitializeComponent();
        WindowFit.ToScreen(this);
        Title = title;
        Prompt.Text = prompt;
        _existing = existing is { Count: > 0 } ? existing : null;
        if (_existing is not null)
        {
            Existing.Visibility = Visibility.Visible;
            MatchHint.Visibility = Visibility.Visible;
            NameBox.TextChanged += (_, _) => Refresh();
        }

        NameBox.Text = initial;
        Refresh();
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string Value { get; private set; } = string.Empty;

    private void Refresh()
    {
        if (_existing is null)
        {
            return;
        }

        _filling = true;
        try
        {
            var matches = WorkspaceNames.Filter(_existing, NameBox.Text);
            Existing.ItemsSource = matches;
            var exact = WorkspaceNames.FindExact(_existing, NameBox.Text);
            if (exact is not null)
            {
                Existing.SelectedItem = matches.FirstOrDefault(m => string.Equals(m, exact, StringComparison.OrdinalIgnoreCase));
            }

            MatchHint.Text = NameBox.Text.Trim().Length == 0
                ? "Type a new name, or pick an existing one to replace it."
                : exact is not null
                    ? $"Saving will replace \"{exact}\"."
                    : matches.Count > 0 ? "A new workspace. Pick a match below to replace it instead." : "A new workspace.";
        }
        finally
        {
            _filling = false;
        }
    }

    private void Existing_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && Existing.SelectedItem is string name)
        {
            NameBox.Text = name;
            NameBox.CaretIndex = name.Length;
        }
    }

    private void NameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_existing is null || Existing.Items.Count == 0)
        {
            return;
        }

        var step = e.Key == Key.Down ? 1 : e.Key == Key.Up ? -1 : 0;
        if (step == 0)
        {
            return;
        }

        var next = Math.Clamp(Existing.SelectedIndex + step, 0, Existing.Items.Count - 1);
        Existing.SelectedIndex = next;
        e.Handled = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var text = NameBox.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        Value = text;
        DialogResult = true;
    }
}
