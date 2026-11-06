# VexBlox Launcher

The official launcher/bootstrapper for VexBlox. It installs and keeps the game
client up to date, registers the `vexblox://` protocol so the website's Play
button can start a game, and launches the client into a game server.

## What it does

- Downloads and verifies the game client (SHA-256 checked) and installs it to
  `%LOCALAPPDATA%\VexBlox\Versions\<year>`.
- Keeps itself up to date on each run.
- Registers the `vexblox://` URL protocol and an entry under
  Installed Apps (with an uninstaller).
- Offers to add a Windows Defender exclusion for the install folder, because
  the old game client is a false positive for some antivirus engines.

## Download

Get the latest launcher from the [Releases](../../releases/latest) page.

## Build

Requires the .NET SDK (the project targets .NET Framework 4.8).

```
dotnet build VexBloxLauncher/VexBloxLauncher.csproj -c Release
```

The output is a single `VexBloxLauncher.exe`.
