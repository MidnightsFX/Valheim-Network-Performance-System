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
    /// This replaces the tie-break with an explicit cost function - two of them, because "who
    /// should own this" has two different answers depending on what the object is. The pass is
    /// tiered:
    ///
    ///   * TIER 1 - simulated things that move: creatures, and Prioritized ZDOs (ships, carts).
    ///     Their state changes continuously between sends, so every viewer eats
    ///     (rtt(owner) + rtt(viewer)) / 2 of staleness, and the cost function above is exactly the
    ///     right thing to minimise. Creatures are found by prefab, not by ZDO.ObjectType: the game
    ///     marks ships and players Prioritized and leaves every creature Default, which kept them
    ///     out of this tier entirely until 1.8.0 (OwnershipPolicy.IsCreature). They are also the
    ///     tier's first priority - their moves drain first and are pushed out at once - and the
    ///     one kind of object never released from an owner that still has it loaded; see
    ///     OwnerStillLoads.
    ///   * TIER 2 - interactables that do not move: pickables, ore veins, rocks, trees, logs,
    ///     destructibles. Staleness is meaningless for these - a berry bush's ZDO changes once,
    ///     when somebody picks it. What costs the player is the interaction, which
    ///     ZNetView.InvokeRPC addresses at the owner, so picking a berry owned by another client
    ///     costs four network legs for one keypress. Tier 2 therefore minimises the expected
    ///     interaction round trip, whose dominant term is "is the person about to touch it the
    ///     owner" - so it places by distance, not by ping. See ArbitrateZone's tier split.
    ///
    /// Note what it still does NOT do. It never moves a building, container, crafting station,
    /// piece or portal away from a present owner: there is nothing to win, and the move races the
    /// game's owner-addressed item RPCs, which for a station means a consumed item (see
    /// RpcOwnerRouter). And it never moves a Player, Ship, ridden tame or attached cart away from
    /// a healthy owner, because authority must follow direct control - that is the failure mode
    /// behind ship-helm stutter. Tier 2 is the same principle one step weaker: it will not take an
    /// object from an owner still standing within reach of it, because that owner may be mid-swing.
    ///
    /// A pass does two things, and keeping them apart is load-bearing:
    ///
    ///   * RESCUE - a ZDO with no owner, or whose owner has left the sector or the session, has
    ///     nothing simulating it at all. Vanilla restores these unbudgeted on the pass that finds
    ///     them, and creatures have no other recovery path (nothing in BaseAI, MonsterAI or
    ///     Character ever calls ClaimOwnership). This is correctness, so it is uncapped.
    ///   * OPTIMISATION - moving a ZDO from a healthy, present owner to a better one: lower-latency
    ///     for tier 1, nearer for tier 2. This is a preference, so it carries the per-pass cap, the
    ///     min-hold hysteresis and the challenge margin - each tier with its own, drained tier 1
    ///     first (see ApplyUpgrades).
    ///
    /// Routing rescues through the optimisation budget is what froze creatures mid-animation and
    /// made them immune to damage: an unowned Character drops every RPC_Damage at its IsOwner
    /// check while ZSyncAnimation keeps replaying the last-replicated run cycle.
    ///
    /// Cost shape matters here because the pass runs every two seconds on the host's main thread
    /// and visits every persistent ZDO near every player. Everything that used to be a dictionary
    /// write per ZDO - dedup, hold-history - is now either structural (each zone is scanned
    /// exactly once, so there is nothing to dedup) or deferred until a ZDO is actually contested.
    /// On a full server that is the difference between a few milliseconds and a visible hitch.
    ///
    /// The pass is organised around one fact about the cost of reading a ZDO: ZDO.GetOwner is a
    /// dictionary lookup keyed by ZDOID, and ZDOExtraData.GetOwner spells it out as ContainsKey
    /// followed by the indexer - two full probes, each hashing a ZDOID through a List indexer.
    /// ZDO.HasOwner and ZDO.IsOwner, by contrast, are single bits of m_dataFlags maintained by
    /// SetOwnerInternal. Profiling put GetOwner at roughly half this pass's cost on a loaded
    /// frame, so the loops below are shaped to answer as many ZDOs as possible from the flags and
    /// to read an owner id only where a decision genuinely needs one:
    ///
    ///   * Zones are visited one at a time and the sector verdict is fetched once per zone. A
    ///     ZDO's sector is the key of the list it lives in, so nothing calls GetSector and there
    ///     is no intermediate list of considered ZDOs to fill and index back out.
    ///   * A zone no candidate covers releases everything owned - a flag test per ZDO, no lookups.
    ///     At the stock near simulation distance of 2 the scan square is 5x5 and the presence
    ///     square is 3x3, so that is sixteen of every twenty-five zones a lone player pulls in.
    ///   * A zone exactly one candidate covers collapses "owner present" and "owner is best" into
    ///     one test, which the flags answer outright whenever that candidate is the host.
    ///   * Only genuinely contested zones run the full comparison.
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
            internal Vector2s Zone;
            internal float RttMs;
            internal bool CanOwn;      // eligible to be assigned ownership
            internal bool IsViewer;    // a human sees this ZDO from here, so its staleness counts

            /// <summary>Zone radius this candidate loads - the old ZoneSystem.m_activeArea, now
            /// negotiated per peer, so it is captured here rather than read once for the pass.</summary>
            internal int NearRadius;

            /// <summary>Whether that near ring is the full square (classic distance) or the
            /// disc-clipped one. Together with NearRadius this is exactly what the candidate has
            /// instantiated - see ZoneCompat.NearRingLoaded, and OwnerStillLoads for why a creature
            /// owner is judged by it.</summary>
            internal bool NearClassic;

            /// <summary>The candidate's own world position, not its zone centre. Tier 2 places by
            /// distance to the ZDO itself, and ZoneSystem.GetZonePos quantises to 64m - larger than
            /// the whole claim radius, so every candidate in a zone would score identically.
            /// ZNetPeer.GetRefPos and ZNet.GetReferencePosition are both plain field reads and
            /// BuildCandidates already reads both, so this costs twelve bytes and nothing else.</summary>
            internal Vector3 RefPos;
        }

        private struct Verdict {
            internal float TotalCostMs;
            internal float WorstCostMs;
            internal float OwnerRttMs;
        }

        private static readonly List<Candidate> Candidates = new List<Candidate>();

        /// <summary>Session id -> index into Candidates, rebuilt with it every pass. Asked only
        /// about the owner of a creature that is outside every presence square covering it, which
        /// is the one question that has to find a candidate who is not in the sector's Present
        /// list.</summary>
        private static readonly Dictionary<long, int> CandidateIndex = new Dictionary<long, int>();

        /// <summary>Every zone inside at least one candidate's scan square this pass. Scanning each
        /// zone exactly once is what keeps the pass duplicate-free without a per-ZDO dictionary:
        /// a ZDO lives in exactly one sector list, so it can only be visited once.
        ///
        /// Except that zones outside the sector grid all share one list (ZoneCompat.InSharedBucket),
        /// so those zones are not walked one by one. Their objects are walked once, from the
        /// shared list, each judged by the zone it is really in - see GroupSharedBucket.</summary>
        private static readonly HashSet<Vector2s> ZonesToScan = new HashSet<Vector2s>();

        /// <summary>
        /// Objects and portals from the shared out-of-grid list, grouped by the zone each one is
        /// really in, for the zones in ZonesToScan and no others. Rebuilt every pass that scans
        /// such a zone, from lists kept in SharedBucketPool.
        ///
        /// Walking that list once per zone is what flipped the same objects between a dedicated
        /// server and nobody on every pass: a server whose reference position a mod had put ~1000km
        /// out scanned 25 zones that all answered with the same 170 objects, nine of them "the
        /// server is present" and sixteen "nobody is", so each object was rescued three times and
        /// released three times a pass, all session. Grouping by position also keeps playable
        /// space that mods build outside the grid arbitrated like anywhere else.
        /// </summary>
        private static readonly Dictionary<Vector2s, List<ZDO>> SharedBucketObjects = new Dictionary<Vector2s, List<ZDO>>();
        private static readonly Dictionary<Vector2s, List<ZDO>> SharedBucketPortals = new Dictionary<Vector2s, List<ZDO>>();
        private static readonly List<List<ZDO>> SharedBucketPool = new List<List<ZDO>>();
        private static int _sharedBucketListsInUse;

        /// <summary>Some zone in ZonesToScan is outside the sector grid this pass.</summary>
        private static bool _scanSharedBucket;

        private static readonly Dictionary<ZDOID, OwnershipRecord> OwnerHistory = new Dictionary<ZDOID, OwnershipRecord>();

        // Two pending lists rather than one with a tier key. The tier is then the list you are in,
        // which is the same "structural rather than bookkeeping" argument this class already makes
        // about zone-level dedup - and the two scores are in different units (milliseconds of
        // staleness against metres of approach), so there is no meaningful order across the
        // boundary for a single sort to find. See ApplyUpgrades.
        private static readonly List<PendingMove> PendingSimulated = new List<PendingMove>();
        private static readonly List<PendingMove> PendingInteractive = new List<PendingMove>();

        /// <summary>Creatures first, then by priority. Only the tier-1 list ever holds a creature,
        /// so on the tier-2 list this is the plain priority sort it always was. On tier 1 it puts
        /// every creature ahead of every cart or idle ship whatever the numbers say: a misplaced
        /// cart answers slowly, a misplaced creature is a fight that lags or a hit that does
        /// nothing, and the per-pass cap should be spent on that first.</summary>
        private static readonly System.Comparison<PendingMove> ByPriority = (a, b) => {
            if (a.Creature != b.Creature) { return a.Creature ? -1 : 1; }
            return b.Priority.CompareTo(a.Priority);
        };

        private static readonly Dictionary<Vector2s, SectorVerdict> SectorCache = new Dictionary<Vector2s, SectorVerdict>();
        private static readonly List<SectorVerdict> VerdictPool = new List<SectorVerdict>();
        private static int _verdictsInUse;
        private static readonly Dictionary<long, int> OwnedCount = new Dictionary<long, int>();
        private static readonly Dictionary<long, int> MovesPerTarget = new Dictionary<long, int>();

        /// <summary>Player id -> session id, for giving an abandoned ship to the player at its
        /// helm. Built at most once per pass and only when a rescued ship names a helmsman, so a
        /// pass with no such ship pays nothing for it.</summary>
        private static readonly Dictionary<long, long> PeerByPlayerId = new Dictionary<long, long>();
        private static bool _peerByPlayerIdBuilt;

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
            /// count as present, or ZDOs it legitimately owns would read as abandoned.
            ///
            /// A list rather than a set: BuildVerdict visits each candidate once so there is
            /// nothing to dedup, and the membership test runs per contested ZDO over a handful
            /// of longs, where a linear scan beats hashing. Its size is also what selects the
            /// specialised zone loops in RunPass - see ZoneMode.</summary>
            internal readonly List<long> Present = new List<long>();

            /// <summary>Index into Candidates for each entry of Present, in the same order.
            ///
            /// Tier 2 scores per ZDO rather than per sector, so it needs each present candidate's
            /// reference position and CanOwn flag. Both alternatives pay per ZDO for something
            /// BuildVerdict already knows per sector: re-running InActiveArea over every candidate,
            /// or searching Candidates by uid.
            ///
            /// Valid for exactly one pass. Candidates is rebuilt at the top of RunPass and
            /// SectorCache and the verdict pool are reset in the same statement block, so an index
            /// can never outlive the list it points into. A refactor that deferred any of this to a
            /// later frame would have to revisit that.</summary>
            internal readonly List<int> PresentIndex = new List<int>();

            internal void Clear() {
                HasEligible = false;
                BestUid = 0L;
                BestTotalMs = float.MaxValue;
                TotalByOwner.Clear();
                Present.Clear();
                PresentIndex.Clear();
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
        /// history after every cheaper early-out has passed, the Prioritized gate included, so the
        /// table scales with the creatures nearby rather than the buildings. A ZDO whose owner is
        /// already the best choice never needs a hold timestamp; when a better candidate later
        /// appears, its first sight in the table counts as a change and it gets one hold-width of
        /// grace before it can move. That is deliberately conservative, and it keeps the table
        /// proportional to the contested set rather than to the whole nearby world.
        /// </summary>
        private struct OwnershipRecord {
            internal long Owner;
            internal float ChangedAt;
            internal float LastSeenAt;
        }

        private struct PendingMove {
            internal ZDO Zdo;
            internal long NewOwner;

            /// <summary>Sort key, higher first - and it means a different thing per tier, because
            /// the tiers optimise different quantities: milliseconds of staleness removed for tier
            /// 1, metres inside the claim radius for tier 2. Only ever compared within one list,
            /// which is one of the two reasons there are two lists. See ApplyUpgrades.</summary>
            internal float Priority;

            /// <summary>The verdict for the sector this ZDO lives in, so a tier-2 move's force-send
            /// can address exactly the peers that can hold an instance of it. Safe to hold for the
            /// same reason SectorVerdict.PresentIndex is safe to store: the pool and the cache are
            /// only reset at the top of RunPass, and ApplyUpgrades runs at the end of that same
            /// pass.</summary>
            internal SectorVerdict Sector;

            /// <summary>A tier-1 move made by the proximity layer rather than by the cost
            /// function: the object is going to the one player standing near it. It shares tier
            /// 1's queue, budget and sort, and differs in two ways when applied - it is pushed to
            /// the sector at once, because the player it is going to is about to hit the thing,
            /// and it is counted and reported apart.</summary>
            internal bool Proximity;

            /// <summary>The object is a creature: sorted ahead of everything else in its queue and
            /// pushed out at once when applied, whichever rule chose it. See ByPriority and
            /// Drain.</summary>
            internal bool Creature;

            /// <summary>Who owned it when the move was queued. A move is pushed to the sector's
            /// Present set, and a creature's owner may be outside it - still simulating the
            /// creature from its loaded ring - while being the one machine that most needs to
            /// hear, because it is the one still writing.</summary>
            internal long OldOwner;
        }

        // Diagnostics. Rescue and optimisation are counted apart on purpose: they are different
        // operations under different budgets, and a report that merges them cannot tell "the
        // world is churning normally" from "part of it has no simulator at all".
        internal static int LastPassCandidates;

        /// <summary>M21: peers dropped from candidacy this pass because they had stopped
        /// answering. Their ZDOs go down the rescue path, so a non-zero figure here and a spike in
        /// LastPassRescued are the same event seen twice.</summary>
        internal static int LastPassGhostsExcluded;
        internal static long TotalGhostsExcluded;

        internal static int LastPassConsidered;
        internal static int LastPassUnownedOnEntry;
        internal static int LastPassRescued;
        internal static int LastPassHelmRescued;   // of those rescues, ships given to the player at their helm
        internal static int LastPassReleased;
        internal static int LastPassOptimised;
        internal static int LastPassDeferred;      // optimisations only; rescues are never deferred
        internal static int LastPassStaticHeld;    // present, not-best owner kept because the ZDO does not move
        internal static int LastPassCap;           // effective per-pass transfer cap after auto-scaling
        internal static long TotalRescued;
        internal static long TotalHelmRescued;
        internal static long TotalOptimised;
        internal static float LastPassMs;

        // Tier 2. Counted apart from tier 1 for the same reason rescue and optimisation are: they
        // run under different budgets and answer different questions, and a merged figure cannot
        // tell "creatures are being placed" from "berry patches are being handed to whoever walked
        // up".
        internal static int LastPassInteractiveOptimised;
        internal static int LastPassInteractiveDeferred;

        /// <summary>Confirmed interactable, a nearer eligible player exists, kept by the hold.
        /// Note this counts only ZDOs tier 2 got as far as CLASSIFYING: one rejected earlier on
        /// radius, stand-off or margin lands in LastPassStaticHeld instead, because the distance
        /// arithmetic deliberately runs before the prefab probe. See TryArbitrateInteractive.</summary>
        internal static int LastPassInteractiveHeld;
        internal static int LastPassInteractiveCap;

        /// <summary>Of LastPassRescued, those placed on the nearest player rather than on the
        /// lowest-latency one. An unvisited berry patch is unowned, so it is rescued rather than
        /// optimised - which makes this, not the tier-2 counter, the figure that moves first when
        /// somebody walks into fresh ground.</summary>
        internal static int LastPassNearestRescued;
        internal static long TotalInteractiveOptimised;

        // The proximity layer: simulated objects with exactly one player near them. Kept counts
        // every such object left where it was, whether because that player already owned it or
        // because its owner was not yet far enough away to lose it - either way the cost function
        // was not asked. Pulled counts moves actually applied, not queued.
        internal static int LastPassProximityKept;
        internal static int LastPassProximityPulled;
        internal static int LastPassProximityRescued;
        internal static long TotalProximityPulled;
        internal static long TotalProximityRescued;

        // Creatures. Rescued and optimised are subsets of the pass-wide counters above, split out
        // because creatures are the reason tier 1 exists and they were invisible to it before
        // 1.8.0. Kept counts creatures whose owner is outside every presence square covering them
        // but still has them loaded - left where they are rather than released or rescued away.
        internal static int LastPassCreaturesRescued;
        internal static int LastPassCreaturesOptimised;
        internal static int LastPassCreaturesKept;
        internal static long TotalCreaturesRescued;
        internal static long TotalCreaturesOptimised;

        // Tier 2's settings, read once per pass. ArbitrateZone already carries five arguments down
        // from RunPass and four more would be noise - but the real reason these are fields is that
        // they are read on a path that runs per ZDO rather than per zone, and a BepInEx
        // ConfigEntry<T>.Value is a property read and an unbox, not a field.
        private static bool _interactiveEnabled;
        private static bool _evictGhostOwners;
        private static float _interactiveClaimRadius;
        private static float _interactiveClaimRadiusSq;
        private static float _interactiveMargin;
        private static float _interactiveHold;

        // The proximity layer's settings, read once per pass for the same reason. Both distances
        // are kept squared: the scan compares squares and never needs a root.
        private static bool _proximityEnabled;
        private static float _proximityRadiusSq;
        private static float _proximityPullSq;

        /// <summary>Arbitrate Creatures, read once per pass. Off leaves every creature on the
        /// static path exactly as before 1.8.0.</summary>
        private static bool _creaturesEnabled;

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

        // Network monitoring's denominators: how many simulated objects sat in a zone one
        // candidate covered, against how many sat in a contested one. Only counted while
        // monitoring is on. See CountCreatures.
        private static int _monitorSoleCreatures;
        private static int _monitorContestedCreatures;

        internal static void RunPass(ZDOMan zdoMan) {
            if (ZNet.instance == null || ZoneSystem.instance == null) { return; }

            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            float now = Time.realtimeSinceStartup;
            float minHold = ValConfig.OwnershipMinHoldSeconds.Value;
            float margin = ValConfig.OwnershipChallengeMarginMs.Value;
            float loadPenalty = ValConfig.OwnershipLoadPenaltyMs.Value;

            _interactiveEnabled = ValConfig.EnableInteractiveOwnership.Value;
            _interactiveClaimRadius = ValConfig.OwnershipInteractiveClaimRadius.Value;
            _interactiveClaimRadiusSq = _interactiveClaimRadius * _interactiveClaimRadius;
            _interactiveMargin = ValConfig.OwnershipInteractiveChallengeMargin.Value;
            _interactiveHold = ValConfig.OwnershipInteractiveMinHoldSeconds.Value;
            _evictGhostOwners = PatchGuard.IsActive(Mechanism.PeerLiveness)
                                && ValConfig.EvictGhostOwners.Value;

            _proximityEnabled = ValConfig.EnableCreatureProximityOwnership.Value;
            float proximityRadius = ValConfig.CreatureProximityRadius.Value;
            float proximityPull = proximityRadius + OwnershipPolicy.ProximityPullMarginMetres;
            _proximityRadiusSq = proximityRadius * proximityRadius;
            _proximityPullSq = proximityPull * proximityPull;
            _creaturesEnabled = ValConfig.OwnershipArbitrateCreatures.Value;

            LastPassGhostsExcluded = 0;
            BuildCandidates(zdoMan);
            TotalGhostsExcluded += LastPassGhostsExcluded;
            LastPassCandidates = Candidates.Count;
            if (Candidates.Count == 0) { LastPassMs = 0f; return; }

            CollectZonesToScan();
            GroupSharedBucket(zdoMan);
            TallyOwnedLoad(zdoMan, loadPenalty > 0f);

            SectorCache.Clear();
            _verdictsInUse = 0;
            PendingSimulated.Clear();
            PendingInteractive.Clear();
            _peerByPlayerIdBuilt = false;

            // Rescues, releases and the static-object hold are applied inside the zone loops
            // rather than queued, so their counters are reset here instead of in ApplyUpgrades.
            LastPassConsidered = 0;
            LastPassUnownedOnEntry = 0;
            LastPassRescued = 0;
            LastPassHelmRescued = 0;
            LastPassReleased = 0;
            LastPassStaticHeld = 0;
            LastPassInteractiveHeld = 0;
            LastPassNearestRescued = 0;
            LastPassProximityKept = 0;
            LastPassProximityRescued = 0;
            LastPassCreaturesRescued = 0;
            LastPassCreaturesKept = 0;
            _monitorSoleCreatures = 0;
            _monitorContestedCreatures = 0;

            long sessionId = zdoMan.m_sessionID;

            // Walk zone by zone rather than over a flat pre-collected list. A ZDO's sector is the
            // key of the list it lives in, so the zone is already known here: nothing needs
            // GetSector, nothing needs a per-ZDO SectorCache probe, and there is no considered
            // list to fill and then index back out. The verdict is fetched once per zone.
            foreach (Vector2s zone in ZonesToScan) {
                // Its list is every out-of-grid object, not this zone's; they are judged below,
                // once each, by the zone they are really in.
                if (_scanSharedBucket && ZoneCompat.InSharedBucket(zone)) { continue; }

                List<ZDO> objects = ZoneObjects(zdoMan, zone);
                List<ZDO> portals = ZonePortals(zdoMan, zone);
                bool hasObjects = objects != null && objects.Count > 0;
                bool hasPortals = portals != null && portals.Count > 0;
                if (!hasObjects && !hasPortals) { continue; }

                SectorVerdict verdict = VerdictFor(zone);

                // Two lists, one verdict: the decision is a property of the sector, and portals
                // only sit apart because the game moved them to their own store.
                if (hasObjects) { ApplyVerdict(objects, zone, verdict, sessionId, now, minHold, margin); }
                if (hasPortals) { ApplyVerdict(portals, zone, verdict, sessionId, now, minHold, margin); }
            }

            if (_scanSharedBucket) {
                foreach (KeyValuePair<Vector2s, List<ZDO>> group in SharedBucketObjects) {
                    ApplyVerdict(group.Value, group.Key, VerdictFor(group.Key), sessionId, now, minHold, margin);
                }
                foreach (KeyValuePair<Vector2s, List<ZDO>> group in SharedBucketPortals) {
                    ApplyVerdict(group.Value, group.Key, VerdictFor(group.Key), sessionId, now, minHold, margin);
                }
            }

            ApplyUpgrades(now);
            PruneHoldTable(now);

            watch.Stop();
            LastPassMs = (float)watch.Elapsed.TotalMilliseconds;

            if (Monitoring.Active) {
                Monitoring.OnPassCompleted(ZonesToScan.Count, _monitorSoleCreatures, _monitorContestedCreatures);
            }

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
                Logger.LogDebug($"Ownership pass: considered {LastPassConsidered} ({LastPassUnownedOnEntry} unowned) over {ZonesToScan.Count} zones, rescued {LastPassRescued} ({LastPassHelmRescued} to a helmsman, {LastPassNearestRescued} to the nearest, {LastPassCreaturesRescued} creatures), released {LastPassReleased}, optimised {LastPassOptimised} ({LastPassCreaturesOptimised} creatures), deferred {LastPassDeferred}, creatures kept by a loading owner {LastPassCreaturesKept}, proximity {LastPassProximityPulled} pulled / {LastPassProximityKept} kept / {LastPassProximityRescued} rescued, interactive {LastPassInteractiveOptimised} ({LastPassInteractiveDeferred} deferred, {LastPassInteractiveHeld} held), static held {LastPassStaticHeld}, ghosts excluded {LastPassGhostsExcluded}, history {OwnerHistory.Count}, {LastPassMs:F1}ms.");
            }
        }

        /// <summary>
        /// Turn one sector verdict into action over one list of that sector's ZDOs.
        /// </summary>
        private static void ApplyVerdict(List<ZDO> objects, Vector2s zone, SectorVerdict verdict, long sessionId,
                                         float now, float minHold, float margin) {
            int present = verdict.Present.Count;

            // A pass of its own rather than a counter in the loops below, so that those stay
            // exactly as they were for everyone who is not monitoring.
            if (present > 0 && Monitoring.Active) { CountCreatures(objects, present == 1); }

            if (!verdict.HasEligible && present == 0) {
                // Nobody covers this zone at all, so no owner can be present and every owned ZDO
                // here is released. That decision needs no owner id, only the flag - which matters
                // because at the stock near simulation distance of 2 the scan square is 5x5 while
                // the presence square is 3x3, so sixteen of every twenty-five zones a lone
                // candidate pulls in land here. The one exception is a creature whose owner still
                // has it loaded, and that is the only case that reads an owner id.
                ReleaseZone(objects, zone);
            } else if (verdict.HasEligible && present == 1) {
                // Exactly one candidate covers this zone, and BuildVerdict only ever picks a
                // winner from the candidates it marked present - so the sole present peer is
                // the best one. "Owner present" and "owner is best" collapse into the same
                // test, which is what lets AssignZone answer most ZDOs from flags alone.
                AssignZone(objects, zone, verdict, sessionId, now, minHold);
            } else {
                ArbitrateZone(objects, zone, verdict, now, minHold, margin);
            }
        }

        /// <summary>Cached verdict for a sector, built on first request this pass.</summary>
        private static SectorVerdict VerdictFor(Vector2s sector) {
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
        private static void BuildVerdict(Vector2s sector, SectorVerdict verdict) {
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
                if (!ZoneCompat.InActiveArea(sector, owner.Zone, ZoneCompat.ActiveZoneRadius)) { continue; }
                verdict.Present.Add(owner.Uid);
                verdict.PresentIndex.Add(i);              // parallel to Present; see PresentIndex

                Verdict score = Score(owner, sector);

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
            CandidateIndex.Clear();

            // The host is always present so that ZDOs it legitimately owns are not read as
            // abandoned, but it only competes for ownership when configured to.
            //
            // Note it is subject to the same active-area test as everyone else, which is what
            // makes host ownership safe rather than a Serverside-Simulations-style trade. A
            // dedicated server only instantiates objects around its own reference position, so
            // restricting it to zones it has actually loaded means it can never win a ZDO it
            // would then fail to simulate. On a vanilla dedicated server that position is the
            // world origin, so the practical consequence is: contested objects anywhere else go to
            // the lowest-RTT peer present, and contested objects in the origin zones go to the host
            // whenever two or more players are there (its cost is the theoretical minimum for
            // every viewer). That last part is intended - see the Allow Host As Owner config.
            //
            // Mods can move it, and do: one recorded server had it about 1000km out, far outside
            // the sector grid. That is handled like any other candidate outside the grid - see
            // GroupSharedBucket - and it simply owns whatever is really near it, which is usually
            // nothing.
            SimulationDistance hostDistance = ZoneCompat.Local();
            Vector3 hostPos = ZNet.instance.GetReferencePosition();
            Candidates.Add(new Candidate {
                Uid = zdoMan.m_sessionID,
                Zone = ZoneSystem.GetZone(hostPos),
                RefPos = hostPos,
                RttMs = 0f,                                                  // zero hops from its own simulation
                CanOwn = ValConfig.OwnershipAllowHostOwner.Value,
                IsViewer = !NpsEnv.IsDedicated(),                            // a listen host has a player looking
                NearRadius = Mathf.Max(1, hostDistance.NearSimulationDistance),
                NearClassic = hostDistance.IsClassic,
            });
            CandidateIndex[zdoMan.m_sessionID] = 0;

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
                // point, and it stays there until the first ServerSyncedPlayerData arrives or M23
                // finds the player's character. A peer that has told us nothing about where it is cannot be
                // simulating anything, so it is neither an owner, a viewer nor present - treating
                // it as a candidate at the origin handed it spawn-area objects it had not
                // instantiated, which then sat frozen until the next pass. No real player stands
                // at exactly y = 0 at the world centre.
                Vector3 refPos = netPeer.GetRefPos();
                if (refPos == Vector3.zero) { continue; }

                long uid = netPeer.m_uid;

                // M21. A peer that has stopped answering is still in this list - it keeps its slot
                // until the hang-up deadline, which M11 may have set to minutes - but it is not
                // simulating anything any more. Leaving it as a candidate is what made raising the
                // timeout cost something: everything it owned stayed frozen for the whole window,
                // because "owner present" was read from list membership rather than from whether
                // the peer was actually there.
                //
                // Dropping it here and nowhere else is deliberate. Every downstream rule already
                // keys off presence: ArbitrateZone's IsPresent test sends its ZDOs down the rescue
                // path, which is uncapped and un-held precisely because an unsimulated object is
                // broken rather than badly placed, and it runs ahead of the tier split so a ghost's
                // containers and stations are rescued too. That last part is not an exception to
                // the tier-2 exclusion - the exclusion exists to avoid moving a station out from
                // under a player who is mid-insert, and a peer sending nothing is by definition
                // not mid-anything. Vanilla reaches the same conclusion, just a timeout later.
                //
                // The peer is not disconnected, and nothing about this is visible to it: if it
                // comes back, NoteTraffic clears the flag and it competes for ownership again on
                // the next pass like anyone else.
                if (_evictGhostOwners && PeerLiveness.IsGhost(uid)) {
                    LastPassGhostsExcluded++;
                    continue;
                }
                SimulationDistance distance = ZoneCompat.For(netPeer);
                CandidateIndex[uid] = Candidates.Count;
                Candidates.Add(new Candidate {
                    Uid = uid,
                    Zone = ZoneSystem.GetZone(refPos),
                    RefPos = refPos,
                    RttMs = LatencyRegistry.HasMeasurement(uid) ? LatencyRegistry.MeasuredRttMs(uid) : unmeasuredMs,
                    CanOwn = true,
                    IsViewer = true,
                    NearRadius = Mathf.Max(1, distance.NearSimulationDistance),        // ZoneCompat.NearFor, without asking twice
                    NearClassic = distance.IsClassic,
                });
            }
        }

        /// <summary>
        /// The zones near any candidate, each exactly once. Vanilla gathers this same set once per
        /// candidate; we take the union of every candidate's scan square at the zone level, so the
        /// scan cost is bounded by the number of distinct zones players occupy rather than by
        /// player count, and no per-ZDO bookkeeping is needed to dedup.
        /// </summary>
        private static void CollectZonesToScan() {
            ZonesToScan.Clear();
            _scanSharedBucket = false;

            // The same (2 * near + 1)^2 square FindSectorObjects walks for that peer's near ring -
            // just collected across all candidates first, so overlapping squares cost nothing
            // extra. The radius is per candidate now that simulation distance is negotiated
            // individually: a peer running a longer distance loads zones a shorter-range peer
            // never sees, and those still need arbitrating.
            for (int i = 0; i < Candidates.Count; i++) {
                Candidate candidate = Candidates[i];
                Vector2s centre = candidate.Zone;
                int area = candidate.NearRadius;
                for (int dx = -area; dx <= area; dx++) {
                    for (int dy = -area; dy <= area; dy++) {
                        Vector2s zone = new Vector2s(centre.x + dx, centre.y + dy);
                        if (ZonesToScan.Add(zone) && !_scanSharedBucket && ZoneCompat.InSharedBucket(zone)) {
                            _scanSharedBucket = true;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Sorts the shared out-of-grid list into SharedBucketObjects and SharedBucketPortals by
        /// each object's real zone, keeping only zones some candidate scans. One walk of the list
        /// per pass, and only on a pass that scans such a zone at all - which on a server with
        /// nobody outside the grid is never, and costs one flag test.
        ///
        /// The groups are copies, so the verdicts applied to them later in the pass read a stable
        /// list: a pass changes owners, and nothing in it moves an object between sectors.
        /// </summary>
        private static void GroupSharedBucket(ZDOMan zdoMan) {
            foreach (List<ZDO> list in SharedBucketObjects.Values) { list.Clear(); }
            foreach (List<ZDO> list in SharedBucketPortals.Values) { list.Clear(); }
            SharedBucketObjects.Clear();
            SharedBucketPortals.Clear();
            _sharedBucketListsInUse = 0;

            if (!_scanSharedBucket) { return; }
            GroupByRealZone(ZoneCompat.SharedBucketObjects(zdoMan), SharedBucketObjects);
            GroupByRealZone(ZoneCompat.SharedBucketPortals(zdoMan), SharedBucketPortals);
        }

        private static void GroupByRealZone(List<ZDO> source, Dictionary<Vector2s, List<ZDO>> groups) {
            if (source == null) { return; }

            for (int i = 0; i < source.Count; i++) {
                ZDO zdo = source[i];
                if (zdo == null) { continue; }

                Vector2s zone = ZoneSystem.GetZone(zdo.GetPosition());
                if (!ZonesToScan.Contains(zone)) { continue; }                // nobody is anywhere near it

                if (!groups.TryGetValue(zone, out List<ZDO> group)) {
                    group = RentSharedBucketList();
                    groups[zone] = group;
                }
                group.Add(zdo);
            }
        }

        private static List<ZDO> RentSharedBucketList() {
            if (_sharedBucketListsInUse < SharedBucketPool.Count) {
                return SharedBucketPool[_sharedBucketListsInUse++];
            }
            List<ZDO> list = new List<ZDO>();
            SharedBucketPool.Add(list);
            _sharedBucketListsInUse++;
            return list;
        }

        /// <summary>
        /// A zone's live sector list, or null when the zone holds nothing.
        ///
        /// ZDOMan.FindObjects copies the list into a scratch buffer; we read it in place. The only
        /// ZDO mutation this pass performs is SetOwner, which writes the owner dictionary, two
        /// flag bits and the owner revision - re-bucketing happens in SetSector alone, and nothing
        /// here moves a ZDO. Skipping the copy matters now that each zone is walked twice, once to
        /// tally load and once to decide: the copy was a memcpy of every ZDO reference in the
        /// zone, which in a built-up base is the largest single allocation-shaped cost in the pass.
        /// </summary>
        private static List<ZDO> ZoneObjects(ZDOMan zdoMan, Vector2s zone) {
            return ZoneCompat.SectorObjects(zdoMan, zone);
        }

        /// <summary>
        /// A zone's portal ZDOs. Portals used to sit in the sector lists with everything else and
        /// now have a store of their own, so they need a second walk or they would silently drop
        /// out of arbitration - and a portal whose owner has left is exactly the kind of ZDO the
        /// rescue path exists for.
        /// </summary>
        private static List<ZDO> ZonePortals(ZDOMan zdoMan, Vector2s zone) {
            return ZoneCompat.PortalObjects(zdoMan, zone);
        }

        /// <summary>
        /// How many simulated objects each candidate already owns, which BuildVerdict turns into
        /// the load handicap. It has to complete before the first verdict is built, so it is a
        /// separate walk - and it is a cheap one: Persistent, Type and HasOwner are all flag reads
        /// on the ZDO itself, so only what survives all three (a character or a ship that has an
        /// owner) costs an owner lookup.
        ///
        /// Counting only simulated objects is deliberate - creatures, ships and carts, the things
        /// that actually cost their owner simulation time, not walls and trees. Counting everything
        /// persistent meant anyone standing in a base was instantly saturated and the term stopped
        /// saying anything. Creatures need a prefab lookup, which the flags above it cannot
        /// avoid: this is the one place the pass classifies every owned object, not just the
        /// contested ones. OwnershipPolicy answers it from a small direct-mapped cache in front of
        /// its prefab table, so the cost per object is an index and a compare.
        /// </summary>
        private static void TallyOwnedLoad(ZDOMan zdoMan, bool tallyLoad) {
            OwnedCount.Clear();
            if (!tallyLoad) { return; }

            foreach (Vector2s zone in ZonesToScan) {
                if (_scanSharedBucket && ZoneCompat.InSharedBucket(zone)) { continue; }   // once, below
                TallyZone(ZoneObjects(zdoMan, zone));
                TallyZone(ZonePortals(zdoMan, zone));
            }
            foreach (List<ZDO> group in SharedBucketObjects.Values) { TallyZone(group); }
            foreach (List<ZDO> group in SharedBucketPortals.Values) { TallyZone(group); }
        }

        private static void TallyZone(List<ZDO> objects) {
            if (objects == null) { return; }

            for (int i = 0; i < objects.Count; i++) {
                ZDO zdo = objects[i];
                if (zdo == null || !zdo.Persistent) { continue; }
                if (!zdo.HasOwner()) { continue; }
                if (zdo.Type != ZDO.ObjectType.Prioritized
                    && !(_creaturesEnabled && OwnershipPolicy.IsCreature(zdo))) { continue; }

                long owner = zdo.GetOwner();
                OwnedCount.TryGetValue(owner, out int count);
                OwnedCount[owner] = count + 1;
            }
        }

        /// <summary>
        /// No candidate covers this zone, so no owner can be present in it and every owned ZDO is
        /// released. Preserves vanilla's release-to-unowned behaviour so an owner who walked away
        /// does not leave a phantom behind: HasOwner() gates real game logic (ZSyncTransform only
        /// extrapolates when a ZDO claims an owner), so an absent owner is worse than no owner.
        ///
        /// Uncapped, and safe precisely because rescue is uncapped too: the moment an eligible
        /// candidate covers this zone again every ZDO here is re-owned on that same pass, exactly
        /// as vanilla does it. The two must stay symmetrical - capping one side and not the other
        /// is what left creatures frozen.
        ///
        /// Note what is absent: no ZDO.GetOwner for anything but a creature. HasOwner() is the
        /// Owned flag on the ZDO, and GetOwner is defined as "!Owned ? 0 : &lt;owner dictionary
        /// lookup&gt;", so the flag is exactly the "owner != 0" test this needs and the lookup never
        /// happens. Vanilla's own ReleaseNearbyZDOS reads it the same way.
        ///
        /// A creature is the exception, and the only one: its owner may still have it loaded - this
        /// zone is in that player's scan square, which is exactly the ring their game instantiates
        /// but does not count as present - and then it stays. See OwnerStillLoads.
        /// </summary>
        private static void ReleaseZone(List<ZDO> objects, Vector2s zone) {
            for (int i = 0; i < objects.Count; i++) {
                ZDO zdo = objects[i];
                if (zdo == null || !zdo.Persistent) { continue; }
                LastPassConsidered++;

                if (!zdo.HasOwner()) { LastPassUnownedOnEntry++; continue; }

                if (_creaturesEnabled && OwnershipPolicy.IsCreature(zdo) && OwnerStillLoads(zdo.GetOwner(), zone)) {
                    LastPassCreaturesKept++;
                    continue;
                }

                if (Monitoring.Active) { Monitoring.NoteCause(zdo, HandoffCause.Release); }
                zdo.SetOwner(0L);
                OwnerHistory.Remove(zdo.m_uid);
                LastPassReleased++;
            }
        }

        /// <summary>
        /// One candidate covers this zone and it is the eligible winner, so the whole decision
        /// reduces to "is this already owned by that candidate". Every other outcome - unowned, or
        /// owned by someone who has left - is a rescue to the same peer, and the optimisation path
        /// is unreachable because a present owner here can only be the best owner.
        ///
        /// Which means most ZDOs never need their owner id read at all. HasOwner() settles the
        /// unowned case, and IsOwner() - the Owner flag, which SetOwnerInternal maintains as
        /// "owner == ZDOMan.GetSessionID()" - settles the rest whenever the host is the sole
        /// candidate. When the winner is a remote peer, IsOwner() still rules out host-owned ZDOs
        /// for free and only the remainder pay for a lookup.
        ///
        /// Creatures are the one thing that costs a prefab lookup here, and only on the two
        /// branches that were going to change an owner anyway: an unowned one is pushed out to its
        /// new owner at once (RescueObject), and one owned by somebody outside the presence square
        /// who still has it loaded is not a rescue at all - see ConsiderLoadedCreature.
        /// </summary>
        private static void AssignZone(List<ZDO> objects, Vector2s zone, SectorVerdict verdict, long sessionId,
                                       float now, float minHold) {
            long bestUid = verdict.BestUid;
            bool bestIsSelf = bestUid == sessionId;

            for (int i = 0; i < objects.Count; i++) {
                ZDO zdo = objects[i];
                if (zdo == null || !zdo.Persistent) { continue; }
                LastPassConsidered++;

                if (!zdo.HasOwner()) {
                    LastPassUnownedOnEntry++;
                    RescueObject(zdo, bestUid, _creaturesEnabled && OwnershipPolicy.IsCreature(zdo), verdict, 0L, now);
                    continue;
                }

                if (bestIsSelf) {
                    if (zdo.IsOwner()) { continue; }
                } else if (!zdo.IsOwner() && zdo.GetOwner() == bestUid) {
                    continue;
                }

                // Owned by someone who is not the one candidate present here - so not present.
                long owner = zdo.GetOwner();
                bool creature = _creaturesEnabled && OwnershipPolicy.IsCreature(zdo);
                if (creature && OwnerStillLoads(owner, zone)) {
                    ConsiderLoadedCreature(zdo, verdict, owner, now, minHold);
                    continue;
                }

                RescueObject(zdo, bestUid, creature, verdict, owner, now);
            }
        }

        /// <summary>
        /// The general case: more than one candidate covers this zone, or candidates cover it but
        /// none may own. This is the only loop that has to read owner ids, and the only one where
        /// a ZDO can reach the optimisation path.
        /// </summary>
        private static void ArbitrateZone(List<ZDO> objects, Vector2s zone, SectorVerdict verdict, float now, float minHold, float margin) {
            for (int i = 0; i < objects.Count; i++) {
                ZDO zdo = objects[i];
                if (zdo == null || !zdo.Persistent) { continue; }
                LastPassConsidered++;

                // Unowned is a flag read, so the commonest rescue costs nothing to spot.
                if (!zdo.HasOwner()) {
                    LastPassUnownedOnEntry++;
                    if (verdict.HasEligible) {
                        bool unownedCreature = _creaturesEnabled && OwnershipPolicy.IsCreature(zdo);
                        RescueObject(zdo, RescueTarget(zdo, verdict, unownedCreature), unownedCreature, verdict, 0L, now);
                    }
                    continue;
                }

                long currentOwner = zdo.GetOwner();
                bool present = IsPresent(verdict, currentOwner);

                // Asked only where the answer changes what happens: here for an owner outside the
                // presence square, and at the tier split below for a present one - so at most once
                // per ZDO, and never for the buildings a present owner simply keeps.
                bool creature = false;

                if (!present) {
                    creature = _creaturesEnabled && OwnershipPolicy.IsCreature(zdo);

                    // Not present is not the same as not simulating, for a creature. The owner's
                    // game has instantiated its whole near ring, the presence square is only the
                    // middle of it, and an owner who has stepped out of the middle is still running
                    // the creature's AI and writing its position. Releasing or rescuing it away from
                    // them is what left creatures frozen mid-fight at the edge of a player's view.
                    if (creature && OwnerStillLoads(currentOwner, zone)) {
                        ConsiderLoadedCreature(zdo, verdict, currentOwner, now, minHold);
                        continue;
                    }
                }

                if (!verdict.HasEligible) {
                    // Nobody can take it - see ReleaseZone for why an absent owner is released
                    // rather than left in place.
                    if (!present) {
                        if (Monitoring.Active) { Monitoring.NoteCause(zdo, HandoffCause.Release); }
                        zdo.SetOwner(0L);
                        OwnerHistory.Remove(zdo.m_uid);
                        LastPassReleased++;
                    }
                    continue;
                }

                // Rescue: nothing is simulating this. Owned by someone who has left the sector or
                // the session. That is not a placement preference, it is a broken object -
                // BaseAI.UpdateAI returns early for non-owners, ZSyncTransform only extrapolates
                // when the ZDO claims an owner, ZSyncAnimation stops writing animator parameters,
                // and Character.Damage routes through ZRoutedRpc to owner 0, which broadcasts and
                // is then dropped by every receiver at RPC_Damage's IsOwner check. Frozen
                // mid-animation and silently invulnerable.
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
                if (!present) {
                    RescueObject(zdo, RescueTarget(zdo, verdict, creature), creature, verdict, currentOwner, now);
                    continue;
                }

                // Everything from here down is optimisation: the ZDO already has a healthy, present
                // owner and we are only deciding whether a better one exists. That is a preference,
                // so every cap and every hysteresis rule belongs here and nowhere else.
                // currentOwner != 0 and IsPresent(currentOwner) are invariants below, which is why
                // none of these checks re-test them - and so are verdict.HasEligible and
                // verdict.Present.Count >= 2, because ApplyVerdict routes the sole-candidate case
                // to AssignZone and the no-candidate case to ReleaseZone, and the !HasEligible
                // branch above has already continued. The tier-2 scan relies on the last of those:
                // the incumbent is always one of the candidates it prices.

                // THE TIER SPLIT. A single flag read - ZDO.Type is (ObjectType)(m_dataFlags &
                // DataFlags.Type) - and it has to come first, because the "owner is already the
                // sector's best" test below is a TIER 1 answer. It ranks by staleness, and tier 2
                // ranks by distance, so a berry bush owned by the lowest-latency player in the zone
                // is not thereby settled.
                //
                // TIER 1 - simulated things that move. Prioritized is the type ZNetView.m_type
                // stamps on ships, carts and players (ZDOMan.ServerSortSendZDOS reads it as "send
                // first"), and a flag read answers it. Creatures are the rest of this tier and the
                // reason it exists, and the type does NOT answer for them - every creature prefab
                // is Default - so they need the prefab lookup, paid only by a Default ZDO whose
                // owner is present in a contested zone. Placed to minimise staleness, because their
                // state changes continuously between sends and every viewer eats
                // (rtt(owner) + rtt(viewer)) / 2 of it.
                //
                // TIER 2 - interactables that do not move: pickables, ore veins, rocks, trees,
                // logs, destructibles. Staleness is the wrong metric for these and always was - a
                // berry bush's ZDO changes once, when somebody picks it. What costs the player is
                // the interaction itself. Pickable.Interact calls m_nview.InvokeRPC("RPC_Pick",
                // num), the overload that addresses m_zdo.GetOwner(), and Pickable.RPC_Pick opens
                // "if (!m_nview.IsOwner() || m_picked) return". Clients only ever peer with the
                // host, so a pick against another client's object costs four legs - picker -> host
                // -> owner, then owner -> host -> picker for the RPC_SetPicked broadcast - plus the
                // ZDO replication of the drops. MineRock5.RPC_Damage, MineRock.RPC_Hit,
                // Destructible, TreeBase and TreeLog all have the same shape. So tier 2 minimises
                // the expected interaction round trip, whose dominant term is simply "is the person
                // who will touch it the owner", and the proxy for that is proximity. RTT only
                // breaks an exact tie.
                //
                // Everything else - buildings, containers, stations, pieces, portals - keeps
                // vanilla's rule: first to arrive owns it, re-owned only when that owner leaves.
                // Two of the three reasons that rule existed still bind, and tier 2 is built around
                // them rather than against them:
                //
                //   * Every move opens a window - one send tick plus one-way latency per peer,
                //     longer under backpressure - in which every other peer's copy of the owner id
                //     is stale, and every owner-addressed handler returns silently on a non-owner.
                //     For a station that is a CONSUMED ITEM: Fermenter.RPC_AddItem,
                //     Smelter.RPC_AddOre/RPC_AddFuel, Fireplace.RPC_AddFuel and
                //     CookingStation.RPC_AddFuel all run after the caller has already removed the
                //     item from the inventory (see RpcOwnerRouter). For a pickable or a
                //     destructible it is a lost keypress and the player presses again - nothing is
                //     spent before the call and all the state is in the ZDO. That difference is
                //     exactly why tier 2 can accept the window and stations still cannot.
                //   * A host-side SetOwner bumps OwnerRevision only. A write the old owner already
                //     had on the wire carries (data+1, oldOwnerRev, oldOwner); ZDOMan.RPC_ZDOData
                //     applies it in full because the data revision is higher and drags owner and
                //     OwnerRevision back, while the old owner has already applied the new owner
                //     and - revisions now equal - is never re-sent. An active station writes its
                //     ZDO continuously, so that race repeats; a tier-2 object is written ONLY by
                //     the person interacting with it, and the person interacting with it is the one
                //     standing next to it - which is the candidate this tier keeps or gives the
                //     object to. OwnershipPolicy.InteractionStandOffMetres makes that explicit
                //     rather than incidental, and ForceSendTo shortens the window besides. (With
                //     Reject Stale Owner Updates on, OwnerRevisionGuard closes this race outright;
                //     the tier-2 rules above do not depend on it.)
                if (zdo.Type != ZDO.ObjectType.Prioritized) {
                    creature = _creaturesEnabled && OwnershipPolicy.IsCreature(zdo);
                    if (!creature) {
                        // Tier 2 first, then the static hold. TryArbitrateInteractive returns true
                        // when it reached a verdict of its own - a queued move, or an interactable
                        // it confirmed and then held - so the counter below keeps exactly the
                        // meaning it has always had: a present but not-best owner kept because the
                        // object does not move.
                        if (_interactiveEnabled && TryArbitrateInteractive(zdo, verdict, currentOwner, now)) { continue; }
                        if (verdict.BestUid != currentOwner) { LastPassStaticHeld++; }
                        continue;
                    }
                }

                // THE PROXIMITY LAYER. Tier 1 prices a whole sector by latency and weighs every
                // player whose active area covers it equally - which is right when they are all
                // watching the same fight, and wrong when two of them are a hundred metres apart
                // with a fight each. Being in range of each other is then enough for one player's
                // creatures to be simulated on the other's machine: every swing costs
                // rtt(attacker) + rtt(owner) instead of nothing, and the creature lags or freezes
                // around the handoff that put it there. With three players present the cost
                // function does that itself, handing a creature to a lower-latency bystander. With
                // exactly two it never challenges a present owner (the two totals are always
                // equal, and the load handicap is capped under the margin), but the same damage is
                // done earlier: whoever loaded the zone first owns what is in it, and an unowned
                // creature is rescued to the lower-latency of the two even with the other
                // standing on it.
                //
                // So: when EXACTLY ONE player is near an object, that player is its owner of
                // choice and the cost function is not consulted at all. If that player already
                // owns it, it is kept. If somebody else does, it is pulled to that player once
                // its owner is far enough away to lose it (OwnershipPolicy.ShouldPullToSoleNearby)
                // and the ordinary hold has run out, and kept where it is until then - NOT handed
                // to the sector's best in the meantime, which is the whole point. Two or more
                // players near it is a shared fight, and nobody near it is nobody's fight; both
                // fall through to the cost function exactly as before.
                //
                // Ahead of the "owner is already the sector's best" test on purpose: the commonest
                // shape of the two-player case is the lower-latency player owning a creature the
                // other one has just walked up to, and that test would retire it.
                //
                // The cost is SoleNearbyViewer: the same handful of multiplies tier 2 runs, per
                // simulated object in a contested zone. Everything that does not move was retired
                // by the tier split above without reaching this.
                if (_proximityEnabled) {
                    int sole = SoleNearbyViewer(verdict, zdo.GetPosition(), currentOwner,
                                                out int ownerIndex, out float ownerSq);
                    if (sole >= 0 && Candidates[sole].CanOwn) {
                        Candidate near = Candidates[sole];

                        // IsPresent above has already established that the owner is one of the
                        // candidates the scan walked, so ownerIndex is real. Belt and braces, as
                        // in tier 2: an owner we somehow failed to find declines the move.
                        //
                        // The hold is read last: Touch writes the history table, and only a ZDO
                        // that is otherwise about to move needs an entry in it. Same discipline
                        // as the cost function's path below.
                        if (near.Uid != currentOwner
                            && ownerIndex >= 0
                            && OwnershipPolicy.ShouldPullToSoleNearby(Candidates[ownerIndex].IsViewer, ownerSq, _proximityPullSq)
                            && !OwnershipPolicy.IsDirectlyControlled(zdo)
                            && now - Touch(zdo.m_uid, currentOwner, now) >= minHold) {
                            // Priority is the staleness the near player stops paying - the same
                            // quantity, in the same unit, as every other entry in this queue, so
                            // the shared sort in Drain stays meaningful across both kinds.
                            PendingSimulated.Add(new PendingMove {
                                Zdo = zdo, NewOwner = near.Uid,
                                Priority = (Candidates[ownerIndex].RttMs + near.RttMs) * 0.5f,
                                Sector = verdict, Proximity = true,
                                Creature = creature, OldOwner = currentOwner,
                            });
                        } else {
                            LastPassProximityKept++;
                        }
                        continue;
                    }
                }

                // Tier 1's cost function from here down, unchanged. A single compare that retires
                // nearly every remaining ZDO in the zone - in a settled world the owner already is
                // the best choice.
                if (verdict.BestUid == currentOwner) { continue; }

                // Directly-controlled objects follow their controller, never the cost function.
                // This is what stops us yanking a ship out from under its helmsman. Tier 2 does not
                // need this test - see TryArbitrateInteractive.
                if (OwnershipPolicy.IsDirectlyControlled(zdo)) { continue; }

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

                PendingSimulated.Add(new PendingMove {
                    Zdo = zdo, NewOwner = verdict.BestUid, Priority = improvement, Sector = verdict,
                    Creature = creature, OldOwner = currentOwner,
                });
            }
        }

        /// <summary>
        /// Whether a creature's owner still has it loaded: a candidate - so connected, positioned
        /// and not a ghost - whose near ring includes the creature's zone. That ring is what the
        /// owner's ZNetScene instantiates, so an owner that passes this is running the creature's
        /// AI and writing its position whether or not it counts as present.
        ///
        /// Asked only of creatures, and it is the whole difference in how they are treated at the
        /// edge of a player's area. The presence square is the middle 3x3 of what a player loads
        /// (the disc-clipped 5x5 at the stock distance), and everything else here reads "not
        /// present" as "not simulating". For a creature that is wrong in the outer ring: its owner
        /// is simulating it - a player who has backed off to shoot at it, or who is simply walking
        /// away - and releasing it or handing it to somebody else there was a creature frozen on
        /// the owner's screen and unhittable for as long as the change took to settle. Measured on
        /// a 25-player server, the old owner's next update undid such a change 93% of the time.
        /// </summary>
        private static bool OwnerStillLoads(long owner, Vector2s zone) {
            if (!CandidateIndex.TryGetValue(owner, out int index)) { return false; }
            Candidate candidate = Candidates[index];
            return ZoneCompat.NearRingLoaded(candidate.Zone, zone, candidate.NearRadius, candidate.NearClassic);
        }

        /// <summary>
        /// A creature whose owner is outside the presence square but still has it loaded (see
        /// OwnerStillLoads). It is not abandoned, so it is not rescued; it moves only if somebody
        /// present is actually near it, and then as an ordinary tier-1 move - capped, held, and
        /// pushed out at once - rather than an uncapped rescue every pass.
        ///
        ///   * nobody present may own, or nobody present is near it -> it stays where it is. The
        ///     owner is the one player simulating it for any reason; moving it would only swap a
        ///     working simulation for one on a machine that may not have been near it.
        ///   * a ridden mount                                         -> it stays. Its rider is its
        ///     controller wherever they are standing.
        ///   * exactly one present player near it                     -> to them, as a proximity
        ///     pull. The owner is outside the presence square, so at least a zone away and beyond
        ///     the pull distance at the default radius.
        ///   * two or more near it                                    -> a shared fight the owner
        ///     is not in, so the cost function's choice among the present candidates.
        ///
        /// The hold applies to all of it: a creature that has just changed hands keeps its owner
        /// for Min Hold Seconds whatever the geometry says.
        /// </summary>
        private static void ConsiderLoadedCreature(ZDO zdo, SectorVerdict verdict, long owner, float now, float minHold) {
            if (!verdict.HasEligible || OwnershipPolicy.IsDirectlyControlled(zdo)) {
                LastPassCreaturesKept++;
                return;
            }

            int sole;
            int near = NearbyViewers(verdict, zdo.GetPosition(), out sole);
            if (near == 0 || now - Touch(zdo.m_uid, owner, now) < minHold) {
                LastPassCreaturesKept++;
                return;
            }

            long target;
            bool proximity = near == 1 && _proximityEnabled && Candidates[sole].CanOwn;
            if (proximity) {
                target = Candidates[sole].Uid;
            } else {
                target = verdict.BestUid;
            }

            int ownerIndex = CandidateIndex[owner];                           // OwnerStillLoads found it
            int targetIndex = CandidateIndex.TryGetValue(target, out int found) ? found : ownerIndex;
            PendingSimulated.Add(new PendingMove {
                Zdo = zdo, NewOwner = target,
                Priority = (Candidates[ownerIndex].RttMs + Candidates[targetIndex].RttMs) * 0.5f,
                Sector = verdict, Proximity = proximity,
                Creature = true, OldOwner = owner,
            });
        }

        /// <summary>
        /// How many present VIEWERS are within the proximity radius of a position - 0, 1, or 2
        /// meaning "two or more" - and, when it is exactly one, which. SoleNearbyViewer's scan
        /// without the owner bookkeeping and with the two answers it folds together kept apart:
        /// a creature whose owner is outside the presence square is treated differently when
        /// nobody is near it (it stays) and when several are (the cost function picks among
        /// them), and a rescue has no owner to find. The radius is the proximity radius whether
        /// or not that layer is enabled: this asks whether anybody present is close enough to be
        /// fighting it, and that question does not go away with the layer.
        /// </summary>
        private static int NearbyViewers(SectorVerdict verdict, Vector3 pos, out int sole) {
            sole = -1;
            int count = 0;

            List<int> presentIndex = verdict.PresentIndex;
            for (int i = 0; i < presentIndex.Count; i++) {
                Candidate candidate = Candidates[presentIndex[i]];
                if (!candidate.IsViewer) { continue; }

                float dx = candidate.RefPos.x - pos.x;
                float dz = candidate.RefPos.z - pos.z;
                if (dx * dx + dz * dz > _proximityRadiusSq) { continue; }

                if (++count >= 2) { sole = -1; return 2; }
                sole = presentIndex[i];
            }
            return count;
        }

        /// <summary>
        /// Tier 2's decision for one ZDO: should this interactable move to whoever is standing
        /// nearest to it? Returns true when tier 2 answered for this ZDO - a move was queued, or
        /// the ZDO was confirmed interactable and then held - and false when, as far as tier 2 can
        /// tell, this is an ordinary static and the caller's static accounting applies.
        ///
        /// The order is the point. The arithmetic runs BEFORE the prefab lookup, which is the
        /// reverse of the obvious order and is deliberate:
        ///
        ///   * The scan is a handful of subtracts and multiplies over Present - at least two
        ///     entries, in practice two to four - against ZDO.GetPosition, which is "return
        ///     m_position" (ZDO.cs:568). No hashing, no pointer chasing.
        ///   * The classification is ZDO.GetPrefab (a plain int field, ZDO.cs:563) and then a
        ///     Dictionary&lt;int, PrefabClass&gt; probe - a hash and a bucket walk.
        ///   * And the cheap test is the selective one. In a contested zone the non-Prioritized
        ///     population is overwhelmingly building pieces, and most of them are neither near a
        ///     player nor nearer to somebody other than their owner. So the dictionary probe is
        ///     paid only by a ZDO that is genuinely about to move.
        ///
        /// The cost of that ordering is that a pickable rejected on radius, stand-off or margin is
        /// counted as static rather than interactive - see LastPassInteractiveHeld.
        ///
        /// Note what is NOT here: OwnershipPolicy.IsDirectlyControlled. It is not needed, because
        /// the one classification below already excludes every class it guards - Classify ranks
        /// Player, Ship, Vagon, Tameable and Container ahead of Interactive, so a prefab that is
        /// both (a cart with a destructible hull, a chest with one) never reads as interactable at
        /// all. IsDirectlyControlled's state tests exist to let an IDLE mount or cart be arbitrated
        /// as an ordinary Prioritized object; there is no equivalent concession to make for tier 2,
        /// so the stricter and cheaper answer is the right one.
        /// </summary>
        private static bool TryArbitrateInteractive(ZDO zdo, SectorVerdict verdict, long currentOwner, float now) {
            Vector3 pos = zdo.GetPosition();

            int nearest = -1;
            float nearestSq = float.MaxValue;
            float nearestRtt = float.MaxValue;
            float ownerSq = float.MaxValue;

            List<int> presentIndex = verdict.PresentIndex;
            for (int i = 0; i < presentIndex.Count; i++) {
                Candidate candidate = Candidates[presentIndex[i]];

                // XZ only. Every presence and interest test in the game is horizontal, and a cellar
                // is not a different object from the floor above it.
                float dx = candidate.RefPos.x - pos.x;
                float dz = candidate.RefPos.z - pos.z;
                float sq = dx * dx + dz * dz;

                // Measured before the CanOwn gate, for the same reason BuildVerdict prices before
                // it: the incumbent may be a host barred from owning, and its distance is still the
                // number the stand-off and the margin are measured against.
                if (candidate.Uid == currentOwner) { ownerSq = sq; }
                if (!candidate.CanOwn) { continue; }

                // RTT only as a tie-break, and it is not decoration: the host scores 0ms, so an
                // exact positional tie goes to the host, which is zero hops from every player and
                // therefore the best possible owner for an interaction by any of them.
                if (sq < nearestSq || (sq == nearestSq && candidate.RttMs < nearestRtt)) {
                    nearestSq = sq;
                    nearestRtt = candidate.RttMs;
                    nearest = presentIndex[i];
                }
            }

            if (nearest < 0) { return false; }                             // present, but nobody may own
            if (nearestSq > _interactiveClaimRadiusSq) { return false; }   // nobody is going to touch it

            long nearestUid = Candidates[nearest].Uid;
            if (nearestUid == currentOwner) { return false; }              // already on the right machine

            // IsPresent in the caller has already established that the owner is one of the
            // candidates scanned above, so ownerSq is real. Belt and braces: an owner we somehow
            // failed to measure reads as adjacent, which declines the move.
            if (ownerSq == float.MaxValue) { return false; }

            float nearestM = Mathf.Sqrt(nearestSq);
            float ownerM = Mathf.Sqrt(ownerSq);
            if (!OwnershipPolicy.ShouldPlaceInteractive(ownerM, nearestM,
                                                        _interactiveClaimRadius, _interactiveMargin)) {
                return false;
            }

            // Only now pay for the prefab.
            if (!OwnershipPolicy.IsInteractive(zdo)) { return false; }

            if (InteractiveHoldActive(zdo.m_uid, currentOwner, now)) {
                LastPassInteractiveHeld++;
                return true;
            }

            // Priority is "metres inside the claim radius", so the shared descending sort in Drain
            // does the nearest object first. Sorting by the improvement instead - how far behind the
            // incumbent is - would put a rock thirty metres away whose owner is a hundred metres
            // away ahead of the bush at your feet, which is exactly backwards: the object you are
            // about to touch is the one that has to win a capped pass.
            PendingInteractive.Add(new PendingMove {
                Zdo = zdo, NewOwner = nearestUid, Priority = _interactiveClaimRadius - nearestM, Sector = verdict,
            });
            return true;
        }

        /// <summary>
        /// Tier 2's hysteresis. Same table as Touch and the same TTL, but it measures from the last
        /// ownership change this pass RECORDED, not from the first sighting.
        ///
        /// Touch's convention - a ZDO the table has never seen is treated as having just changed
        /// hands - exists because tier 1's objects genuinely do change hands from gameplay code: a
        /// cart grab, a mount transfer, ZNetView.ClaimOwnership on a door. None of the seven tier-2
        /// components do. The only ClaimOwnership among them is Pickable.Awake's, on an
        /// already-picked object it Destroy()s in the next statement. So there is no
        /// player-initiated claim here to second-guess - and charging a first-sighting grace would
        /// mean the first berry you pick after walking into a patch is still the slow one, which is
        /// the entire thing this tier exists to fix.
        ///
        /// So: unseen is free to move, and Drain writes the record when the move is actually
        /// applied, which starts the hold for the next one. A move the cap deferred writes nothing
        /// and is reconsidered without penalty next pass. That is what makes a long hold
        /// affordable - it throttles churn without delaying the first placement at all.
        /// </summary>
        private static bool InteractiveHoldActive(ZDOID uid, long currentOwner, float now) {
            if (!OwnerHistory.TryGetValue(uid, out OwnershipRecord record)) { return false; }

            if (record.Owner != currentOwner) {
                // Somebody outside this pass changed the owner since we last looked. That gets the
                // same grace an arbiter move does - the rule Touch applies for tier 1.
                OwnerHistory[uid] = new OwnershipRecord { Owner = currentOwner, ChangedAt = now, LastSeenAt = now };
                return true;
            }

            record.LastSeenAt = now;
            OwnerHistory[uid] = record;
            return now - record.ChangedAt < _interactiveHold;
        }

        /// <summary>The nearest present candidate that may own, within the claim radius, or -1. The
        /// same scan TryArbitrateInteractive runs, without the incumbent bookkeeping - a rescue has
        /// no incumbent. The radius test is applied after the scan rather than folded into the seed
        /// so that it is the identical comparison the optimisation path makes, boundary included;
        /// "nobody within range" and "nobody eligible" then collapse into the same answer, and the
        /// caller falls back to the sector's best candidate either way.</summary>
        private static int NearestEligible(SectorVerdict verdict, Vector3 pos) {
            int nearest = -1;
            float nearestSq = float.MaxValue;
            float nearestRtt = float.MaxValue;

            List<int> presentIndex = verdict.PresentIndex;
            for (int i = 0; i < presentIndex.Count; i++) {
                Candidate candidate = Candidates[presentIndex[i]];
                if (!candidate.CanOwn) { continue; }

                float dx = candidate.RefPos.x - pos.x;
                float dz = candidate.RefPos.z - pos.z;
                float sq = dx * dx + dz * dz;
                if (sq < nearestSq || (sq == nearestSq && candidate.RttMs < nearestRtt)) {
                    nearestSq = sq;
                    nearestRtt = candidate.RttMs;
                    nearest = presentIndex[i];
                }
            }

            if (nearestSq > _interactiveClaimRadiusSq) { return -1; }
            return nearest;
        }

        /// <summary>
        /// The proximity layer's scan: the index into Candidates of the ONE player within the
        /// proximity radius of this position, or -1 when nobody is or more than one is.
        ///
        /// The same loop tier 2 runs, over the same two to four present candidates, asking a
        /// different question - not "who is nearest" but "is anybody here alone". Squares
        /// throughout, XZ only for the reason given in TryArbitrateInteractive, and it returns the
        /// moment a second player turns up, because that is a shared fight and the answer no
        /// longer depends on anything else.
        ///
        /// Only VIEWERS count as being near. A dedicated host's reference position is the world
        /// origin, which is not somewhere anybody is standing; counting it would make every
        /// creature near spawn look like a shared fight and switch the layer off exactly where a
        /// busy server's players gather. A listen host is a viewer and counts like anyone else.
        /// CanOwn is deliberately NOT tested here - a player barred from owning is still a player
        /// standing next to the creature, and still makes it a shared fight. The caller checks
        /// whether the one it got back may own.
        ///
        /// Also reports where the current owner is among the candidates and how far away, so the
        /// caller can decide a pull without a second walk. Both are only meaningful when the
        /// return value is not -1: an early return has not necessarily reached the owner yet. A
        /// caller with no owner to find - a rescue - uses NearbyViewers instead.
        /// </summary>
        private static int SoleNearbyViewer(SectorVerdict verdict, Vector3 pos, long currentOwner,
                                            out int ownerIndex, out float ownerSq) {
            ownerIndex = -1;
            ownerSq = float.MaxValue;
            int sole = -1;

            List<int> presentIndex = verdict.PresentIndex;
            for (int i = 0; i < presentIndex.Count; i++) {
                Candidate candidate = Candidates[presentIndex[i]];

                float dx = candidate.RefPos.x - pos.x;
                float dz = candidate.RefPos.z - pos.z;
                float sq = dx * dx + dz * dz;

                if (candidate.Uid == currentOwner) {
                    ownerIndex = presentIndex[i];
                    ownerSq = sq;
                }

                if (!candidate.IsViewer || sq > _proximityRadiusSq) { continue; }
                if (sole >= 0) { return -1; }                                 // a second player: a shared fight
                sole = presentIndex[i];
            }
            return sole;
        }

        /// <summary>Restore an owner to a ZDO that has none present. Uncapped by design - see
        /// ArbitrateZone.</summary>
        private static void Rescue(ZDO zdo, long newOwner, float now) {
            if (Monitoring.Active) { Monitoring.NoteCause(zdo, HandoffCause.Rescue); }
            zdo.SetOwner(newOwner);
            OwnerHistory[zdo.m_uid] = new OwnershipRecord { Owner = newOwner, ChangedAt = now, LastSeenAt = now };
            LastPassRescued++;
            TotalRescued++;
        }

        /// <summary>
        /// Rescue, and for a creature push the new owner out at once - to everyone who can see it
        /// and to the owner it had, who may still be writing it. Until each viewer's copy names
        /// the new owner their hits on it go to the old one or, from an unowned copy, to everybody
        /// and nobody; the creature hit router catches those, but only an up-to-date copy lets the
        /// new owner's AI and the other players' view start from the right place. Buildings are
        /// not pushed: a login rescues thousands of them at once and none of them is being hit.
        /// </summary>
        private static void RescueObject(ZDO zdo, long newOwner, bool creature, SectorVerdict verdict, long oldOwner, float now) {
            Rescue(zdo, newOwner, now);
            if (!creature) { return; }

            LastPassCreaturesRescued++;
            TotalCreaturesRescued++;
            ForceSendTo(verdict, zdo.m_uid, oldOwner);
        }

        /// <summary>
        /// Who a rescued ZDO goes to: the sector's best candidate, except that a ship with a
        /// player at its helm goes to that player, and a stationary object goes to whoever is
        /// nearest it.
        ///
        /// The tier split comes first, and it is free (ZDO.Type is a flag read). A ship is
        /// Prioritized, so the helm lookup below - the only dictionary probe on this uncapped path
        /// - is now skipped for every wall, tree and bush, which is most of a login burst. This
        /// method is cheaper than it was.
        ///
        /// A rescue is where the nearest rule is both safest and most valuable. Safest, because
        /// nobody present is writing this ZDO - there is no in-flight owner write to race, which is
        /// the same reason the helm branch is allowed to override the cost function here and
        /// nowhere else. Most valuable, because an unowned berry patch is the commonest shape of
        /// the problem: you walk into one nobody has touched, and both vanilla and the cost
        /// function hand it to whoever has the lowest ping rather than to you.
        ///
        /// Applied to every non-Prioritized rescue, not only to confirmed interactables. For a
        /// rescue the classification would not change the answer - nothing about the object is
        /// being written, and the only question is who will touch it next - so it is not worth a
        /// probe per rescued ZDO.
        ///
        /// Only this machine can decide the helm case for a ship nobody present owns - the handoff
        /// in ShipHelmOwnership runs on the owner, and there is none - and giving it to a passenger
        /// instead would leave the helmsman steering a ship simulated elsewhere until that
        /// passenger's machine hands it on, which it only does if it runs this mod. Every condition
        /// that fails falls back to the best candidate, so a stale s_user - the helmsman
        /// disconnected or walked away - never holds a rescue up.
        ///
        /// A creature takes the tier-1 route with the Prioritized objects, the caller having
        /// already looked it up: nearest-within-the-claim-radius is the rule for something nobody
        /// is about to fight, and the proximity layer's "the one player near it" is the rule for
        /// something they are.
        /// </summary>
        private static long RescueTarget(ZDO zdo, SectorVerdict verdict, bool creature) {
            if (zdo.Type != ZDO.ObjectType.Prioritized && !creature) {
                if (!_interactiveEnabled) { return verdict.BestUid; }

                int nearest = NearestEligible(verdict, zdo.GetPosition());
                if (nearest < 0) { return verdict.BestUid; }

                LastPassNearestRescued++;
                return Candidates[nearest].Uid;
            }

            // The helm rule first: a ship with somebody steering it goes to them whoever else is
            // standing on deck. Everything that is not such a ship - every creature - used to fall
            // back to the sector's best candidate from each of the four exits below, and now falls
            // back through the proximity layer instead, which is the sector's best candidate
            // unless exactly one player is near.
            if (!ShipHelmOwnership.Enabled) { return SoleNearbyOrBest(zdo, verdict); }

            long playerId = OwnershipPolicy.HelmsmanPlayerId(zdo);
            if (playerId == 0L) { return SoleNearbyOrBest(zdo, verdict); }

            long helmsman = PeerForPlayer(playerId);
            if (helmsman == 0L) { return SoleNearbyOrBest(zdo, verdict); }
            if (helmsman != verdict.BestUid && (!IsPresent(verdict, helmsman) || !CanOwn(helmsman))) {
                return SoleNearbyOrBest(zdo, verdict);
            }

            LastPassHelmRescued++;
            TotalHelmRescued++;
            return helmsman;
        }

        /// <summary>
        /// The proximity layer's half of a rescue: an object nobody present is simulating goes to
        /// the one player near it, and to the sector's best candidate when there is no such
        /// player - nobody near, or several.
        ///
        /// This is the safest place the layer acts and the one that matters most with two players.
        /// Safest for the reason the nearest rule is safe for stationary objects: nobody present
        /// is writing this ZDO, so there is no in-flight owner write to race. Most valuable
        /// because with exactly two players the cost function's two totals are always equal and
        /// the tie goes to the lower-latency one - so without this, every unowned creature in a
        /// zone they share goes to that player, including the ones the other is standing next to.
        ///
        /// The counters only move when the answer differs from the sector's best, so they read as
        /// "rescues this layer changed" rather than "rescues it looked at".
        /// </summary>
        private static long SoleNearbyOrBest(ZDO zdo, SectorVerdict verdict) {
            if (!_proximityEnabled) { return verdict.BestUid; }

            if (NearbyViewers(verdict, zdo.GetPosition(), out int sole) != 1 || !Candidates[sole].CanOwn) { return verdict.BestUid; }

            long uid = Candidates[sole].Uid;
            if (uid != verdict.BestUid) {
                LastPassProximityRescued++;
                TotalProximityRescued++;
            }
            return uid;
        }

        /// <summary>The candidate's own eligibility - false for a host barred by Allow Host As
        /// Owner, which must not be handed a ship just because its player is steering.</summary>
        private static bool CanOwn(long uid) {
            for (int i = 0; i < Candidates.Count; i++) {
                if (Candidates[i].Uid == uid) { return Candidates[i].CanOwn; }
            }
            return false;
        }

        private static long PeerForPlayer(long playerId) {
            if (!_peerByPlayerIdBuilt) {
                BuildPeerByPlayerId();
                _peerByPlayerIdBuilt = true;
            }
            return PeerByPlayerId.TryGetValue(playerId, out long uid) ? uid : 0L;
        }

        /// <summary>
        /// Map each connected player's player id to their session id. s_user holds the player
        /// id, which lives on the character ZDO; ownership is by session id. A listen host's own
        /// character is included - on a dedicated server m_characterID is None and adds nothing.
        /// </summary>
        private static void BuildPeerByPlayerId() {
            PeerByPlayerId.Clear();

            ZNet net = ZNet.instance;
            ZDOMan zdoMan = ZDOMan.instance;
            if (net == null || zdoMan == null) { return; }

            AddPlayer(zdoMan, net.m_characterID, zdoMan.m_sessionID);

            List<ZNetPeer> peers = net.GetPeers();
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (peer == null || peer.m_uid == 0L) { continue; }
                AddPlayer(zdoMan, peer.m_characterID, peer.m_uid);
            }
        }

        private static void AddPlayer(ZDOMan zdoMan, ZDOID characterId, long sessionId) {
            if (characterId.IsNone()) { return; }
            ZDO character = zdoMan.GetZDO(characterId);
            if (character == null) { return; }
            long playerId = character.GetLong(ZDOVars.s_playerID, 0L);
            if (playerId != 0L) { PeerByPlayerId[playerId] = sessionId; }
        }

        /// <summary>Is this owner one of the candidates covering the sector? Present holds at most
        /// one entry per candidate, so a linear scan of longs is cheaper than hashing one.</summary>
        private static bool IsPresent(SectorVerdict verdict, long uid) {
            List<long> present = verdict.Present;
            for (int i = 0; i < present.Count; i++) {
                if (present[i] == uid) { return true; }
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

        private static Verdict Score(Candidate owner, Vector2s sector) {
            float total = 0f;
            float worst = 0f;

            for (int i = 0; i < Candidates.Count; i++) {
                Candidate viewer = Candidates[i];
                if (!viewer.IsViewer) { continue; }                          // nobody is watching through a dedicated host
                if (viewer.Uid == owner.Uid) { continue; }                   // the owner sees its own simulation instantly
                if (!ZoneCompat.InActiveArea(sector, viewer.Zone, ZoneCompat.ActiveZoneRadius)) { continue; }

                float staleness = (owner.RttMs + viewer.RttMs) * 0.5f;
                total += staleness;
                if (staleness > worst) { worst = staleness; }
            }

            return new Verdict { TotalCostMs = total, WorstCostMs = worst, OwnerRttMs = owner.RttMs };
        }

        /// <summary>
        /// Apply the best moves first, each tier to its own budget. Sorting by priority means a
        /// capped pass does the reassignments that matter most rather than whichever happened to
        /// be enumerated first, and anything skipped is simply reconsidered next pass.
        ///
        /// Two lists rather than one with a tier key, for two reasons. The scores are in different
        /// units - milliseconds of staleness removed for tier 1, metres of approach for tier 2 - so
        /// there is no meaningful order across the boundary to sort by, only a tier-major
        /// comparator whose entire job would be to keep them apart, which two lists do
        /// structurally. And the budgets are separate on purpose: a zone full of contested
        /// creatures must not be able to starve the handful of moves that make a berry patch local,
        /// nor the reverse.
        ///
        /// Tier 1 drains first, because its objects look BROKEN when misplaced - a creature relayed
        /// through an extra hop stutters - where a tier-2 object merely answers slowly. Within tier
        /// 1, creatures go first (ByPriority), for the same reason one step further.
        ///
        /// Only latency- or proximity-driven moves off a healthy, present owner reach here.
        /// Restoring an owner to a ZDO that has none is correctness, not placement, and is applied
        /// unbudgeted in RunPass - so every priority below is finite and both sorts are meaningful.
        /// </summary>
        private static void ApplyUpgrades(float now) {
            LastPassOptimised = 0;
            LastPassDeferred = 0;
            LastPassCreaturesOptimised = 0;
            LastPassInteractiveOptimised = 0;
            LastPassInteractiveDeferred = 0;
            LastPassInteractiveCap = 0;
            LastPassProximityPulled = 0;

            // The configured cap is a floor, not a ceiling: it is tuned for a small group, and a
            // fixed number of transfers per pass means convergence time grows linearly with the
            // number of players who just moved. Scaling it to the candidate count keeps the
            // per-player budget constant - a 200-player server moves at most 200 objects per
            // pass, which is still one resend per player every two seconds.
            int cap = Mathf.Max(Mathf.Max(1, ValConfig.OwnershipMaxReassignsPerPass.Value), Candidates.Count);
            LastPassCap = cap;

            // No single peer may soak up tier 1's whole budget in one pass: each transfer is a
            // resend plus new simulation load for the receiver, and a pass that hands one player
            // everything is the concentration problem in miniature.
            Drain(PendingSimulated, cap, Mathf.Max(1, (cap + 1) / 2), now, forceSend: false,
                  ref LastPassOptimised, ref LastPassDeferred, ref TotalOptimised);

            if (!_interactiveEnabled) { return; }

            int interactiveCap = Mathf.Max(
                Mathf.Max(1, ValConfig.OwnershipInteractiveMaxReassignsPerPass.Value), Candidates.Count);
            LastPassInteractiveCap = interactiveCap;

            // Tier 2 gets NO per-target cap, and that is not an oversight. The per-target cap above
            // exists to stop one peer soaking up simulation load; a tier-2 object costs its owner
            // nothing at all until somebody touches it, and this tier's whole rule is "the person
            // standing there owns it", which by construction concentrates on one peer. That is the
            // intended outcome, not a failure to spread. The total cap already bounds the resend
            // burst, which is the only real cost here.
            Drain(PendingInteractive, interactiveCap, interactiveCap, now, forceSend: true,
                  ref LastPassInteractiveOptimised, ref LastPassInteractiveDeferred, ref TotalInteractiveOptimised);
        }

        /// <summary>
        /// Apply one tier's queued moves, best first, to that tier's own budget and its own
        /// per-target allowance. Keeping the per-target counter per drain rather than shared across
        /// both is what leaves tier 1's behaviour bit-identical to what it was before tier 2
        /// existed: a shared allowance would be computed from a larger combined cap, and tier 1,
        /// which drains first, would simply spend it.
        /// </summary>
        private static void Drain(List<PendingMove> pending, int cap, int perTargetCap, float now,
                                  bool forceSend, ref int applied, ref int deferred, ref long total) {
            if (pending.Count == 0) { return; }

            pending.Sort(ByPriority);
            MovesPerTarget.Clear();

            for (int i = 0; i < pending.Count; i++) {
                if (applied >= cap) { deferred += pending.Count - i; break; }

                PendingMove move = pending[i];
                MovesPerTarget.TryGetValue(move.NewOwner, out int taken);
                if (taken >= perTargetCap) { deferred++; continue; }
                MovesPerTarget[move.NewOwner] = taken + 1;

                // Tier 1 only: NoteCause ignores anything not Prioritized, and the candidate
                // listing is the one part of a record worth skipping when nobody will read it.
                if (Monitoring.Active && !forceSend) {
                    Monitoring.NoteCause(move.Zdo, move.Proximity ? HandoffCause.Proximity : HandoffCause.Optimise,
                                         move.Priority, DescribeCandidates(move));
                }
                move.Zdo.SetOwner(move.NewOwner);
                OwnerHistory[move.Zdo.m_uid] = new OwnershipRecord { Owner = move.NewOwner, ChangedAt = now, LastSeenAt = now };
                applied++;
                total++;

                // A proximity move is pushed out at once for the reasons tier 2's are, and they
                // bite harder here. The player it is going to is standing next to the creature
                // and about to hit it, and until their copy of the owner id catches up that hit is
                // addressed to the old owner, who drops it without a word. And the old owner is
                // writing this ZDO every frame, so it is the peer that most needs to hear soonest.
                // Every creature move is pushed for the same reasons, whichever rule made it: the
                // monitoring data showed no creature had ever reached the cost function, so there
                // was no old behaviour of its own for a creature to keep. A cart or an idle ship
                // moved by the cost function still waits for the ordinary send.
                if (move.Proximity) {
                    LastPassProximityPulled++;
                    TotalProximityPulled++;
                }
                if (move.Creature) {
                    LastPassCreaturesOptimised++;
                    TotalCreaturesOptimised++;
                }
                if (forceSend || move.Proximity || move.Creature) { ForceSendTo(move.Sector, move.Zdo.m_uid, move.OldOwner); }
            }
        }

        /// <summary>
        /// Tell everyone who can see this object who owns it now, at the front of their next send.
        ///
        /// Neither of the two obvious choices is right.
        ///
        /// ZDOMan.ForceSendZDO(ZDOID) broadcasts - it adds the id to EVERY peer's m_forceSend, and
        /// AddForceSendZdos inserts anything ShouldSend accepts at index 0 of that peer's sync list
        /// regardless of where the peer is standing. A peer that has never been sent this ZDO has
        /// no m_zdos entry, so ShouldSend returns true and it gets pushed a berry bush on the other
        /// side of the world, once per move, every pass. That is a real bill on a full server and
        /// it buys nothing.
        ///
        /// ForceSendZDO(newOwner, id) - what RpcOwnerRouter does - is too narrow here, and reason
        /// (b) in ArbitrateZone says why: the stale owner id is a property of every OTHER peer's
        /// copy, not just the new owner's. Telling only the new owner leaves the next player to
        /// press E still addressing the old one. RpcOwnerRouter can target because it has one
        /// specific message for one specific machine and is about to forward it; this has no
        /// message, only a fact that several machines need.
        ///
        /// So: addressed to the sector's Present set. That is exactly the candidates whose active
        /// area covers the sector, which is exactly the set that can hold an instance and therefore
        /// the set that can address an RPC at the owner. It includes the OLD owner when that owner
        /// is present, which is what reason (c) needs - the old owner is the peer that must stop
        /// writing soonest, and the one whose in-flight write would otherwise drag ownership back.
        /// A creature's old owner may not be present - it can still be simulating the creature from
        /// the outer ring of what it loads (OwnerStillLoads) - so it is passed in as alsoTo and told
        /// too. 0 when there was no old owner, or it is in the Present set anyway.
        /// </summary>
        private static void ForceSendTo(SectorVerdict sector, ZDOID id, long alsoTo) {
            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null) { return; }

            long self = zdoMan.m_sessionID;
            List<long> present = sector.Present;
            bool alsoToSent = alsoTo == 0L || alsoTo == self;
            for (int i = 0; i < present.Count; i++) {
                if (present[i] == alsoTo) { alsoToSent = true; }
                if (present[i] == self) { continue; }   // the host's own copy is authoritative already
                zdoMan.ForceSendZDO(present[i], id);
            }
            if (!alsoToSent && CandidateIndex.ContainsKey(alsoTo)) { zdoMan.ForceSendZDO(alsoTo, id); }
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

        // -- network monitoring --------------------------------------------------------------
        // Read-only views for Runtime/Monitoring. None of this runs unless monitoring is on.

        /// <summary>Simulated objects this peer owned as of the last pass, from the load tally.
        /// Zero when the load penalty is off, because then nothing tallies.</summary>
        internal static int OwnedCountFor(long uid) {
            return OwnedCount.TryGetValue(uid, out int owned) ? owned : 0;
        }

        private static void CountCreatures(List<ZDO> objects, bool soleCandidate) {
            int count = 0;
            for (int i = 0; i < objects.Count; i++) {
                ZDO zdo = objects[i];
                if (zdo != null && zdo.Persistent && Monitoring.IsTracked(zdo)) { count++; }
            }
            if (soleCandidate) { _monitorSoleCreatures += count; } else { _monitorContestedCreatures += count; }
        }

        /// <summary>
        /// Everyone the cost function weighed for this move, as a JSON array: what each would
        /// have cost in total, its RTT, how many simulated objects it already owned, and how far
        /// it stood from the object. The last of those is the one the cost function does NOT
        /// use - it is recorded so that a rule which did can be tried against the same moves.
        /// </summary>
        private static string DescribeCandidates(PendingMove move) {
            if (!Monitoring.IsTracked(move.Zdo)) { return null; }

            SectorVerdict verdict = move.Sector;
            Vector3 pos = move.Zdo.GetPosition();
            System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;

            System.Text.StringBuilder sb = new System.Text.StringBuilder(64 * verdict.PresentIndex.Count);
            sb.Append('[');
            for (int i = 0; i < verdict.PresentIndex.Count; i++) {
                Candidate candidate = Candidates[verdict.PresentIndex[i]];
                float dx = candidate.RefPos.x - pos.x;
                float dz = candidate.RefPos.z - pos.z;
                verdict.TotalByOwner.TryGetValue(candidate.Uid, out float total);

                if (i > 0) { sb.Append(','); }
                sb.Append("{\"uid\":\"").Append(candidate.Uid.ToString(invariant)).Append('"')
                  .Append(",\"rtt\":").Append(candidate.RttMs.ToString("0.#", invariant))
                  .Append(",\"totalMs\":").Append(total.ToString("0.#", invariant))
                  .Append(",\"owned\":").Append(OwnedCountFor(candidate.Uid).ToString(invariant))
                  .Append(",\"distM\":").Append(Mathf.Sqrt(dx * dx + dz * dz).ToString("0", invariant))
                  .Append(",\"canOwn\":").Append(candidate.CanOwn ? "true" : "false")
                  .Append(",\"viewer\":").Append(candidate.IsViewer ? "true" : "false")
                  .Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        internal static void Reset() {
            Candidates.Clear();
            CandidateIndex.Clear();
            ZonesToScan.Clear();
            SharedBucketObjects.Clear();
            SharedBucketPortals.Clear();
            SharedBucketPool.Clear();
            _sharedBucketListsInUse = 0;
            _scanSharedBucket = false;
            OwnerHistory.Clear();
            PendingSimulated.Clear();
            PendingInteractive.Clear();
            SectorCache.Clear();
            VerdictPool.Clear();
            _verdictsInUse = 0;
            OwnedCount.Clear();
            MovesPerTarget.Clear();
            PeerByPlayerId.Clear();
            _peerByPlayerIdBuilt = false;
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
            LastPassHelmRescued = 0;
            LastPassReleased = 0;
            LastPassOptimised = 0;
            LastPassDeferred = 0;
            LastPassStaticHeld = 0;
            LastPassCap = 0;
            TotalRescued = 0;
            TotalHelmRescued = 0;
            TotalOptimised = 0;
            LastPassMs = 0f;

            LastPassInteractiveOptimised = 0;
            LastPassInteractiveDeferred = 0;
            LastPassInteractiveHeld = 0;
            LastPassInteractiveCap = 0;
            LastPassNearestRescued = 0;
            TotalInteractiveOptimised = 0;
            _interactiveEnabled = false;
            _interactiveClaimRadius = 0f;
            _interactiveClaimRadiusSq = 0f;
            _interactiveMargin = 0f;
            _interactiveHold = 0f;

            LastPassProximityKept = 0;
            LastPassProximityPulled = 0;
            LastPassProximityRescued = 0;
            TotalProximityPulled = 0;
            TotalProximityRescued = 0;
            _proximityEnabled = false;
            _proximityRadiusSq = 0f;
            _proximityPullSq = 0f;

            LastPassCreaturesRescued = 0;
            LastPassCreaturesOptimised = 0;
            LastPassCreaturesKept = 0;
            TotalCreaturesRescued = 0;
            TotalCreaturesOptimised = 0;
            _creaturesEnabled = false;

            _monitorSoleCreatures = 0;
            _monitorContestedCreatures = 0;
        }
    }
}
