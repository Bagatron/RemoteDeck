using System.Windows;

namespace RemoteDeck.App.Dialogs;

public partial class UnlockDialog : Window
{
    private const int MinimumLength = 8;

    private readonly bool _creating;

    public UnlockDialog(bool creating)
    {
        InitializeComponent();
        _creating = creating;

        if (creating)
        {
            Heading.Text = "Create your vault";
            Explanation.Text = "Passwords you save are encrypted with this master password. "
                + "It is never stored, so it cannot be recovered if you forget it.";
            ConfirmPanel.Visibility = Visibility.Visible;
            OkButton.Content = "Create";
        }
        else
        {
            Heading.Text = "Unlock RemoteDeck";
            Explanation.Text = "Enter your master password to open your saved passwords.";
            OkButton.Content = "Unlock";
        }

        Loaded += (_, _) => PasswordBox.Focus();
    }

    public string Password { get; private set; } = string.Empty;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Password;

        if (_creating)
        {
            if (password.Length < MinimumLength)
            {
                MessageBox.Show(this, $"Use at least {MinimumLength} characters.", "RemoteDeck", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (password != ConfirmBox.Password)
            {
                MessageBox.Show(this, "The two passwords are different.", "RemoteDeck", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        else if (password.Length == 0)
        {
            return;
        }

        Password = password;
        DialogResult = true;
    }
}
