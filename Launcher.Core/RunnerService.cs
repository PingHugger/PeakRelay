using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PeakRelay.Launcher.Core;

/// <summary>Everything <see cref="RunnerService"/> needs to know about one runner install.</summary>
public sealed record RunnerInstall(string Directory, string ServiceName, string DisplayName, bool ServiceExists, bool ServiceRunning);

/// <summary>Result of a service install/remove attempt: success flag plus an operator-readable message.</summary>
public sealed record RunnerServiceResult(bool Ok, string Message);

/// <summary>
/// Manages the Windows service wrapper around a self-hosted GitHub Actions runner —
/// Windows only. A runner that is merely "running in a terminal" dies with that terminal
/// (or the next reboot), which silently strands tag-triggered release builds in the
/// `queued` state. The service wrapper (bin/RunnerService.exe) survives both.
///
/// How it works, mirroring the official `config.cmd run --start` path but deterministic:
/// the runner is CONFIGURED as before (config.cmd --url --token …), and the service entry
/// points at bin/RunnerService.exe — the runner's own service host, which spawns
/// Runner.Listener run. Auto start + restart-on-failure recovery are set explicitly.
///
/// All admin-requiring operations shell out to sc.exe/RunnerService.exe via
/// <see cref="RunElevated"/>; detection and naming are pure and unit-tested.
/// </summary>
public static class RunnerService
{
    /// <summary>Official service naming scheme: actions.runner.&lt;org&gt;.&lt;repo&gt;.&lt;runnerName&gt;.</summary>
    public static string ServiceNameFor(string runnerDirectory) =>
        "actions.runner.PeakRelay.PingHugger." + Path.GetFileName(Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(string.IsNullOrWhiteSpace(runnerDirectory) ? "." : runnerDirectory)));

    public static string DisplayNameFor(string runnerDirectory) =>
        "GitHub Actions Runner (PeakRelay.PingHugger." +
        Path.GetFileName(Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(string.IsNullOrWhiteSpace(runnerDirectory) ? "." : runnerDirectory))) + ")";

    /// <summary>True when dir looks like a configured runner (settings file present).</summary>
    public static bool LooksLikeRunner(string? dir) =>
        !string.IsNullOrWhiteSpace(dir)
        && File.Exists(Path.Combine(dir, ".runner"))
        && File.Exists(Path.Combine(dir, "bin", "RunnerService.exe"));

    /// <summary>Non-Windows or exotic platforms: the whole feature is off.</summary>
    public static bool IsWindowsSupported() => OperatingSystem.IsWindows();

    /// <summary>Sniffs the state of one runner directory (pure I/O, no admin needed).</summary>
    public static RunnerInstall Inspect(string runnerDirectory)
    {
        var name = ServiceNameFor(runnerDirectory);
        return new RunnerInstall(
            runnerDirectory,
            name,
            DisplayNameFor(runnerDirectory),
            ServiceExists: QueryServiceState(name) != null,
            ServiceRunning: QueryServiceState(name) == "Running");
    }

    /// <summary>
    /// Installs and starts the auto-start service for a configured runner. Elevated work
    /// happens in a PowerShell helper launched with UseShellExecute + "runas" (the shell
    /// shows the UAC prompt); non-elevated from a service/CI context, Windows rejects the
    /// elevation and the user sees the reason instead of a half-installed service.
    /// </summary>
    public static RunnerServiceResult Install(string runnerDirectory)
    {
        if (!IsWindowsSupported())
            return new RunnerServiceResult(false, "runner services are a Windows-only feature");
        if (!LooksLikeRunner(runnerDirectory))
            return new RunnerServiceResult(false,
                $"'{runnerDirectory}' is not a configured GitHub Actions runner — run config.cmd first");
        if (QueryServiceState(ServiceNameFor(runnerDirectory)) == "Running")
            return new RunnerServiceResult(true, "nothing to do — the runner service is already installed and running");

        // A live interactive runner would fight the service for jobs; stop it first.
        foreach (var name in new[] { "Runner.Listener", "Runner.Worker" })
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                    proc.Kill(entireProcessTree: true);
            }
            catch (SystemException)
            {
                // nothing running / already gone
            }
        }

        var script = PowerShellScript(runnerDirectory);
        var scriptPath = Path.Combine(Path.GetTempPath(), "peakrelay-runner-service.ps1");
        File.WriteAllText(scriptPath, script);
        return RunElevated(scriptPath, "install the GitHub Actions runner service");
    }

    /// <summary>Stops, removes and unregisters the service (runner registration stays intact).</summary>
    public static RunnerServiceResult Uninstall(string runnerDirectory)
    {
        if (!IsWindowsSupported())
            return new RunnerServiceResult(false, "runner services are a Windows-only feature");
        var name = ServiceNameFor(runnerDirectory);
        if (QueryServiceState(name) == null)
            return new RunnerServiceResult(true, "nothing to remove — no runner service is installed");
        var script = $$"""
            $ErrorActionPreference = 'Continue'
            sc.exe stop '{{name}}' | Out-Null
            Start-Sleep -Seconds 2
            sc.exe delete '{{name}}'
            Start-Sleep -Seconds 1
            if (Get-Service -Name '{{name}}' -ErrorAction SilentlyContinue) { exit 1 }
            exit 0
            """;
        var scriptPath = Path.Combine(Path.GetTempPath(), "peakrelay-runner-service-remove.ps1");
        File.WriteAllText(scriptPath, script);
        return RunElevated(scriptPath, "remove the GitHub Actions runner service");
    }

    /// <summary>sc.exe query → service state string, or null when the service does not exist.</summary>
    internal static string? QueryServiceState(string serviceName)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"query \"{serviceName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            if (proc == null)
                return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10_000);
            if (proc.ExitCode != 0) // 1060 = not installed
                return null;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("STATE", StringComparison.OrdinalIgnoreCase))
                    return trimmed.Split(':') is { Length: > 1 } parts ? parts[1].Trim() : null;
            }
            return null;
        }
        catch (SystemException)
        {
            return null;
        }
    }

    /// <summary>The elevated helper: idempotent install + start with explicit error reporting.</summary>
    private static string PowerShellScript(string runnerDirectory)
    {
        var dir = runnerDirectory.Replace("'", "''");
        return $$"""
            $ErrorActionPreference = 'Continue'
            Set-Location '{{dir}}'
            $bin = Join-Path (Get-Location) 'bin\RunnerService.exe'
            $name = 'actions.runner.PeakRelay.PingHugger.' + (Split-Path -Leaf (Get-Location))

            $existing = Get-CimInstance Win32_Service | Where-Object { $_.PathName -like '*actions-runner*' }
            if ($existing) {
                sc.exe stop $existing.Name | Out-Null
                sc.exe delete $existing.Name | Out-Null
                Start-Sleep -Seconds 2
            }

            sc.exe create $name binPath= "`"$bin`"" start= auto DisplayName= "GitHub Actions Runner (PeakRelay.PingHugger)"
            if ($LASTEXITCODE -ne 0) { Write-Output "sc create failed with $LASTEXITCODE"; exit 1 }
            sc.exe description $name "Self-hosted GitHub Actions runner for PeakRelay releases (auto-start)"
            sc.exe failure $name reset= 86400 actions= restart/5000/restart/5000/restart/5000
            sc.exe start $name
            if ($LASTEXITCODE -ne 0) { Write-Output "sc start failed with $LASTEXITCODE"; exit 1 }
            Start-Sleep -Seconds 4
            $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
            if ($svc -eq $null) { Write-Output "service vanished after start"; exit 1 }
            Write-Output ("service {0} is {1}" -f $name, $svc.Status)
            if ($svc.Status -ne 'Running') { exit 1 }
            exit 0
            """;
    }

    /// <summary>Runs a PowerShell script elevated via the shell's runas dialog; returns the verdict.</summary>
    private static RunnerServiceResult RunElevated(string scriptPath, string what)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas", // Windows shows the UAC prompt; user consents there
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var proc = Process.Start(psi)!;
            if (!proc.WaitForExit(180_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch (SystemException) { /* just timed out */ }
                return new RunnerServiceResult(false, $"timed out while trying to {what} (UAC prompt answered?)");
            }
            return proc.ExitCode == 0
                ? new RunnerServiceResult(true, $"done: {what}")
                : new RunnerServiceResult(false, $"could not {what} (exit code {proc.ExitCode}) — " +
                                                 "check %TEMP%\\peakrelay-runner-service*.ps1 output in an elevated prompt");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new RunnerServiceResult(false, $"cancelled: {what} needs administrator approval (UAC prompt declined)");
        }
    }
}
