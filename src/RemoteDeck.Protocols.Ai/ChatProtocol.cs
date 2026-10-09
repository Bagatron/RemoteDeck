using System.Text;
using System.Text.Json;

namespace RemoteDeck.Protocols.Ai;

/// <summary>One message in the conversation.</summary>
internal sealed record ChatMessage(string Role, string Content);

/// <summary>Request building and response parsing for OpenAI-compatible chat servers (Open WebUI, Ollama, LM Studio, OpenAI).</summary>
internal static class ChatProtocol
{
    /// <summary>The chat endpoint path for a server flavor. <paramref name="pathOverride"/> wins when given.</summary>
    public static string ChatPath(string flavor, string? pathOverride) =>
        !string.IsNullOrWhiteSpace(pathOverride) ? Normalize(pathOverride)
        : IsOpenWebUi(flavor) ? "/api/chat/completions" : "/v1/chat/completions";

    public static string ModelsPath(string flavor) =>
        IsOpenWebUi(flavor) ? "/api/models" : "/v1/models";

    public static bool IsOpenWebUi(string flavor) =>
        !string.Equals(flavor, "openai", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.StartsWith('/') ? path : "/" + path;

    /// <summary>Turns what was typed in the Address box into a base URI. A bare host gets http://, and the port is added if given.</summary>
    public static bool TryBuildBase(string host, int? port, out Uri baseUri, out string error)
    {
        baseUri = null!;
        error = string.Empty;
        var text = host.Trim();
        if (text.Length == 0)
        {
            error = "Enter the address of the AI server, for example http://localhost:3000.";
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "The address must be a web address such as http://localhost:3000 or https://ai.example.com.";
            return false;
        }

        var builder = new UriBuilder(uri);
        if (port is { } p)
        {
            builder.Port = p;
        }

        var path = builder.Path.TrimEnd('/');
        builder.Path = path;
        baseUri = builder.Uri;
        return true;
    }

    public static Uri Combine(Uri baseUri, string path)
    {
        var b = baseUri.AbsoluteUri.TrimEnd('/');
        return new Uri(b + path);
    }

    public static string BuildRequest(string model, IReadOnlyList<ChatMessage> messages, bool stream)
    {
        using var stream2 = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream2))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteBoolean("stream", stream);
            w.WriteStartArray("messages");
            foreach (var m in messages)
            {
                w.WriteStartObject();
                w.WriteString("role", m.Role);
                w.WriteString("content", m.Content);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream2.ToArray());
    }

    /// <summary>Reads the model ids out of a models response ({"data":[{"id":...}]} or {"models":[{"name"/"id":...}]}).</summary>
    public static List<string> ParseModels(string json)
    {
        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement list;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                list = doc.RootElement;
            }
            else if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                list = data;
            }
            else if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                list = models;
            }
            else
            {
                return result;
            }

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                {
                    result.Add(s);
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "id", "name", "model" })
                    {
                        if (item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } id)
                        {
                            result.Add(id);
                            break;
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return result;
    }

    /// <summary>
    /// Reads one line of a streamed reply ("data: {...}"). Returns the text delta (possibly empty), and sets
    /// <paramref name="done"/> on "data: [DONE]". Lines that are not data lines return null.
    /// </summary>
    public static string? ParseStreamLine(string line, out bool done)
    {
        done = false;
        if (!line.StartsWith("data:", StringComparison.Ordinal))
        {
            return null;
        }

        var payload = line[5..].Trim();
        if (payload == "[DONE]")
        {
            done = true;
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString() ?? string.Empty;
                }

                if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var mc) && mc.ValueKind == JsonValueKind.String)
                {
                    return mc.GetString() ?? string.Empty;
                }
            }

            return string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>Reads a non-streamed reply, or an error message from an error body.</summary>
    public static string ParseReply(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                return c.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
        }

        return string.Empty;
    }

    /// <summary>A short human message from an HTTP error body.</summary>
    public static string ErrorText(int status, string body)
    {
        var detail = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                detail = err.ValueKind == JsonValueKind.String ? err.GetString() ?? string.Empty
                    : err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? string.Empty : string.Empty;
            }
            else if (root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
            {
                detail = d.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
        }

        var hint = status switch
        {
            401 or 403 => " Check the API key.",
            404 => " Check the address, the server type and the model name.",
            _ => string.Empty
        };
        return $"The server answered {status}{(detail.Length > 0 ? ": " + detail : string.Empty)}.{hint}";
    }
}
