using RemoteDeck.Core;
using RemoteDeck.Core.Layout;

namespace RemoteDeck.Core.Tests;

public sealed class WorkspaceNamesTests
{
    private static readonly string[] Existing = { "Prod k8s", "Staging", "k8s lab", "Network", "network (old)", "Prod k8s" };

    [Fact]
    public void Filter_NothingTypedGivesEverythingOnceAlphabetically()
    {
        Assert.Equal(new[] { "k8s lab", "Network", "network (old)", "Prod k8s", "Staging" }, WorkspaceNames.Filter(Existing, ""));
    }

    [Fact]
    public void Filter_MatchesAnywhereWithStartsWithFirst()
    {
        Assert.Equal(new[] { "k8s lab", "Prod k8s" }, WorkspaceNames.Filter(Existing, "K8S"));
        Assert.Equal(new[] { "Network", "network (old)" }, WorkspaceNames.Filter(Existing, "net"));
        Assert.Empty(WorkspaceNames.Filter(Existing, "zzz"));
    }

    [Fact]
    public void FindExact_IgnoresCaseAndSpaces()
    {
        Assert.Equal("Staging", WorkspaceNames.FindExact(Existing, "  staging "));
        Assert.Null(WorkspaceNames.FindExact(Existing, "stag"));
        Assert.Null(WorkspaceNames.FindExact(Existing, ""));
    }

    [Theory]
    [InlineData("0.5.0", "v0.5.0")]
    [InlineData("0.5.0+abc123", "v0.5.0")]
    [InlineData("v0.5.0-beta.1+x", "v0.5.0-beta.1")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void AppVersion_Label(string? input, string expected) => Assert.Equal(expected, AppVersion.Label(input));
}
