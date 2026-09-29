using System.IO;
using System.IO.Compression;
using System.Text;

namespace PeakRelay.Installer.Core;

/// <summary>
/// Copies the embedded published relay (PeakRelay.Server, framework-dependent) into a
/// user-chosen folder and drops a start script + README next to it.
/// </summary>
public static class RelayStager
{
    public static string Stage(string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var count = 0;
        using (var stream = PluginDeployer.OpenPayload(PayloadNames.RelayDir))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            // relay payload embedded as relay.zip (one resource, many files)
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;
                entry.ExtractToFile(Path.Combine(targetDir, entry.Name), overwrite: true);
                count++;
            }
        }

        File.WriteAllText(Path.Combine(targetDir, "start-relay.cmd"),
            "@echo off\r\n" +
            "rem PeakRelay relay server: TCP 5055 (game traffic), HTTP 5056 (server browser feed)\r\n" +
            "rem Requires the .NET 8 Runtime (https://dotnet.microsoft.com/download/dotnet/8.0)\r\n" +
            "cd /d \"%~dp0\"\r\n" +
            "dotnet PeakRelay.Server.dll 5055 5056\r\n");

        File.WriteAllText(Path.Combine(targetDir, "README.txt"), new StringBuilder()
            .AppendLine("PeakRelay relay server")
            .AppendLine("======================")
            .AppendLine()
            .AppendLine("start-relay.cmd   starts the relay (TCP 5055 + HTTP 5056)")
            .AppendLine()
            .AppendLine("Requires the .NET 8 Runtime on this machine.")
            .AppendLine("Browse http://localhost:5056/ for the live room list once a dedicated")
            .AppendLine("host has connected. Clients need the PeakRelay client plugin pointed at")
            .AppendLine("this machine's relay host/port (default 5055).")
            .ToString());

        return $"{count} relay files + start-relay.cmd + README.txt -> {targetDir}";
    }
}

/// <summary>Post-install verification shared by both installers.</summary>
public static class Verifier
{
    public sealed record Check(string Name, bool Ok, string Detail);

    public static List<Check> VerifyServerInstall(string gameDir, ServerSettings settings)
    {
        var checks = new List<Check>
        {
            new("game dir contains PEAK.exe", GameLocator.LooksLikeGameRoot(gameDir), gameDir),
            new("BepInEx core present",
                File.Exists(Path.Combine(Layout.BepInExDir(gameDir), "core", "BepInEx.dll")),
                Layout.BepInExDir(gameDir)),
        };
        foreach (var file in new[] { "PeakRelay.Dedicated.dll", "PeakRelay.Protocol.dll" })
        {
            var path = Path.Combine(Layout.DedicatedPluginDir(gameDir), file);
            checks.Add(new($"dedicated plugin: {file}", File.Exists(path), path));
        }
        var cfg = Path.Combine(Layout.DedicatedPluginDir(gameDir), "server.json");
        checks.Add(new("server.json written", File.Exists(cfg), cfg));
        checks.Add(new("room name is 6 chars (join-code format)",
            settings.RoomName.Length == 6, settings.RoomName));
        return checks;
    }

    public static List<Check> VerifyClientInstall(string gameDir, ClientSettings settings)
    {
        var checks = new List<Check>
        {
            new("game dir contains PEAK.exe", GameLocator.LooksLikeGameRoot(gameDir), gameDir),
            new("BepInEx core present",
                File.Exists(Path.Combine(Layout.BepInExDir(gameDir), "core", "BepInEx.dll")),
                Layout.BepInExDir(gameDir)),
        };
        foreach (var file in new[] { "PeakRelay.Client.dll", "PeakRelay.Protocol.dll" })
        {
            var path = Path.Combine(Layout.ClientPluginDir(gameDir), file);
            checks.Add(new($"client plugin: {file}", File.Exists(path), path));
        }
        var cfg = Path.Combine(Layout.ConfigDir(gameDir), "com.peakrelay.client.cfg");
        checks.Add(new("client config written", File.Exists(cfg), cfg));
        if (Directory.Exists(Layout.DedicatedPluginDir(gameDir)))
            checks.Add(new("dedicated plugin also installed in this game dir (host+client on one box — ok, but the game will auto-host when launched headless)",
                true, Layout.DedicatedPluginDir(gameDir)));
        return checks;
    }
}
