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

        /// <summary>How often the host republishes the latency table. Ownership decisions move on
        /// a multi-second hysteresis anyway, so there is nothing to gain from going faster.</summary>
        private const float LatencyTableIntervalSeconds = 2f;

        private static float _latencyTableTimer;

        // -- channel registration ----------------------------------------------------------

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        [HarmonyPostfix]
        private static void RegisterChannels(ZNetPeer peer) {
            if (peer?.m_rpc == null) { return; }

            // Both sides register both handlers. Which one actually fires is decided by who
            // sends, so there is no need to know our role at registration time - and at this
            // point in the handshake peer.m_uid is still 0 anyway.
            peer.m_rpc.Register<ZPackage>(RpcLatencyTable, RPC_LatencyTable);

            // Network monitoring's two. Registered whether or not monitoring is on - it can be
            // switched on mid-session, and an unused handler costs a dictionary entry. Neither
            // does anything until the host has monitoring running.
            peer.m_rpc.Register<ZPackage>(MonitoringUpload.RpcHello, MonitoringUpload.RPC_Hello);
            peer.m_rpc.Register<ZPackage>(MonitoringUpload.RpcBatch, MonitoringUpload.RPC_Batch);
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

            ZNetPeer peer = FindPeerByRpc(__instance);
            if (peer?.m_socket == null || peer.m_uid == 0L) { return; }

            // M21's heartbeat. Deliberately ahead of the RttSampling gate and outside it: this is
            // "a packet arrived from this peer", which is true whether or not we can measure how
            // long it took, and a peer whose RTT we cannot read is exactly the peer whose liveness
            // we most need to track by other means.
            PeerLiveness.NoteTraffic(peer.m_uid);

            if (!PatchGuard.IsActive(Mechanism.RttSampling)) { return; }     // stood down: skip the probe

            if (RttProbe.TryGetPingMs(peer.m_socket, out int ping)) {
                LatencyRegistry.Sample(peer.m_uid, ping);
            }

            // The other half of M2's bandwidth-delay product: the rate Steam paces this connection
            // at. A separate read rather than folded into the ping query, because the client-side
            // ping goes through vanilla's GetConnectionQuality on purpose (so a mod re-pointing it
            // is respected) and that accessor does not expose the rate. One more native call per
            // peer per ping.
            if (RttProbe.TryGetLinkStatus(peer.m_socket, out RttProbe.LinkStatus link)) {
                LatencyRegistry.NoteSteamSendRate(peer.m_uid, link.SendRateBytesPerSec);
                SteamTransport.NoteObservedRate(peer.m_uid, link.SendRateBytesPerSec);
                // M26: the same status carries the share of our packets that reached this peer.
                LossBackoff.Observe(peer, link.QualityRemote);
            }
        }

        internal static ZNetPeer FindPeerByRpc(ZRpc rpc) {
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

            // M21 runs on both sides and before everything else here: the host needs its verdict
            // before the next ownership pass, and the client's watchdog reads it immediately below.
            if (PatchGuard.IsActive(Mechanism.PeerLiveness)) { PeerLiveness.Evaluate(); }

            float dt = Time.unscaledDeltaTime;
            if (NpsEnv.IsHost()) {
                TickLatencyTable(dt);
            } else {
                GhostWatchdog.Tick();                                         // M22, client-side only
            }

            // Both roles. While monitoring is off this is two config reads and a comparison.
            Monitoring.Tick();
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

        // -- handlers ----------------------------------------------------------------------

        private static void RPC_LatencyTable(ZRpc rpc, ZPackage pkg) {
            if (NpsEnv.IsHost()) { return; }                                  // hosts measure, they do not receive
            LatencyRegistry.ApplyTablePackage(pkg);
        }

        // -- lifecycle ---------------------------------------------------------------------

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.RemovePeer))]
        [HarmonyPostfix]
        private static void OnPeerRemoved(ZNetPeer netPeer) {
            if (netPeer == null) { return; }
            LatencyRegistry.ForgetPeer(netPeer.m_uid);
            PeerLiveness.Forget(netPeer.m_uid);
            SendWindow.Forget(netPeer.m_uid);
            NetworkStats.ForgetPeer(netPeer.m_uid);
            SyncListCache.ForgetPeer(netPeer.m_uid);
            Monitoring.ForgetPeer(netPeer.m_uid);
            LiveRefPos.ForgetPeer(netPeer.m_uid);
            SteamTransport.ForgetPeer(netPeer.m_uid);
            LossBackoff.ForgetPeer(netPeer.m_uid);
            // Keyed by connection, not uid: a peer dropped mid-handshake may never have had one.
            ZdoDataGuard.Forget(netPeer.m_rpc);
        }

        /// <summary>StopAll rather than Shutdown: it is the common tail of both Shutdown and
        /// ShutdownWithoutSave, and it is idempotent (m_haveStoped), so this fires exactly once
        /// per session end whichever entry point was used.</summary>
        [HarmonyPatch(typeof(ZNet), "StopAll")]
        [HarmonyPostfix]
        private static void OnStopAll() {
            LatencyRegistry.Reset();
            RttProbe.Reset();
            PeerLiveness.Reset();
            GhostWatchdog.Reset();
            LiveRefPos.Reset();
            SteamTransport.Reset();
            LossBackoff.Reset();
            ZdoDataGuard.Reset();
            _latencyTableTimer = 0f;
        }
    }
}
