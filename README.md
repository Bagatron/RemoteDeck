# RemoteDeck

An open-source, lightweight remote desktop and terminal manager for Windows. No connection limits, encrypted credentials, split-screen terminals, broadcast input, and everything customizable: themes, layouts and plugins are plain files and small libraries.

> **Status: early scaffold.** The extension contracts, the split-pane layout engine, the broadcast engine, the plugin manifest loader and the encrypted credential vault exist and are unit-tested. The UI shell and protocols are next (see the roadmap).

## What is here

| Path | What it is |
|---|---|
| `src/RemoteDeck.Plugin.Abstractions` | The public plugin API: `IConnection`, `ITerminalConnection`, `IConnectionFactory`, `ICredentialBroker`, `IPlugin`, `IPluginContext`. Plugins depend on this and nothing else. |
| `src/RemoteDeck.Core` | UI-independent logic: the split tree and presets (`Layout/`), the broadcast router and safety checks (`Broadcast/`), the saved-connection store with folders, search and credential inheritance (`Connections/`), and the `plugin.json` loader (`Plugins/`). |
| `src/RemoteDeck.Vault` | The encrypted credential vault (Argon2id + AES-256-GCM), its file storage, and the credential broker handed to plugins. |
| `src/RemoteDeck.App` | The Windows app (WPF). One WebView2 draws every terminal with xterm.js; tabs, sessions and SSH are handled in C#. Saved connections, vault unlock, SSH tabs. |
| `src/RemoteDeck.Protocols.Ssh` | SSH terminal connections (built on SSH.NET): key or password login, trust-on-first-use host keys, keep-alive, resize. Plugs in as an `ITerminalConnection`, so split panes and broadcast work with it. |
| `tools/SshSmoke` | A throwaway command-line SSH client for trying the library against a real server. |
| `tests/RemoteDeck.Core.Tests` | xUnit tests for the core library, including the shipped sample files. |
| `tests/RemoteDeck.Vault.Tests` | xUnit tests for the vault: round trips, wrong passwords, tamper detection, locking, storage, broker permissions. |
| `tests/RemoteDeck.Protocols.Ssh.Tests` | xUnit tests for SSH using fake sessions (no network): credentials, host keys, options, lifecycle. |
| `samples/HelloPlugin` | A tiny plugin: one palette command and a demo "echo" terminal type. Copy it to start your own. |
| `themes/`, `workspaces/` | Example theme and workspace files. |
| `schemas/` | JSON Schemas for themes, workspaces and `plugin.json` (add `"$schema"` to a file for editor autocomplete). |

## Install

Download a zip from the [Releases](../../releases) page and unzip it anywhere. There is no installer, and RemoteDeck keeps its data in `%LOCALAPPDATA%\RemoteDeck`.

- `RemoteDeck-<version>-win-x64.zip` includes everything it needs.
- `RemoteDeck-<version>-win-x64-needs-dotnet.zip` is smaller but needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

Run `RemoteDeck.exe`. The app is not code-signed yet, so Windows SmartScreen may warn you the first time; the `SHA256SUMS.txt` on each release lets you check your download. The Edge WebView2 runtime is also required (already on Windows 11). A winget manifest generator and a Scoop manifest are in `packaging/` (see `packaging/README.md`; winget publishing is pending review); a Chocolatey package is planned.

Releases are built by GitHub Actions when a version tag such as `v0.1.0` is pushed.

## Build and test

Requires the **.NET 10 SDK**.

```powershell
dotnet build RemoteDeck.slnx
dotnet test RemoteDeck.slnx
```

Using an older SDK? Change `TargetFramework` in `Directory.Build.props`. The code needs .NET 9 or newer because it uses out-of-order JSON type discriminators.

## Split panes

A layout is an immutable tree of panes and splits. Every edit returns a new tree, so undo/redo is just keeping the old one.

```csharp
var grid = LayoutPresets.Create(LayoutPreset.Grid2x2);          // p1..p4
grid = LayoutTree.AssignConnection(grid, "p1", "web-01");
grid = LayoutTree.SplitPane(grid, "p4", SplitOrientation.Rows, "p5");
var json = LayoutSerializer.Serialize(grid);                    // shareable file
```

Presets: single, two columns, two rows, 2x2 grid, one big + two small, three columns. A workspace file (`workspaces/*.json`) saves a layout together with the connection in each pane.

## Broadcast input

Two modes share one group of panes:

