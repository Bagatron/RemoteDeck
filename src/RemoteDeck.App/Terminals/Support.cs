using System.Windows.Threading;
using RemoteDeck.Plugin;
using RemoteDeck.Protocols.Ssh;

namespace RemoteDeck.App.Terminals;

/// <summary>Hands out one fixed credential for one connection attempt.</summary>
internal sealed class SingleCredentialBroker : ICredentialBroker
{
    private readonly string _username;
    private readonly string _password;

    public SingleCredentialBroker(string username, string password)
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

internal sealed class DelegateObserver : IObserver<ReadOnlyMemory<byte>>
{
    private readonly Action<byte[]> _onData;

    public DelegateObserver(Action<byte[]> onData)
    {
        _onData = onData;
    }

    public void OnNext(ReadOnlyMemory<byte> value) => _onData(value.ToArray());

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }
}

/// <summary>Parses "user@host" or "user@host:port".</summary>
internal static class Target
{
    public static bool TryParse(string text, out string user, out string host, out int port)
    {
        user = host = string.Empty;
        port = 22;

        text = text.Trim();
        var at = text.LastIndexOf('@');
        if (at < 1 || at == text.Length - 1)
        {
            return false;
        }

        user = text[..at];
        host = text[(at + 1)..];

        var colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(':') == colon)
        {
            if (!int.TryParse(host[(colon + 1)..], out port) || port is < 1 or > 65535)
            {
                return false;
            }

            host = host[..colon];
        }

        return host.Length > 0;
    }
}

/// <summary>
/// Asks the user in a message box. Called from the connecting thread, so it hops to the UI thread; the
/// connection waits for the answer. Defaults to "No".
/// </summary>
internal sealed class DialogHostKeyPrompt : IHostKeyPrompt
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<System.Windows.Window?> _owner;

    public DialogHostKeyPrompt(Dispatcher dispatcher, Func<System.Windows.Window?> owner)
    {
        _dispatcher = dispatcher;
        _owner = owner;
    }

    public async ValueTask<bool> ConfirmAsync(
        HostKeyVerdict verdict,
        HostKeyInfo presented,
        IReadOnlyList<HostKeyInfo> knownKeys,
        CancellationToken cancellationToken)
    {
        string text;
        string title;
        System.Windows.MessageBoxImage icon;

        switch (verdict)
        {
            case HostKeyVerdict.Changed:
                title = "WARNING: host key changed";
                icon = System.Windows.MessageBoxImage.Warning;
                var old = knownKeys.FirstOrDefault(k => k.Algorithm == presented.Algorithm)?.Fingerprint;
                text = $"The {presented.Algorithm} key of {presented.Host}:{presented.Port} is different from the one you trusted before.\n\n"
                    + "Someone may be intercepting this connection, or the server was reinstalled.\n\n"
                    + $"Trusted before:\n{old}\n\nPresented now:\n{presented.Fingerprint}\n\nConnect anyway and replace the old key?";
                break;
            case HostKeyVerdict.NewKeyType:
                title = "New host key type";
                icon = System.Windows.MessageBoxImage.Question;
                text = $"{presented.Host}:{presented.Port} is known, but not with a {presented.Algorithm} key.\n\n"
                    + $"Fingerprint:\n{presented.Fingerprint}\n\nTrust this key and continue?";
                break;
            default:
                title = "Unknown host";
                icon = System.Windows.MessageBoxImage.Question;
                text = $"The authenticity of {presented.Host}:{presented.Port} can't be established.\n\n"
                    + $"{presented.Algorithm} fingerprint:\n{presented.Fingerprint}\n\nTrust this key and continue?";
                break;
        }

        var answer = await _dispatcher.InvokeAsync(() =>
            System.Windows.MessageBox.Show(
                _owner(),
                text,
                title,
                System.Windows.MessageBoxButton.YesNo,
                icon,
                System.Windows.MessageBoxResult.No));

        return answer == System.Windows.MessageBoxResult.Yes;
    }
}
