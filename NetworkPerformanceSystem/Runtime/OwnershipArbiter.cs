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
            // Creatures moved off a struggling player (M35) wait behind every other creature move:
            // those are fights being placed, these are load being spread. Within the sheds the
            // cheapest in staleness go first.
            if (a.Shed != b.Shed) { return a.Shed ? 1 : -1; }
            return b.Priority.CompareTo(a.Priority);
        };

        private static readonly Dictionary<Vector2s, SectorVerdict> SectorCache = new Dictionary<Vector2s, SectorVerdict>();
        private static readonly List<SectorVerdict> VerdictPool = new List<SectorVerdict>();
        private static int _verdictsInUse;
        private static readonly Dictionary<long, int> OwnedCount = new Dictionary<long, int>();
        private static readonly Dictionary<long, int> MovesPerTarget = new Dictionary<long, int>();

        // M35, the creature allowance. OwnedCreatures is the tally of creatures alone, per owner,
        // as of the start of the pass. The three lists run parallel to Candidates and are rebuilt
        // with it: how many more creatures each candidate may take this pass (int.MaxValue for
        // anyone without an allowance - everyone, while M35 is off), whether it may take creatures
        // moved off somebody else, and how many creature moves away from it are already queued.
        private static readonly Dictionary<long, int> OwnedCreatures = new Dictionary<long, int>();

        /// <summary>M35: of each owner's creatures, how many sit where somebody else could run
        /// them - a candidate that may own and has no allowance of its own has the zone in its
        /// active area. PeerCapacity sets no allowance on a player for whom this is 0. Helpers is
        /// those candidates, by index, collected once at the start of the tally.</summary>
        private static readonly Dictionary<long, int> SharedCreatures = new Dictionary<long, int>();
        private static readonly List<int> Helpers = new List<int>();

        private static readonly List<int> CreatureRoom = new List<int>();
        private static readonly List<bool> ShedReceiver = new List<bool>();
        private static readonly List<int> PendingOut = new List<int>();
        private static readonly List<int> PendingIn = new List<int>();
        private static readonly List<int> ShedCollected = new List<int>();
        private static readonly List<ShedEntry> ShedEntries = new List<ShedEntry>();
        private static readonly List<ReceiverOption> ReceiverOptions = new List<ReceiverOption>();

        /// <summary>The most creatures collected per over-allowance owner in one pass. Four of
        /// them move at most; the rest only give the order something to choose from.</summary>
        private const int MaxShedCollectedPerOwner = 64;

        /// <summary>A creature that could be moved off a player over their allowance, collected
        /// during the zone walk and chosen from once it is over (SelectSheds).</summary>
        private struct ShedEntry {
            internal ZDO Zdo;
            internal SectorVerdict Sector;
            internal int OwnerIndex;
            internal long Owner;
            internal bool Alert;
            internal float DistSq;               // from its owner, in 3D
        }

        private static readonly System.Comparison<ShedEntry> ShedOrder = (a, b) => {
            if (a.OwnerIndex != b.OwnerIndex) { return a.OwnerIndex.CompareTo(b.OwnerIndex); }
            return CreatureLoadRules.CompareShed(a.Alert, a.DistSq, a.Zdo.m_uid.ID, b.Alert, b.DistSq, b.Zdo.m_uid.ID);
        };

        /// <summary>Player id -> session id, for giving an abandoned ship to the player at its
        /// helm. Built at most once per pass and only when a rescued ship names a helmsman, so a
        /// pass with no such ship pays nothing for it.</summary>
        private static readonly Dictionary<long, long> PeerByPlayerId = new Dictionary<long, long>();
        private static bool _peerByPlayerIdBuilt;

        /// <summary>Player name -> session id, for finding the player a tame is following: s_follow
        /// holds the name Tameable.RPC_Command wrote, which is ZNetPeer.m_playerName. Built at most
        /// once per pass, and only when a following tame is actually asked about. A name two
        /// connected players share maps to 0 - following either would be a guess.</summary>
        private static readonly Dictionary<string, long> PeerByName = new Dictionary<string, long>(System.StringComparer.Ordinal);
        private static bool _peerByNameBuilt;

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

            /// <summary>The full score of each entry of Present, in the same order. TotalByOwner
            /// alone cannot rank them: with two viewers every total is equal and the tie-break on
            /// worst case and RTT decides, so picking a creature's owner among the candidates
            /// with room (M35) needs all three numbers.</summary>
            internal readonly List<Verdict> PresentScore = new List<Verdict>();

            internal void Clear() {
                HasEligible = false;
                BestUid = 0L;
                BestTotalMs = float.MaxValue;
                TotalByOwner.Clear();
                Present.Clear();
                PresentIndex.Clear();
                PresentScore.Clear();
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

            /// <summary>A follower going back to the player it follows (LeaderFor). Queued ahead of
            /// every other creature move - see QueueLeaderMove - and reported apart.</summary>
            internal bool Leader;

            /// <summary>Who owned it when the move was queued. A move is pushed to the sector's
            /// Present set, and a creature's owner may be outside it - still simulating the
            /// creature from its loaded ring - while being the one machine that most needs to
            /// hear, because it is the one still writing.</summary>
            internal long OldOwner;

            /// <summary>A creature moved off a player over their creature allowance (M35), to a
            /// player with room. Queued behind every other creature move and reported apart.</summary>
            internal bool Shed;
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
        // was not asked. Pulled counts moves actually applied, not queued. Held counts objects
        // nobody was near that the cost function would have moved, left with an owner still
        // within the keep distance (OwnershipPolicy.ProximityKeepFactor).
        internal static int LastPassProximityKept;
        internal static int LastPassProximityPulled;
        internal static int LastPassProximityRescued;
        internal static int LastPassProximityHeld;
        internal static long TotalProximityPulled;
        internal static long TotalProximityRescued;
        internal static long TotalProximityHeld;

        // Creatures. Rescued and optimised are subsets of the pass-wide counters above, split out
        // because creatures are the reason tier 1 exists and they were invisible to it before
        // 1.8.0. Kept counts creatures whose owner is outside every presence square covering them
        // but still has them loaded - left where they are rather than released or rescued away.
        internal static int LastPassCreaturesRescued;
        internal static int LastPassCreaturesOptimised;
        internal static int LastPassCreaturesKept;
        internal static long TotalCreaturesRescued;
        internal static long TotalCreaturesOptimised;

        /// <summary>Owner changes not pushed to a player because the host had never sent them the
        /// object: it reaches them in the game's own order instead, behind the floors and walls
        /// around it. Counted per player, not per object. See ForceSendIfHeld.</summary>
        internal static int LastPassFirstSendsInOrder;
        internal static long TotalFirstSendsInOrder;

        /// <summary>Everything else kept the same way: owned objects in a zone nobody is present
        /// in, left with an owner who still has them loaded rather than released (Keep Objects
        /// While Loaded). Each one is a release this pass did not make, and usually the rescue it
        /// did not have to make when that player stepped back.</summary>
        internal static int LastPassLoadedKept;

        // Followers: tames following a player, and summons, kept with or returned to that player
        // (LeaderFor). Kept counts those left with their player this pass - including ones held
        // briefly by Min Hold before a return; Returned counts moves applied back to them;
        // Rescued counts rescues and releases sent to them instead of wherever else they would
        // have gone.
        internal static int LastPassLeaderKept;
        internal static int LastPassLeaderReturned;
        internal static int LastPassLeaderRescued;
        internal static long TotalLeaderReturned;
        internal static long TotalLeaderRescued;

        // M35. Steered counts creatures sent to someone other than the sector's best because the
        // best was at their creature allowance. Shed counts creatures moved off a player over their
        // allowance (applied, not queued); ShedBlocked counts ones that found nobody with room.
        internal static int LastPassSteered;
        internal static int LastPassShed;
        internal static int LastPassShedBlocked;
        internal static long TotalSteered;
        internal static long TotalShed;
        internal static long TotalShedBlocked;

        /// <summary>M35 applies this pass: creatures are arbitrated and the allowance is on.</summary>
        private static bool _allowanceActive;

        /// <summary>Challenge Margin Ms, read once per pass for the shed receiver pick.</summary>
        private static float _challengeMarginMs;

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

        // The proximity layer's settings, read once per pass for the same reason. The distances
        // are kept squared: the scan compares squares and never needs a root.
        private static bool _proximityEnabled;
        private static float _proximityRadiusSq;
        private static float _proximityPullSq;
        private static float _proximityKeepSq;

        /// <summary>Arbitrate Creatures, read once per pass. Off leaves every creature on the
        /// static path exactly as before 1.8.0.</summary>
        private static bool _creaturesEnabled;

        /// <summary>Followers Stay With Their Player, read once per pass, and only meaningful
        /// when creatures are arbitrated at all.</summary>
        private static bool _followersEnabled;

        /// <summary>Keep Objects While Loaded, read once per pass. Off releases every non-creature
        /// in a zone nobody is present in, as vanilla does.</summary>
        private static bool _keepWhileLoaded;

        /// <summary>M33 is holding back owner changes on buildings, trees and rocks, read once per
        /// pass. While it is, a tier-2 move of one of those is pushed only to its new and old
        /// owner - see Drain.</summary>
        private static bool _structureOwnersWait;

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
            float proximityKeep = OwnershipPolicy.KeepDistanceMetres(proximityRadius);
            _proximityKeepSq = proximityKeep * proximityKeep;
            _creaturesEnabled = ValConfig.OwnershipArbitrateCreatures.Value;
            _followersEnabled = _creaturesEnabled && ValConfig.OwnershipFollowersStayWithLeader.Value;
            _keepWhileLoaded = ValConfig.OwnershipKeepWhileLoaded.Value;
            _structureOwnersWait = StructureUpdates.Active && StructureUpdates.OwnerChangesMayWait;
            _allowanceActive = _creaturesEnabled && PeerCapacity.Balancing;
            _challengeMarginMs = margin;

            LastPassGhostsExcluded = 0;
            BuildCandidates(zdoMan);
            TotalGhostsExcluded += LastPassGhostsExcluded;
            LastPassCandidates = Candidates.Count;
            if (Candidates.Count == 0) { LastPassMs = 0f; return; }

            CollectZonesToScan();
            GroupSharedBucket(zdoMan);
            TallyOwnedLoad(zdoMan, loadPenalty > 0f, _allowanceActive);
            BuildCreatureRoom();

            SectorCache.Clear();
            _verdictsInUse = 0;
            PendingSimulated.Clear();
            PendingInteractive.Clear();
            _peerByPlayerIdBuilt = false;
            _peerByNameBuilt = false;

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
            LastPassProximityHeld = 0;
            LastPassCreaturesRescued = 0;
            LastPassCreaturesKept = 0;
            LastPassFirstSendsInOrder = 0;
            LastPassLoadedKept = 0;
            LastPassLeaderKept = 0;
            LastPassLeaderRescued = 0;
            LastPassSteered = 0;
            LastPassShedBlocked = 0;
            ShedEntries.Clear();
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

            if (_allowanceActive && ShedEntries.Count > 0) { SelectShedsGuarded(); }
            ApplyUpgrades(now);
            PruneHoldTable(now);

            // M35 reads this pass's creature tally; it decides the allowances the next pass uses.
            PeerCapacity.OnPassCompleted(now);

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
                Logger.LogDebug($"Ownership pass: considered {LastPassConsidered} ({LastPassUnownedOnEntry} unowned) over {ZonesToScan.Count} zones, rescued {LastPassRescued} ({LastPassHelmRescued} to a helmsman, {LastPassNearestRescued} to the nearest, {LastPassCreaturesRescued} creatures), released {LastPassReleased}, optimised {LastPassOptimised} ({LastPassCreaturesOptimised} creatures), deferred {LastPassDeferred}, creatures kept by a loading owner {LastPassCreaturesKept}, allowance {LastPassSteered} steered / {LastPassShed} shed / {LastPassShedBlocked} blocked, proximity {LastPassProximityPulled} pulled / {LastPassProximityKept} kept / {LastPassProximityRescued} rescued / {LastPassProximityHeld} held, interactive {LastPassInteractiveOptimised} ({LastPassInteractiveDeferred} deferred, {LastPassInteractiveHeld} held), static held {LastPassStaticHeld}, ghosts excluded {LastPassGhostsExcluded}, history {OwnerHistory.Count}, {LastPassMs:F1}ms.");
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
                // Nobody covers this zone at all, so no owner can be present, and every owned ZDO
                // here is released unless its owner still has it loaded. At the stock near
                // simulation distance of 2 the scan square is 5x5 while the presence square is
                // 3x3, so sixteen of every twenty-five zones a lone candidate pulls in land here -
                // which is why ReleaseZone reads each owner id at most once per run of objects
                // that share it.
                ReleaseZone(objects, zone, now);
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
                verdict.PresentScore.Add(score);          // parallel to Present; see PresentScore

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
        /// <param name="tallyLoad">Count simulated objects for the load handicap.</param>
        /// <param name="tallyCreatures">Count creatures alone for the creature allowance (M35),
        /// and which of them somebody else could run. The same walk answers both, so a pass that
        /// needs either pays for one.</param>
        private static void TallyOwnedLoad(ZDOMan zdoMan, bool tallyLoad, bool tallyCreatures) {
            OwnedCount.Clear();
            OwnedCreatures.Clear();
            SharedCreatures.Clear();
            Helpers.Clear();
            if (!tallyLoad && !tallyCreatures) { return; }
            if (tallyCreatures) { CollectHelpers(); }

            foreach (Vector2s zone in ZonesToScan) {
                if (_scanSharedBucket && ZoneCompat.InSharedBucket(zone)) { continue; }   // once, below
                TallyZone(ZoneObjects(zdoMan, zone), zone, tallyLoad, tallyCreatures);
                TallyZone(ZonePortals(zdoMan, zone), zone, tallyLoad, tallyCreatures);
            }
            foreach (KeyValuePair<Vector2s, List<ZDO>> group in SharedBucketObjects) { TallyZone(group.Value, group.Key, tallyLoad, tallyCreatures); }
            foreach (KeyValuePair<Vector2s, List<ZDO>> group in SharedBucketPortals) { TallyZone(group.Value, group.Key, tallyLoad, tallyCreatures); }
        }

        /// <summary>The candidates a creature could be given to instead of a struggling owner:
        /// allowed to own, and not held to an allowance of their own. The host counts when it may
        /// own - the arbiter places creatures on it like on anyone else.</summary>
        private static void CollectHelpers() {
            for (int i = 0; i < Candidates.Count; i++) {
                if (Candidates[i].CanOwn && PeerCapacity.AllowanceFor(Candidates[i].Uid) == int.MaxValue) { Helpers.Add(i); }
            }
        }

        /// <summary>How many helpers have this zone in their active area - 0, 1, or 2 meaning
        /// "two or more" - and which one when it is exactly one. The same presence test
        /// BuildVerdict uses.</summary>
        private static int HelpersPresent(Vector2s zone, out long sole) {
            sole = 0L;
            int count = 0;
            for (int i = 0; i < Helpers.Count; i++) {
                Candidate helper = Candidates[Helpers[i]];
                if (!ZoneCompat.InActiveArea(zone, helper.Zone, ZoneCompat.ActiveZoneRadius)) { continue; }
                if (++count >= 2) { sole = 0L; return 2; }
                sole = helper.Uid;
            }
            return count;
        }

        private static void TallyZone(List<ZDO> objects, Vector2s zone, bool tallyLoad, bool tallyCreatures) {
            if (objects == null) { return; }

            int helpers = -1;                                                 // asked at the first creature
            long soleHelper = 0L;

            for (int i = 0; i < objects.Count; i++) {
                ZDO zdo = objects[i];
                if (zdo == null || !zdo.Persistent) { continue; }
                if (!zdo.HasOwner()) { continue; }

                bool creature = false;
                if (zdo.Type != ZDO.ObjectType.Prioritized) {
                    if (!_creaturesEnabled || !OwnershipPolicy.IsCreature(zdo)) { continue; }
                    creature = true;
                } else if (!tallyLoad) {
                    continue;                                                 // a ship or a player: load only
                }

                long owner = zdo.GetOwner();
                if (tallyLoad) {
                    OwnedCount.TryGetValue(owner, out int count);
                    OwnedCount[owner] = count + 1;
                }
                if (creature && tallyCreatures) {
                    OwnedCreatures.TryGetValue(owner, out int creatures);
                    OwnedCreatures[owner] = creatures + 1;

                    if (helpers < 0) { helpers = HelpersPresent(zone, out soleHelper); }
                    if (helpers >= 2 || (helpers == 1 && soleHelper != owner)) {
                        SharedCreatures.TryGetValue(owner, out int shared);
                        SharedCreatures[owner] = shared + 1;
                    }
                }
            }
        }

        /// <summary>
        /// M35: how many more creatures each candidate may take this pass - its allowance less
        /// what it owns - and whether it may take creatures moved off somebody else. Everyone
        /// without an allowance gets int.MaxValue, which is everyone while M35 is off, so every
        /// test against these lists then reads exactly as the arbiter did before it existed.
        /// </summary>
        private static void BuildCreatureRoom() {
            CreatureRoom.Clear();
            ShedReceiver.Clear();
            PendingOut.Clear();
            PendingIn.Clear();
            ShedCollected.Clear();

            for (int i = 0; i < Candidates.Count; i++) {
                int room = int.MaxValue;
                bool receiver = false;
                if (_allowanceActive) {
                    long uid = Candidates[i].Uid;
                    int allowance = PeerCapacity.AllowanceFor(uid);
                    if (allowance != int.MaxValue) {
                        OwnedCreatures.TryGetValue(uid, out int owned);
                        room = allowance - owned;
                    }
                    receiver = Candidates[i].CanOwn && PeerCapacity.IsShedReceiver(uid);
                }
                CreatureRoom.Add(room);
                ShedReceiver.Add(receiver);
                PendingOut.Add(0);
                PendingIn.Add(0);
                ShedCollected.Add(0);
            }
        }

        /// <summary>How many more creatures this candidate may take this pass.</summary>
        private static int RoomOf(long uid) {
            if (!_allowanceActive || !CandidateIndex.TryGetValue(uid, out int index)) { return int.MaxValue; }
            return CreatureRoom[index];
        }

        /// <summary>The owner holds more creatures than their allowance.</summary>
        private static bool OverAllowance(long uid) {
            return _allowanceActive && RoomOf(uid) < 0;
        }

        /// <summary>A creature move to newOwner has been queued or made: one less it may take,
        /// and one more on its way off oldOwner. The old owner's room is not given back here - the
        /// move may yet be deferred, and until it is applied that player still runs it.</summary>
        private static void ReserveCreature(long newOwner, long oldOwner) {
            if (!_allowanceActive) { return; }
            if (CandidateIndex.TryGetValue(newOwner, out int to)) {
                PendingIn[to]++;
                if (CreatureRoom[to] != int.MaxValue) { CreatureRoom[to]--; }
            }
            if (oldOwner != 0L && CandidateIndex.TryGetValue(oldOwner, out int from)) {
                PendingOut[from]++;
            }
        }

        /// <summary>
        /// The candidate a creature should go to when the sector's best is at their allowance:
        /// the best of the present candidates that may own and have room, by the cost function's
        /// own ranking. 0 when there is none. The sector's best itself when it has room - which is
        /// always, while M35 is off.
        /// </summary>
        private static long RoomyTarget(SectorVerdict verdict, out float total) {
            total = verdict.BestTotalMs;
            if (RoomOf(verdict.BestUid) > 0) { return verdict.BestUid; }

            FillReceiverOptions(verdict, shedReceiversOnly: false);
            int pick = CreatureLoadRules.PickWithRoom(ReceiverOptions, -1);
            if (pick < 0) { return 0L; }
            total = ReceiverOptions[pick].Total;
            return Candidates[ReceiverOptions[pick].Index].Uid;
        }

        /// <summary>
        /// Where a creature goes when the sector's best may be at their allowance: the best
        /// itself when it has room; otherwise the best of the present candidates that may own
        /// and have room (RoomyTarget), counted as steered; and the best ANYWAY when nobody else
        /// has room. An allowance only says "give these to somebody else", so where there is
        /// nobody else it does not hold - a struggling player who is the only one who can run a
        /// creature gets it, exactly as with M35 off. Never 0 while the sector has an eligible
        /// candidate.
        /// </summary>
        private static long PlaceCreature(SectorVerdict verdict, out float total, out bool steered) {
            total = verdict.BestTotalMs;
            steered = false;
            if (RoomOf(verdict.BestUid) > 0) { return verdict.BestUid; }

            long roomy = RoomyTarget(verdict, out float roomyTotal);
            if (roomy == 0L) { return verdict.BestUid; }
            total = roomyTotal;
            steered = roomy != verdict.BestUid;
            return roomy;
        }

        /// <summary>A rescued creature's owner when the sector's best is at their allowance: the
        /// best candidate with room, or the best anyway when nobody has room - a rescue has to
        /// land, because an unowned creature is frozen and cannot be hurt.</summary>
        private static long RoomyRescueTarget(SectorVerdict verdict) {
            long target = PlaceCreature(verdict, out _, out bool steered);
            if (steered) {
                LastPassSteered++;
                TotalSteered++;
            }
            return target;
        }

        private static void FillReceiverOptions(SectorVerdict verdict, bool shedReceiversOnly) {
            ReceiverOptions.Clear();
            List<int> presentIndex = verdict.PresentIndex;
            for (int i = 0; i < presentIndex.Count; i++) {
                int index = presentIndex[i];
                Verdict score = verdict.PresentScore[i];
                OwnedCreatures.TryGetValue(Candidates[index].Uid, out int owned);
                ReceiverOptions.Add(new ReceiverOption {
                    Index = index,
                    Total = score.TotalCostMs,
                    Worst = score.WorstCostMs,
                    Rtt = score.OwnerRttMs,
                    Room = CreatureRoom[index],
                    Load = owned + PendingIn[index],
                    Eligible = Candidates[index].CanOwn && (!shedReceiversOnly || ShedReceiver[index]),
                });
            }
        }

        /// <summary>
        /// Note a creature that could be moved off an owner over their allowance, if it may be:
        /// not directly controlled, out of its hold, and not one only its owner is near (the
        /// proximity rule stands - see CreatureLoadRules.ShedEligible). Which of them actually move,
        /// and to whom, is decided once the whole pass has been walked (SelectSheds).
        /// </summary>
        /// <param name="soleScan">The proximity scan's answer for this creature when it ran
        /// (an index, NobodyNear or SharedFight); int.MinValue to scan here.</param>
        private static void CollectShed(ZDO zdo, SectorVerdict verdict, long owner, float now, float minHold, int soleScan) {
            if (!CandidateIndex.TryGetValue(owner, out int ownerIndex)) { return; }
            if (ShedCollected[ownerIndex] >= MaxShedCollectedPerOwner) { return; }
            if (OwnershipPolicy.IsDirectlyControlled(zdo)) { return; }

            Vector3 pos = zdo.GetPosition();
            Candidate ownerCandidate = Candidates[ownerIndex];
            float dx = ownerCandidate.RefPos.x - pos.x;
            float dz = ownerCandidate.RefPos.z - pos.z;
            float ownerSq = dx * dx + dz * dz;

            int nearState;
            bool ownerIsSole;
            if (soleScan != int.MinValue) {
                nearState = soleScan == SharedFight ? CreatureLoadRules.NearShared
                          : soleScan == NobodyNear ? CreatureLoadRules.NearNobody
                          : CreatureLoadRules.NearOne;
                ownerIsSole = soleScan == ownerIndex;
            } else {
                int near = NearbyViewers(verdict, pos, out int sole);
                nearState = near >= 2 ? CreatureLoadRules.NearShared : near == 1 ? CreatureLoadRules.NearOne : CreatureLoadRules.NearNobody;
                ownerIsSole = near == 1 && sole == ownerIndex;
            }
            if (!CreatureLoadRules.ShedEligible(nearState, ownerIsSole, ownerSq, _proximityPullSq)) { return; }
            if (now - Touch(zdo.m_uid, owner, now) < minHold) { return; }

            float dy = ownerCandidate.RefPos.y - pos.y;
            ShedEntries.Add(new ShedEntry {
                Zdo = zdo,
                Sector = verdict,
                OwnerIndex = ownerIndex,
                Owner = owner,
                Alert = zdo.GetBool(ZDOVars.s_alert) || zdo.GetBool(ZDOVars.s_haveTargetHash),
                DistSq = ownerSq + dy * dy,
            });
            ShedCollected[ownerIndex]++;
        }

        /// <summary>SelectSheds behind a catch: this runs on the host's main thread inside the
        /// ownership pass, and a fault in it must cost the allowance, not the arbiter.</summary>
        private static void SelectShedsGuarded() {
            try {
                SelectSheds();
            } catch (System.Exception e) {
                PendingSimulated.RemoveAll(move => move.Shed);
                ShedEntries.Clear();
                PatchGuard.Disable(Mechanism.CreatureAllowance,
                    $"choosing creatures to move off a struggling player failed ({e.GetType().Name}: {e.Message}). " +
                    "Creatures are placed as before for the rest of this session.");
            }
        }

        /// <summary>
        /// Move creatures off every owner who is over their allowance: up to four a pass each, by
        /// CreatureLoadRules' order (not fighting first, then furthest from that owner), each to a
        /// healthy player with room picked from that creature's own sector (PickReceiver). A
        /// creature nobody can take stays where it is and is counted as blocked; the allowance
        /// waits rather than stepping further while that is so (CreatureLoadRules.Step).
        /// </summary>
        private static void SelectSheds() {
            ShedEntries.Sort(ShedOrder);

            int i = 0;
            while (i < ShedEntries.Count) {
                int ownerIndex = ShedEntries[i].OwnerIndex;
                int end = i;
                while (end < ShedEntries.Count && ShedEntries[end].OwnerIndex == ownerIndex) { end++; }

                int room = CreatureRoom[ownerIndex];
                int overage = room == int.MaxValue ? 0 : -room - PendingOut[ownerIndex];
                int quota = CreatureLoadRules.ShedsThisPass(overage, CreatureLoadRules.MaxShedsPerOwnerPerPass);

                int taken = 0;
                for (int k = i; k < end && taken < quota; k++) {
                    ShedEntry entry = ShedEntries[k];
                    FillReceiverOptions(entry.Sector, shedReceiversOnly: true);
                    int pick = CreatureLoadRules.PickReceiver(ReceiverOptions, ownerIndex, _challengeMarginMs);
                    if (pick < 0) {
                        LastPassShedBlocked++;
                        TotalShedBlocked++;
                        continue;
                    }

                    long receiver = Candidates[ReceiverOptions[pick].Index].Uid;
                    float ownerTotal = entry.Sector.TotalByOwner.TryGetValue(entry.Owner, out float known) ? known : ReceiverOptions[pick].Total;
                    PendingSimulated.Add(new PendingMove {
                        Zdo = entry.Zdo, NewOwner = receiver,
                        Priority = ownerTotal - ReceiverOptions[pick].Total,      // staleness won (usually lost) by the move
                        Sector = entry.Sector, Creature = true, Shed = true, OldOwner = entry.Owner,
                    });
                    ReserveCreature(receiver, entry.Owner);
                    taken++;
                }
                i = end;
            }
        }

        /// <summary>
        /// No candidate covers this zone, so no owner can be present in it. An owned ZDO whose
        /// owner has walked away entirely is released - vanilla's release-to-unowned behaviour, so
        /// an absent owner does not leave a phantom behind: HasOwner() gates real game logic
        /// (ZSyncTransform only extrapolates when a ZDO claims an owner), so an absent owner is
        /// worse than no owner.
        ///
        /// Uncapped, and safe precisely because rescue is uncapped too: the moment an eligible
        /// candidate covers this zone again every ZDO here is re-owned on that same pass, exactly
        /// as vanilla does it. The two must stay symmetrical - capping one side and not the other
        /// is what left creatures frozen.
        ///
        /// An owner who still has the zone loaded keeps what they own here. This zone is in that
        /// player's scan square, which is exactly the ring their game instantiates but does not
        /// count as present (see OwnerStillLoads), so they are not absent at all: their game is
        /// still running the object. Releasing it there is what vanilla does, and it is the churn
        /// the 2026-09-27 recording measured - a player parked on a zone edge released and
        /// re-claimed the same 1,092 objects every time they stepped across it, over 38,000
        /// ownership changes in fifteen minutes for four players, each one a resend to everybody
        /// holding the object. Kept, a step back and forth changes nothing. A creature has always
        /// been kept this way; Keep Objects While Loaded extends it to everything else.
        ///
        /// That test needs the owner id, which the release alone never did: HasOwner() is the Owned
        /// flag, the exact "owner != 0" test a release needs, and GetOwner is a dictionary lookup
        /// behind it. The objects in a zone are nearly always one player's, so the answer is
        /// remembered for the last owner seen and each run of objects sharing an owner costs one
        /// lookup per object and one OwnerStillLoads.
        /// </summary>
        private static void ReleaseZone(List<ZDO> objects, Vector2s zone, float now) {
            long memoOwner = 0L;                                              // never a real owner: HasOwner() is checked first
            bool memoLoads = false;

            for (int i = 0; i < objects.Count; i++) {
                ZDO zdo = objects[i];
                if (zdo == null || !zdo.Persistent) { continue; }
                LastPassConsidered++;

                if (!zdo.HasOwner()) { LastPassUnownedOnEntry++; continue; }

                if (_creaturesEnabled && OwnershipPolicy.IsCreature(zdo)) {
                    long owner = zdo.GetOwner();

                    // A follower trailing its player into a zone nobody else covers stays with
                    // that player rather than being released: an unowned creature runs no AI, so
                    // it would stop following exactly where it fell behind.
                    long leader = LeaderFor(zdo, zone);
                    if (leader != 0L) {
                        if (leader == owner) {
                            LastPassLeaderKept++;
                        } else {
                            GiveToLeader(zdo, leader, owner, now);
                        }
                        continue;
                    }

                    if (OwnerStillLoads(owner, zone)) {
                        LastPassCreaturesKept++;
                        continue;
                    }
                } else if (_keepWhileLoaded) {
                    long owner = zdo.GetOwner();
                    if (owner != memoOwner) {
                        memoOwner = owner;
                        memoLoads = OwnerStillLoads(owner, zone);
                    }
                    if (memoLoads) {
                        LastPassLoadedKept++;
                        continue;
                    }
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
                    bool unownedCreature = _creaturesEnabled && OwnershipPolicy.IsCreature(zdo);
                    RescueObject(zdo, unownedCreature ? LeaderOr(zdo, zone, bestUid) : bestUid, unownedCreature, verdict, 0L, now);
                    continue;
                }

                // Deliberately no follower test on these two early outs: they are what keeps this
                // loop to flag reads for the buildings that fill a zone. A follower the one player
                // here owns while its own player stands two zones off keeps working - its AI runs
                // there, following that player - and it goes back the moment the zone is shared or
                // its owner leaves.
                if (bestIsSelf) {
                    if (zdo.IsOwner()) { continue; }
                } else if (!zdo.IsOwner() && zdo.GetOwner() == bestUid) {
                    continue;
                }

                // Owned by someone who is not the one candidate present here - so not present.
                long owner = zdo.GetOwner();
                bool creature = _creaturesEnabled && OwnershipPolicy.IsCreature(zdo);
                if (creature && OwnerStillLoads(owner, zone)) {
                    ConsiderLoadedCreature(zdo, zone, verdict, owner, now, minHold);
                    continue;
                }

                RescueObject(zdo, creature ? LeaderOr(zdo, zone, bestUid) : bestUid, creature, verdict, owner, now);
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
                        RescueObject(zdo, RescueTarget(zdo, zone, verdict, unownedCreature), unownedCreature, verdict, 0L, now);
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
                        ConsiderLoadedCreature(zdo, zone, verdict, currentOwner, now, minHold);
                        continue;
                    }
                }

                if (!verdict.HasEligible) {
                    // Nobody can take it - see ReleaseZone for why an absent owner is released
                    // rather than left in place, and why one who still has it loaded keeps it.
                    if (!present) {
                        if (_keepWhileLoaded && OwnerStillLoads(currentOwner, zone)) {
                            LastPassLoadedKept++;
                            continue;
                        }
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
                    RescueObject(zdo, RescueTarget(zdo, zone, verdict, creature), creature, verdict, currentOwner, now);
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

                // FOLLOWERS. A tame following a player, or something a player summoned, is part of
                // that player's party: its AI walks where they walk and fights what they fight, and
                // the game runs that AI on whoever owns it. Owned by anybody else, it follows the
                // player's position as that machine last heard it - a pet that lags a step behind
                // and a summon whose attacks land a round trip late - and the cost function did
                // exactly that on a 55-player server: 128 of 130 staff summons in a day were moved
                // off the player who raised them to a lower-latency bystander, and the summoner,
                // still simulating them, kept writing (1,314 stale updates). So such a creature
                // belongs to its player whenever that player
                // can take it, ahead of both the proximity layer and the cost function, and nothing
                // else moves it. A ridden animal is excluded inside LeaderFor: its rider controls it.
                if (creature) {
                    long leader = LeaderFor(zdo, zone);
                    if (leader != 0L) {
                        if (leader == currentOwner) {
                            LastPassLeaderKept++;
                        } else if (now - Touch(zdo.m_uid, currentOwner, now) >= minHold) {
                            QueueLeaderMove(zdo, leader, currentOwner, verdict);
                        } else {
                            LastPassLeaderKept++;                              // held; returned next pass
                        }
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
                // players near it is a shared fight, and falls through to the cost function
                // exactly as before. Nobody near it is nobody's fight: it falls through too, but
                // an owner still within the keep distance (OwnershipPolicy.ShouldKeepWithOwner)
                // keeps it, so a player stepping just outside the radius of the creature they were
                // fighting does not lose it to a lower-latency player further away - only for the
                // layer to pull it back once they step in again.
                //
                // Ahead of the "owner is already the sector's best" test on purpose: the commonest
                // shape of the two-player case is the lower-latency player owning a creature the
                // other one has just walked up to, and that test would retire it.
                //
                // The cost is SoleNearbyViewer: the same handful of multiplies tier 2 runs, per
                // simulated object in a contested zone. Everything that does not move was retired
                // by the tier split above without reaching this.
                bool keepWithOwner = false;
                int soleScan = int.MinValue;                                  // not scanned; see CollectShed
                if (_proximityEnabled) {
                    int sole = SoleNearbyViewer(verdict, zdo.GetPosition(), currentOwner,
                                                out int ownerIndex, out float ownerSq);
                    soleScan = sole;
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
                            // The one player near it takes it whatever their allowance says (the
                            // proximity rule stands), but it still counts against them.
                            if (creature) { ReserveCreature(near.Uid, currentOwner); }
                        } else {
                            LastPassProximityKept++;
                        }
                        continue;
                    }

                    // Nobody near: the scan walked every candidate, so the owner's index and
                    // distance are real. Decided here, applied just before a move would be
                    // queued below, so it only ever stops a move the cost function was about to
                    // make - and the hold history is still refreshed on the way.
                    keepWithOwner = sole == NobodyNear
                        && ownerIndex >= 0
                        && Candidates[ownerIndex].CanOwn
                        && OwnershipPolicy.ShouldKeepWithOwner(Candidates[ownerIndex].IsViewer, ownerSq, _proximityKeepSq);
                }

                // Tier 1's cost function from here down. A single compare that retires nearly
                // every remaining ZDO in the zone - in a settled world the owner already is the
                // best choice.
                //
                // Every way out below that leaves a creature where it is also asks M35 whether its
                // owner is over their creature allowance, and if so notes it as one that could be
                // moved off them (CollectShed). Only these declining exits ask: a creature the
                // cost function was going to move anyway moves the ordinary way.
                bool shedCandidate = creature && OverAllowance(currentOwner);
                if (verdict.BestUid == currentOwner) {
                    if (shedCandidate) { CollectShed(zdo, verdict, currentOwner, now, minHold, soleScan); }
                    continue;
                }

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

                // M35: a creature does not go to a player already at their creature allowance
                // while somebody else present can take it. The best of the rest with room is asked
                // instead, and if that is the owner it already has, it stays put. When nobody else
                // has room the allowance does not hold and the cost function's choice stands
                // (PlaceCreature) - the struggling player is then the only one who can run it.
                long target = verdict.BestUid;
                float targetTotal = verdict.BestTotalMs;
                bool steered = false;
                if (creature && _allowanceActive && RoomOf(target) <= 0) {
                    target = PlaceCreature(verdict, out targetTotal, out steered);
                    if (target == currentOwner) {
                        if (shedCandidate) { CollectShed(zdo, verdict, currentOwner, now, minHold, soleScan); }
                        continue;
                    }
                }

                float improvement = currentTotal - targetTotal;
                if (improvement < margin) {
                    if (shedCandidate) { CollectShed(zdo, verdict, currentOwner, now, minHold, soleScan); }
                    continue;
                }

                // The proximity layer's keep band, decided above: nobody is near and the owner is
                // still close enough to have been fighting it.
                if (keepWithOwner) {
                    LastPassProximityHeld++;
                    TotalProximityHeld++;
                    if (shedCandidate) { CollectShed(zdo, verdict, currentOwner, now, minHold, soleScan); }
                    continue;
                }

                PendingSimulated.Add(new PendingMove {
                    Zdo = zdo, NewOwner = target, Priority = improvement, Sector = verdict,
                    Creature = creature, OldOwner = currentOwner,
                });
                if (creature) { ReserveCreature(target, currentOwner); }
                if (steered) {
                    LastPassSteered++;
                    TotalSteered++;
                }
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
        ///   * a follower whose player can take it (LeaderFor)        -> with that player: kept if
        ///     they own it, returned to them otherwise.
        ///   * exactly one present player near it                     -> to them, as a proximity
        ///     pull. The owner is outside the presence square, so at least a zone away and beyond
        ///     the pull distance at the default radius.
        ///   * two or more near it                                    -> a shared fight the owner
        ///     is not in, so the cost function's choice among the present candidates.
        ///
        /// The hold applies to all of it: a creature that has just changed hands keeps its owner
        /// for Min Hold Seconds whatever the geometry says.
        /// </summary>
        private static void ConsiderLoadedCreature(ZDO zdo, Vector2s zone, SectorVerdict verdict, long owner, float now, float minHold) {
            if (OwnershipPolicy.IsDirectlyControlled(zdo)) {
                LastPassCreaturesKept++;
                return;
            }

            // Ahead of the eligibility test: a follower's player need not be present here to take
            // it, only to have it loaded.
            long leader = LeaderFor(zdo, zone);
            if (leader != 0L) {
                if (leader != owner && now - Touch(zdo.m_uid, owner, now) >= minHold) {
                    QueueLeaderMove(zdo, leader, owner, verdict);
                } else {
                    LastPassCreaturesKept++;
                    LastPassLeaderKept++;
                }
                return;
            }

            if (!verdict.HasEligible) {
                LastPassCreaturesKept++;
                return;
            }

            int sole;
            int near = NearbyViewers(verdict, zdo.GetPosition(), out sole);
            if (near == 0 || now - Touch(zdo.m_uid, owner, now) < minHold) {
                LastPassCreaturesKept++;
                // Nobody near it, and its owner is over their creature allowance (M35): it is one
                // that could go to a player with room.
                if (near == 0 && OverAllowance(owner)) { CollectShed(zdo, verdict, owner, now, minHold, NobodyNear); }
                return;
            }

            long target;
            bool proximity = near == 1 && _proximityEnabled && Candidates[sole].CanOwn;
            if (proximity) {
                target = Candidates[sole].Uid;
            } else {
                target = verdict.BestUid;
                // M35: not onto a player at their allowance while somebody else present can take
                // it. With nobody else, the allowance does not hold (PlaceCreature).
                if (_allowanceActive && RoomOf(target) <= 0) {
                    target = PlaceCreature(verdict, out _, out bool steered);
                    if (steered) {
                        LastPassSteered++;
                        TotalSteered++;
                    }
                }
            }

            int ownerIndex = CandidateIndex[owner];                           // OwnerStillLoads found it
            int targetIndex = CandidateIndex.TryGetValue(target, out int found) ? found : ownerIndex;
            PendingSimulated.Add(new PendingMove {
                Zdo = zdo, NewOwner = target,
                Priority = (Candidates[ownerIndex].RttMs + Candidates[targetIndex].RttMs) * 0.5f,
                Sector = verdict, Proximity = proximity,
                Creature = true, OldOwner = owner,
            });
            ReserveCreature(target, owner);
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

        /// <summary>SoleNearbyViewer's answer when no viewer is within the radius. The scan has
        /// walked every candidate, so the owner's index and distance it reports are real.</summary>
        private const int NobodyNear = -1;

        /// <summary>SoleNearbyViewer's answer when two or more viewers are within the radius. It
        /// returns on the second, so the owner's index and distance may not have been reached.</summary>
        private const int SharedFight = -2;

        /// <summary>
        /// The proximity layer's scan: the index into Candidates of the ONE player within the
        /// proximity radius of this position, NobodyNear when nobody is, or SharedFight when more
        /// than one is.
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
        /// caller can decide a pull or a keep without a second walk. Both are only meaningful when
        /// the return value is not SharedFight: that early return has not necessarily reached the
        /// owner yet. A caller with no owner to find - a rescue - uses NearbyViewers instead.
        /// </summary>
        private static int SoleNearbyViewer(SectorVerdict verdict, Vector3 pos, long currentOwner,
                                            out int ownerIndex, out float ownerSq) {
            ownerIndex = -1;
            ownerSq = float.MaxValue;
            int sole = NobodyNear;

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
                if (sole >= 0) { return SharedFight; }                        // a second player
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
        /// Nor is the creature pushed to a player who has no copy of it yet - which is the new
        /// owner at every login - or it would land before the floor under it (ForceSendIfHeld).
        /// </summary>
        private static void RescueObject(ZDO zdo, long newOwner, bool creature, SectorVerdict verdict, long oldOwner, float now) {
            Rescue(zdo, newOwner, now);
            if (!creature) { return; }

            // Counted against the new owner's creature allowance (M35) like any other creature
            // they are given. Not as one leaving the old owner: they are not simulating it.
            ReserveCreature(newOwner, 0L);

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
        private static long RescueTarget(ZDO zdo, Vector2s zone, SectorVerdict verdict, bool creature) {
            // A follower goes back to its player before anything else is asked. Nobody present is
            // simulating it, so nothing races the move.
            if (creature) {
                long leader = LeaderFor(zdo, zone);
                if (leader != 0L) {
                    LastPassLeaderRescued++;
                    TotalLeaderRescued++;
                    return leader;
                }
            }

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
            if (!ShipHelmOwnership.Enabled) { return SoleNearbyOrBest(zdo, verdict, creature); }

            long playerId = OwnershipPolicy.HelmsmanPlayerId(zdo);
            if (playerId == 0L) { return SoleNearbyOrBest(zdo, verdict, creature); }

            long helmsman = PeerForPlayer(playerId);
            if (helmsman == 0L) { return SoleNearbyOrBest(zdo, verdict, creature); }
            if (helmsman != verdict.BestUid && (!IsPresent(verdict, helmsman) || !CanOwn(helmsman))) {
                return SoleNearbyOrBest(zdo, verdict, creature);
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
        ///
        /// A creature falling back to the sector's best skips a best that is at its creature
        /// allowance (M35) for the best with room, when there is one. Ships and carts are not
        /// creatures and never do.
        /// </summary>
        private static long SoleNearbyOrBest(ZDO zdo, SectorVerdict verdict, bool creature) {
            if (!_proximityEnabled) { return creature ? RoomyRescueTarget(verdict) : verdict.BestUid; }

            if (NearbyViewers(verdict, zdo.GetPosition(), out int sole) != 1 || !Candidates[sole].CanOwn) {
                return creature ? RoomyRescueTarget(verdict) : verdict.BestUid;
            }

            long uid = Candidates[sole].Uid;
            if (uid != verdict.BestUid) {
                LastPassProximityRescued++;
                TotalProximityRescued++;
            }
            return uid;
        }

        // -- followers ----------------------------------------------------------------------

        /// <summary>Sorts a return to a follower's player ahead of every other creature move:
        /// the other moves are preferences about latency, and this is the creature's own party.</summary>
        private const float LeaderMovePriority = 1e6f;

        /// <summary>
        /// The player this creature should stay with, when there is one who can take it; 0
        /// otherwise, and the creature is arbitrated like any other.
        ///
        ///   * A tame follows the player named in its s_follow - set when a player tells it to
        ///     follow, and on spawn for a summon that is a Tameable (the Dead Raiser's skeleton).
        ///     Told to stay, it names nobody and is an ordinary creature. Ridden, it is its rider's
        ///     (IsDirectlyControlled), whoever it was following.
        ///   * A player-side summon without a Tameable (OwnershipPolicy PrefabClass.Summon) belongs
        ///     to the session that created it, which its ZDOID records.
        ///
        /// "Can take it" is two things: the player is a candidate that may own - connected,
        /// positioned, answering - and has the creature's zone in the ring its game has loaded,
        /// so its machine has an instance to run. A follower left behind in a zone its player no
        /// longer loads, or whose player has gone, falls back to the ordinary rules.
        ///
        /// Cost: the classification is the cached one IsCreature just paid for, and only a tame
        /// reads a string - so the only new work on a pass without followers is a switch.
        /// </summary>
        private static long LeaderFor(ZDO zdo, Vector2s zone) {
            if (!_followersEnabled) { return 0L; }

            long leader;
            switch (OwnershipPolicy.FollowerKind(zdo)) {
                case OwnershipPolicy.Follower.Tame:
                    if (OwnershipPolicy.IsDirectlyControlled(zdo)) { return 0L; }
                    string name = zdo.GetString(ZDOVars.s_follow, "");
                    if (name.Length == 0) { return 0L; }
                    leader = PeerForName(name);
                    break;
                case OwnershipPolicy.Follower.Summon:
                    leader = zdo.m_uid.UserID;
                    break;
                default:
                    return 0L;
            }
            return CanTake(leader, zone) ? leader : 0L;
        }

        /// <summary>The candidate may own and has this zone loaded. See LeaderFor.</summary>
        private static bool CanTake(long uid, Vector2s zone) {
            if (uid == 0L || !CandidateIndex.TryGetValue(uid, out int index)) { return false; }
            Candidate candidate = Candidates[index];
            return candidate.CanOwn && ZoneCompat.NearRingLoaded(candidate.Zone, zone, candidate.NearRadius, candidate.NearClassic);
        }

        /// <summary>A rescue target for a creature: its player when it has one who can take it,
        /// the given fallback otherwise.</summary>
        private static long LeaderOr(ZDO zdo, Vector2s zone, long fallback) {
            long leader = LeaderFor(zdo, zone);
            if (leader == 0L) { return fallback; }

            LastPassLeaderRescued++;
            TotalLeaderRescued++;
            return leader;
        }

        /// <summary>
        /// Return a follower to its player from an owner who is present and simulating it: a
        /// tier-1 move like any other - capped, and pushed out at once to the sector and the old
        /// owner when applied - but first in the queue. The caller has already applied the hold.
        /// </summary>
        private static void QueueLeaderMove(ZDO zdo, long leader, long oldOwner, SectorVerdict verdict) {
            PendingSimulated.Add(new PendingMove {
                Zdo = zdo, NewOwner = leader, Priority = LeaderMovePriority,
                Sector = verdict, Creature = true, Leader = true, OldOwner = oldOwner,
            });
            ReserveCreature(leader, oldOwner);
        }

        /// <summary>
        /// Give a follower to its player from a zone nobody is present in, instead of releasing it.
        /// There is no sector verdict here to push the change to, and nobody but the two parties
        /// can see it: its player, who is about to run it, and the old owner, who may still be
        /// writing it.
        /// </summary>
        private static void GiveToLeader(ZDO zdo, long leader, long oldOwner, float now) {
            if (Monitoring.Active) { Monitoring.NoteCause(zdo, HandoffCause.Leader); }
            zdo.SetOwner(leader);
            OwnerHistory[zdo.m_uid] = new OwnershipRecord { Owner = leader, ChangedAt = now, LastSeenAt = now };
            LastPassLeaderRescued++;
            TotalLeaderRescued++;
            ReserveCreature(leader, oldOwner);

            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null) { return; }
            long self = zdoMan.m_sessionID;
            if (leader != self) { ForceSendIfHeld(zdoMan, leader, zdo.m_uid); }
            if (oldOwner != 0L && oldOwner != self && oldOwner != leader && CandidateIndex.ContainsKey(oldOwner)) {
                ForceSendIfHeld(zdoMan, oldOwner, zdo.m_uid);
            }
        }

        private static long PeerForName(string name) {
            if (!_peerByNameBuilt) {
                BuildPeerByName();
                _peerByNameBuilt = true;
            }
            return PeerByName.TryGetValue(name, out long uid) ? uid : 0L;
        }

        /// <summary>Every connected player's name, and a listen host's own. ZNetPeer.m_playerName is
        /// the profile name the client sent in its PeerInfo, which is also what its character's
        /// GetPlayerName returns and so what Tameable wrote.</summary>
        private static void BuildPeerByName() {
            PeerByName.Clear();

            ZNet net = ZNet.instance;
            ZDOMan zdoMan = ZDOMan.instance;
            if (net == null || zdoMan == null) { return; }

            if (!NpsEnv.IsDedicated() && Player.m_localPlayer != null) {
                AddName(Player.m_localPlayer.GetPlayerName(), zdoMan.m_sessionID);
            }

            List<ZNetPeer> peers = net.GetPeers();
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (peer == null || peer.m_uid == 0L) { continue; }
                AddName(peer.m_playerName, peer.m_uid);
            }
        }

        private static void AddName(string name, long uid) {
            if (string.IsNullOrEmpty(name)) { return; }
            PeerByName[name] = PeerByName.ContainsKey(name) ? 0L : uid;      // shared: nobody's
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
            LastPassLeaderReturned = 0;
            LastPassShed = 0;

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
                    if (move.Leader) {
                        Monitoring.NoteCause(move.Zdo, HandoffCause.Leader);
                    } else {
                        HandoffCause cause = move.Shed ? HandoffCause.Capacity
                                           : move.Proximity ? HandoffCause.Proximity
                                           : HandoffCause.Optimise;
                        Monitoring.NoteCause(move.Zdo, cause, move.Priority, DescribeCandidates(move));
                    }
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
                if (move.Leader) {
                    LastPassLeaderReturned++;
                    TotalLeaderReturned++;
                }
                if (move.Shed) {
                    LastPassShed++;
                    TotalShed++;
                }
                if (move.Creature) {
                    LastPassCreaturesOptimised++;
                    TotalCreaturesOptimised++;
                }
                if (forceSend || move.Proximity || move.Creature) {
                    // A rock or tree M33 holds updates for only needs telling its new owner, who
                    // is about to hit it, and its old one, who must stop simulating it. Everyone
                    // else's hits reach the new owner through the router's structure family, and
                    // their copies catch up when the change is due.
                    if (_structureOwnersWait && !move.Creature && StructureUpdates.IsHeldStructure(move.Zdo)) {
                        ForceSendToOwners(move.NewOwner, move.OldOwner, move.Zdo.m_uid);
                    } else {
                        ForceSendTo(move.Sector, move.Zdo.m_uid, move.OldOwner);
                    }
                }
            }
        }

        /// <summary>ForceSendTo narrowed to the two machines an owner change is about.</summary>
        private static void ForceSendToOwners(long newOwner, long oldOwner, ZDOID id) {
            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null) { return; }
            long self = zdoMan.m_sessionID;
            if (newOwner != 0L && newOwner != self) { ForceSendIfHeld(zdoMan, newOwner, id); }
            if (oldOwner != 0L && oldOwner != self && oldOwner != newOwner) { ForceSendIfHeld(zdoMan, oldOwner, id); }
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
        ///
        /// Of those, only the ones that already hold a copy are pushed to. The rest have no stale
        /// owner id to correct, and the object reaches them in the game's order - see
        /// ForceSendIfHeld.
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
                ForceSendIfHeld(zdoMan, present[i], id);
            }
            if (!alsoToSent && CandidateIndex.ContainsKey(alsoTo)) { ForceSendIfHeld(zdoMan, alsoTo, id); }
        }

        /// <summary>
        /// Push an object to the front of one player's next send - only if the host has sent it to
        /// them before.
        ///
        /// A push corrects a copy that names the wrong owner. A player with no ZDOPeer.m_zdos entry
        /// for the object has never been sent it, so has no copy to correct and no instance to
        /// hit. Pushing it to them anyway does harm, because AddForceSendZdos inserts at index 0,
        /// ahead of the order ServerSendCompare gives a first delivery: Terrain, then Solid, then
        /// the rest. Floors and walls are Solid and creatures are Default, so a pushed creature
        /// reaches the player who now owns it before the floor it stands on. That player's physics
        /// runs with nothing under it, and it falls. From 1.8.0 this put penned animals on the
        /// ground below their pens at every login (issue #5): the login rescue hands them to the
        /// arriving player, who by definition holds no copy yet. Left alone, the object arrives in
        /// the game's order and already names its new owner, so nothing is lost by not pushing.
        ///
        /// This is the host, so GetPeer is exactly what ZDOMan.ForceSendZDO(long, ZDOID) does.
        /// </summary>
        private static void ForceSendIfHeld(ZDOMan zdoMan, long uid, ZDOID id) {
            ZDOMan.ZDOPeer peer = zdoMan.GetPeer(uid);
            if (peer == null) { return; }
            if (!peer.m_zdos.ContainsKey(id)) {
                LastPassFirstSendsInOrder++;
                TotalFirstSendsInOrder++;
                return;
            }
            peer.ForceSendZDO(id);
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

        /// <summary>Creatures this peer owned as of the last pass. Zero while the creature
        /// allowance (M35) is off, because then nothing tallies them. Read by PeerCapacity as
        /// well as by monitoring.</summary>
        internal static int OwnedCreaturesFor(long uid) {
            return OwnedCreatures.TryGetValue(uid, out int owned) ? owned : 0;
        }

        /// <summary>Of those, the creatures somebody else could run as of the last pass (see
        /// SharedCreatures). Zero while the allowance is off.</summary>
        internal static int SharedCreaturesFor(long uid) {
            return SharedCreatures.TryGetValue(uid, out int shared) ? shared : 0;
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
                  .Append(",\"viewer\":").Append(candidate.IsViewer ? "true" : "false");
                // M35: the frame rate the player reported, and how many more creatures they could
                // take when this move was decided (left out when they had no allowance).
                if (PeerCapacity.TryGetView(candidate.Uid, out PeerCapacity.View load) && load.HasFps) {
                    sb.Append(",\"fps\":").Append(load.Fps.ToString("0", invariant));
                }
                int room = RoomOf(candidate.Uid);
                if (room != int.MaxValue) {
                    sb.Append(",\"room\":").Append(room.ToString(invariant));
                }
                sb.Append('}');
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
            LastPassProximityHeld = 0;
            TotalProximityPulled = 0;
            TotalProximityRescued = 0;
            TotalProximityHeld = 0;
            LastPassLeaderKept = 0;
            LastPassLeaderReturned = 0;
            LastPassLeaderRescued = 0;
            TotalLeaderReturned = 0;
            TotalLeaderRescued = 0;
            PeerByName.Clear();
            _peerByNameBuilt = false;
            _followersEnabled = false;
            _proximityEnabled = false;
            _proximityRadiusSq = 0f;
            _proximityPullSq = 0f;
            _proximityKeepSq = 0f;

            LastPassCreaturesRescued = 0;
            LastPassCreaturesOptimised = 0;
            LastPassCreaturesKept = 0;
            TotalCreaturesRescued = 0;
            TotalCreaturesOptimised = 0;
            LastPassFirstSendsInOrder = 0;
            TotalFirstSendsInOrder = 0;
            _creaturesEnabled = false;
            LastPassLoadedKept = 0;
            _keepWhileLoaded = false;

            _monitorSoleCreatures = 0;
            _monitorContestedCreatures = 0;

            OwnedCreatures.Clear();
            SharedCreatures.Clear();
            Helpers.Clear();
            CreatureRoom.Clear();
            ShedReceiver.Clear();
            PendingOut.Clear();
            PendingIn.Clear();
            ShedCollected.Clear();
            ShedEntries.Clear();
            ReceiverOptions.Clear();
            _allowanceActive = false;
            _challengeMarginMs = 0f;
            LastPassSteered = 0;
            LastPassShed = 0;
            LastPassShedBlocked = 0;
            TotalSteered = 0;
            TotalShed = 0;
            TotalShedBlocked = 0;
        }
    }
}
