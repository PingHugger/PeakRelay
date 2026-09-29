using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using PeakRelay.Installer.Core;
using PeakRelay.Server;

namespace PeakRelay.Launcher.Core;

/// <summary>State of the launcher's own relay instance.</summary>
public enum RelayHostState { Idle, Starting, Running, Failed }

/// <summary>
/// Hosts the relay in the launcher process: extracts the embedded relay publish into
/// %LOCALAPPDATA%\PeakRelay\relay once, then runs <see cref="RelayServer"/> directly
/// (no child process, ports released on Stop). This is what "host for friends" means:
/// click, and the room directory on the HTTP port answers.
/// </summary>
public sealed class RelayHost : IDisposable
{
    private RelayServer? _server;

    public RelayHostState State { get; private set; } = RelayHostState.Idle;
    public int ListenPort { get; private set; }
    public int HttpPort { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Extracts the embedded relay into LauncherPaths.RelayDir (idempotent).</summary>
    public static string EnsureRelayFiles()
    {
        Directory.CreateDirectory(LauncherPaths.RelayDir);
        var dll = Path.Combine(LauncherPaths.RelayDir, "PeakRelay.Server.dll");
        if (!File.Exists(dll))
        {
            using var zip = PluginDeployer.OpenPayload(PayloadNames.RelayZip);
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;
                var target = Path.GetFullPath(Path.Combine(LauncherPaths.RelayDir, entry.FullName));
                if (!target.StartsWith(Path.GetFullPath(LauncherPaths.RelayDir) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"relay zip entry escapes target dir: {entry.FullName}");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }
        return LauncherPaths.RelayDir;
    }

    /// <summary>Starts the relay on the given ports. Throws when they are taken.</summary>
    public void Start(int listenPort = 5055, int httpPort = 5056)
    {
        if (State == RelayHostState.Running || State == RelayHostState.Starting)
            throw new InvalidOperationException("relay already running");
        Error = null;
        State = RelayHostState.Starting;
        try
        {
            EnsureRelayFiles();
            _server = new RelayServer(listenPort, httpPort);
            ListenPort = listenPort;
            HttpPort = httpPort;
            State = RelayHostState.Running;
        }
        catch (Exception ex)
        {
            _server?.Dispose();
            _server = null;
            Error = ex.Message;
            State = RelayHostState.Failed;
            throw;
        }
    }

    /// <summary>Stops the relay and releases both ports.</summary>
    public void Stop()
    {
        _server?.Dispose();
        _server = null;
        State = RelayHostState.Idle;
    }

    public void Dispose() => Stop();
}
