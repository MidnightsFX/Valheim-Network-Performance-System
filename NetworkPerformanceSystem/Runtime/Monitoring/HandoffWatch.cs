using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// Host side. Follows each host-made ownership change for ten seconds to find out whether it
    /// held, and measures how regularly each owner's creatures actually report in.
    ///
    /// Both questions are answered from the same place - the moment a ZDO arrives in a packet -
    /// because that is the only point at which the host can see who is still writing an object,
    /// as opposed to who it believes owns it. A handoff that held shows the old owner's writes
    /// stopping and the new owner's starting. One that did not shows the old owner's packet
    /// arriving with a higher data revision and an older owner revision, which vanilla
    /// ZDOMan.RPC_ZDOData applies in full and which puts the old owner back.
    ///
    /// Holds ZDOIDs, never ZDO references: ZDOs are pooled, so a reference kept across frames can
    /// come to mean a different object.
    /// </summary>
    internal static class HandoffWatch {

        internal const double WindowMs = 10000d;

        /// <summary>A gap this long between packets for a creature that is awake and busy is a
        /// stall a player would have seen. Idle creatures write nothing and are not counted.</summary>
        private const double StallMs = 1000d;

        private const double GapReportMs = 5000d;
        private const double LastSeenTtlMs = 60000d;

        /// <summary>One object in four carries the packet-gap statistics. See NotePacket.</summary>
        internal const int SampleDivisor = 4;
        private const uint SampleMask = SampleDivisor - 1;

        private sealed class Entry {
            internal long Id;
            internal long From;
            internal long To;
            internal ushort OwnerRev;
            internal double StartMs;
            internal Vector3 StartPos;
            internal double OldLastWriteMs = -1d;
            internal double NewFirstWriteMs = -1d;
            internal bool AwaitNewPos;
            internal float JumpMetres = -1f;
            internal int DragBacks;
            internal int RemoteChanges;

            /// <summary>Packets that would have dragged this handoff back and that
            /// OwnerRevisionGuard refused. Each one is a DragBack that did not happen.</summary>
            internal int Blocked;
        }

        private sealed class OwnerGaps {
            internal int Packets;
            internal int Gaps;
            internal double SumMs;
            internal double MaxMs;
            internal readonly int[] Histogram = new int[GapEdgesMs.Length + 1];
            internal readonly HashSet<ZDOID> Creatures = new HashSet<ZDOID>();

            internal void Clear() {
                Packets = 0;
                Gaps = 0;
                SumMs = 0d;
                MaxMs = 0d;
                System.Array.Clear(Histogram, 0, Histogram.Length);
                Creatures.Clear();
            }
        }

        /// <summary>Upper edges of the gap histogram, in ms; the last bucket is everything above.</summary>
        private static readonly double[] GapEdgesMs = { 100d, 250d, 500d, 1000d, 2000d };

        private static readonly Dictionary<ZDOID, Entry> Watched = new Dictionary<ZDOID, Entry>();
        private static readonly Dictionary<ZDOID, double> LastSeenMs = new Dictionary<ZDOID, double>();
        private static readonly Dictionary<long, OwnerGaps> GapsByOwner = new Dictionary<long, OwnerGaps>();
        private static readonly List<ZDOID> Scratch = new List<ZDOID>();

        private static double _lastGapReportMs;
        private static double _lastPruneMs;

        /// <summary>
        /// The packet names an owner the host has already moved on from. Kept free of game types
        /// so the rule itself can be checked offline. Revision wrap-around (a ushort) is ignored:
        /// it needs 65536 ownership changes of one object inside a ten second window.
        /// </summary>
        internal static bool IsDragBack(long packetOwner, ushort packetOwnerRev, long watchedTo, ushort watchedOwnerRev) {
            return packetOwner != watchedTo && packetOwnerRev < watchedOwnerRev;
        }

        internal static bool IsWatched(ZDOID uid) => Watched.ContainsKey(uid);

        /// <summary>Start following a host-made change. A change to an object already being
        /// followed closes the earlier one out first, marked as superseded.</summary>
        internal static void Open(long id, ZDO zdo, long from, long to, double nowMs) {
            if (Watched.TryGetValue(zdo.m_uid, out Entry earlier)) {
                Resolve(zdo.m_uid, earlier, nowMs, superseded: true);
            }

            Watched[zdo.m_uid] = new Entry {
                Id = id,
                From = from,
                To = to,
                OwnerRev = zdo.OwnerRevision,
                StartMs = nowMs,
                StartPos = zdo.GetPosition(),
            };
        }

        /// <summary>
        /// A Prioritized ZDO has arrived from <paramref name="fromPeer"/> and is about to be
        /// applied. Returns true when this packet drags a watched handoff back. Called before
        /// the packet's fields land, so everything read from the ZDO here is its previous state -
        /// which is the right state for judging the gap that has just ended.
        /// </summary>
        internal static bool NotePacket(ZDO zdo, long fromPeer, long packetOwner, double nowMs) {
            ZDOID uid = zdo.m_uid;

            // The gap statistics are kept for one object in SampleDivisor, chosen by id. This
            // runs for every simulated object in every packet the host receives - thousands a
            // second on a full server - and the two flag reads plus the three table touches below
            // were most of what it cost. A per-owner rate does not need every object to be exact;
            // it needs enough of them, and ids are assigned in sequence so the low bits pick an
            // even quarter. The recv_gap record says what it was divided by.
            if ((uid.ID & SampleMask) == 0) {
                if (LastSeenMs.TryGetValue(uid, out double last)) {
                    bool busy = zdo.GetBool(ZDOVars.s_alert) || zdo.GetBool(ZDOVars.s_haveTargetHash);
                    if (busy) { NoteGap(zdo, fromPeer, nowMs - last, nowMs); }
                }
                LastSeenMs[uid] = nowMs;

                if (!GapsByOwner.TryGetValue(fromPeer, out OwnerGaps gaps)) {
                    gaps = new OwnerGaps();
                    GapsByOwner[fromPeer] = gaps;
                }
                gaps.Packets++;
                gaps.Creatures.Add(uid);
            }

            // Everything below is about handoffs in flight, and there usually are none.
            if (Watched.Count == 0) { return false; }
            if (!Watched.TryGetValue(uid, out Entry entry)) { return false; }

            double sinceStart = nowMs - entry.StartMs;
            if (fromPeer == entry.From) {
                entry.OldLastWriteMs = sinceStart;
            } else if (fromPeer == entry.To && entry.NewFirstWriteMs < 0d) {
                entry.NewFirstWriteMs = sinceStart;
                entry.AwaitNewPos = true;                                     // the position lands after this call
            }

            if (packetOwner == zdo.GetOwner()) { return false; }

            // On the full-apply path vanilla has already overwritten OwnerRevision with the
            // packet's by the time SetOwnerInternal runs, so this reads the packet's revision. On
            // the owner-only path it still reads the host's, which is never below the watched one,
            // so that path can never be mistaken for a drag-back.
            if (IsDragBack(packetOwner, zdo.OwnerRevision, entry.To, entry.OwnerRev)) {
                entry.DragBacks++;
                return true;
            }

            entry.RemoteChanges++;
            return false;
        }

        /// <summary>OwnerRevisionGuard refused a packet that would have put an older owner back on
        /// this object.</summary>
        internal static void NoteBlocked(ZDOID uid) {
            if (Watched.Count == 0) { return; }
            if (Watched.TryGetValue(uid, out Entry entry)) { entry.Blocked++; }
        }

        private static void NoteGap(ZDO zdo, long owner, double gapMs, double nowMs) {
            if (!GapsByOwner.TryGetValue(owner, out OwnerGaps gaps)) {
                gaps = new OwnerGaps();
                GapsByOwner[owner] = gaps;
            }

            gaps.Gaps++;
            gaps.SumMs += gapMs;
            if (gapMs > gaps.MaxMs) { gaps.MaxMs = gapMs; }

            int bucket = GapEdgesMs.Length;
            for (int i = 0; i < GapEdgesMs.Length; i++) {
                if (gapMs < GapEdgesMs[i]) { bucket = i; break; }
            }
            gaps.Histogram[bucket]++;

            if (gapMs < StallMs) { return; }

            // Only a sampled object can be seen to stall, so a stall count is one in SampleDivisor
            // of the real one, the same as the gap counts.
            Monitoring.EmitServer(Monitoring.Line.Begin("stall")
                .Num("t", nowMs, "0.#")
                .Str("zdo", Monitoring.ZdoId(zdo.m_uid))
                .Str("prefab", Monitoring.PrefabName(zdo))
                .Id("owner", owner)
                .Int("sample", SampleDivisor)
                .Num("gapMs", gapMs, "0")
                .End());
        }

        internal static void Tick(double nowMs) {
            if (Watched.Count > 0) {
                Scratch.Clear();
                foreach (KeyValuePair<ZDOID, Entry> pair in Watched) {
                    Entry entry = pair.Value;

                    if (entry.AwaitNewPos) {
                        entry.AwaitNewPos = false;
                        ZDO zdo = ZDOMan.instance.GetZDO(pair.Key);
                        if (zdo != null) { entry.JumpMetres = Vector3.Distance(zdo.GetPosition(), entry.StartPos); }
                    }

                    if (nowMs - entry.StartMs >= WindowMs) { Scratch.Add(pair.Key); }
                }

                for (int i = 0; i < Scratch.Count; i++) {
                    Resolve(Scratch[i], Watched[Scratch[i]], nowMs, superseded: false);
                    Watched.Remove(Scratch[i]);
                }
            }

            if (nowMs - _lastGapReportMs >= GapReportMs) {
                _lastGapReportMs = nowMs;
                ReportGaps(nowMs);
            }

            if (nowMs - _lastPruneMs >= LastSeenTtlMs) {
                _lastPruneMs = nowMs;
                Scratch.Clear();
                foreach (KeyValuePair<ZDOID, double> pair in LastSeenMs) {
                    if (nowMs - pair.Value >= LastSeenTtlMs) { Scratch.Add(pair.Key); }
                }
                for (int i = 0; i < Scratch.Count; i++) { LastSeenMs.Remove(Scratch[i]); }
            }
        }

        private static void Resolve(ZDOID uid, Entry entry, double nowMs, bool superseded) {
            ZDO zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(uid) : null;
            long ownerNow = zdo != null ? zdo.GetOwner() : 0L;

            Monitoring.EmitServer(Monitoring.Line.Begin("handoff_result")
                .Num("t", nowMs, "0.#")
                .Int("id", entry.Id)
                .Str("zdo", Monitoring.ZdoId(uid))
                .Id("to", entry.To)
                .Id("ownerNow", ownerNow)
                .Flag("stuck", zdo != null && ownerNow == entry.To)
                .Flag("gone", zdo == null)
                .Flag("superseded", superseded)
                .Int("dragBacks", entry.DragBacks)
                .Int("blocked", entry.Blocked)
                .Int("remoteChanges", entry.RemoteChanges)
                .Num("oldLastWriteMs", entry.OldLastWriteMs, "0")
                .Num("newFirstWriteMs", entry.NewFirstWriteMs, "0")
                .Num("jumpM", entry.JumpMetres, "0.##")
                .End());
        }

        private static void ReportGaps(double nowMs) {
            foreach (KeyValuePair<long, OwnerGaps> pair in GapsByOwner) {
                OwnerGaps gaps = pair.Value;
                if (gaps.Packets == 0) { continue; }

                JsonLine line = Monitoring.Line.Begin("recv_gap")
                    .Num("t", nowMs, "0.#")
                    .Id("owner", pair.Key)
                    .Int("sample", SampleDivisor)
                    .Int("creatures", gaps.Creatures.Count)
                    .Int("packets", gaps.Packets)
                    .Int("gaps", gaps.Gaps)
                    .Num("meanMs", gaps.Gaps > 0 ? gaps.SumMs / gaps.Gaps : 0d, "0")
                    .Num("maxMs", gaps.MaxMs, "0");
                for (int i = 0; i < gaps.Histogram.Length; i++) {
                    line.Int(i < GapEdgesMs.Length ? "lt" + (int)GapEdgesMs[i] : "ge" + (int)GapEdgesMs[GapEdgesMs.Length - 1],
                             gaps.Histogram[i]);
                }
                Monitoring.EmitServer(line.End());

                gaps.Clear();
            }
        }

        internal static void ForgetPeer(long uid) {
            GapsByOwner.Remove(uid);
        }

        internal static void Reset() {
            Watched.Clear();
            LastSeenMs.Clear();
            GapsByOwner.Clear();
            Scratch.Clear();
            _lastGapReportMs = 0d;
            _lastPruneMs = 0d;
        }
    }
}
