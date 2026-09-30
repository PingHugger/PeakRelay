using System;
using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
using HarmonyLib;
using PeakRelay.Protocol;

namespace PeakRelay.Dedicated;

/// <summary>One console command the operator can type.</summary>
internal interface IConsoleCommand
{
    /// <summary>The word the operator types (lowercase).</summary>
    string Name { get; }

    /// <summary>One-line description shown by 'help'.</summary>
    string Description { get; }

    /// <summary>Usage line shown by 'help &lt;command&gt;' and on errors, e.g. "kick &lt;player&gt;".</summary>
    string Usage { get; }

    /// <summary>Executes the command; report to the operator via ServerConsole.</summary>
    void Run(string[] args);
}

/// <summary>A friendly snapshot row describing one player in the room.</summary>
internal sealed record PlayerLine(string Name, string Id, int Slot);

/// <summary>
/// The full command set. Every command speaks operator language: it prints what happened in
/// plain sentences and never shows stack traces or exception internals.
/// </summary>
internal static class ConsoleCommandRegistry
{
    private static readonly IConsoleCommand[] AllCommands =
    {
        new HelpCommand(),
        new StatusCommand(),
        new PlayersCommand(),
        new SayCommand(),
        new KickCommand(),
        new RestartHostCommand(),
        new StopCommand(),
        new VerboseCommand(),
        new ClearCommand(),
    };

    internal static IReadOnlyList<IConsoleCommand> All => AllCommands;

    internal static IConsoleCommand? Find(string name) =>
        AllCommands.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

// ---------------------------------------------------------------- info commands

internal sealed class HelpCommand : IConsoleCommand
{
    public string Name => "help";
    public string Description => "Shows every command this server understands.";
    public string Usage => "help [command]";

    public void Run(string[] args)
    {
        if (args.Length > 0)
        {
            var command = ConsoleCommandRegistry.Find(args[0]);
            if (command == null)
            {
                ServerConsole.Error($"I don't know the command '{args[0]}'. Type 'help' to see all commands.");
                return;
            }
            ServerConsole.Message($"{command.Usage} — {command.Description}");
            return;
        }

        ServerConsole.Message("Things you can type here (then press Enter):");
        foreach (var command in ConsoleCommandRegistry.All)
            ServerConsole.Message($"  {command.Usage.PadRight(18)} {command.Description}");
    }
}

internal sealed class StatusCommand : IConsoleCommand
{
    public string Name => "status";
    public string Description => "Shows whether the server is running and how busy it is.";
    public string Usage => "status";

    public void Run(string[] args)
    {
        var room = SafeRoomName();
        if (ServerConsole.RelayUp && !string.IsNullOrEmpty(room))
        {
            var players = ServerConsole.PlayersSnapshot();
            var slots = SafeMaxPlayers();
            ServerConsole.Success($"The server is up and running.");
            ServerConsole.Message($"  Expedition: {room}");
            ServerConsole.Message($"  Players:    {players.Count} of {slots} slots in use");
            ServerConsole.Message($"  Server name:{(string.IsNullOrEmpty(SafeDisplayName()) ? "" : " " + SafeDisplayName())}");
        }
        else
        {
            ServerConsole.Warn("The server is not ready yet — it is still connecting to the game network (relay). " +
                               "This usually fixes itself within a minute.");
        }
    }

    internal static string SafeRoomName()
    {
        try { return Photon.Pun.PhotonNetwork.CurrentRoom?.Name ?? ""; }
        catch { return ""; }
    }

    internal static int SafeMaxPlayers()
    {
        try { return Photon.Pun.PhotonNetwork.CurrentRoom?.MaxPlayers ?? 0; }
        catch { return 0; }
    }

    internal static string SafeDisplayName() => DedicatedState.Config?.DisplayName ?? "";
}

internal sealed class PlayersCommand : IConsoleCommand
{
    public string Name => "players";
    public string Description => "Lists everyone currently on the server.";
    public string Usage => "players";

    public void Run(string[] args)
    {
        var players = ServerConsole.PlayersSnapshot();
        if (players.Count == 0)
        {
            ServerConsole.Message("No players are connected right now. The server is waiting for adventurers.");
            return;
        }
        ServerConsole.Message($"{players.Count} player{(players.Count == 1 ? "" : "s")} on the server:");
        foreach (var player in players)
            ServerConsole.Message($"  • {player.Name}" +
                                  (string.IsNullOrEmpty(player.Id) ? "" : $" ({player.Id})"));
    }
}

// ---------------------------------------------------------------- moderation commands

internal sealed class KickCommand : IConsoleCommand
{
    public string Name => "kick";
    public string Description => "Removes a player from the server (by name).";
    public string Usage => "kick <player name or number>";

    public void Run(string[] args)
    {
        if (args.Length == 0)
        {
            ServerConsole.Error("Please tell me who to remove. Example: kick Bob");
            return;
        }

        var players = ServerConsole.PlayersSnapshot();
        var needle = string.Join(' ', args).Trim();
        var matches = MatchPlayers(players, needle);
        if (matches.Count == 0)
        {
            ServerConsole.Error($"No player named '{needle}' is on the server right now. " +
                                "Type 'players' to see who is here.");
            return;
        }
        if (matches.Count > 1)
        {
            ServerConsole.Error($"More than one player matches '{needle}':");
            foreach (var player in matches)
                ServerConsole.Message($"  • {player.Name} ({player.Slot})");
            ServerConsole.Message("Try again with the number in brackets.");
            return;
        }

        var target = matches[0];
        if (KickPlayer(target))
            ServerConsole.Success($"{target.Name} was removed from the server.");
        else
            ServerConsole.Error($"Could not remove {target.Name} right now. " +
                                "Wait until the round has fully started and try again.");
    }

