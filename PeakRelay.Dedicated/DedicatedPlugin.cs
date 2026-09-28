using System;
using System.Collections;
using BepInEx;
using BepInEx.Bootstrap;
using ExitGames.Client.Photon;
using HarmonyLib;
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
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("com.bepinex.plugins.serverconsole", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class DedicatedPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.peakrelay.dedicated";
    public const string PluginName = "PeakRelay.Dedicated";
    public const string PluginVersion = "0.4.0";

    private Harmony? _harmony;
    private DedicatedConfig _config = new();
    private bool _booted;
    private bool _connectingStarted;
    private int _createRoomAttempts;
    private const int MaxCreateRoomAttempts = 5;

    private void Awake()
    {
        // plugin directory: BepInEx/plugins/PeakRelay.Dedicated/
        var pluginDir = System.IO.Path.GetDirectoryName(typeof(DedicatedPlugin).Assembly.Location) ?? ".";
        DedicatedState.Config = _config = DedicatedConfig.Load(pluginDir);
        ServerLog.Initialize(pluginDir);

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(DedicatedPatches));

        if (!DedicatedPatches.IsHeadless)
        {
            ServerLog.Info("not headless (-batchmode -nographics missing): dedicated mode off");
            return;
        }

        ServerLog.Info($"config: room={_config.RoomName} maxPlayers={_config.MaxPlayers} " +
                       $"visible={_config.Visible} open={_config.Open} relay={_config.RelayHost}:{_config.RelayPort} " +
                       $"autoHost={_config.AutoHost} useVanillaName={_config.UseVanillaName}");
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
    private void ConnectToRelay()
    {
        if (_connectingStarted)
            return;
        _connectingStarted = true;

        var config = _config;
        PhotonNetwork.NickName = "peakrelay-dedicated";
        PhotonNetwork.AuthValues = new AuthenticationValues { AuthType = CustomAuthenticationType.None, UserId = "peakrelay-dedicated" };
        PhotonNetwork.GameVersion = Application.version;
        PhotonNetwork.AutomaticallySyncScene = true;
        PhotonNetwork.SerializationRate = 30;
        PhotonNetwork.SendRate = 30;

        var settings = new AppSettings
        {
            UseNameServer = false,          // connect straight to the relay master
            Server = config.RelayHost,
            Port = config.RelayPort,
            Protocol = ConnectionProtocol.Udp, // RelayGameSocket is registered under Udp
            AppVersion = Application.version,
            AuthMode = AuthModeOption.Auth,
        };
        if (!PhotonNetwork.ConnectUsingSettings(settings))
        {
            ServerLog.Error("PhotonNetwork.ConnectUsingSettings returned false — check relay address");
            _connectingStarted = false;
        }
        else
        {
            ServerLog.Info($"connecting to relay {config.RelayHost}:{config.RelayPort}");
        }
    }

    private void Update()
    {
        if (!DedicatedPatches.IsHeadless)
            return;

        // Mirror the room-creation retry the vanilla flow gets from its modal ("Try again"):
        // if CreateRoom failed, NetworkConnector switches back to DefaultConnectionState and
        // we re-arm HostState + reconnect.
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
                _connectingStarted = false;
                _createRoomAttempts++;
                if (_createRoomAttempts > MaxCreateRoomAttempts)
                {
                    ServerLog.Error("too many reconnect attempts, giving up (kill the process to restart)");
                    return;
                }
                StartCoroutine(RestartHostCycle());
            }
        }
        catch (Exception ex)
        {
            ServerLog.Error($"update tick failed: {ex.Message}");
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
        _connectingStarted = false;
        ConnectToRelay();
    }
}
