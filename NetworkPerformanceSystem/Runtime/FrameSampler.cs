using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M34 - each player's game measures its own frame rate and tells the host every two seconds,
    /// so the host can stop handing creatures to a machine that cannot run them (PeerCapacity).
    ///
    /// The window is filled from the per-frame ZNet.Update postfix the rest of the mod already
    /// runs, so it costs an add and a compare per frame. Alongside the frame rate it records the
    /// worst frame, the share of time spent in hitches, how many creature AIs this machine owns,
    /// and - when FrameSamplerPatches could hook MonoUpdaters.FixedUpdate - how long the game's
    /// fixed-step update took and how many steps ran. Those last two are for reading, not for
    /// deciding: see FrameReport.
    ///
    /// A client only sends while the host is known to run this mod (its latency table arrived in
    /// the last ten seconds), and an older or vanilla host would drop the message unread anyway.
    /// A listen host files its own report directly. A dedicated server has no frames worth
    /// reporting and does nothing here.
    ///
    /// The report also says whether the game is the active window, and that one goes out the
    /// moment it changes rather than at the next two-second mark: on some machines the game stops
    /// running entirely while it is alt-tabbed, and the host keeps such a player for longer only
    /// if it heard before they went quiet (ConnectionTimeout, Keep Players In The Background).
    /// The send hands the message to Steam, whose own thread delivers it even if this one stops
    /// straight after.
    /// </summary>
    internal static class FrameSampler {

        private const float ReportIntervalSeconds = 2f;

        private static readonly FrameWindow Window = new FrameWindow();
        private static float _elapsed;
        private static bool _ticking;

        private static bool _wasFocused = true;
        private static bool _focusReportPending;
        private static float _sinceSent;

        /// <summary>Set by FrameSamplerPatches when MonoUpdaters.FixedUpdate is hooked.</summary>
        internal static bool SimTimingAvailable;

        internal static long ReportsSent;
        internal static FrameReport LastReport;
        internal static bool HasLastReport;

        internal static bool Active => PatchGuard.IsActive(Mechanism.FrameReport) && !NpsEnv.IsDedicated();

        /// <summary>Once per frame while connected, from NetworkChannelPatches.Tick.</summary>
        internal static void Tick(float dt) {
            if (!Active) { return; }
            _ticking = true;

            bool focused = Application.isFocused;
            Window.Add(dt);
            if (IsLoading()) { Window.MarkFlags(CreatureLoadRules.FlagLoading); }
            if (!focused) { Window.MarkFlags(CreatureLoadRules.FlagUnfocusedFrames); }

            if (focused != _wasFocused) {
                _wasFocused = focused;
                _focusReportPending = true;
            }

            _elapsed += dt;
            _sinceSent += dt;
            bool focusDue = _focusReportPending && _sinceSent >= CreatureLoadRules.MinFocusReportSeconds;
            if (_elapsed < ReportIntervalSeconds && !focusDue) { return; }
            _elapsed = 0f;

            if (Window.Frames == 0) {
                Window.Reset();
                return;
            }

            byte flags = focused ? (byte)0 : CreatureLoadRules.FlagUnfocused;
            FrameReport report = Window.Snapshot(CountOwnedAi(), flags, Application.targetFrameRate, SimTimingAvailable);
            Window.Reset();
            _focusReportPending = false;
            _sinceSent = 0f;

            LastReport = report;
            HasLastReport = true;
            Deliver(report);
        }

        /// <summary>One MonoUpdaters.FixedUpdate, and how long it took.</summary>
        internal static void NoteFixedStep(double milliseconds) {
            if (_ticking) { Window.AddSim(milliseconds); }
        }

        private static void Deliver(FrameReport report) {
            if (NpsEnv.IsHost()) {
                PeerCapacity.OnReport(NpsEnv.LocalSessionId(), report, Time.realtimeSinceStartup);
                return;
            }
            if (!LatencyRegistry.HasFreshTable) { return; }

            ZNetPeer server = ZNet.instance.GetServerPeer();
            if (server == null || !server.IsReady() || server.m_rpc == null) { return; }

            ZPackage pkg = new ZPackage();
            pkg.Write(CreatureLoadRules.Encode(report));
            server.m_rpc.Invoke(PeerCapacity.RpcClientLoad, pkg);
            ReportsSent++;
        }

        /// <summary>No character yet, dead and waiting to respawn, or mid-teleport: the frames
        /// are the loading screen's, not the world's.</summary>
        private static bool IsLoading() {
            Player player = Player.m_localPlayer;
            return player == null || player.IsTeleporting();
        }

        private static int CountOwnedAi() {
            List<BaseAI> instances = BaseAI.BaseAIInstances;
            int owned = 0;
            for (int i = 0; i < instances.Count; i++) {
                BaseAI ai = instances[i];
                if (ai == null) { continue; }
                ZNetView view = ai.m_nview;
                if (view != null && view.IsValid() && view.IsOwner()) { owned++; }
            }
            return owned;
        }

        internal static void Reset() {
            Window.Reset();
            _elapsed = 0f;
            _ticking = false;
            HasLastReport = false;
            _wasFocused = true;
            _focusReportPending = false;
            _sinceSent = 0f;
        }
    }
}
