using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ExitGames.Client.Photon;
using PeakRelay.Protocol;

namespace PeakRelay.Client;

/// <summary>
/// M0 shim: an IPhotonSocket implementation that behaves like SocketUdp but (a) forwards every
/// datagram through the relay protocol (TCP) and (b) feeds each datagram to the TraceRecorder.
/// The real UDP socket stays the fallback path so the game is always playable. Relay logic is
/// intentionally minimal; the relay becomes the sole path in M1.
/// </summary>
public sealed class RelaySocket : IPhotonSocket, IDisposable
{
    private readonly object _sync = new();
    private Socket? _udp;
    private TcpClient? _relay;
    private NetworkStream? _relayStream;
    private Thread? _receiveThread;
    private int _requestId;

    public RelaySocket(PeerBase peer)
        : base(peer)
    {
        PollReceive = false;
        TraceRecorder.Instance.LogLifecycle("RelaySocket created (passive mode)");
    }

    public void Dispose()
    {
        State = PhotonSocketState.Disconnecting;
        lock (_sync)
        {
            if (_udp != null && _udp.Connected)
            {
                try { _udp.Close(1); } catch { /* already gone */ }
            }
            _udp = null;
        }
        try { _relayStream?.Dispose(); } catch { /* ignore */ }
        try { _relay?.Close(); } catch { /* ignore */ }
        _relayStream = null;
        _relay = null;
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

        var thread = new Thread(DnsAndConnect) { IsBackground = true, Name = "peakrelay-connect" };
        thread.Start();
        return true;
    }

    public override bool Disconnect()
    {
        lock (_sync)
        {
            State = PhotonSocketState.Disconnecting;
            if (_udp != null)
            {
                try { _udp.Close(1); } catch { /* ignore */ }
            }
            _udp = null;
            State = PhotonSocketState.Disconnected;
        }
        return true;
    }

    public override PhotonSocketError Send(byte[] data, int length)
    {
        TraceRecorder.Instance.LogDatagram("send", data, length);
        ForwardToRelay(data, length);

        try
        {
            if (_udp == null || !_udp.Connected)
                return PhotonSocketError.Skipped;
            _udp.Send(data, 0, length, SocketFlags.None);
        }
        catch (SocketException ex)
        {
            if (ex.SocketErrorCode == SocketError.WouldBlock)
                return PhotonSocketError.Busy;
            if (State == PhotonSocketState.Disconnecting || State == PhotonSocketState.Disconnected)
                return PhotonSocketError.Exception;
            SocketErrorCode = (int)ex.SocketErrorCode;
            HandleException(StatusCode.SendError);
            return PhotonSocketError.Exception;
        }
        return PhotonSocketError.Success;
    }

    /// <summary>
    /// Never used by Photon3 with PollReceive=false (see docs/protocol-notes.md) — the receive
    /// thread delivers via HandleReceivedDatagram instead, exactly like SocketUdp.
    /// </summary>
    public override PhotonSocketError Receive(out byte[] data)
    {
        data = null!;
        return PhotonSocketError.NoData;
    }

    private void DnsAndConnect()
    {
        var addresses = GetIpAddresses(ServerAddress);
        if (addresses == null)
            return;

        var errors = string.Empty;
        foreach (var address in addresses)
        {
            try
            {
                var sock = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
                {
                    Blocking = false
                };
                sock.Connect(address, ServerPort);
                if (sock.Connected)
                {
                    _udp = sock;
                    break;
                }
                sock.Close();
            }
            catch (SocketException ex)
            {
                errors += $"{ex} (code {ex.ErrorCode}); ";
            }
            catch (Exception ex)
            {
                errors += $"{ex}; ";
            }
        }

        if (_udp == null || !_udp.Connected)
        {
            EnqueueDebugReturn(DebugLevel.ERROR, $"PeakRelay: failed to open fallback UDP socket. {errors}");
            HandleException(StatusCode.ExceptionOnConnect);
            return;
        }

        AddressResolvedAsIpv6 = _udp.AddressFamily == AddressFamily.InterNetworkV6;
        ServerIpAddress = _udp.RemoteEndPoint.ToString();
        State = PhotonSocketState.Connected;
        peerBase.OnConnect();
        TraceRecorder.Instance.LogLifecycle("RelaySocket connected (passive mode)");

        _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "peakrelay-recv" };
        _receiveThread.Start();
        ThreadPool.UnsafeQueueUserWorkItem(_ => RelayConnectLoop(), null);
    }

    /// <summary>Drains the real socket into the peer, exactly like SocketUdp.ReceiveLoop.</summary>
    private void ReceiveLoop()
    {
        var buffer = new byte[MTU];
        while (State == PhotonSocketState.Connected)
        {
            try
            {
                if (_udp == null)
                    break;
                if (_udp.Poll(5000, SelectMode.SelectRead))
                {
                    var length = _udp.Receive(buffer);
                    TraceRecorder.Instance.LogDatagram("recv", buffer, length);
                    HandleReceivedDatagram(buffer, length, willBeReused: true);
                }
            }
            catch (SocketException ex)
            {
                if (State != PhotonSocketState.Disconnecting && State != PhotonSocketState.Disconnected)
                {
                    SocketErrorCode = (int)ex.SocketErrorCode;
                    HandleException(StatusCode.ExceptionOnReceive);
                }
            }
            catch (Exception)
            {
                if (State != PhotonSocketState.Disconnecting && State != PhotonSocketState.Disconnected)
                    HandleException(StatusCode.ExceptionOnReceive);
            }
        }
    }

    // ---------------------------------------------------------------- relay path (M0)

    private void RelayConnectLoop()
    {
        if (!RelayConfig.RelayEnabled)
            return; // passive M0 mode: no relay connections at all

        var host = RelayConfig.Host;
        var port = RelayConfig.Port;
        while (State == PhotonSocketState.Connected)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(host, port);
                if (!connectTask.Wait(2000) || !client.Connected)
                    continue;
                var stream = client.GetStream();
                var hello = Envelope.Write(RelayOp.Hello, Envelope.FlagNone, NextRequestId(), ReadOnlySpan<byte>.Empty);
                stream.Write(Frame.Write(hello));
                stream.Flush();

                var header = new byte[Frame.HeaderSize];
                while (State == PhotonSocketState.Connected && ReadFull(stream, header))
                {
                    int frameLength = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (frameLength < Frame.HeaderSize || frameLength > Frame.MaxPayload + Frame.HeaderSize)
                        break;
                    var payload = new byte[frameLength - Frame.HeaderSize];
                    if (!ReadFull(stream, payload))
                        break;
                    // M0: relay traffic is observed only; game datagrams stay on the UDP path.
                    if (Envelope.TryRead(payload, out var envelope) && envelope.Op == RelayOp.Kick)
                        break;
                }
            }
            catch
            {
                // relay unreachable: retry until the peer disconnects
            }

            Thread.Sleep(2000);
        }
    }

    private void ForwardToRelay(byte[] data, int length)
    {
        // M0: no forwarding yet (the UDP fallback carries traffic). The envelope/echo path is
        // covered by PeakRelay.Tools.TestClient against PeakRelay.Server.
    }

    private ushort NextRequestId()
    {
        int value = Interlocked.Increment(ref _requestId);
        return (ushort)value;
    }

    internal static bool ReadFull(NetworkStream stream, Span<byte> target)
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
}
