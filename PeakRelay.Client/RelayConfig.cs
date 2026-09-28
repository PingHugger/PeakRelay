using BepInEx.Configuration;

namespace PeakRelay.Client;

/// <summary>BepInEx-bound configuration for the shim and trace collector.</summary>
public static class RelayConfig
{
    public static bool RelayEnabled { get; private set; }
    public static string Host { get; private set; } = "127.0.0.1";
    public static int Port { get; private set; } = 5055;
    public static bool TraceEnabled { get; private set; }
    public static int TracePort { get; private set; } = 5060;

    public static void Bind(ConfigFile file)
    {
        RelayEnabled = file.Bind("Relay", "Enabled", false,
            "M0: observation only. The real Photon path stays active; this flag currently gates trace collection only.")
            .Value;

        Host = file.Bind("Relay", "Host", "127.0.0.1",
            "Relay server address (used from M1 on).")
            .Value;

        Port = file.Bind("Relay", "Port", 5055,
            "Relay server TCP port (used from M1 on).")
            .Value;

        TraceEnabled = file.Bind("Trace", "Enabled", false,
            "Send datagram summaries to a local JSONL collector on TracePort.")
            .Value;

        TracePort = file.Bind("Trace", "Port", 5060,
            "TCP port of the trace collector (PeakRelay trace-collector).")
            .Value;
    }
}
