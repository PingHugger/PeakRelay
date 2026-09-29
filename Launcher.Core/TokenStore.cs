using System;
using System.IO;

namespace PeakRelay.Launcher.Core;

/// <summary>
/// Where the launcher finds its GitHub token for private-repo release channels:
/// 1. PEAKRELAY_GH_TOKEN in the environment (preferred; nothing persisted)
/// 2. %LOCALAPPDATA%\PeakRelay\github.token (per-user file; chmod-equivalent is moot on
///    Windows, but the file stays inside the user profile, never next to the exe)
/// Resolution order matters: a token set for one session must not be shadowed by a stale
/// file, so env wins.
/// </summary>
public static class TokenStore
{
    public const string EnvVar = ReleaseClient.TokenEnvVar;

    /// <summary>The effective token, or null when the repo is reachable anonymously.</summary>
    public static string? Resolve()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();

        try
        {
            if (File.Exists(LauncherPaths.TokenFile))
            {
                var fromFile = File.ReadAllText(LauncherPaths.TokenFile).Trim();
                if (!string.IsNullOrWhiteSpace(fromFile))
                    return fromFile;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return null;
    }

    /// <summary>Writes the token file (creating the directory). Returns the path.</summary>
    public static string SaveToFile(string token)
    {
        Directory.CreateDirectory(LauncherPaths.Root);
        File.WriteAllText(LauncherPaths.TokenFile, token.Trim() + Environment.NewLine);
        return LauncherPaths.TokenFile;
    }

    /// <summary>Removes the token file, if present.</summary>
    public static void DeleteFile()
    {
        try
        {
            File.Delete(LauncherPaths.TokenFile);
        }
        catch (IOException)
        {
        }
    }
}
