using System.Collections;
using System.Collections.Generic;
using PeakRelay.Protocol;
using Xunit;

namespace PeakRelay.Protocol.Tests;

/// <summary>
/// The chat-event contract between the dedicated console (sender), the relay (opaque
/// transport) and the client HUD (receiver). The decode side must be forgiving about key
/// boxing but strict about content — foreign or malformed events are ignored, never thrown.
/// </summary>
public class ChatEventTests
{
    /// <summary>Mimics what PUN does between sender and receiver: rebuilds a Hashtable from the payload.</summary>
    private static Hashtable ToTable(IDictionary source)
    {
        var table = new Hashtable();
        foreach (DictionaryEntry entry in source)
            table[entry.Key] = entry.Value;
        return table;
    }
    [Fact]
    public void Event_code_does_not_collide_with_game_events()
    {
        // the decompiled game raises only event 18 (kick); 199 must stay clear of it and of
        // PUN's own reserved range (0-99)
        Assert.NotEqual((byte)18, ChatEvent.EventCode);
        Assert.True(ChatEvent.EventCode >= 200 || ChatEvent.EventCode <= 255);
        Assert.Equal((byte)199, ChatEvent.EventCode);
    }

    [Fact]
    public void Payload_round_trips_through_a_hashtable()
    {
        // the wire path: sender hands a Dictionary to PUN, the receiver gets a Hashtable
        var payload = ChatEvent.CreatePayload("Server", "Restarting in 5 minutes");
        var table = ToTable(payload);

        var decoded = ChatEvent.TryDecode(table);
        Assert.NotNull(decoded);
        Assert.Equal(("Server", "Restarting in 5 minutes"), decoded!.Value);
    }

    [Fact]
    public void Decode_tolerates_int_boxed_keys()
    {
        // P16 codec variants may hand keys back int-boxed instead of byte-boxed
        var table = new Hashtable
        {
            [(int)ChatEvent.KeyFrom] = "Server",
            [(int)ChatEvent.KeyText] = "hello",
        };
        Assert.Equal(("Server", "hello"), ChatEvent.TryDecode(table)!.Value);
    }

    [Fact]
    public void Decode_accepts_a_plain_dictionary()
    {
        Assert.Equal(("Server", "hi"),
            ChatEvent.TryDecode(ChatEvent.CreatePayload("Server", "hi"))!.Value);
    }

    [Theory]
    [InlineData(42)]                    // foreign payload type
    [InlineData(null)]                  // no payload at all
    public void Decode_ignores_non_dictionary_payloads(object? payload)
        => Assert.Null(ChatEvent.TryDecode(payload));

    [Fact]
    public void Decode_ignores_missing_or_wrongly_typed_keys()
    {
        Assert.Null(ChatEvent.TryDecode(new Hashtable()));
        Assert.Null(ChatEvent.TryDecode(new Hashtable { [ChatEvent.KeyText] = "no sender" }));
        Assert.Null(ChatEvent.TryDecode(new Hashtable { [ChatEvent.KeyFrom] = "no text" }));
        Assert.Null(ChatEvent.TryDecode(new Hashtable { [ChatEvent.KeyFrom] = 7, [ChatEvent.KeyText] = "text" }));
        Assert.Null(ChatEvent.TryDecode(new Hashtable { [ChatEvent.KeyFrom] = "Server", [ChatEvent.KeyText] = 42 }));
    }

    [Fact]
    public void Decode_ignores_empty_and_oversized_text()
    {
        Assert.Null(ChatEvent.TryDecode(ChatEvent.CreatePayload("Server", "")));
        Assert.Null(ChatEvent.TryDecode(ChatEvent.CreatePayload("Server", new string('x', ChatEvent.MaxLength + 1))));
    }

    [Fact]
    public void Decode_accepts_text_at_the_limit()
    {
        var text = new string('x', ChatEvent.MaxLength);
        Assert.Equal(("Server", text), ChatEvent.TryDecode(ChatEvent.CreatePayload("Server", text))!.Value);
    }

    [Fact]
    public void Empty_sender_becomes_server_and_long_senders_are_clamped()
    {
        var defaulted = ChatEvent.TryDecode(new Hashtable { [ChatEvent.KeyFrom] = "", [ChatEvent.KeyText] = "text" });
        Assert.Equal("Server", defaulted!.Value.From);

        var clamped = ChatEvent.TryDecode(ChatEvent.CreatePayload(new string('a', ChatEvent.MaxFromLength + 5), "t"));
        Assert.Equal(ChatEvent.MaxFromLength, clamped!.Value.From.Length);
    }

    [Fact]
    public void Decode_ignores_unknown_extra_keys()
    {
        // forward compatibility: future senders may add fields older receivers don't know
        var table = ToTable(ChatEvent.CreatePayload("Server", "future-proof"));
        table[250] = "some future field";
        Assert.Equal(("Server", "future-proof"), ChatEvent.TryDecode(table)!.Value);
    }
}
