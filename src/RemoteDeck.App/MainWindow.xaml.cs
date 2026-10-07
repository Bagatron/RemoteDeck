using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RemoteDeck.App.Dialogs;
using RemoteDeck.App.Services;
using RemoteDeck.App.Terminals;
using RemoteDeck.Core.Broadcast;
using RemoteDeck.Core.Connections;
using RemoteDeck.Core.Layout;
using RemoteDeck.Core.Themes;
using RemoteDeck.Plugin;
using RemoteDeck.Protocols.Ssh;
using RemoteDeck.Vault;

namespace RemoteDeck.App;

public partial class MainWindow : Window
{
    private readonly AppData _data;
    private readonly TerminalHost _terminals;
    private readonly SshConnectionFactory _ssh;

    internal MainWindow(AppData data)
    {
        InitializeComponent();
        DataContext = this;
        _data = data;

        Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x2E, 0x34, 0x40);
        _terminals = new TerminalHost(Web);
        _ssh = new SshConnectionFactory(new FileHostKeyStore(AppPaths.KnownHosts), new DialogHostKeyPrompt(Dispatcher, () => this));

        _terminals.Input += OnTerminalInput;
        _terminals.Pasted += OnTerminalPasted;
        _terminals.Resized += OnTerminalResized;
        _terminals.Focused += OnTerminalFocused;
        _terminals.RatioChanged += OnRatioChanged;
        _terminals.PaneAction += OnPaneAction;

        RefreshTree();

        App.Themes.Applied += OnThemeApplied;

        Loaded += async (_, _) =>
        {
            try
            {
                await _terminals.InitializeAsync();
                _terminals.SetTheme(App.Themes.Current);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "The terminal view could not start. RemoteDeck needs the Microsoft Edge WebView2 runtime.\n\n" + ex.Message,
                    "RemoteDeck",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };

        Closed += (_, _) =>
        {
            App.Themes.Applied -= OnThemeApplied;

            foreach (var tab in Tabs.ToArray())
            {
                foreach (var session in tab.Sessions)
                {
                    Discard(tab, session);
                }
            }

            _data.Vault.Lock();
        };
    }

    public ObservableCollection<WorkspaceTab> Tabs { get; } = new();

    private WorkspaceTab? Current => TabStrip.SelectedItem as WorkspaceTab;

    // ---- themes ----

    private void OnThemeApplied(Theme theme)
    {
        _terminals.SetTheme(theme);
        if (App.Themes.Problems.Count > 0)
        {
            SetStatus("Theme file problem: " + App.Themes.Problems[0]);
        }
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ThemeButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };

