using System.Runtime.InteropServices;

namespace PeakRelay.Installer.Client;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            AttachConsole(-1);
            return Core.Cli.Run(args, serverMode: false);
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
