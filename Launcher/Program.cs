using PeakRelay.Launcher;
using PeakRelay.Launcher.Core;

namespace PeakRelay.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Scriptable entry point: the same engine the GUI drives (mirrors the installers).
        if (args.Length > 0)
        {
            Environment.ExitCode = LauncherCli.Run(args).GetAwaiter().GetResult();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
