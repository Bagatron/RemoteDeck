using System.Net;
using System.Net.Http.Headers;
using System.Text;
using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Ai;

/// <summary>A problem opening an AI chat. The message is safe to show to the user.</summary>
public sealed class AiConnectionException : Exception
{
    public AiConnectionException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// An AI chat in a terminal pane, talking to any OpenAI-compatible server (Open WebUI, Ollama, LM Studio, OpenAI).
/// Because it is an <see cref="ITerminalConnection"/> it gets split panes and broadcast input like every other type.
///
/// Options: <c>server</c> ("openwebui", the default, or "openai"), <c>path</c> (chat endpoint override),
/// <c>model</c>, <c>system</c> (system prompt) and <c>acceptUntrustedCertificate</c>. The API key is the connection's
/// saved password. Type /help in the chat for the commands.
/// </summary>
public sealed class AiConnection : ITerminalConnection
{
    private const string Prompt = "\u001b[1;36mYou\u001b[0m › ";
    private const string Reset = "\u001b[0m";
    private const string Dim = "\u001b[2m";
    private const string Red = "\u001b[31m";

    private readonly ConnectionDefinition _definition;
    private readonly ICredentialBroker _credentials;
    private readonly HttpMessageHandler? _handler;
    private readonly TerminalOutput _output = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _inputGate = new();
    private readonly LineEditor _editor = new();
    private readonly List<ChatMessage> _history = new();
    private readonly object _historyGate = new();
    private readonly int _contextMessages;
    private readonly string _flavor;

    private ConnectionState _state = ConnectionState.Disconnected;
    private HttpClient? _http;
    private Uri _base = null!;
    private string _model;
    private string _system;
    private string? _apiKey;
    private CancellationTokenSource? _reply;
    private Task _worker = Task.CompletedTask;
    private bool _busy;

    internal AiConnection(ConnectionDefinition definition, ICredentialBroker credentials, HttpMessageHandler? handler)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _definition = definition;
        _credentials = credentials;
        _handler = handler;
        _flavor = Option("server");
        _model = Option("model");
        _system = Option("system");
        HistoryFile = Option("historyFile") is { Length: > 0 } file ? file : null;
        _contextMessages = int.TryParse(Option("contextMessages"), out var context) && context is >= 2 and <= 1000 ? context : 40;
    }

    /// <summary>
    /// Where this chat keeps its conversation, or null to keep it only while the pane is open. The conversation is
    /// loaded from here on connect and written back after every change.
    /// </summary>
    public string? HistoryFile { get; set; }

    /// <summary>Writes the conversation as it is now to another file (used when a workspace is saved).</summary>
    public void SaveHistoryTo(string path)
    {
        ChatHistory.Save(path, Snapshot());
    }

    public string Id => _definition.Id;

    public ConnectionState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    public event EventHandler<ConnectionState>? StateChanged;

    public IObservable<ReadOnlyMemory<byte>> Output => _output;

    /// <summary>The model currently in use (for tests).</summary>
    internal string Model => _model;

