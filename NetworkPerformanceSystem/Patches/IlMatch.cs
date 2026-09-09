using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// Small predicates shared by the transpilers. Every mechanism here works by finding a
    /// hard-coded constant and rewriting it, so "is this instruction the number N" is the one
    /// question all of them ask, and it has to be asked the same way in each - a transpiler that
    /// matches only one of the two encodings silently half-patches its method.
    /// </summary>
    internal static class IlMatch {

        /// <summary>
        /// Matches both int32 constant encodings. Which one Roslyn picks depends on the value
        /// (Ldc_I4_S for anything in sbyte range, Ldc_I4 above it), so an anchor that assumes one
        /// stops matching the day the game's own constant changes magnitude.
        /// </summary>
        internal static bool IsInt32Constant(CodeInstruction code, int value) {
            if (code.opcode == OpCodes.Ldc_I4 && code.operand is int wide) { return wide == value; }
            if (code.opcode == OpCodes.Ldc_I4_S && code.operand is sbyte narrow) { return narrow == value; }
            return false;
        }

        internal static int CountInt32Constant(List<CodeInstruction> codes, int value) {
            int count = 0;
            for (int i = 0; i < codes.Count; i++) {
                if (IsInt32Constant(codes[i], value)) { count++; }
            }
            return count;
        }

        /// <summary>
        /// Name-only match on the member a call or field access targets. Used where the declaring
        /// type lives in an assembly we deliberately do not reference at compile time (PlayFab),
        /// so the transpiler can be JIT-compiled on a process where that assembly is absent.
        /// </summary>
        internal static bool TargetsMemberNamed(CodeInstruction code, string name) {
            if (code.operand is MemberInfo member) { return member.Name == name; }
            return false;
        }

        /// <summary>
        /// Replaces one instruction in place, carrying its labels and exception-handler blocks
        /// onto the replacement. Skipping this is the classic transpiler bug: a branch that
        /// targeted the old instruction ends up pointing at nothing.
        /// </summary>
        internal static void ReplaceInPlace(List<CodeInstruction> codes, int index, CodeInstruction replacement) {
            replacement.labels.AddRange(codes[index].labels);
            replacement.blocks.AddRange(codes[index].blocks);
            codes[index] = replacement;
        }
    }
}
