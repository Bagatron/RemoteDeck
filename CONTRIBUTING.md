# Contributing to RemoteDeck

Thanks for helping. RemoteDeck is a small, MIT-licensed remote desktop and terminal manager for Windows, and the aim is to keep it light and easy to extend.

## Getting set up

You need Windows, the **.NET 10 SDK** and the Microsoft Edge WebView2 runtime (already on Windows 11).

```powershell
dotnet build RemoteDeck.slnx
dotnet test RemoteDeck.slnx
dotnet run --project src/RemoteDeck.App
```

## How the code is organised

- `RemoteDeck.Core`: connection catalog, layouts, broadcast rules, themes, importers. No UI, no network. Most logic and most tests live here.
- `RemoteDeck.Vault`: encrypted credential storage (Argon2id + AES-256-GCM).
- `RemoteDeck.Protocols.*`: one project per connection type, each behind small interfaces so it can be tested with fakes.
- `RemoteDeck.Plugin` / `RemoteDeck.Abstractions`: the contract plugins build against. Treat changes here as breaking changes.
- `RemoteDeck.App`: the WPF shell. The terminals run in one WebView2 page (`Web/terminal.html`) that talks to the host with JSON messages.

## Pull requests

- Keep a change to one thing. Say what it does and why.
- Add or update tests for anything in Core, Vault or the protocol libraries. `dotnet test` must pass.
- Follow the existing style: nullable enabled, small classes, comments that explain why.
- Never log or persist a password. Secrets stay in the vault, and plugins reach them only through the credential broker.
- UI changes: include a screenshot.

## Themes and plugins

Themes are JSON files (see `themes/` and `schemas/`). A new theme with only a few colors is welcome; anything left out falls back to the built-in dark or light theme. Plugin notes are in the README under "Writing a plugin".

## Reporting bugs

Open an issue with your Windows version, what you did, what you expected and what happened. For anything security-related, follow `SECURITY.md` instead of opening a public issue.
