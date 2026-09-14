using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Reflection;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M12 - wiring for StationRpcRouter, which delivers station item requests to the object's
    /// current owner. See the router for the rules; this file is only the hooks.
    ///
    /// Three hooks, all host-side in effect:
    ///   * ZRoutedRpc.RPC_RoutedRPC - every routed RPC a client sends arrives here. The prefix
    ///     peeks at the header without allocating, and only for a station request does it
    ///     deserialize and take over; anything else is rewound and left to vanilla.
    ///   * ZRoutedRpc.RouteRPC - where the host's own sends go. Relayed client RPCs also pass
    ///     through it in vanilla, but a station request never gets that far because the prefix
    ///     above has already taken it, so this one only ever acts on what the host itself sent.
    ///     It runs ahead of the relay filter's prefix on the same method and both honour
    ///     __runOriginal, so a request is delivered by exactly one of them.
    ///   * ZDOMan.Update (postfix) - flushes held forwards after that frame's ZDO sends.
    /// </summary>
    [HarmonyPatch]
    internal static class StationRpcPatches {

        private static bool _checkedForOverlappingMods;

        /// <summary>
        /// Verify the anchors before Harmony touches anything. A game update that renames or
        /// reshapes any of them disables this mechanism loudly and leaves routing as vanilla.
        /// </summary>
        [HarmonyPrepare]
        private static bool Prepare() {
            MethodInfo incoming = AccessTools.Method(typeof(ZRoutedRpc), "RPC_RoutedRPC", new[] { typeof(ZRpc), typeof(ZPackage) });
            MethodInfo route = AccessTools.Method(typeof(ZRoutedRpc), "RouteRPC", new[] { typeof(ZRoutedRpc.RoutedRPCData) });
            MethodInfo handle = AccessTools.Method(typeof(ZRoutedRpc), "HandleRoutedRPC", new[] { typeof(ZRoutedRpc.RoutedRPCData) });
            MethodInfo update = AccessTools.Method(typeof(ZDOMan), "Update", new[] { typeof(float) });
            MethodInfo getPeer = AccessTools.Method(typeof(ZDOMan), "GetPeer", new[] { typeof(long) });
            FieldInfo server = AccessTools.Field(typeof(ZRoutedRpc), "m_server");
            FieldInfo id = AccessTools.Field(typeof(ZRoutedRpc), "m_id");

            if (incoming == null || route == null || handle == null || update == null || getPeer == null || server == null || id == null) {
                PatchGuard.Disable(Mechanism.StationRpcRouting,
                    "ZRoutedRpc.RPC_RoutedRPC / RouteRPC / HandleRoutedRPC, ZDOMan.Update / GetPeer or ZRoutedRpc's id fields do not have the expected shape. " +
                    "Either the game updated or another mod rewrote them first. Station requests go to whoever the sender names, as vanilla.");
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
        [HarmonyPrefix]
        private static bool RouteIncoming(ZRoutedRpc __instance, ZPackage pkg) {
            // Cheapest test first: this runs for every routed RPC a client sends.
            if (!__instance.m_server) { return true; }
            EnsureOverlappingModCheck();
            // true  -> vanilla RPC_RoutedRPC runs on the untouched package
            // false -> the request was delivered (or parked) by the router
            return !StationRpcRouter.TryRouteIncoming(__instance, pkg);
        }

        [HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        private static bool RouteOutgoing(ZRoutedRpc __instance, ZRoutedRpc.RoutedRPCData rpcData, bool __runOriginal) {
            if (!__runOriginal) { return false; }                       // another prefix already delivered it
            if (!__instance.m_server || rpcData == null || rpcData.m_senderPeerID != __instance.m_id) { return true; }
            EnsureOverlappingModCheck();
            return !StationRpcRouter.TryRoute(__instance, rpcData);
        }

        [HarmonyPatch(typeof(ZDOMan), "Update")]
        [HarmonyPostfix]
        private static void FlushHeld() {
            StationRpcRouter.FlushHeld();
        }

        /// <summary>
        /// BetterZeeRouter and EnRoute both replace ZRoutedRpc's receive-and-relay path. Two
        /// routers deciding where a message goes is the race this mod's own ownership code warns
        /// about, so stand down and say so. Checked lazily on first use for the same reason as
        /// the relay filter: BepInEx fills Chainloader.PluginInfos incrementally, so a plugin
        /// ordered after us is not visible from our Awake.
        /// </summary>
        private static void EnsureOverlappingModCheck() {
            if (_checkedForOverlappingMods) { return; }
            _checkedForOverlappingMods = true;

            if (PatchGuard.IsPluginLoaded(PatchGuard.BetterZeeRouterGUID)) {
                PatchGuard.Disable(Mechanism.StationRpcRouting,
                    "BetterZeeRouter is installed and reworks the routed-RPC path. Standing down so there is exactly one router deciding recipients.");
            } else if (PatchGuard.IsPluginLoaded(PatchGuard.EnRouteGUID)) {
                PatchGuard.Disable(Mechanism.StationRpcRouting,
                    "EnRoute is installed and reworks the routed-RPC path. Standing down so there is exactly one router deciding recipients.");
            }
        }
    }
}
