using System;
using System.Buffers.Binary;
using System.Text;
using PeakRelay.Protocol;
using PeakRelay.Server;
using Xunit;

namespace PeakRelay.Protocol.Tests;

/// <summary>
/// ENET-transport completeness, discovered from a real 3-player session: the relay used to
/// ACK-and-drop fragmented commands (type 8) and silently drop unreliable ones (type 7).
/// Fragments carry PEAK's oversized events (PUN Instantiate with full payload hashtables);
/// unreliable commands carry the serialize/movement stream. Dropping either desyncs late
/// joiners from everyone already in the room.
/// </summary>
public sealed class TransportReliabilityTests
{
    // ---- datagram/command builders matching the wire layouts in docs/protocol-notes.md ----

    private static byte[] Datagram(params byte[][] commands)
    {
        var body = new byte[commands.Sum(c => c.Length)];
        int at = 0;
        foreach (var command in commands)
        {
            command.CopyTo(body, at);
            at += command.Length;
        }
        var datagram = new byte[12 + body.Length];
        BinaryPrimitives.WriteInt16BigEndian(datagram, (short)1);
        datagram[3] = (byte)commands.Length;
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(4), 12345);
        body.CopyTo(datagram, 12);
        return datagram;
    }

    private static byte[] ReliableCommand(byte channel, int seq, byte[] payload)
    {
        var cmd = new byte[12 + payload.Length];
        cmd[0] = EnetPeer.CmdSendReliable;
        cmd[1] = channel;
        cmd[2] = 1;
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(4), cmd.Length);
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(8), seq);
        payload.CopyTo(cmd, 12);
        return cmd;
    }

    private static byte[] UnreliableCommand(byte channel, int reliableSeq, int unreliableSeq, byte[] payload)
    {
        var cmd = new byte[16 + payload.Length];
        cmd[0] = EnetPeer.CmdSendUnreliable;
        cmd[1] = channel;
        cmd[2] = 0;
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(4), cmd.Length);
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(8), reliableSeq);
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(12), unreliableSeq);
        payload.CopyTo(cmd, 16);
        return cmd;
    }

    private static byte[] FragmentCommand(byte channel, int seq, int startSeq, int count, int number,
        int totalLength, int offset, byte[] payload)
    {
        var cmd = new byte[32 + payload.Length];
        cmd[0] = EnetPeer.CmdSendFragment;
        cmd[1] = channel;
        cmd[2] = 1;
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(4), cmd.Length);
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(8), seq);
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(12), startSeq);
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(16), count);
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(20), number);
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(24), totalLength);
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(28), offset);
        payload.CopyTo(cmd, 32);
        return cmd;
    }

    private static byte[] AckCommand(byte channel, int ackedSeq, int sentTime)
    {
        var cmd = new byte[20];
        cmd[0] = EnetPeer.CmdAck;
        cmd[1] = channel;
        cmd[2] = 1;
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(4), 20);
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(8), 0);
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(12), ackedSeq);
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(16), sentTime);
        return cmd;
    }

    [Fact]
    public void Fragmented_command_is_reassembled_in_order()
    {
        var peer = new EnetPeer();
        var whole = Encoding.UTF8.GetBytes(new string('x', 3000)); // > MTU
        var first = whole[..2000];
        var second = whole[2000..];

        peer.OnDatagram(Datagram(
            FragmentCommand(0, seq: 1, startSeq: 1, count: 2, number: 0, whole.Length, offset: 0, first),
            FragmentCommand(0, seq: 2, startSeq: 1, count: 2, number: 1, whole.Length, offset: 2000, second)));

        Assert.Single(peer.IncomingPayloads);
        Assert.Equal(whole, peer.IncomingPayloads.Dequeue());
    }

    [Fact]
    public void Fragments_arriving_out_of_order_still_reassemble()
    {
        var peer = new EnetPeer();
        var whole = new byte[1500];
        Array.Fill(whole, (byte)7);
        var second = whole[1000..];

        // tail fragment first: nothing may be delivered yet
        peer.OnDatagram(Datagram(FragmentCommand(0, 2, 1, 2, 1, whole.Length, 1000, second)));
        Assert.Empty(peer.IncomingPayloads);

        peer.OnDatagram(Datagram(FragmentCommand(0, 1, 1, 2, 0, whole.Length, 0, whole[..1000])));
        Assert.Single(peer.IncomingPayloads);
        Assert.Equal(whole, peer.IncomingPayloads.Dequeue());
    }

    [Fact]
    public void Fragments_of_different_sets_do_not_mix()
    {
        var peer = new EnetPeer();
        var a = new byte[100];
        var b = new byte[100];
        Array.Fill(a, (byte)1);
        Array.Fill(b, (byte)2);

        peer.OnDatagram(Datagram(
            FragmentCommand(0, 1, 10, 2, 0, 200, 0, a),
            FragmentCommand(0, 2, 20, 2, 0, 200, 0, b)));
        peer.OnDatagram(Datagram(
            FragmentCommand(0, 3, 10, 2, 1, 200, 100, a),
            FragmentCommand(0, 4, 20, 2, 1, 200, 100, b)));

        Assert.Equal(2, peer.IncomingPayloads.Count);
        Assert.All(peer.IncomingPayloads, p => Assert.Equal(200, p.Length));
    }

    [Fact]
    public void Unreliable_commands_are_forwarded_not_dropped()
    {
        var peer = new EnetPeer();
        var payload = new byte[] { 1, 2, 3, 4 };

        peer.OnDatagram(Datagram(UnreliableCommand(0, reliableSeq: 5, unreliableSeq: 9, payload)));

        Assert.Single(peer.IncomingPayloads);
        Assert.Equal(payload, peer.IncomingPayloads.Dequeue());
    }

    [Fact]
    public void Reliable_commands_still_flow_and_ack()
    {
        var peer = new EnetPeer();
        var payload = new byte[] { 9, 8, 7 };

        peer.OnDatagram(Datagram(ReliableCommand(0, 3, payload)));

        Assert.Single(peer.IncomingPayloads);
        Assert.Equal(payload, peer.IncomingPayloads.Dequeue());
        // every reliable command must be ACKed (client resends otherwise); outbound
        // entries are datagrams, so the command byte sits behind the 12-byte header
        Assert.Contains(peer.PopOutgoing(), d => d.Length > 12 && d[12] == EnetPeer.CmdAck);
    }

    [Fact]
    public void Client_acks_are_tracked_per_channel_and_monotonic()
    {
        var peer = new EnetPeer();
        Assert.Equal(0, peer.LastAckedSeq(0));

        peer.OnDatagram(Datagram(AckCommand(0, ackedSeq: 7, sentTime: 100)));
        Assert.Equal(7, peer.LastAckedSeq(0));
        Assert.Equal(0, peer.LastAckedSeq(1)); // channels are independent

        peer.OnDatagram(Datagram(AckCommand(0, ackedSeq: 5, sentTime: 101)));
        Assert.Equal(7, peer.LastAckedSeq(0)); // never moves backwards

        peer.OnDatagram(Datagram(AckCommand(0, ackedSeq: 12, sentTime: 102)));
        Assert.Equal(12, peer.LastAckedSeq(0));
    }
}
