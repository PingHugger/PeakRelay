using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PeakRelay.Dedicated;

/// <summary>
/// One selectable gamemode: a named preset over the game's own RunSettings/Ascents engine
/// (see GameModePatches for where it is applied). A mode may
/// <list type="bullet">
/// <item>force a specific <see cref="Ascent"/> (null = leave whatever the game flow uses),</item>
/// <item>override named run settings ("FallDamage": 0 — validated against the game's
/// SETTINGTYPE names, values clamped later by the game itself),</item>
/// <item>turn on PeakRelay extras such as <see cref="SandboxNoGhost"/>.</item>
/// </list>
/// Modes come either built-in (standard, sandbox) or from a JSON file in the gamemodes
/// folder next to the plugin DLL — see <see cref="GameModeCatalog"/> for the file rules.
/// </summary>
public sealed class GameModeDefinition
{
    /// <summary>The word operators type (lowercase, letters/digits/dash only).</summary>
    public string Name { get; init; } = "";

    /// <summary>One-line operator-facing description.</summary>
    public string Description { get; init; } = "";

    /// <summary>Ascent the game runs at (-1 peaceful … 9). null = do not force one.</summary>
    public int? Ascent { get; init; }

    /// <summary>RunSettings overrides by setting name, e.g. "FallDamage": 0.</summary>
    public IReadOnlyDictionary<string, int> Settings { get; init; } =
        new Dictionary<string, int>();

    /// <summary>
    /// PeakRelay extra: the host auto-revives dead/fully-passed-out players at the next
    /// campfire a few seconds after death — nobody becomes a ghost and runs cannot
    /// soft-lock. Only meaningful with RevivalAllowed-style peaceful modes.
    /// </summary>
    public bool SandboxNoGhost { get; init; }

    /// <summary>Where this mode came from (console display + diagnostics).</summary>
    public string Source { get; init; } = "builtin";

    /// <summary>File path when loaded from the gamemodes folder, else null.</summary>
    public string? FilePath { get; init; }

    internal static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Length <= 40 &&
        name.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
}

/// <summary>
/// Owns the available gamemodes: the two built-ins plus every valid JSON file from the
/// gamemodes folder. Load errors are collected as friendly console lines (the file is
/// skipped, the server never crashes) and resurfaced by `mode` and `modeapply`.
/// </summary>
internal static class GameModeCatalog
{
    /// <summary>The always-present standard mode: no forcing at all — pure vanilla.</summary>
    internal static readonly GameModeDefinition Standard = new()
    {
        Name = "standard",
        Description = "Vanilla PEAK — the game decides everything (ascent 0, no overrides).",
        Source = "builtin",
    };

    /// <summary>
    /// Sandbox: peaceful exploration. Ascent -1 (no fog, reduced hunger/stamina, no night
    /// cold), all damage and zombie/scoutmaster hazards off, revival on, and PeakRelay's
    /// no-ghost auto-revive so nobody stays dead.
    /// </summary>
    internal static readonly GameModeDefinition Sandbox = new()
    {
        Name = "sandbox",
        Description = "Peaceful sandbox — no fall/other damage, no zombies, and dead players " +
                      "respawn at the next campfire (nobody becomes a ghost).",
        Ascent = -1,
        Settings = new Dictionary<string, int>
        {
            ["FallDamage"] = 0,
            ["EtcDamage"] = 0,
            ["PetrificationDamage"] = 0,
            ["HungerRate"] = 0,
            ["Hazard_Zombies"] = 0,
            ["Hazard_Scoutmaster"] = 0,
            ["RevivalAllowed"] = 1,
        },
        SandboxNoGhost = true,
        Source = "builtin",
    };

    private static readonly List<string> Warnings = new();
    private static readonly List<GameModeDefinition> FileModes = new();

    /// <summary>All selectable modes (standard first, sandbox second, then files a→z).</summary>
    internal static IReadOnlyList<GameModeDefinition> All { get; private set; } =
        new[] { Standard, Sandbox };

    /// <summary>Human-readable load problems from the last Reload (empty when all clean).</summary>
    internal static IReadOnlyList<string> LoadWarnings => Warnings;

    /// <summary>The gamemodes folder next to the plugin DLL.</summary>
    internal static string FolderFor(string pluginDir) =>
        Path.Combine(pluginDir, "gamemodes");

