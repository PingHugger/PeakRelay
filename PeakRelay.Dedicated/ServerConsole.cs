using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace PeakRelay.Dedicated;

/// <summary>
/// The dedicated server's operator console: a dedicated Windows console window that stays
/// open for the process lifetime and keeps a persistent, color-coded log pane with a
/// "server&gt;" prompt at the bottom.
///
/// Friendly by design: messages are full sentences with high-level wordings ("The expedition
/// 'X' is now open for adventurers", not "CreateRoom roomName=X returnCode=32762"), operator
/// input is echoed in white, help is written in plain language, and raw technical detail
/// (datagram sizes, op streams, exception internals) is hidden unless the operator opts in
/// with `console --verbose`.
///
/// Threads: the game thread calls the log API; a background thread runs the blocking stdin
/// loop and executes commands. Console.Window* calls throw when no real window exists —
/// every such call is guarded and degrades to plain scrolling output, so a piped/service
/// run never crashes from logging.
/// </summary>
internal static class ServerConsole
{
    private static readonly object Sync = new();

    // Log pane state (under Sync).
    private static readonly List<string> Pane = new();
    private const int PaneCapacity = 4000;
    private static bool _paneValid; // false → full repaint instead of delta-append

    // The dedicated log: every line we write, newest last, oldest trimmed from the top.
    private static readonly Queue<string> DedicatedLog = new();
    private const int DedicatedLogCapacity = 200;

    /// <summary>Test hook: when set, receives every pane line as (text, color).</summary>
    internal static Action<string, string>? TestSink;

    /// <summary>Test hooks: force console state without a real console window.</summary>
    internal static void SetVerboseForTests(bool verbose) => _verbose = verbose;

    internal static void SetWindowWidthForTests(int width)
    {
        _consoleWidth = width;
        _paneValid = true;
    }

    private static bool _verbose;

    /// <summary>
    /// Live verbose gate: prefers the config so the 'verbose on/off' command (which mutates
    /// the config) takes effect immediately; the field is only the fallback when no config
    /// is loaded (tests).
    /// </summary>
    private static bool VerboseEnabled
    {
        get
        {
            try { return DedicatedState.Config?.ConsoleVerbose ?? _verbose; }
            catch { return _verbose; }
        }
    }

    private static Thread? _inputThread;
    private static volatile bool _shuttingDown;
    private static int _consoleWidth;
    private const int DefaultWidth = 120;
    private static string? _lastRelayStatus;

    /// <summary>Local time format used across the console and the file log mirror.</summary>
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>
    /// Substrings of developer-oriented ServerLog lines that the console hides by default;
    /// `console --verbose` or server.json "consoleVerbose" reveals them.
    /// </summary>
    private static readonly string[] VerboseOnlySnippets =
    {
        "C->S", "S->C", "serialization forced", "connect redirected", "via vanilla options",
    };

    // ---------------------------------------------------------------- console lifecycle

    /// <summary>
    /// Opens the console window (AttachConsole/AllocConsole give Unity's -batchmode process
    /// a console when it has none, or reuse the parent terminal when started from one).
    /// Safe even when no window results: everything degrades to plain Console.WriteLine.
    /// </summary>
    internal static void Start(DedicatedConfig config)
    {
        try { AllocateConsole(); }
        catch { /* cosmetic only */ }

        // AFTER the console exists (and stdout points at it), so the encoding sticks.
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch { /* stdout redirected to a pipe */ }

        _consoleWidth = TryGetWindowWidth() ?? DefaultWidth;
        _verbose = config.ConsoleVerbose;

        WriteRule();
        Message("PEAK dedicated server console");
        Message("Type 'help' and press Enter to see what this server can do.");
        WriteRule();

        _inputThread = new Thread(InputLoop) { IsBackground = true, Name = "PeakRelay Console" };
        _inputThread.Start();
    }

    internal static void Shutdown()
    {
        _shuttingDown = true;
        try { Console.Out.Flush(); } catch { /* closing */ }
    }

