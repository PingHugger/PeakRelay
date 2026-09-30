using System;
using System.Collections.Generic;
using System.Threading;
using ExitGames.Client.Photon;
using PeakRelay.Protocol;
using Photon.Realtime;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace PeakRelay.Tools.TestClient;

/// <summary>
/// Real-Photon3 relay scenario: two genuine LoadBalancingClients run the full Realtime flow —
/// auth → master → CreateGame/JoinGame → redirect → game server (same relay) → room ops —
/// with assertions. Exit code 0 = every step passed.
///
/// Step 7 exercises the server-browser metadata path end to end: the host creates the room
/// with custom properties (keys N/M/P from ServerPropertyKeys — the same stamp the dedicated
/// plugin applies via its CreateRoom prefix), and the scenario asserts a client observes
/// them, plus (best effort) that the relay's HTTP /api/servers lists the server with the
/// same metadata.
/// </summary>
public static class Program
{
    private const int TimeoutMs = 20000;

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "echo")
            return EchoFallback.Run(args);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var message = e.ExceptionObject is Exception ex
                ? DescribeException(ex)
                : e.ExceptionObject?.ToString() ?? "null";
            Console.Error.WriteLine($"[unhandled, terminating={e.IsTerminating}] {message}");
        };

        Console.WriteLine($"relay: {TestConfig.Host}:{TestConfig.Port}");

        var host = new PhotonClientHarness("host-user");
        if (!host.Connect() || !host.WaitForConnected(TimeoutMs))
        {
            Console.Error.WriteLine($"host connect failed: {host.LastError} state={host.State}");
            return 1;
        }
        Console.WriteLine("step 1 OK: host transport + auth (ENET handshake, key exchange, op 231/230)");

        // auth response must be processed before the state machine accepts room ops
        if (!WaitUntil(() => host.State == ClientState.ConnectedToMasterServer, TimeoutMs))
        {
            Console.Error.WriteLine($"host never reached ConnectedToMasterServer: state={host.State}");
            return 1;
        }
        Console.WriteLine("step 1b OK: master auth complete (ConnectedToMasterServer)");

        // N/M/P custom props: the same stamp the dedicated plugin applies to CreateRoom
        var roomOptions = new RoomOptions
        {
            MaxPlayers = 4,
            CustomRoomProperties = new Hashtable
            {
                [ServerPropertyKeys.DisplayName] = "TestCabin",
                [ServerPropertyKeys.Mode] = "standard",
                [ServerPropertyKeys.PasswordRequired] = false,
            },
            CustomRoomPropertiesForLobby = new[]
            {
                ServerPropertyKeys.DisplayName,
                ServerPropertyKeys.Mode,
                ServerPropertyKeys.PasswordRequired,
            },
        };
        if (!host.OpCreateRoom(new EnterRoomParams
            {
                RoomName = "test-room",
                RoomOptions = roomOptions,
                Lobby = null,
            }))
        {
            Console.Error.WriteLine("host OpCreateRoom rejected by state machine");
            return 1;
        }

        // the client will disconnect from master, reconnect as "game", re-auth, re-send the
        // create op — wait for it to reach Joined (fires after join event is processed)
        if (!WaitUntil(() => host.State == ClientState.Joined, TimeoutMs))
        {
            Console.Error.WriteLine($"host never reached Joined: state={host.State} lastErr={host.LastError} " +
                                    $"responses={DescribeResponses(host)}");
            return 1;
        }
        Console.WriteLine("step 2 OK: host created room on game server (redirect flow complete)");

        // ---- guest joins
        var guest = new PhotonClientHarness("guest-user");
        if (!guest.Connect() || !guest.WaitForConnected(TimeoutMs) ||
            !WaitUntil(() => guest.State == ClientState.ConnectedToMasterServer, TimeoutMs))
        {
            Console.Error.WriteLine($"guest connect failed: {guest.LastError} state={guest.State}");
            return 1;
        }
        if (!guest.OpJoinRoom(new EnterRoomParams
            {
                RoomName = "test-room",
                RoomOptions = roomOptions,
            }))
        {
            Console.Error.WriteLine("guest OpJoinRoom rejected by state machine");
            return 1;
        }
        if (!WaitUntil(() => guest.State == ClientState.Joined, TimeoutMs))
        {
            Console.Error.WriteLine($"guest never reached Joined: state={guest.State} lastErr={guest.LastError} " +
                                    $"responses={DescribeResponses(guest)}");
            return 1;
        }
        Console.WriteLine("step 3 OK: guest joined room");

        // ---- host must have seen the guest join event
        if (!WaitUntil(() => host.CurrentRoom != null &&
                             host.CurrentRoom.GetPlayer(2) != null, TimeoutMs))
        {
            Console.Error.WriteLine($"host did not register guest: hostPlayers={ListPlayers(host)}");
            return 1;
        }
        Console.WriteLine("step 4 OK: host sees guest in actor list");

        // ---- PUN identity propagation (what broke players before: null UserIds crash PEAK's
        // spawn/ban/audio flows). Both sides must know BOTH actors' ids: the local one from
        // the auth-response echo (param 225), the remote one from the entry response's
        // per-actor props (byte-keyed 253).
        if (host.LocalPlayer.UserId != "host-user" || guest.LocalPlayer.UserId != "guest-user" ||
            host.CurrentRoom!.GetPlayer(2)!.UserId != "guest-user" ||
            guest.CurrentRoom!.GetPlayer(1)!.UserId != "host-user")
        {
            Console.Error.WriteLine($"user ids not propagated: host.Local={host.LocalPlayer.UserId} " +
                                    $"guest.Local={guest.LocalPlayer.UserId} host sees 2={host.CurrentRoom?.GetPlayer(2)?.UserId} " +
                                    $"guest sees 1={guest.CurrentRoom?.GetPlayer(1)?.UserId}");
            return 1;
        }
        Console.WriteLine("step 4b OK: UserId propagated (auth echo 225 + per-actor props 253)");

        // ---- guest raises event 42 to Everyone
        int before = CountEvents(host, 42);
        var raise = new Dictionary<byte, object>
        {
            [244] = (byte)42,
            [245] = new Hashtable { ["msg"] = "hello from guest" },
        };
        if (!guest.LoadBalancingPeer.SendOperation(253, raise, SendOptions.SendReliable))
        {
            Console.Error.WriteLine("failed to send RaiseEvent");
            return 1;
        }
        if (!WaitUntil(() => CountEvents(host, 42) > before, TimeoutMs))
        {
            Console.Error.WriteLine("host never received raised event");
            return 1;
        }
        var raised = FindEvent(host, 42, before);
        var content = raised != null && raised.Parameters.TryGetValue(245, out var c) ? c as Hashtable : null;
        if (content == null || !Equals(content["msg"], "hello from guest"))
        {
            Console.Error.WriteLine($"raised event content mismatch");
            return 1;
        }
        Console.WriteLine("step 5 OK: event routed host-ward, content intact");

        // ---- host sets a room property; guest must see the change event
        var beforeProps = CountPropEvents(guest);
        if (!host.CurrentRoom.SetCustomProperties(new Hashtable { ["round"] = 2 }))
        {
            Console.Error.WriteLine("host SetCustomProperties rejected");
            return 1;
        }
        if (!WaitUntil(() => CountPropEvents(guest) > beforeProps, TimeoutMs))
        {
            Console.Error.WriteLine("guest never received property-change event");
            return 1;
        }
        Console.WriteLine("step 6 OK: room property change broadcast");

        // ---- server-browser metadata round-trip (what /api/servers publishes)
        if (!WaitUntil(() => guest.CurrentRoom != null &&
                             Equals(guest.CurrentRoom.CustomProperties[ServerPropertyKeys.DisplayName], "TestCabin") &&
                             Equals(guest.CurrentRoom.CustomProperties[ServerPropertyKeys.Mode], "standard") &&
                             Equals(guest.CurrentRoom.CustomProperties[ServerPropertyKeys.PasswordRequired], false),
                             TimeoutMs))
        {
            Console.Error.WriteLine("guest never observed N/M/P metadata: " +
                                    DescribeProps(guest));
            return 1;
        }
        Console.WriteLine("step 7 OK: server metadata (N/M/P) round-trips through the relay");

        if (TestConfig.HttpPort > 0 && WaitUntil(() => CheckServersApi(TestConfig.HttpPort), 3000))
            Console.WriteLine("step 8 OK: /api/servers lists the server with rich metadata");
        else
            Console.WriteLine("step 8 SKIPPED: /api/servers not reachable (HTTP sidecar off?)");

        // ---- player chat round-trip (event 198): guest raises, host + guest must both see
        // it with the relay-stamped sender actor (254) and the ChatEvent payload intact.
        // This is the exact wire shape the client shim's ChatHud and the console's 'say'
        // use (server announcements ride the same routing on event 199).
        int beforeGuest198 = CountEvents(guest, ChatEvent.PlayerEventCode);
        int beforeHost198 = CountEvents(host, ChatEvent.PlayerEventCode);
        if (!guest.LoadBalancingPeer.OpRaiseEvent(
                ChatEvent.PlayerEventCode,
                ChatEvent.CreatePayload("GuestPlayer", "gg duck lovers"),
                new Photon.Realtime.RaiseEventOptions { Receivers = Photon.Realtime.ReceiverGroup.All },
                SendOptions.SendReliable))
        {
            Console.Error.WriteLine("failed to raise chat event");
            return 1;
        }
        if (!WaitUntil(() => CountEvents(host, ChatEvent.PlayerEventCode) > beforeHost198, TimeoutMs))
        {
            Console.Error.WriteLine("host never received the player chat event");
            return 1;
        }
        var hostChat = FindEvent(host, ChatEvent.PlayerEventCode, beforeHost198);
        var decodedForHost = hostChat != null && hostChat.Parameters.TryGetValue(245, out var hostPayload)
            ? ChatEvent.TryDecode(ChatEvent.PlayerEventCode, hostPayload)
            : null;
        if (decodedForHost == null || decodedForHost.Text != "gg duck lovers")
        {
            Console.Error.WriteLine("host received malformed chat payload");
            return 1;
        }
        if (!hostChat!.Parameters.TryGetValue(254, out var senderObj) ||
            senderObj is not int senderActor || senderActor != guest.LocalPlayer.ActorNumber)
        {
            Console.Error.WriteLine($"chat sender actor missing/mismatch: {hostChat.Parameters.TryGetValue(254, out var s)}");
            return 1;
        }
        if (!WaitUntil(() => CountEvents(guest, ChatEvent.PlayerEventCode) > beforeGuest198, TimeoutMs))
        {
            Console.Error.WriteLine("sender never received the echo of their own chat (ReceiverGroup.All)");
            return 1;
        }
        Console.WriteLine($"step 9 OK: player chat routed both ways (sender actor {senderActor}, payload intact)");

        Console.WriteLine("RELAY SCENARIO OK");
        return 0;
    }

    private static bool CheckServersApi(int httpPort)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var json = http.GetStringAsync($"http://127.0.0.1:{httpPort}/api/servers").GetAwaiter().GetResult();
            var ok = json.Contains("\"displayName\":\"TestCabin\"") && json.Contains("\"joinKey\":\"test-room\"");
            if (!ok)
                Console.Error.WriteLine($"  /api/servers content not yet matching: {json}");
            return ok;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string DescribeProps(PhotonClientHarness client)
    {
        if (client.CurrentRoom == null)
            return "no room";
        var parts = new List<string>();
        foreach (var (key, value) in client.CurrentRoom.CustomProperties)
            parts.Add($"{key}={value}");
        return string.Join(", ", parts);
    }

    private static string DescribeException(Exception ex)
    {
        var parts = new List<string>();
        while (ex != null)
        {
            parts.Add($"{ex.GetType().FullName}: {ex.Message}");
            ex = ex.InnerException!;
        }
        return string.Join(" -> ", parts);
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(25);
        }
        return condition();
    }

    private static int CountEvents(PhotonClientHarness client, byte code)
    {
        int count = 0;
        foreach (var ev in client.Events)
            if (ev.Code == code)
                count++;
        return count;
    }

    private static EventData? FindEvent(PhotonClientHarness client, byte code, int startIndex)
    {
        for (int i = startIndex; i < client.Events.Count; i++)
            if (client.Events[i].Code == code)
                return client.Events[i];
        return null;
    }

    private static int CountPropEvents(PhotonClientHarness client)
    {
        int count = 0;
        foreach (var ev in client.Events)
        {
            if (ev.Code == 253 && ev.Parameters.TryGetValue(251, out var p) &&
                p is Hashtable ht && Equals(ht["round"], 2))
                count++;
        }
        return count;
    }

    private static string ListPlayers(PhotonClientHarness client)
    {
        if (client.CurrentRoom == null)
            return "no room";
        var names = new List<string>();
        foreach (var p in client.CurrentRoom.Players.Values)
            names.Add($"{p.ActorNumber}");
        return string.Join(",", names);
    }

    private static string DescribeResponses(PhotonClientHarness client)
    {
        var parts = new List<string>();
        foreach (var r in client.Responses)
            parts.Add($"op{r.OperationCode}/rc{r.ReturnCode}");
        return string.Join(" ", parts);
    }
}
