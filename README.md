# RpcClean

Keeps chosen Discord RPC settings of the PulseSync mod for Yandex Music (e.g. no "Install mod" button) and re-applies them after updates.

Unofficial. Not affiliated with PulseSync or Yandex.

## How it works

- Edits only `modSettings.discordRPC` in `%APPDATA%\YandexMusic\config.json`. Mod files and `app.asar` are not touched.
- Background watcher restores the values within ~1 s if the mod or the app resets them. Extra check every 30 s.
- Does not unlock Premium features. `hideBranding` still needs PulseSync Premium.

Default overrides:

```
showButtons=false
showGitHubButton=false
showAlbum=true
```

## Install

1. Download `RpcClean.exe` from [Releases](../../releases/latest).
2. Run it. No admin rights needed.

It copies itself to `%LOCALAPPDATA%\RpcClean\`, adds the scheduled task `RpcCleanWatcher` (at logon) and starts the watcher.

## Configure

Edit `%LOCALAPPDATA%\RpcClean\settings.txt`, one `key=value` per line (`true`/`false`/number/text). Any `discordRPC` key works, e.g. `applicationIDForRPC=<your app id>`.

## Uninstall

```
RpcClean.exe --uninstall
```

Values already written to `config.json` stay.

## Build

Needs only .NET Framework (ships with Windows):

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /optimize+ /out:RpcClean.exe /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Web.dll src\RpcClean.cs
```

## Limits

- Tracks without an album still show `PulseSync Mod x.x.x` on the cover.
- Windows only.
