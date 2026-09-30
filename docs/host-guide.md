# PeakRelay host guide

PeakRelay replaces Photon Cloud for PEAK with two cooperating pieces:

| Piece | What it is | Where it runs |
|---|---|---|
| **PeakRelay.Server** | Self-hosted LoadBalancing relay (master + game roles in one TCP endpoint) plus the room-list HTTP sidecar | Any .NET 8 box or container |
| **PeakRelay.Dedicated** | BepInEx plugin that turns a stock PEAK install into a headless dedicated server (`-batchmode -nographics`) | Windows PC with PEAK installed |

Regular players run the optional `PeakRelay.Client` shim (or plain PEAK pointed at the relay via its config) and join the dedicated room like any other — see `docs/client-guide.md`.

---

## 1. Start the relay server

```bash
dotnet PeakRelay.Server.dll 5055 5056
```

* `5055` — relay port (Photon LoadBalancing over TCP envelopes). Clients connect here.
* `5056` — room-list HTTP. `GET /rooms` (JSON) and `/` (browser UI, auto-refresh).

On Windows the HTTP sidecar prefers `HttpListener`; without a URL ACL it automatically
falls back to a raw `TcpListener` — both serve identical responses, no `netsh` needed.

### Docker

```bash
docker build -t peakrelay-server .
docker run -p 5055:5055 -p 5056:5056 peakrelay-server
```

## 2. Install BepInEx + the dedicated plugin (one-time)

```bash
scripts/peak-server.sh install
```

This unpacks BepInEx into the PEAK install, copies the built plugin DLLs to
`BepInEx/plugins/PeakRelay.Dedicated/`, and writes a default `server.json`.

## 3. Configure

`BepInEx/plugins/PeakRelay.Dedicated/server.json`:

```json
{
  "roomName": "DBPEAK",
  "displayName": "PeakRelay Dedicated",
  "maxPlayers": 20,
  "visible": true,
  "open": true,
  "relayHost": "127.0.0.1",
  "relayPort": 5055,
  "autoHost": true,
  "useVanillaName": false,
  "logDatagrams": false,
  "noSteam": null,
  "hostName": "DedicatedHost",
  "console": true,
  "consoleVerbose": false
}
```

Every field has an environment-variable override — `PEAKRELAY_ROOM`,
`PEAKRELAY_DISPLAYNAME`, `PEAKRELAY_MAXPLAYERS`, `PEAKRELAY_HOST`, `PEAKRELAY_PORT`,
`PEAKRELAY_AUTOHOST`, `PEAKRELAY_VISIBLE`, `PEAKRELAY_OPEN`, `PEAKRELAY_LOGDATAGRAMS`,
`PEAKRELAY_NOSTEAM`, `PEAKRELAY_HOSTNAME`, `PEAKRELAY_CONSOLE`,
`PEAKRELAY_CONSOLEVERBOSE` — so container orchestrators never need to
 touch files. (`displayName`/`mode`/`password` are what the server browser shows;
`hostName` is the host's player name when Steam can't provide one — see §5.)

### Room names

PEAK's join-by-code UI requires exactly **6 characters**: `[region letter][platform letter][4 more]`
(`Utilities.ROOM_NAME_LENGTH = 6`; region letters map A–O over the 15 regions, so `eu` = `D`;
`CodeToPlatform` buckets the second character by platform family, Windows ≈ A–D).
The default `DBPEAK` satisfies the format. Names that break the format still work for
friends joining via the debug join path, but the standard room-code UI will reject them.
`useVanillaName: true` restores the game's stock random-name behavior instead.

## 4. Run the dedicated server

```bash
scripts/peak-server.sh run
```

Equivalent to launching `PEAK.exe -batchmode -nographics`. The plugin then:

1. waits for `GameHandler` + the Title scene,
2. arms `HostState` with the configured room and loads `WilIsland` (the game's own
   auto-host path),
3. connects PUN to the relay through the relay TCP socket,
4. on `OnConnectedToMaster` the game itself calls `CreateRoom(roomName)`.

Status lands in `BepInEx/plugins/PeakRelay.Dedicated/server.log`;
`scripts/peak-server.sh status` tails it. Disconnects re-arm the host cycle
(5 attempts) without human input.

Launch tip: pass an explicit `-logFile <path>` when running PEAK manually — a fresh
`-batchmode` install sometimes never writes the default `Player.log`, which makes the
first boot look hung when it is merely silent.

Success markers in `server.log`, in order: `config: …` → `GameHandler ready` →
`Title scene active` → `HostState armed (room '…')` → `CreateRoom('…')` →
`room created` → `room '…' joined — dedicated server is UP`.

### The operator console window

With `console: true` (the default) the server opens its own console window — even when
started by the launcher or by double-clicking `PeakServer.exe`. Everything there is written
in plain language for non-developers:

```
2026-09-30 17:05 [PEAK] The expedition 'DBPEAK' is now open for adventurers (up to 20 players).
2026-09-30 17:06 [PEAK] Bob joined the expedition (1 of 20 slots in use).
2026-09-30 17:07 [PEAK] The server is up and running.
```

