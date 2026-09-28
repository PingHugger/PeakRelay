using System;
using System.Buffers.Binary;
using PeakRelay.Protocol;
using Xunit;

namespace PeakRelay.Protocol.Tests;

public class PhotonDatagramTests
{
    /// <summary>Builds a datagram from the EnetPeer.SendData layout (big-endian header).</summary>
    private static byte[] BuildDatagram(short peerId, byte flagByte, byte commandCount, int sentTime, int challenge, params byte[][] commandBytes)
    {
        var datagram = new byte[PhotonDatagram.EnvelopeHeaderSize];
        BinaryPrimitives.WriteInt16BigEndian(datagram, peerId);
        datagram[2] = flagByte;
        datagram[3] = commandCount;
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(4), sentTime);
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(8), challenge);
        var parts = new byte[commandBytes.Length + 1][];
        parts[0] = datagram;
        for (int i = 0; i < commandBytes.Length; i++)
            parts[i + 1] = commandBytes[i];
        return Concat(parts);
    }

    /// <summary>Builds a SendReliable command: type, ch, flags, reserved, size, seq, payload.</summary>
    private static byte[] BuildReliableCommand(byte channel, int sequence, params byte[] payload)
    {
        var cmd = new byte[PhotonDatagram.CommandHeaderSize + payload.Length];
        cmd[0] = PhotonDatagram.CmdSendReliable;
        cmd[1] = channel;
        cmd[2] = 1; // reliable flag
        cmd[3] = 4; // reserved
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(4), cmd.Length);
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(8), sequence);
        payload.CopyTo(cmd.AsSpan(PhotonDatagram.CommandHeaderSize));
        return cmd;
    }

    private static byte[] BuildPingCommand()
    {
        var cmd = new byte[PhotonDatagram.CommandHeaderSize];
        cmd[0] = PhotonDatagram.CmdPing;
        BinaryPrimitives.WriteInt32BigEndian(cmd.AsSpan(4), cmd.Length);
        return cmd;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts)
            total += p.Length;
        var result = new byte[total];
        int offset = 0;
        foreach (var p in parts)
        {
            p.CopyTo(result, offset);
            offset += p.Length;
        }
        return result;
    }

    [Fact]
    public void TryReadEnvelopeHeader_RoundTrips()
    {
        var datagram = BuildDatagram(peerId: 7, flagByte: 0, commandCount: 1, sentTime: 123456, challenge: unchecked((int)0xDEADBEEF), BuildPingCommand());
        Assert.True(PhotonDatagram.TryReadEnvelopeHeader(datagram, out var header));
        Assert.Equal(7, header.PeerId);
        Assert.Equal(1, header.CommandCount);
        Assert.Equal(123456, header.SentTime);
    }

    [Fact]
    public void ReadCommands_ParsesMultipleCommands()
    {
        var payloadA = new byte[] { 0xAA, 0xBB };
        var payloadB = new byte[] { 0xCC };
        var datagram = BuildDatagram(1, 0, 2, 100, 200,
            BuildReliableCommand(0, 1, payloadA),
            BuildReliableCommand(1, 2, payloadB));

        var commands = PhotonDatagram.ReadCommands(datagram);
        Assert.Equal(2, commands.Count);
        Assert.Equal(PhotonDatagram.CmdSendReliable, commands[0].CommandType);
        Assert.Equal(0, commands[0].ChannelId);
        Assert.True(commands[0].IsReliable);
        Assert.Equal(1, commands[0].ReliableSeq);
        Assert.Equal(2, commands[0].PayloadLength);
        Assert.Equal(1, commands[1].ChannelId);
    }

    [Fact]
    public void ReadCommands_TruncatedOrLyingCommand_FindsNothing()
    {
        var datagram = BuildDatagram(1, 0, 1, 0, 0, BuildPingCommand());
        // Overwrite size field of the only command with a huge value.
        var lying = (byte[])datagram.Clone();
        BinaryPrimitives.WriteInt32BigEndian(lying.AsSpan(PhotonDatagram.EnvelopeHeaderSize + 4), 1 << 30);
        var commands = PhotonDatagram.ReadCommands(lying);
        Assert.Empty(commands);
    }

    [Fact]
    public void ReadCommands_EmptyDatagram_ReturnsEmpty()
    {
        Assert.Empty(PhotonDatagram.ReadCommands(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void CommandTypeNames_AreStable()
    {
        Assert.Equal("Ping", PhotonDatagram.CommandTypeName(PhotonDatagram.CmdPing));
        Assert.Equal("SendReliable", PhotonDatagram.CommandTypeName(PhotonDatagram.CmdSendReliable));
        Assert.Equal("Unknown(99)", PhotonDatagram.CommandTypeName(99));
    }
}
