namespace RemoteDeck.Core.Security;

/// <summary>
/// Clears a copied secret from the clipboard after a delay, but only if the clipboard still holds it, so
/// something the user copied in the meantime is never wiped. The clipboard itself is passed in as functions.
/// </summary>
public sealed class ClipboardGuard
{
    public const int DefaultSeconds = 30;

    private readonly Func<string?> _read;
    private readonly Action _clear;
    private readonly object _gate = new();
    private string? _pending;
    private CancellationTokenSource? _timer;

    public ClipboardGuard(Func<string?> read, Action clear)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clear);
        _read = read;
        _clear = clear;
    }

    /// <summary>Whether a copied secret is waiting to be cleared.</summary>
    public bool IsWatching
    {
        get
        {
            lock (_gate)
            {
                return _pending is not null;
            }
        }
    }

    /// <summary>Starts the countdown for a secret that was just copied. A new secret replaces the old countdown. 0 or less disables clearing.</summary>
    public Task WatchAsync(string copied, TimeSpan delay)
    {
        ArgumentException.ThrowIfNullOrEmpty(copied);
        if (delay <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        CancellationTokenSource timer;
        lock (_gate)
        {
            _timer?.Cancel();
            _pending = copied;
            timer = _timer = new CancellationTokenSource();
        }

        return RunAsync(copied, delay, timer);
    }

    /// <summary>Clears now if the clipboard still holds the watched secret (for example when the app closes).</summary>
    public void ClearNow()
    {
        string? secret;
        lock (_gate)
        {
            secret = _pending;
            _pending = null;
            _timer?.Cancel();
        }

        if (secret is not null)
        {
            ClearIfUnchanged(secret);
        }
    }

    private async Task RunAsync(string secret, TimeSpan delay, CancellationTokenSource timer)
    {
        try
        {
            await Task.Delay(delay, timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_timer, timer))
            {
                return;
            }

            _pending = null;
        }

        ClearIfUnchanged(secret);
    }

    private void ClearIfUnchanged(string secret)
    {
        string? current;
        try
        {
            current = _read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return;
        }

        if (string.Equals(current, secret, StringComparison.Ordinal))
        {
            try
            {
                _clear();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The clipboard was busy; nothing more to do.
            }
        }
    }
}
