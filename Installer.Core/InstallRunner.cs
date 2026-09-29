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

/// <summary>Executes an InstallRequest; the GUI shows the same steps in a progress list.</summary>
public static class InstallRunner
{
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

/// <summary>Bad CLI input: message goes to stderr, process exits 2, nothing is installed.</summary>
public sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }
}

/// <summary>
/// CLI mode for both installers: the same InstallRunner path as the GUI. Supported flags:
///   --dir &lt;path&gt;       PEAK install root (required unless a default install is found)
///   --host &lt;host&gt;      relay host (client: config; server: where to find the relay)
///   --port &lt;port&gt;      relay port (default 5055)
///   --room &lt;code&gt;      server only: room code (default DBPEAK)
///   --name &lt;text&gt;      server only: display name
///   --max &lt;n&gt;          server only: max players (default 20, GUI range 1-100)
///   --password &lt;text&gt;  server only: room password (default none)
///   --stage-relay &lt;d&gt;  server only: also copy relay files into &lt;d&gt;
///   --hostname &lt;text&gt;  server only: host player name without Steam (default DedicatedHost)
///   --yes              non-interactive; reserved (input never prompts)
/// </summary>
public static class Cli
{
    public sealed class Args
    {
        public string? Dir, Host, Room, Name, Password, StageRelay, Hostname;
        public int Port = 5055;
        public bool MaxSet;
        public int Max = 20;
        public bool Help;
    }

    private const int MinMaxPlayers = 1;
    private const int MaxMaxPlayers = 100; // same range as the GUI's NumericUpDown

    /// <summary>
    /// Parses the flags. Throws <see cref="UsageException"/> on anything a script must not
    /// silently misread (unknown flag, missing flag value, unparsable/out-of-range number).
    /// </summary>
    public static Args Parse(string[] arguments)
    {
        var args = new Args();
        for (var i = 0; i < arguments.Length; i++)
        {
            string Value(string flag)
            {
                if (i + 1 >= arguments.Length || arguments[i + 1].StartsWith('-'))
                    throw new UsageException($"missing value for {flag}");
                return arguments[++i];
            }

            switch (arguments[i].ToLowerInvariant())
            {
                case "--dir": args.Dir = Value("--dir"); break;
                case "--host": args.Host = Value("--host"); break;
                case "--port":
                    if (!int.TryParse(Value("--port"), out var port) || port is < 1 or > 65535)
                        throw new UsageException("--port must be a TCP port (1-65535)");
                    args.Port = port;
                    break;
                case "--room": args.Room = Value("--room"); break;
                case "--name": args.Name = Value("--name"); break;
                case "--password": args.Password = Value("--password"); break;
                case "--max":
                    if (!int.TryParse(Value("--max"), out var max))
                        throw new UsageException("--max must be an integer");
                    if (max is < MinMaxPlayers or > MaxMaxPlayers)
                        throw new UsageException($"--max must be {MinMaxPlayers}-{MaxMaxPlayers}");
                    args.Max = max;
                    args.MaxSet = true;
                    break;
                case "--stage-relay": args.StageRelay = Value("--stage-relay"); break;
                case "--hostname": args.Hostname = Value("--hostname"); break;
                case "--yes": break; // accepted for script compatibility; input never prompts
                case "-h" or "--help": args.Help = true; break;
                default:
                    throw new UsageException($"unknown argument '{arguments[i]}'");
            }
        }
        return args;
    }

    /// <summary>Runs CLI mode. Exit codes: 0 install verified, 1 install failed, 2 usage error.</summary>
    public static int Run(string[] arguments, bool serverMode)
    {
        Args args;
        try
        {
            args = Parse(arguments);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"usage error: {ex.Message}");
            PrintHelp(serverMode);
            return 2;
        }

        if (args.Help)
        {
            PrintHelp(serverMode);
            return 0;
        }
        if (arguments.Length == 0)
        {
            PrintHelp(serverMode);
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
            InstallRunner.Run(request, s => Console.WriteLine("  " + s));
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
            Console.WriteLine("  --max <n>           max players, 1-100 (default 20)");
            Console.WriteLine("  --password <text>   room password (default none)");
            Console.WriteLine("  --hostname <text>   host player name without Steam (default DedicatedHost)");
            Console.WriteLine("  --stage-relay <dir> also copy the relay server into this folder");
        }
        Console.WriteLine("  --host <host>       relay host (default 127.0.0.1)");
        Console.WriteLine("  --port <port>       relay port (default 5055)");
        Console.WriteLine("  --yes               non-interactive (input never prompts)");
        Console.WriteLine("  -h, --help          this help");
    }
}
