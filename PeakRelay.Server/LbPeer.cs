using System;
using System.Collections.Concurrent;
using PeakRelay.Protocol;

namespace PeakRelay.Server;

/// <summary>
/// LoadBalancing-layer state for one connected client. The outbound queue is thread-safe:
/// the session's reader thread (op dispatch) and OTHER sessions' reader threads (event
/// broadcast to room members) enqueue here; this session's writer thread is the only
/// consumer and delivers the messages to the client.
/// </summary>
public sealed class LbPeer
{
    public required EnetPeer Enet { get; init; }

    /// <summary>Master until the client re-auths with a token on its game connection.</summary>
    public RelayServerRole Role { get; set; } = RelayServerRole.Master;

    public int ActorNumber { get; set; }

    public bool Authenticated { get; set; }

    public LbRoom? Room { get; set; }

    public string? UserId { get; set; }

    public string? GameVersion { get; set; }

    public Hashtable PlayerProperties { get; } = new();

    /// <summary>(P16 body, isEvent) pairs awaiting delivery; consumer is the writer thread.</summary>
    public readonly ConcurrentQueue<(byte[] Bytes, bool IsEvent)> LbOutbound = new();

    public void EnqueueResponse(byte[] p16Body) => LbOutbound.Enqueue((p16Body, IsEvent: false));

    public void EnqueueLbEvent(byte[] p16Body) => LbOutbound.Enqueue((p16Body, IsEvent: true));
}
