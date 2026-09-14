using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// Which ZDOs are off-limits to the arbiter.
    ///
    /// The rule is "authority follows direct control". An object a player is actively driving must
    /// stay on that player's machine no matter what the latency maths says, because moving it
    /// makes their own input round-trip through someone else. This is a well-attested failure mode
    /// rather than a theoretical one: valheim-serverside issue #42 is the server taking ship
    /// ownership and the helmsman stuttering, VBNetTweaks explicitly hands ships to whoever takes
    /// the helm, and SkadiNet ships a hard-coded AllowShipOwnership = false.
    /// </summary>
    internal static class OwnershipPolicy {

        private enum PrefabClass : byte {
            Ordinary = 0,
            Player = 1,
            Ship = 2,
            Mount = 3,
            Cart = 4,
        }

        private static readonly Dictionary<int, PrefabClass> ClassCache = new Dictionary<int, PrefabClass>();

        /// <summary>
        /// True when this ZDO represents something a player is or could be directly driving.
        ///
        /// Only ever reached for a ZDO whose owner is still present: the arbiter's rescue branch
        /// runs first and returns, so an unowned ship, or one whose helmsman disconnected, never
        /// consults this at all. That ordering is what stops a stale s_user - a rider id left
        /// behind by a disconnected player - from reading as "controlled" forever and excluding
        /// the mount from recovery permanently.
        ///
        /// Also only reached for Prioritized ZDOs - the arbiter never moves anything else off a
        /// present owner - so a Player, Ship or cart that somehow carried the Default type would
        /// simply never move, which is the stricter outcome.
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
                default:
                    return false;
            }
        }

        private static PrefabClass Classify(ZDO zdo) {
            int prefab = zdo.GetPrefab();
            if (ClassCache.TryGetValue(prefab, out PrefabClass cached)) { return cached; }

            PrefabClass result = PrefabClass.Ordinary;
            if (ZNetScene.instance != null) {
                GameObject go = ZNetScene.instance.GetPrefab(prefab);
                if (go != null) {
                    if (go.GetComponent<Player>() != null) { result = PrefabClass.Player; }
                    else if (go.GetComponent<Ship>() != null) { result = PrefabClass.Ship; }
                    else if (go.GetComponent<Vagon>() != null) { result = PrefabClass.Cart; }
                    else if (go.GetComponent<Tameable>() != null) { result = PrefabClass.Mount; }
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
            return result;
        }

        internal static void Reset() {
            ClassCache.Clear();
        }
    }
}
