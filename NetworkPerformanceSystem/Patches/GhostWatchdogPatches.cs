using HarmonyLib;
using NetworkPerformanceSystem.Runtime;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M22 - the one seam the client ghost watchdog needs that is not already patched.
    ///
    /// Detection and the decision to leave run from the ZNet.Update postfix in
    /// NetworkChannelPatches, alongside the rest of the mod's periodic work; there is no reason
    /// for a second driver. All that is left is the main menu, which by then is a different scene
    /// with no ZNet in it - so the reason has to be carried across rather than read.
    ///
    /// FejdStartup.ShowConnectError is a postfix rather than a prefix on purpose: vanilla has
    /// already decided which localized string applies and switched the panel on, so this only has
    /// to overwrite the text. That keeps every other disconnect reason - wrong version, banned,
    /// server full - exactly as it was, which a prefix returning false would not.
    /// </summary>
    [HarmonyPatch]
    internal static class GhostWatchdogPatches {

        [HarmonyPatch(typeof(FejdStartup), "ShowConnectError")]
        [HarmonyPostfix]
        private static void ExplainGhostDisconnect(FejdStartup __instance) {
            string reason = GhostWatchdog.PendingReason;
            if (reason == null) { return; }

            // Read once. The panel can be shown again for an unrelated failure later in the same
            // menu session, and that one is not ours to caption.
            GhostWatchdog.ClearPendingReason();

            if (__instance != null && __instance.m_connectionFailedError != null) {
                __instance.m_connectionFailedError.text = reason;
            }
        }
    }
}
