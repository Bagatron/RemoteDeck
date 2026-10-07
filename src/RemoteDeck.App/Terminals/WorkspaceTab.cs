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

    public ITerminalConnection? Connection { get; set; }

    public ConnectionState State { get; set; } = ConnectionState.Disconnected;

    /// <summary>Last size reported by the terminal, in characters.</summary>
    public int Columns { get; set; } = 80;

    public int Rows { get; set; } = 24;

    public bool IsEmpty => Connection is null;

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

/// <summary>A tab: a split layout of panes, each holding at most one session, plus the tab's broadcast group.</summary>
public sealed class WorkspaceTab : INotifyPropertyChanged
{
    private LayoutNode _root;
    private Dictionary<string, PaneSession> _panes = new();
    private int _nextPane;

    public WorkspaceTab(LayoutPreset preset = LayoutPreset.Single)
    {
        _root = LayoutPresets.Create(preset);
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
