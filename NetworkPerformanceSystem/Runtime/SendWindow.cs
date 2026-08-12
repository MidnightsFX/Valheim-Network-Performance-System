using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// Sizes the per-peer in-flight ZDO window from measured round-trip time.
    ///
    /// Vanilla hard-codes 10240 bytes in ZDOMan.SendZDOs. That number is not a queue limit - on
    /// Steam sockets GetSendQueueSize() includes m_cbSentUnackedReliable, so it is a congestion
    /// window, and throughput through a fixed window is window/RTT:
    ///
    ///     20ms  -> 512 KB/s     (well above what the transport allows anyway)
    ///     67ms  -> 153 KB/s     (exactly Valheim's pinned Steam send rate)
    ///     250ms ->  41 KB/s     (27% of it)
    ///
    /// So vanilla is correctly sized up to about 67ms and starves everything beyond, regardless of
    /// how good the distant player's connection actually is. Worse, it fails hard rather than
    /// gracefully: over the threshold SendZDOs returns false and that peer receives nothing at all
    /// that tick, which is the freeze-then-teleport symptom.
    ///
    /// Sizing by bandwidth-delay product fixes the distant peer and is a no-op for the local one
    /// by construction. That second half matters: simply raising the constant for everyone - which
    /// is what every other mod in this space does - hands a 20ms peer tens of kilobytes of standing
    /// queue, which is latency added to a player who did not have a problem.
    /// </summary>
    internal static class SendWindow {

        /// <summary>Vanilla's constant. Also our floor: we only ever open the window, never close
        /// it below what the game already allowed, so a missing RTT sample can never make a peer
        /// worse off than stock.</summary>
        internal const int VanillaWindowBytes = 10240;

        /// <summary>Last computed window per peer, for diagnostics only.</summary>
        private static readonly System.Collections.Generic.Dictionary<long, int> LastWindow =
            new System.Collections.Generic.Dictionary<long, int>();

        internal static bool TryGetLastWindow(long peerUid, out int bytes) {
            return LastWindow.TryGetValue(peerUid, out bytes);
        }

        /// <summary>
        /// Called from the rewritten IL in ZDOMan.SendZDOs, once per peer per send attempt.
        /// Must be cheap and must never throw - it sits directly in the send path.
        /// </summary>
        internal static int For(ZDOMan.ZDOPeer peer) {
            if (!PatchGuard.IsActive(Mechanism.SendWindow)) { return VanillaWindowBytes; }
            if (!ValConfig.EnableSendWindowSizing.Value) { return VanillaWindowBytes; }

            ZNetPeer netPeer = peer?.m_peer;
            if (netPeer == null) { return VanillaWindowBytes; }

            long uid = netPeer.m_uid;

            // Both sides measure their own sockets, so this works for the host sizing each client
            // and for a client sizing its own upstream to the host.
            if (!LatencyRegistry.HasMeasurement(uid)) { return VanillaWindowBytes; }

            float rttSeconds = LatencyRegistry.MeasuredRttMs(uid) / 1000f;
            float targetBytesPerSecond = ValConfig.SendWindowTargetRateKBps.Value * 1024f;
            float bdp = targetBytesPerSecond * rttSeconds * ValConfig.SendWindowBdpFactor.Value;

            int window = Mathf.Clamp(
                Mathf.RoundToInt(bdp),
                VanillaWindowBytes,
                Mathf.Max(VanillaWindowBytes, ValConfig.SendWindowMaxBytes.Value));

            LastWindow[uid] = window;
            return window;
        }

        internal static void Reset() {
            LastWindow.Clear();
        }
    }
}
