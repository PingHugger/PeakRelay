using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PeakRelay.Installer.Core;

namespace PeakRelay.Launcher.Core;

/// <summary>
/// The dedicated server runs from its OWN copy of the game (default
/// %LOCALAPPDATA%\PeakRelay\server), so playing and hosting never interfere:
///
/// - the copy's exe is renamed PEAK.exe -> PeakServer.exe (and PEAK_Data ->
///   PeakServer_Data, which Unity requires to match the exe name) so both processes are
///   distinguishable in Task Manager and can run side by side;
/// - the copy carries ONLY the dedicated plugin (no client plugin, no client config);
/// - the main install stays player-only: ModApply removes dedicated files from it.
///
/// Sync = robocopy /MIR from the game install (BepInEx excluded — always deployed fresh
/// from the embedded payload), then rename, then deploy dedicated + server.json, then
/// stamp the source exe (size + mtime) so the doctor can detect game-update drift.
/// </summary>
public static class ServerCopy
{
    public const string ServerExeName = "PeakServer.exe";
    public const string ServerDataDirName = "PeakServer_Data";

    /// <summary>Test seams: the payload-backed deploy steps are replaced in unit tests.</summary>
    internal static Func<string, List<string>>? EnsureBepInExOverride;
    internal static Func<string, List<string>>? DeployDedicatedOverride;

    public static string DefaultServerDir => Path.Combine(LauncherPaths.Root, "server");

    private sealed record Stamp(string SourceExe, long Size, long MTimeUtcTicks);

    /// <summary>
    /// Creates or refreshes the server copy. Safe to re-run any time (mirror semantics);
    /// the game must not be running and the server must be stopped first.
    /// Returns progress lines.
    /// </summary>
    public static Task<List<string>> SyncAsync(string gameDir, string serverDir)
    {
        return Task.Run(() => Sync(gameDir, serverDir));
    }

    public static List<string> Sync(string gameDir, string serverDir)
    {
        var log = new List<string>();
        if (!GameLocator.LooksLikeGameRoot(gameDir))
            throw new InvalidOperationException($"'{gameDir}' is not a PEAK install (no PEAK.exe)");
        if (Directory.Exists(serverDir) && File.Exists(Path.Combine(serverDir, ServerExeName))
            && Process.GetProcessesByName("PeakServer").Length > 0)
            throw new InvalidOperationException("stop the dedicated server before syncing its copy");

        log.Add($"mirroring {gameDir} -> {serverDir} (robocopy /MIR, BepInEx excluded)…");
        Directory.CreateDirectory(serverDir);
        var rc = RunRobocopy(gameDir, serverDir);
        log.Add($"robocopy done (exit {rc}, <8 = ok)");

        // Unity resolves its data folder from the exe name: PEAK_Data must follow the exe.
        RenameOrReplace(Path.Combine(serverDir, "PEAK.exe"), Path.Combine(serverDir, ServerExeName), log);
        RenameOrReplaceDir(Path.Combine(serverDir, "PEAK_Data"), Path.Combine(serverDir, ServerDataDirName), log);

        // Fresh mod layer from the embedded payload — never inherited from the game install.
        var ensure = EnsureBepInExOverride ?? PluginDeployer.EnsureBepInEx;
        foreach (var line in ensure(serverDir))
            log.Add(line);
        var deploy = DeployDedicatedOverride ?? PluginDeployer.DeployDedicated;
        foreach (var line in deploy(serverDir))
            log.Add(line);

        // Server side config (same defaults as the installers' CLI).
        var settings = new ServerSettings(
            RoomName: "DBPEAK",
            DisplayName: "PeakRelay Dedicated",
            Mode: "standard",
            Password: "",
            MaxPlayers: 20,
            RelayHost: "127.0.0.1",
            RelayPort: 5055,
            AutoHost: true,
            HostName: "DedicatedHost");
        log.Add($"server.json written: {PluginDeployer.WriteServerConfig(serverDir, settings)}");

        // The copy must never carry the client plugin or client config.
        var clientDir = Layout.ClientPluginDir(serverDir);
        if (Directory.Exists(clientDir))
        {
            Directory.Delete(clientDir, recursive: true);
            log.Add("removed client plugin from the server copy (server carries dedicated only)");
        }

        WriteStamp(gameDir, serverDir);
        log.Add($"server copy ready: {Path.Combine(serverDir, ServerExeName)}");
        return log;
    }

