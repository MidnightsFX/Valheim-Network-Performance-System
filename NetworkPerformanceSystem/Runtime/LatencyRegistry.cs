using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// Per-peer round-trip time, measured on the host and published to clients.
    ///
    /// Vanilla has no usable RTT: ZNet.GetServerPing() returns seconds since the last pong, not a
    /// round trip. Two measurements are kept for each peer:
    ///
    ///   * The transport's - SteamNetConnectionRealTimeStatus_t.m_nPing, Steam's own smoothed
    ///     estimate, which RttProbe reads on every ZRpc ping and answer (about twice a second)
    ///     through whichever Steamworks interface this process initialised. Vanilla's accessor,
    ///     ZSteamSocket.GetConnectionQuality, is hard-wired to the client interface and throws on a
    ///     dedicated server, where only the game-server interface exists; nothing in the game calls
    ///     it server-side, so that went unnoticed. PlayFab sockets report nothing. Both sides keep
    ///     this one: a client measures its own link to the host.
    ///   * The game's (M41, host only) - GamePing times the host's own once-a-second ZRpc ping to
    ///     its answer. Every transport has it, crossplay included, and it is what a player's
    ///     updates actually wait for: the transport's round trip plus the queues and a frame at
    ///     each end.
    ///
    /// Who uses which: ownership and latency compensation use the game's figure while Use In-Game
    /// Ping is on (OwnershipRttMs, TryGetPublishedRttMs), and Steam's otherwise. Send windows and
    /// send rates always use Steam's (the Transport accessors): they size Steam's own congestion
    /// window, and a figure that grows with the queue would grow the window with it.
    ///
    /// Every consumer treats "no figure" as a reason to fall back to vanilla behaviour, so a peer
    /// nobody can measure is never made worse off.
    /// </summary>
    internal static class LatencyRegistry {

        /// <summary>Weight of each new transport sample in the EWMA. Read about twice a second (on
        /// each ping and each answer), this settles in roughly two seconds - fast enough to track a
        /// route change, slow enough that a single spike does not move a decision.</summary>
        private const float SampleAlpha = 0.25f;

        /// <summary>RFC3550-style jitter smoothing: jitter += (|delta| - jitter) / 16.</summary>
        private const float JitterDivisor = 16f;

        /// <summary>Transport samples below this are no measurement. Samples above the maximum are
        /// a measurement of a terrible link and are counted at the maximum, so that link reads as
        /// bad rather than keeping its last good figure.</summary>
        private const int MinPlausiblePingMs = 1;
        private const int MaxPlausiblePingMs = PingRules.MaxPingMs;

        /// <summary>A transport figure with no sample for this long is not used by ownership or
        /// compensation. Send windows keep reading the last one, as they always have.</summary>
        private const float TransportFreshSeconds = (float)PingRules.FreshSeconds;

        internal sealed class PeerLatency {
            // The transport's (Steam's) figure.
            internal float EwmaMs;
            internal float JitterMs;
            internal int LastMs;
            internal float LastSampleRealtime;
            internal bool HasSample;

            /// <summary>The connection's ping with nothing queued on it; see PingBaseline.</summary>
            internal readonly PingBaseline Best = new PingBaseline();

            /// <summary>The game's figure (M41), host side only.</summary>
            internal readonly PingRules.PingEstimate Game = new PingRules.PingEstimate();
        }

        /// <summary>Live measurements, keyed by peer session id. On a client, only the host.</summary>
        private static readonly Dictionary<long, PeerLatency> Measured = new Dictionary<long, PeerLatency>();

        /// <summary>The rate Steam reports it is sending to each peer at, in bytes/sec, read at the
        /// same cadence as the RTT. Kept apart from PeerLatency so a peer with a rate but no RTT
        /// yet does not read as measured.</summary>
        private static readonly Dictionary<long, int> SteamSendRate = new Dictionary<long, int>();

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

        private static PeerLatency StateFor(long peerUid) {
            if (!Measured.TryGetValue(peerUid, out PeerLatency state)) {
                state = new PeerLatency();
                Measured[peerUid] = state;
            }
            return state;
        }

        // -- the transport's figure ----------------------------------------------------------------

        internal static void SampleTransport(long peerUid, int pingMs) {
            if (peerUid == 0L) { return; }                                    // pre-handshake, uid not assigned yet
            if (pingMs < MinPlausiblePingMs) { return; }
            if (pingMs > MaxPlausiblePingMs) { pingMs = MaxPlausiblePingMs; }

            PeerLatency state = StateFor(peerUid);
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
            state.Best.Add(state.LastSampleRealtime, pingMs);
        }

        /// <summary>The transport's ping at its best (PingBaseline) in milliseconds, or 0 until
        /// there is one. Either side: each measures its own sockets.</summary>
        internal static float TransportBaselineMs(long peerUid) {
            return Measured.TryGetValue(peerUid, out PeerLatency state) && state.Best.Value > 0f ? state.Best.Value : 0f;
        }

        /// <summary>
        /// The transport's smoothed RTT to a peer in milliseconds, or 0 when unknown. Either side,
        /// for its own sockets. Zero means "no information", and every caller must treat it as a
        /// reason to fall back to vanilla behaviour rather than as "this peer is fast".
        /// </summary>
        internal static float TransportRttMs(long peerUid) {
            return Measured.TryGetValue(peerUid, out PeerLatency state) && state.HasSample ? state.EwmaMs : 0f;
        }

        /// <summary>The transport's smoothed RTT when it was sampled within the last few seconds;
        /// 0 otherwise.</summary>
        internal static float FreshTransportRttMs(long peerUid) {
            return Measured.TryGetValue(peerUid, out PeerLatency state) && state.HasSample
                && Time.realtimeSinceStartup - state.LastSampleRealtime <= TransportFreshSeconds
                ? state.EwmaMs : 0f;
        }

        internal static void NoteSteamSendRate(long peerUid, int bytesPerSec) {
            if (peerUid == 0L || bytesPerSec <= 0) { return; }
            SteamSendRate[peerUid] = bytesPerSec;
        }

        /// <summary>The rate Steam last reported for this peer's connection. False, with 0, until
        /// the first reading - and always on crossplay, which reports none.</summary>
        internal static bool TryGetSteamSendRate(long peerUid, out int bytesPerSec) {
            return SteamSendRate.TryGetValue(peerUid, out bytesPerSec);
        }

        internal static float TransportJitterMs(long peerUid) {
            return Measured.TryGetValue(peerUid, out PeerLatency state) && state.HasSample ? state.JitterMs : 0f;
        }

        internal static bool HasTransport(long peerUid) {
            return Measured.TryGetValue(peerUid, out PeerLatency state) && state.HasSample;
        }

        /// <summary>The whole measurement for one peer - both figures. False until the transport
        /// has been sampled at least once.</summary>
        internal static bool TryGetState(long peerUid, out PeerLatency state) {
            return Measured.TryGetValue(peerUid, out state) && state.HasSample;
        }

        // -- the game's figure (M41) ------------------------------------------------------------------

        internal static void SampleGame(long peerUid, int rttMs, double now) {
            if (peerUid == 0L || rttMs <= 0) { return; }
            StateFor(peerUid).Game.Add(now, rttMs);
        }

        /// <summary>The game's measurement of one peer, host side, once it has a sample.</summary>
        internal static bool TryGetGame(long peerUid, out PingRules.PingEstimate game) {
            game = Measured.TryGetValue(peerUid, out PeerLatency state) && state.Game.Samples > 0 ? state.Game : null;
            return game != null;
        }

        // -- what each consumer reads ---------------------------------------------------------------

        /// <summary>
        /// What the ownership pass prices a peer at, host side. With Use In-Game Ping on: the
        /// game's figure at its best (PingRules.OwnershipFigure). Off: Steam's smoothed figure.
        /// Either way, a peer with nothing fresh is priced at <paramref name="unmeasuredMs"/>, not
        /// at zero - a peer nobody can measure must not win every contested object in range.
        /// </summary>
        internal static float OwnershipRttMs(long peerUid, float unmeasuredMs) {
            if (GamePing.Active) {
                Measured.TryGetValue(peerUid, out PeerLatency state);
                return PingRules.OwnershipFigure(state?.Game, GamePing.NowSeconds, unmeasuredMs);
            }
            float transport = FreshTransportRttMs(peerUid);
            return transport > 0f ? transport : unmeasuredMs;
        }

        /// <summary>
        /// The figure published for latency compensation, host side: the game's smoothed round trip
        /// with Use In-Game Ping on, Steam's otherwise - fresh in either case. False when there is
        /// none, and the peer is left out of the table.
        /// </summary>
        internal static bool TryGetPublishedRttMs(long peerUid, out float rttMs) {
            rttMs = 0f;
            if (!Measured.TryGetValue(peerUid, out PeerLatency state)) { return false; }
            if (GamePing.Active) {
                rttMs = PingRules.PublishFigure(state.Game, GamePing.NowSeconds);
                return rttMs > 0f;
            }
            rttMs = FreshTransportRttMs(peerUid);
            return rttMs > 0f;
        }

        /// <summary>
        /// RTT for any session id, usable on either side: the host reads the figure it publishes,
        /// a client reads the table the host published. Returns 0 when unknown.
        /// </summary>
        internal static float RttMs(long peerUid) {
            if (NpsEnv.IsHost()) { return TryGetPublishedRttMs(peerUid, out float ms) ? ms : 0f; }
            return Published.TryGetValue(peerUid, out int published) ? published : 0f;
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
                if (!TryGetPublishedRttMs(ownerUid, out float ownerRtt)) { return 0f; }
                return ownerRtt * 0.5f / 1000f;
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
        /// Only peers with a figure (TryGetPublishedRttMs) are included. The client treats a
        /// missing entry as "no number" and renders vanilla, whereas a published 0 would read as a
        /// 0ms peer and produce half-path compensation from a value nobody measured - every peer is
        /// unmeasured for its first seconds, and without M41 a crossplay peer never has a figure.
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
                if (Qualifies(peers[i], recipientZone, radius, out float _)) { count++; }
            }

            pkg.Write(count);
            pkg.Write(NpsEnv.LocalSessionId());                               // built host-side, so this is the host
            pkg.Write((ushort)0);                                             // host is zero hops from its own data

            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (!Qualifies(peer, recipientZone, radius, out float rtt)) { continue; }
                pkg.Write(peer.m_uid);
                pkg.Write((ushort)Mathf.Clamp(Mathf.RoundToInt(rtt), 0, ushort.MaxValue));
            }

            return pkg;
        }

        private static bool Qualifies(ZNetPeer peer, Vector2s recipientZone, int radius, out float rttMs) {
            rttMs = 0f;
            long uid = peer.m_uid;
            if (uid == 0L || !TryGetPublishedRttMs(uid, out rttMs)) { return false; }
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
            SteamSendRate.Remove(peerUid);
        }

        internal static void Reset() {
            Measured.Clear();
            SteamSendRate.Clear();
            Published.Clear();
            _hasPublishedTable = false;
            _publishedAtRealtime = 0f;
            _warnedBadTable = false;
        }
    }
}
