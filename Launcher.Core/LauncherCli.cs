using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeakRelay.Installer.Core;

namespace PeakRelay.Launcher.Core;

/// <summary>Bad CLI input: message goes to stderr, process exits 2, nothing happens.</summary>
public sealed class LauncherUsageException : Exception
{
    public LauncherUsageException(string message) : base(message) { }
}

/// <summary>
/// CLI mode for the launcher — the same engine the GUI drives. Verbs:
///   doctor                inspect the install, print a status report (default)
///   install               apply the latest release asset to the game dir (+configs)
///   update                install only when a newer release exists
///   play                  run the doctor, then start PEAK.exe
///   host [--port] [--http] host the relay in-process until Ctrl+C
///   selfupdate            download and start the latest launcher installer
/// Exit codes mirror the installers: 0 ok, 1 failure, 2 usage.
/// </summary>
public static class LauncherCli
{
    public sealed class Args
    {
        public string? Dir;
        public string? Host, Room, Password, Hostname, Token;
        public int Port = 5055;
        public int HttpPort = 5056;
        public int Max = 20;
        public bool Yes;
        public bool Dedicated;
        public string Command = "doctor";
    }

    public static async Task<int> Run(string[] arguments)
    {
        Args args;
        try
        {
            args = Parse(arguments);
        }
        catch (LauncherUsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine("usage: PeakRelay.Launcher [--dir <path>] [--yes] [--dedicated] [doctor|install|update|play|host|selfupdate|set-token|clear-token]");
            return 2;
        }

        try
        {
            return args.Command switch
            {
                "doctor" => await DoctorAsync(args).ConfigureAwait(false),
                "install" => await InstallAsync(args, force: true).ConfigureAwait(false),
                "update" => await InstallAsync(args, force: false).ConfigureAwait(false),
                "play" => await PlayAsync(args).ConfigureAwait(false),
                "host" => await HostAsync(args).ConfigureAwait(false),
                "selfupdate" => await SelfUpdateAsync().ConfigureAwait(false),
                "set-token" => await SetTokenAsync(args).ConfigureAwait(false),
                "clear-token" => await ClearTokenAsync().ConfigureAwait(false),
                _ => throw new LauncherUsageException($"unknown command '{args.Command}'"),
            };
        }
        catch (LauncherUsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static Args Parse(string[] arguments)
    {
        var args = new Args();
        var positional = new List<string>();
        for (var i = 0; i < arguments.Length; i++)
        {
            string Value(string flag)
            {
                if (i + 1 >= arguments.Length || arguments[i + 1].StartsWith('-'))
                    throw new LauncherUsageException($"missing value for {flag}");
                return arguments[++i];
            }
            switch (arguments[i].ToLowerInvariant())
            {
                case "--dir": args.Dir = Value("--dir"); break;
                case "--host": args.Host = Value("--host"); break;
                case "--room": args.Room = Value("--room"); break;
                case "--password": args.Password = Value("--password"); break;
                case "--hostname": args.Hostname = Value("--hostname"); break;
                case "--port":
                    if (!int.TryParse(Value("--port"), out var port) || port is < 1 or > 65535)
                        throw new LauncherUsageException("--port must be a TCP port (1-65535)");
                    args.Port = port;
                    break;
                case "--http":
                    if (!int.TryParse(Value("--http"), out var http) || http is < 1 or > 65535)
                        throw new LauncherUsageException("--http must be a TCP port (1-65535)");
                    args.HttpPort = http;
                    break;
                case "--max":
                    if (!int.TryParse(Value("--max"), out var max) || max is < 1 or > 100)
                        throw new LauncherUsageException("--max must be 1-100");
                    args.Max = max;
                    break;
                case "--yes": args.Yes = true; break;
                case "--dedicated": args.Dedicated = true; break;
                case "--token": args.Token = Value("--token"); break;
                default:
                    if (arguments[i].StartsWith('-'))
                        throw new LauncherUsageException($"unknown flag '{arguments[i]}'");
                    positional.Add(arguments[i]);
                    break;
            }
        }
        if (positional.Count > 1)
            throw new LauncherUsageException($"expected at most one command, got: {string.Join(' ', positional)}");
        if (positional.Count == 1)
            args.Command = positional[0];
        return args;
    }

    private static string ResolveDir(Args args, LauncherState state) =>
        args.Dir ?? (string.IsNullOrWhiteSpace(state.GameDir) ? GameLocator.FindDefaultGameDir() : state.GameDir)
        ?? throw new LauncherUsageException("no --dir given, no saved game dir, and no PEAK install found");

    /// <summary>Stores a GitHub token for private-repo channels. Env var still wins.</summary>
    private static Task<int> SetTokenAsync(Args args)
    {
        var token = args.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Write("paste GitHub token (stored per-user in %LOCALAPPDATA%\\PeakRelay\\github.token): ");
            token = Console.ReadLine();
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("error: no token given (use 'set-token --token <value>' or paste at the prompt)");
            return Task.FromResult(2);
        }
        TokenStore.SaveToFile(token);
        Console.WriteLine($"token saved to {LauncherPaths.TokenFile}");
        Console.WriteLine($"(note: {TokenStore.EnvVar} in the environment takes precedence over the file)");
        return Task.FromResult(0);
    }

    private static Task<int> ClearTokenAsync()
    {
        TokenStore.DeleteFile();
        Console.WriteLine($"token file removed: {LauncherPaths.TokenFile}");
        return Task.FromResult(0);
    }

    private static Task<int> DoctorAsync(Args args)
    {
        var state = LauncherState.Load();
        var gameDir = ResolveDir(args, state);
        var checks = Doctor.Inspect(gameDir, state);
        foreach (var check in checks)
        {
            var mark = check.Status switch
            {
                CheckStatus.Pass => "[OK ]",
                CheckStatus.Warn => "[WARN]",
                _ => "[FAIL]",
            };
            Console.WriteLine($"{mark} {check.Name}: {check.Detail}");
            if (check.Fix != null)
                Console.WriteLine($"       fix: {check.Fix}");
        }
        Console.WriteLine(checks.All(c => c.Status != CheckStatus.Fail)
            ? "ready to play — run 'PeakRelay.Launcher play'"
            : "not ready — run 'PeakRelay.Launcher install' to repair");
        return Task.FromResult(checks.Any(c => c.Status == CheckStatus.Fail) ? 1 : 0);
    }

    private static async Task<int> InstallAsync(Args args, bool force)
    {
        var state = LauncherState.Load();
        var gameDir = ResolveDir(args, state);
        var checks = Doctor.Inspect(gameDir, state);
        if (!GameLocator.LooksLikeGameRoot(gameDir))
        {
            Console.Error.WriteLine($"error: '{gameDir}' is not a PEAK install (no PEAK.exe)");
            return 1;
        }

        using var client = new ReleaseClient();
        var latest = await client.LatestAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("no releases found — nothing to install");
        var asset = ModApply.DefaultAssetFor(latest);
        if (!force && latest.Tag == state.InstalledTag)
        {
            Console.WriteLine($"already on {latest.Tag} ({asset.Name})");
            return 0;
        }
        if (!args.Yes && latest.Tag == state.SkippedTag)
        {
            Console.WriteLine($"{latest.Tag} was skipped earlier; use --yes to apply it anyway");
            return 0;
        }
        if (!args.Yes)
        {
            Console.Write($"Apply {latest.Tag} ({asset.Name}) to '{gameDir}'? [y/N] ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("aborted");
                return 0;
            }
        }

        var log = await ModApply.ApplyAsync(gameDir, asset, state, latest.Tag,
            (a, destination, t) => client.DownloadAsync(a, destination, t),
            args.Host, args.Port, args.Room, args.Password, args.Hostname, args.Max,
            includeDedicated: args.Dedicated).ConfigureAwait(false);
        foreach (var line in log)
            Console.WriteLine("  " + line);

        Console.WriteLine();
        foreach (var check in Doctor.Inspect(gameDir, state))
            Console.WriteLine($"[{check.Status}] {check.Name}: {check.Detail}");
        return 0;
    }

    private static Task<int> PlayAsync(Args args)
    {
        var state = LauncherState.Load();
        var gameDir = ResolveDir(args, state);
        var checks = Doctor.Inspect(gameDir, state);
        if (!Doctor.PlayReady(checks))
        {
            foreach (var check in checks.Where(c => c.Status == CheckStatus.Fail))
                Console.Error.WriteLine($"[FAIL] {check.Name}: {check.Detail}");
            Console.Error.WriteLine("not ready — run 'PeakRelay.Launcher install' first");
            return Task.FromResult(1);
        }
        var process = PlayLauncher.Start(gameDir);
        Console.WriteLine($"PEAK started (pid {process.Id})");
        return Task.FromResult(0);
    }

    private static async Task<int> HostAsync(Args args)
    {
        using var host = new RelayHost();
        try
        {
            host.Start(args.Port, args.HttpPort);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"relay failed to start: {ex.Message}");
            return 1;
        }
        Console.WriteLine($"relay running: game {args.Port}, directory http://127.0.0.1:{args.HttpPort}/rooms — Ctrl+C to stop");
        var done = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            done.Set();
        };
        await Task.Run(done.Wait).ConfigureAwait(false);
        host.Stop();
        Console.WriteLine("relay stopped");
        return 0;
    }

    private static async Task<int> SelfUpdateAsync()
    {
        using var client = new ReleaseClient();
        var latest = await client.LatestAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("no releases found");
        if (!ReleaseClient.VersionDiffers(latest.Tag, ReleaseClient.RunningVersion()))
        {
            Console.WriteLine($"already on {latest.Tag} — nothing to self-update");
            return 0;
        }
        var asset = latest.Assets.FirstOrDefault(a => a.Name.StartsWith("PeakRelayLauncher", StringComparison.OrdinalIgnoreCase)
                                                      && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (asset == null)
        {
            Console.WriteLine($"release {latest.Tag} has no launcher exe yet — nothing to self-update to");
            return 1;
        }
        var temp = Path.Combine(Path.GetTempPath(), asset.Name);
        Console.WriteLine($"downloading {asset.Name} ({asset.Size / 1024 / 1024} MB)…");
        await client.DownloadAsync(asset, temp).ConfigureAwait(false);
        Console.WriteLine($"launching {asset.Name}…");
        Process.Start(new ProcessStartInfo { FileName = temp, UseShellExecute = true });
        return 0;
    }
}
