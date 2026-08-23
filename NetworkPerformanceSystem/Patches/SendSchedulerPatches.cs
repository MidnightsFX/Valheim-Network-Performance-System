using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Collections.Generic;
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
    /// This is the one mechanism here that is not novel - ReturnToSender, VBNetTweaks and SkadiNet
    /// all fix it and agree on the shape. We carry it because we are standalone.
    /// </summary>
    [HarmonyPatch]
    internal static class SendSchedulerPatches {

        private static bool _checkedForReturnToSender;

        /// <summary>Fractional peers owed a send, accumulated per frame. Clamped to one full
        /// round so a hitch is repaid as at most one burst, not several.</summary>
        private static float _strideAccumulator;
        private static int _strideIndex;

        [HarmonyPatch(typeof(ZDOMan), "SendZDOToPeers2")]
        [HarmonyPrefix]
        private static bool ReplaceRoundRobin(ZDOMan __instance, float dt) {
            EnsureReturnToSenderCheck();

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

            for (int i = 0; i < toService; i++) {
                if (_strideIndex >= count) { _strideIndex = 0; }
                ZDOMan.ZDOPeer peer = peers[_strideIndex];
                _strideIndex++;

                if (peer?.m_peer?.m_socket == null || !peer.m_peer.m_socket.IsConnected()) { continue; }
                __instance.SendZDOs(peer, false);
            }

            return false;
        }

        internal static void Reset() {
            _strideAccumulator = 0f;
            _strideIndex = 0;
        }

        /// <summary>
        /// ReturnToSender transpiles ZDOMan.Update to redirect the SendZDOToPeers2 call to its own
        /// all-peers loop, so our prefix would never fire. It already does this job correctly -
        /// stand down rather than leave dead code that looks active in the log.
        ///
        /// Checked lazily on first tick rather than at Awake because BepInEx populates
        /// Chainloader.PluginInfos incrementally as plugins load, so a plugin ordered after us is
        /// not visible from our own Awake.
        /// </summary>
        private static void EnsureReturnToSenderCheck() {
            if (_checkedForReturnToSender) { return; }
            _checkedForReturnToSender = true;

            if (PatchGuard.IsPluginLoaded(PatchGuard.ReturnToSenderGUID)) {
                PatchGuard.Disable(Mechanism.SendScheduler,
                    "ReturnToSender is installed and already services every peer per tick. " +
                    "Standing down so there is exactly one scheduler in the send path.");
            }
        }
    }
}
