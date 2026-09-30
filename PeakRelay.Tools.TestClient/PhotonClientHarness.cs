using System;
using System.Collections.Generic;
using System.Threading;
using ExitGames.Client.Photon;
using Photon.Realtime;
using PeakRelay.Protocol;

namespace PeakRelay.Tools.TestClient;

/// <summary>
/// A headless LoadBalancingClient running the real Photon3 stack over RelayTestSocket:
/// full Realtime state machine (auth → master → redirect → game → room ops) with
/// GpBinaryV16 serialization, Service()-pumped on a timer thread.
/// </summary>
public sealed class PhotonClientHarness : LoadBalancingClient
{
    private readonly AutoResetEvent _connectedEvent = new(false);
    private readonly AutoResetEvent _responseEvent = new(false);
    private readonly AutoResetEvent _eventEvent = new(false);
    private readonly Timer _pump;
    private int _lastResponseCount;
    private int _lastEventCount;

    public readonly List<OperationResponse> Responses = new();
    public readonly List<EventData> Events = new();
    public string? LastError;

    public PhotonClientHarness(string userId) : base(ConnectionProtocol.Udp)
    {
        SerializationProtocol = SerializationProtocol.GpBinaryV16;
        LoadBalancingPeer.SocketImplementationConfig[ConnectionProtocol.Udp] = typeof(RelayTestSocket);
        MasterServerAddress = $"{TestConfig.Host}:{TestConfig.Port}";
        AppId = "LoadBalancing";
        AppVersion = "peakrelay-test_1.0";
        AuthMode = AuthModeOption.Auth;
        AuthValues = new AuthenticationValues { UserId = userId };
        _pump = new Timer(_ =>
        {
            try
            {
                Service();
            }
            catch (Exception ex)
            {
                // describe without ToString (type-load failures break it)
                Console.Error.WriteLine($"[harness {userId}] pump exception: {Describe(ex)}");
            }
        }, null, 0, 10);
    }

    public new bool Connect()
    {
        return ConnectToMasterServer();
    }

    public bool WaitForConnected(int ms)
    {
        if (!_connectedEvent.WaitOne(ms))
            return false;
        var deadline = Environment.TickCount + ms;
        while (Environment.TickCount < deadline)
        {
            if (LoadBalancingPeer.PeerState == PeerStateValue.Connected && Server == ServerConnection.MasterServer)
                return true;
            Thread.Sleep(20);
        }
        return false;
    }

    public bool WaitForNextResponse(int ms)
    {
        int before = Volatile.Read(ref _lastResponseCount);
        if (!_responseEvent.WaitOne(ms))
            return false;
        Volatile.Write(ref _lastResponseCount, Responses.Count);
        return Responses.Count > before;
    }

    public bool WaitForNextEvent(int ms)
    {
        int before = Volatile.Read(ref _lastEventCount);
        if (!_eventEvent.WaitOne(ms))
            return false;
        Volatile.Write(ref _lastEventCount, Events.Count);
        return Events.Count > before;
    }

    public override void DebugReturn(DebugLevel level, string message)
    {
        // base routes to UnityEngine.Debug.Log* — Unity ECALLs throw on plain .NET.
        if (level <= DebugLevel.ERROR)
            Console.WriteLine($"[photon] {level}: {message}");
    }

    public override void OnStatusChanged(StatusCode statusCode)
    {
        // base.OnStatusChanged constructs SystemConnectionSummary for several statuses, which
        // calls Application.internetReachability — Unity-only, throws on plain .NET.
        // Statuses that DON'T hit the SCS path are forwarded to base untouched (they drive the
        // whole state machine); SCS-triggering ones are handled here without base.
        bool scsStatus = statusCode is StatusCode.SecurityExceptionOnConnect
            or StatusCode.ExceptionOnConnect
            or StatusCode.EncryptionFailedToEstablish
            or StatusCode.Exception
            or StatusCode.SendError
            or StatusCode.ExceptionOnReceive
            or StatusCode.DisconnectByServerTimeout
            or StatusCode.TimeoutDisconnect
            or StatusCode.DisconnectByServerLogic
            or StatusCode.DisconnectByServerReasonUnknown;

        if (scsStatus)
        {
            LastError = $"status: {statusCode}";
            if (statusCode == StatusCode.DisconnectByServerLogic)
            {
                DisconnectedCause = DisconnectCause.DisconnectByServerLogic;
                State = ClientState.Disconnecting;
            }
            else if (statusCode == StatusCode.DisconnectByServerReasonUnknown)
            {
                DisconnectedCause = DisconnectCause.DisconnectByServerReasonUnknown;
                State = ClientState.Disconnecting;
            }
            else
            {
                DisconnectedCause = DisconnectCause.Exception;
                State = ClientState.Disconnecting;
            }
            return;
        }

        if (statusCode == StatusCode.Connect)
            _connectedEvent.Set();
        base.OnStatusChanged(statusCode);
    }

    public override void OnOperationResponse(OperationResponse response)
    {
        if (response.OperationCode is 230 or 231)
        {
            var keys = new List<string>();
            foreach (var (k, v) in response.Parameters)
                keys.Add($"{k}:{v?.GetType().Name}={Truncate(v)}");
            Console.WriteLine($"[harness {UserId}] auth-resp rc={response.ReturnCode} server={Server} params=[{string.Join(", ", keys)}] localUserId={LocalPlayer?.UserId}");
        }
        Responses.Add(response);
        _responseEvent.Set();
        base.OnOperationResponse(response);
    }

    private static string Truncate(object? v)
    {
        var s = v is byte[] b ? Convert.ToHexString(b, 0, Math.Min(b.Length, 8)) : v?.ToString() ?? "null";
        return s.Length > 40 ? s[..40] + "…" : s;
    }

    public override void OnEvent(EventData photonEvent)
    {
        Events.Add(photonEvent);
        _eventEvent.Set();
        base.OnEvent(photonEvent);
    }

    public void Shutdown()
    {
        _pump.Dispose();
        Disconnect();
    }

    private static string Describe(Exception? ex)
    {
        var parts = new List<string>();
        while (ex != null)
        {
            parts.Add($"{ex.GetType().FullName}: {ex.Message}");
            ex = ex.InnerException;
        }
        return string.Join(" -> ", parts);
    }
}
