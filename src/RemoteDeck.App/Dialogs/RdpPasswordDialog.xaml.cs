using RemoteDeck.App.Services;
using System.Windows;

namespace RemoteDeck.App.Dialogs;

/// <summary>Asks for the Remote Desktop password; it goes to the session and is kept only if the user asks.</summary>
public partial class RdpPasswordDialog : Window
{
    public RdpPasswordDialog(string user, string host)
    {
        InitializeComponent();
        WindowFit.ToScreen(this);
        Heading.Text = string.IsNullOrWhiteSpace(user) ? $"Connect to {host}" : $"Connect to {host} as {user}";
        Loaded += (_, _) => PasswordBox.Focus();
    }

    public string Password { get; private set; } = string.Empty;

    /// <summary>True when the user wants the password kept in the vault.</summary>
    public bool Remember { get; private set; }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        Password = PasswordBox.Password;
        Remember = RememberBox.IsChecked == true && Password.Length > 0;
        DialogResult = true;
    }
}
