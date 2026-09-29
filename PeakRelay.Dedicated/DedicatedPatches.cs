using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ExitGames.Client.Photon;
using HarmonyLib;
using Peak.Network;
using Photon.Pun;
using PeakRelay.Protocol;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace PeakRelay.Dedicated;

/// <summary>
/// Surgical Harmony patches for dedicated mode. Only the room-name guard is patched on the
/// hot path; everything else is vanilla game code (StateMachine, scene flow, PUN callbacks).
/// </summary>
public static class DedicatedPatches
{
    /// <summary>
    /// Forces PUN's serialization to GpBinaryV16 — the format the relay speaks byte-exact
    /// (the game's LoadBalancingClient ctor sets GpBinaryV18; a live headless run died with
    /// 'unknown P16 tag 96', a V18-only type, in the key-exchange op). A postfix on the two
    /// client constructors overrides the value after the client sets it (patching the
    /// property setter itself NREs inside Harmony — live-run find).
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
                ServerLog.Info("serialization forced to GpBinaryV16 (relay format)");
            }
        }
    }

    /// <summary>
    /// Registers RelayGameSocket in the Udp slot of every LoadBalancingPeer this process
    /// constructs — without it the host would dial vanilla UDP at the relay's TCP port.
    /// The peer has TWO constructors, so the patch must name them explicitly (an unqualified
    /// MethodType.Constructor is ambiguous and Harmony throws at PatchAll — live-run find).
    /// </summary>
    [HarmonyPatch]
    internal static class PeerSocketPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Constructor(typeof(LoadBalancingPeer), new[] { typeof(ConnectionProtocol) });
            yield return AccessTools.Constructor(typeof(LoadBalancingPeer), new[] { typeof(IPhotonPeerListener), typeof(ConnectionProtocol) });
        }

        [HarmonyPostfix]
        public static void InstallRelaySocket(LoadBalancingPeer __instance)
        {
            var config = __instance.SocketImplementationConfig;
            if (config.TryGetValue(ConnectionProtocol.Udp, out var current) &&
                current == typeof(RelayGameSocket))
            {
                return;
            }
            config[ConnectionProtocol.Udp] = typeof(RelayGameSocket);
        }
    }
    /// <summary>
    /// Headless detection mirroring ServerConsole (Konnichiwa): both -batchmode and
    /// -nographics, with the graphics-device counter as tiebreaker. Evaluated once.
    /// </summary>
    private static bool? _isHeadless;

    public static bool IsHeadless
    {
        get
        {
            _isHeadless ??= SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null
                && Environment.GetCommandLineArgs().Any(a =>
                    string.Equals(a, "-batchmode", StringComparison.OrdinalIgnoreCase))
                && Environment.GetCommandLineArgs().Any(a =>
                    string.Equals(a, "-nographics", StringComparison.OrdinalIgnoreCase));
            return _isHeadless.Value;
        }
    }

    /// <summary>
    /// Vanilla HandleConnectionState overwrites HostState.RoomName unless the local player
    /// carries the "Player1" play-mode tag (CurrentPlayer is a playmode-test type not present
    /// in every headless setup, so patching is more robust than tag mutation). Config stays
    /// authoritative; useVanillaName=true restores the stock random-name behavior.
    ///
    /// The same prefix stamps the server's public metadata (display name, mode,
    /// password-required flag) into RoomOptions.CustomRoomProperties — key N/M/P — which the
    /// relay publishes on /api/servers for the client server browser. Key P is a bool flag,
    /// never the password itself; the secret stays on the server for its own join check.
    /// </summary>
    [HarmonyPatch(typeof(NetworkConnector), "HandleConnectionState")]
    internal static class HostRoomNamePatch
    {
        /// <summary>
        /// Runs the vanilla CreateRoom ourselves and SKIPS the original body: the vanilla
        /// HostState branch overwrites RoomName ("Player1"-tag logic) after our prefix, and
        /// a live run proved the original then re-created the room with a random name.
        /// Skipping preserves the vanilla call shape (HostRoomOptions + CreateRoom) with
        /// config-authoritative values; all non-HostState branches fall through to vanilla.
        /// </summary>
        [HarmonyPrefix]
        public static bool KeepConfiguredRoomName(NetworkConnector __instance, ConnectionState state)
        {
            if (state is not HostState hostState)
                return true; // vanilla handles every other state
            var config = DedicatedState.Config;
            if (config == null)
                return true;

            if (!config.UseVanillaName)
                hostState.RoomName = DedicatedState.ConfiguredRoomName;
            DedicatedState.PendingRoomMetadata = new ExitGames.Client.Photon.Hashtable
            {
                [ServerPropertyKeys.DisplayName] = config.DisplayName,
                [ServerPropertyKeys.Mode] = config.Mode,
                [ServerPropertyKeys.PasswordRequired] = !string.IsNullOrEmpty(config.Password),
            };

            var roomOptions = NetworkingUtilities.HostRoomOptions();
            ServerLog.Info($"CreateRoom('{hostState.RoomName}') via vanilla options (skip-original prefix)");
            Photon.Pun.PhotonNetwork.CreateRoom(hostState.RoomName, roomOptions);
            return false; // skip vanilla HostState branch entirely
        }
    }

    /// <summary>
    /// Guarded prefix on the game's own connect: when PUN already connects or is connected,
    /// stay vanilla (return false skips nothing — we delegate to the original). When the
    /// game's connect would dial vanilla Photon Cloud, rewrite the shared settings asset
    /// toward the relay first. The shared asset (not the method args) is what later paths
    /// (region swap, re-auth) read, so one write here redirects every path.
    /// </summary>
    [HarmonyPatch(typeof(Photon.Pun.PhotonNetwork), nameof(Photon.Pun.PhotonNetwork.ConnectUsingSettings), new[] { typeof(AppSettings), typeof(bool) })]
    internal static class ConnectRedirectPatch
    {
        [HarmonyPrefix]
        public static void RedirectToRelay(ref AppSettings appSettings)
        {
            var config = DedicatedState.Config;
            if (config == null || appSettings == null)
                return;

            appSettings.Server = config.RelayHost;
            appSettings.Port = config.RelayPort;
            appSettings.Protocol = ConnectionProtocol.Udp;
            appSettings.UseNameServer = false;
            appSettings.FixedRegion = null;
            appSettings.EnableProtocolFallback = false;
            appSettings.AppIdRealtime = "peakrelay";

            var shared = Photon.Pun.PhotonNetwork.PhotonServerSettings;
            if (shared != null)
            {
                shared.AppSettings.Server = config.RelayHost;
                shared.AppSettings.Port = config.RelayPort;
                shared.AppSettings.UseNameServer = false;
                shared.AppSettings.FixedRegion = null;
                shared.AppSettings.AppIdRealtime = "peakrelay";
            }
            ServerLog.Info($"connect redirected to relay {config.RelayHost}:{config.RelayPort}");
        }
    }

    /// <summary>
    /// CreateRoom is the single chokepoint every host path funnels through (HostState flow
    /// and any other); when it fires with our pending metadata present, stamp it into the
    /// room options so the relay can publish the server's public profile on /api/servers.
    /// Key P is a bool flag, never the password itself — the secret stays on the server.
    /// </summary>
    [HarmonyPatch(typeof(Photon.Pun.PhotonNetwork), nameof(Photon.Pun.PhotonNetwork.CreateRoom))]
    internal static class RoomOptionsMetadataPatch
    {
        [HarmonyPrefix]
        public static void AttachMetadata(string roomName, RoomOptions roomOptions)
        {
            var props = DedicatedState.PendingRoomMetadata;
            if (props == null || props.Count == 0 || roomOptions == null)
                return;
            if (!string.Equals(roomName, DedicatedState.ConfiguredRoomName, StringComparison.Ordinal))
                return; // not our hosted room
            DedicatedState.CreateRoomSent = true;
            // CustomRoomProperties is null by default (RoomOptions.cs:19) — the vanilla flow
            // never sets it; a live run NRE'd here and killed the CreateRoom dispatch.
            roomOptions.CustomRoomProperties ??= new ExitGames.Client.Photon.Hashtable();
            foreach (var (key, value) in props)
                roomOptions.CustomRoomProperties[key] = value;
            roomOptions.CustomRoomPropertiesForLobby = new[]
            {
                ServerPropertyKeys.DisplayName,
                ServerPropertyKeys.Mode,
                ServerPropertyKeys.PasswordRequired,
            };
            DedicatedState.PendingRoomMetadata = null;
            ServerLog.Info($"room metadata attached to '{roomName}': display='{props[ServerPropertyKeys.DisplayName]}' " +
                           "mode, passwordRequired");
        }
    }

    /// <summary>
    /// CreateRoom failure in the vanilla flow opens a UI modal — which nobody can click
    /// headless. Log instead and let the plugin's restart cycle re-arm HostState.
    /// </summary>
    [HarmonyPatch(typeof(NetworkConnector), nameof(NetworkConnector.OnCreateRoomFailed))]
    internal static class CreateRoomFailedPatch
    {
        [HarmonyPrefix]
        public static void LogFailure(short returnCode, string message)
        {
            ServerLog.Error($"CreateRoom failed: code={returnCode} message='{message}' — host cycle will retry");
        }
    }

    /// <summary>
    /// Log the successful room creation (also proves the full master→game relay round-trip).
    /// </summary>
    [HarmonyPatch(typeof(NetworkConnector), nameof(NetworkConnector.OnCreatedRoom))]
    internal static class CreatedRoomPatch
    {
        [HarmonyPostfix]
        public static void LogSuccess()
        {
            var room = PhotonNetwork.CurrentRoom;
            ServerLog.Info($"room created: '{room?.Name}' maxPlayers={room?.MaxPlayers} " +
                           $"open={room?.IsOpen} visible={room?.IsVisible}");
        }
    }
}