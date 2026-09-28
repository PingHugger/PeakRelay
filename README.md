# PeakRelay

Self-hosted multiplayer relay for PEAK (Landfall / Aggro Crab), built as a mod. PEAK uses
Photon PUN2 for its networking; this project keeps PUN2's client logic untouched and replaces
only the transport underneath it, using a public Photon3 extension point
(`PhotonPeer.SocketImplementationConfig`) instead of binary patching.

Status: **M0 (recon + passive logging shim)** — see `docs/protocol-notes.md` for the pinned
networking facts and `docs/ops-inventory.md` for the op-code inventory format.

## Layout

| Path | Purpose |
|---|---|
| `PeakRelay.Protocol/` | Framing + relay envelope + Photon datagram/command decoder (shared) |
| `PeakRelay.Server/` | Relay server (room logic comes in M1) |
| `PeakRelay.Client/` | BepInEx 5 plugin: `RelaySocket` (IPhotonSocket) with passthrough+logging shim |
| `PeakRelay.Tools.TestClient/` | Offline protocol tests; headless PUN2 client arrives in M1 |
| `PeakRelay.Tools.Trace/` | JSONL trace summarizer (`dotnet run --project ... -- file.jsonl`) |
| `references/` | Decompiled shipped assemblies (Photon3, PUN, game code) — recon evidence |
| `lib/` | Reference assemblies used for compiling the client plugin |
| `docs/` | protocol-notes, ops-inventory, host guide (later) |

## Building

Requires the .NET 8 SDK.

```sh
dotnet build PeakRelay.sln
dotnet test PeakRelay.Protocol.Tests
```

The client plugin build needs `lib/` to be populated (BepInEx + game DLLs); `scripts/fetch-libs.sh`
does that from the local Steam install and GitHub releases. Everything else builds standalone.

## Ground rules

- Steam/Epic authentication passes through the relay untouched, always.
- Protocol payloads (Protocol16) are never re-encoded — the relay routes opaque datagrams.
- Original code; no Photon Server SDK assets are used or distributed.
