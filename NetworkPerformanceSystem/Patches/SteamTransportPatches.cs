using HarmonyLib;
using NetworkPerformanceSystem.Runtime;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M8 - writes the Steam send-rate bounds and Nagle timer after vanilla has written its own.
    ///
    /// ZSteamSocket.RegisterGlobalCallbacks is the seam vanilla itself uses to write its four
    /// networking config values, on both the client and the dedicated-server build, before any
    /// socket exists - so a postfix here lands after vanilla's values and before anything can be
    /// affected by them. No transpiler and no IL assertions: these are Steamworks API calls at
    /// Global scope, not game constants, so there is no IL shape to verify. What replaces that
    /// check is the readback in SteamTransport.Apply - every value is read back out of Steam after
    /// the write and logged, so a refused write is visible rather than assumed.
    /// </summary>
    [HarmonyPatch]
    internal static class SteamTransportPatches {

        [HarmonyPatch(typeof(ZSteamSocket), "RegisterGlobalCallbacks")]
        [HarmonyPostfix]
        private static void ApplyTransportConfig() {
            SteamTransport.OnGlobalCallbacksRegistered();
        }
    }
}
