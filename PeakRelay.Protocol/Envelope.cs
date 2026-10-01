using System;
using System.Buffers.Binary;

namespace PeakRelay.Protocol;

/// <summary>Message classes carried by the relay envelope.</summary>
public enum RelayOp : byte
{
    /// <summary>Hello/handshake from client. Body: [byte wireVersion][0x00 pad].</summary>
    Hello = 1,

    /// <summary>Carries one raw Photon UDP datagram. Body: opaque bytes.</summary>
    Data = 2,

    /// <summary>Relay-level disconnect. No body.</summary>
    Bye = 3,

    /// <summary>Server -> client: ack of handshake. Empty body = accepted.</summary>
    Welcome = 4,

    /// <summary>Server -> client: relay-level disconnect notice. Body: reason string (UTF-8), e.g. "relay is v0.7.14, client speaks wire v1 — update the client".</summary>
    Kick = 5,
}

/// <summary>
/// Relay envelope, shared by shim and relay server.
/// [u8 op][u8 flags][u16 requestId][u32 payloadLength][payload].
/// </summary>
public static class Envelope
{
    public const int HeaderSize = 8;
    public const byte FlagNone = 0;
    public const byte FlagCompressed = 1; // reserved; never set in M0

    /// <summary>Hello payload: [byte wireVersion][0x00 pad] — 2 bytes, keep short so the
    /// handshake stays a single tiny frame. Older clients sent an EMPTY Hello; a missing
    /// body therefore means wire v0 (pre-0.7.14, never version-checked itself).</summary>
    public const int HelloBodySize = 2;

    /// <summary>Extracts the wire version from a Hello body (0 = pre-versioning client).</summary>
    public static int HelloWireVersion(ReadOnlySpan<byte> helloBody) =>
        helloBody.Length >= 1 ? helloBody[0] : 0;
    public static byte[] Write(RelayOp op, byte flags, ushort requestId, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[HeaderSize + payload.Length];
        buffer[0] = (byte)op;
        buffer[1] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), requestId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }

    public static bool TryRead(ReadOnlySpan<byte> source, out EnvelopeReader reader)
    {
        reader = default;
        if (source.Length < HeaderSize)
            return false;
        var op = (RelayOp)source[0];
        if (op is < RelayOp.Hello or > RelayOp.Kick)
            return false;
        var flags = source[1];
        var requestId = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(2));
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4));
        if (payloadLength > MaxPayloadSize)
            return false;
        if (source.Length - HeaderSize < payloadLength)
            return false;
        reader = new EnvelopeReader(op, flags, requestId, source.Slice(HeaderSize, (int)payloadLength));
        return true;
    }

    public const int MaxPayloadSize = 64 * 1024;

    public readonly ref struct EnvelopeReader
    {
        public EnvelopeReader(RelayOp op, byte flags, ushort requestId, ReadOnlySpan<byte> payload)
        {
            Op = op;
            Flags = flags;
            RequestId = requestId;
            Payload = payload;
        }

        public RelayOp Op { get; }
        public byte Flags { get; }
        public ushort RequestId { get; }
        public ReadOnlySpan<byte> Payload { get; }
    }
}
