using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using PeakRelay.Protocol;

namespace PeakRelay.Tools.TestClient;

/// <summary>Raw-socket echo check of the relay transport (M0 selftest), bypassing Photon3.</summary>
public static class EchoFallback
{
    public static int Run(string[] args)
    {
        var host = args.Length > 1 ? args[1] : "127.0.0.1";
        var port = args.Length > 2 && int.TryParse(args[2], out var p) ? p : 5055;

        using var a = new TcpClient();
        using var b = new TcpClient();
        a.Connect(host, port);
        b.Connect(host, port);
        Console.WriteLine($"connected two clients to {host}:{port}");
        SendHello(a);
        SendHello(b);

        SendDataEnvelope(a, 0x0001, "ping-from-a"u8);
        var echoed = ReceiveDataEnvelope(b, 10);
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

    private static void SendHello(TcpClient client)
    {
        var hello = Envelope.Write(RelayOp.Hello, 0, 0, new byte[] { Frame.Version, 0x00 });
        client.GetStream().Write(Frame.Write(hello));
        client.GetStream().Flush();
        var welcome = ReceiveEnvelope(client, 10, RelayOp.Welcome);
        if (welcome == null)
            throw new InvalidOperationException("relay did not answer the Hello handshake");
    }

    private static byte[]? ReceiveEnvelope(TcpClient client, int timeoutSeconds, RelayOp expected)
    {
        var stream = client.GetStream();
        stream.ReadTimeout = timeoutSeconds * 1000;
        try
        {
            var header = new byte[Frame.HeaderSize];
            ReadFull(stream, header);
            int frameLength = BinaryPrimitives.ReadInt32LittleEndian(header);
            int version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (version != Frame.Version || frameLength < Frame.HeaderSize)
                throw new InvalidOperationException($"bad frame: len={frameLength} v={version}");
            var payload = new byte[frameLength - Frame.HeaderSize];
            ReadFull(stream, payload);
            if (Envelope.TryRead(payload, out var envelope) && envelope.Op == expected)
                return envelope.Payload.ToArray();
            return null;
        }
        finally
        {
            stream.ReadTimeout = Timeout.Infinite;
        }
    }

    private static void SendDataEnvelope(TcpClient client, ushort requestId, ReadOnlySpan<byte> datagram)
    {
        var envelope = Envelope.Write(RelayOp.Data, 0, requestId, datagram);
        client.GetStream().Write(Frame.Write(envelope));
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
