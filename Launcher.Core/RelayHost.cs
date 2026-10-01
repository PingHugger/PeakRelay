using System;
using System.Net;
using System.Net.Sockets;
using PeakRelay.Server;

namespace PeakRelay.Launcher.Core;

/// <summary>State of the launcher's own relay instance.</summary>
public enum RelayHostState { Idle, Starting, Running, Failed }

/// <summary>
/// Hosts the relay in the launcher process: runs <see cref="RelayServer"/> directly (no child
/// process, ports released on Stop). This is what "host for friends" means: click, and the
/// room directory on the HTTP port answers.
///
/// The relay runs from the launcher's own binaries — no files are extracted to disk. The old
/// EnsureRelayFiles() extraction existed only for a copy nobody loaded and caused the v0.7.12
/// "file in use" lock bug when a standalone relay was staged into the same folder; use
/// `PeakRelayLauncher stage-relay` for an explicit standalone copy instead.
/// </summary>
public sealed class RelayHost : IDisposable
{
    private RelayServer? _server;

    public RelayHostState State { get; private set; } = RelayHostState.Idle;
    public int ListenPort { get; private set; }
    public int HttpPort { get; private set; }
    public string? Error { get; private set; }

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
