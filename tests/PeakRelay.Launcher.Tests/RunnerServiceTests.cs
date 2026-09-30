using System;
using System.IO;
using PeakRelay.Launcher.Core;
using Xunit;

namespace PeakRelay.Launcher.Tests;

/// <summary>
/// The runner-service manager: naming scheme, runner detection, and the guards that keep
/// elevated operations away from non-runner directories. Live service manipulation is NOT
/// tested here — it needs admin rights and a real GitHub registration.
/// </summary>
public sealed class RunnerServiceTests : IDisposable
{
    private readonly string _temp =
        Path.Combine(Path.GetTempPath(), "peakrelay-runnerservice-" + Path.GetRandomFileName());

    public RunnerServiceTests() => Directory.CreateDirectory(_temp);

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void Service_name_follows_the_official_scheme()
    {
        var dir = Path.Combine(Path.GetTempPath(), "actions-runner");
        Assert.Equal("actions.runner.PeakRelay.PingHugger.actions-runner", RunnerService.ServiceNameFor(dir));
        Assert.Equal("GitHub Actions Runner (PeakRelay.PingHugger.actions-runner)", RunnerService.DisplayNameFor(dir));
    }

    [Fact]
    public void LooksLikeRunner_requires_settings_and_service_host()
    {
        Assert.False(RunnerService.LooksLikeRunner(_temp));
        Assert.False(RunnerService.LooksLikeRunner(null));
        Assert.False(RunnerService.LooksLikeRunner(""));

        File.WriteAllText(Path.Combine(_temp, ".runner"), "{}");
        Directory.CreateDirectory(Path.Combine(_temp, "bin"));
        Assert.False(RunnerService.LooksLikeRunner(_temp)); // settings but no service host

        File.WriteAllText(Path.Combine(_temp, "bin", "RunnerService.exe"), "host");
        Assert.True(RunnerService.LooksLikeRunner(_temp));
    }

    [Fact]
    public void Inspect_reports_a_non_runner_as_absent()
    {
        var install = RunnerService.Inspect(_temp);
        Assert.False(install.ServiceExists);
        Assert.False(install.ServiceRunning);
        Assert.Equal(RunnerService.ServiceNameFor(_temp), install.ServiceName);
    }

    [Fact]
    public void Windows_supported_only_on_windows()
    {
        Assert.Equal(OperatingSystem.IsWindows(), RunnerService.IsWindowsSupported());
    }

    [Fact]
    public async Task Install_rejects_a_directory_that_is_not_a_configured_runner()
    {
        var result = await System.Threading.Tasks.Task.Run(() => RunnerService.Install(_temp));
        Assert.False(result.Ok);
        Assert.Contains("not a configured", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Uninstall_without_a_service_is_a_polite_no_op()
    {
        // A dir that LOOKS like a runner but has no service installed: removing is a no-op
        // that reports success — and never elevates (no UAC prompt from a unit test).
        Directory.CreateDirectory(Path.Combine(_temp, "bin"));
        File.WriteAllText(Path.Combine(_temp, ".runner"), "{}");
        File.WriteAllText(Path.Combine(_temp, "bin", "RunnerService.exe"), "host");

        var result = await System.Threading.Tasks.Task.Run(() => RunnerService.Uninstall(_temp));
        Assert.True(result.Ok);
        Assert.Contains("nothing to remove", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
