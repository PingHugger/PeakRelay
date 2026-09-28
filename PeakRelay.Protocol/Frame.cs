using System;
using System.Buffers.Binary;
using System.Text;

namespace PeakRelay.Protocol;

/// <summary>
/// Binary framing shared by the relay client (shim) and relay server.
/// Frame = int32 length (LE) + int32 version (LE) + payload.
/// </summary>
public static class Frame
{
    public const int HeaderSize = sizeof(int) + sizeof(int);
    public const int Version = 1;
    public const int MaxPayload = 16 * 1024;

    public static byte[] Write(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload)
            throw new InvalidOperationException($"Payload too large: {payload.Length} > {MaxPayload}");
        var buffer = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, buffer.Length);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), Version);
        payload.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }

    public static string ReadString(ReadOnlySpan<byte> payload)
        => Encoding.UTF8.GetString(payload.ToArray());
}
