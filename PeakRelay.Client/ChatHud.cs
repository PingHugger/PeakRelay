using System;
using System.Collections.Generic;
using ExitGames.Client.Photon;
using PeakRelay.Protocol;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace PeakRelay.Client;

/// <summary>
/// Renders PeakRelay server announcements (custom PUN event 199, see
/// PeakRelay.Protocol.ChatEvent) as a small banner stack top-center.
///
/// MonoBehaviourPunCallbacks self-registers with the PUN client, so OnEvent delivers the
/// game's traffic too — anything that is not our code 199 is forwarded untouched (the
/// callback is passive). PEAK itself has no text chat (verified against the decompiled
/// game), so this HUD is the only surface that consumes the event.
///
/// Rendering is IMGUI (module already referenced by the shim): a fixed top-center stack of
/// the last few messages, each fading out after a few seconds. No scene objects, no
/// prefabs, no game-UI integration — it cannot break the game's own interface.
/// </summary>
public sealed class ChatHud : MonoBehaviourPunCallbacks
{
    private sealed class Entry
    {
        public string Text;
        public float ShownAt;
        public float Lifetime;
    }

    private readonly List<Entry> _entries = new();
    private GUIStyle? _style;
    private bool _subscribed;

    /// <summary>How long a message stays fully visible before fading.</summary>
    private const float HoldSeconds = 7f;

    /// <summary>Fade-out duration after the hold.</summary>
    private const float FadeSeconds = 2f;

    /// <summary>Maximum messages on screen at once (oldest drop off first).</summary>
    private const int MaxEntries = 5;

    private void OnEnable()
    {
        // Instance registrations survive across rooms; PUN keeps delivering while we exist.
        if (!_subscribed)
        {
            PhotonNetwork.AddCallbackTarget(this);
            _subscribed = true;
        }
    }

    private void OnDisable()
    {
        if (_subscribed)
        {
            PhotonNetwork.RemoveCallbackTarget(this);
            _subscribed = false;
        }
    }

    /// <summary>
    /// Raw event tap: only PeakRelay chat (code 199) is consumed, everything else is
    /// ignored here and continues to the game's own handlers untouched.
    /// </summary>
    public void OnEvent(EventData eventData)
    {
        if (eventData == null || eventData.Code != ChatEvent.EventCode)
            return;
        var decoded = ChatEvent.TryDecode(eventData.CustomData);
        if (decoded == null)
            return; // malformed — ignore, never crash on foreign traffic
        var (from, text) = decoded.Value;
        Announce($"{from}: {text}");
    }

    /// <summary>Room teardown: nothing to announce about an empty room.</summary>
    public override void OnLeftRoom()
    {
        _entries.Clear();
    }

    private void Announce(string text)
    {
        _entries.Add(new Entry { Text = text, ShownAt = Time.unscaledTime, Lifetime = HoldSeconds + FadeSeconds });
        while (_entries.Count > MaxEntries)
            _entries.RemoveAt(0);
    }

    private void OnGUI()
    {
        if (_entries.Count == 0)
            return;

        _style ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = 20,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperCenter,
            wordWrap = true,
        };

        var now = Time.unscaledTime;
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            if (now - _entries[i].ShownAt > _entries[i].Lifetime)
            {
                _entries.RemoveAt(i);
            }
        }

        var width = Mathf.Min(560f, Screen.width * 0.7f);
        var x = (Screen.width - width) / 2f;
        var y = 24f;

        foreach (var entry in _entries)
        {
            var age = now - entry.ShownAt;
            var alpha = age <= HoldSeconds ? 1f : 1f - (age - HoldSeconds) / FadeSeconds;
            var content = new GUIContent(entry.Text);
            var height = _style.CalcHeight(content, width);

            var previous = GUI.color;
            GUI.color = new Color(previous.r, previous.g, previous.b, Mathf.Clamp01(alpha));
            var box = new Rect(x, y, width, height + 10f);

            // shadow + text for readability on any background
            var shadow = new Rect(box.x + 1, box.y + 1, box.width, box.height);
            GUI.Label(shadow, content, ShadowStyle(_style));
            GUI.Label(box, content, _style);

            GUI.color = previous;
            y += height + 14f;
        }
    }

    private static GUIStyle _shadowStyle;

    private static GUIStyle ShadowStyle(GUIStyle baseStyle)
    {
        if (_shadowStyle == null)
        {
            _shadowStyle = new GUIStyle(baseStyle)
            {
                normal = { textColor = new Color(0f, 0f, 0f, 0.85f) },
            };
        }
        return _shadowStyle;
    }
}
