# NetworkPerformanceSystem

**For groups playing together across long distances.** If your friends are 200ms+ away and the game
feels like mush no matter how much bandwidth everyone has, this is aimed at you.

## Why this is different from the other networking mods

Every other Valheim networking mod treats multiplayer performance as a **bandwidth** problem —
compression, bigger buffers, higher send rates, filtered broadcasts. That is the right diagnosis for
ten people in one base saturating a shared cap.

It does not explain why two friends on opposite sides of the world feel awful at 5% link
utilisation. Two things in vanilla punish *distance* specifically, and neither is about bandwidth:

**1. The send window is a fixed 10 KB of in-flight data.** Throughput through a fixed window is
`window ÷ round-trip-time`, so vanilla's window is correctly sized up to about 67ms and starves
everything past it — roughly 41 KB/s at 250ms, no matter how fast your connection is. And it fails
hard rather than gracefully: over the limit, that player receives *nothing* that tick. That is the
freeze half of freeze-then-teleport.

**2. Simulation authority is handed out by arrival order.** Valheim gives each object to one peer to
simulate, and everyone else sees it relayed through the host. Vanilla picks whoever happens to be
first in an internal list — uncorrelated with who is fighting the creature and uncorrelated with
latency. For a spread-out group that is close to the worst available choice, and it is why a mob can
feel laggy to everyone at once.

This mod sizes the window **per peer from measured round-trip time**, and places authority to
minimise how stale things look to the people actually watching. Notably, sizing by RTT is a **no-op
for players who were already fine** — simply raising the constant for everybody, which is what the
other mods do, hands a 20ms player tens of kilobytes of standing queue, which is latency added to
someone who did not have a problem.

## What it does

| | |
|---|---|
| **Per-peer send window** | Sized from measured RTT (bandwidth-delay product) instead of a fixed 10 KB. Applies in both directions, so a distant player's *uploads* stop being throttled too. |
| **Latency-aware ownership** | Assigns each object to whoever minimises perceived staleness, with hysteresis so ownership cannot thrash. Never takes an object somebody is actively driving. |
| **Send scheduler fix** | Vanilla services one peer per rendered frame, so the advertised 20Hz silently becomes ~5.5Hz at ten players. This sends to everyone each tick. |
| **Live position reporting** | Vanilla reports your position to the server only every 2 seconds, and the server uses it to decide both what to send you and who owns what. A 12-byte side channel keeps it current. |
| **Latency compensation** | Draws other players' creatures where they *are*, not where they were when the packet left. This is the one you feel. |
| **`nps_stats`** | Per-peer RTT, window size, and how often peers are being starved. Run `nps_stats collect`, play, then `nps_stats`. |

## Installing

**Server-only works.** Vanilla clients get the send window, ownership, and scheduler fixes with
nothing installed on their end.

**Installing on clients too** adds latency compensation and live position reporting for those
clients. Mixed groups are fine — benefits are per-player, and a client without the mod behaves
exactly as vanilla. There is **no version lock**: nobody gets kicked for not having it.

Works on dedicated servers and on player-hosted games.

## Known limitations

- On a **dedicated** server, contested objects go to the lowest-latency player present rather than
  to the server itself. The server only takes ownership of zones it has actually loaded, because
  owning something it is not simulating would freeze it. This is a deliberate safety limit.
- Latency compensation is dead reckoning: an entity that stops abruptly will overshoot slightly and
  settle back. Lower `LatencyCompensationStrength` if you find it distracting; `0` disables it.
- Requires the Steam backend. Crossplay/PlayFab connections do not report round-trip time, and
  without a measurement every mechanism falls back to vanilla behaviour rather than guessing.
- If the log shows `RttSampling disabled`, the server's Steam interface could not be queried and
  everything runs as vanilla; `nps_stats` shows the reason.
- Honest send windows mean a full server can actually use its bandwidth: 50 players at the
  default 150 KB/s target is ~60 Mbit/s of upload worst case. Backpressure degrades gracefully
  if the link is smaller, but provision the server's uplink for the player count rather than
  assuming vanilla's artificially starved usage.
- On large servers (20+ players), consider raising `Max Reassigns Per Pass` so ownership
  converges faster after groups move; the default is tuned for small-group play. The `Load
  Penalty Ms` setting spreads contested objects across low-latency peers instead of piling
  everything on the single lowest-ping player - leave it on unless you specifically want pure
  staleness placement.

## Incompatible with

Other networking mods that rewrite the same code: FiresGhettoNetworking, VBNetTweaks, SkadiNet,
BetterNetworking, Smoothbrain's Network, NetworkTweaks, WarheimNetwork, TimeoutLimit. BepInEx will
refuse to load this mod alongside them rather than leave you half-patched.

**Verified compatible** (different layers, no overlap): LeanNet, Compress, EnRoute, BetterZeeRouter,
Scenic. ReturnToSender is fine too — it already fixes the scheduler, so that one mechanism stands
down and the rest keeps working.

## Changelog

See CHANGELOG.md.
