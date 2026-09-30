using System.Collections;
using System.Collections.Generic;

namespace PeakRelay.Protocol;

/// <summary>
/// Server-announcement chat, shared by all three PeakRelay pieces: the dedicated console
/// raises it, the relay transports it opaquely (it forwards game datagrams without looking
/// inside), and the client shim renders it.
///
/// Custom PUN event code 199: the game's own code raises only event 18 (kick) — verified
/// against the decompiled Assembly-CSharp — so 199 cannot collide with game traffic, and
/// the game's event dispatcher ignores unknown codes.
///
/// The payload is one dictionary with two byte keys (P16-serializable, small, and
/// version-tolerant: unknown extra keys are ignored by the decoder). Deliberately
/// dependency-free: this library also builds for plain net8.0 (relay + tests), where the
/// Photon assemblies do not exist — the sender wraps the dictionary in PUN calls, the
/// receiver reads it back as an IDictionary (PUN hands the deserialized payload over as a
/// Hashtable, and dictionary keys may arrive byte- or int-boxed depending on the codec, so
/// the decoder accepts both).
/// </summary>
public static class ChatEvent
{
    /// <summary>Custom PUN event code for PeakRelay server chat.</summary>
    public const byte EventCode = 199;

    /// <summary>Message text.</summary>
    public const byte KeyText = 0;

    /// <summary>Sender label shown to players ("Server", or the operator's name later).</summary>
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
    /// Validates and decodes a received event payload. Returns null for anything that is
    /// not a well-formed chat message (wrong container, missing keys, wrong types, empty
    /// or oversized text) — receivers silently ignore those instead of crashing.
    /// </summary>
    public static (string From, string Text)? TryDecode(object? payload)
    {
        if (payload is not IDictionary dict)
            return null;
        if (TryGet(dict, KeyText) is not string text || TryGet(dict, KeyFrom) is not string from)
            return null;
        if (text.Length == 0 || text.Length > MaxLength)
            return null;
        if (from.Length == 0)
            from = "Server";
        if (from.Length > MaxFromLength)
            from = from[..MaxFromLength];
        return (from, text);
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
