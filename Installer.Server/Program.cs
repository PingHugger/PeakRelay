using System.Runtime.InteropServices;

namespace PeakRelay.Installer.Server;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            // GUI-subsystem app: reattach to the calling terminal so CLI output is visible
            AttachConsole(-1);
            return Core.Cli.Run(args, serverMode: true);
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
