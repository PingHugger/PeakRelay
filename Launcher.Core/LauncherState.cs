using System.IO;
using System.Text.Json;

namespace PeakRelay.Launcher.Core;

/// <summary>Persisted launcher state (launcher.json under %LOCALAPPDATA%\PeakRelay).</summary>
public sealed class LauncherState
{
    public string GameDir { get; set; } = "";
    public bool ServerSide { get; set; }
    public bool ClientSide { get; set; }

    /// <summary>
    /// Whether Install also deploys the dedicated-host files (server side). Default false:
    /// most players only want to join, not host. When false, Install REMOVES any dedicated
    /// files a previous install left behind.
    /// </summary>
    public bool InstallDedicated { get; set; }

    /// <summary>What the last ModApply installed (release tag + asset), for update checks.</summary>
    public string? InstalledTag { get; set; }
    public string? InstalledAsset { get; set; }

    /// <summary>Offered-but-declined update, so it is not nagged every start.</summary>
    public string? SkippedTag { get; set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static LauncherState Load()
    {
        try
        {
            if (File.Exists(LauncherPaths.StateFile))
                return JsonSerializer.Deserialize<LauncherState>(File.ReadAllText(LauncherPaths.StateFile)) ?? new LauncherState();
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return new LauncherState();
    }

    public void Save()
    {
        Directory.CreateDirectory(LauncherPaths.Root);
        File.WriteAllText(LauncherPaths.StateFile, JsonSerializer.Serialize(this, Options));
    }
}
