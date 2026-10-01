using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using PeakRelay.Protocol;

namespace PeakRelay.Server;

/// <summary>
/// ENET-layer bridge carrying the LB role for one session (the dispatcher flips Master→Game
/// when the client re-authenticates with a token, i.e. on its game-server connection).
///
/// Wire semantics live in <see cref="EnetWire"/> — one shared, tested implementation instead
/// of a per-consumer re-derivation of the decompiled Photon3 layouts.
///
/// Reliability contract: the relay transport is TCP, which already guarantees ordered
/// delivery, so the relay does NOT retransmit — it only ACKs (to clear the client's own
/// in-flight queue). The server-side ENET sequence counters exist because Photon clients
/// dispatch reliable commands per channel and expect each stream contiguous from 1.
/// </summary>
public sealed class EnetPeer
{
    public const int DefaultMtu = 1200;

    /// <summary>Fragments claiming more than this total length are dropped (untrusted wire data).</summary>
    public const int MaxReassembledLength = 1024 * 1024;

    /// <summary>Fragments claiming more than this many parts are dropped (untrusted wire data).</summary>
    public const int MaxFragmentCount = 256;

    public RelayServerRole Role { get; set; } = RelayServerRole.Master;

    public int PeerId { get; set; } = -1;
    public bool HandshakeComplete { get; private set; }

    /// <summary>Observed client MTU (Connect payload offset +2); diagnostics only.</summary>
    public int ClientMtu { get; private set; } = DefaultMtu;

    private readonly object _sync = new();

    /// <summary>Per-channel reliable sequence counters (server → client streams).</summary>
    private readonly Dictionary<byte, int> _outgoingSeq = new();

    /// <summary>Highest reliable sequence number the client has ACKed, per channel.</summary>
    private readonly Dictionary<byte, int> _clientAckedSeq = new();

    /// <summary>Highest reliable sequence number the client ACKed on a channel.</summary>
    public int LastAckedSeq(byte channel)
    {
        lock (_sync)
            return _clientAckedSeq.TryGetValue(channel, out var value) ? value : 0;
    }

    private int NextSeq(byte channel)
    {
        lock (_sync)
        {
            _outgoingSeq.TryGetValue(channel, out var current);
            current = EnetWire.NextSeq(channel, current);
            _outgoingSeq[channel] = current;
            return current;
        }
    }

    /// <summary>Next outgoing reliable sequence number for a channel (server-side).</summary>
    public int NextOutgoingSeq(byte channel) => NextSeq(channel);

    /// <summary>Payloads of delivered Send commands, in order (op-level, ENET stripped).</summary>
    public readonly Queue<byte[]> IncomingPayloads = new();

    // ---- fragment reassembly (type 8) ----
    // PEAK raises events above the MTU (PUN Instantiate carries the full payload hashtable);
    // Photon splits those into SendFragment commands. Without reassembly the relay ACKs and
    // silently DROPS every oversized event — a late joiner then never receives the
    // avatars/objects the others already created. Sizes are capped: fragment headers are
    // untrusted wire data and must not drive allocations.
    private sealed class FragmentSet
    {
        public required byte[][] Parts;
        public required int[] Offsets;
        public required bool[] Received;
        public int Remaining;
        public long LastProgressMs;
    }

    private readonly Dictionary<(byte Channel, int StartSeq), FragmentSet> _fragments = new();

    private readonly List<byte[]> _outgoingDatagrams = new();
    private int _currentDatagramSentTime;
    private int _clientChallenge;

