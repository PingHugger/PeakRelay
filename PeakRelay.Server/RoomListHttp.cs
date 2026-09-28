using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PeakRelay.Protocol;

namespace PeakRelay.Server;

/// <summary>
/// Minimal HTTP sidecar exposing the relay's room table for the server browser (M5).
/// GET /rooms → JSON array of {name, players, max}. No external HTTP dependency.
/// </summary>
public static class RoomListHttp
{
    public static async Task RunAsync(LbDispatcher dispatcher, int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://*:{port}/rooms/");
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"relay: room-list HTTP disabled ({ex.Message})");
            return;
        }

        Console.WriteLine($"relay: room list at http://0.0.0.0:{port}/rooms/");
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch
            {
                return; // listener stopped
            }

            try
            {
                var rooms = dispatcher.SnapshotRooms();
                var payload = JsonSerializer.Serialize(rooms, new JsonSerializerOptions
                {
                    IncludeFields = true,
                });
                var bytes = Encoding.UTF8.GetBytes(payload);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch
            {
                try { context.Response.Close(); } catch { /* ignore */ }
            }
        }
    }
}
