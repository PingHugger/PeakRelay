using System.Diagnostics;
using System.Text;

namespace PeakRelay.Installer.Core;

/// <summary>What one installer run does, resolved from CLI flags or GUI fields.</summary>
public sealed class InstallRequest
{
    public string GameDir { get; init; } = "";
    public ServerSettings? Server { get; init; }
    public ClientSettings? Client { get; init; }
    public string? RelayStageDir { get; init; }
}

/// <summary>Executes an InstallRequest; GUI shows the same steps in a progress list.</summary>
public static class InstallRunner
{
    public sealed record Step(string Text);

    /// <summary>Runs the install, invoking onStep for each completed action (returns step log).</summary>
    public static List<string> Run(InstallRequest request, Action<string>? onStep = null)
    {
        var log = new List<string>();
        void Say(string text)
        {
            log.Add(text);
            onStep?.Invoke(text);
        }

        if (!GameLocator.LooksLikeGameRoot(request.GameDir))
            throw new InvalidOperationException(
                $"'{request.GameDir}' does not look like a PEAK install (no PEAK.exe)");

        Say($"game dir: {request.GameDir}");
        foreach (var line in PluginDeployer.EnsureBepInEx(request.GameDir))
            Say(line);

        if (request.Server != null)
        {
            foreach (var line in PluginDeployer.DeployDedicated(request.GameDir))
                Say(line);
            var cfg = PluginDeployer.WriteServerConfig(request.GameDir, request.Server);
            Say($"server.json written: {cfg}");
        }

        if (request.Client != null)
        {
            foreach (var line in PluginDeployer.DeployClient(request.GameDir))
                Say(line);
            var cfg = PluginDeployer.WriteClientConfig(request.GameDir, request.Client);
            Say($"client config written: {cfg}");
        }

        if (request.RelayStageDir != null)
            Say($"relay staged: {RelayStager.Stage(request.RelayStageDir)}");

        return log;
    }

    /// <summary>Post-install verification lines.</summary>
    public static List<string> Verify(InstallRequest request)
    {
        var checks = request.Server != null
            ? Verifier.VerifyServerInstall(request.GameDir, request.Server)
            : Verifier.VerifyClientInstall(request.GameDir, request.Client!);
        return checks.Select(c => $"[{(c.Ok ? "OK" : "FAIL")}] {c.Name}: {c.Detail}").ToList();
    }
}

/// <summary>
/// CLI mode for both installers: same InstallRunner path as the GUI. Supported flags:
///   --dir <path>       PEAK install root (required unless --find)
///   --host <host>      relay host (client: config; server: where to find the relay)
///   --port <port>      relay port (default 5055)
///   --room <code>      server only: room code (default DBPEAK)
///   --name <text>      server only: display name
///   --max <n>          server only: max players (default 20)
///   --password <text>  server only: room password (default none)
///   --stage-relay <d>  server only: also copy relay files into <d>
///   --hostname <text>  server only: host player name without Steam (default DedicatedHost)
///   --yes              non-interactive; fails instead of prompting
/// </summary>
public static class Cli
{
    public sealed class Args
    {
        public string? Dir, Host, Room, Name, Password, StageRelay, Hostname;
        public int Port = 5055;
        public bool Yes, MaxSet;
        public int Max = 20;
    }

