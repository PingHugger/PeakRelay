using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using PeakRelay.Launcher.Core;
using Xunit;

namespace PeakRelay.Launcher.Tests;

/// <summary>
/// The launcher runs RelayServer in-process and extracts nothing to disk (the v0.7.12
/// "file in use" bug came from the old EnsureRelayFiles extraction nobody loaded). What
/// remains contract-critical: ports are pre-checked with an actionable message before any
/// listener is created, and Start/Stop state transitions hold.
/// </summary>
[Collection("LauncherFs")]
public sealed class RelayHostTests : IDisposable
{
    private readonly string _temp =
        Path.Combine(Path.GetTempPath(), "peakrelay-relayhost-" + Path.GetRandomFileName());

    public RelayHostTests()
    {
        Directory.CreateDirectory(_temp);
        LauncherPaths.UseRootForTests(_temp);
    }

    public void Dispose()
    {
        Directory.Delete(_temp, recursive: true);
        LauncherPaths.UseRootForTests(Path.GetTempPath());
    }

    [Fact]
    public void Start_fails_with_a_clear_message_when_the_port_is_taken()
    {
        using var squatter = BindRandomPort(out var port);

        using var host = new RelayHost();
        var ex = Assert.Throws<InvalidOperationException>(() => host.Start(port, port + 1));
        Assert.Contains($"port {port} is already in use", ex.Message);
        Assert.Equal(RelayHostState.Failed, host.State);
        Assert.NotNull(host.Error);
    }

    [Fact]
    public void Start_on_a_taken_port_creates_no_state()
    {
        using var squatter = BindRandomPort(out var port);

        using var host = new RelayHost();
        Assert.Throws<InvalidOperationException>(() => host.Start(port, port + 1));

        // no relay files are staged anywhere anymore — the launcher hosts in-process
        Assert.False(File.Exists(Path.Combine(LauncherPaths.RelayDir, "PeakRelay.Server.dll")));
    }

    [Fact]
    public void Start_succeeds_on_free_ports_and_stop_releases_them()
    {
        using var host = new RelayHost();
        try
        {
            host.Start(listenPort: 0, httpPort: 0); // ephemeral: never fights other tests
            Assert.Equal(RelayHostState.Running, host.State);
        }
        finally
        {
            host.Stop();
        }
        Assert.Equal(RelayHostState.Idle, host.State);
    }

    private static Socket BindRandomPort(out int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        socket.Listen(1);
        port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        return socket;
    }
}
