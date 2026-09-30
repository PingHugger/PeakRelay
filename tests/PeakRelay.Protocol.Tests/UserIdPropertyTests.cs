using System.Linq;
using PeakRelay.Protocol;
using PeakRelay.Server;
using Xunit;

namespace PeakRelay.Protocol.Tests;

/// <summary>
/// The relay's player-identity contract. PEAK's host spawn flow (ReconnectHandler,
/// PlayerHandler.IsBanned, AudioLevels) and client identity flow (CharacterCustomization,
/// CharacterVoiceHandler, IsLookedAt) dictionary-lookup on Player.UserId — when the relay
/// leaves it null, the host throws ArgumentNullException every frame and remote players
/// spawn broken (stuck passed-out, empty hotbar) while the world itself stays fine.
///
/// Contract verified against the shipped assemblies (references/):
///   LoadBalancingClient auth handling:  auth response param 225 → LocalPlayer.UserId
///   GameEnteredOnGameServer → ReadoutProperties(..., 0): entry response 249 is nested
///   {actorNr(int): {props}} and EVERY actor's table feeds InternalCacheProperties, which
///   reads UserId from key 253. Key 253 must arrive BYTE-boxed on the wire: the client's
///   properties.ContainsKey(253) binds the int constant to Photon Hashtable's byte overload
///   (constant→byte conversion beats object), which matches boxed byte keys only — any
///   other encoding silently leaves Player.UserID null. Outer actorNr keys stay int-typed
///   (clients unbox '(int)key').
/// </summary>
public sealed class UserIdPropertyTests
{
    private const byte KeyUserId = LbParam.PlayerPropUserId; // byte 253

    private static LbPeer NewPeer() => new() { Enet = new EnetPeer() };

    private static void Auth(LbDispatcher dispatcher, LbPeer peer, string userId)
    {
        dispatcher.Dispatch(peer, new LbRequest
        {
            Op = LbOp.Authenticate,
            Parameters = new Dictionary<byte, object> { [LbParam.UserId] = userId },
        }, RelayServerRole.Master);
        peer.Role = RelayServerRole.Game; // real clients reach game role via token re-auth
    }

