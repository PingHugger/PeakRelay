using System;
using System.Collections.Generic;
using PeakRelay.Protocol;

namespace PeakRelay.Server;

/// <summary>Immutable copy of a room's public state for HTTP consumers.</summary>
public sealed record RoomSnapshot(string Name, int ActorCount, int MaxPlayers, Hashtable Properties);

/// <summary>
/// LoadBalancing operation dispatch across the master/game role split the Realtime client
/// drives (LoadBalancingClient.cs:1518+: on the game server the client re-sends the cached
/// room entry op; responses there carry 254/252/249/248 and trigger OnJoinedRoom).
///
/// master role: CreateGame/JoinGame → Ok {230: redirect-address} (no room state touched)
/// game role:   CreateGame/JoinGame → Ok {255: name, 254: actorNr, 252: actorList, 249: props}
///              + join event {255: code, 254: joiner, 249: joinerProps, 252: actorList}
///
/// Op shapes verified against LoadBalancingPeer.cs:
///   CreateGame/JoinGame request: {255: name, 249: playerProps?, 248: gameProps?, 215: joinMode?}
///   RaiseEvent: {244: code, 245: content, 252: targetActors?, 246: receivers?, 247: cache?}
///   SetProperties room: {251: props, 250: true} · player: {254: actorNr, 251: props}
///   GetProperties: {250: mask 1=room 2=actor, 251: keys?} → {248: room, 249: actor}
/// </summary>
public sealed class LbDispatcher
{
    private readonly object _sync = new();
    private readonly Dictionary<string, LbRoom> _rooms = new(StringComparer.Ordinal);

    private readonly string _redirectAddress;

    public LbDispatcher(string redirectAddress)
    {
        _redirectAddress = redirectAddress;
    }

    public int RoomCount { get { lock (_sync) return _rooms.Count; } }

    /// <summary>Room summary for the HTTP sidecar.</summary>
    public List<(string Name, int Players, int Max)> SnapshotRooms()
    {
        lock (_sync)
        {
            var result = new List<(string, int, int)>(_rooms.Count);
            foreach (var room in _rooms.Values)
                result.Add((room.Name, room.ActorCount, room.MaxPlayers));
            return result;
        }
    }

    /// <summary>
    /// Room state for the server directory: name, capacity and a copy of the room's
    /// custom properties (server-browser metadata lives there, keys N/M/P).
    /// </summary>
    public List<RoomSnapshot> SnapshotRoomStates()
    {
        lock (_sync)
        {
            var result = new List<RoomSnapshot>(_rooms.Count);
            foreach (var room in _rooms.Values)
            {
                var props = new Hashtable();
                foreach (var (key, value) in room.Properties)
                    props[key] = value;
                result.Add(new RoomSnapshot(room.Name, room.ActorCount, room.MaxPlayers, props));
            }
            return result;
        }
    }

    /// <summary>Room cleanup when a TCP session dies without a Leave op.</summary>
    public void HandleSilentDisconnect(LbPeer peer) => HandleLeave(peer);

    public void Dispatch(LbPeer peer, LbRequest request, RelayServerRole role)
    {
        Diagnostics.Dump($"op {request.Op} role={role} params={request.Parameters.Count}", Array.Empty<byte>(), 0);
        switch (request.Op)
        {
            case LbOp.Authenticate:
            case 231: // AuthenticateOnce
                HandleAuthenticate(peer, request);
                break;
            case LbOp.JoinLobby:
                break; // accepted; lobby stats flow comes with the M5 directory
            case LbOp.CreateGame:
            case LbOp.JoinGame:
            case LbOp.JoinRandomGame:
                HandleJoinLike(peer, request, role);
                break;
            case LbOp.Leave:
                HandleLeave(peer);
                SendResponse(peer, LbOp.Leave, LbError.Ok);
                break;
            case LbOp.RaiseEvent:
                HandleRaiseEvent(peer, request);
                break;
            case LbOp.SetProperties:
                HandleSetProperties(peer, request);
                break;
            case 248: // ChangeGroups (Photon Voice interest management): accepted, no state
                SendResponse(peer, 248, LbError.Ok);
                break;
            case LbOp.GetProperties:
                HandleGetProperties(peer, request);
                break;
            default:
                SendResponse(peer, request.Op, LbError.InvalidOperation, $"relay: unsupported op {request.Op}");
                break;
        }
    }

    private static void SendResponse(LbPeer peer, byte op, short returnCode, string? debug = null,
        Dictionary<byte, object>? parameters = null)
    {
        peer.EnqueueResponse(P16.EncodeResponse(new LbMessage
        {
            Code = op,
            ReturnCode = returnCode,
            DebugMessage = debug,
            Parameters = parameters ?? new Dictionary<byte, object>(),
        }));
    }

