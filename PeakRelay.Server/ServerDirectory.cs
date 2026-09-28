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
            var props = room.Properties;

            var displayName = room.Name;
            if (props[ServerPropertyKeys.DisplayName] is string n)
                displayName = n;

            var mode = "standard";
            if (props[ServerPropertyKeys.Mode] is string m)
                mode = m;

            var passwordRequired = props[ServerPropertyKeys.PasswordRequired] is true;

            servers.Add(new ServerInfo(room.Name, displayName, mode, passwordRequired, room.ActorCount, room.MaxPlayers));
        }
        return servers;
    }
}
