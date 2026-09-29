using System.IO;

namespace PeakRelay.Launcher.Core;

/// <summary>
/// Where the launcher keeps its own files: %LOCALAPPDATA%\PeakRelay. Never inside the
/// game install — the game dir belongs to Steam and updates wipe whatever it likes.
/// </summary>
public static class LauncherPaths
{
    private static string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PeakRelay");

    public static string Root => _root;

    /// <summary>Test hook: point the launcher's storage at a temp dir. Tests must restore
    /// the old value (or delete the temp dir) when done.</summary>
    public static void UseRootForTests(string path) => _root = path;

    public static string StateFile => Path.Combine(Root, "launcher.json");

    /// <summary>Relay files and run artifacts (logs) for in-process hosting.</summary>
    public static string RelayDir => Path.Combine(Root, "relay");

    /// <summary>Optional GitHub token for private-repo release channels (plaintext,
    /// per-user profile; prefer a fine-grained read-only PAT).</summary>
    public static string TokenFile => Path.Combine(Root, "github.token");

    public static string LogDir => Path.Combine(Root, "logs");

    public static string LogFile(string name) => Path.Combine(LogDir, name);
}
