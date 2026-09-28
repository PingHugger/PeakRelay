# PeakRelay host guide

PeakRelay replaces Photon Cloud for PEAK with two cooperating pieces:

| Piece | What it is | Where it runs |
|---|---|---|
| **PeakRelay.Server** | Self-hosted LoadBalancing relay (master + game roles in one TCP endpoint) plus the room-list HTTP sidecar | Any .NET 8 box or container |
| **PeakRelay.Dedicated** | BepInEx plugin that turns a stock PEAK install into a headless dedicated server (`-batchmode -nographics`) | Windows PC with PEAK installed |

Regular players run the optional `PeakRelay.Client` shim (or plain PEAK pointed at the relay via its config) and join the dedicated room like any other.

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
  "maxPlayers": 20,
  "visible": true,
  "open": true,
  "relayHost": "127.0.0.1",
  "relayPort": 5055,
  "autoHost": true,
  "useVanillaName": false,
  "logDatagrams": false
}
```

Every field has an environment-variable override — `PEAKRELAY_ROOM`,
`PEAKRELAY_MAXPLAYERS`, `PEAKRELAY_HOST`, `PEAKRELAY_PORT`, `PEAKRELAY_AUTOHOST`,
`PEAKRELAY_VISIBLE`, `PEAKRELAY_OPEN`, `PEAKRELAY_LOGDATAGRAMS` — so container
orchestrators never need to touch files.

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

## 5. Join

Players start PEAK normally with the relay shim installed and set
`relayHost`/`relayPort` to the relay, then enter the room code in the standard
join UI. The relay's browser UI (`http://<relay>:5056/`) shows who is in the room.

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
