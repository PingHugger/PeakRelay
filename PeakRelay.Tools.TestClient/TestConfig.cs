namespace PeakRelay.Tools.TestClient;

/// <summary>Test-client configuration via environment variables.</summary>
public static class TestConfig
{
    public static string Host { get; } = Environment.GetEnvironmentVariable("PEAKRELAY_HOST") ?? "127.0.0.1";
    public static int Port { get; } = int.TryParse(Environment.GetEnvironmentVariable("PEAKRELAY_PORT"), out var p) ? p : 5055;
    /// <summary>HTTP sidecar port for /api/servers checks; 0 disables the check.</summary>
    public static int HttpPort { get; } = int.TryParse(Environment.GetEnvironmentVariable("PEAKRELAY_HTTPPORT"), out var h) ? h : 0;
}
