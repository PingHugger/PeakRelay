using System;
using System.Net;
using System.Net.Sockets;
using PeakRelay.Server;

var listenPort = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5055;
var httpPort = args.Length > 1 && int.TryParse(args[1], out var h) ? h : 5056;

// the master-role redirect points clients back at this same endpoint (game-role connection)
var dispatcher = new LbDispatcher($"127.0.0.1:{listenPort}");
var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
listener.Bind(new IPEndPoint(IPAddress.Any, listenPort));
listener.Listen(16);

Console.WriteLine($"PeakRelay server: TCP {listenPort}, HTTP {httpPort} (LoadBalancing master+game)");

_ = RoomListHttp.RunAsync(dispatcher, httpPort);

try
{
    while (true)
    {
        var client = listener.Accept();
        _ = new Session(client, dispatcher);
    }
}
catch (SocketException)
{
    // listener closed
}
finally
{
    listener.Close();
}
