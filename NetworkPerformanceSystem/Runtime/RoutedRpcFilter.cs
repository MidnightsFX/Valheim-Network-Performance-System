using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M7 - relays broadcast RPCs only to the peers that can use them.
    ///
    /// A routed RPC addressed to ZRoutedRpc.Everybody is sent once to the host and relayed by the
    /// host to every other ready peer (ZRoutedRpc.RouteRPC). Its cost is therefore events x
    /// players, and nearly all of the events in question are local by nature: footsteps,
    /// animation triggers, hit-stop, damage numbers, building damage, pickables being picked,
    /// objects being destroyed. At ten players nobody notices; at two hundred the relay stream
    /// alone is tens of kilobytes per second per peer of things that peer cannot see - and it
    /// shares the reliable send queue with ZDO data, so it eats straight into the window that
    /// decides whether a peer receives world updates at all this tick.
    ///
    /// Three filters, each chosen so that the receiver's observable behaviour cannot change:
    ///
    ///   * ZDO-targeted RPCs (anything sent through ZNetView.InvokeRPC(ZNetView.Everybody, ...)).
    ///     The receiving ZRoutedRpc.HandleRoutedRPC looks the target ZDO up and returns unless
    ///     ZNetScene has a live instance for it, which only exists inside that client's active
    ///     area. The host knows precisely which peers hold the ZDO - ZDOPeer.m_zdos is the set
    ///     of ZDOs it has sent them and not since invalidated - and that set is a superset of
    ///     "has an instance". Relaying only to those peers is lossless by construction, for
    ///     vanilla and for any mod RPC that goes through ZNetView.
    ///
    ///   * DestroyZDO. The right recipients are the peers that hold a copy of the ZDO - not the
    ///     peers near it: a client that visited an area and left still has the ZDO, and on
    ///     return would instantiate a ghost if it never heard the destroy. ZDOPeer.m_zdos is
    ///     again the exact predicate, with one wrinkle: vanilla handles the RPC locally before
    ///     relaying it, and HandleDestroyedZDO removes the ids from every peer's m_zdos. So the
    ///     holder set is snapshotted by a prefix on RPC_DestroyZDO and consumed by the relay,
    ///     with the batch identity checked so a snapshot can never be applied to the wrong
    ///     message. Any doubt falls back to the vanilla broadcast.
    ///
    ///   * Positional non-ZDO RPCs - RPC_DamageText (receiver discards beyond its own text
    ///     distance) and SpawnObject (receiver instantiates the effect anywhere in the world).
    ///     The position is read out of the parameter package and the RPC goes to peers whose
    ///     reference position is within the active area plus a zone of it.
    ///
    /// Everything else - chat, map pings, server messages, sleep, random events, global keys,
    /// and any RPC this code does not recognise - is left to vanilla's relay untouched.
    /// </summary>
    internal static class RoutedRpcFilter {

        private const string RoutedRpcMethod = "RoutedRPC";

        private static readonly int DestroyZdoHash = "DestroyZDO".GetStableHashCode();
        private static readonly int DamageTextHash = "RPC_DamageText".GetStableHashCode();
        private static readonly int SpawnObjectHash = "SpawnObject".GetStableHashCode();

        /// <summary>A destroy batch longer than this is not something vanilla produces (one batch
        /// per sender per frame); treat it as unparseable and let vanilla broadcast it.</summary>
        private const int MaxDestroyBatch = 65536;

        // -- destroy-batch snapshot -------------------------------------------------------
        // Filled by SnapshotDestroyHolders from inside RPC_DestroyZDO, consumed by the RouteRPC
        // that immediately follows it on the same call stack. The batch identity (id count and
        // first id) is kept so a snapshot that somehow outlives its message is rejected rather
        // than applied to the next one.

        private static readonly HashSet<long> DestroyHolders = new HashSet<long>();
        private static int _destroyBatchCount = -1;
        private static ZDOID _destroyBatchFirst = ZDOID.None;

        // -- diagnostics ------------------------------------------------------------------
        // "Events" are broadcast RPCs seen at the relay; "sent"/"suppressed" are per-peer
        // deliveries, which is what the network actually pays for.

        internal static long TargetedEvents, TargetedSent, TargetedSuppressed;
        internal static long DestroyEvents, DestroySent, DestroySuppressed;
        internal static long PositionalEvents, PositionalSent, PositionalSuppressed;
        internal static long GlobalEvents;
        internal static int SentLastSecond;
        internal static int SuppressedLastSecond;
        private static int _sentAccum;
        private static int _suppressedAccum;
        private static float _windowStart;

        /// <summary>
        /// Called from the prefix on ZRoutedRpc.RouteRPC. Returns true when the relay has been
        /// performed here and vanilla must not run; false to let vanilla relay as it always has.
        /// Never throws: anything unexpected about a message means "vanilla broadcast".
        /// </summary>
        internal static bool TryRelay(ZRoutedRpc router, ZRoutedRpc.RoutedRPCData data) {
            if (!PatchGuard.IsActive(Mechanism.RoutedRpcFilter)) { return false; }
            if (!ValConfig.EnableRoutedRpcFilter.Value) { return false; }
            if (router == null || data == null || !router.m_server) { return false; }
            if (data.m_targetPeerID != ZRoutedRpc.Everybody) { return false; }

            try {
                if (!data.m_targetZDO.IsNone()) { return RelayTargeted(data); }
                if (data.m_methodHash == DestroyZdoHash) { return RelayDestroy(router, data); }
                if (data.m_methodHash == DamageTextHash || data.m_methodHash == SpawnObjectHash) {
                    return RelayPositional(router, data);
                }
            } catch (Exception e) {
                // A throw here would land in ZRpc.Update's catch-all, logged without our name,
                // and the message would be lost for everyone. Log once-ish and fall back.
                Logger.LogWarning($"Routed RPC filter fell back to vanilla relay: {e.GetType().Name}: {e.Message}");
                ClearDestroySnapshot();
                return false;
            }

            GlobalEvents++;
            return false;
        }

        // -- ZDO-targeted ------------------------------------------------------------------

        private static bool RelayTargeted(ZRoutedRpc.RoutedRPCData data) {
            ZDOMan zdoMan = ZDOMan.s_instance;
            if (zdoMan == null) { return false; }

            // ZDOMan's peer list rather than ZRoutedRpc's: vanilla maintains both in lockstep
            // (AddPeer/RemovePeer are called together), and only this one knows what each peer
            // has been sent. A peer with no ZDOPeer cannot hold the ZDO either way.
            List<ZDOMan.ZDOPeer> peers = zdoMan.m_peers;
            ZPackage pkg = null;
            int sent = 0;
            int suppressed = 0;

            for (int i = 0; i < peers.Count; i++) {
                ZDOMan.ZDOPeer zdoPeer = peers[i];
                ZNetPeer peer = zdoPeer?.m_peer;
                if (peer == null || !peer.IsReady() || peer.m_uid == data.m_senderPeerID) { continue; }

                if (!zdoPeer.m_zdos.ContainsKey(data.m_targetZDO)) { suppressed++; continue; }

                if (pkg == null) { pkg = new ZPackage(); data.Serialize(pkg); }   // serialize once, and only if someone gets it
                peer.m_rpc.Invoke(RoutedRpcMethod, pkg);
                sent++;
            }

            TargetedEvents++;
            TargetedSent += sent;
            TargetedSuppressed += suppressed;
            Record(sent, suppressed);
            return true;
        }

        // -- DestroyZDO --------------------------------------------------------------------

        /// <summary>
        /// Prefix on ZDOMan.RPC_DestroyZDO: record which peers hold any ZDO in this batch before
        /// vanilla's HandleDestroyedZDO forgets. Reads the package and restores its position, so
        /// the original sees it untouched. Runs on clients too (they receive destroys), where it
        /// does nothing.
        /// </summary>
        internal static void SnapshotDestroyHolders(ZDOMan zdoMan, ZPackage pkg) {
            ClearDestroySnapshot();
            if (!PatchGuard.IsActive(Mechanism.RoutedRpcFilter)) { return; }
            if (!ValConfig.EnableRoutedRpcFilter.Value) { return; }
            if (zdoMan == null || pkg == null || !NpsEnv.IsHost()) { return; }

            int saved = pkg.GetPos();
            try {
                int count = pkg.ReadInt();
                if (count < 0 || count > MaxDestroyBatch) { return; }
                _destroyBatchCount = count;

                List<ZDOMan.ZDOPeer> peers = zdoMan.m_peers;
                for (int i = 0; i < count; i++) {
                    ZDOID id = pkg.ReadZDOID();
                    if (i == 0) { _destroyBatchFirst = id; }
                    if (DestroyHolders.Count >= peers.Count) { break; }       // everyone already qualifies

                    for (int p = 0; p < peers.Count; p++) {
                        ZDOMan.ZDOPeer zdoPeer = peers[p];
                        ZNetPeer peer = zdoPeer?.m_peer;
                        if (peer == null || peer.m_uid == 0L) { continue; }
                        if (DestroyHolders.Contains(peer.m_uid)) { continue; }
                        if (zdoPeer.m_zdos.ContainsKey(id)) { DestroyHolders.Add(peer.m_uid); }
                    }
                }
            } catch (Exception) {
                ClearDestroySnapshot();
            } finally {
                pkg.SetPos(saved);
            }
        }

        private static bool RelayDestroy(ZRoutedRpc router, ZRoutedRpc.RoutedRPCData data) {
            if (!SnapshotMatches(data)) {
                // No snapshot, or not for this batch - never guess about destroys.
                ClearDestroySnapshot();
                DestroyEvents++;
                return false;
            }

            List<ZNetPeer> peers = router.m_peers;
            ZPackage pkg = null;
            int sent = 0;
            int suppressed = 0;

            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (peer == null || !peer.IsReady() || peer.m_uid == data.m_senderPeerID) { continue; }

                if (!DestroyHolders.Contains(peer.m_uid)) { suppressed++; continue; }

                if (pkg == null) { pkg = new ZPackage(); data.Serialize(pkg); }
                peer.m_rpc.Invoke(RoutedRpcMethod, pkg);
                sent++;
            }

            ClearDestroySnapshot();
            DestroyEvents++;
            DestroySent += sent;
            DestroySuppressed += suppressed;
            Record(sent, suppressed);
            return true;
        }

        /// <summary>The relayed batch must be the one we snapshotted: same id count, same first id.
        /// The parameter package is a ZPackage-typed parameter, so it is one nested package.</summary>
        private static bool SnapshotMatches(ZRoutedRpc.RoutedRPCData data) {
            if (_destroyBatchCount < 0) { return false; }

            ZPackage parameters = data.m_parameters;
            int saved = parameters.GetPos();
            try {
                parameters.SetPos(0);
                ZPackage inner = parameters.ReadPackage();
                int count = inner.ReadInt();
                if (count != _destroyBatchCount) { return false; }
                if (count > 0 && inner.ReadZDOID() != _destroyBatchFirst) { return false; }
                return true;
            } catch (Exception) {
                return false;
            } finally {
                parameters.SetPos(saved);
            }
        }

        private static void ClearDestroySnapshot() {
            DestroyHolders.Clear();
            _destroyBatchCount = -1;
            _destroyBatchFirst = ZDOID.None;
        }

        // -- positional ---------------------------------------------------------------------

        private static bool RelayPositional(ZRoutedRpc router, ZRoutedRpc.RoutedRPCData data) {
            if (ZoneSystem.instance == null) { return false; }
            if (!TryReadPosition(data, out Vector3 pos)) { return false; }

            // Generous on purpose: the receiver's own test is tighter (damage text has a camera
            // distance cap; an effect is only visible within the active area), and a peer's
            // reference position on the host can be a couple of seconds old. A zone beyond the
            // active area covers both.
            Vector2s zone = ZoneSystem.GetZone(pos);

            List<ZNetPeer> peers = router.m_peers;
            ZPackage pkg = null;
            int sent = 0;
            int suppressed = 0;

            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (peer == null || !peer.IsReady() || peer.m_uid == data.m_senderPeerID) { continue; }

                // Per peer, not once for the whole relay: simulation distance is negotiated
                // individually now, so a peer that loads more of the world around itself has to
                // be judged against its own radius or it stops receiving events it can see.
                int radius = ZoneCompat.NearFor(peer) + 1;
                Vector2s peerZone = ZoneSystem.GetZone(peer.GetRefPos());
                if (!ZoneCompat.InActiveArea(peerZone, zone, radius)) {
                    suppressed++;
                    continue;
                }

                if (pkg == null) { pkg = new ZPackage(); data.Serialize(pkg); }
                peer.m_rpc.Invoke(RoutedRpcMethod, pkg);
                sent++;
            }

            PositionalEvents++;
            PositionalSent += sent;
            PositionalSuppressed += suppressed;
            Record(sent, suppressed);
            return true;
        }

        /// <summary>
        /// RPC_DamageText carries one ZPackage parameter: (int type, Vector3 pos, string text, bool
        /// player). SpawnObject carries (Vector3 pos, Quaternion rot, int prefabHash). Anything that
        /// does not read cleanly, or is not a finite position, is "unknown" and goes to vanilla.
        /// </summary>
        private static bool TryReadPosition(ZRoutedRpc.RoutedRPCData data, out Vector3 pos) {
            pos = default;
            ZPackage parameters = data.m_parameters;
            int saved = parameters.GetPos();
            try {
                parameters.SetPos(0);
                if (data.m_methodHash == DamageTextHash) {
                    ZPackage inner = parameters.ReadPackage();
                    inner.ReadInt();                                          // DamageText.TextType
                    pos = inner.ReadVector3();
                } else {
                    pos = parameters.ReadVector3();
                }
                return !float.IsNaN(pos.x) && !float.IsNaN(pos.z)
                    && !float.IsInfinity(pos.x) && !float.IsInfinity(pos.z);
            } catch (Exception) {
                return false;
            } finally {
                parameters.SetPos(saved);
            }
        }

        // -- telemetry ----------------------------------------------------------------------

        private static void Record(int sent, int suppressed) {
            _sentAccum += sent;
            _suppressedAccum += suppressed;

            float now = Time.realtimeSinceStartup;
            if (now - _windowStart < 1f) { return; }
            SentLastSecond = _sentAccum;
            SuppressedLastSecond = _suppressedAccum;
            _sentAccum = 0;
            _suppressedAccum = 0;
            _windowStart = now;
        }

        internal static void Reset() {
            ClearDestroySnapshot();
            TargetedEvents = 0; TargetedSent = 0; TargetedSuppressed = 0;
            DestroyEvents = 0; DestroySent = 0; DestroySuppressed = 0;
            PositionalEvents = 0; PositionalSent = 0; PositionalSuppressed = 0;
            GlobalEvents = 0;
            SentLastSecond = 0;
            SuppressedLastSecond = 0;
            _sentAccum = 0;
            _suppressedAccum = 0;
            _windowStart = 0f;
        }
    }
}
