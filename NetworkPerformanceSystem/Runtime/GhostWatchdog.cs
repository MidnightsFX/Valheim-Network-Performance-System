using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M22 - the client half of M21: tell the player the server has stopped answering, and take
    /// them out cleanly rather than leaving them playing into a world that is no longer there.
    ///
    /// The idea, and the two-stage shape (warn at half the deadline, leave at the whole of it),
    /// are taken from ClientGhostWatchdog by DreamWraith
    /// (https://github.com/dreamwraith/Valheim-ClientGhostWatchdog). No code is reused and the
    /// detector underneath is a different one - see PeerLiveness for what Steam can answer that
    /// ZRpc.GetTimeSinceLastPing cannot.
    ///
    /// <para>WHY THIS IS NOT REDUNDANT WITH VANILLA. Vanilla's chain is complete and does work:
    /// ZRpc.UpdatePing closes the socket after m_timeout, ZNet.UpdatePeers sees the closed socket
    /// and sets ErrorDisconnected, and Game.FixedUpdate logs out on any status that is neither
    /// Connecting nor Connected. At a stock 30s that chain beats this class to the answer and this
    /// class does nothing but warn. It earns its place in the two cases where the deadline is not
    /// 30s:</para>
    ///
    /// <list type="bullet">
    /// <item>ZRpc.m_timeout is <b>static</b> and ZPlayFabSocket sets it to 90s. One accepted
    /// crossplay socket anywhere in the process triples the ghost window for everything in it,
    /// which is the version of this most players actually hit and is nobody's setting.</item>
    /// <item>M11 raises it deliberately, which is the right call for a weak link mid-join and the
    /// wrong one to also apply to "how long may I keep playing a world that is gone".</item>
    /// </list>
    ///
    /// <para>So the warning is scaled off whatever deadline is actually in force rather than off a
    /// number of its own - a second, independent timeout setting would simply disagree with M11's
    /// the moment an admin touched either.</para>
    ///
    /// <para>HOW IT LEAVES. By writing ZNet's connection status and letting Game.FixedUpdate do
    /// the rest, rather than calling Game.Logout directly. That is the same path vanilla takes on
    /// every other disconnect: it saves the character, tears the session down in the right order,
    /// and cannot double-fire against a logout already in flight.</para>
    /// </summary>
    internal static class GhostWatchdog {

        /// <summary>Fraction of the deadline at which the player is told. Half leaves enough time
        /// to notice and stop swinging at something, and is late enough that an ordinary hitch has
        /// already resolved.</summary>
        private const float WarnFraction = 0.5f;

        /// <summary>The on-screen countdown is refreshed at this rate. Detection does not run on
        /// this clock - this is purely how often MessageHud is asked to redraw.</summary>
        private const float WarnRepeatSeconds = 1f;

        private static bool _warning;
        private static float _lastWarnAt;
        private static bool _tripped;

        /// <summary>Set when we take the player out, read by the FejdStartup postfix so the main
        /// menu says why instead of showing the generic "disconnected". Cleared once shown, so a
        /// later ordinary disconnect does not inherit it.</summary>
        internal static string PendingReason { get; private set; }

        internal static void ClearPendingReason() { PendingReason = null; }

        // -- diagnostics -------------------------------------------------------------------

        internal static long TotalWarnings { get; private set; }
        internal static long TotalTrips { get; private set; }
        internal static bool Warning => _warning;

        internal static bool Active =>
            PatchGuard.IsActive(Mechanism.GhostWatchdog)
            && ValConfig.EnableGhostWatchdog != null
            && ValConfig.EnableGhostWatchdog.Value;

        /// <summary>
        /// Stand down if ClientGhostWatchdog is installed - it does this half itself, and two
        /// watchdogs on one connection is a race with no upside. See PatchGuard for why its
        /// timeout and M11's disagreeing is the specific problem.
        ///
        /// Called once from the plugin's Start rather than at Awake, because BepInEx fills
        /// Chainloader.PluginInfos incrementally and a plugin ordered after us is not yet visible
        /// from our own Awake. Start also puts it ahead of the startup summary, so the summary
        /// does not list this as active when it is about to stand down.
        /// </summary>
        internal static void CheckForRival() {
            if (PatchGuard.IsPluginLoaded(PatchGuard.ClientGhostWatchdogGUID)) {
                PatchGuard.Disable(Mechanism.GhostWatchdog,
                    "ClientGhostWatchdog is installed and already watches this connection from the client side; "
                    + "its timeout is the one in force. Host-side ghost detection (PeerLiveness) is unaffected");
            }
        }

        /// <summary>
        /// One pass, from the ZNet.Update postfix. Client-side only - a host has no server peer to
        /// watch, and PeerLiveness already covers the other direction for it.
        /// </summary>
        internal static void Tick() {
            if (!Active || _tripped) { return; }
            if (!NpsEnv.IsClient()) { return; }

            // Not in a world yet, or already on the way out. Game.FixedUpdate owns the status at
            // both of those moments and writing it from here would race it.
            Game game = Game.instance;
            if (game == null || game.IsShuttingDown()) { return; }
            if (ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected) { return; }

            ZNetPeer server = ZNet.instance.GetServerPeer();
            if (server == null || server.m_uid == 0L) { return; }

            // A teleport stops the client acknowledging anything for as long as the destination
            // takes to load, and the player cannot act on a warning during it either way.
            Player local = Player.m_localPlayer;
            if (local != null && local.IsTeleporting()) { Recover(silent: true); return; }

            float deadline = ConnectionTimeout.DeadlineFor(server.m_rpc);
            if (deadline <= 0f) { return; }

            // Longer than usual only while the host has said its game is in the background
            // (ConnectionTimeout, Keep Players In The Background).
            bool hostAway = deadline > ConnectionTimeout.UsualDeadlineFor(server.m_rpc);

            float silence = PeerLiveness.SilenceSeconds(server.m_uid);
            bool transportDead = PeerLiveness.LinkStateOf(server.m_uid) == RttProbe.LinkState.Dead;

            if (transportDead || silence >= deadline) {
                Trip(transportDead, silence, hostAway);
                return;
            }

            if (silence >= WarnAfterSeconds(server.m_rpc)) {
                Warn(deadline - silence, hostAway);
                return;
            }

            Recover(silent: false);
        }

        /// <summary>
        /// How long the server may be silent before the player is told: half the deadline. While
        /// the host's game is in the background, half the deadline it would otherwise have - the
        /// player is told why the world has stopped as soon as they would have been told it was
        /// lost, rather than minutes into a wait they can cut short by leaving.
        /// </summary>
        internal static float WarnAfterSeconds(ZRpc serverRpc) {
            return ConnectionTimeout.UsualDeadlineFor(serverRpc) * WarnFraction;
        }

        private static void Warn(float secondsLeft, bool hostAway) {
            if (_warning && Time.realtimeSinceStartup - _lastWarnAt < WarnRepeatSeconds) { return; }

            // No number to count down while the host's game has no background limit
            // (Enable Background Timeout off).
            bool noLimit = float.IsPositiveInfinity(secondsLeft);
            if (!_warning) {
                _warning = true;
                TotalWarnings++;
                Logger.LogWarning(!hostAway
                    ? $"The server has stopped answering. Leaving in {secondsLeft:F0}s unless it comes back."
                    : noLimit
                        ? "The server has stopped answering, and the host's game is in the background. Waiting for it for as long as it stays there."
                        : $"The server has stopped answering, and the host's game is in the background. Waiting up to {secondsLeft:F0}s more for it.");
            }
            _lastWarnAt = Time.realtimeSinceStartup;

            // showDespiteHiddenHUD: a player who has hidden the HUD for a screenshot still needs
            // to know the session is about to end.
            if (MessageHud.instance != null) {
                float left = Mathf.Max(0f, secondsLeft);
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center,
                    !hostAway ? $"Lost contact with the server - reconnecting ({left:F0}s)"
                    : noLimit ? "The host's game is in the background - waiting for it"
                    : $"The host's game is in the background - waiting for it ({left:F0}s)",
                    0, null, showDespiteHiddenHUD: true, log: false);
            }
        }

        private static void Recover(bool silent) {
            if (!_warning) { return; }
            _warning = false;

            if (silent) { return; }
            Logger.LogInfo("The server is answering again.");
            if (MessageHud.instance != null) {
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center,
                    "Contact with the server restored", 0, null, showDespiteHiddenHUD: true, log: false);
            }
        }

        private static void Trip(bool transportDead, float silence, bool hostAway) {
            _tripped = true;
            _warning = false;
            TotalTrips++;

            string why = transportDead
                ? "the connection is dead as far as the transport is concerned"
                : $"nothing has arrived for {silence:F0}s";
            if (hostAway) { why += " while the host's game was in the background"; }
            Logger.LogWarning($"Leaving the session: {why}. Anything done since the server went quiet was "
                              + "never received by it.");

            PendingReason = hostAway && !transportDead
                ? "Lost connection to the server.\n\nThe host's game went into the background and did not come "
                  + "back in time. Progress made after contact was lost did not reach it."
                : "Lost connection to the server.\n\nYou were returned to the menu rather than left "
                  + "playing a world the server is no longer part of. Progress made after contact was "
                  + "lost did not reach it.";

            if (MessageHud.instance != null) {
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center,
                    "Lost connection to the server - returning to the menu",
                    0, null, showDespiteHiddenHUD: true, log: false);
            }

            // Vanilla's own disconnect path from here: Game.FixedUpdate reads this on the next
            // fixed step, calls Logout(save: true) and tears the session down in the order the
            // game expects. Nothing here has to know how to do that.
            ZNet.m_connectionStatus = ZNet.ConnectionStatus.ErrorDisconnected;
        }

        /// <summary>Session end. PendingReason deliberately survives - it is read on the main menu,
        /// which is after the session it describes has gone.</summary>
        internal static void Reset() {
            _warning = false;
            _tripped = false;
            _lastWarnAt = 0f;
        }
    }
}