    /// <summary>Rescans the gamemodes folder and rebuilds the mode list. Never throws.</summary>
    internal static void Reload(string pluginDir)
    {
        Warnings.Clear();
        FileModes.Clear();
        try
        {
            var folder = FolderFor(pluginDir);
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            WriteSampleFile(folder); // no-op when it already exists

            foreach (var path in Directory.EnumerateFiles(folder, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var mode = ParseFile(path);
                    if (mode == null)
                        continue;
                    if (mode.Name.Equals(Standard.Name, StringComparison.OrdinalIgnoreCase) ||
                        mode.Name.Equals(Sandbox.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        Warnings.Add($"gamemode '{Path.GetFileName(path)}' ignored: '{mode.Name}' is a built-in name");
                        continue;
                    }
                    if (FileModes.Any(m => m.Name.Equals(mode.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        Warnings.Add($"gamemode '{Path.GetFileName(path)}' ignored: another file already defines '{mode.Name}'");
                        continue;
                    }
                    FileModes.Add(mode);
                }
                catch (Exception ex)
                {
                    Warnings.Add($"gamemode '{Path.GetFileName(path)}' could not be loaded: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Warnings.Add($"gamemodes folder could not be scanned: {ex.Message}");
        }

        All = new[] { Standard, Sandbox }.Concat(FileModes).ToList();
    }

    /// <summary>Finds a mode by name (case-insensitive), null when unknown.</summary>
    internal static GameModeDefinition? Find(string? name) =>
        name == null
            ? null
            : All.FirstOrDefault(m => m.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>JSON schema for one gamemode file.</summary>
    private static GameModeDefinition? ParseFile(string path)
    {
        JObject root;
        try
        {
            // Comment-tolerant: copying the commented SAMPLE file to a .json just works.
            root = JObject.Parse(File.ReadAllText(path),
                new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
        }
        catch (Exception ex) { throw new FormatException($"not valid JSON ({ex.Message})"); }

        var name = (string?)root["name"];
        if (!GameModeDefinition.IsValidName(name))
            throw new FormatException("needs a \"name\" of letters/digits/dash (max 40 chars)");

        var ascent = (int?)root["ascent"];
        if (ascent is < -1 or > 9)
            throw new FormatException($"\"ascent\" must be -1..9 (got {ascent})");

        var settings = new Dictionary<string, int>();
        if (root["settings"] is JObject obj)
        {
            foreach (var prop in obj.Properties())
            {
                // NB: Newtonsoft boxes JSON integers as long — check the token type,
                // not the runtime type of the boxed value.
                if (prop.Value.Type != JTokenType.Integer)
                    throw new FormatException($"settings \"{prop.Name}\" must be a whole number");
                if (!GameModeEngine.IsKnownSetting(prop.Name))
                    throw new FormatException($"unknown setting \"{prop.Name}\" (see the sample file)");
                settings[prop.Name] = (int)prop.Value;
            }
        }

        return new GameModeDefinition
        {
            Name = name!.ToLowerInvariant(),
            Description = ((string?)root["description"])?.Trim() ?? "",
            Ascent = ascent,
            Settings = settings,
            SandboxNoGhost = root["sandboxNoGhost"]?.Type == JTokenType.Boolean && (bool)root["sandboxNoGhost"]!,
            Source = "file",
            FilePath = path,
        };
    }

    /// <summary>Ships a documented example (without .json extension so it never auto-loads).</summary>
    private static void WriteSampleFile(string folder)
    {
        var sample = Path.Combine(folder, "SAMPLE-custom.txt");
        if (File.Exists(sample))
            return;
        File.WriteAllText(sample,
            """
            Copy this file to e.g. "chill-climb.json" to create your own gamemode.

            {
              "name": "chill-climb",            // lowercase letters/digits/dash, this is what you type
              "description": "Relaxed climbing with extra snacks.",
              "ascent": -1,                     // -1 peaceful … 9; omit to keep the game's default flow
              "sandboxNoGhost": false,          // true = PeakRelay auto-revives dead players at the next campfire
              "settings": {                     // game RunSettings overrides (names must match the game)
                "FallDamage": 0,                //   0 off, 1 half, 2 normal, 3 double
                "HungerRate": 1,                //   0 off, 1 slow, 2 normal, 3 fast
                "Hazard_Zombies": 0,            //   0 off, 1 on
                "GrappleMode": 1                //   1 = everyone spawns with the rescue hook
              }
            }

            Useful setting names: Fog, HungerRate, FallDamage, EtcDamage, PetrificationDamage,
            ColdNight, ItemWeight, FlaresAtPeak, ClimbingStaminaUsage, RevivalAllowed,
            GrappleMode, ChaosItems, MiniRun, TimeScale, and every Hazard_* toggle
            (Hazard_Zombies, Hazard_Scoutmaster, Hazard_Bees, Hazard_Geysers, ...).
            Unknown settings are rejected when the file loads, and the mode is skipped.
            """);
    }
}
