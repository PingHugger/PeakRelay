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

    private const byte KeyMasterClientId = 203;

    public string Name { get; }

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

    /// <summary>Game properties snapshot including the MasterClientId key.</summary>
    public Hashtable SnapshotGameProperties()
    {
        var props = new Hashtable();
        foreach (var (key, value) in Properties)
            props[key] = value;
        props[KeyMasterClientId] = MasterClientNumber;
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
}
