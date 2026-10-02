using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Reflection;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M24 - wiring for ZdoDataGuard, which holds world updates a client receives before it can
    /// accept them. See the guard for why; this file is only the hooks. Both are client-side:
    ///   * ZNet.OnNewConnection (postfix) - the connection has just opened. Our handler takes the
    ///     "ZDOData" slot until the game's own arrives. NetworkChannelPatches.RegisterChannels
    ///     postfixes the same method for the mod's own channels; the two are independent.
    ///   * ZDOMan.AddPeer (postfix) - the game's handler is registered now, having replaced ours;
    ///     whatever was held is handed to it.
    /// Cleanup rides on NetworkChannelPatches' RemovePeer and StopAll hooks. ZNet.Disconnect
    /// reaches RemovePeer whether or not AddPeer ever ran.
    /// </summary>
    [HarmonyPatch]
    internal static class ZdoDataGuardPatches {

        private const string ZdoDataRpc = "ZDOData";

        [HarmonyPrepare]
        private static bool Prepare() {
            MethodInfo zdoData = AccessTools.Method(typeof(ZDOMan), "RPC_ZDOData", new[] { typeof(ZRpc), typeof(ZPackage) });
            MethodInfo addPeer = AccessTools.Method(typeof(ZDOMan), "AddPeer", new[] { typeof(ZNetPeer) });
            MethodInfo newConnection = AccessTools.Method(typeof(ZNet), "OnNewConnection", new[] { typeof(ZNetPeer) });

            if (zdoData == null || addPeer == null || newConnection == null) {
                PatchGuard.Disable(Mechanism.EarlyZdoData,
                    "ZDOMan.RPC_ZDOData / AddPeer or ZNet.OnNewConnection do not have the expected shape. " +
                    "Either the game updated or another mod rewrote them first. World updates that arrive during the handshake are dropped, as vanilla.");
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        [HarmonyPostfix]
        private static void HoldEarlyZdoData(ZNet __instance, ZNetPeer peer) {
            if (__instance == null || __instance.IsServer()) { return; }       // the server's gap cannot open; see the guard
            if (peer?.m_rpc == null || !ZdoDataGuard.Wanted) { return; }

            peer.m_rpc.Register<ZPackage>(ZdoDataRpc, ZdoDataGuard.RPC_HoldZDOData);
            ZdoDataGuard.Arm(peer.m_rpc);
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.AddPeer))]
        [HarmonyPostfix]
        private static void ReplayHeld(ZDOMan __instance, ZNetPeer netPeer) {
            ZdoDataGuard.Replay(__instance, netPeer);
        }
    }
}
