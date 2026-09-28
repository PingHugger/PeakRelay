using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using PeakRelay.Server;

var listenPort = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5055;
var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
listener.Bind(new IPEndPoint(IPAddress.Any, listenPort));
listener.Listen(16);

var room = new Room("default");
var clients = new List<Socket>();

Console.WriteLine($"PeakRelay server: TCP {listenPort}, room '{room.Name}' (M0 echo fan-out)");

try
{
    while (true)
    {
        var client = listener.Accept();
        clients.Add(client);
        _ = new Session(client, room); // reader thread; cleans itself up on disconnect
        Console.WriteLine($"session connected (room members: {room.MemberCount})");
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
