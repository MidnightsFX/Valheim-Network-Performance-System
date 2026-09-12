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
| **Relay filtering** | Vanilla relays every footstep, swing, damage number and destroyed object to every player on the server, who then discards it unless they can see it. The host now relays only to the players who can. Nothing visible changes; on a busy server this is most of the relay traffic. |
| **Configurable player limit** | Vanilla is hard-wired to 10. Set your own — and it is set in all four places the game keeps the number: the check that enforces it, the Steam and crossplay lobbies the server browser reads its `x / y` from, and the crossplay Party network, which has no UI at all and is the lowest ceiling of the four. |
| **Configurable timeouts** | Vanilla gives up on a quiet connection after 30 seconds, which is not enough for a slow link mid-join. Raise it — in both places the game times out, since the shorter one is what actually fires. |
| **`nps_stats`** | Per-peer RTT, window size, and how often peers are being starved. Open to anyone. Run `nps_stats_collect` (needs `devcommands`, since sampling costs a Steam call per peer per tick), play, then `nps_stats`. |

## Installing

**Server-only works.** Vanilla clients get the send window, ownership, and scheduler fixes with
nothing installed on their end.

**Installing on clients too** adds latency compensation and live position reporting for those
clients. Mixed groups are fine — benefits are per-player, and a client without the mod behaves
exactly as vanilla. There is **no version lock**: nobody gets kicked for not having it.

Works on dedicated servers and on player-hosted games.

## Known limitations

- On a **dedicated** server, contested objects go to the lowest-latency player present rather than
  to the server itself - except in the zones the server has actually loaded, which are the ones
  around the world origin. There, whenever two or more players are present, the server wins
  contested objects (it is zero hops from everyone, so that is the lowest possible staleness) and
  simulates them. That is intended and is a CPU cost to plan for on a busy spawn hub; set
  `Allow Host As Owner` to false to place purely on players. The server never takes ownership of
  zones it has not loaded, because owning something it is not simulating would freeze it.
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
- On large servers the two knobs that matter are `Frame Budget Ms` (how much of each server frame
  the send path may use - under load the per-peer send rate degrades gracefully instead of the
  frame time growing without bound; `nps_stats` shows the effective rate) and `Max Reassigns Per
  Pass`, which auto-scales with player count so ownership converges at the same per-player rate on
  a full server as in a small group. The `Load Penalty Ms` setting spreads contested objects
  across low-latency peers instead of piling everything on the single lowest-ping player - leave
  it on unless you specifically want pure staleness placement.
- `Max Players` raises vanilla's cap of 10, but a **crossplay** server cannot go past 128 whatever
  it is set to - PlayFab's lobbies do not hold more, and crossplay clients join that lobby before
  the server ever sees them. Steam-only servers have no such ceiling. Crossplay (PlayFab) peers
  also never report a round-trip time, so they are priced at `Unmeasured Peer RTT Ms` for ownership
  and rendered at vanilla by other clients.
- `Connection Timeout Seconds` is for players who get dropped mid-join or during a hitch on a weak
  link. It does not make a slow connection faster — it stops both ends declaring it dead while it is
  still working — and it is not free: a player who is genuinely gone now holds their slot, and keeps
  ownership of everything they were simulating, for that long instead of 30 seconds, and objects an
  absent owner holds do not move. Size it to the worst connection you actually want to keep. The one
  timeout a server cannot set for a client is `Connect Timeout Seconds`, which covers the handshake
  before anything has been synced — whoever cannot get connected has to raise that one themselves.
- Raising `Max Players` is not free and nothing here makes it free: each added player costs the
  host upload and CPU against every other player. `Frame Budget Ms` and the Steam transport
  ceiling below are what decide whether a bigger number is actually playable - read the two
  bullets above before setting one.

## Incompatible with

Other networking mods that rewrite the same code: FiresGhettoNetworking, VBNetTweaks, SkadiNet,
BetterNetworking, Smoothbrain's Network, NetworkTweaks, WarheimNetwork, TimeoutLimit. BepInEx will
refuse to load this mod alongside them rather than leave you half-patched. TimeoutLimit in
particular is no longer something you give up: the `Connection Timeout` settings cover the same
ground, and unlike a timeout raised at one layer only, they move both of the places vanilla times
out at.

**Verified compatible** (different layers, no overlap): LeanNet, Compress, Scenic. ReturnToSender is
fine too — it already fixes the scheduler, so that one mechanism stands down and the rest keeps
working. EnRoute and BetterZeeRouter are fine in the same way: both rework the routed-RPC relay, so
the relay-filtering mechanism stands down when either is present and everything else keeps working.

## Changelog

See CHANGELOG.md.
