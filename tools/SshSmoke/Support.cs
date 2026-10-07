using System.Text;
using RemoteDeck.Plugin;
using RemoteDeck.Protocols.Ssh;

namespace SshSmoke;

/// <summary>Writes the terminal output bytes straight to the console, untouched.</summary>
internal sealed class StreamObserver : IObserver<ReadOnlyMemory<byte>>
{
    private readonly Stream _stream;

    public StreamObserver(Stream stream)
    {
        _stream = stream;
    }

    public void OnNext(ReadOnlyMemory<byte> value)
    {
        _stream.Write(value.Span);
        _stream.Flush();
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }
}

/// <summary>Hands out one fixed credential. The real app uses the vault; this tool does not touch it.</summary>
internal sealed class PromptedBroker : ICredentialBroker
{
    private readonly string _username;
    private readonly string _password;

    public PromptedBroker(string username, string password)
    {
        _username = username;
        _password = password;
    }

    public ValueTask<T> UseAsync<T>(
        string connectionId,
        Func<ICredential, ValueTask<T>> use,
        CancellationToken cancellationToken = default) =>
        use(new Credential(_username, _password));

    private sealed class Credential : ICredential
    {
        private readonly string _password;

        public Credential(string username, string password)
        {
            Username = username;
            _password = password;
        }

        public string? Username { get; }

        public void ReadPassword(SecretReader reader) => reader(_password.AsSpan());
    }
}

/// <summary>Asks on the console, the way ssh does. Anything but a full "yes" is a no.</summary>
internal sealed class ConsoleHostKeyPrompt : IHostKeyPrompt
{
    public ValueTask<bool> ConfirmAsync(
        HostKeyVerdict verdict,
        HostKeyInfo presented,
        IReadOnlyList<HostKeyInfo> knownKeys,
        CancellationToken cancellationToken)
    {
        var error = Console.Error;
        error.WriteLine();

        switch (verdict)
        {
            case HostKeyVerdict.Changed:
                error.WriteLine("@@@ WARNING: THE HOST KEY HAS CHANGED @@@");
                error.WriteLine("Someone may be intercepting this connection, or the server was reinstalled.");
                error.WriteLine($"Remembered: {knownKeys.First(k => k.Algorithm == presented.Algorithm).Fingerprint}");
                error.WriteLine($"Presented:  {presented.Fingerprint}");
                break;
            case HostKeyVerdict.NewKeyType:
                error.WriteLine($"{presented.Host}:{presented.Port} is known, but not with a {presented.Algorithm} key.");
                error.WriteLine($"Fingerprint: {presented.Fingerprint}");
                break;
            default:
                error.WriteLine($"The authenticity of {presented.Host}:{presented.Port} can't be established.");
                error.WriteLine($"{presented.Algorithm} fingerprint: {presented.Fingerprint}");
                break;
        }

        error.Write("Trust this key and continue? (yes/no): ");
        var answer = Console.ReadLine();
        return ValueTask.FromResult(string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Turns a console key press into the bytes a terminal would send.</summary>
internal static class KeyMap
{
    public static byte[] ToBytes(ConsoleKeyInfo key, ref char? highSurrogate)
    {
        var special = key.Key switch
        {
            ConsoleKey.Enter => "\r",
            ConsoleKey.Backspace => "\u007f",
            ConsoleKey.Tab when (key.Modifiers & ConsoleModifiers.Shift) != 0 => "\u001b[Z",
            ConsoleKey.Tab => "\t",
            ConsoleKey.Escape => "\u001b",
            ConsoleKey.UpArrow => "\u001b[A",
            ConsoleKey.DownArrow => "\u001b[B",
            ConsoleKey.RightArrow => "\u001b[C",
            ConsoleKey.LeftArrow => "\u001b[D",
            ConsoleKey.Home => "\u001b[H",
            ConsoleKey.End => "\u001b[F",
            ConsoleKey.Delete => "\u001b[3~",
            ConsoleKey.Insert => "\u001b[2~",
            ConsoleKey.PageUp => "\u001b[5~",
            ConsoleKey.PageDown => "\u001b[6~",
            _ => null,
        };

        if (special is not null)
        {
            return Encoding.ASCII.GetBytes(special);
        }

        var c = key.KeyChar;
        if (c == '\0')
        {
            return Array.Empty<byte>();
        }

        if (char.IsHighSurrogate(c))
        {
            highSurrogate = c;
            return Array.Empty<byte>();
        }

        if (char.IsLowSurrogate(c) && highSurrogate is { } high)
        {
            highSurrogate = null;
            return Encoding.UTF8.GetBytes(new[] { high, c });
        }

        highSurrogate = null;
        return Encoding.UTF8.GetBytes(c.ToString());
    }
}
