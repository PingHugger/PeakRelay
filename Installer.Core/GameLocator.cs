using System.IO;

namespace PeakRelay.Installer.Core;

/// <summary>
/// Validates a PEAK install directory and finds sensible defaults. The single rule both
/// installers enforce: the chosen folder must contain PEAK.exe (the game root, not
/// PEAK_Data, not a parent).
/// </summary>
public static class GameLocator
{
    public static bool LooksLikeGameRoot(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "PEAK.exe"));

    /// <summary>Candidate defaults, best first (env var → repo-sibling → Steam library).</summary>
    public static IReadOnlyList<string> DefaultCandidates()
    {
        var list = new List<string>();
        var env = Environment.GetEnvironmentVariable("PEAK_GAME_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            list.Add(env);

        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            // dist/installers/… → walk up a few levels looking for a game root (repo checkout)
            var dir = Path.GetDirectoryName(Path.GetFullPath(exe));
            for (var i = 0; i < 4 && dir != null; i++)
            {
                list.Add(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }

        list.Add(@"C:\Program Files (x86)\Steam\steamapps\common\PEAK");
        return list;
    }

    /// <summary>First candidate that is actually a game root, or null.</summary>
    public static string? FindDefaultGameDir()
    {
        foreach (var candidate in DefaultCandidates())
        {
            if (LooksLikeGameRoot(candidate))
                return Path.GetFullPath(candidate);
        }
        return null;
    }
}