    internal static List<PlayerLine> MatchPlayers(List<PlayerLine> players, string needle)
    {
        if (int.TryParse(needle, out var slot))
        {
            var byNumber = players.Where(p => p.Slot == slot).ToList();
            if (byNumber.Count > 0)
                return byNumber;
        }
        return players
            .Where(p => p.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Uses the game's own removal path (the same one the in-game kick button uses), so
    /// gameplay state stays consistent. Needs a started round; before that PlayerHandler
    /// has no instances and the command reports that honestly.
    /// </summary>
    private static bool KickPlayer(PlayerLine target)
    {
        try
        {
            var handlerType = AccessTools.TypeByName("PlayerHandler");
            var kick = AccessTools.Method(handlerType, "Kick", new[] { typeof(int) });
            if (kick == null)
                return false;
            kick.Invoke(null, new object[] { target.Slot });
            return true;
        }
        catch
        {
            return false;
        }
    }
}/// <summary>
/// Broadcasts a server announcement to every player in the room as a custom PUN event
/// (code 199, payload spec in PeakRelay.Protocol.ChatEvent). The game ignores unknown
/// event codes; the PeakRelay client shim listens for 199 and shows a banner. On a vanilla
/// client (no shim) the event arrives and is simply not displayed — harmless.
/// </summary>
internal sealed class SayCommand : IConsoleCommand
{
    public string Name => "say";
    public string Description => "Broadcasts a server announcement to all connected players.";
    public string Usage => "say <message>";

    public void Run(string[] args)
    {
        var text = string.Join(' ', args).Trim();
        if (text.Length == 0)
        {
            ServerConsole.Error("What should I announce? Example: say Restarting in 5 minutes");
            return;
        }
        if (text.Length > ChatEvent.MaxLength)
        {
            ServerConsole.Error($"Messages are limited to {ChatEvent.MaxLength} characters (yours has {text.Length}).");
            return;
        }
        if (!ServerConsole.RelayUp)
        {
            ServerConsole.Warn("The server is not connected yet — nobody would hear this. Try again once it is up.");
            return;
        }

        try
        {
            var peers = Photon.Pun.PhotonNetwork.NetworkingClient?.LoadBalancingPeer;
            if (peers == null)
                throw new InvalidOperationException("network peer not ready");

            var options = new Photon.Realtime.RaiseEventOptions { Receivers = Photon.Realtime.ReceiverGroup.All };
            var result = peers.OpRaiseEvent(ChatEvent.ServerEventCode,
                ChatEvent.CreatePayload("Server", text), options, SendOptions.SendReliable);
            if (!result)
                throw new InvalidOperationException("the network rejected the message");

            ServerConsole.Success($"Announcement sent to all players: {text}");
        }
        catch (Exception ex)
        {
            // one friendly line, no jargon — the cause is logged to server.log via Verbose
            ServerConsole.Verbose($"say failed: {ex.Message}");
            ServerConsole.Warn("The announcement could not be sent right now. " +
                               "Is an expedition running? (check 'status')");
        }
    }
}

// ---------------------------------------------------------------- server control commands

internal sealed class RestartHostCommand : IConsoleCommand
{
    public string Name => "resethost";
    public string Description => "Restarts the expedition hosting (players rejoin automatically).";
    public string Usage => "resethost";

    public void Run(string[] args)
    {
        var plugin = DedicatedPlugin.Instance;
        if (plugin == null)
        {
            ServerConsole.Error("The server is not fully started yet — hosting cannot be restarted right now.");
            return;
        }
        ServerConsole.Message("Restarting the expedition hosting…");
        plugin.PluginStartHosting(DedicatedState.ConfiguredRoomName);
        ServerConsole.Success("Hosting restarted.");
    }
}

internal sealed class StopCommand : IConsoleCommand
{
    public string Name => "stop";
    public string Description => "Shuts the dedicated server down (players get disconnected).";
    public string Usage => "stop";

    public void Run(string[] args)
    {
        ServerConsole.Warn("Shutting the server down now. Goodbye!");
        ServerConsole.Shutdown();
        try { UnityEngine.Application.Quit(); } catch { /* headless quit */ }
        Environment.Exit(0);
    }
}

internal sealed class VerboseCommand : IConsoleCommand
{
    public string Name => "verbose";
    public string Description => "Shows extra technical detail (for support requests).";
    public string Usage => "verbose <on|off>";

    public void Run(string[] args)
    {
        if (args.Length == 0)
        {
            ServerConsole.Message($"Technical detail is currently {(DedicatedState.Config?.ConsoleVerbose == true ? "ON" : "OFF")}. " +
                                  "Use 'verbose on' or 'verbose off'.");
            return;
        }
        var config = DedicatedState.Config;
        if (config == null)
        {
            ServerConsole.Error("Server settings are not loaded yet.");
            return;
        }
        switch (args[0].ToLowerInvariant())
        {
            case "on":
            case "true":
            case "1":
                config.ConsoleVerbose = true;
                ServerConsole.Success("Technical detail is now ON — support staff will appreciate it.");
                break;
            case "off":
            case "false":
            case "0":
                config.ConsoleVerbose = false;
                ServerConsole.Success("Technical detail is now OFF — back to the friendly view.");
                break;
            default:
                ServerConsole.Error("Please use 'verbose on' or 'verbose off'.");
                break;
        }
    }
}

internal sealed class ClearCommand : IConsoleCommand
{
    public string Name => "clear";
    public string Description => "Clears the console window (the log file keeps everything).";
    public string Usage => "clear";

    public void Run(string[] args)
    {
        ServerConsole.ClearPane();
        ServerConsole.Message("Cleared. The full history is still in the server.log file.");
    }
}
