using System.Collections;
using System.Collections.Generic;
using PeakRelay.Protocol;
using Xunit;

namespace PeakRelay.Protocol.Tests;

/// <summary>
/// The chat-event contract between senders (dedicated console, client shim), the relay
/// (opaque transport) and the client HUD (receiver). The decode side must be forgiving
/// about key boxing but strict about content — foreign or malformed events are ignored,
/// never thrown.
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
    public void Event_codes_do_not_collide_with_game_events()
    {
        // the decompiled game raises only event 18 (kick); both chat codes must stay clear
        // of it and of PUN's own reserved range (0-99)
        Assert.NotEqual((byte)18, ChatEvent.ServerEventCode);
        Assert.NotEqual((byte)18, ChatEvent.PlayerEventCode);
        Assert.All(new byte[] { ChatEvent.ServerEventCode, ChatEvent.PlayerEventCode },
            code => Assert.True(code > 99, "PUN reserves event codes 0-99"));
        Assert.Equal((byte)199, ChatEvent.ServerEventCode);
        Assert.Equal((byte)198, ChatEvent.PlayerEventCode);
    }

    [Theory]
    [InlineData(ChatEvent.ServerEventCode)]
    [InlineData(ChatEvent.PlayerEventCode)]
    public void Payload_round_trips_through_a_hashtable(byte kind)
    {
        // the wire path: sender hands a Dictionary to PUN, the receiver gets a Hashtable
        var payload = ChatEvent.CreatePayload("Server", "Restarting in 5 minutes");
        var decoded = ChatEvent.TryDecode(kind, ToTable(payload));
        Assert.NotNull(decoded);
        Assert.Equal((kind, "Server", "Restarting in 5 minutes"),
            (decoded!.Kind, decoded.From, decoded.Text));
    }

    [Fact]
    public void Decode_tolerates_int_boxed_keys()
    {
        // P16 codec variants may hand keys back int-boxed instead of byte-boxed
        var table = new Hashtable
        {
            [(int)ChatEvent.KeyFrom] = "Kynetik",
            [(int)ChatEvent.KeyText] = "hello",
        };
        var decoded = ChatEvent.TryDecode(ChatEvent.PlayerEventCode, table);
        Assert.Equal(("Kynetik", "hello"), (decoded!.From, decoded.Text));
        Assert.Equal(ChatEvent.PlayerEventCode, decoded.Kind);
    }

    [Fact]
    public void Decode_accepts_a_plain_dictionary()
    {
        var decoded = ChatEvent.TryDecode(ChatEvent.PlayerEventCode,
            ChatEvent.CreatePayload("Bob", "hi"));
        Assert.Equal(("Bob", "hi"), (decoded!.From, decoded.Text));
    }

    [Theory]
    [InlineData(42)]                    // foreign payload type
    [InlineData(null)]                  // no payload at all
    public void Decode_ignores_non_dictionary_payloads(object? payload)
        => Assert.Null(ChatEvent.TryDecode(ChatEvent.PlayerEventCode, payload));

    [Fact]
    public void Decode_ignores_missing_or_wrongly_typed_keys()
    {
        Assert.Null(ChatEvent.TryDecode(ChatEvent.PlayerEventCode, new Hashtable()));
        Assert.Null(ChatEvent.TryDecode(ChatEvent.PlayerEventCode,
            new Hashtable { [ChatEvent.KeyText] = "no sender" }));
        Assert.Null(ChatEvent.TryDecode(ChatEvent.PlayerEventCode,
            new Hashtable { [ChatEvent.KeyFrom] = "no text" }));
        Assert.Null(ChatEvent.TryDecode(ChatEvent.PlayerEventCode,
            new Hashtable { [ChatEvent.KeyFrom] = 7, [ChatEvent.KeyText] = "text" }));
        Assert.Null(ChatEvent.TryDecode(ChatEvent.PlayerEventCode,
            new Hashtable { [ChatEvent.KeyFrom] = "Server", [ChatEvent.KeyText] = 42 }));
    }

    [Fact]
    public void Decode_ignores_empty_and_oversized_text()
    {
        Assert.Null(ChatEvent.TryDecode(ChatEvent.ServerEventCode, ChatEvent.CreatePayload("Server", "")));
        Assert.Null(ChatEvent.TryDecode(ChatEvent.ServerEventCode,
            ChatEvent.CreatePayload("Server", new string('x', ChatEvent.MaxLength + 1))));
    }

    [Fact]
    public void Decode_accepts_text_at_the_limit()
    {
        var text = new string('x', ChatEvent.MaxLength);
        var decoded = ChatEvent.TryDecode(ChatEvent.ServerEventCode, ChatEvent.CreatePayload("Server", text));
        Assert.Equal(text, decoded!.Text);
    }

    [Fact]
    public void Empty_sender_defaults_per_kind_and_long_senders_are_clamped()
    {
        var server = ChatEvent.TryDecode(ChatEvent.ServerEventCode,
            new Hashtable { [ChatEvent.KeyFrom] = "", [ChatEvent.KeyText] = "text" });
        Assert.Equal("Server", server!.From);

        var player = ChatEvent.TryDecode(ChatEvent.PlayerEventCode,
            new Hashtable { [ChatEvent.KeyFrom] = "", [ChatEvent.KeyText] = "text" });
        Assert.Equal("Player", player!.From);

        var clamped = ChatEvent.TryDecode(ChatEvent.PlayerEventCode,
            ChatEvent.CreatePayload(new string('a', ChatEvent.MaxFromLength + 5), "t"));
        Assert.Equal(ChatEvent.MaxFromLength, clamped!.From.Length);
    }

    [Fact]
    public void Decode_ignores_unknown_extra_keys()
    {
        // forward compatibility: future senders may add fields older receivers don't know
        var table = ToTable(ChatEvent.CreatePayload("Server", "future-proof"));
        table[250] = "some future field";
        var decoded = ChatEvent.TryDecode(ChatEvent.ServerEventCode, table);
        Assert.Equal("future-proof", decoded!.Text);
    }
}
