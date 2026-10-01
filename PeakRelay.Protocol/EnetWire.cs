using System;
using System.Buffers.Binary;

namespace PeakRelay.Protocol;

/// <summary>
/// ENET wire constants and codec helpers shared by the relay server and any datagram-level
/// consumer. Layouts follow the decompiled Photon3 EnetPeer/NCommand (docs/protocol-notes.md):
///
///   datagram: [int16 peerID BE][u8 flag: 0=plain 1=encrypted 204=CRC][u8 commandCount]
///             [int32 sentTime BE][int32 challenge BE]
///   command:  [u8 type][u8 channel][u8 flags][u8 reserved][int32 size BE][int32 reliableSeq BE]
///             + type 7/11: int32 unreliableSeq  (16-byte header)
///             + type 8/15: startSeq, fragmentCount, fragmentNumber, totalLength, fragmentOffset
///               (32-byte header)
///             + type 1/16 (ACK): ackReceivedReliableSeq, ackSentTime (20 bytes total)
///
/// One implementation, byte-exact, instead of one per consumer.
/// </summary>
public static class EnetWire
{
    // command types (NCommand)
    public const byte CmdAck = 1;
    public const byte CmdConnect = 2;
    public const byte CmdVerifyConnect = 3;
    public const byte CmdDisconnect = 4;
    public const byte CmdPing = 5;
    public const byte CmdSendReliable = 6;
    public const byte CmdSendUnreliable = 7;
    public const byte CmdSendFragment = 8;
    public const byte CmdServerTime = 12;

    public const int DatagramHeaderSize = 12;
    public const int CommandHeaderSize = 12;
    public const int UnreliableHeaderSize = 16;
    public const int FragmentHeaderSize = 32;
    public const int AckSize = 20;

    /// <summary>Reliable sequence numbers are 15-bit and wrap (client dispatch compares shorts).</summary>
    public const int MaxReliableSeq = short.MaxValue;

    public static int NextSeq(byte channel, int current) => current >= MaxReliableSeq ? 1 : current + 1;

    public static byte[] WrapDatagram(int peerId, byte flag, int sentTime, int challenge, params byte[][] commands)
    {
        var body = 0;
        foreach (var command in commands)
            body += command.Length;
        var datagram = new byte[DatagramHeaderSize + body];
        BinaryPrimitives.WriteInt16BigEndian(datagram, (short)peerId);
        datagram[2] = flag;
        datagram[3] = (byte)commands.Length;
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(4), sentTime);
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(8), challenge);
        var at = DatagramHeaderSize;
        foreach (var command in commands)
        {
            command.CopyTo(datagram, at);
            at += command.Length;
        }
        return datagram;
    }

    public readonly record struct Command(
        byte Type, byte Channel, byte Flags, int ReliableSeq, byte[] Payload,
        int StartSeq, int FragmentCount, int FragmentNumber, int TotalLength, int FragmentOffset)
    {
        public static Command Create(byte type, byte channel, byte flags, int reliableSeq, byte[] payload) =>
            new(type, channel, flags, reliableSeq, payload, 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// Splits a datagram into parsed commands; returns false when the datagram is malformed
    /// or uses an unsupported variant (encrypted / CRC) — matching the reference parser,
    /// which aborts the rest of the datagram on a bad command. Payloads are copied.
    /// </summary>
    public static bool TrySplit(ReadOnlySpan<byte> datagram, out List<Command> commands)
    {
        commands = new List<Command>();
        if (datagram.Length < DatagramHeaderSize)
            return false;
        var flag = datagram[2];
        if (flag is 1 or 204)
            return false; // encrypted / CRC variants: not supported
        var count = datagram[3];
        var offset = DatagramHeaderSize;
        for (var i = 0; i < count && offset + CommandHeaderSize <= datagram.Length; i++)
        {
            var type = datagram[offset];
            var channel = datagram[offset + 1];
            var flags = datagram[offset + 2];
            var size = BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(offset + 4));
            if (size < CommandHeaderSize || offset + size > datagram.Length)
                return false;
            var reliableSeq = BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(offset + 8));
            var headerExtra = type switch
            {
                CmdSendUnreliable => 4, // unreliableSeq
                CmdSendFragment => 20,  // startSeq, count, number, totalLength, offset
                _ => 0,
            };
            var payloadStart = offset + CommandHeaderSize + headerExtra;
            if (payloadStart > offset + size)
                return false;
            var payload = payloadStart == offset + size
                ? Array.Empty<byte>()
                : datagram.Slice(payloadStart, size - CommandHeaderSize - headerExtra).ToArray();
            var command = type == CmdSendFragment
                ? new Command(
                    type, channel, flags, reliableSeq, payload,
                    StartSeq: BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(offset + 12)),
                    FragmentCount: BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(offset + 16)),
                    FragmentNumber: BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(offset + 20)),
                    TotalLength: BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(offset + 24)),
                    FragmentOffset: BinaryPrimitives.ReadInt32BigEndian(datagram.Slice(offset + 28)))
                : Command.Create(type, channel, flags, reliableSeq, payload);
            commands.Add(command);
            offset += size;
        }
        return true;
    }
}
