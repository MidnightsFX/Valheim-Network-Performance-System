using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Reflection;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M23 - wiring for LiveRefPos, which has the host follow each player's character instead of
    /// the position their game reports every 2 seconds. See LiveRefPos for the rules; this file is
    /// only the hooks. Both are host-side:
    ///   * ZDOMan.Update (prefix) - the per-frame refresh. ZNet.Update runs UpdatePeers, then
    ///     SendPeriodicData, then ZDOMan.Update, so this lands after the frame's reports and object
    ///     updates have been applied and before ReleaseZDOS and the sends read the result.
    ///     RpcOwnerRouterPatches' postfix on the same method is unaffected, and ReturnToSender's
    ///     transpiler rewrites the body, which a prefix does not care about.
    ///   * ZNet.RPC_ServerSyncedPlayerData (postfix) - vanilla's 2 second report has just been
    ///     written into m_refPos. Recorded and replaced immediately, because SendPlayerList reads
    ///     it later in the same ZNet.Update, before the refresh above runs.
    /// </summary>
    [HarmonyPatch]
    internal static class LiveRefPosPatches {

        [HarmonyPrepare]
        private static bool Prepare() {
            MethodInfo update = AccessTools.Method(typeof(ZDOMan), "Update", new[] { typeof(float) });
            MethodInfo synced = AccessTools.Method(typeof(ZNet), "RPC_ServerSyncedPlayerData", new[] { typeof(ZRpc), typeof(ZPackage) });
            MethodInfo getZdo = AccessTools.Method(typeof(ZDOMan), "GetZDO", new[] { typeof(ZDOID) });
            FieldInfo characterId = AccessTools.Field(typeof(ZNetPeer), "m_characterID");
            FieldInfo refPos = AccessTools.Field(typeof(ZNetPeer), "m_refPos");

            if (update == null || synced == null || getZdo == null || characterId == null || refPos == null) {
                PatchGuard.Disable(Mechanism.LiveRefPos,
                    "ZDOMan.Update / GetZDO, ZNet.RPC_ServerSyncedPlayerData or ZNetPeer's m_characterID / m_refPos do not have the expected shape. " +
                    "Either the game updated or another mod rewrote them first. Player positions are taken from their reports, as vanilla.");
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(ZDOMan), "Update")]
        [HarmonyPrefix]
        private static void RefreshReferencePositions() {
            // Runs whatever the setting says, so that switching it off hands every peer its own
            // report back on the next frame rather than leaving the last live position frozen.
            if (!NpsEnv.IsHost()) { return; }
            LiveRefPos.Refresh();
        }

        [HarmonyPatch(typeof(ZNet), "RPC_ServerSyncedPlayerData")]
        [HarmonyPostfix]
        private static void CaptureReport(ZRpc rpc) {
            if (!NpsEnv.IsHost()) { return; }
            ZNetPeer peer = NetworkChannelPatches.FindPeerByRpc(rpc);
            if (peer != null) { LiveRefPos.NoteReported(peer); }
        }
    }
}
