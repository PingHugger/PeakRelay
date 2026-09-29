using ExitGames.Client.Photon;

namespace PeakRelay.Dedicated;

/// <summary>Runtime config handle for patches and the socket (set once at Awake).</summary>
public static class DedicatedState
{
    public static DedicatedConfig? Config { get; set; }
    public static string ConfiguredRoomName { get; set; } = "DBPEAK";

    /// <summary>
    /// Metadata stamped into the next CreateRoom's CustomRoomProperties by the
    /// HandleConnectionState prefix; consumed (and cleared) by RoomOptionsMetadataPatch.
    /// </summary>
    public static Hashtable? PendingRoomMetadata { get; set; }

    /// <summary>
    /// Set by the CreateRoom prefix when a CreateRoom goes out; lets the plugin's
    /// "missed OnConnectedToMaster" recovery know a create is already in flight.
    /// Reset on disconnect by the host cycle.
    /// </summary>
    public static bool CreateRoomSent { get; set; }
}
