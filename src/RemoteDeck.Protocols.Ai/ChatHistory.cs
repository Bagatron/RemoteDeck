using System.Text.Json;

namespace RemoteDeck.Protocols.Ai;

/// <summary>What a chat keeps between sessions: the model and system prompt in use, and the conversation.</summary>
internal sealed record ChatState(string Model, string System, IReadOnlyList<ChatMessage> Messages);

/// <summary>Reads and writes a chat's saved conversation as a small JSON file.</summary>
internal static class ChatHistory
{
    public const int FormatVersion = 1;

    private sealed class FileShape
    {
        public int Version { get; set; } = FormatVersion;

        public string? Model { get; set; }

        public string? System { get; set; }

        public List<MessageShape>? Messages { get; set; }
    }

    private sealed class MessageShape
    {
        public string? Role { get; set; }

        public string? Content { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>The saved state, or null when there is no file or it cannot be read. A damaged file is never an error.</summary>
    public static ChatState? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), Json);
            if (shape is null)
            {
                return null;
            }

            var messages = (shape.Messages ?? new List<MessageShape>())
                .Where(m => m.Role is "user" or "assistant" && !string.IsNullOrEmpty(m.Content))
                .Select(m => new ChatMessage(m.Role!, m.Content!))
                .ToList();
            return new ChatState(shape.Model ?? string.Empty, shape.System ?? string.Empty, messages);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the state through a temporary file so a crash never leaves half a file. A state with nothing in it removes the file.</summary>
    public static void Save(string path, ChatState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (state.Messages.Count == 0 && state.System.Length == 0 && state.Model.Length == 0)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var shape = new FileShape
        {
            Model = state.Model,
            System = state.System,
            Messages = state.Messages.Select(m => new MessageShape { Role = m.Role, Content = m.Content }).ToList()
        };
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(shape, Json));
        File.Move(temp, path, overwrite: true);
    }
}
