using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace PeakRelay.Protocol;

/// <summary>
/// Decodes the Photon UDP datagram envelope (peerID + flag byte + command count + sentTime +
/// challenge) and the commands inside it. This is the M0 observation layer: we never re-encode
/// any of this — the shim forwards bytes opaquely, the relay routes them opaquely.
/// </summary>
public static class PhotonDatagram
{
    public const int EnvelopeHeaderSize = 12;
    public const int CommandHeaderSize = 12;

    // NCommand.commandType values (from decompiled NCommand.cs, shipped PEAK build)
    public const byte CmdAck = 1;
    public const byte CmdConnect = 2;
    public const byte CmdVerifyConnect = 3;
    public const byte CmdDisconnect = 4;
    public const byte CmdPing = 5;
    public const byte CmdSendReliable = 6;
    public const byte CmdSendUnreliable = 7;
    public const byte CmdSendFragment = 8;
    public const byte CmdSendUnsequenced = 11;
    public const byte CmdServerTime = 12;
    public const byte CmdUnreliableProcessed = 13;
    public const byte CmdReliableUnsequenced = 14;
    public const byte CmdAckUnsequenced = 16;

    // EnetPeer.SendData: peerID(2) + flagByte(1) + commandCount(1) + sentTime(4) + challenge(4)
    public static bool TryReadEnvelopeHeader(ReadOnlySpan<byte> datagram, out DatagramHeader header)
    {
        header = default;
        if (datagram.Length < EnvelopeHeaderSize)
            return false;
        header = new DatagramHeader(
            BinaryPrimitives.ReadInt16BigEndian(datagram),
            datagram[2],
            datagram[3],
            BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(4)),
            BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(8)));
        return true;
    }

    public static IReadOnlyList<CommandInfo> ReadCommands(ReadOnlySpan<byte> datagram)
    {
        var commands = new List<CommandInfo>();
        if (!TryReadEnvelopeHeader(datagram, out _))
            return commands;
        int offset = EnvelopeHeaderSize;
        while (offset + CommandHeaderSize <= datagram.Length)
        {
            byte commandType = datagram[offset];
            byte channelId = datagram[offset + 1];
            byte flags = datagram[offset + 2];
            // reserved byte at offset+3
            int size = BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(offset + 4));
            int reliableSeq = BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(offset + 8));
            if (size < CommandHeaderSize || offset + size > datagram.Length)
                break; // malformed or truncated; stop instead of throwing on live data

            int payloadLength = size - CommandHeaderSize;
            commands.Add(new CommandInfo(
                commandType, channelId, flags, reliableSeq,
                payloadLength, offset + size));
            offset += size;
        }

        return commands;
    }

    public static string CommandTypeName(byte commandType) => commandType switch
    {
        CmdAck => "Ack",
        CmdConnect => "Connect",
        CmdVerifyConnect => "VerifyConnect",
        CmdDisconnect => "Disconnect",
        CmdPing => "Ping",
        CmdSendReliable => "SendReliable",
        CmdSendUnreliable => "SendUnreliable",
        CmdSendFragment => "SendFragment",
        CmdSendUnsequenced => "SendUnsequenced",
        CmdServerTime => "ServerTime",
        CmdUnreliableProcessed => "UnreliableProcessed",
        CmdReliableUnsequenced => "ReliableUnsequenced",
        CmdAckUnsequenced => "AckUnsequenced",
        _ => $"Unknown({commandType})",
    };

    public readonly struct DatagramHeader
    {
        public DatagramHeader(short peerId, byte flagByte, byte commandCount, int sentTime, int challenge)
        {
            PeerId = peerId;
            FlagByte = flagByte;
            CommandCount = commandCount;
            SentTime = sentTime;
            Challenge = challenge;
        }

        public short PeerId { get; }
        public byte FlagByte { get; }
        public byte CommandCount { get; }
        public int SentTime { get; }
        public int Challenge { get; }

        public override string ToString()
            => $"peer={PeerId} flags=0x{FlagByte:X2} cmds={CommandCount} time={SentTime} chal=0x{Challenge:X8}";
    }

    public readonly struct CommandInfo
    {
        public CommandInfo(byte commandType, byte channelId, byte flags, int reliableSeq, int payloadLength, int endOffset)
        {
            CommandType = commandType;
            ChannelId = channelId;
            Flags = flags;
            ReliableSeq = reliableSeq;
            PayloadLength = payloadLength;
            EndOffset = endOffset;
        }

        public byte CommandType { get; }
        public byte ChannelId { get; }
        public byte Flags { get; }
        public int ReliableSeq { get; }
        public int PayloadLength { get; }
        public int EndOffset { get; }

        public bool IsReliable => (Flags & 1) != 0;

        public string Describe()
            => $"{CommandTypeName(CommandType)} ch={ChannelId} {ReliableFlag()} size={PayloadLength + CommandHeaderSize}";

        private string ReliableFlag() => IsReliable ? "[R]" : "[U]";
    }
}
