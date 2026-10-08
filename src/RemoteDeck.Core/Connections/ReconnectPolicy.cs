namespace RemoteDeck.Core.Connections;

/// <summary>How long to wait between attempts when a dropped connection is re-established automatically.</summary>
public static class ReconnectPolicy
{
    /// <summary>Attempts before giving up, so a server that is really gone does not keep a retry loop running forever.</summary>
    public const int MaxAttempts = 8;

    /// <summary>The wait before attempt <paramref name="attempt"/> (1 is the first): 2, 4, 8, 16, then 30 seconds.</summary>
    public static TimeSpan Delay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var seconds = attempt >= 5 ? 30 : 1 << attempt;
        return TimeSpan.FromSeconds(Math.Min(seconds, 30));
    }
}
