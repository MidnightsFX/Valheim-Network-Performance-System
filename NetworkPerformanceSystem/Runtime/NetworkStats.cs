using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M5 - measurement. A performance mod that cannot demonstrate its own effect is
    /// unfalsifiable, and the specific number this mod exists to move (how often a peer is skipped
    /// entirely because its in-flight window is full) is invisible in-game otherwise.
    ///
    /// Instrumentation is off until someone asks for it. Sampling GetSendQueueSize costs a Steam
    /// API round trip per peer per send tick, which is fine at 20Hz while measuring and pointless
    /// otherwise.
    /// </summary>
    internal static class NetworkStats {

        internal sealed class PeerStats {
            internal string Name;
            internal long Uid;
            internal int SendAttempts;
            internal int SendsSkippedByBackpressure;
            internal int LastQueueBytes;
            internal int LastWindowBytes;
        }

        private static readonly Dictionary<long, PeerStats> Stats = new Dictionary<long, PeerStats>();

        /// <summary>Vanilla bails when the remaining window is under this, so the effective gate
        /// is `queue &lt; window - 2048`, not `queue &lt; window`.</summary>
        private const int MinPackageBytes = 2048;

        internal static bool Collecting { get; private set; }
        private static float _collectingSince;

        internal static void SetCollecting(bool enabled) {
            Collecting = enabled;
            if (enabled) {
                Stats.Clear();
                _collectingSince = Time.realtimeSinceStartup;
            }
        }

        internal static float CollectingSeconds =>
            Collecting ? Time.realtimeSinceStartup - _collectingSince : 0f;

        /// <summary>
        /// Called from a prefix on ZDOMan.SendZDOs while collecting. Reproduces vanilla's own
        /// backpressure test so we count the exact condition that makes a peer receive nothing
        /// this tick - the freeze half of freeze-then-teleport.
        /// </summary>
        internal static void RecordSendAttempt(ZDOMan.ZDOPeer peer, bool flush) {
            if (!Collecting) { return; }

            ZNetPeer netPeer = peer?.m_peer;
            if (netPeer?.m_socket == null || netPeer.m_uid == 0L) { return; }

            PeerStats entry = GetOrCreate(netPeer.m_uid, netPeer.m_playerName);

            int queue = netPeer.m_socket.GetSendQueueSize();
            int window = SendWindow.For(peer);

            entry.SendAttempts++;
            entry.LastQueueBytes = queue;
            entry.LastWindowBytes = window;

            bool overWindow = !flush && queue > window;
            bool tooLittleHeadroom = (window - queue) < MinPackageBytes;
            if (overWindow || tooLittleHeadroom) { entry.SendsSkippedByBackpressure++; }
        }

        private static PeerStats GetOrCreate(long uid, string name) {
            if (!Stats.TryGetValue(uid, out PeerStats entry)) {
                entry = new PeerStats { Uid = uid, Name = name };
                Stats[uid] = entry;
            }
            if (!string.IsNullOrEmpty(name)) { entry.Name = name; }
            return entry;
        }

        internal static string BuildReport() {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("=== NetworkPerformanceSystem ===");
            sb.AppendLine(Describe());
            sb.AppendLine();

            if (ZNet.instance == null) {
                sb.AppendLine("Not connected.");
                return sb.ToString();
            }

            sb.AppendLine(NpsEnv.IsHost()
                ? (NpsEnv.IsDedicated() ? "Role: dedicated host" : "Role: listen host")
                : "Role: client");

            AppendPeerTable(sb);
            AppendOwnership(sb);
            AppendExtrapolation(sb);

            if (!Collecting) {
                sb.AppendLine();
                sb.AppendLine("Send-window instrumentation is off. Run 'nps_stats collect' to start sampling,");
                sb.AppendLine("play for a while, then run 'nps_stats' again.");
            }

            return sb.ToString();
        }

        private static string Describe() {
            List<string> parts = new List<string>();
            foreach (Mechanism mechanism in System.Enum.GetValues(typeof(Mechanism))) {
                string reason = PatchGuard.GetDisableReason(mechanism);
                parts.Add(reason == null ? $"{mechanism}: on" : $"{mechanism}: OFF ({reason})");
            }
            return string.Join("\n", parts.ToArray());
        }

        private static void AppendPeerTable(StringBuilder sb) {
            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            sb.AppendLine();
            sb.AppendLine($"Peers ({peers.Count}):");

            if (peers.Count == 0) {
                sb.AppendLine("  (none)");
                return;
            }

            sb.AppendLine("  name                 rtt     jitter  window    queue   skipped");
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                long uid = peer.m_uid;

                string name = string.IsNullOrEmpty(peer.m_playerName) ? "(connecting)" : peer.m_playerName;
                string rtt = LatencyRegistry.HasMeasurement(uid)
                    ? $"{LatencyRegistry.MeasuredRttMs(uid):F0}ms"
                    : "-";
                string jitter = LatencyRegistry.HasMeasurement(uid)
                    ? $"{LatencyRegistry.MeasuredJitterMs(uid):F0}ms"
                    : "-";
                string window = SendWindow.TryGetLastWindow(uid, out int w) ? $"{w / 1024f:F1}KB" : "vanilla";

                string queue = "-";
                string skipped = "-";
                if (Stats.TryGetValue(uid, out PeerStats entry) && entry.SendAttempts > 0) {
                    queue = $"{entry.LastQueueBytes / 1024f:F1}KB";
                    float pct = 100f * entry.SendsSkippedByBackpressure / entry.SendAttempts;
                    skipped = $"{pct:F1}% ({entry.SendsSkippedByBackpressure}/{entry.SendAttempts})";
                }

                sb.AppendLine($"  {Pad(name, 20)} {Pad(rtt, 7)} {Pad(jitter, 7)} {Pad(window, 9)} {Pad(queue, 7)} {skipped}");
            }

            if (Collecting) {
                sb.AppendLine($"  (sampling for {CollectingSeconds:F0}s)");
            }
        }

        private static void AppendOwnership(StringBuilder sb) {
            if (!NpsEnv.IsHost()) { return; }

            sb.AppendLine();
            sb.AppendLine("Ownership (last pass):");
            sb.AppendLine($"  considered  {OwnershipArbiter.LastPassConsidered}");
            sb.AppendLine($"  reassigned  {OwnershipArbiter.LastPassReassigned}");
            sb.AppendLine($"  deferred    {OwnershipArbiter.LastPassDeferredByCap} (hit the per-pass cap)");
            sb.AppendLine($"  total since start  {OwnershipArbiter.TotalReassignments}");
        }

        private static void AppendExtrapolation(StringBuilder sb) {
            if (NpsEnv.IsHost() && NpsEnv.IsDedicated()) { return; }          // nothing rendered here

            sb.AppendLine();
            sb.AppendLine("Latency compensation (per second):");
            if (!NpsEnv.IsHost() && !LatencyRegistry.HasPublishedTable) {
                sb.AppendLine("  no latency table received - the host is not running this mod, so");
                sb.AppendLine("  rendering is exactly vanilla.");
                return;
            }
            sb.AppendLine($"  entities corrected  {NpsExtrapolate.SamplesThisSecond}");
            sb.AppendLine($"  mean staleness      {NpsExtrapolate.MeanStalenessMs:F0}ms");
            sb.AppendLine($"  mean correction     {NpsExtrapolate.MeanDisplacement:F2}m");
            sb.AppendLine($"  peak correction     {NpsExtrapolate.MaxDisplacement:F2}m");
            sb.AppendLine($"  clamp hits (total)  {NpsExtrapolate.ClampHits}");
        }

        internal static string BuildOverlay() {
            if (!NpsEnv.IsHost() && !LatencyRegistry.HasPublishedTable) {
                return "NPS: no latency table (host not running this mod) - rendering is vanilla";
            }

            long self = NpsEnv.LocalSessionId();
            float myRtt = NpsEnv.IsHost() ? 0f : LatencyRegistry.RttMs(self);

            return $"NPS  myRtt {myRtt:F0}ms   corrected {NpsExtrapolate.SamplesThisSecond}/s" +
                   $"   staleness {NpsExtrapolate.MeanStalenessMs:F0}ms" +
                   $"   shift {NpsExtrapolate.MeanDisplacement:F2}m (peak {NpsExtrapolate.MaxDisplacement:F2}m)" +
                   $"   clamps {NpsExtrapolate.ClampHits}";
        }

        private static string Pad(string value, int width) {
            if (value == null) { value = ""; }
            return value.Length >= width ? value : value + new string(' ', width - value.Length);
        }

        internal static void Reset() {
            Stats.Clear();
            Collecting = false;
        }
    }
}
