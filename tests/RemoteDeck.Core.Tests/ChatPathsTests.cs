using RemoteDeck.Core.Chats;

namespace RemoteDeck.Core.Tests;

public class ChatPathsTests
{
    [Fact]
    public void WorkspaceChatsLiveUnderTheWorkspaceAndPane()
    {
        var path = ChatPaths.ForWorkspacePane("C:\\chats", "Lab: day 1", "pane-2");

        Assert.Equal(Path.Combine("C:\\chats", "workspaces", "Lab_ day 1", "pane-2.json"), path);
    }

    [Fact]
    public void DifferentPanesAndWorkspacesDoNotShareAFile()
    {
        Assert.NotEqual(ChatPaths.ForWorkspacePane("r", "A", "p1"), ChatPaths.ForWorkspacePane("r", "A", "p2"));
        Assert.NotEqual(ChatPaths.ForWorkspacePane("r", "A", "p1"), ChatPaths.ForWorkspacePane("r", "B", "p1"));
    }

    [Fact]
    public void AConnectionOpenedOnItsOwnHasItsOwnFile() =>
        Assert.Equal(Path.Combine("r", "connections", "abc123.json"), ChatPaths.ForConnection("r", "abc123"));
}
