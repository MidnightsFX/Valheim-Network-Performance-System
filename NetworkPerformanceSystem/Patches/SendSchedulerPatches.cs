using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M2b - services every peer once per send interval instead of one peer per rendered frame.
    ///
    /// Vanilla's SendZDOToPeers2 is a round-robin that advances one peer per Update, and the frame
    /// that opens a round sends to nobody at all. A full round therefore costs N+1 frames on top of
    /// the 0.05s gate, so the advertised 20Hz silently becomes:
    ///
    ///     2 players  @60fps -> 3 frames  -> 20Hz
    ///     10 players @60fps -> 11 frames -> ~5.5Hz  (~180ms of added staleness)
    ///
    /// and it degrades further as server frame rate drops. None of that is network latency, but it
    /// lands on top of it, so a distant group pays for it twice.
    ///
    /// The sends are strided across the frames inside each interval rather than fired all at
    /// once: every SendZDOs call is a sector scan, a revision filter and a sort, and running 50
    /// of those back-to-back in one frame is a stutter that every dt-driven system downstream
    /// inherits. Striding keeps the per-peer rate at exactly 1/interval while holding per-frame
    /// cost to a handful of peers.
    ///
    /// Striding alone is not enough on a large server, though. Vanilla's one-peer-per-frame is
    /// accidentally self-limiting: its CPU cost never exceeds one sector scan per frame, and what
    /// gives instead is the per-peer rate. Servicing every peer per interval inverts that - the
    /// per-peer rate is fixed and the CPU cost grows with player count, and once a frame's worth
    /// of sends costs more than the interval the accumulator owes a full round every frame and
    /// the server never catches up. So each frame also has a wall-clock budget: peers are
    /// serviced in round-robin order until either the owed count or the budget is exhausted, and
    /// whatever is left is owed to the next frame. The per-peer rate then degrades gracefully
    /// under load - min(1/interval, budget/cost) - instead of the frame time exploding, and no
    /// peer can be starved because the order never resets. nps_stats reports the effective rate.
    ///
    /// This is the one mechanism here that is not novel - ReturnToSender, VBNetTweaks and SkadiNet
    /// all fix it and agree on the shape. We carry it because we are standalone.
    /// </summary>
    [HarmonyPatch]
    internal static class SendSchedulerPatches {

        /// <summary>Fractional peers owed a send, accumulated per frame. Clamped to one full
        /// round so a hitch is repaid as at most one burst, not several.</summary>
        private static float _strideAccumulator;
        private static int _strideIndex;

        private static readonly Stopwatch FrameWatch = new Stopwatch();

        // Telemetry for nps_stats. ServicedLastSecond / peer count is the effective per-peer
        // send rate, which is the number this mechanism exists to move - and, under load, the
        // number the frame budget is trading away.
        internal static int LastFrameServiced;
        internal static int ServicedLastSecond;
        internal static int BudgetBreaksLastSecond;
        private static int _servicedAccum;
        private static int _budgetBreaksAccum;
        private static float _windowStart;

        [HarmonyPatch(typeof(ZDOMan), "SendZDOToPeers2")]
        [HarmonyPrefix]
        private static bool ReplaceRoundRobin(ZDOMan __instance, float dt) {
            if (!PatchGuard.IsActive(Mechanism.SendScheduler)) { return true; }
            if (!ValConfig.EnableSchedulerFix.Value) { return true; }

            List<ZDOMan.ZDOPeer> peers = __instance.m_peers;
            int count = peers.Count;
            if (count == 0) { return false; }

            float interval = Mathf.Max(0.01f, ValConfig.SendIntervalSeconds.Value);
            _strideAccumulator = Mathf.Min(_strideAccumulator + dt * count / interval, count);

            int toService = Mathf.FloorToInt(_strideAccumulator);
            if (toService <= 0) { return false; }
            _strideAccumulator -= toService;

            double budgetMs = ValConfig.SendSchedulerFrameBudgetMs.Value;
            FrameWatch.Restart();

            int serviced = 0;
            bool brokeBudget = false;
            for (int i = 0; i < toService; i++) {
                if (_strideIndex >= count) { _strideIndex = 0; }
                ZDOMan.ZDOPeer peer = peers[_strideIndex];
                _strideIndex++;
                serviced++;                                                   // the slot is consumed either way

                if (peer?.m_peer?.m_socket == null || !peer.m_peer.m_socket.IsConnected()) { continue; }
                __instance.SendZDOs(peer, false);

                // At least one peer is always serviced, so progress is guaranteed even when a
                // single send exceeds the budget; the check only decides whether to go on.
                if (i + 1 < toService && FrameWatch.Elapsed.TotalMilliseconds > budgetMs) {
                    brokeBudget = true;
                    break;
                }
            }

            // Whatever the budget cut off is still owed. Adding it back keeps the long-run
            // per-peer rate honest when load is bursty; the clamp to one round means a sustained
            // overload is paid down at "everyone once per frame" at most, never compounding.
            _strideAccumulator = Mathf.Min(_strideAccumulator + (toService - serviced), count);

            RecordFrame(serviced, brokeBudget);
            return false;
        }

        private static void RecordFrame(int serviced, bool brokeBudget) {
            LastFrameServiced = serviced;
            _servicedAccum += serviced;
            if (brokeBudget) { _budgetBreaksAccum++; }

            float now = Time.realtimeSinceStartup;
            if (now - _windowStart < 1f) { return; }
            ServicedLastSecond = _servicedAccum;
            BudgetBreaksLastSecond = _budgetBreaksAccum;
            _servicedAccum = 0;
            _budgetBreaksAccum = 0;
            _windowStart = now;
        }

        internal static void Reset() {
            _strideAccumulator = 0f;
            _strideIndex = 0;
            LastFrameServiced = 0;
            ServicedLastSecond = 0;
            BudgetBreaksLastSecond = 0;
            _servicedAccum = 0;
            _budgetBreaksAccum = 0;
            _windowStart = 0f;
        }

        /// <summary>
        /// ReturnToSender transpiles ZDOMan.Update to redirect the SendZDOToPeers2 call to its own
        /// all-peers loop, so our prefix would never fire. It already does this job correctly -
        /// stand down rather than leave dead code that looks active in the log.
        ///
        /// Called once from the plugin's Start, not from the prefix: with ReturnToSender installed
        /// the prefix never runs, so a check inside it could never find the mod it is looking for.
        /// Not from Awake either, because BepInEx populates Chainloader.PluginInfos incrementally
        /// as plugins load, so a plugin ordered after us is not visible from our own Awake.
        /// </summary>
        internal static void CheckForReturnToSender() {
            if (PatchGuard.IsPluginLoaded(PatchGuard.ReturnToSenderGUID)) {
                PatchGuard.Disable(Mechanism.SendScheduler,
                    "ReturnToSender is installed and already services every peer per tick. " +
                    "Standing down so there is exactly one scheduler in the send path.");
            }
        }
    }
}
