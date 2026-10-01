using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using PeakRelay.Protocol;

namespace PeakRelay.Server;

/// <summary>
/// One connected Photon client. Reader thread: relay frames → ENET datagrams → decrypted
/// LB messages → dispatcher ops. Writer thread: ENET replies (ACKs/VerifyConnect) plus a
/// periodic drain of the LB outbound queue — the drain is on the writer (not inline with op
/// dispatch) so events broadcast by OTHER sessions' readers are delivered without waiting
/// for this client to send something first.
/// </summary>
public sealed class Session : IDisposable
{
    private const int MaxFrameSize = Frame.HeaderSize + Frame.MaxPayload;

    private sealed record OutgoingMessage(byte[] Datagram, byte Channel, int Seq, DateTime CreatedAt);

    private const int RetransmitIntervalMs = 250;
    private const int ReliableTimeoutSeconds = 30;

    private readonly Socket _socket;
    private readonly ConcurrentQueue<byte[]> _outbound = new();
    private readonly Thread _reader;
    private readonly Thread _writer;
    private readonly LbDispatcher _dispatcher;
    private readonly SemaphoreSlim _writeSignal = new(0);
    private readonly EnetPeer _enet = new();
    private readonly LbConnection _lb;
    private readonly LbPeer _peer;
    private int _disposed;

    // ---- S->C reliable delivery ----
    // Events/responses go out as ENET reliable commands the client ACKs. Unlike real ENET
    // the server retransmits: if a frame is lost (or the client's ACK is), Photon's
    // reliable stream would otherwise stall — the client waits for seq N forever.
    // "Delivered" is detected via the client's ACKs (EnetPeer.LastAckedSeq); anything
    // unACKed after ReliableTimeoutSeconds kills the session (the client reports a
    // timeout, like against a real Photon server).
    private readonly object _reliableSync = new();
    private readonly List<OutgoingMessage> _reliableInFlight = new();

    private void EnqueueReliable(byte[] wrapped, byte channel)
    {
        int seq;
        byte[] datagram;
        lock (_reliableSync)
        {
            seq = _enet.NextOutgoingSeq(channel);
            datagram = _enet.BuildReliableDatagram(channel, wrapped, seq);
            _reliableInFlight.Add(new OutgoingMessage(datagram, channel, seq, DateTime.UtcNow));
        }
        _writeSignal.Release();
    }

