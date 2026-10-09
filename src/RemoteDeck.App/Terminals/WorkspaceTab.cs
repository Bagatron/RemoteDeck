using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Windows.Media;
using RemoteDeck.Core.Broadcast;
using RemoteDeck.Core.Layout;
using RemoteDeck.Plugin;

namespace RemoteDeck.App.Terminals;

/// <summary>
/// One pane's content: an optional connection plus the terminal that shows it. It keeps its terminal when a
/// layout change moves it to another pane. Keystrokes for one pane go through a queue so they arrive in order.
/// </summary>
public sealed class PaneSession
{
    private readonly Channel<Func<Task>> _work =
        Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });

    private int _pumpStarted;

    public string TerminalId { get; } = Guid.NewGuid().ToString("N");

    public string? Title { get; set; }

    /// <summary>The saved connection this pane was opened from; null for a quick connection or an empty pane.</summary>
    public string? ConnectionId { get; set; }

    public ITerminalConnection? Connection { get; set; }

    public ConnectionState State { get; set; } = ConnectionState.Disconnected;

    /// <summary>Last size reported by the terminal, in characters.</summary>
    public int Columns { get; set; } = 80;

    public int Rows { get; set; } = 24;

    /// <summary>Set when this pane shows a web page instead of a terminal; the browser itself is owned by the window.</summary>
    public WebPageInfo? WebPage { get; set; }

    /// <summary>For a web pane: whether its browser can go back / forward (the header buttons follow these).</summary>
    public bool WebCanGoBack { get; set; }

    public bool WebCanGoForward { get; set; }

    /// <summary>Set when this pane shows the built-in editor; the editor itself is owned by the window.</summary>
    public EditorTabInfo? Editor { get; set; }

    /// <summary>For an editor pane: the folder of notes it shows.</summary>
    public string? NotesFolder { get; set; }

    public bool IsEmpty => Connection is null && WebPage is null && Editor is null;

    /// <summary>Runs <paramref name="job"/> after everything queued before it for this pane.</summary>
    public void Enqueue(Func<Task> job)
    {
        if (Interlocked.Exchange(ref _pumpStarted, 1) == 0)
        {
            _ = Task.Run(Pump);
        }

        _work.Writer.TryWrite(job);
    }

    public void Stop() => _work.Writer.TryComplete();

    private async Task Pump()
    {
        await foreach (var job in _work.Reader.ReadAllAsync())
        {
            try
            {
                await job().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A failed write must not stop later input; the job reports its own problems.
            }
        }
    }
}

/// <summary>A saved Remote Desktop connection shown in a tab. The password is never part of it.</summary>
public sealed record RdpInfo(string Name, string Host, int Port, string? User, string? Domain);

/// <summary>A saved web connection shown in a tab instead of terminal panes.</summary>
/// <summary>An editor window shown inside a tab.</summary>
public sealed record EditorTabInfo(string Name);

/// <summary>A saved web connection shown in a tab instead of terminal panes.</summary>
public sealed record WebPageInfo(string Name, Uri Address, bool AcceptUntrustedCertificate);

/// <summary>
/// A tab: a split layout of panes, each holding at most one session, plus the tab's broadcast group.
/// A web tab has the same shape (one empty pane that is never shown) so the rest of the app can treat it as a tab;
/// <see cref="WebPage"/> says it is one.
/// </summary>
public sealed class WorkspaceTab : INotifyPropertyChanged
{
    private LayoutNode _root;
    private Dictionary<string, PaneSession> _panes = new();
    private int _nextPane;

    public WorkspaceTab(LayoutPreset preset = LayoutPreset.Single)
        : this(LayoutPresets.Create(preset))
    {
    }

    /// <summary>A tab that shows a web page.</summary>
    public WorkspaceTab(WebPageInfo webPage)
        : this(LayoutPreset.Single)
    {
        WebPage = webPage;
    }

    /// <summary>A tab that shows a Remote Desktop session.</summary>
    public WorkspaceTab(RdpInfo rdp)
        : this(LayoutPreset.Single)
    {
        Rdp = rdp;
    }

    /// <summary>A tab that holds an editor's window.</summary>
    public WorkspaceTab(EditorTabInfo editor)
        : this(LayoutPreset.Single)
    {
        Editor = editor;
    }

    /// <summary>Set for an editor tab; null otherwise.</summary>
    public EditorTabInfo? Editor { get; }

    /// <summary>Set for a web tab; null otherwise.</summary>
    public WebPageInfo? WebPage { get; }

    /// <summary>Set for a Remote Desktop tab; null otherwise.</summary>
    public RdpInfo? Rdp { get; }

