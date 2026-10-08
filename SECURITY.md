# Security policy

RemoteDeck stores credentials and opens remote sessions, so security reports are taken seriously.

## Reporting a vulnerability

Please do **not** open a public issue. Use GitHub's private vulnerability reporting (the **Security** tab of this repository, then **Report a vulnerability**). Include what you found, how to reproduce it and the version you tested. You will get a reply as soon as the maintainer can; this is a small volunteer project, so please allow a few days.

## Status of the crypto

The credential vault (Argon2id key derivation, AES-256-GCM encryption, format version 1) has **not** been independently audited. Until it has, treat it as unreviewed and do not rely on it for secrets you could not afford to lose. Reviews of the vault code are very welcome.

## Known limitations

- A master password typed into the unlock dialog lives in a .NET string for a short time and cannot be wiped from memory.
- There is no recovery for a forgotten master password.
- SSH host keys are trusted on first use and stored in `%LOCALAPPDATA%\RemoteDeck\known_hosts.json`; a changed key always asks before it is replaced.
- Importers never read stored passwords from other tools. RDP connections open in the Windows client, which handles the password itself.
- Windows Hello / DPAPI unlock, an auto-lock timer and clipboard clearing are not implemented yet.

## Supported versions

Only the latest release receives fixes while the project is pre-1.0.
