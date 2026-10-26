using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Steamworks;
using UnityEngine;

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
    /// <para>A THIRD, FOR A GAME IN THE BACKGROUND. On some machines the game stops running
    /// altogether while it is not the active window - alt-tabbed or minimised - and a player who
    /// steps away for longer than the in-world deadline comes back to a dropped connection. Seen
    /// on 2026-10-06: three drops in a quarter of an hour, each closed from the server's end
    /// (its in-world deadline was 30s), and each time the player's own game had not been running
    /// through the gap - PeerLiveness credited the whole silence as exactly one second, its cap
    /// for a stalled main thread. Players with this mod say when their game
    /// leaves or comes back to the foreground (FrameSampler sends it at once), and with Keep
    /// Players In The Background on, such a player gets Background Timeout Seconds instead - for
    /// as long as the last word from their game was that it is in the background. Steam is the
    /// lower layer again, so TimeoutConnected takes that deadline too; a game that is merely not
    /// running still has Steam's own thread acknowledging, so it is a whole machine gone to sleep
    /// that Steam would otherwise drop sooner. Their objects are not held: PeerLiveness stops
    /// trusting a quiet peer to simulate anything long before either deadline.</para>
    ///
    /// <para>OR NONE. With Enable Background Timeout off, ZRpc holds a connection whose game is in
    /// the background to no deadline at all, however long it stays there - for an admin who would
    /// rather hold a slot than have a player who stepped away come back to the menu. Steam's
    /// TimeoutConnected keeps Background Timeout Seconds, and that is the bound left: a game that
    /// has merely stopped still has Steam's thread acknowledging, so it is kept; a computer that
    /// has gone to sleep, lost its line or lost the game to a crash has not, and Steam closes the
    /// connection after Background Timeout Seconds as before.</para>
    ///
    /// <para>AND THE SAME FOR THE HOST. On a server hosted from the game itself the host is a
    /// player who alt-tabs too. Usually nothing happens: most hosts play windowed, the game keeps
    /// running in the background, and its main thread goes on answering every client's ZRpc
    /// ping. But where the host's game does stop it is the clients that hang up - only the
    /// host's main thread answers those pings, so at the client's in-world deadline every player
    /// is dropped at once. (The host's own end drops nobody: when it runs again it reads the
    /// replies that queued up before it counts the silence.) So a listen host tells every player
    /// with this mod when its game goes into the background, and their games give the host's
    /// connection the background deadline the same way.</para>
    ///
    /// <para>Only for as long as the host's game might have stopped, though. A game that stops
    /// in the background stops at once, so one still running a few seconds after losing focus
    /// is a game that keeps running, and the host then says so: its players go back to their
    /// usual deadline, and a host whose game crashes or whose line drops while it sits behind
    /// another window is noticed as quickly as one in front - not after the background deadline,
    /// in a frozen world, under a message blaming the background. A game that does stall in the
    /// background later on (minimised after the alt-tab, say) has shown it can stop, and from
    /// then on its players wait for it whenever it is in the background, for the rest of the
    /// session. The message goes out the moment anything changes and every two seconds while
    /// the game stays in the background, so a player who joins meanwhile hears it too. When the
    /// host's players are waiting, GhostWatchdog says on screen why, so they can leave sooner.
    /// Vanilla clients and older versions of this mod never hear it; HostKeepalive answers their
    /// pings for the host instead, for as long as players with the mod are waiting.</para>
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

        /// <summary>The ZRpc deadline for a peer whose game last said it is in the background,
        /// when Keep Players In The Background is on; 0 when it is off. Never below either of the
        /// other two. With BackgroundUnlimited it is Steam's deadline only.</summary>
        internal static float BackgroundTimeoutSeconds { get; private set; }

        /// <summary>Enable Background Timeout is off, under Keep Players In The Background: ZRpc
        /// never hangs up on a peer whose game is in the background.</summary>
        internal static bool BackgroundUnlimited { get; private set; }

        /// <summary>What ZRpc holds a peer in the background to: BackgroundTimeoutSeconds, or
        /// infinity with no limit, which ZRpc's "silence > deadline" never passes.</summary>
        internal static float BackgroundDeadlineSeconds =>
            BackgroundUnlimited ? float.PositiveInfinity : BackgroundTimeoutSeconds;

        /// <summary>Whether ZRpc.Update rewrites the static per peer. Off while tuning is off, so
        /// the game's own value - or another mod's - is left alone.</summary>
        private static bool _perPeer;

        /// <summary>What decides one connection's deadline.</summary>
        private sealed class PeerState {
            /// <summary>Its player has reached the world: on a host, the peer's client has sent
            /// its character id; on a client, our own character has spawned. Sticky - a respawn
            /// clears the character id for a moment and is not a new join.</summary>
            internal bool InWorld;

            /// <summary>The game at the other end last said it is not the active window: on a
            /// host, this player's frame report; on a client, the host's own word.</summary>
            internal bool InBackground;

            /// <summary>The background deadline has been logged as holding this player since they
            /// last answered.</summary>
            internal bool Held;
        }

        /// <summary>Per connection. Weak keys, so a disconnected peer's ZRpc is not kept alive by
        /// being listed here.</summary>
        private static ConditionalWeakTable<ZRpc, PeerState> _peers = new ConditionalWeakTable<ZRpc, PeerState>();

        /// <summary>The Steam readback from the last apply, for nps_stats. Null until we have run
        /// against a Steam interface.</summary>
        internal static string LastSteamReadback { get; private set; }

        /// <summary>Suppresses repeats of the RPC log line: ZPlayFabSocket.Accept calls
        /// SetLongTimeout once per accepted socket, and re-logging an unchanged value per join
        /// would be noise.</summary>
        private static string _lastLoggedRpc;

        /// <summary>A listen host telling its players whether its own game is in the background,
        /// and whether to wait for it. One byte of flags, so a later version can add to it.</summary>
        internal const string RpcHostFocus = "Nps.HostFocus";

        /// <summary>Wait for the host: its game is in the background and has not been seen to keep
        /// running there. The only flag that changes a deadline.</summary>
        private const byte HostFocusWait = 1;

        /// <summary>The host's game is in the background at all - for display.</summary>
        private const byte HostFocusInBackground = 2;

        /// <summary>While the host's game stays in the background it says so again this often,
        /// so a player who joined in the meantime hears it too. A few bytes per player.</summary>
        private const float HostFocusRepeatSeconds = 2f;

        /// <summary>A game still running this long after losing focus is one that keeps running
        /// in the background. One that stops does so straight away - the minimise that comes
        /// with an alt-tab out of fullscreen takes a fraction of a second.</summary>
        internal const float HostRunningProofSeconds = 5f;

        /// <summary>A frame at least this long in the background means this game can stop there:
        /// half the in-world deadline, the point at which a stall starts to threaten anyone's
        /// connection, and never under this floor so ordinary hitches do not count.</summary>
        private const float MinHostStallSeconds = 5f;

        /// <summary>The listen host's game is in the background. On the host: what its players
        /// were last told. On a client: what the host last said. Only while Keep Players In The
        /// Background is on - with it off the host tells its players it is in front.</summary>
        internal static bool HostInBackground { get; private set; }

        /// <summary>Listen host: its players were last told to wait for it.</summary>
        internal static bool HostToldWait { get; private set; }

        /// <summary>Listen host: its game has stalled in the background this session, so its
        /// players wait for it whenever it is there, however long it has been running.</summary>
        internal static bool HostStopsInBackground { get; private set; }

        private static byte _hostToldFlags;
        private static float _hostFocusRepeatAt;
        private static bool _hostWasBackground;
        private static float _hostBackgroundSince;
        private static float _hostLastTickAt = -1f;

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

        /// <summary>Keep Players In The Background, on top of Active. It rides on the per-peer
        /// deadline, so it has nothing to stand on while timeout tuning is off.</summary>
        private static bool KeepInBackground =>
            Active
            && ValConfig.KeepPlayersInBackground != null
            && ValConfig.BackgroundTimeoutSeconds != null
            && ValConfig.KeepPlayersInBackground.Value;

        /// <summary>
        /// The deadline ZRpc enforces for this connection: the joining allowance until its player
        /// has reached the world, the in-world one after - and the background one, when it is
        /// longer, while their game says it is not the active window.
        /// </summary>
        internal static float DeadlineFor(ZRpc rpc) {
            if (rpc == null || !_peers.TryGetValue(rpc, out PeerState state)) { return JoiningTimeoutSeconds; }
            return DeadlineOf(state);
        }

        /// <summary>This connection's deadline without the background one - what it would be if
        /// the game at the other end were the active window.</summary>
        internal static float UsualDeadlineFor(ZRpc rpc) {
            if (rpc == null || !_peers.TryGetValue(rpc, out PeerState state)) { return JoiningTimeoutSeconds; }
            return BaseDeadlineOf(state);
        }

        private static float BaseDeadlineOf(PeerState state) {
            return state.InWorld ? InWorldTimeoutSeconds : JoiningTimeoutSeconds;
        }

        private static float DeadlineOf(PeerState state) {
            float deadline = BaseDeadlineOf(state);
            float background = BackgroundDeadlineSeconds;
            return state.InBackground && background > deadline ? background : deadline;
        }

        /// <summary>Whether the host is holding this connection to the background deadline right
        /// now. For nps_stats and monitoring.</summary>
        internal static bool IsInBackground(ZRpc rpc) {
            return rpc != null && _peers.TryGetValue(rpc, out PeerState state) && state.InBackground;
        }

        /// <summary>Called from the ZRpc.Update prefix, before vanilla compares the silence against
        /// the static. Every ZRpc.Update is on the main thread, so the write cannot leak into
        /// another peer's comparison.</summary>
        internal static void BeforeRpcUpdate(ZRpc rpc) {
            if (!_perPeer) { return; }
            if (rpc == null || !_peers.TryGetValue(rpc, out PeerState state)) {
                ZRpc.m_timeout = JoiningTimeoutSeconds;
                return;
            }

            float deadline = DeadlineOf(state);
            ZRpc.m_timeout = deadline;
            if (state.InBackground) { NoteBackgroundSilence(rpc, state, deadline); }
        }

        /// <summary>
        /// Say so once when the background deadline starts doing its job - a player in the
        /// background whose silence has passed the deadline they would otherwise have been dropped
        /// at - and again for the next stretch once they have answered in between.
        /// </summary>
        private static void NoteBackgroundSilence(ZRpc rpc, PeerState state, float deadline) {
            float silent = rpc.GetTimeSinceLastPing();
            if (state.Held) {
                if (silent < 1f) { state.Held = false; }
                return;
            }

            float usual = BaseDeadlineOf(state);
            if (deadline <= usual || silent <= usual) { return; }
            state.Held = true;
            BackgroundHolds++;

            if (!NpsEnv.IsHost()) {
                Logger.LogInfo($"The host's game is in the background and has not answered for {usual:F0}s. " +
                               $"Waiting for it {HowLong(deadline)} (Keep Players In The Background).");
                return;
            }

            ZNetPeer peer = ZNet.instance != null ? Patches.NetworkChannelPatches.FindPeerByRpc(rpc) : null;
            string name = peer != null && !string.IsNullOrEmpty(peer.m_playerName) ? peer.m_playerName : "A player";
            Logger.LogInfo($"{name}'s game is in the background and has not answered for {usual:F0}s. " +
                           $"Keeping them connected {HowLong(deadline)} (Keep Players In The Background).");
            if (peer != null && Monitoring.Active) { Monitoring.OnBackgroundHold(peer.m_uid, usual, deadline); }
        }

        private static string HowLong(float deadline) {
            return float.IsPositiveInfinity(deadline)
                ? "for as long as it stays there"
                : $"for up to {deadline:F0}s while it stays there";
        }

        /// <summary>Times the background deadline has kept a quiet player connected past the one
        /// they would otherwise have had, this session.</summary>
        internal static long BackgroundHolds { get; private set; }

        /// <summary>
        /// This connection's player is in the world from here on. Host: from the RPC_CharacterID
        /// postfix. Client: from the SetCharacterID postfix, for the server's ZRpc.
        /// </summary>
        internal static void NoteInWorld(ZRpc rpc) {
            if (rpc == null) { return; }
            _peers.GetOrCreateValue(rpc).InWorld = true;
        }

        /// <summary>The game at the other end of this connection said whether it is the active
        /// window. Host: a player's frame report (PeerCapacity.RPC_ClientLoad). Client: the
        /// host's own word about its game (RPC_HostFocus).</summary>
        internal static void NoteFocus(ZRpc rpc, bool inBackground) {
            if (rpc == null) { return; }
            PeerState state;
            if (inBackground) {
                state = _peers.GetOrCreateValue(rpc);
            } else if (!_peers.TryGetValue(rpc, out state)) {
                return;
            }
            state.InBackground = inBackground;
            if (!inBackground) { state.Held = false; }
        }

        /// <summary>
        /// Listen host, once per frame from the ZNet.Update postfix: tell every player whether
        /// this game is in the background and whether to wait for it. The send hands the message
        /// to Steam, whose own thread delivers it even if this one stops straight after - which,
        /// on a machine that stops the game in the background, it does. A dedicated server has
        /// no window.
        /// </summary>
        internal static void TickHostFocus() {
            if (NpsEnv.IsDedicated()) { return; }

            // The loading screen's frames are long whatever the window is doing.
            Player player = Player.m_localPlayer;
            bool loading = player == null || player.IsTeleporting();
            bool background = KeepInBackground && !Application.isFocused;
            if (StepHostFocus(background, loading, Time.realtimeSinceStartup, out byte flags)) {
                SendHostFocus(flags);
            }
        }

        /// <summary>A stall in the background at least this long means this game can stop there.</summary>
        private static float HostStallSeconds => Math.Max(MinHostStallSeconds, InWorldTimeoutSeconds * 0.5f);

        /// <summary>
        /// The host's decision for this frame, with nothing of Unity in it: the flags its players
        /// should have, and true when they need telling now - the moment the flags change, and
        /// every couple of seconds while the game is in the background.
        ///
        /// Players wait for the host from the moment its game loses focus until it has been seen
        /// running in the background for HostRunningProofSeconds; after that its game evidently
        /// keeps running there, and they go back to their usual deadline. A frame of
        /// HostStallSeconds or more in the background, outside a loading screen, means it does
        /// stop there after all, and they wait whenever it is in the background from then on.
        /// </summary>
        internal static bool StepHostFocus(bool background, bool loading, float now, out byte flags) {
            float gap = _hostLastTickAt >= 0f ? now - _hostLastTickAt : 0f;
            _hostLastTickAt = now;

            if (background && !_hostWasBackground) {
                _hostBackgroundSince = now;
            } else if (background && !loading && !HostStopsInBackground && gap >= HostStallSeconds) {
                HostStopsInBackground = true;
                Logger.LogInfo($"This game stopped for {gap:F0}s while it was in the background. For the rest of the session, " +
                               "players with this mod will wait for it whenever it is in the background (Keep Players In The Background).");
            }
            _hostWasBackground = background;

            flags = 0;
            if (background) {
                flags = HostFocusInBackground;
                if (HostStopsInBackground || now - _hostBackgroundSince < HostRunningProofSeconds) { flags |= HostFocusWait; }
            }
            HostInBackground = background;
            HostToldWait = (flags & HostFocusWait) != 0;

            if (flags != _hostToldFlags) {
                _hostToldFlags = flags;
                _hostFocusRepeatAt = now;
                return true;
            }
            if (flags == 0 || now - _hostFocusRepeatAt < HostFocusRepeatSeconds) { return false; }
            _hostFocusRepeatAt = now;
            return true;
        }

        private static void SendHostFocus(byte flags) {
            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (peer == null || !peer.IsReady() || peer.m_rpc == null) { continue; }
                ZPackage pkg = new ZPackage();
                pkg.Write(flags);
                peer.m_rpc.Invoke(RpcHostFocus, pkg);
            }
        }

        /// <summary>Client: the host says whether its own game is in the background, and whether
        /// to wait for it. A host ignores it - only a listen host sends one, and only to its
        /// players.</summary>
        internal static void RPC_HostFocus(ZRpc rpc, ZPackage pkg) {
            if (NpsEnv.IsHost() || rpc == null) { return; }
            if (!TryReadHostFocus(pkg, out bool wait, out bool inBackground)) { return; }
            HostInBackground = inBackground;
            NoteFocus(rpc, wait);
        }

        /// <summary>The host's message, read. False for an empty or unreadable one, which
        /// changes nothing.</summary>
        internal static bool TryReadHostFocus(ZPackage pkg, out bool wait, out bool inBackground) {
            wait = false;
            inBackground = false;
            if (pkg == null) { return false; }
            try {
                byte flags = pkg.ReadByte();
                wait = (flags & HostFocusWait) != 0;
                inBackground = wait || (flags & HostFocusInBackground) != 0;
                return true;
            } catch (Exception) {
                return false;
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
            _lastLoggedRpc = null;
            _peers = new ConditionalWeakTable<ZRpc, PeerState>();
            BackgroundHolds = 0;
            HostInBackground = false;
            HostToldWait = false;
            HostStopsInBackground = false;
            _hostToldFlags = 0;
            _hostFocusRepeatAt = 0f;
            _hostWasBackground = false;
            _hostBackgroundSince = 0f;
            _hostLastTickAt = -1f;
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
                BackgroundTimeoutSeconds = KeepInBackground
                    ? Math.Max(ValConfig.BackgroundTimeoutSeconds.Value, JoiningTimeoutSeconds)
                    : 0f;
                BackgroundUnlimited = KeepInBackground
                    && ValConfig.EnableBackgroundTimeout != null
                    && !ValConfig.EnableBackgroundTimeout.Value;
            } else {
                InWorldTimeoutSeconds = vanilla;
                JoiningTimeoutSeconds = vanilla;
                BackgroundTimeoutSeconds = 0f;
                BackgroundUnlimited = false;
            }

            float before = ZRpc.m_timeout;
            ZRpc.m_timeout = JoiningTimeoutSeconds;

            string background = BackgroundUnlimited
                ? $", no limit while their game is in the background (Steam still drops a computer silent for {BackgroundTimeoutSeconds}s)"
                : BackgroundTimeoutSeconds > 0f ? $", {BackgroundTimeoutSeconds}s while their game is in the background" : "";
            string line = _perPeer
                ? $"ZRpc ping timeout {before}s -> {JoiningTimeoutSeconds}s while joining, {InWorldTimeoutSeconds}s once in the world{background}"
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
            // Background Timeout Seconds even with BackgroundUnlimited: it is then the only bound
            // on a computer that has gone silent while its game is in the background.
            int wantConnected = Active
                ? Math.Max(Math.Max(ValConfig.LoadingTimeoutSeconds.Value, ValConfig.ConnectionTimeoutSeconds.Value),
                           KeepInBackground ? ValConfig.BackgroundTimeoutSeconds.Value : 0) * MillisPerSecond
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
