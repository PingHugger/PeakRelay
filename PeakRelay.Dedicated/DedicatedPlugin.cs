using System;
using System.Collections;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using ExitGames.Client.Photon;
using HarmonyLib;
using Peak.Network;
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
/// </summary>    [BepInPlugin(PluginGuid, PluginName, BuildVersion.Version)]

[BepInDependency("com.bepinex.plugins.serverconsole", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class DedicatedPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.peakrelay.dedicated";
    public const string PluginName = "PeakRelay.Dedicated";

    /// <summary>Version from the assembly (Directory.Build.props is the single source).</summary>
    public static string PluginVersion { get; } =
        typeof(DedicatedPlugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? BuildVersion.Version;

    private Harmony? _harmony;
    private DedicatedConfig _config = new();
    private bool _booted;
    private int _createRoomAttempts;
    private const int MaxCreateRoomAttempts = 5;

    private void Awake()
    {
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
                       $"noSteam={noSteam}{(noSteam ? $" hostName='{_config.HostName}'" : "")}");
        StartCoroutine(Boot());
    }

    private void OnDestroy()
    {
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
    /// Own connect path instead of NetworkingUtilities.ConnectToNetwork: the game version
    /// reads Application.version and Steam persona names, both fragile headless. We set the
    /// same fields ourselves and connect with an explicit AppSettings — this is the same
    /// call shape PUN's ConnectUsingSettings(appSettings) overload takes.
    /// </summary>
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
            }
            else if (_booted && PhotonNetwork.NetworkClientState == ClientState.Disconnected)
            {
                ServerLog.Warn("disconnected from relay — restarting host cycle");
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
