using System.Windows;
using RemoteDeck.App.Services;

namespace RemoteDeck.App.Dialogs;

/// <summary>
/// The questions for a backup: for an export, whether passwords go in and under which passphrase; for a restore, the
/// passphrase of an encrypted file and whether existing items may be replaced.
/// </summary>
public partial class BackupDialog : Window
{
    private const int MinimumLength = 8;

    private readonly bool _exporting;
    private readonly bool _needsPassphrase;

    /// <summary>The dialog for writing a backup.</summary>
    public BackupDialog(bool vaultAvailable)
    {
        InitializeComponent();
        WindowFit.ToScreen(this);
        _exporting = true;

        Heading.Text = "Export a backup";
        Explanation.Text = "Saves your connections, folders, workspaces, notes, saved chats, themes and settings in one file you can "
            + "restore on another computer. Plugins and the SSH known-hosts list are not included.";
        OkButton.Content = "Choose file...";

        if (!vaultAvailable)
        {
            PasswordsBox.IsEnabled = false;
            PasswordsBox.Content = "Include my saved passwords (unlock the vault first)";
        }

        Passwords_Changed(this, new RoutedEventArgs());
    }

    /// <summary>The dialog for reading a backup. <paramref name="encrypted"/> asks for its passphrase.</summary>
    public BackupDialog(string fileName, bool encrypted, string? details)
    {
        InitializeComponent();
        WindowFit.ToScreen(this);
        _exporting = false;
        _needsPassphrase = encrypted;

        Heading.Text = "Restore a backup";
        Explanation.Text = fileName + (string.IsNullOrWhiteSpace(details) ? string.Empty : "\n" + details);
        PasswordsBox.Visibility = Visibility.Collapsed;
        OverwriteBox.Visibility = Visibility.Visible;
        OkButton.Content = "Restore";

        if (encrypted)
        {
            PassphraseLabel.Text = "Passphrase this backup was saved with";
            PassphrasePanel.Visibility = Visibility.Visible;
            Loaded += (_, _) => PassphraseBox.Focus();
        }
        else
        {
            PassphrasePanel.Visibility = Visibility.Collapsed;
        }
    }

    public bool IncludePasswords { get; private set; }

    public string Passphrase { get; private set; } = string.Empty;

    public bool Overwrite { get; private set; }

    private void Passwords_Changed(object sender, RoutedEventArgs e)
    {
        if (!_exporting || PassphrasePanel is null)
        {
            return;
        }

        var on = PasswordsBox.IsChecked == true;
        PassphrasePanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ConfirmPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        PassphraseLabel.Text = "Passphrase (encrypts the whole file; there is no way to recover it)";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting)
        {
            IncludePasswords = PasswordsBox.IsChecked == true;
            if (IncludePasswords)
            {
                var passphrase = PassphraseBox.Password;
                if (passphrase.Length < MinimumLength)
                {
                    MessageBox.Show(this, $"Use at least {MinimumLength} characters.", "RemoteDeck", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (passphrase != ConfirmBox.Password)
                {
                    MessageBox.Show(this, "The two passphrases are different.", "RemoteDeck", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                Passphrase = passphrase;
            }
        }
        else
        {
            if (_needsPassphrase && PassphraseBox.Password.Length == 0)
            {
                return;
            }

            Passphrase = PassphraseBox.Password;
            Overwrite = OverwriteBox.IsChecked == true;
        }

        DialogResult = true;
    }
}
