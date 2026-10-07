using System.Text;
using RemoteDeck.Core.Broadcast;

namespace RemoteDeck.Core.Tests;

public class BroadcastRouterTests
{
    private static readonly byte[] Ls = Encoding.UTF8.GetBytes("ls");

    private static (BroadcastRouter Router, FakeTerminal A, FakeTerminal B, FakeTerminal C) CreateGroup()
    {
        var router = new BroadcastRouter();
        var a = new FakeTerminal("a");
        var b = new FakeTerminal("b");
        var c = new FakeTerminal("c");
        router.Register("p1", a);
        router.Register("p2", b);
        router.Register("p3", c);
        return (router, a, b, c);
    }

    private static Func<BroadcastRisk, ValueTask<bool>> Answer(bool approve, List<BroadcastRisk>? asked = null) =>
        risk =>
        {
            asked?.Add(risk);
            return ValueTask.FromResult(approve);
        };

    [Fact]
    public async Task Input_StaysInSourcePane_WhenBroadcastIsOff()
    {
        var (router, a, b, c) = CreateGroup();
        router.SetMember("p1", true);
        router.SetMember("p2", true);

        var result = await router.RouteInputAsync("p1", Ls);

        Assert.Equal(new[] { "ls" }, a.Received);
        Assert.Empty(b.Received);
        Assert.Empty(c.Received);
        Assert.Equal(1, result.Attempted);
    }

    [Fact]
    public async Task Input_IsMirroredToEveryMember_WhenBroadcastIsOn()
    {
        var (router, a, b, c) = CreateGroup();
        router.SetMember("p1", true);
        router.SetMember("p2", true);
        router.SetEnabled(true);

        var result = await router.RouteInputAsync("p1", Ls);

        Assert.Equal(new[] { "ls" }, a.Received);
        Assert.Equal(new[] { "ls" }, b.Received);
        Assert.Empty(c.Received);
        Assert.Equal(2, result.Sent);
    }

    [Fact]
    public async Task TypingInANonMemberPane_DoesNotBroadcast()
    {
        var (router, a, b, c) = CreateGroup();
        router.SetMember("p1", true);
        router.SetMember("p2", true);
        router.SetEnabled(true);

        await router.RouteInputAsync("p3", Ls);

        Assert.Equal(new[] { "ls" }, c.Received);
        Assert.Empty(a.Received);
        Assert.Empty(b.Received);
    }

    [Fact]
    public async Task OneFailingSession_DoesNotStopTheOthers()
    {
        var (router, a, b, _) = CreateGroup();
        b.ThrowOnWrite = true;
        router.SetMember("p1", true);
        router.SetMember("p2", true);
        router.SetEnabled(true);

        var result = await router.RouteInputAsync("p1", Ls);

        Assert.Equal(new[] { "ls" }, a.Received);
        Assert.Equal(2, result.Attempted);
        Assert.Equal(1, result.Sent);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("p2", failure.PaneId);
    }