    /// <summary>Waits for the chat to finish its current job (for tests).</summary>
    internal Task Idle()
    {
        lock (_inputGate)
        {
            return _worker;
        }
    }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == ConnectionState.Connected)
            {
                return;
            }

            SetState(ConnectionState.Connecting);
            try
            {
                if (!ChatProtocol.TryBuildBase(_definition.Host, _definition.Port, out var baseUri, out var error))
                {
                    throw new AiConnectionException(error);
                }

                _base = baseUri;
                _apiKey = await ReadKeyAsync(cancellationToken).ConfigureAwait(false);
                _http?.Dispose();
                _http = CreateClient();
                SetState(ConnectionState.Connected);
                lock (_inputGate)
                {
                    _worker = Task.Run(StartAsync);
                }
            }
            catch (OperationCanceledException)
            {
                SetState(ConnectionState.Disconnected);
                throw;
            }
            catch (Exception)
            {
                SetState(ConnectionState.Failed);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Close();
            SetState(ConnectionState.Disconnected);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
    {
        if (State != ConnectionState.Connected)
        {
            throw new InvalidOperationException("The AI chat is not connected.");
        }

        var lines = new List<string>();
        string echo;
        lock (_inputGate)
        {
            echo = _editor.Feed(input.Span, lines);
        }

        if (echo.Length > 0)
        {
            Print(echo);
        }

        foreach (var line in lines)
        {
            HandleLine(line);
        }

        return ValueTask.CompletedTask;
    }

    public void Resize(int columns, int rows)
    {
    }

    public ValueTask DisposeAsync()
    {
        Close();
        SetState(ConnectionState.Disconnected);
        _output.Complete();
        return ValueTask.CompletedTask;
    }

    private async Task<string?> ReadKeyAsync(CancellationToken cancellationToken)
    {
        try
        {
            string? key = null;
            await _credentials.UseAsync(
                _definition.Id,
                credential =>
                {
                    credential.ReadPassword(secret => key = secret.ToString());
                    return ValueTask.FromResult(true);
                },
                cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch (KeyNotFoundException)
        {
            // No key saved: fine for local servers such as Ollama or LM Studio.
            return null;
        }
    }

    private HttpClient CreateClient()
    {
        HttpMessageHandler handler;
        if (_handler is not null)
        {
            handler = _handler;
        }
        else
        {
            var socketHandler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) };
            if (string.Equals(Option("acceptUntrustedCertificate"), "true", StringComparison.OrdinalIgnoreCase))
            {
                socketHandler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }

            handler = socketHandler;
        }

        var client = new HttpClient(handler, disposeHandler: _handler is null) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RemoteDeck");
        if (_apiKey is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        return client;
    }

    private ChatState Snapshot()
    {
        lock (_historyGate)
        {
            return new ChatState(_model, _system, _history.ToList());
        }
    }

    /// <summary>Saves to <see cref="HistoryFile"/>; a file that cannot be written never interrupts the chat.</summary>
    private void SaveHistory()
    {
        var file = HistoryFile;
        if (file is null)
        {
            return;
        }

        try
        {
            ChatHistory.Save(file, Snapshot());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Print($"{Dim}[could not save the conversation: {ex.Message}]{Reset}\r\n");
        }
    }

    private void Remember(ChatMessage message)
    {
        lock (_historyGate)
        {
            _history.Add(message);
        }
    }

    private void Forget(int count)
    {
        lock (_historyGate)
        {
            var n = Math.Min(count, _history.Count);
            _history.RemoveRange(_history.Count - n, n);
        }
    }

    private void ClearHistory()
    {
        lock (_historyGate)
        {
            _history.Clear();
        }
    }

    /// <summary>The part of the conversation that is sent along with a new message: the most recent messages, starting at a question.</summary>
    internal List<ChatMessage> Context()
    {
        lock (_historyGate)
        {
            var tail = _history.Count > _contextMessages ? _history.Skip(_history.Count - _contextMessages).ToList() : _history.ToList();
            while (tail.Count > 0 && tail[0].Role != "user")
            {
                tail.RemoveAt(0);
            }

            return tail;
        }
    }

    private void Restore()
    {
        var saved = ChatHistory.Load(HistoryFile);
        if (saved is null)
        {
            return;
        }

        lock (_historyGate)
        {
            _history.Clear();
            _history.AddRange(saved.Messages);
        }

        // What was chosen in the earlier session wins over the connection's defaults.
        if (saved.Model.Length > 0)
        {
            _model = saved.Model;
        }

        if (saved.System.Length > 0)
        {
            _system = saved.System;
        }

        if (saved.Messages.Count == 0)
        {
            return;
        }

        Print($"{Dim}Restored {saved.Messages.Count} earlier message(s); they are sent along as context.{Reset}\r\n");
        var shown = saved.Messages.Count > 6 ? saved.Messages.Skip(saved.Messages.Count - 6).ToList() : saved.Messages.ToList();
        if (shown.Count < saved.Messages.Count)
        {
            Print($"{Dim}...{Reset}\r\n");
        }

        foreach (var message in shown)
        {
            Print(message.Role == "user"
                ? $"{Prompt}{Dim}{ToTerminal(message.Content)}{Reset}\r\n"
                : $"{Dim}{ToTerminal(message.Content)}{Reset}\r\n");
        }
    }

    private async Task StartAsync()
    {
        Print($"\u001b[1mAI chat\u001b[0m {Dim}— {_base.GetLeftPart(UriPartial.Authority)}{Reset}\r\n");
        Restore();
        if (_model.Length == 0)
        {
            try
            {
                var models = await FetchModelsAsync(CancellationToken.None).ConfigureAwait(false);
                if (models.Count > 0)
                {
                    _model = models[0];
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AiConnectionException)
            {
                Print($"{Red}{Message(ex)}{Reset}\r\n");
            }
        }

        Print(_model.Length > 0
            ? $"{Dim}Model: {_model}. Type a message, or /help for commands.{Reset}\r\n"
            : $"{Dim}No model chosen. Type /models to list them, then /model <name>.{Reset}\r\n");
        Print(Prompt);
    }

    private void HandleLine(string line)
    {
        lock (_inputGate)
        {
            if (line == "\u0003")
            {
                if (_busy)
                {
                    _reply?.Cancel();
                }
                else
                {
                    Print(Prompt);
                }

                return;
            }

            if (_busy)
            {
                // Lines typed while a reply is streaming are ignored; Ctrl+C stops the reply.
                return;
            }

            var text = line.Trim();
            if (text.Length == 0)
            {
                Print(Prompt);
                return;
            }

            _busy = true;
            _worker = Task.Run(() => RunAsync(text));
        }
    }

    private async Task RunAsync(string text)
    {
        try
        {
            if (text.StartsWith('/'))
            {
                await RunCommandAsync(text).ConfigureAwait(false);
            }
            else
            {
                await AskAsync(text).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Print($"\r\n{Red}{Message(ex)}{Reset}\r\n");
        }
        finally
        {
            lock (_inputGate)
            {
                _busy = false;
            }

            if (State == ConnectionState.Connected && text != "/exit")
            {
                Print(Prompt);
            }
        }
    }

    private async Task RunCommandAsync(string text)
    {
        var space = text.IndexOf(' ');
        var command = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var arg = space < 0 ? string.Empty : text[(space + 1)..].Trim();
        switch (command)
        {
            case "/help":
                Print(
                    "/models          list the models on the server\r\n" +
                    "/model <name>    switch model (a number from /models works too)\r\n" +
                    "/system <text>   set the system prompt and start over (no text shows it)\r\n" +
                    "/clear           forget the conversation\r\n" +
                    "/exit            close this chat\r\n" +
                    $"{Dim}Ctrl+C stops a reply that is being written.{Reset}\r\n");
                break;
            case "/clear":
                ClearHistory();
                SaveHistory();
                Print($"{Dim}Conversation cleared.{Reset}\r\n");
                break;
            case "/system":
                if (arg.Length == 0)
                {
                    Print(_system.Length > 0 ? _system + "\r\n" : $"{Dim}No system prompt.{Reset}\r\n");
                }
                else
                {
                    _system = arg;
                    ClearHistory();
                    SaveHistory();
                    Print($"{Dim}System prompt set. Conversation cleared.{Reset}\r\n");
                }

                break;
            case "/models":
                _lastModels = await FetchModelsAsync(CancellationToken.None).ConfigureAwait(false);
                if (_lastModels.Count == 0)
                {
                    Print($"{Dim}The server listed no models.{Reset}\r\n");
                }

                for (var i = 0; i < _lastModels.Count; i++)
                {
                    var mark = _lastModels[i] == _model ? "*" : " ";
                    Print($"{mark} {i + 1,2}  {_lastModels[i]}\r\n");
                }

                break;
            case "/model":
                if (arg.Length == 0)
                {
                    Print($"{Dim}Model: {(_model.Length > 0 ? _model : "(none)")}{Reset}\r\n");
                }
                else
                {
                    _model = int.TryParse(arg, out var number) && number >= 1 && number <= _lastModels.Count ? _lastModels[number - 1] : arg;
                    SaveHistory();
                    Print($"{Dim}Model: {_model}{Reset}\r\n");
                }

                break;
            case "/exit":
                Print($"{Dim}Goodbye.{Reset}\r\n");
                Close();
                SetState(ConnectionState.Disconnected);
                break;
            default:
                Print($"{Red}Unknown command {command}. Type /help.{Reset}\r\n");
                break;
        }
    }

    private List<string> _lastModels = new();

    private async Task<List<string>> FetchModelsAsync(CancellationToken cancellationToken)
    {
        var http = _http ?? throw new AiConnectionException("The AI chat is not connected.");
        using var response = await http.GetAsync(ChatProtocol.Combine(_base, ChatProtocol.ModelsPath(_flavor)), cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new AiConnectionException(ChatProtocol.ErrorText((int)response.StatusCode, body));
        }

        return ChatProtocol.ParseModels(body);
    }

    private async Task AskAsync(string text)
    {
        if (_model.Length == 0)
        {
            Print($"{Red}No model chosen. Type /models, then /model <name>.{Reset}\r\n");
            return;
        }

        var http = _http ?? throw new AiConnectionException("The AI chat is not connected.");
        Remember(new ChatMessage("user", text));
        var messages = new List<ChatMessage>();
        if (_system.Length > 0)
        {
            messages.Add(new ChatMessage("system", _system));
        }

        messages.AddRange(Context());

        var cancel = new CancellationTokenSource();
        lock (_inputGate)
        {
            _reply = cancel;
        }

        var reply = new StringBuilder();
        var stopped = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ChatProtocol.Combine(_base, ChatProtocol.ChatPath(_flavor, Option("path"))))
            {
                Content = new StringContent(ChatProtocol.BuildRequest(_model, messages, stream: true), Encoding.UTF8, "application/json")
            };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancel.Token).ConfigureAwait(false);
                Forget(1);
                throw new AiConnectionException(ChatProtocol.ErrorText((int)response.StatusCode, body));
            }

            Print($"\u001b[1;32m{(_model.Length > 24 ? _model[..24] : _model)}\u001b[0m › ");
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            await using var stream = await response.Content.ReadAsStreamAsync(cancel.Token).ConfigureAwait(false);
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) && !contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                using var all = new StreamReader(stream, Encoding.UTF8);
                var whole = ChatProtocol.ParseReply(await all.ReadToEndAsync(cancel.Token).ConfigureAwait(false));
                reply.Append(whole);
                Print(ToTerminal(whole));
            }
            else
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = await reader.ReadLineAsync(cancel.Token).ConfigureAwait(false)) is not null)
                {
                    var delta = ChatProtocol.ParseStreamLine(line, out var done);
                    if (delta is { Length: > 0 })
                    {
                        reply.Append(delta);
                        Print(ToTerminal(delta));
                    }

                    if (done)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            stopped = true;
        }
        finally
        {
            lock (_inputGate)
            {
                _reply = null;
            }

            cancel.Dispose();
        }

        if (stopped)
        {
            Print($" {Dim}[stopped]{Reset}");
        }

        Print("\r\n");
        if (reply.Length > 0)
        {
            Remember(new ChatMessage("assistant", reply.ToString()));
        }
        else
        {
            Forget(1);
        }

        SaveHistory();
    }

    internal static string ToTerminal(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    private static string Message(Exception ex) => ex switch
    {
        AiConnectionException => ex.Message,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => "The server refused the API key.",
        HttpRequestException h => $"Could not reach the server: {h.Message}",
        TaskCanceledException => "The server did not answer in time.",
        _ => $"Unexpected error: {ex.Message}"
    };

    private void Print(string text) => _output.Publish(Encoding.UTF8.GetBytes(text));

    private void Close()
    {
        CancellationTokenSource? reply;
        lock (_inputGate)
        {
            reply = _reply;
        }

        try
        {
            reply?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _http?.Dispose();
        _http = null;
    }

    private string Option(string key) =>
        _definition.Options is not null && _definition.Options.TryGetValue(key, out var value) ? value : string.Empty;

    private void SetState(ConnectionState state)
    {
        lock (_stateGate)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        StateChanged?.Invoke(this, state);
    }
}
