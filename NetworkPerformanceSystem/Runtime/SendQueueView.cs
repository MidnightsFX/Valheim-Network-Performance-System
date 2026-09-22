using System;
using System.Collections.Generic;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M14 - the send queue other mods read, shown the way vanilla's window would have left it.
    ///
    /// Several mods wait, before sending, for a peer's socket send queue to fall under a fixed
    /// number, and disconnect the peer if it has not within 30 seconds: ServerSync and every mod
    /// that bundles it (20000, 10000 in older copies), ConditionalConfigSync, ServerCharacters,
    /// Jotunn's CustomRPC (20000). Each polls ISocket.GetSendQueueSize once a frame while it has
    /// something to send - at login, and when an admin changes a setting.
    ///
    /// On a Steam socket that figure includes bytes already on the wire and not yet acknowledged.
    /// Vanilla's ZDOMan.SendZDOs stops filling at 10240 bytes and only sends again below 8192, so a
    /// backlogged peer's queue swings between about 8 KB and 10 KB, and those thresholds were set
    /// against that. M2 lets it fill to the peer's sized window instead - tens of kilobytes past
    /// ~100ms - and the whole swing moves up with it, above every one of those thresholds, for as
    /// long as the backlog lasts.
    ///
    /// So what they read is changed, not what is sent. A read of ZSteamSocket.GetSendQueueSize
    /// that does not come from this mod has the peer's extra window - its window minus 10240 -
    /// taken off. The backlogged swing is then the same 8 KB to 10 KB it would be under vanilla,
    /// and each of those mods waits exactly as long as it would without this one. The window is
    /// never touched. The only thing that moves is where their payload joins the queue: up to one
    /// extra window of ZDO data can still be ahead of it, a third of a second at the default
    /// ceiling. A socket that is genuinely stuck still grows past their threshold and still trips
    /// their 30 seconds, because the adjustment is bounded by the extra window.
    ///
    /// This replaces the 1.4.2-1.6.0 drain, which held a backlogged peer's window at 8 KB every
    /// eight seconds so that the real figure would dip - a stall of at least a round trip for every
    /// distant peer, whether or not anybody was waiting, and up to three seconds with no ZDOs at
    /// all when other traffic kept the queue up.
    ///
    /// Two things keep the real figure where it is needed. SendZDOs's own read is bracketed by
    /// BeginOwnRead/EndOwnRead (SendWindowPatches), and this mod's other readers go through Real.
    /// And only a peer SendWindow is currently widening is adjusted, so crossplay peers, low-RTT
    /// peers and a server with sizing off read exactly vanilla's number.
    /// </summary>
    internal static class SendQueueView {

        /// <summary>Set while this mod is reading for itself. Those reads compare against the real
        /// window - the send path, nps_stats sampling, the monitoring uploader - and must see the
        /// real queue. Main thread only, like the send path.</summary>
        private static bool _ownRead;

        /// <summary>True once SendWindowPatches has bracketed SendZDOs's read. Until then that
        /// read cannot be told apart from anyone else's, and adjusting it would hand the send path
        /// a queue with the extra window already taken off - an effective window of the sized one
        /// plus its own extra. Nothing is adjusted until this is set.</summary>
        internal static bool OwnReadMarked;

        // Diagnostics, since the session started.
        internal static long OutsideReads;     // reads from anyone but this mod, while the view was on
        internal static long AdjustedReads;    // of those, reads of a peer whose window was above vanilla

        internal static bool Enabled =>
            OwnReadMarked
            && PatchGuard.IsActive(Mechanism.QueueSizeView)
            && ValConfig.ReportVanillaQueueSize != null
            && ValConfig.ReportVanillaQueueSize.Value;

        /// <summary>Called from the rewritten IL in ZDOMan.SendZDOs immediately before its queue
        /// read. Takes nothing off the stack and leaves nothing on it.</summary>
        internal static void BeginOwnRead() {
            _ownRead = true;
        }

        /// <summary>Called from the rewritten IL immediately after the read, with its result, which
        /// it hands straight back. Clears the mark unconditionally, so a read that threw in between
        /// leaves it set only until the next send attempt.</summary>
        internal static int EndOwnRead(int queue) {
            _ownRead = false;
            return queue;
        }

        /// <summary>
        /// The queue as the socket reports it, for this mod's own readers. Restores the previous
        /// mark rather than clearing it, so it can never end a mark it did not start.
        /// </summary>
        internal static int Real(ISocket socket) {
            bool was = _ownRead;
            _ownRead = true;
            try {
                return socket.GetSendQueueSize();
            } finally {
                _ownRead = was;
            }
        }

        /// <summary>
        /// The postfix body on ZSteamSocket.GetSendQueueSize: every read of every Steam socket
        /// passes through here, the send path's included, so the own-read test comes first and is
        /// all that one pays. Never throws - it runs inside other mods' wait loops.
        /// </summary>
        internal static int ForReader(ZSteamSocket socket, int queue) {
            if (_ownRead || !Enabled) { return queue; }
            OutsideReads++;

            try {
                List<ZNetPeer> peers = ZNet.instance?.GetPeers();
                if (peers == null) { return queue; }

                int extra = SendWindow.ExtraBytes(PeerUid(peers, socket));
                if (extra <= 0) { return queue; }

                AdjustedReads++;
                return Adjust(queue, extra);
            } catch (Exception) {
                // A reader on another thread racing a peer list change, or anything else
                // unexpected: the real figure is always a safe answer.
                return queue;
            }
        }

        /// <summary>The queue less the extra window, never below zero. Pure, for the offline
        /// harness.</summary>
        internal static int Adjust(int queue, int extraWindow) {
            if (extraWindow <= 0) { return queue; }
            return queue > extraWindow ? queue - extraWindow : 0;
        }

        /// <summary>
        /// Which peer this socket belongs to, or 0. A linear scan: outside reads are a handful per
        /// frame at most, each already paying a Steam status call, and a scan needs no table to
        /// keep in step with connects and disconnects. ServerSync replaces peer.m_socket with a
        /// wrapper for the whole config handshake - exactly when its own wait loop is reading - so
        /// a peer whose socket is not this one by reference is looked through.
        /// </summary>
        internal static long PeerUid(List<ZNetPeer> peers, ZSteamSocket socket) {
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                ISocket peerSocket = peer?.m_socket;
                if (peerSocket == null) { continue; }
                if (ReferenceEquals(peerSocket, socket)
                    || (!(peerSocket is ZSteamSocket) && ReferenceEquals(RttProbe.Unwrap(peerSocket), socket))) {
                    return peer.m_uid;
                }
            }
            return 0L;
        }

        internal static void Reset() {
            OutsideReads = 0;
            AdjustedReads = 0;
            _ownRead = false;
        }
    }
}
