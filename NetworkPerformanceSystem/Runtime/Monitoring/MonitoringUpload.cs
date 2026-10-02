using System.Collections.Generic;
using System.Text;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// Gets client records to the host, which is the only machine that writes files.
    ///
    /// Two direct peer RPCs, registered alongside the mod's other channels. Nothing is routed, so
    /// nothing is ever relayed to a third party, and a peer without this mod drops the unknown
    /// method hash without a word.
    ///
    ///   Nps.MonHello  host -> client   "send me your records, at no more than this many bytes a
    ///                                   second" - or, later, "stop".
    ///   Nps.MonBatch  client -> host   up to 4KB of newline-joined records (the host accepts up
    ///                                   to 32KB), the client's clock at the moment of sending,
    ///                                   and how many records it has had to drop.
    ///
    /// A client records and uploads only after the host has asked, which is what keeps it silent
    /// on a server that is not monitoring, or is not running this mod at all. The player can
    /// refuse regardless (AllowMonitoringUpload).
    ///
    /// On the host every batch is untrusted: it is size-checked before it is read, rate-limited
    /// per peer, and each line is shape-checked by MonitoringBatch before it is written. The host
    /// prefixes each accepted batch with a header of its own carrying the sender, its receive
    /// time and the sender's RTT, which is all an offline pass needs to line the clocks up.
    /// </summary>
    internal static class MonitoringUpload {

        internal const string RpcHello = "Nps.MonHello";
        internal const string RpcBatch = "Nps.MonBatch";

        /// <summary>
        /// The most a client puts on the wire in one batch. Small on purpose, and much smaller
        /// than what the host will accept (MonitoringBatch.MaxBatchChars).
        ///
        /// A batch is a reliable message on the same socket as the game's own traffic, and the
        /// ZDO send path skips a tick whenever that socket's queue is over its window
        /// (ZDOMan.SendZDOs; the window is vanilla's 10240 or M2's sizing). A 32KB batch put
        /// every client over the window on every send, and its creatures froze for the time the
        /// batch took to be acknowledged - the monitor causing the stall it was there to record.
        /// 4KB fits inside any window the send path uses, and LinkHasRoom below checks that it
        /// does before each send rather than assuming it.
        /// </summary>
        internal const int SendBatchChars = 4096;

        /// <summary>The send path will not use a tick with less than this much window free
        /// (ZDOMan.SendZDOs: "if (num &lt; 2048) return false"), so a batch must leave at least
        /// that much behind it or the next ZDO tick is lost to it.</summary>
        private const int ZdoSendFloorBytes = 2048;

        private const double MinSendIntervalMs = 1000d;
        private const double MaxSendIntervalMs = 5000d;
        private const double HelloIntervalMs = 1000d;

        /// <summary>Records held while waiting for budget. Past this the newest are dropped: the
        /// alternative is unbounded memory on a client whose upload has stalled.</summary>
        private const int MaxBufferedChars = MonitoringBatch.MaxBatchChars * 4;

        /// <summary>How much looser the host's per-peer limit is than the rate it asked for. The
        /// client's own budget is the real control; this only has to stop a client that ignores
        /// it from filling the disk.</summary>
        private const double HostToleranceFactor = 4d;

        // -- client side -------------------------------------------------------------------

        /// <summary>The host has asked this client for its records.</summary>
        internal static bool HostWantsData;

        private static readonly List<string> Buffered = new List<string>();
        private static readonly StringBuilder Payload = new StringBuilder(MonitoringBatch.MaxBatchChars);
        private static int _bufferedChars;
        private static int _droppedTotal;
        private static int _bytesPerSecond = 2048;
        private static double _lastSendMs;
        private static ByteBudget _budget;

        /// <summary>Sends this client had to hold back because its link was already near its
        /// window. Not a loss on its own - the records wait - but a run of them means the link
        /// is what is limiting, not the budget.</summary>
        internal static int SendsDeferredForLink;

        // -- host side ---------------------------------------------------------------------

        private static readonly Dictionary<long, bool> Greeted = new Dictionary<long, bool>();
        private static readonly Dictionary<long, ByteBudget> PeerBudgets = new Dictionary<long, ByteBudget>();
        private static readonly List<string> ValidLines = new List<string>();
        private static readonly System.Diagnostics.Stopwatch HostClock = System.Diagnostics.Stopwatch.StartNew();
        private static double _lastHelloMs;

        internal static long BatchesAccepted;
        internal static long BatchesRejected;
        internal static long LinesRejected;
        internal static int LocalRecordsDropped => _droppedTotal;

        // -- client ------------------------------------------------------------------------

        internal static void Buffer(string line) {
            if (_bufferedChars + line.Length > MaxBufferedChars) {
                _droppedTotal++;
                return;
            }
            Buffered.Add(line);
            _bufferedChars += line.Length + 1;
        }

        /// <summary>
        /// One batch per interval, and the interval is what the rate asks for: a full batch every
        /// SendBatchChars / rate seconds, between one and five. The budget below still applies,
        /// but with the batch capped this is the effective rate limit and the budget only bites
        /// when the host asks for less than a batch a second.
        /// </summary>
        private static double SendIntervalMs() {
            double interval = 1000d * SendBatchChars / System.Math.Max(1, _bytesPerSecond);
            return System.Math.Max(MinSendIntervalMs, System.Math.Min(MaxSendIntervalMs, interval));
        }

        internal static void TickClient(double nowMs) {
            if (Buffered.Count == 0) { return; }
            if (nowMs - _lastSendMs < SendIntervalMs()) { return; }
            _lastSendMs = nowMs;
            SendOne(nowMs, respectLimits: true);
        }

        /// <summary>Monitoring is stopping. Whatever fits in one batch goes now; the rest is
        /// counted as dropped rather than trickled out after the fact.</summary>
        internal static void FlushOnStop() {
            if (Buffered.Count > 0) { SendOne(Monitoring.NowMs, respectLimits: false); }
            _droppedTotal += Buffered.Count;
            Buffered.Clear();
            _bufferedChars = 0;
        }

        /// <summary>
        /// Measure first, build second: a batch the budget or the link refuses is never built,
        /// and its lines wait in the buffer for the next interval. The host's own records take
        /// the host's accept limit rather than the wire cap - there is no link to protect, and a
        /// dedicated server rescuing a spawn area to itself writes a few hundred at once.
        /// </summary>
        private static void SendOne(double nowMs, bool respectLimits) {
            bool local = NpsEnv.IsHost();
            int cap = local ? MonitoringBatch.MaxBatchChars : SendBatchChars;

            int taken = 0;
            int chars = 0;
            while (taken < Buffered.Count && taken < MonitoringBatch.MaxBatchLines) {
                int next = Buffered[taken].Length + (taken > 0 ? 1 : 0);
                if (chars + next > cap) { break; }
                chars += next;
                taken++;
            }
            if (taken == 0) {
                // A single record longer than a batch. Cannot happen with the records this mod
                // builds; drop it rather than wedge the queue behind it.
                _bufferedChars -= Buffered[0].Length + 1;
                Buffered.RemoveAt(0);
                _droppedTotal++;
                return;
            }

            ZNetPeer server = null;
            if (!local) {
                server = ZNet.instance != null ? ZNet.instance.GetServerPeer() : null;
                if (server == null || !server.IsReady()) { DropFront(taken); return; }

                if (respectLimits) {
                    if (!LinkHasRoom(server, chars)) { SendsDeferredForLink++; return; }
                    if (_budget == null) {
                        _budget = new ByteBudget(_bytesPerSecond, 10d, SendBatchChars, nowMs / 1000d);
                    }
                    if (!_budget.TryTake(chars, nowMs / 1000d)) { return; }        // next interval
                }
            }

            Payload.Length = 0;
            for (int i = 0; i < taken; i++) {
                if (i > 0) { Payload.Append('\n'); }
                Payload.Append(Buffered[i]);
            }
            string payload = Payload.ToString();
            DropFront(taken);

            if (local) {
                // The host's own player, or a dedicated server's own simulation. Same records,
                // same header, no network.
                AcceptBatch(NpsEnv.LocalSessionId(), nowMs, _droppedTotal, payload);
                return;
            }

            ZPackage pkg = new ZPackage();
            pkg.Write(nowMs);
            pkg.Write(_droppedTotal);
            pkg.Write(payload);
            server.m_rpc.Invoke(RpcBatch, pkg);
        }

        private static void DropFront(int count) {
            for (int i = 0; i < count; i++) { _bufferedChars -= Buffered[i].Length + 1; }
            Buffered.RemoveRange(0, count);
        }

        /// <summary>
        /// Whether a batch this size fits on the link with the game's own traffic still able to
        /// move behind it. The test is the send path's own: the socket queue plus this batch must
        /// stay far enough under the window that the next ZDO tick still has its floor. The
        /// window is whatever the send path is using for the server right now - M2's sizing when
        /// it is on (SendWindow records it per send), vanilla's constant otherwise - and the queue
        /// is read the way the send path reads it, without M14's adjustment for other mods. A link
        /// that is already that full is one this client's creatures are already waiting on; adding
        /// to it is the one thing not to do.
        /// </summary>
        private static bool LinkHasRoom(ZNetPeer server, int batchBytes) {
            if (server.m_socket == null) { return false; }

            int window = SendWindow.TryGetLastWindow(server.m_uid, out int sized) ? sized : SendWindow.VanillaWindowBytes;
            int queued = SendQueueView.Real(server.m_socket);
            return queued + batchBytes <= window - ZdoSendFloorBytes;
        }

        internal static void RPC_Hello(ZRpc rpc, ZPackage pkg) {
            if (NpsEnv.IsHost()) { return; }

            try {
                bool wanted = pkg.ReadBool();
                int bytesPerSecond = pkg.ReadInt();
                HostWantsData = wanted;
                _bytesPerSecond = System.Math.Max(256, System.Math.Min(4096, bytesPerSecond));
                _budget = null;                                               // rebuilt at the new rate
            } catch (System.Exception) {
                HostWantsData = false;
            }
        }

        // -- host --------------------------------------------------------------------------

        /// <summary>
        /// Tell each ready peer whether its records are wanted, once, and again whenever that
        /// answer changes. Runs whether or not monitoring is on, because "stop" has to be sent
        /// after it has been switched off; a peer that was never asked is never told anything.
        /// </summary>
        internal static void TickHost() {
            double now = HostClock.Elapsed.TotalMilliseconds;
            if (now - _lastHelloMs < HelloIntervalMs) { return; }
            _lastHelloMs = now;

            bool wanted = Monitoring.Active && ValConfig.MonitoringCollectFromClients.Value;
            if (!wanted && Greeted.Count == 0) { return; }

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (!peer.IsReady()) { continue; }

                bool known = Greeted.TryGetValue(peer.m_uid, out bool told);
                if (known ? told == wanted : !wanted) { continue; }

                ZPackage pkg = new ZPackage();
                pkg.Write(wanted);
                pkg.Write(ValConfig.MonitoringClientBytesPerSecond.Value);
                peer.m_rpc.Invoke(RpcHello, pkg);
                Greeted[peer.m_uid] = wanted;
            }
        }

        internal static void RPC_Batch(ZRpc rpc, ZPackage pkg) {
            if (!NpsEnv.IsHost() || !Monitoring.Active) { return; }
            if (!ValConfig.MonitoringCollectFromClients.Value) { return; }

            ZNetPeer peer = Patches.NetworkChannelPatches.FindPeerByRpc(rpc);
            if (peer == null || peer.m_uid == 0L) { return; }

            int size = pkg.Size();
            if (size > MonitoringBatch.MaxPackageBytes) { BatchesRejected++; return; }

            double nowSeconds = HostClock.Elapsed.TotalSeconds;
            if (!PeerBudgets.TryGetValue(peer.m_uid, out ByteBudget budget)) {
                budget = new ByteBudget(ValConfig.MonitoringClientBytesPerSecond.Value * HostToleranceFactor, 60d,
                                        MonitoringBatch.MaxPackageBytes * 2, nowSeconds);
                PeerBudgets[peer.m_uid] = budget;
            }
            if (!budget.TryTake(size, nowSeconds)) { BatchesRejected++; return; }

            double clientMs;
            int dropped;
            string payload;
            try {
                clientMs = pkg.ReadDouble();
                dropped = pkg.ReadInt();
                payload = pkg.ReadString();
            } catch (System.Exception) {
                BatchesRejected++;
                return;
            }

            AcceptBatch(peer.m_uid, clientMs, dropped, payload);
        }

        private static void AcceptBatch(long source, double clientMs, int dropped, string payload) {
            ValidLines.Clear();
            int rejected = MonitoringBatch.Split(payload, ValidLines);
            LinesRejected += rejected;
            if (ValidLines.Count == 0) {
                if (rejected > 0) { BatchesRejected++; }
                return;
            }
            BatchesAccepted++;

            // Header and lines are queued back to back from the main thread, and the writer is a
            // single FIFO, so a batch's lines always follow their own header in the file.
            Monitoring.EmitServer(Monitoring.Line.Begin("batch")
                .Num("t", Monitoring.NowMs, "0.#")
                .Id("src", source)
                .Num("clientMs", clientMs, "0.#")
                .Num("rtt", LatencyRegistry.MeasuredRttMs(source), "0.#")
                .Int("lines", ValidLines.Count)
                .Int("rejected", rejected)
                .Int("clientDropped", dropped)
                .End());
            for (int i = 0; i < ValidLines.Count; i++) { Monitoring.EmitServer(ValidLines[i]); }
        }

        internal static void ForgetPeer(long uid) {
            Greeted.Remove(uid);
            PeerBudgets.Remove(uid);
        }

        internal static void Reset() {
            HostWantsData = false;
            Buffered.Clear();
            _bufferedChars = 0;
            _droppedTotal = 0;
            _lastSendMs = 0d;
            _budget = null;
            SendsDeferredForLink = 0;
            Greeted.Clear();
            PeerBudgets.Clear();
            ValidLines.Clear();
            _lastHelloMs = 0d;
            BatchesAccepted = 0L;
            BatchesRejected = 0L;
            LinesRejected = 0L;
        }
    }
}
