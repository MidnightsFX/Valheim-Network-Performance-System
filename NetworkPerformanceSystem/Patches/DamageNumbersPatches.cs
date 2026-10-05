using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System;
using System.Reflection;
using UnityEngine;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M36 - the sender's half of Routed RPC / Damage Numbers (see DamageNumbers). The server's
    /// half rides on the relay hook in RoutedRpcPatches.
    ///
    ///   * DamageText.ShowText(TextType, Vector3, string, bool) - the one overload every damage
    ///     number goes through before it is broadcast. With Everyone Nearby it runs untouched.
    ///   * ZRoutedRpc.HandleRoutedRPC - remembers whose routed message this machine is handling,
    ///     which is the attacker when the message is a hit. A finalizer rather than a postfix, so a
    ///     handler that throws cannot leave a stale sender behind.
    /// </summary>
    [HarmonyPatch]
    internal static class DamageNumbersPatches {

        [HarmonyPrepare]
        private static bool Prepare() {
            MethodInfo show = AccessTools.Method(typeof(DamageText), "ShowText",
                new[] { typeof(DamageText.TextType), typeof(Vector3), typeof(string), typeof(bool) });
            MethodInfo local = AccessTools.Method(typeof(DamageText), "RPC_DamageText", new[] { typeof(long), typeof(ZPackage) });
            MethodInfo handle = AccessTools.Method(typeof(ZRoutedRpc), "HandleRoutedRPC", new[] { typeof(ZRoutedRpc.RoutedRPCData) });

            if (show == null || local == null || handle == null) {
                PatchGuard.Disable(Mechanism.DamageNumbers,
                    "DamageText.ShowText(TextType, Vector3, string, bool) / RPC_DamageText(long, ZPackage) or ZRoutedRpc.HandleRoutedRPC(RoutedRPCData) " +
                    "do not have the expected shape. Either the game updated or another mod rewrote them first. Damage numbers are sent as the game sends them.");
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(DamageText), "ShowText", new[] { typeof(DamageText.TextType), typeof(Vector3), typeof(string), typeof(bool) })]
        [HarmonyPrefix]
        private static bool RouteNumber(DamageText __instance, DamageText.TextType type, Vector3 pos, string text, bool player) {
            try {
                return DamageNumbers.OnShowText(__instance, type, pos, text, player);
            } catch (Exception e) {
                PatchGuard.Disable(Mechanism.DamageNumbers, $"routing a damage number failed ({e.GetType().Name}: {e.Message}). Damage numbers are sent as the game sends them.");
                return true;
            }
        }

        [HarmonyPatch(typeof(ZRoutedRpc), "HandleRoutedRPC")]
        [HarmonyPrefix]
        private static void EnterHandler(ZRoutedRpc.RoutedRPCData data) {
            DamageNumbers.EnterRouted(data != null ? data.m_senderPeerID : 0L);
        }

        [HarmonyPatch(typeof(ZRoutedRpc), "HandleRoutedRPC")]
        [HarmonyFinalizer]
        private static Exception ExitHandler(Exception __exception) {
            DamageNumbers.ExitRouted();
            return __exception;
        }
    }
}
