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
    /// M14 rides on the same method. The queue read on SendZDOs's first line is bracketed so
    /// SendQueueView can tell it apart from every other caller: this read is compared against the
    /// sized window and must see the real queue, while other mods' reads have the extra window
    /// taken off. The call itself is left in place, so anything else that anchors on it or patches
    /// it still finds it.
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

            // Harmony re-runs transpilers whenever another mod patches this method, so the mark is
            // only claimed by the run that actually places it.
            SendQueueView.OwnReadMarked = false;

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
                PatchGuard.Disable(Mechanism.QueueSizeView,
                    "the send window is not applied, so no peer's queue grows past vanilla's and there is nothing to adjust.");
                return codes;
            }

            // M14's anchor is checked on its own. Without the mark this mod's own window check
            // would be handed the adjusted figure too - an effective window of the sized one plus
            // its own extra - so a missing read stands the view down and leaves the window alone.
            int queueReads = CountQueueReads(codes);
            bool markQueueRead = queueReads == ExpectedQueueReads;
            if (!markQueueRead) {
                PatchGuard.Disable(Mechanism.QueueSizeView,
                    $"ZDOMan.SendZDOs reads the send queue {queueReads} times, expected {ExpectedQueueReads}. " +
                    "Other mods read the real queue size, so ones that wait for it to fall under a fixed number " +
                    "(ServerSync, ConditionalConfigSync) may time out distant peers; the send window itself is unaffected.");
            }

            System.Reflection.MethodInfo windowFor =
                AccessTools.Method(typeof(SendWindow), nameof(SendWindow.For));
            System.Reflection.MethodInfo beginOwnRead =
                AccessTools.Method(typeof(SendQueueView), nameof(SendQueueView.BeginOwnRead));
            System.Reflection.MethodInfo endOwnRead =
                AccessTools.Method(typeof(SendQueueView), nameof(SendQueueView.EndOwnRead));

            List<CodeInstruction> patched = new List<CodeInstruction>(codes.Count + ExpectedWindowConstants + 2);
            int rewritten = 0;
            int marked = 0;

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
                } else if (markQueueRead && IsQueueRead(code)) {
                    // Keep the read and bracket it:
                    // `call BeginOwnRead(); callvirt GetSendQueueSize; call EndOwnRead(int)`.
                    // The socket reference is already on the stack when BeginOwnRead runs, and it
                    // takes nothing and leaves nothing; EndOwnRead hands back the int it was given.
                    // Stack-neutral both sides, so the stloc that follows sees exactly what it did
                    // before. Labels move onto BeginOwnRead so a branch to this read still marks it.
                    CodeInstruction begin = new CodeInstruction(OpCodes.Call, beginOwnRead);
                    begin.labels.AddRange(code.labels);
                    begin.blocks.AddRange(code.blocks);
                    code.labels.Clear();
                    code.blocks.Clear();

                    patched.Add(begin);
                    patched.Add(code);
                    patched.Add(new CodeInstruction(OpCodes.Call, endOwnRead));
                    marked++;
                } else {
                    patched.Add(code);
                }
            }

            SendQueueView.OwnReadMarked = marked == ExpectedQueueReads;
            Logger.LogInfo($"Send window sizing active ({rewritten} sites rewritten in ZDOMan.SendZDOs" +
                           (SendQueueView.OwnReadMarked ? ", queue read marked as the mod's own)." : ")."));
            return patched;
        }

        /// <summary>The name plus an int return: EndOwnRead takes the read's result off the stack,
        /// so a same-named call that returned anything else would make the method unloadable rather
        /// than merely unmarked.</summary>
        private static bool IsQueueRead(CodeInstruction code) {
            return (code.opcode == OpCodes.Callvirt || code.opcode == OpCodes.Call)
                   && code.operand is System.Reflection.MethodInfo method
                   && method.ReturnType == typeof(int)
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
