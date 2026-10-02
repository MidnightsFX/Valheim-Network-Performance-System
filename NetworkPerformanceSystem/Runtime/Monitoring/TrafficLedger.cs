using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// What each player's connection to the host carries, message by message, in both
    /// directions.
    ///
    /// The 2026-09-28 recording had every client uploading 60-135 KB/s, and creatures, ships and
    /// the player's own character made up only 30-47% of the ZDOs in it. Nothing recorded said
    /// what the rest was - other objects, or RPCs such as a mod's periodic upload, which at the
    /// connection level is only ever "RoutedRPC".
    ///
    /// Everything a connection carries is a ZRpc package, [int method hash][parameters]. Every
    /// received package passes ZRpc.HandlePackage and every sent one ZRpc.SendPackage, except the
    /// host's relay, which M19 writes to the socket itself and reports here directly. Each package
    /// is tallied three ways:
    ///
    ///   * by ZRpc method: ZDOData, RoutedRPC, the ping (hash 0), and anything a mod registers on
    ///     the connection itself;
    ///   * a RoutedRPC again by the routed method inside it - an object RPC's name, or a Jotunn
    ///     CustomRPC's "Mod.GUID!NAME" - read straight out of the package bytes, where
    ///     RoutedRPCData always puts it;
    ///   * the ZDOs in a ZDOData package by prefab, as the host applies each one it receives
    ///     (ZDO.Deserialize) and writes each one it sends (ZDO.Serialize).
    ///
    /// Each prefab also counts the distinct objects behind its sends. A count alone cannot tell a
    /// player walking into a fortress (1,250 walls, each sent once) from the 2026-10-01 support
    /// loop (the same 1,250 walls re-sent every second); the number of objects can.
    ///
    /// Sizes are the ZRpc package as the game hands it over, before Steam's framing, resends and
    /// acks; the peer record's inBps/outBps are Steam's own count of the wire, for comparison.
    ///
    /// Host only, and only while monitoring runs: the hooks are MonitoringPatches'. Each window
    /// becomes one "traffic" record per player and direction, heaviest entries first.
    /// </summary>
    internal static class TrafficLedger {

        internal enum Kind : byte { Rpc = 0, Routed = 1, Zdo = 2 }

        /// <summary>What a ZDO costs in a ZDOData package besides its serialised data: id (12),
        /// owner revision (2), data revision (4), owner (8), position (12), data length (4).</summary>
        internal const int ZdoHeaderBytes = 42;

        /// <summary>Where the routed method hash sits in a "RoutedRPC" package: after the ZRpc
        /// method hash (4) and the length of the parameter package (4), RoutedRPCData.Serialize
        /// writes the message id, sender and target (8 each) and the target ZDO (12).</summary>
        internal const int RoutedMethodOffset = 44;

        internal static readonly int RoutedRpcHash = "RoutedRPC".GetStableHashCode();
        internal static readonly int ZdoDataHash = "ZDOData".GetStableHashCode();

        private const int TopEntries = 16;
        private const int ReportMs = 10000;
        private const int PeersPerReportInterval = 12;

        internal sealed class Tally {
            internal long Count;
            internal long Bytes;

            /// <summary>The distinct ZDOs this window, for prefab tallies only; null for methods.</summary>
            internal HashSet<ZDOID> Objects;
        }

        internal sealed class Direction {
            internal readonly Dictionary<long, Tally> Entries = new Dictionary<long, Tally>();
            internal long Packages;
            internal long Bytes;

            internal Tally Add(Kind kind, int hash, int bytes) {
                long key = Key(kind, hash);
                if (!Entries.TryGetValue(key, out Tally tally)) {
                    tally = new Tally();
                    Entries[key] = tally;
                }
                tally.Count++;
                tally.Bytes += bytes;
                return tally;
            }

            internal void AddZdo(int prefab, ZDOID uid, int bytes) {
                Tally tally = Add(Kind.Zdo, prefab, bytes);
                if (tally.Objects == null) { tally.Objects = new HashSet<ZDOID>(); }
                tally.Objects.Add(uid);
            }

            /// <summary>Counts back to zero, entries kept: the same methods and prefabs come
            /// round every window, and the set of them is bounded by what the game and its mods
            /// contain. The object sets keep their capacity, which is the most of that prefab one
            /// player has had in range at once.</summary>
            internal void Clear() {
                foreach (Tally tally in Entries.Values) {
                    tally.Count = 0;
                    tally.Bytes = 0;
                    tally.Objects?.Clear();
                }
                Packages = 0;
                Bytes = 0;
            }
        }

        internal sealed class PeerLedger {
            internal long Uid;
            internal readonly Direction In = new Direction();
            internal readonly Direction Out = new Direction();

            /// <summary>The last window's heaviest entries per direction, for nps_stats.</summary>
            internal string InSummary;
            internal string OutSummary;
        }

        private static readonly Dictionary<ZRpc, PeerLedger> ByRpc = new Dictionary<ZRpc, PeerLedger>();
        private static readonly Dictionary<long, PeerLedger> ByUid = new Dictionary<long, PeerLedger>();
        private static readonly List<KeyValuePair<long, Tally>> Sorted = new List<KeyValuePair<long, Tally>>();
        private static readonly List<ZRpc> Stale = new List<ZRpc>();
        private static readonly StringBuilder Scratch = new StringBuilder(2048);

        /// <summary>The peer SendZDOs is writing for, while it runs; 0 otherwise.</summary>
        internal static long SendingTo;

        private static double _lastReportMs;
        private static double _windowStartMs;

        internal static long Key(Kind kind, int hash) => ((long)kind << 32) | (uint)hash;
        internal static Kind KindOf(long key) => (Kind)(key >> 32);
        internal static int HashOf(long key) => unchecked((int)(uint)key);

        // -- taps ----------------------------------------------------------------------------

        internal static void OnReceived(ZRpc rpc, ZPackage package) {
            PeerLedger peer = For(rpc);
            if (peer != null) { Count(peer.In, package); }
        }

        internal static void OnSent(ZRpc rpc, ZPackage package) {
            PeerLedger peer = For(rpc);
            if (peer != null) { Count(peer.Out, package); }
        }

        internal static void OnZdoReceived(long peerUid, ZDO zdo, int dataBytes) {
            if (peerUid == 0L || zdo == null) { return; }
            ForUid(peerUid).In.AddZdo(zdo.GetPrefab(), zdo.m_uid, dataBytes + ZdoHeaderBytes);
        }

        internal static void OnZdoSent(ZDO zdo, int dataBytes) {
            if (SendingTo == 0L || zdo == null) { return; }
            ForUid(SendingTo).Out.AddZdo(zdo.GetPrefab(), zdo.m_uid, dataBytes + ZdoHeaderBytes);
        }

        /// <summary>
        /// One package, whose bytes start at 0 in its buffer: a received package fresh from the
        /// socket, a sent one just written from the start by Invoke, or M19's relay frame, which
        /// is laid out as Invoke lays it out.
        /// </summary>
        private static void Count(Direction direction, ZPackage package) {
            int size = package.Size();
            if (size < 4) { return; }

            byte[] buffer = package.m_stream.GetBuffer();
            int method = ReadInt(buffer, 0);
            direction.Packages++;
            direction.Bytes += size;
            direction.Add(Kind.Rpc, method, size);

            // Debug mode writes the method name after the hash, which moves everything after it.
            if (method == RoutedRpcHash && size >= RoutedMethodOffset + 4 && !ZRpc.m_DEBUG) {
                direction.Add(Kind.Routed, ReadInt(buffer, RoutedMethodOffset), size);
            }
        }

        /// <summary>Little-endian, as BinaryWriter writes it. Pure.</summary>
        internal static int ReadInt(byte[] buffer, int offset) {
            return buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);
        }

        /// <summary>The ledger for this connection. A peer still handshaking has no session id yet
        /// and is not counted; it is looked up again on its next package rather than cached.</summary>
        private static PeerLedger For(ZRpc rpc) {
            if (rpc == null) { return null; }
            if (ByRpc.TryGetValue(rpc, out PeerLedger known)) { return known; }

            ZNetPeer peer = Patches.NetworkChannelPatches.FindPeerByRpc(rpc);
            if (peer == null || peer.m_uid == 0L) { return null; }

            PeerLedger ledger = ForUid(peer.m_uid);
            ByRpc[rpc] = ledger;
            return ledger;
        }

        private static PeerLedger ForUid(long uid) {
            if (!ByUid.TryGetValue(uid, out PeerLedger ledger)) {
                ledger = new PeerLedger { Uid = uid };
                ByUid[uid] = ledger;
            }
            return ledger;
        }

        // -- reports -------------------------------------------------------------------------

        /// <summary>Every ten seconds, stretched by a further ten for each twelve players so a full
        /// server writes about as much as a small one.</summary>
        internal static double ReportIntervalMs(int peers) {
            int stretch = (peers + PeersPerReportInterval - 1) / PeersPerReportInterval;
            return ReportMs * (double)System.Math.Max(1, stretch);
        }

        internal static void Tick(double nowMs, int peers) {
            if (_windowStartMs <= 0d) {
                _windowStartMs = nowMs;
                _lastReportMs = nowMs;
                return;
            }
            if (nowMs - _lastReportMs < ReportIntervalMs(peers)) { return; }

            double windowMs = nowMs - _windowStartMs;
            _lastReportMs = nowMs;
            _windowStartMs = nowMs;

            TrafficNames.LearnJotunnRpcs();
            foreach (PeerLedger ledger in ByUid.Values) {
                ledger.InSummary = Report(ledger, ledger.In, "in", nowMs, windowMs);
                ledger.OutSummary = Report(ledger, ledger.Out, "out", nowMs, windowMs);
                ledger.In.Clear();
                ledger.Out.Clear();
            }
        }

        /// <summary>One "traffic" record, and the nps_stats line for the same window. Null for a
        /// direction that carried nothing.</summary>
        private static string Report(PeerLedger ledger, Direction direction, string label, double nowMs, double windowMs) {
            if (direction.Packages == 0) { return null; }

            long zdoDataBytes = TallyBytes(direction, Kind.Rpc, ZdoDataHash);
            long routedBytes = TallyBytes(direction, Kind.Rpc, RoutedRpcHash);

            JsonLine line = Monitoring.Line.Begin("traffic")
                .Num("t", nowMs, "0.#")
                .Id("uid", ledger.Uid)
                .Str("dir", label)
                .Num("ms", windowMs, "0")
                .Int("pkts", direction.Packages)
                .Int("bytes", direction.Bytes)
                .Raw("rpc", ListJson(direction, Kind.Rpc, int.MaxValue, 0L))
                .Raw("routed", ListJson(direction, Kind.Routed, TopEntries, 0L))
                .Raw("zdo", ListJson(direction, Kind.Zdo, TopEntries, zdoDataBytes));
            Monitoring.EmitServer(line.End());

            return Summarise(direction, windowMs, zdoDataBytes, routedBytes);
        }

        private static long TallyBytes(Direction direction, Kind kind, int hash) {
            return direction.Entries.TryGetValue(Key(kind, hash), out Tally tally) ? tally.Bytes : 0L;
        }

        /// <summary>
        /// [["name", count, bytes], ...] for one kind, heaviest first: the first <paramref name="top"/>
        /// by name, the rest as "(other)". ZDO entries carry a fourth member, the distinct objects
        /// those sends were - count over objects is how many times each went out in the window.
        /// A ZDO has one prefab, so the objects of "(other)" are a plain sum. For ZDOs,
        /// <paramref name="containerBytes"/> is the ZDOData total, and what the per-prefab tallies
        /// do not account for - the packages' own framing, and received ZDOs the host did not apply
        /// (no newer than its own copy, or a creature update M25 refused from a stale owner) - is
        /// listed as "(not attributed)", with no objects.
        /// </summary>
        private static string ListJson(Direction direction, Kind kind, int top, long containerBytes) {
            Sorted.Clear();
            long attributed = 0L;
            foreach (KeyValuePair<long, Tally> entry in direction.Entries) {
                if (entry.Value.Count == 0 || KindOf(entry.Key) != kind) { continue; }
                Sorted.Add(entry);
                attributed += entry.Value.Bytes;
            }
            Sorted.Sort((a, b) => b.Value.Bytes.CompareTo(a.Value.Bytes));

            bool zdo = kind == Kind.Zdo;
            Scratch.Length = 0;
            Scratch.Append('[');
            long otherCount = 0L;
            long otherBytes = 0L;
            long otherObjects = 0L;
            for (int i = 0; i < Sorted.Count; i++) {
                Tally tally = Sorted[i].Value;
                long objects = zdo ? ObjectsOf(tally) : -1L;
                if (i >= top) {
                    otherCount += tally.Count;
                    otherBytes += tally.Bytes;
                    otherObjects += objects;
                    continue;
                }
                Entry(NameOf(Sorted[i].Key), tally.Count, tally.Bytes, objects);
            }
            if (otherCount > 0L) { Entry("(other)", otherCount, otherBytes, zdo ? otherObjects : -1L); }
            if (zdo && containerBytes > attributed) { Entry("(not attributed)", 0L, containerBytes - attributed, 0L); }
            Scratch.Append(']');
            Sorted.Clear();
            return Scratch.ToString();
        }

        private static long ObjectsOf(Tally tally) => tally.Objects != null ? tally.Objects.Count : 0L;

        /// <summary>One list member; <paramref name="objects"/> is written only when it is not
        /// negative, which is ZDO entries only.</summary>
        private static void Entry(string name, long count, long bytes, long objects) {
            if (Scratch.Length > 1) { Scratch.Append(','); }
            Scratch.Append('[');
            JsonLine.AppendString(Scratch, name);
            Scratch.Append(',').Append(count.ToString(CultureInfo.InvariantCulture))
                   .Append(',').Append(bytes.ToString(CultureInfo.InvariantCulture));
            if (objects >= 0L) { Scratch.Append(',').Append(objects.ToString(CultureInfo.InvariantCulture)); }
            Scratch.Append(']');
        }

        /// <summary>"x12" - how many times each object went out on average, for a prefab entry in
        /// the nps_stats summary; empty below 1.5, where repeats are not the story. Pure.</summary>
        internal static string RepeatsLabel(long count, long objects) {
            if (objects <= 0L) { return string.Empty; }
            double each = (double)count / objects;
            return each < 1.5d ? string.Empty : " x" + each.ToString("0", CultureInfo.InvariantCulture);
        }

        internal static string NameOf(long key) {
            int hash = HashOf(key);
            switch (KindOf(key)) {
                case Kind.Zdo:
                    return Monitoring.PrefabNameByHash(hash);
                case Kind.Rpc:
                    if (hash == 0) { return "(ping)"; }
                    return TrafficNames.Describe(hash);
                default:
                    return TrafficNames.Describe(hash);
            }
        }

        /// <summary>
        /// "12.3 KB/s: ZDOData 9.1 (Ashlands_Wall_2x2 6.0 x10, Wolf 3.2 x150), RoutedRPC 2.0
        /// (Mod!NAME 1.5), ..." - the connection methods, with the heaviest routed methods and
        /// prefabs inside the two containers. KB/s over the window; "xN" after a prefab is how
        /// many times each of its objects was sent (see RepeatsLabel).
        /// </summary>
        private static string Summarise(Direction direction, double windowMs, long zdoDataBytes, long routedBytes) {
            double seconds = System.Math.Max(0.001d, windowMs / 1000d);
            StringBuilder sb = new StringBuilder(256);
            sb.Append(Rate(direction.Bytes, seconds)).Append(" KB/s:");

            Sorted.Clear();
            foreach (KeyValuePair<long, Tally> entry in direction.Entries) {
                if (entry.Value.Count > 0 && KindOf(entry.Key) == Kind.Rpc) { Sorted.Add(entry); }
            }
            Sorted.Sort((a, b) => b.Value.Bytes.CompareTo(a.Value.Bytes));
            List<KeyValuePair<long, Tally>> methods = new List<KeyValuePair<long, Tally>>(Sorted);

            for (int i = 0; i < methods.Count && i < 4; i++) {
                int hash = HashOf(methods[i].Key);
                sb.Append(i == 0 ? " " : ", ").Append(NameOf(methods[i].Key)).Append(' ').Append(Rate(methods[i].Value.Bytes, seconds));
                if (hash == ZdoDataHash && zdoDataBytes > 0) { AppendInside(sb, direction, Kind.Zdo, seconds); }
                if (hash == RoutedRpcHash && routedBytes > 0) { AppendInside(sb, direction, Kind.Routed, seconds); }
            }
            Sorted.Clear();
            return sb.ToString();
        }

        private static void AppendInside(StringBuilder sb, Direction direction, Kind kind, double seconds) {
            Sorted.Clear();
            foreach (KeyValuePair<long, Tally> entry in direction.Entries) {
                if (entry.Value.Count > 0 && KindOf(entry.Key) == kind) { Sorted.Add(entry); }
            }
            if (Sorted.Count == 0) { return; }
            Sorted.Sort((a, b) => b.Value.Bytes.CompareTo(a.Value.Bytes));

            sb.Append(" (");
            for (int i = 0; i < Sorted.Count && i < 3; i++) {
                if (i > 0) { sb.Append(", "); }
                sb.Append(NameOf(Sorted[i].Key)).Append(' ').Append(Rate(Sorted[i].Value.Bytes, seconds));
                if (kind == Kind.Zdo) { sb.Append(RepeatsLabel(Sorted[i].Value.Count, ObjectsOf(Sorted[i].Value))); }
            }
            sb.Append(')');
            Sorted.Clear();
        }

        private static string Rate(long bytes, double seconds) {
            return (bytes / 1024d / seconds).ToString("0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>The last window's summary lines for one peer, for nps_stats.</summary>
        internal static bool TryGetSummary(long uid, out string received, out string sent) {
            if (ByUid.TryGetValue(uid, out PeerLedger ledger) && (ledger.InSummary != null || ledger.OutSummary != null)) {
                received = ledger.InSummary;
                sent = ledger.OutSummary;
                return true;
            }
            received = null;
            sent = null;
            return false;
        }

        internal static void ForgetPeer(long uid) {
            ByUid.Remove(uid);
            Stale.Clear();
            foreach (KeyValuePair<ZRpc, PeerLedger> entry in ByRpc) {
                if (entry.Value.Uid == uid) { Stale.Add(entry.Key); }
            }
            for (int i = 0; i < Stale.Count; i++) { ByRpc.Remove(Stale[i]); }
            Stale.Clear();
        }

        internal static void Reset() {
            ByRpc.Clear();
            ByUid.Clear();
            Sorted.Clear();
            Stale.Clear();
            SendingTo = 0L;
            _lastReportMs = 0d;
            _windowStartMs = 0d;
        }
    }
}
