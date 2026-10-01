using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace PeakRelay.Dedicated;

/// <summary>
/// Applies the active gamemode to the game's own RunSettings/Ascents engine and keeps it
/// authoritative. Two mechanisms:
///
/// 1. Run-start assert (Harmony postfix on AirportCheckInKiosk.BeginIslandLoadRPC): the
///    kiosk RPC carries the kiosk user's own settings byte[] and OVERWRITES
///    Ascents.currentAscent + RunSettings on everyone — including this dedicated host.
///    Our postfix runs after the game's method body and re-applies the mode, then
///    re-broadcasts via RunSettings.PushRunSettings (late joiners additionally get the
///    host state from GameUtils.OnPlayerEnteredRoom). The kiosk applies its bytes inline
///    in that same method, so no separate sync-guard patch is needed.
///
/// 2. Sandbox no-ghost watcher (Tick, driven from DedicatedPlugin.Update): in modes with
///    SandboxNoGhost, any player dead or fully passed out for more than a few seconds is
///    revived at the current base-camp spawn point via the game's own
///    Character.RPCA_ReviveAtPosition RPC (the same one respawn chests use), with
///    applyStatus=false so no curse/hunger penalty is applied. Nobody stays a ghost and
///    a run cannot soft-lock when the last climber dies.
///
/// Everything here only acts on the headless dedicated process (DedicatedPatches.IsHeadless);
/// normal clients loading the Dedicated plugin are untouched by design.
/// </summary>
internal static class GameModeEngine
{
    /// <summary>Seconds a player must be dead/fully-passed-out before the host revives them.</summary>
    internal const float AutoReviveGraceSeconds = 5f;

    private static GameModeDefinition _active = GameModeCatalog.Standard;
    private static string? _overrideName; // set live by the 'mode <name>' console command
    private static string _configuredName = "";

    /// <summary>The mode currently in force (override via 'mode <name>' wins over config).</summary>
    internal static GameModeDefinition Active => _active;

    /// <summary>
    /// Resolves the configured mode at boot: scans the gamemodes folder, picks the mode,
    /// and tells the operator what is active (falling back to standard with a warning when
    /// the configured name is unknown). Never throws.
    /// </summary>
    internal static void Initialize(string pluginDir, string configuredMode)
    {
        _configuredName = configuredMode?.Trim() ?? "";
        GameModeCatalog.Reload(pluginDir);

        foreach (var warning in GameModeCatalog.LoadWarnings)
            ServerConsole.Warn(warning);

        var found = GameModeCatalog.Find(_configuredName);
        if (found != null)
        {
            _active = found;
        }
        else
        {
            if (_configuredName.Length > 0)
                ServerConsole.Warn($"Unknown gamemode '{_configuredName}' — using 'standard'. " +
                                   "Type 'mode' to see the available modes.");
            _active = GameModeCatalog.Standard;
        }

        ServerConsole.Message($"Gamemode '{_active.Name}' — {_active.Description}");
    }

    /// <summary>Live switch from the 'mode <name>' console command. Reports honestly.</summary>
    internal static string SwitchTo(string name)
    {
        var mode = GameModeCatalog.Find(name);
        if (mode == null)
        {
            var available = string.Join(", ", GameModeCatalog.All.Select(m => m.Name));
            return $"I don't know the gamemode '{name}'. Available: {available}.";
        }
        _overrideName = mode.Name;
        _active = mode;
        var persistence = mode.FilePath != null
            ? "It comes from a file, so it survives restarts."
            : "Put this name in server.json (\"mode\") to keep it across restarts.";
        return $"Gamemode switched to '{mode.Name}' — applies from the next run start. {persistence}";
    }

    /// <summary>Clears the live override (a server restart re-reads server.json).</summary>
    internal static void ResetOverrideForTests() => _overrideName = null;

    /// <summary>Run-start hook (kiosk postfix). Re-asserts the mode after the kiosk's bytes.</summary>
    internal static void OnRunStarting(string sceneName)
    {
        var notes = Apply(_active, out var applied);
        if (applied)
            ServerConsole.Success($"Run '{sceneName}' starting — gamemode '{_active.Name}' asserted ({notes}).");
        else
            ServerConsole.Message($"Run '{sceneName}' starting — gamemode '{_active.Name}' is vanilla (nothing forced).");
    }

    /// <summary>
    /// Forces the mode's ascent + settings onto the game engine and broadcasts them.
    /// Returns a short human summary; out flag tells whether anything was forced at all.
    /// </summary>
    internal static string Apply(GameModeDefinition mode, out bool applied)
    {
        applied = false;
        var notes = new List<string>();
        try
        {
            if (mode.Ascent is int ascent)
            {
                Ascents.currentAscent = ascent;
                notes.Add($"ascent {ascent}");
                applied = true;
            }
            if (mode.Settings.Count > 0)
            {
                RunSettings.IsCustomRun = true; // the game only honors overrides in custom runs
                foreach (var (setting, value) in mode.Settings)
                {
                    var type = Enum.Parse<RunSettings.SETTINGTYPE>(setting);
                    if (!RunSettings.SetValue(type, value))
                        notes.Add($"{setting} rejected by game");
                }
                RunSettings.PushRunSettings();
                notes.Add($"{mode.Settings.Count} setting(s)");
                applied = true;
            }
        }
        catch (Exception ex)
        {
            ServerConsole.Error($"Gamemode '{mode.Name}' could not be applied: {ex.Message}");
        }
        return string.Join(", ", notes);
    }

