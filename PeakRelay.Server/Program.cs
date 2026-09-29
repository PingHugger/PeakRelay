using System;
using PeakRelay.Server;

using var server = new RelayServer(
    args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5055,
    args.Length > 1 && int.TryParse(args[1], out var h) ? h : 5056);

// Host until the process is asked to stop (Ctrl+C or kill).
var done = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    done.Set();
};
done.Wait();
