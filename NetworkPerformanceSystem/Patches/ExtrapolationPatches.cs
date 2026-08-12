using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M4 - redirects ZSyncTransform's own extrapolation multiply through a latency-aware one.
    ///
    /// SyncPosition contains two `velocity * m_targetPosTimer` sites: one on the parented path
    /// (a player standing on a moving ship, in the parent's local space) and one on the main
    /// world-space path. Both have the same uncorrected transit offset, so both are rewritten.
    ///
    /// The anchor is an `ldfld m_targetPosTimer` immediately followed by
    /// Vector3.op_Multiply(Vector3, float). The field is also loaded twice more for the
    /// `Mathf.Min(timer, 2f)` clamps, which is why the adjacency test matters - matching the
    /// field load alone would hit four sites and rewrite the wrong two.
    /// </summary>
    [HarmonyPatch]
    internal static class ExtrapolationPatches {

        private const int ExpectedSites = 2;

        [HarmonyPatch(typeof(ZSyncTransform), "SyncPosition")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> CompensateForPathLatency(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);

            FieldInfo timerField = AccessTools.Field(typeof(ZSyncTransform), "m_targetPosTimer");
            MethodInfo vectorMultiply = AccessTools.Method(
                typeof(Vector3), "op_Multiply", new[] { typeof(Vector3), typeof(float) });
            MethodInfo offset = AccessTools.Method(typeof(NpsExtrapolate), nameof(NpsExtrapolate.Offset));

            if (timerField == null || vectorMultiply == null) {
                PatchGuard.Disable(Mechanism.Extrapolation,
                    "Could not resolve ZSyncTransform.m_targetPosTimer or Vector3.op_Multiply. Rendering stays at vanilla.");
                return codes;
            }

            List<int> sites = FindMultiplySites(codes, timerField, vectorMultiply);
            if (sites.Count != ExpectedSites) {
                PatchGuard.Disable(Mechanism.Extrapolation,
                    $"ZSyncTransform.SyncPosition has {sites.Count} extrapolation sites, expected {ExpectedSites}. " +
                    "Either the game updated or another mod rewrote this method first. Rendering stays at vanilla.");
                return codes;
            }

            // Walk backwards so earlier indices stay valid as we insert.
            for (int i = sites.Count - 1; i >= 0; i--) {
                int callIndex = sites[i];

                // Stack here is [velocity, rawTimer]; NpsExtrapolate.Offset also wants the ZDO,
                // which is SyncPosition's first parameter.
                //
                // Labels and exception-block markers have to migrate to the first instruction we
                // emit, or a branch to this offset would land between our argument push and the
                // call.
                CodeInstruction loadZdo = new CodeInstruction(OpCodes.Ldarg_1);
                loadZdo.labels.AddRange(codes[callIndex].labels);
                loadZdo.blocks.AddRange(codes[callIndex].blocks);

                codes[callIndex] = new CodeInstruction(OpCodes.Call, offset);
                codes.Insert(callIndex, loadZdo);
            }

            Logger.LogInfo($"Latency compensation active ({sites.Count} extrapolation sites rewritten in ZSyncTransform.SyncPosition).");
            return codes;
        }

        private static List<int> FindMultiplySites(List<CodeInstruction> codes, FieldInfo timerField, MethodInfo vectorMultiply) {
            List<int> sites = new List<int>();

            for (int i = 0; i < codes.Count - 1; i++) {
                if (codes[i].opcode != OpCodes.Ldfld) { continue; }
                if (!SameField(codes[i].operand as FieldInfo, timerField)) { continue; }

                CodeInstruction next = codes[i + 1];
                if (next.opcode != OpCodes.Call) { continue; }
                if (!SameMethod(next.operand as MethodInfo, vectorMultiply)) { continue; }

                sites.Add(i + 1);
            }

            return sites;
        }

        // Reflection objects are not guaranteed to be reference-equal across separate lookups, and
        // MemberInfo's == can be unreliable under Mono. Compare identity by name and declaring
        // type instead: getting this wrong would silently find zero sites and disable the
        // mechanism with a misleading "the game must have updated" message.

        private static bool SameField(FieldInfo a, FieldInfo b) {
            return a != null && b != null
                   && a.Name == b.Name
                   && a.DeclaringType == b.DeclaringType;
        }

        private static bool SameMethod(MethodInfo a, MethodInfo b) {
            return a != null && b != null
                   && a.Name == b.Name
                   && a.DeclaringType == b.DeclaringType
                   && a.GetParameters().Length == b.GetParameters().Length;
        }
    }
}
