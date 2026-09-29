using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using PeakRelay.Installer.Core;

namespace PeakRelay.Launcher.Core;

/// <summary>
/// Applies a release asset to a game install. Two asset shapes exist today:
/// <list type="bullet">
/// <item>installer zips (contain a PeakRelayInstaller-*.exe) — the plugin DLLs come from the
/// launcher's own embedded payload, and the installer exe is skipped (re-launching a GUI
/// wizard is not "apply");</item>
/// <item>operator zips (PeakRelay-plugins.zip / PeakRelay.Server.zip) — plugin folders are
/// copied straight over the game's BepInEx plugins.</item>
/// </list>
/// In both cases the Doorstop 4.5.0 loader and BepInEx core always come from the launcher's
/// embedded payload — never from the download — so a compromised/partial asset cannot
/// replace the executable-level loader bytes the doctor verifies.
/// </summary>
public static class ModApply
{
    private static readonly byte[] BepInPluginMarker = System.Text.Encoding.ASCII.GetBytes("BepInPlugin");

    /// <summary>Test seam: replace the embedded-payload EnsureBepInEx step in unit tests.</summary>
    internal static Func<string, List<string>>? EnsureBepInExOverride;

    /// <summary>
    /// Applies <paramref name="asset"/> to the PLAY install and returns progress/log lines.
    /// The play install is always PLAYER-ONLY: dedicated files are never installed here and
    /// any leftovers from older versions are removed (hosting runs from the ServerCopy).
    /// Loader + BepInEx core always come from the embedded payload, never the download.
    /// </summary>
    public static async Task<List<string>> ApplyAsync(string gameDir, ReleaseAsset asset,
        LauncherState state, string tag,
        Func<ReleaseAsset, string, CancellationToken, Task<long>> download,
        string? host, int port,
        string? room = null, string? password = null, string? hostName = null, int maxPlayers = 20,
        CancellationToken token = default)
    {
        var log = new List<string>();
        if (!GameLocator.LooksLikeGameRoot(gameDir))
            throw new InvalidOperationException($"'{gameDir}' is not a PEAK install (no PEAK.exe)");

        var cachePath = Path.Combine(LauncherPaths.RelayDir, "assets");
        Directory.CreateDirectory(cachePath);
        var tempPath = Path.Combine(cachePath, asset.Name + ".download");

        log.Add($"downloading {asset.Name} ({asset.Size / 1024.0:F0} KB)…");
        var bytes = await download(asset, tempPath, token).ConfigureAwait(false);
        log.Add($"downloaded {bytes / 1024.0:F0} KB");

        await using (var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.Asynchronous))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            var classified = ClassifyPluginEntries(zip, gameDir).ToList();
            // Play install: client files only — dedicated belongs to the server copy.
            classified.RemoveAll(c => c.TargetDir == Layout.DedicatedPluginDir(gameDir));
            if (classified.Count == 0)
                throw new InvalidDataException($"'{asset.Name}' contains no client plugin files");

