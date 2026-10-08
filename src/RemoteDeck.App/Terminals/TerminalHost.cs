using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RemoteDeck.Core.Themes;

namespace RemoteDeck.App.Terminals;

/// <summary>
/// One WebView2 that draws every tab's panes (xterm.js). The window owns tabs, layouts and sessions; this class
/// sends the page what to show and passes keystrokes and layout gestures back. Call everything on the UI thread.
/// Panes are identified by their terminal id.
/// </summary>
public sealed class TerminalHost
{
    private const string VirtualHost = "remotedeck.app";

    private readonly WebView2 _web;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TerminalHost(WebView2 web)
    {
        _web = web;
    }

    /// <summary>Keystrokes typed into a pane, as the bytes a terminal sends.</summary>
    public event Action<string, byte[]>? Input;

    /// <summary>Text pasted into a pane (already in terminal form: carriage returns, bracketed-paste markers).</summary>
    public event Action<string, string>? Pasted;

    /// <summary>A pane's size in characters changed.</summary>
    public event Action<string, int, int>? Resized;

    /// <summary>The user clicked into a pane.</summary>
    public event Action<string>? Focused;

    /// <summary>A splitter was dragged: tab id, path to the split, new ratio.</summary>
    public event Action<string, IReadOnlyList<int>, double>? RatioChanged;

    /// <summary>A pane header button was pressed: terminal id and the action ("member", "splitRight", "splitDown", "close").</summary>
    public event Action<string, string>? PaneAction;

    /// <summary>An app shortcut was pressed while the terminal page had focus, e.g. "ctrl+shift+p".</summary>
    public event Action<string>? Shortcut;

    public async Task InitializeAsync()
    {
        var dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteDeck",
            "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
        await _web.EnsureCoreWebView2Async(environment);

        var core = _web.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(
            VirtualHost,
            Path.Combine(AppContext.BaseDirectory, "Web"),
            CoreWebView2HostResourceAccessKind.Allow);

        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;

        // The page is ours and local; nothing the remote side prints may navigate it or open windows.
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith($"https://{VirtualHost}/", StringComparison.Ordinal))
            {
                e.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) =>
        {
            // Only clipboard access, only for our own page (copy and paste in the terminal).
            e.State = e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
        };

        core.WebMessageReceived += OnMessage;
        core.Navigate($"https://{VirtualHost}/terminal.html");

        await _ready.Task;
    }

    /// <summary>Shows a tab's current layout and pane details. Safe to call as often as needed.</summary>
    public void Sync(WorkspaceTab tab) => Send(tab.ToSync());

    /// <summary>Gives the page the interface colors, the terminal palette and the terminal font.</summary>
    public void SetTheme(Theme theme)
    {
        var c = theme.Colors;
        var t = theme.Terminal;
        var names = new[]
        {
            "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
            "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite",
        };

        var term = new JsonObject
        {
            ["background"] = t.Background,
            ["foreground"] = t.Foreground,
            ["cursor"] = t.Cursor,
            ["cursorAccent"] = t.Background,
            ["selectionBackground"] = t.Selection,
        };
        for (var i = 0; i < names.Length; i++)
        {
            term[names[i]] = t.Ansi[i];
        }

        // The configured font first, then sensible fallbacks. Quotes are stripped so a name cannot break out of the CSS.
        var mono = theme.Font.Mono.Replace("\"", string.Empty).Replace("'", string.Empty);
        Send(new JsonObject
        {
            ["t"] = "theme",
            ["ui"] = new JsonObject
            {
                ["bg"] = c.Background,
                ["panel"] = c.Surface,
                ["alt"] = c.SurfaceAlt,
                ["line"] = c.Border,
                ["text"] = c.Foreground,
                ["dim"] = c.MutedForeground,
                ["accent"] = c.Accent,
                ["danger"] = c.Danger,
                ["warning"] = c.Warning,
                ["success"] = c.Success,
                ["broadcast"] = c.Broadcast,
            },
            ["shape"] = new JsonObject
            {
                ["radius"] = theme.Shape.CornerRadius,
                ["borderWidth"] = theme.Shape.BorderWidth,
                ["density"] = theme.Shape.Density,
            },
            ["term"] = term,
            ["font"] = new JsonObject
            {
                ["family"] = $"\"{mono}\", \"Cascadia Mono\", Consolas, \"Courier New\", monospace",
                ["size"] = theme.Font.Size + 1,
            },
        });
    }

    public void ShowTab(string tabId) => Send(new JsonObject { ["t"] = "show", ["tab"] = tabId });

    public void CloseTab(string tabId) => Send(new JsonObject { ["t"] = "closeTab", ["tab"] = tabId });

    public void FocusPane(string terminalId) => Send(new JsonObject { ["t"] = "focusPane", ["id"] = terminalId });

    public void Output(string terminalId, byte[] data) =>
        Send(new JsonObject { ["t"] = "out", ["id"] = terminalId, ["b64"] = Convert.ToBase64String(data) });

    private void Send(JsonNode message)
    {
        _web.CoreWebView2?.PostWebMessageAsJson(message.ToJsonString());
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var document = JsonDocument.Parse(e.WebMessageAsJson);
        var root = document.RootElement;

        switch (root.GetProperty("t").GetString())
        {
            case "ready":
                _ready.TrySetResult();
                break;

            case "in":
                Input?.Invoke(Id(root), Convert.FromBase64String(root.GetProperty("b64").GetString()!));
                break;

            case "paste":
                Pasted?.Invoke(Id(root), root.GetProperty("text").GetString() ?? string.Empty);
                break;

            case "size":
                var columns = root.GetProperty("cols").GetInt32();
                var rows = root.GetProperty("rows").GetInt32();
                if (columns > 0 && rows > 0)
                {
                    Resized?.Invoke(Id(root), columns, rows);
                }

                break;

            case "focus":
                Focused?.Invoke(Id(root));
                break;

            case "ratio":
                var path = root.GetProperty("path").EnumerateArray().Select(p => p.GetInt32()).ToArray();
                RatioChanged?.Invoke(root.GetProperty("tab").GetString()!, path, root.GetProperty("ratio").GetDouble());
                break;

            case "key":
                Shortcut?.Invoke(root.GetProperty("k").GetString() ?? string.Empty);
                break;

            case "action":
                PaneAction?.Invoke(Id(root), root.GetProperty("a").GetString()!);
                break;
        }
    }

    private static string Id(JsonElement root) => root.GetProperty("id").GetString()!;
}
