using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Steamworks;
using Unity.Multiplayer.PlayMode;

namespace PeakRelay.Dedicated;

/// <summary>
/// Runs the game without Steam (dedicated-server requirement, config: noSteam).
///
/// The game ships a built-in no-Steam mode gated on the play-mode tag "NoSteam":
/// GameHandler.Start then skips SteamManager (the component whose Awake calls
/// SteamAPI.RestartAppIfNecessary(3527290) → Application.Quit — the shutdown seen in the
/// first no-Steam attempt), skips SteamLobbyHandler/SteamAuthTicketService, and NetCode /
/// RichPresenceService / GetBestUserID fall back to NoMatchmaking / NoRichPresence /
/// PlayerPrefs UserID. The only access to the tag list is the CurrentPlayer.Tags getter,
/// so one postfix there flips every call site at once (Tags has no public mutator).
///
/// Not everything is tag-guarded, so two small layers are added on top:
/// • SteamAPI.RestartAppIfNecessary/Init prefixes — a scene-placed SteamManager (it can
///   also be added via AddComponent) must never quit the process or touch native init;
///   all downstream Steamworks use guards on SteamManager.Initialized, so returning false
///   makes the whole stack a safe no-op.
/// • Nickname fallbacks — NetworkingUtilities.GetUsername and
///   SteamRuntimeManager.NickName read SteamFriends.GetPersonaName() unguarded, which
///   without Steam init logs native errors; they return the configured host name instead.
/// </summary>
[HarmonyPatch]
internal static class SteamBypassPatch
{
    private const string NoSteamTag = "NoSteam";

    /// <summary>noSteam resolved: explicit config wins, else auto = headless process.</summary>
    internal static bool NoSteamActive =>
        DedicatedState.Config?.NoSteam ?? DedicatedPatches.IsHeadless;

    /// <summary>
    /// Every CurrentPlayer.Tags read flows through this getter; merging "NoSteam" in here
    /// arms the game's own no-Steam branches before GameHandler.Start runs. When the mode
    /// is off this is a pure pass-through.
    /// </summary>
    [HarmonyPatch(typeof(CurrentPlayer), nameof(CurrentPlayer.Tags), MethodType.Getter)]
    [HarmonyPostfix]
    private static void InjectNoSteamTag(ref IReadOnlyList<string> __result)
    {
        if (!NoSteamActive || __result.Contains(NoSteamTag))
            return;
        __result = __result.Concat(new[] { NoSteamTag }).ToList();
    }

    /// <summary>
    /// Steamworks.NET restarts the app through Steam when launched outside the client —
    /// the exact shutdown that killed the first headless no-Steam run. Never restart.
    /// </summary>
    [HarmonyPatch(typeof(SteamAPI), nameof(SteamAPI.RestartAppIfNecessary))]
    [HarmonyPrefix]
    private static bool NeverRestartThroughSteam(ref bool __result)
    {
        if (!NoSteamActive)
            return true;
        __result = false;
        return false;
    }

    /// <summary>
    /// Steam init without the Steam client fails anyway (and would emit native errors);
    /// skipping it keeps SteamManager.Initialized false, which every consumer guards on.
    /// </summary>
    [HarmonyPatch(typeof(SteamAPI), nameof(SteamAPI.Init))]
    [HarmonyPrefix]
    private static bool SkipSteamInit(ref bool __result)
    {
        if (!NoSteamActive)
            return true;
        __result = false;
        return false;
    }

    /// <summary>GetPersonaName without a Steam session logs native errors; use the config name.</summary>
    [HarmonyPatch(typeof(Peak.Network.NetworkingUtilities), nameof(Peak.Network.NetworkingUtilities.GetUsername))]
    [HarmonyPrefix]
    private static bool FallbackUsername(ref string __result)
    {
        if (!NoSteamActive)
            return true;
        __result = DedicatedState.Config?.HostName ?? "DedicatedHost";
        return false;
    }

    /// <summary>Menu UI reads the persona name here; same fallback as GetUsername.</summary>
    [HarmonyPatch(typeof(SteamRuntimeManager), nameof(SteamRuntimeManager.NickName), MethodType.Getter)]
    [HarmonyPrefix]
    private static bool FallbackNickName(ref string __result)
    {
        if (!NoSteamActive)
            return true;
        __result = DedicatedState.Config?.HostName ?? "DedicatedHost";
        return false;
    }

    /// <summary>
    /// The game's boot gate: PlatformBootstrap.Start spins until the platform reports
    /// Initialized — with Steam absent that never happens, because
    /// SteamRuntimeManager.Initialized ⇒ SteamManager.Initialized and the SteamManager
    /// component is skipped in no-Steam mode (two live runs stalled at "Waiting for
    /// Windows to Initialise" and never reached Title). Fire the scene-wired onComplete
    /// directly instead of faking Initialized, which would open the guarded native calls
    /// (RunCallbacks, GetAppID, GetPersonaName) that every other consumer still checks.
    /// </summary>
    [HarmonyPatch(typeof(PlatformBootstrap), "Start")]
    [HarmonyPrefix]
    private static bool CompleteBootstrapWithoutSteam(PlatformBootstrap __instance)
    {
        if (!NoSteamActive)
            return true;
        var onComplete = AccessTools.Field(typeof(PlatformBootstrap), "onComplete")
            ?.GetValue(__instance) as UnityEngine.Events.UnityEvent;
        ServerLog.Info("platform bootstrap completed without Steam (onComplete fired)");
        onComplete?.Invoke();
        return false;
    }

    /// <summary>
    /// NetworkConnector.Start/OnConnectedToMaster call PrintNetworkStates, which reads
    /// GameHandler.GetService&lt;SteamLobbyHandler&gt;() — a service that no-Steam mode never
    /// registers. The unguarded lookup throws and aborts OnConnectedToMaster BEFORE
    /// HandleConnectionState runs (the root cause of the "connected before HostState was
    /// armed" race every with-Steam run had masked). Skip the diagnostic dump; the state
    /// machine flow itself then runs vanilla.
    /// </summary>
    [HarmonyPatch(typeof(NetworkConnector), nameof(NetworkConnector.PrintNetworkStates))]
    [HarmonyPrefix]
    private static bool SkipNetworkStateDump()
    {
        return !NoSteamActive;
    }
}
