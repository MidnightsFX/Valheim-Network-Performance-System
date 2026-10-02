using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System;
using System.Reflection;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M14 - wiring for SendQueueView, which shows other mods a peer's send queue as vanilla's
    /// window would have left it. See that class for the rules; this file is only the hook.
    ///
    /// A postfix on the socket rather than a rewrite of each caller. The callers are other mods'
    /// compiler-generated wait loops, one copy per mod that bundles ServerSync, and whichever ones
    /// a given server has installed; the socket is the one place every one of them reaches. The
    /// send path's own read passes through here too, and is told apart by the mark
    /// SendWindowPatches places around it.
    ///
    /// Installed unconditionally, unlike the allocation hooks: the method it wraps already makes a
    /// Steam status call per read, so the wrapper is noise beside it, and leaving it in place is
    /// what lets Report Vanilla Queue Size apply the moment it is switched either way.
    /// </summary>
    [HarmonyPatch]
    internal static class SendQueueViewPatches {

        [HarmonyPrepare]
        private static bool Prepare() {
            MethodInfo read = AccessTools.Method(typeof(ZSteamSocket), "GetSendQueueSize", Type.EmptyTypes);
            if (read == null || read.ReturnType != typeof(int)) {
                PatchGuard.Disable(Mechanism.QueueSizeView,
                    "ZSteamSocket.GetSendQueueSize() does not have the expected shape. Either the game updated or another " +
                    "mod rewrote it first. Other mods read the real queue size, so ones that wait for it to fall under a " +
                    "fixed number (ServerSync, ConditionalConfigSync) may time out distant peers.");
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(ZSteamSocket), "GetSendQueueSize")]
        [HarmonyPostfix]
        private static void ReportAsVanilla(ZSteamSocket __instance, ref int __result) {
            __result = SendQueueView.ForReader(__instance, __result);
        }
    }
}
