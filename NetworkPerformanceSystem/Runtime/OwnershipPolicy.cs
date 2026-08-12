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
        }

        private static readonly Dictionary<int, PrefabClass> ClassCache = new Dictionary<int, PrefabClass>();

        /// <summary>
        /// True when this ZDO represents something a player is or could be directly driving.
        /// Callers only consult this for ZDOs that already have an owner - an unowned ship still
        /// needs somebody to simulate it.
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
                    return zdo.GetBool(ZDOVars.s_tamed, false);
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
