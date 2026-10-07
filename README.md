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

Not covered yet: Windows Hello/DPAPI unlock, an auto-lock timer, and a clipboard-clearing helper. Treat the format as version 1 and **unreviewed**: have the crypto code audited before trusting it with real secrets, and note that .NET strings holding a typed password cannot be wiped, so the UI should collect it in a `char[]`.

## Themes

The `Theme` button at the bottom of the sidebar lists the built-in themes (RemoteDeck Dark and Light), the example files in the `themes` folder next to the program, and your own. Put your own `.json` files in `%LOCALAPPDATA%\RemoteDeck\themes` (the `Open my themes folder` item opens it); a file there replaces a shipped theme of the same name. Saving a theme file applies it straight away, no restart needed. A file with a mistake is skipped and the `Theme problems` item names the file and the field.

Applied today: interface colors, terminal colors (background, cursor, selection, the 16 ANSI colors) and fonts (the terminal uses the theme's font size plus one). Corner radius, border width, density and backdrop are read and checked but not drawn yet. The choice is remembered in `settings.json`.

## Importing

`Import...` in the sidebar reads an OpenSSH `config`, a PuTTY registry export (`reg export HKCU\Software\SimonTatham\PuTTY\Sessions putty.reg`), a `.rdp` file, an RDCMan `.rdg` or an mRemoteNG `confCons.xml` (not fully encrypted). Folders are kept. Passwords are never imported; add them afterwards by editing the connection. Anything skipped (a protocol not supported yet, `ProxyJump`, wildcard hosts) is listed in a note when the import finishes.

## Running the app

```powershell
dotnet run --project src/RemoteDeck.App
```

The first start asks you to create a master password; after that it asks for it each time. Passwords you save are encrypted in `%LOCALAPPDATA%\RemoteDeck\vault.json` (Argon2id + AES-256-GCM), and your connections are stored without any secrets in `connections.json` next to it. There is no way to recover a forgotten master password.

- **Sidebar:** `+ Connection` and `+ Folder` add entries, double-click (or Enter) opens one, right-click edits, renames or deletes. The search box filters as you type.
- **Split panes:** the `Layout` button switches the current tab to a preset (two columns, 2 x 2 grid, and so on). Each pane header has buttons to split right or down. Drag the bars between panes to resize them. A saved connection opens in the focused empty pane, or in a new tab if none is empty; use right-click, `Open in new tab` to force a new tab. The x in a pane header ends its session, and a second click on an empty pane removes it.
- **Broadcast:** click the broadcast icon in each pane header you want in the group, then either switch on `Broadcast` (whatever you type in a grouped pane goes to all of them; the panes get a red border) or type a command in the bar and press Enter to run it on every grouped pane. Multi-line pastes and risky commands such as `rm -rf` or `shutdown` ask for confirmation first. Broadcast always starts off.
- **Quick connect:** type `user@host` (or `user@host:port`) above the tabs for a one-off connection that is not saved.
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

The port defaults to 22. Host keys use trust on first use: unknown keys and changed keys always ask, and with no prompt available they are refused. Trusted keys are remembered per host and port in `known_hosts.json`.

To try it against a real server (use PowerShell or Windows Terminal):

```powershell
dotnet run --project tools/SshSmoke -- me@my-server
dotnet run --project tools/SshSmoke -- me@my-server:2222 -i C:\Users\me\.ssh\id_ed25519
```

Press Ctrl+] to quit.

## Writing a plugin

1. Copy `samples/HelloPlugin`.
2. Implement `IPlugin.Initialize(IPluginContext)` and register commands or connection types.
3. Edit `plugin.json`: id, version, `minHostApiVersion`, the entry-point DLL and the permissions you need.

Plugins never see the credential vault. They ask the `ICredentialBroker` for one connection's credential, read the password inside a callback, and the host wipes it afterwards. This needs the `useCredentials` permission.

## Roadmap

1. **MVP**: WPF shell (WPF-UI), connection tree and tabs, RDP and SSH, encrypted vault (Argon2id + AES-256-GCM)
2. Importers (mRemoteNG, RDCMan, PuTTY, `.rdp`, `~/.ssh/config`)
3. Wire split panes and broadcast into the UI (single WebView2 hosting all terminals)
4. VNC, SFTP, web tabs, command palette, plugin loading with `AssemblyLoadContext`
5. Packaging for winget, Scoop and Chocolatey

## License

MIT. See `LICENSE`.
