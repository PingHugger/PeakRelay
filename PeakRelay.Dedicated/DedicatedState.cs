namespace PeakRelay.Dedicated;

/// <summary>Runtime config handle for patches and the socket (set once at Awake).</summary>
public static class DedicatedState
{
    public static DedicatedConfig? Config { get; set; }
    public static string ConfiguredRoomName { get; set; } = "DBPEAK";
}
