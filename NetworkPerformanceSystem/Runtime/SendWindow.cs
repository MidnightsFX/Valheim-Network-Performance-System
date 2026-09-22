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

        /// <summary>The window each peer was given on its last send, present only while that peer
        /// is being sized. Read by the peer table, by the monitoring uploader, and by M14, which
        /// takes the part above vanilla off what other mods read - so an entry must not outlive
        /// the sizing it records.</summary>
        private static readonly System.Collections.Generic.Dictionary<long, int> LastWindow =
            new System.Collections.Generic.Dictionary<long, int>();

        internal static bool TryGetLastWindow(long peerUid, out int bytes) {
            return LastWindow.TryGetValue(peerUid, out bytes);
        }

        /// <summary>How far above vanilla this peer's window was on its last send, or 0. This is
        /// the part of its queue M14 keeps out of what other mods read.</summary>
        internal static int ExtraBytes(long peerUid) {
            return LastWindow.TryGetValue(peerUid, out int window) && window > VanillaWindowBytes
                ? window - VanillaWindowBytes
                : 0;
        }

        /// <summary>
        /// Called from the rewritten IL in ZDOMan.SendZDOs, once per peer per send attempt.
        /// Must be cheap and must never throw - it sits directly in the send path.
        /// </summary>
        internal static int For(ZDOMan.ZDOPeer peer) {
            ZNetPeer netPeer = peer?.m_peer;
            if (netPeer == null) { return VanillaWindowBytes; }

            long uid = netPeer.m_uid;
            if (!TrySize(uid, out int window)) {
                // Not sized this time: sizing switched off, stood down, or no RTT for this peer.
                // Forget any earlier window so M14 stops adjusting on the same send that stops
                // using it, rather than on the next reconnect.
                LastWindow.Remove(uid);
                return VanillaWindowBytes;
            }

            LastWindow[uid] = window;
            return window;
        }

        private static bool TrySize(long uid, out int window) {
            window = VanillaWindowBytes;
            if (!PatchGuard.IsActive(Mechanism.SendWindow)) { return false; }
            if (!ValConfig.EnableSendWindowSizing.Value) { return false; }

            // Both sides measure their own sockets, so this works for the host sizing each client
            // and for a client sizing its own upstream to the host.
            if (!LatencyRegistry.HasMeasurement(uid)) { return false; }

            float rttSeconds = LatencyRegistry.MeasuredRttMs(uid) / 1000f;
            float targetBytesPerSecond = ValConfig.SendWindowTargetRateKBps.Value * 1024f;
            float bdp = targetBytesPerSecond * rttSeconds * ValConfig.SendWindowBdpFactor.Value;

            window = Mathf.Clamp(
                Mathf.RoundToInt(bdp),
                VanillaWindowBytes,
                Mathf.Max(VanillaWindowBytes, ValConfig.SendWindowMaxBytes.Value));
            return true;
        }

        internal static void Forget(long peerUid) {
            LastWindow.Remove(peerUid);
        }

        internal static void Reset() {
            LastWindow.Clear();
        }
    }
}
