using System;
using System.Linq;
using HarmonyLib;
using Photon.Pun;
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
    /// </summary>
    [HarmonyPatch(typeof(NetworkConnector), "HandleConnectionState")]
    internal static class HostRoomNamePatch
    {
        [HarmonyPrefix]
        public static void KeepConfiguredRoomName(ConnectionState state)
        {
            if (state is not HostState hostState)
                return;
            var config = DedicatedState.Config;
            if (config == null || config.UseVanillaName)
                return;
            if (string.Equals(hostState.RoomName, DedicatedState.ConfiguredRoomName, StringComparison.Ordinal))
                return;
            ServerLog.Info($"HostState.RoomName '{hostState.RoomName}' → '{DedicatedState.ConfiguredRoomName}' (config wins)");
            hostState.RoomName = DedicatedState.ConfiguredRoomName;
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