using System;
using PeakRelay.Protocol;
using Xunit;

namespace PeakRelay.Protocol.Tests;

public class EnvelopeTests
{
    [Fact]
    public void Write_ThenTryRead_RoundTrips()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var bytes = Envelope.Write(RelayOp.Data, Envelope.FlagNone, 0xBEEF, payload);

        Assert.True(Envelope.TryRead(bytes, out var reader));
        Assert.Equal(RelayOp.Data, reader.Op);
        Assert.Equal(Envelope.FlagNone, reader.Flags);
        Assert.Equal(0xBEEF, reader.RequestId);
        Assert.Equal(payload, reader.Payload.ToArray());
    }

    [Fact]
    public void TryRead_EmptyPayload_RoundTrips()
    {
        var bytes = Envelope.Write(RelayOp.Hello, Envelope.FlagNone, 7, ReadOnlySpan<byte>.Empty);
        Assert.True(Envelope.TryRead(bytes, out var reader));
        Assert.Equal(RelayOp.Hello, reader.Op);
        Assert.Equal(0, reader.Payload.Length);
    }

    [Theory]
    [InlineData(new byte[] { })]                       // empty
    [InlineData(new byte[] { 2, 0, 0 })]               // truncated header
    [InlineData(new byte[] { 9, 0, 0, 0, 0, 0, 0, 0 })] // invalid op
    [InlineData(new byte[] { 2, 0, 1, 0, 0xFF, 0xFF, 0, 0 })] // oversized declared length
    public void TryRead_MaliciousInput_ReturnsFalse(byte[] bytes)
    {
        Assert.False(Envelope.TryRead(bytes, out _));
    }

    [Fact]
    public void TryRead_TruncatedPayload_ReturnsFalse()
    {
        var bytes = Envelope.Write(RelayOp.Data, Envelope.FlagNone, 1, new byte[16]);
        var truncated = bytes[..^4]; // cut payload bytes
        Assert.False(Envelope.TryRead(truncated, out _));
    }
}
