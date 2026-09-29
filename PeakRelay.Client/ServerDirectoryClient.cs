using System;
using System.Collections.Generic;
using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace PeakRelay.Client;

/// <summary>One server row from the relay's /api/servers feed.</summary>
public sealed record ServerEntry(
    string JoinKey,
    string DisplayName,
    string Mode,
    bool PasswordRequired,
    int Players,
    int Max);

/// <summary>Fetches the relay's server directory over HTTP (no in-Photon lobby involved).</summary>
public static class ServerDirectoryClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>Why the last Fetch returned an empty list (null on success).</summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// GET /api/servers from the relay's HTTP DIRECTORY port. Empty list on failure, with
    /// LastError set — the page distinguishes "relay unreachable" from "no servers".
    /// </summary>
    public static List<ServerEntry> Fetch(string relayHost, int directoryPort)
    {
        var entries = new List<ServerEntry>();
        LastError = null;
        try
        {
            var json = Http.GetStringAsync($"http://{relayHost}:{directoryPort}/api/servers")
                .GetAwaiter().GetResult();
            entries = Parse(json);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        return entries;
    }

    /// <summary>Coroutine-friendly fetch (page yields until completed).</summary>
    public static System.Threading.Tasks.Task<List<ServerEntry>> FetchAsync(string relayHost, int directoryPort)
    {
        return System.Threading.Tasks.Task.Run(() => Fetch(relayHost, directoryPort));
    }

    private static List<ServerEntry> Parse(string json)
    {
        var entries = new List<ServerEntry>();
        var array = JArray.Parse(json);
        foreach (var token in array)
        {
            var obj = (JObject)token;
            var joinKey = (string?)obj["joinKey"] ?? "";
            var displayName = (string?)obj["displayName"];
            var mode = (string?)obj["mode"];
            entries.Add(new ServerEntry(
                joinKey,
                string.IsNullOrEmpty(displayName) ? (string.IsNullOrEmpty(joinKey) ? "?" : joinKey) : displayName,
                string.IsNullOrEmpty(mode) ? "standard" : mode,
                (bool?)obj["passwordRequired"] == true,
                (int?)obj["players"] ?? 0,
                (int?)obj["max"] ?? 0));
        }
        return entries;
    }
}
