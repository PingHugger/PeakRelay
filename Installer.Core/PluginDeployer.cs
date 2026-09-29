using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace PeakRelay.Installer.Core;

/// <summary>Where BepInEx and plugin files land inside a game install.</summary>
public static class Layout
{
    public static string BepInExDir(string gameDir) => Path.Combine(gameDir, "BepInEx");
    public static string PluginsDir(string gameDir) => Path.Combine(BepInExDir(gameDir), "plugins");
    public static string ConfigDir(string gameDir) => Path.Combine(BepInExDir(gameDir), "config");
    public static string DedicatedPluginDir(string gameDir) => Path.Combine(PluginsDir(gameDir), "PeakRelay.Dedicated");
    public static string ClientPluginDir(string gameDir) => Path.Combine(PluginsDir(gameDir), "PeakRelay.Client");
    public static string ServerLog(string gameDir) => Path.Combine(DedicatedPluginDir(gameDir), "server.log");
}

/// <summary>
/// All install operations, driven identically by the GUI wizards and the CLI flags.
/// Everything is idempotent: re-running overwrites payloads and config, touches nothing else.
/// </summary>
public static class PluginDeployer
{
    public const string BepInExVersion = "5.4.23.2";

    /// <summary>
    /// Opens an embedded payload resource. Resources are embedded in the installer *exe*
    /// (the payload differs per installer), so probe the entry assembly first and fall
    /// back to the executing one for tests.
    /// </summary>
    public static Stream OpenPayload(string relativePath)
    {
        var name = $"PeakRelay.Installer.Payload.{relativePath.Replace('/', '.').Replace('\\', '.')}";
        var stream = Assembly.GetEntryAssembly()?.GetManifestResourceStream(name)
                     ?? Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream == null)
            throw new FileNotFoundException($"embedded payload missing: {name} (rebuild with scripts/prepare-installer-payload.sh)");
        return stream;
    }

    /// <summary>Installs BepInEx 5.4.23.2 into the game root if not already present.</summary>
    public static List<string> EnsureBepInEx(string gameDir)
    {
        var log = new List<string>();
        var core = Path.Combine(Layout.BepInExDir(gameDir), "core");
        var marker = Path.Combine(core, "BepInEx.dll");
        var gameRoot = Path.GetFullPath(gameDir);

        if (File.Exists(marker))
        {
            log.Add($"BepInEx already present: {core}");
        }
        else
        {
            var count = 0;
            using (var zip = OpenPayload(PayloadNames.BepInExZip))
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Read))
        {
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/'))
                    continue;
                var target = Path.GetFullPath(Path.Combine(gameRoot, name));
                if (!target.StartsWith(gameRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"zip entry escapes target dir: {name}");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); // ZipArchive doesn't create parents
                entry.ExtractToFile(target, overwrite: true);
                count++;
            }
            }
            if (!File.Exists(marker))
                throw new InvalidOperationException($"BepInEx extraction finished but {marker} is missing");
            log.Add($"BepInEx {BepInExVersion} extracted ({count} files)");
        }

        // Doorstop 4.5.0 proxy: BepInEx 5.4.23.2 ships 4.3.0 (2024), whose winhttp.dll
        // native-crashes the 2026-09 PEAK update during injection (before any BepInEx log).
        // ALWAYS overwrite the game's winhttp.dll last — the BepInEx zip ships its own
        // 4.3.0 winhttp.dll at the archive root and would clobber an earlier copy (caught
        // by a byte-compare against the payload during the crash fix).
        using (var loader = OpenPayload(PayloadNames.DoorstopWinhttp))
        using (var output = File.Create(Path.Combine(gameRoot, "winhttp.dll")))
            loader.CopyTo(output);
        log.Add("Doorstop 4.5.0 loader installed (winhttp.dll)");
        return log;
    }

    /// <summary>
    /// Copies embedded plugin DLLs into BepInEx/plugins/&lt;dir&gt;. Each DLL is embedded
    /// loose (payload name "plugins/&lt;set&gt;/&lt;file&gt;.dll"); a missing set or DLL
    /// throws here rather than at runtime-deep in an install.
    /// </summary>
    public static List<string> DeployPlugins(string gameDir, string payloadDir, string targetDirName)
    {
        var log = new List<string>();
        var target = Path.Combine(Layout.PluginsDir(gameDir), targetDirName);
        Directory.CreateDirectory(target);

        foreach (var dll in PayloadNames.PluginSets[payloadDir])
        {
            using var stream = OpenPayload($"{payloadDir}/{dll}");
            var file = Path.Combine(target, dll);
            using var output = File.Create(file);
            stream.CopyTo(output);
            log.Add($"deployed {targetDirName}/{dll}");
        }
        return log;
    }

    /// <summary>Deploy the dedicated-host plugin set.</summary>
    public static List<string> DeployDedicated(string gameDir) =>
        DeployPlugins(gameDir, PayloadNames.Dedicated, "PeakRelay.Dedicated");

    /// <summary>Deploy the client plugin set.</summary>
    public static List<string> DeployClient(string gameDir) =>
        DeployPlugins(gameDir, PayloadNames.Client, "PeakRelay.Client");

    /// <summary>Writes server.json next to the dedicated plugin. Returns the file path.</summary>
    public static string WriteServerConfig(string gameDir, ServerSettings settings)
    {
        var dir = Layout.DedicatedPluginDir(gameDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "server.json");

        string J(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var json = new StringBuilder()
            .AppendLine("{")
            .AppendLine($"  \"roomName\": \"{J(settings.RoomName)}\",")
            .AppendLine($"  \"displayName\": \"{J(settings.DisplayName)}\",")
            .AppendLine($"  \"mode\": \"{J(settings.Mode)}\",")
            .AppendLine($"  \"password\": \"{J(settings.Password)}\",")
            .AppendLine($"  \"maxPlayers\": {settings.MaxPlayers},")
            .AppendLine("  \"visible\": true,")
            .AppendLine("  \"open\": true,")
            .AppendLine($"  \"relayHost\": \"{J(settings.RelayHost)}\",")
            .AppendLine($"  \"relayPort\": {settings.RelayPort},")
            .AppendLine($"  \"autoHost\": {(settings.AutoHost ? "true" : "false")},")
            .AppendLine("  \"useVanillaName\": false,")
            .AppendLine("  \"logDatagrams\": false,")
            .AppendLine("  \"noSteam\": null,")
            .AppendLine($"  \"hostName\": \"{J(settings.HostName)}\"")
            .AppendLine("}")
            .ToString();
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Writes the BepInEx config for the client plugin. Returns the file path.</summary>
    public static string WriteClientConfig(string gameDir, ClientSettings settings)
    {
        Directory.CreateDirectory(Layout.ConfigDir(gameDir));
        var path = Path.Combine(Layout.ConfigDir(gameDir), "com.peakrelay.client.cfg");
        File.WriteAllText(path, new StringBuilder()
            .AppendLine("## Settings generated by the PeakRelay installer")
            .AppendLine()
            .AppendLine("[Relay]")
            .AppendLine()
            .AppendLine($"## Route PUN through the PeakRelay relay (TCP envelopes). false = vanilla Photon Cloud UDP.")
            .AppendLine($"# Acceptable values: True, False")
            .AppendLine("Enabled = True")
            .AppendLine()
            .AppendLine($"## Relay server address.")
            .AppendLine($"Host = {settings.RelayHost}")
            .AppendLine()
            .AppendLine($"## Relay server TCP port (game protocol).")
            .AppendLine($"Port = {settings.RelayPort}")
            .AppendLine()
            .AppendLine($"## Relay HTTP directory port (server browser).")
            .AppendLine($"DirectoryPort = {settings.DirectoryPort}")
            .AppendLine()
            .ToString());
        return path;
    }
}

/// <summary>server.json values the server installer writes.</summary>
public sealed record ServerSettings(
    string RoomName,
    string DisplayName,
    string Mode,
    string Password,
    int MaxPlayers,
    string RelayHost,
    int RelayPort,
    bool AutoHost,
    string HostName);

/// <summary>Client config values the client installer writes.</summary>
public sealed record ClientSettings(string RelayHost, int RelayPort, int DirectoryPort = 5056);
