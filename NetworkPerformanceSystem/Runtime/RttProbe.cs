using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Steamworks;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// Reads a peer's round-trip time out of its socket without ever throwing into ZRpc.
    ///
    /// Vanilla's accessor, ZSteamSocket.GetConnectionQuality, is hard-wired to the Steamworks
    /// *client* interface (SteamNetworkingSockets). A dedicated server only initialises the
    /// *game-server* interface, so on the mod's primary target that call throws "Steamworks is
    /// not initialized" on every ping - swallowed by ZRpc.Update's catch-all, logged without
    /// this mod's name, and leaving LatencyRegistry permanently empty. Nothing in the game calls
    /// it server-side (only the ConnectPanel UI, via ZNet.GetNetStats), so vanilla never noticed.
    ///
    /// This is the only file that touches Steamworks types; everything else goes through
    /// TryGetPingMs. The two Steam queries live in their own non-inlined methods so the types are
    /// only resolved when one is actually invoked, and anything they throw - including a
    /// type-load failure - lands in the caller's catch rather than escaping into ZRpc.
    /// </summary>
    internal static class RttProbe {

        private enum SteamApi { Unresolved, Client, GameServer }

        /// <summary>Which Steamworks interface this process has. Decided by the first successful
        /// read and then fixed for the process lifetime: which context was initialised cannot
        /// change, so a client hopping between servers does not re-probe.</summary>
        private static SteamApi _resolved = SteamApi.Unresolved;

        /// <summary>Consecutive calls on which both direct Steam paths threw. Only direct failures
        /// on a genuine ZSteamSocket count; a wrapper we could not see through may throw exactly
        /// as vanilla does, and that is never evidence that sampling itself is broken.</summary>
        private static int _consecutiveDirectFailures;
        private const int MaxConsecutiveDirectFailures = 3;

        /// <summary>Per-type lookup of a wrapper's inner-socket field (null when the type is not
        /// a wrapper). ServerSync's BufferingSocket replaces peer.m_socket for the whole config
        /// handshake - seconds to tens of seconds on a busy modpack - and exposes the real socket
        /// as a public "Original" field. Cached so reflection runs once per type, ever.</summary>
        private static readonly Dictionary<Type, FieldInfo> WrapperOriginal = new Dictionary<Type, FieldInfo>();
        private const int MaxUnwrapDepth = 4;

        /// <summary>
        /// Round-trip time for <paramref name="socket"/> in milliseconds. Never throws. False means
        /// "no RTT available right now" - PlayFab, a connection Steam has not measured yet, or a
        /// transport we cannot read - and the caller simply takes no sample this time.
        /// </summary>
        internal static bool TryGetPingMs(ISocket socket, out int pingMs) {
            pingMs = 0;
            if (socket == null || !PatchGuard.IsActive(Mechanism.RttSampling)) { return false; }

            ZSteamSocket steam = Resolve(socket, out SocketPath _, out ISocket inner);
            if (steam != null) { return TrySteam(steam, out pingMs); }

            // PlayFab, or a wrapper we could neither see through nor match to a Steam connection.
            // ZPlayFabSocket inherits ZNetStats.GetConnectionQuality, which always reports 0 -
            // LatencyRegistry.Sample rejects that, which is the existing crossplay no-measurement
            // path. The wrapper forwards to the real socket here and may throw on a dedicated server
            // exactly as vanilla does: swallowed, and deliberately not counted against the sampler.
            try {
                inner.GetConnectionQuality(out float _, out float _, out pingMs, out float _, out float _);
                return pingMs > 0;
            } catch (Exception) {
                pingMs = 0;
                return false;
            }
        }

        private static bool TrySteam(ZSteamSocket steam, out int pingMs) {
            pingMs = 0;
            if (!steam.IsConnected()) { return false; }                      // m_con invalid: closing, or not up yet

            SteamApi first = _resolved != SteamApi.Unresolved
                ? _resolved
                : (NpsEnv.IsDedicated() ? SteamApi.GameServer : SteamApi.Client);

            if (Query(first, steam, out pingMs, out Exception firstError)) { Resolve(first); return true; }
            if (firstError == null) { return false; }                        // right interface; Steam has no ping for this connection yet

            // The preferred interface is not initialised in this process - a server build started
            // without -batchmode, a client build run headless, or a game update that moved the
            // dedicated server onto the client context. Try the other one before giving up.
            SteamApi other = first == SteamApi.Client ? SteamApi.GameServer : SteamApi.Client;
            if (Query(other, steam, out pingMs, out Exception otherError)) {
                if (_resolved != other) {
                    Logger.LogInfo($"RTT sampling: the Steamworks {first} interface is not initialised in this process; using {other}.");
                }
                Resolve(other);
                return true;
            }
            if (otherError == null) { return false; }                        // other interface is live but has no ping yet; not a failure

            if (++_consecutiveDirectFailures >= MaxConsecutiveDirectFailures) {
                PatchGuard.Disable(Mechanism.RttSampling,
                    $"neither Steamworks interface will report connection status ({firstError.GetType().Name}: {firstError.Message}); "
                    + "send window, ownership arbitration and latency compensation fall back to vanilla for the rest of this process");
            }
            return false;
        }

        private static void Resolve(SteamApi api) {
            _resolved = api;
            _consecutiveDirectFailures = 0;
        }

        private static bool Query(SteamApi api, ZSteamSocket steam, out int pingMs, out Exception error) {
            error = null;
            try {
                return api == SteamApi.GameServer ? QueryGameServer(steam, out pingMs) : QueryClient(steam, out pingMs);
            } catch (Exception e) {
                // InvalidOperationException("Steamworks is not initialized." / "GameServer is not
                // initialized") for the interface this process does not have - or a type-load
                // failure if Steamworks.NET itself could not be resolved.
                pingMs = 0;
                error = e;
                return false;
            }
        }

        /// <summary>Vanilla's own accessor, so a mod that re-points it (Parrot transpiles it onto
        /// the game-server interface for dedicated servers) is respected rather than bypassed.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool QueryClient(ZSteamSocket steam, out int pingMs) {
            steam.GetConnectionQuality(out float qualityLocal, out float _, out pingMs, out float _, out float _);
            return AcceptPing(ref pingMs, qualityLocal);
        }

        /// <summary>The same SteamNetConnectionRealTimeStatus_t read vanilla does, through the
        /// game-server interface. m_con is the same handle either way; only the interface that
        /// owns it differs, and a dedicated server's sockets are owned by this one.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool QueryGameServer(ZSteamSocket steam, out int pingMs) {
            pingMs = 0;
            SteamNetConnectionRealTimeStatus_t status = default;
            SteamNetConnectionRealTimeLaneStatus_t lanes = default;
            if (SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(steam.m_con, ref status, 0, ref lanes) != EResult.k_EResultOK) {
                return false;
            }
            pingMs = status.m_nPing;
            return AcceptPing(ref pingMs, status.m_flConnectionQualityLocal);
        }

        /// <summary>Stands in for a ping Steam rounded down to 0: the lowest figure
        /// LatencyRegistry accepts, and an honest one for a connection under a millisecond.</summary>
        internal const int SubMillisecondPingMs = 1;

        /// <summary>
        /// Whether Steam's ping for a connection is a measurement, with a LAN player counted as
        /// one. Pure.
        ///
        /// Steam reports whole milliseconds, and -1 until it has measured the connection. A
        /// player on the server's own network is under a millisecond away, so it reads 0 - which
        /// used to be taken as "no figure". The arbiter prices an unmeasured peer at Unmeasured
        /// Peer RTT Ms (150 by default), so the fastest players on the server lost every creature
        /// they shared with a remote one, then pulled it back through the proximity layer the
        /// moment they were the only one near it, over and over.
        ///
        /// A 0 is believed only alongside a connection quality above zero. Steam reports -1 for
        /// that until it has end-to-end figures for the connection, and vanilla's accessor
        /// reports 0 when its status read fails, so neither can pass for a LAN player.
        /// </summary>
        internal static bool AcceptPing(ref int pingMs, float qualityLocal) {
            if (pingMs > 0) { return true; }
            if (pingMs == 0 && qualityLocal > 0f) {
                pingMs = SubMillisecondPingMs;
                return true;
            }
            return false;
        }

        /// <summary>
        /// The three numbers ZDOMan.SendZDOs cannot tell apart, plus the rate Steam paces the
        /// connection at.
        ///
        /// GetSendQueueSize - the value vanilla budgets against and the one nps_stats reported
        /// until now - sums pending and in-flight bytes into a single figure. Those two mean
        /// opposite things:
        ///
        ///   * IN FLIGHT (m_cbSentUnackedReliable) is data on the wire and not yet acknowledged.
        ///     A high number here is the bandwidth-delay product being filled, which is precisely
        ///     what the M2 window is sized to achieve. It is the goal, not a problem.
        ///   * PENDING (m_cbPendingReliable/Unreliable) is data Steam has accepted but has not put
        ///     on the wire yet, because its own rate limiter will not pass it. This is real
        ///     congestion, and it is standing queue - latency added to every subsequent update.
        ///
        /// Summed, a peer whose link is working perfectly and a peer being offered more than
        /// Steam's fixed send rate look identical. Separated, they are unmistakable.
        ///
        /// SendRateBytesPerSec is not an estimate. Steam paces each connection at a fixed rate
        /// (see SteamTransport), and this is that rate - which is what M2 sizes the window from.
        /// </summary>
        internal struct LinkStatus {
            internal int PendingBytes;         // queued in Steam, not yet sent - offered faster than the rate
            internal int InFlightBytes;        // sent, unacknowledged - the window doing its job
            internal int SendRateBytesPerSec;  // the fixed rate Steam paces this connection at
            internal float QualityLocal;       // share of the remote's packets that reached us
            internal float QualityRemote;      // share of our packets that reached the remote - loss on the way to them
            internal float OutBytesPerSec;     // what we are actually sending them now, resends included - at most the rate
            internal float InBytesPerSec;      // what they are sending us now
        }

        /// <summary>Consecutive calls on which neither interface produced a status. This read runs
        /// per peer per ping, and per peer per send tick while sampling, so a build where it simply
        /// does not work must stop costing two interop exceptions every time.</summary>
        private static int _consecutiveStatusFailures;
        private static bool _statusUnavailable;
        private const int MaxConsecutiveStatusFailures = 20;

        /// <summary>
        /// Reads the transport's real-time view of one socket. Never throws. False means the
        /// socket is not a Steam socket, is not connected, or this process has no interface that
        /// will answer - in every case the caller simply records no sample.
        /// </summary>
        internal static bool TryGetLinkStatus(ISocket socket, out LinkStatus status) {
            status = default;
            if (socket == null || _statusUnavailable) { return false; }
            if (!PatchGuard.IsActive(Mechanism.RttSampling)) { return false; }

            ZSteamSocket steam = ResolveSteam(socket, out SocketPath _);
            if (steam == null || !steam.IsConnected()) { return false; }

            // Reuse whichever interface the ping path already proved out. Before the first ping
            // has resolved that, guess from the build exactly as TrySteam does, and let a failure
            // fall through to the other one.
            SteamApi first = _resolved != SteamApi.Unresolved
                ? _resolved
                : (NpsEnv.IsDedicated() ? SteamApi.GameServer : SteamApi.Client);
            SteamApi other = first == SteamApi.Client ? SteamApi.GameServer : SteamApi.Client;

            if (TryStatus(first, steam, ref status) || TryStatus(other, steam, ref status)) {
                _consecutiveStatusFailures = 0;
                return true;
            }

            // A connected socket that will not report status on either interface is a property of
            // the build, not of the moment. Latch off rather than keep probing - this is a
            // diagnostic, and it must never cost more than the thing it measures.
            if (++_consecutiveStatusFailures >= MaxConsecutiveStatusFailures) {
                _statusUnavailable = true;
                Logger.LogInfo("Link-pressure sampling is unavailable on this build - neither Steamworks sockets " +
                               "interface reports connection status. nps_stats still shows RTT, window and queue size; " +
                               "the pending-vs-in-flight split is what is missing, and send windows are sized from the " +
                               "configured Steam rate instead of the one each connection reports.");
            }
            return false;
        }

        /// <summary>
        /// The Steam connection handle under a peer's socket, for a config write addressed to that
        /// one connection (M26). False for anything that is not a connected ZSteamSocket - a
        /// crossplay peer, or a connection already closed - and the handle is only meaningful
        /// until the socket closes; a caller that kept it must compare it with a fresh read before
        /// acting on it again.
        /// </summary>
        internal static bool TryGetConnectionHandle(ISocket socket, out uint handle) {
            handle = 0;
            ZSteamSocket steam = ResolveSteam(socket, out SocketPath _);
            if (steam == null || !steam.IsConnected()) { return false; }
            handle = steam.m_con.m_HSteamNetConnection;
            return handle != 0;
        }

        private static bool TryStatus(SteamApi api, ZSteamSocket steam, ref LinkStatus status) {
            try {
                return api == SteamApi.GameServer
                    ? StatusGameServer(steam, ref status)
                    : StatusClient(steam, ref status);
            } catch (System.Exception) {
                // The interface this process does not have, or a type-load failure. Deliberately
                // not counted against the RTT sampler's failure streak: this is a diagnostic read
                // and must never be able to stand down the mechanisms that matter.
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool StatusGameServer(ZSteamSocket steam, ref LinkStatus status) {
            SteamNetConnectionRealTimeStatus_t raw = default;
            SteamNetConnectionRealTimeLaneStatus_t lanes = default;
            if (SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(steam.m_con, ref raw, 0, ref lanes) != EResult.k_EResultOK) {
                return false;
            }
            Fill(raw, ref status);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool StatusClient(ZSteamSocket steam, ref LinkStatus status) {
            SteamNetConnectionRealTimeStatus_t raw = default;
            SteamNetConnectionRealTimeLaneStatus_t lanes = default;
            if (SteamNetworkingSockets.GetConnectionRealTimeStatus(steam.m_con, ref raw, 0, ref lanes) != EResult.k_EResultOK) {
                return false;
            }
            Fill(raw, ref status);
            return true;
        }

        // -- transport liveness ------------------------------------------------------------

        /// <summary>
        /// What the transport thinks of a connection, with Steam's ten states collapsed to the
        /// only distinction a caller can act on.
        /// </summary>
        internal enum LinkState {
            /// <summary>Not a Steam socket, or no interface in this process will answer for it.
            /// Never evidence of anything - the caller must fall back to its timer.</summary>
            Unknown,
            /// <summary>Connecting, finding a route, or connected. All three mean Steam has not
            /// given up on the link.</summary>
            Alive,
            /// <summary>Steam has given up: the peer closed it, a local problem was detected, or
            /// the handle is in one of the post-mortem states. Authoritative.</summary>
            Dead,
        }

        /// <summary>Latched when neither interface will report a connection's state, for the same
        /// reason _statusUnavailable is: which interfaces this build has cannot change, and a
        /// liveness read runs per peer per ping.</summary>
        private static bool _stateUnavailable;
        private static int _consecutiveStateFailures;
        private const int MaxConsecutiveStateFailures = 20;

        /// <summary>
        /// Steam's own verdict on whether a connection is still alive. Never throws.
        ///
        /// This exists because the game has no such test. ZSteamSocket.IsConnected answers
        /// "m_con != Invalid" - whether anyone has called Close(), not whether the link works -
        /// and ZPlayFabSocket.IsConnected reports true for CONNECTING as well as CONNECTED. So
        /// vanilla's only real liveness signal is ZRpc's ping timer, which is a static shared
        /// deadline (see ConnectionTimeout) and cannot be read per peer. Steam has known the
        /// answer the whole time; nothing asked it.
        ///
        /// Read at the ZRpc ping cadence, so this costs one Steam call per peer per second -
        /// the same order as the RTT probe alongside it.
        /// </summary>
        internal static LinkState GetLinkState(ISocket socket) {
            if (socket == null || _stateUnavailable) { return LinkState.Unknown; }
            if (!PatchGuard.IsActive(Mechanism.RttSampling)) { return LinkState.Unknown; }

            // PlayFab, and a wrapper we can neither see through nor match to a Steam connection: no
            // transport verdict is available, and saying so is the honest answer. PeerLiveness then
            // runs on its silence timer alone, which is exactly the crossplay case that needs it most.
            ZSteamSocket steam = ResolveSteam(socket, out SocketPath _);
            if (steam == null) { return LinkState.Unknown; }

            // m_con invalid means the socket has already been closed - by Steam's own
            // OnStatusChanged callback, by ZRpc's ping timeout, or by us shutting down. Vanilla's
            // UpdatePeers acts on that next frame anyway, so reporting Dead here only ever agrees
            // with the disconnect already in flight. A connection found by Steam ID is only ever
            // handed out while it is connected, so it reads Unknown rather than Dead once it closes.
            if (!steam.IsConnected()) { return LinkState.Dead; }

            SteamApi first = _resolved != SteamApi.Unresolved
                ? _resolved
                : (NpsEnv.IsDedicated() ? SteamApi.GameServer : SteamApi.Client);
            SteamApi other = first == SteamApi.Client ? SteamApi.GameServer : SteamApi.Client;

            if (TryState(first, steam, out LinkState state) || TryState(other, steam, out state)) {
                _consecutiveStateFailures = 0;
                return state;
            }

            if (++_consecutiveStateFailures >= MaxConsecutiveStateFailures) {
                _stateUnavailable = true;
                Logger.LogInfo("Transport liveness is unavailable on this build - neither Steamworks sockets "
                               + "interface reports connection state. Ghost detection falls back to its silence "
                               + "timer alone, which is slower but still correct.");
            }
            return LinkState.Unknown;
        }

        private static bool TryState(SteamApi api, ZSteamSocket steam, out LinkState state) {
            state = LinkState.Unknown;
            try {
                SteamNetConnectionRealTimeStatus_t raw = default;
                bool ok = api == SteamApi.GameServer
                    ? StateGameServer(steam, ref raw)
                    : StateClient(steam, ref raw);
                if (!ok) { return false; }
                state = Classify(raw.m_eState);
                return true;
            } catch (Exception) {
                // The interface this process does not have, or a type-load failure. Not counted
                // against the RTT sampler: this is a liveness read and must never be able to
                // stand down the mechanisms that carry traffic.
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool StateGameServer(ZSteamSocket steam, ref SteamNetConnectionRealTimeStatus_t raw) {
            SteamNetConnectionRealTimeLaneStatus_t lanes = default;
            return SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(steam.m_con, ref raw, 0, ref lanes) == EResult.k_EResultOK;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool StateClient(ZSteamSocket steam, ref SteamNetConnectionRealTimeStatus_t raw) {
            SteamNetConnectionRealTimeLaneStatus_t lanes = default;
            return SteamNetworkingSockets.GetConnectionRealTimeStatus(steam.m_con, ref raw, 0, ref lanes) == EResult.k_EResultOK;
        }

        /// <summary>
        /// Steam's connection states, split into "given up" and "has not given up".
        ///
        /// Connecting and FindingRoute are alive on purpose: a link renegotiating its route is
        /// exactly the case a ghost check must not shoot, and it is also the state a crossplay
        /// peer sits in legitimately. The negative-valued states (Dead, Linger, FinWait) are the
        /// post-mortem handles Steam keeps until the app closes them, and None means Steam has no
        /// record of this handle at all - which, for a handle we hold, is itself terminal.
        /// </summary>
        private static LinkState Classify(ESteamNetworkingConnectionState state) {
            switch (state) {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_FindingRoute:
                    return LinkState.Alive;
                default:
                    return LinkState.Dead;
            }
        }

        private static void Fill(SteamNetConnectionRealTimeStatus_t raw, ref LinkStatus status) {
            // Both pending classes are standing queue, so they are summed: unreliable pending is
            // rarer here (Valheim's ZDO traffic is reliable) but it delays the reliable stream
            // just the same when it is present.
            status.PendingBytes = raw.m_cbPendingReliable + raw.m_cbPendingUnreliable;
            status.InFlightBytes = raw.m_cbSentUnackedReliable;
            status.SendRateBytesPerSec = raw.m_nSendRateBytesPerSecond;
            status.QualityLocal = raw.m_flConnectionQualityLocal;
            status.QualityRemote = raw.m_flConnectionQualityRemote;
            status.OutBytesPerSec = raw.m_flOutBytesPerSec;
            status.InBytesPerSec = raw.m_flInBytesPerSec;
        }

        /// <summary>See through socket wrappers to the transport underneath. Steady state is the
        /// first check: a ZSteamSocket is returned before any reflection happens.</summary>
        internal static ISocket Unwrap(ISocket socket) {
            for (int depth = 0; depth < MaxUnwrapDepth; depth++) {
                if (socket is ZSteamSocket) { return socket; }
                Type type = socket.GetType();
                if (type == typeof(ZPlayFabSocket)) { return socket; }        // genuine PlayFab, not a wrapper

                FieldInfo field = OriginalField(type);
                if (field == null) { return socket; }
                if (!(field.GetValue(socket) is ISocket inner) || ReferenceEquals(inner, socket)) { return socket; }
                socket = inner;
            }
            return socket;
        }

        /// <summary>A wrapper type's inner-socket field, or null for a type that is not one.
        /// Looked up once per type and cached, null included.</summary>
        private static FieldInfo OriginalField(Type type) {
            if (!WrapperOriginal.TryGetValue(type, out FieldInfo field)) {
                field = type.GetField("Original", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && !typeof(ISocket).IsAssignableFrom(field.FieldType)) { field = null; }
                WrapperOriginal[type] = field;
            }
            return field;
        }

        // -- the Steam connection under a peer's socket ------------------------------------

        /// <summary>How the Steam connection under a peer's socket was reached. Recorded per peer
        /// by monitoring and counted in nps_stats, because Lost is otherwise invisible: a player
        /// who is connected and playing, whose connection this mod cannot read, and who is
        /// therefore priced at Unmeasured Peer RTT Ms by the arbiter and skipped by loss backoff
        /// and window sizing.</summary>
        internal enum SocketPath : byte {
            Direct,     // peer.m_socket is the ZSteamSocket itself
            Unwrapped,  // reached through wrappers' Original fields
            SteamId,    // wrapped in a way Unwrap cannot see through; matched in the game's own list by Steam ID
            Crossplay,  // a genuine PlayFab socket - there is no Steam connection to read
            Lost,       // none of the above
        }

        internal static string PathName(SocketPath path) {
            switch (path) {
                case SocketPath.Direct: return "direct";
                case SocketPath.Unwrapped: return "unwrapped";
                case SocketPath.SteamId: return "steamid";
                case SocketPath.Crossplay: return "crossplay";
                default: return "lost";
            }
        }

        /// <summary>
        /// The ZSteamSocket carrying this peer's connection, or null when there is none to read.
        ///
        /// Unwrap first. When that ends on a wrapper it cannot see through, the connection is
        /// looked up in ZSteamSocket's own list of connections by the Steam ID the wrapper reports
        /// - every wrapper has to forward GetHostName, since ZNet's ban, admin and permitted
        /// checks all read it. On a sixty-player modded server in September 2026 most players
        /// gave one reading at join and none after it, for their whole session: the likeliest
        /// explanation is something wrapping peer.m_socket after the handshake, and every one of
        /// them was priced at Unmeasured Peer RTT Ms while connected and playing. The monitoring
        /// peer_sock record says what the wrapper actually is.
        /// </summary>
        internal static ZSteamSocket ResolveSteam(ISocket socket, out SocketPath path) {
            return Resolve(socket, out path, out ISocket _);
        }

        /// <summary>ResolveSteam, also handing back the socket the walk ended on - the one
        /// TryGetPingMs falls back to asking when there is no Steam connection to read.</summary>
        private static ZSteamSocket Resolve(ISocket socket, out SocketPath path, out ISocket inner) {
            path = SocketPath.Lost;
            inner = socket;
            if (socket == null) { return null; }
            if (socket is ZSteamSocket direct) { path = SocketPath.Direct; return direct; }
            if (socket.GetType() == typeof(ZPlayFabSocket)) { path = SocketPath.Crossplay; return null; }

            // A wrapper already matched by Steam ID is answered before the walk down it, which is
            // a reflection read per layer and ends on the same wrapper every time.
            if (SteamIdLookups.TryGetValue(socket, out SteamIdLookup known)) {
                ZSteamSocket matched = known.Steam;
                if (matched != null && matched.IsConnected()) { path = SocketPath.SteamId; return matched; }
            }

            inner = Unwrap(socket);
            if (inner is ZSteamSocket steam) { path = SocketPath.Unwrapped; return steam; }
            if (inner.GetType() == typeof(ZPlayFabSocket)) { path = SocketPath.Crossplay; return null; }

            ZSteamSocket found = FromSteamId(socket);
            path = found != null ? SocketPath.SteamId : SocketPath.Lost;
            return found;
        }

        private sealed class SteamIdLookup {
            internal ZSteamSocket Steam;                    // the match, while it is connected
            internal double RetryAtMs;                      // no scan before this
            internal double RetryMs = SteamIdRetryMs;       // how long the next failure is believed
            internal bool Matched;                          // has matched once: a later miss is its connection closing
            internal bool LoggedMatch;                      // each outcome is described and logged once per wrapper
            internal bool LoggedMiss;
        }

        /// <summary>Per wrapper instance, weakly held: a wrapper belongs to one connection for its
        /// whole life, so the answer is kept until that connection closes. Every caller here reads
        /// once a second per peer, and the lookup itself is a scan of every peer and connection.
        /// One entry per wrapper, changed in place - a failure that is retried allocates nothing.</summary>
        private static readonly ConditionalWeakTable<ISocket, SteamIdLookup> SteamIdLookups =
            new ConditionalWeakTable<ISocket, SteamIdLookup>();

        private static readonly ConditionalWeakTable<ISocket, SteamIdLookup>.CreateValueCallback NewSteamIdLookup =
            _ => new SteamIdLookup();

        /// <summary>How long a failed lookup is believed before it is tried again: short at first,
        /// so a connection which settles after the handshake is picked up quickly, then doubling
        /// to the ceiling, so a wrapper that never will match - crossplay under something this
        /// mod cannot see through - costs next to nothing for the rest of its session.</summary>
        private const double SteamIdRetryMs = 5000d;
        private const double SteamIdRetryMaxMs = 60000d;

        private static readonly Stopwatch LookupClock = Stopwatch.StartNew();

        /// <summary>Latched when the game's connection list or a Steam ID cannot be read at all -
        /// a renamed field, or a missing native entry point. Which fields a build has cannot change.</summary>
        private static bool _steamIdUnavailable;

        private static ZSteamSocket FromSteamId(ISocket wrapper) {
            if (_steamIdUnavailable) { return null; }

            SteamIdLookup lookup = SteamIdLookups.GetValue(wrapper, NewSteamIdLookup);
            double now = LookupClock.Elapsed.TotalMilliseconds;

            ZSteamSocket steam = lookup.Steam;
            if (steam != null) {
                if (steam.IsConnected()) { return steam; }

                // The connection it matched has closed. ZNet drops a closed peer one a frame, so
                // this wrapper is still read for a frame or several, and there is nothing left
                // for it to match: vanilla clears the identity on Close. Not scanned for now, and
                // never logged as a failed match - the player was read, and has left.
                lookup.Steam = null;
                lookup.RetryMs = SteamIdRetryMs;
                lookup.RetryAtMs = now + SteamIdRetryMs;
                return null;
            }
            if (now < lookup.RetryAtMs) { return null; }

            ZSteamSocket found = null;
            try {
                found = FindBySteamId(wrapper);
            } catch (Exception e) when (e is MissingMemberException || e is TypeLoadException
                                        || e is DllNotFoundException || e is EntryPointNotFoundException) {
                _steamIdUnavailable = true;
                Logger.LogInfo($"RTT sampling: players whose connection is wrapped by another mod cannot be matched to their Steam connection on this build ({e.GetType().Name}); they stay unmeasured.");
                return null;
            } catch (Exception) {
                // A peer list changing under a reader, or a wrapper throwing from GetHostName.
                // Not a property of the build; tried again after the retry interval.
            }

            if (found != null) {
                lookup.Steam = found;
                lookup.Matched = true;
                lookup.RetryMs = SteamIdRetryMs;
                if (!lookup.LoggedMatch) {
                    lookup.LoggedMatch = true;
                    LogChainOnce(DescribeChain(wrapper), true);
                }
            } else {
                lookup.RetryAtMs = now + lookup.RetryMs;
                lookup.RetryMs = Math.Min(lookup.RetryMs * 2d, SteamIdRetryMaxMs);
                if (!lookup.Matched && !lookup.LoggedMiss) {
                    lookup.LoggedMiss = true;
                    LogChainOnce(DescribeChain(wrapper), false);
                }
            }
            return found;
        }

        /// <summary>The game-facing half of the lookup: builds the two ID lists
        /// MatchSteamSocket works on. Not inlined, so a build without these fields throws here,
        /// into FromSteamId's catch, instead of failing the JIT of every caller.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ZSteamSocket FindBySteamId(ISocket wrapper) {
            // A wrapper that reports no Steam ID - crossplay, another transport, a connection
            // already closed - matches nothing whatever the lists hold, so they are not built.
            ulong wanted = ReportedSteamId(wrapper);
            if (wanted == 0UL) { return null; }

            List<ZNetPeer> peers = ZNet.instance?.GetPeers();
            if (peers == null) { return null; }

            ulong[] peerIds = new ulong[peers.Count];
            int peerIndex = -1;
            for (int i = 0; i < peers.Count; i++) {
                ISocket socket = peers[i]?.m_socket;
                if (socket == null) { continue; }
                if (ReferenceEquals(socket, wrapper)) {
                    peerIndex = i;
                    peerIds[i] = wanted;
                } else {
                    // The transport itself is asked for the number its host name is made from.
                    peerIds[i] = socket is ZSteamSocket direct ? direct.m_peerID.GetSteamID().m_SteamID : ReportedSteamId(socket);
                }
            }
            if (peerIndex < 0) { return null; }

            List<ZSteamSocket> sockets = ZSteamSocket.m_sockets;
            ulong[] socketIds = new ulong[sockets.Count];
            for (int i = 0; i < sockets.Count; i++) {
                ZSteamSocket steam = sockets[i];
                socketIds[i] = steam != null && steam.IsConnected() ? steam.m_peerID.GetSteamID().m_SteamID : 0UL;
            }

            int index = MatchSteamSocket(socketIds, peerIds, peerIndex);
            return index >= 0 ? sockets[index] : null;
        }

        /// <summary>The Steam ID a socket reports as its host name, or 0 - a crossplay ID, a
        /// closed connection (vanilla clears the identity on Close), or a wrapper that throws.</summary>
        private static ulong ReportedSteamId(ISocket socket) {
            try {
                return ulong.TryParse(socket.GetHostName(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : 0UL;
            } catch (Exception) {
                return 0UL;
            }
        }

        /// <summary>
        /// Which of the game's Steam connections belongs to a peer. Pure, for the offline harness.
        /// <paramref name="socketIds"/> holds the Steam ID of every entry in ZSteamSocket's list,
        /// in its order, 0 for one that is not connected; <paramref name="peerIds"/> the Steam ID
        /// each peer's socket reports, in ZNet's peer order. Returns an index into socketIds, or -1.
        ///
        /// One connection with the peer's ID is the answer. Several - a player who rejoined while
        /// the server still holds their old, silent connection - are told apart by order:
        /// ZSteamSocket adds each accepted connection to its list as Steam reports it, and ZNet
        /// adds the peer for it in the same order, so the k-th peer with an ID owns the k-th
        /// connection with it. A connection accepted but not yet a peer is always the newest, so
        /// extra connections at the end change nothing. Fewer connections than peers means one of
        /// those peers' connections has closed and the order no longer lines up, so nothing is
        /// claimed rather than risk handing one player another's connection.
        /// </summary>
        internal static int MatchSteamSocket(IList<ulong> socketIds, IList<ulong> peerIds, int peerIndex) {
            if (socketIds == null || peerIds == null || peerIndex < 0 || peerIndex >= peerIds.Count) { return -1; }
            ulong steamId = peerIds[peerIndex];
            if (steamId == 0UL) { return -1; }

            int rank = 0;
            int peersWithId = 0;
            for (int i = 0; i < peerIds.Count; i++) {
                if (peerIds[i] != steamId) { continue; }
                if (i < peerIndex) { rank++; }
                peersWithId++;
            }

            int connections = 0;
            int match = -1;
            for (int i = 0; i < socketIds.Count; i++) {
                if (socketIds[i] != steamId) { continue; }
                if (connections == rank) { match = i; }
                connections++;
            }
            return connections >= peersWithId ? match : -1;
        }

        // -- what is wrapped around a peer's socket -----------------------------------------

        /// <summary>What sits on top of a peer's transport, outermost first. Diagnostic only: the
        /// question it answers is why a player's ping went missing, so it is built when a
        /// wrapper is first met and never on a steady-state read.</summary>
        internal struct SocketChain {
            internal string Layers;    // "BufferingSocket@ModA > BufferingSocket@ModB > ZSteamSocket"
            internal int Wrappers;     // layers walked through before the walk ended
            internal string Stop;      // steam, crossplay, no inner field, inner null, loop, too deep
            internal string Fields;    // on "no inner field": the fields that type has that could hold a socket
        }

        /// <summary>Well past MaxUnwrapDepth, so a chain Unwrap gives up on still shows its full
        /// length - which is the difference between "too many wrappers" and "a wrapper of a kind
        /// we do not recognise".</summary>
        private const int MaxDescribeDepth = 16;

        internal static SocketChain DescribeChain(ISocket socket) {
            SocketChain chain = new SocketChain { Stop = "inner null" };
            StringBuilder layers = new StringBuilder();
            List<ISocket> visited = new List<ISocket>();

            // Reflection over another mod's type, from inside probes that must not throw: a field
            // whose type sits in an assembly that mod did not ship with fails to load right here.
            try {
                ISocket current = socket;
                while (current != null) {
                    Type type = current.GetType();
                    if (layers.Length > 0) { layers.Append(" > "); }
                    layers.Append(type.Name);
                    if (current is ZSteamSocket) { chain.Stop = "steam"; break; }
                    if (type == typeof(ZPlayFabSocket)) { chain.Stop = "crossplay"; break; }
                    layers.Append('@').Append(type.Assembly.GetName().Name);

                    if (visited.Count >= MaxDescribeDepth) { chain.Stop = "too deep"; break; }
                    visited.Add(current);

                    FieldInfo field = OriginalField(type);
                    if (field == null) {
                        chain.Stop = "no inner field";
                        chain.Fields = SocketLikeFields(type);
                        break;
                    }
                    ISocket inner = field.GetValue(current) as ISocket;
                    if (inner == null) { chain.Stop = "inner null"; break; }
                    if (visited.Exists(v => ReferenceEquals(v, inner))) { chain.Stop = "loop"; break; }

                    chain.Wrappers++;
                    current = inner;
                }
            } catch (Exception) {
                chain.Stop = "describe failed";
            }

            chain.Layers = layers.ToString();
            return chain;
        }

        /// <summary>A wrapper's fields that could be holding the socket it wraps, as
        /// "name:Type" - what a future Unwrap would need to look through it.</summary>
        private static string SocketLikeFields(Type type) {
            StringBuilder fields = new StringBuilder();
            int count = 0;
            for (Type t = type; t != null && t != typeof(object) && count < 8; t = t.BaseType) {
                foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                    Type ft = field.FieldType;
                    if (!(typeof(ISocket).IsAssignableFrom(ft) || ft == typeof(object) || ft.IsInterface)) { continue; }
                    if (fields.Length > 0) { fields.Append(", "); }
                    fields.Append(field.Name).Append(':').Append(ft.Name);
                    if (++count >= 8) { break; }
                }
            }
            return count > 0 ? fields.ToString() : "none";
        }

        private static readonly HashSet<string> LoggedChains = new HashSet<string>();

        /// <summary>Distinct shapes logged per session. Several mods each wrapping in turn can
        /// leave the layers in a different order per player, so the shapes are not few.</summary>
        private const int MaxLoggedChains = 10;

        private static void LogChainOnce(SocketChain chain, bool matched) {
            bool last;
            lock (LoggedChains) {                                            // SendQueueView's readers may be off the main thread
                if (LoggedChains.Count >= MaxLoggedChains) { return; }
                if (!LoggedChains.Add(chain.Layers + "|" + chain.Stop + "|" + matched)) { return; }
                last = LoggedChains.Count == MaxLoggedChains;
            }

            string shape = $"{chain.Layers} ({chain.Wrappers} layers, stopped: {chain.Stop}"
                           + (chain.Fields != null ? $"; its fields: {chain.Fields}" : "") + ")";
            Logger.LogInfo(matched
                ? $"RTT sampling: a player's connection is wrapped by another mod as {shape}. Reading their ping from their Steam connection, found by Steam ID."
                : $"RTT sampling: a player's connection is wrapped by another mod as {shape}, and no Steam connection matches their Steam ID. They stay unmeasured and are priced at Unmeasured Peer RTT Ms.");
            if (last) {
                Logger.LogInfo("RTT sampling: further wrapped-connection shapes are not logged this session.");
            }
        }

        /// <summary>One line for nps_stats on how each peer's connection is being read, or null
        /// when every one is the transport itself - the ordinary case, with nothing to say.</summary>
        internal static string DescribeSocketPaths(List<ZNetPeer> peers) {
            int direct = 0, unwrapped = 0, steamId = 0, crossplay = 0, lost = 0;
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (peer?.m_socket == null || peer.m_uid == 0L) { continue; }
                ResolveSteam(peer.m_socket, out SocketPath path);
                switch (path) {
                    case SocketPath.Direct: direct++; break;
                    case SocketPath.Unwrapped: unwrapped++; break;
                    case SocketPath.SteamId: steamId++; break;
                    case SocketPath.Crossplay: crossplay++; break;
                    default: lost++; break;
                }
            }
            if (unwrapped + steamId + lost == 0) { return null; }
            return $"connections: {direct} direct, {unwrapped} through a wrapper, {steamId} found by Steam ID, {lost} unreadable"
                   + (crossplay > 0 ? $", {crossplay} crossplay" : "");
        }

        /// <summary>Session end. _resolved, the wrapper cache and the PatchGuard state are process
        /// facts and deliberately survive; only the failure streak is per-session noise.
        /// _statusUnavailable is a process fact too - which interfaces this build has cannot
        /// change between sessions - so it survives as well. The Steam ID lookups are held per
        /// wrapper and go with their connections; the logged shapes start over, so each session's
        /// log says what that session saw.</summary>
        internal static void Reset() {
            _consecutiveDirectFailures = 0;
            _consecutiveStatusFailures = 0;
            _consecutiveStateFailures = 0;
            lock (LoggedChains) { LoggedChains.Clear(); }
        }
    }
}
