using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Collections.Generic;
using System.Reflection;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M9 - wiring for the per-peer sector scan cache. See SyncListCache for the rules.
    ///
    /// Three hooks:
    ///   * ZDOMan.CreateSyncList - the prefix either builds the sync list itself from the cached
    ///     scan and skips vanilla, or declines and lets vanilla run untouched.
    ///   * ZDOMan.HandleDestroyedZDO - the single steady-state site that returns a ZDO to the
    ///     pool, and therefore the moment a cached reference can start meaning something else.
    ///   * ZDOMan.LoadChunks and ZDOMan.Load - the current and legacy world loads, both of which
    ///     bulk-release every ZDO in the world. Nothing cached survives either.
    ///
    /// ZDOMan.ShutDown also bulk-releases, and is covered by the Reset on ZNet.StopAll.
    /// </summary>
    [HarmonyPatch]
    internal static class SyncListCachePatches {

        /// <summary>
        /// Verify every method the replacement calls into before Harmony touches anything. Unlike
        /// a transpiler there is no IL shape to assert here, but the prefix reimplements vanilla's
        /// server branch, so it must not run at all if any piece of that branch has moved: the
        /// failure mode would be peers silently receiving a wrong or empty sync list.
        /// </summary>
        [HarmonyPrepare]
        private static bool Prepare() {
            MethodInfo create = AccessTools.Method(typeof(ZDOMan), "CreateSyncList",
                new[] { typeof(ZDOMan.ZDOPeer), typeof(List<ZDO>) });
            MethodInfo find = AccessTools.Method(typeof(ZDOMan), "FindSectorObjects",
                new[] { typeof(Vector2s), typeof(SimulationDistance), typeof(List<ZDO>), typeof(List<ZDO>) });
            MethodInfo sort = AccessTools.Method(typeof(ZDOMan), "ServerSortSendZDOS",
                new[] { typeof(List<ZDO>), typeof(UnityEngine.Vector3), typeof(ZDOMan.ZDOPeer) });
            MethodInfo force = AccessTools.Method(typeof(ZDOMan), "AddForceSendZdos",
                new[] { typeof(ZDOMan.ZDOPeer), typeof(List<ZDO>) });
            MethodInfo shouldSend = AccessTools.Method(typeof(ZDOMan.ZDOPeer), "ShouldSend", new[] { typeof(ZDO) });
            MethodInfo destroyed = AccessTools.Method(typeof(ZDOMan), "HandleDestroyedZDO", new[] { typeof(ZDOID) });
            MethodInfo isValid = AccessTools.Method(typeof(ZDO), "IsValid");

            if (create == null || find == null || sort == null || force == null ||
                shouldSend == null || destroyed == null || isValid == null) {
                PatchGuard.Disable(Mechanism.SyncListCache,
                    "ZDOMan.CreateSyncList and the methods its server branch is built from do not have the expected shape " +
                    "(CreateSyncList / FindSectorObjects / ServerSortSendZDOS / AddForceSendZdos / ZDOPeer.ShouldSend / " +
                    "HandleDestroyedZDO / ZDO.IsValid). Either the game updated or another mod rewrote them first. " +
                    "The sector scan runs on every send, as vanilla.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// true  -> vanilla CreateSyncList runs (client branch, disabled, or stood down)
        /// false -> the sync list has already been built from the cached scan
        /// </summary>
        [HarmonyPatch(typeof(ZDOMan), "CreateSyncList")]
        [HarmonyPrefix]
        private static bool BuildFromCachedScan(ZDOMan __instance, ZDOMan.ZDOPeer peer, List<ZDO> toSync) {
            return !SyncListCache.TryBuildSyncList(__instance, peer, toSync);
        }

        /// <summary>
        /// The ZDO reaching this method is about to be handed back to ZDOPool, where a later
        /// Get() can hand the same instance out as an unrelated object. Postfix rather than
        /// prefix so the generation only moves once the release has actually happened.
        /// </summary>
        [HarmonyPatch(typeof(ZDOMan), "HandleDestroyedZDO")]
        [HarmonyPostfix]
        private static void OnZdoDestroyed() {
            SyncListCache.OnZdoDestroyed();
        }

        /// <summary>
        /// The legacy world load. Still reached, but only when migrating a pre-chunk save.
        /// </summary>
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.Load))]
        [HarmonyPostfix]
        private static void OnOldWorldLoaded() {
            SyncListCache.InvalidateAll();
        }

        /// <summary>
        /// The current world load. ZNet.LoadWorld calls this and only falls back to Load for an
        /// old-format save, so hooking Load alone would leave the cache holding references to
        /// ZDOs from the previous world on every normal startup - the exact aliasing the
        /// generation counter exists to prevent.
        /// </summary>
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.LoadChunks))]
        [HarmonyPostfix]
        private static void OnWorldLoaded() {
            SyncListCache.InvalidateAll();
        }
    }
}
