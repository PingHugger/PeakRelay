using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace PeakRelay.Dedicated;

/// <summary>
/// Dedicated-server configuration. Read from <c>server.json</c> next to the plugin DLL;
/// every value can be overridden by a <c>PEAKRELAY_*</c> environment variable, which is
/// how container orchestrators inject settings without touching files.
/// Uses the Newtonsoft.Json copy the game itself ships (no extra dependency).
/// </summary>
public sealed class DedicatedConfig
{
    public string RoomName { get; init; } = "DBPEAK";
    public string DisplayName { get; init; } = "PeakRelay Dedicated";
    public string Mode { get; init; } = "standard";
    public string Password { get; init; } = "";
    public int MaxPlayers { get; init; } = 20;
    public bool Visible { get; init; } = true;
    public bool Open { get; init; } = true;
    public string RelayHost { get; init; } = "127.0.0.1";
    public int RelayPort { get; init; } = 5055;
    public bool AutoHost { get; init; } = true;
    public bool UseVanillaName { get; init; } = false;
    public bool LogDatagrams { get; init; } = false;

    public static DedicatedConfig Load(string pluginDir)
    {
        var config = new DedicatedConfig();
        var path = Path.Combine(pluginDir, "server.json");
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                config = ApplyJson(config, JObject.Parse(json));
            }
        }
        catch (Exception ex)
        {
            ServerLog.Warn($"server.json could not be parsed ({ex.Message}); using defaults");
        }

        config = ApplyEnv(config);
        return config;
    }

    private static DedicatedConfig ApplyJson(DedicatedConfig config, JObject root)
    {
        string? S(string key) => root[key]?.Type == JTokenType.String ? (string?)root[key] : null;
        int? I(string key) => root[key]?.Type == JTokenType.Integer ? (int?)root[key] : null;
        bool? B(string key) => root[key]?.Type == JTokenType.Boolean ? (bool?)root[key] : null;

        return new DedicatedConfig
        {
            RoomName = S("roomName") ?? config.RoomName,
            DisplayName = S("displayName") ?? config.DisplayName,
            Mode = S("mode") ?? config.Mode,
            Password = S("password") ?? config.Password,
            MaxPlayers = I("maxPlayers") ?? config.MaxPlayers,
            Visible = B("visible") ?? config.Visible,
            Open = B("open") ?? config.Open,
            RelayHost = S("relayHost") ?? config.RelayHost,
            RelayPort = I("relayPort") ?? config.RelayPort,
            AutoHost = B("autoHost") ?? config.AutoHost,
            UseVanillaName = B("useVanillaName") ?? config.UseVanillaName,
            LogDatagrams = B("logDatagrams") ?? config.LogDatagrams,
        };
    }

    private static DedicatedConfig ApplyEnv(DedicatedConfig config)
    {
        string? Env(string name) => Environment.GetEnvironmentVariable(name);
        int? EnvInt(string name) => int.TryParse(Env(name), out var i) ? i : null;
        bool? EnvBool(string name) => bool.TryParse(Env(name), out var b) ? b : null;

        return new DedicatedConfig
        {
            RoomName = Env("PEAKRELAY_ROOM") ?? config.RoomName,
            DisplayName = Env("PEAKRELAY_DISPLAYNAME") ?? config.DisplayName,
            Mode = Env("PEAKRELAY_MODE") ?? config.Mode,
            Password = Env("PEAKRELAY_PASSWORD") ?? config.Password,
            MaxPlayers = EnvInt("PEAKRELAY_MAXPLAYERS") ?? config.MaxPlayers,
            Visible = EnvBool("PEAKRELAY_VISIBLE") ?? config.Visible,
            Open = EnvBool("PEAKRELAY_OPEN") ?? config.Open,
            RelayHost = Env("PEAKRELAY_HOST") ?? config.RelayHost,
            RelayPort = EnvInt("PEAKRELAY_PORT") ?? config.RelayPort,
            AutoHost = EnvBool("PEAKRELAY_AUTOHOST") ?? config.AutoHost,
            UseVanillaName = EnvBool("PEAKRELAY_USEVANILLANAME") ?? config.UseVanillaName,
            LogDatagrams = EnvBool("PEAKRELAY_LOGDATAGRAMS") ?? config.LogDatagrams,
        };
    }
}

/// <summary>File logging that survives headless runs without a Unity console.</summary>
public static class ServerLog
{
    private static readonly object Sync = new();
    private static string? _logPath;

    public static void Initialize(string pluginDir)
    {
        _logPath = Path.Combine(pluginDir, "server.log");
        Info("=== PeakRelay dedicated server starting ===");
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Console.WriteLine(line);
        try
        {
            if (_logPath == null)
                return;
            lock (Sync)
                File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch
        {
            // logging must never take the server down
        }
    }
}
