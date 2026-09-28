using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading;
using ExitGames.Client.Photon;
using PeakRelay.Protocol;

namespace PeakRelay.Client;

/// <summary>
/// Active relay transport for the player plugin: pipes PUN datagrams to the relay server
/// over TCP envelopes. This is the M1-validated transport (RelayTestSocket /
/// RelayGameSocket) packaged for normal game clients — Photon3 sees a connected socket
/// and speaks its usual datagrams; the envelope framing is ours.
///
/// Photon3 drives IPhotonSocket: Connect() must complete asynchronously by calling
/// peerBase.OnConnect() (or HandleException on failure) from a worker thread.
/// </summary>
public sealed class RelayClientSocket : IPhotonSocket, IDisposable
{
    private readonly object _sync = new();
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private int _requestId;

    public RelayClientSocket(PeerBase peer) : base(peer)
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
        new Thread(ConnectThread) { IsBackground = true, Name = "peakrelay-client-connect" }.Start();
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
        catch (Exception)
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
        try
        {
            var client = new TcpClient();
            var task = client.ConnectAsync(RelayConfig.Host, RelayConfig.Port);
            if (!task.Wait(5000) || !client.Connected)
                throw new TimeoutException($"relay {RelayConfig.Host}:{RelayConfig.Port} did not accept within 5s");
            _tcp = client;
            _stream = client.GetStream();

            var hello = Envelope.Write(RelayOp.Hello, Envelope.FlagNone, NextId(), ReadOnlySpan<byte>.Empty);
            _stream.Write(Frame.Write(hello));
            _stream.Flush();

            State = PhotonSocketState.Connected;
            peerBase.OnConnect();
            new Thread(ReceiveLoop) { IsBackground = true, Name = "peakrelay-client-recv" }.Start();
        }
        catch (Exception ex)
        {
            EnqueueDebugReturn(DebugLevel.ERROR, $"PeakRelay: relay connect to {RelayConfig.Host}:{RelayConfig.Port} failed: {ex.Message}");
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
                    HandleReceivedDatagram(datagram, datagram.Length, willBeReused: false);
                }
            }
        }
        catch (Exception)
        {
            // fall through to the disconnect below
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
