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
///
/// Transport contract (docs/protocol-notes.md): the relay transport is TCP, which already
/// guarantees ordered delivery. The relay therefore ACKs client commands (the client's own
/// reliability runs on those ACKs) but does NOT retransmit its own sends — a lost frame
/// means a dead connection, which the liveness timeout below catches like any other.
/// </summary>
public sealed class Session : IDisposable
{
    private const int MaxFrameSize = Frame.HeaderSize + Frame.MaxPayload;

    /// <summary>Silence (no frame in either direction) longer than this drops the session.</summary>
    private static readonly TimeSpan LivenessTimeout = TimeSpan.FromSeconds(60);

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
    private long _lastTrafficUtcTicks = DateTime.UtcNow.Ticks;
    private volatile bool _handshaked;
    private volatile bool _kickRequested;

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

    private void Touch() => Volatile.Write(ref _lastTrafficUtcTicks, DateTime.UtcNow.Ticks);

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
                Touch();
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
        try
        {
            var lastFragmentSweep = DateTime.UtcNow;
            while (_disposed == 0)
            {
                _writeSignal.Wait(25); // poll interval doubles as cross-session delivery latency bound

                while (_outbound.TryDequeue(out var frame))
                {
                    if (!WriteAll(frame))
                        return;
                    Touch();
                }

                if (_kickRequested)
                    return; // kick frame is written (drain above); close via Dispose

                while (_peer.LbOutbound.TryDequeue(out var message))
                {
                    var (bytes, isEvent) = message;
                    var msgType = isEvent ? LbConnection.MsgType_Event : LbConnection.MsgType_OperationResponse;
                    var wrapped = _lb.Wrap(bytes, msgType, encrypted: false);
                    Diagnostics.Dump($"S->C lb-{(isEvent ? "event" : "resp")}", wrapped, wrapped.Length);
                    if (!WriteAll(Frame.Write(Envelope.Write(RelayOp.Data, 0, 0,
                            _enet.BuildReliableDatagram(0, wrapped)))))
                        return;
                    Touch();
                }

                // reclaim fragment sets abandoned mid-reassembly (sender died); reassembly
                // itself is reliable — this only bounds memory, never drops live sets
                if ((DateTime.UtcNow - lastFragmentSweep).TotalSeconds > 30)
                {
                    lastFragmentSweep = DateTime.UtcNow;
                    _enet.SweepStaleFragments();
                }

                if (!_handshaked &&
                    DateTime.UtcNow.Ticks - Volatile.Read(ref _lastTrafficUtcTicks) > TimeSpan.FromSeconds(10).Ticks)
                {
                    Console.Error.WriteLine("[relay-session] no Hello within 10 s; dropping session");
                    return;
                }

                var idle = DateTime.UtcNow.Ticks - Volatile.Read(ref _lastTrafficUtcTicks);
                if (idle > LivenessTimeout.Ticks)
                {
                    Console.Error.WriteLine("[relay-session] liveness timeout; dropping session");
                    return;
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
                HandleHello(envelope);
                return;
            case RelayOp.Bye:
                Dispose();
                return;
            case RelayOp.Data:
                if (!_handshaked)
                    return; // Data before Hello: not ours to answer
                HandleDatagram(envelope.Payload, envelope.RequestId);
                return;
        }
    }

    private void HandleHello(Envelope.EnvelopeReader envelope)
    {
        var wireVersion = Envelope.HelloWireVersion(envelope.Payload);
        if (wireVersion != Frame.Version)
        {
            // incompatible client: say why, then close. Older relays just closed silently.
            var reason = $"relay wire v{Frame.Version}, client speaks v{wireVersion} — update PeakRelay";
            Post(Frame.Write(Envelope.Write(RelayOp.Kick, 0, envelope.RequestId,
                System.Text.Encoding.UTF8.GetBytes(reason))));
            Console.Error.WriteLine($"[relay-session] wire version mismatch (client v{wireVersion}); kicked");
            _kickRequested = true; // writer flushes the Kick frame, then closes the session
            return;
        }
        _handshaked = true;
        Post(Frame.Write(Envelope.Write(RelayOp.Welcome, 0, envelope.RequestId, Array.Empty<byte>())));
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

            // key-exchange replies go out immediately (encrypted flag false)
            foreach (var lbBytes in _lb.LbOutbound)
            {
                Diagnostics.Dump("S->C lb-internal", lbBytes, lbBytes.Length);
                Post(Frame.Write(Envelope.Write(RelayOp.Data, 0, requestId,
                    _enet.BuildReliableDatagram(0, lbBytes))));
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
