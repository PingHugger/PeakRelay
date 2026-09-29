namespace PeakRelay.Installer.Core;

/// <summary>
/// Single source of truth for the embedded payload. This file lives at
/// Installer.Core/Payload/payload/… inside the Installer.Core project; each item is
/// embedded with LogicalName="PeakRelay.Installer.Payload.%(RecursiveDir)%(Filename)%(Extension)",
/// so a resource's name IS its path under Payload/payload — renames move both sides
/// together and a missing file fails the build (EmbeddedResource globs are not optional).
/// </summary>
public static class PayloadNames
{
    /// <summary>BepInEx 5.4.23.2 win_x64 archive, extracted at the game root.</summary>
    public const string BepInExZip = "bepinex.zip";

    /// <summary>Doorstop 4.5.0 loader proxy (installed as the game's winhttp.dll).</summary>
    public const string DoorstopWinhttp = "doorstop/winhttp.dll";

    /// <summary>Published PeakRelay.Server (relay.zip), staged as-is.</summary>
    public const string RelayZip = "relay/relay.zip";

    /// <summary>Payload dir (relative to Payload/payload) of each plugin set, with the
    /// exact DLLs it must contain — a missing or extra file breaks the build here.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> PluginSets =
        new Dictionary<string, string[]>
        {
            ["plugins/Dedicated"] = new[] { "PeakRelay.Dedicated.dll", "PeakRelay.Protocol.dll" },
            ["plugins/Client"] = new[] { "PeakRelay.Client.dll", "PeakRelay.Protocol.dll" },
        };

    /// <summary>Dedicated-host plugin set (payload dir "plugins/Dedicated").</summary>
    public const string Dedicated = "plugins/Dedicated";

    /// <summary>Client plugin set (payload dir "plugins/Client").</summary>
    public const string Client = "plugins/Client";
}
