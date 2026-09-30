using System.Collections;
using System.Collections.Generic;

namespace PeakRelay.Protocol;

/// <summary>Who a chat message came from and what it says.</summary>
public sealed record ChatMessage(byte Kind, string From, string Text);

/// <summary>
/// PeakRelay chat, shared by every piece: the dedicated console raises server messages,
/// clients raise player messages, every client's HUD renders them, and the relay routes
/// them opaquely (it forwards game datagrams without looking inside).
///
/// Two custom PUN event codes, both verified clear of the game (which raises only event 18,
/// kick, in decompiled Assembly-CSharp) and of PUN's reserved 0-99 range:
///   199 = server announcement (dedicated console)
///   198 = player chat (client shim; the relay's event routing stamps param 254 with the
///         sender's actor number, which the HUD resolves to a player name)
///
/// The payload is one dictionary with two byte keys (P16-serializable, small, and
/// version-tolerant: unknown extra keys are ignored by the decoder). Deliberately
/// dependency-free: this library also builds for plain net8.0 (relay + tests), where the
/// Photon assemblies do not exist — senders wrap the dictionary in PUN calls, receivers
/// read it back as an IDictionary (PUN hands the deserialized payload over as a Hashtable,
/// and keys may arrive byte- or int-boxed depending on the codec, so decoding accepts both).
/// </summary>
public static class ChatEvent
{
    /// <summary>Custom PUN event code for server announcements (dedicated console).</summary>
    public const byte ServerEventCode = 199;

    /// <summary>Custom PUN event code for player chat messages.</summary>
    public const byte PlayerEventCode = 198;

    /// <summary>Message text.</summary>
    public const byte KeyText = 0;

    /// <summary>Sender label shown to players ("Server" or the player's name).</summary>
    public const byte KeyFrom = 1;

    /// <summary>Maximum message length in characters — enforced by sender and receiver.</summary>
    public const int MaxLength = 200;

    /// <summary>Maximum sender-label length in characters.</summary>
    public const int MaxFromLength = 32;

    /// <summary>Builds the event payload for a chat message.</summary>
    public static Dictionary<byte, object> CreatePayload(string from, string text) => new()
    {
        [KeyFrom] = from,
        [KeyText] = text,
    };

    /// <summary>
    /// Validates and decodes a received event payload into a message of the given kind.
    /// Returns null for anything that is not a well-formed chat message (wrong container,
    /// missing keys, wrong types, empty or oversized text) — receivers silently ignore
    /// those instead of crashing.
    /// </summary>
    public static ChatMessage? TryDecode(byte kind, object? payload)
    {
        if (payload is not IDictionary dict)
            return null;
        if (TryGet(dict, KeyText) is not string text || TryGet(dict, KeyFrom) is not string from)
            return null;
        if (text.Length == 0 || text.Length > MaxLength)
            return null;
        if (from.Length == 0)
            from = kind == ServerEventCode ? "Server" : "Player";
        if (from.Length > MaxFromLength)
            from = from[..MaxFromLength];
        return new ChatMessage(kind, from, text);
    }

    /// <summary>Hashtable/dictionary lookup that tolerates byte- vs int-boxed keys.</summary>
    private static object? TryGet(IDictionary dict, byte key)
    {
        if (dict.Contains(key))
            return dict[key];
        var widened = (int)key;
        return dict.Contains(widened) ? dict[widened] : null;
    }
}
