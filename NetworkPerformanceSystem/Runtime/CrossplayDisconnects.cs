using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M42 and M43 - crossplay (PlayFab) connections that end, or stall, without the other side
    /// finding out.
    ///
    /// <para>M42, HOST: A DROPPED CROSSPLAY PLAYER IS TOLD. Closing a Steam connection reaches the
    /// other end. ZPlayFabSocket.Dispose sends nothing, and the host stays in the Party network, so a
    /// player the server drops with ZNet.Disconnect - a mod's join check refusing them, an admin
    /// tool, a mod kicking - hears nothing at all. Their game keeps whatever had arrived, spawns into
    /// a half-loaded world and waits; 26 s later it starts resetting its Party connection, and only
    /// its own ZRpc timeout finally returns it to the menu. And a PlayFab socket compresses on a
    /// worker thread and hands the result to PlayFab only in its LateUpdate, while Dispose throws
    /// away whatever is still in that queue: a mod that sends a refusal and disconnects in the same
    /// frame never reaches a crossplay player either. Seen 2026-10-14 on a crossplay listen host,
    /// where Elite Creatures Reborn's mod check dropped a player whose game was frozen building the
    /// world (Investigations/lenouveaumonde-join-hang-2026-10-14.md).</para>
    ///
    /// <para>Before vanilla's Disconnect body runs, the host sends that player the game's own
    /// "Disconnect" RPC, which it otherwise only sends when it shuts down, and then hands everything
    /// still in the socket's compression queue to PlayFab at once, that RPC last, compressed on the
    /// main thread as vanilla's InternalSend does once ZNet has stopped. Every message carries the
    /// sequence number ZPlayFabSocket.Send gave it, and the receiver takes them strictly in order, so
    /// the queue has to go out whole: one earlier message left behind would hold the notice up
    /// forever. The player needs nothing installed - vanilla's RPC_Disconnect sets ErrorDisconnected
    /// and their game goes back to the menu, as it would on Steam. A frozen game that catches up on
    /// an error and the notice in one frame shows "disconnected" rather than that error.</para>
    ///
    /// <para>Nothing is sent to a player who is gone or has been told already: the socket has closed
    /// (a timeout), the player said goodbye themselves (their own "Disconnect" is what is being
    /// handled), the game is kicking them ("Kicked" went a second earlier), or the Party link is
    /// being reset (vanilla holds every send then, and PlayFab would refuse this one too). The
    /// server's own shutdown sends "Disconnect" to everyone itself and never calls ZNet.Disconnect.
    /// A socket another mod has wrapped is left alone: what goes on the wire there is that mod's
    /// business.</para>
    ///
    /// <para>M43, A PLAYER'S GAME: A CROSSPLAY CONNECTION THAT COMES BACK GETS TIME TO RESUME.
    /// After 26 s with nothing from the host, ZPlayFabSocket.Update resets the Party connection.
    /// When it reconnects, Connect refreshes the static s_lastReception only for a socket it has not
    /// seen before. The socket that was just reset is one it has, so the same 26+ s are still on the
    /// clock and the very next Update resets it again, before anything from the host can arrive.
    /// The log above shows "Resume TX" and the next reset in the same second, four times running.
    /// Here a reconnect of a socket the game already had counts as hearing from the host, so the
    /// host gets the normal 26 s. A connection that never resumes still ends at the game's own
    /// ZRpc timeout.</para>
    /// </summary>
    internal static class CrossplayDisconnects {

        /// <summary>Vanilla's silence limit before a client resets its Party connection
        /// (ZPlayFabSocket's NETWORK_ERROR_WATCHDOG).</summary>
        internal const double ResetAfterSeconds = 26d;

        /// <summary>The RPC vanilla's ZNet.SendDisconnect sends and RPC_Disconnect handles.</summary>
        internal const string DisconnectRpc = "Disconnect";

        // M42, host. Since start, main thread.
        internal static long Told;            // dropped crossplay players sent the notice
        internal static long WouldTell;       // the same with the setting off: dropped without a word, as vanilla
        internal static long Messages;        // messages handed to PlayFab with the notices, the notices included
        internal static long Bytes;           // compressed bytes of those
        internal static long Refused;         // PlayFab refused a hand-off part-way: the link went down
        internal static long SkippedClosed;   // the socket had already closed: a timeout
        internal static long SkippedGoodbye;  // the player was leaving by themselves
        internal static long SkippedKicked;   // the game is kicking them: "Kicked" went out already
        internal static long SkippedLinkDown; // the Party link was being reset: nothing reaches them now
        internal static string LastTold;      // the last player told, for nps_stats

        // M43, a player's game. Since start, main thread.
        internal static long Resumes;         // reconnects given the full time again
        internal static long StaleResumes;    // reconnects left on the old clock: setting off
        internal static double LastResumeSilence = -1d; // seconds without data before the last reconnect

        internal static bool NoticeHooked => PatchGuard.IsActive(Mechanism.CrossplayDropNotice);

        internal static bool NoticeOn =>
            NoticeHooked
            && ValConfig.TellDroppedCrossplayPlayers != null
            && ValConfig.TellDroppedCrossplayPlayers.Value;

        internal static bool ResumeHooked => PatchGuard.IsActive(Mechanism.CrossplayResume);

        internal static bool ResumeOn =>
            ResumeHooked
            && ValConfig.ResumeAfterCrossplayReset != null
            && ValConfig.ResumeAfterCrossplayReset.Value;

        // -- M42 ---------------------------------------------------------------------------

        /// <summary>The connection whose own "Disconnect" ZNet.RPC_Disconnect is handling, while it
        /// runs. Main thread only, like the RPC.</summary>
        private static ZRpc _goodbyeFrom;

        internal static void EnterGoodbye(ZRpc rpc) { _goodbyeFrom = rpc; }

        internal static void ExitGoodbye() { _goodbyeFrom = null; }

        /// <summary>
        /// ZNet.Disconnect prefix: decides whether the peer about to be disposed is a crossplay
        /// player who should be told, and tells them. Takes ZNet's fields rather than reading
        /// ZNet.instance, so it can run outside the game.
        /// </summary>
        internal static void BeforeDisconnect(bool isServer, bool stopped, ZNetPeer peer, bool kickPending) {
            if (!NoticeHooked || !isServer || stopped || peer == null || peer.m_rpc == null) { return; }

            // Exactly the game's socket. A wrapper's framing, compression and queues are its own.
            ISocket socket = peer.m_socket;
            if (socket == null || socket.GetType() != typeof(ZPlayFabSocket)) { return; }

            if (!socket.IsConnected()) { SkippedClosed++; return; }
            if (ReferenceEquals(peer.m_rpc, _goodbyeFrom)) { SkippedGoodbye++; return; }
            if (kickPending) { SkippedKicked++; return; }

            Tell(peer, (ZPlayFabSocket)socket);
        }

        /// <summary>
        /// Kept out of BeforeDisconnect so the ZPlayFabSocket internals are only JIT-compiled once a
        /// PlayFab peer is actually being dropped, and a failure there reaches the caller's catch.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Tell(ZNetPeer peer, ZPlayFabSocket socket) {
            // Vanilla's InternalSend drops every send while a reset is in progress, and PlayFab has
            // just refused one. The notice would go nowhere.
            if (socket.PartyResetInProgress()) { SkippedLinkDown++; return; }

            if (!NoticeOn) { WouldTell++; return; }

            // Vanilla's own message, framed and numbered by vanilla: ZRpc.Invoke -> ZPlayFabSocket.Send
            // gives it the next sequence number and keeps it in the in-flight queue, and InternalSend
            // queues it for compression (or, before the version check has switched compression on,
            // hands it to PlayFab right there).
            uint before = socket.m_inFlightQueue.Head;
            peer.m_rpc.Invoke(DisconnectRpc);
            if (socket.m_inFlightQueue.Head == before) { return; }

            List<byte[]> pending = TakePending(socket.m_zlibWorkQueue);
            int handed = 0;
            long bytes = 0;
            for (int i = 0; i < pending.Count; i++) {
                socket.InternalSendCont(pending[i]);
                // A refused send suspends TX (vanilla's own reconnect state), and every later send
                // returns at once - nothing after it can go.
                if (socket.PartyResetInProgress()) { break; }
                handed++;
                bytes += pending[i].Length;
            }

            // Nothing was waiting when compression is still off: the notice went to PlayFab inside
            // Invoke, behind everything before it.
            int sent = pending.Count == 0 ? 1 : handed;
            Messages += sent;
            Bytes += bytes;
            string who = Describe(peer, socket);
            if (handed < pending.Count || socket.PartyResetInProgress()) {
                Refused++;
                Logger.LogWarning($"Could not tell crossplay player {who} that the server dropped them: PlayFab refused the send " +
                    $"({handed} of {pending.Count} waiting message(s) went). Their game will find out from its own timeout.");
                return;
            }

            Told++;
            LastTold = who;
            Logger.LogInfo($"Told crossplay player {who} that the server dropped them ({sent} message(s), the notice last, " +
                "handed to PlayFab before the connection was closed).");
        }

        /// <summary>
        /// Everything this socket's compression queue still holds, in the order it was sent: first
        /// what the worker thread has compressed and LateUpdate has not handed on yet, then what the
        /// worker has not got to, compressed here. Both are taken under the lock the worker holds
        /// while it works, so nothing is taken twice or overtaken, and once the socket is disposed
        /// there is nothing left in it for vanilla to throw away. Received data is left alone.
        /// </summary>
        internal static List<byte[]> TakePending(PlayFabZLibWorkQueue queue) {
            List<byte[]> pending = new List<byte[]>();
            List<byte[]> raw = null;
            lock (PlayFabZLibWorkQueue.m_lock) {
                while (queue.m_outCompress.Count > 0) {
                    pending.Add(queue.m_outCompress.Dequeue());
                }
                if (queue.m_inCompress.Count > 0) {
                    raw = new List<byte[]>(queue.m_inCompress);
                    queue.m_inCompress.Clear();
                }
            }

            if (raw != null) {
                for (int i = 0; i < raw.Count; i++) {
                    try {
                        pending.Add(queue.CompressOnThisThread(raw[i]));
                    } catch {
                        // The worker drops a buffer it cannot compress, too.
                    }
                }
            }
            return pending;
        }

        private static string Describe(ZNetPeer peer, ZPlayFabSocket socket) {
            string name = string.IsNullOrEmpty(peer.m_playerName) ? "(not named yet)" : peer.m_playerName;
            return $"{name} ({socket.GetEndPointString()})";
        }

        // -- M43 ---------------------------------------------------------------------------

        /// <summary>
        /// ZPlayFabSocket.Connect prefix: true for a player's own connection to a host that the game
        /// already had - a reconnect after a Party reset. Vanilla refreshes the reception time only
        /// for one it has not seen. Matched by value so nothing here names a PlayFab type.
        /// </summary>
        internal static bool IsReconnect(ZPlayFabSocket socket) {
            return socket != null && socket.m_isClient && ZPlayFabSocket.s_connectSockets.ContainsValue(socket);
        }

        /// <summary>ZPlayFabSocket.Connect postfix: a reconnect counts as hearing from the host.</summary>
        internal static void AfterConnect(bool reconnect) {
            if (!reconnect || !ResumeHooked) { return; }

            DateTime now = DateTime.UtcNow;
            double silent = (now - ZPlayFabSocket.s_lastReception).TotalSeconds;
            LastResumeSilence = silent;
            if (!ResumeOn) {
                StaleResumes++;
                return;
            }

            ZPlayFabSocket.s_lastReception = now;
            Resumes++;
            Logger.LogInfo($"Crossplay connection to the host is back after {silent:F0}s without data from it; " +
                $"the host has {ResetAfterSeconds:F0}s to resume before the game resets the connection again.");
        }
    }
}
