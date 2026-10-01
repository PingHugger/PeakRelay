using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PeakRelay.Protocol;
using PeakRelay.Server;
using PeakRelay.Tools.TestClient;
using Photon.Realtime;
using Xunit;
using EventData = ExitGames.Client.Photon.EventData;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace PeakRelay.Conformance.Tests;

/// <summary>
/// In-proc relay fixture: binds ephemeral ports, points the TestConfig endpoint at it and
/// provides PhotonClientHarness factories + wait helpers. Every test runs the REAL Photon3
/// client stack against the real relay transport — the layer unit tests cannot see.
/// </summary>
public sealed class RelayConformanceFixture : IDisposable
{
    public RelayConformanceFixture()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var gamePort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var httpListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        httpListener.Start();
        HttpPort = ((System.Net.IPEndPoint)httpListener.LocalEndpoint).Port;
        httpListener.Stop();

        Relay = new RelayServer(gamePort, HttpPort);
        TestConfig.Configure("127.0.0.1", gamePort, HttpPort);
    }

    public RelayServer Relay { get; }

    public int HttpPort { get; }

    public PhotonClientHarness NewClient(string userId)
    {
        var client = new PhotonClientHarness(userId);
        _clients.Add(client);
        return client;
    }

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            try { client.Shutdown(); } catch { /* teardown best effort */ }
        }
        Relay.Dispose();
    }

    private readonly List<PhotonClientHarness> _clients = new();

    public const int TimeoutMs = 20000;

    public static bool WaitUntil(Func<bool> condition, int ms = TimeoutMs)
    {
        var deadline = Environment.TickCount + ms;
        while (Environment.TickCount < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(25);
        }
        return condition();
    }

    public static List<EventData> CachedEvents(PhotonClientHarness client, byte code) =>
        client.Events.Where(e => e.Code == code).ToList();

    /// <summary>Waits for the client to sit in a room (post game-connection join).</summary>
    public static void WaitUntilInRoom(PhotonClientHarness client, string actorCheck)
    {
        Assert.True(WaitUntil(() => client.State == ClientState.Joined),
            $"{actorCheck} never reached Joined (state={client.State}, error={client.LastError})");
    }
}

[CollectionDefinition("RelayConformance")]
public sealed class RelayConformanceCollection : ICollectionFixture<RelayConformanceFixture>;
