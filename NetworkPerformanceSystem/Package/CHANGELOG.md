# Changelog

**1.1.0**

Scaling pass: what breaks first as a server grows from a small group towards a couple of hundred
players, plus the correctness issues found on the way. Config additions are all advanced and all
default to behaviour that is a no-op for a small group.

**New: routed RPC relay filtering (host).** Every "broadcast" RPC - footsteps, animation triggers,
hit-stop, damage numbers, building damage, pickables, object-destroyed notices - is sent once to the
host and relayed by the host to every other player, so its cost is events × players and it shares
the per-peer send window with ZDO updates. A receiving client discards a ZDO-targeted RPC unless it
has that object loaded, which only happens inside its own active area; the host knows exactly which
peers that is and now relays only to them. `DestroyZDO` goes only to peers that hold a copy of the
ZDO; damage text and `SpawnObject` go only to peers within range of the position. Chat, pings,
server messages, sleep, events, global keys and any RPC the filter does not recognise are relayed
to everyone exactly as before. Nothing visible changes for anyone; on a full server this removes
most of the relay traffic. `nps_stats` reports sent vs suppressed per category. Stands down if
BetterZeeRouter or EnRoute is installed, since both rework the same relay path.

**Fixed: peers with no round-trip measurement were scored as 0 ms and won ownership.** Crossplay
(PlayFab) sockets never report RTT and every Steam peer is unmeasured for its first seconds; the
cost function and the lowest-RTT tie-break both preferred them, so on a mixed Steam/Xbox server
every contested object in range of a console player migrated to it and stayed. Unmeasured peers are
now priced at the new `Unmeasured Peer RTT Ms` (default 150): they still win objects only they can
see, and lose contested ones to any measured lower-ping peer. The published latency table likewise
omits unmeasured peers instead of publishing a 0, so clients render those owners at vanilla rather
than compensating from a number nobody measured.

**Fixed: a peer that has not yet reported a position was a candidate at the world origin.** Vanilla
copies the client's reference position from its PeerInfo, which is `(0,0,0)` until the client has
chosen a spawn point; such a peer was handed spawn-area objects it had not instantiated, which sat
frozen until the next pass - once per login, continuously on a busy server. A peer at exactly zero
is now neither an owner, a viewer nor present. The fast reference-position channel also no longer
sends `(0,0,0)` on connect, and the server validates incoming positions (finite, inside the world)
before using them.

**Fixed: the load-spreading handicap could satisfy the challenge margin on its own.** It was capped
at exactly the margin and counted every persistent object (walls, trees), so anyone in a base was
instantly saturated and each newcomer of equal ping trickled objects off the incumbent for minutes.
It now counts only simulated objects (creatures, ships) and is capped at half the margin: load may
shade a close call but never amounts to a transfer by itself.

**Send scheduler frame budget.** Servicing every peer each interval fixes the per-peer rate, which
also means the host's send-path CPU grows with player count - and once a round costs more than
the interval it owes a full round every frame and never recovers. Each frame now has a wall-clock
budget (`Frame Budget Ms`, default 4): peers are serviced in round-robin order until the budget is
spent and the rest are owed to the next frame. Under load the per-peer rate degrades to
`min(1/interval, budget/cost)` instead of the frame time growing without bound; `nps_stats` shows
the effective rate and how often the budget is hit.

**Ownership pass cost.** The pass now scans each zone exactly once (the union of every player's
scan square) instead of once per player with a per-object dedup dictionary, records hold history
only for objects that are actually contested, and caches the sector verdict across the zone-grouped
list. Per-object work drops to one owner lookup and a few compares; the history table shrinks from
"everything near anyone" to "things someone could take". One deliberate consequence: the first
challenge against an object whose owner has been the best choice all along now waits one
`Min Hold Seconds` before moving - the same grace the pass already gave first-sight objects.

**Max Reassigns Per Pass now auto-scales.** The configured value is a floor; the effective cap is the
larger of it and the number of connected players, so a full server converges at the same
per-player rate as a small group. `nps_stats` shows the effective cap.

**Latency table is per recipient.** Each client now receives the host plus the measured peers within
render range of it, instead of every peer on the server: bytes go from O(players²) to O(players ×
group) and the table can no longer hit a size cap on a large server. Malformed tables are rejected
whole (the previous table is kept) instead of being half-applied.

