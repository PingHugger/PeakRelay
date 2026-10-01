using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading;
using ExitGames.Client.Photon;
using PeakRelay.Protocol;

namespace PeakRelay.Dedicated;

/// <summary>
/// Photon socket that pipes datagrams to the PeakRelay server over TCP envelopes —
/// the M1-validated transport (RelayTestSocket) packaged for the game process.
/// Registered for ConnectionProtocol.Udp via a Harmony postfix on the
/// LoadBalancingPeer constructor (see DedicatedPatches), so every PUN client in this
/// process speaks the relay protocol.
/// </summary>
public sealed class RelayGameSocket : IPhotonSocket, IDisposable
{
    private readonly object _sync = new();
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private int _requestId;

    public RelayGameSocket(PeerBase peer) : base(peer)
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
        new Thread(ConnectThread) { IsBackground = true, Name = "peakrelay-dedicated-connect" }.Start();
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
        if (DedicatedState.Config?.LogDatagrams == true)
            ServerLog.Info($"C->S {length}B op-stream");
        var stream = _stream;
        if (stream == null)
            return PhotonSocketError.Skipped;
        try
        {
            var envelope = Envelope.Write(RelayOp.Data, Envelope.FlagNone, NextId(), data.AsSpan(0, length));
            stream.Write(Frame.Write(envelope));
            stream.Flush();
            return PhotonSocketError.Success;
        }
        catch (Exception ex)
        {
            ServerLog.Error($"relay send failed: {ex.Message}");
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
        var config = DedicatedState.Config;
        var host = config?.RelayHost ?? "127.0.0.1";
        var port = config?.RelayPort ?? 5055;
        try
        {
            var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            if (!task.Wait(5000) || !client.Connected)
                throw new TimeoutException($"relay {host}:{port} did not accept within 5s");
            _tcp = client;
            _stream = client.GetStream();

            var helloBody = new byte[] { Frame.Version, 0x00 };
            var hello = Envelope.Write(RelayOp.Hello, Envelope.FlagNone, NextId(), helloBody);
            _stream.Write(Frame.Write(hello));
            _stream.Flush();

            State = PhotonSocketState.Connected;
            ServerLog.Info($"relay link established to {host}:{port}");
            peerBase.OnConnect();
            new Thread(ReceiveLoop) { IsBackground = true, Name = "peakrelay-dedicated-recv" }.Start();
        }
        catch (Exception ex)
        {
            ServerLog.Error($"relay connect to {host}:{port} failed: {ex.Message}");
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
                var stream = _stream;
                if (stream == null || !ReadFull(stream, header))
                    break;
                int frameLength = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (frameLength < Frame.HeaderSize || frameLength > Frame.HeaderSize + Frame.MaxPayload)
                    break;
                var payload = new byte[frameLength - Frame.HeaderSize];
                if (!ReadFull(stream, payload))
                    break;
                if (Envelope.TryRead(payload, out var envelope) && envelope.Op == RelayOp.Data)
                {
                    var datagram = envelope.Payload.ToArray();
                    if (DedicatedState.Config?.LogDatagrams == true)
                        ServerLog.Info($"S->C {datagram.Length}B datagram");
                    HandleReceivedDatagram(datagram, datagram.Length, willBeReused: false);
                }
            }
        }
        catch (Exception ex)
        {
            if (State == PhotonSocketState.Connected)
                ServerLog.Error($"relay receive loop ended: {ex.Message}");
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
