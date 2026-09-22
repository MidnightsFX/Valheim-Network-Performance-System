using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// Per-peer round-trip time, measured on the host and published to clients.
    ///
    /// Vanilla has no usable RTT: ZNet.GetServerPing() returns seconds since the last pong, not a
    /// round trip. The real number is the transport's own estimate
    /// (SteamNetConnectionRealTimeStatus_t.m_nPing), which RttProbe reads once per ZRpc ping
    /// through whichever Steamworks interface this process initialised. Vanilla's accessor,
    /// ZSteamSocket.GetConnectionQuality, is hard-wired to the client interface and throws on a
    /// dedicated server, where only the game-server interface exists; nothing in the game calls
    /// it server-side, so that went unnoticed. PlayFab sockets report 0.
    ///
    /// Every consumer treats "no sample" as zero and falls back to vanilla behaviour, so a peer on
    /// a transport that does not report ping is never made worse off.
    /// </summary>
    internal static class LatencyRegistry {

        /// <summary>Weight of each new sample in the EWMA. At the 1Hz ping cadence this settles
        /// in roughly 3-4 seconds - fast enough to track a route change, slow enough that a single
        /// spike does not move an ownership decision.</summary>
        private const float SampleAlpha = 0.25f;

        /// <summary>RFC3550-style jitter smoothing: jitter += (|delta| - jitter) / 16.</summary>
        private const float JitterDivisor = 16f;

        /// <summary>Samples outside this range are transport noise, not latency.</summary>
        private const int MinPlausiblePingMs = 1;
        private const int MaxPlausiblePingMs = 2000;

        internal sealed class PeerLatency {
            internal float EwmaMs;
            internal float JitterMs;
            internal int LastMs;
            internal float LastSampleRealtime;
            internal bool HasSample;
        }

        /// <summary>Host side: live measurements, keyed by peer session id.</summary>
        private static readonly Dictionary<long, PeerLatency> Measured = new Dictionary<long, PeerLatency>();

        /// <summary>Client side: the table most recently published by the host, keyed by session
        /// id. Includes the host itself at 0ms, so consumers need no special case for host-owned
        /// ZDOs.</summary>
        private static readonly Dictionary<long, int> Published = new Dictionary<long, int>();

        private static bool _hasPublishedTable;
        private static float _publishedAtRealtime;
        private static bool _warnedBadTable;

        /// <summary>The host republishes every 2 seconds; a table this many seconds old means the
        /// channel has gone quiet (host left, mod unloaded, connection dying) and its numbers can
        /// no longer be trusted. Consumers treat a stale table as no table - collapse to vanilla
        /// rather than keep compensating from frozen data.</summary>
        private const float PublishedTableTtlSeconds = 10f;

        internal static IEnumerable<KeyValuePair<long, PeerLatency>> AllMeasured => Measured;

        /// <summary>A table arrived at some point this session - i.e. the host runs this mod.</summary>
        internal static bool HasPublishedTable => _hasPublishedTable;

        /// <summary>The table is recent enough to base decisions on.</summary>
        internal static bool HasFreshTable =>
            _hasPublishedTable && Time.realtimeSinceStartup - _publishedAtRealtime < PublishedTableTtlSeconds;

        internal static float PublishedTableAgeSeconds =>
            _hasPublishedTable ? Time.realtimeSinceStartup - _publishedAtRealtime : 0f;

        /// <summary>Number of entries in the most recently published table, for diagnostics.</summary>
        internal static int PublishedEntryCount => Published.Count;

        internal static void Sample(long peerUid, int pingMs) {
            if (peerUid == 0L) { return; }                                    // pre-handshake, uid not assigned yet
            if (pingMs < MinPlausiblePingMs || pingMs > MaxPlausiblePingMs) { return; }

            if (!Measured.TryGetValue(peerUid, out PeerLatency state)) {
                state = new PeerLatency();
                Measured[peerUid] = state;
            }

            if (!state.HasSample) {
                state.EwmaMs = pingMs;
                state.JitterMs = 0f;
                state.HasSample = true;
            } else {
                state.JitterMs += (Mathf.Abs(pingMs - state.LastMs) - state.JitterMs) / JitterDivisor;
                state.EwmaMs += (pingMs - state.EwmaMs) * SampleAlpha;
            }

            state.LastMs = pingMs;
            state.LastSampleRealtime = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Smoothed RTT to a peer in milliseconds, or 0 when unknown. Host-side query - reads the
        /// live measurements. Zero means "no information", and every caller must treat it as a
        /// reason to fall back to vanilla behaviour rather than as "this peer is fast".
        /// </summary>
        internal static float MeasuredRttMs(long peerUid) {
            return Measured.TryGetValue(peerUid, out PeerLatency state) && state.HasSample ? state.EwmaMs : 0f;
        }

        internal static float MeasuredJitterMs(long peerUid) {
            return Measured.TryGetValue(peerUid, out PeerLatency state) && state.HasSample ? state.JitterMs : 0f;
        }

        internal static bool HasMeasurement(long peerUid) {
            return Measured.TryGetValue(peerUid, out PeerLatency state) && state.HasSample;
        }

        /// <summary>The whole measurement for one peer - last sample, smoothed value and jitter
        /// together. Host side. False until the peer has been sampled at least once.</summary>
        internal static bool TryGetState(long peerUid, out PeerLatency state) {
            return Measured.TryGetValue(peerUid, out state) && state.HasSample;
        }

        /// <summary>
        /// RTT for any session id, usable on either side: the host reads its own measurements, a
        /// client reads the table the host published. Returns 0 when unknown.
        /// </summary>
        internal static float RttMs(long peerUid) {
            if (NpsEnv.IsHost()) { return MeasuredRttMs(peerUid); }
            return Published.TryGetValue(peerUid, out int ms) ? ms : 0f;
        }

        /// <summary>
        /// How stale an entity owned by <paramref name="ownerUid"/> looks to us right now, in
        /// seconds. This is the one-way owner-&gt;host-&gt;viewer path:
        ///     (rtt(owner) + rtt(viewer)) / 2
        /// Returns 0 whenever we cannot justify a number, which makes M4 collapse to exact
        /// vanilla behaviour rather than guessing.
        /// </summary>
        internal static float PathStalenessSeconds(long ownerUid) {
            if (ownerUid == 0L) { return 0f; }

            long self = NpsEnv.LocalSessionId();
            if (ownerUid == self) { return 0f; }                              // we own it; nothing to compensate

            if (NpsEnv.IsHost()) {
                // A listen host renders client-owned entities too, and it already has the
                // measurements first-hand - it never receives a published table. Its own hop is
                // zero, so the whole path is the owner's half.
                if (!HasMeasurement(ownerUid)) { return 0f; }
                return MeasuredRttMs(ownerUid) * 0.5f / 1000f;
            }

            if (!HasFreshTable) { return 0f; }
            // Both halves of the path must be in the table. A just-joined peer, an owner id the
            // host no longer knows, or a peer the host has no measurement for (the host omits
            // those rather than publish a 0) is a number we cannot justify - not a fast peer.
            if (!Published.TryGetValue(ownerUid, out int ownerMs)
                || !Published.TryGetValue(self, out int selfMs)) {
                return 0f;
            }
            return (ownerMs + selfMs) * 0.5f / 1000f;
        }

        // -- wire format -------------------------------------------------------------------
        // int count, then (long sessionId, ushort rttMs) per entry. Built per recipient, so a
        // table holds the host plus the measured peers near that recipient - tens of bytes for a
        // small group, and bounded by group size rather than server size on a full server.

        private const int EntryBytes = 8 + 2;

        /// <summary>Hostile-input guard only: a legitimate table is bounded by the recipient's
        /// neighbourhood, not the server, so anything near this is garbage.</summary>
        private const int MaxTableEntries = 4096;

        /// <summary>
        /// The latency table for one recipient. Two filters, both deliberate:
        ///
        /// Only peers with a measurement are included. The client treats a missing entry as "no
        /// number" and renders vanilla, whereas a published 0 would read as a 0ms peer and
        /// produce half-path compensation from a value nobody measured - PlayFab/crossplay peers
        /// never report RTT, and every Steam peer is unmeasured for its first second or two.
        ///
        /// Only peers near the recipient are included. A viewer only renders ZDOs inside its own
        /// active area, and an owner is only ever within the activated area of a sector it owns,
        /// so an owner can be at most (activeArea + activeArea - 1) zones from any viewer that
        /// renders its objects; one more zone covers reference-position drift between updates.
        /// Entries beyond that are never consulted, and sending them to everyone made the channel
        /// cost O(peers^2) bytes on a full server.
        ///
        /// The host's own entry (0ms) and the recipient's own entry (if measured) always qualify.
        /// </summary>
        internal static ZPackage BuildTablePackage(ZNetPeer recipient) {
            ZPackage pkg = new ZPackage();
            List<ZNetPeer> peers = ZNet.instance.GetPeers();

            int radius = int.MaxValue;
            Vector2s recipientZone = default;
            if (ZoneSystem.instance != null && recipient != null) {
                // Scaled off the recipient's own simulation distance, since that is whose
                // surroundings the table is being trimmed to.
                radius = 2 * ZoneCompat.NearFor(recipient) + 1;
                recipientZone = ZoneSystem.GetZone(recipient.GetRefPos());
            }

            int count = 1;                                                    // the host's own entry
            for (int i = 0; i < peers.Count; i++) {
                if (Qualifies(peers[i], recipientZone, radius)) { count++; }
            }

            pkg.Write(count);
            pkg.Write(NpsEnv.LocalSessionId());                               // built host-side, so this is the host
            pkg.Write((ushort)0);                                             // host is zero hops from its own data

            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (!Qualifies(peer, recipientZone, radius)) { continue; }
                pkg.Write(peer.m_uid);
                pkg.Write((ushort)Mathf.Clamp(Mathf.RoundToInt(MeasuredRttMs(peer.m_uid)), 0, ushort.MaxValue));
            }

            return pkg;
        }

        private static bool Qualifies(ZNetPeer peer, Vector2s recipientZone, int radius) {
            long uid = peer.m_uid;
            if (uid == 0L || !HasMeasurement(uid)) { return false; }
            if (radius == int.MaxValue) { return true; }
            Vector2s zone = ZoneSystem.GetZone(peer.GetRefPos());
            return ZoneCompat.InActiveArea(zone, recipientZone, radius);
        }

        internal static void ApplyTablePackage(ZPackage pkg) {
            if (pkg == null) { return; }

            int count = pkg.ReadInt();
            // Validate before touching Published: a throw here is swallowed by ZRpc's handler
            // wrapper, but a half-applied table would leave us compensating from a partial
            // picture until the next publish. Reject whole and keep the previous table instead.
            if (count < 0 || count > MaxTableEntries || pkg.Size() - pkg.GetPos() < count * EntryBytes) {
                if (!_warnedBadTable) {
                    _warnedBadTable = true;
                    Logger.LogWarning($"Ignoring malformed latency table ({count} entries, {pkg.Size() - pkg.GetPos()} bytes remaining). Further occurrences this session are not logged.");
                }
                return;
            }

            Published.Clear();
            for (int i = 0; i < count; i++) {
                long uid = pkg.ReadLong();
                int rtt = pkg.ReadUShort();
                Published[uid] = rtt;
            }
            _hasPublishedTable = true;
            _publishedAtRealtime = Time.realtimeSinceStartup;
        }

        internal static void ForgetPeer(long peerUid) {
            Measured.Remove(peerUid);
        }

        internal static void Reset() {
            Measured.Clear();
            Published.Clear();
            _hasPublishedTable = false;
            _publishedAtRealtime = 0f;
            _warnedBadTable = false;
        }
    }
}
