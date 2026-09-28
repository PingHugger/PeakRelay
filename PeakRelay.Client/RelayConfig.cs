using BepInEx.Configuration;

namespace PeakRelay.Client;

/// <summary>BepInEx-bound configuration for the player plugin.</summary>
public static class RelayConfig
{
    /// <summary>
    /// Relay on (default): PUN speaks the relay protocol through RelayClientSocket.
    /// Off: vanilla Photon Cloud UDP — the game stays fully playable without the relay.
    /// </summary>
    public static bool RelayEnabled { get; private set; } = true;

    /// <summary>Relay address clients connect to (master and game roles are the same endpoint).</summary>
    public static string Host { get; private set; } = "127.0.0.1";

    /// <summary>Relay TCP port.</summary>
    public static int Port { get; private set; } = 5055;

    /// <summary>
    /// True once we've rewritten PhotonServerSettings for this session: AppId and
    /// Server/Port now describe the relay, so every PUN connect path (menu auto-connect,
    /// region swap, game re-auth) lands on the relay.
    /// </summary>
    public static bool Redirected { get; internal set; }

    /// <summary>Datagram dump for support/debugging.</summary>
    public static bool TraceEnabled { get; private set; }
    public static int TracePort { get; private set; } = 5060;

    public static void Bind(ConfigFile file)
    {
        RelayEnabled = file.Bind("Relay", "Enabled", true,
            "Route PUN through the PeakRelay relay (TCP envelopes). false = vanilla Photon Cloud UDP.")
            .Value;

        Host = file.Bind("Relay", "Host", "127.0.0.1",
            "Relay server address.")
            .Value;

        Port = file.Bind("Relay", "Port", 5055,
            "Relay server TCP port.")
            .Value;

        TraceEnabled = file.Bind("Trace", "Enabled", false,
            "Log datagram summaries for support.")
            .Value;

        TracePort = file.Bind("Trace", "Port", 5060,
            "TCP port of the trace collector (PeakRelay trace-collector).")
            .Value;
    }
}
