namespace PeakRelay.Protocol;

/// <summary>
/// Room custom-property keys carrying a server's public profile. The dedicated plugin
/// stamps them into RoomOptions.CustomRoomProperties at create time; the relay publishes
/// them on /api/servers for the client server browser. PasswordRequired is a flag — the
/// secret itself never leaves the server.
///
/// Keys are STRINGS on purpose: Realtime's RoomInfo.InternalCacheProperties merges room
/// properties into CustomProperties via MergeStringKeys, which drops every non-string key
/// (byte-keyed props never reach the client's room view). PEAK's own custom properties
/// ("csid", "round") use string keys too.
/// </summary>
public static class ServerPropertyKeys
{
    public const string DisplayName = "N";
    public const string Mode = "M";
    public const string PasswordRequired = "P";
}
