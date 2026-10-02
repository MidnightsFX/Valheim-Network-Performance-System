using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M32 - wiring for PlayerHistoryRepeats. See it for why; this file is only the hooks.
    ///
    ///   * ZNet.SendHistoricalPlayerList, transpiled: its one `ZRpc.Invoke(string, object[])`, the
    ///     send to each ready player, becomes PlayerHistoryRepeats.Invoke with the same stack. A
    ///     call swapped for a call, so it composes with anything else patching the method, and the
    ///     history update before it and the event after it are untouched.
    ///   * The same method, prefix and postfix: which of the two UpdatePlayerHistory calls in an
    ///     update is running, and the end of an update's record.
    ///   * ZNet.UpdatePlayerHistory, prefix and postfix, host only: a snapshot of the history and
    ///     what differs from it afterwards. It runs on clients too, when the host's list arrives,
    ///     and is left alone there.
    /// </summary>
    [HarmonyPatch]
    internal static class PlayerHistoryRepeatsPatches {

        private const string SendMethod = "SendHistoricalPlayerList";
        private const string UpdateMethod = "UpdatePlayerHistory";

        /// <summary>The send to each ready player, inside the foreach over m_peers.</summary>
        private const int ExpectedInvokes = 1;

        private static readonly MethodInfo RpcInvoke =
            AccessTools.Method(typeof(ZRpc), nameof(ZRpc.Invoke), new[] { typeof(string), typeof(object[]) });

        [HarmonyPrepare]
        private static bool Prepare() {
            MethodInfo send = AccessTools.Method(typeof(ZNet), SendMethod);
            MethodInfo update = AccessTools.Method(typeof(ZNet), UpdateMethod);
            if (send == null || update == null || RpcInvoke == null) {
                PatchGuard.Disable(Mechanism.PlayerHistoryRepeats,
                    $"ZNet.{SendMethod}, ZNet.{UpdateMethod} or ZRpc.Invoke(string, object[]) do not have the expected shape. Either the game " +
                    "updated or another mod rewrote them. The player history is sent to every player each time the game marks it changed, as vanilla.");
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(ZNet), SendMethod)]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> SendOnlyWhenChanged(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);

            int sites = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (IsRpcInvoke(codes[i])) { sites++; }
            }
            if (sites != ExpectedInvokes) {
                PatchGuard.Disable(Mechanism.PlayerHistoryRepeats,
                    $"ZNet.{SendMethod} calls ZRpc.Invoke(string, object[]) {sites} time(s), expected {ExpectedInvokes}. Either the game updated " +
                    "or another mod rewrote this method first. The player history is sent to every player each time the game marks it changed, " +
                    $"as vanilla. Found: {IlMatch.DescribeNeighbours(codes, nameof(ZRpc.Invoke))}");
                return codes;
            }

            // An instance call on (ZRpc, string, object[]) swapped for a static one taking the same
            // three, so the stack is the same on both sides. ReplaceInPlace carries the labels over:
            // the call sits inside the loop over m_peers.
            MethodInfo replacement = AccessTools.Method(typeof(PlayerHistoryRepeats), nameof(PlayerHistoryRepeats.Invoke));
            for (int i = 0; i < codes.Count; i++) {
                if (!IsRpcInvoke(codes[i])) { continue; }
                IlMatch.ReplaceInPlace(codes, i, new CodeInstruction(OpCodes.Call, replacement));
            }

            IlMatch.LogOnce($"Player history resend check active (ZNet.{SendMethod}).");
            return codes;
        }

        private static bool IsRpcInvoke(CodeInstruction code) {
            return (code.opcode == OpCodes.Callvirt || code.opcode == OpCodes.Call)
                   && code.operand is MethodInfo method
                   && method == RpcInvoke;
        }

        [HarmonyPatch(typeof(ZNet), SendMethod)]
        [HarmonyPrefix]
        private static void EnterSend() {
            if (PlayerHistoryRepeats.Hooked) { PlayerHistoryRepeats.InSend = true; }
        }

        [HarmonyPatch(typeof(ZNet), SendMethod)]
        [HarmonyPostfix]
        private static void LeaveSend() {
            if (PlayerHistoryRepeats.Hooked) { PlayerHistoryRepeats.OnSendDone(); }
        }

        [HarmonyPatch(typeof(ZNet), UpdateMethod)]
        [HarmonyPrefix]
        private static void BeforeHistoryUpdate(ZNet __instance) {
            if (!PlayerHistoryRepeats.Hooked || !__instance.IsServer() || ZNet.m_world == null) { return; }
            PlayerHistoryRepeats.BeforeUpdate(ZNet.m_world.m_playerHistory, __instance.m_historicalPlayerListUpdated);
        }

        [HarmonyPatch(typeof(ZNet), UpdateMethod)]
        [HarmonyPostfix]
        private static void AfterHistoryUpdate(ZNet __instance) {
            if (!PlayerHistoryRepeats.Hooked || !__instance.IsServer() || ZNet.m_world == null) { return; }
            PlayerHistoryRepeats.AfterUpdate(ZNet.m_world.m_playerHistory, __instance.m_historicalPlayerListUpdated, __instance.m_players);
        }
    }
}
