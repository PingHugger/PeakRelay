# PeakRelay build guide

How to build every piece of the project from a fresh clone: the relay server, the two game
plugins (dedicated host / client), the test client, and the Windows GUI installers. Every
command here has been executed against this tree.

There are **two deliverables per role** — pick what you need:

| You want to… | Build | Then |
|---|---|---|
| run the relay | `PeakRelay.Server` | §4, then `docs/host-guide.md` |
| host a headless dedicated PEAK | `PeakRelay.Dedicated` plugin | §3 + §5, then `docs/host-guide.md` |
| play on a relay as a normal client | `PeakRelay.Client` plugin | §3 + §6, then `docs/client-guide.md` |
| hand one-click installers to players | `Installer.*` | §7, then `docs/installers.md` |

---

## 1. Prerequisites

- **Windows** for everything GUI- and plugin-related (plugins are installed into the PEAK
  game dir; the relay also builds and runs on Linux).
- **.NET 8 SDK.** Two supported options:
  - *Portable, repo-local (recommended, what this checkout uses):* `tools/dotnet/`.
    Bootstrap it with the official script — no admin rights, survives game reinstalls:
    ```sh
    curl -sL -o dotnet-install.ps1 https://dot.net/v1/dotnet-install.ps1
    powershell -NoProfile -ExecutionPolicy Bypass \
        -File dotnet-install.ps1 -Channel 8.0 -InstallDir "<repo>\tools\dotnet"
    ```
    All `dotnet` commands below then use `tools/dotnet/dotnet.exe` (scripts auto-detect it);
    when invoking dotnet directly also `export DOTNET_ROOT="<repo>\tools\dotnet"`.
  - *System-wide:* install the .NET 8 SDK from https://dotnet.microsoft.com — everything
    works with plain `dotnet` on PATH.
- **A PEAK installation** (Steam) — the plugin build references game assemblies, and
  `lib/` must be provisioned from it (next section).
- **MSYS2/Git Bash** on Windows for the shell scripts (they use `cygpath`/`unzip`/`curl`;
  the scripts deliberately avoid a `zip` binary by shelling out to PowerShell's
  `Compress-Archive`).

## 2. Provision `lib/` (one-time, required for the plugins)

The plugins compile against the game's own DLLs plus BepInEx/Harmony; none of those are
committed. Fetch them (defaults to the Steam library path, or pass any PEAK install):

```sh
scripts/fetch-libs.sh
# or: scripts/fetch-libs.sh "D:/Games/PEAK"
```

This fills `lib/` (~20 DLLs incl. BepInEx, 0Harmony, Photon3/PUN, Assembly-CSharp,
Steamworks.NET, SteamCommon, UnityEngine.MultiplayerModule — the last three are required by
the noSteam dedicated build). `lib/` is gitignored; never redistribute it.

Sanity check — this should already build now:

```sh
tools/dotnet/dotnet.exe build PeakRelay.sln        # or: dotnet build PeakRelay.sln
```

The solution includes: relay, protocol lib, both plugins, test client, trace tool,
protocol tests, and the three installer projects. 0 errors = toolchain good.

## 3. Build the two game plugins

Both target `netstandard2.1` (BepInEx 5 plugin profile) and depend on `PeakRelay.Protocol`:

```sh
tools/dotnet/dotnet.exe build PeakRelay.Dedicated/PeakRelay.Dedicated.csproj
tools/dotnet/dotnet.exe build PeakRelay.Client/PeakRelay.Client.csproj
```

Outputs (fresh timestamps matter — a stale bin has caused ghost bugs here):

- `PeakRelay.Dedicated/bin/Debug/netstandard2.1/PeakRelay.Dedicated.dll` (+ `PeakRelay.Protocol.dll`)
- `PeakRelay.Client/bin/Debug/netstandard2.1/PeakRelay.Client.dll` (+ `PeakRelay.Protocol.dll`)

Deployment is normally not done by hand — §5/§6 install them for you; the manual copy is:

```
BepInEx/plugins/PeakRelay.Dedicated/{PeakRelay.Dedicated.dll,PeakRelay.Protocol.dll}
BepInEx/plugins/PeakRelay.Client/{PeakRelay.Client.dll,PeakRelay.Protocol.dll}
```

## 4. Build & run the relay server

```sh
scripts/publish.sh          # Release publish -> dist/PeakRelay.Server/
```

Run it (needs the .NET 8 **runtime** on the target box):

```sh
cd dist/PeakRelay.Server && dotnet PeakRelay.Server.dll 5055 5056
```

- TCP `5055` — game traffic (LoadBalancing master+game roles in one endpoint)
- HTTP `5056` — `GET /rooms` (JSON), `/api/servers` (rich directory), `/` (browser UI)

For a quick debug build instead: `dotnet run --project PeakRelay.Server -- 5055 5056`.

## 5. Server role: install the dedicated plugin into PEAK

