using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M41 - wiring for GamePing, which times the host's own once-a-second ZRpc ping to its answer.
    /// See GamePing for why; this file is only the hooks.
    ///
    ///   * ZRpc.UpdatePing (prefix + postfix) - the ping goes out when m_pingTimer passes the
    ///     interval, and UpdatePing resets the timer to 0 as it sends; a timer that went down
    ///     across the call is a ping sent. Comparing before and after, rather than repeating the
    ///     interval test, keeps this right if anything changes the interval.
    ///   * ZRpc.ReceivePing - the answer is timed from M1's existing postfix
    ///     (NetworkChannelPatches.SampleRtt), which already finds the peer.
    ///   * ZNet.Update (prefix) - one tick per host frame, to notice the host's own stalls.
    /// </summary>
    [HarmonyPatch]
    internal static class GamePingPatches {

        [HarmonyPrepare]
        private static bool Prepare() {
            MethodInfo updatePing = AccessTools.Method(typeof(ZRpc), "UpdatePing", new[] { typeof(float) });
            MethodInfo receivePing = AccessTools.Method(typeof(ZRpc), "ReceivePing", new[] { typeof(ZPackage) });
            MethodInfo sendPackage = AccessTools.Method(typeof(ZRpc), "SendPackage", new[] { typeof(ZPackage) });
            FieldInfo pingTimer = AccessTools.Field(typeof(ZRpc), "m_pingTimer");
            MethodInfo netUpdate = AccessTools.Method(typeof(ZNet), "Update");

            if (updatePing == null || receivePing == null || sendPackage == null || pingTimer == null
                || pingTimer.FieldType != typeof(float) || netUpdate == null || !SendsAfterResettingTimer(updatePing, pingTimer, sendPackage)) {
                PatchGuard.Disable(Mechanism.GamePing,
                    "ZRpc.UpdatePing / ReceivePing / m_pingTimer or ZNet.Update do not have the expected shape. " +
                    "Either the game updated or another mod rewrote them first. Players' pings are Steam's figure, as before 1.18.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// UpdatePing must still reset m_pingTimer to 0 and then send: that reset is how a sent
        /// ping is recognised. Read from the IL, so a game that started timing pings some other
        /// way stands this mechanism down instead of timing nothing.
        /// </summary>
        private static bool SendsAfterResettingTimer(MethodInfo updatePing, FieldInfo pingTimer, MethodInfo sendPackage) {
            try {
                List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(updatePing);
                int reset = -1;
                for (int i = 1; i < code.Count; i++) {
                    if (reset < 0 && code[i].opcode == OpCodes.Stfld && code[i].operand is FieldInfo field && field == pingTimer
                        && code[i - 1].opcode == OpCodes.Ldc_R4 && code[i - 1].operand is float value && value == 0f) {
                        reset = i;
                        continue;
                    }
                    if (reset >= 0 && (code[i].opcode == OpCodes.Call || code[i].opcode == OpCodes.Callvirt)
                        && code[i].operand is MethodInfo called && called == sendPackage) {
                        return true;
                    }
                }
            } catch (System.Exception) {
                return false;
            }
            return false;
        }

        [HarmonyPatch(typeof(ZRpc), "UpdatePing")]
        [HarmonyPrefix]
        private static void BeforeUpdatePing(float ___m_pingTimer, out float __state) {
            __state = ___m_pingTimer;
        }

        [HarmonyPatch(typeof(ZRpc), "UpdatePing")]
        [HarmonyPostfix]
        private static void AfterUpdatePing(ZRpc __instance, float dt, float ___m_pingTimer, float __state) {
            // Without a send the timer ends at before + dt; with one it ends at 0. So it went down,
            // or - a frame of over a second straight after the last ping - it stayed at 0 although
            // time passed. Missing one would pair every later answer with the wrong ping.
            if (___m_pingTimer < __state || (___m_pingTimer == 0f && dt > 0f)) { GamePing.OnPingSent(__instance); }
        }

        [HarmonyPatch(typeof(ZNet), "Update")]
        [HarmonyPrefix]
        private static void NoteFrame() {
            GamePing.NoteFrame();
        }
    }
}
