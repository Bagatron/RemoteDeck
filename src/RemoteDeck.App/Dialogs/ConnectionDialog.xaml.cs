using RemoteDeck.App.Services;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RemoteDeck.Core.Connections;
using RemoteDeck.Plugin;

namespace RemoteDeck.App.Dialogs;

/// <summary>Add or edit a saved SSH connection. The password is returned separately and goes to the vault, never into the entry.</summary>
public partial class ConnectionDialog : Window
{
    private sealed record FolderChoice(string? Id, string Label);

    private sealed record JumpChoice(string? Id, string Label);

    private readonly ConnectionEntry? _existing;

    /// <summary>Connection types provided by running plugins; offered next to SSH and RDP.</summary>
    internal static IReadOnlyList<IConnectionFactory> ExtraTypes { get; set; } = Array.Empty<IConnectionFactory>();

    internal ConnectionDialog(ConnectionStore store, ConnectionEntry? existing, string? defaultFolderId)
    {
        InitializeComponent();
        WindowFit.ToScreen(this);
        _existing = existing;
        Title = existing is null ? "New connection" : "Edit connection";

        var choices = new List<FolderChoice> { new(null, "(top level)") };
        choices.AddRange(store.Folders
            .Select(f => new FolderChoice(f.Id, store.FolderPath(f.Id)))
            .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase));
        FolderBox.ItemsSource = choices;

        var folderId = existing is null ? defaultFolderId : existing.FolderId;
        FolderBox.SelectedItem = choices.FirstOrDefault(c => c.Id == folderId) ?? choices[0];

