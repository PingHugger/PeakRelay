using System;
using System.Linq;
using System.Text;
using PeakRelay.Protocol;
using PeakRelay.Tools.TestClient;
using Photon.Realtime;
using Xunit;
using static PeakRelay.Conformance.Tests.RelayConformanceFixture;
using SendOptions = ExitGames.Client.Photon.SendOptions;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace PeakRelay.Conformance.Tests;

/// <summary>
/// The late-join desync incident (v0.7.13) shipped with 134 green unit tests — only real
/// Photon3 clients caught the broken transports. These scenarios run the genuine
/// LoadBalancingClient stack against the genuine relay, in-proc, so the class of bug that
/// reached players is a red build instead.
///
/// Every scenario here is a regression lock for one wire behavior the game depends on.
/// </summary>
[Collection("RelayConformance")]
public sealed class LateJoinScenarioTests
{
    private readonly RelayConformanceFixture _fx;

    public LateJoinScenarioTests(RelayConformanceFixture fx) => _fx = fx;

    [Fact]
    public void Three_players_late_joiner_sees_full_actor_list_and_props()
    {
        var host = _fx.NewClient("cf-host");
        Assert.True(host.Connect() && host.WaitForConnected(RelayConformanceFixture.TimeoutMs), $"host connect: {host.LastError}");
        WaitUntilState(host, ClientState.ConnectedToMasterServer);
        Assert.True(host.OpCreateRoom(new EnterRoomParams
        {
            RoomName = "cf-late",
            RoomOptions = new RoomOptions { MaxPlayers = 4, IsVisible = true, IsOpen = true },
        }), "host CreateRoom failed");

        var first = _fx.NewClient("cf-first");
        Assert.True(first.Connect() && first.WaitForConnected(RelayConformanceFixture.TimeoutMs));
        WaitUntilState(first, ClientState.ConnectedToMasterServer);
        Assert.True(first.OpJoinRoom(new EnterRoomParams { RoomName = "cf-late" }));

        WaitUntilInRoom(host, "host");
        WaitUntilInRoom(first, "first");

        var late = _fx.NewClient("cf-late-joiner");
        Assert.True(late.Connect() && late.WaitForConnected(RelayConformanceFixture.TimeoutMs));
        WaitUntilState(late, ClientState.ConnectedToMasterServer);
        Assert.True(late.OpJoinRoom(new EnterRoomParams { RoomName = "cf-late" }));
        WaitUntilInRoom(late, "late");

        // every client must end with all three actors known with identities (the identity
        // contract: 225 auth echo + byte-keyed 253 per actor)
        foreach (var (client, name) in new[] { (host, "host"), (first, "first"), (late, "late") })
        {
            var players = client.CurrentRoom!.Players.Values.Select(p => p.UserId).ToHashSet();
            Assert.True(players.IsSupersetOf(new[] { "cf-host", "cf-first", "cf-late-joiner" }),
                $"{name} sees actors: {string.Join(",", client.CurrentRoom.Players.Values.Select(p => p.UserId))}");
        }
    }

    [Fact]
    public void Late_joiner_receives_cached_events_raised_before_the_join()
    {
        var host = _fx.NewClient("cache-host");
        Assert.True(host.Connect() && host.WaitForConnected(RelayConformanceFixture.TimeoutMs));
        WaitUntilState(host, ClientState.ConnectedToMasterServer);
        Assert.True(host.OpCreateRoom(new EnterRoomParams
        {
            RoomName = "cf-cache",
            RoomOptions = new RoomOptions { MaxPlayers = 4 },
        }));
        WaitUntilInRoom(host, "host");

        // raise a cached event BEFORE any joiner exists (op 4 room cache, like PUN RPCs)
        var content = new Hashtable { [(byte)1] = "pre-join-object" };
        Assert.True(host.OpRaiseEvent(200, content, new RaiseEventOptions
        {
            CachingOption = EventCaching.AddToRoomCache,
        }, SendOptions.SendReliable));

        var joiner = _fx.NewClient("cache-joiner");
        Assert.True(joiner.Connect() && joiner.WaitForConnected(RelayConformanceFixture.TimeoutMs));
        WaitUntilState(joiner, ClientState.ConnectedToMasterServer);
        Assert.True(joiner.OpJoinRoom(new EnterRoomParams { RoomName = "cf-cache" }));
        WaitUntilInRoom(joiner, "joiner");

        var codes = joiner.Events.Select(e => e.Code).ToList();
        Assert.Contains((byte)200, codes);
        var cached = joiner.Events.First(e => e.Code == 200);
        Assert.Equal("pre-join-object",
            (cached.Parameters[LbParam.CustomEventContent] as Hashtable)?[(byte)1]);
    }

