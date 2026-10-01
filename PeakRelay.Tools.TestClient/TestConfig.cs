namespace PeakRelay.Tools.TestClient;

/// <summary>
/// Where the harness dials its relay. Defaults from the environment (CI/manual runs against
/// a started relay); tests can pin host/ports directly via <see cref="Configure"/> when the
/// relay runs in-process on ephemeral ports.
/// </summary>
public static class TestConfig
{
    private static string _host = Environment.GetEnvironmentVariable("PEAKRELAY_HOST") ?? "127.0.0.1";
    private static int _port = int.TryParse(Environment.GetEnvironmentVariable("PEAKRELAY_PORT"), out var p) ? p : 5055;
    private static int _httpPort = int.TryParse(Environment.GetEnvironmentVariable("PEAKRELAY_HTTPPORT"), out var h) ? h : 0;

    public static string Host => _host;

    public static int Port => _port;

    public static int HttpPort => _httpPort;

    /// <summary>Test seam: pins the relay endpoint (in-proc relay on ephemeral ports).</summary>
    public static void Configure(string host, int port, int httpPort)
    {
        _host = host;
        _port = port;
        _httpPort = httpPort;
    }
}
