using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using PeakRelay.Protocol;

namespace PeakRelay.Server;

/// <summary>
/// ENET-layer bridge carrying the LB role for one session (the dispatcher flips Master→Game
/// when the client re-authenticates with a token, i.e. on its game-server connection).
/// </summary>
public interface RelayEnetRoleCarrier
{
    RelayServerRole Role { get; set; }
}

/// <summary>
/// ENET-layer state for one connected Photon client: datagram envelope parsing/building,
/// the Connect/VerifyConnect handshake, ACK generation (sentTime echo), ping/server-time
/// replies, and ordered delivery of reliable command payloads to the LB message layer.
///
/// Layouts follow the decompiled EnetPeer/NCommand (see docs/protocol-notes.md).
/// </summary>
public sealed class EnetPeer : RelayEnetRoleCarrier
{
    public const byte CmdAck = 1;
    public const byte CmdConnect = 2;
    public const byte CmdVerifyConnect = 3;
    public const byte CmdDisconnect = 4;
    public const byte CmdPing = 5;
    public const byte CmdSendReliable = 6;
    public const byte CmdSendUnreliable = 7;
    public const byte CmdSendFragment = 8;
    public const byte CmdServerTime = 12;

    private const int DatagramHeaderSize = 12;
    private const int CommandHeaderSize = 12;
    private const int VerifyConnectSize = 44;

    public RelayServerRole Role { get; set; } = RelayServerRole.Master;

    public int PeerId { get; set; } = -1;
    public bool HandshakeComplete { get; private set; }

    /// <summary>
    /// Per-channel reliable sequence counters. The client dispatches reliable commands
    /// per channel and expects each channel's stream to be contiguous starting at 1
    /// (EnetPeer.QueueIncomingCommand / channel dispatch). ACKs carry seq 0 and consume
    /// nothing (NCommand.CreateAck writes 0).
    /// </summary>
    private readonly Dictionary<byte, int> _outgoingSeq = new();

    private readonly object _seqSync = new();

    private int NextSeq(byte channel)
    {
        lock (_seqSync)
        {
            _outgoingSeq.TryGetValue(channel, out var current);
            // Photon sequence numbers are 15-bit and wrap at short.MaxValue (EnetChannel
            // dispatch compares shorts); a server that keeps counting past the wrap point
            // would make the client discard everything as "old".
            current = current >= short.MaxValue ? 1 : current + 1;
            _outgoingSeq[channel] = current;
            return current;
        }
    }

    /// <summary>Payloads of delivered Send commands, in order (op-level, ENET stripped).</summary>
    public readonly Queue<byte[]> IncomingPayloads = new();

    // ---- client ACK tracking (drives the server's S->C reliability) ----
    // The client ACKs every reliable command we send it (type 1, 20-byte header:
    // ackReceivedReliableSeq at +12). A channel's max ACKed seq tells the server exactly
    // what still needs retransmission. Accessed from the reader (ACKs arrive) and the
    // writer (retransmit pass) threads, so it locks.
    private readonly Dictionary<byte, int> _clientAckedSeq = new();

    /// <summary>Highest reliable sequence number the client has ACKed on a channel.</summary>
    public int LastAckedSeq(byte channel)
    {
        lock (_seqSync)
            return _clientAckedSeq.TryGetValue(channel, out var value) ? value : 0;
    }

    private void TrackAck(byte channel, int ackedSeq)
    {
        lock (_seqSync)
        {
            _clientAckedSeq.TryGetValue(channel, out var current);
            if (ackedSeq > current)
                _clientAckedSeq[channel] = ackedSeq;
        }
    }

    // ---- fragment reassembly (type 8) ----
    // PEAK raises events above the MTU (PUN Instantiate carries the full payload hashtable,
    // and serialize batches grow with player count); Photon splits those into SendFragment
    // commands. Without reassembly the relay ACKs and silently DROPS every oversized event —
    // a late joiner then never receives the avatars/objects the others already created.
    private sealed class FragmentSet
    {
        public required byte[][] Parts;
        public required int[] Offsets;
        public required bool[] Received;
        public int Remaining;
    }

    private readonly Dictionary<(byte Channel, int StartSeq), FragmentSet> _fragments = new();

    private readonly List<byte[]> _outgoingDatagrams = new();
    private int _currentDatagramSentTime;
    private int _clientChallenge;

