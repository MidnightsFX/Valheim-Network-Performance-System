using System;
using System.Runtime.CompilerServices;
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
    /// <para>TWO DEADLINES, PER PEER. A joining player and a player already in the world go quiet
    /// for different reasons and need different answers. Loading a large modded world freezes
    /// the joining client's main thread, and with it its ZRpc pings: on a 30-45 player server,
    /// 837 join-time silences over eight days reached 34s at the 99th percentile and 110s at the
    /// worst, every one of them recovering. Once the player is in, a recovered silence was under
    /// 2 minutes in 179 of 180 cases, while one that never recovered is a crashed game or a dead
    /// link - and every second of deadline beyond that is a player left in a frozen world and a
    /// slot held for nobody. ZRpc's deadline is a single static, so the static is rewritten
    /// before each peer's ZRpc.Update to that peer's deadline: Loading Timeout Seconds until the
    /// peer's character exists, Connection Timeout Seconds from then on.</para>
    ///
    /// <para>Steam's TimeoutConnected stays process-global and takes the longer of the two. It
    /// is the lower layer that wins, so the joining allowance has to hold there too; and ZRpc
    /// still hangs up on an in-world peer at the shorter deadline, so nothing is kept longer for
    /// it. (In the same data Steam went on acknowledging through every join-time freeze, at about
    /// 100 B/s - the transport lives on its own thread - so this is belt and braces.)</para>
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

        /// <summary>The ZRpc deadline for a peer whose character is in the world, for nps_stats and
        /// for the readers that size themselves off the hang-up deadline. Cached by ApplyRpc
        /// rather than computed per read: ZRpc.Update reads it for every peer on every frame.</summary>
        internal static float InWorldTimeoutSeconds { get; private set; } = VanillaRpcTimeoutSeconds;

        /// <summary>The ZRpc deadline for a peer that is still joining - from the connection
        /// until its character exists. Never below InWorldTimeoutSeconds.</summary>
        internal static float JoiningTimeoutSeconds { get; private set; } = VanillaRpcTimeoutSeconds;

        /// <summary>Whether ZRpc.Update rewrites the static per peer. Off while tuning is off, so
        /// the game's own value - or another mod's - is left alone.</summary>
        private static bool _perPeer;

        /// <summary>
        /// The ZRpcs whose player has reached the world: on a host, each peer whose client has
        /// sent its character id; on a client, the server's, once our own character has spawned.
        /// Sticky - a respawn clears the character id for a moment and is not a new join. Weak
        /// keys, so a disconnected peer's ZRpc is not kept alive by being listed here.
        /// </summary>
        private static ConditionalWeakTable<ZRpc, object> _inWorld = new ConditionalWeakTable<ZRpc, object>();

        private static readonly object Present = new object();

        /// <summary>The Steam readback from the last apply, for nps_stats. Null until we have run
        /// against a Steam interface.</summary>
        internal static string LastSteamReadback { get; private set; }

        /// <summary>Suppresses repeats of the RPC log line: ZPlayFabSocket.Accept calls
        /// SetLongTimeout once per accepted socket, and re-logging an unchanged value per join
        /// would be noise.</summary>
        private static string _lastLoggedRpc;

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
        /// The deadline ZRpc enforces for this connection: the joining allowance until its player
        /// has reached the world, the in-world one after.
        /// </summary>
        internal static float DeadlineFor(ZRpc rpc) {
            return rpc != null && _inWorld.TryGetValue(rpc, out _) ? InWorldTimeoutSeconds : JoiningTimeoutSeconds;
        }

        /// <summary>Called from the ZRpc.Update prefix, before vanilla compares the silence against
        /// the static. Every ZRpc.Update is on the main thread, so the write cannot leak into
        /// another peer's comparison.</summary>
        internal static void BeforeRpcUpdate(ZRpc rpc) {
            if (!_perPeer) { return; }
            ZRpc.m_timeout = DeadlineFor(rpc);
        }

        /// <summary>
        /// This connection's player is in the world from here on. Host: from the RPC_CharacterID
        /// postfix. Client: from the SetCharacterID postfix, for the server's ZRpc.
        /// </summary>
        internal static void NoteInWorld(ZRpc rpc) {
            if (rpc == null || _inWorld.TryGetValue(rpc, out _)) { return; }
            _inWorld.Add(rpc, Present);
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
            _lastLoggedRpc = null;
            _inWorld = new ConditionalWeakTable<ZRpc, object>();
        }

        /// <summary>
        /// Recomputes both deadlines and overwrites the value vanilla just wrote into ZRpc's
        /// static. Read back out of the field rather than assumed: this is the half with no Steam
        /// call to refuse it, but it is also the half that would silently do nothing if the field
        /// were ever renamed.
        ///
        /// Vanilla's "long" mode is ZPlayFabSocket raising the static to 90s for the whole process
        /// the first time a crossplay socket appears, and never lowering it again. That is kept as
        /// a floor under the in-world deadline, so a crossplay server never drops anyone sooner
        /// than the game itself would.
        ///
        /// Between per-peer writes the static holds the joining deadline, the longer one, so a
        /// ZRpc updated by some path our prefix does not see is never cut short mid-join.
        /// </summary>
        private static void ApplyRpc(string reason) {
            float vanilla = _longMode ? VanillaRpcLongTimeoutSeconds : VanillaRpcTimeoutSeconds;
            _perPeer = Active;
            if (_perPeer) {
                InWorldTimeoutSeconds = Math.Max(ValConfig.ConnectionTimeoutSeconds.Value, _longMode ? vanilla : 0f);
                JoiningTimeoutSeconds = Math.Max(ValConfig.LoadingTimeoutSeconds.Value, InWorldTimeoutSeconds);
            } else {
                InWorldTimeoutSeconds = vanilla;
                JoiningTimeoutSeconds = vanilla;
            }

            float before = ZRpc.m_timeout;
            ZRpc.m_timeout = JoiningTimeoutSeconds;

            string line = _perPeer
                ? $"ZRpc ping timeout {before}s -> {JoiningTimeoutSeconds}s while joining, {InWorldTimeoutSeconds}s once in the world"
                : $"ZRpc ping timeout {before}s -> {ZRpc.m_timeout}s (vanilla)";
            if (_longMode) { line += " [crossplay]"; }
            if (line == _lastLoggedRpc) { return; }
            _lastLoggedRpc = line;

            Logger.LogInfo($"Connection timeout ({reason}): {line}");
        }

        /// <summary>
        /// Writes the two Steam-level timeouts at Global scope. Both keys are int32 milliseconds;
        /// vanilla writes TimeoutConnected as a float, which Steam converts, so a read back as
        /// int32 returns the same number either way. TimeoutConnected takes the joining
        /// allowance - see the class summary for why the longer of the two is the right one here.
        /// </summary>
        private static void ApplySteam(string reason) {
            if (!SteamNetConfig.Available) { return; }   // reports itself once, then goes quiet

            int wantInitial = Active
                ? ValConfig.ConnectTimeoutSeconds.Value * MillisPerSecond
                : VanillaSteamInitialMillis;
            int wantConnected = Active
                ? Math.Max(ValConfig.LoadingTimeoutSeconds.Value, ValConfig.ConnectionTimeoutSeconds.Value) * MillisPerSecond
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
