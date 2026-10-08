using RemoteDeck.App.Services;
using System.Windows;
using Microsoft.Win32;

namespace RemoteDeck.App.Dialogs;

public partial class LoginDialog : Window
{
    public LoginDialog(string user, string host)
    {
        InitializeComponent();
        WindowFit.ToScreen(this);
        Heading.Text = $"Connect to {host} as {user}";
        Loaded += (_, _) => PasswordBox.Focus();
    }

    public string Password { get; private set; } = string.Empty;

    public string? KeyFile { get; private set; }

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

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        Password = PasswordBox.Password;
        KeyFile = string.IsNullOrWhiteSpace(KeyBox.Text) ? null : KeyBox.Text.Trim();

        if (Password.Length == 0 && KeyFile is null)
        {
            MessageBox.Show(this, "Enter a password, or choose a private key.", "Sign in", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
