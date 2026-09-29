using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using PeakRelay.Installer.Core;

namespace PeakRelay.Launcher.Core;

public enum CheckStatus { Pass, Warn, Fail }

/// <summary>One doctor row: what was checked, how it went, what to do about it.</summary>
public sealed record Check(CheckStatus Status, string Name, string Detail, string? Fix = null);

/// <summary>
/// Read-only status report of one game install — the launcher's "doctor". Diagnoses only;
/// the UI decides what to offer (Install / Update / Play) from the aggregate.
/// </summary>
public static class Doctor
{
    /// <summary>Doorstop 4.5.0 proxy SHA-256, lowercase hex (x64/winhttp.dll from the
    /// NeighTools/UnityDoorstop 4.5.0 release — byte-identical across the live game install
    /// and the staged payload). The stock BepInEx 5.4.23.2 loader (4.3.0) native-crashes the
    /// 2026-09 PEAK update before any BepInEx log, so the exact loader bytes are the
    /// difference between "works" and "dead game". release.yml fails if upstream ever
    /// changes these bytes without this pin being updated.</summary>
    public const string KnownLoaderSha256 =
        "8c6cdbc38836dee87e3368f5de1994d7c0ccebf29e4ce7aba3c0981f9375412c";

    public static List<Check> Inspect(string gameDir, LauncherState state)
    {
        var checks = new List<Check>();

        if (!GameLocator.LooksLikeGameRoot(gameDir))
        {
            checks.Add(new Check(CheckStatus.Fail, "Game",
                $"'{gameDir}' is not a PEAK install (no PEAK.exe).",
                "Pick the folder that contains PEAK.exe (Steam library default is fine)."));
            return checks; // nothing else can be checked without a game root
        }

        checks.Add(new Check(CheckStatus.Pass, "Game", gameDir));

        // Loader: exact bytes or nothing — close enough crashes the game.
        var winhttp = Path.Combine(gameDir, "winhttp.dll");
        if (!File.Exists(winhttp))
        {
            checks.Add(new Check(CheckStatus.Fail, "Loader",
                "winhttp.dll missing — the game will start vanilla.",
                "Install (or repair) to deploy the Doorstop 4.5.0 loader."));
        }
        else if (Sha256(winhttp).Equals(KnownLoaderSha256, StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(new Check(CheckStatus.Pass, "Loader", "Doorstop 4.5.0 (known-good bytes)."));
        }
        else
        {
            checks.Add(new Check(CheckStatus.Warn, "Loader",
                "winhttp.dll present but not the known-good Doorstop 4.5.0 bytes — the 2026-09 " +
                "PEAK update crashes the old 4.3.0 loader before BepInEx logs anything.",
                "Install to replace it with the known-good loader."));
        }

        // BepInEx core.
        var bepinexMarker = Path.Combine(Layout.BepInExDir(gameDir), "core", "BepInEx.dll");
        checks.Add(File.Exists(bepinexMarker)
            ? new Check(CheckStatus.Pass, "BepInEx", $"5.4.23.2 core present ({bepinexMarker}).")
            : new Check(CheckStatus.Fail, "BepInEx", "BepInEx core not found.",
                "Install to deploy BepInEx 5.4.23.2."));

        // Plugins + side: which sides are usable given what is on disk.
        var dedicatedMarker = Path.Combine(Layout.DedicatedPluginDir(gameDir), "PeakRelay.Dedicated.dll");
        var clientMarker = Path.Combine(Layout.ClientPluginDir(gameDir), "PeakRelay.Client.dll");
        var hasDedicated = File.Exists(dedicatedMarker);
        var hasClient = File.Exists(clientMarker);
        if (hasDedicated)
            checks.Add(new Check(CheckStatus.Pass, "Dedicated plugin", dedicatedMarker));
        else
            checks.Add(new Check(CheckStatus.Warn, "Dedicated plugin",
                "PeakRelay.Dedicated.dll not found — hosting not available from this install.",
                "Install with the server side to add hosting."));
        if (hasClient)
            checks.Add(new Check(CheckStatus.Pass, "Client plugin", clientMarker));
        else
            checks.Add(new Check(CheckStatus.Warn, "Client plugin",
                "PeakRelay.Client.dll not found — browser/relay routing not available.",
                "Install with the client side to add it."));

        // server.json: on a server-side install its absence means an incomplete install.
        if (hasDedicated)
        {
            var serverJson = Path.Combine(Layout.DedicatedPluginDir(gameDir), "server.json");
            checks.Add(File.Exists(serverJson)
                ? new Check(CheckStatus.Pass, "server.json", serverJson)
                : new Check(CheckStatus.Warn, "server.json",
                    "Dedicated plugin present but no server.json — hosting would use defaults.",
                    "Re-install with the server side to write it."));
        }

        // What the launcher last did here (provenance for "update" decisions).
        if (string.Equals(state.GameDir, gameDir, StringComparison.OrdinalIgnoreCase))
        {
            var last = state.InstalledTag is null
                ? "never recorded (installed outside the launcher?)"
                : $"{state.InstalledTag} via {state.InstalledAsset}";
            checks.Add(new Check(CheckStatus.Pass, "Launcher record", $"last install: {last}."));
        }
        else
        {
            checks.Add(new Check(CheckStatus.Pass, "Launcher record",
                "no record for this install (installed outside the launcher?)."));
        }

        return checks;
    }

    /// <summary>True when everything needed to play through a relay is in place.</summary>
    public static bool PlayReady(IReadOnlyList<Check> checks) =>
        !checks.Any(c => c.Status == CheckStatus.Fail);

    /// <summary>SHA-256 of a file, lowercase hex.</summary>
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>True when something answers TCP on host:port within the timeout.</summary>
    public static bool PortOpen(string host, int port, int timeoutMs = 800)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            return task.Wait(timeoutMs) && client.Connected;
        }
        catch (AggregateException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
