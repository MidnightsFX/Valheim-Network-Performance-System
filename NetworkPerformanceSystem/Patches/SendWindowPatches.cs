using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M2/M2c - rewrites the two hard-coded 10240 constants in ZDOMan.SendZDOs into a per-peer
    /// call, so the in-flight window is sized from that peer's measured RTT.
    ///
    /// Deliberately ungated by side: SendZDOs runs on clients too, for the client-&gt;host
    /// direction, against the same fixed window. A distant client's uploads are throttled by
    /// exactly the same mechanism, and when it saturates they send nothing at all - their own
    /// character and every creature they own freeze for everybody else.
    ///
    /// M14 rides on the same method. The queue size SendZDOs reads on its first line is handed
    /// to QueueDrain.Observe before either window site runs, so the drain sees every send attempt
    /// - vanilla SendZDOToPeers2, our scheduler, ReturnToSender's loop, clients - without a second
    /// Steam status call per peer per tick, which is the cost this mod's own diagnostics prefix
    /// goes out of its way not to pay.
    /// </summary>
    [HarmonyPatch]
    internal static class SendWindowPatches {

        private const int VanillaWindowConstant = 10240;
        private const int VanillaMinPackageConstant = 2048;

        private const int ExpectedWindowConstants = 2;
        private const int ExpectedMinPackageConstants = 1;
        private const int ExpectedQueueReads = 1;

        private const string QueueReadName = "GetSendQueueSize";

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.SendZDOs))]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> SizeWindowByRtt(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);

            // Verify the IL is the shape we think it is before touching anything. A game update
            // that changes these constants must disable the mechanism loudly, not silently apply
            // a window to the wrong comparison.
            int windowConstants = IlMatch.CountInt32Constant(codes, VanillaWindowConstant);
            int minPackageConstants = IlMatch.CountInt32Constant(codes, VanillaMinPackageConstant);

            if (windowConstants != ExpectedWindowConstants || minPackageConstants != ExpectedMinPackageConstants) {
                PatchGuard.Disable(Mechanism.SendWindow,
                    $"ZDOMan.SendZDOs does not have the expected IL shape (found {windowConstants} x {VanillaWindowConstant} " +
                    $"and {minPackageConstants} x {VanillaMinPackageConstant}, expected {ExpectedWindowConstants} and {ExpectedMinPackageConstants}). " +
                    "Either the game updated or another mod rewrote this method first. Send windows stay at vanilla.");
                PatchGuard.Disable(Mechanism.QueueDrain, "the send window is not applied, so there is no widened queue to drain.");
                return codes;
            }

            // M14's anchor is checked on its own: a missing queue read stands the drain down and
            // leaves the window alone, because the window does not depend on it.
            int queueReads = CountQueueReads(codes);
            bool observeQueue = queueReads == ExpectedQueueReads;
            if (!observeQueue) {
                PatchGuard.Disable(Mechanism.QueueDrain,
                    $"ZDOMan.SendZDOs reads the send queue {queueReads} times, expected {ExpectedQueueReads}. " +
                    "The periodic queue drain has nowhere to observe from and stays off; the send window itself is unaffected.");
            }

            System.Reflection.MethodInfo windowFor =
                AccessTools.Method(typeof(SendWindow), nameof(SendWindow.For));
            System.Reflection.MethodInfo observe =
                AccessTools.Method(typeof(QueueDrain), nameof(QueueDrain.Observe));

            List<CodeInstruction> patched = new List<CodeInstruction>(codes.Count + ExpectedWindowConstants + 4);
            int rewritten = 0;

            foreach (CodeInstruction code in codes) {
                if (IlMatch.IsInt32Constant(code, VanillaWindowConstant)) {
                    // Replace `ldc.i4 10240` with `ldarg.1; call SendWindow.For(peer)`.
                    // SendZDOs is an instance method, so arg1 is the ZDOPeer. Labels and exception
                    // blocks must stay on the first emitted instruction or branches to this offset
                    // would land in the middle of our replacement.
                    CodeInstruction loadPeer = new CodeInstruction(OpCodes.Ldarg_1);
                    loadPeer.labels.AddRange(code.labels);
                    loadPeer.blocks.AddRange(code.blocks);

                    patched.Add(loadPeer);
                    patched.Add(new CodeInstruction(OpCodes.Call, windowFor));
                    rewritten++;
                } else if (observeQueue && IsQueueRead(code)) {
                    // Keep the read, then hand a copy of its result to the drain before the method
                    // stores it: `dup; ldarg.1; ldarg.2; call QueueDrain.Observe(queue, peer, flush)`.
                    // Stack-neutral, so the stloc that follows sees exactly what it did before, and
                    // nothing goes in front of the call, so its labels stay where they were.
                    patched.Add(code);
                    patched.Add(new CodeInstruction(OpCodes.Dup));
                    patched.Add(new CodeInstruction(OpCodes.Ldarg_1));
                    patched.Add(new CodeInstruction(OpCodes.Ldarg_2));
                    patched.Add(new CodeInstruction(OpCodes.Call, observe));
                } else {
                    patched.Add(code);
                }
            }

            Logger.LogInfo($"Send window sizing active ({rewritten} sites rewritten in ZDOMan.SendZDOs" +
                           (observeQueue ? ", queue drain observing)." : ")."));
            return patched;
        }

        private static bool IsQueueRead(CodeInstruction code) {
            return (code.opcode == OpCodes.Callvirt || code.opcode == OpCodes.Call)
                   && IlMatch.TargetsMemberNamed(code, QueueReadName);
        }

        private static int CountQueueReads(List<CodeInstruction> codes) {
            int count = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (IsQueueRead(codes[i])) { count++; }
            }
            return count;
        }
    }
}