    public void OnDatagram(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < EnetWire.DatagramHeaderSize)
            return;
        _currentDatagramSentTime = BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(4));
        _clientChallenge = BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(8)); // echo required (client validates)
        if (!EnetWire.TrySplit(datagram, out var commands))
            return;

        foreach (var command in commands)
        {
            switch (command.Type)
            {
                case EnetWire.CmdConnect:
                    if ((command.Flags & 1) != 0)
                        QueueAck(command.Channel, command.ReliableSeq); // stop the client resending its Connect
                    HandleConnect(command.Payload);
                    break;
                case EnetWire.CmdVerifyConnect:
                    break;
                case EnetWire.CmdPing:
                    if ((command.Flags & 1) != 0)
                        QueueAck(command.Channel, command.ReliableSeq);
                    break;
                case EnetWire.CmdDisconnect:
                    HandshakeComplete = false;
                    break;
                case EnetWire.CmdAck: // ackReceivedReliableSeq @ +12 (EnetWire.AckSize total)
                    if (command.Payload.Length >= 4)
                        TrackAck(command.Channel, BinaryPrimitives.ReadInt32BigEndian(command.Payload));
                    break;
                case EnetWire.CmdSendReliable:
                    IncomingPayloads.Enqueue(command.Payload);
                    QueueAck(command.Channel, command.ReliableSeq);
                    break;
                case EnetWire.CmdSendUnreliable:
                    IncomingPayloads.Enqueue(command.Payload); // unreliableSeq already stripped by EnetWire
                    break;
                case EnetWire.CmdServerTime:
                    if ((command.Flags & 1) != 0)
                        QueueAck(command.Channel, command.ReliableSeq); // client syncs clock from our ACK (EnetPeer.cs:1286)
                    break;
                case EnetWire.CmdSendFragment:
                    HandleFragment(command);
                    break;
            }
        }
    }

    /// <summary>
    /// Reassembles fragmented reliable commands. Fragment headers (after the 20-byte prefix
    /// EnetWire already parsed: startSeq, fragmentCount, fragmentNumber, totalLength,
    /// fragmentOffset) are untrusted: sizes are capped and sets are swept when stale.
    /// Fragments arrive reliably, so a live set always completes unless the session dies;
    /// the sweep only reclaims memory from abandoned sets.
    /// </summary>
    private void HandleFragment(in EnetWire.Command command)
    {
        QueueAck(command.Channel, command.ReliableSeq);

        int startSeq = command.StartSeq;
        int fragmentCount = command.FragmentCount;
        int fragmentNumber = command.FragmentNumber;
        int totalLength = command.TotalLength;
        int fragmentOffset = command.FragmentOffset;
        var part = command.Payload;

        // hard caps: a hostile header must never drive a big allocation
        if (fragmentCount is <= 0 or > MaxFragmentCount ||
            totalLength is < 0 or > MaxReassembledLength ||
            fragmentNumber >= fragmentCount ||
            fragmentOffset < 0 || fragmentOffset + part.Length > totalLength)
            return;

        var key = (command.Channel, startSeq);
        byte[]? whole = null;
        lock (_sync)
        {
            if (!_fragments.TryGetValue(key, out var set))
            {
                set = new FragmentSet
                {
                    Parts = new byte[fragmentCount][],
                    Offsets = new int[fragmentCount],
                    Received = new bool[fragmentCount],
                    Remaining = fragmentCount,
                    LastProgressMs = Environment.TickCount64,
                };
                _fragments[key] = set;
            }
            if (fragmentNumber < set.Parts.Length && !set.Received[fragmentNumber])
            {
                set.Received[fragmentNumber] = true;
                set.Parts[fragmentNumber] = part;
                set.Offsets[fragmentNumber] = fragmentOffset;
                set.Remaining--;
                set.LastProgressMs = Environment.TickCount64;
            }
            else
            {
                return; // duplicate or out-of-range fragment number
            }

            if (set.Remaining != 0)
                return;

            _fragments.Remove(key);
            whole = new byte[totalLength];
            for (int i = 0; i < set.Parts.Length; i++)
                set.Parts[i]!.CopyTo(whole, set.Offsets[i]);
        }

        IncomingPayloads.Enqueue(whole);
    }

    /// <summary>Drops fragment sets with no progress for over a minute (abandoned by a dead sender).</summary>
    public void SweepStaleFragments()
    {
        lock (_sync)
        {
            var stale = new List<(byte Channel, int StartSeq)>();
            foreach (var (key, set) in _fragments)
            {
                if (Environment.TickCount64 - set.LastProgressMs > 60_000)
                    stale.Add(key);
            }
            foreach (var key in stale)
                _fragments.Remove(key);
        }
    }

    private void HandleConnect(ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= 6)
        {
            var mtu = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2, 2));
            if (mtu >= 576 && mtu <= 65000)
                ClientMtu = mtu;
        }
        if (PeerId < 0)
            PeerId = 1;
        var verify = new byte[44];
        verify[0] = EnetWire.CmdVerifyConnect;
        verify[1] = 255; // control channel
        verify[2] = 1;
        verify[3] = 0;
        BinaryPrimitives.WriteInt32BigEndian(verify.AsSpan(4), 44);
        BinaryPrimitives.WriteInt32BigEndian(verify.AsSpan(8), NextSeq(255));
        BinaryPrimitives.WriteInt16BigEndian(verify.AsSpan(12), (short)PeerId);
        _outgoingDatagrams.Add(EnetWire.WrapDatagram(PeerId, flag: 0, _currentDatagramSentTime, _clientChallenge, verify));
        HandshakeComplete = true;
    }

    /// <summary>
    /// ACK: sentTime echoes the client's command sentTime (RTT basis, EnetPeer.cs:1257) and
    /// reliableSeq is 0 — ACKs are executed immediately client-side and never consume
    /// sequence numbers (NCommand.CreateAck writes 0). The client derives server time from
    /// this echo, so it must ride the ACK even though we need no retransmit machinery.
    /// </summary>
    private void QueueAck(byte channel, int reliableSeq)
    {
        var ack = new byte[EnetWire.AckSize];
        ack[0] = EnetWire.CmdAck;
        ack[1] = channel;
        ack[2] = 1;
        BinaryPrimitives.WriteInt32BigEndian(ack.AsSpan(4), EnetWire.AckSize);
        BinaryPrimitives.WriteInt32BigEndian(ack.AsSpan(12), reliableSeq);
        BinaryPrimitives.WriteInt32BigEndian(ack.AsSpan(16), _currentDatagramSentTime);
        _outgoingDatagrams.Add(EnetWire.WrapDatagram(PeerId, flag: 0, _currentDatagramSentTime, _clientChallenge, ack));
    }

    private void TrackAck(byte channel, int ackedSeq)
    {
        lock (_sync)
        {
            _clientAckedSeq.TryGetValue(channel, out var current);
            if (ackedSeq > current)
                _clientAckedSeq[channel] = ackedSeq;
        }
    }

    /// <summary>Build a SendReliable datagram carrying one P16 message payload.</summary>
    public byte[] BuildReliableDatagram(byte channel, byte[] payload)
    {
        var cmd = new byte[EnetWire.CommandHeaderSize + payload.Length];
        cmd[0] = EnetWire.CmdSendReliable;
        cmd[1] = channel;
        cmd[2] = 1;
        cmd[3] = 4; // reserved byte, matches the client's own writer
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(4), cmd.Length);
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(8), NextSeq(channel));
        payload.CopyTo(cmd, EnetWire.CommandHeaderSize);
        return EnetWire.WrapDatagram(PeerId, flag: 0, Environment.TickCount, _clientChallenge, cmd);
    }

    /// <summary>Disconnect command with reason byte.</summary>
    public byte[] BuildDisconnect(byte reason)
    {
        var cmd = new byte[EnetWire.CommandHeaderSize];
        cmd[0] = EnetWire.CmdDisconnect;
        cmd[1] = 255;
        cmd[2] = 1;
        cmd[3] = reason;
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(4), EnetWire.CommandHeaderSize);
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(8), NextSeq(255));
        return EnetWire.WrapDatagram(PeerId, flag: 0, Environment.TickCount, _clientChallenge, cmd);
    }

    /// <summary>Datagrams produced by the last OnDatagram call (replies to the client).</summary>
    public IReadOnlyList<byte[]> PopOutgoing()
    {
        var copy = new List<byte[]>(_outgoingDatagrams);
        _outgoingDatagrams.Clear();
        return copy;
    }
}
