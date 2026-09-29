using System.Collections.Generic;
using PeakRelay.Protocol;

namespace PeakRelay.Server;

/// <summary>
/// Public profile of one hosted server, projected from the room's custom properties
/// (keys N/M/P, stamped by the dedicated host at create time) for the server browser.
/// </summary>
public sealed record ServerInfo(
    string JoinKey,
    string DisplayName,
    string Mode,
    bool PasswordRequired,
    int Players,
    int Max);

public static class ServerDirectory
{
    public static List<ServerInfo> SnapshotServers(LbDispatcher dispatcher)
    {
        var servers = new List<ServerInfo>();
        foreach (var room in dispatcher.SnapshotRoomStates())
        {
            // Photon Voice joins a sibling room named "<room>_voice_" — not a browsable server
            if (room.Name.EndsWith("_voice_", StringComparison.Ordinal))
                continue;

            var props = room.Properties;

            var displayName = room.Name;
            if (props.TryGetValue(ServerPropertyKeys.DisplayName, out var n) && n is string name)
                displayName = name;

            var mode = "standard";
            if (props.TryGetValue(ServerPropertyKeys.Mode, out var m) && m is string modeValue)
                mode = modeValue;

            var passwordRequired = props.TryGetValue(ServerPropertyKeys.PasswordRequired, out var p) && p is true;

            servers.Add(new ServerInfo(room.Name, displayName, mode, passwordRequired, room.ActorCount, room.MaxPlayers));
        }
        return servers;
    }
}
