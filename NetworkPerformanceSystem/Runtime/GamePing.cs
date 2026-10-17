using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M41 - each player's ping as the game itself experiences it, timed on the host.
    ///
    /// Every other figure this mod had was Steam's: the transport's own smoothed round trip, read
    /// out of the socket. That left three holes. A crossplay server's connections are PlayFab
    /// sockets, which report nothing, so on such a server nobody had a ping at all - every
    /// ownership decision priced every player at Unmeasured Peer RTT Ms, and latency compensation
    /// was off for everyone. A LAN player reads 0 there. And the transport's figure leaves out what
    /// a player's updates actually wait for on top of it: the send queues at both ends, and a frame
    /// on each side before anything is read.
    ///
    /// ZRpc already sends every peer a ping once a second and answers theirs, on the same ordered,
    /// reliable stream as everything else - for every client, modded or not, on every transport.
    /// So the host notes when it sends each ping (UpdatePing) and times the answer when it is read
    /// (ReceivePing). No message is added or changed; a player needs nothing installed.
    ///
    /// What is not a measurement is thrown away (PingRules.Judge): answers to pings sent while the
    /// player was still joining or behind another mod's handshake socket, round trips that span a
    /// stall of the host's own frames, players whose game is in the background, and anything far
    /// below what the transport itself says. LatencyRegistry keeps the result and decides who uses
    /// which figure.
    /// </summary>
    internal static class GamePing {

        private sealed class PingClock {
            internal readonly PingRules.PingFifo Fifo = new PingRules.PingFifo();
            internal long ReadySinceTicks;
        }

        /// <summary>Keyed by the ZRpc, not the player id: pings start before the player has an id,
        /// and a reconnecting player gets a new ZRpc and starts clean.</summary>
        private static ConditionalWeakTable<ZRpc, PingClock> _clocks = new ConditionalWeakTable<ZRpc, PingClock>();

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>A player's pings count only once they have been ready this long. Before that
        /// they are queued behind the join's own download, or held by another mod's handshake
        /// socket (ServerSync buffers everything until its config exchange is done).</summary>
        internal const double WarmupSeconds = 5d;

        private static long _lastFrameTicks;
        private static long _lastStallEndTicks;

        // For nps_stats and monitoring. Totals since the session started.
        internal static long PingsSent;
        internal static readonly long[] Verdicts = new long[PingRules.VerdictCount];
        internal static long Stalls;
        internal static double LongestStallMs;

        internal static long NowTicks => Clock.ElapsedTicks;
        internal static double NowSeconds => Clock.ElapsedTicks / (double)Stopwatch.Frequency;

        /// <summary>The in-game figure is in use: this is the host, the setting is on, and the
        /// hooks went in. Otherwise every consumer reads Steam's figure, as before 1.18.</summary>
        internal static bool Active =>
            ValConfig.UseInGamePing != null && ValConfig.UseInGamePing.Value
            && PatchGuard.IsActive(Mechanism.GamePing)
            && NpsEnv.IsHost();

        /// <summary>
        /// ZNet.Update prefix: one call per host frame. A gap of StallMs or more since the last one
        /// was a stall, and any ping already out when it ended has it inside its round trip.
        /// </summary>
        internal static void NoteFrame() {
            long now = NowTicks;
            if (_lastFrameTicks != 0L) {
                double gapMs = (now - _lastFrameTicks) * 1000d / Stopwatch.Frequency;
                if (gapMs >= PingRules.StallMs) {
                    _lastStallEndTicks = now;
                    Stalls++;
                    if (gapMs > LongestStallMs) { LongestStallMs = gapMs; }
                }
            }
            _lastFrameTicks = now;
        }

        /// <summary>ZRpc.UpdatePing has just sent the host's ping to this peer. Kept whether or not
        /// the setting is on, so switching it on finds every queue already in step.</summary>
        internal static void OnPingSent(ZRpc rpc) {
            if (rpc == null || !NpsEnv.IsHost()) { return; }

            PingClock clock = _clocks.GetValue(rpc, _ => new PingClock());
            long now = NowTicks;

            ZNetPeer peer = ZNet.instance != null ? Patches.NetworkChannelPatches.FindPeerByRpc(rpc) : null;
            bool valid = peer != null && peer.m_uid != 0L && peer.IsReady();
            if (!valid) {
                clock.ReadySinceTicks = 0L;
            } else {
                if (clock.ReadySinceTicks == 0L) { clock.ReadySinceTicks = now; }
                valid = (now - clock.ReadySinceTicks) >= (long)(WarmupSeconds * Stopwatch.Frequency);
            }

            clock.Fifo.Push(now, valid, rpc.GetSocket());
            PingsSent++;
        }

        /// <summary>
        /// ZRpc.ReceivePing has just read a ping message from this peer. A ping from them is
        /// theirs to time; an answer to ours is timed here.
        /// </summary>
        internal static void OnPingMessage(ZRpc rpc, ZPackage package, ZNetPeer peer) {
            if (rpc == null || !NpsEnv.IsHost() || !HostKeepalive.WasPong(package)) { return; }
            if (!_clocks.TryGetValue(rpc, out PingClock clock)) {
                Verdicts[(int)PingRules.PingVerdict.Unmatched]++;
                return;
            }

            long now = NowTicks;
            bool popped = clock.Fifo.TryPop(out long sent, out bool valid, out object socket, out bool skipped);
            float transportMs = peer != null ? LatencyRegistry.FreshTransportRttMs(peer.m_uid) : 0f;
            PingRules.PingVerdict verdict = PingRules.Judge(popped, skipped, valid, sent, now, Stopwatch.Frequency,
                                                           _lastStallEndTicks, ReferenceEquals(socket, rpc.GetSocket()),
                                                           ConnectionTimeout.IsInBackground(rpc), transportMs, out int ms);
            Verdicts[(int)verdict]++;
            if (verdict == PingRules.PingVerdict.Sample && peer != null && peer.m_uid != 0L) {
                LatencyRegistry.SampleGame(peer.m_uid, ms, NowSeconds);
            }
        }

        internal static long Samples => Verdicts[(int)PingRules.PingVerdict.Sample];

        internal static void Reset() {
            _clocks = new ConditionalWeakTable<ZRpc, PingClock>();
            _lastFrameTicks = 0L;
            _lastStallEndTicks = 0L;
            PingsSent = 0L;
            System.Array.Clear(Verdicts, 0, Verdicts.Length);
            Stalls = 0L;
            LongestStallMs = 0d;
        }
    }
}
