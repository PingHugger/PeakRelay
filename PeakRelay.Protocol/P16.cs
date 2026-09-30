using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PeakRelay.Protocol;

/// <summary>LoadBalancing operation codes (Photon.Realtime.OperationCode).</summary>
public static class LbOp
{
    public const byte Authenticate = 230;
    public const byte JoinLobby = 229;
    public const byte CreateGame = 227;
    public const byte JoinGame = 226;
    public const byte JoinRandomGame = 225;
    public const byte Leave = 254;
    public const byte RaiseEvent = 253;
    public const byte SetProperties = 252;
    public const byte GetProperties = 251;
    public const byte ServerSettings = 218;
}

/// <summary>LoadBalancing parameter codes (Photon.Realtime.ParameterCode).</summary>
public static class LbParam
{
    public const byte RoomName = 255;
    public const byte ActorList = 252;
    public const byte ActorNr = 254;
    public const byte PlayerProperties = 249;
    public const byte GameProperties = 248;
    public const byte Properties = 251;
    public const byte Broadcast = 250;
    public const byte SuppressRoomEvents = 237;
    public const byte EmptyRoomTTL = 236;
    public const byte PlayerTTL = 235;
    public const byte CheckUserOnJoin = 232;
    public const byte UserId = 225;
    public const byte ApplicationId = 224;
    public const byte AppVersion = 220;
    public const byte Token = 221;
    public const byte CustomEventContent = 245;
    public const byte Data = 245;
    public const byte Code = 244;
    public const byte TargetActorNr = 253;
    public const byte ReceiverGroup = 246;
    public const byte Cache = 247;
    public const byte MasterClientId = 203;
    public const byte Address = 230;
    public const byte Region = 210;
    public const byte UriPath = 209;

    /// <summary>
    /// Player-properties key carrying the PUN identity (Photon.Realtime.ActorProperties.UserId).
    /// Value must be encoded as a P16 BYTE so it lands in the client's Hashtable byte-boxed:
    /// the C# lookup properties.ContainsKey(253) binds the int constant to Hashtable's byte
    /// overload (constant conversion beats object), and Photon's boxedByte table matches
    /// boxed byte keys only — a differently-boxed 253 silently leaves Player.UserID null
    /// (PEAK then throws ArgumentNullException from its spawn/ban/audio flows every frame).
    /// </summary>
    public const byte PlayerPropUserId = 253;
    public const byte PlayerPropNickName = byte.MaxValue; // ActorProperties.PlayerName, byte-keyed
}

/// <summary>LoadBalancing event codes (Photon.Realtime.EventCode).</summary>
public static class LbEvent
{
    public const byte Join = 255;
    public const byte Leave = 254;
    public const byte PropertiesChanged = 253;
    public const byte AppStats = 226;
    public const byte AuthEvent = 223;
}

/// <summary>LoadBalancing return codes (Photon.Realtime.ErrorCode subset used by the relay).</summary>
public static class LbError
{
    public const short Ok = 0;
    public const short InvalidOperation = -2;
    public const short InternalServerError = -1;
    public const short InvalidAuthentication = 32767;
    public const short GameIdAlreadyExists = 32766;
    public const short GameFull = 32765;
    public const short GameClosed = 32764;
    public const short NoRandomMatchFound = 32760;
    public const short GameDoesNotExist = 32758;
    public const short MaxCcuReached = 32757;
    public const short CustomAuthenticationFailed = 32755;
    public const short JoinFailedPeerAlreadyJoined = 32750;
    public const short JoinFailedFoundActiveJoiner = 32746;
}

public enum EnvelopeFlag : byte
{
    Datagram = 1,
    Disconnect = 2,
}

/// <summary>A decoded LoadBalancing operation request from a client.</summary>
public sealed class LbRequest
{
    public byte Op { get; init; }
    public Dictionary<byte, object> Parameters { get; init; } = new();
}

/// <summary>A LoadBalancing event or response to be encoded and sent to a client.</summary>
public sealed class LbMessage
{
    public byte Code { get; init; }
    public short ReturnCode { get; init; }
    public string? DebugMessage { get; init; }
    public Dictionary<byte, object> Parameters { get; init; } = new();
    public bool IsEvent { get; init; }
}

