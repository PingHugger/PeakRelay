using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using PeakRelay.Protocol;

namespace PeakRelay.Tools.TestClient;

/// <summary>
/// M0 selftest: verifies the relay's framing + echo fan-out path end to end without the game.
/// Usage: selftest [host] [port]  (two connections are made; one echoes to the other).
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var host = args.Length > 0 ? args[0] : "127.0.0.1";
        var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 5055;

        using var a = new TcpClient();
        using var b = new TcpClient();
        a.Connect(host, port);
        b.Connect(host, port);
        Console.WriteLine($"connected two clients to {host}:{port}");

        SendDataEnvelope(a, 0x0001, "ping-from-a"u8);
        var echoed = ReceiveDataEnvelope(b, timeoutSeconds: 10);
        var text = Encoding.UTF8.GetString(echoed);
        Console.WriteLine($"b received: \"{text}\"");

        if (text == "ping-from-a")
        {
            Console.WriteLine("selftest OK");
            return 0;
        }
        Console.Error.WriteLine("selftest FAILED: unexpected echo payload");
        return 1;
    }

    private static void SendDataEnvelope(TcpClient client, ushort requestId, ReadOnlySpan<byte> datagram)
    {
        var envelope = Envelope.Write(RelayOp.Data, Envelope.FlagNone, requestId, datagram);
        var frame = Frame.Write(envelope);
        client.GetStream().Write(frame);
        client.GetStream().Flush();
    }

    private static byte[] ReceiveDataEnvelope(TcpClient client, int timeoutSeconds)
    {
        var stream = client.GetStream();
        stream.ReadTimeout = timeoutSeconds * 1000;
        var header = new byte[Frame.HeaderSize];
        ReadFull(stream, header);
        int frameLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        int version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (version != Frame.Version || frameLength < Frame.HeaderSize)
            throw new InvalidOperationException($"bad frame: len={frameLength} v={version}");
        var payload = new byte[frameLength - Frame.HeaderSize];
        ReadFull(stream, payload);
        if (!Envelope.TryRead(payload, out var envelope) || envelope.Op != RelayOp.Data)
            throw new InvalidOperationException("expected a Data envelope");
        return envelope.Payload.ToArray();
    }

    private static void ReadFull(NetworkStream stream, Span<byte> target)
    {
        int total = 0;
        while (total < target.Length)
        {
            int read = stream.Read(target[total..]);
            if (read == 0)
                throw new InvalidOperationException("connection closed by relay");
            total += read;
        }
    }
}
