using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RemoteDeck.App.Dialogs;
using RemoteDeck.App.Services;
using RemoteDeck.App.Sftp;
using RemoteDeck.App.Terminals;
using RemoteDeck.Core.Broadcast;
using RemoteDeck.Core.Connections;
using RemoteDeck.Core.Logging;
using RemoteDeck.Core.Import;
using RemoteDeck.Core.Layout;
using RemoteDeck.Core.Plugins;
using RemoteDeck.Core.Themes;
using RemoteDeck.Plugin;
using RemoteDeck.Protocols.Ssh;
using RemoteDeck.Protocols.Telnet;
using RemoteDeck.Vault;

namespace RemoteDeck.App;

public partial class MainWindow : Window
{
    private readonly AppData _data;
    private readonly TerminalHost _terminals;
    private readonly SshConnectionFactory _ssh;
    private readonly TelnetConnectionFactory _telnet = new();
    private readonly PluginManager _plugins;

    internal MainWindow(AppData data)
    {
        InitializeComponent();
        DataContext = this;
        _data = data;

        Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x2E, 0x34, 0x40);
        _terminals = new TerminalHost(Web);
        _ssh = new SshConnectionFactory(
            new FileHostKeyStore(AppPaths.KnownHosts),
            new DialogHostKeyPrompt(Dispatcher, () => this),
            id => _data.Store.ResolveDefinition(id));

        _terminals.Input += OnTerminalInput;
        _terminals.Pasted += OnTerminalPasted;
        _terminals.Resized += OnTerminalResized;
        _terminals.Focused += OnTerminalFocused;
        _terminals.RatioChanged += OnRatioChanged;
        _terminals.PaneAction += OnPaneAction;

        RefreshTree();

        App.Themes.Applied += OnThemeApplied;

        _plugins = new PluginManager(
            App.Settings,
            new VaultCredentialBroker(_data.Vault, _data.Store.CredentialIdFor),
            text => Dispatcher.InvokeAsync(() => SetStatus(text)));
        _plugins.Changed += () => ConnectionDialog.ExtraTypes = _plugins.Host.ConnectionTypes.ToList();
        _plugins.Start();
        _terminals.Shortcut += combo => Dispatcher.InvokeAsync(() => RunShortcut(combo));
        PreviewKeyDown += OnWindowKeyDown;

