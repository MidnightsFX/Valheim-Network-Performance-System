using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Diagnostics;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M34's optional half: how long the game's fixed-step update takes on this machine, and how
    /// many steps run. MonoUpdaters.FixedUpdate is where every Character, ZSyncTransform and
    /// creature AI is stepped, so it is the part of a frame that grows with what a player
    /// simulates. Wrapped from both ends - first prefix, last postfix - so other mods' patches on
    /// it are counted as the game's work.
    ///
    /// Not applied on a dedicated server, which reports nothing. If the method is missing the
    /// frame report still goes out, flagged as having no simulation timing.
    /// </summary>
    [HarmonyPatch]
    internal static class FrameSamplerPatches {

        [HarmonyPrepare]
        private static bool Prepare() {
            if (NpsEnv.IsDedicated()) { return false; }

            if (AccessTools.Method(typeof(MonoUpdaters), "FixedUpdate") == null) {
                Logger.LogInfo("Frame report: MonoUpdaters.FixedUpdate was not found, so frame rate is reported without simulation time.");
                return false;
            }
            FrameSampler.SimTimingAvailable = true;
            return true;
        }

        [HarmonyPatch(typeof(MonoUpdaters), "FixedUpdate")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void StartTiming(out long __state) {
            __state = Stopwatch.GetTimestamp();
        }

        [HarmonyPatch(typeof(MonoUpdaters), "FixedUpdate")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void StopTiming(long __state) {
            FrameSampler.NoteFixedStep((Stopwatch.GetTimestamp() - __state) * 1000d / Stopwatch.Frequency);
        }
    }
}
