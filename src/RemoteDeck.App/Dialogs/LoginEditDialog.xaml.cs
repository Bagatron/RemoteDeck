using System.Windows;
using RemoteDeck.App.Services;
using RemoteDeck.Core.Connections;

namespace RemoteDeck.App.Dialogs;

/// <summary>Adds or edits one saved login: a name, a user name and a password.</summary>
public partial class LoginEditDialog : Window
{
    private readonly LoginEntry? _existing;

    internal LoginEditDialog(LoginEntry? existing)
    {
        InitializeComponent();
        WindowFit.ToScreen(this);
        _existing = existing;
        Title = existing is null ? "New saved login" : "Edit saved login";
        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            UserBox.Text = existing.Username ?? string.Empty;
            PasswordLabel.Text = "Password (leave empty to keep the saved one)";
        }

        Loaded += (_, _) => NameBox.Focus();
    }

    public string LoginName { get; private set; } = string.Empty;

    public string Username { get; private set; } = string.Empty;

    /// <summary>What was typed; empty means "keep the saved password" when editing.</summary>
    public string Password { get; private set; } = string.Empty;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Give the login a name.", "RemoteDeck", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_existing is null && PasswordBox.Password.Length == 0)
        {
            MessageBox.Show(this, "Enter the password to keep.", "RemoteDeck", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        LoginName = name;
        Username = UserBox.Text.Trim();
        Password = PasswordBox.Password;
        DialogResult = true;
    }
}
