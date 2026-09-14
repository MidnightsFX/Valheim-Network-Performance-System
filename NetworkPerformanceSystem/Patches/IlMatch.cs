using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// Small predicates shared by the transpilers. Every mechanism here works by finding a
    /// hard-coded constant and rewriting it, so "is this instruction the number N" is the one
    /// question all of them ask, and it has to be asked the same way in each - a transpiler that
    /// matches only one of the two encodings silently half-patches its method.
    /// </summary>
    internal static class IlMatch {

        /// <summary>
        /// Reads both int32 constant encodings. Which one Roslyn picks depends on the value
        /// (Ldc_I4_S for anything in sbyte range, Ldc_I4 above it), so an anchor that assumes one
        /// stops matching the day the game's own constant changes magnitude.
        /// </summary>
        internal static bool TryGetInt32Constant(CodeInstruction code, out int value) {
            if (code.opcode == OpCodes.Ldc_I4 && code.operand is int wide) { value = wide; return true; }
            if (code.opcode == OpCodes.Ldc_I4_S && code.operand is sbyte narrow) { value = narrow; return true; }
            value = 0;
            return false;
        }

        internal static bool IsInt32Constant(CodeInstruction code, int value) {
            return TryGetInt32Constant(code, out int found) && found == value;
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
        /// Several names are accepted for the sites where the client and dedicated server builds
        /// of the game reach the same number through different calls.
        /// </summary>
        internal static bool TargetsMemberNamed(CodeInstruction code, params string[] names) {
            if (!(code.operand is MemberInfo member)) { return false; }
            for (int i = 0; i < names.Length; i++) {
                if (member.Name == names[i]) { return true; }
            }
            return false;
        }

        /// <summary>
        /// One entry per reference to any of the named members, each paired with the instruction
        /// before it - the operand the anchors expect to be a constant. Logged when an anchor
        /// misses: "found 0" says nothing about what the game actually has there, and that is the
        /// one fact a report from a build we cannot see has to carry. The dedicated server build
        /// asking for 11 where the client asks for 10 went undiagnosed for exactly that reason.
        /// </summary>
        internal static string DescribeNeighbours(List<CodeInstruction> codes, params string[] memberNames) {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < codes.Count; i++) {
                if (!TargetsMemberNamed(codes[i], memberNames)) { continue; }
                if (sb.Length > 0) { sb.Append("; "); }
                if (i > 0) { sb.Append(Describe(codes[i - 1])).Append(" -> "); }
                sb.Append(Describe(codes[i]));
            }
            return sb.Length > 0 ? sb.ToString() : "no reference to " + string.Join("/", memberNames) + " in the method";
        }

        private static string Describe(CodeInstruction code) {
            if (code.operand is MemberInfo member) { return $"{code.opcode} {member.DeclaringType?.Name}.{member.Name}"; }
            return code.operand == null ? code.opcode.ToString() : $"{code.opcode} {code.operand}";
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
