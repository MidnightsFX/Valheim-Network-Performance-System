using System;
using Steamworks;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M11 - how long a link may go quiet before either end hangs up.
    ///
    /// Vanilla enforces this at two layers that do not know about each other, and a peer dies at
    /// whichever one fires first:
    /// <list type="bullet">
    /// <item>ZRpc runs a 1Hz application-level ping and closes the socket after m_timeout seconds
    /// without a reply - 30s normally, raised to 90s by ZRpc.SetLongTimeout, which vanilla calls
    /// only for PlayFab sockets. This is a static, so both ends enforce their own copy.</item>
    /// <item>Steam's TimeoutConnected, which ZSteamSocket.RegisterGlobalCallbacks pins to 30000ms
    /// at Global scope, and TimeoutInitial - Steam's own 10000ms default, covering the handshake
    /// before a connection exists, which vanilla never writes at all.</item>
    /// </list>
    ///
    /// The failure this exists for is a join that stalls: a player on a weak or distant link, or a
    /// heavily modded server whose main thread blocks long enough that no ping goes out, and the
    /// two ends give up on each other mid-handshake or mid-world-transfer. Nothing here makes such
    /// a connection faster - it only stops both layers from calling it dead while it is still
    /// working. Which is why the two are moved together: the effective timeout is the LOWER of the
    /// two, so raising the ZRpc timeout to 120s while Steam still hangs up at 30s buys nothing at
    /// all, and offering them as separate numbers would mostly produce that pairing.
    ///
    /// The cost of raising it is real and falls on the host: a peer that is genuinely gone now
    /// holds its slot, and keeps ownership of everything it was simulating, for the configured
    /// time instead of 30 seconds. Objects an absent owner holds do not move. So this is sized to
    /// the worst connection you actually want to keep, not set high as a matter of course.
    ///
    /// Everything is written after vanilla has written its own - a postfix on each seam - and read
    /// back, so a refused write is visible in the log rather than assumed to have worked.
    /// </summary>
    internal static class ConnectionTimeout {

        /// <summary>ZRpc's own default, and what SetLongTimeout(false) writes.</summary>
        internal const float VanillaRpcTimeoutSeconds = 30f;

        /// <summary>What SetLongTimeout(true) writes. Vanilla uses it for PlayFab sockets only.</summary>
        internal const float VanillaRpcLongTimeoutSeconds = 90f;

        /// <summary>What ZSteamSocket.RegisterGlobalCallbacks pins TimeoutConnected to.</summary>
        internal const int VanillaSteamConnectedMillis = 30000;

        /// <summary>Steam's own TimeoutInitial default. Vanilla never writes this key, so this is
        /// what we restore it to rather than what we found there.</summary>
        internal const int VanillaSteamInitialMillis = 10000;

        private const int MillisPerSecond = 1000;

        /// <summary>Whether vanilla last asked for the long timeout. Tracked rather than read back
        /// out of ZRpc because we overwrite the field vanilla sets, so its current value no longer
        /// says which mode the game thinks it is in.</summary>
        private static bool _longMode;

        /// <summary>Set once SetLongTimeout has run, so a config edit that arrives before ZNet
        /// starts defers instead of writing a value the next SetLongTimeout would overwrite.</summary>
        private static bool _rpcSeamSeen;

        /// <summary>Set once RegisterGlobalCallbacks has run, for the same reason.</summary>
        private static bool _steamUp;

        /// <summary>What ZRpc is enforcing now, for nps_stats.</summary>
        internal static float EffectiveRpcTimeoutSeconds { get; private set; } = VanillaRpcTimeoutSeconds;

        /// <summary>The Steam readback from the last apply, for nps_stats. Null until we have run
        /// against a Steam interface.</summary>
        internal static string LastSteamReadback { get; private set; }

        /// <summary>Suppresses repeats of the RPC log line: ZPlayFabSocket.Accept calls
        /// SetLongTimeout once per accepted socket, and re-logging an unchanged value per join
        /// would be noise.</summary>
        private static float _lastLoggedRpcSeconds = -1f;

        /// <summary>
        /// True when the admin has left the mechanism switched on and its patches survived.
        /// When false every value below resolves to vanilla's, so disabling it live puts the game
        /// back rather than freezing it at whatever was last written.
        /// </summary>
        internal static bool Active {
            get {
                return PatchGuard.IsActive(Mechanism.ConnectionTimeout)
                       && ValConfig.EnableConnectionTimeoutTuning != null
                       && ValConfig.ConnectionTimeoutSeconds != null
                       && ValConfig.ConnectTimeoutSeconds != null
                       && ValConfig.LoadingTimeoutSeconds != null
                       && ValConfig.EnableConnectionTimeoutTuning.Value;
            }
        }

        /// <summary>
        /// The loading-phase timeout in force. Floored at the ordinary one: a "long" timeout below
        /// the normal one would mean a crossplay peer gets LESS slack during the world transfer
        /// than it gets while idle, which is the opposite of what that value is for and is the one
        /// pairing an operator can produce here by accident.
        /// </summary>
        internal static float EffectiveLoadingTimeoutSeconds {
            get {
                return Active
                    ? Math.Max(ValConfig.LoadingTimeoutSeconds.Value, ValConfig.ConnectionTimeoutSeconds.Value)
                    : VanillaRpcLongTimeoutSeconds;
            }
        }

        /// <summary>Called from the ZRpc.SetLongTimeout postfix - the one place vanilla writes
        /// that field, on both builds, and it runs from ZNet.Start before any peer exists.</summary>
        internal static void OnVanillaRpcTimeoutSet(bool longMode) {
            _longMode = longMode;
            _rpcSeamSeen = true;
            ApplyRpc(longMode ? "SetLongTimeout(true)" : "SetLongTimeout(false)");
        }

        /// <summary>Called from the ZSteamSocket.RegisterGlobalCallbacks postfix, after vanilla
        /// has pinned TimeoutConnected.</summary>
        internal static void OnGlobalCallbacksRegistered() {
            _steamUp = true;
            ApplySteam("RegisterGlobalCallbacks");
        }

        /// <summary>
        /// Called from the SettingChanged handlers. Both layers are process-global and re-writable
        /// at any time, so an edit takes effect without a restart - including the edit Jotunn makes
        /// on a client when the server pushes its own value down at join time.
        /// </summary>
        internal static void OnConfigChanged() {
            if (_rpcSeamSeen) { ApplyRpc("config changed"); }
            if (_steamUp) { ApplySteam("config changed"); }
        }

        internal static void Reset() {
            _longMode = false;
            _lastLoggedRpcSeconds = -1f;
        }

        /// <summary>
        /// Overwrites the value vanilla just wrote into ZRpc's static timeout. Read back out of
        /// the field rather than assumed: this is the half with no Steam call to refuse it, but it
        /// is also the half that would silently do nothing if the field were ever renamed.
        /// </summary>
        private static void ApplyRpc(string reason) {
            float want = Active
                ? (_longMode ? EffectiveLoadingTimeoutSeconds : ValConfig.ConnectionTimeoutSeconds.Value)
                : (_longMode ? VanillaRpcLongTimeoutSeconds : VanillaRpcTimeoutSeconds);

            float before = ZRpc.m_timeout;
            ZRpc.m_timeout = want;
            EffectiveRpcTimeoutSeconds = ZRpc.m_timeout;

            if (EffectiveRpcTimeoutSeconds == _lastLoggedRpcSeconds) { return; }
            _lastLoggedRpcSeconds = EffectiveRpcTimeoutSeconds;

            Logger.LogInfo($"Connection timeout ({reason}): ZRpc ping timeout {before}s -> {EffectiveRpcTimeoutSeconds}s" +
                           (_longMode ? " [loading phase]" : ""));
        }

        /// <summary>
        /// Writes the two Steam-level timeouts at Global scope. Both keys are int32 milliseconds;
        /// vanilla writes TimeoutConnected as a float, which Steam converts, so a read back as
        /// int32 returns the same number either way.
        /// </summary>
        private static void ApplySteam(string reason) {
            if (!SteamNetConfig.Available) { return; }   // reports itself once, then goes quiet

            int wantInitial = Active
                ? ValConfig.ConnectTimeoutSeconds.Value * MillisPerSecond
                : VanillaSteamInitialMillis;
            int wantConnected = Active
                ? ValConfig.ConnectionTimeoutSeconds.Value * MillisPerSecond
                : VanillaSteamConnectedMillis;

            int beforeInitial = ReadOr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutInitial, VanillaSteamInitialMillis);
            int beforeConnected = ReadOr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected, VanillaSteamConnectedMillis);

            SteamNetConfig.TryWrite(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutInitial, wantInitial);
            SteamNetConfig.TryWrite(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected, wantConnected);

            int afterInitial = ReadOr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutInitial, beforeInitial);
            int afterConnected = ReadOr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected, beforeConnected);

            string readback =
                $"TimeoutInitial {Sec(beforeInitial)} -> {Sec(afterInitial)}, " +
                $"TimeoutConnected {Sec(beforeConnected)} -> {Sec(afterConnected)}";

            if (readback == LastSteamReadback) { return; }
            LastSteamReadback = readback;

            Logger.LogInfo($"Connection timeout ({SteamNetConfig.InterfaceName}, {reason}): {readback}");
        }

        private static int ReadOr(ESteamNetworkingConfigValue key, int fallback) {
            return SteamNetConfig.TryRead(key, out int value) ? value : fallback;
        }

        private static string Sec(int millis) {
            return $"{millis / (float)MillisPerSecond}s";
        }
    }
}