/// <summary>
/// Minimal Protocol16 (GpBinaryV16) codec for the relay's LoadBalancing layer, byte-exact with
/// the shipped client (see references/.../Protocol16.cs and docs/protocol-notes.md).
/// Supports: byte, bool, short, int, long, float, double, string, string[], byte[], int[],
/// Hashtable, Dictionary{byte,object}, object[], EventData/OperationRequest/OperationResponse
/// (nested), and raw passthrough for unknown/custom payloads.
///
/// Layouts (verified against decompiled Protocol16.cs, GpType enum at :13):
///   All multi-byte ints BIG-endian. Lengths are int16 ("short") unless byte[] (int32).
///   string: [short len][utf8].  hashtable 'h': [short n]{[tagged key][tagged val]}.
///   dictionary 'D': [keyTag][valTag][short n] entries; entry key/val are untagged when the
///   header tag is a concrete type, fully tagged when the header tag is 0.
///   object[] 'z': [short n] then each element FULLY TAGGED (no shared element tag).
///   Arrays 'a'/'n': [short n] untagged elements. byte[] 'x': [int32 len] bytes.
///   Command payload (inside ENET SendReliable): request = [op][paramtable]; response =
///   [op][short ret][tagged dbg][paramtable]; event = [code][paramtable] — no wrapper tags.
/// </summary>
public static class P16
{
    private const byte TagNull = 42;
    private const byte TagByte = 98;
    private const byte TagBool = 111;
    private const byte TagShort = 107;
    private const byte TagInt = 105;
    private const byte TagLong = 108;
    private const byte TagFloat = 102;
    private const byte TagDouble = 100;
    private const byte TagString = 115;
    private const byte TagStringArray = 97;
    private const byte TagArray = 121;      // [121][len][elemTag] untagged elements (client writes int[]/string[] this way)
    private const byte TagByteArray = 120;
    private const byte TagIntArray = 110;
    private const byte TagHashtable = 104;
    private const byte TagDictionary = 68;
    private const byte TagObjectArray = 122;
    private const byte TagEventData = 101;
    private const byte TagOperationRequest = 113;
    private const byte TagOperationResponse = 112;
    private const byte TagCustom = 99; // [99][code][short len][len bytes] — passthrough

    /// <summary>Passthrough marker: value is raw P16-encoded bytes, forwarded verbatim.</summary>
    public sealed class Raw
    {
        public byte[] Bytes { get; }
        public Raw(byte[] bytes) => Bytes = bytes;
    }

    public static byte[] EncodeEvent(LbMessage ev) => Build(ev.Code, s => WriteParameterTable(s, ev.Parameters));

    public static byte[] EncodeResponse(LbMessage r) => Build(r.Code, s =>
    {
        WriteShort(s, r.ReturnCode);
        if (string.IsNullOrEmpty(r.DebugMessage)) s.WriteByte(TagNull);
        else { s.WriteByte(TagString); WriteStringBody(s, r.DebugMessage!); }
        WriteParameterTable(s, r.Parameters);
    });

