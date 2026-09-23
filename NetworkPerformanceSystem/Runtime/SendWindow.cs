using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// Sizes the per-peer in-flight ZDO window from measured round-trip time and the rate Steam
    /// actually sends to that peer at.
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
    ///
    /// The bandwidth half of the product is the rate Steam paces this connection at, read back
    /// from the transport once a second (see M8: it is a fixed rate, not an estimate, so there is
    /// nothing to guess). The window therefore follows Send Rate KBps by itself - a window sized
    /// for more than the transport sends only turns into queue, and one sized for less leaves the
    /// raised rate unused.
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

        /// <summary>The send rate that window was sized for, kept alongside it for nps_stats.</summary>
        private static readonly System.Collections.Generic.Dictionary<long, int> LastRate =
            new System.Collections.Generic.Dictionary<long, int>();

        internal static bool TryGetLastWindow(long peerUid, out int bytes) {
            return LastWindow.TryGetValue(peerUid, out bytes);
        }

        internal static bool TryGetLastRate(long peerUid, out int bytesPerSec) {
            return LastRate.TryGetValue(peerUid, out bytesPerSec);
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
            if (!TrySize(uid, out int window, out int rate)) {
                // Not sized this time: sizing switched off, stood down, or no RTT for this peer.
                // Forget any earlier window so M14 stops adjusting on the same send that stops
                // using it, rather than on the next reconnect.
                LastWindow.Remove(uid);
                LastRate.Remove(uid);
                return VanillaWindowBytes;
            }

            LastWindow[uid] = window;
            LastRate[uid] = rate;
            return window;
        }

        private static bool TrySize(long uid, out int window, out int rate) {
            window = VanillaWindowBytes;
            rate = 0;
            if (!PatchGuard.IsActive(Mechanism.SendWindow)) { return false; }
            if (!ValConfig.EnableSendWindowSizing.Value) { return false; }

            // Both sides measure their own sockets, so this works for the host sizing each client
            // and for a client sizing its own upstream to the host.
            if (!LatencyRegistry.HasMeasurement(uid)) { return false; }

            LatencyRegistry.TryGetSteamSendRate(uid, out int observed);
            rate = PickRate(observed, SteamTransport.PinnedSendRateBytesPerSec);
            window = Compute(rate, LatencyRegistry.MeasuredRttMs(uid),
                             ValConfig.SendWindowBdpFactor.Value, ValConfig.SendWindowMaxBytes.Value);
            return true;
        }

        /// <summary>
        /// Which rate to size for, most specific first: what Steam reports for this very
        /// connection, then the rate every connection is pinned to, then the game's own. The last
        /// two only matter for the second or so before a peer's first transport reading, or on a
        /// build where the status read is unavailable. Pure.
        /// </summary>
        internal static int PickRate(int observedBytesPerSec, int pinnedBytesPerSec) {
            if (observedBytesPerSec > 0) { return observedBytesPerSec; }
            if (pinnedBytesPerSec > 0) { return pinnedBytesPerSec; }
            return SteamTransport.VanillaSendRateBytesPerSec;
        }

        /// <summary>rate x RTT x factor, clamped to [vanilla, max]. Pure.</summary>
        internal static int Compute(float rateBytesPerSec, float rttMs, float bdpFactor, int maxBytes) {
            float bdp = rateBytesPerSec * (rttMs / 1000f) * bdpFactor;
            return Mathf.Clamp(
                Mathf.RoundToInt(bdp),
                VanillaWindowBytes,
                Mathf.Max(VanillaWindowBytes, maxBytes));
        }

        internal static void Forget(long peerUid) {
            LastWindow.Remove(peerUid);
            LastRate.Remove(peerUid);
        }

        internal static void Reset() {
            LastWindow.Clear();
            LastRate.Clear();
        }
    }
}
