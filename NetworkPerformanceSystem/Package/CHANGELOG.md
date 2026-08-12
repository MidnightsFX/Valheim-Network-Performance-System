# Changelog

## 0.1.0

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