    /// <summary>True for a tab that shows a web page or a remote desktop instead of terminal panes.</summary>
    public bool IsEmbedded => WebPage is not null || Rdp is not null || Editor is not null;

    /// <summary>The page's own title once it has loaded, shown on a web tab.</summary>
    public string? WebTitle { get; set; }

    /// <summary>A tab with an existing layout (a saved workspace). Every pane starts empty.</summary>
    public WorkspaceTab(LayoutNode root)
    {
        _root = root;
        foreach (var pane in LayoutTree.Panes(_root))
        {
            _panes[pane.Id] = new PaneSession();
        }

        _nextPane = HighestPaneNumber();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public BroadcastRouter Router { get; } = new();

    public LayoutNode Root => _root;

    public string? FocusedTerminalId { get; set; }

    /// <summary>The panes in visual order (left to right, top to bottom).</summary>
    public IReadOnlyList<PaneSession> Sessions =>
        LayoutTree.Panes(_root).Select(p => _panes[p.Id]).ToArray();

    public string Title
    {
        get
        {
            if (Rdp is not null)
            {
                return Rdp.Name;
            }

            if (Editor is not null)
            {
                return Editor.Name;
            }

            if (WebPage is not null)
            {
                return string.IsNullOrWhiteSpace(WebTitle) ? WebPage.Name : WebTitle;
            }

            var active = Sessions.Where(s => !s.IsEmpty).ToList();
            return active.Count switch
            {
                0 => "New tab",
                1 => active[0].Title ?? "Session",
                _ => $"{active[0].Title} +{active.Count - 1}",
            };
        }
    }

    public Brush StatusBrush
    {
        get
        {
            if (Rdp is not null)
            {
                return Brushes.MediumPurple;
            }

            if (WebPage is not null)
            {
                return Brushes.SteelBlue;
            }

            var first = Sessions.FirstOrDefault(s => !s.IsEmpty);
            return first?.State switch
            {
                ConnectionState.Connected => Brushes.MediumSeaGreen,
                ConnectionState.Connecting => Brushes.Goldenrod,
                ConnectionState.Failed => Brushes.IndianRed,
                _ => Brushes.Gray,
            };
        }
    }

    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
    }

    public PaneSession? FindByTerminal(string terminalId) =>
        _panes.Values.FirstOrDefault(s => s.TerminalId == terminalId);

    /// <summary>An empty pane to open a connection in: the focused one if it is empty, otherwise the first.</summary>
    public PaneSession? PickEmptyPane()
    {
        var sessions = Sessions;
        return sessions.FirstOrDefault(s => s.IsEmpty && s.TerminalId == FocusedTerminalId)
            ?? sessions.FirstOrDefault(s => s.IsEmpty);
    }

    /// <summary>Splits the pane showing <paramref name="terminalId"/>; the new pane is empty.</summary>
    public PaneSession? Split(string terminalId, SplitOrientation orientation)
    {
        var paneId = PaneIdOf(terminalId);
        if (paneId is null)
        {
            return null;
        }

        var newId = "p" + (++_nextPane);
        _root = LayoutTree.SplitPane(_root, paneId, orientation, newId);
        var session = new PaneSession();
        _panes[newId] = session;
        return session;
    }

    /// <summary>
    /// Swaps what two panes show: each session moves to the other's place in the layout (sizes stay where they are).
    /// Returns false when either pane is not in this tab or both are the same.
    /// </summary>
    public bool Swap(string terminalIdA, string terminalIdB)
    {
        var a = PaneIdOf(terminalIdA);
        var b = PaneIdOf(terminalIdB);
        if (a is null || b is null || a == b)
        {
            return false;
        }

        (_panes[a], _panes[b]) = (_panes[b], _panes[a]);
        return true;
    }

    /// <summary>Removes a pane from the layout. Not for the last pane; close the tab instead. Returns the removed session.</summary>
    public PaneSession? RemovePane(string terminalId)
    {
        var paneId = PaneIdOf(terminalId);
        if (paneId is null || _panes.Count <= 1)
        {
            return null;
        }

        var session = _panes[paneId];
        _root = LayoutTree.ClosePane(_root, paneId) ?? _root;
        _panes.Remove(paneId);
        if (FocusedTerminalId == terminalId)
        {
            FocusedTerminalId = null;
        }

        return session;
    }

    public void SetRatio(IReadOnlyList<int> path, double ratio)
    {
        try
        {
            _root = LayoutTree.SetRatio(_root, path, ratio);
        }
        catch (ArgumentException)
        {
            // The page and the host disagreed about the layout for a moment; ignore the stale drag.
        }
    }

