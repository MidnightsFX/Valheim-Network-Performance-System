using HarmonyLib;
using NetworkPerformanceSystem.Runtime;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M11 - writes the connection timeouts after vanilla has written its own, at both layers.
    ///
    /// ZRpc.SetLongTimeout is the only place vanilla assigns ZRpc's static timeout, and ZNet.Start
    /// calls it unconditionally before either a client connects or a server loads, so a postfix
    /// there lands ahead of every peer on both builds. ZSteamSocket.RegisterGlobalCallbacks is the
    /// same seam M8 uses, and vanilla pins TimeoutConnected inside it.
    ///
    /// No transpiler and no IL assertions on either: one is a whole method whose absence Harmony
    /// reports at patch time, the other is a set of Steamworks calls at Global scope with no game
    /// constants in it. What replaces that check is the readback in ConnectionTimeout - both
    /// layers are read back after the write and logged, so a refused write is visible rather than
    /// assumed.
    /// </summary>
    [HarmonyPatch]
    internal static class ConnectionTimeoutPatches {

        [HarmonyPatch(typeof(ZRpc), nameof(ZRpc.SetLongTimeout))]
        [HarmonyPostfix]
        private static void ApplyRpcTimeout(bool enable) {
            ConnectionTimeout.OnVanillaRpcTimeoutSet(enable);
        }

        /// <summary>Separate from M8's postfix on the same method on purpose: the two mechanisms
        /// have their own config toggles and either can be off while the other applies.</summary>
        [HarmonyPatch(typeof(ZSteamSocket), "RegisterGlobalCallbacks")]
        [HarmonyPostfix]
        private static void ApplySteamTimeouts() {
            ConnectionTimeout.OnGlobalCallbacksRegistered();
        }
    }
}