    private static LbMessage? LastResponse(LbPeer peer)
    {
        while (peer.LbOutbound.TryDequeue(out var entry))
        {
            if (entry.IsEvent)
                continue;
            Assert.True(P16.TryDecodeResponse(entry.Bytes, out var message), "queued bytes decode as a response");
            return message;
        }
        return null;
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

    [Fact]
    public void Auth_response_echoes_the_clients_user_id()
    {
        var dispatcher = new LbDispatcher("127.0.0.1:5055");
        var peer = NewPeer();

        dispatcher.Dispatch(peer, new LbRequest
        {
            Op = LbOp.Authenticate,
            Parameters = new Dictionary<byte, object> { [LbParam.UserId] = "76561199135009349" },
        }, RelayServerRole.Master);

        var response = LastResponse(peer);
        Assert.NotNull(response);
        Assert.Equal(0, response!.ReturnCode);
        Assert.Equal("76561199135009349", response.Parameters[LbParam.UserId]);
    }

    [Fact]
    public void Auth_without_user_id_assigns_a_synthetic_one()
    {
        var dispatcher = new LbDispatcher("127.0.0.1:5055");
        var peer = NewPeer();

        dispatcher.Dispatch(peer, new LbRequest
        {
            Op = LbOp.Authenticate,
            Parameters = new Dictionary<byte, object>(),
        }, RelayServerRole.Master);

        var response = LastResponse(peer);
        Assert.NotNull(response);
        var echoed = Assert.IsType<string>(response!.Parameters[LbParam.UserId]);
        Assert.StartsWith("relay-", echoed);
        Assert.Equal(echoed, peer.UserId);
    }

    [Fact]
    public void Entry_response_carries_every_actors_props_with_int_keyed_user_id()
    {
        var dispatcher = new LbDispatcher("127.0.0.1:5055");
        var host = NewPeer();
        Auth(dispatcher, host, "user-host");
        dispatcher.Dispatch(host, new LbRequest
        {
            Op = LbOp.CreateGame,
            Parameters = new Dictionary<byte, object>
            {
                [LbParam.RoomName] = "id-room",
                // what PEAK actually sends: string-keyed custom props ("UserID", cosmetics…)
                [LbParam.PlayerProperties] = new Hashtable { ["UserID"] = "76561199135009349" },
            },
        }, RelayServerRole.Game);
        LastResponse(host); // drain create response

        var joiner = NewPeer();
        dispatcher.Dispatch(joiner, new LbRequest
        {
            Op = LbOp.JoinGame,
            Parameters = new Dictionary<byte, object>
            {
                [LbParam.RoomName] = "id-room",
                [LbParam.PlayerProperties] = new Hashtable { ["UserID"] = "76561199135009350" },
            },
        }, RelayServerRole.Game);

        var response = LastResponse(joiner);
        Assert.NotNull(response);
        Assert.Equal(0, response!.ReturnCode);

        var actorProps = Assert.IsType<Hashtable>(response.Parameters[LbParam.PlayerProperties]);
        Assert.Equal(2, actorProps.Count);

        // outer actorNr keys: int-typed (clients cast '(int)key')
        Assert.All(actorProps.Keys, key => Assert.IsType<int>(key));

        // inner tables carry the UserId under the BYTE-boxed key 253
        var hostTable = Assert.IsType<Hashtable>(actorProps[1]);
        var joinerTable = Assert.IsType<Hashtable>(actorProps[2]);
        Assert.Equal("user-host", hostTable[KeyUserId]);
        Assert.Equal(joiner.UserId, joinerTable[KeyUserId]);

        // the PUN identity rides on both actors, the game's own "UserID" custom prop alongside
        Assert.Equal("76561199135009349", hostTable["UserID"]);
        Assert.Equal("76561199135009350", joinerTable["UserID"]);
    }

    [Fact]
    public void Join_event_and_late_joiner_response_carry_all_ids()
    {
        var dispatcher = new LbDispatcher("127.0.0.1:5055");
        var host = NewPeer();
        dispatcher.Dispatch(host, new LbRequest
        {
            Op = LbOp.CreateGame,
            Parameters = new Dictionary<byte, object> { [LbParam.RoomName] = "join-id-room" },
        }, RelayServerRole.Game);
        LastResponse(host);

        var joiner = NewPeer();
        dispatcher.Dispatch(joiner, new LbRequest
        {
            Op = LbOp.JoinGame,
            Parameters = new Dictionary<byte, object> { [LbParam.RoomName] = "join-id-room" },
        }, RelayServerRole.Game);

        // the HOST must receive a join event whose 249 table carries the JOINER's 253 id.
        // (Two 255 events arrive: the host's own join event from create, then the joiner's —
        // each client's own join event is what fires OnJoinedRoom, matching Photon Cloud.)
        var hostEvents = DrainEvents(host);
        var joinEvent = hostEvents.Last(e => e.Code == 255);
        Assert.Equal(joiner.ActorNumber, joinEvent.Parameters[LbParam.ActorNr]);
        var joinerProps = Assert.IsType<Hashtable>(joinEvent.Parameters[LbParam.PlayerProperties]);
        Assert.Equal(joiner.UserId, joinerProps[KeyUserId]);

        // the JOINER's entry response covers every actor (host included)
        var joinerResponse = LastResponse(joiner);
        Assert.NotNull(joinerResponse);
        var allProps = Assert.IsType<Hashtable>(joinerResponse!.Parameters[LbParam.PlayerProperties]);
        var hostEntry = Assert.IsType<Hashtable>(allProps[1]);
        var joinerEntry = Assert.IsType<Hashtable>(allProps[2]);
        Assert.Equal(2, allProps.Count);
        Assert.Equal(host.UserId, hostEntry[KeyUserId]);
        Assert.Equal(joiner.UserId, joinerEntry[KeyUserId]);
    }

    [Fact]
    public void Key_253_must_be_byte_boxed_for_the_client_lookup()
    {
        // documents WHY the wire type matters: the client's ContainsKey(253) binds to the
        // byte overload (constant conversion beats object), so the id is found only under a
        // byte-boxed key. byte-boxed and int-boxed 253 are DIFFERENT entries in a
        // Dictionary<object, object> — our P16 byte encoding lands byte-boxed after decode.
        object byteBoxed = (byte)253;
        object intBoxed = 253;
        var table = new Hashtable { [byteBoxed] = "the-id" };
        Assert.True(table.ContainsKey(byteBoxed));
        Assert.False(table.ContainsKey(intBoxed));
    }

    [Fact]
    public void GetProperties_returns_user_ids_for_every_actor()
    {
        var dispatcher = new LbDispatcher("127.0.0.1:5055");
        var host = NewPeer();
        dispatcher.Dispatch(host, new LbRequest
        {
            Op = LbOp.CreateGame,
            Parameters = new Dictionary<byte, object> { [LbParam.RoomName] = "getprops-room" },
        }, RelayServerRole.Game);
        LastResponse(host);

        var joiner = NewPeer();
        dispatcher.Dispatch(joiner, new LbRequest
        {
            Op = LbOp.JoinGame,
            Parameters = new Dictionary<byte, object> { [LbParam.RoomName] = "getprops-room" },
        }, RelayServerRole.Game);
        LastResponse(joiner);

        dispatcher.Dispatch(host, new LbRequest
        {
            Op = LbOp.GetProperties,
            Parameters = new Dictionary<byte, object> { [LbParam.Broadcast] = (byte)2 }, // actor props
        }, RelayServerRole.Game);

        var response = LastResponse(host);
        Assert.NotNull(response);
        var actorProps = Assert.IsType<Hashtable>(response!.Parameters[LbParam.PlayerProperties]);
        var hostEntry = Assert.IsType<Hashtable>(actorProps[1]);
        var joinerEntry = Assert.IsType<Hashtable>(actorProps[2]);
        Assert.Equal(host.UserId, hostEntry[KeyUserId]);
        Assert.Equal(joiner.UserId, joinerEntry[KeyUserId]);
    }
}
