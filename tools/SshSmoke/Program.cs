using System.Text;
using RemoteDeck.Plugin;
using RemoteDeck.Protocols.Ssh;
using SshSmoke;

// A throwaway command-line client for trying the SSH library against a real server.
// Not part of the product: the real UI comes later.

const string Usage = """
    Usage: SshSmoke user@host[:port] [-i keyfile] [--known-hosts path]

      -i keyfile           Log in with a private key. The passphrase (if any) is asked for.
      --known-hosts path   Where trusted host keys are remembered
                           (default: %LOCALAPPDATA%\RemoteDeck\known_hosts.json)

    The password (or key passphrase) is read from the REMOTEDECK_SMOKE_PASSWORD
    environment variable if set, otherwise asked for with hidden input.
    Press Ctrl+] to quit. Use Windows Terminal or PowerShell for keystroke-by-keystroke
    mode; when input is redirected (or in Git Bash) it works one line at a time.
    """;

string? target = null;
string? keyFile = null;
string? knownHostsPath = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-i" when i + 1 < args.Length:
            keyFile = args[++i];
            break;
        case "--known-hosts" when i + 1 < args.Length:
            knownHostsPath = args[++i];
            break;
        case "-h" or "--help":
            Console.WriteLine(Usage);
            return 0;
        default:
            if (target is not null || args[i].StartsWith('-'))
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            target = args[i];
            break;
    }
}

if (target is null || !TryParseTarget(target, out var user, out var host, out var port))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

knownHostsPath ??= Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "RemoteDeck",
    "known_hosts.json");

var interactive = !Console.IsInputRedirected;

var password = Environment.GetEnvironmentVariable("REMOTEDECK_SMOKE_PASSWORD");
if (string.IsNullOrEmpty(password))
{
    var label = keyFile is null ? $"Password for {user}@{host}: " : "Key passphrase (leave empty if none): ";
    password = ReadHidden(label, interactive);
}

var options = new Dictionary<string, string> { ["username"] = user };
if (keyFile is not null)
{
    options["privateKeyPath"] = keyFile;
}

var hasPassword = !string.IsNullOrEmpty(password);
var definition = new ConnectionDefinition(
    "smoke",
    $"{user}@{host}",
    "ssh",
    host,
    port,
    hasPassword ? "smoke" : null,
    options);

var factory = new SshConnectionFactory(new FileHostKeyStore(knownHostsPath), new ConsoleHostKeyPrompt());
var connection = (ITerminalConnection)factory.Create(definition, new PromptedBroker(user, password ?? string.Empty));

var closed = new TaskCompletionSource();
connection.StateChanged += (_, state) =>
{
    if (state is ConnectionState.Disconnected or ConnectionState.Failed)
    {
        closed.TrySetResult();
    }
};

using var stdout = Console.OpenStandardOutput();
using var subscription = connection.Output.Subscribe(new StreamObserver(stdout));

if (interactive)
{
    connection.Resize(Math.Max(Console.WindowWidth, 1), Math.Max(Console.WindowHeight, 1));
}

try
{
    Console.Error.WriteLine($"Connecting to {host}:{port} as {user}...");

    // Off the main thread: the host-key question blocks the connecting thread while it waits for an answer.
    await Task.Run(async () => await connection.ConnectAsync());
}
catch (SshConnectionException ex)
{
    Console.Error.WriteLine("Failed: " + ex.Message);
    return 1;
}

Console.Error.WriteLine("Connected. Press Ctrl+] to quit.");

using var quit = new CancellationTokenSource();
var input = interactive ? RunRawInput(connection, quit) : RunLineInput(connection, closed.Task);
var watcher = interactive ? WatchWindowSize(connection, quit.Token) : Task.CompletedTask;

await Task.WhenAny(closed.Task, input);
quit.Cancel();
await connection.DisposeAsync();
Console.Error.WriteLine();
Console.Error.WriteLine("Connection closed.");
return 0;

static bool TryParseTarget(string text, out string user, out string host, out int port)
{
    user = host = string.Empty;
    port = 22;

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

static string ReadHidden(string label, bool interactive)
{
    Console.Error.Write(label);
    if (!interactive)
    {
        return Console.ReadLine() ?? string.Empty;
    }

    var text = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.Error.WriteLine();
            return text.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (text.Length > 0)
            {
                text.Length--;
            }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            text.Append(key.KeyChar);
        }
    }
}

static Task RunRawInput(ITerminalConnection connection, CancellationTokenSource quit)
{
    return Task.Run(async () =>
    {
        char? highSurrogate = null;
        while (!quit.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                await Task.Delay(10);
                continue;
            }

            var key = Console.ReadKey(intercept: true);
            if (key.KeyChar == '\u001d')
            {
                return; // Ctrl+]
            }

            var bytes = KeyMap.ToBytes(key, ref highSurrogate);
            if (bytes.Length > 0)
            {
                try
                {
                    await connection.WriteAsync(bytes);
                }
                catch (InvalidOperationException)
                {
                    return;
                }
            }
        }
    });
}

static Task RunLineInput(ITerminalConnection connection, Task closed)
{
    return Task.Run(async () =>
    {
        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            try
            {
                await connection.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }

        // Input ended (piped commands): give the output a moment to arrive, then finish.
        await Task.WhenAny(closed, Task.Delay(TimeSpan.FromSeconds(2)));
    });
}

static async Task WatchWindowSize(ITerminalConnection connection, CancellationToken token)
{
    int width = Console.WindowWidth, height = Console.WindowHeight;
    try
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(250, token);
            if (Console.WindowWidth != width || Console.WindowHeight != height)
            {
                width = Console.WindowWidth;
                height = Console.WindowHeight;
                connection.Resize(Math.Max(width, 1), Math.Max(height, 1));
            }
        }
    }
    catch (OperationCanceledException)
    {
    }
}
