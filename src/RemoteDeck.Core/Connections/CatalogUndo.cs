namespace RemoteDeck.Core.Connections;

/// <summary>
/// Undo for changes to the connection list (deleting, moving, renaming). Each step is run through <see cref="Run"/>,
/// which keeps a copy of the list from just before it; <see cref="Undo"/> puts the last copy back. A change made any
/// other way (editing a connection, importing) starts a fresh history, because copies from before it would wipe it
/// out when restored.
/// </summary>
/// <remarks>
/// Deleting a connection must not lose its saved password while the delete can still be undone, so the caller names
/// the credentials a step would orphan and the vault clean-up waits until the step can no longer be undone
/// (<see cref="CredentialsReleased"/>).
/// </remarks>
public sealed class CatalogUndo : IDisposable
{
    private sealed record Step(string Label, string Snapshot, IReadOnlyList<string> Credentials);

    private readonly ConnectionStore _store;
    private readonly int _limit;
    private readonly List<Step> _steps = new();
    private bool _busy;
    private bool _disposed;

    public CatalogUndo(ConnectionStore store, int limit = 20)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _limit = Math.Max(1, limit);
        _store.Changed += OnChanged;
    }

    /// <summary>Raised with credential ids that were set aside by deleted items and can no longer come back. The host removes the ones nothing uses.</summary>
    public event Action<IReadOnlyList<string>>? CredentialsReleased;

    public bool CanUndo => _steps.Count > 0;

    /// <summary>What the next <see cref="Undo"/> would undo, such as "Delete \"web-1\"", or null when there is nothing to undo.</summary>
    public string? NextLabel => _steps.Count > 0 ? _steps[^1].Label : null;

    public int Count => _steps.Count;

    /// <summary>
    /// Runs a change as one undoable step. If it changes nothing, no step is kept. <paramref name="credentialsToRelease"/>
    /// are the vault entries the change leaves unused.
    /// </summary>
    public void Run(string label, Action action, IEnumerable<string>? credentialsToRelease = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        var before = _store.Serialize();
        _busy = true;
        try
        {
            action();
        }
        finally
        {
            _busy = false;

            // Even if the action stopped half way, whatever it did can be undone.
            if (_store.Serialize() != before)
            {
                _steps.Add(new Step(label, before, credentialsToRelease?.Distinct(StringComparer.Ordinal).ToList() ?? new List<string>()));
                while (_steps.Count > _limit)
                {
                    Release(_steps[0]);
                    _steps.RemoveAt(0);
                }
            }
        }
    }

    /// <summary>Undoes the last step and returns its label, or returns null if there is nothing to undo.</summary>
    public string? Undo()
    {
        if (_steps.Count == 0)
        {
            return null;
        }

        var step = _steps[^1];
        _steps.RemoveAt(_steps.Count - 1);
        _busy = true;
        try
        {
            _store.Restore(step.Snapshot);
        }
        finally
        {
            _busy = false;
        }

        // The restored items use their credentials again, so nothing is released.
        return step.Label;
    }

    /// <summary>Forgets all history; the credentials set aside by its steps are released.</summary>
    public void Clear()
    {
        var steps = _steps.ToArray();
        _steps.Clear();
        foreach (var step in steps)
        {
            Release(step);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.Changed -= OnChanged;
        Clear();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (!_busy)
        {
            Clear();
        }
    }

    private void Release(Step step)
    {
        if (step.Credentials.Count > 0)
        {
            CredentialsReleased?.Invoke(step.Credentials);
        }
    }
}
