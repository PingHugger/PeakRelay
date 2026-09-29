# PeakRelay client guide

The client mod (`PeakRelay.Client`) is the **player-side** piece: a BepInEx 5 plugin that
routes a normal PEAK installation to a self-hosted relay instead of Photon Cloud, and adds
an in-game server browser fed by the relay's directory. The game's own networking code is
never replaced — PUN2 keeps running exactly as shipped, only its transport and endpoint
change.

| File | Role |
|---|---|
| `BepInEx/plugins/PeakRelay.Client/PeakRelay.Client.dll` | the plugin |
| `BepInEx/plugins/PeakRelay.Client/PeakRelay.Protocol.dll` | shared framing/codec (required next to the plugin) |
| `BepInEx/config/com.peakrelay.client.cfg` | BepInEx config (created on first launch) |

Requires the same BepInEx 5.x install as the dedicated plugin (see `host-guide.md` §2 —
the same `BepInEx/` folder serves both; a machine can be a dedicated server and a client).

---

## 1. What the plugin does

Two mechanisms, both vanilla-preserving:

1. **Transport + endpoint.** A Harmony postfix on every `LoadBalancingPeer` constructor
   registers `RelayClientSocket` (an `IPhotonSocket` implementation speaking the relay's
   TCP envelope framing) in the UDP protocol slot; a prefix on
   `PhotonNetwork.ConnectUsingSettings` rewrites the app settings (and the shared
   `PhotonServerSettings` asset, so region swaps and re-auth redirect too) to
   `Server = relay host`, `UseNameServer = false`, `AppIdRealtime = "peakrelay"`.
   Every PUN connect path — menu auto-connect, region change, game-server re-auth —
   lands on the relay.
2. **Server browser.** A code-built `UIPage` ("PEAKRELAY SERVERS") is injected into the
   main menu via postfixes on `MainMenuPageHandler.Start` / `MainMenuMainPage.Start`,
   and the vanilla Join button is repurposed (relabelled **SERVERS**) to open it.
   The page fetches `http://<relay>/api/servers` over plain HTTP and joins a row with
   the exact vanilla join shape (`LoadingScreenHandler.Load(PhotonDriven, …)` wrapping
   `PhotonNetwork.JoinRoom(joinKey)` + character-spawn wait).

Version safety: the browser resolves all game-specific members through `GameAPI`
(reflection). On an unknown game build the browser self-disables with a log line and the
menu stays fully vanilla — the transport shim keeps working.

Disabled mode: with `[Relay] Enabled = false` both patches early-return — no socket
registration, no settings rewrite — and the game talks vanilla Photon Cloud UDP.

## 2. Install

Build first (repo root):

```sh
scripts/fetch-libs.sh          # one-time: reference DLLs from the local PEAK install
dotnet build PeakRelay.Client/PeakRelay.Client.csproj
```

Copy the two DLLs into the game:

```sh
mkdir -p "<PEAK install>/BepInEx/plugins/PeakRelay.Client"
cp PeakRelay.Client/bin/Debug/netstandard2.1/PeakRelay.Client.dll \
   PeakRelay.Client/bin/Debug/netstandard2.1/PeakRelay.Protocol.dll \
   "<PEAK install>/BepInEx/plugins/PeakRelay.Client/"
```

(Do **not** install `PeakRelay.Dedicated` on a machine you play on; the dedicated plugin
is host-only and auto-hosts when headless.)

## 3. Configure

`BepInEx/config/com.peakrelay.client.cfg` (BepInEx format; created with defaults on first
launch):

| Section | Key | Default | Meaning |
|---|---|---|---|
| `Relay` | `Enabled` | `true` | route PUN through the relay (`false` = vanilla Photon Cloud) |
| `Relay` | `Host` | `127.0.0.1` | relay address (master and game roles share it) |
| `Relay` | `Port` | `5055` | relay TCP port |
| `Trace` | `Enabled` | `false` | datagram summaries for support/debugging |
| `Trace` | `Port` | `5060` | trace collector port (PeakRelay.Tools.Trace) |

Edit `Host` to the machine running `PeakRelay.Server` (see `host-guide.md` §1) and launch
PEAK normally. The redirect log line to expect in
`BepInEx/LogOutput.log`:

