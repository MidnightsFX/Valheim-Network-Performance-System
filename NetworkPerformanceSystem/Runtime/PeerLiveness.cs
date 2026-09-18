using System.Collections.Generic;
using System.Diagnostics;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M21 - whether a peer is still there, asked per peer and answered independently of the
    /// deadline that decides when to hang up on it.
    ///
    /// The idea is taken from ClientGhostWatchdog by DreamWraith
    /// (https://github.com/dreamwraith/Valheim-ClientGhostWatchdog), which spotted that a client
    /// can keep playing into a world that is no longer there. None of its code is reused - it is a
    /// client-only mod built on ZRpc.GetTimeSinceLastPing, and the two changes below are the host
    /// half it could not reach and a better signal than the one available to it - but the
    /// observation that the game needs a second, independent opinion on liveness is theirs.
    ///
    /// What vanilla has, and why it is not enough:
    ///
    /// <list type="bullet">
    /// <item>ZSteamSocket.IsConnected returns "m_con != Invalid" - whether anything has called
    /// Close(), not whether the link works. ZPlayFabSocket.IsConnected is worse: it answers true
    /// for CONNECTING as well as CONNECTED.</item>
    /// <item>So the only real detector is ZRpc.UpdatePing, which closes the socket after
    /// m_timeout seconds of silence. m_timeout is a <b>static</b>: every ZRpc in the process
    /// shares one deadline, ZPlayFabSocket.Accept raises it to 90s for everybody the moment one
    /// crossplay socket is accepted, and M11 raises it further when an admin asks for it. There is
    /// no per-peer question to ask and no answer short of that deadline.</item>
    /// </list>
    ///
    /// Two consumers, both of which need an answer sooner than the hang-up deadline:
    ///
    /// <list type="bullet">
    /// <item><b>The host</b> (OwnershipArbiter). A peer that is gone still holds ownership of
    /// everything it was simulating, and objects an absent owner holds do not move - frozen
    /// mid-animation and, because Character.Damage routes to an owner nobody answers for,
    /// silently invulnerable. Tying that to the hang-up deadline is what made raising M11's
    /// timeout a trade rather than a free setting. It is not one: "stop trusting this peer to
    /// simulate" and "give up on this peer entirely" are different questions, and only the second
    /// one should wait 30+ seconds.</item>
    /// <item><b>The client</b> (GhostWatchdog). Same deadline, opposite end.</item>
    /// </list>
    ///
    /// <para>THE CLOCK. Silence is measured on a monotonic wall clock rather than by accumulating
    /// Time.deltaTime the way ZRpc does - but credited a bounded amount per tick. Frame-time
    /// accumulation stops during a main-thread stall, which is precisely when a link is most
    /// likely to have died; raw wall-clock time keeps counting through that stall and would
    /// declare every peer a ghost at once when the process comes back. Capping the credit per tick
    /// at <see cref="MaxTickCreditSeconds"/> takes the useful half of each: the timer tracks real
    /// time while the process is servicing the network, and a twenty-second freeze contributes one
    /// second of apparent silence rather than twenty or zero.</para>
    /// </summary>
    internal static class PeerLiveness {

        /// <summary>Longest gap between two ZNet.Update ticks that is credited as real elapsed
        /// time. Above this the process was not servicing the network, so the gap says nothing
        /// about any peer. Comfortably above a normal frame and above ZRpc's 1Hz ping interval, so
        /// it only ever binds on a genuine stall.</summary>
        private const float MaxTickCreditSeconds = 1f;

        /// <summary>Wall clock. Monotonic, unaffected by timeScale, frame pacing or the system
        /// clock being set.</summary>
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>Our stall-aware clock: advances with the wall clock while ticks are arriving
        /// at a plausible rate, and stops advancing while they are not. Every timestamp below is
        /// on this scale, not on Time.realtimeSinceStartup.</summary>
        private static float _observed;
        private static float _lastTickWall;

        /// <summary>Whether _lastTickWall holds a real reading yet. A flag rather than a negative
        /// sentinel in _lastTickWall itself: the stopwatch starts at type initialisation and
        /// legitimately reads near zero, so "is it negative" conflates "no tick yet" with "the
        /// first tick arrived very early".</summary>
        private static bool _haveTick;

        /// <summary>
        /// How often the transport is asked about a given peer. This pass is driven from
        /// ZNet.Update, which is every rendered frame; the Steam query behind GetLinkState is an
        /// interop call, and one per peer per frame is the mistake this mod calls out other
        /// networking mods for making. Once a second matches ZRpc's own ping cadence, which is the
        /// finest granularity any of this can actually resolve, and costs the same order as the
        /// RTT probe already running alongside it.
        /// </summary>
        private const float LinkPollIntervalSeconds = 1f;

        private sealed class State {
            internal float LastTrafficAt;
            internal RttProbe.LinkState Link;
            internal float NextLinkPollAt;
            internal bool Ghost;
            internal float GhostSince;
            internal bool EverAlive;
        }

        private static readonly Dictionary<long, State> Peers = new Dictionary<long, State>();

        /// <summary>Scratch for the departed-peer sweep, so the per-tick evaluation allocates
        /// nothing. A set rather than a list because the sweep is a membership test per tracked
        /// peer, and the alternative - comparing counts and skipping when they match - misses the
        /// case where one peer leaves and another joins on the same tick.</summary>
        private static readonly HashSet<long> Seen = new HashSet<long>();

        // -- diagnostics -------------------------------------------------------------------

        internal static int GhostsNow { get; private set; }
        internal static long TotalGhosted { get; private set; }
        internal static long TotalRecovered { get; private set; }

        /// <summary>Set while the local process is being credited less than real time - i.e. we
        /// are coming out of a stall. Surfaced in nps_stats because it explains a silence figure
        /// that does not match a wall clock.</summary>
        internal static long TotalStallsIgnored { get; private set; }

        /// <summary>How long this peer has been quiet, in seconds, on the stall-aware clock.
        /// Zero for a peer we have no record of.</summary>
        internal static float SilenceSeconds(long peerUid) {
            return Peers.TryGetValue(peerUid, out State state) ? _observed - state.LastTrafficAt : 0f;
        }

        internal static RttProbe.LinkState LinkStateOf(long peerUid) {
            return Peers.TryGetValue(peerUid, out State state) ? state.Link : RttProbe.LinkState.Unknown;
        }

        /// <summary>
        /// Set when every tracked peer went quiet at once and there is more than one of them.
        /// Simultaneous independent failures are vanishingly unlikely; our own uplink, or a stall
        /// long enough to outlast the eviction window, explains it in one. Acting on it would
        /// rescue the entire world to the host on the tick before everybody comes back, so
        /// eviction is suspended until at least one peer answers again.
        ///
        /// Two or more, because with a single peer the two explanations are indistinguishable -
        /// and the single-peer case is the ordinary one this feature exists for.
        /// </summary>
        internal static bool LocalFaultSuspected { get; private set; }

        /// <summary>
        /// True when this peer is present in the peer list but has stopped being a peer in every
        /// way that matters. The one question the rest of the mod asks.
        /// </summary>
        internal static bool IsGhost(long peerUid) {
            if (LocalFaultSuspected) { return false; }
            return Peers.TryGetValue(peerUid, out State state) && state.Ghost;
        }

        // -- signals -----------------------------------------------------------------------

        /// <summary>
        /// A packet arrived from this peer. Called from the ZRpc.ReceivePing postfix, which fires
        /// for both halves of the ping exchange and is therefore a 1Hz heartbeat in each
        /// direction for as long as the link carries anything at all.
        /// </summary>
        internal static void NoteTraffic(long peerUid) {
            if (peerUid == 0L) { return; }
            State state = Entry(peerUid);
            state.LastTrafficAt = _observed;
            state.EverAlive = true;
            if (state.Ghost) {
                state.Ghost = false;
                GhostsNow--;
                TotalRecovered++;
                Logger.LogInfo($"Peer {peerUid} is answering again after {_observed - state.GhostSince:F1}s; "
                               + "it is eligible to own objects once more.");
            }
        }

        /// <summary>
        /// One pass over the peer list: advance the clock, refresh each peer's transport verdict,
        /// and re-decide who is a ghost. Driven from the ZNet.Update postfix on both sides.
        /// </summary>
        internal static void Evaluate() {
            if (!NpsEnv.NetReady()) { return; }

            AdvanceClock();

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            float evictAfter = EvictAfterSeconds();

            Seen.Clear();
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (peer == null || peer.m_uid == 0L) { continue; }           // pre-handshake: no identity to track

                Seen.Add(peer.m_uid);
                State state = Entry(peer.m_uid);

                // Steam's own verdict, at the same cadence as the RTT probe rather than at frame
                // rate. Unknown for a crossplay peer or a build with no answering interface -
                // which is not evidence of anything, so the silence timer alone decides those.
                if (_observed >= state.NextLinkPollAt) {
                    state.NextLinkPollAt = _observed + LinkPollIntervalSeconds;
                    state.Link = RttProbe.GetLinkState(peer.m_socket);
                }

                bool ghost = state.EverAlive
                    && (state.Link == RttProbe.LinkState.Dead
                        || _observed - state.LastTrafficAt >= evictAfter);

                if (ghost == state.Ghost) { continue; }

                state.Ghost = ghost;
                if (ghost) {
                    state.GhostSince = _observed;
                    GhostsNow++;
                    TotalGhosted++;
                    Logger.LogInfo($"Peer {peer.m_uid} has gone quiet "
                                   + $"({_observed - state.LastTrafficAt:F1}s, transport says {state.Link}). "
                                   + "It keeps its slot; it stops being trusted to simulate anything.");
                } else {
                    GhostsNow--;
                    TotalRecovered++;
                }
            }

            PruneDeparted();

            bool allQuiet = Peers.Count > 1 && GhostsNow == Peers.Count;
            if (allQuiet != LocalFaultSuspected) {
                LocalFaultSuspected = allQuiet;
                Logger.LogWarning(allQuiet
                    ? $"All {Peers.Count} peers went quiet at once - treating that as a fault at this end rather "
                      + "than as everybody leaving. Nobody is being evicted from ownership until one answers."
                    : "Peers are answering again; ownership eviction is back in force.");
            }
        }

        /// <summary>
        /// How long a peer may be silent before it stops being trusted to simulate. Held strictly
        /// below the hang-up deadline: a value at or above it would never fire, since the peer is
        /// disconnected outright at that point and this would be answering a question nobody is
        /// still asking.
        /// </summary>
        private static float EvictAfterSeconds() {
            float configured = ValConfig.GhostOwnerEvictSeconds != null
                ? ValConfig.GhostOwnerEvictSeconds.Value
                : 10f;
            float hangUp = ConnectionTimeout.EffectiveRpcTimeoutSeconds;
            return hangUp > 0f && configured > hangUp * 0.75f ? hangUp * 0.75f : configured;
        }

        private static void AdvanceClock() {
            float wall = (float)Clock.Elapsed.TotalSeconds;
            if (!_haveTick) {                                                 // first tick: nothing to credit yet
                _haveTick = true;
                _lastTickWall = wall;
                return;
            }

            float delta = wall - _lastTickWall;
            _lastTickWall = wall;
            if (delta > MaxTickCreditSeconds) {
                TotalStallsIgnored++;
                delta = MaxTickCreditSeconds;
            }
            _observed += delta;
        }

        private static State Entry(long peerUid) {
            if (Peers.TryGetValue(peerUid, out State state)) { return state; }
            // A peer we are meeting for the first time starts its silence clock now rather than at
            // zero, or every new peer would read as having been quiet since the session began.
            state = new State { LastTrafficAt = _observed, Link = RttProbe.LinkState.Unknown };
            Peers[peerUid] = state;
            return state;
        }

        /// <summary>Drop peers that have left the peer list. ZDOMan.RemovePeer already calls
        /// Forget for a clean disconnect; this covers anything that removes a peer without going
        /// through it, so the table cannot outlive the session's peers.</summary>
        private static void PruneDeparted() {
            List<long> gone = null;
            foreach (KeyValuePair<long, State> entry in Peers) {
                if (!Seen.Contains(entry.Key)) { (gone ?? (gone = new List<long>())).Add(entry.Key); }
            }
            if (gone == null) { return; }                                     // steady state: no allocation
            for (int i = 0; i < gone.Count; i++) { Forget(gone[i]); }
        }

        internal static void Forget(long peerUid) {
            if (!Peers.TryGetValue(peerUid, out State state)) { return; }
            if (state.Ghost) { GhostsNow--; }
            Peers.Remove(peerUid);
        }

        internal static void Reset() {
            Peers.Clear();
            Seen.Clear();
            GhostsNow = 0;
            LocalFaultSuspected = false;
            _observed = 0f;
            _lastTickWall = 0f;
            _haveTick = false;
        }
    }
}
