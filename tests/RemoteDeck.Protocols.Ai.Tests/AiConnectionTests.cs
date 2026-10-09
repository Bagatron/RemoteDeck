using System.Net;
using System.Text;
using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Ai.Tests;

public class AiConnectionTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Body, string? Auth)> Requests { get; } = new();

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
            {
                Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, body, request.Headers.Authorization?.ToString()));
            }

            return Respond(request);
        }
    }

    private sealed class Broker(string? key) : ICredentialBroker
    {
        public ValueTask<T> UseAsync<T>(string connectionId, Func<ICredential, ValueTask<T>> use, CancellationToken cancellationToken = default)
        {
            if (key is null)
            {
                throw new KeyNotFoundException();
            }

            return use(new Cred(key));
        }

        private sealed class Cred(string key) : ICredential
        {
            public string? Username => null;

            public void ReadPassword(SecretReader reader) => reader(key.AsSpan());
        }
    }

    private sealed class Collector : IObserver<ReadOnlyMemory<byte>>
    {
        private readonly StringBuilder _text = new();

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        public void OnNext(ReadOnlyMemory<byte> value)
        {
            lock (_text)
            {
                _text.Append(Encoding.UTF8.GetString(value.Span));
            }
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Sse(params string[] deltas)
    {
        var sb = new StringBuilder();
        foreach (var d in deltas)
        {
            sb.Append("data: {\"choices\":[{\"delta\":{\"content\":\"").Append(d).Append("\"}}]}\n\n");
        }

        sb.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    private static async Task<(AiConnection Connection, Collector Out, FakeHandler Http)> Open(
        Dictionary<string, string>? options = null, string? key = "sk-test", Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var http = new FakeHandler
        {
            Respond = respond ?? (r => r.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
                ? Json("{\"data\":[{\"id\":\"llama3\"},{\"id\":\"qwen\"}]}")
                : Sse("Hel", "lo\\nthere"))
        };
        var def = new ConnectionDefinition("a1", "Chat", "ai", "chat.lan", 3000, Options: options);
        var connection = new AiConnection(def, new Broker(key), http);
        var output = new Collector();
        connection.Output.Subscribe(output);
        await connection.ConnectAsync();
        await connection.Idle();
        return (connection, output, http);
    }

    private static async Task Type(AiConnection connection, string text)
    {
        await connection.WriteAsync(Encoding.UTF8.GetBytes(text + "\r"));
        await Task.Delay(20);
        await connection.Idle();
    }

    [Fact]
    public async Task Connect_PicksTheFirstModelAndShowsAPrompt()
    {
        var (connection, output, http) = await Open();
        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Equal("llama3", connection.Model);
        Assert.Contains("llama3", output.Text);
        Assert.Contains("You", output.Text);
        Assert.Equal("http://chat.lan:3000/api/models", http.Requests[0].Url);
        Assert.Equal("Bearer sk-test", http.Requests[0].Auth);
    }

    [Fact]
    public async Task AMessageStreamsTheReplyAndSendsHistory()
    {
        var (connection, output, http) = await Open(new Dictionary<string, string> { ["model"] = "qwen", ["system"] = "Be brief." });
        await Type(connection, "hello");
        Assert.Contains("Hello\r\nthere", output.Text);
        var chat = http.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("http://chat.lan:3000/api/chat/completions", chat.Url);
        Assert.Contains("\"model\":\"qwen\"", chat.Body);
        Assert.Contains("Be brief.", chat.Body);
        Assert.Contains("hello", chat.Body);

        await Type(connection, "again");
        var second = http.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Contains("Hello\\nthere", second.Body);
    }

    [Fact]
    public async Task OpenAiFlavorUsesV1Paths()
    {
        var (connection, _, http) = await Open(new Dictionary<string, string> { ["server"] = "openai" });
        await Type(connection, "hi");
        Assert.Contains(http.Requests, r => r.Url == "http://chat.lan:3000/v1/models");
        Assert.Contains(http.Requests, r => r.Url == "http://chat.lan:3000/v1/chat/completions");
    }

    [Fact]
    public async Task NoSavedKeyStillConnectsWithoutAuthorization()
    {
        var (_, _, http) = await Open(key: null);
        Assert.Null(http.Requests[0].Auth);
    }

    [Fact]
    public async Task ModelsCommandListsAndModelSwitchesByNumber()
    {
        var (connection, output, _) = await Open();
        await Type(connection, "/models");
        Assert.Contains("qwen", output.Text);
        await Type(connection, "/model 2");
        Assert.Equal("qwen", connection.Model);
    }

    [Fact]
    public async Task ServerErrorsAreShownNotThrown()
    {
        var (connection, output, _) = await Open(respond: r => r.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
            ? Json("{\"data\":[{\"id\":\"m\"}]}")
            : new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"detail\":\"bad key\"}") });
        await Type(connection, "hi");
        Assert.Contains("401", output.Text);
        Assert.Contains("bad key", output.Text);
        Assert.Equal(ConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task ExitDisconnects()
    {
        var (connection, _, _) = await Open();
        await Type(connection, "/exit");
        Assert.Equal(ConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task ABadAddressFailsToConnect()
    {
        var connection = new AiConnection(new ConnectionDefinition("a2", "Chat", "ai", "ftp://nope"), new Broker(null), new FakeHandler());
        await Assert.ThrowsAsync<AiConnectionException>(async () => await connection.ConnectAsync());
        Assert.Equal(ConnectionState.Failed, connection.State);
    }

    private static string TempFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rd-ai-" + Guid.NewGuid().ToString("N"));
        return Path.Combine(folder, "chat.json");
    }

    private static Dictionary<string, string> WithHistory(string file, params (string Key, string Value)[] more)
    {
        var options = new Dictionary<string, string> { ["historyFile"] = file, ["model"] = "qwen" };
        foreach (var (key, value) in more)
        {
            options[key] = value;
        }

        return options;
    }

    [Fact]
    public async Task TheConversationIsSavedAfterEachReply()
    {
        var file = TempFile();
        var (connection, _, _) = await Open(WithHistory(file));
        await Type(connection, "hello");

        var saved = ChatHistory.Load(file)!;
        Assert.Equal("qwen", saved.Model);
        Assert.Equal(new[] { "user", "assistant" }, saved.Messages.Select(m => m.Role));
        Assert.Equal("hello", saved.Messages[0].Content);
        Assert.Equal("Hello\nthere", saved.Messages[1].Content);
    }

    [Fact]
    public async Task AReopenedChatRestoresAndSendsTheEarlierContext()
    {
        var file = TempFile();
        var (first, _, _) = await Open(WithHistory(file));
        await Type(first, "my name is Mickey");

        var (second, output, http) = await Open(WithHistory(file));
        Assert.Contains("Restored 2 earlier message", output.Text);
        Assert.Contains("my name is Mickey", output.Text);

        await Type(second, "what is my name?");
        var chat = http.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Contains("my name is Mickey", chat.Body);
        Assert.Contains("what is my name?", chat.Body);
    }

    [Fact]
    public async Task ClearForgetsTheSavedConversation()
    {
        var file = TempFile();
        var (connection, _, _) = await Open(WithHistory(file));
        await Type(connection, "hello");
        await Type(connection, "/clear");

        Assert.Empty(ChatHistory.Load(file)!.Messages);
    }

    [Fact]
    public async Task SaveHistoryToWritesACopyForAnotherWorkspace()
    {
        var file = TempFile();
        var copy = TempFile();
        var (connection, _, _) = await Open(WithHistory(file));
        await Type(connection, "hello");

        connection.SaveHistoryTo(copy);

        Assert.Equal(2, ChatHistory.Load(copy)!.Messages.Count);
    }

    [Fact]
    public async Task OnlyTheMostRecentMessagesAreSentAsContext()
    {
        var file = TempFile();
        var (connection, _, http) = await Open(WithHistory(file, ("contextMessages", "2")));
        await Type(connection, "first");
        await Type(connection, "second");
        await Type(connection, "third");

        var last = http.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Contains("third", last.Body);
        Assert.DoesNotContain("first", last.Body);
        Assert.Equal(3, ChatHistory.Load(file)!.Messages.Count(m => m.Role == "user"));
    }

    [Fact]
    public void ADamagedHistoryFileIsIgnored()
    {
        var file = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ not json");

        Assert.Null(ChatHistory.Load(file));
        Assert.Null(ChatHistory.Load(Path.Combine(Path.GetTempPath(), "does-not-exist.json")));
    }

    [Fact]
    public void AnEmptyStateRemovesTheFile()
    {
        var file = TempFile();
        ChatHistory.Save(file, new ChatState("m", "be brief", new[] { new ChatMessage("user", "hi") }));
        Assert.True(File.Exists(file));

        ChatHistory.Save(file, new ChatState(string.Empty, string.Empty, Array.Empty<ChatMessage>()));

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void ToTerminalUsesCarriageReturns() =>
        Assert.Equal("a\r\nb\r\nc", AiConnection.ToTerminal("a\nb\r\nc"));

    [Fact]
    public void FactoryCreatesAnAiTerminal()
    {
        var factory = new AiConnectionFactory();
        Assert.Equal("ai", factory.Type);
        Assert.IsAssignableFrom<ITerminalConnection>(factory.Create(new ConnectionDefinition("x", "x", "ai", "h"), new Broker(null)));
    }
}
