using System;
using System.IO;
using System.Linq;
using PeakRelay.Dedicated;
using Xunit;

namespace PeakRelay.Dedicated.Tests;

/// <summary>
/// Gamemode system contract: built-in definitions, JSON loading with friendly validation
/// errors, name resolution, and the mode-switch path. Everything here runs without Unity —
/// the runtime application to the game engine (GameModeEngine.Apply) is exercised only on
/// the dedicated server, where the game assemblies are live.
/// </summary>
public class GameModeTests : IDisposable
{
    private readonly string _dir;

    public GameModeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "peakrelay-gamemodes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        GameModeEngine.ResetOverrideForTests();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
    }

    private string WriteMode(string fileName, string json)
    {
        var folder = GameModeCatalog.FolderFor(_dir);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    // ------------------------------------------------------------ built-ins

    [Fact]
    public void Standard_is_pure_vanilla_with_no_forcing()
    {
        Assert.Equal("standard", GameModeCatalog.Standard.Name);
        Assert.Null(GameModeCatalog.Standard.Ascent);
        Assert.Empty(GameModeCatalog.Standard.Settings);
        Assert.False(GameModeCatalog.Standard.SandboxNoGhost);
    }

    [Fact]
    public void Sandbox_disables_all_damage_and_enables_no_ghost_revival()
    {
        var sandbox = GameModeCatalog.Sandbox;
        Assert.Equal("sandbox", sandbox.Name);
        Assert.True(sandbox.SandboxNoGhost);
        Assert.Equal(-1, sandbox.Ascent); // peaceful ascent: no fog, reduced hunger/stamina

        Assert.Equal(0, sandbox.Settings["FallDamage"]);
        Assert.Equal(0, sandbox.Settings["EtcDamage"]);
        Assert.Equal(0, sandbox.Settings["PetrificationDamage"]);
        Assert.Equal(0, sandbox.Settings["HungerRate"]);
        Assert.Equal(0, sandbox.Settings["Hazard_Zombies"]);
        Assert.Equal(0, sandbox.Settings["Hazard_Scoutmaster"]);
        Assert.Equal(1, sandbox.Settings["RevivalAllowed"]);
    }

    [Fact]
    public void Catalog_always_contains_the_builtins_even_with_no_folder()
    {
        GameModeCatalog.Reload(_dir); // no gamemodes folder yet — must be created, never throw
        Assert.Contains(GameModeCatalog.All, m => m.Name == "standard");
        Assert.Contains(GameModeCatalog.All, m => m.Name == "sandbox");
    }

    [Fact]
    public void First_scan_creates_the_folder_and_the_documented_sample()
    {
        GameModeCatalog.Reload(_dir);
        Assert.True(Directory.Exists(GameModeCatalog.FolderFor(_dir)));
        Assert.True(File.Exists(Path.Combine(GameModeCatalog.FolderFor(_dir), "SAMPLE-custom.txt")));
    }

    // ------------------------------------------------------------ loading valid files

    [Fact]
    public void Valid_custom_mode_is_listed_and_findable()
    {
        WriteMode("chill-climb.json",
            """{ "name": "chill-climb", "description": "Relaxed.", "ascent": -1, "settings": { "FallDamage": 0 } }""");
        GameModeCatalog.Reload(_dir);

        var mode = GameModeCatalog.Find("Chill-Climb"); // case-insensitive
        Assert.NotNull(mode);
        Assert.Equal("file", mode!.Source);
        Assert.Equal(-1, mode.Ascent);
        Assert.Equal(0, mode.Settings["FallDamage"]);
        Assert.Contains(mode, GameModeCatalog.All);
    }

    [Fact]
    public void Commented_json_files_load_fine_like_the_sample_promises()
    {
        WriteMode("c.json",
            """
            {
              // operators copy the commented sample, so comments must be tolerated
              "name": "commented",
              "ascent": 2 // trailing comment
            }
            """);
        GameModeCatalog.Reload(_dir);

        Assert.NotNull(GameModeCatalog.Find("commented"));
        Assert.Empty(GameModeCatalog.LoadWarnings);
    }

    [Fact]
    public void Modes_are_sorted_builtins_first_then_files_alphabetically()
    {
        WriteMode("b-mode.json", """{ "name": "b-mode" }""");
        WriteMode("a-mode.json", """{ "name": "a-mode" }""");
        GameModeCatalog.Reload(_dir);

        Assert.Equal(new[] { "standard", "sandbox", "a-mode", "b-mode" },
            GameModeCatalog.All.Select(m => m.Name).ToArray());
    }

    [Fact]
    public void SandboxNoGhost_flag_is_read_from_files()
    {
        WriteMode("no-ghost.json", """{ "name": "no-ghost", "sandboxNoGhost": true }""");
        GameModeCatalog.Reload(_dir);

        Assert.True(GameModeCatalog.Find("no-ghost")!.SandboxNoGhost);
    }

    // ------------------------------------------------------------ validation errors

    [Theory]
    [InlineData("""{ "name": "bad name!" }""", "name")]                    // invalid characters
    [InlineData("""{ }""", "name")]                                        // missing name
    [InlineData("""not json at all""", "JSON")]                            // not JSON
    [InlineData("""{ "name": "x", "ascent": 12 }""", "ascent")]            // out of range
    [InlineData("""{ "name": "x", "settings": { "Nope_Setting": 1 } }""", "unknown setting")] // unknown setting
    [InlineData("""{ "name": "x", "settings": { "FallDamage": "high" } }""", "whole number")] // bad value type
    public void Broken_mode_files_are_skipped_with_a_friendly_warning(string json, string expectedIn)
    {
        WriteMode("broken.json", json);
        GameModeCatalog.Reload(_dir);

        Assert.DoesNotContain(GameModeCatalog.All, m => m.Name == "x" || m.Name == "broken");
        Assert.Contains(GameModeCatalog.LoadWarnings, w => w.Contains("broken.json") && w.Contains(expectedIn));
    }

    [Fact]
    public void Builtin_names_cannot_be_shadowed_by_files()
    {
        WriteMode("standard.json", """{ "name": "standard", "ascent": 9 }""");
        WriteMode("SANDBOX.json", """{ "name": "SANDBOX", "ascent": 9 }""");
        GameModeCatalog.Reload(_dir);

        Assert.Null(GameModeCatalog.Standard.Ascent); // built-ins untouched
        Assert.Equal(-1, GameModeCatalog.Sandbox.Ascent);
        Assert.Contains(GameModeCatalog.LoadWarnings, w => w.Contains("built-in"));
    }

    [Fact]
    public void Duplicate_names_across_files_keep_the_first_and_warn()
    {
        WriteMode("a-dup.json", """{ "name": "dup" }""");
        WriteMode("z-dup.json", """{ "name": "dup" }""");
        GameModeCatalog.Reload(_dir);

        Assert.Equal(1, GameModeCatalog.All.Count(m => m.Name == "dup"));
        Assert.Contains(GameModeCatalog.LoadWarnings, w => w.Contains("already defines"));
    }

    // ------------------------------------------------------------ resolution & switching

    [Fact]
    public void Initialize_resolves_the_configured_mode()
    {
        GameModeEngine.Initialize(_dir, "sandbox");
        Assert.Equal("sandbox", GameModeEngine.Active.Name);
    }

    [Fact]
    public void Initialize_falls_back_to_standard_on_unknown_names()
    {
        GameModeEngine.Initialize(_dir, "does-not-exist");
        Assert.Equal("standard", GameModeEngine.Active.Name);
    }

    [Fact]
    public void Initialize_without_config_selects_standard()
    {
        GameModeEngine.Initialize(_dir, "");
        Assert.Equal("standard", GameModeEngine.Active.Name);
    }

    [Fact]
    public void SwitchTo_selects_file_modes_and_reports_unknown_names()
    {
        WriteMode("switch-me.json", """{ "name": "switch-me", "ascent": 3 }""");
        GameModeCatalog.Reload(_dir);

        var ok = GameModeEngine.SwitchTo("switch-me");
        Assert.Contains("switch-me", ok);
        Assert.Equal("switch-me", GameModeEngine.Active.Name);

        var bad = GameModeEngine.SwitchTo("nope");
        Assert.Contains("Available:", bad); // friendly, lists what exists
    }

    [Fact]
    public void Setting_validation_knows_game_settings_and_rejects_fantasy()
    {
        Assert.True(GameModeEngine.IsKnownSetting("FallDamage"));
        Assert.True(GameModeEngine.IsKnownSetting("Hazard_Zombies"));
        Assert.True(GameModeEngine.IsKnownSetting("GrappleMode"));
        Assert.False(GameModeEngine.IsKnownSetting("Romance_BingBong")); // never registered by the game
        Assert.False(GameModeEngine.IsKnownSetting("Firearms"));
        Assert.False(GameModeEngine.IsKnownSetting("TotallyMadeUp"));
    }

    [Fact]
    public void Mode_command_is_registered_and_documented()
    {
        var command = ConsoleCommandRegistry.Find("mode");
        Assert.NotNull(command);
        Assert.Equal("mode [name]", command!.Usage);
        Assert.Contains("gamemode", command.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mode_command_lists_all_modes_with_the_active_marker()
    {
        GameModeEngine.Initialize(_dir, "sandbox");
        ServerConsole.TestSink = (text, _) => _seen.Add(text);
        try
        {
            ServerConsole.ExecuteCommand("mode");
            Assert.Contains(_seen, l => l.Contains("standard"));
            Assert.Contains(_seen, l => l.Contains("sandbox"));
            Assert.Contains(_seen, l => l.Contains("←"));
        }
        finally { ServerConsole.TestSink = null; }
    }

    private readonly List<string> _seen = new();
}
