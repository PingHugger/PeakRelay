using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using ExitGames.Client.Photon;
using PeakRelay.Protocol;

namespace PeakRelay.Tools.TestClient;

/// <summary>
/// Datagram pipe between a real Photon3 peer and the relay, over TCP + relay envelopes.
/// Mirrors RelaySocket from the client plugin, minus BepInEx/Unity dependencies.
/// </summary>
public sealed class RelayTestSocket : IPhotonSocket, IDisposable
{
    private readonly object _sync = new();
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private int _requestId;

    public RelayTestSocket(PeerBase peer) : base(peer)
    {
        PollReceive = false;
    }

    public void Dispose()
    {
        State = PhotonSocketState.Disconnecting;
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _tcp?.Close(); } catch { /* ignore */ }
        _stream = null;
        _tcp = null;
        State = PhotonSocketState.Disconnected;
    }

    public override bool Connect()
    {
        lock (_sync)
        {
            if (!base.Connect())
                return false;
            State = PhotonSocketState.Connecting;
        }
        new Thread(ConnectThread) { IsBackground = true }.Start();
        return true;
    }

    public override bool Disconnect()
    {
        lock (_sync)
        {
            State = PhotonSocketState.Disconnecting;
            try { _stream?.Dispose(); } catch { /* ignore */ }
            try { _tcp?.Close(); } catch { /* ignore */ }
            _stream = null;
            _tcp = null;
            State = PhotonSocketState.Disconnected;
        }
        return true;
    }

    public override PhotonSocketError Send(byte[] data, int length)
    {
        Diagnostics.Dump("C->S send", data, length);
        var stream = _stream;
        if (stream == null)
            return PhotonSocketError.Skipped;
        try
        {
            var envelope = Envelope.Write(RelayOp.Data, 0, NextId(), data.AsSpan(0, length));
            stream.Write(Frame.Write(envelope));
            stream.Flush();
            return PhotonSocketError.Success;
        }
        catch
        {
            HandleException(StatusCode.SendError);
            return PhotonSocketError.Exception;
        }
    }

    public override PhotonSocketError Receive(out byte[] data)
    {
        data = null!;
        return PhotonSocketError.NoData; // thread-driven receive, like SocketUdp
    }

    private void ConnectThread()
    {
        var host = TestConfig.Host;
        var port = TestConfig.Port;
        try
        {
            var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            if (!task.Wait(5000) || !client.Connected)
                throw new TimeoutException($"relay {host}:{port}");
            _tcp = client;
            _stream = client.GetStream();

            var helloBody = new byte[] { Frame.Version, 0x00 };
            var hello = Envelope.Write(RelayOp.Hello, 0, NextId(), helloBody);
            _stream.Write(Frame.Write(hello));
            _stream.Flush();

            State = PhotonSocketState.Connected;
            peerBase.OnConnect();
            new Thread(ReceiveLoop) { IsBackground = true }.Start();
        }
        catch (Exception ex)
        {
            EnqueueDebugReturn(DebugLevel.ERROR, $"RelayTestSocket connect failed: {ex.Message}");
            HandleException(StatusCode.ExceptionOnConnect);
        }
    }

    private void ReceiveLoop()
    {
        var header = new byte[Frame.HeaderSize];
        try
        {
            while (State == PhotonSocketState.Connected)
            {
                if (!ReadFull(_stream!, header))
                    break;
                int frameLength = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (frameLength < Frame.HeaderSize || frameLength > Frame.HeaderSize + Frame.MaxPayload)
                    break;
                var payload = new byte[frameLength - Frame.HeaderSize];
                if (!ReadFull(_stream!, payload))
                    break;
                if (Envelope.TryRead(payload, out var envelope) && envelope.Op == RelayOp.Data)
                {
                    var datagram = envelope.Payload.ToArray();
                    Diagnostics.Dump("S->C recv ", datagram, datagram.Length);
                    HandleReceivedDatagram(datagram, datagram.Length, willBeReused: false);
                }
            }
        }
        catch (Exception ex)
        {
            // surface the real failure instead of silently degrading to ExceptionOnReceive
            var parts = new List<string>();
            var cur = (Exception?)ex;
            while (cur != null)
            {
                parts.Add($"{cur.GetType().FullName}: {cur.Message}");
                cur = cur.InnerException;
            }
            Console.Error.WriteLine($"[recv-loop] {string.Join(" -> ", parts)}");
        }
        if (State == PhotonSocketState.Connected)
            HandleException(StatusCode.ExceptionOnReceive);
    }

    private static bool ReadFull(NetworkStream stream, Span<byte> target)
    {
        int total = 0;
        while (total < target.Length)
        {
            int read = stream.Read(target[total..]);
            if (read == 0)
                return false;
            total += read;
        }
        return true;
    }

    private ushort NextId() => (ushort)Interlocked.Increment(ref _requestId);
}
