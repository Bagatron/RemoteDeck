using RemoteDeck.Core.Connections;

namespace RemoteDeck.Core.Tests;

public class ReconnectPolicyTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 30)]
    [InlineData(8, 30)]
    [InlineData(1000, 30)]
    public void WaitsLongerEachTime_UpToThirtySeconds(int attempt, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), ReconnectPolicy.Delay(attempt));
    }

    [Fact]
    public void AttemptsStartAtOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconnectPolicy.Delay(0));
    }
}
