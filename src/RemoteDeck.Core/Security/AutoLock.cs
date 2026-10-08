namespace RemoteDeck.Core.Security;

/// <summary>When an idle RemoteDeck should lock its vault.</summary>
public static class AutoLock
{
    public const int DefaultMinutes = 15;

    /// <summary>The longest setting accepted (a day), so a typo cannot turn locking off by accident.</summary>
    public const int MaxMinutes = 24 * 60;

    /// <summary>0 means never; anything else is clamped to 1 minute up to a day.</summary>
    public static int Normalize(int minutes) => minutes <= 0 ? 0 : Math.Min(minutes, MaxMinutes);

    /// <summary>True when the user has been idle for at least the configured time. Never true when auto-lock is off.</summary>
    public static bool IsDue(TimeSpan idle, int minutes)
    {
        var normalized = Normalize(minutes);
        return normalized > 0 && idle >= TimeSpan.FromMinutes(normalized);
    }
}
