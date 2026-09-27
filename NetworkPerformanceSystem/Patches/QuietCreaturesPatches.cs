using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M27 - wiring for QuietCreatures. See it for why; this file is only the hooks.
    ///
    /// Six of the game's own ZDO writes are pointed at QuietCreatures instead, each by swapping
    /// the one call instruction for a static call that takes exactly what it took off the stack:
    ///
    ///     ZSyncTransform.OwnerSync   zDO.SetPosition(position)                    -> SetPosition
    ///                                zDO.Set(ZDOVars.s_velHash, velocity)         -> SetTransformVelocity
    ///                                GetZDO().Set(ZDOVars.s_bodyVelHash, ...)     -> SetRigidbodyVelocity
    ///     Character.SyncVelocity     GetZDO().Set(ZDOVars.s_bodyVelocity, ...)    -> SetBodyVelocity
    ///     Character.UpdateGroundTilt GetZDO().Set(ZDOVars.s_tiltrot, rotation)    -> SetTilt (both sites)
    ///
    /// Call sites rather than a prefix on ZDO.Set: that would put a test in front of every Vector3
    /// and Quaternion write in the game, with no way to know which of them it was looking at. The
    /// game's own "changed since last frame" checks around these calls stay exactly as they were,
    /// so QuietCreatures is only asked when the game was about to write anyway.
    ///
    /// Anchored on shape, not position: a ZDO call of the right name and parameter types, keyed by
    /// the ZDOVars field loaded just before it. OwnerSync writes s_velHash twice - once for the
    /// transform, once for the relative velocity of a parented object - and the transform one is
    /// the first within a few instructions of the SetPosition. The client and dedicated-server
    /// builds of the game compile all three methods identically. Anything else stands the
    /// mechanism down with what was found, and the methods are left untouched.
    /// </summary>
    [HarmonyPatch]
    internal static class QuietCreaturesPatches {

        private const string SetName = "Set";
        private const string SetPositionName = "SetPosition";

        /// <summary>How far back from a Set call its key may be loaded. Two instructions for a
        /// local value, four for a value read off a field of a field (m_body.linearVelocity).</summary>
        private const int MaxKeyDistance = 4;

        /// <summary>How far after the SetPosition the transform's s_velHash write may be. Twelve
        /// instructions in the game; the relative-velocity write is over seventy further on.</summary>
        private const int MaxVelocityAfterPosition = 16;

        private const int ExpectedTiltSites = 2;

        internal struct OwnerSyncSites {
            internal int Position;
            internal int Velocity;
            internal int RigidbodyVelocity;
        }

        /// <summary>
        /// Checks all three methods before Harmony touches any of them, so a shape change in one
        /// never leaves the other two rewritten on their own. Each transpiler still checks again:
        /// another mod's transpiler may reach the method first and change what it sees.
        /// </summary>
        [HarmonyPrepare]
        private static bool Prepare() {
            MethodInfo ownerSync = AccessTools.Method(typeof(ZSyncTransform), "OwnerSync");
            MethodInfo syncVelocity = AccessTools.Method(typeof(Character), "SyncVelocity");
            MethodInfo groundTilt = AccessTools.Method(typeof(Character), "UpdateGroundTilt");
            if (ownerSync == null || syncVelocity == null || groundTilt == null) {
                PatchGuard.Disable(Mechanism.QuietCreatures,
                    "ZSyncTransform.OwnerSync, Character.SyncVelocity or Character.UpdateGroundTilt was not found. " +
                    "Either the game updated or another mod replaced it. Idle creatures keep re-sending themselves, as vanilla.");
                return false;
            }

            string miss;
            if (!TryFindOwnerSyncSites(PatchProcessor.GetOriginalInstructions(ownerSync), out _, out miss)
                || !TryFindKeyedSets(PatchProcessor.GetOriginalInstructions(syncVelocity), typeof(Vector3), nameof(ZDOVars.s_bodyVelocity), 1, out _, out miss)
                || !TryFindKeyedSets(PatchProcessor.GetOriginalInstructions(groundTilt), typeof(Quaternion), nameof(ZDOVars.s_tiltrot), ExpectedTiltSites, out _, out miss)) {
                PatchGuard.Disable(Mechanism.QuietCreatures,
                    $"The game's creature sync methods do not have the expected shape ({miss}). Either the game updated " +
                    "or another mod rewrote them. Idle creatures keep re-sending themselves, as vanilla.");
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(ZSyncTransform), "OwnerSync")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> QuietOwnerSync(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            if (!TryFindOwnerSyncSites(codes, out OwnerSyncSites sites, out string miss)) {
                StandDown("ZSyncTransform.OwnerSync", miss, codes);
                return codes;
            }

            Replace(codes, sites.Position, nameof(QuietCreatures.SetPosition));
            Replace(codes, sites.Velocity, nameof(QuietCreatures.SetTransformVelocity));
            Replace(codes, sites.RigidbodyVelocity, nameof(QuietCreatures.SetRigidbodyVelocity));
            Logger.LogInfo("Idle creature updates: 3 sites rewritten in ZSyncTransform.OwnerSync.");
            return codes;
        }

        [HarmonyPatch(typeof(Character), "SyncVelocity")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> QuietSyncVelocity(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            if (!TryFindKeyedSets(codes, typeof(Vector3), nameof(ZDOVars.s_bodyVelocity), 1, out List<int> sites, out string miss)) {
                StandDown("Character.SyncVelocity", miss, codes);
                return codes;
            }

            Replace(codes, sites[0], nameof(QuietCreatures.SetBodyVelocity));
            Logger.LogInfo("Idle creature updates: 1 site rewritten in Character.SyncVelocity.");
            return codes;
        }

        [HarmonyPatch(typeof(Character), "UpdateGroundTilt")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> QuietGroundTilt(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            if (!TryFindKeyedSets(codes, typeof(Quaternion), nameof(ZDOVars.s_tiltrot), ExpectedTiltSites, out List<int> sites, out string miss)) {
                StandDown("Character.UpdateGroundTilt", miss, codes);
                return codes;
            }

            for (int i = 0; i < sites.Count; i++) { Replace(codes, sites[i], nameof(QuietCreatures.SetTilt)); }
            Logger.LogInfo($"Idle creature updates: {sites.Count} sites rewritten in Character.UpdateGroundTilt.");
            return codes;
        }

        private static void Replace(List<CodeInstruction> codes, int index, string method) {
            IlMatch.ReplaceInPlace(codes, index,
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(QuietCreatures), method)));
        }

        /// <summary>A method that has already been rewritten keeps its rewrite, but the helpers read
        /// PatchGuard first and pass straight through to the game's own call from here on, so the
        /// mechanism is off everywhere at once rather than half on.</summary>
        private static void StandDown(string method, string miss, List<CodeInstruction> codes) {
            PatchGuard.Disable(Mechanism.QuietCreatures,
                $"{method} does not have the expected shape ({miss}). Either the game updated or another mod rewrote this " +
                "method first. Idle creatures keep re-sending themselves, as vanilla. " +
                $"Sites: {IlMatch.DescribeNeighbours(codes, SetName, SetPositionName)}");
        }

        // -- anchors: internal so the offline harness can run them against both builds ------

        /// <summary>The three OwnerSync writes, or which one is missing.</summary>
        internal static bool TryFindOwnerSyncSites(List<CodeInstruction> codes, out OwnerSyncSites sites, out string miss) {
            sites = default;

            int position = -1;
            int positions = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (IsZdoCall(codes[i], SetPositionName, typeof(Vector3))) {
                    if (position < 0) { position = i; }
                    positions++;
                }
            }
            if (positions != 1) {
                miss = $"{positions} ZDO.SetPosition calls in OwnerSync, expected 1";
                return false;
            }

            int velocity = -1;
            for (int i = position + 1; i < codes.Count && i <= position + MaxVelocityAfterPosition; i++) {
                if (IsZdoCall(codes[i], SetName, typeof(int), typeof(Vector3)) && KeyOf(codes, i) == nameof(ZDOVars.s_velHash)) {
                    velocity = i;
                    break;
                }
            }
            if (velocity < 0) {
                miss = $"no s_velHash write within {MaxVelocityAfterPosition} instructions of OwnerSync's SetPosition";
                return false;
            }

            if (!TryFindKeyedSets(codes, typeof(Vector3), nameof(ZDOVars.s_bodyVelHash), 1, out List<int> body, out miss)) {
                return false;
            }

            sites = new OwnerSyncSites { Position = position, Velocity = velocity, RigidbodyVelocity = body[0] };
            miss = null;
            return true;
        }

        /// <summary>Every ZDO.Set(int, valueType) keyed by the named ZDOVars field, which must be
        /// exactly <paramref name="expected"/> of them.</summary>
        internal static bool TryFindKeyedSets(List<CodeInstruction> codes, System.Type valueType, string keyField, int expected,
                                              out List<int> sites, out string miss) {
            sites = new List<int>();
            for (int i = 0; i < codes.Count; i++) {
                if (IsZdoCall(codes[i], SetName, typeof(int), valueType) && KeyOf(codes, i) == keyField) { sites.Add(i); }
            }
            if (sites.Count != expected) {
                miss = $"{sites.Count} ZDO.Set({valueType.Name}) writes keyed by ZDOVars.{keyField}, expected {expected}";
                return false;
            }
            miss = null;
            return true;
        }

        /// <summary>The ZDOVars field loaded nearest before a Set call, within MaxKeyDistance, or
        /// null.</summary>
        internal static string KeyOf(List<CodeInstruction> codes, int callIndex) {
            for (int i = callIndex - 1; i >= 0 && i >= callIndex - MaxKeyDistance; i--) {
                if (codes[i].opcode == OpCodes.Ldsfld
                    && codes[i].operand is FieldInfo field
                    && field.DeclaringType == typeof(ZDOVars)) {
                    return field.Name;
                }
            }
            return null;
        }

        private static bool IsZdoCall(CodeInstruction code, string name, params System.Type[] parameters) {
            if (code.opcode != OpCodes.Callvirt && code.opcode != OpCodes.Call) { return false; }
            if (!(code.operand is MethodInfo method) || method.DeclaringType != typeof(ZDO) || method.Name != name) { return false; }
            ParameterInfo[] actual = method.GetParameters();
            if (actual.Length != parameters.Length) { return false; }
            for (int i = 0; i < actual.Length; i++) {
                if (actual[i].ParameterType != parameters[i]) { return false; }
            }
            return true;
        }
    }
}
