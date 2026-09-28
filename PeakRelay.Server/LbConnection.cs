using System;
using System.Collections.Generic;
using PeakRelay.Protocol;

namespace PeakRelay.Server;

/// <summary>Which server role this connection currently emulates.</summary>
public enum RelayServerRole
{
    /// <summary>Client connected for the first time (auth + CreateGame/JoinGame happen here).</summary>
    Master,

    /// <summary>Client reconnected after we redirected it to itself (real room ops happen here).</summary>
    Game,
}

/// <summary>LB ops the connection layer handles internally (rest are forwarded).</summary>
internal static class InternalOps
{
    public const byte Init = 0;          // PhotonCodes.InitEncryption — server replies ServerKey
}

/// <summary>
/// Photon message-level server state for one TCP session. Implements the [243][msgType]
/// framing with payload encryption, the DH key exchange (internal op 0), and the
/// master/game role split the Realtime client drives:
///
///   master:  Authenticate(230/231) → Ok {221: token} · CreateGame(227)/JoinGame(226) →
///            Ok {230: "same-host:port"} (client then disconnects and reconnects; role → Game)
///   game:    Authenticate(230/231) with token → Ok {221: token} · CreateGame/JoinGame →
///            Ok {255: name, 254: actorNr, 252: actorList} + Join event broadcast
/// </summary>
public sealed class LbConnection
{
    public const byte MsgType_Init = 0;              // unused by clients (legacy init v2)
    public const byte MsgType_InitResponse = 1;
    public const byte MsgType_Operation = 2;
    public const byte MsgType_OperationResponse = 3;
    public const byte MsgType_Event = 4;
    public const byte MsgType_InternalOperationRequest = 6;
    public const byte MsgType_InternalOperationResponse = 7;
    public const byte MsgType_EncryptFlag = 0x80;

    private const byte OpInitEncryption = 0;   // PhotonCodes.InitEncryption
    private const byte ParamClientKey = 1;     // PhotonCodes.ClientKey
    private const byte ParamServerKey = 1;     // PhotonCodes.ServerKey

    public RelayServerRole Role { get; set; } = RelayServerRole.Master;

    public bool EncryptionEstablished { get; private set; }

    public SessionCrypto Crypto { get; } = new();

    /// <summary>Decrypted, deframed operation requests awaiting dispatch (in order).</summary>
    public readonly Queue<(LbRequest Request, bool WasEncrypted)> PendingOps = new();

    /// <summary>LB events/responses encoded and ready to send (in order).</summary>
    public readonly Queue<byte[]> LbOutbound = new();

    public void HandleClientPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= 2 && (payload[0] == 243 || payload[0] == 253) && payload[1] == MsgType_Init)
        {
            // legacy init request (PeerBase.WriteInitRequest): reply with an empty InitResponse;
            // the client's InitCallback completes the transport handshake (StatusCode.Connect)
            LbOutbound.Enqueue(Wrap(Array.Empty<byte>(), MsgType_InitResponse, encrypted: false));
            return;
        }

        if (payload.Length < 2 || payload[0] != 243)
            return; // not a regular message (253/other prefixes unsupported)
        byte typeByte = payload[1];
        bool encrypted = (typeByte & MsgType_EncryptFlag) != 0;
        byte msgType = (byte)(typeByte & 0x7F);
        var body = payload[2..].ToArray();

        if (encrypted)
        {
            if (!EncryptionEstablished)
                return; // can't decrypt; drop
            body = Crypto.Decrypt(body);
        }

        switch (msgType)
        {
            case MsgType_InternalOperationRequest:
                HandleInternalRequest(body);
                break;
            case MsgType_Operation:
                LbRequest request;
                try
                {
                    request = P16.ReadRequest(body);
                }
                catch (Exception ex)
                {
                    // log and skip: an op we cannot parse must not kill the session
                    Console.Error.WriteLine($"[relay-lb] op parse failed ({ex.GetType().Name}: {ex.Message}); payload {body.Length}B");
                    return;
                }
                if (request.Op == InternalOps.Init && !EncryptionEstablished)
                {
                    // client chose the internal-op key exchange instead of the legacy Init
                    HandleKeyExchange(request);
                }
                else
                {
                    PendingOps.Enqueue((request, encrypted));
                }
                break;
        }
    }

    private void HandleInternalRequest(byte[] body)
    {
        var request = P16.ReadRequest(body);
        if (request.Op == OpInitEncryption)
            HandleKeyExchange(request);
    }

    private void HandleKeyExchange(LbRequest request)
    {
        if (!request.Parameters.TryGetValue(ParamClientKey, out var key) || key is not byte[] clientKey)
            return;
        Crypto.DeriveSharedKey(clientKey);
        EncryptionEstablished = true;

        var responseParams = new Dictionary<byte, object>
        {
            [ParamServerKey] = Crypto.PublicKey,
        };
        var response = P16.EncodeResponse(new LbMessage
        {
            Code = OpInitEncryption,
            ReturnCode = 0,
            Parameters = responseParams,
        });
        LbOutbound.Enqueue(Wrap(response, MsgType_InternalOperationResponse, encrypted: false));
    }

    /// <summary>Wrap a P16 op-response/event body into the [243][type] message format.</summary>
    public byte[] Wrap(byte[] p16Body, byte msgType, bool encrypted)
    {
        if (encrypted)
        {
            var enc = Crypto.Encrypt(p16Body);
            var msg = new byte[2 + enc.Length];
            msg[0] = 243;
            msg[1] = (byte)(msgType | MsgType_EncryptFlag);
            Buffer.BlockCopy(enc, 0, msg, 2, enc.Length);
            return msg;
        }
        var plain = new byte[2 + p16Body.Length];
        plain[0] = 243;
        plain[1] = msgType;
        Buffer.BlockCopy(p16Body, 0, plain, 2, p16Body.Length);
        return plain;
    }
}