        foreach (var theme in App.Themes.Themes)
        {
            var item = new MenuItem
            {
                Header = theme.Name,
                IsCheckable = true,
                IsChecked = theme.Name == App.Themes.Current.Name,
            };
            var name = theme.Name;
            item.Click += (_, _) => App.Themes.Select(name);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var folder = new MenuItem { Header = "Open my themes folder" };
        folder.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{App.Themes.UserFolder}\"") { UseShellExecute = true });
        menu.Items.Add(folder);

        var reload = new MenuItem { Header = "Reload themes" };
        reload.Click += (_, _) => App.Themes.Reload();
        menu.Items.Add(reload);

        if (App.Themes.Problems.Count > 0)
        {
            var problems = new MenuItem { Header = $"Theme problems ({App.Themes.Problems.Count})..." };
            problems.Click += (_, _) => MessageBox.Show(
                this,
                string.Join("\n\n", App.Themes.Problems),
                "Theme problems",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            menu.Items.Add(problems);
        }

        menu.IsOpen = true;
    }

    // ---- sidebar ----

    private void RefreshTree()
    {
        Tree.Items.Clear();

        if (!string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            foreach (var result in _data.Store.Search(SearchBox.Text))
            {
                Tree.Items.Add(MakeConnectionItem(result.Connection, result.FolderPath));
            }

            return;
        }

        foreach (var node in _data.Store.BuildTree())
        {
            Tree.Items.Add(MakeItem(node));
        }
    }

    private TreeViewItem MakeItem(CatalogNode node)
    {
        if (node is ConnectionNode connection)
        {
            return MakeConnectionItem(connection.Connection, null);
        }

        var folder = (FolderNode)node;
        var item = new TreeViewItem
        {
            Header = new TextBlock { Text = "\U0001F4C1  " + folder.Folder.Name, FontWeight = FontWeights.SemiBold },
            Tag = folder.Folder,
            IsExpanded = true,
        };

        foreach (var child in folder.Children)
        {
            item.Items.Add(MakeItem(child));
        }

        return item;
    }

    private TreeViewItem MakeConnectionItem(ConnectionEntry entry, string? folderPath)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        if (entry.Favorite)
        {
            header.Children.Add(new TextBlock { Text = "★ ", Foreground = Brushes.Goldenrod });
        }

        header.Children.Add(new TextBlock { Text = entry.Name });
        header.Children.Add(new TextBlock
        {
            Text = "   " + (folderPath is { Length: > 0 } ? folderPath + "  ·  " : string.Empty) + entry.Host,
            Foreground = (Brush)FindResource("TextDim"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new TreeViewItem { Header = header, Tag = entry };
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshTree();

    private string? SelectedFolderId() => Tree.SelectedItem is TreeViewItem { Tag: var tag }
        ? tag switch
        {
            FolderEntry folder => folder.Id,
            ConnectionEntry connection => connection.FolderId,
            _ => null,
        }
        : null;

    private static TreeViewItem? FindItem(DependencyObject? source)
    {
        while (source is not null && source is not TreeViewItem)
        {
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return source as TreeViewItem;
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindItem(e.OriginalSource as DependencyObject);
        if (item is { Tag: ConnectionEntry entry } && item.IsSelected)
        {
            e.Handled = true;
            _ = OpenSavedAsync(entry);
        }
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Tree.SelectedItem is TreeViewItem { Tag: ConnectionEntry entry })
        {
            e.Handled = true;
            _ = OpenSavedAsync(entry);
        }
        else if (e.Key == Key.Delete && Tree.SelectedItem is TreeViewItem { Tag: not null } item)
        {
            e.Handled = true;
            Delete(item.Tag);
        }
    }

    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindItem(e.OriginalSource as DependencyObject) is { } item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void TreeMenu_Opened(object sender, RoutedEventArgs e)
    {
        var tag = (Tree.SelectedItem as TreeViewItem)?.Tag;
        var connection = tag is ConnectionEntry;
        var folder = tag is FolderEntry;

        MenuOpen.Visibility = connection ? Visibility.Visible : Visibility.Collapsed;
        MenuOpenNew.Visibility = connection ? Visibility.Visible : Visibility.Collapsed;
        MenuEdit.Visibility = connection ? Visibility.Visible : Visibility.Collapsed;
        MenuRename.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;
        MenuDelete.Visibility = connection || folder ? Visibility.Visible : Visibility.Collapsed;

        if (!connection && !folder)
        {
            ((ContextMenu)sender).IsOpen = false;
        }
    }

    private void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (Tree.SelectedItem is TreeViewItem { Tag: ConnectionEntry entry })
        {
            _ = OpenSavedAsync(entry);
        }
    }

    private void MenuOpenNew_Click(object sender, RoutedEventArgs e)
    {
        if (Tree.SelectedItem is TreeViewItem { Tag: ConnectionEntry entry })
        {
            _ = OpenSavedAsync(entry, newTab: true);
        }
    }

    private void MenuEdit_Click(object sender, RoutedEventArgs e)
    {
        if (Tree.SelectedItem is TreeViewItem { Tag: ConnectionEntry entry })
        {
            EditConnection(entry);
        }
    }

    private void MenuRename_Click(object sender, RoutedEventArgs e)
    {
        if (Tree.SelectedItem is TreeViewItem { Tag: FolderEntry folder })
        {
            var dialog = new NameDialog("Rename folder", "Folder name", folder.Name) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                Try(() => _data.Store.UpdateFolder(folder with { Name = dialog.Value }));
                RefreshTree();
            }
        }
    }

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Tree.SelectedItem is TreeViewItem { Tag: not null } item)
        {
            Delete(item.Tag);
        }
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NameDialog("New folder", "Folder name") { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            Try(() => _data.Store.AddFolder(new FolderEntry(ConnectionStore.NewId(), dialog.Value, SelectedFolderId())));
            RefreshTree();
        }
    }

    private void NewConnection_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConnectionDialog(_data.Store, null, SelectedFolderId()) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            SaveConnection(dialog, null);
        }
    }

    private void EditConnection(ConnectionEntry entry)
    {
        var dialog = new ConnectionDialog(_data.Store, entry, null) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            SaveConnection(dialog, entry);
        }
    }

    private void SaveConnection(ConnectionDialog dialog, ConnectionEntry? existing)
    {
        var entry = dialog.Result!;

        // The password goes into the encrypted vault; the saved connection only remembers which vault entry to use.
        var credentialId = existing?.CredentialId;
        if (dialog.Password.Length > 0)
        {
            credentialId ??= "cred-" + entry.Id;
            _data.Vault.Set(credentialId, null, dialog.Password);
        }

        entry = entry with { CredentialId = credentialId };
        Try(() =>
        {
            if (existing is null)
            {
                _data.Store.AddConnection(entry);
            }
            else
            {
                _data.Store.UpdateConnection(entry);
            }
        });

        RefreshTree();
    }

    private void Delete(object tag)
    {
        switch (tag)
        {
            case ConnectionEntry entry:
                if (Confirm($"Delete the connection \"{entry.Name}\"?"))
                {
                    Try(() => _data.Store.RemoveConnection(entry.Id));
                    if (entry.CredentialId is { } id && !_data.Store.ReferencedCredentialIds().Contains(id))
                    {
                        _data.Vault.Remove(id);
                    }
                }

                break;

            case FolderEntry folder:
                if (Confirm($"Delete the folder \"{folder.Name}\"? Its connections move up one level; nothing inside is deleted."))
                {
                    Try(() => _data.Store.RemoveFolder(folder.Id, deleteContents: false));
                }

                break;
        }

        RefreshTree();
    }

    private bool Confirm(string text) =>
        MessageBox.Show(this, text, "RemoteDeck", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (CatalogException ex)
        {
            MessageBox.Show(this, ex.Message, "RemoteDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- opening sessions ----

    private Task OpenSavedAsync(ConnectionEntry entry, bool newTab = false)
    {
        if (!string.Equals(entry.Type, "ssh", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, $"\"{entry.Type}\" connections are not supported yet. Only SSH is.", "RemoteDeck", MessageBoxButton.OK, MessageBoxImage.Information);
            return Task.CompletedTask;
        }

        var definition = _data.Store.ResolveDefinition(entry.Id);
        if (definition is null)
        {
            return Task.CompletedTask;
        }

        var broker = new VaultCredentialBroker(_data.Vault, _data.Store.CredentialIdFor);
        return StartSessionAsync(definition, broker, entry.Name, newTab);
    }

    private Task OpenQuickAsync(string address)
    {
        if (!Target.TryParse(address, out var user, out var host, out var port))
        {
            MessageBox.Show(this, "Type the address as user@host or user@host:port.", "RemoteDeck", MessageBoxButton.OK, MessageBoxImage.Information);
            return Task.CompletedTask;
        }

        var login = new LoginDialog(user, host) { Owner = this };
        if (login.ShowDialog() != true)
        {
            return Task.CompletedTask;
        }

        var options = new Dictionary<string, string> { ["username"] = user };
        if (login.KeyFile is not null)
        {
            options["privateKeyPath"] = login.KeyFile;
        }

        var definition = new ConnectionDefinition(
            Guid.NewGuid().ToString("N"),
            $"{user}@{host}",
            "ssh",
            host,
            port,
            login.Password.Length > 0 ? "login" : null,
            options);

        AddressBox.Clear();
        return StartSessionAsync(definition, new SingleCredentialBroker(user, login.Password), host, newTab: false);
    }

    /// <summary>Opens a session in an empty pane of the current tab (the focused one first), or in a new tab if there is none.</summary>
    private async Task StartSessionAsync(ConnectionDefinition definition, ICredentialBroker broker, string title, bool newTab)
    {
        WorkspaceTab tab;
        PaneSession pane;
        var current = newTab ? null : Current;
        if (current?.PickEmptyPane() is { } empty)
        {
            tab = current;
            pane = empty;
        }
        else
        {
            tab = NewTab(LayoutPreset.Single);
            pane = tab.Sessions[0];
        }

        var connection = (ITerminalConnection)_ssh.Create(definition, broker);
        pane.Connection = connection;
        pane.Title = title;
        pane.State = ConnectionState.Connecting;
        tab.Router.Register(pane.TerminalId, connection);
        tab.FocusedTerminalId = pane.TerminalId;

        connection.StateChanged += (_, state) => Dispatcher.InvokeAsync(() =>
        {
            if (pane.Connection != connection)
            {
                return;
            }

            pane.State = state;
            SyncTab(tab);
            if (state == ConnectionState.Disconnected)
            {
                Write(pane, "\r\n\u001b[2m[connection closed]\u001b[0m\r\n");
            }
        });

        connection.Output.Subscribe(new DelegateObserver(data => Dispatcher.InvokeAsync(() =>
        {
            if (pane.Connection == connection)
            {
                _terminals.Output(pane.TerminalId, data);
            }
        })));

        connection.Resize(pane.Columns, pane.Rows);
        SyncTab(tab);
        _terminals.FocusPane(pane.TerminalId);

        try
        {
            // Off the UI thread: the host-key question blocks the connecting thread until you answer it.
            await Task.Run(() => connection.ConnectAsync().AsTask());
        }
        catch (SshConnectionException ex)
        {
            Write(pane, $"\r\n\u001b[31m{ex.Message}\u001b[0m\r\n");
        }
        catch (Exception ex) when (ex is VaultException or KeyNotFoundException)
        {
            Write(pane, $"\r\n\u001b[31mCould not read the saved password: {ex.Message}\u001b[0m\r\n");
        }
        catch (Exception ex)
        {
            Write(pane, $"\r\n\u001b[31mUnexpected error: {ex.Message}\u001b[0m\r\n");
        }
    }

    // ---- tabs, panes and layouts ----

    private WorkspaceTab NewTab(LayoutPreset preset)
    {
        var tab = new WorkspaceTab(preset);
        tab.Router.StateChanged += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (Tabs.Contains(tab))
            {
                SyncTab(tab);
                if (tab == Current)
                {
                    UpdateBroadcastUi();
                }
            }
        });

        // The page must know the tab before it is told to show it.
        _terminals.Sync(tab);
        Tabs.Add(tab);
        TabStrip.SelectedItem = tab;
        return tab;
    }

    private void SyncTab(WorkspaceTab tab)
    {
        tab.Refresh();
        _terminals.Sync(tab);
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => NewTab(LayoutPreset.Single);

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceTab tab })
        {
            e.Handled = true;
            CloseTab(tab);
        }
    }

    private void CloseTab(WorkspaceTab tab)
    {
        var running = tab.Sessions.Count(s => !s.IsEmpty);
        if (running > 1 && !Confirm($"This tab has {running} open sessions. Close them all?"))
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        foreach (var session in tab.Sessions)
        {
            Discard(tab, session);
        }

        tab.Router.Reset();
        Tabs.Remove(tab);
        _terminals.CloseTab(tab.Id);

        if (Tabs.Count > 0)
        {
            TabStrip.SelectedItem = Tabs[Math.Min(index, Tabs.Count - 1)];
        }
        else
        {
            UpdateBroadcastUi();
        }
    }

    private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Current is { } tab)
        {
            _terminals.ShowTab(tab.Id);
        }

        UpdateBroadcastUi();
    }

    /// <summary>Ends a pane's session but keeps the pane (and its terminal) in the layout.</summary>
    private void EndSession(WorkspaceTab tab, PaneSession session)
    {
        var connection = session.Connection;
        tab.Router.Unregister(session.TerminalId);
        session.Connection = null;
        session.Title = null;
        session.State = ConnectionState.Disconnected;
        if (connection is not null)
        {
            _ = Task.Run(async () => await connection.DisposeAsync());
        }
    }

    /// <summary>Ends a pane's session and forgets the pane for good.</summary>
    private void Discard(WorkspaceTab tab, PaneSession session)
    {
        EndSession(tab, session);
        session.Stop();
    }

    private void OnPaneAction(string terminalId, string action)
    {
        var (tab, session) = Find(terminalId);
        if (tab is null || session is null)
        {
            return;
        }

        switch (action)
        {
            case "splitRight":
            case "splitDown":
                tab.Split(terminalId, action == "splitRight" ? SplitOrientation.Columns : SplitOrientation.Rows);
                SyncTab(tab);
                break;

            case "member":
                if (!session.IsEmpty)
                {
                    tab.Router.SetMember(terminalId, !tab.Router.Members.Contains(terminalId));
                }

                break;

            case "close":
                if (!session.IsEmpty)
                {
                    // End the session but keep the pane, so a grid stays a grid.
                    EndSession(tab, session);
                    SyncTab(tab);
                }
                else if (tab.Sessions.Count > 1)
                {
                    if (tab.RemovePane(terminalId) is { } removed)
                    {
                        removed.Stop();
                    }

                    SyncTab(tab);
                }
                else
                {
                    CloseTab(tab);
                }

                break;
        }
    }

    private (WorkspaceTab? Tab, PaneSession? Session) Find(string terminalId)
    {
        foreach (var tab in Tabs)
        {
            if (tab.FindByTerminal(terminalId) is { } session)
            {
                return (tab, session);
            }
        }

        return (null, null);
    }

    private void LayoutButton_Click(object sender, RoutedEventArgs e)
    {
        LayoutButton.ContextMenu.PlacementTarget = LayoutButton;
        LayoutButton.ContextMenu.IsOpen = true;
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string name } || !Enum.TryParse<LayoutPreset>(name, out var preset))
        {
            return;
        }

        var tab = Current;
        if (tab is null)
        {
            NewTab(preset);
            return;
        }

        var lost = tab.SessionsLostBy(preset);
        if (lost > 0 && !Confirm($"That layout has fewer panes than you have sessions open. {lost} session(s) will be closed. Continue?"))
        {
            return;
        }

        foreach (var dropped in tab.ApplyPreset(preset))
        {
            Discard(tab, dropped);
        }

        tab.Router.Reset();
        SyncTab(tab);
        UpdateBroadcastUi();
    }

    private void OnRatioChanged(string tabId, IReadOnlyList<int> path, double ratio)
    {
        Tabs.FirstOrDefault(t => t.Id == tabId)?.SetRatio(path, ratio);
    }

    private void OnTerminalFocused(string terminalId)
    {
        var (tab, _) = Find(terminalId);
        if (tab is not null)
        {
            tab.FocusedTerminalId = terminalId;
        }
    }

    private void OnTerminalResized(string terminalId, int columns, int rows)
    {
        var (_, session) = Find(terminalId);
        if (session is null)
        {
            return;
        }

        session.Columns = columns;
        session.Rows = rows;
        session.Connection?.Resize(columns, rows);
    }

    // ---- input and broadcast ----

    private void OnTerminalInput(string terminalId, byte[] data)
    {
        var (tab, session) = Find(terminalId);
        if (tab is null || session is null || session.IsEmpty)
        {
            return;
        }

        // Goes to every pane in the group while live broadcast is on and this pane is in it, otherwise just here.
        session.Enqueue(async () => await tab.Router.RouteInputAsync(terminalId, data));
    }

    private void OnTerminalPasted(string terminalId, string text)
    {
        var (tab, session) = Find(terminalId);
        if (tab is null || session is null || session.IsEmpty)
        {
            return;
        }

        session.Enqueue(async () =>
        {
            var result = await tab.Router.RoutePasteAsync(terminalId, text, ConfirmRisk);
            if (result.WasBlocked)
            {
                await Dispatcher.InvokeAsync(() => SetStatus("Paste cancelled: nothing was sent."));
            }
        });
    }

    private void BroadcastToggle_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } tab)
        {
            return;
        }

        var turnOn = BroadcastToggle.IsChecked == true;
        if (turnOn && tab.Router.Members.Count == 0)
        {
            // The switch only mirrors typing between panes that are in the group, so offer to put them all in.
            var running = tab.Sessions.Where(s => !s.IsEmpty).ToList();
            if (running.Count < 2)
            {
                BroadcastToggle.IsChecked = false;
                SetStatus("Broadcast needs at least two open sessions in this tab.");
                return;
            }

            if (!Confirm($"No panes are in the broadcast group yet. Add all {running.Count} open sessions to it?"))
            {
                BroadcastToggle.IsChecked = false;
                SetStatus("Click the broadcast icon in each pane header to choose which panes join the group.");
                return;
            }

            foreach (var session in running)
            {
                tab.Router.SetMember(session.TerminalId, true);
            }
        }

        tab.Router.SetEnabled(turnOn);
    }

    private void CommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = SendCommandAsync();
        }
    }

    private void SendCommand_Click(object sender, RoutedEventArgs e) => _ = SendCommandAsync();

    private async Task SendCommandAsync()
    {
        var tab = Current;
        var text = CommandBox.Text;
        if (tab is null || text.Length == 0)
        {
            return;
        }

        if (tab.Router.Members.Count == 0)
        {
            SetStatus("No panes are in the group yet. Click the broadcast icon in a pane header to add it.");
            return;
        }

        var result = await tab.Router.SendCommandAsync(text, ConfirmRisk);
        if (result.WasBlocked)
        {
            SetStatus("Cancelled: nothing was sent.");
            return;
        }

        CommandBox.Clear();
        var failed = result.Failures
            .Select(f => Find(f.PaneId).Session?.Title ?? "a pane")
            .ToArray();
        SetStatus(failed.Length == 0
            ? $"Sent to {result.Sent} pane(s)."
            : $"Sent to {result.Sent} of {result.Attempted}. Failed: {string.Join(", ", failed)}.");
    }

    private ValueTask<bool> ConfirmRisk(BroadcastRisk risk) =>
        new(Dispatcher.InvokeAsync(() => AskRisk(risk)).Task);

    private bool AskRisk(BroadcastRisk risk)
    {
        var text = risk.Kind == BroadcastRiskKind.RiskyCommand
            ? $"This looks dangerous: {risk.Detail}.\n\nSend it to every pane in the group?"
            : "This text has several lines and will go to every pane in the group.\n\nSend it?";

        return MessageBox.Show(this, text, "Broadcast", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            == MessageBoxResult.Yes;
    }

    private void UpdateBroadcastUi()
    {
        var tab = Current;
        if (tab is null)
        {
            BroadcastToggle.IsChecked = false;
            BroadcastToggle.IsEnabled = false;
            BroadcastToggle.Content = "Broadcast: off";
            BroadcastToggle.Foreground = (Brush)FindResource("Text");
            SetStatus(string.Empty);
            return;
        }

        var on = tab.Router.Enabled;
        var members = tab.Router.Members.Count;
        BroadcastToggle.IsEnabled = true;
        BroadcastToggle.IsChecked = on;
        BroadcastToggle.Content = on ? "Broadcast: ON" : "Broadcast: off";
        BroadcastToggle.Foreground = on ? Brushes.IndianRed : (Brush)FindResource("Text");
        SetStatus(members switch
        {
            0 => "No panes in the group",
            1 when on => "Only 1 pane is in the group, so nothing is mirrored. Click the broadcast icon in the other panes' headers.",
            _ => $"{members} pane(s) in the group",
        });
    }

    private void SetStatus(string text) => BroadcastStatus.Text = text;

    // ---- quick connect ----

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = OpenQuickAsync(AddressBox.Text);
        }
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => _ = OpenQuickAsync(AddressBox.Text);

    private void Write(PaneSession pane, string text) =>
        _terminals.Output(pane.TerminalId, Encoding.UTF8.GetBytes(text));
}
