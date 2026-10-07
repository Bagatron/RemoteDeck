using System.Text;
using RemoteDeck.Plugin;

namespace RemoteDeck.Core.Broadcast;

public sealed record BroadcastFailure(string PaneId, Exception Error);

/// <summary>Outcome of one routed write.</summary>
/// <param name="Attempted">How many sessions the input was sent to.</param>
/// <param name="Failures">Sessions that threw. One failing host never stops the others.</param>
/// <param name="BlockedBy">Set when nothing was sent because a safety check was not confirmed.</param>
public sealed record BroadcastResult(
    int Attempted,
    IReadOnlyList<BroadcastFailure> Failures,
    BroadcastRisk? BlockedBy = null)
{
    public int Sent => Attempted - Failures.Count;

    public bool WasBlocked => BlockedBy is not null;

    public static BroadcastResult Blocked(BroadcastRisk risk) =>
        new(0, Array.Empty<BroadcastFailure>(), risk);
}

/// <summary>
/// Sends input from one terminal pane to a group of panes.
///
/// Two modes share the same group:
/// * Live broadcast: while <see cref="Enabled"/>, input typed in a member pane is mirrored to every member.
/// * Command bar: <see cref="SendCommandAsync"/> sends a whole line to every member, whether or not live broadcast is on.
///
/// Broadcast always starts off, and <see cref="Reset"/> turns it off and clears the group.
/// </summary>
public sealed class BroadcastRouter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ITerminalConnection> _panes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _members = new(StringComparer.Ordinal);
    private bool _enabled;

    public BroadcastRouter(BroadcastPolicy? policy = null)
    {
        Policy = policy ?? new BroadcastPolicy();
    }

    public BroadcastPolicy Policy { get; }

    /// <summary>Raised when live broadcast is toggled or group membership changes.</summary>
    public event EventHandler? StateChanged;

    public bool Enabled
    {
        get
        {
            lock (_gate)
            {
                return _enabled;
            }
        }
    }

    public IReadOnlyCollection<string> Members
    {
        get
        {
            lock (_gate)
            {
                return _members.ToArray();
            }
        }
    }

    public void Register(string paneId, ITerminalConnection connection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId);
        ArgumentNullException.ThrowIfNull(connection);

        lock (_gate)
        {
            _panes[paneId] = connection;
        }
    }

    public void Unregister(string paneId)
    {
        bool changed;
        lock (_gate)
        {
            _panes.Remove(paneId);
            changed = _members.Remove(paneId);
        }

        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetMember(string paneId, bool isMember)
    {
        bool changed;
        lock (_gate)
        {
            if (!_panes.ContainsKey(paneId))
            {
                throw new KeyNotFoundException($"Pane '{paneId}' is not registered.");
            }

            changed = isMember ? _members.Add(paneId) : _members.Remove(paneId);
        }

        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetEnabled(bool enabled)
    {
        bool changed;
        lock (_gate)
        {
            changed = _enabled != enabled;
            _enabled = enabled;
        }

        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Turns live broadcast off and empties the group. Call when a layout or the app closes.</summary>
    public void Reset()
    {
        bool changed;
        lock (_gate)
        {
            changed = _enabled || _members.Count > 0;
            _enabled = false;
            _members.Clear();
        }

        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Routes raw keystrokes typed in <paramref name="sourcePaneId"/>. With live broadcast on and the source in the
    /// group, every member receives them; otherwise only the source does.
    /// </summary>
    public async ValueTask<BroadcastResult> RouteInputAsync(
        string sourcePaneId,
        ReadOnlyMemory<byte> input,
        CancellationToken cancellationToken = default)
    {
        var targets = SnapshotLiveTargets(sourcePaneId);
        return await WriteAllAsync(targets, input, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Routes pasted text. When it would reach more than one session, multi-line or risky text must be
    /// confirmed first; with no <paramref name="confirm"/> callback it is blocked.
    /// </summary>
    public async ValueTask<BroadcastResult> RoutePasteAsync(
        string sourcePaneId,
        string text,
        Func<BroadcastRisk, ValueTask<bool>>? confirm = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        var targets = SnapshotLiveTargets(sourcePaneId);
        if (targets.Count > 1)
        {
            var (approved, risk) = await ConfirmAsync(text, confirm).ConfigureAwait(false);
            if (!approved)
            {
                return BroadcastResult.Blocked(risk);
            }
        }

        return await WriteAllAsync(targets, Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Command bar mode: sends a full line (plus Enter) to every group member, regardless of live broadcast.
    /// Risky or multi-line commands are blocked unless <paramref name="confirm"/> approves them.
    /// </summary>
    public async ValueTask<BroadcastResult> SendCommandAsync(
        string command,
        Func<BroadcastRisk, ValueTask<bool>>? confirm = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var targets = SnapshotMembers();
        if (targets.Count == 0)
        {
            return new BroadcastResult(0, Array.Empty<BroadcastFailure>());
        }

        var (approved, risk) = await ConfirmAsync(command, confirm).ConfigureAwait(false);
        if (!approved)
        {
            return BroadcastResult.Blocked(risk);
        }

        // Terminals expect a carriage return for Enter.
        var line = command.ReplaceLineEndings("\r");
        if (!line.EndsWith('\r'))
        {
            line += "\r";
        }

        return await WriteAllAsync(targets, Encoding.UTF8.GetBytes(line), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<(bool Approved, BroadcastRisk Risk)> ConfirmAsync(
        string text,
        Func<BroadcastRisk, ValueTask<bool>>? confirm)
    {
        var risk = Policy.Evaluate(text);
        if (!risk.IsRisky)
        {
            return (true, risk);
        }

        // Fail closed: no way to ask the user means no send.
        var approved = confirm is not null && await confirm(risk).ConfigureAwait(false);
        return (approved, risk);
    }

    private List<(string PaneId, ITerminalConnection Connection)> SnapshotLiveTargets(string sourcePaneId)
    {
        lock (_gate)
        {
            if (!_panes.TryGetValue(sourcePaneId, out var source))
            {
                throw new KeyNotFoundException($"Pane '{sourcePaneId}' is not registered.");
            }

            if (_enabled && _members.Contains(sourcePaneId))
            {
                return MembersLocked();
            }

            return new List<(string, ITerminalConnection)> { (sourcePaneId, source) };
        }
    }

    private List<(string PaneId, ITerminalConnection Connection)> SnapshotMembers()
    {
        lock (_gate)
        {
            return MembersLocked();
        }
    }

    private List<(string PaneId, ITerminalConnection Connection)> MembersLocked()
    {
        var list = new List<(string, ITerminalConnection)>(_members.Count);
        foreach (var id in _members)
        {
            if (_panes.TryGetValue(id, out var connection))
            {
                list.Add((id, connection));
            }
        }

        return list;
    }

    private static async ValueTask<BroadcastResult> WriteAllAsync(
        List<(string PaneId, ITerminalConnection Connection)> targets,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(
            targets.Select(t => TryWriteAsync(t.PaneId, t.Connection, data, cancellationToken))).ConfigureAwait(false);

        var failures = results.OfType<BroadcastFailure>().ToList();
        return new BroadcastResult(targets.Count, failures);
    }

    private static async Task<BroadcastFailure?> TryWriteAsync(
        string paneId,
        ITerminalConnection connection,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        try
        {
            await connection.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BroadcastFailure(paneId, ex);
        }
    }
}