        Loaded += async (_, _) =>
        {
            try
            {
                await _terminals.InitializeAsync();
                _terminals.SetTheme(App.Themes.Current);
                _terminals.SetScrollback(App.Settings.ScrollbackLines);
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

        StartAutoLock();

        Closed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            _idleTimer?.Stop();
            App.Themes.Applied -= OnThemeApplied;
            _plugins.Dispose();

            foreach (var view in _webViews.Values)
            {
                (view as IDisposable)?.Dispose();
            }

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

    /// <summary>The selected tab, whatever kind it is.</summary>
    private WorkspaceTab? SelectedTab => TabStrip.SelectedItem as WorkspaceTab;

    /// <summary>The selected terminal tab; null when nothing is selected or a web tab is. Terminal actions use this.</summary>
    private WorkspaceTab? Current => SelectedTab is { IsEmbedded: false } tab ? tab : null;

    private readonly Dictionary<string, FrameworkElement> _webViews = new(StringComparer.Ordinal);

    // ---- locking ----

    private System.Windows.Threading.DispatcherTimer? _idleTimer;
    private DateTime _lastActivity = DateTime.UtcNow;
    private bool _locked;
    private bool _closing;

    /// <summary>Locks the vault after the configured idle time, and when Windows itself is locked.</summary>
    private void StartAutoLock()
    {
        InputManager.Current.PreProcessInput += (_, e) =>
        {
            if (e.StagingItem.Input is MouseEventArgs or KeyboardEventArgs)
            {
                _lastActivity = DateTime.UtcNow;
            }
        };
        _terminals.Activity += () => _lastActivity = DateTime.UtcNow;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;

        _idleTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _idleTimer.Tick += (_, _) =>
        {
            if (!_locked && RemoteDeck.Core.Security.AutoLock.IsDue(DateTime.UtcNow - _lastActivity, App.Settings.AutoLockMinutes))
            {
                LockNow();
            }
        };
        _idleTimer.Start();
        Closing += (_, _) => _closing = true;
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock)
        {
            Dispatcher.InvokeAsync(LockNow);
        }
    }

    /// <summary>
    /// Wipes the vault key, hides the window's contents and asks for the master password again. Sessions that are
    /// already connected keep running; saved passwords cannot be used (or changed) until you unlock.
    /// </summary>
    private void LockNow()
    {
        if (_locked || _closing)
        {
            return;
        }

        _locked = true;
        _data.Vault.Lock();
        var content = (UIElement)Content;
        content.Visibility = Visibility.Collapsed;
        Title = "RemoteDeck (locked)";

        while (!_closing)
        {
            var dialog = new UnlockDialog(creating: false) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                if (MessageBox.Show(this, "Exit RemoteDeck? Open sessions will be closed.", "RemoteDeck", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    Close();
                    return;
                }

                continue;
            }

            try
            {
                _data.Vault.Unlock(dialog.Password.AsSpan());
                break;
            }
            catch (InvalidMasterPasswordException)
            {
                MessageBox.Show(this, "That is not the master password.", "RemoteDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        if (_closing)
        {
            return;
        }

        content.Visibility = Visibility.Visible;
        Title = "RemoteDeck";
        _locked = false;
        _lastActivity = DateTime.UtcNow;
        if (Current?.FocusedTerminalId is { } terminalId)
        {
            _terminals.FocusPane(terminalId);
        }
    }

    private PaletteItem AutoLockItem(int minutes) =>
        new(
            (minutes == 0 ? "Auto-lock: never" : $"Auto-lock: after {minutes} minutes")
                + (App.Settings.AutoLockMinutes == minutes ? " (current)" : string.Empty),
            string.Empty,
            () =>
            {
                App.Settings.AutoLockMinutes = minutes;
                App.Settings.Save();
                SetStatus(minutes == 0
                    ? "Auto-lock is off. Ctrl+Shift+L still locks on demand."
                    : $"RemoteDeck locks after {minutes} minutes without use, and when Windows locks.");
            });

    // ---- plugins ----

    private void ShowPlugins() => new PluginsWindow(this, _plugins).ShowDialog();

    private void Plugins_Click(object sender, RoutedEventArgs e) => ShowPlugins();

    private async Task RunPluginCommandAsync(PluginCommand command)
    {
        try
        {
            await command.Execute(CancellationToken.None);
        }
        catch (Exception ex)
        {
            SetStatus($"Plugin command \"{command.Title}\" failed: {ex.Message}");
        }
    }

    // ---- workspaces ----

    private WorkspaceLibrary Library => _workspaces ??= new WorkspaceLibrary(AppPaths.Workspaces);

    private WorkspaceLibrary? _workspaces;

    private void SaveWorkspace()
    {
        if (Current is not { } tab)
        {
            MessageBox.Show(this, "Open a tab first; a workspace saves the current tab.", "Workspaces", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new NameDialog("Save workspace", "Name for this layout and its connections") { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var exists = Library.LoadAll().Any(f => string.Equals(f.Workspace?.Name, dialog.Value, StringComparison.OrdinalIgnoreCase));
        if (exists && !Confirm($"A workspace named \"{dialog.Value}\" already exists. Replace it?"))
        {
            return;
        }

        try
        {
            var workspace = tab.ToWorkspace(dialog.Value);
            Library.Save(workspace);
            var saved = LayoutTree.Panes(workspace.Layout).Count(p => p.ConnectionId is not null);
            SetStatus($"Saved workspace \"{dialog.Value}\" ({saved} connection(s)).");
        }
        catch (Exception ex) when (ex is LayoutException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Workspaces", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenWorkspace(Workspace workspace)
    {
        var panes = LayoutTree.Panes(workspace.Layout).ToList();
        var wanted = panes
            .Select(p => (Pane: p, Entry: p.ConnectionId is null ? null : _data.Store.FindConnection(p.ConnectionId)))
            .ToList();
        var missing = wanted.Count(w => w.Pane.ConnectionId is not null && w.Entry is null);
        var connect = wanted.Where(w => w.Entry is not null).ToList();

        if (connect.Count > 0 && !workspace.AutoConnect
            && !Confirm($"Connect {connect.Count} session(s) now? Choose No to open just the layout."))
        {
            connect.Clear();
        }

        var tab = NewTab(new WorkspaceTab(workspace.Layout));
        var members = workspace.BroadcastMembers?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
        var sessions = tab.PanesWithIds().ToDictionary(p => p.PaneId, p => p.Session, StringComparer.Ordinal);

        foreach (var (pane, entry) in connect)
        {
            var session = sessions[pane.Id];
            if (string.Equals(entry!.Type, "rdp", StringComparison.OrdinalIgnoreCase))
            {
                OpenRemoteDesktop(entry);
                continue;
            }

            if (string.Equals(entry.Type, "web", StringComparison.OrdinalIgnoreCase))
            {
                OpenWebPage(entry);
                continue;
            }

            var definition = _data.Store.ResolveDefinition(entry.Id);
            var factory = FactoryFor(entry.Type);
            if (definition is null || factory is null)
            {
                continue;
            }

            var broker = new VaultCredentialBroker(_data.Vault, _data.Store.CredentialIdFor);
            _ = ConnectPaneAsync(tab, session, factory, definition, broker, entry.Name, members.Contains(pane.Id));
        }

        SyncTab(tab);

        // Web pages in the workspace opened as tabs of their own; stay on the workspace tab.
        if (!ReferenceEquals(TabStrip.SelectedItem, tab))
        {
            TabStrip.SelectedItem = tab;
        }

        SetStatus(missing > 0
            ? $"Opened \"{workspace.Name}\"; {missing} connection(s) in it no longer exist."
            : $"Opened \"{workspace.Name}\".");
    }

    private void WorkspacesButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = WorkspacesButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        var files = Library.LoadAll();

        foreach (var file in files.Where(f => f.Workspace is not null))
        {
            var workspace = file.Workspace!;
            var item = new MenuItem { Header = workspace.Name };
            item.Click += (_, _) => OpenWorkspace(workspace);
            menu.Items.Add(item);
        }

        if (files.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        var save = new MenuItem { Header = "Save current tab as workspace..." };
        save.Click += (_, _) => SaveWorkspace();
        menu.Items.Add(save);

        if (files.Any(f => f.Workspace is not null))
        {
            var delete = new MenuItem { Header = "Delete a workspace" };
            foreach (var workspace in files.Where(f => f.Workspace is not null).Select(f => f.Workspace!))
            {
                var name = workspace.Name;
                var entry = new MenuItem { Header = name };
                entry.Click += (_, _) =>
                {
                    if (Confirm($"Delete the workspace \"{name}\"? Your connections are not affected."))
                    {
                        Library.Delete(name);
                    }
                };
                delete.Items.Add(entry);
            }

            menu.Items.Add(delete);
        }

        var folder = new MenuItem { Header = "Open workspaces folder" };
        folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(AppPaths.Workspaces);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Workspaces}\"") { UseShellExecute = true });
        };
        menu.Items.Add(folder);

        var broken = files.Where(f => f.Error is not null).ToList();
        if (broken.Count > 0)
        {
            var problems = new MenuItem { Header = $"Workspace problems ({broken.Count})..." };
            problems.Click += (_, _) => MessageBox.Show(
                this,
                string.Join("\n\n", broken.Select(b => $"{System.IO.Path.GetFileName(b.Path)}: {b.Error}")),
                "Workspace problems",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            menu.Items.Add(problems);
        }

        menu.IsOpen = true;
    }

    // ---- shortcuts and command palette ----

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            return;
        }

        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? "shift+" : string.Empty;
        var name = e.Key switch
        {
            Key.P => "p",
            Key.T => "t",
            Key.W => "w",
            Key.B => "b",
            Key.F => "f",
            Key.L => "l",
            Key.Tab => "tab",
            _ => null,
        };

        if (name is not null && RunShortcut("ctrl+" + shift + name))
        {
            e.Handled = true;
        }
    }

    /// <summary>Runs an app shortcut; returns false when the combination is not one of ours.</summary>
    private bool RunShortcut(string combo)
    {
        switch (combo)
        {
            case "ctrl+shift+p":
                ShowPalette();
                return true;
            case "ctrl+shift+t":
                NewTab(LayoutPreset.Single);
                return true;
            case "ctrl+shift+w":
                if (SelectedTab is { } tab)
                {
                    CloseTab(tab);
                }

                return true;
            case "ctrl+tab":
                StepTab(1);
                return true;
            case "ctrl+shift+tab":
                StepTab(-1);
                return true;
            case "ctrl+shift+b":
                if (BroadcastToggle.IsEnabled)
                {
                    BroadcastToggle.IsChecked = BroadcastToggle.IsChecked != true;
                    BroadcastToggle_Click(BroadcastToggle, new RoutedEventArgs());
                }

                return true;
            case "ctrl+shift+l":
                LockNow();
                return true;
            case "ctrl+shift+f":
                SearchBox.Focus();
                SearchBox.SelectAll();
                return true;
            default:
                return false;
        }
    }

    private void StepTab(int step)
    {
        if (Tabs.Count > 1)
        {
            TabStrip.SelectedIndex = (TabStrip.SelectedIndex + step + Tabs.Count) % Tabs.Count;
        }
    }

    private void ShowPalette()
    {
        var palette = new CommandPalette(this, BuildPalette);
        palette.ShowDialog();
        palette.Chosen?.Invoke();

        // Give the keyboard back to the terminal that had it, unless the action moved focus elsewhere.
        if (palette.Chosen is null && Current?.FocusedTerminalId is { } id)
        {
            _terminals.FocusPane(id);
        }
    }

    private IReadOnlyList<PaletteItem> BuildPalette(string query)
    {
        var actions = new List<PaletteItem>
        {
            new("New tab", "Ctrl+Shift+T", () => NewTab(LayoutPreset.Single)),
            new("Close tab", "Ctrl+Shift+W", () => RunShortcut("ctrl+shift+w")),
            new("Next tab", "Ctrl+Tab", () => StepTab(1)),
            new("Previous tab", "Ctrl+Shift+Tab", () => StepTab(-1)),
            new("Toggle broadcast", "Ctrl+Shift+B", () => RunShortcut("ctrl+shift+b")),
            new("Search connections", "Ctrl+Shift+F", () => RunShortcut("ctrl+shift+f")),
            new("Save current tab as workspace...", string.Empty, SaveWorkspace),
            new("Import connections...", string.Empty, () => Import_Click(this, new RoutedEventArgs())),
            new("Lock now", "Ctrl+Shift+L", LockNow),
            AutoLockItem(5),
            AutoLockItem(15),
            AutoLockItem(60),
            AutoLockItem(0),
            new("Open session logs folder", string.Empty, OpenLogsFolder),
            ScrollbackItem(1_000),
            ScrollbackItem(10_000),
            ScrollbackItem(50_000),
            ScrollbackItem(100_000),
            new("Reload themes", string.Empty, () => App.Themes.Reload()),
            new("Plugins...", string.Empty, ShowPlugins),
        };

        foreach (var preset in Enum.GetNames<LayoutPreset>())
        {
            var name = preset;
            actions.Add(new PaletteItem(
                "Layout: " + System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " "),
                string.Empty,
                () => Preset_Click(new MenuItem { Tag = name }, new RoutedEventArgs())));
        }

        foreach (var command in _plugins.Host.Commands.ToList())
        {
            var captured = command;
            actions.Add(new PaletteItem("Plugin: " + captured.Title, captured.DefaultKeybinding ?? string.Empty, () => _ = RunPluginCommandAsync(captured)));
        }

        foreach (var file in Library.LoadAll().Where(f => f.Workspace is not null))
        {
            var workspace = file.Workspace!;
            actions.Add(new PaletteItem("Workspace: " + workspace.Name, workspace.Description ?? string.Empty, () => OpenWorkspace(workspace)));
        }

        foreach (var theme in App.Themes.Themes)
        {
            var name = theme.Name;
            actions.Add(new PaletteItem("Theme: " + name, string.Empty, () => App.Themes.Select(name)));
        }

        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sftpOnly = words.Length > 0 && words[0].Equals("sftp", StringComparison.OrdinalIgnoreCase);
        if (sftpOnly)
        {
            var rest = string.Join(' ', words.Skip(1));
            return _data.Store.Search(rest, 30)
                .Where(r => r.Connection.Type == "ssh")
                .Select(r =>
                {
                    var entry = r.Connection;
                    return new PaletteItem("SFTP: " + entry.Name, string.IsNullOrEmpty(r.FolderPath) ? entry.Type : r.FolderPath, () => _ = OpenSftpAsync(entry));
                })
                .ToList();
        }

        var matchingActions = actions.Where(a => words.All(w => a.Title.Contains(w, StringComparison.OrdinalIgnoreCase)));
        var connections = _data.Store.Search(query, 30).Select(r =>
        {
            var entry = r.Connection;
            return new PaletteItem(
                "Connect: " + entry.Name,
                string.IsNullOrEmpty(r.FolderPath) ? entry.Type : r.FolderPath,
                () => _ = OpenSavedAsync(entry));
        });

        // With nothing typed, show the actions first; once you type, connections you are probably after come first.
        return words.Length == 0
            ? matchingActions.Concat(connections).ToList()
            : connections.Concat(matchingActions).ToList();
    }

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

    // ---- import ----

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import connections",
            Filter = ConnectionImport.FileDialogFilter,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var result = ConnectionImport.FromFile(dialog.FileName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var problems = result.AddTo(_data.Store);
            RefreshTree();

            var notes = result.Warnings.Concat(problems).ToList();
            SetStatus($"Imported {result.Connections.Count - problems.Count} connection(s)" + (notes.Count > 0 ? $", {notes.Count} note(s)." : "."));
            if (notes.Count > 0)
            {
                MessageBox.Show(this, string.Join("\n", notes.Take(20)), "Import notes", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex) when (ex is RemoteDeck.Core.Connections.CatalogException)
        {
            MessageBox.Show(this, ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
        MenuSftp.Visibility = tag is ConnectionEntry { Type: "ssh" } ? Visibility.Visible : Visibility.Collapsed;
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

    private void MenuSftp_Click(object sender, RoutedEventArgs e)
    {
        if (Tree.SelectedItem is TreeViewItem { Tag: ConnectionEntry entry })
        {
            _ = OpenSftpAsync(entry);
        }
    }

    private async Task OpenSftpAsync(ConnectionEntry entry)
    {
        var definition = _data.Store.ResolveDefinition(entry.Id);
        if (definition is null)
        {
            return;
        }

        var broker = new VaultCredentialBroker(_data.Vault, _data.Store.CredentialIdFor);
        SetStatus($"Connecting to {entry.Name} for files...");
        try
        {
            // Off the UI thread: the host-key question blocks the connecting thread until you answer it.
            var session = await Task.Run(() => _ssh.OpenSftpAsync(definition, broker));
            SetStatus(string.Empty);
            new SftpWindow(this, entry.Name, session).Show();
        }
        catch (SshConnectionException ex)
        {
            SetStatus(string.Empty);
            MessageBox.Show(this, ex.Message, "SFTP", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is VaultException or KeyNotFoundException)
        {
            SetStatus(string.Empty);
            MessageBox.Show(this, "Could not read the saved password: " + ex.Message, "SFTP", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    /// <summary>A palette entry that sets how many lines each terminal keeps; the current choice is marked.</summary>
    private PaletteItem ScrollbackItem(int lines) =>
        new(
            $"Scrollback: {lines:N0} lines" + (App.Settings.ScrollbackLines == lines ? " (current)" : string.Empty),
            string.Empty,
            () =>
            {
                App.Settings.ScrollbackLines = lines;
                App.Settings.Save();
                _terminals.SetScrollback(lines);
                SetStatus($"Terminals now keep {lines:N0} lines of scrollback. Larger values use more memory per pane.");
            });

    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppPaths.Logs}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, ex.Message, "RemoteDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- opening sessions ----

    /// <summary>The factory for a terminal connection type: SSH, or one a running plugin provides.</summary>
    private IConnectionFactory? FactoryFor(string type) =>
        string.Equals(type, "ssh", StringComparison.OrdinalIgnoreCase)
            ? _ssh
            : string.Equals(type, "telnet", StringComparison.OrdinalIgnoreCase)
                ? _telnet
                : _plugins.Host.ConnectionTypes.FirstOrDefault(f => string.Equals(f.Type, type, StringComparison.OrdinalIgnoreCase));

    private Task OpenSavedAsync(ConnectionEntry entry, bool newTab = false)
    {
        if (string.Equals(entry.Type, "web", StringComparison.OrdinalIgnoreCase))
        {
            OpenWebPage(entry);
            return Task.CompletedTask;
        }

        if (string.Equals(entry.Type, "rdp", StringComparison.OrdinalIgnoreCase))
        {
            OpenRemoteDesktop(entry);
            return Task.CompletedTask;
        }

        var factory = FactoryFor(entry.Type);
        if (factory is null)
        {
            MessageBox.Show(
                this,
                $"\"{entry.Type}\" connections can't be opened. Built in: SSH, Telnet, RDP and web pages. Other types come from plugins, which may be turned off (see Plugins).",
                "RemoteDeck",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return Task.CompletedTask;
        }

        var definition = _data.Store.ResolveDefinition(entry.Id);
        if (definition is null)
        {
            return Task.CompletedTask;
        }

        var broker = new VaultCredentialBroker(_data.Vault, _data.Store.CredentialIdFor);
        return StartSessionAsync(definition, broker, entry.Name, newTab, factory);
    }

    /// <summary>Opens a saved web connection in a tab of its own.</summary>
    private void OpenWebPage(ConnectionEntry entry)
    {
        if (!WebAddress.TryNormalize(entry.Host, entry.Port, out var address, out var error))
        {
            MessageBox.Show(this, error, "RemoteDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var untrusted = entry.Options is not null
            && entry.Options.TryGetValue("acceptUntrustedCertificate", out var flag)
            && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);

        var tab = new WorkspaceTab(new WebPageInfo(entry.Name, address, untrusted));
        var view = new WebPageView(address, untrusted) { Visibility = Visibility.Collapsed };
        view.TitleChanged += title =>
        {
            tab.WebTitle = title;
            tab.Refresh();
        };
        _webViews[tab.Id] = view;
        WebHost.Children.Add(view);
        NewTab(tab);
    }

    /// <summary>
    /// Opens a saved Remote Desktop connection in a tab, asking for the password (which is not saved). Falls back to the
    /// Windows Remote Desktop app when the connection is set to use it or the embedded control is not available.
    /// </summary>
    private void OpenRemoteDesktop(ConnectionEntry entry)
    {
        var external = entry.Options is not null
            && entry.Options.TryGetValue("externalClient", out var flag)
            && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
        if (external || !RdpSessionView.IsAvailable)
        {
            LaunchRemoteDesktop(entry);
            return;
        }

        var user = entry.Options is not null && entry.Options.TryGetValue("username", out var u) ? u : string.Empty;
        var domain = entry.Options is not null && entry.Options.TryGetValue("domain", out var d) ? d : null;

        var prompt = new RdpPasswordDialog(user, entry.Host) { Owner = this };
        if (prompt.ShowDialog() != true)
        {
            return;
        }

        var info = new RdpInfo(entry.Name, entry.Host, entry.Port ?? 3389, user.Length > 0 ? user : null, domain);
        var tab = new WorkspaceTab(info);
        var view = new RdpSessionView(info, prompt.Password) { Visibility = Visibility.Collapsed };
        _webViews[tab.Id] = view;
        WebHost.Children.Add(view);
        NewTab(tab);
        SetStatus($"Opened {entry.Name} in a tab.");
    }

    /// <summary>Opens the connection in the Windows Remote Desktop client. It asks for the password itself, so none is passed on.</summary>
    private void LaunchRemoteDesktop(ConnectionEntry entry)
    {
        try
        {
            var lines = new List<string>
            {
                "full address:s:" + (entry.Port is { } port ? $"{entry.Host}:{port}" : entry.Host),
                "prompt for credentials:i:1",
                "authentication level:i:2",
            };

            if (entry.Options is not null && entry.Options.TryGetValue("username", out var user) && user.Length > 0)
            {
                var domain = entry.Options.TryGetValue("domain", out var d) && d.Length > 0 ? d + "\\" : string.Empty;
                lines.Add("username:s:" + domain + user);
            }

            // One file per connection id, reused each time, so they do not pile up in the temp folder.
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RemoteDeck");
            Directory.CreateDirectory(folder);
            var file = System.IO.Path.Combine(folder, entry.Id + ".rdp");
            File.WriteAllLines(file, lines);
            Process.Start(new ProcessStartInfo("mstsc.exe", $"\"{file}\"") { UseShellExecute = true });
            SetStatus($"Opened {entry.Name} in Remote Desktop.");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Could not start Remote Desktop: " + ex.Message, "RemoteDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
    private async Task StartSessionAsync(ConnectionDefinition definition, ICredentialBroker broker, string title, bool newTab, IConnectionFactory? factory = null)
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

        await ConnectPaneAsync(tab, pane, factory ?? _ssh, definition, broker, title, member: false);
    }

    /// <summary>Starts an SSH session in a specific pane and wires its output, state and broadcast membership.</summary>
    /// <summary>Starts a log file when the connection has "Log session output" turned on. Failing to log never stops the session.</summary>
    private SessionLog? OpenSessionLog(ConnectionDefinition definition, PaneSession pane)
    {
        if (definition.Options is null
            || !definition.Options.TryGetValue("logSession", out var flag)
            || !string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var log = SessionLog.Open(AppPaths.Logs, definition.Name, DateTimeOffset.Now);
            Write(pane, $"\u001b[2m[logging to {log.Path}]\u001b[0m\r\n");
            return log;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Write(pane, $"\u001b[33mCould not start the session log: {ex.Message}\u001b[0m\r\n");
            return null;
        }
    }

    private enum ConnectOutcome
    {
        Connected,
        CouldNotConnect,

        /// <summary>Trying again would not help (wrong password, rejected host key, bad settings).</summary>
        GaveUp,
    }

    private static bool WantsAutoReconnect(ConnectionDefinition definition) =>
        definition.Options is not null
        && definition.Options.TryGetValue("autoReconnect", out var flag)
        && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// After a connection drops, tries again with growing pauses. It stops as soon as the pane is closed or reused,
    /// when a retry could not help (wrong password, host key refused), or after <see cref="ReconnectPolicy.MaxAttempts"/> attempts.
    /// </summary>
    private async Task ReconnectAsync(
        WorkspaceTab tab,
        PaneSession pane,
        IConnectionFactory factory,
        ConnectionDefinition definition,
        ICredentialBroker broker,
        string title,
        ITerminalConnection dropped)
    {
        var previous = dropped;
        for (var attempt = 1; attempt <= ReconnectPolicy.MaxAttempts; attempt++)
        {
            var delay = ReconnectPolicy.Delay(attempt);
            Write(pane, $"\u001b[2m[reconnecting in {(int)delay.TotalSeconds}s, attempt {attempt} of {ReconnectPolicy.MaxAttempts}; close the pane to stop]\u001b[0m\r\n");
            await Task.Delay(delay);

            if (pane.Connection != previous)
            {
                return;
            }

            var member = tab.Router.Members.Contains(pane.TerminalId);
            var old = previous;
            _ = Task.Run(async () => await old.DisposeAsync());

            var outcome = await ConnectPaneAsync(tab, pane, factory, definition, broker, title, member);
            if (outcome == ConnectOutcome.Connected)
            {
                return;
            }

            if (outcome == ConnectOutcome.GaveUp || pane.Connection is null)
            {
                return;
            }

            previous = pane.Connection;
        }

        Write(pane, "\u001b[33m[gave up reconnecting]\u001b[0m\r\n");
    }

    private async Task<ConnectOutcome> ConnectPaneAsync(
        WorkspaceTab tab,
        PaneSession pane,
        IConnectionFactory factory,
        ConnectionDefinition definition,
        ICredentialBroker broker,
        string title,
        bool member)
    {
        var created = factory.Create(definition, broker);
        if (created is not ITerminalConnection connection)
        {
            await created.DisposeAsync();
            MessageBox.Show(this, $"\"{definition.Type}\" connections can't open in a terminal pane.", "RemoteDeck", MessageBoxButton.OK, MessageBoxImage.Information);
            return ConnectOutcome.GaveUp;
        }

        pane.ConnectionId = _data.Store.FindConnection(definition.Id) is null ? null : definition.Id;
        pane.Connection = connection;
        pane.Title = title;
        pane.State = ConnectionState.Connecting;
        tab.Router.Register(pane.TerminalId, connection);
        if (member)
        {
            tab.Router.SetMember(pane.TerminalId, true);
        }

        tab.FocusedTerminalId = pane.TerminalId;

        var log = OpenSessionLog(definition, pane);
        var wasConnected = false;
        var reconnecting = WantsAutoReconnect(definition);

        connection.StateChanged += (_, state) => Dispatcher.InvokeAsync(() =>
        {
            if (state is ConnectionState.Disconnected or ConnectionState.Failed)
            {
                log?.Dispose();
            }

            if (pane.Connection != connection)
            {
                return;
            }

            pane.State = state;
            SyncTab(tab);
            if (state == ConnectionState.Connected)
            {
                wasConnected = true;
            }

            if (state == ConnectionState.Disconnected)
            {
                Write(pane, "\r\n\u001b[2m[connection closed]\u001b[0m\r\n");

                // Only a connection that was up and then dropped is retried. Typing "exit", closing the pane, or a
                // connection that never came up are not drops.
                if (reconnecting && wasConnected && connection is SshConnection { DroppedUnexpectedly: true })
                {
                    _ = ReconnectAsync(tab, pane, factory, definition, broker, title, connection);
                }
            }
        });

        connection.Output.Subscribe(new DelegateObserver(
            data =>
            {
                log?.Write(data);
                Dispatcher.InvokeAsync(() =>
                {
                    if (pane.Connection == connection)
                    {
                        _terminals.Output(pane.TerminalId, data);
                    }
                });
            },
            () => log?.Dispose()));

        connection.Resize(pane.Columns, pane.Rows);
        SyncTab(tab);
        _terminals.FocusPane(pane.TerminalId);

        try
        {
            // Off the UI thread: the host-key question blocks the connecting thread until you answer it.
            await Task.Run(() => connection.ConnectAsync().AsTask());
            return ConnectOutcome.Connected;
        }
        catch (SshConnectionException ex)
        {
            Write(pane, $"\r\n\u001b[31m{ex.Message}\u001b[0m\r\n");
            return ex.Transient ? ConnectOutcome.CouldNotConnect : ConnectOutcome.GaveUp;
        }
        catch (TelnetConnectionException ex)
        {
            Write(pane, $"\r\n\u001b[31m{ex.Message}\u001b[0m\r\n");
            return ConnectOutcome.GaveUp;
        }
        catch (Exception ex) when (ex is VaultException or KeyNotFoundException)
        {
            Write(pane, $"\r\n\u001b[31mCould not read the saved password: {ex.Message}\u001b[0m\r\n");
            return ConnectOutcome.GaveUp;
        }
        catch (Exception ex)
        {
            Write(pane, $"\r\n\u001b[31mUnexpected error: {ex.Message}\u001b[0m\r\n");
            return ConnectOutcome.GaveUp;
        }
    }

    // ---- tabs, panes and layouts ----

    private WorkspaceTab NewTab(LayoutPreset preset) => NewTab(new WorkspaceTab(preset));

    private WorkspaceTab NewTab(WorkspaceTab tab)
    {
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
        if (!tab.IsEmbedded)
        {
            _terminals.Sync(tab);
        }

        Tabs.Add(tab);
        TabStrip.SelectedItem = tab;
        return tab;
    }

    private void SyncTab(WorkspaceTab tab)
    {
        tab.Refresh();
        if (!tab.IsEmbedded)
        {
            _terminals.Sync(tab);
        }
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
        if (_webViews.Remove(tab.Id, out var webView))
        {
            WebHost.Children.Remove(webView);
            (webView as IDisposable)?.Dispose();
        }
        else
        {
            _terminals.CloseTab(tab.Id);
        }

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
        foreach (var view in _webViews.Values)
        {
            view.Visibility = Visibility.Collapsed;
        }

        if (SelectedTab is { IsEmbedded: true } webTab && _webViews.TryGetValue(webTab.Id, out var shown))
        {
            Web.Visibility = Visibility.Collapsed;
            WebHost.Visibility = Visibility.Visible;
            shown.Visibility = Visibility.Visible;
        }
        else
        {
            WebHost.Visibility = Visibility.Collapsed;
            Web.Visibility = Visibility.Visible;
            if (Current is { } tab)
            {
                _terminals.ShowTab(tab.Id);
            }
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
