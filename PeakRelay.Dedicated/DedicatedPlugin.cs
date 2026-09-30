using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using ExitGames.Client.Photon;
using HarmonyLib;
using Peak.Network;
using PeakRelay.Protocol;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakRelay.Dedicated;

/// <summary>
/// PeakRelay M4: turns a stock PEAK install into a headless dedicated server.
///
/// Activation: the plugin idles unless the process is headless (-batchmode -nographics,
/// mirrors ServerConsole detection) — regular clients are untouched.
///
/// Boot sequence, all inside the Unity main loop:
///   1. Wait for GameHandler (services, including ConnectionService, registered).
///   2. Wait for the Title scene (NetworkConnector exists; the ConnectionService state
///      machine is what NetworkConnector.HandleConnectionState reacts to).
///   3. SwitchState&lt;HostState&gt; with RoomName from server.json, then load WilIsland —
///      exactly what the game's own DebugMainMenu auto-host path does.
///   4. NetworkingUtilities.ConnectToNetwork connects Photon (via RelayGameSocket) to the
///      relay; NetworkConnector.OnConnectedToMaster then calls
///      HandleConnectionState(HostState) → PhotonNetwork.CreateRoom(RoomName).
///
/// The vanilla HostState overwrites RoomName unless CurrentPlayer.Tags contains "Player1"
/// (a play-mode test tag that headless never gets); DedicatedPatches keeps our configured
/// name instead — config is authoritative (useVanillaName restores stock behavior).
///
/// Operator console: when config.console is on (default), ServerLog lines are rerouted
/// through ServerConsole (friendly phrasing) and a PunEventBridge announces join/leave in
/// plain language. Console commands run on a background stdin thread; anything that must
/// touch Unity is marshaled back to the main loop via MainThreadJobs.
/// </summary>
[BepInPlugin(PluginGuid, PluginName, BuildVersion.Version)]
[BepInDependency("com.bepinex.plugins.serverconsole", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class DedicatedPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.peakrelay.dedicated";
    public const string PluginName = "PeakRelay.Dedicated";

    /// <summary>Version from the assembly (Directory.Build.props is the single source).</summary>
    public static string PluginVersion { get; } =
        typeof(DedicatedPlugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? BuildVersion.Version;

    /// <summary>Singleton access for console commands (null before Awake).</summary>
    internal static DedicatedPlugin? Instance { get; private set; }

    /// <summary>Work handed over from the console thread, drained every Update.</summary>
    internal static readonly ConcurrentQueue<Action> MainThreadJobs = new();

    private Harmony? _harmony;
    private DedicatedConfig _config = new();
    private bool _booted;
    private int _createRoomAttempts;
    private const int MaxCreateRoomAttempts = 5;

    private void Awake()
    {
        Instance = this;

        // plugin directory: BepInEx/plugins/PeakRelay.Dedicated/
        var pluginDir = System.IO.Path.GetDirectoryName(typeof(DedicatedPlugin).Assembly.Location) ?? ".";
        DedicatedState.Config = _config = DedicatedConfig.Load(pluginDir);
        ServerLog.Initialize(pluginDir);

        _harmony = new Harmony(PluginGuid);
        // PatchAll(type) processes ONLY that type's [HarmonyPatch]s — nested classes need
        // PatchAll(assembly) (the headless live run proved this: no patch fired).
        _harmony.PatchAll(typeof(DedicatedPlugin).Assembly);

        if (!DedicatedPatches.IsHeadless)
        {
            ServerLog.Info("not headless (-batchmode -nographics missing): dedicated mode off");
            return;
        }

        var noSteam = SteamBypassPatch.NoSteamActive;
        ServerLog.Info($"config: room={_config.RoomName} display='{_config.DisplayName}' mode={_config.Mode} " +
                       $"password={(string.IsNullOrEmpty(_config.Password) ? "no" : "yes")} maxPlayers={_config.MaxPlayers} " +
                       $"visible={_config.Visible} open={_config.Open} relay={_config.RelayHost}:{_config.RelayPort} " +
                       $"autoHost={_config.AutoHost} useVanillaName={_config.UseVanillaName} " +
                       $"noSteam={noSteam}{(noSteam ? $" hostName='{_config.HostName}'" : "")} " +
                       $"console={_config.Console}");

        if (_config.Console)
        {
            ServerLog.Routed = ServerConsole.HandleServerLog;
            ServerConsole.Start(_config);
        }
        else
        {
            ServerLog.Routed = null; // classic stdout logging for piped/service hosts
        }

        var bridge = new GameObject("PeakRelay.PunEventBridge");
        DontDestroyOnLoad(bridge);
        bridge.AddComponent<PunEventBridge>();

        StartCoroutine(Boot());
    }

    private void OnDestroy()
    {
        ServerConsole.Shutdown();
        _harmony?.UnpatchSelf();
    }

    private IEnumerator Boot()
    {
        // 1) GameHandler services (ConnectionService among them)
        yield return new WaitUntil(() =>
        {
            try { return GameHandlerReady(); }
            catch { return false; }
        });
        ServerLog.Info("GameHandler ready");

        // 2) Title scene: gives us NetworkConnector + the scene-load flow
        yield return new WaitUntil(() =>
            SceneManager.GetActiveScene().name.Equals("Title", StringComparison.OrdinalIgnoreCase));
        ServerLog.Info("Title scene active");

        if (!_config.AutoHost)
        {
            ServerLog.Info("autoHost=false: hosting manually via 'peakrelay host <room>' console command");
            yield break;
        }

        yield return StartHosting(_config.RoomName);
    }

    internal static bool GameHandlerReady()
    {
        // reflection-light probe: ConnectionService must be resolvable
        var service = GameHandler.GetService<ConnectionService>();
        return service != null && service.StateMachine != null;
    }

    internal IEnumerator StartHosting(string roomName)
    {
        // capacity: the vanilla HostState flow reads NetworkingUtilities.MAX_PLAYERS
        NetworkingUtilities.SetMaxPlayers(_config.MaxPlayers);

        var host = GameHandler.GetService<ConnectionService>().StateMachine.SwitchState<HostState>();
        host.RoomName = roomName;
        DedicatedState.ConfiguredRoomName = roomName;
        ServerLog.Info($"HostState armed (room '{roomName}'), loading WilIsland");
        SceneManager.LoadScene("WilIsland");
        yield return null; // let the scene switch settle before connecting
        ConnectToRelay();
    }

    /// <summary>
    /// Console command 'resethost' (runs on the console thread): queue the host-cycle
    /// restart onto the Unity main thread — Unity objects may only be touched there.
    /// </summary>
    internal void PluginStartHosting(string roomName)
    {
        MainThreadJobs.Enqueue(() =>
        {
            _booted = false;
            DedicatedState.CreateRoomSent = false;
            _createRoomAttempts = 0;
            ServerConsole.RelayUp = false;
            StartCoroutine(StartHosting(roomName));
        });
    }

    /// <summary>
    /// Connect through the game's own path (NetworkingUtilities.ConnectToNetwork); the
    /// ConnectRedirectPatch prefix points it at the relay. Our previous own-call approach
    /// raced the game's connect flow (live-run bug).
    /// </summary>
    private void ConnectToRelay()
    {
        NetworkingUtilities.ConnectToNetwork();
        ServerLog.Info("connect requested via NetworkingUtilities.ConnectToNetwork (relay-redirected)");
    }

    private void Update()
    {
        if (!DedicatedPatches.IsHeadless)
            return;

        // Drain console-thread work (e.g. resethost) before the network tick.
        while (MainThreadJobs.TryDequeue(out var job))
        {
            try { job(); }
            catch (Exception ex) { ServerLog.Error($"console command failed: {ex.Message}"); }
        }

        // Mirror the room-creation retry the vanilla flow gets from its modal ("Try again"):
        // if CreateRoom failed, NetworkConnector switches back to DefaultConnectionState and
        // we re-arm HostState.
        try
        {
            if (!_booted && PhotonNetwork.NetworkClientState == ClientState.Joined)
            {
                _booted = true;
                ServerLog.Info($"room '{PhotonNetwork.CurrentRoom?.Name}' joined — dedicated server is UP " +
                               $"(actorNr={PhotonNetwork.LocalPlayer.ActorNumber}, maxPlayers={PhotonNetwork.CurrentRoom?.MaxPlayers})");
                ServerConsole.RelayUp = true;
            }
            else if (_booted && PhotonNetwork.NetworkClientState == ClientState.Disconnected)
            {
                ServerLog.Warn("disconnected from relay — restarting host cycle");
                ServerConsole.RelayUp = false;
                _booted = false;
                DedicatedState.CreateRoomSent = false;
                _createRoomAttempts++;
                if (_createRoomAttempts > MaxCreateRoomAttempts)
                {
                    ServerLog.Error("too many reconnect attempts, giving up (kill the process to restart)");
                    return;
                }
                StartCoroutine(RestartHostCycle());
            }
            else if (!_booted && !DedicatedState.CreateRoomSent &&
                     PhotonNetwork.NetworkClientState == ClientState.ConnectedToMasterServer &&
                     CurrentStateIsHostState())
            {
                // Recovery for the connect-finishes-before-HostState race (live-run find):
                // OnConnectedToMaster already fired with the wrong state, so nothing will
                // create the room. Drive the game's own handler now.
                ServerLog.Info("connected before HostState was armed — invoking HandleConnectionState");
                DedicatedState.CreateRoomSent = true;
                HandleConnectionStateNow();
            }
        }
        catch (Exception ex)
        {
            ServerLog.Error($"update tick failed: {ex.Message}");
        }
    }

    private static bool CurrentStateIsHostState()
    {
        try
        {
            return GameHandler.GetService<ConnectionService>().StateMachine.CurrentState is HostState;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs the game's own state handler (the OnConnectedToMaster body's core).</summary>
    private static void HandleConnectionStateNow()
    {
        try
        {
            var connector = UnityEngine.Object.FindFirstObjectByType<NetworkConnector>();
            if (connector == null)
            {
                ServerLog.Error("recovery: NetworkConnector not found in scene");
                return;
            }
            var state = GameHandler.GetService<ConnectionService>().StateMachine.CurrentState;
            var method = AccessTools.Method(typeof(NetworkConnector), "HandleConnectionState");
            method?.Invoke(connector, new[] { (ConnectionState)state });
        }
        catch (Exception ex)
        {
            ServerLog.Error($"recovery HandleConnectionState failed: {ex.Message}");
        }
    }

    private IEnumerator RestartHostCycle()
    {
        yield return new WaitForSecondsRealtime(3f);
        var current = GameHandler.GetService<ConnectionService>().StateMachine.CurrentState;
        if (current is not HostState)
        {
            var host = GameHandler.GetService<ConnectionService>().StateMachine.SwitchState<HostState>();
            host.RoomName = DedicatedState.ConfiguredRoomName;
        }
        ConnectToRelay();
    }
}

/// <summary>
/// PeakRelay PUN event bridge: announces player join/leave to the operator console in plain
/// language ("Bob joined the expedition (2 of 20 slots in use)") and logs player chat
/// ("Bob: hello") for the operator.
///
/// MonoBehaviourPunCallbacks self-registers with the PUN client — the same mechanism the
/// game's NetworkConnector uses — so the room callbacks fire reliably without touching any
/// game component. CHAT (bug report: messages never appeared in the console) needs the
/// interface below: LoadBalancingClient.AddCallbackTarget wires EventReceived only for
/// targets that implement IOnEventCallback — a public OnEvent method alone is never
/// registered (same root cause the client HUD had, fixed in v0.7.9).
/// </summary>
internal sealed class PunEventBridge : MonoBehaviourPunCallbacks, IInRoomCallbacks, IOnEventCallback
{
    // The Player parameter MUST be fully qualified: the game's own global 'Player' class
    // would otherwise shadow Photon.Realtime.Player in these signatures (real compile find).
    public override void OnPlayerEnteredRoom(Photon.Realtime.Player newPlayer)
    {
        if (newPlayer.IsLocal)
            return;
        var players = ServerConsole.PlayersSnapshot();
        ServerConsole.Success($"{newPlayer.NickName} joined the expedition " +
                              $"({players.Count} of {MaxPlayersOrConfig()} slots in use).");
    }

    public override void OnPlayerLeftRoom(Photon.Realtime.Player leavingPlayer)
    {
        if (leavingPlayer.IsLocal)
            return;
        var players = ServerConsole.PlayersSnapshot();
        ServerConsole.Message($"{leavingPlayer.NickName} left the expedition " +
                              $"({players.Count} of {MaxPlayersOrConfig()} slots in use).");
    }

    public override void OnConnected()
    {
        ServerConsole.Message("Connected to the game network (relay).");
    }

    public override void OnDisconnected(DisconnectCause cause)
    {
        ServerConsole.RelayUp = false;
        ServerConsole.Warn("The server lost its connection — it will try to rejoin automatically.");
    }

    public override void OnConnectedToMaster()
    {
        ServerConsole.Message("Relay link is live — opening the room…");
    }

    /// <summary>
    /// Player chat (event 198) lands in the operator console as a chat line. Server
    /// announcements (199) are deliberately not re-logged — 'say' already reports them
    /// when sent. Malformed payloads are ignored; the console must never crash on traffic.
    /// </summary>
    public void OnEvent(EventData eventData)
    {
        if (eventData == null)
            return;
        HandleChatEvent(eventData.Code, eventData.CustomData);
    }

    /// <summary>The chat-logging core, factored out so tests can drive it without PUN.</summary>
    internal void HandleChatEvent(byte eventCode, object? payload)
    {
        if (eventCode != ChatEvent.PlayerEventCode)
            return;
        var decoded = ChatEvent.TryDecode(eventCode, payload);
        if (decoded == null)
            return;

        ServerConsole.Chat($"{ResolveSender(null, decoded.From)}: {decoded.Text}");
    }

    /// <summary>
    /// Prefers the room's current nickname for the sending actor (param 254, stamped by the
    /// relay), falling back to the label the sender chose for themselves.
    /// </summary>
    private static string ResolveSender(EventData? eventData, string fallback)
    {
        try
        {
            if (eventData != null && eventData.Parameters.TryGetValue(254, out var actor) && actor is int actorNr)
            {
                var player = PhotonNetwork.CurrentRoom?.GetPlayer(actorNr);
                if (player != null && !string.IsNullOrWhiteSpace(player.NickName))
                    return player.NickName.Trim();
            }
        }
        catch { /* name resolution is cosmetic — the fallback label is fine */ }
        return fallback;
    }

    // ---------------------------------------------------------------- test hooks

    /// <summary>Test hook: runs the chat-logging path without a Photon client.</summary>
    internal static void LogChatForTests(byte eventCode, object payload) =>
        new PunEventBridge().HandleChatEvent(eventCode, payload);

    /// <summary>Test hook: builds a chat payload exactly the way senders do.</summary>
    internal static Dictionary<byte, object> BuildChatPayloadForTests(string from, string text) =>
        ChatEvent.CreatePayload(from, text);

    private static int MaxPlayersOrConfig()
    {
        try { return PhotonNetwork.CurrentRoom?.MaxPlayers ?? DedicatedState.Config?.MaxPlayers ?? 0; }
        catch { return DedicatedState.Config?.MaxPlayers ?? 0; }
    }
}
