using System;
using System.Buffers.Binary;
using PeakRelay.Protocol;
using Xunit;

namespace PeakRelay.Protocol.Tests;

public class FrameTests
{
    [Fact]
    public void Write_ThenReadHeader_RoundTrips()
    {
        var payload = "peak"u8.ToArray();
        var frame = Frame.Write(payload);

        Assert.Equal(Frame.HeaderSize + payload.Length, BinaryPrimitives.ReadInt32LittleEndian(frame));
        Assert.Equal(Frame.Version, BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(4)));
        Assert.Equal(payload, frame.AsSpan(Frame.HeaderSize, payload.Length).ToArray());
    }

    [Fact]
    public void Write_OversizedPayload_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Frame.Write(new byte[Frame.MaxPayload + 1]));
    }
}
