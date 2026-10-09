using RemoteDeck.Core.Notes;

namespace RemoteDeck.Core.Chats;

/// <summary>
/// Where AI chat conversations are kept: one file per pane of a saved workspace, so opening the workspace again brings
/// each chat back with its context, and one file per connection for chats that are not part of a workspace.
/// </summary>
public static class ChatPaths
{
    public static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDeck", "chats");

    /// <summary>The conversation of one pane in a saved workspace.</summary>
    public static string ForWorkspacePane(string root, string workspaceName, string paneId) =>
        Path.Combine(root, "workspaces", NoteLibrary.Slug(workspaceName), NoteLibrary.Slug(paneId) + ".json");

    /// <summary>The conversation of a connection opened on its own (when it is set to remember its chat).</summary>
    public static string ForConnection(string root, string connectionId) =>
        Path.Combine(root, "connections", NoteLibrary.Slug(connectionId) + ".json");
}
