# Packaging

## winget

winget installs from a public repository, [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs). A release is published there by sending a pull request with three small manifest files; Microsoft's bots check the download and the hash, and a person approves it.

For each release:

1. Wait for the GitHub release to finish (the zip and `SHA256SUMS.txt` are attached).
2. Create the manifests: `./tools/New-WingetManifest.ps1 -Version 0.4.0`. They go in `packaging/winget/0.4.0/`.
3. Check them: `winget validate packaging/winget/0.4.0`.
4. Try them on your own PC: run `winget settings --enable LocalManifestFiles` once (needs an administrator prompt), then `winget install --manifest packaging/winget/0.4.0`. RemoteDeck should install and `remotedeck` should start it from a new terminal.
5. Submit: the easy way is `wingetcreate` (`winget install wingetcreate`), then `wingetcreate submit packaging/winget/0.4.0` (it asks you to sign in to GitHub and opens the pull request for you). Or copy the folder to `manifests/b/Bagatron/RemoteDeck/0.4.0/` in a fork of winget-pkgs and open the pull request yourself.
6. After the first version is accepted, later versions can be sent with `wingetcreate update Bagatron.RemoteDeck --version 0.5.0 --urls <new zip url> --submit`.

The package is the self-contained zip, so nothing else has to be installed first. It is installed as a *portable* package: winget unpacks it into its own folder, adds the `remotedeck` command and removes it again on uninstall. It does not create a Start menu entry; a proper installer (signed MSI or MSIX) is a later step.

Note that Windows SmartScreen may warn about an unsigned download until the app is signed.

## Scoop

Scoop installs from a JSON manifest, and nobody has to approve it. `packaging/scoop/remotedeck.json` is that manifest. Anyone can install straight from it:

```
scoop install https://raw.githubusercontent.com/Bagatron/RemoteDeck/main/packaging/scoop/remotedeck.json
```

That adds a `remotedeck` command and a Start menu shortcut, and `scoop update remotedeck` follows new releases once the manifest is updated.

For each release, change `version`, `url` and `hash` in the manifest (the hash is the line for the self-contained zip in the release's `SHA256SUMS.txt`). If you have Scoop's tools, `checkver -u` and `checkver` do this for you, and `scoop-bucket` style repositories can pick the file up automatically.

Later, the manifest can move into a bucket of its own (a repository named `scoop-remotedeck`, installed with `scoop bucket add remotedeck https://github.com/Bagatron/scoop-remotedeck`), or be sent to Scoop's `extras` bucket once the project is better known.

The releases are currently marked as *pre-release* on GitHub; the manifest's `checkver` reads the full list of releases for that reason.