    public void OnDatagram(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < DatagramHeaderSize)
            return;
        short peerId = BinaryPrimitives.ReadInt16BigEndian(datagram);
        byte flag = datagram[2];
        byte commandCount = datagram[3];
        _currentDatagramSentTime = BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(4));
        _clientChallenge = BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(8)); // echo required (client validates)
        if (flag == 1)
            return; // encrypted datagrams: not supported
        if (flag == 204)
            return; // CRC variant: not enabled by PUN defaults

        int offset = DatagramHeaderSize;
        for (int i = 0; i < commandCount && offset + CommandHeaderSize <= datagram.Length; i++)
        {
            byte type = datagram[offset];
            byte channel = datagram[offset + 1];
            byte flags = datagram[offset + 2];
            int size = BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(offset + 4));
            int reliableSeq = BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(offset + 8));
            if (size < CommandHeaderSize || offset + size > datagram.Length)
                return; // malformed; drop datagram
            var payload = datagram.Slice(offset + CommandHeaderSize, size - CommandHeaderSize).ToArray();

            switch (type)
            {
                case CmdConnect:
                    if ((flags & 1) != 0)
                        QueueAck(channel, reliableSeq); // stop the client resending its Connect
                    HandleConnect();
                    break;
                case CmdVerifyConnect:
                    break;
                case CmdPing:
                    if ((flags & 1) != 0)
                        QueueAck(channel, reliableSeq);
                    break;
                case CmdDisconnect:
                    HandshakeComplete = false;
                    break;
                case CmdAck: // 20-byte header: ackReceivedReliableSeq @ +12, ackSentTime @ +16
                    if (size >= 20)
                        TrackAck(channel, BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(offset + 12)));
                    break;
                case CmdSendReliable:
                    IncomingPayloads.Enqueue(payload);
                    QueueAck(channel, reliableSeq);
                    break;
                case CmdSendUnreliable:
                    // 16-byte header: common 12 + int32 unreliableSeq; payload follows
                    if (size >= 16)
                        IncomingPayloads.Enqueue(datagram.Slice(offset + 16, size - 16).ToArray());
                    break;
                case CmdServerTime:
                    if ((flags & 1) != 0)
                        QueueAck(channel, reliableSeq); // client syncs clock from our ACK (EnetPeer.cs:1286)
                    break;
                case CmdSendFragment:
                    HandleFragment(datagram, offset, size, channel, reliableSeq);
                    break;
            }
            offset += size;
        }
    }

    /// <summary>
    /// Reassembles fragmented reliable commands (NCommand type 8, 32-byte header:
    /// common 12 + startSeq, fragmentCount, fragmentNumber, totalLength, fragmentOffset).
    /// Fragments arrive reliably (each is ACKed), so a set completes as long as the
    /// connection lives; the reassembled payload is enqueued as ONE reliable message with
    /// a fresh contiguous server-side sequence so per-channel client ordering holds.
    /// </summary>
    private void HandleFragment(ReadOnlySpan<byte> datagram, int offset, int size, byte channel, int reliableSeq)
    {
        QueueAck(channel, reliableSeq);
        if (size < 32)
            return; // malformed

        int startSeq = BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(offset + 12));
        int fragmentCount = BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(offset + 16));
        int fragmentNumber = BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(offset + 20));
        int totalLength = BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(offset + 24));
        int fragmentOffset = BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(offset + 28));
        var part = datagram.Slice(offset + 32, size - 32).ToArray();

        if (fragmentCount <= 0 || fragmentNumber >= fragmentCount ||
            fragmentOffset < 0 || fragmentOffset + part.Length > totalLength)
            return; // bad sizes — drop the fragment

        var key = (channel, startSeq);
        if (!_fragments.TryGetValue(key, out var set))
        {
            set = new FragmentSet
            {
                Parts = new byte[fragmentCount][],
                Offsets = new int[fragmentCount],
                Received = new bool[fragmentCount],
                Remaining = fragmentCount,
            };
            _fragments[key] = set;
        }
        if (fragmentNumber >= set.Parts.Length)
            return;

        if (!set.Received[fragmentNumber])
        {
            set.Received[fragmentNumber] = true;
            set.Parts[fragmentNumber] = part;
            set.Offsets[fragmentNumber] = fragmentOffset;
            set.Remaining--;
        }

        if (set.Remaining != 0)
            return;

        var whole = new byte[totalLength];
        for (int i = 0; i < set.Parts.Length; i++)
            set.Parts[i]!.CopyTo(whole, set.Offsets[i]);
        _fragments.Remove(key);
        IncomingPayloads.Enqueue(whole);
    }

    /// <summary>Ping reply: 12-byte header, no payload — keeps the client's ACK/clock loop alive.</summary>
    public byte[] BuildPingReplyDatagram()
    {
        var cmd = new byte[CommandHeaderSize];
        cmd[0] = CmdPing;
        cmd[1] = 255; // control channel
        cmd[2] = 0;
        cmd[3] = 0;
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(4), CommandHeaderSize);
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(8), 0);
        return WrapDatagram(cmd);
    }

    private void HandleConnect()
    {
        if (PeerId < 0)
            PeerId = 1;
        var verify = new byte[VerifyConnectSize];
        verify[0] = CmdVerifyConnect;
        verify[1] = 255; // control channel
        verify[2] = 1;
        verify[3] = 0;
        BinaryPrimitives.WriteInt32BigEndian(verify.AsSpan(4), VerifyConnectSize);
        BinaryPrimitives.WriteInt32BigEndian(verify.AsSpan(8), NextSeq(255));
        BinaryPrimitives.WriteInt16BigEndian(verify.AsSpan(12), (short)PeerId);
        _outgoingDatagrams.Add(WrapDatagram(verify));
        HandshakeComplete = true;
    }

    /// <summary>
    /// ACK: sentTime echoes the client's command sentTime (RTT basis, EnetPeer.cs:1257) and
    /// reliableSeq is 0 — ACKs are executed immediately client-side and never consume
    /// sequence numbers (NCommand.CreateAck writes 0).
    /// </summary>
    private void QueueAck(byte channel, int reliableSeq)
    {
        var ack = new byte[20];
        ack[0] = CmdAck;
        ack[1] = channel;
        ack[2] = 1;
        ack[3] = 0;
        BinaryPrimitives.WriteInt32BigEndian(ack.AsSpan(4), 20);
        BinaryPrimitives.WriteInt32BigEndian(ack.AsSpan(8), 0);
        BinaryPrimitives.WriteInt32BigEndian(ack.AsSpan(12), reliableSeq);
        BinaryPrimitives.WriteInt32BigEndian(ack.AsSpan(16), _currentDatagramSentTime);
        _outgoingDatagrams.Add(WrapDatagram(ack));
    }

    /// <summary>Next outgoing reliable sequence number for a channel (server-side).</summary>
    public int NextOutgoingSeq(byte channel) => NextSeq(channel);

    /// <summary>Build a SendReliable datagram carrying one P16 message payload.</summary>
    public byte[] BuildReliableDatagram(byte channel, byte[] payload) =>
        BuildReliableDatagram(channel, payload, NextSeq(channel));

    /// <summary>Build a SendReliable datagram with an explicit sequence number (queued sends).</summary>
    public byte[] BuildReliableDatagram(byte channel, byte[] payload, int seq)
    {
        var cmd = new byte[CommandHeaderSize + payload.Length];
        cmd[0] = CmdSendReliable;
        cmd[1] = channel;
        cmd[2] = 1;
        cmd[3] = 4; // reserved byte, matches the client's own writer
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(4), cmd.Length);
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(8), seq);
        payload.CopyTo(cmd, CommandHeaderSize);
        return WrapDatagram(cmd);
    }

    /// <summary>Disconnect command with reason byte.</summary>
    public byte[] BuildDisconnect(byte reason)
    {
        var cmd = new byte[CommandHeaderSize];
        cmd[0] = CmdDisconnect;
        cmd[1] = 255;
        cmd[2] = 1;
        cmd[3] = reason;
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(4), CommandHeaderSize);
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(8), NextSeq(255));
        return WrapDatagram(cmd);
    }

    private byte[] WrapDatagram(byte[] command)
    {
        var datagram = new byte[DatagramHeaderSize + command.Length];
        BinaryPrimitives.WriteInt16BigEndian(datagram, (short)PeerId);
        datagram[2] = 0; // plain
        datagram[3] = 1; // one command
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(4), Environment.TickCount);
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(8), _clientChallenge);
        command.CopyTo(datagram, DatagramHeaderSize);
        return datagram;
    }

    /// <summary>Datagrams produced by the last OnDatagram call (replies to the client).</summary>
    public IReadOnlyList<byte[]> PopOutgoing()
    {
        var copy = new List<byte[]>(_outgoingDatagrams);
        _outgoingDatagrams.Clear();
        return copy;
    }
}
