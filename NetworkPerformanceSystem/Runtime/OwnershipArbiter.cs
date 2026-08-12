using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M3 - decides which peer should simulate each ZDO, by latency rather than by arrival order.
    ///
    /// Valheim is distributed-authority: exactly one peer owns and simulates each ZDO, and
    /// everyone else sees it via owner -> host -> viewer. So the staleness a viewer perceives is
    /// (rtt(owner) + rtt(viewer)) / 2, and picking the wrong owner adds a whole extra hop to
    /// everything in that zone.
    ///
    /// Vanilla picks by iteration order: ReleaseZDOS walks the host then each peer in list order,
    /// and whoever gets there first claims any unowned ZDO and keeps it. That is uncorrelated with
    /// both engagement and latency, and for a geographically spread group it is close to the worst
    /// available choice.
    ///
    /// This replaces the tie-break with an explicit cost function. Note what it does NOT do: it
    /// never moves a Player, Ship or tamed creature away from a healthy owner, because authority
    /// must follow direct control - that is the failure mode behind ship-helm stutter.
    /// </summary>
    internal static class OwnershipArbiter {

        /// <summary>
        /// A participant in an arbitration decision. The two roles are deliberately separate:
        /// a dedicated host simulates but nobody is looking through its eyes, and it may be barred
        /// from owning for CPU reasons while still needing to appear here so that ZDOs it already
        /// owns are not mistaken for abandoned.
        /// </summary>
        private struct Candidate {
            internal long Uid;
            internal Vector2i Zone;
            internal float RttMs;
            internal bool CanOwn;      // eligible to be assigned ownership
            internal bool IsViewer;    // a human sees this ZDO from here, so its staleness counts
        }

        private struct Verdict {
            internal float TotalCostMs;
            internal float WorstCostMs;
            internal float OwnerRttMs;
        }

        private static readonly List<Candidate> Candidates = new List<Candidate>();
        private static readonly List<ZDO> ScratchZdos = new List<ZDO>();
        private static readonly Dictionary<ZDOID, ZDO> Considered = new Dictionary<ZDOID, ZDO>();
        private static readonly Dictionary<ZDOID, float> LastAssigned = new Dictionary<ZDOID, float>();
        private static readonly List<PendingMove> Pending = new List<PendingMove>();

        private struct PendingMove {
            internal ZDO Zdo;
            internal long NewOwner;
            internal float ImprovementMs;
        }

        // Diagnostics
        internal static int LastPassConsidered;
        internal static int LastPassReassigned;
        internal static int LastPassDeferredByCap;
        internal static long TotalReassignments;

        /// <summary>Entries older than this are dropped so the hold-time table cannot grow without
        /// bound on a long-running server.</summary>
        private const float HoldTableTtlSeconds = 120f;
        private static float _lastPrune;

        internal static void RunPass(ZDOMan zdoMan) {
            if (ZNet.instance == null || ZoneSystem.instance == null) { return; }

            BuildCandidates(zdoMan);
            if (Candidates.Count == 0) { return; }

            CollectConsideredZdos(zdoMan);

            float now = Time.realtimeSinceStartup;
            float minHold = ValConfig.OwnershipMinHoldSeconds.Value;
            float margin = ValConfig.OwnershipChallengeMarginMs.Value;

            Pending.Clear();
            LastPassConsidered = Considered.Count;

            foreach (KeyValuePair<ZDOID, ZDO> entry in Considered) {
                ZDO zdo = entry.Value;
                long currentOwner = zdo.GetOwner();

                if (!TryPickOwner(zdo, currentOwner, out long best, out float bestTotal, out float currentTotal)) {
                    // Nobody can take it. Preserve vanilla's release-to-unowned behaviour so an
                    // owner who walked away does not leave a phantom behind: HasOwner() gates real
                    // game logic (ZSyncTransform only extrapolates when a ZDO claims an owner), so
                    // an absent owner is worse than no owner.
                    if (currentOwner != 0L && !IsOwnerStillInArea(zdo, currentOwner)) {
                        zdo.SetOwner(0L);
                        LastAssigned.Remove(zdo.m_uid);
                    }
                    continue;
                }
                if (best == currentOwner) { continue; }

                // Hysteresis: an owner that was just handed this ZDO keeps it for a while, so
                // ping jitter around the margin cannot ping-pong ownership between two peers.
                if (currentOwner != 0L
                    && LastAssigned.TryGetValue(zdo.m_uid, out float assignedAt)
                    && now - assignedAt < minHold) {
                    continue;
                }

                float improvement = currentTotal - bestTotal;
                if (currentOwner != 0L && improvement < margin) { continue; }

                Pending.Add(new PendingMove { Zdo = zdo, NewOwner = best, ImprovementMs = improvement });
            }

            ApplyPending(now);
            PruneHoldTable(now);
        }

        /// <summary>
        /// Everyone who could take a ZDO: the host plus every connected peer. The host is included
        /// on the same footing as anyone else and, critically, is still subject to the same
        /// active-area test in TryPickOwner.
        /// </summary>
        private static void BuildCandidates(ZDOMan zdoMan) {
            Candidates.Clear();

            // The host is always present so that ZDOs it legitimately owns are not read as
            // abandoned, but it only competes for ownership when configured to.
            //
            // Note it is subject to the same active-area test as everyone else, which is what
            // makes host ownership safe rather than a Serverside-Simulations-style trade. A
            // dedicated server only instantiates objects around its own reference position, so
            // restricting it to zones it has actually loaded means it can never win a ZDO it
            // would then fail to simulate. The practical consequence on a dedicated server is
            // that contested ZDOs go to the lowest-RTT peer present rather than to the host.
            Candidates.Add(new Candidate {
                Uid = zdoMan.m_sessionID,
                Zone = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition()),
                RttMs = 0f,                                                  // zero hops from its own simulation
                CanOwn = ValConfig.OwnershipAllowHostOwner.Value,
                IsViewer = !NpsEnv.IsDedicated(),                            // a listen host has a player looking
            });

            List<ZDOMan.ZDOPeer> peers = zdoMan.m_peers;
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer netPeer = peers[i]?.m_peer;
                if (netPeer == null || netPeer.m_uid == 0L) { continue; }

                Candidates.Add(new Candidate {
                    Uid = netPeer.m_uid,
                    Zone = ZoneSystem.GetZone(netPeer.GetRefPos()),
                    RttMs = LatencyRegistry.MeasuredRttMs(netPeer.m_uid),
                    CanOwn = true,
                    IsViewer = true,
                });
            }
        }

        /// <summary>
        /// The union of persistent ZDOs near any candidate. Vanilla gathers this same set once per
        /// candidate; we gather it once and dedupe, so the scan cost does not grow with player
        /// count the way vanilla's does.
        /// </summary>
        private static void CollectConsideredZdos(ZDOMan zdoMan) {
            Considered.Clear();

            for (int i = 0; i < Candidates.Count; i++) {
                ScratchZdos.Clear();
                zdoMan.FindSectorObjects(Candidates[i].Zone, ZoneSystem.instance.m_activeArea, 0, ScratchZdos);

                for (int j = 0; j < ScratchZdos.Count; j++) {
                    ZDO zdo = ScratchZdos[j];
                    if (zdo == null || !zdo.Persistent) { continue; }
                    Considered[zdo.m_uid] = zdo;
                }
            }
        }

        /// <summary>
        /// Choose the owner minimising perceived staleness across everyone who can see this ZDO.
        ///
        ///     Cost(O) = sum over interested p != O of (rtt(O) + rtt(p)) / 2
        ///
        /// With one interested peer that resolves to "the peer owns it" (cost 0). With two peers in
        /// different latency classes every choice has the same total, so the tie-break on worst
        /// case decides - and the lowest-RTT candidate wins, which is the fair outcome.
        /// </summary>
        private static bool TryPickOwner(ZDO zdo, long currentOwner, out long best, out float bestTotal, out float currentTotal) {
            best = 0L;
            bestTotal = float.MaxValue;
            currentTotal = float.MaxValue;

            Vector2i sector = zdo.GetSector();
            int activatedArea = ZoneSystem.instance.m_activeArea - 1;

            // Anyone eligible to take it: in the ZDO's active area and allowed to own.
            int eligibleCount = 0;
            for (int i = 0; i < Candidates.Count; i++) {
                if (Candidates[i].CanOwn && ZNetScene.InActiveArea(sector, Candidates[i].Zone, activatedArea)) {
                    eligibleCount++;
                }
            }
            if (eligibleCount == 0) { return false; }

            // Directly-controlled objects follow their controller, never the cost function - but
            // only while that controller is actually still there. Both halves matter:
            //
            //   * skipping while the owner is present is what stops us yanking a ship out from
            //     under its helmsman, which is the classic failure mode here;
            //   * NOT skipping once the owner is gone is what stops a ship whose driver
            //     disconnected from keeping a phantom owner forever - it would be excluded from
            //     reassignment and from the release path below, so nothing would ever simulate it
            //     again.
            if (currentOwner != 0L
                && OwnershipPolicy.IsDirectlyControlled(zdo)
                && IsOwnerStillInArea(zdo, currentOwner)) {
                return false;
            }

            Verdict bestVerdict = default;
            bool found = false;

            for (int i = 0; i < Candidates.Count; i++) {
                Candidate owner = Candidates[i];
                if (!owner.CanOwn) { continue; }
                if (!ZNetScene.InActiveArea(sector, owner.Zone, activatedArea)) { continue; }

                Verdict verdict = Score(owner, sector, activatedArea);

                if (owner.Uid == currentOwner) { currentTotal = verdict.TotalCostMs; }

                if (!found || IsBetter(verdict, bestVerdict)) {
                    bestVerdict = verdict;
                    best = owner.Uid;
                    found = true;
                }
            }

            if (!found) { return false; }

            bestTotal = bestVerdict.TotalCostMs;
            // currentTotal is left at float.MaxValue when the current owner is not an eligible
            // candidate - it walked away, or disconnected. That reads as infinite cost, so any
            // eligible candidate wins outright and the margin check cannot block the handover.
            return true;
        }

        /// <summary>
        /// Mirrors vanilla's IsInPeerActiveArea test: is the ZDO still inside its owner's active
        /// area? False for an owner that has moved on or is no longer connected.
        /// </summary>
        private static bool IsOwnerStillInArea(ZDO zdo, long ownerUid) {
            Vector2i sector = zdo.GetSector();
            int activatedArea = ZoneSystem.instance.m_activeArea - 1;

            for (int i = 0; i < Candidates.Count; i++) {
                if (Candidates[i].Uid == ownerUid) {
                    return ZNetScene.InActiveArea(sector, Candidates[i].Zone, activatedArea);
                }
            }
            return false;
        }

        /// <summary>
        /// Ranks two candidates: lowest total staleness, then lowest worst case, then lowest RTT.
        ///
        /// The third key is not decoration. The cost function is symmetric, so with exactly two
        /// viewers every choice scores identically - A owning means B eats the full path and vice
        /// versa. Without a tie-break that case would fall through to candidate list order, which
        /// is precisely the arbitrary ordering this mechanism exists to replace. Preferring the
        /// lower-RTT owner is both stable (immune to list reordering, so no thrash) and better on
        /// the margin: that peer's updates reach the host sooner, which is what a third player
        /// arriving later, and world persistence, both depend on.
        /// </summary>
        private static bool IsBetter(Verdict candidate, Verdict incumbent) {
            if (!Mathf.Approximately(candidate.TotalCostMs, incumbent.TotalCostMs)) {
                return candidate.TotalCostMs < incumbent.TotalCostMs;
            }
            if (!Mathf.Approximately(candidate.WorstCostMs, incumbent.WorstCostMs)) {
                return candidate.WorstCostMs < incumbent.WorstCostMs;
            }
            return candidate.OwnerRttMs < incumbent.OwnerRttMs;
        }

        private static Verdict Score(Candidate owner, Vector2i sector, int activatedArea) {
            float total = 0f;
            float worst = 0f;

            for (int i = 0; i < Candidates.Count; i++) {
                Candidate viewer = Candidates[i];
                if (!viewer.IsViewer) { continue; }                          // nobody is watching through a dedicated host
                if (viewer.Uid == owner.Uid) { continue; }                   // the owner sees its own simulation instantly
                if (!ZNetScene.InActiveArea(sector, viewer.Zone, activatedArea)) { continue; }

                float staleness = (owner.RttMs + viewer.RttMs) * 0.5f;
                total += staleness;
                if (staleness > worst) { worst = staleness; }
            }

            return new Verdict { TotalCostMs = total, WorstCostMs = worst, OwnerRttMs = owner.RttMs };
        }

        /// <summary>
        /// Apply the best moves first, up to the per-pass cap. Sorting by improvement means a
        /// capped pass does the reassignments that matter most rather than whichever happened to
        /// be enumerated first, and anything skipped is simply reconsidered next pass.
        /// </summary>
        private static void ApplyPending(float now) {
            LastPassReassigned = 0;
            LastPassDeferredByCap = 0;

            if (Pending.Count == 0) { return; }

            Pending.Sort((a, b) => b.ImprovementMs.CompareTo(a.ImprovementMs));

            int cap = Mathf.Max(1, ValConfig.OwnershipMaxReassignsPerPass.Value);
            for (int i = 0; i < Pending.Count; i++) {
                if (i >= cap) { LastPassDeferredByCap = Pending.Count - cap; break; }

                PendingMove move = Pending[i];
                move.Zdo.SetOwner(move.NewOwner);
                LastAssigned[move.Zdo.m_uid] = now;
                LastPassReassigned++;
                TotalReassignments++;
            }

            if (Logger.Level >= BepInEx.Logging.LogLevel.Debug) {
                Logger.LogDebug($"Ownership pass: considered {LastPassConsidered}, reassigned {LastPassReassigned}, deferred {LastPassDeferredByCap}.");
            }
        }

        private static void PruneHoldTable(float now) {
            if (now - _lastPrune < HoldTableTtlSeconds) { return; }
            _lastPrune = now;

            List<ZDOID> stale = null;
            foreach (KeyValuePair<ZDOID, float> entry in LastAssigned) {
                if (now - entry.Value > HoldTableTtlSeconds) {
                    (stale ??= new List<ZDOID>()).Add(entry.Key);
                }
            }
            if (stale == null) { return; }
            for (int i = 0; i < stale.Count; i++) { LastAssigned.Remove(stale[i]); }
        }

        internal static void Reset() {
            Candidates.Clear();
            ScratchZdos.Clear();
            Considered.Clear();
            LastAssigned.Clear();
            Pending.Clear();
            LastPassConsidered = 0;
            LastPassReassigned = 0;
            LastPassDeferredByCap = 0;
            TotalReassignments = 0;
        }
    }
}