    /// <summary>How many running sessions would not fit if <paramref name="preset"/> were applied.</summary>
    public int SessionsLostBy(LayoutPreset preset)
    {
        var capacity = LayoutTree.Panes(LayoutPresets.Create(preset)).Count();
        return Math.Max(0, Sessions.Count(s => !s.IsEmpty) - capacity);
    }

    /// <summary>
    /// Switches to a preset. Running sessions keep their terminals and fill the new panes in order; sessions that
    /// do not fit are returned so the caller can end them.
    /// </summary>
    public IReadOnlyList<PaneSession> ApplyPreset(LayoutPreset preset)
    {
        var newRoot = LayoutPresets.Create(preset);
        var sessions = Sessions;
        var queue = new Queue<PaneSession>(sessions.Where(s => !s.IsEmpty).Concat(sessions.Where(s => s.IsEmpty)));

        var map = new Dictionary<string, PaneSession>();
        foreach (var pane in LayoutTree.Panes(newRoot))
        {
            map[pane.Id] = queue.Count > 0 ? queue.Dequeue() : new PaneSession();
        }

        _root = newRoot;
        _panes = map;
        _nextPane = HighestPaneNumber();
        return queue.ToArray();
    }

    /// <summary>
    /// Captures this tab as a workspace: the layout, which saved connection is in each pane, and which panes are in the
    /// broadcast group. Broadcast itself is never saved as on.
    /// </summary>
    public Workspace ToWorkspace(string name, string? description = null)
    {
        var layout = _root;
        var members = new List<string>();
        var noteFolders = new Dictionary<string, string>(StringComparer.Ordinal);
        var inGroup = Router.Members.ToHashSet(StringComparer.Ordinal);
        foreach (var pane in LayoutTree.Panes(_root))
        {
            var session = _panes[pane.Id];
            layout = LayoutTree.AssignConnection(layout, pane.Id, session.IsEmpty ? null : session.ConnectionId);
            if (inGroup.Contains(session.TerminalId))
            {
                members.Add(pane.Id);
            }

            // Notes kept with a workspace come back from that workspace's own folder.
            if (session.Editor is not null && session.NotesFolder is { } folder && RemoteDeck.Core.Notes.NoteLibrary.IsWorkspaceFolder(folder))
            {
                noteFolders[pane.Id] = folder;
            }
        }

        return new Workspace(name, layout, description, AutoConnect: true, BroadcastMembers: members.Count > 0 ? members : null,
            NoteFolders: noteFolders.Count > 0 ? noteFolders : null);
    }

    /// <summary>The pane ids and sessions in visual order, so a saved layout can be matched to its panes.</summary>
    public IReadOnlyList<(string PaneId, PaneSession Session)> PanesWithIds() =>
        LayoutTree.Panes(_root).Select(p => (p.Id, _panes[p.Id])).ToArray();

    /// <summary>The message that makes the page show this tab's current layout and pane details.</summary>
    public JsonObject ToSync()
    {
        var members = Router.Members.ToHashSet(StringComparer.Ordinal);

        JsonObject Node(LayoutNode node) => node switch
        {
            PaneNode pane => new JsonObject { ["k"] = "pane", ["id"] = _panes[pane.Id].TerminalId },
            SplitNode split => new JsonObject
            {
                ["k"] = "split",
                ["o"] = split.Orientation == SplitOrientation.Columns ? "columns" : "rows",
                ["r"] = split.Ratio,
                ["a"] = Node(split.First),
                ["b"] = Node(split.Second),
            },
            _ => throw new InvalidOperationException("Unknown layout node."),
        };

        var panes = new JsonArray();
        foreach (var session in Sessions)
        {
            panes.Add(new JsonObject
            {
                ["id"] = session.TerminalId,
                ["title"] = session.Title ?? string.Empty,
                ["state"] = session.State.ToString(),
                ["empty"] = session.IsEmpty,
                ["web"] = session.WebPage is not null || session.Editor is not null,
                ["editor"] = session.Editor is not null,
                ["back"] = session.WebCanGoBack,
                ["fwd"] = session.WebCanGoForward,
                ["member"] = members.Contains(session.TerminalId),
            });
        }

        return new JsonObject
        {
            ["t"] = "sync",
            ["tab"] = Id,
            ["tree"] = Node(_root),
            ["panes"] = panes,
            ["broadcast"] = Router.Enabled,
        };
    }

    private string? PaneIdOf(string terminalId) =>
        _panes.FirstOrDefault(kv => kv.Value.TerminalId == terminalId).Key;

    private int HighestPaneNumber() =>
        _panes.Keys.Select(k => int.TryParse(k.AsSpan(1), out var n) ? n : 0).DefaultIfEmpty(0).Max();
}
