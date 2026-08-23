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
    ///
    /// A pass does two things, and keeping them apart is load-bearing:
    ///
    ///   * RESCUE - a ZDO with no owner, or whose owner has left the sector or the session, has
    ///     nothing simulating it at all. Vanilla restores these unbudgeted on the pass that finds
    ///     them, and creatures have no other recovery path (nothing in BaseAI, MonsterAI or
    ///     Character ever calls ClaimOwnership). This is correctness, so it is uncapped.
    ///   * OPTIMISATION - moving a ZDO from a healthy, present owner to a lower-latency one. This
    ///     is a preference, so it carries the per-pass cap, the per-target cap, the min-hold
    ///     hysteresis and the challenge margin.
    ///
    /// Routing rescues through the optimisation budget is what froze creatures mid-animation and
    /// made them immune to damage: an unowned Character drops every RPC_Damage at its IsOwner
    /// check while ZSyncAnimation keeps replaying the last-replicated run cycle.
    ///
    /// Cost shape matters here because the pass runs every two seconds on the host's main thread
    /// and visits every persistent ZDO near every player. The work per ZDO is kept to: one
    /// sector-list read, one owner lookup and a handful of compares. Everything that used to be a
    /// dictionary write per ZDO - dedup, hold-history - is now either structural (each zone is
    /// scanned exactly once, so there is nothing to dedup) or deferred until a ZDO is actually
    /// contested. On a full server that is the difference between a few milliseconds and a
    /// visible hitch.
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

        /// <summary>Every zone inside at least one candidate's scan square this pass. Scanning each
        /// zone exactly once is what makes the considered list duplicate-free without a per-ZDO
        /// dictionary: a ZDO lives in exactly one sector list, so it can only be appended once.</summary>
        private static readonly HashSet<Vector2i> ZonesToScan = new HashSet<Vector2i>();
        private static readonly List<ZDO> ZoneScratch = new List<ZDO>();

        /// <summary>Persistent ZDOs under consideration this pass, in scan order - which means
        /// grouped by zone, so consecutive entries almost always share a sector and a verdict.
        /// ConsideredOwner carries the owner observed at collection time, so the decision loop
        /// does not repeat the owner lookup. Nothing between collection and decision changes
        /// ownership, and the loop only ever writes the ZDO it is currently looking at.</summary>
        private static readonly List<ZDO> Considered = new List<ZDO>();
        private static readonly List<long> ConsideredOwner = new List<long>();

        private static readonly Dictionary<ZDOID, OwnershipRecord> OwnerHistory = new Dictionary<ZDOID, OwnershipRecord>();
        private static readonly List<PendingMove> Pending = new List<PendingMove>();
        private static readonly Dictionary<Vector2i, SectorVerdict> SectorCache = new Dictionary<Vector2i, SectorVerdict>();
        private static readonly List<SectorVerdict> VerdictPool = new List<SectorVerdict>();
        private static int _verdictsInUse;
        private static readonly Dictionary<long, int> OwnedCount = new Dictionary<long, int>();
        private static readonly Dictionary<long, int> MovesPerTarget = new Dictionary<long, int>();

        /// <summary>
        /// Everything the cost function can say about one sector. Eligibility and Score depend
        /// only on (sector, candidates), not on the individual ZDO, so every ZDO in a sector
        /// shares one verdict - computed once per pass instead of once per ZDO. With a full
        /// server that is the difference between thousands of iterations and tens of millions.
        /// Instances are pooled across passes; only the cache dictionary is rebuilt.
        /// </summary>
        private sealed class SectorVerdict {
            internal bool HasEligible;
            internal long BestUid;
            internal float BestTotalMs;
            /// <summary>Total cost per present candidate, for the incumbent-owner lookup. Priced
            /// even for candidates barred from owning, because an owner missing here reads as
            /// infinite cost - which would make every challenge against it tie at float.MaxValue
            /// and jump the queue on a capped pass, ahead of moves that improve something real.
            /// </summary>
            internal readonly Dictionary<long, float> TotalByOwner = new Dictionary<long, float>();
            /// <summary>Candidates whose active area covers the sector, regardless of CanOwn.
            /// Backs the "is the owner still here" test - a host barred from owning must still
            /// count as present, or ZDOs it legitimately owns would read as abandoned.</summary>
            internal readonly HashSet<long> Present = new HashSet<long>();

            internal void Clear() {
                HasEligible = false;
                BestUid = 0L;
                BestTotalMs = float.MaxValue;
                TotalByOwner.Clear();
                Present.Clear();
            }
        }

        /// <summary>
        /// Ownership history per ZDO: who owned it as of the last pass that looked, when that
        /// changed, and when a pass last saw it. ChangedAt drives the min-hold hysteresis; because
        /// it is refreshed whenever the observed owner differs from the recorded one, ownership
        /// claimed by gameplay code between passes (cart grabs, mount transfers,
        /// ZNetView.ClaimOwnership on doors and the like) gets the same grace period as an
        /// arbiter assignment instead of being challengeable on the very next pass.
        ///
        /// Only ZDOs that are actually contested are recorded - the decision loop consults the
        /// history after every cheaper early-out has passed. A ZDO whose owner is already the best
        /// choice never needs a hold timestamp; when a better candidate later appears, its first
        /// sight in the table counts as a change and it gets one hold-width of grace before it can
        /// move. That is deliberately conservative, and it keeps the table proportional to the
        /// contested set rather than to the whole nearby world.
        /// </summary>
        private struct OwnershipRecord {
            internal long Owner;
            internal float ChangedAt;
            internal float LastSeenAt;
        }

        private struct PendingMove {
            internal ZDO Zdo;
            internal long NewOwner;
            internal float ImprovementMs;
        }

        // Diagnostics. Rescue and optimisation are counted apart on purpose: they are different
        // operations under different budgets, and a report that merges them cannot tell "the
        // world is churning normally" from "part of it has no simulator at all".
        internal static int LastPassCandidates;
        internal static int LastPassConsidered;
        internal static int LastPassUnownedOnEntry;
        internal static int LastPassRescued;
        internal static int LastPassReleased;
        internal static int LastPassOptimised;
        internal static int LastPassDeferred;      // optimisations only; rescues are never deferred
        internal static int LastPassCap;           // effective per-pass transfer cap after auto-scaling
        internal static long TotalRescued;
        internal static long TotalOptimised;
        internal static float LastPassMs;

        /// <summary>Entries no pass has seen for this long are dropped so the history table
        /// cannot grow without bound on a long-running server. Pruning keys off LastSeenAt, not
        /// ChangedAt: a stable owner's entry must survive, or expiry would read as a fresh claim
        /// and re-grant it a hold.</summary>
        private const float HoldTableTtlSeconds = 120f;
        private static float _lastPrune;

        /// <summary>Rate limit for the rescue-backlog warning. The pass runs every two seconds, so
        /// an unthrottled warning would bury the rest of the log the moment it fires.</summary>
        private const float RescueWarningIntervalSeconds = 30f;
        private static float _lastRescueWarning;

        /// <summary>
        /// How many consecutive passes must rescue more than a quarter of the nearby world before
        /// that is reported as a problem. One such pass is the normal shape of every login on a
        /// dedicated server: the joining peer's reference position covers thousands of ZDOs nobody
        /// is simulating, and vanilla's ReleaseNearbyZDOS grants them all unbudgeted too. A login
        /// can legitimately produce two in a row - one around the peer's initial zero reference
        /// position if a pass lands before its first RefPos arrives, then one around the real
        /// spawn point once it has. Three (six seconds) is not a login or a teleport shape: after
        /// the spawn burst, walking, a portal or a boat adds at most a zone row per crossing, and
        /// the failure this exists to catch - something releasing ownership faster than the pass
        /// restores it - is indefinite, so six seconds of detection latency costs nothing. A
        /// server restart where several players rejoin disjoint areas within those seconds can
        /// still chain a burst; if that is ever seen, raise this to 5.
        /// </summary>
        private const int RescueWarningConsecutivePasses = 3;
        private static int _rescueBurstStreak;

        internal static void RunPass(ZDOMan zdoMan) {
            if (ZNet.instance == null || ZoneSystem.instance == null) { return; }

            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            float now = Time.realtimeSinceStartup;
            float minHold = ValConfig.OwnershipMinHoldSeconds.Value;
            float margin = ValConfig.OwnershipChallengeMarginMs.Value;
            float loadPenalty = ValConfig.OwnershipLoadPenaltyMs.Value;

            BuildCandidates(zdoMan);
            LastPassCandidates = Candidates.Count;
            if (Candidates.Count == 0) { LastPassMs = 0f; return; }

            CollectConsideredZdos(zdoMan, loadPenalty > 0f);

            SectorCache.Clear();
            _verdictsInUse = 0;
            Pending.Clear();

            // Rescues and releases are applied inside the loop below rather than queued, so the
            // counters are reset here instead of in ApplyUpgrades. LastPassUnownedOnEntry is
            // deliberately absent: CollectConsideredZdos has already filled it in.
            LastPassConsidered = Considered.Count;
            LastPassRescued = 0;
            LastPassReleased = 0;

            // The considered list is grouped by zone, so the verdict for the previous ZDO is very
            // likely the verdict for this one. Holding it in locals turns the per-ZDO SectorCache
            // lookup into two int compares.
            Vector2i lastSector = default;
            SectorVerdict verdict = null;

            for (int i = 0; i < Considered.Count; i++) {
                ZDO zdo = Considered[i];
                long currentOwner = ConsideredOwner[i];

                Vector2i sector = zdo.GetSector();
                if (verdict == null || sector.x != lastSector.x || sector.y != lastSector.y) {
                    verdict = VerdictFor(sector);
                    lastSector = sector;
                }

                if (!verdict.HasEligible) {
                    // Nobody can take it. Preserve vanilla's release-to-unowned behaviour so an
                    // owner who walked away does not leave a phantom behind: HasOwner() gates real
                    // game logic (ZSyncTransform only extrapolates when a ZDO claims an owner), so
                    // an absent owner is worse than no owner.
                    //
                    // Uncapped, and safe precisely because the rescue below is uncapped too: the
                    // moment an eligible candidate covers this sector again the ZDO is re-owned on
                    // that same pass, exactly as vanilla does it. The two must stay symmetrical -
                    // capping one side and not the other is what left creatures frozen.
                    if (currentOwner != 0L && !verdict.Present.Contains(currentOwner)) {
                        zdo.SetOwner(0L);
                        OwnerHistory.Remove(zdo.m_uid);
                        LastPassReleased++;
                    }
                    continue;
                }

                // Rescue: nothing is simulating this. Unowned, or owned by someone who has left
                // the sector or the session. That is not a placement preference, it is a broken
                // object - BaseAI.UpdateAI returns early for non-owners, ZSyncTransform only
                // extrapolates when the ZDO claims an owner, ZSyncAnimation stops writing animator
                // parameters, and Character.Damage routes through ZRoutedRpc to owner 0, which
                // broadcasts and is then dropped by every receiver at RPC_Damage's IsOwner check.
                // Frozen mid-animation and silently invulnerable.
                //
                // Vanilla's ReleaseNearbyZDOS grants every one of these on the pass that finds it
                // and budgets nothing, and no creature component ever calls ClaimOwnership, so
                // this pass is the only recovery path they have. So: no cap, no min-hold, no
                // margin. A cap here does not smooth the cost, it leaves objects broken for
                // another pass. If this ever needs to cost less, the lever is spreading rescues
                // across more eligible candidates - never deferring them in time.
                //
                // Deliberately ahead of the direct-control and hysteresis checks below. Those all
                // require a present owner, so the two paths are disjoint by construction: this can
                // never take a ship from a helmsman still aboard, but it does rescue one whose
                // helmsman disconnected - which the release path above cannot reach either,
                // because some other candidate is still eligible here.
                if (currentOwner == 0L || !verdict.Present.Contains(currentOwner)) {
                    zdo.SetOwner(verdict.BestUid);
                    OwnerHistory[zdo.m_uid] = new OwnershipRecord { Owner = verdict.BestUid, ChangedAt = now, LastSeenAt = now };
                    LastPassRescued++;
                    TotalRescued++;
                    continue;
                }

                // Everything from here down is optimisation: the ZDO already has a healthy, present
                // owner and we are only deciding whether a better one exists. That is a preference,
                // so every cap and every hysteresis rule belongs here and nowhere else.
                // currentOwner != 0 and Present.Contains(currentOwner) are invariants below, which
                // is why none of these checks re-test them.

                // Directly-controlled objects follow their controller, never the cost function.
                // This is what stops us yanking a ship out from under its helmsman.
                if (OwnershipPolicy.IsDirectlyControlled(zdo)) { continue; }

                if (verdict.BestUid == currentOwner) { continue; }

                // Only a ZDO that has survived every early-out above is contested, and only a
                // contested ZDO needs a hold timestamp - so this is the first and only point at
                // which the history table is touched for it. See OwnershipRecord for why that is
                // both cheaper and slightly more conservative than recording everything.
                float changedAt = Touch(zdo.m_uid, currentOwner, now);

                // Hysteresis: an owner that just received this ZDO - from us or from gameplay
                // code - keeps it for a while, so ping jitter around the margin cannot
                // ping-pong ownership between two peers and a player-initiated claim is not
                // second-guessed on the very next pass.
                if (now - changedAt < minHold) { continue; }

                // Present owners are always priced by BuildVerdict; the fallback is belt and braces.
                float currentTotal = verdict.TotalByOwner.TryGetValue(currentOwner, out float knownTotal)
                    ? knownTotal
                    : float.MaxValue;

                float improvement = currentTotal - verdict.BestTotalMs;
                if (improvement < margin) { continue; }

                Pending.Add(new PendingMove { Zdo = zdo, NewOwner = verdict.BestUid, ImprovementMs = improvement });
            }

            ApplyUpgrades(now);
            PruneHoldTable(now);

            watch.Stop();
            LastPassMs = (float)watch.Elapsed.TotalMilliseconds;

            bool burst = LastPassConsidered > 0 && LastPassRescued * 4 > LastPassConsidered;
            _rescueBurstStreak = burst ? _rescueBurstStreak + 1 : 0;
            if (_rescueBurstStreak >= RescueWarningConsecutivePasses
                && now - _lastRescueWarning > RescueWarningIntervalSeconds) {
                _lastRescueWarning = now;
                // Over a quarter of the nearby world had no simulator at the start of each of the
                // last few passes. One or two such passes are a login, a world load or a mass
                // teleport and only show on the debug line below; sustained, something is
                // releasing ownership faster than this pass can restore it.
                Logger.LogWarning($"Ownership: rescued {LastPassRescued} of {LastPassConsidered} nearby ZDOs with no present owner, {_rescueBurstStreak} passes in a row.");
            }

            if (Logger.Level >= BepInEx.Logging.LogLevel.Debug) {
                Logger.LogDebug($"Ownership pass: considered {LastPassConsidered} ({LastPassUnownedOnEntry} unowned) over {ZonesToScan.Count} zones, rescued {LastPassRescued}, released {LastPassReleased}, optimised {LastPassOptimised}, deferred {LastPassDeferred}, history {OwnerHistory.Count}, {LastPassMs:F1}ms.");
            }
        }

        /// <summary>Cached verdict for a sector, built on first request this pass.</summary>
        private static SectorVerdict VerdictFor(Vector2i sector) {
            if (SectorCache.TryGetValue(sector, out SectorVerdict cached)) { return cached; }

            SectorVerdict verdict;
            if (_verdictsInUse < VerdictPool.Count) {
                verdict = VerdictPool[_verdictsInUse];
                verdict.Clear();
            } else {
                verdict = new SectorVerdict();
                VerdictPool.Add(verdict);
            }
            _verdictsInUse++;

            BuildVerdict(sector, verdict);
            SectorCache[sector] = verdict;
            return verdict;
        }

        /// <summary>
        /// Choose the owner minimising perceived staleness across everyone who can see this
        /// sector.
        ///
        ///     Cost(O) = sum over interested p != O of (rtt(O) + rtt(p)) / 2
        ///
        /// With one interested peer that resolves to "the peer owns it" (cost 0). With two peers
        /// in different latency classes every choice has the same total, so the tie-break on
        /// worst case decides - and the lowest-RTT candidate wins, which is the fair outcome.
        /// </summary>
        private static void BuildVerdict(Vector2i sector, SectorVerdict verdict) {
            int activatedArea = ZoneSystem.instance.m_activeArea - 1;
            float loadPenalty = ValConfig.OwnershipLoadPenaltyMs.Value;
            // The challenge margin is "the smallest staleness difference worth acting on". Load may
            // shade a decision but must never, on its own, amount to one - so the handicap is
            // capped strictly below the margin. Capping it AT the margin (as this once did) let a
            // saturated incumbent lose exactly one margin's worth to any unsaturated newcomer of
            // equal RTT, which passed the challenge and trickled objects to every new arrival.
            float loadCap = 0.5f * ValConfig.OwnershipChallengeMarginMs.Value;

            Verdict bestVerdict = default;
            bool found = false;

            for (int i = 0; i < Candidates.Count; i++) {
                Candidate owner = Candidates[i];
                if (!ZNetScene.InActiveArea(sector, owner.Zone, activatedArea)) { continue; }
                verdict.Present.Add(owner.Uid);

                Verdict score = Score(owner, sector, activatedArea);

                // Load-aware term: every simulated object this candidate already owns makes the
                // next one slightly less attractive, so a run of low-RTT wins spreads across
                // peers instead of stacking one player's CPU and upload. Deliberately kept out of
                // WorstCostMs, which stays a pure staleness metric for the tie-break.
                if (loadPenalty > 0f && OwnedCount.TryGetValue(owner.Uid, out int owned)) {
                    score.TotalCostMs += Mathf.Min(owned * loadPenalty, loadCap);
                }

                // Priced before the CanOwn gate: a host barred from owning still owns ZDOs
                // legitimately, and an incumbent missing from this table reads as infinite cost.
                // CanOwn gates winning, not being priced.
                verdict.TotalByOwner[owner.Uid] = score.TotalCostMs;
                if (!owner.CanOwn) { continue; }

                if (!found || IsBetter(score, bestVerdict)) {
                    bestVerdict = score;
                    verdict.BestUid = owner.Uid;
                    found = true;
                }
            }

            verdict.HasEligible = found;
            verdict.BestTotalMs = found ? bestVerdict.TotalCostMs : float.MaxValue;
        }

        /// <summary>
        /// Records what this pass observed about a ZDO's ownership and returns the timestamp of
        /// the last ownership change, which is what the hold check measures from. An observed
        /// owner that differs from the recorded one is an external claim and restarts the clock.
        /// </summary>
        private static float Touch(ZDOID uid, long currentOwner, float now) {
            if (OwnerHistory.TryGetValue(uid, out OwnershipRecord record) && record.Owner == currentOwner) {
                record.LastSeenAt = now;
                OwnerHistory[uid] = record;
                return record.ChangedAt;
            }

            OwnerHistory[uid] = new OwnershipRecord { Owner = currentOwner, ChangedAt = now, LastSeenAt = now };
            return now;
        }

        /// <summary>
        /// Everyone who could take a ZDO: the host plus every connected peer. The host is included
        /// on the same footing as anyone else and, critically, is still subject to the same
        /// active-area test in BuildVerdict.
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
            // would then fail to simulate. On a dedicated server that position is the world
            // origin, so the practical consequence is: contested objects anywhere else go to the
            // lowest-RTT peer present, and contested objects in the origin zones go to the host
            // whenever two or more players are there (its cost is the theoretical minimum for
            // every viewer). That last part is intended - see the Allow Host As Owner config.
            Candidates.Add(new Candidate {
                Uid = zdoMan.m_sessionID,
                Zone = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition()),
                RttMs = 0f,                                                  // zero hops from its own simulation
                CanOwn = ValConfig.OwnershipAllowHostOwner.Value,
                IsViewer = !NpsEnv.IsDedicated(),                            // a listen host has a player looking
            });

            // A peer we have no round-trip measurement for - PlayFab/crossplay, which never
            // reports one, or a Steam peer in its first seconds - must not read as 0ms, or the
            // cost function and the lowest-RTT tie-break both hand it every contested object in
            // range. Price it pessimistically instead: it still wins outright when it is the only
            // viewer (cost 0 regardless of RTT) and loses to any measured lower-latency peer.
            float unmeasuredMs = ValConfig.OwnershipUnmeasuredRttMs.Value;

            List<ZDOMan.ZDOPeer> peers = zdoMan.m_peers;
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer netPeer = peers[i]?.m_peer;
                if (netPeer == null || netPeer.m_uid == 0L) { continue; }

                // Exactly (0,0,0) is the "no position yet" sentinel: ZNet.RPC_PeerInfo copies it
                // from the client's PeerInfo, which is sent before the client has chosen a spawn
                // point, and it stays there until the first ServerSyncedPlayerData or RefPos
                // arrives. A peer that has told us nothing about where it is cannot be
                // simulating anything, so it is neither an owner, a viewer nor present - treating
                // it as a candidate at the origin handed it spawn-area objects it had not
                // instantiated, which then sat frozen until the next pass. No real player stands
                // at exactly y = 0 at the world centre.
                Vector3 refPos = netPeer.GetRefPos();
                if (refPos == Vector3.zero) { continue; }

                long uid = netPeer.m_uid;
                Candidates.Add(new Candidate {
                    Uid = uid,
                    Zone = ZoneSystem.GetZone(refPos),
                    RttMs = LatencyRegistry.HasMeasurement(uid) ? LatencyRegistry.MeasuredRttMs(uid) : unmeasuredMs,
                    CanOwn = true,
                    IsViewer = true,
                });
            }
        }

        /// <summary>
        /// The persistent ZDOs near any candidate, each exactly once. Vanilla gathers this same
        /// set once per candidate; we take the union of every candidate's scan square at the zone
        /// level and read each zone's sector list a single time, so the scan cost is bounded by
        /// the number of distinct zones players occupy rather than by player count, and no per-ZDO
        /// bookkeeping is needed to dedup.
        /// </summary>
        private static void CollectConsideredZdos(ZDOMan zdoMan, bool tallyLoad) {
            Considered.Clear();
            ConsideredOwner.Clear();
            OwnedCount.Clear();
            ZonesToScan.Clear();

            // The same (2 * activeArea + 1)^2 square that FindSectorObjects(zone, activeArea, 0)
            // walks - just collected across all candidates first, so overlapping squares cost
            // nothing extra.
            int area = ZoneSystem.instance.m_activeArea;
            for (int i = 0; i < Candidates.Count; i++) {
                Vector2i centre = Candidates[i].Zone;
                for (int dx = -area; dx <= area; dx++) {
                    for (int dy = -area; dy <= area; dy++) {
                        ZonesToScan.Add(new Vector2i(centre.x + dx, centre.y + dy));
                    }
                }
            }

            // The unowned count is the health metric for the rescue path - in a steady world it
            // tracks the release ring's population and stays flat, and a number that climbs pass
            // on pass means rescue is not keeping up. The load tally feeds BuildVerdict and counts
            // only Prioritized ZDOs (characters, ships - things that actually cost their owner
            // simulation time), not walls and trees: counting everything persistent meant anyone
            // standing in a base was instantly saturated and the term stopped saying anything.
            LastPassUnownedOnEntry = 0;
            foreach (Vector2i zone in ZonesToScan) {
                ZoneScratch.Clear();
                zdoMan.FindObjects(zone, ZoneScratch);

                for (int j = 0; j < ZoneScratch.Count; j++) {
                    ZDO zdo = ZoneScratch[j];
                    if (zdo == null || !zdo.Persistent) { continue; }

                    long owner = zdo.GetOwner();
                    Considered.Add(zdo);
                    ConsideredOwner.Add(owner);

                    if (owner == 0L) { LastPassUnownedOnEntry++; continue; }
                    if (tallyLoad && zdo.Type == ZDO.ObjectType.Prioritized) {
                        OwnedCount.TryGetValue(owner, out int count);
                        OwnedCount[owner] = count + 1;
                    }
                }
            }
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
        ///
        /// Only latency-driven moves off a healthy, present owner reach here. Restoring an owner
        /// to a ZDO that has none is correctness, not placement, and is applied unbudgeted in
        /// RunPass - so every improvement below is finite and the sort is meaningful.
        /// </summary>
        private static void ApplyUpgrades(float now) {
            LastPassOptimised = 0;
            LastPassDeferred = 0;

            // The configured cap is a floor, not a ceiling: it is tuned for a small group, and a
            // fixed number of transfers per pass means convergence time grows linearly with the
            // number of players who just moved. Scaling it to the candidate count keeps the
            // per-player budget constant - a 200-player server moves at most 200 objects per
            // pass, which is still one resend per player every two seconds.
            int cap = Mathf.Max(Mathf.Max(1, ValConfig.OwnershipMaxReassignsPerPass.Value), Candidates.Count);
            LastPassCap = cap;

            if (Pending.Count == 0) { return; }

            Pending.Sort((a, b) => b.ImprovementMs.CompareTo(a.ImprovementMs));

            // No single peer may soak up the whole budget in one pass: each transfer is a resend
            // plus new simulation load for the receiver, and a pass that hands one player
            // everything is the concentration problem in miniature.
            int perTargetCap = Mathf.Max(1, (cap + 1) / 2);
            MovesPerTarget.Clear();

            for (int i = 0; i < Pending.Count; i++) {
                if (LastPassOptimised >= cap) { LastPassDeferred += Pending.Count - i; break; }

                PendingMove move = Pending[i];
                MovesPerTarget.TryGetValue(move.NewOwner, out int taken);
                if (taken >= perTargetCap) { LastPassDeferred++; continue; }
                MovesPerTarget[move.NewOwner] = taken + 1;

                move.Zdo.SetOwner(move.NewOwner);
                OwnerHistory[move.Zdo.m_uid] = new OwnershipRecord { Owner = move.NewOwner, ChangedAt = now, LastSeenAt = now };
                LastPassOptimised++;
                TotalOptimised++;
            }
        }

        private static void PruneHoldTable(float now) {
            if (now - _lastPrune < HoldTableTtlSeconds) { return; }
            _lastPrune = now;

            List<ZDOID> stale = null;
            foreach (KeyValuePair<ZDOID, OwnershipRecord> entry in OwnerHistory) {
                if (now - entry.Value.LastSeenAt > HoldTableTtlSeconds) {
                    (stale ??= new List<ZDOID>()).Add(entry.Key);
                }
            }
            if (stale == null) { return; }
            for (int i = 0; i < stale.Count; i++) { OwnerHistory.Remove(stale[i]); }
        }

        internal static void Reset() {
            Candidates.Clear();
            ZonesToScan.Clear();
            ZoneScratch.Clear();
            Considered.Clear();
            ConsideredOwner.Clear();
            OwnerHistory.Clear();
            Pending.Clear();
            SectorCache.Clear();
            VerdictPool.Clear();
            _verdictsInUse = 0;
            OwnedCount.Clear();
            MovesPerTarget.Clear();
            // Both timers are realtimeSinceStartup-based and so survive a shutdown; clearing them
            // means the next session prunes and warns on its own schedule rather than inheriting
            // one that has already half-elapsed.
            _lastPrune = 0f;
            _lastRescueWarning = 0f;
            _rescueBurstStreak = 0;
            LastPassCandidates = 0;
            LastPassConsidered = 0;
            LastPassUnownedOnEntry = 0;
            LastPassRescued = 0;
            LastPassReleased = 0;
            LastPassOptimised = 0;
            LastPassDeferred = 0;
            LastPassCap = 0;
            TotalRescued = 0;
            TotalOptimised = 0;
            LastPassMs = 0f;
        }
    }
}
