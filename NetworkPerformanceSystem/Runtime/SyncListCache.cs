using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M9 - reuses each peer's sector scan across the send sweep. This exists to pay back a cost
    /// M2b creates.
    ///
    /// ZDOMan.CreateSyncList's server branch does, per peer, per send:
    ///     FindSectorObjects(zone, peer simulation distance, sector, distant)
    ///       -> filter by peer.ShouldSend -> ServerSortSendZDOS (a full List.Sort)
    ///       -> if toSync.Count &lt; 10, filter the distant list too
    ///       -> AddForceSendZdos
    ///
    /// FindSectorObjects walks (2*near+1)^2 sector buckets plus the distant ring and copies
    /// every ZDO in them into a list. Vanilla serviced one peer per frame behind a 50ms gate, so
    /// that ran roughly 4x/sec per peer. The M2b scheduler services every peer every interval -
    /// 20x/sec per peer, and the cost scales with player count. M2b's frame budget keeps that
    /// bounded by cutting the per-peer send rate, which is the one thing the scheduler exists to
    /// raise. Making the scan cheaper is the better lever: the budget then stops being reached at
    /// all on the servers where it currently bites.
    ///
    /// WHAT IS AND IS NOT CACHED - the split is load-bearing.
    ///
    ///   * The sector SCAN is cached. Which ZDOs live in which sector buckets changes only when a
    ///     ZDO moves sector or the peer moves zone, neither of which happens on a send-tick
    ///     timescale.
    ///   * The FILTER and the SORT are not, and must never be. peer.ShouldSend compares each ZDO's
    ///     revisions against peer.m_zdos, and ZDOMan.SendZDOs mutates peer.m_zdos on every send.
    ///     Caching the finished sorted list would re-send the peer ZDOs it already holds - the
    ///     same bytes, over and over. They are re-run here exactly as vanilla runs them, so the
    ///     wire output is unchanged.
    ///
    /// THE POOLING HAZARD, and why destruction invalidates rather than expires.
    ///
    /// ZDOs are pooled. ZDOMan.HandleDestroyedZDO ends in ZDOPool.Release(zdo), which calls
    /// ZDO.Reset() (clearing the Valid flag) and pushes the instance onto a free stack that
    /// ZDOPool.Get() pops from - and Get() calls Init(), which sets Valid back to true. So a
    /// destroyed ZDO that is recycled inside the cache window becomes a live, valid ZDO for a
    /// completely different object, still sitting in our cached list. A validity test alone does
    /// not catch that, because by then the flag is true again.
    ///
    /// So the two directions of staleness are treated differently, because they are not the same
    /// kind of problem:
    ///
    ///   * An object APPEARING in a peer's area late is a latency tradeoff. It is bounded by
    ///     Cache Ms, it is small next to the send interval, and it is the price being paid.
    ///   * An object being DESTROYED is a correctness problem, so it is not left to the timer at
    ///     all: every destruction bumps a generation counter and every cache entry from an older
    ///     generation is rebuilt on its next use. Destroys are rare next to sends, so this costs
    ///     one int comparison in the common case, and on a server destroying objects fast enough
    ///     to invalidate constantly the cache degrades to vanilla's behaviour rather than to
    ///     something wrong.
    ///
    /// zdo.IsValid() is still checked per entry on top of that. It is a single flag test rather
    /// than a dictionary lookup, and it covers any release path that does not route through the
    /// generation counter.
    /// </summary>
    internal static class SyncListCache {

        /// <summary>Vanilla's threshold for pulling in the distant ring as well.</summary>
        private const int DistantFillThreshold = 10;

        /// <summary>Peers whose entries are dropped once the table exceeds this, so a long-lived
        /// server does not accumulate entries for peers that came and went.</summary>
        private const int PruneAboveEntries = 64;

        private sealed class Entry {
            internal Vector2s Zone;
            internal SimulationDistance Distance;
            internal float StampedAt;
            internal int Generation;
            internal readonly List<ZDO> Sector = new List<ZDO>();
            internal readonly List<ZDO> Distant = new List<ZDO>();
        }

        private static readonly Dictionary<long, Entry> Cache = new Dictionary<long, Entry>();

        /// <summary>Bumped by every ZDO destruction. An entry stamped at an older value describes a
        /// world in which ZDOs that may since have been recycled were still alive.</summary>
        private static int _generation;

        // Diagnostics for nps_stats. The hit rate is what says whether this mechanism is doing
        // anything at all on a given server - a busy world destroying objects constantly will
        // show a low rate, and that is the mechanism correctly declining to be unsafe.
        internal static long Hits;
        internal static long Misses;
        internal static long ScansAvoided => Hits;

        /// <summary>Every destruction, from the ZDOMan.HandleDestroyedZDO postfix.</summary>
        internal static void OnZdoDestroyed() {
            _generation++;
        }

        /// <summary>Bulk ZDO release - a world load. Nothing cached describes the new world.</summary>
        internal static void InvalidateAll() {
            _generation++;
        }

        /// <summary>
        /// Builds <paramref name="toSync"/> exactly as vanilla's server branch would, reusing the
        /// sector scan where it is still good. False means we declined and vanilla should run.
        /// </summary>
        internal static bool TryBuildSyncList(ZDOMan man, ZDOMan.ZDOPeer peer, List<ZDO> toSync) {
            if (!PatchGuard.IsActive(Mechanism.SyncListCache)) { return false; }
            if (!ValConfig.EnableSyncListCache.Value) { return false; }

            // The client branch of CreateSyncList walks m_clientChangeQueue instead of the
            // sectors and has nothing to cache. Deliberately gated on the same IsServer() test
            // vanilla branches on rather than on NpsEnv.IsHost, so the two can never disagree.
            if (ZNet.instance == null || !ZNet.instance.IsServer()) { return false; }
            if (man == null || peer?.m_peer == null) { return false; }
            if (ZoneSystem.instance == null) { return false; }

            Vector3 refPos = peer.m_peer.GetRefPos();
            Vector2s zone = ZoneSystem.GetZone(refPos);
            SimulationDistance distance = ZoneCompat.For(peer.m_peer);
            long uid = peer.m_peer.m_uid;
            float now = Time.realtimeSinceStartup;
            float ttlSeconds = ValConfig.SyncListCacheMs.Value / 1000f;

            if (!Cache.TryGetValue(uid, out Entry entry)) {
                entry = new Entry();
                Cache[uid] = entry;
                if (Cache.Count > PruneAboveEntries) { Prune(man); }
            }

            // Simulation distance is part of the key, not just the zone: it is negotiated per peer
            // and a player can change it mid-session from the graphics settings, which changes
            // which sectors the scan covers without moving the peer out of its zone.
            bool fresh = ttlSeconds > 0f
                         && entry.StampedAt > 0f
                         && now - entry.StampedAt < ttlSeconds
                         && entry.Generation == _generation
                         && entry.Zone.x == zone.x
                         && entry.Zone.y == zone.y
                         && entry.Distance.Equals(distance);

            if (fresh) {
                Hits++;
            } else {
                Misses++;
                entry.Sector.Clear();
                entry.Distant.Clear();
                man.FindSectorObjects(zone, distance, entry.Sector, entry.Distant);
                entry.Zone = zone;
                entry.Distance = distance;
                entry.StampedAt = now;
                entry.Generation = _generation;
            }

            // From here down this is vanilla's server branch, line for line, with the validity
            // test added. Order matters: the sort runs on the near set only, and the distant ring
            // is appended after it unsorted, exactly as vanilla does.
            for (int i = 0; i < entry.Sector.Count; i++) {
                ZDO zdo = entry.Sector[i];
                if (zdo == null || !zdo.IsValid()) { continue; }
                if (peer.ShouldSend(zdo)) { toSync.Add(zdo); }
            }

            man.ServerSortSendZDOS(toSync, refPos, peer);

            if (toSync.Count < DistantFillThreshold) {
                for (int i = 0; i < entry.Distant.Count; i++) {
                    ZDO zdo = entry.Distant[i];
                    if (zdo == null || !zdo.IsValid()) { continue; }
                    if (peer.ShouldSend(zdo)) { toSync.Add(zdo); }
                }
            }

            man.AddForceSendZdos(peer, toSync);
            return true;
        }

        internal static void ForgetPeer(long peerUid) {
            Cache.Remove(peerUid);
        }

        private static void Prune(ZDOMan man) {
            HashSet<long> live = new HashSet<long>();
            for (int i = 0; i < man.m_peers.Count; i++) {
                ZDOMan.ZDOPeer peer = man.m_peers[i];
                if (peer?.m_peer != null) { live.Add(peer.m_peer.m_uid); }
            }

            List<long> dead = new List<long>();
            foreach (KeyValuePair<long, Entry> kv in Cache) {
                if (!live.Contains(kv.Key)) { dead.Add(kv.Key); }
            }
            for (int i = 0; i < dead.Count; i++) { Cache.Remove(dead[i]); }
        }

        internal static void Reset() {
            Cache.Clear();
            _generation = 0;
            Hits = 0;
            Misses = 0;
        }
    }
}