            foreach (var (entry, targetDir) in classified)
            {
                // Attribute scan on the plugin ENTRY assemblies only (Protocol.dll is a
                // library and legitimately has no [BepInPlugin]). An entry without the
                // attribute would load as "0 plugins" — silently dead. Reject the asset
                // before touching the game.
                var isEntry = entry.Name.Equals("PeakRelay.Client.dll", StringComparison.OrdinalIgnoreCase)
                              || entry.Name.Equals("PeakRelay.Dedicated.dll", StringComparison.OrdinalIgnoreCase);
                byte[] dllBytes;
                using (var pe = entry.Open())
                {
                    var ms = new MemoryStream();
                    pe.CopyTo(ms);
                    dllBytes = ms.ToArray();
                }
                if (isEntry && dllBytes.AsSpan().IndexOf(BepInPluginMarker) < 0)
                    throw new InvalidDataException(
                        $"{entry.Name} has no BepInPlugin attribute - refusing to apply '{asset.Name}'");
                Directory.CreateDirectory(targetDir);
                entry.ExtractToFile(Path.Combine(targetDir, entry.Name), overwrite: true);
                log.Add($"applied {Path.GetFileName(targetDir)}/{entry.Name}");
            }
        }

        RemoveDedicatedSide(gameDir, log); // legacy cleanup: dedicated files never live in the play install

        // Loader + BepInEx core always from the embedded payload (see class comment).
        var ensureBepInEx = EnsureBepInExOverride ?? PluginDeployer.EnsureBepInEx;
        foreach (var line in ensureBepInEx(gameDir))
            log.Add(line);

        // Side settings NOW — after extraction, so newly added sides get configured too.
        var (serverSettings, clientSettings) = ConfigFor(gameDir, host, port, room, password, hostName, maxPlayers);
        if (serverSettings != null)
            log.Add($"server.json written: {PluginDeployer.WriteServerConfig(gameDir, serverSettings)}");
        if (clientSettings != null)
            log.Add($"client config written: {PluginDeployer.WriteClientConfig(gameDir, clientSettings)}");

        state.GameDir = gameDir;
        state.InstalledTag = tag;
        state.InstalledAsset = asset.Name;
        state.ServerSide = Directory.Exists(Layout.DedicatedPluginDir(gameDir)) &&
                           File.Exists(Path.Combine(Layout.DedicatedPluginDir(gameDir), "PeakRelay.Dedicated.dll"));
        state.ClientSide = Directory.Exists(Layout.ClientPluginDir(gameDir)) &&
                           File.Exists(Path.Combine(Layout.ClientPluginDir(gameDir), "PeakRelay.Client.dll"));
        state.Save();

        File.Move(tempPath, Path.Combine(cachePath, asset.Name), overwrite: true);
        log.Add($"applied {tag} ({asset.Name}) to {gameDir}");
        return log;
    }

    /// <summary>Best asset for applying mods: the operator plugins zip, else any PeakRelay zip.</summary>
    public static ReleaseAsset DefaultAssetFor(ReleaseInfo release)
    {
        var asset = release.Assets.FirstOrDefault(a => a.Name.Equals("PeakRelay-plugins.zip", StringComparison.OrdinalIgnoreCase))
                    ?? release.Assets.FirstOrDefault(a => a.Name.Contains("PeakRelay", StringComparison.OrdinalIgnoreCase)
                                                          && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (asset == null)
            throw new InvalidOperationException(
                $"release {release.Tag} has no applicable zip asset (needs PeakRelay-plugins.zip or similar)");
        return asset;
    }

    /// <summary>
    /// Side settings for the config files: configure whatever the install has on disk
    /// (server.json only when the dedicated plugin is there, client cfg likewise). Callers
    /// must invoke this AFTER applying plugin files. Same defaults as the installers' CLI.
    /// </summary>
    public static (ServerSettings? Server, ClientSettings? Client) ConfigFor(
        string gameDir, string? host, int port,
        string? room = null, string? password = null, string? hostName = null, int maxPlayers = 20)
    {
        var relayHost = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
        ServerSettings? server = File.Exists(Path.Combine(Layout.DedicatedPluginDir(gameDir), "PeakRelay.Dedicated.dll"))
            ? new ServerSettings(
                RoomName: room ?? "DBPEAK",
                DisplayName: hostName != null ? $"{hostName} (PeakRelay)" : "PeakRelay Dedicated",
                Mode: "standard",
                Password: password ?? "",
                MaxPlayers: maxPlayers,
                RelayHost: relayHost,
                RelayPort: port,
                AutoHost: true,
                HostName: hostName ?? "DedicatedHost")
            : null;
        ClientSettings? client = File.Exists(Path.Combine(Layout.ClientPluginDir(gameDir), "PeakRelay.Client.dll"))
            ? new ClientSettings(relayHost, port)
            : null;
        return (server, client);
    }

    /// <summary>Removes the dedicated-host plugin files and server.json (player-only mode).</summary>
    internal static void RemoveDedicatedSide(string gameDir, List<string>? log = null)
    {
        var dedicatedDir = Layout.DedicatedPluginDir(gameDir);
        if (!Directory.Exists(dedicatedDir))
            return;
        Directory.Delete(dedicatedDir, recursive: true);
        log?.Add("removed dedicated-host files (player-only install)");
    }

    internal static bool IsPluginEntry(ZipArchiveEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Name))
            return false; // directory entries
        var name = entry.FullName.Replace('\\', '/');
        return name.Contains("PeakRelay.Dedicated/", StringComparison.OrdinalIgnoreCase)
               || name.Contains("PeakRelay.Client/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One plugin zip entry → the game-install dir it lands in. Dedicated over Client wins;
    /// anything else in the asset is ignored (installer exes, relay payloads, READMEs).
    /// </summary>
    internal static string TargetDirFor(ZipArchiveEntry entry, string gameDir)
    {
        var name = entry.FullName.Replace('\\', '/');
        return name.Contains("PeakRelay.Dedicated/", StringComparison.OrdinalIgnoreCase)
            ? Layout.DedicatedPluginDir(gameDir)
            : Layout.ClientPluginDir(gameDir);
    }

    private static IEnumerable<(ZipArchiveEntry Entry, string TargetDir)> ClassifyPluginEntries(ZipArchive zip, string gameDir)
    {
        foreach (var entry in zip.Entries.Where(IsPluginEntry))
            yield return (entry, TargetDirFor(entry, gameDir));
    }
}
