using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
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
        if (!NeedsExtraction())
            return LauncherPaths.RelayDir;

        using var zip = PluginDeployer.OpenPayload(PayloadNames.RelayZip);
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            // Historical zips (PowerShell Compress-Archive) carry a "relay/" folder root and
            // backslash separators; unzip both into RelayDir itself.
            var relative = entry.FullName.Replace('\\', '/');
            if (relative.StartsWith("relay/", StringComparison.OrdinalIgnoreCase))
                relative = relative["relay/".Length..];
            if (relative.Length == 0)
                continue;
            var target = Path.GetFullPath(Path.Combine(LauncherPaths.RelayDir, relative));
            if (!target.StartsWith(Path.GetFullPath(LauncherPaths.RelayDir) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"relay zip entry escapes target dir: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // A standalone relay staged here (installer) may be RUNNING with its DLLs locked —
            // fail soft: keep the existing file. The launcher hosts RelayServer in-process and
            // never loads these files, so only version staleness is at risk, not a crash.
            try
            {
                entry.ExtractToFile(target, overwrite: true);
            }
            catch (IOException)
            {
                if (!File.Exists(target))
                    throw; // nothing we can keep — surface the real problem
            }
            catch (UnauthorizedAccessException) when (File.Exists(target))
            {
                // locked or read-only: keep what is there
            }
        }
        return LauncherPaths.RelayDir;
    }

    /// <summary>True when the relay payload should be (re)extracted: marker DLL missing
    /// anywhere in RelayDir (flat or in the historical relay/ subfolder) or older than the
    /// embedded copy. Only files in RelayDir itself are refreshed — a running standalone
    /// relay keeps its locked files untouched.</summary>
    internal static bool NeedsExtraction()
    {
        var flatMarker = Path.Combine(LauncherPaths.RelayDir, "PeakRelay.Server.dll");
        var nestedMarker = Path.Combine(LauncherPaths.RelayDir, "relay", "PeakRelay.Server.dll");
        var marker = File.Exists(flatMarker) ? flatMarker
            : File.Exists(nestedMarker) ? nestedMarker
            : null;
        if (marker is null)
            return true;

        try
        {
            using var zip = PluginDeployer.OpenPayload(PayloadNames.RelayZip);
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
            ZipArchiveEntry? embedded = null;
            foreach (var entry in archive.Entries)
            {
                var normalized = entry.FullName.Replace('\\', '/');
                if (normalized.StartsWith("relay/", StringComparison.OrdinalIgnoreCase))
                    normalized = normalized["relay/".Length..];
                if (normalized.Equals("PeakRelay.Server.dll", StringComparison.OrdinalIgnoreCase))
                {
                    embedded = entry;
                    break;
                }
            }
            if (embedded is null)
                return true; // unknown layout — re-extract and let the entry loop sort it out
            return File.GetLastWriteTimeUtc(marker) < embedded.LastWriteTime.UtcDateTime;
        }
        catch (FileNotFoundException)
        {
            // payload unavailable but usable files exist — the launcher hosts RelayServer
            // in-process and never loads the extracted files, so keep what is there.
            return false;
        }
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
            EnsurePortsFree(listenPort, httpPort);
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

    /// <summary>Throws a clear message when another relay (e.g. a standalone one started via
    /// start-relay.cmd) already holds the ports, instead of letting SocketException surface.</summary>
    private static void EnsurePortsFree(int listenPort, int httpPort)
    {
        foreach (var port in new[] { listenPort, httpPort })
            if (PortTaken(port))
                throw new InvalidOperationException(
                    $"port {port} is already in use — a relay (standalone or another launcher) is likely " +
                    $"already running; stop it first or pick different ports.");
    }

    private static bool PortTaken(int port)
    {
        try
        {
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                listener.Bind(new IPEndPoint(IPAddress.Any, port));
                return false;
            }
            finally
            {
                listener.Close();
            }
        }
        catch (SocketException)
        {
            return true;
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
