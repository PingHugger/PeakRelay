using System.Diagnostics;
using System.IO;
using PeakRelay.Installer.Core;

namespace PeakRelay.Launcher.Core;

/// <summary>Starts the game after the doctor says it is safe.</summary>
public static class PlayLauncher
{
    /// <summary>
    /// Launches PEAK.exe from <paramref name="gameDir"/>. The caller runs the doctor first;
    /// this throws when the game root vanished between the check and the click.
    /// </summary>
    public static Process Start(string gameDir)
    {
        if (!GameLocator.LooksLikeGameRoot(gameDir))
            throw new InvalidOperationException($"'{gameDir}' is not a PEAK install (no PEAK.exe)");
        var exe = Path.Combine(Path.GetFullPath(gameDir), "PEAK.exe");
        return Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetFullPath(gameDir),
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException($"could not start {exe}");
    }
}
