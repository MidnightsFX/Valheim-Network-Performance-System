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

        /// <summary>
        /// A gap this long between packets for a creature that is awake, busy and moving is a stall
        /// a player would have seen. Idle creatures are not counted at all.
        ///
        /// Busy alone is not enough: every owned creature is written at least every two seconds
        /// whatever it is doing - BaseAI.UpdateRegeneration stores the world time (lastWorldTime)
        /// every 2 s of AI ticks - so an alert creature standing still (a Dverger mage on its spot,
        /// a leech waiting in the water) sends nothing but that heartbeat, and used to show up as a
        /// steady stream of 1.95-2.2 s "stalls": about half of every stall record before 1.13.0.
        /// So a gap of StallMs or more is judged once the packet that ended it has landed (see
        /// PendingGap): a creature that was still and still is - IsStandingGap - had nothing to
        /// say, and is counted as standing instead.
        /// </summary>
        private const double StallMs = 1000d;

        /// <summary>How far a creature may have moved across a gap and still count as standing.
        /// Over a gap of a second or more that is under half a metre a second.</summary>
        internal const float StandingMoveMetres = 0.5f;
        private const float StandingMoveSq = StandingMoveMetres * StandingMoveMetres;

        /// <summary>The velocity it last told everybody, under which it counts as still: the speed
        /// at which CreaturePacing reads a creature as moving. What viewers extrapolated during
        /// the gap, so under it they saw at most half a metre of drift either.</summary>
        private const float StandingSpeedSq = CreaturePacing.MovingSpeed * CreaturePacing.MovingSpeed;

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

            /// <summary>Gaps of StallMs or more from creatures standing still - left out of
            /// everything else here.</summary>
            internal int Standing;
            internal readonly int[] Histogram = new int[GapEdgesMs.Length + 1];
            internal readonly HashSet<ZDOID> Creatures = new HashSet<ZDOID>();

            internal void Clear() {
                Packets = 0;
                Gaps = 0;
                SumMs = 0d;
                MaxMs = 0d;
                Standing = 0;
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

        /// <summary>
        /// A long gap waiting to be judged. NotePacket runs before the packet that ended it is
        /// applied, so it can only take what the ZDO held before: where the creature was and the
        /// velocity it last told everybody. Tick, later in the same frame, reads where the packet
        /// put it. Held by ZDOID, never ZDO: see the class summary.
        /// </summary>
        private struct PendingGap {
            internal ZDOID Uid;
            internal long Owner;
            internal double GapMs;
            internal double AtMs;
            internal Vector3 PrevPos;
            internal float PrevSpeedSq;
        }

        private static readonly List<PendingGap> PendingGaps = new List<PendingGap>();

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
                    if (busy) {
                        double gapMs = nowMs - last;
                        if (gapMs < StallMs) {
                            NoteGap(zdo, fromPeer, gapMs, nowMs);
                        } else {
                            // Judged in Tick, once this packet's position has landed.
                            PendingGaps.Add(new PendingGap {
                                Uid = uid, Owner = fromPeer, GapMs = gapMs, AtMs = nowMs,
                                PrevPos = zdo.GetPosition(),
                                PrevSpeedSq = zdo.GetVec3(ZDOVars.s_velHash, Vector3.zero).sqrMagnitude,
                            });
                        }
                    }
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
            // A refused packet's position never lands, so a gap it ended cannot be judged by
            // movement: it is counted the way every gap was before.
            if (PendingGaps.Count > 0) { ResolveRefusedGap(uid); }
            if (Watched.Count == 0) { return; }
            if (Watched.TryGetValue(uid, out Entry entry)) { entry.Blocked++; }
        }

        /// <summary>Whether a gap of StallMs or more came from a creature standing still: the
        /// velocity it last sent was under the moving speed, and the packet that ended the gap put
        /// it less than StandingMoveMetres from where it was. Pure.</summary>
        internal static bool IsStandingGap(float prevSpeedSq, float movedSq) {
            return prevSpeedSq < StandingSpeedSq && movedSq < StandingMoveSq;
        }

        /// <summary>Judge the long gaps NotePacket queued this frame. Run before anything else in
        /// Tick, so they land in the window they happened in.</summary>
        private static void ResolvePendingGaps() {
            if (PendingGaps.Count == 0) { return; }
            ZDOMan zdoMan = ZDOMan.instance;
            for (int i = 0; i < PendingGaps.Count; i++) {
                PendingGap gap = PendingGaps[i];
                // A peer forgotten in between: NoteGap would make it an entry nobody reports.
                if (!GapsByOwner.TryGetValue(gap.Owner, out OwnerGaps gaps)) { continue; }
                ZDO zdo = zdoMan != null ? zdoMan.GetZDO(gap.Uid) : null;
                if (zdo == null) { continue; }

                float movedSq = (zdo.GetPosition() - gap.PrevPos).sqrMagnitude;
                if (IsStandingGap(gap.PrevSpeedSq, movedSq)) {
                    gaps.Standing++;
                    continue;
                }
                NoteGap(zdo, gap.Owner, gap.GapMs, gap.AtMs, Mathf.Sqrt(movedSq), Mathf.Sqrt(gap.PrevSpeedSq));
            }
            PendingGaps.Clear();
        }

        private static void ResolveRefusedGap(ZDOID uid) {
            for (int i = PendingGaps.Count - 1; i >= 0; i--) {
                PendingGap gap = PendingGaps[i];
                if (gap.Uid != uid) { continue; }
                PendingGaps.RemoveAt(i);

                ZDO zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(uid) : null;
                if (zdo != null && GapsByOwner.ContainsKey(gap.Owner)) {
                    NoteGap(zdo, gap.Owner, gap.GapMs, gap.AtMs);
                }
            }
        }

        private static void NoteGap(ZDO zdo, long owner, double gapMs, double nowMs,
                                    float movedMetres = -1f, float speed = -1f) {
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
            // of the real one, the same as the gap counts. movedM and speed are left out for a
            // gap whose packet was refused, which could not be measured.
            JsonLine line = Monitoring.Line.Begin("stall")
                .Num("t", nowMs, "0.#")
                .Str("zdo", Monitoring.ZdoId(zdo.m_uid))
                .Str("prefab", Monitoring.PrefabName(zdo))
                .Id("owner", owner)
                .Int("sample", SampleDivisor)
                .Num("gapMs", gapMs, "0");
            if (movedMetres >= 0f) { line.Num("movedM", movedMetres, "0.##"); }
            if (speed >= 0f) { line.Num("speed", speed, "0.##"); }
            Monitoring.EmitServer(line.End());
        }

        internal static void Tick(double nowMs) {
            ResolvePendingGaps();

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
                    .Num("maxMs", gaps.MaxMs, "0")
                    .Int("standing", gaps.Standing);
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
            PendingGaps.Clear();
            _lastGapReportMs = 0d;
            _lastPruneMs = 0d;
        }
    }
}
