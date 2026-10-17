using System;
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
    /// THE POOLING HAZARD, and why each entry carries the id it was scanned with.
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
    ///     all. Every entry keeps the id its object had when the scan found it, and the filter
    ///     passes over an entry whose object is no longer valid (destroyed and still in the pool)
    ///     or no longer has that id (recycled into something else). Nothing destroyed or recycled
    ///     is ever listed from a cached scan, and a destroy no longer throws every peer's scan
    ///     away - which it used to, by a generation counter, so in a fight, with arrows and drops
    ///     destroyed every frame, the cache rescanned on nearly every send. An object recycled
    ///     into this peer's area is an object appearing, and the next rescan lists it.
    ///
    /// The generation counter still covers a world load, which releases every ZDO at once.
    ///
    /// THE FILTER'S "ALREADY SENT" ANSWERS ARE REMEMBERED, and only those.
    ///
    /// peer.ShouldSend is a dictionary lookup per object in the scan, and on a big base that is
    /// thousands of lookups per send - most of what a send costs. Profiled on a 2.3M-object
    /// world it was ~70% of building the list; the scan itself was ~20%. Nearly every answer is
    /// "no": the object has not changed since this peer was sent it. So each cached scan keeps,
    /// beside every entry, the revision pair from the last time ShouldSend said no. While the
    /// object still carries exactly that pair (and its id), the lookup is skipped: SendZDOs and
    /// RPC_ZDOData only ever raise a peer's record, so the answer is still no. Everything else -
    /// an object that changed, a new one, one that was listed last time - asks ShouldSend as
    /// before, so the list is the one vanilla would build from the scan, in the same order.
    ///
    /// What could make a remembered answer wrong is a peer's record going DOWN while the object
    /// stays the same. Vanilla never does that: it removes a record only when the object is
    /// destroyed, or moves sector (after its revision has moved, or out of every scan). The code
    /// here that does lower one says so through OnRecordLowered - M33 putting a parked record
    /// back, M25 recording the older owner revision a stale sender holds - and a peer that
    /// reconnects under the same uid gets a new ZDOPeer, which drops everything remembered. For a
    /// mod that lowers records without saying so, every remembered answer is dropped every
    /// RecheckAllSeconds, so the worst it sees is that long a delay before a resend.
    ///
    /// Remembered answers outlive a rescan. The new scan is walked against the old one by
    /// reference, stepping over the few objects that left it, and an answer moves over only to the
    /// same instance still carrying the same id, so a ZDO recycled from the pool never inherits one.
    /// </summary>
    internal static class SyncListCache {

        /// <summary>Vanilla's threshold for pulling in the distant ring as well.</summary>
        private const int DistantFillThreshold = 10;

        /// <summary>Peers whose entries are dropped once the table exceeds this, so a long-lived
        /// server does not accumulate entries for peers that came and went.</summary>
        private const int PruneAboveEntries = 64;

        /// <summary>Longest a remembered "already sent" answer is used without asking ShouldSend
        /// again. See the class summary: it bounds what a record lowered without notice costs.</summary>
        private const float RecheckAllSeconds = 0.5f;

        /// <summary>Set in every remembered revision word, so a remembered answer is never 0 and
        /// 0 can mean "ask".</summary>
        private const ulong Remembered = 1UL << 63;

        /// <summary>How far ahead in the old scan a rescan looks for an object before taking it
        /// as new. Objects leaving a sector come out a few at a time; a bigger shift (a zone
        /// change) has little worth carrying over anyway.</summary>
        private const int RescanWindow = 16;

        /// <summary>
        /// One list a scan fills, near or distant, and beside each entry the id its object had
        /// when scanned and the last "already sent" answer for it: Remembered | ownerRevision
        /// &lt;&lt; 32 | dataRevision, or 0 for none. The arrays are at least as long as Items.
        /// </summary>
        internal sealed class Scan {
            internal List<ZDO> Items = new List<ZDO>();
            internal ZDOID[] Ids = Array.Empty<ZDOID>();
            internal ulong[] Revisions = Array.Empty<ulong>();

            /// <summary>Objects whose record for this peer went down since this list was last
            /// filtered: their remembered answers are not used on the next pass.</summary>
            internal readonly HashSet<ZDOID> Lowered = new HashSet<ZDOID>();

            // The previous scan, kept only to line the next one up against.
            private List<ZDO> _previous = new List<ZDO>();
            private ZDOID[] _previousIds = Array.Empty<ZDOID>();
            private ulong[] _previousRevisions = Array.Empty<ulong>();

            /// <summary>Before a rescan fills Items: the current list becomes the previous one.</summary>
            internal void BeginRescan() {
                List<ZDO> items = _previous;
                _previous = Items;
                Items = items;
                Items.Clear();

                ZDOID[] ids = _previousIds;
                _previousIds = Ids;
                Ids = ids;
                ulong[] revisions = _previousRevisions;
                _previousRevisions = Revisions;
                Revisions = revisions;
            }

            /// <summary>After the rescan: note every entry's id, and walk both lists in step to
            /// carry each remembered answer over to the same object with the same id. A run that
            /// left the old list (up to RescanWindow long) is stepped over; an object the old list
            /// does not have just ahead is asked about. Nothing is carried over a scan of another
            /// area, whose order has nothing in common with this one.</summary>
            internal void EndRescan(bool sameArea) {
                int count = Items.Count;
                int previousCount = sameArea ? _previous.Count : 0;
                if (Ids.Length < count) {
                    int size = Math.Max(count, Ids.Length * 2);
                    Ids = new ZDOID[size];
                    Revisions = new ulong[size];
                }

                int old = 0;
                for (int i = 0; i < count; i++) {
                    ZDO zdo = Items[i];
                    ZDOID id = zdo != null ? zdo.m_uid : ZDOID.None;
                    if (old < previousCount && !ReferenceEquals(_previous[old], zdo)) {
                        int limit = Math.Min(previousCount, old + 1 + RescanWindow);
                        for (int ahead = old + 1; ahead < limit; ahead++) {
                            if (ReferenceEquals(_previous[ahead], zdo)) { old = ahead; break; }
                        }
                    }
                    if (old < previousCount && ReferenceEquals(_previous[old], zdo)) {
                        Revisions[i] = _previousIds[old] == id ? _previousRevisions[old] : 0UL;
                        old++;
                    } else {
                        Revisions[i] = 0UL;
                    }
                    Ids[i] = id;
                }

                _previous.Clear();                                             // no references held past their use
            }

            /// <summary>Ask ShouldSend for every entry on the next pass.</summary>
            internal void Forget() {
                Array.Clear(Revisions, 0, Items.Count);
                Lowered.Clear();
            }
        }

        private sealed class Entry {
            internal ZDOMan.ZDOPeer Peer;
            internal Vector2s Zone;
            internal SimulationDistance Distance;
            internal float StampedAt;
            internal float RecheckAt;
            internal int Generation;
            internal readonly Scan Sector = new Scan();
            internal readonly Scan Distant = new Scan();
        }

        private static readonly Dictionary<long, Entry> Cache = new Dictionary<long, Entry>();

        /// <summary>Bumped by a world load. An entry stamped at an older value describes a world
        /// whose ZDOs have all been released.</summary>
        private static int _generation;

        // Diagnostics for nps_stats. The hit rate is what says whether this mechanism is doing
        // anything at all on a given server.
        internal static long Hits;
        internal static long Misses;
        internal static long ScansAvoided => Hits;

        // Objects the filter looked at, and of those, how many a remembered answer settled.
        internal static long Checks;
        internal static long ChecksSkipped;

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

            // A peer that reconnected under the same uid has a new ZDOPeer with an empty record,
            // so nothing remembered about the old one holds. Past that, everything is asked again
            // every RecheckAllSeconds whatever happened.
            if (!ReferenceEquals(entry.Peer, peer) || now >= entry.RecheckAt) {
                entry.Peer = peer;
                entry.RecheckAt = now + RecheckAllSeconds;
                entry.Sector.Forget();
                entry.Distant.Forget();
            }

            // Simulation distance is part of the key, not just the zone: it is negotiated per peer
            // and a player can change it mid-session from the graphics settings, which changes
            // which sectors the scan covers without moving the peer out of its zone.
            bool sameArea = entry.StampedAt > 0f
                            && entry.Generation == _generation
                            && entry.Zone.x == zone.x
                            && entry.Zone.y == zone.y
                            && entry.Distance.Equals(distance);
            bool fresh = sameArea && ttlSeconds > 0f && now - entry.StampedAt < ttlSeconds;

            if (fresh) {
                Hits++;
            } else {
                Misses++;
                entry.Sector.BeginRescan();
                entry.Distant.BeginRescan();
                man.FindSectorObjects(zone, distance, entry.Sector.Items, entry.Distant.Items);
                entry.Sector.EndRescan(sameArea);
                entry.Distant.EndRescan(sameArea);
                entry.Zone = zone;
                entry.Distance = distance;
                entry.StampedAt = now;
                entry.Generation = _generation;
            }

            // From here down this is vanilla's server branch, line for line, with the identity
            // test and the remembered answers added. Order matters: the sort runs on the near set
            // only, and the distant ring is appended after it unsorted, exactly as vanilla does.
            Filter(entry.Sector, peer, toSync);

            man.ServerSortSendZDOS(toSync, refPos, peer);

            if (toSync.Count < DistantFillThreshold) {
                Filter(entry.Distant, peer, toSync);
            }

            man.AddForceSendZdos(peer, toSync);
            return true;
        }

        /// <summary>
        /// Appends every entry of the scan ShouldSend would send, in scan order, passing over any
        /// whose object was destroyed or recycled since the scan. A remembered "no" stands while
        /// the object keeps the revisions it had then and its record has not been lowered since;
        /// anything else asks ShouldSend, and a "no" is remembered.
        /// </summary>
        internal static void Filter(Scan scan, ZDOMan.ZDOPeer peer, List<ZDO> toSync) {
            List<ZDO> items = scan.Items;
            ZDOID[] ids = scan.Ids;
            ulong[] revisions = scan.Revisions;
            HashSet<ZDOID> lowered = scan.Lowered;
            bool anyLowered = lowered.Count > 0;
            int count = items.Count;
            int skipped = 0;

            for (int i = 0; i < count; i++) {
                ZDO zdo = items[i];
                if (zdo == null || !zdo.IsValid()) { continue; }
                ZDOID id = zdo.m_uid;
                if (ids[i] != id) { continue; }                                // recycled into another object
                ulong pair = Remembered | ((ulong)zdo.OwnerRevision << 32) | zdo.DataRevision;
                if (revisions[i] == pair && (!anyLowered || !lowered.Contains(id))) {
                    skipped++;
                    continue;
                }
                if (peer.ShouldSend(zdo)) {
                    toSync.Add(zdo);
                    revisions[i] = 0UL;
                } else {
                    revisions[i] = pair;
                }
            }

            if (anyLowered) { lowered.Clear(); }
            Checks += count;
            ChecksSkipped += skipped;
        }

        /// <summary>
        /// This peer's record for this object was just set lower than it was, while the object
        /// itself did not change - so a remembered "already sent" answer for it no longer holds.
        /// Null peer when the caller could not tell whose record it was: everything is asked again.
        /// </summary>
        internal static void OnRecordLowered(ZDOMan.ZDOPeer peer, ZDOID id) {
            if (peer?.m_peer == null) {
                foreach (Entry each in Cache.Values) {
                    each.Sector.Forget();
                    each.Distant.Forget();
                }
                return;
            }
            if (Cache.TryGetValue(peer.m_peer.m_uid, out Entry entry) && ReferenceEquals(entry.Peer, peer)) {
                entry.Sector.Lowered.Add(id);
                entry.Distant.Lowered.Add(id);
            }
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
            Checks = 0;
            ChecksSkipped = 0;
        }
    }
}
