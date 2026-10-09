using System.Windows;
using System.Windows.Input;
using RemoteDeck.App.Services;
using RemoteDeck.Core.Connections;
using RemoteDeck.Plugin;
using RemoteDeck.Vault;

namespace RemoteDeck.App.Dialogs;

/// <summary>The saved logins: a small credential manager on top of the vault.</summary>
public partial class LoginsWindow : Window
{
    private sealed record Row(LoginEntry Login, int Uses)
    {
        public string Name => Login.Name;

        public string UserText => Login.Username ?? string.Empty;

        public string UsedText => Uses == 0 ? "nothing" : Uses == 1 ? "1 connection" : $"{Uses} connections";
    }

    private readonly CredentialVault _vault;
    private readonly ConnectionStore _store;

    internal LoginsWindow(Window owner, CredentialVault vault, ConnectionStore store)
    {
        InitializeComponent();
        WindowFit.ToScreen(this, capMaximum: false);
        Owner = owner;
        _vault = vault;
        _store = store;
        Reload();
    }

    private LoginEntry? Selected => (List.SelectedItem as Row)?.Login;

    private void Reload(string? select = null)
    {
        List.ItemsSource = _store.Logins.Select(l => new Row(l, _store.UsesOfCredential(l.Id))).ToList();
        if (select is not null)
        {
            List.SelectedItem = List.Items.OfType<Row>().FirstOrDefault(r => r.Login.Id == select);
        }
    }

    private void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        EditButton.IsEnabled = DeleteButton.IsEnabled = Selected is not null;
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not null)
        {
            Edit_Click(sender, e);
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new LoginEditDialog(null) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var id = "login-" + ConnectionStore.NewId();
        try
        {
            _vault.Set(id, dialog.Username, dialog.Password);
            _store.AddLogin(new LoginEntry(id, dialog.LoginName, dialog.Username));
            Reload(id);
        }
        catch (Exception ex) when (ex is CatalogException or VaultException)
        {
            _vault.Remove(id);
            MessageBox.Show(this, ex.Message, "Saved logins", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } login)
        {
            return;
        }

        var dialog = new LoginEditDialog(login) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var updated = login with { Name = dialog.LoginName, Username = dialog.Username };
            _store.UpdateLogin(updated);
            if (dialog.Password.Length > 0)
            {
                _vault.Set(login.Id, dialog.Username, dialog.Password);
            }
            else
            {
                // Keep the password, but store the new user name alongside it.
                string? current = null;
                await _vault.UseAsync(login.Id, c =>
                {
                    c.ReadPassword(secret => current = secret.ToString());
                    return ValueTask.FromResult(true);
                });
                if (current is not null)
                {
                    _vault.Set(login.Id, dialog.Username, current);
                }
            }

            Reload(login.Id);
        }
        catch (Exception ex) when (ex is CatalogException or VaultException or KeyNotFoundException)
        {
            MessageBox.Show(this, ex.Message, "Saved logins", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } login)
        {
            return;
        }

        var uses = _store.UsesOfCredential(login.Id);
        var message = uses == 0
            ? $"Delete the saved login \"{login.Name}\"? Its password is removed from the vault."
            : $"Delete the saved login \"{login.Name}\" from this list?\n\n{uses} connection(s) use it. They keep working: the password stays in the vault for them, but it is no longer listed here.";
        if (MessageBox.Show(this, message, "Saved logins", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _store.RemoveLogin(login.Id);
        if (!_store.ReferencedCredentialIds().Contains(login.Id))
        {
            _vault.Remove(login.Id);
        }

        Reload();
    }
}
