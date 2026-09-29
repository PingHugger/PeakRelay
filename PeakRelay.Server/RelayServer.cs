using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PeakRelay.Server;

/// <summary>
/// The relay itself: TCP 5055-style game listener + HTTP room directory, one instance per
/// pair of ports. Program.cs hosts one for the process lifetime; the launcher hosts one
/// in-process and stops it when the user asks (ports are released on Dispose).
/// </summary>
public sealed class RelayServer : IDisposable
{
    private readonly int _listenPort;
    private readonly int _httpPort;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _httpTask;
    private readonly Task _acceptTask;
    private readonly Socket _listener;

    public int ListenPort => _listenPort;
    public int HttpPort => _httpPort;

    public RelayServer(int listenPort = 5055, int httpPort = 5056)
    {
        _listenPort = listenPort;
        _httpPort = httpPort;

        // the master-role redirect points clients back at this same endpoint (game-role connection)
        var dispatcher = new LbDispatcher($"127.0.0.1:{listenPort}");
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Any, listenPort));
        _listener.Listen(16);

        Console.WriteLine($"PeakRelay server: TCP {listenPort}, HTTP {httpPort} (LoadBalancing master+game)");

        _httpTask = RoomListHttp.RunAsync(dispatcher, httpPort, _cts.Token);
        _acceptTask = Task.Run(() => AcceptLoopAsync(dispatcher, _cts.Token));
    }

    private async Task AcceptLoopAsync(LbDispatcher dispatcher, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await _listener.AcceptAsync(token).ConfigureAwait(false);
                _ = new Session(client, dispatcher);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
            // listener closed
        }
    }

    /// <summary>Stops accepting, closes the HTTP sidecar, releases both ports.</summary>
    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _listener.Close();
        }
        catch (Exception)
        {
            // best effort — Dispose must never throw into a UI click handler
        }
        try
        {
            _httpTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // HttpListener paths may observe the cancel as an exception; ports are released either way
        }
        _cts.Dispose();
    }
}
