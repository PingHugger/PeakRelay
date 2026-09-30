using System;
using System.Collections.Generic;
using ExitGames.Client.Photon;
using PeakRelay.Protocol;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace PeakRelay.Client;

/// <summary>
/// PeakRelay chat UI for players: renders server announcements (event 199, raised by the
/// dedicated console) AND player chat (event 198, raised by this HUD's input box), as a
/// banner stack top-left. Press Y (or Enter) to open the input, type, Enter to send,
/// Escape to close — keys are consumed while the box is open so the game never sees them.
///
/// PEAK itself has no text chat (verified against the decompiled game); the relay routes
/// our events like any game event — it stamps param 254 (sender actor number), which the
/// HUD resolves to a player name. Vanilla clients ignore both codes. Events the game does
/// use flow through OnEvent untouched (the callback is passive).
///
/// Implementation constraints (release-runner compile finds):
/// - NO UnityEngine.Input: the InputLegacyModule is not in every provisioned lib/ set, and
///   OnGUI key events cover the same need portably;
/// - OnEnable/OnDisable are NOT overridden (newer MonoBehaviourPunCallbacks defines them
///   non-virtual) — callback registration happens lazily in OnEvent/Update instead;
/// - all fields are initialized inline to satisfy nullable analysis under any SDK.
///
/// Rendering is IMGUI only: no scene objects, no prefabs, no game-UI integration — it
/// cannot break the game's own interface.
/// </summary>
public sealed class ChatHud : MonoBehaviourPunCallbacks
{
    private sealed class Entry
    {
        public string Text = "";
        public float ShownAt;
        public float Lifetime;
        public bool IsServer;
    }

    private readonly List<Entry> _entries = new();
    private GUIStyle? _style;
    private GUIStyle? _serverStyle;
    private GUIStyle? _boxStyle;
    private GUIStyle? _inputStyle;
    private GUIStyle? _shadowStyle;
    private bool _subscribed;

    private string _input = "";
    private bool _inputOpen;
    private bool _focusNextFrame;

    /// <summary>How long a message stays fully visible before fading (seconds).</summary>
    private const float HoldSeconds = 9f;

    /// <summary>Fade-out duration after the hold.</summary>
    private const float FadeSeconds = 2f;

    /// <summary>Maximum messages on screen at once (oldest drop off first).</summary>
    private const int MaxEntries = 6;

    /// <summary>
    /// Lazily registers with the PUN callback list (first Update/OnGUI after Awake). Done
    /// here instead of OnEnable because MonoBehaviourPunCallbacks declares OnEnable on some
    /// PUN builds, which would make a plain declaration a compile error.
    /// </summary>
    private void EnsureSubscribed()
    {
        if (_subscribed)
            return;
        try
        {
            PhotonNetwork.AddCallbackTarget(this);
            _subscribed = true;
        }
        catch (Exception)
        {
            // PUN not ready yet — retried next frame; never throw from the HUD
        }
    }

    private void Update()
    {
        EnsureSubscribed();
    }

    private void OpenInput()
    {
        _inputOpen = true;
        _input = "";
        _focusNextFrame = true;
    }

    private void CloseInput()
    {
        _inputOpen = false;
        _input = "";
    }

    /// <summary>
    /// Raw event tap: consumes only PeakRelay chat codes (198 player, 199 server); every
    /// other event continues to the game's own handlers untouched.
    /// </summary>
    public void OnEvent(EventData eventData)
    {
        EnsureSubscribed();
        byte? kind = eventData?.Code switch
        {
            ChatEvent.ServerEventCode => ChatEvent.ServerEventCode,
            ChatEvent.PlayerEventCode => ChatEvent.PlayerEventCode,
            _ => null,
        };
        if (kind == null)
            return;

        var decoded = ChatEvent.TryDecode(kind.Value, eventData!.CustomData);
        if (decoded == null)
            return; // malformed — ignore, never crash on foreign traffic

        var from = decoded.From;
        if (kind == ChatEvent.PlayerEventCode)
        {
            // the relay stamps the raising actor into param 254 (Sender); resolve to a name,
            // falling back to the label the sender chose for themselves
            var actor = eventData.Parameters.TryGetValue(254, out var a) ? a as int? : null;
            var player = actor != null ? PhotonNetwork.CurrentRoom?.GetPlayer(actor.Value) : null;
            if (player != null && !string.IsNullOrWhiteSpace(player.NickName))
                from = player.NickName;
        }

        Announce(new Entry
        {
            Text = $"{from}: {decoded.Text}",
            ShownAt = Time.unscaledTime,
            Lifetime = HoldSeconds + FadeSeconds,
            IsServer = kind == ChatEvent.ServerEventCode,
        });
    }

    /// <summary>Room teardown: nothing to say about an empty room.</summary>
    public override void OnLeftRoom()
    {
        _entries.Clear();
        CloseInput();
    }

