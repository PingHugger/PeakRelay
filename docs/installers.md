# PeakRelay installers

Two Windows GUI installers, one per role — no .NET required on the target machine
(single-file self-contained exes):

| Installer | Installs into the game dir | Extra outputs |
|---|---|---|
| `PeakRelayInstaller-Server.exe` | BepInEx 5.4.23.2 (if missing) + `PeakRelay.Dedicated` plugin + `server.json` | optionally the relay server files + `start-relay.cmd` + `README.txt` into any folder |
| `PeakRelayInstaller-Client.exe` | BepInEx 5.4.23.2 (if missing) + `PeakRelay.Client` plugin + `com.peakrelay.client.cfg` | — |

Both: pick the PEAK install folder (validated: must contain `PEAK.exe`), configure,
install with a step log, and finish with a verification checklist. Re-running is safe —
payloads and config are overwritten, nothing else is touched.

Download/locate them in `dist/installers/` (rebuilt with the commands below).

## Server installer (dedicated host)

Tabs: **1 · Game** (install dir), **2 · Room** (room code, display name, password, max
players, host player name), **3 · Relay** (relay host/port + optional relay staging).

After installing:

1. Start the relay (staged folder → `start-relay.cmd`, needs the .NET 8 Runtime; or your
   own copy: `dotnet PeakRelay.Server.dll 5055 5056`).
2. Launch the game headless: `PEAK.exe -batchmode -nographics -logFile <path>`.
   **Steam does not need to run** — the dedicated plugin's `noSteam` mode activates
   automatically in headless launches.
3. Watch `BepInEx/plugins/PeakRelay.Dedicated/server.log`; the room is live when you see
   `room 'DBPEAK' joined — dedicated server is UP`. Check `http://<relay>:5056/`.

## Client installer (players)

Single page: game dir + relay host/port. After installing, launch PEAK **normally**
(Steam running, no headless flags): the main-menu Join button becomes **SERVERS** (the
in-game server browser fed by the relay) and all connect paths route to the relay.
Join via the browser or the usual 6-character room code.

## CLI mode (same code path as the GUI)

```
PeakRelayInstaller-Server.exe --dir <game> [--room DBPEAK] [--name "..."] [--max 20]
                              [--password ...] [--hostname DedicatedHost]
                              [--host 127.0.0.1] [--port 5055]
                              [--stage-relay <dir>] --yes
PeakRelayInstaller-Client.exe --dir <game> [--host 127.0.0.1] [--port 5055] --yes
```

`--yes` makes it fully non-interactive. Exit codes: 0 = install verified, 1 = install
failed, 2 = usage error (unknown flag, missing flag value, out-of-range `--max`/`--port` —
nothing is installed). Without `--dir` the installers probe `PEAK_GAME_DIR`, the folder
they live in, and the default Steam library path.

## Building the installers

From the repo root (needs .NET 8 SDK — `tools/dotnet` is fine):

```sh
scripts/prepare-installer-payload.sh     # fetch BepInEx zip, pack plugin DLLs, publish relay
dotnet publish Installer.Server/Installer.Server.csproj -c Release -r win-x64 \
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o dist/installers
dotnet publish Installer.Client/Installer.Client.csproj -c Release -r win-x64 \
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o dist/installers
```

The payload (BepInEx zip, plugin DLLs, relay publish output — staged under
`Installer.Core/Payload/payload/`) is embedded into Installer.Core at build time;
the build fails if the payload is missing, and
`tests/…/InstallerPayloadTests.cs` proves every embedded resource is named by the code
(and vice versa), so stale or half-staged payloads cannot ship.

## Layout reference

```
<game dir>/BepInEx/core/…                       BepInEx 5.4.23.2
<game dir>/BepInEx/plugins/PeakRelay.Dedicated/ server plugin + server.json
<game dir>/BepInEx/plugins/PeakRelay.Client/    client plugin
<game dir>/BepInEx/config/com.peakrelay.client.cfg
<staged dir>/PeakRelay.Server.dll … start-relay.cmd  README.txt
```