    public static Args? Parse(string[] arguments, TextWriter errors)
    {
        var args = new Args();
        for (var i = 0; i < arguments.Length; i++)
        {
            string Value(string flag)
            {
                if (i + 1 >= arguments.Length)
                {
                    errors.WriteLine($"missing value for {flag}");
                    return null!;
                }
                return arguments[++i];
            }

            switch (arguments[i].ToLowerInvariant())
            {
                case "--dir": args.Dir = Value("--dir"); break;
                case "--host": args.Host = Value("--host"); break;
                case "--port": int.TryParse(Value("--port"), out args.Port); break;
                case "--room": args.Room = Value("--room"); break;
                case "--name": args.Name = Value("--name"); break;
                case "--password": args.Password = Value("--password"); break;
                case "--max": if (int.TryParse(Value("--max"), out var max)) { args.Max = max; args.MaxSet = true; } break;
                case "--stage-relay": args.StageRelay = Value("--stage-relay"); break;
                case "--hostname": args.Hostname = Value("--hostname"); break;
                case "--yes": args.Yes = true; break;
                case "-h" or "--help":
                    return null;
                default:
                    errors.WriteLine($"unknown argument '{arguments[i]}' (see --help)");
                    return args;
            }
        }
        return args;
    }

    /// <summary>Runs CLI mode. Returns process exit code.</summary>
    public static int Run(string[] arguments, bool serverMode)
    {
        if (arguments.Contains("-h") || arguments.Contains("--help") || arguments.Length == 0)
        {
            PrintHelp(serverMode);
            return 2;
        }

        var errors = Console.Error;
        var args = Parse(arguments, errors);
        if (args == null || (arguments.Length > 0 && args.Dir == null && !arguments.Contains("--dir")))
        {
            // Parse printed an error for unknown flags; only continue when --dir came through
            if (args == null)
                return 2;
        }

        try
        {
            var gameDir = args.Dir ?? GameLocator.FindDefaultGameDir()
                ?? throw new InvalidOperationException("no --dir given and no PEAK install found");

            InstallRequest request;
            if (serverMode)
            {
                var settings = new ServerSettings(
                    RoomName: args.Room ?? "DBPEAK",
                    DisplayName: args.Name ?? "PeakRelay Dedicated",
                    Mode: "standard",
                    Password: args.Password ?? "",
                    MaxPlayers: args.Max,
                    RelayHost: args.Host ?? "127.0.0.1",
                    RelayPort: args.Port,
                    AutoHost: true,
                    HostName: args.Hostname ?? "DedicatedHost");
                request = new InstallRequest { GameDir = gameDir, Server = settings, RelayStageDir = args.StageRelay };
            }
            else
            {
                request = new InstallRequest
                {
                    GameDir = gameDir,
                    Client = new ClientSettings(args.Host ?? "127.0.0.1", args.Port),
                };
            }

            Console.WriteLine($"PeakRelay {(serverMode ? "server" : "client")} installer (CLI)");
            foreach (var line in InstallRunner.Run(request, s => Console.WriteLine("  " + s)))
            {
            }
            Console.WriteLine();
            var failed = false;
            foreach (var line in InstallRunner.Verify(request))
            {
                Console.WriteLine(line);
                if (line.Contains("[FAIL]"))
                    failed = true;
            }
            return failed ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"install failed: {ex.Message}");
            return 1;
        }
    }

    private static void PrintHelp(bool serverMode)
    {
        Console.WriteLine($"PeakRelay {(serverMode ? "server" : "client")} installer");
        Console.WriteLine();
        Console.WriteLine("Usage: installer [flags]");
        Console.WriteLine("  --dir <path>        PEAK install root (folder containing PEAK.exe)");
        if (serverMode)
        {
            Console.WriteLine("  --room <code>       room code, 6 chars (default DBPEAK)");
            Console.WriteLine("  --name <text>       display name for the browser (default 'PeakRelay Dedicated')");
            Console.WriteLine("  --max <n>           max players (default 20)");
            Console.WriteLine("  --password <text>   room password (default none)");
            Console.WriteLine("  --hostname <text>   host player name without Steam (default DedicatedHost)");
            Console.WriteLine("  --stage-relay <dir> also copy the relay server into this folder");
        }
        Console.WriteLine("  --host <host>       relay host (default 127.0.0.1)");
        Console.WriteLine("  --port <port>       relay port (default 5055)");
        Console.WriteLine("  --yes               non-interactive");
        Console.WriteLine("  -h, --help          this help");
    }
}