    private static int RunRobocopy(string source, string dest)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = $"\"{Path.GetFullPath(source)}\" \"{Path.GetFullPath(dest)}\" " +
                        "/MIR /R:2 /W:2 /NFL /NDL /NJH /NP /XD BepInEx",
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        // robocopy: 0-7 = success variants (1 = files copied), >= 8 = real failure.
        return proc.ExitCode;
    }

    private static void RenameOrReplace(string sourceFile, string targetFile, List<string> log)
    {
        if (File.Exists(targetFile))
            File.Delete(targetFile);
        File.Move(sourceFile, targetFile);
        log.Add($"renamed {Path.GetFileName(sourceFile)} -> {Path.GetFileName(targetFile)}");
    }

    private static void RenameOrReplaceDir(string sourceDir, string targetDir, List<string> log)
    {
        if (Directory.Exists(targetDir))
            Directory.Delete(targetDir, recursive: true);
        Directory.Move(sourceDir, targetDir);
        log.Add($"renamed {Path.GetFileName(sourceDir)} -> {Path.GetFileName(targetDir)}");
    }

    private static void WriteStamp(string gameDir, string serverDir)
    {
        var exe = Path.Combine(gameDir, "PEAK.exe");
        var info = new FileInfo(exe);
        var stamp = new Stamp(exe, info.Length, info.LastWriteTimeUtc.Ticks);
        File.WriteAllText(Path.Combine(serverDir, "peakrelay-server.json"),
            JsonSerializer.Serialize(stamp, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>False when the game updated since the last sync (copy needs a refresh).</summary>
    public static bool StampIsCurrent(string gameDir, string serverDir)
    {
        try
        {
            var stampPath = Path.Combine(serverDir, "peakrelay-server.json");
            if (!File.Exists(stampPath))
                return false;
            var stamp = JsonSerializer.Deserialize<Stamp>(File.ReadAllText(stampPath));
            var info = new FileInfo(Path.Combine(gameDir, "PEAK.exe"));
            return stamp != null
                   && stamp.Size == info.Length
                   && stamp.MTimeUtcTicks == info.LastWriteTimeUtc.Ticks;
        }
        catch (IOException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>True when the copy looks like a synced server dir (has the renamed exe).</summary>
    public static bool LooksLikeServerCopy(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, ServerExeName));

    /// <summary>True while a PeakServer.exe process is alive.</summary>
    public static bool IsRunning() => Process.GetProcessesByName("PeakServer").Length > 0;

    /// <summary>Starts the dedicated server headless from the copy. Returns the process.</summary>
    public static Process Start(string serverDir)
    {
        if (!LooksLikeServerCopy(serverDir))
            throw new InvalidOperationException(
                $"'{serverDir}' is not a PeakRelay server copy (no {ServerExeName}) — run a server sync first");
        var exe = Path.Combine(Path.GetFullPath(serverDir), ServerExeName);
        Directory.CreateDirectory(LauncherPaths.LogDir);
        return Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetFullPath(serverDir),
            UseShellExecute = false,
            Arguments = $"-batchmode -nographics -logFile \"{LauncherPaths.LogFile("peakserver.log")}\"",
        }) ?? throw new InvalidOperationException($"could not start {exe}");
    }

    /// <summary>Stops every PeakServer.exe process (headless: no window to close).</summary>
    public static void Stop()
    {
        foreach (var proc in Process.GetProcessesByName("PeakServer"))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(10_000);
            }
            catch (SystemException)
            {
                // already gone
            }
        }
    }
}
