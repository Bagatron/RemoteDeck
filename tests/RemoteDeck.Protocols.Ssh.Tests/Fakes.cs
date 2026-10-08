using System.Text;
using RemoteDeck.Plugin;

namespace RemoteDeck.Protocols.Ssh.Tests;

internal sealed class FakeShell : ISshShell
{
    public List<byte[]> Written { get; } = new();

    public (int Columns, int Rows)? LastResize { get; private set; }

    public int StartCount { get; private set; }

    public bool Disposed { get; private set; }

    public bool Faulted { get; set; }

    public event EventHandler<byte[]>? DataReceived;

    public event EventHandler? Closed;

    public void Start() => StartCount++;

    public void Write(ReadOnlySpan<byte> data) => Written.Add(data.ToArray());

    public void Resize(int columns, int rows) => LastResize = (columns, rows);

    public void Dispose() => Disposed = true;

    public void Receive(string text) => DataReceived?.Invoke(this, Encoding.UTF8.GetBytes(text));

    public void RemoteClose() => Closed?.Invoke(this, EventArgs.Empty);
}

internal sealed class FakeSession : ISshSession
{
    public FakeShell Shell { get; } = new();

    public string? Terminal { get; private set; }

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public bool Disposed { get; private set; }

    public ISshShell OpenShell(string terminal, int columns, int rows)
    {
        Terminal = terminal;
        Columns = columns;
        Rows = rows;
        return Shell;
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeSessionFactory : ISshSessionFactory
{
    public FakeSession Session { get; private set; } = new();

    public SshConnectRequest? Request { get; private set; }

    /// <summary>A copy of the secret taken the moment the connection was requested, before the caller wipes it.</summary>
    public byte[]? SecretAtConnectTime { get; private set; }

    public int ConnectCount { get; private set; }

    /// <summary>Runs while "connecting", e.g. to simulate the host-key callback.</summary>
    public Action<SshConnectRequest>? OnConnect { get; set; }

    public Exception? Failure { get; set; }

    public void NextSession() => Session = new FakeSession();

    public Task<ISshSession> ConnectAsync(SshConnectRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectCount++;
        Request = request;
        SecretAtConnectTime = request.Secret?.ToArray();
        OnConnect?.Invoke(request);

        if (Failure is not null)
        {
            throw Failure;
        }

        return Task.FromResult<ISshSession>(Session);
    }
}

internal sealed class FakeCredential : ICredential
{
    private readonly string _password;

    public FakeCredential(string? username, string password)
    {
        Username = username;
        _password = password;
    }

    public string? Username { get; }

    public void ReadPassword(SecretReader reader) => reader(_password.AsSpan());
}

internal sealed class FakeBroker : ICredentialBroker
{
    public Dictionary<string, (string? Username, string Password)> ByConnection { get; } = new();

    public ValueTask<T> UseAsync<T>(
        string connectionId,
        Func<ICredential, ValueTask<T>> use,
        CancellationToken cancellationToken = default)
    {
        if (!ByConnection.TryGetValue(connectionId, out var entry))
        {
            throw new KeyNotFoundException($"No credential for '{connectionId}'.");
        }

        return use(new FakeCredential(entry.Username, entry.Password));
    }
}

internal sealed class MemoryHostKeyStore : IHostKeyStore
{
    public List<HostKeyInfo> Keys { get; } = new();

    public IReadOnlyList<HostKeyInfo> FindAll(string host, int port) =>
        Keys.Where(k => string.Equals(k.Host, host, StringComparison.OrdinalIgnoreCase) && k.Port == port).ToArray();

    public void Save(HostKeyInfo key)
    {
        Keys.RemoveAll(k =>
            string.Equals(k.Host, key.Host, StringComparison.OrdinalIgnoreCase)
            && k.Port == key.Port
            && string.Equals(k.Algorithm, key.Algorithm, StringComparison.OrdinalIgnoreCase));
        Keys.Add(key);
    }
}

internal sealed class ScriptedPrompt : IHostKeyPrompt
{
    private readonly bool _answer;

    public ScriptedPrompt(bool answer)
    {
        _answer = answer;
    }

    public List<(HostKeyVerdict Verdict, HostKeyInfo Presented, int KnownCount)> Asked { get; } = new();

    public ValueTask<bool> ConfirmAsync(
        HostKeyVerdict verdict,
        HostKeyInfo presented,
        IReadOnlyList<HostKeyInfo> knownKeys,
        CancellationToken cancellationToken)
    {
        Asked.Add((verdict, presented, knownKeys.Count));
        return ValueTask.FromResult(_answer);
    }
}

internal sealed class Collector : IObserver<ReadOnlyMemory<byte>>
{
    public List<string> Chunks { get; } = new();

    public bool Completed { get; private set; }

    public void OnNext(ReadOnlyMemory<byte> value) => Chunks.Add(Encoding.UTF8.GetString(value.Span));

    public void OnCompleted() => Completed = true;

    public void OnError(Exception error)
    {
    }
}
