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
        /// facts and deliberately survive; only the failure streak is per-session noise.</summary>
        internal static void Reset() {
            _consecutiveDirectFailures = 0;
        }
    }
}