    [Fact]
    public async Task UnknownSourcePane_Throws()
    {
        var (router, _, _, _) = CreateGroup();

        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await router.RouteInputAsync("nope", Ls));
    }

    [Fact]
    public async Task CommandBar_SendsTheLineWithEnter_ToAllMembers_EvenWithLiveBroadcastOff()
    {
        var (router, a, b, c) = CreateGroup();
        router.SetMember("p1", true);
        router.SetMember("p3", true);

        var result = await router.SendCommandAsync("uptime");

        Assert.Equal(new[] { "uptime\r" }, a.Received);
        Assert.Equal(new[] { "uptime\r" }, c.Received);
        Assert.Empty(b.Received);
        Assert.Equal(2, result.Sent);
    }

    [Fact]
    public async Task CommandBar_NormalizesLineEndingsToCarriageReturn()
    {
        var (router, a, _, _) = CreateGroup();
        router.SetMember("p1", true);

        await router.SendCommandAsync("echo one\r\necho two\n", Answer(true));

        Assert.Equal(new[] { "echo one\recho two\r" }, a.Received);
    }

    [Fact]
    public async Task CommandBar_WithNoMembers_SendsNothing()
    {
        var (router, a, _, _) = CreateGroup();

        var result = await router.SendCommandAsync("uptime");

        Assert.Equal(0, result.Attempted);
        Assert.Empty(a.Received);
    }

    [Fact]
    public async Task CommandBar_RiskyCommand_IsBlockedWithoutConfirmation()
    {
        var (router, a, b, _) = CreateGroup();
        router.SetMember("p1", true);
        router.SetMember("p2", true);

        var result = await router.SendCommandAsync("sudo rm -rf /var/www");

        Assert.True(result.WasBlocked);
        Assert.Equal(BroadcastRiskKind.RiskyCommand, result.BlockedBy!.Kind);
        Assert.Empty(a.Received);
        Assert.Empty(b.Received);
    }

    [Fact]
    public async Task CommandBar_RiskyCommand_IsSentWhenUserConfirms()
    {
        var (router, a, b, _) = CreateGroup();
        router.SetMember("p1", true);
        router.SetMember("p2", true);
        var asked = new List<BroadcastRisk>();

        var result = await router.SendCommandAsync("sudo reboot", Answer(true, asked));

        Assert.False(result.WasBlocked);
        Assert.Single(asked);
        Assert.Equal(new[] { "sudo reboot\r" }, a.Received);
        Assert.Equal(new[] { "sudo reboot\r" }, b.Received);
    }

    [Fact]
    public async Task CommandBar_RiskyCommand_IsBlockedWhenUserDeclines()
    {
        var (router, a, _, _) = CreateGroup();
        router.SetMember("p1", true);

        var result = await router.SendCommandAsync("shutdown -h now", Answer(false));

        Assert.True(result.WasBlocked);
        Assert.Empty(a.Received);
    }

    [Fact]
    public async Task Paste_MultiLine_NeedsConfirmation_WhenItWouldReachSeveralSessions()
    {
        var (router, a, b, _) = CreateGroup();
        router.SetMember("p1", true);
        router.SetMember("p2", true);
        router.SetEnabled(true);
        var asked = new List<BroadcastRisk>();

        var declined = await router.RoutePasteAsync("p1", "echo a\necho b\n", Answer(false, asked));

        Assert.True(declined.WasBlocked);
        Assert.Equal(BroadcastRiskKind.MultiLinePaste, Assert.Single(asked).Kind);
        Assert.Empty(a.Received);
        Assert.Empty(b.Received);

        var approved = await router.RoutePasteAsync("p1", "echo a\necho b\n", Answer(true));

        Assert.False(approved.WasBlocked);
        Assert.Equal(new[] { "echo a\necho b\n" }, a.Received);
        Assert.Equal(new[] { "echo a\necho b\n" }, b.Received);
    }

    [Fact]
    public async Task Paste_ToASingleSession_NeverAsksForConfirmation()
    {
        var (router, a, _, _) = CreateGroup();
        var asked = new List<BroadcastRisk>();

        var result = await router.RoutePasteAsync("p1", "echo a\necho b\n", Answer(false, asked));

        Assert.False(result.WasBlocked);
        Assert.Empty(asked);
        Assert.Equal(new[] { "echo a\necho b\n" }, a.Received);
    }

    [Fact]
    public async Task Paste_WithNoConfirmCallback_FailsClosed_WhenBroadcasting()
    {
        var (router, a, b, _) = CreateGroup();
        router.SetMember("p1", true);
        router.SetMember("p2", true);
        router.SetEnabled(true);

        var result = await router.RoutePasteAsync("p1", "line one\nline two");

        Assert.True(result.WasBlocked);
        Assert.Empty(a.Received);
        Assert.Empty(b.Received);
    }

    [Fact]
    public void Reset_TurnsBroadcastOffAndEmptiesTheGroup()
    {
        var (router, _, _, _) = CreateGroup();
        router.SetMember("p1", true);
        router.SetMember("p2", true);
        router.SetEnabled(true);
        var changes = 0;
        router.StateChanged += (_, _) => changes++;

        router.Reset();

        Assert.False(router.Enabled);
        Assert.Empty(router.Members);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Unregister_RemovesThePaneFromTheGroup()
    {
        var (router, _, _, _) = CreateGroup();
        router.SetMember("p1", true);
        router.SetMember("p2", true);

        router.Unregister("p2");

        Assert.Equal(new[] { "p1" }, router.Members);
    }

    [Fact]
    public void SetMember_ForUnregisteredPane_Throws()
    {
        var (router, _, _, _) = CreateGroup();

        Assert.Throws<KeyNotFoundException>(() => router.SetMember("nope", true));
    }

    [Fact]
    public void StateChanged_FiresOnlyWhenSomethingActuallyChanges()
    {
        var (router, _, _, _) = CreateGroup();
        var changes = 0;
        router.StateChanged += (_, _) => changes++;

        router.SetEnabled(false);
        router.SetMember("p1", true);
        router.SetMember("p1", true);
        router.SetEnabled(true);

        Assert.Equal(2, changes);
    }
}
