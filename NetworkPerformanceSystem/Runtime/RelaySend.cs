using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M19 - the host's routed-RPC relay, sent without rebuilding the message for every recipient.
    ///
    /// Vanilla's relay (ZRoutedRpc.RouteRPC) builds a fresh ZPackage per relayed message and
    /// serialises the RoutedRPCData into it, which copies the parameter package out through
    /// GetArray. It then calls ZRpc.Invoke("RoutedRPC", pkg) once per recipient, and each of those
    /// allocates a params object[], hashes the method name, and copies the whole message out
    /// through GetArray a second time into ZRpc's own package - before the socket copies it a
    /// third time into its send queue. Only that last copy has to exist.
    ///
    /// Here the message is written once into a kept frame, laid out exactly as ZRpc.Invoke lays it
    /// out - [int hash of "RoutedRPC"][int length][serialised RoutedRPCData] - and that frame is
    /// handed to each recipient's socket. Roughly ten objects fewer per relayed message and two
    /// fewer per recipient. The idea is EnRoute's (ComfyMods); the reset below is what it lacks.
    ///
    /// THE PLAYFAB TRAP. ZPlayFabSocket.Send appends its in-flight sequence number and a message
    /// type byte to the package it is given, in place, before copying it out. ZRpc.Invoke never
    /// notices because it rebuilds its package on every call. A frame shared across recipients
    /// would carry every earlier crossplay peer's five bytes into the next one's message, so the
    /// frame is cut back to its own length before every single send. That is two field writes,
    /// and it also protects against any socket wrapper that writes into what it is given.
    ///
    /// WHY SHARING IS OTHERWISE SAFE:
    ///   * both sockets copy the frame out (GetArray) before Send returns, and ServerSync's
    ///     buffering socket - the one known wrapper - copies too and restores the read position;
    ///   * the relay runs on the main thread, and nothing inside ISocket.Send calls back into
    ///     RouteRPC. Nesting is handled anyway: a message whose frame was overwritten by another
    ///     one simply writes its own again (see RelayMessage).
    ///
    /// THE ONE BEHAVIOURAL DIFFERENCE is that the relay no longer passes through ZRpc.Invoke, so a
    /// mod that hooks ZRpc.Invoke to observe traffic will not see relayed RoutedRPCs. Nothing known
    /// depends on that - Compress hooks the ZDO send's own call site, not Invoke - but it is why
    /// this has its own switch. ZRpc's sent-packet and sent-byte counters are kept exactly as
    /// Invoke keeps them, and ZRpc's debug mode (which writes the method name into the frame) is
    /// left to vanilla.
    /// </summary>
    internal static class RelaySend {

        private const string RoutedRpcMethod = "RoutedRPC";
        private static readonly int RoutedRpcHash = RoutedRpcMethod.GetStableHashCode();

        /// <summary>Hash, then the length of the serialised RoutedRPCData.</summary>
        private const int FrameHeaderBytes = 8;

        private static readonly ZPackage Frame = new ZPackage();
        private static int _frameLength;

        /// <summary>Which message the frame currently holds. 0 is never issued.</summary>
        private static int _frameOwner;
        private static int _nextOwner;

        /// <summary>
        /// Called from the prefix on ZRoutedRpc.RouteRPC after the relay filter has declined.
        /// Performs the relay vanilla would have performed - the same recipients, in the same order,
        /// with the same bytes - and returns true, or returns false to let vanilla do it.
        ///
        /// Host only. A client's RouteRPC sends to its single server peer and is left alone.
        /// </summary>
        internal static bool TryRelayAsVanilla(ZRoutedRpc router, ZRoutedRpc.RoutedRPCData data) {
            if (router == null || data == null || !router.m_server) { return false; }

            RelayMessage message = new RelayMessage(data);
            if (!message.Reusing) { return false; }

            if (data.m_targetPeerID != ZRoutedRpc.Everybody) {
                ZNetPeer target = router.GetPeer(data.m_targetPeerID);
                if (target != null && target.IsReady()) { message.Send(target); }
                return true;
            }

            List<ZNetPeer> peers = router.m_peers;
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (peer != null && peer.m_uid != data.m_senderPeerID && peer.IsReady()) { message.Send(peer); }
            }
            return true;
        }

        /// <summary>
        /// Writes the frame for a message. Kept out of line so that the members it names are only
        /// resolved when the fast path is actually taken: if a game update removed one, the
        /// patch-time check has already stood the mechanism down and this is never compiled.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteFrame(ZRoutedRpc.RoutedRPCData data, int owner) {
            ZPackage frame = Frame;
            frame.Clear();
            frame.Write(RoutedRpcHash);
            frame.Write(0);                                                   // length, patched below

            // RoutedRPCData.Serialize, less the GetArray copy of the parameters: the same fields in
            // the same order, with the parameter bytes read straight out of their stream. Every
            // ZPackage stream is an expandable MemoryStream, so its buffer is always exposed.
            frame.Write(data.m_msgID);
            frame.Write(data.m_senderPeerID);
            frame.Write(data.m_targetPeerID);
            frame.Write(data.m_targetZDO);
            frame.Write(data.m_methodHash);

            ZPackage parameters = data.m_parameters;
            int parameterBytes = parameters.Size();                            // flushes, and is Length - as GetArray copies
            frame.Write(parameterBytes);
            frame.m_writer.Write(parameters.m_stream.GetBuffer(), 0, parameterBytes);

            int length = frame.Size();
            frame.m_stream.Position = 4;
            frame.Write(length - FrameHeaderBytes);
            frame.m_writer.Flush();
            frame.m_stream.Position = length;

            _frameLength = length;
            _frameOwner = owner;
        }

        /// <summary>
        /// ZRpc.Invoke followed by ZRpc.SendPackage, with the frame in place of the package Invoke
        /// would have built. The counters are updated before the send and with the pre-send size,
        /// which is what SendPackage does.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SendFrame(ZRpc rpc) {
            if (!rpc.IsConnected()) { return; }

            // Cut back anything the previous recipient's socket appended. See THE PLAYFAB TRAP.
            Frame.m_stream.SetLength(_frameLength);
            Frame.m_stream.Position = _frameLength;

            rpc.m_sentPackages++;
            rpc.m_sentData += _frameLength;
            rpc.m_socket.Send(Frame);
            AllocationRelief.RelaySendsReused++;
        }

        /// <summary>ZRpc's debug mode writes the method name after the hash, which the frame does not
        /// reproduce - so in that mode vanilla sends. Out of line for the same reason as WriteFrame:
        /// the relay filter builds a RelayMessage for every message, switched on or not.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool FrameAllowed() => !ZRpc.m_DEBUG;

        private static int NextOwner() {
            _nextOwner++;
            if (_nextOwner == 0) { _nextOwner = 1; }
            return _nextOwner;
        }

        /// <summary>
        /// One relayed message, sent to any number of peers. Holds the decision between the reused
        /// frame and vanilla's Invoke for the whole message, so a setting flipped mid-relay cannot
        /// deliver one message two different ways, and builds whichever it needs only when the first
        /// recipient is found - a message nobody should receive costs nothing.
        ///
        /// A struct used through a local, so constructing one allocates nothing.
        /// </summary>
        internal struct RelayMessage {

            private readonly ZRoutedRpc.RoutedRPCData _data;
            private int _owner;          // 0 until this message has written the frame
            private ZPackage _package;   // vanilla path only

            internal readonly bool Reusing;

            internal RelayMessage(ZRoutedRpc.RoutedRPCData data) {
                _data = data;
                _owner = 0;
                _package = null;
                Reusing = AllocationRelief.RelaySendActive && FrameAllowed();
            }

            internal void Send(ZNetPeer peer) {
                if (!Reusing) {
                    if (_package == null) { _package = new ZPackage(); _data.Serialize(_package); }
                    peer.m_rpc.Invoke(RoutedRpcMethod, _package);
                    return;
                }

                // Written on first use, and again only if another message took the frame since.
                if (_owner == 0 || _frameOwner != _owner) {
                    if (_owner == 0) { _owner = NextOwner(); }
                    WriteFrame(_data, _owner);
                }
                SendFrame(peer.m_rpc);
            }
        }
    }
}
