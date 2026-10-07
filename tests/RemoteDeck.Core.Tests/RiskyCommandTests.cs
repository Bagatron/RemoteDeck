using RemoteDeck.Core.Broadcast;

namespace RemoteDeck.Core.Tests;

public class RiskyCommandTests
{
    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("sudo rm -fr /var/www")]
    [InlineData("rm -R build")]
    [InlineData("rm /tmp/old --recursive")]
    [InlineData("shutdown -h now")]
    [InlineData("sudo reboot")]
    [InlineData("poweroff")]
    [InlineData("mkfs.ext4 /dev/sdb1")]
    [InlineData("dd if=/dev/zero of=/dev/sda bs=1M")]
    [InlineData("cat image.iso > /dev/sdb")]
    [InlineData("chmod -R 777 /")]
    [InlineData(":(){ :|:& };:")]
    [InlineData("DROP TABLE users;")]
    [InlineData("drop database prod;")]
    [InlineData(@"Remove-Item C:\data -Recurse -Force")]
    [InlineData("format d:")]
    public void Detects_RiskyCommands(string command)
    {
        Assert.NotNull(RiskyCommandDetector.Detect(command));
    }

    [Theory]
    [InlineData("ls -la")]
    [InlineData("rm notes.txt")]
    [InlineData("rm -f notes.txt")]
    [InlineData("rm -f my-report.txt")]
    [InlineData("echo hello")]
    [InlineData("git status")]
    [InlineData("systemctl status nginx")]
    [InlineData("cat firmware.txt")]
    [InlineData("tail -f /var/log/syslog")]
    [InlineData("Get-ChildItem C:\\data")]
    public void Ignores_OrdinaryCommands(string command)
    {
        Assert.Null(RiskyCommandDetector.Detect(command));
    }

    [Fact]
    public void Policy_FlagsMultiLineText_ButNotATrailingNewline()
    {
        var policy = new BroadcastPolicy();

        Assert.Equal(BroadcastRiskKind.MultiLinePaste, policy.Evaluate("echo a\necho b").Kind);
        Assert.Equal(BroadcastRiskKind.MultiLinePaste, policy.Evaluate("echo a\r\necho b\r\n").Kind);
        Assert.False(policy.Evaluate("ls\n").IsRisky);
        Assert.False(policy.Evaluate("ls").IsRisky);
    }

    [Fact]
    public void Policy_RiskyCommand_TakesPriorityOverMultiLine()
    {
        var risk = new BroadcastPolicy().Evaluate("echo ok\nrm -rf /tmp/x");

        Assert.Equal(BroadcastRiskKind.RiskyCommand, risk.Kind);
        Assert.False(string.IsNullOrWhiteSpace(risk.Detail));
    }

    [Fact]
    public void Policy_CanTurnChecksOff()
    {
        var policy = new BroadcastPolicy { ConfirmMultiLine = false, ConfirmRiskyCommands = false };

        Assert.False(policy.Evaluate("rm -rf /\nreboot").IsRisky);
    }
}
