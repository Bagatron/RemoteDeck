namespace RemoteDeck.Protocols.Ai.Tests;

public class ChatProtocolTests
{
    [Theory]
    [InlineData("openwebui", null, "/api/chat/completions")]
    [InlineData("", null, "/api/chat/completions")]
    [InlineData("openai", null, "/v1/chat/completions")]
    [InlineData("OpenAI", "", "/v1/chat/completions")]
    [InlineData("openwebui", "custom/chat", "/custom/chat")]
    public void ChatPath_FollowsTheServerType(string flavor, string? over, string expected) =>
        Assert.Equal(expected, ChatProtocol.ChatPath(flavor, over));

    [Fact]
    public void ModelsPath_FollowsTheServerType()
    {
        Assert.Equal("/api/models", ChatProtocol.ModelsPath("openwebui"));
        Assert.Equal("/v1/models", ChatProtocol.ModelsPath("openai"));
    }

    [Theory]
    [InlineData("localhost", null, "http://localhost/")]
    [InlineData("localhost", 3000, "http://localhost:3000/")]
    [InlineData("https://ai.example.com/", null, "https://ai.example.com/")]
    [InlineData("http://10.0.0.5:8080", null, "http://10.0.0.5:8080/")]
    public void TryBuildBase_AcceptsAddresses(string host, int? port, string expected)
    {
        Assert.True(ChatProtocol.TryBuildBase(host, port, out var uri, out _));
        Assert.Equal(expected, uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://host")]
    public void TryBuildBase_RejectsBadAddresses(string host)
    {
        Assert.False(ChatProtocol.TryBuildBase(host, null, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Combine_KeepsAPathPrefix()
    {
        ChatProtocol.TryBuildBase("https://host/openwebui/", null, out var uri, out _);
        Assert.Equal("https://host/openwebui/api/models", ChatProtocol.Combine(uri, "/api/models").AbsoluteUri);
    }

    [Fact]
    public void BuildRequest_EscapesAndOrders()
    {
        var json = ChatProtocol.BuildRequest("m", new[] { new ChatMessage("user", "say \"hi\"\nnow") }, true);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("m", doc.RootElement.GetProperty("model").GetString());
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("say \"hi\"\nnow", doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void ParseModels_ReadsOpenAiAndOllamaShapes()
    {
        Assert.Equal(new[] { "a", "b" }, ChatProtocol.ParseModels("{\"data\":[{\"id\":\"a\"},{\"id\":\"b\"}]}"));
        Assert.Equal(new[] { "llama3" }, ChatProtocol.ParseModels("{\"models\":[{\"name\":\"llama3\"}]}"));
        Assert.Empty(ChatProtocol.ParseModels("not json"));
    }

    [Fact]
    public void ParseStreamLine_ReadsDeltasAndDone()
    {
        Assert.Equal("Hel", ChatProtocol.ParseStreamLine("data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}", out var done));
        Assert.False(done);
        Assert.Equal(string.Empty, ChatProtocol.ParseStreamLine("data: [DONE]", out done));
        Assert.True(done);
        Assert.Null(ChatProtocol.ParseStreamLine(": keep-alive", out _));
        Assert.Equal(string.Empty, ChatProtocol.ParseStreamLine("data: {\"choices\":[{\"delta\":{}}]}", out _));
    }

    [Fact]
    public void ParseReply_ReadsAWholeMessage() =>
        Assert.Equal("hi", ChatProtocol.ParseReply("{\"choices\":[{\"message\":{\"content\":\"hi\"}}]}"));

    [Theory]
    [InlineData(401, "{\"detail\":\"Not authenticated\"}", "Not authenticated")]
    [InlineData(404, "{\"error\":{\"message\":\"no such model\"}}", "no such model")]
    [InlineData(500, "oops", "500")]
    public void ErrorText_UsesTheServersMessage(int status, string body, string expected) =>
        Assert.Contains(expected, ChatProtocol.ErrorText(status, body));
}
