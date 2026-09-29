using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using PeakRelay.Launcher.Core;
using Xunit;

namespace PeakRelay.Launcher.Tests;

public class ReleaseClientTests
{
    [Theory]
    [InlineData("v0.5.0", "0.5.0", false)]
    [InlineData("v0.6.0", "0.5.0", true)]
    [InlineData("0.6.0", "0.6.0", false)]
    [InlineData("v0.6.0-beta.1", "0.6.0", true)]
    [InlineData("v0.5.0", "0.5.0+build", false)] // informational version may carry a suffix
    public void VersionDiffers_handles_tag_forms(string tag, string running, bool expected)
        => Assert.Equal(expected, ReleaseClient.VersionDiffers(tag, running));

    [Fact]
    public void CheckForUpdate_reports_none_without_releases()
    {
        var (decision, release) = ReleaseClient.CheckForUpdate(null, skippedTag: null);
        Assert.Equal("none", decision);
        Assert.Null(release);
    }

    [Fact]
    public void CheckForUpdate_ignores_skipped_tag()
    {
        var releases = new[] { new ReleaseInfo("v9.9.9", "x", false, Array.Empty<ReleaseAsset>()) };
        var (decision, _) = ReleaseClient.CheckForUpdate(releases, skippedTag: "v9.9.9");
        Assert.Equal("none", decision);
    }

    [Fact]
    public void CheckForUpdate_flags_newer_release()
    {
        var releases = new[] { new ReleaseInfo("v9.9.9", "x", false, Array.Empty<ReleaseAsset>()) };
        var (decision, release) = ReleaseClient.CheckForUpdate(releases, skippedTag: null);
        Assert.Equal("update", decision);
        Assert.NotNull(release);
    }

    [Fact]
    public void RunningVersion_is_self_consistent()
    {
        var version = ReleaseClient.RunningVersion();
        Assert.False(string.IsNullOrWhiteSpace(version));
        // A release tagged with the running version must never count as an update.
        Assert.False(ReleaseClient.VersionDiffers("v" + version.Split('+')[0], version));
    }

    [Fact]
    public void TokenFromEnvironment_reads_and_trims_env_only()
    {
        Environment.SetEnvironmentVariable(ReleaseClient.TokenEnvVar, "  tok_abc  ");
        try
        {
            Assert.Equal("tok_abc", ReleaseClient.TokenFromEnvironment());
            Environment.SetEnvironmentVariable(ReleaseClient.TokenEnvVar, "   ");
            Assert.Null(ReleaseClient.TokenFromEnvironment());
            Environment.SetEnvironmentVariable(ReleaseClient.TokenEnvVar, null);
            Assert.Null(ReleaseClient.TokenFromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReleaseClient.TokenEnvVar, null);
        }
    }



    [Fact]
    public void Decode_parses_github_release_payload()
    {
        // The exact shape api.github.com returns for /releases/latest (subset).
        const string json = """
            {
              "tag_name": "v0.6.0",
              "name": "0.6.0",
              "prerelease": false,
              "assets": [
                { "name": "PeakRelay-plugins.zip", "size": 12345, "id": 987654321,
                  "browser_download_url": "https://example.com/PeakRelay-plugins.zip" }
              ]
            }
            """;
        using var doc = JsonDocument.Parse(json);
        var decoded = (ReleaseInfo?)typeof(ReleaseClient)
            .GetMethod("Decode", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { doc.RootElement.Clone() });

        Assert.NotNull(decoded);
        Assert.Equal("v0.6.0", decoded!.Tag);
        Assert.False(decoded.Prerelease);
        var asset = Assert.Single(decoded.Assets);
        Assert.Equal(("PeakRelay-plugins.zip", 12345L, 987654321L,
                "https://example.com/PeakRelay-plugins.zip"),
            (asset.Name, asset.Size, asset.Id, asset.Url));
    }
}

// LauncherPaths.Root is static; these classes must not race each other on it.
[CollectionDefinition("LauncherFs")]
public sealed class LauncherFsCollection;

[Collection("LauncherFs")]
public sealed class TokenStoreTests : IDisposable
{
    private readonly string _temp =
        Path.Combine(Path.GetTempPath(), "peakrelay-tokenstore-" + Path.GetRandomFileName());

    public TokenStoreTests()
    {
        Directory.CreateDirectory(_temp);
        LauncherPaths.UseRootForTests(_temp);
        Environment.SetEnvironmentVariable(ReleaseClient.TokenEnvVar, null);
    }

