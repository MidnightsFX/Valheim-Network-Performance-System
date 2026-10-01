using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Reflection;
using UnityEngine;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M30 - wiring for StatusEffectRepeats. See it for why; this file is only the hooks.
    ///
    ///   * SEMan.AddStatusEffect(int, bool, int, float, short) - the overload that sends
    ///     RPC_AddStatusEffect when the caller does not own the character. Every machine. The
    ///     owner's path returns at the first test and is otherwise untouched; the other
    ///     overload, which takes the effect itself, never sends anything and is not hooked.
    ///   * ZRoutedRpc.RPC_RoutedRPC - the host's entry point for every routed RPC a player
    ///     sends. RpcOwnerRouterPatches prefixes the same method for its own messages; the two
    ///     look for different method hashes and never act on the same one.
    /// </summary>
    [HarmonyPatch]
    internal static class StatusEffectRepeatsPatches {

        [HarmonyPrepare]
        private static bool Prepare() {
            MethodInfo add = AccessTools.Method(typeof(SEMan), nameof(SEMan.AddStatusEffect),
                new[] { typeof(int), typeof(bool), typeof(int), typeof(float), typeof(short) });
            MethodInfo handler = AccessTools.Method(typeof(SEMan), "RPC_AddStatusEffect",
                new[] { typeof(long), typeof(int), typeof(bool), typeof(int), typeof(float), typeof(int) });
            FieldInfo view = AccessTools.Field(typeof(SEMan), "m_nview");
            MethodInfo incoming = AccessTools.Method(typeof(ZRoutedRpc), "RPC_RoutedRPC", new[] { typeof(ZRpc), typeof(ZPackage) });

            if (add == null || handler == null || view == null || incoming == null) {
                PatchGuard.Disable(Mechanism.StatusEffectRepeats,
                    "SEMan.AddStatusEffect(int, bool, int, float, short) / RPC_AddStatusEffect, SEMan.m_nview or ZRoutedRpc.RPC_RoutedRPC do not have " +
                    "the expected shape. Either the game updated or another mod rewrote them. A status effect asked for on somebody else's creature is " +
                    "sent every time it is asked for, as vanilla.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// true  -> vanilla runs: this machine owns the character, or the request should go out
        /// false -> a repeat of the request just sent; vanilla's non-owner path returns null too
        /// </summary>
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.AddStatusEffect), typeof(int), typeof(bool), typeof(int), typeof(float), typeof(short))]
        [HarmonyPrefix]
        private static bool HoldRepeats(SEMan __instance, int nameHash, bool resetTime, int itemLevel, float skillLevel, short variant, ref StatusEffect __result) {
            if (nameHash == 0 || !StatusEffectRepeats.Hooked) { return true; }

            ZNetView view = __instance.m_nview;
            if (view == null || !view.IsValid() || view.IsOwner()) { return true; }

            if (StatusEffectRepeats.AllowLocal(view.GetZDO().m_uid, nameHash, resetTime, itemLevel, skillLevel, variant, Time.time)) { return true; }

            __result = null;
            return false;
        }

        [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
        [HarmonyPrefix]
        private static bool DropRepeats(ZRoutedRpc __instance, ZPackage pkg) {
            // Cheapest test first: this runs for every routed RPC a client sends.
            if (!__instance.m_server) { return true; }
            return !StatusEffectRepeats.ShouldDropIncoming(pkg);
        }
    }
}
