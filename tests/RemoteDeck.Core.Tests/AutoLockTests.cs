using RemoteDeck.Core.Security;

namespace RemoteDeck.Core.Tests;

public class AutoLockTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(1, 1)]
    [InlineData(15, 15)]
    [InlineData(100000, 1440)]
    public void Normalize_KeepsSensibleValues(int input, int expected)
    {
        Assert.Equal(expected, AutoLock.Normalize(input));
    }

    [Fact]
    public void LocksOnlyOnceTheIdleTimeIsReached()
    {
        Assert.False(AutoLock.IsDue(TimeSpan.FromMinutes(14.9), 15));
        Assert.True(AutoLock.IsDue(TimeSpan.FromMinutes(15), 15));
        Assert.True(AutoLock.IsDue(TimeSpan.FromHours(3), 15));
    }

    [Fact]
    public void ZeroNeverLocks()
    {
        Assert.False(AutoLock.IsDue(TimeSpan.FromDays(30), 0));
    }
}
