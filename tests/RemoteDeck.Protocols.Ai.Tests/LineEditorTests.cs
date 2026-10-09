using System.Text;

namespace RemoteDeck.Protocols.Ai.Tests;

public class LineEditorTests
{
    private static (string Echo, List<string> Lines) Feed(LineEditor editor, string text)
    {
        var lines = new List<string>();
        var echo = editor.Feed(Encoding.UTF8.GetBytes(text), lines);
        return (echo, lines);
    }

    [Fact]
    public void TypingEchoesAndEnterCompletesTheLine()
    {
        var e = new LineEditor();
        var (echo, lines) = Feed(e, "hi");
        Assert.Equal("hi", echo);
        Assert.Empty(lines);
        (echo, lines) = Feed(e, "\r");
        Assert.Equal("\r\n", echo);
        Assert.Equal(new[] { "hi" }, lines);
        Assert.Equal(string.Empty, e.Current);
    }

    [Fact]
    public void BackspaceRemovesTheLastCharacter()
    {
        var e = new LineEditor();
        Feed(e, "abc");
        var (echo, _) = Feed(e, "\u007f");
        Assert.Equal("\b \b", echo);
        Assert.Equal("ab", e.Current);
        Assert.Equal(string.Empty, Feed(new LineEditor(), "\u007f").Echo);
    }

    [Fact]
    public void CtrlUClearsTheLine()
    {
        var e = new LineEditor();
        Feed(e, "abc");
        Feed(e, "\u0015");
        Assert.Equal(string.Empty, e.Current);
    }

    [Fact]
    public void CtrlCReportsAnInterrupt()
    {
        var e = new LineEditor();
        Feed(e, "abc");
        var (_, lines) = Feed(e, "\u0003");
        Assert.Equal(new[] { "\u0003" }, lines);
        Assert.Equal(string.Empty, e.Current);
    }

    [Fact]
    public void PastedTextWithSeveralLinesGivesSeveralLines()
    {
        var (_, lines) = Feed(new LineEditor(), "one\r\ntwo\nthree\r");
        Assert.Equal(new[] { "one", "two", "three" }, lines);
    }

    [Fact]
    public void ArrowKeysAreIgnored()
    {
        var e = new LineEditor();
        Feed(e, "a\u001b[Ab");
        Assert.Equal("ab", e.Current);
    }

    [Fact]
    public void ASplitUtf8CharacterIsJoinedAcrossCalls()
    {
        var e = new LineEditor();
        var bytes = Encoding.UTF8.GetBytes("é");
        var lines = new List<string>();
        e.Feed(bytes.AsSpan(0, 1), lines);
        e.Feed(bytes.AsSpan(1), lines);
        Assert.Equal("é", e.Current);
    }
}
