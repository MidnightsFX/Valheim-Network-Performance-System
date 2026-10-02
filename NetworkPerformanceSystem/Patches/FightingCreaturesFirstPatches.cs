using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Collections.Generic;
using System.Reflection;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M29 - wiring for FightingCreaturesFirst. See it for why.
    ///
    /// One postfix on ZDOMan.CreateSyncList, on the client branch: the same IsServer() test
    /// vanilla branches on. By the time it runs, ClientSortSendZDOS has ordered the list and
    /// AddForceSendZdos has put the forced ZDOs at its head, still marked in the peer's force-send
    /// set, which is how they are recognised and left where they are.
    /// </summary>
    [HarmonyPatch]
    internal static class FightingCreaturesFirstPatches {

        [HarmonyPrepare]
        private static bool Prepare() {
            MethodInfo create = AccessTools.Method(typeof(ZDOMan), "CreateSyncList",
                new[] { typeof(ZDOMan.ZDOPeer), typeof(List<ZDO>) });
            FieldInfo forceSend = AccessTools.Field(typeof(ZDOMan.ZDOPeer), "m_forceSend");

            if (create == null || forceSend == null) {
                PatchGuard.Disable(Mechanism.FightingCreaturesFirst,
                    "ZDOMan.CreateSyncList or ZDOPeer.m_forceSend does not have the expected shape. Either the game updated " +
                    "or another mod rewrote them. Creatures a player is fighting queue behind their other changes, as vanilla.");
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(ZDOMan), "CreateSyncList")]
        [HarmonyPostfix]
        private static void SendFightingCreaturesFirst(ZDOMan.ZDOPeer peer, List<ZDO> toSync) {
            if (toSync == null || toSync.Count < 2) { return; }
            if (!FightingCreaturesFirst.Active) { return; }
            if (ZNet.instance == null || ZNet.instance.IsServer()) { return; }

            FightingCreaturesFirst.Reorder(peer, toSync);
        }
    }
}
