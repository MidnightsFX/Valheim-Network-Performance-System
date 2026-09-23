using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Reflection;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M7 - hooks the host's routed-RPC relay so broadcast RPCs go only to peers that can use
    /// them. See RoutedRpcFilter for the rules; this file is only the wiring. M19 (RelaySend)
    /// shares the same hook.
    ///
    /// Two hooks, both host-side in effect:
    ///   * ZRoutedRpc.RouteRPC - the single point every relayed RPC passes through on the
    ///     server, whether it originated here or arrived from a client for relay. The prefix
    ///     either performs the filtered relay itself and skips vanilla, or declines - and then
    ///     either M19 performs vanilla's own relay from a reused frame, or vanilla runs exactly
    ///     as before.
    ///   * ZDOMan.RPC_DestroyZDO - runs before RouteRPC on the same call stack for every destroy
    ///     batch, and is the last moment at which the peers' ZDO tables still say who holds the
    ///     ids being destroyed.
    /// </summary>
    [HarmonyPatch]
    internal static class RoutedRpcPatches {

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
                PatchGuard.Disable(Mechanism.RelaySendReuse,
                    "The routed-RPC relay hook it shares with relay filtering could not be installed. Relayed RPCs are sent as vanilla.");
                return false;
            }

            CheckRelaySendAnchors();
            return true;
        }

        /// <summary>
        /// M19 reaches past ZRpc.Invoke into the fields Invoke itself uses, and writes the frame
        /// through ZPackage's own stream and writer. Those are compiled references, so a game update
        /// that renamed one would surface as a MissingFieldException from inside the relay rather
        /// than as a patch failure. Look them up now and stand M19 down instead - the relay filter
        /// does not depend on any of them and keeps working.
        /// </summary>
        private static void CheckRelaySendAnchors() {
            FieldInfo socket = AccessTools.Field(typeof(ZRpc), "m_socket");
            FieldInfo sentPackages = AccessTools.Field(typeof(ZRpc), "m_sentPackages");
            FieldInfo sentData = AccessTools.Field(typeof(ZRpc), "m_sentData");
            FieldInfo debug = AccessTools.Field(typeof(ZRpc), "m_DEBUG");
            FieldInfo stream = AccessTools.Field(typeof(ZPackage), "m_stream");
            FieldInfo writer = AccessTools.Field(typeof(ZPackage), "m_writer");
            MethodInfo getPeer = AccessTools.Method(typeof(ZRoutedRpc), "GetPeer", new[] { typeof(long) });
            MethodInfo serialize = AccessTools.Method(typeof(ZRoutedRpc.RoutedRPCData), "Serialize", new[] { typeof(ZPackage) });

            if (socket == null || sentPackages == null || sentData == null || debug == null
                || stream == null || writer == null || getPeer == null || serialize == null) {
                PatchGuard.Disable(Mechanism.RelaySendReuse,
                    "ZRpc's socket / sent counters / debug flag, ZPackage's stream and writer, ZRoutedRpc.GetPeer(long) or RoutedRPCData.Serialize(ZPackage) " +
                    "do not have the expected shape. Either the game updated or another mod rewrote them first. Relayed RPCs are sent as vanilla.");
                return;
            }

            // Serialize is not called on the fast path, but the frame reproduces it field for field.
            // If it has grown a field the frame does not know about, the frame would be wrong, so
            // compare the IL's field reads against the six the frame writes.
            int fieldReads = CountFieldReads(serialize);
            if (fieldReads != ExpectedSerializeFieldReads) {
                PatchGuard.Disable(Mechanism.RelaySendReuse,
                    $"RoutedRPCData.Serialize reads {fieldReads} field(s), expected {ExpectedSerializeFieldReads}. The message layout has changed and the " +
                    "reused frame no longer matches it. Relayed RPCs are sent as vanilla.");
            }
        }

        /// <summary>msgID, sender, target, target ZDO, method hash, parameters.</summary>
        private const int ExpectedSerializeFieldReads = 6;

        /// <summary>-1 when the body cannot be read, which never equals the expected count.</summary>
        private static int CountFieldReads(MethodInfo method) {
            try {
                int count = 0;
                foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(method)) {
                    if (instruction.opcode == System.Reflection.Emit.OpCodes.Ldfld) { count++; }
                }
                return count;
            } catch (System.Exception) {
                return -1;
            }
        }

        [HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC")]
        [HarmonyPrefix]
        private static bool FilterRelay(ZRoutedRpc __instance, ZRoutedRpc.RoutedRPCData rpcData, bool __runOriginal) {
            // RpcOwnerRouterPatches.RouteOutgoing runs ahead of this on the same method and may
            // already have delivered the message; Harmony still runs the remaining prefixes, so
            // honour its verdict rather than relay a second copy.
            if (!__runOriginal) { return false; }
            // false -> the filtered relay already happened
            if (RoutedRpcFilter.TryRelay(__instance, rpcData)) { return false; }
            // false -> M19 relayed it to vanilla's recipients from a reused frame
            // true  -> vanilla RouteRPC runs (client side, M19 off, or stood down)
            return !RelaySend.TryRelayAsVanilla(__instance, rpcData);
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
        ///
        /// Called once from the plugin's Start, for the same reason as the scheduler's
        /// ReturnToSender check. Not from the relay prefix: either mod can skip RouteRPC or leave
        /// __runOriginal false ahead of it, and then a check there would never run. Not from
        /// Awake: BepInEx fills Chainloader.PluginInfos incrementally, so a plugin ordered after
        /// us is not visible from our Awake.
        /// </summary>
        internal static void CheckForOverlappingMods() {
            if (PatchGuard.IsPluginLoaded(PatchGuard.BetterZeeRouterGUID)) {
                PatchGuard.Disable(Mechanism.RoutedRpcFilter,
                    "BetterZeeRouter is installed and reworks the routed-RPC relay path. Standing down so there is exactly one router deciding recipients.");
                PatchGuard.Disable(Mechanism.RelaySendReuse,
                    "BetterZeeRouter is installed and reworks the routed-RPC relay path. Standing down so there is exactly one relay.");
            } else if (PatchGuard.IsPluginLoaded(PatchGuard.EnRouteGUID)) {
                PatchGuard.Disable(Mechanism.RoutedRpcFilter,
                    "EnRoute is installed and reworks the routed-RPC relay path. Standing down so there is exactly one router deciding recipients.");
                PatchGuard.Disable(Mechanism.RelaySendReuse,
                    "EnRoute is installed and reworks the routed-RPC relay path. Standing down so there is exactly one relay.");
            }
        }
    }
}
