using RemoteDeck.Core.Security;

namespace RemoteDeck.Core.Tests;

public class ClipboardGuardTests
{
    private sealed class FakeClipboard
    {
        public string? Text { get; set; }

        public int Clears { get; private set; }

        public void Clear()
        {
            Text = null;
            Clears++;
        }
    }

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(30);

    [Fact]
    public async Task ClearsTheSecretAfterTheDelay()
    {
        var clip = new FakeClipboard { Text = "hunter2" };
        var guard = new ClipboardGuard(() => clip.Text, clip.Clear);

        await guard.WatchAsync("hunter2", Short);

        Assert.Null(clip.Text);
        Assert.False(guard.IsWatching);
    }

    [Fact]
    public async Task LeavesSomethingElseTheUserCopiedAlone()
    {
        var clip = new FakeClipboard { Text = "hunter2" };
        var guard = new ClipboardGuard(() => clip.Text, clip.Clear);

        var watching = guard.WatchAsync("hunter2", Short);
        clip.Text = "my shopping list";
        await watching;

        Assert.Equal("my shopping list", clip.Text);
        Assert.Equal(0, clip.Clears);
    }

    [Fact]
    public async Task ANewSecretReplacesTheOldCountdown()
    {
        var clip = new FakeClipboard { Text = "first" };
        var guard = new ClipboardGuard(() => clip.Text, clip.Clear);

        var first = guard.WatchAsync("first", TimeSpan.FromMilliseconds(400));
        clip.Text = "second";
        await guard.WatchAsync("second", Short);
        await first;

        Assert.Null(clip.Text);
        Assert.Equal(1, clip.Clears);
    }

    [Fact]
    public async Task ZeroDelayDisablesClearing()
    {
        var clip = new FakeClipboard { Text = "x" };
        var guard = new ClipboardGuard(() => clip.Text, clip.Clear);

        await guard.WatchAsync("x", TimeSpan.Zero);

        Assert.Equal("x", clip.Text);
        Assert.False(guard.IsWatching);
    }

    [Fact]
    public void ClearNowWipesAPendingSecretOnce()
    {
        var clip = new FakeClipboard { Text = "pw" };
        var guard = new ClipboardGuard(() => clip.Text, clip.Clear);
        _ = guard.WatchAsync("pw", TimeSpan.FromMinutes(5));

        guard.ClearNow();
        guard.ClearNow();

        Assert.Null(clip.Text);
        Assert.Equal(1, clip.Clears);
    }

    [Fact]
    public async Task ABusyClipboardDoesNotCrash()
    {
        var guard = new ClipboardGuard(() => throw new InvalidOperationException("busy"), () => { });

        await guard.WatchAsync("pw", Short);
    }
}