Either run the **server installer** (§7, GUI or CLI) or the shell script:

```sh
scripts/peak-server.sh install    # BepInEx + plugin DLLs + server.json template
PEAK_GAME_DIR="C:/Program Files (x86)/Steam/steamapps/common/PEAK" scripts/peak-server.sh run
# (run = PEAK.exe -batchmode -nographics -logFile BepInEx/LogOutput.log)
scripts/peak-server.sh status     # tails BepInEx/plugins/PeakRelay.Dedicated/server.log
```

UP marker chain in `server.log`: `config: …` → `GameHandler ready` → `Title scene active`
→ `HostState armed` → `CreateRoom('DBPEAK')` → `room created` →
`room 'DBPEAK' joined — dedicated server is UP`. **Steam does not need to run** — headless
launches enable `noSteam` mode automatically (see `docs/host-guide.md` §5).

## 6. Client role: install the client plugin into PEAK

Run the **client installer** (§7) or copy the two DLLs from §3 into
`BepInEx/plugins/PeakRelay.Client/` of the player's game install, then write
`BepInEx/config/com.peakrelay.client.cfg` (the installer generates it):

```ini
[Relay]
Enabled = True
Host = 127.0.0.1     # the relay's address
Port = 5055
```

Launch PEAK normally (Steam running, no headless flags). The Join button becomes
**SERVERS** (in-game browser fed by `/api/servers`); room-code joining still works.
Known limitation: the client plugin does not yet force `GpBinaryV16` — until that fix is
ported (flagged in every recent pass), a client join against the relay is not expected to
complete. Transport redirect + browser UI work.

## 7. Build the GUI installers (Windows)

Two single-file **self-contained** exes (~155 MB each; no .NET needed on the target),
built from an embedded payload that must be staged first:

```sh
scripts/prepare-installer-payload.sh
# stages Installer.Core/Payload/payload/:
#   bepinex.zip (5.4.23.2), plugins/Dedicated/*.dll, plugins/Client/*.dll, relay/relay.zip

tools/dotnet/dotnet.exe publish Installer.Server/Installer.Server.csproj \
    -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o dist/installers
tools/dotnet/dotnet.exe publish Installer.Client/Installer.Client.csproj \
    -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o dist/installers
```

Guards baked in: building without the payload **fails** (`CheckPayloadPresent` target), and
`tests/…/InstallerPayloadTests.cs` proves every embedded resource is named by the code and
vice versa — stale installers cannot ship. The exes embed everything (BepInEx zip, plugin
DLLs, relay publish); both GUI wizards *and* a scriptable CLI live inside:

```sh
dist/installers/PeakRelayInstaller-Server.exe --dir "<PEAK install>" --room DBPEAK --yes
dist/installers/PeakRelayInstaller-Client.exe --dir "<PEAK install>" --host 127.0.0.1 --port 5055 --yes
```

Exit codes: 0 = installed & verified, 1 = install failed, 2 = usage error (unknown flag,
missing value, out-of-range `--max` 1–100 / `--port` 1–65535). Without `--dir` they probe
`PEAK_GAME_DIR`, their own folder, and the default Steam library.

## 8. Tests & verification

```sh
tools/dotnet/dotnet.exe test tests/PeakRelay.Protocol.Tests     # 18 tests incl. payload contract
dotnet run --project PeakRelay.Tools.TestClient                 # live 8-step relay scenario
PEAKRELAY_HTTPPORT=5056 dotnet run --project PeakRelay.Tools.TestClient
```

`TestClient` needs a **running relay** (§4) and exits 0 only if all steps pass: transport +
auth, master auth, room create/join, actor lists, event routing, property broadcast,
N/M/P metadata round-trip, `/api/servers` check. It is the fastest full-stack proof after
any relay/protocol change.

## 9. Troubleshooting

- **`no dotnet found (tools/dotnet or PATH)`** from a script — bootstrap §1's portable SDK
  or install the system SDK.
- **Plugin build fails on missing `SteamCommon`/`Steamworks.NET`/`MultiplayerModule`** —
  `lib/` is stale: re-run `scripts/fetch-libs.sh` (it now provisions all three).
- **`embedded payload missing` at installer runtime** — the exe predates
  `prepare-installer-payload.sh`; rebuild the installers (§7). The build-time guard makes
  this near-impossible; if you see it, someone shipped a hand-patched binary.
- **`error MSB3027 … being used by another process`** — a relay/PEAK instance still holds
  DLLs: kill `dotnet.exe`/`PEAK.exe` before rebuilding.
- **Building a single csproj leaves stale sibling outputs** — known quirk here: build the
  specific project you deploy from, and verify DLL timestamps after builds.
- **Game-folder resets wipe BepInEx** — keep the SDK repo-local (§1); reinstall the
  plugins with the installers or `peak-server.sh install`; game resets cannot touch
  `tools/` or the repo.