    /// <summary>
    /// The 'stop' command's shutdown path (runs on the console stdin thread — bug report:
    /// the old stop never exited). Two hard-won lessons are baked in:
    ///
    /// 1. Application.Quit/Environment.Exit from this background thread hang the process in
    ///    Unity players (Mono shutdown waits on the main thread) — so quit is marshaled to
    ///    the Unity main loop via MainThreadJobs (drained by DedicatedPlugin.Update), and
    ///    only then Environment.Exit from the main thread.
    /// 2. Nothing is trusted to finish: a watchdog hard-kills the process if the graceful
    ///    path stalls (stuck Unity teardown, blocked finalizers…).
    /// </summary>
    internal static void RequestShutdown()
    {
        Shutdown(); // idempotent: ends the stdin loop, flushes the log
        ArmShutdownWatchdog();
        DedicatedPlugin.MainThreadJobs.Enqueue(RequestedQuitOnMainThread);
    }

    /// <summary>
    /// Runs on the Unity main thread (via MainThreadJobs): flushes the log file, then asks
    /// Unity to quit. Live-run lesson: Environment.Exit from any thread HANGS in this Unity
    /// player (observed even on the main thread), so the watchdog — not this method — is
    /// what actually guarantees the exit.
    /// </summary>
    private static void RequestedQuitOnMainThread()
    {
        try { WriteLogLine("PEAK", "cyan", "Server shut down by operator (stop). Goodbye!", verboseOnly: false); }
        catch { /* logging never throws */ }
        try { UnityEngine.Application.Quit(); } catch { /* headless quit niceties */ }
    }

