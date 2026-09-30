using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace PeakRelay.Client;

/// <summary>
/// PeakRelay player plugin. Two jobs, both vanilla-preserving:
///
/// 1. Transport: installs RelayClientSocket for the Udp protocol slot and (when the relay
///    is enabled, the default) rewrites PhotonServerSettings so every PUN connect path —
///    menu auto-connect, region swaps, game-server re-auth — resolves to the relay.
///    When the relay is disabled the shim stays fully passive (vanilla Photon Cloud).
/// 2. Observation (only when explicitly enabled): datagram summaries for support.
/// </summary>
[BepInPlugin(PluginGuid, PluginName, BuildVersion.Version)]
public sealed class RelayPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.peakrelay.client";
    public const string PluginName = "PeakRelay";

    /// <summary>Version from the assembly (Directory.Build.props is the single source).</summary>
    public static string PluginVersion { get; } =
        typeof(RelayPlugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? BuildVersion.Version;

    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource(PluginName);
    private Harmony? _harmony;

    private void Awake()
    {
        RelayConfig.Bind(Config);

        _harmony = new Harmony(PluginGuid);
        try
        {
            _harmony.PatchAll(typeof(PunSocketPatches));
            _harmony.PatchAll(typeof(SerializationProtocolPatch));
            _harmony.PatchAll(typeof(ConnectPatches));
            _harmony.PatchAll(typeof(ChatInputBlockPatch));
        }
        catch (Exception ex)
        {
            Log.LogError($"patch installation failed: {ex.Message}");
            throw;
        }

        // server browser: patches (when the game version matches) + page injection
        GameAPI.Initialize(_harmony);

        // server announcements (custom PUN event 199): a passive HUD GameObject that only
        // renders when the dedicated server broadcasts — never touches game state
        var hud = new GameObject("PeakRelay.ChatHud");
        UnityEngine.Object.DontDestroyOnLoad(hud);
        hud.AddComponent<ChatHud>();

        Log.LogInfo($"PeakRelay {PluginVersion}: relay={RelayConfig.RelayEnabled} " +
                    $"({RelayConfig.Host}:{RelayConfig.Port}) trace={RelayConfig.TraceEnabled}");
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }

    internal static void LogInfo(string message) => Log.LogInfo(message);
    internal static void LogWarning(string message) => Log.LogWarning(message);

    /// <summary>
    /// Registers RelayClientSocket in the Udp slot of every LoadBalancingPeer (game, voice,
    /// any) when the relay is enabled. The socket itself speaks the relay protocol; when the
    /// relay is disabled the slot stays vanilla and nothing else in this plugin activates.
    ///
    /// The peer has TWO constructors, so the patch must name them explicitly — an unqualified
    /// MethodType.Constructor is ambiguous and Harmony throws at PatchAll, killing the plugin
    /// in Awake (live client-run find, 2026-09-29: identical to the Dedicated plugin's bug).
    /// </summary>
    [HarmonyPatch]
    internal static class PunSocketPatches
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Constructor(typeof(LoadBalancingPeer), new[] { typeof(ConnectionProtocol) });
            yield return AccessTools.Constructor(typeof(LoadBalancingPeer), new[] { typeof(IPhotonPeerListener), typeof(ConnectionProtocol) });
        }

        [HarmonyPostfix]
        public static void InstallSocketImplementation(LoadBalancingPeer __instance)
        {
            if (!RelayConfig.RelayEnabled)
                return;
            var config = __instance.SocketImplementationConfig;
            if (config.TryGetValue(ConnectionProtocol.Udp, out var current) &&
                current == typeof(RelayClientSocket))
            {
                return; // already installed (peers can be constructed more than once)
            }
            config[ConnectionProtocol.Udp] = typeof(RelayClientSocket);
        }
    }

    /// <summary>
    /// Forces PUN's serialization to GpBinaryV16 — the format the relay speaks byte-exact.
    /// The game's LoadBalancingClient constructor sets GpBinaryV18; without this override a
    /// relay session dies with 'unknown P16 tag 96' (a V18-only type) in the key-exchange op.
    /// Ported from the Dedicated plugin's live-proven fix: postfix on both client constructors
    /// (patching the property setter itself NREs inside Harmony).
    /// </summary>
    [HarmonyPatch]
    internal static class SerializationProtocolPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var t = typeof(LoadBalancingClient);
            yield return AccessTools.Constructor(t, new[] { typeof(ConnectionProtocol) });
            yield return AccessTools.Constructor(t, new[]
            {
                typeof(string), typeof(string), typeof(string), typeof(ConnectionProtocol),
            });
        }

        [HarmonyPostfix]
        public static void ForceV16(LoadBalancingClient __instance)
        {
            if (__instance.SerializationProtocol == SerializationProtocol.GpBinaryV18)
            {
                __instance.SerializationProtocol = SerializationProtocol.GpBinaryV16;
                LogInfo("serialization forced to GpBinaryV16 (relay format)");
            }
        }
    }

    /// <summary>
    /// Points PUN at the relay by rewriting the shared PhotonServerSettings asset once per
    /// session: AppId must be a non-empty valid-format string or PhotonNetwork refuses to
    /// connect, Server/Port select the relay endpoint, UseNameServer=false makes every
    /// path (ConnectUsingSettings, region swap, re-auth) go straight to the master role.
    /// </summary>
    [HarmonyPatch(typeof(PhotonNetwork), nameof(PhotonNetwork.ConnectUsingSettings), new[] { typeof(AppSettings), typeof(bool) })]
    internal static class ConnectPatches
    {
        [HarmonyPrefix]
        public static void RedirectAppSettings(ref AppSettings appSettings)
        {
            if (!RelayConfig.RelayEnabled || appSettings == null)
                return;

            if (RelayConfig.Redirected)
            {
                // subsequent connects: keep pointing at the relay even if the game resets fields
                appSettings.Server = RelayConfig.Host;
                appSettings.Port = RelayConfig.Port;
                appSettings.UseNameServer = false;
                appSettings.AppIdRealtime = "peakrelay";
                return;
            }

            RelayConfig.Redirected = true;
            appSettings.Server = RelayConfig.Host;
            appSettings.Port = RelayConfig.Port;
            appSettings.Protocol = ConnectionProtocol.Udp;
            appSettings.UseNameServer = false;
            appSettings.FixedRegion = null;
            appSettings.EnableProtocolFallback = false;
            appSettings.AppIdRealtime = "peakrelay";

            // the shared asset drives every later connect path (region swap, re-auth)
            var settings = PhotonNetwork.PhotonServerSettings;
            if (settings != null)
            {
                settings.AppSettings.Server = RelayConfig.Host;
                settings.AppSettings.Port = RelayConfig.Port;
                settings.AppSettings.UseNameServer = false;
                settings.AppSettings.FixedRegion = null;
                settings.AppSettings.AppIdRealtime = "peakrelay";
            }

            LogInfo($"PUN redirected to relay {RelayConfig.Host}:{RelayConfig.Port}");
        }
    }
}