```
[Info : PeakRelay] PeakRelay 0.2.0: relay=True (127.0.0.1:5055) trace=False
[Info : PeakRelay] PUN redirected to relay 127.0.0.1:5055
[Info : PeakRelay] server browser page injected into the main menu
```

## 4. Using the server browser

1. Reach the main menu — the Join button is now **SERVERS**.
2. The page lists every hosted room as
   `DisplayName  (JOINKEY)  players/max  mode`, `[LOCKED]` marking password servers.
   The list comes from the relay's HTTP directory; it reflects **live** rooms only
   (a row exists exactly while a dedicated host is connected to the relay).
3. Click a row to join. Password servers open an in-page password prompt first.
4. Back navigation returns to the main page; **REFRESH** re-fetches the list.

Joining by typed 6-character room code still works in the vanilla UI — both paths are
the same `PhotonNetwork.JoinRoom` underneath.

## 5. Verify you're on the relay (not silently on vanilla)

The fallback design (relay off ⇒ vanilla) makes silence ambiguous, so check explicitly:

* **Plugin log** — the two `PUN redirected…` / `server browser page injected…` lines
  above. If `relay=True` never logs, the plugin isn't loading (check BepInEx console).
* **Client-side socket** — with `[Trace] Enabled = true` each PUN datagram is summarized;
  traffic proves the TCP envelope path is live.
* **Relay side** — the session must appear in the relay log, and while in the menu the
  client shows as a connected master-role peer; after joining, the room's player count on
  `http://<relay>:5056/api/servers` includes you. If the server list loads in the browser
  page, the HTTP channel (same relay host) is reachable.
* **Vanilla fingerprint** — a client that ignored the redirect tries to reach Photon Cloud
  name servers on UDP 5055-5058 (`ns.exitgames.com`); a firewall drop of that plus a
  working relay join is unambiguous proof of the redirect.

## 6. Troubleshooting

* **Game crashes at launch (Unity crash handler, no `BepInEx/LogOutput.log`) after a game
  update** — BepInEx 5.4.23.2's bundled Doorstop 4.3.0 loader native-crashes the 2026-09
  PEAK update during injection. Fix: replace the game's `winhttp.dll` with Doorstop 4.5.0
  (`doorstop_win_release_4.5.0.zip` → `x64/winhttp.dll`). The PeakRelay installers ship
  this fixed loader and always overwrite `winhttp.dll`; older manual installs need it done
  once by hand. Vanilla game unaffected — to play vanilla, rename `winhttp.dll` away.
* **`Unable to start Unity log writer` in LogOutput.log** — cosmetic under the updated
  runtime: BepInEx-side lines still land in `LogOutput.log`, game-engine lines in the
  normal `Player.log`.
* **No `PUN redirected` line** — plugin not loaded: verify BepInEx 5.4.x is installed
  (`winhttp.dll`, `BepInEx/core` exist in the PEAK folder) and the two DLLs are in
  `BepInEx/plugins/PeakRelay.Client/`. If `relay=True` logs but nothing else does, the
  plugin's patches failed at startup — check for `patch installation failed` in
  LogOutput.log and update the plugin build.
* **`relay connect … failed` / Photon timeout** — the relay isn't reachable at
  `Host:Port` (down, wrong port, firewall). The relay must accept **TCP** 5055 (not UDP).
* **Server browser missing** — check `LogOutput.log` for
  `GameAPI: '<member>' not found (game update?)`: a game update moved a member the browser
  needs; transport still works, only the page is disabled. Report/re-add the member in
  `GameAPI.cs`.
* **"no servers online"** — relay reachable but no dedicated host connected. Start one
  (`host-guide.md` §4); rows only exist for live rooms.
* **Join clicked, loading screen closes, nothing happens** — the join failed at PUN level
  (room closed/full, password rejected). PUN's own failure modal may appear; retry via
  REFRESH.
* **Voice** — PUN Voice joins the sibling room `<room>_voice_` automatically through the
  same relay; no client config needed (the relay accepts its op 248 group changes).

## 7. What the client does *not* do

* No Steam bypass on the client side: clients still launch PEAK normally (Steam running).
  The no-Steam mode is a dedicated-server feature (`host-guide.md` §7).
* No matchmaking UI of its own beyond the browser page; lobbies/friends lists remain
  Steam features and are bypassed, not emulated.
* No encryption negotiation changes: the relay speaks the same unencrypted PUN datagrams
  the LAN path would.