    private void Announce(Entry entry)
    {
        _entries.Add(entry);
        while (_entries.Count > MaxEntries)
            _entries.RemoveAt(0);
    }

    /// <summary>Sends the typed message to everyone in the room.</summary>
    private void Send()
    {
        var text = _input.Trim();
        CloseInput();
        if (text.Length == 0)
            return;
        if (text.Length > ChatEvent.MaxLength)
            text = text[..ChatEvent.MaxLength];

        var peer = PhotonNetwork.NetworkingClient?.LoadBalancingPeer;
        if (peer == null || !PhotonNetwork.InRoom)
        {
            AnnounceSystem("PeakRelay: not connected — message not sent");
            return;
        }

        var from = string.IsNullOrWhiteSpace(PhotonNetwork.LocalPlayer?.NickName)
            ? "Player"
            : PhotonNetwork.LocalPlayer.NickName.Trim();
        if (from.Length > ChatEvent.MaxFromLength)
            from = from[..ChatEvent.MaxFromLength];

        var options = new RaiseEventOptions { Receivers = ReceiverGroup.All };
        var ok = peer.OpRaiseEvent(ChatEvent.PlayerEventCode,
            ChatEvent.CreatePayload(from, text), options, SendOptions.SendReliable);
        if (!ok)
            AnnounceSystem("PeakRelay: the network rejected your message");
        // success feedback arrives as the event echoes back via the relay (ReceiverGroup.All
        // includes the sender), so no local optimistic echo is needed
    }

    private void AnnounceSystem(string text)
    {
        Announce(new Entry
        {
            Text = text,
            ShownAt = Time.unscaledTime,
            Lifetime = HoldSeconds + FadeSeconds,
            IsServer = false,
        });
    }

    private void OnGUI()
    {
        EnsureSubscribed();

        // open hotkey via GUI events (no UnityEngine.Input: the InputLegacyModule is not
        // provisioned in every lib/ set — release-runner compile find)
        if (!_inputOpen && PhotonNetwork.InRoom && Event.current.type == EventType.KeyDown &&
            Event.current.keyCode is KeyCode.Y or KeyCode.Return or KeyCode.KeypadEnter)
        {
            OpenInput();
        }

        DrawEntries();
        if (_inputOpen)
            DrawInput();
    }

    private void DrawEntries()
    {
        if (_entries.Count == 0)
            return;

        _style ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft,
            wordWrap = false,
            normal = { textColor = new Color(0.92f, 0.95f, 1f) },
        };
        _serverStyle ??= new GUIStyle(_style)
        {
            normal = { textColor = new Color(1f, 0.85f, 0.4f) },
        };
        _shadowStyle ??= new GUIStyle(_style)
        {
            normal = { textColor = new Color(0f, 0f, 0f, 0.85f) },
        };

        var now = Time.unscaledTime;
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            if (now - _entries[i].ShownAt > _entries[i].Lifetime)
                _entries.RemoveAt(i);
        }

        var width = Mathf.Min(560f, Screen.width * 0.7f);
        var y = 24f;

        foreach (var entry in _entries)
        {
            var age = now - entry.ShownAt;
            var alpha = age <= HoldSeconds ? 1f : 1f - (age - HoldSeconds) / FadeSeconds;
            var content = new GUIContent(entry.Text);
            var height = _style.CalcHeight(content, width);

            var previous = GUI.color;
            GUI.color = new Color(previous.r, previous.g, previous.b, Mathf.Clamp01(alpha));

            var style = entry.IsServer ? _serverStyle : _style;
            GUI.Label(new Rect(11f, y + 1f, width, height), content, _shadowStyle);
            GUI.Label(new Rect(10f, y, width, height), content, style);

            GUI.color = previous;
            y += height + 4f;
        }
    }

    private void DrawInput()
    {
        _boxStyle ??= new GUIStyle(GUI.skin.box);
        _inputStyle ??= new GUIStyle(GUI.skin.textField)
        {
            fontSize = 15,
            fontStyle = FontStyle.Bold,
        };

        var width = Mathf.Min(560f, Screen.width * 0.7f);
        var box = new Rect(10f, Screen.height - 60f, width, 34f);
        GUI.Box(box, GUIContent.none, _boxStyle);

        if (_focusNextFrame)
        {
            _focusNextFrame = false;
            GUI.FocusControl("peakrelay-chat-input");
        }
        GUI.SetNextControlName("peakrelay-chat-input");
        _input = GUI.TextField(new Rect(box.x + 8f, box.y + 7f, box.width - 16f, 22f), _input, ChatEvent.MaxLength, _inputStyle);

        var enter = Event.current.keyCode is KeyCode.Return or KeyCode.KeypadEnter &&
                    Event.current.type == EventType.KeyUp;
        var escape = Event.current.keyCode == KeyCode.Escape && Event.current.type == EventType.KeyUp;
        if (enter)
        {
            Event.current.Use();
            Send();
        }
        else if (escape)
        {
            Event.current.Use();
            CloseInput();
        }
    }
}
