using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using UnityEngine;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// Wires up the mod's own peer channels and drives its periodic work.
    ///
    /// Everything here uses direct peer RPCs rather than ZRoutedRpc, so nothing we add can ever be
    /// relayed on to a third party, and an unmodded peer simply drops the unknown method hash
    /// (ZRpc.HandlePackage returns silently for unregistered hashes). That is what lets a modded
    /// server help vanilla clients and vice versa without a version handshake.
    /// </summary>
    [HarmonyPatch]
    internal static class NetworkChannelPatches {

        internal const string RpcLatencyTable = "Nps.LatencyTable";
        internal const string RpcRefPos = "Nps.RefPos";

        /// <summary>How often the host republishes the latency table. Ownership decisions move on
        /// a multi-second hysteresis anyway, so there is nothing to gain from going faster.</summary>
        private const float LatencyTableIntervalSeconds = 2f;

        /// <summary>Anything further from the origin than this is not a position in a Valheim
        /// world (radius 10 000m plus margin). Used to reject garbage reference positions before
        /// they reach the zone maths, where a NaN or an out-of-range float turns into an
        /// int.MinValue zone and a sector scan over overflowed coordinates.</summary>
        private const float MaxRefPosCoordinate = 12000f;

        private static float _latencyTableTimer;
        private static float _refPosTimer;
        private static Vector3 _lastSentRefPos = Vector3.positiveInfinity;

        // -- channel registration ----------------------------------------------------------

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        [HarmonyPostfix]
        private static void RegisterChannels(ZNetPeer peer) {
            if (peer?.m_rpc == null) { return; }

            // Both sides register both handlers. Which one actually fires is decided by who
            // sends, so there is no need to know our role at registration time - and at this
            // point in the handshake peer.m_uid is still 0 anyway.
            peer.m_rpc.Register<ZPackage>(RpcLatencyTable, RPC_LatencyTable);
            peer.m_rpc.Register<Vector3>(RpcRefPos, RPC_RefPos);
        }

        // -- RTT sampling ------------------------------------------------------------------

        /// <summary>
        /// ZRpc pings every peer once a second and this fires for both the ping and the pong, so
        /// it is a free, correctly-paced sampling clock. The RTT itself comes out of the socket
        /// via RttProbe rather than from timing the pong ourselves. RttProbe, not the vanilla
        /// accessor: ZSteamSocket.GetConnectionQuality is hard-wired to the Steamworks client
        /// interface and throws on a dedicated server, which only has the game-server one. This
        /// postfix runs inside ZRpc.Update's catch-all, so a throw here would be swallowed, logged
        /// without our name, and leave every mechanism silently on vanilla.
        /// </summary>
        [HarmonyPatch(typeof(ZRpc), "ReceivePing")]
        [HarmonyPostfix]
        private static void SampleRtt(ZRpc __instance) {
            if (ZNet.instance == null) { return; }
            if (!PatchGuard.IsActive(Mechanism.RttSampling)) { return; }     // stood down: skip the peer scan too

            ZNetPeer peer = FindPeerByRpc(__instance);
            if (peer?.m_socket == null || peer.m_uid == 0L) { return; }

            if (RttProbe.TryGetPingMs(peer.m_socket, out int ping)) {
                LatencyRegistry.Sample(peer.m_uid, ping);
            }
        }

        private static ZNetPeer FindPeerByRpc(ZRpc rpc) {
            System.Collections.Generic.List<ZNetPeer> peers = ZNet.instance.GetPeers();
            for (int i = 0; i < peers.Count; i++) {
                if (peers[i].m_rpc == rpc) { return peers[i]; }
            }
            return null;
        }

        // -- periodic work -----------------------------------------------------------------

        [HarmonyPatch(typeof(ZNet), "Update")]
        [HarmonyPostfix]
        private static void Tick() {
            if (!NpsEnv.NetReady()) { return; }

            float dt = Time.unscaledDeltaTime;
            if (NpsEnv.IsHost()) {
                TickLatencyTable(dt);
            } else {
                TickRefPos(dt);
            }
        }

        private static void TickLatencyTable(float dt) {
            _latencyTableTimer += dt;
            if (_latencyTableTimer < LatencyTableIntervalSeconds) { return; }
            _latencyTableTimer = 0f;

            System.Collections.Generic.List<ZNetPeer> peers = ZNet.instance.GetPeers();
            if (peers.Count == 0) { return; }

            // Built per recipient: each peer gets the host plus the measured peers near it, not
            // the whole server. Invoke consumes the package, which is why a fresh one per peer is
            // the natural shape here anyway.
            for (int i = 0; i < peers.Count; i++) {
                if (!peers[i].IsReady()) { continue; }
                peers[i].m_rpc.Invoke(RpcLatencyTable, LatencyRegistry.BuildTablePackage(peers[i]));
            }
        }

        /// <summary>
        /// M6. Vanilla only reports our reference position inside ZNet.SendPeriodicData, which is
        /// gated behind a single 2 second timer it shares with SendNetTime and SendPlayerList.
        /// The server uses that position for both the interest set and ownership arbitration, so
        /// at sprint speed it can be arbitrating against a position 10m out of date, and on a
        /// longship 20m+ - a third of a zone. This is a 12 byte side-channel that keeps it fresh
        /// without touching the vanilla path or its heavier payload.
        /// </summary>
        private static void TickRefPos(float dt) {
            if (!ValConfig.EnableFastRefPos.Value) { return; }

            _refPosTimer += dt;
            float interval = 1f / Mathf.Max(1f, ValConfig.RefPosSendHz.Value);
            if (_refPosTimer < interval) { return; }
            _refPosTimer = 0f;

            ZNetPeer server = ZNet.instance.GetServerPeer();
            if (server == null || !server.IsReady()) { return; }

            Vector3 pos = ZNet.instance.GetReferencePosition();
            // Exactly zero is what ZNet holds before Game.FindSpawnPoint has chosen where we are
            // going. The server already treats it as "no position yet"; advertising it would only
            // re-assert the sentinel the vanilla PeerInfo has already given it.
            if (pos == Vector3.zero) { return; }

            float minMove = ValConfig.RefPosMinMoveDistance.Value;
            if (minMove > 0f && (pos - _lastSentRefPos).sqrMagnitude < minMove * minMove) {
                return;                                                       // standing still costs nothing
            }

            _lastSentRefPos = pos;
            server.m_rpc.Invoke(RpcRefPos, pos);
        }

        // -- handlers ----------------------------------------------------------------------

        private static void RPC_LatencyTable(ZRpc rpc, ZPackage pkg) {
            if (NpsEnv.IsHost()) { return; }                                  // hosts measure, they do not receive
            LatencyRegistry.ApplyTablePackage(pkg);
        }

        private static void RPC_RefPos(ZRpc rpc, Vector3 pos) {
            if (!NpsEnv.IsHost()) { return; }
            if (!ValConfig.EnableFastRefPos.Value) { return; }
            if (!IsPlausibleRefPos(pos)) { return; }

            ZNetPeer peer = FindPeerByRpc(rpc);
            if (peer == null) { return; }
            peer.m_refPos = pos;
        }

        /// <summary>Client-supplied, so it is checked before it can reach ZoneSystem.GetZone.
        /// Vanilla's own RPC_ServerSyncedPlayerData trusts the same value; this channel is five
        /// times as frequent, so it at least should not.</summary>
        private static bool IsPlausibleRefPos(Vector3 pos) {
            if (float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z)) { return false; }
            if (float.IsInfinity(pos.x) || float.IsInfinity(pos.y) || float.IsInfinity(pos.z)) { return false; }
            return Mathf.Abs(pos.x) <= MaxRefPosCoordinate
                && Mathf.Abs(pos.y) <= MaxRefPosCoordinate
                && Mathf.Abs(pos.z) <= MaxRefPosCoordinate;
        }

        // -- lifecycle ---------------------------------------------------------------------

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.RemovePeer))]
        [HarmonyPostfix]
        private static void OnPeerRemoved(ZNetPeer netPeer) {
            if (netPeer == null) { return; }
            LatencyRegistry.ForgetPeer(netPeer.m_uid);
            SendWindow.Forget(netPeer.m_uid);
            QueueDrain.Forget(netPeer.m_uid);
            NetworkStats.ForgetPeer(netPeer.m_uid);
            SyncListCache.ForgetPeer(netPeer.m_uid);
        }

        /// <summary>StopAll rather than Shutdown: it is the common tail of both Shutdown and
        /// ShutdownWithoutSave, and it is idempotent (m_haveStoped), so this fires exactly once
        /// per session end whichever entry point was used.</summary>
        [HarmonyPatch(typeof(ZNet), "StopAll")]
        [HarmonyPostfix]
        private static void OnStopAll() {
            LatencyRegistry.Reset();
            RttProbe.Reset();
            _latencyTableTimer = 0f;
            _refPosTimer = 0f;
            _lastSentRefPos = Vector3.positiveInfinity;
        }
    }
}
