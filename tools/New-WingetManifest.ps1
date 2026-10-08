<#
.SYNOPSIS
  Writes the winget manifest files for a RemoteDeck release into packaging/winget/<version>/.

.EXAMPLE
  ./tools/New-WingetManifest.ps1 -Version 0.4.0

  Downloads the release zip from GitHub, works out its SHA256 and writes the three manifest files.
  Check them with `winget validate packaging/winget/0.4.0`, test with
  `winget install --manifest packaging/winget/0.4.0` (needs `winget settings --enable LocalManifestFiles`),
  then submit them to https://github.com/microsoft/winget-pkgs (see packaging/README.md).
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Repo = 'Bagatron/RemoteDeck'
)

$ErrorActionPreference = 'Stop'
$Version = $Version.TrimStart('v')
$id = 'Bagatron.RemoteDeck'
$asset = "RemoteDeck-$Version-win-x64.zip"
$url = "https://github.com/$Repo/releases/download/v$Version/$asset"

$temp = Join-Path ([IO.Path]::GetTempPath()) $asset
Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $temp
$hash = (Get-FileHash $temp -Algorithm SHA256).Hash.ToUpper()
Remove-Item $temp

$folder = Join-Path $PSScriptRoot "..\packaging\winget\$Version"
New-Item -ItemType Directory -Force $folder | Out-Null
$schema = '1.6.0'
$date = Get-Date -Format 'yyyy-MM-dd'

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@ | Set-Content (Join-Path $folder "$id.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
- RelativeFilePath: RemoteDeck.exe
  PortableCommandAlias: remotedeck
Commands:
- remotedeck
ReleaseDate: $date
Installers:
- Architecture: x64
  InstallerUrl: $url
  InstallerSha256: $hash
ManifestType: installer
ManifestVersion: $schema
"@ | Set-Content (Join-Path $folder "$id.installer.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: RemoteDeck contributors
PublisherUrl: https://github.com/$Repo
PublisherSupportUrl: https://github.com/$Repo/issues
PackageName: RemoteDeck
PackageUrl: https://github.com/$Repo
License: MIT
LicenseUrl: https://github.com/$Repo/blob/main/LICENSE
ShortDescription: Lightweight remote desktop and terminal manager with an encrypted password vault.
Description: RemoteDeck manages SSH, Remote Desktop and web connections in one window. It has no connection limit, an encrypted password vault, split-pane terminals with broadcast input, themes and plugins.
Moniker: remotedeck
Tags:
- ssh
- rdp
- remote-desktop
- terminal
- sftp
- connection-manager
ManifestType: defaultLocale
ManifestVersion: $schema
"@ | Set-Content (Join-Path $folder "$id.locale.en-US.yaml") -Encoding utf8

Write-Host "Wrote manifests to $folder"
Write-Host "Next: winget validate `"$folder`""
