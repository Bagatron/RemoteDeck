using System.Windows;

namespace RemoteDeck.App.Dialogs;

/// <summary>Asks for the Remote Desktop password each time; it is passed to the session and never stored.</summary>
public partial class RdpPasswordDialog : Window
{
    public RdpPasswordDialog(string user, string host)
    {
        InitializeComponent();
        Heading.Text = string.IsNullOrWhiteSpace(user) ? $"Connect to {host}" : $"Connect to {host} as {user}";
        Loaded += (_, _) => PasswordBox.Focus();
    }

    public string Password { get; private set; } = string.Empty;

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        Password = PasswordBox.Password;
        DialogResult = true;
    }
}
