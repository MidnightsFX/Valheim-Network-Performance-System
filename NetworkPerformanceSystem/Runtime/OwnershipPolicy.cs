using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// What kind of thing a ZDO is, for the two questions the arbiter asks about it: may it be moved
    /// off a present owner at all, and if so, by which tier's rules.
    ///
    /// The first rule is "authority follows direct control". An object a player is actively driving
    /// must stay on that player's machine no matter what the latency maths says, because moving it
    /// makes their own input round-trip through someone else. This is a well-attested failure mode
    /// rather than a theoretical one: valheim-serverside issue #42 is the server taking ship
    /// ownership and the helmsman stuttering, VBNetTweaks explicitly hands ships to whoever takes
    /// the helm, and SkadiNet ships a hard-coded AllowShipOwnership = false.
    ///
    /// The second is the tier-2 rule, and it is the same principle one step weaker: an object
    /// nobody is driving but somebody is about to touch should be on the machine of whoever is
    /// about to touch it. See OwnershipArbiter's tier-2 section for why that is a different
    /// question from tier 1's staleness cost, and ShouldPlaceInteractive below for the geometry.
    ///
    /// One prefab classification answers both, which is the reason the tier-2 values live in this
    /// enum rather than in a cache of their own: the arbiter pays a single dictionary probe per
    /// contested ZDO and gets "may I move it" and "by which rule" together.
    /// </summary>
    internal static class OwnershipPolicy {

        private enum PrefabClass : byte {
            Ordinary = 0,
            Player = 1,
            Ship = 2,
            Mount = 3,
            Cart = 4,

            /// <summary>A store somebody can have open. Ranked ahead of Interactive so that a chest
            /// with a Destructible on it is a chest, not an interactable: Container.RPC_RequestOpen
            /// hands the opener ownership and OnContainerChanged only saves on the owner, so a move
            /// mid-use drops whatever they just put in.</summary>
            Container = 5,

            /// <summary>Stationary, but its whole purpose is to be touched: a pickable, an ore
            /// vein, a rock, a tree, a log, a destructible. Every one of these routes its
            /// interaction through ZNetView.InvokeRPC(string, ...), which addresses
            /// m_zdo.GetOwner() - so whether the person about to touch it is the one simulating it
            /// is the entire latency story for these objects.</summary>
            Interactive = 6,

            /// <summary>Something with a Character that is not a player and not a tame: the
            /// creatures the arbiter's tier 1 was written for. Not found by ZDO.ObjectType, which
            /// marks ships and players as Prioritized but leaves every creature Default - the
            /// reason tier 1 and the proximity layer had never reached one. A tame is a creature
            /// too; it is Mount above so that riding one is still direct control, and IsCreature
            /// counts both.</summary>
            Creature = 7,
        }

        private static readonly Dictionary<int, PrefabClass> ClassCache = new Dictionary<int, PrefabClass>();

        /// <summary>
        /// A small direct-mapped cache in front of ClassCache. IsCreature is asked of every owned
        /// object in the arbiter's load tally and of every Default object a contested zone reaches
        /// the tier split with - mostly building pieces, every pass, and the tier split cannot be
        /// ordered behind tier 2's cheap distance tests because a creature nobody is standing next
        /// to still belongs to tier 1. So the classification itself has to be nearly free. A base
        /// alternates among a few dozen prefabs, which a single "last prefab" slot missed on every
        /// change of wall to floor to torch; a slot picked from the hash's low bits holds all of
        /// them at once and costs an index and a compare where the dictionary costs a hash and a
        /// bucket walk. A prefab hash of 0 is the "not deserialized yet" value and is never stored,
        /// so 0 doubles as the empty marker. Main thread only, like everything that reads it.
        /// </summary>
        private const int FastSlots = 256;
        private static readonly int[] FastPrefab = new int[FastSlots];
        private static readonly PrefabClass[] FastClass = new PrefabClass[FastSlots];

        /// <summary>
        /// How far an incumbent owner must be from an object before tier 2 will take it away.
        ///
        /// Not a preference, so not a config entry: it is Player.m_maxInteractDistance (5f,
        /// Player.cs:173) plus slack for the host's copy of a reference position being up to a send
        /// interval old. Same standing as ZoneCompat.ActiveZoneRadius - a constant with a citation.
        ///
        /// This is the strongest of the three things holding the duplication window shut. Vanilla
        /// only ever changes one of these objects' owners while that owner is ABSENT, so it has no
        /// instance and cannot be interacting; tier 2 moves objects between two peers who both hold
        /// one. For the window in which their copies of the owner id disagree, two RPC_Pick calls -
        /// one to the old owner, one to the new - can each pass their own IsOwner gate and each run
        /// the drop loop. Refusing to take an object from an owner still within reach of it means
        /// only one end of that window is ever live for a pick or a melee swing, which is the case
        /// that matters. It does not close ranged damage into a Destructible from forty metres.
        /// </summary>
        internal const float InteractionStandOffMetres = 8f;

        /// <summary>
        /// True when this ZDO represents something a player is or could be directly driving.
        ///
        /// Only ever reached for a ZDO whose owner is still present: the arbiter's rescue branch
        /// runs first and returns, so an unowned ship, or one whose helmsman disconnected, never
        /// consults this at all. That ordering is what stops a stale s_user - a rider id left
        /// behind by a disconnected player - from reading as "controlled" forever and excluding
        /// the mount from recovery permanently.
        ///
        /// Also only reached for tier 1 - Prioritized ZDOs and creatures - the only path that consults this, and
        /// tier 2 does not need to, because Classify ranks every class below ahead of Interactive
        /// and so a prefab that is both (a cart with a destructible hull, a chest with one) never
        /// reads as interactable at all.
        /// </summary>
        internal static bool IsDirectlyControlled(ZDO zdo) {
            switch (Classify(zdo)) {
                case PrefabClass.Player:
                    return true;
                case PrefabClass.Ship:
                    return true;
                case PrefabClass.Mount:
                    // A tame only counts as controlled while somebody is actually riding it;
                    // an idle tame is an ordinary creature and benefits from arbitration.
                    // Sadle shares the animal's ZDO and stores the rider's id in s_user
                    // (RPC_RequestControl also hands the rider ownership), so this reads
                    // "has a rider right now" straight off the ZDO we are arbitrating.
                    return zdo.GetLong(ZDOVars.s_user, 0L) != 0L;
                case PrefabClass.Cart:
                    // Vagon mirrors the ship handoff: grabbing the handle transfers ownership
                    // to the puller and sets s_attachJointHash until detach. While attached,
                    // the cart's physics are the puller's input loop - moving it is the same
                    // failure mode as taking a ship from its helmsman.
                    //
                    // An open cargo chest is the same thing: Container.RPC_RequestOpen hands the
                    // opener ownership and OnContainerChanged only saves on the owner, so a move
                    // mid-use drops whatever they just put in. Container writes s_inUse as 0/1.
                    return zdo.GetBool(ZDOVars.s_attachJointHash, false)
                        || zdo.GetInt(ZDOVars.s_inUse, 0) == 1;
                case PrefabClass.Container:
                    // The cargo-hold rule above, for a chest that is not part of a cart. A strict
                    // tightening: a Container-bearing prefab that is Prioritized and neither a
                    // Vagon nor a Ship does not exist in vanilla, so tier 1 cannot notice this.
                    return zdo.GetInt(ZDOVars.s_inUse, 0) == 1;
                default:
                    return false;
            }
        }

        /// <summary>
        /// True when this ZDO is a stationary object whose only interesting operation is an
        /// owner-addressed interaction - tier 2's population.
        ///
        /// Deliberately NOT a state test. Unlike a mount or a cart there is no "idle" concession to
        /// make here: a berry bush is a berry bush whether or not anyone is standing at it, so one
        /// cached classification answers it outright.
        /// </summary>
        internal static bool IsInteractive(ZDO zdo) {
            return Classify(zdo) == PrefabClass.Interactive;
        }

        /// <summary>
        /// True for a creature, tame or wild: anything with a Character that is not a player.
        /// This, not ZDO.ObjectType, is how the arbiter and the monitor recognise the objects that
        /// fight - see PrefabClass.Creature for why the object type cannot answer it.
        /// </summary>
        internal static bool IsCreature(ZDO zdo) {
            PrefabClass cls = Classify(zdo);
            return cls == PrefabClass.Creature || cls == PrefabClass.Mount;
        }

        /// <summary>
        /// The player id recorded at this ship's helm, or 0 when the ZDO is not a ship or nobody
        /// is steering it. ShipControlls shares the ship's ZNetView, so s_user is on the ship's
        /// own ZDO. The value can be stale - a helmsman who disconnected is never cleared when
        /// nobody owns the ship - so callers must confirm the player is actually present.
        /// </summary>
        internal static long HelmsmanPlayerId(ZDO zdo) {
            if (Classify(zdo) != PrefabClass.Ship) { return 0L; }
            return zdo.GetLong(ZDOVars.s_user, 0L);
        }

        /// <summary>
        /// Tier 2's placement rule, with the geometry factored out so the offline harness can table
        /// it against the thresholds directly. Pure - same reason ZoneCompat.ZdoInstancePossible is.
        ///
        ///   * nobody within the claim radius   -> nothing to win, so do not spend an update on it
        ///   * incumbent still within reach     -> it may be mid-swing; see InteractionStandOffMetres
        ///   * challenger not ahead by a margin -> hysteresis, or two players drifting around one
        ///                                         patch trade every bush in it back and forth
        /// </summary>
        internal static bool ShouldPlaceInteractive(float ownerMetres, float nearestMetres,
                                                    float claimRadiusMetres, float marginMetres) {
            if (nearestMetres > claimRadiusMetres) { return false; }
            if (ownerMetres < InteractionStandOffMetres) { return false; }
            return ownerMetres - nearestMetres >= marginMetres;
        }

        /// <summary>
        /// How far beyond the proximity radius an owner has to be before a simulated object is
        /// taken from it for the one player standing near. Same standing as
        /// InteractionStandOffMetres: a constant, because it is not a preference.
        ///
        /// It is the whole of the proximity layer's distance hysteresis. "Exactly one player
        /// within the radius, and it is not the owner" already puts the owner outside the radius;
        /// without a margin a creature pacing along that line would change hands every time the
        /// min-hold allowed. With it, the creature has to cross the margin plus whatever gap lies
        /// between the two players' radii before it can go back. The distances are measured
        /// against reference positions, which are up to 200ms old on the fast channel and up to
        /// two seconds old for a client without this mod, so the band absorbs some of that error
        /// too. The min-hold is the other half: whatever the geometry says, a new owner keeps the
        /// object for Min Hold Seconds.
        /// </summary>
        internal const float ProximityPullMarginMetres = 8f;

        /// <summary>
        /// The proximity layer's pull rule, with the geometry factored out for the same reason
        /// ShouldPlaceInteractive is: pure, so the offline harness can table it. Squared distances
        /// in and out, because the caller never needs a root.
        ///
        /// Asked only once the caller has established that exactly one player is near the object
        /// and that player is not its owner. What is left to decide is whether the owner is far
        /// enough away to lose it:
        ///
        ///   * owner is not a viewer -> yes. That is a dedicated host, whose reference position is
        ///                              the world origin and not a place anybody is standing, so
        ///                              its distance means nothing. It simulates the object for
        ///                              nobody's benefit but latency's, and one player fighting
        ///                              alone is better off at zero hops than at one.
        ///   * owner beyond the margin -> yes.
        ///   * otherwise               -> no, and the caller holds the object where it is.
        /// </summary>
        internal static bool ShouldPullToSoleNearby(bool ownerIsViewer, float ownerSq, float pullSq) {
            if (!ownerIsViewer) { return true; }
            return ownerSq > pullSq;
        }

        private static PrefabClass Classify(ZDO zdo) {
            int prefab = zdo.GetPrefab();
            int slot = (prefab ^ (prefab >> 16)) & (FastSlots - 1);
            if (prefab != 0 && FastPrefab[slot] == prefab) { return FastClass[slot]; }
            if (ClassCache.TryGetValue(prefab, out PrefabClass cached)) { return Remember(slot, prefab, cached); }

            PrefabClass result = PrefabClass.Ordinary;
            if (ZNetScene.instance != null) {
                GameObject go = ZNetScene.instance.GetPrefab(prefab);
                if (go != null) {
                    // The first four keep the precedence they have always had, so nothing this
                    // classified before is classified differently now. The later cases sit
                    // strictly below them: a cart's cargo hold is already covered by Cart, and
                    // anything a player drives is never merely interactable. Creature sits below
                    // Container so that a modded creature carrying a chest keeps the in-use
                    // protection a chest gets, and above Interactive because a creature that
                    // also carries a Destructible is still something that moves and fights.
                    if (go.GetComponent<Player>() != null) { result = PrefabClass.Player; }
                    else if (go.GetComponent<Ship>() != null) { result = PrefabClass.Ship; }
                    else if (go.GetComponent<Vagon>() != null) { result = PrefabClass.Cart; }
                    else if (go.GetComponent<Tameable>() != null) { result = PrefabClass.Mount; }
                    else if (go.GetComponent<Container>() != null) { result = PrefabClass.Container; }
                    else if (go.GetComponent<Character>() != null) { result = PrefabClass.Creature; }
                    else if (IsInteractable(go)) { result = PrefabClass.Interactive; }
                } else {
                    // Unknown prefab (content mod not loaded here, or a stale ZDO). Do not cache a
                    // verdict we cannot justify - treat it as ordinary this time and look again
                    // once ZNetScene knows about it.
                    return PrefabClass.Ordinary;
                }
            } else {
                return PrefabClass.Ordinary;
            }

            ClassCache[prefab] = result;
            return Remember(slot, prefab, result);
        }

        private static PrefabClass Remember(int slot, int prefab, PrefabClass cls) {
            FastPrefab[slot] = prefab;
            FastClass[slot] = cls;
            return cls;
        }

        /// <summary>
        /// Does this prefab carry one of the components whose interaction is owner-addressed?
        /// Answered from the prefab rather than the name, the same way RpcOwnerRouter.IsStation
        /// is, so content mods that build on the vanilla components are covered.
        ///
        /// WearNTear - and Piece, which it always accompanies - excludes the prefab outright, and
        /// that exclusion is the structural safety rule. WearNTear is what makes an object part of
        /// a building: UpdateSupport writes s_support only on the owner (WearNTear.cs:529, :915),
        /// and a collapsing piece addresses RPC_ClearCachedSupport at a NEIGHBOURING piece's
        /// GetZDO().GetOwner() (WearNTear.cs:889) - an owner-addressed call across two different
        /// ZDOs, which is a strictly harder stale-owner problem than anything tier 2 accepts.
        /// Nothing in the seven components below belongs on a player-built piece, so the exclusion
        /// costs nothing and keeps every support network out of a tier that moves owners between
        /// two present peers. It is also what keeps portals (TeleportWorld + Piece + WearNTear) on
        /// vanilla's rule.
        /// </summary>
        private static bool IsInteractable(GameObject go) {
            if (go.GetComponent<WearNTear>() != null) { return false; }
            if (go.GetComponent<Piece>() != null) { return false; }

            return go.GetComponent<Pickable>() != null
                || go.GetComponent<PickableItem>() != null
                || go.GetComponent<MineRock>() != null
                || go.GetComponent<MineRock5>() != null
                || go.GetComponent<Destructible>() != null
                || go.GetComponent<TreeBase>() != null
                || go.GetComponent<TreeLog>() != null;
        }

        internal static void Reset() {
            ClassCache.Clear();
            System.Array.Clear(FastPrefab, 0, FastSlots);
        }
    }
}