    private static byte[] Build(byte code, Action<MemoryStream> body)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(code);
        body(ms);
        return ms.ToArray();
    }

    public static void WriteShort(MemoryStream s, short v)
    {
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    public static void WriteInt(MemoryStream s, int v)
    {
        WriteShort(s, (short)(v >> 16));
        WriteShort(s, (short)v);
    }

    public static void WriteStringBody(MemoryStream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > short.MaxValue) throw new InvalidDataException("string too long");
        WriteShort(s, (short)bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }

    public static void WriteParameterTable(MemoryStream s, Dictionary<byte, object> parameters)
    {
        WriteShort(s, (short)(parameters?.Count ?? 0));
        if (parameters == null) return;
        foreach (var (key, value) in parameters)
        {
            s.WriteByte(key);
            WriteValue(s, value, setType: true);
        }
    }

    public static void WriteValue(MemoryStream s, object? value, bool setType)
    {
        switch (value)
        {
            case Raw v:
                s.Write(v.Bytes, 0, v.Bytes.Length);
                break;
            case null:
                if (setType) s.WriteByte(TagNull);
                break;
            case byte v:
                if (setType) s.WriteByte(TagByte);
                s.WriteByte(v);
                break;
            case bool v:
                if (setType) s.WriteByte(TagBool);
                s.WriteByte(v ? (byte)1 : (byte)0);
                break;
            case short v:
                if (setType) s.WriteByte(TagShort);
                WriteShort(s, v);
                break;
            case int v:
                if (setType) s.WriteByte(TagInt);
                WriteInt(s, v);
                break;
            case long v:
                if (setType) s.WriteByte(TagLong);
                WriteLong(s, v);
                break;
            case float v:
                if (setType) s.WriteByte(TagFloat);
                WriteFloat(s, v);
                break;
            case double v:
                if (setType) s.WriteByte(TagDouble);
                WriteDouble(s, v);
                break;
            case string v:
                if (setType) s.WriteByte(TagString);
                WriteStringBody(s, v);
                break;
            case string[] v:
                if (setType) s.WriteByte(TagStringArray);
                WriteShort(s, (short)v.Length);
                foreach (var str in v) WriteStringBody(s, str);
                break;
            case byte[] v:
                if (setType) s.WriteByte(TagByteArray);
                WriteInt(s, v.Length);
                s.Write(v, 0, v.Length);
                break;
            case int[] v:
                // client's DeserializeIntArray reads an int32 length (Protocol16.cs:892)
                if (setType) s.WriteByte(TagIntArray);
                WriteInt(s, v.Length);
                foreach (var i in v) WriteInt(s, i);
                break;
            case Hashtable v:
                if (setType) s.WriteByte(TagHashtable);
                WriteHashtableBody(s, v);
                break;
            case Dictionary<byte, object> v:
                if (setType) s.WriteByte(TagDictionary);
                WriteDictionaryBody(s, v);
                break;
            case object[] v:
                if (setType) s.WriteByte(TagObjectArray);
                WriteShort(s, (short)v.Length);
                foreach (var item in v) WriteValue(s, item, setType: true);
                break;
            case Array v when v.GetType().GetElementType()!.IsPrimitive || v.GetType().GetElementType() == typeof(string):
            {
                // generic typed array: [121][i16 len][elemTag] + untagged elements
                // (Protocol16.SerializeArray; PEAK gameplay events carry bool[] etc.)
                if (setType) s.WriteByte(TagArray);
                WriteShort(s, (short)v.Length);
                switch (v)
                {
                    case bool[] a:
                        s.WriteByte(TagBool);
                        foreach (var item in a) s.WriteByte(item ? (byte)1 : (byte)0);
                        break;
                    case byte[] a:
                        s.WriteByte(TagByte);
                        foreach (var item in a) s.WriteByte(item);
                        break;
                    case short[] a:
                        s.WriteByte(TagShort);
                        foreach (var item in a) WriteShort(s, item);
                        break;
                    case int[] a:
                        s.WriteByte(TagInt);
                        foreach (var item in a) WriteInt(s, item);
                        break;
                    case long[] a:
                        s.WriteByte(TagLong);
                        foreach (var item in a) WriteLong(s, item);
                        break;
                    case float[] a:
                        s.WriteByte(TagFloat);
                        foreach (var item in a) WriteFloat(s, item);
                        break;
                    case double[] a:
                        s.WriteByte(TagDouble);
                        foreach (var item in a) WriteDouble(s, item);
                        break;
                    case string[] a:
                        s.WriteByte(TagString);
                        foreach (var item in a) WriteStringBody(s, item);
                        break;
                    default:
                        throw new InvalidDataException(
                            $"unsupported array element type for P16: {v.GetType().GetElementType()!.Name}");
                }
                break;
            }
            case LbMessage v when v.IsEvent:
                if (setType) s.WriteByte(TagEventData);
                s.WriteByte(v.Code);
                WriteParameterTable(s, v.Parameters);
                break;
            default:
                throw new InvalidDataException($"unsupported type for P16: {value?.GetType().Name}");
        }
    }

    public static void WriteHashtableBody(MemoryStream s, Hashtable table)
    {
        WriteShort(s, (short)table.Count);
        foreach (var (key, value) in table)
        {
            WriteValue(s, key, setType: true);
            WriteValue(s, value, setType: true);
        }
    }

    private static void WriteDictionaryBody(MemoryStream s, Dictionary<byte, object> v)
    {
        s.WriteByte(TagByte);           // key header tag: concrete byte → untagged keys
        s.WriteByte(0);                 // value header tag: 0 (object) → tagged values
        WriteShort(s, (short)v.Count);
        foreach (var (key, val) in v)
        {
            s.WriteByte(key);
            WriteValue(s, val, setType: true);
        }
    }

    private static void WriteLong(MemoryStream s, long v)
    {
        WriteInt(s, (int)(v >> 32));
        WriteInt(s, (int)v);
    }

    private static void WriteFloat(MemoryStream s, float v)
    {
        var bytes = BitConverter.GetBytes(v);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        s.Write(bytes, 0, 4);
    }

    private static void WriteDouble(MemoryStream s, double v)
    {
        var bytes = BitConverter.GetBytes(v);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        s.Write(bytes, 0, 8);
    }

    // ---------------------------------------------------------------- read

    public static LbRequest ReadRequest(byte[] payload)
    {
        using var s = new MemoryStream(payload);
        int op = s.ReadByte();
        if (op < 0) throw new InvalidDataException("empty payload");
        return new LbRequest { Op = (byte)op, Parameters = ReadParameterTable(s) };
    }

    /// <summary>
    /// Decodes an encoded operation-response body ([op][i16 ret][tagged dbg][paramtable])
    /// back into an LbMessage. Used by tests to assert exact response shapes; values decode
    /// via the same ReadValue the request path uses (Raw passthrough for exotic content).
    /// </summary>
    public static bool TryDecodeResponse(byte[] payload, out LbMessage message)
    {
        message = new LbMessage();
        try
        {
            using var s = new MemoryStream(payload);
            int code = s.ReadByte();
            if (code < 0) return false;
            short returnCode = ReadShort(s);
            var debugTag = (byte)s.ReadByte();
            var debugMessage = ReadValue(s, debugTag) as string;
            message = new LbMessage
            {
                Code = (byte)code,
                ReturnCode = returnCode,
                DebugMessage = debugMessage,
                Parameters = ReadParameterTable(s),
            };
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes an encoded event body ([code][paramCount][key/value…]) back into an
    /// LbMessage. Used by the relay's cache-management paths and tests; values decode via
    /// the same ReadValue the request path uses (Raw passthrough for exotic content).
    /// </summary>
    public static bool TryDecodeEvent(byte[] payload, out LbMessage message)
    {
        message = new LbMessage { IsEvent = true };
        try
        {
            using var s = new MemoryStream(payload);
            int code = s.ReadByte();
            if (code < 0) return false;
            message = new LbMessage { IsEvent = true, Code = (byte)code, Parameters = ReadParameterTable(s) };
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            return false;
        }
    }

    public static Dictionary<byte, object> ReadParameterTable(MemoryStream s)
    {
        var result = new Dictionary<byte, object>();
        short count = ReadShort(s);
        for (int i = 0; i < count; i++)
        {
            byte key = (byte)s.ReadByte();
            byte tag = (byte)s.ReadByte();
            result[key] = ReadValue(s, tag);
        }
        return result;
    }

    public static short ReadShort(MemoryStream s)
    {
        int b0 = s.ReadByte();
        int b1 = s.ReadByte();
        if (b1 < 0) throw new EndOfStreamException();
        return (short)((b0 << 8) | b1);
    }

    public static int ReadInt(MemoryStream s) => (ReadShort(s) << 16) | (ReadShort(s) & 0xFFFF);

    public static object ReadValue(MemoryStream s, byte tag)
    {
        return tag switch
        {
            TagNull => null!,
            TagByte => (byte)s.ReadByte(),
            TagBool => s.ReadByte() != 0,
            TagShort => ReadShort(s),
            TagInt => ReadInt(s),
            TagLong => ReadLong(s),
            TagFloat => ReadFloat(s),
            TagDouble => ReadDouble(s),
            TagString => ReadStringBody(s),
            TagStringArray => ReadStringArray(s),
            TagArray => ReadGenericArray(s),
            TagByteArray => ReadByteArray(s),
            TagIntArray => ReadIntArray(s),
            TagHashtable => ReadHashtableBody(s),
            TagDictionary => ReadDictionaryBody(s),
            TagObjectArray => ReadObjectArray(s),
            TagEventData => ReadEventData(s),
            TagCustom => ReadCustom(s),
            TagOperationRequest => ReadRequestTagged(s),
            TagOperationResponse => ReadResponseTagged(s),
            _ => throw new InvalidDataException($"unknown P16 tag {tag}"),
        };
    }

    /// <summary>Custom types are preserved byte-exact: [99][code][short len][bytes].</summary>
    private static object ReadCustom(MemoryStream s)
    {
        int code = s.ReadByte();
        short len = ReadShort(s);
        if (len < 0) throw new InvalidDataException("negative custom type length");
        var result = new byte[4 + len];
        result[0] = TagCustom;
        result[1] = (byte)code;
        result[2] = (byte)(len >> 8);
        result[3] = (byte)len;
        int read = s.Read(result, 4, len);
        if (read != len) throw new EndOfStreamException();
        return new Raw(result);
    }

    private static object ReadRequestTagged(MemoryStream s)
    {
        int op = s.ReadByte();
        return new LbRequest { Op = (byte)op, Parameters = ReadParameterTable(s) };
    }

    private static object ReadResponseTagged(MemoryStream s)
    {
        int op = s.ReadByte();
        short ret = ReadShort(s);
        var dbgTag = (byte)s.ReadByte();
        var dbg = ReadValue(s, dbgTag) as string;
        return new LbMessage
        {
            Code = (byte)op,
            ReturnCode = ret,
            DebugMessage = dbg,
            Parameters = ReadParameterTable(s),
        };
    }

    private static object ReadEventData(MemoryStream s)
    {
        int code = s.ReadByte();
        return new LbMessage
        {
            Code = (byte)code,
            IsEvent = true,
            Parameters = ReadParameterTable(s),
        };
    }

    private static string ReadStringBody(MemoryStream s)
    {
        short len = ReadShort(s);
        if (len < 0) throw new InvalidDataException("negative string length");
        var bytes = new byte[len];
        int read = s.Read(bytes, 0, len);
        if (read != len) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Generic array: [121][short len][byte elemTag] then untagged elements.</summary>
    private static object ReadGenericArray(MemoryStream s)
    {
        short len = ReadShort(s);
        var elemTag = (byte)s.ReadByte();
        return elemTag switch
        {
            TagInt => ReadIntArray(s, len),
            TagString => ReadStringArray(s, len),
            TagByte => ReadByteArray(s, len),
            TagShort => ReadShortArray(s, len),
            TagLong => ReadLongArray(s, len),
            TagFloat => ReadFloatArray(s, len),
            TagDouble => ReadDoubleArray(s, len),
            TagBool => ReadBoolArray(s, len),
            _ => throw new InvalidDataException($"unsupported array element tag {elemTag}"),
        };
    }

    private static object ReadIntArray(MemoryStream s, int len)
    {
        var result = new int[len];
        for (int i = 0; i < len; i++) result[i] = ReadInt(s);
        return result;
    }

    private static object ReadStringArray(MemoryStream s, int len)
    {
        var result = new string[len];
        for (int i = 0; i < len; i++) result[i] = ReadStringBody(s);
        return result;
    }

    private static object ReadShortArray(MemoryStream s, int len)
    {
        var result = new short[len];
        for (int i = 0; i < len; i++) result[i] = ReadShort(s);
        return result;
    }

    private static object ReadLongArray(MemoryStream s, int len)
    {
        var result = new long[len];
        for (int i = 0; i < len; i++) result[i] = (long)ReadLong(s);
        return result;
    }

    private static object ReadFloatArray(MemoryStream s, int len)
    {
        var result = new float[len];
        for (int i = 0; i < len; i++) result[i] = (float)ReadFloat(s);
        return result;
    }

    private static object ReadDoubleArray(MemoryStream s, int len)
    {
        var result = new double[len];
        for (int i = 0; i < len; i++) result[i] = (double)ReadDouble(s);
        return result;
    }

    private static object ReadBoolArray(MemoryStream s, int len)
    {
        var result = new bool[len];
        for (int i = 0; i < len; i++) result[i] = s.ReadByte() != 0;
        return result;
    }

    private static object ReadByteArray(MemoryStream s, int len)
    {
        var bytes = new byte[len];
        int read = s.Read(bytes, 0, len);
        if (read != len) throw new EndOfStreamException();
        return bytes;
    }

    private static object ReadStringArray(MemoryStream s)
    {
        short len = ReadShort(s);
        return ReadStringArray(s, len);
    }

    private static object ReadByteArray(MemoryStream s)
    {
        int len = ReadInt(s);
        if (len < 0 || len > s.Length - s.Position) throw new InvalidDataException("bad byte[] length");
        var bytes = new byte[len];
        int read = s.Read(bytes, 0, len);
        if (read != len) throw new EndOfStreamException();
        return bytes;
    }

    private static object ReadIntArray(MemoryStream s)
    {
        // client's SerializeIntArrayOptimized writes int32 length (Protocol16.cs:892)
        int len = ReadInt(s);
        return ReadIntArray(s, len);
    }

    private static object ReadHashtableBody(MemoryStream s)
    {
        short count = ReadShort(s);
        var result = new Hashtable();
        for (int i = 0; i < count; i++)
        {
            var keyTag = (byte)s.ReadByte();
            var key = ReadValue(s, keyTag);
            var valTag = (byte)s.ReadByte();
            var value = ReadValue(s, valTag);
            result[key] = value;
        }
        return result;
    }

    private static object ReadDictionaryBody(MemoryStream s)
    {
        var keyTag = (byte)s.ReadByte();
        var valueTag = (byte)s.ReadByte();
        short count = ReadShort(s);
        var result = new Dictionary<byte, object>();
        for (int i = 0; i < count; i++)
        {
            var key = ReadValue(s, keyTag == 0 ? TagByte : keyTag); // header says byte keys
            var entryTag = (byte)s.ReadByte();                      // object values: per-entry tag
            var value = ReadValue(s, entryTag);
            result[(byte)key] = value;
        }
        return result;
    }

    private static object ReadObjectArray(MemoryStream s)
    {
        short len = ReadShort(s);
        var result = new object[len];
        for (int i = 0; i < len; i++)
        {
            var tag = (byte)s.ReadByte();
            result[i] = ReadValue(s, tag);
        }
        return result;
    }

    private static object ReadLong(MemoryStream s)
    {
        long hi = ReadInt(s);
        long lo = ReadInt(s) & 0xFFFFFFFFL;
        return (hi << 32) | lo;
    }

    private static object ReadFloat(MemoryStream s)
    {
        var bytes = new byte[4];
        if (s.Read(bytes, 0, 4) != 4) throw new EndOfStreamException();
        Array.Reverse(bytes);
        return BitConverter.ToSingle(bytes, 0);
    }

    private static object ReadDouble(MemoryStream s)
    {
        var bytes = new byte[8];
        if (s.Read(bytes, 0, 8) != 8) throw new EndOfStreamException();
        Array.Reverse(bytes);
        return BitConverter.ToDouble(bytes, 0);
    }
}

/// <summary>Deterministic ordered hashtable stand-in for the relay's LB state and tests.</summary>
public sealed class Hashtable : Dictionary<object, object?>
{
    public Hashtable() { }
    public Hashtable(int capacity) : base(capacity) { }
}
