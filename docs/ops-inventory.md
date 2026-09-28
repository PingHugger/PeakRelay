# PEAK ops-inventory (live-capture template)

> **Status: PENDING LIVE CAPTURE.** The M0 tooling (shim + collector + summarizer) is built
> and validated against synthetic data, but no PEAK session has been captured yet — that
> requires launching the game (Steam session, GPU). Fill every section from real traces,
> one capture per scenario, saved as `traces/<scenario>.jsonl`.

## Capture matrix

| # | Scenario | Trace file | Captured |
|---|---|---|---|
| 1 | Vanilla Photon Cloud: menu → host → one full run | `traces/vanilla-host.jsonl` | ☐ |
| 2 | Vanilla Photon Cloud: join via room code | `traces/vanilla-join.jsonl` | ☐ |
| 3 | LocalMP self-hosted baseline (same flow as 1) | `traces/localmp-host.jsonl` | ☐ |
| 4 | Lobby interactions: invite/kick/leave/rejoin | `traces/lobby.jsonl` | ☐ |
| 5 | Voice chat active vs muted (compare) | `traces/voice-on.jsonl` | ☐ |
| 6 | Disconnect scenarios (host migrate, hard quit) | `traces/disconnect.jsonl` | ☐ |

## Sections to fill per capture

1. **Auth flow** — `Authenticate` op payload keys (Steam/Epic ticket fields present?),
   response shape, token refresh cadence.
2. **Join/create sequence** — exact op order CreateGame/JoinGame/JoinRandomGame, the
   `gameVersion`/AppVersion strings sent, expected failures and error codes.
3. **Actor assignment** — actor numbering on join/leave, master-client election events.
4. **Property operations** — which properties are set at room creation vs during play,
   broadcast-vs-send targets, cache slice usage.
5. **Event traffic profile** — event codes seen, per-code size/frequency, channel usage.
6. **Ping cadence** — observed CT_PING interval, RTT field behavior.
7. **Voice endpoints** — separate voice peer? Same app or `voice` app? Address/port?
8. **MTU / fragmentation** — observed datagram sizes, CT_SEND_FRAGMENT frequency and
   fragment sizes (informs M1's relay envelope sizing and M3's UDP fast path).

## Known breakages to confirm/refute on self-hosted paths (from LocalMP docs)

- Campfire scrolls: present but vanish when opened.
- Steam invites: nonfunctional; room codes are the join flow.
- (M2 retest) Achievements, progression, customization: expected to work — confirm.
