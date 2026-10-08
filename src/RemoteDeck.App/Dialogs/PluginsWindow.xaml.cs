using System.Diagnostics;
using System.IO;
using System.Windows;
using RemoteDeck.App.Services;

namespace RemoteDeck.App.Dialogs;

/// <summary>Lists the plugins found on disk and turns them on or off.</summary>
public partial class PluginsWindow : Window
{
    private sealed record Item(PluginRow Row)
    {
        public string Name => Row.Candidate.Manifest?.Name ?? System.IO.Path.GetFileName(Row.Candidate.Directory);

        public string Version => Row.Candidate.Manifest?.Version ?? string.Empty;

        public string Permissions => Row.Candidate.Manifest?.Permissions is { Count: > 0 } p
            ? string.Join(", ", p)
            : "nothing special";

        public string Status => Row.Status;
    }

    private readonly PluginManager _plugins;

    internal PluginsWindow(Window owner, PluginManager plugins)
    {
        InitializeComponent();
        Owner = owner;
        _plugins = plugins;
        Reload();
    }

    private Item? Selected => List.SelectedItem as Item;

    private void Reload()
    {
        List.ItemsSource = _plugins.Rows().Select(r => new Item(r)).ToList();
        UpdateButton();
    }

    private void UpdateButton()
    {
        var row = Selected?.Row;
        ToggleButton.IsEnabled = row is not null && (row.Running || row.CanEnable);
        ToggleButton.Content = row?.Running == true ? "Turn off" : "Turn on";
    }

    private void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateButton();

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.Row is not { } row)
        {
            return;
        }

        if (row.Running)
        {
            _plugins.Disable(row.Candidate);
        }
        else
        {
            var manifest = row.Candidate.Manifest!;
            var asks = manifest.Permissions is { Count: > 0 } p ? string.Join(", ", p) : "no special permissions";
            var author = manifest.Author is null ? string.Empty : $" by {manifest.Author}";
            var answer = MessageBox.Show(
                this,
                $"Turn on \"{manifest.Name}\"{author}?\n\nIt asks for: {asks}.\n\nPlugin code runs with your Windows account's full access. Only continue if you trust where it came from.",
                "Turn on plugin",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            if (_plugins.Enable(row.Candidate) is { } error)
            {
                MessageBox.Show(this, error, "Plugin", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        Reload();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _plugins.Refresh();
        Reload();
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_plugins.UserFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_plugins.UserFolder}\"") { UseShellExecute = true });
    }
}