    private void HandleAuthenticate(LbPeer peer, LbRequest request)
    {
        peer.Authenticated = true;
        if (request.Parameters.TryGetValue(LbParam.UserId, out var userId) && userId is string uid)
            peer.UserId = uid;
        if (request.Parameters.TryGetValue(LbParam.AppVersion, out var gv) && gv is string version)
            peer.GameVersion = version;

        // A token-carrying auth is the game-server reconnect (OpAuthenticate sends ONLY {221}
        // when a token is cached): switch this connection to the Game role.
        if (request.Parameters.ContainsKey(LbParam.Token))
            peer.Role = RelayServerRole.Game;

        // token (221) is what the client caches and re-sends as its game-server auth
        SendResponse(peer, request.Op, LbError.Ok, parameters: new Dictionary<byte, object>
        {
            [LbParam.Token] = Array.Empty<byte>(),
        });
    }

    private void HandleJoinLike(LbPeer peer, LbRequest request, RelayServerRole role)
    {
        if (role == RelayServerRole.Master)
        {
            // hand the client back to itself: reconnect target == this relay
            SendResponse(peer, request.Op, LbError.Ok, parameters: new Dictionary<byte, object>
            {
                [LbParam.Address] = _redirectAddress,
            });
            return;
        }

        string? roomName = null;
        if (request.Parameters.TryGetValue(LbParam.RoomName, out var name) && name is string s && s.Length > 0)
            roomName = s;

        bool create = request.Op == LbOp.CreateGame;
        bool random = request.Op == LbOp.JoinRandomGame;
        bool createIfMissing =
            create || (request.Parameters.TryGetValue(215, out var jm) && jm is byte b && b == 1);

        lock (_sync)
        {
            if (roomName == null && random)
            {
                foreach (var candidate in _rooms.Values)
                {
                    if (candidate.IsOpen && candidate.ActorCount < candidate.MaxPlayers)
                    {
                        roomName = candidate.Name;
                        break;
                    }
                }
                if (roomName == null)
                {
                    SendResponse(peer, request.Op, LbError.NoRandomMatchFound, "no open room");
                    return;
                }
            }

            if (roomName == null)
            {
                SendResponse(peer, request.Op, LbError.GameDoesNotExist, "missing room name");
                return;
            }

            if (!_rooms.TryGetValue(roomName, out var room))
            {
                if (!createIfMissing)
                {
                    SendResponse(peer, request.Op, LbError.GameDoesNotExist, "room not found");
                    return;
                }
                room = new LbRoom(roomName);
                _rooms[roomName] = room;
            }
            else if (create)
            {
                SendResponse(peer, request.Op, LbError.GameIdAlreadyExists, "room exists");
                return;
            }

            if (room.ActorCount >= room.MaxPlayers)
            {
                SendResponse(peer, request.Op, LbError.GameFull, "room full");
                return;
            }

            // seed game properties on create
            if (request.Parameters.TryGetValue(LbParam.GameProperties, out var gp) && gp is Hashtable props &&
                room.ActorCount == 0)
            {
                foreach (var (key, value) in props)
                    room.Properties[key] = value;
            }

            // MaxPlayers rides inside the game-properties hashtable: key 243 (int) and key
            // 255 (byte) — LoadBalancingPeer.cs:111-112 — not as a top-level op parameter.
            if (room.ActorCount == 0 && gp is Hashtable gpTable)
            {
                if (gpTable.ContainsKey((byte)243) && gpTable[(byte)243] is int max243)
                    room.MaxPlayers = max243;
                else if (gpTable.ContainsKey((byte)byte.MaxValue) &&
                         gpTable[(byte)byte.MaxValue] is byte max255)
                    room.MaxPlayers = max255;
            }

            // admit the actor
            peer.ActorNumber = room.NextActorNumber();
            room.Actors[peer.ActorNumber] = peer;
            peer.Room = room;

            if (request.Parameters.TryGetValue(LbParam.PlayerProperties, out var pp) && pp is Hashtable playerProps)
            {
                foreach (var (key, value) in playerProps)
                    peer.PlayerProperties[key] = value;
            }

            // game-server entry response: {255: name, 254: actorNr, 252: actorList, 249: joiner
            // props, 248: room props} — Realtime's GameEnteredOnGameServer reads 249/248 and
            // feeds ReadoutProperties, so joiners learn custom room metadata (N/M/P) at join.
            var actorList = new int[room.Actors.Count];
            int i = 0;
            foreach (var key in room.Actors.Keys)
                actorList[i++] = key;
            SendResponse(peer, request.Op, LbError.Ok, parameters: new Dictionary<byte, object>
            {
                [LbParam.RoomName] = roomName,
                [LbParam.ActorNr] = peer.ActorNumber,
                [LbParam.ActorList] = actorList,
                [LbParam.PlayerProperties] = CopyOf(peer.PlayerProperties),
                [LbParam.GameProperties] = room.SnapshotGameProperties(),
            });

            // join event to everyone (the joiner caches room state and fires OnJoinedRoom from it)
            room.BroadcastEvent(room.BuildJoinEvent(peer), null);
        }
    }

    private static Hashtable CopyOf(Hashtable source)
    {
        var copy = new Hashtable(source.Count);
        foreach (var (key, value) in source)
            copy[key] = value;
        return copy;
    }

