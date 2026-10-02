using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M33 - wiring for StructureUpdates. See it for the rules. The decision itself rides on M28's
    /// CreateSyncList postfix (CreaturePacingPatches); these are the hooks around it, host only:
    ///
    ///   * ZDO.Deserialize postfix - what the host just applied changed something, or did not.
    ///     A postfix runs whether or not M15's prefix replaced the body, and on M25's applied path.
    ///   * ZDO.SetOwnerInternal prefix - who an object is being taken from. Every owner change on
    ///     the host passes through it: the arbiter's SetOwner, a player's claim in RPC_ZDOData, the
    ///     router's claim.
    ///   * ZDOMan.CreateSyncList prefix, at Priority.High so it runs ahead of M9's (Harmony runs
    ///     every prefix even when one skips the original) - force-sends and expired holds put back
    ///     before the list is built.
    ///   * ZDOMan.SendZDOs prefix and postfix - whether the last send got through its whole list.
    /// </summary>
    [HarmonyPatch]
    internal static class StructureUpdatesPatches {

        [HarmonyPrepare]
        private static bool Prepare() {
            MethodInfo deserialize = AccessTools.Method(typeof(ZDO), nameof(ZDO.Deserialize), new[] { typeof(ZPackage) });
            MethodInfo setOwner = AccessTools.Method(typeof(ZDO), nameof(ZDO.SetOwnerInternal), new[] { typeof(long) });
            MethodInfo create = AccessTools.Method(typeof(ZDOMan), "CreateSyncList", new[] { typeof(ZDOMan.ZDOPeer), typeof(List<ZDO>) });
            MethodInfo send = AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.SendZDOs), new[] { typeof(ZDOMan.ZDOPeer), typeof(bool) });
            FieldInfo zdos = AccessTools.Field(typeof(ZDOMan.ZDOPeer), "m_zdos");
            FieldInfo forceSend = AccessTools.Field(typeof(ZDOMan.ZDOPeer), "m_forceSend");
            FieldInfo sent = AccessTools.Field(typeof(ZDOMan), "m_zdosSent");
            FieldInfo tempToSync = AccessTools.Field(typeof(ZDOMan), "m_tempToSync");
            ConstructorInfo info = AccessTools.Constructor(typeof(ZDOMan.ZDOPeer.PeerZDOInfo), new[] { typeof(uint), typeof(ushort), typeof(float) });
            bool tables = AccessTools.Field(typeof(ZDOExtraData), "s_floats") != null
                && AccessTools.Field(typeof(ZDOExtraData), "s_vec3") != null
                && AccessTools.Field(typeof(ZDOExtraData), "s_quats") != null
                && AccessTools.Field(typeof(ZDOExtraData), "s_ints") != null
                && AccessTools.Field(typeof(ZDOExtraData), "s_longs") != null
                && AccessTools.Field(typeof(ZDOExtraData), "s_strings") != null
                && AccessTools.Field(typeof(ZDOExtraData), "s_byteArrays") != null;

            if (deserialize == null || setOwner == null || create == null || send == null || zdos == null
                || forceSend == null || sent == null || tempToSync == null || info == null || !tables) {
                PatchGuard.Disable(Mechanism.StructureUpdates,
                    "ZDO.Deserialize / SetOwnerInternal, ZDOMan.CreateSyncList / SendZDOs or the send records and value tables " +
                    "they use (ZDOPeer.m_zdos / m_forceSend, PeerZDOInfo, ZDOMan.m_zdosSent / m_tempToSync, ZDOExtraData's " +
                    "tables) do not have the expected shape. Either the game updated or another mod rewrote them. Buildings, " +
                    "trees and rocks are sent on every change, as vanilla.");
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize))]
        [HarmonyPostfix]
        private static void NoteApplied(ZDO __instance) {
            if (!NpsEnv.IsHost() || !StructureUpdates.Active) { return; }
            StructureUpdates.OnApplied(__instance, Time.time);
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwnerInternal))]
        [HarmonyPrefix]
        private static void NoteFormerOwner(ZDO __instance, long uid) {
            if (!NpsEnv.IsHost() || !StructureUpdates.Active) { return; }
            StructureUpdates.OnOwnerChanging(__instance, uid, Time.time);
        }

        [HarmonyPatch(typeof(ZDOMan), "CreateSyncList")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        private static void BeforeSyncList(ZDOMan.ZDOPeer peer) {
            if (peer?.m_peer == null || !NpsEnv.IsHost()) { return; }
            float now = Time.time;
            bool active = StructureUpdates.Active;
            StructureUpdates.Tick(now, active, active && StructureUpdates.OwnerChangesMayWait);
            if (!active) { return; }
            StructureUpdates.BeforeSyncList(peer, now);
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.SendZDOs))]
        [HarmonyPrefix]
        private static void SendStart(ZDOMan __instance) {
            StructureUpdates.OnSendStart(__instance);
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.SendZDOs))]
        [HarmonyPostfix]
        private static void SendEnd(ZDOMan __instance, ZDOMan.ZDOPeer peer, bool flush) {
            if (flush || !NpsEnv.IsHost()) { return; }
            StructureUpdates.OnSendEnd(__instance, peer);
        }
    }
}
