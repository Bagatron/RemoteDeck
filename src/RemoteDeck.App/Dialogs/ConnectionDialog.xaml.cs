using System.Windows;
using Microsoft.Win32;
using RemoteDeck.Core.Connections;

namespace RemoteDeck.App.Dialogs;

/// <summary>Add or edit a saved SSH connection. The password is returned separately and goes to the vault, never into the entry.</summary>
public partial class ConnectionDialog : Window
{
    private sealed record FolderChoice(string? Id, string Label);

    private readonly ConnectionEntry? _existing;

    internal ConnectionDialog(ConnectionStore store, ConnectionEntry? existing, string? defaultFolderId)
    {
        InitializeComponent();
        _existing = existing;
        Title = existing is null ? "New connection" : "Edit connection";

        var choices = new List<FolderChoice> { new(null, "(top level)") };
        choices.AddRange(store.Folders
            .Select(f => new FolderChoice(f.Id, store.FolderPath(f.Id)))
            .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase));
        FolderBox.ItemsSource = choices;

        var folderId = existing is null ? defaultFolderId : existing.FolderId;
        FolderBox.SelectedItem = choices.FirstOrDefault(c => c.Id == folderId) ?? choices[0];

        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            HostBox.Text = existing.Host;
            PortBox.Text = existing.Port?.ToString() ?? string.Empty;
            UserBox.Text = Option(existing, "username");
            KeyBox.Text = Option(existing, "privateKeyPath");
            FavoriteBox.IsChecked = existing.Favorite;

            if (existing.CredentialId is not null)
            {
                PasswordLabel.Text = "Password (leave empty to keep the saved one)";
            }
        }

        Loaded += (_, _) => (existing is null ? NameBox : PasswordBox as UIElement).Focus();
    }

    /// <summary>The entry to save. Its credential id is filled in by the caller.</summary>
    internal ConnectionEntry? Result { get; private set; }

    /// <summary>What was typed in the password box (empty means "no change").</summary>
    public string Password { get; private set; } = string.Empty;

    private static string Option(ConnectionEntry entry, string key) =>
        entry.Options is not null && entry.Options.TryGetValue(key, out var value) ? value : string.Empty;

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a private key",
            InitialDirectory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ssh"),
        };

        if (dialog.ShowDialog(this) == true)
        {
            KeyBox.Text = dialog.FileName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        var user = UserBox.Text.Trim();
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            name = host;
        }

        if (host.Length == 0 || user.Length == 0)
        {
            Warn("A connection needs a host and a username.");
            return;
        }

        int? port = null;
        if (PortBox.Text.Trim().Length > 0)
        {
            if (!int.TryParse(PortBox.Text.Trim(), out var parsed) || parsed is < 1 or > 65535)
            {
                Warn("The port must be a number from 1 to 65535.");
                return;
            }

            port = parsed;
        }

        var key = KeyBox.Text.Trim();
        var options = new Dictionary<string, string>(_existing?.Options ?? new Dictionary<string, string>())
        {
            ["username"] = user,
        };

        if (key.Length > 0)
        {
            options["privateKeyPath"] = key;
        }
        else
        {
            options.Remove("privateKeyPath");
        }

        var hasSecret = PasswordBox.Password.Length > 0 || _existing?.CredentialId is not null;
        if (key.Length == 0 && !hasSecret)
        {
            Warn("Enter a password, or choose a private key.");
            return;
        }

        var folder = (FolderChoice?)FolderBox.SelectedItem;
        Result = (_existing ?? new ConnectionEntry(ConnectionStore.NewId(), name, "ssh", host)) with
        {
            Name = name,
            Host = host,
            Port = port,
            FolderId = folder?.Id,
            Options = options,
            Favorite = FavoriteBox.IsChecked == true
        };
        Password = PasswordBox.Password;
        DialogResult = true;
    }

    private void Warn(string text) =>
        MessageBox.Show(this, text, "RemoteDeck", MessageBoxButton.OK, MessageBoxImage.Information);
}
