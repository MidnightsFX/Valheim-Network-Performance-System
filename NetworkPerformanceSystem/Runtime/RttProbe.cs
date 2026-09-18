using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
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

            socket = Unwrap(socket);
            if (socket is ZSteamSocket steam) { return TrySteam(steam, out pingMs); }

            // PlayFab or an unknown transport. ZPlayFabSocket inherits ZNetStats.GetConnectionQuality,
            // which always reports 0 - LatencyRegistry.Sample rejects that, which is the existing
            // crossplay no-measurement path. A wrapper we could not see through forwards to the real
            // socket here and may throw on a dedicated server exactly as vanilla does: swallowed, and
            // deliberately not counted against the sampler.
            try {
                socket.GetConnectionQuality(out float _, out float _, out pingMs, out float _, out float _);
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
            steam.GetConnectionQuality(out float _, out float _, out pingMs, out float _, out float _);
            return pingMs > 0;
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
            return pingMs > 0;
        }

        /// <summary>
        /// The three numbers ZDOMan.SendZDOs cannot tell apart, plus Steam's own rate estimate.
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
        /// Summed, a peer whose link is working perfectly and a peer being overdriven into
        /// bufferbloat look identical. Separated, they are unmistakable, which is the whole reason
        /// this read exists: the M2 window is currently open-loop - RTT times a *configured*
        /// target rate - so nothing in the mod can otherwise tell that the target is set above
        /// what a given link will actually carry.
        /// </summary>
        internal struct LinkStatus {
            internal int PendingBytes;         // queued in Steam, not yet sent - congestion
            internal int InFlightBytes;        // sent, unacknowledged - the window doing its job
            internal int SendRateBytesPerSec;  // Steam's own bandwidth estimate for this connection
            internal float QualityLocal;
            internal float QualityRemote;
        }

        /// <summary>
        /// Reads the transport's real-time view of one socket. Never throws. False means the
        /// socket is not a Steam socket, is not connected, or this process has no interface that
        /// will answer - in every case the caller simply records no sample.
        /// </summary>
        /// <summary>Consecutive calls on which neither interface produced a status. This read runs
        /// per peer per send tick while sampling, so a build where it simply does not work must
        /// stop costing two interop exceptions every time rather than paying them at 20Hz.</summary>
        private static int _consecutiveStatusFailures;
        private static bool _statusUnavailable;
        private const int MaxConsecutiveStatusFailures = 20;

        internal static bool TryGetLinkStatus(ISocket socket, out LinkStatus status) {
            status = default;
            if (socket == null || _statusUnavailable) { return false; }
            if (!PatchGuard.IsActive(Mechanism.RttSampling)) { return false; }

            if (!(Unwrap(socket) is ZSteamSocket steam)) { return false; }
            if (!steam.IsConnected()) { return false; }

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
                               "the pending-vs-in-flight split is what is missing.");
            }
            return false;
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

            // PlayFab and anything we cannot see through: no transport verdict is available, and
            // saying so is the honest answer. PeerLiveness then runs on its silence timer alone,
            // which is exactly the crossplay case that needs it most.
            if (!(Unwrap(socket) is ZSteamSocket steam)) { return LinkState.Unknown; }

            // m_con invalid means the socket has already been closed - by Steam's own
            // OnStatusChanged callback, by ZRpc's ping timeout, or by us shutting down. Vanilla's
            // UpdatePeers acts on that next frame anyway, so reporting Dead here only ever agrees
            // with the disconnect already in flight.
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
        }

        /// <summary>See through socket wrappers to the transport underneath. Steady state is the
        /// first check: a ZSteamSocket is returned before any reflection happens.</summary>
        private static ISocket Unwrap(ISocket socket) {
            for (int depth = 0; depth < MaxUnwrapDepth; depth++) {
                if (socket is ZSteamSocket) { return socket; }
                Type type = socket.GetType();
                if (type == typeof(ZPlayFabSocket)) { return socket; }        // genuine PlayFab, not a wrapper

                if (!WrapperOriginal.TryGetValue(type, out FieldInfo field)) {
                    field = type.GetField("Original", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field != null && !typeof(ISocket).IsAssignableFrom(field.FieldType)) { field = null; }
                    WrapperOriginal[type] = field;                           // null is cached too
                }
                if (field == null) { return socket; }
                if (!(field.GetValue(socket) is ISocket inner) || ReferenceEquals(inner, socket)) { return socket; }
                socket = inner;
            }
            return socket;
        }

        /// <summary>Session end. _resolved, the wrapper cache and the PatchGuard state are process
        /// facts and deliberately survive; only the failure streak is per-session noise.
        /// _statusUnavailable is a process fact too - which interfaces this build has cannot
        /// change between sessions - so it survives as well.</summary>
        internal static void Reset() {
            _consecutiveDirectFailures = 0;
            _consecutiveStatusFailures = 0;
            _consecutiveStateFailures = 0;
        }
    }
}
