using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Reflection;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M7 - hooks the host's routed-RPC relay so broadcast RPCs go only to peers that can use
    /// them. See RoutedRpcFilter for the rules; this file is only the wiring.
    ///
    /// Two hooks, both host-side in effect:
    ///   * ZRoutedRpc.RouteRPC - the single point every Everybody RPC passes through on the
    ///     server, whether it originated here or arrived from a client for relay. The prefix
    ///     either performs the filtered relay itself and skips vanilla, or declines and lets
    ///     vanilla broadcast exactly as before.
    ///   * ZDOMan.RPC_DestroyZDO - runs before RouteRPC on the same call stack for every destroy
    ///     batch, and is the last moment at which the peers' ZDO tables still say who holds the
    ///     ids being destroyed.
    /// </summary>
    [HarmonyPatch]
    internal static class RoutedRpcPatches {

        private static bool _checkedForOverlappingMods;

        /// <summary>
        /// Verify the anchors before Harmony touches anything. A game update that renames or
        /// reshapes either method disables this mechanism loudly and leaves the rest of the
        /// plugin - and vanilla's relay - exactly as they were.
        /// </summary>
        [HarmonyPrepare]
        private static bool Prepare() {
            MethodInfo route = AccessTools.Method(typeof(ZRoutedRpc), "RouteRPC", new[] { typeof(ZRoutedRpc.RoutedRPCData) });
            MethodInfo destroy = AccessTools.Method(typeof(ZDOMan), "RPC_DestroyZDO", new[] { typeof(long), typeof(ZPackage) });
            FieldInfo server = AccessTools.Field(typeof(ZRoutedRpc), "m_server");
            FieldInfo peers = AccessTools.Field(typeof(ZRoutedRpc), "m_peers");

            if (route == null || destroy == null || server == null || peers == null) {
                PatchGuard.Disable(Mechanism.RoutedRpcFilter,
                    "ZRoutedRpc.RouteRPC(RoutedRPCData) / ZDOMan.RPC_DestroyZDO(long, ZPackage) or ZRoutedRpc's peer fields do not have the expected shape. " +
                    "Either the game updated or another mod rewrote them first. Broadcast RPCs are relayed to everyone, as vanilla.");
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC")]
        [HarmonyPrefix]
        private static bool FilterRelay(ZRoutedRpc __instance, ZRoutedRpc.RoutedRPCData rpcData, bool __runOriginal) {
            // StationRpcPatches.RouteOutgoing runs ahead of this on the same method and may
            // already have delivered the message; Harmony still runs the remaining prefixes, so
            // honour its verdict rather than relay a second copy.
            if (!__runOriginal) { return false; }
            EnsureOverlappingModCheck();
            // true  -> vanilla RouteRPC runs (targeted, client side, global, unknown, or stood down)
            // false -> the filtered relay already happened
            return !RoutedRpcFilter.TryRelay(__instance, rpcData);
        }

        [HarmonyPatch(typeof(ZDOMan), "RPC_DestroyZDO")]
        [HarmonyPrefix]
        private static void SnapshotDestroyHolders(ZDOMan __instance, ZPackage pkg) {
            RoutedRpcFilter.SnapshotDestroyHolders(__instance, pkg);
        }

        /// <summary>
        /// BetterZeeRouter and EnRoute both rework ZRoutedRpc's relay. Whatever their exact
        /// policy, two systems deciding who receives a routed RPC is the race this mod's own
        /// ownership code warns about, so stand down and say so rather than layer on top.
        /// Checked lazily on first relay for the same reason as the scheduler's ReturnToSender
        /// check: BepInEx fills Chainloader.PluginInfos incrementally, so a plugin ordered after
        /// us is not visible from our Awake.
        /// </summary>
        private static void EnsureOverlappingModCheck() {
            if (_checkedForOverlappingMods) { return; }
            _checkedForOverlappingMods = true;

            if (PatchGuard.IsPluginLoaded(PatchGuard.BetterZeeRouterGUID)) {
                PatchGuard.Disable(Mechanism.RoutedRpcFilter,
                    "BetterZeeRouter is installed and reworks the routed-RPC relay path. Standing down so there is exactly one router deciding recipients.");
            } else if (PatchGuard.IsPluginLoaded(PatchGuard.EnRouteGUID)) {
                PatchGuard.Disable(Mechanism.RoutedRpcFilter,
                    "EnRoute is installed and reworks the routed-RPC relay path. Standing down so there is exactly one router deciding recipients.");
            }
        }
    }
}