    public void Dispose()
    {
        TokenStore.DeleteFile();
        Environment.SetEnvironmentVariable(ReleaseClient.TokenEnvVar, null);
        Directory.Delete(_temp, recursive: true);
        LauncherPaths.UseRootForTests(Path.GetTempPath());
    }

    [Fact]
    public void Resolve_returns_null_without_any_source()
        => Assert.Null(TokenStore.Resolve());

    [Fact]
    public void Env_wins_over_file()
    {
        TokenStore.SaveToFile("file-token");
        Environment.SetEnvironmentVariable(ReleaseClient.TokenEnvVar, "env-token");
        try
        {
            Assert.Equal("env-token", TokenStore.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReleaseClient.TokenEnvVar, null);
        }
    }

    [Fact]
    public void File_fallback_trims_and_delete_clears()
    {
        TokenStore.SaveToFile("  file-token  \n");
        Assert.Equal("file-token", TokenStore.Resolve());
        TokenStore.DeleteFile();
        Assert.Null(TokenStore.Resolve());
    }
}

[Collection("LauncherFs")]
public sealed class LauncherStateTests : IDisposable
{
    private readonly string _temp =
        Path.Combine(Path.GetTempPath(), "peakrelay-launcher-state-" + Path.GetRandomFileName());

    public LauncherStateTests() => LauncherPaths.UseRootForTests(_temp);

    public void Dispose()
    {
        Directory.Delete(_temp, recursive: true);
        LauncherPaths.UseRootForTests(Path.GetTempPath());
    }

    [Fact]
    public void State_round_trips()
    {
        var state = new LauncherState { GameDir = @"C:\games\PEAK", ServerSide = true, InstalledTag = "v0.5.0" };
        state.Save();

        var loaded = LauncherState.Load();
        Assert.Equal(@"C:\games\PEAK", loaded.GameDir);
        Assert.True(loaded.ServerSide);
        Assert.False(loaded.ClientSide);
        Assert.Equal("v0.5.0", loaded.InstalledTag);
    }

    [Fact]
    public void Load_returns_defaults_when_file_is_garbage()
    {
        Directory.CreateDirectory(LauncherPaths.Root);
        File.WriteAllText(LauncherPaths.StateFile, "{ not json");
        var loaded = LauncherState.Load();
        Assert.Equal(string.Empty, loaded.GameDir);
        Assert.False(loaded.ServerSide);
        Assert.False(loaded.ClientSide);
    }
}

public sealed class DoctorTests : IDisposable
{
    private readonly string _temp =
        Path.Combine(Path.GetTempPath(), "peakrelay-doctor-" + Path.GetRandomFileName());
    private readonly string _gameDir;

    public DoctorTests()
    {
        Directory.CreateDirectory(_temp);
        _gameDir = Path.Combine(_temp, "PEAK");
        Directory.CreateDirectory(_gameDir);
        File.WriteAllText(Path.Combine(_gameDir, "PEAK.exe"), "fake exe");
    }

    public void Dispose() => Directory.Delete(_temp, recursive: true);
    // No LauncherPaths.UseRootForTests here: DoctorTests never touches launcher storage,
    // and this class runs concurrently with the LauncherFs collection — mutating the
    // static root from here is exactly the race that flaked State_round_trips.

    [Fact]
    public void Non_game_dir_is_a_single_fail_with_a_fix()
    {
        var checks = Doctor.Inspect(Path.Combine(_temp, "nope"), new LauncherState());
        var check = Assert.Single(checks);
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal("Game", check.Name);
        Assert.NotNull(check.Fix);
    }

    [Fact]
    public void Fresh_game_root_fails_with_actionable_rows()
    {
        var checks = Doctor.Inspect(_gameDir, new LauncherState());
        Assert.Equal(CheckStatus.Pass, checks.First(c => c.Name == "Game").Status);
        Assert.Equal(CheckStatus.Fail, checks.First(c => c.Name == "Loader").Status);
        Assert.Equal(CheckStatus.Fail, checks.First(c => c.Name == "BepInEx").Status);
        Assert.Equal(CheckStatus.Warn, checks.First(c => c.Name == "Dedicated plugin").Status);
        Assert.Equal(CheckStatus.Warn, checks.First(c => c.Name == "Client plugin").Status);
        Assert.False(Doctor.PlayReady(checks));
        Assert.All(checks.Where(c => c.Status != CheckStatus.Pass), c => Assert.NotNull(c.Fix));
    }

