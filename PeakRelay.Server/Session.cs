using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using PeakRelay.Protocol;

namespace PeakRelay.Server;

/// <summary>
/// One connected client: a TCP connection with a reader thread and a writer thread, plus an
/// outbound frame queue between them. On disconnect, cleanup removes the session from its room.
/// </summary>
public sealed class Session : IDisposable
{
    private const int MaxFrameSize = Frame.HeaderSize + Frame.MaxPayload;

    private readonly Socket _socket;
    private readonly ConcurrentQueue<byte[]> _outbound = new();
    private readonly Thread _reader;
    private readonly Thread _writer;
    private readonly Room _room;
    private readonly SemaphoreSlim _writeSignal = new(0);
    private int _disposed;

    public Session(Socket socket, Room room)
    {
        _socket = socket;
        _room = room;
        _room.Add(this);
        _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "relay-session-reader" };
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "relay-session-writer" };
        _reader.Start();
        _writer.Start();
    }

    public Room Room => _room;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _writeSignal.Release();
        try { _socket.Shutdown(SocketShutdown.Both); } catch { /* already gone */ }
        _socket.Close();
        _room.Remove(this);
    }

    /// <summary>Queue one frame for delivery to this client. Safe from any thread.</summary>
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
        catch
        {
            // socket died or peer misbehaved: drop the session
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
            while (_disposed == 0)
            {
                _writeSignal.Wait(500);
                while (_outbound.TryDequeue(out var frame))
                {
                    var buffer = frame;
                    int total = 0;
                    while (total < buffer.Length)
                    {
                        int sent = _socket.Send(buffer[total..]);
                        if (sent <= 0)
                            return;
                        total += sent;
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

    /// <summary>M0 behavior: echo Data envelopes to every other member of the room.</summary>
    private void HandleFrame(byte[] payload)
    {
        if (!Envelope.TryRead(payload, out var envelope))
            return;
        if (envelope.Op != RelayOp.Data)
            return;
        var outboundEnvelope = Envelope.Write(RelayOp.Data, Envelope.FlagNone, envelope.RequestId, envelope.Payload);
        var frame = Frame.Write(outboundEnvelope);
        foreach (var member in _room.MembersExcluding(this))
            member.Post(frame);
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
