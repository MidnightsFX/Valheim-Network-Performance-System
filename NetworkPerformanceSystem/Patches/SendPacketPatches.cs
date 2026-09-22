using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M17 - rewrites the two `new ZPackage()` in ZDOMan.SendZDOs into calls that hand back reused
    /// instances. See SendPackets for why that is safe and what it assumes.
    ///
    /// THIS HAS TO BE A TRANSPILER, and the reason is worth spelling out because the alternative
    /// fails quietly rather than loudly.
    ///
    /// This mod already touches SendZDOs twice: M2/M2c transpiles it (SendWindowPatches rewrites
    /// both window constants and marks M14's queue read) and M5 prefixes it for telemetry. A
    /// prefix here that returned false to run a replacement body would skip the very body M2 had
    /// just transpiled. That is not a crash - it is worse. M2 runs at patch time, finds the IL
    /// shape intact, rewrites its two sites and logs "Send window sizing active", and then that
    /// code never executes: every peer silently reverts to a fixed 10 KB window, with a startup
    /// line asserting the opposite. M2's own guard cannot catch it either, because a prefix leaves
    /// the constants exactly where they were and its count still passes.
    ///
    /// As transpilers the two compose in either order and neither can hide the other's failure:
    /// this one only touches `newobj ZPackage`, which M2 never emits or removes, and M2 only
    /// touches the 10240/2048 constants and the queue read, none of which this one emits or
    /// removes. Whichever Harmony runs first, both anchor counts still come out right.
    /// </summary>
    [HarmonyPatch]
    internal static class SendPacketPatches {

        private const string PackageTypeName = "ZPackage";
        private const int ParameterlessCtor = 0;

        /// <summary>The outer packet and the per-ZDO scratch, in IL order.</summary>
        private const int ExpectedPackageConstructions = 2;

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.SendZDOs))]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> ReuseSendPackages(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);

            // Count and bail. Two constructions, no more and no fewer: a third would mean the
            // method now builds a package this does not understand the lifetime of, and reusing a
            // buffer whose lifetime we have guessed wrong is corruption rather than a slow path.
            int constructions = IlMatch.CountNewObj(codes, PackageTypeName, ParameterlessCtor);
            if (constructions != ExpectedPackageConstructions) {
                PatchGuard.Disable(Mechanism.SendPacketReuse,
                    $"ZDOMan.SendZDOs constructs {constructions} empty ZPackage(s), expected {ExpectedPackageConstructions}. " +
                    "Either the game updated or another mod rewrote this method first. Send packets are built as vanilla. " +
                    $"Found: {IlMatch.DescribeNewObj(codes, PackageTypeName)}");
                return codes;
            }

            System.Reflection.MethodInfo outer = AccessTools.Method(typeof(SendPackets), nameof(SendPackets.Outer));
            System.Reflection.MethodInfo scratch = AccessTools.Method(typeof(SendPackets), nameof(SendPackets.Scratch));

            // One instruction swapped for one instruction, stack-neutral in both directions:
            // newobj leaves one reference on the stack and so does the call. ReplaceInPlace carries
            // the labels and exception blocks over, which matters because the second site sits
            // inside the loop the method branches back into.
            int rewritten = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (!IlMatch.IsNewObj(codes[i], PackageTypeName, ParameterlessCtor)) { continue; }

                // IL order is the declaration order: the outer packet is built first and stored to
                // the local the whole packet accumulates into, the per-ZDO scratch second.
                IlMatch.ReplaceInPlace(codes, i, new CodeInstruction(OpCodes.Call, rewritten == 0 ? outer : scratch));
                rewritten++;
            }

            Logger.LogInfo($"Send package reuse active ({rewritten} sites rewritten in ZDOMan.SendZDOs).");
            return codes;
        }
    }
}
