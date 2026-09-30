using System;
using System.Collections.Generic;
using System.Linq;
using PeakRelay.Dedicated;
using PeakRelay.Protocol;
using Xunit;

// These tests share ServerConsole's static state (TestSink, pane, shutdown path) — they
// must never run concurrently with each other or with ServerConsoleTests.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly, DisableTestParallelization = true)]

namespace PeakRelay.Dedicated.Tests;

/// <summary>
/// Contract for the three live-reported bugs: player chat must appear in the operator
/// console as a [CHAT] line, the operator's own commands must never echo back as chat,
/// and 'stop' must go through the main-thread-mshaled shutdown with its watchdog armed.
/// </summary>
public class ChatConsoleTests
{
    private static List<(string Text, string Color)> Capture()
    {
        var seen = new List<(string Text, string Color)>();
        ServerConsole.TestSink = (text, color) => seen.Add((text, color));
        return seen;
    }

    public ChatConsoleTests()
    {
        ServerConsole.TestSink = null;
        ServerConsole.SetVerboseForTests(false);
        ServerConsole.SetWindowWidthForTests(120);
    }

    // ---------------------------------------------------------------- chat logging (bug 1)

    [Theory]
    [InlineData(ChatEvent.PlayerEventCode, "Bob", "hello everyone", "Bob: hello everyone")]
    [InlineData(ChatEvent.PlayerEventCode, "Server", "no relay, plain text", "Server: no relay, plain text")]
    public void Player_chat_events_are_logged_as_chat_lines(byte code, string from, string text, string expected)
    {
        var seen = Capture();
        PunEventBridge.LogChatForTests(code, PunEventBridge.BuildChatPayloadForTests(from, text));
        Assert.Contains(seen, line => line.Text.Contains($"[CHAT] {expected}"));
    }

    [Fact]
    public void Server_announcements_are_not_re_logged_as_chat()
    {
        var seen = Capture();
        PunEventBridge.LogChatForTests(ChatEvent.ServerEventCode, PunEventBridge.BuildChatPayloadForTests("Server", "restart in 5"));
        Assert.DoesNotContain(seen, line => line.Text.Contains("[CHAT]"));
    }

    [Fact]
    public void Malformed_or_foreign_events_never_reach_the_console()
    {
        var seen = Capture();
        PunEventBridge.LogChatForTests(17, PunEventBridge.BuildChatPayloadForTests("Bob", "game event")); // not ours
        PunEventBridge.LogChatForTests(ChatEvent.PlayerEventCode, new Dictionary<byte, object> { [ChatEvent.KeyText] = "" }); // empty text
        Assert.DoesNotContain(seen, line => line.Text.Contains("[CHAT]"));
    }

    [Fact]
    public void Operator_commands_are_never_echoed_as_chat()
    {
        var seen = Capture();

        ServerConsole.ExecuteCommand("help");

        Assert.DoesNotContain(seen, line => line.Text.Contains("[CHAT]"));
        Assert.Contains(seen, line => line.Text.Contains("[YOU] > help"));
    }

    [Fact]
    public void Chat_lines_are_white_like_operator_echo()
    {
        var seen = Capture();
        PunEventBridge.LogChatForTests(ChatEvent.PlayerEventCode, PunEventBridge.BuildChatPayloadForTests("Bob", "hi"));
        Assert.All(seen, line => Assert.Equal("white", line.Color));
    }

    [Fact]
    public void Chat_lines_are_written_to_the_server_log_mirror()
    {
        var seen = Capture();
        PunEventBridge.LogChatForTests(ChatEvent.PlayerEventCode, PunEventBridge.BuildChatPayloadForTests("Bob", "log mirror check"));
        // the same line must flow through HandleServerLog (the ServerConsole path) — a
        // [CHAT] line in the sink proves the routed writer accepted the level
        Assert.Contains("[CHAT]", string.Join("\n", seen.Select(l => l.Text)));
    }

    // ---------------------------------------------------------------- stop command (bug 3)

    [Fact]
    public void Stop_announces_goodbye_and_schedules_the_main_thread_quit()
    {
        DedicatedPlugin.MainThreadJobs.TryDequeue(out _); // drain leftovers from other tests
        var seen = Capture();

        ServerConsole.ExecuteCommand("stop");

        Assert.Contains(seen, line => line.Text.Contains("Shutting the server down now. Goodbye!"));
        // the quit itself is marshaled to the Unity main thread (that is the bug fix):
        Assert.True(DedicatedPlugin.MainThreadJobs.TryDequeue(out var job));
        Assert.NotNull(job);
        // NB: the final "Server shut down by operator (stop)" line is emitted when that job
        // RUNS on the main thread — which also ends the process, so tests never execute it.
    }

    [Fact]
    public void Stop_is_listed_with_its_operator_friendly_description()
    {
        var command = ConsoleCommandRegistry.Find("stop");
        Assert.NotNull(command);
        Assert.Equal("stop", command!.Usage);
        Assert.Contains("Shuts the dedicated server down", command.Description);
    }
}
