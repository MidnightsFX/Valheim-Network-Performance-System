using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Reflection;
using UnityEngine;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M31 - the owner half of QuietWildlife. See it for why; this file is only the hook. The host
    /// half is CreaturePacingPatches' postfix.
    ///
    /// A prefix on ZSyncTransform.OwnerSync that skips the whole method on a frame a fish or bird
    /// this machine owns is not due. It sits alongside M27's transpiler on the same method: the
    /// prefix runs first, and M27 never applies to these anyway.
    ///
    /// It runs for every ZSyncTransform in the scene every frame, so the tests are ordered by
    /// cost, cheapest first, and almost everything leaves at the first or the fourth:
    ///   1. m_wasOwner      - a field. False on the frame an object becomes ours, which must run
    ///                        (OwnerSync snaps the transform to the ZDO then), and on anything
    ///                        somebody else owns.
    ///   2-3. view and ZDO  - the ZNetView by reference, without Unity's overloaded compare.
    ///   4. KindOf          - one array slot for nearly every prefab.
    ///   5-6. setting, ZDO.IsOwner - the latter so a just-lost object runs and clears m_wasOwner.
    ///   7. HoldThisFrame   - the schedule, and the hooked-fish test only when not due.
    /// </summary>
    [HarmonyPatch]
    internal static class QuietWildlifePatches {

        [HarmonyPrepare]
        private static bool Prepare() {
            MethodInfo ownerSync = AccessTools.Method(typeof(ZSyncTransform), "OwnerSync");
            FieldInfo wasOwner = AccessTools.Field(typeof(ZSyncTransform), "m_wasOwner");
            FieldInfo nview = AccessTools.Field(typeof(ZSyncTransform), "m_nview");
            if (ownerSync == null || wasOwner == null || nview == null) {
                PatchGuard.Disable(Mechanism.QuietWildlife,
                    "ZSyncTransform.OwnerSync or its m_wasOwner / m_nview fields were not found. Either the game updated or " +
                    "another mod replaced them. Fish and birds keep sending themselves on every frame, as vanilla.");
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(ZSyncTransform), "OwnerSync")]
        [HarmonyPrefix]
        private static bool CapWildlife(ZSyncTransform __instance) {
            if (!__instance.m_wasOwner) { return true; }
            ZNetView view = __instance.m_nview;
            if ((object)view == null) { return true; }
            ZDO zdo = view.GetZDO();
            if (zdo == null) { return true; }

            QuietWildlife.Kind kind = QuietWildlife.KindOf(zdo);
            if (kind == QuietWildlife.Kind.None) { return true; }
            if (!QuietWildlife.OwnerActive || !zdo.IsOwner()) { return true; }

            return !QuietWildlife.HoldThisFrame(zdo, kind, Time.timeAsDouble, Time.deltaTime);
        }
    }
}