    [Fact]
    public void Oversized_cached_event_survives_fragmentation()
    {
        var host = _fx.NewClient("frag-host");
        Assert.True(host.Connect() && host.WaitForConnected(RelayConformanceFixture.TimeoutMs));
        WaitUntilState(host, ClientState.ConnectedToMasterServer);
        Assert.True(host.OpCreateRoom(new EnterRoomParams
        {
            RoomName = "cf-frag",
            RoomOptions = new RoomOptions { MaxPlayers = 4 },
        }));
        WaitUntilInRoom(host, "host");

        // >MTU payload: Photon fragments this into type-8 commands — the v0.7.13 bug class.
        // Raising it CACHED (op 5, PUN Instantiate's op) exercises the full path:
        // fragmented raise -> store -> replay to a late joiner.
        var big = new byte[6000];
        new Random(7).NextBytes(big);
        var content = new Hashtable { [(byte)1] = big };
        Assert.True(host.OpRaiseEvent(201, content, new RaiseEventOptions
        {
            CachingOption = EventCaching.AddToRoomCacheGlobal,
        }, SendOptions.SendReliable));

        var joiner = _fx.NewClient("frag-joiner");
        Assert.True(joiner.Connect() && joiner.WaitForConnected(RelayConformanceFixture.TimeoutMs));
        WaitUntilState(joiner, ClientState.ConnectedToMasterServer);
        Assert.True(joiner.OpJoinRoom(new EnterRoomParams { RoomName = "cf-frag" }));
        WaitUntilInRoom(joiner, "joiner");

        var raised = joiner.Events.FirstOrDefault(e => e.Code == 201);
        Assert.NotNull(raised);
        var roundTripped = (raised!.Parameters[LbParam.CustomEventContent] as Hashtable)?[(byte)1] as byte[];
        Assert.NotNull(roundTripped);
        Assert.Equal(big, roundTripped);
    }

    [Fact]
    public void Unreliable_events_reach_the_room()
    {
        var host = _fx.NewClient("unrel-host");
        Assert.True(host.Connect() && host.WaitForConnected(RelayConformanceFixture.TimeoutMs));
        WaitUntilState(host, ClientState.ConnectedToMasterServer);
        Assert.True(host.OpCreateRoom(new EnterRoomParams
        {
            RoomName = "cf-unrel",
            RoomOptions = new RoomOptions { MaxPlayers = 4 },
        }));

        var peer = _fx.NewClient("unrel-peer");
        Assert.True(peer.Connect() && peer.WaitForConnected(RelayConformanceFixture.TimeoutMs));
        WaitUntilState(peer, ClientState.ConnectedToMasterServer);
        Assert.True(peer.OpJoinRoom(new EnterRoomParams { RoomName = "cf-unrel" }));

        WaitUntilInRoom(host, "host");
        WaitUntilInRoom(peer, "peer");

        // movement-style: unreliable raise; several attempts since the network may drop
        var before = host.Events.Count(e => e.Code == 205);
        for (var attempt = 0; attempt < 10 && host.Events.Count(e => e.Code == 205) == before; attempt++)
        {
            Assert.True(peer.OpRaiseEvent(205, new Hashtable { [(byte)1] = attempt },
                new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendUnreliable));
            Assert.True(RelayConformanceFixture.WaitUntil(
                () => host.Events.Count(e => e.Code == 205) > before, 2000));
        }
        Assert.True(host.Events.Count(e => e.Code == 205) > before,
            "unreliable event never arrived — the serialize stream would be dead");
    }

    [Fact]
    public void Global_cache_survives_the_creating_actor_leaving()
    {
        var host = _fx.NewClient("global-host");
        Assert.True(host.Connect() && host.WaitForConnected(RelayConformanceFixture.TimeoutMs));
        WaitUntilState(host, ClientState.ConnectedToMasterServer);
        Assert.True(host.OpCreateRoom(new EnterRoomParams
        {
            RoomName = "cf-global",
            RoomOptions = new RoomOptions { MaxPlayers = 4, EmptyRoomTtl = 60000 },
        }));
        WaitUntilInRoom(host, "host");

        Assert.True(host.OpRaiseEvent(202, new Hashtable { [(byte)1] = "world" },
            new RaiseEventOptions { CachingOption = EventCaching.AddToRoomCacheGlobal },
            SendOptions.SendReliable));

        var joiner = _fx.NewClient("global-joiner");
        Assert.True(joiner.Connect() && joiner.WaitForConnected(RelayConformanceFixture.TimeoutMs));
        WaitUntilState(joiner, ClientState.ConnectedToMasterServer);
        Assert.True(joiner.OpJoinRoom(new EnterRoomParams { RoomName = "cf-global" }));
        WaitUntilInRoom(joiner, "joiner");

        host.Disconnect(); // the creator leaves
        Assert.True(RelayConformanceFixture.WaitUntil(
            () => host.State == ClientState.Disconnected, RelayConformanceFixture.TimeoutMs));

        // a fresh joiner must still see the globally cached world event
        var late = _fx.NewClient("global-late");
        Assert.True(late.Connect() && late.WaitForConnected(RelayConformanceFixture.TimeoutMs));
        WaitUntilState(late, ClientState.ConnectedToMasterServer);
        Assert.True(late.OpJoinRoom(new EnterRoomParams { RoomName = "cf-global" }));
        WaitUntilInRoom(late, "late");

        Assert.Contains((byte)202, late.Events.Select(e => e.Code));
    }

    private static void WaitUntilState(PhotonClientHarness client, ClientState state)
    {
        Assert.True(RelayConformanceFixture.WaitUntil(() => client.State == state, RelayConformanceFixture.TimeoutMs),
            $"never reached {state} (state={client.State}, error={client.LastError})");
    }
}
