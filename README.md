# PeakRelay

Self-hosted multiplayer relay for PEAK (Landfall / Aggro Crab), built as a mod. PEAK uses
Photon PUN2 for its networking; this project keeps PUN2's client logic untouched and replaces
only the transport underneath it, using a public Photon3 extension point
(`PhotonPeer.SocketImplementationConfig`) instead of binary patching.

Status: **dedicated hosting verified live, Steam not required** — validated LoadBalancing
relay (M1, live against real Photon3 clients), headless dedicated-server plugin (M4,
no-Steam mode live-verified), room-list HTTP + in-game server browser (M5/P1).
See `docs/protocol-notes.md` for the pinned networking facts, `docs/host-guide.md` to run
your own server, and `docs/client-guide.md` for the player-side mod.

## Layout

| Path | Purpose |
|---|---|
| `PeakRelay.Protocol/` | Framing + relay envelope + Photon datagram/command decoder (shared) |
| `PeakRelay.Server/` | Relay server: LoadBalancing master/game roles, room logic, room-list HTTP sidecar + browser UI |
| `PeakRelay.Client/` | BepInEx 5 plugin for normal clients: relay transport shim + in-game server browser |
| `PeakRelay.Dedicated/` | BepInEx 5 plugin: turns stock PEAK into a headless dedicated server (`server.json`, auto-host, retry) |
| `PeakRelay.Tools.TestClient/` | Headless PUN2 client that runs the full relay scenario (host join + guest join + events + properties) |
| `PeakRelay.Tools.Trace/` | JSONL trace summarizer (`dotnet run --project ... -- file.jsonl`) |
| `tests/PeakRelay.Protocol.Tests/` | Protocol unit tests (framing, P16 codec, datagram parsing) |
| `references/` | Decompiled shipped assemblies (Photon3, PUN, game code) — recon evidence |
| `lib/` | Reference assemblies used for compiling the plugins (`scripts/fetch-libs.sh`) |
| `Installer.Core/` | shared installer engine: embedded BepInEx/plugin/relay payloads, config writers, verification |
| `Installer.Server/`, `Installer.Client/` | one-click Windows GUI installers (also scriptable via CLI flags) |
| `scripts/` | fetch-libs, publish, peak-server (dedicated install/run/status), prepare-installer-payload |
| `docs/` | protocol-notes, ops-inventory, host-guide, client-guide, installers, distribution, building |

## Quick start

```sh
# 1. relay server (any .NET 8 box)
dotnet run --project PeakRelay.Server -- 5055 5056
#    room list: http://localhost:5056/  (browser UI) · http://localhost:5056/rooms (JSON)

# 2. headless dedicated PEAK server (same machine: 127.0.0.1)
scripts/peak-server.sh install
scripts/peak-server.sh run
```

**Distribution** — pick your tier (see `docs/distribution.md`):

| Tier | For | Get it | Guide |
|---|---|---|---|
| Consumer | players & Windows server owners | **PeakRelayLauncher.exe** (status doctor, install/update/play/host) | `docs/launcher.md` |
| Operator | Docker/K8s/service deployments | `PeakRelay.Server.zip` + plugin DLLs from Releases | `docs/distribution.md` |

The dedicated host runs with Steam closed; players install the client plugin and join via
the in-game SERVERS browser. CI runs on every push; version tags publish both tiers to
GitHub Releases.

Players join with the room code in PEAK's standard join UI or the in-game **SERVERS**
browser page. Full guide incl. Docker and troubleshooting: `docs/host-guide.md`;
player-side setup: `docs/client-guide.md`. The headless host runs with Steam closed
(`noSteam` mode is automatic under `-batchmode -nographics`).

## Building

Requires the .NET 8 SDK (a portable one lives in `tools/dotnet`; see `docs/building.md`).

```sh
dotnet build PeakRelay.sln
dotnet test tests/PeakRelay.Protocol.Tests
```

Full build guide — lib provisioning, plugins, relay, installers, tests, troubleshooting:
`docs/building.md`.

The plugin builds need `lib/` to be populated (BepInEx + game DLLs); `scripts/fetch-libs.sh`
does that from the local Steam install and GitHub releases. The server and tests build standalone.

## Ground rules

- Steam/Epic authentication passes through the relay untouched, always.
- Protocol payloads (Protocol16) are never re-encoded — the relay routes opaque datagrams.
- Original code; no Photon Server SDK assets are used or distributed.
