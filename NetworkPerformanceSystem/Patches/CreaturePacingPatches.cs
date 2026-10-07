using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M28 - wiring for CreaturePacing. See it for the rules.
    ///
    /// One postfix on ZDOMan.CreateSyncList. Harmony runs postfixes whether or not a prefix
    /// skipped the original, so this sees the finished list both when M9 built it from the cached
    /// scan and when vanilla did - and by then AddForceSendZdos has already put the forced ZDOs at
    /// the head, still marked in the peer's force-send set, which is how they are recognised and
    /// kept. The host branch only: the same IsServer() test vanilla branches on.
    ///
    /// It also carries M31's host half (QuietWildlife) and M33 (StructureUpdates): fish and birds,
    /// floating objects, and buildings, trees and rocks, are filtered by the same pass, so the
    /// postfix runs when any of the four settings is on and each obeys its own.
    /// </summary>
    [HarmonyPatch]
    internal static class CreaturePacingPatches {

        [HarmonyPrepare]
        private static bool Prepare() {
            MethodInfo create = AccessTools.Method(typeof(ZDOMan), "CreateSyncList",
                new[] { typeof(ZDOMan.ZDOPeer), typeof(List<ZDO>) });
            FieldInfo zdos = AccessTools.Field(typeof(ZDOMan.ZDOPeer), "m_zdos");
            FieldInfo forceSend = AccessTools.Field(typeof(ZDOMan.ZDOPeer), "m_forceSend");
            FieldInfo netPeer = AccessTools.Field(typeof(ZDOMan.ZDOPeer), "m_peer");
            FieldInfo syncTime = AccessTools.Field(typeof(ZDOMan.ZDOPeer.PeerZDOInfo), "m_syncTime");
            FieldInfo ownerRevision = AccessTools.Field(typeof(ZDOMan.ZDOPeer.PeerZDOInfo), "m_ownerRevision");

            if (create == null || zdos == null || forceSend == null || netPeer == null || syncTime == null || ownerRevision == null) {
                PatchGuard.Disable(Mechanism.CreaturePacing,
                    "ZDOMan.CreateSyncList or the per-peer send records it relies on (ZDOPeer.m_zdos / m_forceSend / m_peer, " +
                    "PeerZDOInfo.m_syncTime / m_ownerRevision) do not have the expected shape. Either the game updated or another " +
                    "mod rewrote them. Creatures are sent on every update, as vanilla.");
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(ZDOMan), "CreateSyncList")]
        [HarmonyPostfix]
        private static void PaceCreatures(ZDOMan.ZDOPeer peer, List<ZDO> toSync) {
            if (toSync == null || toSync.Count == 0 || peer?.m_peer == null) { return; }
            bool creatures = CreaturePacing.Active;
            bool wildlife = QuietWildlife.RelayActive;
            bool floating = QuietWildlife.FloatingRelayActive;
            bool structures = StructureUpdates.Active;
            if (!creatures && !wildlife && !floating && !structures) { return; }
            if (ZNet.instance == null || !ZNet.instance.IsServer()) { return; }

            CreaturePacing.Filter(peer, toSync, peer.m_peer.GetRefPos(), Time.time, creatures, wildlife, floating,
                                  structures, structures && StructureUpdates.OwnerChangesMayWait);
        }
    }
}