* **Live broadcast**: keystrokes typed in a group member are mirrored to every member.
* **Command bar**: send a whole line to every member, whether or not live broadcast is on. This is the safer default for ad-hoc commands.

Safety built in:

* Broadcast always starts **off**; `Reset()` turns it off and clears the group. Workspaces store group membership but never "on".
* Multi-line pastes and risky commands (`rm -r`, `shutdown`, `mkfs`, `DROP TABLE`, ...) need confirmation before reaching more than one host. With no way to ask, they are blocked.
* One failing host never stops the others; failures are reported per pane.

Live broadcast sends raw keystrokes, so panes in different states (one in `vim`, one at a password prompt) will diverge. That is inherent to the feature, and it is why the command bar exists. Typed commands cannot be inspected key by key, so risky-command checks apply to pasted text and the command bar.

## Saved connections

`ConnectionStore` (in `RemoteDeck.Core`) keeps the user's hosts and folders in one JSON file. It holds credential **references** only, never secrets, so the file is safe to sync or commit and stays searchable while the vault is locked.

* **Folders** nest, and a credential set on a folder is inherited by everything below it. A connection's own credential wins; otherwise the nearest folder above it supplies one.
* **Per-connection extras:** tags, favorite, color and icon (e.g. a red accent for production), notes, and type-specific options.
* **Search** powers quick-connect and the Ctrl+K palette: every word must match (name, host, tag, folder path or type), and abbreviations like `prd` find `prod-web`.
* **Safe by construction:** ids are unique, folders can never contain themselves, and a damaged or hand-edited file is rejected with a readable message instead of loading half-broken.

```csharp
var store = CatalogStorage.LoadOrCreate(catalogPath);
store.AddFolder(new FolderEntry("prod", "Prod", CredentialId: "cred-prod"));
store.AddConnection(new ConnectionEntry("web-01", "Web 01", "ssh", "10.0.0.5", FolderId: "prod"));

// Connect the store to the vault: plugins ask for a connection, the broker finds its credential.
var broker = new VaultCredentialBroker(vault, store.CredentialIdFor);
```

## Credential vault

Only credentials live in the vault; hosts, names and tags are stored elsewhere so they stay searchable while the vault is locked.

* A random 256-bit **data key** encrypts each credential with AES-256-GCM and a fresh nonce. Every credential is bound to its id (swapping entries in the file is detected) and padded to a multiple of 256 bytes (the file does not reveal password lengths).
* The data key is stored **wrapped** by a key derived from the master password with **Argon2id** (default 64 MiB, 3 passes, 4 lanes; the settings are stored in the file so they can be raised later). Changing the master password re-wraps one small blob and re-encrypts nothing. A second unlock method, such as Windows Hello, can wrap the same data key later.
* Credentials stay encrypted in memory. One is decrypted only inside a broker callback and wiped when the callback returns; locking wipes the data key.
* Files are written atomically (temp file, flush, swap) with the previous version kept as `.bak`.
* A wrong password and a damaged key block are indistinguishable by design.

```csharp
using var vault = CredentialVault.Create(masterPassword);
vault.Set("cred-web", "admin", "hunter2");
var path = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteDeck", "vault.json");
VaultStorage.Save(vault, path);
```

**Auto-lock:** after 15 minutes without use (change it with `Ctrl+Shift+P`, `auto-lock`: 5, 15, 60 minutes or never), when Windows locks, or with `Ctrl+Shift+L` (*Lock now*), RemoteDeck wipes the data key, hides its window and asks for the master password. Sessions that are already connected keep running, but saved passwords cannot be used until you unlock, so a dropped session will not reconnect while locked. Moving the mouse or typing inside a terminal counts as use.

Not covered yet: Windows Hello/DPAPI unlock and a clipboard-clearing helper. Treat the format as version 1 and **unreviewed**: have the crypto code audited before trusting it with real secrets, and note that .NET strings holding a typed password cannot be wiped, so the UI should collect it in a `char[]`.

## Themes

The `Theme` button at the bottom of the sidebar lists the built-in themes (RemoteDeck Dark and Light), the example files in the `themes` folder next to the program, and your own. Put your own `.json` files in `%LOCALAPPDATA%\RemoteDeck\themes` (the `Open my themes folder` item opens it); a file there replaces a shipped theme of the same name. Saving a theme file applies it straight away, no restart needed. A file with a mistake is skipped and the `Theme problems` item names the file and the field.

