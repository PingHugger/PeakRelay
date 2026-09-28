using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PeakRelay.Server;

/// <summary>
/// HTTP sidecar exposing the relay's room table for the server browser (M5).
/// Endpoints:
///   GET /rooms          → JSON array of {name, players, max}
///   GET / (or /index)   → browser UI (auto-refreshing room table)
///   anything else       → 404
///
/// Transport: HttpListener when the OS grants the URL ACL; otherwise a raw TcpListener
/// speaking just enough HTTP/1.1 for curl and browsers (fixes "Zugriff verweigert"
/// access-denied setups without netsh urlacl changes).
/// </summary>
public static class RoomListHttp
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static Task RunAsync(LbDispatcher dispatcher, int port) =>
        RunAsync(dispatcher, port, CancellationToken.None);

    public static async Task RunAsync(LbDispatcher dispatcher, int port, CancellationToken token)
    {
        if (TryStartHttpListener(dispatcher, port, token))
            return;
        await RunTcpListenerAsync(dispatcher, port, token).ConfigureAwait(false);
    }

    private static bool TryStartHttpListener(LbDispatcher dispatcher, int port, CancellationToken token)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://*:{port}/");
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"relay: HttpListener unavailable ({ex.Message}) — using TcpListener fallback");
            return false;
        }

        Console.WriteLine($"relay: room list at http://0.0.0.0:{port}/rooms (HttpListener)");
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    break; // listener stopped
                }
                _ = Task.Run(() => ServeHttpListener(dispatcher, context));
            }
            try { listener.Stop(); } catch { /* ignore */ }
        }, token);
        return true;
    }

    private static async Task ServeHttpListener(LbDispatcher dispatcher, HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var (status, contentType, body) = BuildResponse(dispatcher, path);
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.Close();
        }
        catch
        {
            try { context.Response.Close(); } catch { /* ignore */ }
        }
    }

    private static async Task RunTcpListenerAsync(LbDispatcher dispatcher, int port, CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"relay: room-list HTTP disabled ({ex.Message})");
            return;
        }

        Console.WriteLine($"relay: room list at http://0.0.0.0:{port}/rooms (TcpListener)");
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch
            {
                break; // listener stopped
            }
            _ = Task.Run(() => ServeTcp(dispatcher, client), token);
        }
        try { listener.Stop(); } catch { /* ignore */ }
    }

    private static async Task ServeTcp(LbDispatcher dispatcher, TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var request = await ReadHttpRequestAsync(stream).ConfigureAwait(false);
                var path = ExtractPath(request);
                var (status, contentType, body) = BuildResponse(dispatcher, path);
                var bytes = Encoding.UTF8.GetBytes(body);
                var statusText = status == 200 ? "OK" : status == 404 ? "Not Found" : "Error";
                var header =
                    $"HTTP/1.1 {status} {statusText}\r\n" +
                    "Content-Type: " + contentType + "\r\n" +
                    "Content-Length: " + bytes.Length + "\r\n" +
                    "Connection: close\r\n" +
                    "\r\n";
                var headerBytes = Encoding.ASCII.GetBytes(header);
                await stream.WriteAsync(headerBytes).ConfigureAwait(false);
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // a malformed request must not take the sidecar down
        }
    }

    private static async Task<string> ReadHttpRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        var sb = new StringBuilder();
        // read until the header terminator; requests we care about have no body
        while (sb.Length < 8192)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
            if (read == 0)
                break;
            sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (sb.ToString().Contains("\r\n\r\n"))
                break;
        }
        return sb.ToString();
    }

    private static string ExtractPath(string request)
    {
        // "GET /rooms HTTP/1.1"
        var firstLineEnd = request.IndexOf("\r\n", StringComparison.Ordinal);
        var firstLine = firstLineEnd < 0 ? request : request[..firstLineEnd];
        var parts = firstLine.Split(' ');
        if (parts.Length < 2)
            return "/";
        var raw = parts[1];
        var query = raw.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? raw : raw[..query];
    }

    private sealed record RoomDto(string Name, int Players, int Max);

    private static List<RoomDto> ToRoomDtos(LbDispatcher dispatcher)
    {
        var rooms = dispatcher.SnapshotRooms();
        var dtos = new List<RoomDto>(rooms.Count);
        foreach (var (name, players, max) in rooms)
            dtos.Add(new RoomDto(name, players, max));
        return dtos;
    }

    private static (int Status, string ContentType, string Body) BuildResponse(LbDispatcher dispatcher, string path)
    {
        switch (path)
        {
            case "/rooms":
            {
                var payload = JsonSerializer.Serialize(ToRoomDtos(dispatcher), JsonOptions);
                return (200, "application/json", payload);
            }
            case "/":
            case "/index.html":
            {
                return (200, "text/html; charset=utf-8", BuildIndexPage(dispatcher));
            }
            default:
                return (404, "text/plain", "not found\n");
        }
    }

    private static string BuildIndexPage(LbDispatcher dispatcher)
    {
        var rooms = dispatcher.SnapshotRooms();
        var rows = new StringBuilder();
        foreach (var room in rooms)
        {
            rows.Append("<tr><td>").Append(WebUtility.HtmlEncode(room.Name))
                .Append("</td><td>").Append(room.Players)
                .Append("</td><td>").Append(room.Max)
                .Append("</td></tr>");
        }
        if (rooms.Count == 0)
            rows.Append("<tr><td colspan=\"3\" class=\"empty\">no rooms — waiting for hosts…</td></tr>");

        var page = new StringBuilder();
        page.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        page.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        page.Append("<title>PeakRelay — room list</title>\n<style>\n");
        page.Append(":root { color-scheme: dark; }\n");
        page.Append("body { font-family: system-ui, sans-serif; background: #10141c; color: #dfe6f1; margin: 0; padding: 2rem; }\n");
        page.Append("h1 { font-size: 1.4rem; margin: 0 0 .25rem; }\n");
        page.Append("p.sub { color: #8fa0b8; margin: 0 0 1.5rem; }\n");
        page.Append("table { border-collapse: collapse; width: 100%; max-width: 640px; }\n");
        page.Append("th, td { text-align: left; padding: .5rem .75rem; border-bottom: 1px solid #232c3d; }\n");
        page.Append("th { color: #8fa0b8; font-weight: 600; font-size: .8rem; text-transform: uppercase; }\n");
        page.Append("tr:hover td { background: #182031; }\n");
        page.Append("td.empty { color: #8fa0b8; font-style: italic; }\n");
        page.Append(".dot { display: inline-block; width: .6rem; height: .6rem; border-radius: 50%; background: #3fb950; margin-right: .4rem; }\n");
        page.Append("</style>\n</head>\n<body>\n");
        page.Append("<h1><span class=\"dot\"></span>PeakRelay rooms</h1>\n");
        page.Append("<p class=\"sub\">auto-refreshes every 3 s · JSON endpoint: /rooms</p>\n");
        page.Append("<table>\n  <thead><tr><th>Room</th><th>Players</th><th>Max</th></tr></thead>\n");
        page.Append("  <tbody id=\"rooms\">\n");
        page.Append(rows);
        page.Append("  </tbody>\n</table>\n");
        page.Append("<script>\nasync function refresh() {\n");
        page.Append("  try {\n");
        page.Append("    const res = await fetch('/rooms');\n");
        page.Append("    const rooms = await res.json();\n");
        page.Append("    const body = document.getElementById('rooms');\n");
        page.Append("    const esc = s => String(s).replace(/[<>&]/g, '');\n");
        page.Append("    body.innerHTML = rooms.length === 0\n");
        page.Append("      ? '<tr><td colspan=\"3\" class=\"empty\">no rooms — waiting for hosts…</td></tr>'\n");
        page.Append("      : rooms.map(r => `<tr><td>${esc(r.name)}</td><td>${r.players}</td><td>${r.max}</td></tr>`).join('');\n");
        page.Append("  } catch (e) { /* keep last snapshot */ }\n");
        page.Append("}\nsetInterval(refresh, 3000);\n</script>\n");
        page.Append("</body>\n</html>\n");
        return page.ToString();
    }
}
