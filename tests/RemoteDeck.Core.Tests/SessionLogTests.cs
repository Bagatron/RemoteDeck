using System.Text;
using RemoteDeck.Core.Logging;

namespace RemoteDeck.Core.Tests;

public sealed class SessionLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rd-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 14, 30, 5, TimeSpan.Zero);

    /// <summary>Feeds the chunks in order, closes the log and returns its lines after the header.</summary>
    private string[] Run(params string[] chunks)
    {
        string path;
        using (var log = SessionLog.Open(_dir, "web-01", Now))
        {
            path = log.Path;
            foreach (var chunk in chunks)
            {
                log.Write(Encoding.UTF8.GetBytes(chunk));
            }
        }

        return File.ReadAllLines(path).Skip(1).ToArray();
    }

    [Fact]
    public void WritesPlainLines_AndAHeader()
    {
        string path;
        using (var log = SessionLog.Open(_dir, "web-01", Now))
        {
            path = log.Path;
            log.Write("hello\r\nworld\r\n"u8);
        }

        var lines = File.ReadAllLines(path);
        Assert.StartsWith("# RemoteDeck session log: web-01, started 2026-10-08 14:30:05", lines[0]);
        Assert.Equal(new[] { "hello", "world" }, lines.Skip(1));
        Assert.EndsWith("web-01_20261008-143005.log", path);
    }

    [Fact]
    public void ColorsAndWindowTitles_AreRemoved()
    {
        var lines = Run("\u001b[1;32muser@host\u001b[0m:\u001b[34m~\u001b[0m$ ls\r\n\u001b]0;title\u0007done\r\n");

        Assert.Equal(new[] { "user@host:~$ ls", "done" }, lines);
    }

    [Fact]
    public void SequencesSplitAcrossChunks_AreStillRemoved()
    {
        var lines = Run("a\u001b", "[3", "1mb\u001b]0;ti", "tle\u001b", "\\c\r\n");

        Assert.Equal(new[] { "abc" }, lines);
    }

    [Fact]
    public void ACharacterSplitAcrossChunks_IsKeptWhole()
    {
        var bytes = Encoding.UTF8.GetBytes("caf\u00e9\r\n");
        string path;
        using (var log = SessionLog.Open(_dir, "x", Now))
        {
            path = log.Path;
            log.Write(bytes.AsSpan(0, 4));
            log.Write(bytes.AsSpan(4));
        }

        Assert.Equal("caf\u00e9", File.ReadAllLines(path)[1]);
    }

    [Fact]
    public void Backspace_AndCarriageReturn_Overwrite()
    {
        var lines = Run("lss\b \b\r\n", "50%\r100%\r\n");

        Assert.Equal(new[] { "ls", "100%" }, lines);
    }

    [Fact]
    public void EraseToEndOfLine_TrimsWhatTheShellRedraws()
    {
        var lines = Run("echo hello\b\b\b\u001b[Kworld\r\n");

        Assert.Equal(new[] { "echo hello".Substring(0, 7) + "world" }, lines);
    }

    [Fact]
    public void ALastLineWithoutANewline_IsWrittenWhenTheLogCloses()
    {
        var lines = Run("done\r\nuser@host:~$ ");

        Assert.Equal(new[] { "done", "user@host:~$" }, lines);
    }

    [Fact]
    public void OtherControlCharacters_AreDropped()
    {
        var lines = Run("a\u0007b\u0000c\r\n");

        Assert.Equal(new[] { "abc" }, lines);
    }

    [Fact]
    public void WritingAfterClose_IsIgnored()
    {
        var log = SessionLog.Open(_dir, "x", Now);
        log.Dispose();

        log.Write("late\r\n"u8);
        log.Dispose();

        Assert.Single(File.ReadAllLines(log.Path));
    }

    [Fact]
    public void TwoLogsStartedTheSameSecond_GetDifferentFiles()
    {
        using var a = SessionLog.Open(_dir, "x", Now);
        using var b = SessionLog.Open(_dir, "x", Now);

        Assert.NotEqual(a.Path, b.Path);
    }

    [Theory]
    [InlineData("web/01: prod", "web_01_ prod")]
    [InlineData("   ", "session")]
    [InlineData("...", "session")]
    public void SafeName_RemovesCharactersWindowsRejects(string name, string expected)
    {
        Assert.Equal(expected, SessionLog.SafeName(name));
    }

    [Fact]
    public void SafeName_LimitsTheLength()
    {
        Assert.Equal(60, SessionLog.SafeName(new string('a', 200)).Length);
    }
}