Applied today: interface colors, terminal colors (background, cursor, selection, the 16 ANSI colors), fonts (the terminal uses the theme's font size plus one) and the shape of the terminal area: `cornerRadius` rounds the panes and their buttons, `borderWidth` is the pane outline (0 hides it), and `density` (`compact`, `comfortable`, `spacious`) sets the pane header height and text padding. The standard Windows controls (buttons, lists, menus) keep their own Fluent shape, and `backdrop` (Mica, acrylic) is read and checked but not drawn yet. The choice is remembered in `settings.json`.

## Importing

`Import...` in the sidebar reads an OpenSSH `config`, a PuTTY registry export (`reg export HKCU\Software\SimonTatham\PuTTY\Sessions putty.reg`), a `.rdp` file, an RDCMan `.rdg` or an mRemoteNG `confCons.xml` (not fully encrypted). Folders are kept. Passwords are never imported; add them afterwards by editing the connection. Anything skipped (a protocol not supported yet, a `ProxyJump` it cannot link, `ProxyCommand`, wildcard hosts) is listed in a note when the import finishes.

## Running the app

```powershell
dotnet run --project src/RemoteDeck.App
```

The first start asks you to create a master password; after that it asks for it each time. Passwords you save are encrypted in `%LOCALAPPDATA%\RemoteDeck\vault.json` (Argon2id + AES-256-GCM), and your connections are stored without any secrets in `connections.json` next to it. There is no way to recover a forgotten master password.

- **Sidebar:** `+ Connection` and `+ Folder` add entries, double-click (or Enter) opens one, right-click edits, renames or deletes. The search box filters as you type.
- **Split panes:** the `Layout` button switches the current tab to a preset (two columns, 2 x 2 grid, and so on). Each pane header has buttons to split right or down. Drag the bars between panes to resize them. A saved connection opens in the focused empty pane, or in a new tab if none is empty; use right-click, `Open in new tab` to force a new tab. The x in a pane header ends its session, and a second click on an empty pane removes it.
- **Broadcast:** click the broadcast icon in each pane header you want in the group, then either switch on `Broadcast` (whatever you type in a grouped pane goes to all of them; the panes get a red border) or type a command in the bar and press Enter to run it on every grouped pane. Multi-line pastes and risky commands such as `rm -rf` or `shutdown` ask for confirmation first. Broadcast always starts off.
- **Quick connect:** type `user@host` (or `user@host:port`) above the tabs for a one-off connection that is not saved.
- **Remote Desktop:** a connection of type RDP (add one with `+ Connection`, or import an `.rdp`/RDCMan/mRemoteNG file) opens in a tab of the main window, using the Windows Remote Desktop control that ships with Windows. RemoteDeck asks for the password each time and hands it straight to the session; it is never saved. The remote desktop follows the tab's size, and a bar above it shows the status with a Disconnect / Reconnect button. Because the control is a native window, it always draws above anything that overlaps it inside the main window (menus and dialogs open in their own windows, so they are fine). Tick *Open in the Windows Remote Desktop app instead of a tab* on a connection to use `mstsc` as before; that is also the fallback if the control is missing. Keyboard shortcuts such as `Ctrl+Shift+L` do not reach the app while the remote desktop has focus; click a tab first.
- **Files (SFTP):** right-click an SSH connection, `Browse files (SFTP)` (or type `sftp` and a name in the palette). A file window opens for the same server, with the same saved password and host-key check. Double-click a folder to open it; `Upload files...`, `Upload folder...`, drag files in from Explorer, `Download`, `New folder`, `Rename`, `Delete` (folders are deleted with their contents, after a confirmation). Transfers can be cancelled.
- **Workspaces:** open a tab the way you like it (layout, connections in their panes, which panes are in the broadcast group), then `Workspaces`, `Save current tab as workspace...` (or the same command in the palette). Opening a saved workspace puts it in a new tab and connects every pane. Broadcast always starts off. Workspaces are plain JSON files in `%LOCALAPPDATA%\RemoteDeck\workspaces` (see `workspaces/` for examples and `schemas/` for the format); they refer to connections by id, so a connection you deleted is reported and left out.
- **Command palette:** `Ctrl+Shift+P` searches saved connections and actions (new tab, layouts, broadcast, themes, import). Type, arrow keys, Enter.
- **Auto-reconnect:** tick **Reconnect automatically if the connection drops** in an SSH connection's dialog. If the network or server drops an established session, RemoteDeck waits 2, 4, 8, 16 and then 30 seconds between up to 8 attempts, keeping the pane and its scrollback. It does not retry after you type `exit` or close the pane, or when the password is wrong or the host key is refused (retrying a wrong password could lock the account).
- **Session logs:** tick **Log session output to a file** in an SSH connection's dialog and each session is saved as plain text (colors and escape sequences removed; backspaces and progress bars collapse to what you saw) in `%LOCALAPPDATA%\RemoteDeck\logs`, named after the connection and the time. Only what the server prints is logged, not what you type, but anything shown on screen is, so treat the folder like the sessions themselves. `Ctrl+Shift+P`, `logs` opens the folder.
- **Scrollback size:** `Ctrl+Shift+P`, `scrollback` picks how many lines each terminal keeps (1,000 to 100,000; the default is 10,000). It applies at once and is remembered. Bigger values use more memory per pane.
- **Find in scrollback:** the magnifier in a pane's header, or `Ctrl+Shift+S` with the pane focused. Matches are highlighted as you type; `Enter` / `Shift+Enter` (or `F3`) step through them, `Aa` matches case, `Esc` closes. (Plain `Ctrl+F` is left to the shell, where it moves the cursor.)
- **Shortcuts:** `Ctrl+Shift+T` new tab, `Ctrl+Shift+W` close tab, `Ctrl+Tab` / `Ctrl+Shift+Tab` switch tabs, `Ctrl+Shift+B` broadcast on/off, `Ctrl+Shift+F` search connections. Plain Ctrl+letter keys always go to the shell.
- **Terminal:** Ctrl+C copies when text is selected, and Ctrl+V or Ctrl+Shift+V pastes.

Needs the Microsoft Edge WebView2 runtime (already present on Windows 11).

## SSH

Connections of type `ssh` use these options (all optional, set in the connection's `Options`):

| Option | Default | Meaning |
|---|---|---|
| `username` | none | Used when no saved credential supplies one |
| `privateKeyPath` | none | Log in with a key; the credential's password is then the key passphrase |
| `term` | `xterm-256color` | Terminal type requested from the server |
| `keepAliveSeconds` | `30` | `0` turns keep-alive off (max 3600) |
| `connectTimeoutSeconds` | `15` | 1 to 300 |
| `useAgent` | off | `true` also signs in with keys held by the Windows OpenSSH agent or Pageant |
| `autoReconnect` | off | `true` re-establishes a dropped session automatically |
| `logSession` | off | `true` saves the session's output to a log file |
| `forwards` | none | Port forwards, one per line (see below) |
| `proxyJump` | none | Id of another saved SSH connection to tunnel through (choose it as the **Jump host** in the connection dialog) |

The port defaults to 22. Host keys use trust on first use: unknown keys and changed keys always ask, and with no prompt available they are refused. Trusted keys are remembered per host and port in `known_hosts.json`.

### Web pages

Choose **Web page (http / https)** as the connection type to save the address of something with a web interface (a router, Proxmox, Grafana, a printer). Double-clicking it opens the page in a tab of the main window, titled with the page, with back, forward and reload buttons. Only `http` and `https` pages load; links that try to open files or other schemes are blocked, and "open in new window" links stay in the same tab. Sites keep their sign-in between visits in a separate browser profile (`WebView2Sites` in the data folder), apart from the terminals.

Devices often use a self-signed certificate. Tick **Accept this site's certificate even if it is not trusted** on that connection and the window loads it anyway, with a warning banner. The check is skipped only for that one connection; every other site is verified as usual. Web connections store no username or password.

### SSH agent

Tick **Use keys from the SSH agent** in the connection dialog and RemoteDeck asks a running agent to sign the login, so you need no key file path and no passphrase prompt. It tries the Windows OpenSSH agent first (start the *OpenSSH Authentication Agent* service, then `ssh-add C:\Users\you\.ssh\id_ed25519`), then Pageant. The agent does the signing: the private key never enters RemoteDeck. Jump hosts can use the agent too. Agent support comes from the MIT-licensed [SshNet.Agent](https://github.com/darinkes/SshNet.Agent) package; ed25519, ECDSA and RSA (SHA-2) keys work.

### Port forwards

Add them in the connection dialog, one per line, in a short form of the OpenSSH options:

| Line | Meaning |
|---|---|
| `L:8080:db.internal:5432` | Local port 8080 reaches `db.internal:5432` through the server (`ssh -L`) |
| `R:9000:localhost:3000` | Port 9000 on the server reaches your `localhost:3000` (`ssh -R`) |
| `D:1080` | A SOCKS proxy on local port 1080 that sends traffic through the server (`ssh -D`) |

They start when the session connects and stop when it closes. Listeners are bound to 127.0.0.1 only, so nothing is exposed to your network. If a port is already taken the connection fails with a message saying so. `LocalForward`, `RemoteForward` and `DynamicForward` lines in an imported OpenSSH `config` are converted.

### Jump hosts

Pick a **Jump host** in the connection dialog to reach a server that is only visible from a bastion. RemoteDeck connects to the bastion with its own saved credential, then opens the SSH connection to your server through it, so the login to the server is still end to end and the bastion only carries encrypted bytes. Jump hosts can be chained (up to 5), and terminals and the SFTP browser both use them. Each hop's host key is checked separately. Importing an OpenSSH `config` links `ProxyJump <alias>` when the alias is in the same file; chains written as `a,b` and `user@host` jumps are not imported.

To try it against a real server (use PowerShell or Windows Terminal):

```powershell
dotnet run --project tools/SshSmoke -- me@my-server
dotnet run --project tools/SshSmoke -- me@my-server:2222 -i C:\Users\me\.ssh\id_ed25519
```

Press Ctrl+] to quit.

## Plugins

The `Plugins...` button (or the palette) lists plugins found in `%LOCALAPPDATA%\RemoteDeck\plugins\<name>\` (each folder holds a `plugin.json` and the plugin's DLL) and in a `plugins` folder next to the program. A plugin is **off until you turn it on**: you see what it asks for and confirm. If a newer version asks for more than you approved, it stays off until you approve again. A plugin can add palette commands (shown as `Plugin: ...`) and new connection types (they appear in the `+ Connection` type list and open in a terminal pane). Each plugin loads in its own isolated load context, so its dependencies cannot clash with RemoteDeck's or another plugin's, and turning it off unloads it.

**What the permissions mean.** `useCredentials` is enforced: without it, the plugin's request for a saved credential is refused, and with it the plugin still only gets one connection's credential inside a callback that the host wipes afterwards. `network`, `fileSystem` and `launchProcess` are declarations that tell you what the plugin intends; .NET cannot sandbox code that runs in-process, so a plugin runs with your Windows account's access. Only turn on plugins whose source you trust.

### Writing a plugin

1. Copy `samples/HelloPlugin`.
2. Implement `IPlugin.Initialize(IPluginContext)` and register commands or connection types.
3. Edit `plugin.json`: id, version, `minHostApiVersion`, the entry-point DLL and the permissions you need.
4. `dotnet build -c Release`, then copy the output folder (the DLL and `plugin.json`, but not `RemoteDeck.Plugin.Abstractions.dll`) into a new folder under the plugins folder above.

To try the sample: build `samples/HelloPlugin`, copy `HelloPlugin.dll` and `plugin.json` from its `bin\Release\net10.0` folder into `%LOCALAPPDATA%\RemoteDeck\plugins\hello`, then open `Plugins...` and turn it on. `Ctrl+Shift+P`, `hello` runs its command, and `Echo (demo)` appears as a connection type.

Plugins never see the credential vault. They ask the `ICredentialBroker` for one connection's credential, read the password inside a callback, and the host wipes it afterwards. This needs the `useCredentials` permission.

## Roadmap

Done: encrypted vault, jump hosts, port forwards, saved connections, SSH terminals with split panes and broadcast, themes with hot reload, importers (PuTTY, OpenSSH config, mRemoteNG, RDCMan, `.rdp`), command palette and shortcuts, RDP (in a tab or the Windows client), SFTP file browser, scrollback search, session logs, auto-reconnect, SSH agent, scrollback size, auto-lock, web pages, saved workspaces, plugin loading, CI.

Next, in no fixed order:

1. More connection types: VNC, telnet and serial, and a git terminal (not scheduled; to be designed)
2. Theme backdrop (Mica, acrylic) and shape for the standard controls
3. Publish to winget (the manifest generator exists, see `packaging/README.md`), Scoop and Chocolatey packages, and a signed installer (release zips are already built by the tag workflow)
4. Windows Hello / DPAPI unlock and clipboard clearing

## License

MIT. See `LICENSE`.