    private void HandleLeave(LbPeer peer)
    {
        var room = peer.Room;
        if (room == null)
            return;
        lock (_sync)
        {
            room.Actors.Remove(peer.ActorNumber);
            if (room.ActorCount == 0)
                _rooms.Remove(room.Name);
        }
        if (room.ActorCount > 0)
            room.BroadcastEvent(room.BuildLeaveEvent(peer), null);
        peer.Room = null;
        peer.ActorNumber = 0;
    }

    private void HandleRaiseEvent(LbPeer peer, LbRequest request)
    {
        var room = peer.Room;
        if (room == null)
        {
            SendResponse(peer, LbOp.RaiseEvent, LbError.InvalidOperation, "not in a room");
            return;
        }

        byte eventCode = request.Parameters.TryGetValue(LbParam.Code, out var codeObj) && codeObj is byte b ? b : (byte)0;
        var content = request.Parameters.GetValueOrDefault(LbParam.CustomEventContent);

        byte[] BuildEvent() => P16.EncodeEvent(new LbMessage
        {
            Code = eventCode,
            IsEvent = true,
            Parameters = new Dictionary<byte, object>
            {
                [LbParam.Code] = eventCode,
                [LbParam.Data] = content!,
                [LbParam.ActorNr] = peer.ActorNumber, // Sender derives from param 254
            },
        });

        if (request.Parameters.TryGetValue(LbParam.ActorList, out var targets))
        {
            switch (targets)
            {
                case int one:
                    if (room.Actors.TryGetValue(one, out var t1))
                        t1.EnqueueLbEvent(BuildEvent());
                    return;
                case int[] many:
                    foreach (var t in many)
                        if (room.Actors.TryGetValue(t, out var target))
                            target.EnqueueLbEvent(BuildEvent());
                    return;
            }
        }

        if (request.Parameters.TryGetValue(LbParam.ReceiverGroup, out var rg) && rg is byte receivers)
        {
            switch (receivers)
            {
                case 1: // MasterClient only
                    var masterNr = room.MasterClientNumber;
                    if (room.Actors.TryGetValue(masterNr, out var master))
                        master.EnqueueLbEvent(BuildEvent());
                    return;
                case 2: // Others
                    room.BroadcastEvent(BuildEvent(), peer);
                    return;
            }
        }

        room.BroadcastEvent(BuildEvent(), peer); // Everyone except source
    }

    private void HandleSetProperties(LbPeer peer, LbRequest request)
    {
        var room = peer.Room;
        if (room == null)
        {
            SendResponse(peer, LbOp.SetProperties, LbError.InvalidOperation, "not in a room");
            return;
        }

        var props = request.Parameters.TryGetValue(LbParam.Properties, out var p) && p is Hashtable h ? h : null;
        if (props == null)
        {
            SendResponse(peer, LbOp.SetProperties, LbError.InvalidOperation, "no properties");
            return;
        }

        if (request.Parameters.TryGetValue(LbParam.ActorNr, out var actorNr) && actorNr is int target && target != 0)
        {
            if (target != peer.ActorNumber)
            {
                SendResponse(peer, LbOp.SetProperties, LbError.InvalidOperation, "cannot set other actors' props");
                return;
            }
            foreach (var (key, value) in props)
                peer.PlayerProperties[key] = value;
            BroadcastPlayerProps(room, peer);
        }
        else
        {
            foreach (var (key, value) in props)
                room.Properties[key] = value;
            room.BroadcastEvent(room.BuildPropertiesChangedEvent(props), null);
        }
        SendResponse(peer, LbOp.SetProperties, LbError.Ok);
    }

    private void BroadcastPlayerProps(LbRoom room, LbPeer peer)
    {
        var snapshot = new Hashtable();
        foreach (var (key, value) in peer.PlayerProperties)
            snapshot[key] = value;
        var parameters = new Dictionary<byte, object>
        {
            [253] = peer.ActorNumber,        // targetActorNr ≠ 0 → actor-properties branch
            [LbParam.Properties] = snapshot, // 251
        };
        room.BroadcastEvent(P16.EncodeEvent(new LbMessage { Code = 253, IsEvent = true, Parameters = parameters }), null);
    }

    private void HandleGetProperties(LbPeer peer, LbRequest request)
    {
        var room = peer.Room;
        if (room == null)
        {
            SendResponse(peer, LbOp.GetProperties, LbError.InvalidOperation, "not in a room");
            return;
        }

        byte mask = request.Parameters.TryGetValue(LbParam.Broadcast, out var m) && m is byte b ? b : (byte)0;
        var response = new Dictionary<byte, object>();

        if ((mask & 1) != 0)
            response[LbParam.GameProperties] = room.SnapshotGameProperties();
        if ((mask & 2) != 0)
        {
            var actorProps = new Hashtable();
            foreach (var actor in room.Actors.Values)
            {
                var copy = new Hashtable();
                foreach (var (key, value) in actor.PlayerProperties)
                    copy[key] = value;
                actorProps[actor.ActorNumber] = copy;
            }
            response[LbParam.PlayerProperties] = actorProps;
        }
        SendResponse(peer, LbOp.GetProperties, LbError.Ok, parameters: response);
    }
}
