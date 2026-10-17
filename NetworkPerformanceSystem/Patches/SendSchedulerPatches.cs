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
    /// Budget and share are both per frame, so neither caps the CPU the sends take per second -
    /// and on a dedicated server at 30 fps a 0.033s interval owes every peer a send every frame.
    /// The share then takes ceil(N/4) sends each frame whatever they cost: 20 players in big
    /// bases at 3 ms a send is 15 ms of every frame, near half a core, and it grows with frame
    /// rate. So there is a third limit, per second (Max Send CPU Percent): the interval is
    /// stretched until a full round - every peer once, at the measured cost of one send - fits
    /// inside the allowed share of each second. The accumulator owes sends at that interval, so
    /// budget and share only ever spend what the ceiling allowed, and under it the per-peer rate
    /// is share/(N x cost) with no frame-rate term at all. The cost is the last second's mean,
    /// each send's part capped so that a GC pause landing inside one send cannot throttle
    /// everyone for the next second.
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
        internal static float CpuPercentLastSecond;      // share of the second spent sending
        private static int _servicedAccum;
        private static int _budgetBreaksAccum;
        private static int _pastBudgetAccum;
        private static int _sendsAccum;
        private static double _sendMsAccum;
        private static double _ceilingMsAccum;
        private static float _windowStart;

        /// <summary>Cost of one send the CPU ceiling is computed from: the last second that sent
        /// anything, each send capped by CeilingSampleMs. 0 until the first such second, and then
        /// the ceiling waits for it.</summary>
        internal static float CeilingCostMs;

        /// <summary>The interval this frame's sends were owed at - Send Interval Seconds, or longer
        /// while the CPU ceiling holds the rate down. 0 before the first frame.</summary>
        private static float _currentInterval;

        /// <summary>A send may count for at most this many times the larger of the current cost
        /// and the frame budget. A real jump - a player walking into a big base - is tracked within
        /// a second or two; a GC pause inside one send is not believed.</summary>
        internal const float HitchCapFactor = 4f;

        /// <summary>The ceiling never stretches the interval past this. Past it a full round costs
        /// more than the share and the CPU goes over the limit, but every player still gets an
        /// update a second, and a wildly wrong cost figure cannot silence everyone while the
        /// sends that would correct it are themselves held back.</summary>
        internal const float MaxCeilingIntervalSeconds = 1f;

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

            float interval = CeilingIntervalSeconds(Mathf.Max(0.01f, ValConfig.SendIntervalSeconds.Value), count,
                                                    CeilingCostMs, ValConfig.SendSchedulerMaxCpuPercent.Value);
            _currentInterval = interval;
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
            double ceilingMs = 0d;
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
                double before = FrameWatch.Elapsed.TotalMilliseconds;
                __instance.SendZDOs(peer, false);
                ceilingMs += CeilingSampleMs(FrameWatch.Elapsed.TotalMilliseconds - before, CeilingCostMs, budgetMs);
                sends++;
                if (overBudget) { pastBudget++; }
            }

            // Whatever the budget cut off is still owed. Adding it back keeps the long-run
            // per-peer rate honest when load is bursty; the clamp to one round means a sustained
            // overload is paid down at "everyone once per frame" at most, never compounding.
            _strideAccumulator = Mathf.Min(_strideAccumulator + (toService - serviced), count);

            RecordFrame(serviced, sends, pastBudget, FrameWatch.Elapsed.TotalMilliseconds, ceilingMs, brokeBudget);
            return false;
        }

        /// <summary>
        /// The interval every peer is owed a send at: the configured one, stretched until a full
        /// round - every peer once, at the cost of one send - takes no more than the allowed
        /// percent of a second, and never past MaxCeilingIntervalSeconds. With the limit off, no
        /// peers, or no cost measured yet, the configured interval stands. Pure.
        /// </summary>
        internal static float CeilingIntervalSeconds(float configuredSeconds, int peerCount, float sendCostMs, int maxCpuPercent) {
            if (maxCpuPercent <= 0 || peerCount <= 0 || !(sendCostMs > 0f)) { return configuredSeconds; }
            float roundSeconds = peerCount * sendCostMs / 1000f;
            float stretched = roundSeconds * 100f / Mathf.Min(maxCpuPercent, 100);
            return Mathf.Max(configuredSeconds, Mathf.Min(stretched, MaxCeilingIntervalSeconds));
        }

        /// <summary>What one send counts for in the ceiling's cost figure: its wall time, but at
        /// most HitchCapFactor times the larger of the current figure and the frame budget. The
        /// budget term lets the very first second, with no figure yet, be capped too. Pure.</summary>
        internal static double CeilingSampleMs(double sendMs, float costMs, double budgetMs) {
            return System.Math.Min(sendMs, HitchCapFactor * System.Math.Max(costMs, budgetMs));
        }

        /// <summary>The ceiling's cost figure after a second: the mean of that second's capped
        /// sends, or the old figure if nothing was sent. Pure.</summary>
        internal static float NextCeilingCostMs(float previousMs, double cappedMsTotal, int sends) {
            return sends > 0 ? (float)(cappedMsTotal / sends) : previousMs;
        }

        /// <summary>The interval the scheduler is sending at: what the last frame was owed at, or
        /// Send Interval Seconds before the first one. Read by the send window, which has to
        /// carry a whole interval of data, and by nps_stats.</summary>
        internal static float CurrentIntervalSeconds =>
            _currentInterval > 0f ? _currentInterval : Mathf.Max(0.01f, ValConfig.SendIntervalSeconds.Value);

        /// <summary>The fewest peers a frame services before the budget may stop it: the configured
        /// share of the peer list, rounded up, and never less than one.</summary>
        internal static int MinPeersPerFrame(int peerCount) {
            int percent = Mathf.Clamp(ValConfig.SendSchedulerMinPeersPercent.Value, 0, 100);
            return Mathf.Max(1, (peerCount * percent + 99) / 100);
        }

        private static void RecordFrame(int serviced, int sends, int pastBudget, double sendMs, double ceilingMs, bool brokeBudget) {
            LastFrameServiced = serviced;
            _servicedAccum += serviced;
            _sendsAccum += sends;
            _pastBudgetAccum += pastBudget;
            _sendMsAccum += sendMs;
            _ceilingMsAccum += ceilingMs;
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
            CpuPercentLastSecond = (float)(_sendMsAccum / ((now - _windowStart) * 1000d) * 100d);
            CeilingCostMs = NextCeilingCostMs(CeilingCostMs, _ceilingMsAccum, _sendsAccum);
            _servicedAccum = 0;
            _budgetBreaksAccum = 0;
            _pastBudgetAccum = 0;
            _sendsAccum = 0;
            _sendMsAccum = 0d;
            _ceilingMsAccum = 0d;
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
            CpuPercentLastSecond = 0f;
            CeilingCostMs = 0f;
            _currentInterval = 0f;
            _servicedAccum = 0;
            _budgetBreaksAccum = 0;
            _pastBudgetAccum = 0;
            _sendsAccum = 0;
            _sendMsAccum = 0d;
            _ceilingMsAccum = 0d;
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
