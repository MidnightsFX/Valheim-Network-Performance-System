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
    /// </summary>
    [HarmonyPatch]
    internal static class SendWindowPatches {

        private const int VanillaWindowConstant = 10240;
        private const int VanillaMinPackageConstant = 2048;

        private const int ExpectedWindowConstants = 2;
        private const int ExpectedMinPackageConstants = 1;

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
                return codes;
            }

            System.Reflection.MethodInfo windowFor =
                AccessTools.Method(typeof(SendWindow), nameof(SendWindow.For));

            List<CodeInstruction> patched = new List<CodeInstruction>(codes.Count + ExpectedWindowConstants);
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
                } else {
                    patched.Add(code);
                }
            }

            Logger.LogInfo($"Send window sizing active ({rewritten} sites rewritten in ZDOMan.SendZDOs).");
            return patched;
        }
    }
}