        var jumps = new List<JumpChoice> { new(null, "(connect directly)") };
        jumps.AddRange(store.Connections
            .Where(c => string.Equals(c.Type, "ssh", StringComparison.OrdinalIgnoreCase) && c.Id != existing?.Id)
            .Select(c => new JumpChoice(c.Id, c.Name))
            .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase));
        var savedJump = existing is null ? string.Empty : Option(existing, "proxyJump");
        if (savedJump.Length > 0 && jumps.All(j => j.Id != savedJump))
        {
            jumps.Add(new JumpChoice(savedJump, "(deleted connection)"));
        }

        JumpBox.ItemsSource = jumps;
        JumpBox.SelectedItem = jumps.FirstOrDefault(j => j.Id == (savedJump.Length > 0 ? savedJump : null)) ?? jumps[0];

        foreach (var factory in ExtraTypes)
        {
            TypeBox.Items.Add(new ComboBoxItem { Content = factory.DisplayName, Tag = factory.Type });
        }

        var type = existing?.Type ?? "ssh";
        var match = TypeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == type);
        if (match is null)
        {
            // A saved connection of a type whose plugin is not running: keep its type when editing.
            match = new ComboBoxItem { Content = type + " (plugin not running)", Tag = type };
            TypeBox.Items.Add(match);
            TypeBox.IsEnabled = false;
        }

        TypeBox.SelectedItem = match;

        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            HostBox.Text = existing.Host;
            PortBox.Text = existing.Port?.ToString() ?? string.Empty;
            UserBox.Text = Option(existing, "username");
            KeyBox.Text = Option(existing, "privateKeyPath");
            ReconnectBox.IsChecked = string.Equals(Option(existing, "autoReconnect"), "true", StringComparison.OrdinalIgnoreCase);
            AgentBox.IsChecked = string.Equals(Option(existing, "useAgent"), "true", StringComparison.OrdinalIgnoreCase);
            CertBox.IsChecked = string.Equals(Option(existing, "acceptUntrustedCertificate"), "true", StringComparison.OrdinalIgnoreCase);
            LocalEchoBox.IsChecked = string.Equals(Option(existing, "localEcho"), "true", StringComparison.OrdinalIgnoreCase);
            ExternalBox.IsChecked = string.Equals(Option(existing, "externalClient"), "true", StringComparison.OrdinalIgnoreCase);
            LogBox.IsChecked = string.Equals(Option(existing, "logSession"), "true", StringComparison.OrdinalIgnoreCase);
            ForwardsBox.Text = Option(existing, "forwards").Replace("\n", Environment.NewLine);
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

    private string SelectedType => (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "ssh";

    private bool IsSsh => SelectedType == "ssh";

    private bool IsWeb => SelectedType == "web";

    private bool IsTelnet => SelectedType == "telnet";

    private bool IsRdp => (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string == "rdp";

    private void TypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PortBox is null || JumpPanel is null || LoginPanel is null || WebPanel is null || HostLabel is null || SecretPanel is null || RdpPanel is null || TelnetPanel is null)
        {
            return;
        }

        LoginPanel.Visibility = IsWeb || IsTelnet ? Visibility.Collapsed : Visibility.Visible;
        TelnetPanel.Visibility = IsTelnet ? Visibility.Visible : Visibility.Collapsed;
        WebPanel.Visibility = IsWeb ? Visibility.Visible : Visibility.Collapsed;
        RdpPanel.Visibility = IsRdp ? Visibility.Visible : Visibility.Collapsed;

        // Remote Desktop asks for the password each time and never keeps one, so there is nothing to enter here.
        SecretPanel.Visibility = IsRdp ? Visibility.Collapsed : Visibility.Visible;
        HostLabel.Text = IsWeb ? "Address (for example pve.lan, or https://pve.lan:8006)" : "Host";

        JumpPanel.Visibility = IsSsh ? Visibility.Visible : Visibility.Collapsed;

        PortBox.ToolTip = IsWeb ? "Leave empty for 443 (https) or 80 (http)" : IsRdp ? "Leave empty for 3389" : IsTelnet ? "Leave empty for 23" : IsSsh ? "Leave empty for 22" : "Leave empty for the default";
    }

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

        if (host.Length == 0 || (IsSsh && user.Length == 0))
        {
            Warn(IsSsh ? "A connection needs a host and a username." : "A connection needs a host.");
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

        if (IsWeb && !WebAddress.TryNormalize(host, port, out _, out var webError))
        {
            Warn(webError);
            return;
        }

        var key = IsSsh ? KeyBox.Text.Trim() : string.Empty;
        var options = new Dictionary<string, string>(_existing?.Options ?? new Dictionary<string, string>());
        if (user.Length > 0)
        {
            options["username"] = user;
        }
        else
        {
            options.Remove("username");
        }

        if (key.Length > 0)
        {
            options["privateKeyPath"] = key;
        }
        else
        {
            options.Remove("privateKeyPath");
        }

        if (IsSsh && (JumpBox.SelectedItem as JumpChoice)?.Id is { } jumpId)
        {
            options["proxyJump"] = jumpId;
        }
        else
        {
            options.Remove("proxyJump");
        }

        var forwards = IsSsh
            ? string.Join(
                "\n",
                ForwardsBox.Text.Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : string.Empty;
        if (forwards.Length > 0)
        {
            try
            {
                // Same rules the connection applies later, so mistakes show up now.
                RemoteDeck.Protocols.Ssh.PortForwardCheck.Validate(forwards);
            }
            catch (RemoteDeck.Protocols.Ssh.SshConnectionException ex)
            {
                Warn(ex.Message);
                return;
            }

            options["forwards"] = forwards;
        }
        else
        {
            options.Remove("forwards");
        }

        if (IsWeb && CertBox.IsChecked == true)
        {
            options["acceptUntrustedCertificate"] = "true";
        }
        else
        {
            options.Remove("acceptUntrustedCertificate");
        }

        if (IsTelnet && LocalEchoBox.IsChecked == true)
        {
            options["localEcho"] = "true";
        }
        else
        {
            options.Remove("localEcho");
        }

        if (IsRdp && ExternalBox.IsChecked == true)
        {
            options["externalClient"] = "true";
        }
        else
        {
            options.Remove("externalClient");
        }

        if (IsSsh && ReconnectBox.IsChecked == true)
        {
            options["autoReconnect"] = "true";
        }
        else
        {
            options.Remove("autoReconnect");
        }

        if (IsSsh && LogBox.IsChecked == true)
        {
            options["logSession"] = "true";
        }
        else
        {
            options.Remove("logSession");
        }

        if (IsSsh && AgentBox.IsChecked == true)
        {
            options["useAgent"] = "true";
        }
        else
        {
            options.Remove("useAgent");
        }

        var hasSecret = PasswordBox.Password.Length > 0 || _existing?.CredentialId is not null;
        if (IsSsh && key.Length == 0 && !hasSecret && AgentBox.IsChecked != true)
        {
            Warn("Enter a password, choose a private key, or use the SSH agent.");
            return;
        }

        var folder = (FolderChoice?)FolderBox.SelectedItem;
        Result = (_existing ?? new ConnectionEntry(ConnectionStore.NewId(), name, "ssh", host)) with
        {
            Type = SelectedType,
            Name = name,
            Host = host,
            Port = port,
            FolderId = folder?.Id,
            Options = options,
            Favorite = FavoriteBox.IsChecked == true
        };
        // Remote Desktop opens in the Windows client, which asks for the password itself, so none is stored.
        Password = IsRdp || IsWeb || IsTelnet ? string.Empty : PasswordBox.Password;
        DialogResult = true;
    }

    private void Warn(string text) =>
        MessageBox.Show(this, text, "RemoteDeck", MessageBoxButton.OK, MessageBoxImage.Information);
}
