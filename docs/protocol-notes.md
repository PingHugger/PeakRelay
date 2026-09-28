# PEAK networking pin-down (M0 recon)

Every claim below was verified against decompiled shipped assemblies (`references/`, generated
with ILSpy 8.2 from `PEAK_Data/Managed/*.dll`, game 2.4.c) or binary inspection of the install.
File/line references point into `references/`.

## Versions

| Component | Version | Evidence |
|---|---|---|
| Unity | 6000.3.15f1 | `globalgamemanagers` header |
| PUN2 | **2.49** | `PhotonNetwork.PunVersion` (`references/PhotonUnityNetworking-src/Photon.Pun/PhotonNetwork.cs:105`) |
| Photon3 | clientVersion 4.1.8.13 | `ExitGames.Client.Photon/Version.cs:5` |
| Photon transport | UDP (datagram envelope + commands) | `EnetPeer.SendData`, `NCommand` |
| App version string | `{gameVersion}_2.49` | `PhotonNetwork.cs:270` |

## The one patch point (verified)

`PhotonPeer.SocketImplementationConfig` is a **public dictionary**
(`ExitGames.Client.Photon/PhotonPeer.cs:47`) mapping `ConnectionProtocol → socket Type`:

```csharp
SocketImplementationConfig[ConnectionProtocol.Udp]      = typeof(SocketUdp);   // :555
SocketImplementationConfig[ConnectionProtocol.Tcp]      = typeof(SocketTcp);   // :556
SocketImplementationConfig[ConnectionProtocol.WebSocket] = typeof(PhotonClientWebSocket);
```

At connect time, Photon3 instantiates the registered type via
`Activator.CreateInstance(SocketImplementation, peerBase)` (`PhotonPeer.cs:608`) and requires a
single-`(PeerBase)` constructor. **Therefore a custom socket needs no binary patching at all** —
`RelayPlugin` installs `typeof(RelaySocket)` under the `Udp` key via a Harmony postfix on the
`LoadBalancingPeer` constructor (`LoadBalancingClient.cs:320` is the only `new LoadBalancingPeer`
site in Realtime, which also covers the Photon Voice peer).

Note: `PhotonPeer.Initialize` does **not exist** in this build — do not target it.

## IPhotonSocket surface (contract for RelaySocket)

`IPhotonSocket` is an **abstract class** (not an interface), `ExitGames.Client.Photon/IPhotonSocket.cs`:

| Member | Kind | Notes for implementers |
|---|---|---|
| `ctor(PeerBase)` | required | stores `peerBase`, copies `ServerAddress`; call `base.Connect()` logic via override |
| `bool Connect()` | override | must set `State = Connecting`, then `Connected` + `peerBase.OnConnect()` when ready |
| `bool Disconnect()` | override | set `Disconnecting` → `Disconnected` |
| `PhotonSocketError Send(byte[], int)` | override | called on Photon's send path, any thread |
| `PhotonSocketError Receive(out byte[])` | override | **pull model** — `PollReceive=false` for thread-driven receive |
| `void HandleReceivedDatagram(byte[], int, bool willBeReused)` | inherited helper | THE injection point: routes into `peerBase.ReceiveIncomingCommands` (applies network simulation + traffic recording) |
| `PollReceive`, `State`, `MTU`, `ServerAddress/Port`, `EnqueueDebugReturn`, `HandleException(StatusCode)` | inherited | mirror `SocketUdp`'s usage exactly |

**Threading model (from `SocketUdp` and `SupportClass`):** receive is thread-driven, not polled —
`SocketUdp` spawns a background `ReceiveLoop` thread that polls the OS socket (5 ms) and calls
`HandleReceivedDatagram(buffer, length, willBeReused: true)` per datagram
(`SocketUdp.cs:153-215`). `Receive(out _)` must return `NoData` when nothing is queued
(`SocketUdp.Receive` is literally `data = null; return NoData;` because it never pulls).
`RelaySocket` clones this structure 1:1 (`PeakRelay.Client/RelaySocket.cs`).

**Reference pattern:** `SocketUdp.cs` — non-blocking UDP `Socket`, `Connect()` spawns
`DnsAndConnect` thread, which resolves DNS, connects, sets state, calls `peerBase.OnConnect()`,
then starts the receive thread. Error handling via `HandleException(StatusCode.*)`.

## Wire layout (decoded, for the observation layer)

Datagram envelope (`EnetPeer.SendData`, big-endian), 12 bytes:

```
[int16 peerID][u8 flagByte: 0=plain, 1=encrypted, 204=CRC][u8 commandCount]
[int32 serverSentTime][int32 challenge]   (+ [int32 crc] if CRC enabled)
```

Commands (`NCommand.SerializeHeader`/`Initialize`): header is
`[u8 type][u8 channel][u8 flags][u8 reserved][int32 size][int32 reliableSeq]` (12 bytes),
then per type: `7/11` add `int32 unreliableSeq` (16B header); `8/15` (fragments) add
`startSeq, fragmentCount, fragmentNumber, totalLength, fragmentOffset` (32B header);
`1/16` (ACK) add `ackReceivedSeq, ackSentTime` (20B total); `3` (VerifyConnect) carries
peerID and a 30-byte tail; `2` (Connect) is 44 bytes with MTU at offset +2 and channel count
at +11 of its payload.

Command types: 1=ACK, 2=Connect, 3=VerifyConnect, 4=Disconnect, 5=Ping, 6=SendReliable,
7=SendUnreliable, 8=SendFragment, 11=SendUnsequenced, 12=ServerTime, 13=UnreliableProcessed,
14=ReliableUnsequenced, 15=SendFragmentUnsequenced, 16=AckUnsequenced. Flags: bit0=reliable,
bit1=unsequenced.

Payloads inside `6/7/8` are **Protocol16**-encoded LoadBalancing operations/events. Op-code
names: see `references/PhotonRealtime-src/Photon.Realtime/OperationCode.cs`
(Authenticate=230, CreateGame=227, JoinGame=226, RaiseEvent=253, SetProperties=252, …).
Protocol16 itself is publicly documented (Photon "Binary Protocol" reference) — we reuse the
client's own encoder/decoder and never re-encode these payloads.

## Key architectural consequence

Photon3's **reliability/ACK layer sits above the socket** (ACKs, resends, sequencing are
commands generated in `EnetPeer`, routed through `peerBase`). The socket is a dumb datagram
pipe. So `RelaySocket` can carry PUN's own datagrams opaquely over our relay protocol with
PUN2 semantics fully intact — no reliability logic of ours, no Protocol16 handling of ours.

## What M1 must build on top (from this pin-down)

1. `RelaySocket.Send` → wrap datagram in relay envelope → relay; relay assigns/echoes actor
   context per room (M0 echo fan-out already proves the transport path end to end).
2. Relay → `RelaySocket` incoming queue → `HandleReceivedDatagram` (already wired).
3. Room/actor/property semantics for real LoadBalancing emulation (op-level relay in M1;
   the trace decoder in `PeakRelay.Protocol` is the starting parser).
4. MTU: datagrams are MTU-sized (`IPhotonSocket.MTU`); our relay frame allows 16 KiB payloads,
   comfortably above Photon's 1200-byte default MTU.
