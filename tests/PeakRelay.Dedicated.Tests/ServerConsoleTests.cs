using System.Linq;
using PeakRelay.Dedicated;
using Xunit;

namespace PeakRelay.Dedicated.Tests;

/// <summary>
/// The friendly-phrasing contract: ServerLog lines the dedicated plugin emits must reach the
/// console as plain sentences a non-developer understands — never jargon like "HostState",
/// "relay link" or raw ids in the default (non-verbose) view. Technical lines must stay
/// hidden unless verbose is on.
/// </summary>
public class FriendlyPhrasingTests
{
    private static List<(string Text, string Color)> Capture()
    {
        var seen = new List<(string Text, string Color)>();
        ServerConsole.TestSink = (text, color) => seen.Add((text, color));
        return seen;
    }

    public FriendlyPhrasingTests()
    {
        ServerConsole.TestSink = null;
        ServerConsole.SetVerboseForTests(false);
        ServerConsole.SetWindowWidthForTests(120);
    }

    [Theory]
    [InlineData("config: room=DBPEAK display='PeakRelay Dedicated' mode=standard password=no maxPlayers=20 visible=true open=true relay=127.0.0.1:5055 autoHost=true useVanillaName=false noSteam=True console=True",
                "Server settings loaded")]
    [InlineData("GameHandler ready", "Game engine is ready.")]
    [InlineData("Title scene active", "Main menu reached.")]
    [InlineData("platform bootstrap completed without Steam (onComplete fired)",
                "running as a standalone server")]
    [InlineData("HostState armed (room 'DBPEAK'), loading WilIsland", "Opening the expedition 'DBPEAK'")]
    [InlineData("room created: 'DBPEAK' maxPlayers=20 open=True visible=True", "open for adventurers")]
    [InlineData("room metadata attached to 'DBPEAK': display='PeakRelay Dedicated' mode, passwordRequired",
                "published to the server list")]
    [InlineData("connect requested via NetworkingUtilities.ConnectToNetwork (relay-redirected)",
                "Connecting to the game network (relay)")]
    [InlineData("relay link established to 127.0.0.1:5055", "Connecting to the game network (relay)")]
    [InlineData("relay connect to 127.0.0.1:5055 failed: connection refused",
                "Could not reach the game network")]
    [InlineData("CreateRoom failed: code=32762 message='room exists' — host cycle will retry",
                "try again")]
    [InlineData("room 'DBPEAK' joined — dedicated server is UP (actorNr=1, maxPlayers=20)",
                "ready for players")]
    [InlineData("disconnected from relay — restarting host cycle", "connection to the relay dropped")]
    [InlineData("too many reconnect attempts, giving up (kill the process to restart)",
                "start the server again")]
    [InlineData("not headless (-batchmode -nographics missing): dedicated mode off",
                "not as a dedicated server")]
    [InlineData("recovery: NetworkConnector not found in scene", "start the server again")]
    [InlineData("update tick failed: something broke", "details in the technical log")]
    public void Known_technical_lines_are_translated_into_plain_sentences(string rawLine, string expectedContains)
    {
        var seen = Capture();
        ServerConsole.HandleServerLog(rawLine);
        Assert.Contains(seen, line => line.Text.Contains(expectedContains, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("room created: 'DBPEAK' maxPlayers=20 open=True visible=True")]
    [InlineData("room metadata attached to 'DBPEAK': display='X' mode, passwordRequired")]
    [InlineData("relay link established to 127.0.0.1:5055")]
    public void Translated_lines_never_leak_developer_words(string rawLine)
    {
        var seen = Capture();
        ServerConsole.HandleServerLog(rawLine);
        var joined = string.Join("\n", seen.Select(l => l.Text));
        foreach (var jargon in new[] { "HostState", "relay link", "maxPlayers=", "actorNr", "127.0.0.1:5055",
                                        "CreateRoom(", "metadata attached" })
        {
            Assert.DoesNotContain(jargon, joined);
        }
    }

    [Fact]
    public void Technical_datagram_lines_are_hidden_until_verbose()
    {
        var seen = Capture();

        ServerConsole.HandleServerLog("C->S 512B op-stream");
        Assert.DoesNotContain(seen, line => line.Text.Contains("C->S"));

        ServerConsole.SetVerboseForTests(true);
        ServerConsole.HandleServerLog("C->S 512B op-stream");
        Assert.Contains(seen, line => line.Text.Contains("C->S"));
    }

    [Fact]
    public void Operator_input_is_echoed_and_commands_are_dispatched()
    {
        var seen = Capture();

        ServerConsole.ExecuteCommand("status");

        Assert.Contains(seen, line => line.Text.Contains("[YOU] > status"));
    }

    [Fact]
    public void Unknown_commands_get_a_friendly_hint()
    {
        var seen = Capture();

        ServerConsole.ExecuteCommand("warp 10");

        Assert.Contains(seen, line => line.Text.Contains("Unknown command 'warp'"));
        Assert.Contains(seen, line => line.Text.Contains("Type 'help'"));
    }

    [Fact]
    public void Help_lists_every_registered_command_in_plain_language()
    {
        var seen = Capture();

        ServerConsole.ExecuteCommand("help");

        Assert.Contains(seen, line => line.Text.Contains("Things you can type here"));
        foreach (var expected in new[] { "help", "status", "players", "kick", "resethost", "stop", "verbose", "clear" })
            Assert.Contains(seen, line => line.Text.Contains(expected));
    }

    [Fact]
    public void Help_for_a_single_command_explains_usage()
    {
        var seen = Capture();

        ServerConsole.ExecuteCommand("help kick");

        Assert.Contains(seen, line => line.Text.Contains("kick <player name or number>"));
    }

    [Fact]
    public void Kick_without_a_name_asks_politely()
    {
        var seen = Capture();

        ServerConsole.ExecuteCommand("kick");

        Assert.Contains(seen, line => line.Text.Contains("Please tell me who to remove"));
    }

    [Fact]
    public void Long_lines_are_word_wrapped_not_hard_cut()
    {
        var words = string.Join(' ', Enumerable.Repeat("word", 60));
        var wrapped = ServerConsole.Wrap(words, 40);

        Assert.True(wrapped.Count > 1);
        Assert.All(wrapped, line => Assert.True(line.Length <= 40));
        Assert.Equal(words, string.Join(' ', wrapped));
    }

    [Fact]
    public void Wrapping_never_splits_words_across_lines()
    {
        var line = "The expedition AlpineAscent is now open for adventurers (up to 20 players).";
        var wrapped = ServerConsole.Wrap(line, 30);

        Assert.All(wrapped, l => Assert.All(l.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            word => Assert.Contains(word, line)));
    }

    [Fact]
    public void Relay_status_changes_are_printed_exactly_once()
    {
        var seen = Capture();

        ServerConsole.RelayStatus(connected: true, players: 0, maxPlayers: 20);
        ServerConsole.RelayStatus(connected: true, players: 0, maxPlayers: 20); // same → silent
        Assert.Contains(seen, line => line.Text.Contains("Server is up and running"));

        seen.Clear();
        ServerConsole.RelayStatus(connected: true, players: 1, maxPlayers: 20);
        Assert.Contains(seen, line => line.Text.Contains("1 of 20 player slots in use"));
    }

    [Fact]
    public void Every_registered_command_has_a_usable_help_entry()
    {
        foreach (var command in ServerConsole.Commands)
        {
            Assert.False(string.IsNullOrWhiteSpace(command.Name));
            Assert.False(string.IsNullOrWhiteSpace(command.Description));
            Assert.False(string.IsNullOrWhiteSpace(command.Usage));
        }
    }
}
