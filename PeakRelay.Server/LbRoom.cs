using System;
using System.Collections.Generic;
using PeakRelay.Protocol;

namespace PeakRelay.Server;

/// <summary>
/// One LoadBalancing room on the relay: actor registry, property store, event routing.
/// Auth is passthrough: the relay never inspects or alters Steam/Epic tickets.
/// </summary>
public sealed class LbRoom
{
    private const byte EvJoin = 255;
    private const byte EvLeave = 254;
    private const byte EvProps = 253;

    /// <summary>Master-client key INSIDE the room-properties hashtable (GamePropertyKey.MasterClientId).</summary>
    private const byte KeyMasterClientIdInProps = 248;

    public string Name { get; }

    private readonly object _sync = new();

    /// <summary>Actors by number; 1-based, numbers reused as gaps close.</summary>
    public SortedDictionary<int, LbPeer> Actors { get; } = new();

    public int MaxPlayers { get; set; } = 4;

    public bool IsOpen { get; set; } = true;

    /// <summary>Room (game) properties, stored opaquely.</summary>
    public Hashtable Properties { get; } = new();

    public int ActorCount => Actors.Count;

    public LbRoom(string name) => Name = name;

    public int NextActorNumber()
    {
        int n = 1;
        while (Actors.ContainsKey(n))
            n++;
        return n;
    }

    public int MasterClientNumber
    {
        get
        {
            foreach (var key in Actors.Keys)
                return key;
            return 0;
        }
    }

    /// <summary>
    /// Game properties snapshot as the game-entry response must carry them (param 248):
    /// custom props plus the master-client key (Realtime's RoomInfo caches key 248 as
    /// MasterClientId — LoadBalancingClient.GameEnteredOnGameServer → ReadoutProperties).
    /// </summary>
    public Hashtable SnapshotGameProperties()
    {
        var props = new Hashtable();
        foreach (var (key, value) in Properties)
            props[key] = value;
        props[KeyMasterClientIdInProps] = MasterClientNumber;
        return props;
    }

    /// <summary>
    /// Join event for one joiner: {254: joinerActorNr, 249: joinerProps, 252: actorList}.
    /// Sent to every actor including the joiner (LoadBalancingClient.cs case 255 reads it
    /// as the props of the player identified by [254]).
    /// </summary>
    public byte[] BuildJoinEvent(LbPeer joiner)
    {
        var actors = new int[Actors.Count];
        int i = 0;
        foreach (var key in Actors.Keys)
            actors[i++] = key;

        var joinerProps = new Hashtable();
        foreach (var (key, value) in joiner.PlayerProperties)
            joinerProps[key] = value;

        var ev = new LbMessage
        {
            Code = EvJoin,
            IsEvent = true,
            Parameters = new Dictionary<byte, object>
            {
                [LbParam.ActorNr] = joiner.ActorNumber,
                [LbParam.PlayerProperties] = joinerProps,
                [LbParam.ActorList] = actors,
            },
        };
        return P16.EncodeEvent(ev);
    }

    public byte[] BuildLeaveEvent(LbPeer leaver)
    {
        var ev = new LbMessage
        {
            Code = EvLeave,
            IsEvent = true,
            Parameters = new Dictionary<byte, object>
            {
                [LbParam.ActorNr] = leaver.ActorNumber,
                [LbParam.MasterClientId] = MasterClientNumber,
            },
        };
        return P16.EncodeEvent(ev);
    }

    /// <summary>Room-properties-changed event: {253: 0, 251: changedProps} (LoadBalancingClient.cs:1987).</summary>
    public byte[] BuildPropertiesChangedEvent(Hashtable changed)
    {
        var parameters = new Dictionary<byte, object>
        {
            [253] = 0,
            [LbParam.Properties] = changed,
        };
        return P16.EncodeEvent(new LbMessage { Code = EvProps, IsEvent = true, Parameters = parameters });
    }

    /// <summary>Broadcast raw event bytes to every actor (source sees it too when source==null).</summary>
    public void BroadcastEvent(byte[] eventBytes, LbPeer? source)
    {
        foreach (var actor in Actors.Values)
        {
            if (source != null && actor.ActorNumber == source.ActorNumber)
                continue;
            actor.EnqueueLbEvent(eventBytes);
        }
    }

    // ------------------------------------------------------------------ event cache

    /// <summary>
    /// Photon Cloud's room event cache (OpRaiseEvent param 247): events raised with
    /// AddToRoomCache(Global) are stored by the server and replayed to every later joiner.
    /// PUN2's PhotonNetwork.Instantiate depends on this — without replay a joiner spawns
    /// into a world without any of the host's networked objects (frozen/dead character).
    /// We store the raw P16 event bytes exactly as raised; on join they are re-sent as-is
    /// (the sender actorNr inside still points at the original actor, which is what the
    /// client's PhotonView ownership logic expects).
    /// </summary>
    private sealed record CachedEvent(int SenderActorNr, byte EventCode, byte[] Bytes);

    private readonly List<CachedEvent> _eventCache = new();

    public int CacheCount { get { lock (_sync) return _eventCache.Count; } }

    /// <summary>Store an already-encoded event in the room cache.</summary>
    public void CacheEvent(int senderActorNr, byte eventCode, byte[] eventBytes)
    {
        lock (_sync)
            _eventCache.Add(new CachedEvent(senderActorNr, eventCode, eventBytes));
    }

    /// <summary>Replays every cached event to one actor (Photon Cloud join semantics).</summary>
    public void ReplayCacheTo(LbPeer joiner)
    {
        List<CachedEvent> snapshot;
        lock (_sync)
            snapshot = new List<CachedEvent>(_eventCache);
        foreach (var entry in snapshot)
            joiner.EnqueueLbEvent(entry.Bytes);
    }

    /// <summary>
    /// RemoveFromRoomCache (cache op 6): drop cached events raised by
    /// <paramref name="senderActorNr"/> with the given event code (0 = any code).
    /// </summary>
    public void RemoveCachedEvents(int senderActorNr, byte eventCode)
    {
        lock (_sync)
            _eventCache.RemoveAll(e => e.SenderActorNr == senderActorNr &&
                                       (eventCode == 0 || e.EventCode == eventCode));
    }
}
