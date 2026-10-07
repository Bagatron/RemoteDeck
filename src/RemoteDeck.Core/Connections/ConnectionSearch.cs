namespace RemoteDeck.Core.Connections;

/// <summary>
/// Ranks connections for the quick-connect box and the Ctrl+K palette.
/// Every word the user typed must match something; the best field per word decides the score.
/// </summary>
internal static class ConnectionSearch
{
    private const StringComparison Ignore = StringComparison.OrdinalIgnoreCase;

    /// <summary>Returns 0 when any word matches nothing.</summary>
    public static int Score(ConnectionEntry connection, string folderPath, IReadOnlyList<string> words)
    {
        var total = 0;
        foreach (var word in words)
        {
            var score = ScoreWord(connection, folderPath, word);
            if (score == 0)
            {
                return 0;
            }

            total += score;
        }

        return connection.Favorite ? total + 5 : total;
    }

    private static int ScoreWord(ConnectionEntry connection, string folderPath, string word)
    {
        var best = 0;

        var name = connection.Name;
        if (name.Equals(word, Ignore))
        {
            best = 100;
        }
        else if (name.StartsWith(word, Ignore))
        {
            best = 80;
        }
        else if (name.Contains(word, Ignore))
        {
            best = 60;
        }

        if (connection.Host.StartsWith(word, Ignore))
        {
            best = Math.Max(best, 45);
        }
        else if (connection.Host.Contains(word, Ignore))
        {
            best = Math.Max(best, 40);
        }

        if (connection.Tags is { Count: > 0 } tags)
        {
            if (tags.Any(t => t.Equals(word, Ignore)))
            {
                best = Math.Max(best, 35);
            }
            else if (tags.Any(t => t.Contains(word, Ignore)))
            {
                best = Math.Max(best, 25);
            }
        }

        if (folderPath.Contains(word, Ignore))
        {
            best = Math.Max(best, 20);
        }

        if (connection.Type.Equals(word, Ignore))
        {
            best = Math.Max(best, 15);
        }

        if (best == 0 && word.Length >= 2 && IsSubsequence(word, name))
        {
            best = 10;
        }

        return best;
    }

    /// <summary>True when the letters of <paramref name="needle"/> appear in order in <paramref name="haystack"/> ("prd" in "prod-web").</summary>
    internal static bool IsSubsequence(string needle, string haystack)
    {
        var next = 0;
        foreach (var ch in haystack)
        {
            if (next < needle.Length && char.ToUpperInvariant(ch) == char.ToUpperInvariant(needle[next]))
            {
                next++;
            }
        }

        return next == needle.Length;
    }
}