    public Session(Socket socket, LbDispatcher dispatcher)
    {
        _socket = socket;
        _dispatcher = dispatcher;
        _lb = new LbConnection();
        _peer = new LbPeer { Enet = _enet };
        _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "relay-session-reader" };
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "relay-session-writer" };
        _reader.Start();
        _writer.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _writeSignal.Release();
        try { _socket.Shutdown(SocketShutdown.Both); } catch { /* already gone */ }
        _socket.Close();
        if (_peer.Room != null)
            _dispatcher.HandleSilentDisconnect(_peer);
    }

    public void Post(byte[] frame)
    {
        _outbound.Enqueue(frame);
        _writeSignal.Release();
    }

    private void ReaderLoop()
    {
        var header = new byte[Frame.HeaderSize];
        try
        {
            while (_disposed == 0)
            {
                if (!TryReadFull(_socket, header))
                    break;
                int frameLength = BinaryPrimitives.ReadInt32LittleEndian(header);
                int version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                if (frameLength < Frame.HeaderSize || frameLength > MaxFrameSize || version != Frame.Version)
                    throw new InvalidOperationException($"Bad frame header len={frameLength} v={version}");
                var payload = new byte[frameLength - Frame.HeaderSize];
                if (!TryReadFull(_socket, payload))
                    break;
                HandleFrame(payload);
            }
        }
        catch (Exception ex)
        {
            // surface reader failures (bad frames, parse throws) instead of dying silently
            var parts = new List<string>();
            var cur = (Exception?)ex;
            while (cur != null)
            {
                parts.Add($"{cur.GetType().FullName}: {cur.Message}");
                cur = cur.InnerException;
            }
            Console.Error.WriteLine($"[relay-reader] {string.Join(" -> ", parts)}");
        }
        finally
        {
            Dispose();
        }
    }

    private void WriterLoop()
    {
        var lastRetransmitCheck = DateTime.UtcNow;
        try
        {
            while (_disposed == 0)
            {
                _writeSignal.Wait(25); // poll interval doubles as cross-session delivery latency bound

                // 1) ENET-level + inline op replies (queued by this session's reader)
                while (_outbound.TryDequeue(out var frame))
                {
                    if (!WriteAll(frame))
                        return;
                }

                // 2) LB events/responses queued from anywhere (own dispatch, room broadcasts)
                while (_peer.LbOutbound.TryDequeue(out var message))
                {
                    var (bytes, isEvent) = message;
                    var msgType = isEvent ? LbConnection.MsgType_Event : LbConnection.MsgType_OperationResponse;
                    var wrapped = _lb.Wrap(bytes, msgType, encrypted: false);
                    Diagnostics.Dump($"S->C lb-{(isEvent ? "event" : "resp")}", wrapped, wrapped.Length);
                    EnqueueReliable(wrapped, channel: 0);
                }

                // 3) retransmit still-unacknowledged reliable messages, drop the session
                //    when even retransmits stay unacknowledged
                var now = DateTime.UtcNow;
                if ((now - lastRetransmitCheck).TotalMilliseconds >= RetransmitIntervalMs)
                {
                    lastRetransmitCheck = now;
                    var resend = new List<byte[]>();
                    var timedOut = false;
                    lock (_reliableSync)
                    {
                        _reliableInFlight.RemoveAll(m => m.Seq <= _enet.LastAckedSeq(m.Channel));
                        foreach (var message in _reliableInFlight)
                        {
                            if ((now - message.CreatedAt).TotalSeconds > ReliableTimeoutSeconds)
                            {
                                timedOut = true;
                                break;
                            }
                            resend.Add(message.Datagram);
                        }
                    }
                    if (timedOut)
                    {
                        Console.Error.WriteLine("[relay-session] reliable S->C delivery timed out; dropping session");
                        return;
                    }
                    foreach (var datagram in resend)
                    {
                        if (!WriteAll(Frame.Write(Envelope.Write(RelayOp.Data, 0, 0, datagram))))
                            return;
                    }
                }
            }
        }
        catch
        {
            // socket died mid-write
        }
        finally
        {
            Dispose();
        }
    }

    private bool WriteAll(byte[] frame)
    {
        int total = 0;
        while (total < frame.Length)
        {
            int sent;
            try
            {
                sent = _socket.Send(frame[total..]);
            }
            catch
            {
                return false;
            }
            if (sent <= 0)
                return false;
            total += sent;
        }
        return true;
    }

    private void HandleFrame(byte[] framePayload)
    {
        if (!Envelope.TryRead(framePayload, out var envelope))
            return;

        switch (envelope.Op)
        {
            case RelayOp.Hello:
                Post(Frame.Write(Envelope.Write(RelayOp.Welcome, 0, envelope.RequestId, Array.Empty<byte>())));
                return;
            case RelayOp.Bye:
                Dispose();
                return;
            case RelayOp.Data:
                HandleDatagram(envelope.Payload, envelope.RequestId);
                return;
        }
    }

    private void HandleDatagram(ReadOnlySpan<byte> datagram, ushort requestId)
    {
        var copy = datagram.ToArray();
        Diagnostics.Dump("C->S datagram", copy, copy.Length);
        _enet.OnDatagram(datagram);

        foreach (var reply in _enet.PopOutgoing())
        {
            Diagnostics.Dump("S->C enet-reply", reply, reply.Length);
            Post(Frame.Write(Envelope.Write(RelayOp.Data, 0, requestId, reply)));
        }

        while (_enet.IncomingPayloads.Count > 0)
        {
            var payload = _enet.IncomingPayloads.Dequeue();
            _lb.HandleClientPayload(payload);

            // key-exchange replies go out as reliable commands like everything else
            foreach (var lbBytes in _lb.LbOutbound)
            {
                Diagnostics.Dump("S->C lb-internal", lbBytes, lbBytes.Length);
                EnqueueReliable(lbBytes, channel: 0);
            }
            _lb.LbOutbound.Clear();

            while (_lb.PendingOps.Count > 0)
            {
                var (op, _) = _lb.PendingOps.Dequeue();
                try
                {
                    _dispatcher.Dispatch(_peer, op, _peer.Role);
                }
                catch (Exception ex)
                {
                    // one malformed op (e.g. an exotic payload) must not kill the session;
                    // the client sees no response for that op and recovers on its own
                    Console.Error.WriteLine($"relay: op {op.Op} dispatch failed: {ex.Message}");
                }
                // responses/events land on _peer.LbOutbound and are flushed by the writer loop
            }
        }
    }

    private static bool TryReadFull(Socket socket, Span<byte> target)
    {
        int total = 0;
        while (total < target.Length)
        {
            int read = socket.Receive(target[total..]);
            if (read == 0)
                return false;
            total += read;
        }
        return true;
    }
}