    /// <summary>
    /// Background safety net: if the graceful quit has not ended the process in time, kill
    /// it at OS level. Process.Kill is the only shutdown primitive that cannot hang —
    /// Environment.Exit was observed to deadlock in the Unity player even on the main
    /// thread, so it is deliberately NOT attempted here.
    /// </summary>
    private static void ArmShutdownWatchdog()
    {
        // Never in tests (TestSink set): a stray kill would take down the test runner.
        if (TestSink != null)
            return;
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(5000);
            try { Console.Out.WriteLine("(the server did not close in time — forcing it down)"); Console.Out.Flush(); } catch { /* console gone */ }
            try { ServerLog.MirrorToDedicatedLog($"{DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture)} [PEAK] graceful shutdown stalled — forcing exit"); } catch { /* never throws */ }
            try { System.Diagnostics.Process.GetCurrentProcess().Kill(); } catch { /* nothing left to try */ }
        })
        { IsBackground = true, Name = "PeakRelay Shutdown Watchdog" };
        watchdog.Start();
    }

    /// <summary>
    /// Windows: ALWAYS create our own console window. Attaching to the parent is not enough
    /// — the server is usually started by the launcher or a service whose console is hidden
    /// (live-run find: the operator then sees nothing at all). A dedicated window is the
    /// predictable outcome, whether PeakServer.exe was double-clicked, launched by the
    /// launcher, or started from a terminal.
    /// </summary>
    private static void AllocateConsole()
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
            return;
        AllocConsole(); // fails harmlessly (ACCESS_DENIED) if a console already exists
        try
        {
            SetConsoleOutputCP(65001); // UTF-8, so rules and arrows render everywhere
            if (GetConsoleWindow() != IntPtr.Zero)
                SetConsoleTitleW("PEAK dedicated server — PeakRelay");
            // Unity's -batchmode GUI process starts without console handles — point stdin and
            // stdout at the console we just (at)tached. BUT: when a parent deliberately
            // redirected them (launcher, terminal `> file`, Docker), we keep that and stay a
            // well-behaved pipe citizen.
            if (StdHandleIsUnset(StdOutputHandle))
                RedirectStdHandle(StdOutputHandle, "CONOUT$");
            if (StdHandleIsUnset(StdErrorHandle))
                RedirectStdHandle(StdErrorHandle, "CONOUT$");
            if (StdHandleIsUnset(StdInputHandle))
                RedirectStdHandle(StdInputHandle, "CONIN$");
        }
        catch
        {
            // cosmetic only — the console also works without title/codepage niceties
        }
    }

    private static bool StdHandleIsUnset(int stdHandle)
    {
        try
        {
            var handle = GetStdHandle(stdHandle);
            return handle == IntPtr.Zero || handle == new IntPtr(-1);
        }
        catch
        {
            return false;
        }
    }

    private static void RedirectStdHandle(int stdHandle, string device)
    {
        // GENERIC_READ|GENERIC_WRITE, FILE_SHARE_READ|FILE_SHARE_WRITE, OPEN_EXISTING
        var handle = CreateFileW(device, 0xC0000000u, 0x3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return;
        SetStdHandle(stdHandle, handle);
        CloseHandle(handle);
    }

    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    // ---------------------------------------------------------------- friendly write API

    /// <summary>A neutral status update (cyan).</summary>
    internal static void Message(string text) => WriteLogLine("PEAK", "cyan", text, verboseOnly: false);

    /// <summary>Something went well (green).</summary>
    internal static void Success(string text) => WriteLogLine("PEAK", "green", text, verboseOnly: false);

    /// <summary>A warning the operator should know about (yellow).</summary>
    internal static void Warn(string text) => WriteLogLine("PEAK", "yellow", text, verboseOnly: false);

    /// <summary>A problem that needs attention (red).</summary>
    internal static void Error(string text) => WriteLogLine("PEAK", "red", text, verboseOnly: false);

    /// <summary>Player chat as heard in the room, e.g. "Bob: hello everyone" (white).</summary>
    internal static void Chat(string text) => WriteLogLine("CHAT", "white", text, verboseOnly: false);

    /// <summary>
    /// A technical detail hidden unless the operator asked for verbose output.
    /// Always mirrored to server.log so nothing is lost.
    /// </summary>
    internal static void Verbose(string text) => WriteLogLine("PEAK", "dark", text, verboseOnly: true);

    /// <summary>
    /// Relay status — the piece of information operators ask about most. Only prints when
    /// something actually changed.
    /// </summary>
    internal static void RelayStatus(bool connected, int players, int maxPlayers)
    {
        var text = connected
            ? $"Server is up and running — {players} of {maxPlayers} player slots in use"
            : "Server is starting up — no connection to the relay yet";
        bool changed;
        lock (Sync)
        {
            changed = text != _lastRelayStatus;
            _lastRelayStatus = text;
        }
        if (changed)
            WriteLogLine("PEAK", "cyan", text, verboseOnly: false);
    }

    // ---------------------------------------------------------------- command loop

    /// <summary>True once the dedicated host has fully joined its room.</summary>
    internal static bool RelayUp { get; set; }

    /// <summary>Snapshot of the room's player list for 'status' and 'players'.</summary>
    internal static List<PlayerLine> PlayersSnapshot()
    {
        try
        {
            var room = Photon.Pun.PhotonNetwork.CurrentRoom;
            if (room == null || !ServerConsole.RelayUp)
                return new List<PlayerLine>();
            return room.Players
                .Values
                .OrderBy(p => p.ActorNumber)
                .Select(p => new PlayerLine(
                    string.IsNullOrWhiteSpace(p.NickName) ? $"Player {p.ActorNumber}" : p.NickName.Trim(),
                    p.UserId ?? "",
                    p.ActorNumber))
            .ToList();
        }
        catch
        {
            return new List<PlayerLine>();
        }
    }

    /// <summary>Wipes the on-screen pane ('clear' command).</summary>
    internal static void ClearPane()
    {
        lock (Sync)
        {
            Pane.Clear();
            try
            {
                Console.Clear();
                _paneValid = true;
                DrawPrompt();
            }
            catch
            {
                _paneValid = false;
            }
        }
    }

    private static void InputLoop()
    {
        while (!_shuttingDown)
        {
            string? line;
            try { line = Console.ReadLine(); }
            catch { return; } // console closed or stdin gone
            if (line == null)
                return; // stdin EOF
            try { ExecuteCommand(line); }
            catch (Exception ex)
            {
                Error("That command could not be executed. " + ex.Message);
            }
        }
    }

    /// <summary>Executes one command line (public to tests and the launcher).</summary>
    internal static void ExecuteCommand(string rawInput)
    {
        var input = rawInput.Trim();
        if (input.Length == 0)
            return;

        EchoUserLine(input);
        DedicatedLog.Enqueue($"{DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture)} [YOU] You typed: {input}");
        while (DedicatedLog.Count > DedicatedLogCapacity)
            DedicatedLog.Dequeue();

        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = parts[0].ToLowerInvariant();
        var args = parts.Skip(1).ToArray();

        var command = ConsoleCommandRegistry.Find(name);
        if (command == null)
        {
            Error($"Unknown command '{name}'. Type 'help' to see everything this server understands.");
            return;
        }
        command.Run(args);
    }

    /// <summary>The command set, listed by 'help'.</summary>
    internal static IReadOnlyList<IConsoleCommand> Commands => ConsoleCommandRegistry.All;

    // ---------------------------------------------------------------- output core

    private static void WriteLogLine(string level, string color, string text, bool verboseOnly)
    {
        var line = $"{DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture)} [{level}] {text}";
        try { ServerLog.MirrorToDedicatedLog(line); } catch { /* logging never throws */ }

        lock (Sync)
        {
            DedicatedLog.Enqueue(line);
            while (DedicatedLog.Count > DedicatedLogCapacity)
                DedicatedLog.Dequeue();
        }

        if (verboseOnly && !VerboseEnabled)
            return;
        if (IsVerboseOnly(line) && !VerboseEnabled)
            return; // with verbose ON, even snippet-tagged lines are welcome
        Append(line, color);
    }

    /// <summary>Routes a ServerLog line into the console: friendly phrasing, friendly status.</summary>
    internal static void HandleServerLog(string message)
    {
        if (TryHandleFriendly(message, out var friendly, out var color))
        {
            WriteLogLine("PEAK", color, friendly, verboseOnly: false);
            return;
        }
        // Anything the translation table does not know is technical: keep it on the verbose
        // side of the console (it still lands in server.log above).
        WriteLogLine("PEAK", "dark", message, verboseOnly: true);
    }

    /// <summary>Certain raw log lines are warnings/problems regardless of wording.</summary>
    private static string FriendlyColor(string message)
    {
        if (message.Contains("not headless", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("will retry", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("disconnected from relay", StringComparison.Ordinal))
            return "yellow";
        if (message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("giving up", StringComparison.OrdinalIgnoreCase))
            return "red";
        return "cyan";
    }

    private static bool IsVerboseOnly(string line)
    {
        foreach (var snippet in VerboseOnlySnippets)
        {
            if (line.Contains(snippet, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static void EchoUserLine(string input)
    {
        Append($"{DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture)} [YOU] > {input}", "white");
    }

    // ---------------------------------------------------------------- friendly translation

    /// <summary>
    /// Turns known technical log lines into full sentences a non-developer understands.
    /// Unknown lines stay as-is (and stay out of the default view).
    /// </summary>
    private static bool TryHandleFriendly(string message, out string friendly, out string color)
    {
        friendly = message;
        color = FriendlyColor(message);

        if (message.StartsWith("config: ", StringComparison.Ordinal))
        {
            friendly = "Server settings loaded (see server.json for details).";
            return true;
        }
        if (message.StartsWith("not headless", StringComparison.Ordinal))
        {
            friendly = "This PEAK was started as a normal game, not as a dedicated server. " +
                       "Dedicated-server mode stays off (start with -batchmode -nographics).";
            return true;
        }
        if (message.StartsWith("platform bootstrap completed", StringComparison.Ordinal))
        {
            friendly = "The game started up without Steam — running as a standalone server.";
            return true;
        }
        if (message.StartsWith("GameHandler ready", StringComparison.Ordinal))
        {
            friendly = "Game engine is ready.";
            return true;
        }
        if (message.StartsWith("Title scene active", StringComparison.Ordinal))
        {
            friendly = "Main menu reached.";
            return true;
        }
        if (message.StartsWith("autoHost=false", StringComparison.Ordinal))
        {
            friendly = "Automatic hosting is switched off — the expedition will not start on its own.";
            return true;
        }
        if (message.StartsWith("HostState armed", StringComparison.Ordinal))
        {
            var room = ExtractQuoted(message);
            friendly = string.IsNullOrEmpty(room)
                ? "Preparing to open the expedition…"
                : $"Opening the expedition '{room}'…";
            return true;
        }
        if (message.StartsWith("room created:", StringComparison.Ordinal))
        {
            var room = ExtractQuoted(message);
            var max = ExtractNumberAfter(message, "maxPlayers=");
            friendly = $"The expedition '{room}' is now open for adventurers" +
                       (max > 0 ? $" (up to {max} players)." : ".");
            return true;
        }
        if (message.StartsWith("room metadata attached", StringComparison.Ordinal))
        {
            friendly = "Server details (name, game mode, password requirement) published to the server list.";
            return true;
        }
        if (message.StartsWith("connect requested via", StringComparison.Ordinal) ||
            message.StartsWith("relay link established", StringComparison.Ordinal))
        {
            friendly = "Connecting to the game network (relay)…";
            return true;
        }
        if (message.StartsWith("relay connect to", StringComparison.Ordinal))
        {
            friendly = "Could not reach the game network (relay). Is the relay program running? Retrying…";
            return true;
        }
        if (message.StartsWith("relay send failed", StringComparison.Ordinal) ||
            message.StartsWith("relay receive loop ended", StringComparison.Ordinal))
        {
            friendly = "Lost the connection to the relay for a moment — trying to recover.";
            return true;
        }
        if (message.StartsWith("CreateRoom failed", StringComparison.Ordinal))
        {
            friendly = "The game network refused to open the room — the server will try again " +
                       "automatically in a few seconds.";
            return true;
        }
        if (message.StartsWith("room '", StringComparison.Ordinal) &&
            message.Contains("' joined", StringComparison.Ordinal))
        {
            var room = ExtractQuoted(message);
            friendly = $"The server joined the expedition '{room}' and is ready for players.";
            return true;
        }
        if (message.StartsWith("disconnected from relay", StringComparison.Ordinal))
        {
            friendly = "The connection to the relay dropped — restarting the server session…";
            return true;
        }
        if (message.StartsWith("too many reconnect attempts", StringComparison.Ordinal))
        {
            friendly = "The server gave up after several failed connection attempts. " +
                       "Please close this window and start the server again.";
            return true;
        }
        if (message.StartsWith("connected before HostState was armed", StringComparison.Ordinal))
        {
            friendly = "Fixing a startup hiccup (the network answered faster than expected).";
            return true;
        }
        if (message.StartsWith("recovery: NetworkConnector not found", StringComparison.Ordinal))
        {
            friendly = "Startup could not finish. Please close this window and start the server again.";
            return true;
        }
        if (message.StartsWith("update tick failed", StringComparison.Ordinal))
        {
            friendly = "A routine server task hit a hiccup (details in the technical log).";
            return true;
        }
        return false;
    }

    private static string? ExtractQuoted(string message)
    {
        var open = message.IndexOf('\'');
        if (open < 0)
            return null;
        var close = message.IndexOf('\'', open + 1);
        return close > open ? message[(open + 1)..close] : null;
    }

    private static int ExtractNumberAfter(string message, string marker)
    {
        var at = message.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            return 0;
        var digits = new StringBuilder();
        for (var i = at + marker.Length; i < message.Length && char.IsDigit(message[i]); i++)
            digits.Append(message[i]);
        return int.TryParse(digits.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>Appends a line to the log pane, keeping the prompt at the bottom.</summary>
    private static void Append(string text, string color)
    {
        lock (Sync)
        {
            var width = TryGetWindowWidth();
            if (width.HasValue && width.Value != _consoleWidth)
            {
                _consoleWidth = width.Value;
                _paneValid = false; // wrapping may have changed
            }
            var wrapped = Wrap(text, _consoleWidth);
            Pane.AddRange(wrapped);
            while (Pane.Count > PaneCapacity)
                Pane.RemoveAt(0);
            TestSink?.Invoke(text, color);

            if (TryWriteWrapped(wrapped, color))
                return;
            // Degraded mode: no usable window — plain output without prompt juggling.
            try
            {
                var previous = Console.ForegroundColor;
                Console.ForegroundColor = ColorFor(color);
                Console.WriteLine(text);
                Console.ForegroundColor = previous;
            }
            catch
            {
                // last resort: absolutely never throw from a log call
            }
        }
    }

    /// <summary>Delta-writes lines above the persistent prompt; falls back to full repaint.</summary>
    private static bool TryWriteWrapped(List<string> wrapped, string color)
    {
        try
        {
            var promptRow = Console.CursorTop - Console.WindowTop;

            if (_paneValid && promptRow >= 0 && promptRow < Console.WindowHeight)
            {
                // Delta append: wipe the prompt, print the new lines, restore the prompt.
                ClearCurrentLine();
                foreach (var line in wrapped)
                    WriteLineColored(line, color);
                DrawPrompt();
                return true;
            }

            // Full repaint (after a resize or before the first draw).
            Console.Clear();
            var capacity = Math.Max(1, Console.WindowHeight - 1);
            foreach (var line in Pane.Skip(Math.Max(0, Pane.Count - capacity)))
                WriteLineColored(line, "gray");
            DrawPrompt();
            _paneValid = true;
            return true;
        }
        catch
        {
            _paneValid = false;
            return false;
        }
    }

    private static void DrawPrompt()
    {
        try { Console.Write("server> "); }
        catch { /* no real console — degraded mode */ }
    }

    private static void ClearCurrentLine()
    {
        try
        {
            if (Console.CursorTop - Console.WindowTop < 0 || Console.CursorTop - Console.WindowTop >= Console.WindowHeight)
                return;
            Console.CursorLeft = 0;
            Console.Write(new string(' ', Math.Max(0, Console.WindowWidth - 1)));
            Console.CursorLeft = 0;
        }
        catch
        {
            // resizing races — the next Append repaints everything
        }
    }

    private static void WriteLineColored(string text, string color)
    {
        try
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = ColorFor(color);
            Console.WriteLine(text);
            Console.ForegroundColor = previous;
        }
        catch
        {
            try { Console.WriteLine(text); } catch { /* console gone */ }
        }
    }

    /// <summary>Splits a line so it never wraps mid-word onto the prompt line.</summary>
    internal static List<string> Wrap(string text, int width)
    {
        var result = new List<string>();
        if (width < 20)
        {
            result.Add(text);
            return result;
        }
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            while (line.Length > width)
            {
                var cut = line.LastIndexOf(' ', width);
                if (cut <= 0)
                    cut = width;
                result.Add(line[..cut]);
                line = line[cut..].TrimStart();
            }
            result.Add(line);
        }
        return result;
    }

    private static int? TryGetWindowWidth()
    {
        try { return Console.WindowWidth; }
        catch { return null; }
    }

    private static void WriteRule()
    {
        Append(new string('─', Math.Max(20, _consoleWidth - 1)), "dark");
    }

    private static ConsoleColor ColorFor(string name) => name switch
    {
        "green" => ConsoleColor.Green,
        "yellow" => ConsoleColor.DarkYellow,
        "red" => ConsoleColor.Red,
        "white" => ConsoleColor.White,
        "dark" => ConsoleColor.DarkGray,
        _ => ConsoleColor.Cyan,
    };

    // ---------------------------------------------------------------- native console attach

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetConsoleWindow();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetConsoleTitleW(string title);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetConsoleOutputCP(uint codePage);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int stdHandle, IntPtr handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int stdHandle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
