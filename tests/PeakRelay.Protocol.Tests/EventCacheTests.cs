using System.Linq;
using PeakRelay.Protocol;
using PeakRelay.Server;
using Xunit;

namespace PeakRelay.Protocol.Tests;

/// <summary>
/// The room event cache is what lets late joiners see the host's Instantiates/RPCs —
/// without it a joiner spawns into an empty frozen world (PhotonView-not-found spam).
/// </summary>
public sealed class EventCacheTests
{
    private static LbPeer NewPeer(EnetPeer enet) => new() { Enet = enet };

    private static LbRequest Raise(byte evCode, byte cacheOp, object content) => new()
    {
        Op = LbOp.RaiseEvent,
        Parameters = new Dictionary<byte, object>
        {
            [LbParam.Code] = evCode,
            [LbParam.Data] = content,
            [LbParam.Cache] = cacheOp,
        },
    };

    [Fact]
    public void Cached_events_replay_to_a_late_joiner()
    {
        var dispatcher = new LbDispatcher("127.0.0.1:5055");
        var host = NewPeer(new EnetPeer());
        var joiner = NewPeer(new EnetPeer());

        // Host creates the room (game role) and raises two cached events.
        dispatcher.Dispatch(host, new LbRequest
        {
            Op = LbOp.CreateGame,
            Parameters = new Dictionary<byte, object> { [LbParam.RoomName] = "cache-room" },
        }, RelayServerRole.Game);
        Assert.Equal(1, host.ActorNumber);

        // A spectator is already in the room and must ALSO receive the cached raise
        // (Photon Cloud stores AND delivers; store-only would blind current members).
        var spectator = NewPeer(new EnetPeer());
        dispatcher.Dispatch(spectator, Join("cache-room"), RelayServerRole.Game);
        DrainEvents(spectator); // its own join event etc.

        dispatcher.Dispatch(host, Raise(200, cacheOp: 4, "instantiate-1"), RelayServerRole.Game);
        dispatcher.Dispatch(host, Raise(202, cacheOp: 5, "rpc-1"), RelayServerRole.Game);
        Assert.Equal(2, RoomCacheCount(dispatcher, "cache-room"));

        var spectatorEvents = DrainEvents(spectator).Select(e => e.Code).ToList();
        Assert.Contains((byte)200, spectatorEvents);
        Assert.Contains((byte)202, spectatorEvents);

        // The joiner gets the room-entry response, its join event, THEN the replay.
        dispatcher.Dispatch(joiner, new LbRequest
        {
            Op = LbOp.JoinGame,
            Parameters = new Dictionary<byte, object> { [LbParam.RoomName] = "cache-room" },
        }, RelayServerRole.Game);

        var events = DrainEvents(joiner);
        Assert.Equal(3, events.Count); // join(255) + 2 cached
        Assert.Equal((byte)255, events[0].Code);
        Assert.Equal((byte)200, events[1].Code);
        Assert.Equal((byte)202, events[2].Code);
        // Sender inside a replayed cached event still points at the original actor (1).
        Assert.Equal(1, events[1].Parameters[LbParam.ActorNr]);
    }

    [Fact]
    public void RemoveCache_drops_only_that_senders_events()
    {
        var dispatcher = new LbDispatcher("127.0.0.1:5055");
        var host = NewPeer(new EnetPeer());
        var other = NewPeer(new EnetPeer());

        dispatcher.Dispatch(host, Create("rc-room"), RelayServerRole.Game);
        dispatcher.Dispatch(other, Join("rc-room"), RelayServerRole.Game);

        dispatcher.Dispatch(host, Raise(200, 4, "a"), RelayServerRole.Game);
        dispatcher.Dispatch(other, Raise(200, 4, "b"), RelayServerRole.Game);
        Assert.Equal(2, RoomCacheCount(dispatcher, "rc-room"));

        // Host removes its own cached 200s; the other actor's stay.
        dispatcher.Dispatch(host, Raise(200, 6, null!), RelayServerRole.Game);
        Assert.Equal(1, RoomCacheCount(dispatcher, "rc-room"));

        var fresh = NewPeer(new EnetPeer());
        dispatcher.Dispatch(fresh, Join("rc-room"), RelayServerRole.Game);
        var events = DrainEvents(fresh);
        Assert.Equal((byte)200, events[^1].Code);
        Assert.Equal(2, events[^1].Parameters[LbParam.ActorNr]); // raised by 'other'
    }

    [Fact]
    public void Leaving_purges_the_leavers_cached_events()
    {
        var dispatcher = new LbDispatcher("127.0.0.1:5055");
        var host = NewPeer(new EnetPeer());
        var guest = NewPeer(new EnetPeer());

        dispatcher.Dispatch(host, Create("purge-room"), RelayServerRole.Game);
        dispatcher.Dispatch(guest, Join("purge-room"), RelayServerRole.Game);
        dispatcher.Dispatch(guest, Raise(210, 4, "guest-object"), RelayServerRole.Game);
        Assert.Equal(1, RoomCacheCount(dispatcher, "purge-room"));

        dispatcher.Dispatch(guest, new LbRequest { Op = LbOp.Leave, Parameters = new() }, RelayServerRole.Game);
        Assert.Equal(0, RoomCacheCount(dispatcher, "purge-room"));
    }

    private static int RoomCacheCount(LbDispatcher dispatcher, string room)
    {
        var field = typeof(LbDispatcher).GetField("_rooms",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var rooms = (Dictionary<string, LbRoom>)field.GetValue(dispatcher)!;
        return rooms[room].CacheCount;
    }

    private static List<LbMessage> DrainEvents(LbPeer peer)
    {
        var result = new List<LbMessage>();
        while (peer.LbOutbound.TryDequeue(out var entry))
        {
            if (!entry.IsEvent)
                continue;
            Assert.True(P16.TryDecodeEvent(entry.Bytes, out var message), "queued bytes decode as an event");
            result.Add(message);
        }
        return result;
    }

    private static LbRequest Create(string room) => new()
    {
        Op = LbOp.CreateGame,
        Parameters = new Dictionary<byte, object> { [LbParam.RoomName] = room },
    };

    private static LbRequest Join(string room) => new()
    {
        Op = LbOp.JoinGame,
        Parameters = new Dictionary<byte, object> { [LbParam.RoomName] = room },
    };
}
