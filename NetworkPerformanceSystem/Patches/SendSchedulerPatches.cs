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
    /// The budget alone fails on exactly the servers it is for. Once one send costs more than the
    /// budget - a big base around a player is thousands of objects to scan and sort - every frame
    /// stops after a single peer, which is vanilla's one peer per frame again: forty players on a
    /// 30 fps server each wait 40 frames, 1.3 s, between updates. So the budget may not stop a
    /// frame until a minimum share of the peer list has been serviced (Min Players Per Frame
    /// Percent). Every peer then gets a send at least once every 100/share frames however many
    /// have joined, the frame pays for it, and the budget is the ceiling above that floor.
    /// NetworkTweaks' answer, a fixed count of peers per 50 ms tick in its own loop, caps the
    /// rate instead - 10 per tick is 5 Hz each at 40 players - which is why it is not used here.
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
        internal static int PastBudgetLastSecond;        // sends the minimum share made after the budget ran out
        internal static float SendCostMsLastSecond;      // mean wall time of one SendZDOs
        private static int _servicedAccum;
        private static int _budgetBreaksAccum;
        private static int _pastBudgetAccum;
        private static int _sendsAccum;
        private static double _sendMsAccum;
        private static float _windowStart;

        // Since start, for the monitoring record, which takes its own deltas.
        internal static long TotalServiced;
        internal static long TotalSends;
        internal static long TotalPastBudget;
        internal static long TotalBudgetBreaks;
        internal static double TotalSendMs;

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

            int floor = MinPeersPerFrame(count);
            double budgetMs = ValConfig.SendSchedulerFrameBudgetMs.Value;
            FrameWatch.Restart();

            int serviced = 0;
            int sends = 0;
            int pastBudget = 0;
            bool brokeBudget = false;
            for (int i = 0; i < toService; i++) {
                // The first `floor` slots are taken whatever the budget says - at least one, so
                // progress is guaranteed even when a single send exceeds the budget. After that
                // the budget decides whether to go on.
                bool overBudget = i > 0 && FrameWatch.Elapsed.TotalMilliseconds > budgetMs;
                if (overBudget && i >= floor) {
                    brokeBudget = true;
                    break;
                }

                if (_strideIndex >= count) { _strideIndex = 0; }
                ZDOMan.ZDOPeer peer = peers[_strideIndex];
                _strideIndex++;
                serviced++;                                                   // the slot is consumed either way

                if (peer?.m_peer?.m_socket == null || !peer.m_peer.m_socket.IsConnected()) { continue; }
                __instance.SendZDOs(peer, false);
                sends++;
                if (overBudget) { pastBudget++; }
            }

            // Whatever the budget cut off is still owed. Adding it back keeps the long-run
            // per-peer rate honest when load is bursty; the clamp to one round means a sustained
            // overload is paid down at "everyone once per frame" at most, never compounding.
            _strideAccumulator = Mathf.Min(_strideAccumulator + (toService - serviced), count);

            RecordFrame(serviced, sends, pastBudget, FrameWatch.Elapsed.TotalMilliseconds, brokeBudget);
            return false;
        }

        /// <summary>The fewest peers a frame services before the budget may stop it: the configured
        /// share of the peer list, rounded up, and never less than one.</summary>
        internal static int MinPeersPerFrame(int peerCount) {
            int percent = Mathf.Clamp(ValConfig.SendSchedulerMinPeersPercent.Value, 0, 100);
            return Mathf.Max(1, (peerCount * percent + 99) / 100);
        }

        private static void RecordFrame(int serviced, int sends, int pastBudget, double sendMs, bool brokeBudget) {
            LastFrameServiced = serviced;
            _servicedAccum += serviced;
            _sendsAccum += sends;
            _pastBudgetAccum += pastBudget;
            _sendMsAccum += sendMs;
            if (brokeBudget) { _budgetBreaksAccum++; }

            TotalServiced += serviced;
            TotalSends += sends;
            TotalPastBudget += pastBudget;
            TotalSendMs += sendMs;
            if (brokeBudget) { TotalBudgetBreaks++; }

            float now = Time.realtimeSinceStartup;
            if (now - _windowStart < 1f) { return; }
            ServicedLastSecond = _servicedAccum;
            BudgetBreaksLastSecond = _budgetBreaksAccum;
            PastBudgetLastSecond = _pastBudgetAccum;
            SendCostMsLastSecond = _sendsAccum > 0 ? (float)(_sendMsAccum / _sendsAccum) : 0f;
            _servicedAccum = 0;
            _budgetBreaksAccum = 0;
            _pastBudgetAccum = 0;
            _sendsAccum = 0;
            _sendMsAccum = 0d;
            _windowStart = now;
        }

        internal static void Reset() {
            _strideAccumulator = 0f;
            _strideIndex = 0;
            LastFrameServiced = 0;
            ServicedLastSecond = 0;
            BudgetBreaksLastSecond = 0;
            PastBudgetLastSecond = 0;
            SendCostMsLastSecond = 0f;
            _servicedAccum = 0;
            _budgetBreaksAccum = 0;
            _pastBudgetAccum = 0;
            _sendsAccum = 0;
            _sendMsAccum = 0d;
            _windowStart = 0f;
            TotalServiced = 0;
            TotalSends = 0;
            TotalPastBudget = 0;
            TotalBudgetBreaks = 0;
            TotalSendMs = 0d;
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