    /// <summary>
    /// Per-frame housekeeping (called from DedicatedPlugin.Update): drives the sandbox
    /// no-ghost auto-revive. Lives in SandboxNoGhostWatcher so test hosts never load the
    /// game assembly through this class.
    /// </summary>
    internal static void Tick()
    {
        if (_active.SandboxNoGhost && DedicatedPatches.IsHeadless && PhotonNetwork.InRoom)
            SandboxNoGhostWatcher.Tick();
        else
            SandboxNoGhostWatcher.Reset();
    }

    // ---------------------------------------------------------------- setting validation

    /// <summary>
    /// Setting names the game actually registers (RunSettings.InitializeDefaultValues) and
    /// therefore honors. The SETTINGTYPE enum contains a few more entries (Romance_*,
    /// Firearms, MonkeyMode, newer hazards) that the game never registers — files using
    /// those are rejected at load instead of silently doing nothing.
    /// </summary>
    private static readonly HashSet<string> KnownSettings = new(StringComparer.Ordinal)
    {
        "Fog", "HungerRate", "FallDamage", "EtcDamage", "PetrificationDamage", "ColdNight",
        "ItemWeight", "FlaresAtPeak", "ClimbingStaminaUsage", "RevivalAllowed",
        "Hazard_Jellyfish", "Hazard_Urchins", "Hazard_ExplodingMushrooms", "Hazard_Rain",
        "Hazard_Bees", "Hazard_Zombies", "Hazard_Beetles", "Hazard_Spiders",
        "Hazard_SporeClouds", "Hazard_Wind", "Hazard_Storm", "Hazard_FlashPlant",
        "Hazard_Geysers", "Hazard_Tornado", "Hazard_Antlion", "Hazard_Scorpions",
        "Hazard_Tumbleweeds", "Hazard_Condors", "Hazard_TheLavaRises", "Hazard_Thorns",
        "Hazard_Dynamite", "Hazard_Heat", "Hazard_NapberryHypno", "Hazard_Mandrake",
        "Hazard_Scoutmaster", "Hazard_TheGloomRises", "Hazard_SleepyGloom",
        "Hazard_FrogTongues", "Hazard_Ravens", "Hazard_BigGhost", "Hazard_Flytraps",
        "Hazard_ArrowTraps", "Hazard_SpikeTraps", "Hazard_SwingingSpikeballs",
        "Hazard_Sawblades", "Hazard_SpinnySpikes", "Hazard_EtcTraps",
        "Hazard_PetrifyingStones", "Hazard_TrapChest",
        "MiniRun", "MiniRunBiome", "GameMode", "GrappleMode", "ChaosItems",
        "HelpingHand", "TimeScale",
    };

    /// <summary>True when a gamemode file may use this setting name.</summary>
    internal static bool IsKnownSetting(string name) => KnownSettings.Contains(name);
}

// ---------------------------------------------------------------- sandbox no-ghost watcher

/// <summary>
/// The sandbox no-ghost auto-revive: watches all characters and revives anyone dead or
/// fully passed out for more than a few seconds at the current base-camp spawn point via
/// the game's own Character.RPCA_ReviveAtPosition RPC (applyStatus=false — no curse or
/// hunger penalty). Kept in its own class so test hosts never touch game types through
/// GameModeEngine.
/// </summary>
internal static class SandboxNoGhostWatcher
{
    private static readonly Dictionary<Character, float> DeadSince = new();

    internal static void Reset()
    {
        if (DeadSince.Count > 0)
            DeadSince.Clear();
    }

    internal static void Tick()
    {
        List<Character>? ready = null;
        foreach (var character in Character.AllCharacters)
        {
            if (character == null)
                continue;
            bool dead;
            try { dead = character.data.dead || character.data.fullyPassedOut; }
            catch { continue; } // teardown races — skip this frame

            if (!dead)
            {
                DeadSince.Remove(character);
                continue;
            }
            var since = DeadSince.TryGetValue(character, out var t)
                ? t
                : (DeadSince[character] = Time.unscaledTime);
            if (Time.unscaledTime - since >= GameModeEngine.AutoReviveGraceSeconds)
                (ready ??= new List<Character>()).Add(character);
        }

        if (ready == null)
            return;

        Vector3 spawn;
        try { spawn = MapHandler.CurrentBaseCampSpawnPoint.position; }
        catch { return; } // not in a loaded run yet — try again next frame

        foreach (var character in ready)
        {
            DeadSince.Remove(character);
            try
            {
                character.photonView.RPC("RPCA_ReviveAtPosition", RpcTarget.All, spawn, false, -1);
                ServerConsole.Success($"Auto-revived {SafeName(character)} at the campfire (sandbox: nobody stays a ghost).");
            }
            catch (Exception ex)
            {
                ServerConsole.Warn($"Auto-revive failed for one player: {ex.Message}");
            }
        }
    }

    private static string SafeName(Character character)
    {
        try
        {
            return character.photonView.Owner?.NickName is { Length: > 0 } nick ? nick : "a player";
        }
        catch { return "a player"; }
    }
}

// ---------------------------------------------------------------- harmony patches

/// <summary>Re-asserts the gamemode after the kiosk's own run-start sync (see engine doc).</summary>
[HarmonyPatch(typeof(AirportCheckInKiosk), "BeginIslandLoadRPC")]
internal static class GameModeRunStartPatch
{
    private static void Postfix(string sceneName)
    {
        try
        {
            if (DedicatedPatches.IsHeadless)
                GameModeEngine.OnRunStarting(sceneName);
        }
        catch
        {
            // never break the game's own run start because of our mode
        }
    }
}
