# Changelog

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