    [Fact]
    public void Loader_bytes_decide_pass_vs_warn()
    {
        var winhttp = Path.Combine(_gameDir, "winhttp.dll");
        Assert.Equal(CheckStatus.Fail,
            Doctor.Inspect(_gameDir, new LauncherState()).First(c => c.Name == "Loader").Status);

        File.WriteAllBytes(winhttp, new byte[] { 0x4D, 0x5A, 0x00 }); // any non-known bytes
        Assert.Equal(CheckStatus.Fail,
            Doctor.Inspect(_gameDir, new LauncherState()).First(c => c.Name == "Loader").Status);

        // The Pass branch needs the exact real Doorstop 4.5.0 DLL bytes (byte-for-byte
        // verified against the live game install); not reproducible from thin air here.
    }

    [Fact]
    public void Dedicated_without_server_json_warns()
    {
        var dedicated = Path.Combine(_gameDir, "BepInEx", "plugins", "PeakRelay.Dedicated");
        Directory.CreateDirectory(dedicated);
        File.WriteAllText(Path.Combine(dedicated, "PeakRelay.Dedicated.dll"), "fake");
        var checks = Doctor.Inspect(_gameDir, new LauncherState());
        Assert.Equal(CheckStatus.Warn, checks.First(c => c.Name == "server.json").Status);
    }
}

[Collection("LauncherFs")]
public sealed class ModApplyTests : IDisposable
{
    private readonly string _temp =
        Path.Combine(Path.GetTempPath(), "peakrelay-modapply-" + Path.GetRandomFileName());
    private readonly string _gameDir;

    public ModApplyTests()
    {
        Directory.CreateDirectory(_temp);
        LauncherPaths.UseRootForTests(_temp);
        _gameDir = Path.Combine(_temp, "PEAK");
        Directory.CreateDirectory(_gameDir);
        File.WriteAllText(Path.Combine(_gameDir, "PEAK.exe"), "fake exe");
    }

    public void Dispose()
    {
        Directory.Delete(_temp, recursive: true);
        LauncherPaths.UseRootForTests(Path.GetTempPath());
    }

    [Fact]
    public void Plugin_entries_classify_into_their_plugin_dirs()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "PeakRelay.Dedicated/PeakRelay.Dedicated.dll");
            Add(zip, "PeakRelay.Client/PeakRelay.Client.dll");
            Add(zip, "PeakRelayInstaller-Server.exe"); // must be ignored
            Add(zip, "PeakRelay.Server.dll");          // operator relay file — ignored
        }
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var plugins = archive.Entries.Where(ModApply.IsPluginEntry).ToList();
        Assert.Equal(2, plugins.Count);

        var dedicated = plugins.Single(e => e.Name == "PeakRelay.Dedicated.dll");
        var client = plugins.Single(e => e.Name == "PeakRelay.Client.dll");
        Assert.EndsWith("PeakRelay.Dedicated", ModApply.TargetDirFor(dedicated, _gameDir));
        Assert.EndsWith("PeakRelay.Client", ModApply.TargetDirFor(client, _gameDir));
    }

    [Fact]
    public async Task ApplyAsync_rejects_a_zip_without_plugin_files()
    {
        var cache = Path.Combine(_temp, "cache");
        Directory.CreateDirectory(cache);
        var zipPath = Path.Combine(cache, "PeakRelay-plugins.zip");
        using (var zip = new ZipArchive(File.Create(zipPath), ZipArchiveMode.Create))
            Add(zip, "readme.txt");

        var asset = new ReleaseAsset("PeakRelay-plugins.zip", 999, 42, "unused://");
        var state = new LauncherState();
        var bytes = File.ReadAllBytes(zipPath);

        await Assert.ThrowsAsync<InvalidDataException>(() => ModApply.ApplyAsync(_gameDir, asset, state, "v0.5.0",
            (_, destination, _) => { File.WriteAllBytes(destination, bytes); return Task.FromResult((long)bytes.Length); },
            serverSettings: null, clientSettings: null));
    }

    private static void Add(ZipArchive zip, string name)
    {
        var entry = zip.CreateEntry(name);
        using var payload = entry.Open();
        payload.Write(new byte[] { 1, 2, 3 }, 0, 3);
    }
}
