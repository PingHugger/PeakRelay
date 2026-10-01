using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using PeakRelay.Launcher.Core;
using Xunit;

namespace PeakRelay.Launcher.Tests;

/// <summary>
/// RelayHost.EnsureRelayFiles must be genuinely idempotent and must tolerate a RUNNING
/// standalone relay staged into the same tree (the installer can target
/// %LOCALAPPDATA%\PeakRelay\relay — the launcher's own RelayDir). That collision used to
/// surface as "The process cannot access the file ... because it is being used by another
/// process" on every Host click: the marker check looked one level above where the
/// folder-rooted relay.zip actually landed, so extraction re-ran with overwrite on every
/// start and hit the standalone relay's locked DLLs.
/// </summary>
[Collection("LauncherFs")]
public sealed class RelayHostTests : IDisposable
{
    private static readonly string RepoRoot = FindRepoRoot();
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

    /// <summary>CI checkouts may have no staged payload; the tests self-skip then.</summary>
    private static bool PayloadStaged() =>
        File.Exists(Path.Combine(RepoRoot, "Installer.Core", "Payload", "payload", "relay.zip"));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PeakRelay.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    [Fact]
    public void EnsureRelayFiles_extracts_flat_and_is_idempotent()
    {
        if (!PayloadStaged())
        {
            Console.WriteLine("[skip] installer payload not staged — run scripts/prepare-installer-payload.sh");
            return;
        }

        RelayHost.EnsureRelayFiles();

        var dll = Path.Combine(LauncherPaths.RelayDir, "PeakRelay.Server.dll");
        Assert.True(File.Exists(dll), "relay payload should extract flat into RelayDir");
        Assert.False(File.Exists(Path.Combine(LauncherPaths.RelayDir, "relay", "PeakRelay.Server.dll")),
            "the historical relay/ subfolder must not be recreated");

        // Second call must not rewrite anything (mtime unchanged) — the old bug re-extracted
        // with overwrite on every Host click.
        var before = File.GetLastWriteTimeUtc(dll);
        Thread.Sleep(50);
        RelayHost.EnsureRelayFiles();
        Assert.Equal(before, File.GetLastWriteTimeUtc(dll));
    }

    [Fact]
    public void EnsureRelayFiles_migrates_the_historical_folder_rooted_layout()
    {
        if (!PayloadStaged())
        {
            Console.WriteLine("[skip] installer payload not staged — run scripts/prepare-installer-payload.sh");
            return;
        }

        // Simulate what older Compress-Archive runs shipped: files one level down in a
        // relay/ subfolder, marker older than the embedded payload so extraction is due.
        var nestedDir = Path.Combine(LauncherPaths.RelayDir, "relay");
        Directory.CreateDirectory(nestedDir);
        var nestedMarker = Path.Combine(nestedDir, "PeakRelay.Server.dll");
        File.WriteAllText(nestedMarker, "old");
        File.SetLastWriteTimeUtc(nestedMarker, DateTime.UtcNow.AddDays(-30));

        RelayHost.EnsureRelayFiles();

        Assert.True(File.Exists(Path.Combine(LauncherPaths.RelayDir, "PeakRelay.Server.dll")),
            "nested entries must extract into RelayDir itself");
        Assert.True(File.Exists(Path.Combine(LauncherPaths.RelayDir, "PeakRelay.Protocol.dll")));
    }

    [Fact]
    public void EnsureRelayFiles_tolerates_a_running_standalone_relay_holding_files()
    {
        if (!PayloadStaged())
        {
            Console.WriteLine("[skip] installer payload not staged — run scripts/prepare-installer-payload.sh");
            return;
        }

        RelayHost.EnsureRelayFiles();

        // Simulate the standalone relay: DLLs opened with no sharing (what a loaded
        // assembly looks like to other processes).
        using var lockedDll = File.Open(Path.Combine(LauncherPaths.RelayDir, "PeakRelay.Protocol.dll"),
            FileMode.Open, FileAccess.Read, FileShare.None);
        using var lockedExe = File.Open(Path.Combine(LauncherPaths.RelayDir, "PeakRelay.Server.exe"),
            FileMode.Open, FileAccess.Read, FileShare.None);

        // Force the refresh path (marker older than the embedded payload) and let it run
        // against the locks: must keep the existing files instead of throwing.
        File.SetLastWriteTimeUtc(Path.Combine(LauncherPaths.RelayDir, "PeakRelay.Server.dll"),
            DateTime.UtcNow.AddDays(-30));

        RelayHost.EnsureRelayFiles();

        Assert.True(File.Exists(Path.Combine(LauncherPaths.RelayDir, "PeakRelay.Protocol.dll")));
        Assert.True(File.Exists(Path.Combine(LauncherPaths.RelayDir, "PeakRelay.Server.exe")));
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
    public void Start_reports_ports_before_touching_any_files()
    {
        using var squatter = BindRandomPort(out var port);

        using var host = new RelayHost();
        Assert.Throws<InvalidOperationException>(() => host.Start(port, port + 1));

        Assert.False(File.Exists(Path.Combine(LauncherPaths.RelayDir, "PeakRelay.Server.dll")),
            "a blocked start must not extract relay files");
    }

    [Fact]
    public void Start_succeeds_on_free_ports()
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
