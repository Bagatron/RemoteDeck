using System.Windows;

namespace RemoteDeck.App.Dialogs;

/// <summary>Asks for one line of text, e.g. a folder name.</summary>
public partial class NameDialog : Window
{
    public NameDialog(string title, string prompt, string initial = "")
    {
        InitializeComponent();
        Title = title;
        Prompt.Text = prompt;
        NameBox.Text = initial;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string Value { get; private set; } = string.Empty;

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