Technical relay lines (datagram sizes, serialization details) stay hidden — they still go
to `server.log`. Type `verbose on` in the console to see them, `verbose off` to hide them
again (`consoleVerbose: true` in `server.json` does the same from startup).

**Commands** — type one and press Enter (input is echoed as `[YOU] > …`):

| Command | What it does |
|---|---|
| `help [command]` | Lists every command, or explains one |
| `status` | Is the server running, which expedition, how full is it |
| `players` | Who is on the server right now |
| `kick <name or slot>` | Removes a player (same path as the in-game kick button) |
| `say <message>` | Broadcasts a message to all players in the round |
| `resethost` | Restarts the expedition hosting (players rejoin automatically) |
| `stop` | Shuts the server down cleanly |
| `verbose <on\|off>` | Shows/hides technical detail (for support requests) |
| `clear` | Clears the window (`server.log` keeps everything) |

Anything the console does is also mirrored to `server.log`, so the file remains the complete
record. Set `console: false` to run without the window (output goes to stdout, e.g. for
services or Docker where `PEAKRELAY_CONSOLE=false` keeps the container log clean).

## 5. Running without Steam (headless dedicated mode)

A headless dedicated server does **not** need Steam running. The plugin ships a
`noSteam` mode (config `noSteam` / env `PEAKRELAY_NOSTEAM`; default **auto** = on when
the process is headless, off for normal windowed play).

How it works: PEAK itself has a built-in no-Steam path gated on the play-mode tag
`NoSteam` — with the tag set the game skips SteamManager (the component whose startup
calls `RestartAppIfNecessary` and quits when Steam doesn't own the launch), skips the
Steam lobby/achievement services, and falls back to `NoMatchmaking`, no rich presence,
and a persistent random `UserID`. The plugin injects the tag and repairs the two gaps
the shipped build still has in that mode:

* `PlatformBootstrap` otherwise waits forever for `SteamManager.Initialized` — the
  plugin completes the boot gate directly (log: `platform bootstrap completed without
  Steam`).
* `PrintNetworkStates` otherwise throws on the missing Steam lobby service and aborts
  the connect callback before room creation — the plugin skips that diagnostic dump.

Additionally `SteamAPI.Init`/`RestartAppIfNecessary` are never allowed to touch the
native Steamworks library, and the host's player name falls back to `hostName`
(default `DedicatedHost`) wherever the game would read the Steam persona name.

Operation is identical otherwise: launch with Steam fully closed
(`steam.exe` not in `tasklist`) and watch for the §4 success markers. The room shows up
on the relay exactly as with-Steam runs; joining clients are unaffected.

## 6. Join

Players start PEAK normally with the relay shim installed and set
`relayHost`/`relayPort` to the relay, then either open the **SERVERS** browser page or
enter the room code in the standard join UI. The relay's browser UI
(`http://<relay>:5056/`) shows who is in the room. Client setup:
`docs/client-guide.md`.

## Troubleshooting

* **`relay connect failed` in server.log** — relay not reachable at `relayHost:relayPort`.
  Check the relay process and firewall.
* **`CreateRoom failed: GameIdAlreadyExists`** — another host owns the name; pick another
  `roomName` or stop the other server.
* **Room list empty in the browser** — the room shows up only while a host is connected;
  the host never connected (see `server.log`).
* **Players time out joining** — clients must point at the *relay*, not Photon Cloud.
  The relay never talks to the internet unless you expose its ports.
* **HttpListener "Zugriff verweigert"** — harmless; the TcpListener fallback takes over
  (visible in the relay startup line).
* **Headless run stalls after `GameHandler ready`, no Title scene** — the process is
  waiting on Steam: `noSteam` is not active (explicitly set `noSteam: true` in
  `server.json` and restart) or Steam is half-running. Check for
  `Steam runtime manager initialized: False` in the Unity log.
* **`[Steamworks.NET] Shutting down because RestartAppIfNecessary returned true`** —
  Steam tried to relaunch the game: `noSteam` mode is off in a headless run. Set
  `noSteam: true` (or check that the deployed `PeakRelay.Dedicated.dll` is current).
* **`KeyNotFoundException: 'SteamLobbyHandler'` after connect** — running an older
  plugin build without the noSteam patch set while Steam is absent; update the plugin.
* **`ArgumentNullException ... Parameter name: key` storms at spawn (ReconnectHandler /
  PlayerHandler.IsBanned / AudioLevels), players spawn "broken" (stuck passed-out, empty
  hotbar) while the world works** — an OUTDATED RELAY: builds before the player-identity
  fix left every `Player.UserId` null (see `docs/protocol-notes.md`, "Player identity
  contract"). Replace `PeakRelay.Server.dll` + `PeakRelay.Protocol.dll` on the relay and
  restart it; the dedicated host re-connects on its own host cycle. The old
  "Audio slider ArgumentNullException — cosmetic" note described the same root cause
  before it was understood; it is not cosmetic and not headless-only.
