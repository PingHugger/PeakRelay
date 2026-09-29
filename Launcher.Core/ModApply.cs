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
    /// <summary>Applies <paramref name="asset"/> and returns progress/log lines.</summary>
    public static async Task<List<string>> ApplyAsync(string gameDir, ReleaseAsset asset,
        LauncherState state, string tag,
        Func<ReleaseAsset, string, CancellationToken, Task<long>> download,
        ServerSettings? serverSettings, ClientSettings? clientSettings,
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
            if (classified.Count == 0)
                throw new InvalidDataException($"'{asset.Name}' contains no PeakRelay plugin files");

            foreach (var (entry, targetDir) in classified)
            {
                // Attribute scan: a plugin DLL without [BepInPlugin] would load as "0
                // plugins" — silently dead. Reject the whole asset before touching the game.
                using var pe = entry.Open();
                using var ms = new MemoryStream();
                pe.CopyTo(ms);
                if (ms.ToArray().AsSpan().IndexOf(BepInPluginMarker) < 0)
                    throw new InvalidDataException(
                        $"{entry.Name} has no BepInPlugin attribute - refusing to apply '{asset.Name}'");
                entry.ExtractToFile(Path.Combine(targetDir, entry.Name), overwrite: true);
                log.Add($"applied {Path.GetFileName(targetDir)}/{entry.Name}");
            }
        }

        // Loader + BepInEx core always from the embedded payload (see class comment).
        foreach (var line in PluginDeployer.EnsureBepInEx(gameDir))
            log.Add(line);

        if (serverSettings != null)
            log.Add($"server.json written: {PluginDeployer.WriteServerConfig(gameDir, serverSettings)}");
        if (clientSettings != null)
            log.Add($"client config written: {PluginDeployer.WriteClientConfig(gameDir, clientSettings)}");

        state.GameDir = gameDir;
        state.InstalledTag = tag;
        state.InstalledAsset = asset.Name;
        state.ServerSide |= Directory.Exists(Layout.DedicatedPluginDir(gameDir)) &&
                            File.Exists(Path.Combine(Layout.DedicatedPluginDir(gameDir), "PeakRelay.Dedicated.dll"));
        state.ClientSide |= Directory.Exists(Layout.ClientPluginDir(gameDir)) &&
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
    /// Side settings for the config files: configure whatever the install actually has on
    /// disk (server.json only when the dedicated plugin is there, client cfg likewise).
    /// Same defaults as the installers' CLI.
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
