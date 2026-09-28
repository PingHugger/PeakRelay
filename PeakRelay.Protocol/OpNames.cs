using System.Collections.Generic;

namespace PeakRelay.Protocol;

/// <summary>Names for Photon LoadBalancing operation codes ( Photon.Realtime.OperationCode, shipped build).</summary>
public static class OpNames
{
    public static string Name(byte op) => op switch
    {
        230 => "Authenticate",
        231 => "AuthenticateOnce",
        229 => "JoinLobby",
        228 => "LeaveLobby",
        227 => "CreateGame",
        226 => "JoinGame",
        225 => "JoinRandomGame",
        254 => "Leave",
        253 => "RaiseEvent",
        252 => "SetProperties",
        251 => "GetProperties",
        248 => "ChangeGroups",
        222 => "FindFriends",
        221 => "GetLobbyStats",
        220 => "GetRegions",
        219 => "WebRpc",
        218 => "ServerSettings",
        217 => "GetGameList",
        _ => $"Op{op}",
    };
}

/// <summary>Common PUN2 event codes (Photon.Pun.EventCode, shipped build).</summary>
public static class EventNames
{
    public static string Name(byte ev) => ev switch
    {
        230 => "AuthEvent/Ok",
        226 => "Join",
        254 => "Leave",
        253 => "PropertiesChanged",
        251 => "PropertiesRemoved",
        252 => "EventSend/Debug",
        224 => "AppStats",
        223 => "GameList",
        218 => "GameListUpdate",
        227 => "CacheSliceEmpty",
        228 => "GameProperties",
        229 => "LobbyStats",
        _ => $"Event{ev}",
    };
}
