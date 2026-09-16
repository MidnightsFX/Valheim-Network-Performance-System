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
| **Latency-aware ownership** | Assigns each **moving** object (creatures, physics props) to whoever minimises perceived staleness, with hysteresis so ownership cannot thrash. Never takes an object somebody is actively driving, and never moves a stationary object — buildings, containers, stations — away from a player who is still there: those follow vanilla's rules, since moving them gains nothing and races the RPCs that put items into them. |
| **Send scheduler fix** | Vanilla services one peer per rendered frame, so the advertised 20Hz silently becomes ~5.5Hz at ten players. This sends to everyone each tick. |
| **Live position reporting** | Vanilla reports your position to the server only every 2 seconds, and the server uses it to decide both what to send you and who owns what. A 12-byte side channel keeps it current. |
| **Latency compensation** | Draws other players' creatures where they *are*, not where they were when the packet left. This is the one you feel. |
| **Relay filtering** | Vanilla relays every footstep, swing, damage number and destroyed object to every player on the server, who then discards it unless they can see it. The host now relays only to the players who can. Nothing visible changes; on a busy server this is most of the relay traffic. The opt-in `Limit Relay By Distance` goes further for objects a player walked past earlier in the session — the host otherwise keeps relaying a base's building damage to everyone who ever visited it — and `nps_stats` counts what it would save before you turn it on. |
| **Station requests reach the owner** | Putting an item into a fermenter, smelter, cooking station, fireplace, shield generator or ballista removes it from your inventory and then asks *the owner* to account for it — the owner as your copy of the world names it, with no acknowledgement if that player has walked off, logged out, or just lost ownership. The host now delivers the request to whoever owns the object right now, hands ownership to you first if nobody present does, and waits for the new owner to be told before forwarding. No item is lost to a stale owner, and players do not need the mod for it. |
| **Configurable player limit** | Vanilla is hard-wired to 10. Set your own — and it is set in all four places the game keeps the number: the check that enforces it, the Steam listing and the crossplay lobby the server browser reads its `x / y` from, and the crossplay Party network, which has no UI at all and is the lowest ceiling of the four. |
| **Configurable timeouts** | Vanilla gives up on a quiet connection after 30 seconds, which is not enough for a slow link mid-join. Raise it — in both places the game times out, since the shorter one is what actually fires. |
| **Allocation relief** *(opt-in)* | Five changes that remove short-lived objects from the network path. None of them alters a byte on the wire or a value in a ZDO; what they reduce is how often the garbage collector has to run.<br>• **ZDO Deserialize** — reads a received ZDO's fields directly, instead of through the fourteen delegates the game allocates for every one whether the packet contains that field type or not. The largest of the four, and clients gain more than servers.<br>• **Packet Read** — reads each incoming payload straight into the buffer about to hold it, not into a temporary array that is copied across and thrown away.<br>• **Send Package Reuse** — reuses the two packet buffers the send path builds, rather than constructing and discarding both on every send to every peer.<br>• **RPC Invoke** — calls an incoming RPC's handler directly when its signature is the common one, instead of going through reflection for every message. The largest CPU saving of the four ZDO-path changes.<br>• **Relay Send Reuse** — the host writes each relayed RPC once and hands the same bytes to every player it goes to, instead of rebuilding and re-copying the whole message for each one. Host-only; applies without a restart.<br>All five ship **off** — see "Known limitations" for what they are and are not worth. |
| **`nps_stats`** | Per-peer RTT, window size, and how often peers are being starved. Open to anyone. Run `nps_stats_collect` (needs `devcommands`, since sampling costs a Steam call per peer per tick), play, then `nps_stats`. |

## Installing

**Server-only works.** Vanilla clients get the send window, ownership, scheduler and station-request fixes with
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
- Ownership arbitration only ever re-places creatures and other simulated, moving objects. Stationary
  objects — pieces, containers, fermenters, smelters, cooking stations — follow vanilla exactly: owned by
  whoever arrived first, re-owned only when that player leaves. This is deliberate. A stationary object's
  owner is where the game sends item-insert RPCs, and the game removes the item from your inventory
  before sending; moving the owner while a player is mid-insert loses the item.
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
- Some mods wait for a peer's socket send queue to fall under a fixed byte count before they send
  (Jotunn's `CustomRPC`, ServerSync and every mod bundling it, ConditionalConfigSync) and disconnect
  the peer after 30 seconds if it never does. That number was sized against vanilla; a latency-sized
  window legitimately keeps more in flight past ~100 ms RTT. Jotunn's limit is a field, and it is
  raised automatically to `Max Window Bytes` plus 20000 on both ends. The others have theirs compiled
  in, so the `Compatibility` settings briefly bring a backlogged peer's queue under it every
  `Queue Drain Interval Seconds` - a few percent of throughput, only while that peer is backlogged. If
  a mod with its own threshold still times out, lower `Queue Drain Floor Bytes` or the interval;
  `nps_stats` shows both limits and how often the drain ran.
- The `Allocation` settings are **churn reductions, not a fix for running out of memory**, and it is
  worth being precise about the difference. Mono's collector runs more often the faster objects are
  created, so removing allocations from the ZDO path means fewer collections and less CPU spent on
  each one. It does **not** reduce how much is live at any moment - on a very large world that is
  millions of field tables, one or two per ZDO, and it is a function of how much world has been
  generated. No mod changes that, and it is what the ceiling is actually set by. If your server is
  hitting a memory wall, these lengthen the interval between incidents and make the server cheaper
  to run in between; they do not remove the wall. Reducing generated world area is what does that.
- All five `Allocation` settings ship **off**. They are byte-identical to vanilla by construction -
  same values, same wire format, same order - but they are IL-level changes to the hottest paths in
  the game, so they are opt-in for their first release: enable them one at a time and soak each.
  Three of them need a **restart** to turn on, because the mod refuses to install a hook on a method
  called for every replicated object in every packet unless someone has asked for it; turning any of
  them off applies immediately. `nps_stats` says which are running, and tells you plainly when one is
  switched on in the config but was not installed this session.
- `Limit Relay By Distance` ships **off**. It judges "too far to have the object loaded" from each
  player's negotiated simulation distance plus one zone of slack, so it is lossless for players whose
  game loads what the server agreed to. A client-side mod that loads more of the world than that
  could miss building damage or effects at the very edge of its view. Leave it off and read the
  "out of range" line in `nps_stats` first: if that number is small, it is not worth turning on.

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
relay filtering and relay send reuse stand down when either is present and everything else keeps
working.
Valheim Plus is fine too: it has its own player limit, so this mod's `Player Limit` settings are
ignored when it is installed and V+'s `maxPlayers` is the one in force.

## Changelog

See CHANGELOG.md.