**Changed:** latency compensation's distance cap now bounds the *total* extrapolated displacement
(vanilla's own gap extrapolation plus this correction), since the game's 5m snap test measures the
whole distance. Session state is reset from `ZNet.StopAll`, which covers both `Shutdown` and
`ShutdownWithoutSave`.

**1.0.0**

**Fixed: creatures could freeze mid-animation and stop taking damage.** Ownership arbitration
budgeted *every* assignment through `Max Reassigns Per Pass`, including restoring an owner to a ZDO
that had none. Vanilla does that part unbudgeted, and creatures have no other recovery path —
nothing in `BaseAI`, `MonsterAI` or `Character` ever claims ownership for itself. On a
single-player world, where the host is the only candidate, the whole world regained ownership at
four objects per two-second pass, so anything that crossed a zone boundary could sit unowned
indefinitely: no AI, position pinned, animator frozen replaying its last run cycle, and every hit
silently dropped at `RPC_Damage`'s owner check.

The pass now separates the two jobs it was doing:

- **Rescue** — a ZDO with no owner, or whose owner has left the area or the session — is
  correctness, and is restored immediately, uncapped, ignoring min-hold and the challenge margin.
  This matches vanilla.
- **Optimisation** — moving a ZDO to a lower-latency owner — is a preference, and keeps the
  per-pass cap, the per-target cap, min-hold and the challenge margin unchanged.

Consequences beyond the headline fix:

- A ship whose helmsman disconnected, or a mount whose rider disconnected, recovers on the next
  pass instead of being held by hysteresis and then queued behind the reassignment budget. A stale
  rider id can no longer mark a mount "controlled" forever.
- **Load Penalty Ms** is now capped at **Challenge Margin Ms**. It counts all nearby persistent
  objects, so in a built-up base the uncapped handicap reached 100ms+ — larger than any real ping
  spread, which quietly made placement load-driven rather than latency-driven.
- An owner barred from owning (`Allow Host As Owner = false`) is now still priced, so challenges
  against it no longer tie at infinity and jump ahead of moves that improve something real.
- `nps_stats` reports candidates, unowned-on-entry, and rescued / released / optimised / deferred
  separately, and warns when the unowned backlog exceeds what a pass restored. The old single
  `reassigned` figure could not distinguish normal churn from objects with no simulator at all.

**Fixed: no round-trip measurements on dedicated servers.** RTT was read through vanilla's
`ZSteamSocket.GetConnectionQuality`, which is hard-wired to the Steamworks *client* interface. A
dedicated server only initialises the *game-server* interface, so every read threw
`Steamworks is not initialized` — about twice a second per peer — swallowed by vanilla's
`ZRpc.Update` and logged as `Exception in ZRpc::HandlePackage` without naming this mod. With no
measurements the send window stayed at vanilla's 10 KB, ownership arbitration saw every peer at
0 ms and the published latency table was all zeros: every mechanism quietly degraded to vanilla on
the mod's primary target. RTT is now read through whichever Steamworks interface the process
actually initialised, with the other tried as a fallback. If neither works the mod says so once,
under its own name, and `nps_stats` shows `RttSampling: OFF` with the reason. Listen hosts and
clients are unchanged; crossplay/PlayFab still reports no RTT.

**Changed:** the `Ownership: rescued N of M nearby ZDOs` warning now needs three consecutive passes
over threshold (six seconds). A single such pass is the normal shape of a login — the joining
player's area has nobody simulating it yet — and now only appears on the debug line.

No config changes are required, and no settings were added — `Max Reassigns Per Pass` now bounds
latency-driven transfers only.

**0.1.0**

First release. Targets latency rather than bandwidth — see the README for why that distinction
matters.

- **Per-peer send window sized by round-trip time.** Replaces vanilla's fixed 10240-byte in-flight
  window in `ZDOMan.SendZDOs` with a bandwidth-delay product. No change for peers under ~53ms;
  opens to ~48 KB for a 250ms peer. Applies host→client and client→host.
- **Latency-aware ownership arbitration.** Replaces the tie-break in vanilla's ownership pass with
  a cost function over perceived staleness. Includes minimum hold time, a challenge margin, a
  per-pass reassignment cap, and a hard exclusion for anything a player is directly driving
  (players, ships, ridden tames).
- **Send scheduler fix.** Services every peer per send tick instead of one peer per rendered frame.
  Stands down automatically if ReturnToSender is installed.
- **Fast reference-position channel.** 12-byte movement-gated position updates so the server
  arbitrates against live positions rather than up-to-2-second-old ones.
- **Latency-compensated extrapolation.** Seeds `ZSyncTransform`'s existing extrapolation with the
  measured owner→viewer path latency, with the added displacement clamped to stay under the game's
  5m snap threshold.
- **`nps_stats` console command** plus an optional client overlay for tuning compensation strength.

Each mechanism verifies its own IL anchor at patch time and disables itself individually — with a
warning — rather than taking the mod down or silently doing nothing.
