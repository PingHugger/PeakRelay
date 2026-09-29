# The launcher (consumer tier)

`PeakRelayLauncher.exe` is the one window a player needs: it checks the game install,
repairs it from GitHub Releases, starts PEAK, and can host the relay for friends. The
installers (`PeakRelayInstaller-*.exe`) remain for a first one-shot setup — the launcher
absorbs their engine (same `InstallRunner`/`PluginDeployer` code) and their update story:
every release ships a fresh launcher exe, and `selfupdate` fetches and starts it.

## The window

- **Game dir row** — must contain `PEAK.exe`; prefilled from saved state or the default
  Steam library. Changing it re-runs the doctor.
- **Status list (the doctor)** — one row per check: game root, loader bytes, BepInEx,
  both plugins, `server.json`, launcher record. Red rows block **PLAY**; amber rows have
  a `fix:` line. Re-runs automatically every 20 s.
- **Install / Update** — pulls the newest GitHub Release, applies its plugin files to the
  game dir, and (always) re-deploys the Doorstop 4.5.0 loader + BepInEx core **from the
  launcher's own embedded payload** — never from the download. Writes `server.json` /
  client config for whatever sides are selected.
- **Include dedicated-host files** — unchecked by default: players who only want to JOIN
  rooms get just the client plugin. Check it (or pass `--dedicated`) to also install the
  dedicated-host files for running your own server; unchecking it again removes them on
  the next Install.
- **PLAY** — runs the doctor; if nothing FAILs, starts `PEAK.exe`.
- **Host relay** — hosts the relay **inside the launcher process** (game TCP 5055, room
  directory http://127.0.0.1:5056/rooms). Click again to stop; ports are released
  immediately. Friends join via the in-game SERVERS browser.

## Loader integrity (why the doctor is byte-exact)

BepInEx 5.4.23.2 ships a 2024 Doorstop 4.3.0 loader that **native-crashes the 2026-09
PEAK update** before any log. The fix is the exact Doorstop 4.5.0 `winhttp.dll`, so the
doctor compares SHA-256 against a pinned constant (`Doctor.KnownLoaderSha256`). Steam
updates can silently overwrite that file — the doctor catches it, Install repairs it.
The release pipeline fails if upstream bytes ever drift from the pin.

## CLI

Same engine, scriptable (exit codes 0 ok / 1 failure / 2 usage):

```sh
PeakRelayLauncher.exe doctor                     # status report (default verb)
PeakRelayLauncher.exe install [--yes] [--dedicated]  # apply latest release (+configs);
                                                     # without --dedicated: player-only
PeakRelayLauncher.exe update                     # only when a newer release exists
PeakRelayLauncher.exe play                       # doctor gate, then start PEAK
PeakRelayLauncher.exe host [--port 5055] [--http 5056]
PeakRelayLauncher.exe selfupdate                 # download + start the newest launcher
PeakRelayLauncher.exe set-token [--token <pat>]  # store a private-repo read token
PeakRelayLauncher.exe clear-token                # remove the stored token
```

Shared flags: `--dir <path>` (game root), server-side `--room --name --max --password
--hostname`, relay `--host --port`.

## Update channel

The launcher checks `https://api.github.com/repos/PingHugger/PeakRelay/releases/latest`
whenever you Install/Update (GUI or CLI). On a **public** repo this is fully anonymous. While the repo is
**private**, the launcher needs a read-only GitHub token, in one of two places (env wins):

1. `PEAKRELAY_GH_TOKEN` in the environment, or
2. `%LOCALAPPDATA%\PeakRelay\github.token` — paste it once via
   `PeakRelayLauncher.exe set-token` (or the GUI asks for it on the first failed
   Install/Update), remove with `clear-token`.

The token is used for API reads and asset downloads; it is never embedded in the binary.
A private/unreachable channel is reported as an explicit error (never as "no releases").
A release counts as an update when its tag
parses to a different version than the running exe (`v0.6.0` ↔ `0.6.0` are equal;
prerelease tags like `v0.7.0-beta.1` count as updates). Declined updates are remembered
(`SkippedTag` in `launcher.json`) and not re-nagged. `selfupdate` downloads
`PeakRelayLauncher.exe` from the release to `%TEMP%` and starts it.

Launcher state lives in `%LOCALAPPDATA%\PeakRelay\launcher.json` (game dir, last
installed tag/asset, skipped tag). Relay files for in-process hosting are extracted once
to `%LOCALAPPDATA%\PeakRelay\relay\`; run logs go to `%LOCALAPPDATA%\PeakRelay\logs\`.
Nothing launcher-owned is ever written inside the game install except the mod files
themselves.
