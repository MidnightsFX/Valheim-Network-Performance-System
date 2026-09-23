using NetworkPerformanceSystem.Patches;
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

            // M8 - the transport's own view, which splits the single queue figure above into the
            // two halves that mean opposite things. See RttProbe.LinkStatus.
            internal int StatusSamples;
            internal long PendingByteSum;
            internal int PendingBytesPeak;
            internal long InFlightByteSum;
            internal int InFlightBytesPeak;
            internal int LastSendRateBytesPerSec;
            /// <summary>Samples where Steam was holding data it had not yet put on the wire. The
            /// count matters more than the size: sustained pending is standing queue, whereas a
            /// single busy tick is just a burst passing through.</summary>
            internal int SamplesWithPending;
            /// <summary>Steam's share of our packets that reached this peer. Steam sends at a
            /// fixed rate and never slows for loss, so this is the only sign that a peer's
            /// connection cannot take the rate it is being sent at.</summary>
            internal double QualityRemoteSum;
            /// <summary>Samples where Steam knew the delivery share. It reports a negative value
            /// until it has one, which must not be averaged in as total loss.</summary>
            internal int QualitySamples;

            internal float MeanPendingBytes => StatusSamples > 0 ? (float)PendingByteSum / StatusSamples : 0f;
            internal float MeanInFlightBytes => StatusSamples > 0 ? (float)InFlightByteSum / StatusSamples : 0f;
            internal float PendingSampleShare => StatusSamples > 0 ? (float)SamplesWithPending / StatusSamples : 0f;
            internal float MeanQualityRemote => QualitySamples > 0 ? (float)(QualityRemoteSum / QualitySamples) : 1f;

            // Send truncation: sends whose list ran past the window before its end, and what was
            // left behind. See RecordSendResult.
            internal int SendsOut;
            internal int SendsTruncated;
            internal long LeftOutSum;
            internal int LeftOutPeak;
            internal long ActorsLeftOutSum;
            internal int SendsWithActorsLeftOut;

            internal float TruncatedShare => SendsOut > 0 ? (float)SendsTruncated / SendsOut : 0f;
            internal float MeanLeftOut => SendsTruncated > 0 ? (float)LeftOutSum / SendsTruncated : 0f;
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

            int queue = SendQueueView.Real(netPeer.m_socket);                // what the send path reads, not what other mods see
            int window = SendWindow.For(peer);

            entry.SendAttempts++;
            entry.LastQueueBytes = queue;
            entry.LastWindowBytes = window;

            bool overWindow = !flush && queue > window;
            bool tooLittleHeadroom = (window - queue) < MinPackageBytes;
            if (overWindow || tooLittleHeadroom) { entry.SendsSkippedByBackpressure++; }

            // M8. One more transport read per peer per send tick, on the same opt-in budget as
            // GetSendQueueSize above. A socket that will not answer simply contributes no sample,
            // which is why StatusSamples is counted separately from SendAttempts.
            if (RttProbe.TryGetLinkStatus(netPeer.m_socket, out RttProbe.LinkStatus status)) {
                entry.StatusSamples++;
                entry.PendingByteSum += status.PendingBytes;
                entry.InFlightByteSum += status.InFlightBytes;
                entry.LastSendRateBytesPerSec = status.SendRateBytesPerSec;
                if (status.QualityRemote >= 0f) {
                    entry.QualityRemoteSum += status.QualityRemote;
                    entry.QualitySamples++;
                }
                if (status.PendingBytes > entry.PendingBytesPeak) { entry.PendingBytesPeak = status.PendingBytes; }
                if (status.InFlightBytes > entry.InFlightBytesPeak) { entry.InFlightBytesPeak = status.InFlightBytes; }
                if (status.PendingBytes > 0) { entry.SamplesWithPending++; }
            }
        }

        /// <summary>
        /// Called from a postfix on ZDOMan.SendZDOs while collecting, for a send that went out.
        ///
        /// SendZDOs writes its sorted list front to back and stops at the first object that does
        /// not fit in the window, so what went out is exactly the first <paramref name="sent"/>
        /// entries and everything after is what waits for next time. The question this answers is
        /// whether a send-priority rule could matter at all: priority only decides anything when
        /// a list is cut short, and then only if the cut falls among the objects players notice.
        /// "Actors" here are those objects: creatures, and Prioritized ZDOs (ships, carts,
        /// players), owned by someone other than the receiver - Monitoring.IsTracked, the same
        /// test everything else in 1.8.0 uses for "the things players watch". Not vanilla's own
        /// first send tier, which is the Prioritized flag alone: the game leaves every creature
        /// Default, so its sort puts ships and players at the front and creatures wherever they
        /// fall, and a creature past the cut is exactly the case a send priority that knew about
        /// creatures would change. A Prioritized object past the cut can only get there by a
        /// force-send inserted ahead of the sorted list.
        /// </summary>
        internal static void RecordSendResult(ZDOMan man, ZDOMan.ZDOPeer peer, int sent) {
            if (!Collecting || man == null) { return; }
            ZNetPeer netPeer = peer?.m_peer;
            if (netPeer == null || netPeer.m_uid == 0L) { return; }

            List<ZDO> list = man.m_tempToSync;
            int listed = list.Count;
            int leftOut = LeftOut(listed, sent);
            if (leftOut < 0) { return; }                                     // the counter reset under us; not a sample

            PeerStats entry = GetOrCreate(netPeer.m_uid, netPeer.m_playerName);
            entry.SendsOut++;
            if (leftOut == 0) { return; }

            int actors = 0;
            long receiver = netPeer.m_uid;
            for (int i = sent; i < listed; i++) {
                ZDO zdo = list[i];
                // Flag and owner first: they retire the buildings that make up most of any list
                // before the prefab lookup a Default ZDO needs to be recognised as a creature.
                if (zdo != null && zdo.HasOwner() && zdo.GetOwner() != receiver && Monitoring.IsTracked(zdo)) {
                    actors++;
                }
            }

            entry.SendsTruncated++;
            entry.LeftOutSum += leftOut;
            if (leftOut > entry.LeftOutPeak) { entry.LeftOutPeak = leftOut; }
            entry.ActorsLeftOutSum += actors;
            if (actors > 0) { entry.SendsWithActorsLeftOut++; }
        }

        /// <summary>How many of a send's list were left for next time, or -1 when the numbers
        /// cannot belong to one send. Pure.</summary>
        internal static int LeftOut(int listed, int sent) {
            if (sent < 0 || sent > listed) { return -1; }
            return listed - sent;
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

            AppendPlayerLimit(sb);
            AppendPeerTable(sb);
            AppendLinkPressure(sb);
            AppendSendTruncation(sb);
            AppendTransport(sb);
            AppendLossBackoff(sb);
            AppendTimeouts(sb);
            AppendPeerLiveness(sb);
            AppendEarlyZdoData(sb);
            AppendThirdPartyThresholds(sb);
            AppendScheduler(sb);
            AppendSyncListCache(sb);
            AppendRoutedRpc(sb);
            AppendOwnerRpc(sb);
            AppendReferencePositions(sb);
            AppendOwnership(sb);
            AppendShipHelm(sb);
            AppendExtrapolation(sb);
            AppendAllocationRelief(sb);
            AppendMonitoring(sb);

            if (!Collecting) {
                sb.AppendLine();
                sb.AppendLine("Send-window instrumentation is off. Run 'nps_stats_collect' to start sampling,");
                sb.AppendLine("play for a while, then run 'nps_stats' again.");
            }

            return sb.ToString();
        }

        private static string Describe() {
            List<string> parts = new List<string>();
            foreach (Mechanism mechanism in System.Enum.GetValues(typeof(Mechanism))) {
                string reason = PatchGuard.GetDisableReason(mechanism);
                if (reason == null && !IsEnabledInConfig(mechanism)) { reason = "disabled in config"; }
                parts.Add(reason == null ? $"{mechanism}: on" : $"{mechanism}: OFF ({reason})");
            }
            return string.Join("\n", parts.ToArray());
        }

        /// <summary>The config toggle behind each mechanism. PatchGuard tracks whether the patch
        /// could be applied; this tracks whether the admin actually wants it. The report has to
        /// reflect both, or an intentionally disabled feature reads as active.</summary>
        private static bool IsEnabledInConfig(Mechanism mechanism) {
            switch (mechanism) {
                case Mechanism.RttSampling: return true;    // no config toggle - it is the measurement, not a behaviour
                case Mechanism.SendWindow: return ValConfig.EnableSendWindowSizing.Value;
                case Mechanism.SendScheduler: return ValConfig.EnableSchedulerFix.Value;
                case Mechanism.Ownership: return ValConfig.EnableOwnershipArbitration.Value;
                case Mechanism.Extrapolation: return ValConfig.EnableLatencyCompensation.Value;
                case Mechanism.RoutedRpcFilter: return ValConfig.EnableRoutedRpcFilter.Value;
                case Mechanism.SteamTransport: return ValConfig.EnableSteamTransportTuning.Value;
                case Mechanism.SyncListCache: return ValConfig.EnableSyncListCache.Value;
                case Mechanism.ConnectionTimeout: return ValConfig.EnableConnectionTimeoutTuning.Value;
                case Mechanism.RpcOwnerRouting: return ValConfig.EnableStationRpcRouting.Value || ValConfig.EnableCreatureHitRouting.Value;
                case Mechanism.JotunnQueueLimit: return ValConfig.EnableSendWindowSizing.Value;
                case Mechanism.QueueSizeView: return ValConfig.EnableSendWindowSizing.Value && ValConfig.ReportVanillaQueueSize.Value;
                case Mechanism.DeserializeAlloc: return AllocationRelief.DeserializeWanted;
                case Mechanism.PacketReadAlloc: return AllocationRelief.PacketReadWanted;
                case Mechanism.SendPacketReuse: return AllocationRelief.SendPackageReuseWanted;
                case Mechanism.RpcInvokeFastPath: return AllocationRelief.RpcInvokeWanted;
                case Mechanism.RelaySendReuse: return AllocationRelief.RelaySendWanted;
                case Mechanism.ShipHelmOwnership: return ValConfig.ShipOwnershipFollowsHelmsman.Value;
                // No toggle of its own: it is the measurement, like RttSampling. What the two
                // consumers do with it is what the config controls.
                case Mechanism.PeerLiveness: return true;
                case Mechanism.GhostWatchdog: return ValConfig.EnableGhostWatchdog.Value;
                case Mechanism.LiveRefPos: return ValConfig.UseCharacterRefPos.Value;
                case Mechanism.EarlyZdoData: return ValConfig.EnableEarlyZdoDataGuard.Value;
                case Mechanism.OwnerRevisionGuard: return ValConfig.RejectStaleOwnerUpdates.Value;
                case Mechanism.LossBackoff: return ValConfig.EnableSteamTransportTuning.Value && ValConfig.EnableLossBackoff.Value;
                default: return true;
            }
        }

        private static void AppendPeerTable(StringBuilder sb) {
            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            sb.AppendLine();
            sb.AppendLine($"Peers ({peers.Count}):");

            if (peers.Count == 0) {
                sb.AppendLine("  (none)");
                return;
            }

            sb.AppendLine("  name                 rtt     jitter  window    for rate   queue   skipped");
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
                // The Steam rate the window was sized for - the other half of the product.
                string rate = SendWindow.TryGetLastRate(uid, out int r) ? $"{r / 1024}KB/s" : "-";

                string queue = "-";
                string skipped = "-";
                if (Stats.TryGetValue(uid, out PeerStats entry) && entry.SendAttempts > 0) {
                    queue = $"{entry.LastQueueBytes / 1024f:F1}KB";
                    float pct = 100f * entry.SendsSkippedByBackpressure / entry.SendAttempts;
                    skipped = $"{pct:F1}% ({entry.SendsSkippedByBackpressure}/{entry.SendAttempts})";
                }

                sb.AppendLine($"  {Pad(name, 20)} {Pad(rtt, 7)} {Pad(jitter, 7)} {Pad(window, 9)} {Pad(rate, 10)} {Pad(queue, 7)} {skipped}");
            }

            if (Collecting) {
                sb.AppendLine($"  (sampling for {CollectingSeconds:F0}s)");
            }
        }

        /// <summary>
        /// M8 - the question the peer table above cannot answer.
        ///
        /// Its "queue" column is GetSendQueueSize, which sums bytes on the wire with bytes Steam
        /// is holding back. Those mean opposite things: in-flight is the bandwidth-delay product
        /// being filled, which is what the M2 window is sized to do, while pending is standing
        /// queue that adds delay to everything behind it. A peer whose link is working perfectly
        /// and one being offered more than Steam's rate produce the same summed number.
        ///
        /// Steam paces every connection at one fixed rate and never slows for loss, so a player
        /// whose connection cannot take that rate does not show up as pending - they show up as
        /// packets that never arrive. That is the delivery column, and it is the thing to read
        /// before raising Send Rate KBps.
        /// </summary>
        private static void AppendLinkPressure(StringBuilder sb) {
            sb.AppendLine();
            sb.AppendLine("Link pressure (transport view):");

            if (!Collecting) {
                sb.AppendLine("  (not sampling - run 'nps_stats_collect')");
                return;
            }

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            bool anySamples = false;

            sb.AppendLine("  name                 in-flight  pending   pending%  steam rate  fill   delivered  verdict");
            for (int i = 0; i < peers.Count; i++) {
                long uid = peers[i].m_uid;
                if (!Stats.TryGetValue(uid, out PeerStats entry) || entry.StatusSamples == 0) { continue; }
                anySamples = true;

                string name = string.IsNullOrEmpty(peers[i].m_playerName) ? "(connecting)" : peers[i].m_playerName;
                int window = entry.LastWindowBytes > 0 ? entry.LastWindowBytes : SendWindow.VanillaWindowBytes;
                float fill = entry.MeanInFlightBytes / window;
                float pendingShare = entry.PendingSampleShare;
                float delivered = entry.MeanQualityRemote;

                sb.AppendLine(
                    $"  {Pad(name, 20)} " +
                    $"{Pad($"{entry.MeanInFlightBytes / 1024f:F1}KB", 10)} " +
                    $"{Pad($"{entry.MeanPendingBytes / 1024f:F1}KB", 9)} " +
                    $"{Pad($"{pendingShare * 100f:F0}%", 9)} " +
                    $"{Pad($"{entry.LastSendRateBytesPerSec / 1024f:F0}KB/s", 11)} " +
                    $"{Pad($"{fill * 100f:F0}%", 6)} " +
                    $"{Pad($"{delivered * 100f:F1}%", 10)} " +
                    Verdict(pendingShare, fill, delivered, LossAction(uid)));
            }

            if (!anySamples) {
                sb.AppendLine("  (no transport samples - Steam sockets only; crossplay peers report nothing here)");
                return;
            }

            sb.AppendLine("  in-flight = on the wire, unacked (the window working). pending = offered faster than Steam's rate.");
            sb.AppendLine("  steam rate = the fixed rate Steam sends to this player at. fill = in-flight as a share of the window.");
            sb.AppendLine("  delivered = share of packets that reached the player. Steam never slows down for loss by itself, so");
            sb.AppendLine("  this is the sign a player's connection cannot take the rate; loss backoff (below) is what acts on it.");
        }

        /// <summary>Below this share of packets delivered, a player is losing a meaningful part of
        /// what the fixed rate sends them - and every lost packet is sent again at the same rate.
        /// The same line loss backoff (M26) acts on, so the verdict and the mechanism agree.</summary>
        private static float LossyDeliveredShare =>
            ValConfig.LossBackoffThreshold != null ? ValConfig.LossBackoffThreshold.Value : 0.95f;

        /// <summary>What the LOSSY verdict should say for this peer: what loss backoff has done about
        /// it while it runs, and the manual remedy when it does not.</summary>
        private static string LossAction(long uid) {
            if (!LossBackoff.Wanted) { return "LOSSY - lower Send Rate KBps"; }
            if (LossBackoff.TryGetView(uid, out LossBackoff.View view) && view.Steps > 0) {
                return view.AtFloor
                    ? $"LOSSY - held at the {LossBackoff.FloorBytesPerSec / 1024} KB/s back-off floor"
                    : $"LOSSY - backed off to {view.OverrideBytesPerSec / 1024} KB/s (step {view.Steps})";
            }
            return "LOSSY - back-off pending (see Loss backoff)";
        }

        /// <summary>
        /// Three signals, most serious first:
        ///   delivered low             -> the player's connection drops what it is sent at this rate.
        ///                                Steam will not back off by itself; loss backoff does, or
        ///                                the admin lowers Send Rate KBps (lossAction says which).
        ///   pending high              -> more is being offered than Steam's fixed rate lets out.
        ///                                Raising Send Rate KBps would move it, if the host's upload
        ///                                and the player's connection can take it.
        ///   pending low, fill high    -> the window is the binding constraint and is doing its job.
        ///   pending low, fill low     -> nothing is the limit; there is simply not that much to send.
        /// </summary>
        private static string Verdict(float pendingShare, float fill, float delivered, string lossAction) {
            if (delivered < LossyDeliveredShare) { return lossAction; }
            if (pendingShare > 0.25f) { return "RATE-BOUND - Steam's rate is the limit for this player"; }
            if (pendingShare > 0.05f) { return "some queueing at Steam's rate"; }
            if (fill > 0.8f) { return "window-bound (working)"; }
            if (fill > 0.25f) { return "healthy"; }
            return "idle - nothing to send";
        }

        /// <summary>
        /// Whether a send-priority rule could matter here. Priority only decides anything when a
        /// send's list is cut short by the window, and only matters to players if the cut falls
        /// among the things they watch. See RecordSendResult.
        /// </summary>
        private static void AppendSendTruncation(StringBuilder sb) {
            sb.AppendLine();
            sb.AppendLine("Send truncation (sends that ran out of window before the end of their list):");

            if (!Collecting) {
                sb.AppendLine("  (not sampling - run 'nps_stats_collect')");
                return;
            }

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            bool any = false;
            for (int i = 0; i < peers.Count; i++) {
                if (!Stats.TryGetValue(peers[i].m_uid, out PeerStats entry) || entry.SendsOut == 0) { continue; }
                if (!any) {
                    sb.AppendLine("  name                 sends    truncated  left out mean/peak  actors left out");
                    any = true;
                }
                string name = string.IsNullOrEmpty(peers[i].m_playerName) ? "(connecting)" : peers[i].m_playerName;
                sb.AppendLine(
                    $"  {Pad(name, 20)} " +
                    $"{Pad(entry.SendsOut.ToString(), 8)} " +
                    $"{Pad($"{entry.TruncatedShare * 100f:F1}%", 10)} " +
                    $"{Pad($"{entry.MeanLeftOut:F1} / {entry.LeftOutPeak}", 20)} " +
                    $"{entry.ActorsLeftOutSum} (in {entry.SendsWithActorsLeftOut} sends)");
            }

            if (!any) {
                sb.AppendLine("  (no sends sampled yet)");
                return;
            }

            sb.AppendLine("  actors = creatures, ships, carts and players owned by someone else. The game sends ships and players first");
            sb.AppendLine("  but not creatures. Near 0 means the cut never falls on them and a different send priority would not change");
            sb.AppendLine("  what players see; a steady count is creatures waiting behind walls and trees.");
        }

        /// <summary>The rate underneath everything else, and what it costs the host.</summary>
        private static void AppendTransport(StringBuilder sb) {
            sb.AppendLine();
            sb.AppendLine("Steam transport:");
            if (SteamTransport.LastReadback == null) {
                sb.AppendLine("  not applied (disabled, or no Steam networking interface in this process)");
                return;
            }

            int pinned = SteamTransport.PinnedSendRateBytesPerSec;
            int configured = ValConfig.SteamSendRateKBps.Value;
            string setting = configured > 0 ? $"{configured} KB/s set" : "game default";
            if (!NpsEnv.IsHost()) { setting += ", not applied on a client"; }
            string backedOff = LossBackoff.BackedOffNow > 0 ? $" except {LossBackoff.BackedOffNow} backed off (see Loss backoff)" : "";
            sb.AppendLine(pinned > 0
                ? $"  send rate        {pinned / 1024} KB/s on every connection{backedOff} ({setting}) - a fixed pace, not a ceiling"
                : $"  send rate        UNEQUAL BOUNDS: SendRateMin {SteamTransport.ReadbackMinBytesPerSec / 1024} KB/s, SendRateMax {SteamTransport.ReadbackMaxBytesPerSec / 1024} KB/s ({setting})");
            if (pinned <= 0) {
                sb.AppendLine("                   another mod wrote one bound. Each connection runs at max(Min, 4380 bytes / its ping at connect),");
                sb.AppendLine("                   capped at Max - usually just Min. Send Rate KBps writes both.");
            }

            int nagle = SteamTransport.ReadbackNagleMicros;
            sb.AppendLine($"  nagle            {nagle}us (game default {SteamTransport.VanillaNagleMicros}us)");

            if (NpsEnv.IsHost() && pinned > 0) {
                int players = ZNet.instance.GetPeers().Count;
                float kbps = players * pinned / 1024f;
                sb.AppendLine($"  worst case       {players} players x {pinned / 1024} KB/s = {kbps:F0} KB/s ({kbps * 8f / 1024f:F1} Mbit/s) of host upload;");
                sb.AppendLine("                   Steam never slows down for a player who cannot keep up");
            }
            sb.AppendLine($"  readback         {SteamTransport.LastReadback}");
        }

        /// <summary>
        /// M26 - which players the host has slowed down for loss, and by how much. Host-only, like
        /// the mechanism: a client's upload stays at its own game's rate.
        /// </summary>
        private static void AppendLossBackoff(StringBuilder sb) {
            if (!NpsEnv.IsHost()) { return; }
            sb.AppendLine();
            sb.AppendLine("Loss backoff (per-player send rate):");

            string standDown = PatchGuard.GetDisableReason(Mechanism.LossBackoff);
            if (standDown != null) {
                sb.AppendLine($"  stood down: {standDown}");
                return;
            }
            if (!ValConfig.EnableSteamTransportTuning.Value) {
                sb.AppendLine("  off (Enable Transport Tuning is off, and this needs it)");
                return;
            }
            if (!ValConfig.EnableLossBackoff.Value) {
                sb.AppendLine("  off (Enable Loss Backoff is off) - a lossy player keeps the full rate and gets resends instead");
                return;
            }

            int pinned = SteamTransport.PinnedSendRateBytesPerSec;
            if (pinned <= 0) {
                sb.AppendLine("  idle: no single send rate to step down from (see Steam transport above)");
                return;
            }

            int floor = LossBackoff.FloorBytesPerSec;
            sb.AppendLine($"  rule             under {ValConfig.LossBackoffThreshold.Value * 100f:F0}% delivered for {ValConfig.LossBackoffHoldSeconds.Value}s: a quarter off that player's rate, never below {floor / 1024} KB/s;");
            sb.AppendLine($"                   clean for {ValConfig.LossBackoffRecoverSeconds.Value}s: one step back up, until it follows Send Rate KBps again");
            if (floor >= pinned) {
                sb.AppendLine($"  floor {floor / 1024} KB/s is not below the send rate {pinned / 1024} KB/s: nothing to step down to");
            }

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            float now = UnityEngine.Time.realtimeSinceStartup;
            bool any = false;
            for (int i = 0; i < peers.Count; i++) {
                if (!LossBackoff.TryGetView(peers[i].m_uid, out LossBackoff.View view)) { continue; }
                if (view.Steps == 0 && !view.Lossy) { continue; }
                if (!any) {
                    sb.AppendLine("  name                 delivered  rate now   global    steps  backed off for");
                    any = true;
                }
                string name = string.IsNullOrEmpty(peers[i].m_playerName) ? "(connecting)" : peers[i].m_playerName;
                string delivered = view.HasSample ? $"{view.Delivered * 100f:F1}%" : "-";
                string rateNow = view.Steps > 0 ? $"{view.OverrideBytesPerSec / 1024}KB/s" : $"{pinned / 1024}KB/s";
                string since = view.Steps > 0 ? Elapsed(now - view.BackedOffSince) : "(hold running)";
                sb.AppendLine($"  {Pad(name, 20)} {Pad(delivered, 10)} {Pad(rateNow, 10)} {Pad($"{pinned / 1024}KB/s", 9)} {Pad(view.Steps.ToString(), 6)} {since}");
            }
            if (!any) {
                sb.AppendLine("  (nobody backed off)");
            }
            sb.AppendLine($"  session totals   {LossBackoff.TotalStepsDown} steps down, {LossBackoff.TotalStepsUp} steps up, {LossBackoff.TotalCleared} back at the global rate");
        }

        private static string Elapsed(float seconds) {
            if (seconds < 60f) { return $"{seconds:F0}s"; }
            int minutes = (int)(seconds / 60f);
            if (minutes < 60) { return $"{minutes}m{(int)(seconds % 60f):D2}s"; }
            return $"{minutes / 60}h{minutes % 60:D2}m";
        }

        /// <summary>
        /// What either end will actually hang up at. Worth printing whether or not it has been
        /// changed, because the number in force on a client is the server's rather than the one in
        /// that player's own config file - and because the two layers are independent, so a stray
        /// mod writing one of them is otherwise invisible.
        /// </summary>
        private static void AppendTimeouts(StringBuilder sb) {
            sb.AppendLine();
            sb.AppendLine("Connection timeouts:");
            sb.AppendLine($"  drop after       {ConnectionTimeout.EffectiveRpcTimeoutSeconds}s without a packet (ZRpc ping)");
            sb.AppendLine(ConnectionTimeout.LastSteamReadback == null
                ? "  steam layer      not applied (no Steam networking interface in this process)"
                : $"  steam layer      {ConnectionTimeout.LastSteamReadback}");
            sb.AppendLine($"  loading phase    {ConnectionTimeout.EffectiveLoadingTimeoutSeconds}s (crossplay joins and world transfer)");
            if (!ConnectionTimeout.Active) {
                sb.AppendLine("  vanilla (timeout tuning is off)");
            } else if (NpsEnv.IsHost()
                       && ValConfig.ConnectionTimeoutSeconds.Value > ConnectionTimeout.VanillaRpcTimeoutSeconds
                       && !GhostEvictionOn) {
                sb.AppendLine("  note: a peer that is genuinely gone holds its slot, and ownership of everything it was");
                sb.AppendLine("  simulating, for that long. Objects an absent owner holds do not move. Turning on");
                sb.AppendLine("  'Evict Ghost Owners' separates the two, so only the slot is held that long.");
            }
        }

        private static bool GhostEvictionOn =>
            PatchGuard.IsActive(Mechanism.PeerLiveness) && ValConfig.EvictGhostOwners.Value;

        /// <summary>
        /// M21/M22 - who has stopped answering, and what is being done about it. Printed for both
        /// roles because the two halves are the same measurement read from opposite ends: a host
        /// sees which peers went quiet, a client sees whether the server did.
        /// </summary>
        private static void AppendPeerLiveness(StringBuilder sb) {
            sb.AppendLine();
            sb.AppendLine("Peer liveness:");

            string standDown = PatchGuard.GetDisableReason(Mechanism.PeerLiveness);
            if (standDown != null) {
                sb.AppendLine($"  stood down: {standDown}");
                return;
            }

            if (NpsEnv.IsHost()) {
                sb.AppendLine(GhostEvictionOn
                    ? $"  ghost owners     evicted after {ValConfig.GhostOwnerEvictSeconds.Value:F0}s of silence, or at once on a dead transport"
                    : "  ghost owners     not evicted (Evict Ghost Owners is off) - an absent owner holds its objects until it is disconnected");
                sb.AppendLine($"  quiet now        {PeerLiveness.GhostsNow} of {ZNet.instance.GetPeers().Count} peers"
                              + (PeerLiveness.LocalFaultSuspected ? "  [ALL QUIET - eviction suspended, fault looks local]" : ""));
                sb.AppendLine($"  session totals   {PeerLiveness.TotalGhosted} went quiet, {PeerLiveness.TotalRecovered} came back, {OwnershipArbiter.TotalGhostsExcluded} ownership exclusions");
            } else {
                ZNetPeer server = ZNet.instance.GetServerPeer();
                long uid = server != null ? server.m_uid : 0L;
                sb.AppendLine($"  server silence   {PeerLiveness.SilenceSeconds(uid):F1}s (transport says {PeerLiveness.LinkStateOf(uid)})");

                string watchdogDown = PatchGuard.GetDisableReason(Mechanism.GhostWatchdog);
                if (watchdogDown != null) {
                    sb.AppendLine($"  watchdog         stood down: {watchdogDown}");
                } else if (!ValConfig.EnableGhostWatchdog.Value) {
                    sb.AppendLine("  watchdog         off (EnableGhostWatchdog)");
                } else {
                    float deadline = ConnectionTimeout.EffectiveRpcTimeoutSeconds;
                    sb.AppendLine($"  watchdog         warns at {deadline * 0.5f:F0}s, leaves at {deadline:F0}s{(GhostWatchdog.Warning ? "  [WARNING ACTIVE]" : "")}");
                    sb.AppendLine($"  session totals   {GhostWatchdog.TotalWarnings} warnings, {GhostWatchdog.TotalTrips} disconnects");
                }
            }

            if (PeerLiveness.TotalStallsIgnored > 0) {
                sb.AppendLine($"  note: {PeerLiveness.TotalStallsIgnored} main-thread stalls were discounted from the silence timers");
                sb.AppendLine("  rather than counted as peers going quiet.");
            }
        }

        /// <summary>
        /// M24 - client only. Whether world updates ever arrived before this game could accept
        /// them. On a healthy install the answer is "never"; anything else names a mod that holds
        /// the handshake back, and says whether anything was lost to it.
        /// </summary>
        private static void AppendEarlyZdoData(StringBuilder sb) {
            if (NpsEnv.IsHost()) { return; }
            sb.AppendLine();
            sb.AppendLine("Early world data (arriving before the connection handshake finished):");

            string standDown = PatchGuard.GetDisableReason(Mechanism.EarlyZdoData);
            if (standDown != null) {
                sb.AppendLine($"  stood down: {standDown}");
                return;
            }
            if (!ValConfig.EnableEarlyZdoDataGuard.Value) {
                sb.AppendLine("  not held (EnableEarlyZdoDataGuard is off) - any that arrive early are dropped, as the game does");
                return;
            }
            if (ZdoDataGuard.HandshakesDelayed == 0 && ZdoDataGuard.PackagesDropped == 0) {
                sb.AppendLine("  none arrived early since the game started");
                return;
            }

            sb.AppendLine($"  delayed joins    {ZdoDataGuard.HandshakesDelayed} (last one {ZdoDataGuard.LastDelaySeconds:F1}s early)");
            sb.AppendLine($"  applied          {ZdoDataGuard.PackagesReplayed} packets, {ZdoDataGuard.BytesReplayed / 1024} KB");
            if (ZdoDataGuard.PackagesDropped > 0) {
                sb.AppendLine($"  lost             {ZdoDataGuard.PackagesDropped} packets (past the holding limit, or the connection closed first)");
            }
            if (ZdoDataGuard.ReplayFailures > 0) {
                sb.AppendLine($"  failed to apply  {ZdoDataGuard.ReplayFailures} (see the warning in the log)");
            }
            sb.AppendLine("  another mod delays ZNet.RPC_PeerInfo on this client; the log has a line for each delayed join.");
        }

        /// <summary>
        /// M13/M14 - the two answers to mods that wait on a fixed send queue size. Jotunn's limit
        /// is printed as read back from its field, so a Jotunn update that renamed it shows here
        /// as "not applied" rather than as a mystery 30-second disconnect. The view rows say
        /// whether anybody outside this mod is actually reading the queue, and how much is being
        /// kept out of what they read for each peer right now.
        /// </summary>
        private static void AppendThirdPartyThresholds(StringBuilder sb) {
            sb.AppendLine();
            sb.AppendLine("Third-party send queue thresholds:");

            if (JotunnSendQueue.Active) {
                string derivation = ValConfig.EnableSendWindowSizing.Value
                    ? $"Max Window Bytes {ValConfig.SendWindowMaxBytes.Value} + {JotunnSendQueue.HeadroomBytes} headroom"
                    : "send window sizing is off; Jotunn's own value";
                sb.AppendLine($"  Jotunn CustomRPC   {JotunnSendQueue.EffectiveLimit} bytes (Jotunn default {JotunnSendQueue.JotunnDefault}; {derivation})");
            } else {
                string reason = PatchGuard.GetDisableReason(Mechanism.JotunnQueueLimit);
                sb.AppendLine($"  Jotunn CustomRPC   not applied: {reason ?? "not started"}");
            }

            string viewReason = PatchGuard.GetDisableReason(Mechanism.QueueSizeView);
            if (viewReason != null) {
                sb.AppendLine($"  vanilla queue view stood down: {viewReason}");
                return;
            }
            if (!ValConfig.ReportVanillaQueueSize.Value) {
                sb.AppendLine("  vanilla queue view off (Report Vanilla Queue Size) - other mods read the real queue, and ones");
                sb.AppendLine("                     that wait for it to fall under 10-20KB may time out distant players");
                return;
            }
            if (!SendQueueView.OwnReadMarked) {
                sb.AppendLine("  vanilla queue view not running - the send path's queue read was not marked this session, so");
                sb.AppendLine("                     it could not be told apart from other mods' reads");
                return;
            }

            sb.AppendLine("  vanilla queue view on - other mods read each player's queue less the window opened above vanilla's");
            sb.AppendLine($"  outside reads      {SendQueueView.OutsideReads} since start, {SendQueueView.AdjustedReads} of them adjusted");

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            bool any = false;
            for (int i = 0; i < peers.Count; i++) {
                long uid = peers[i].m_uid;
                int extra = SendWindow.ExtraBytes(uid);
                if (extra <= 0) { continue; }
                if (!any) {
                    sb.AppendLine("  name                 window    kept out of other mods' reading");
                    any = true;
                }
                string name = string.IsNullOrEmpty(peers[i].m_playerName) ? "(connecting)" : peers[i].m_playerName;
                string window = $"{(extra + SendWindow.VanillaWindowBytes) / 1024f:F1}KB";
                sb.AppendLine($"  {Pad(name, 20)} {Pad(window, 9)} {extra / 1024f:F1}KB");
            }
            if (!any) {
                sb.AppendLine("  (no player's window is above vanilla right now - other mods read the real queue)");
            }
        }

        /// <summary>Hit rate is the whole story: it is the share of sends that did not have to
        /// rebuild the sector scan. A low rate on a busy world is the cache correctly refusing to
        /// serve entries invalidated by object destruction, not a fault.</summary>
        /// <summary>
        /// What the server will actually turn people away at. Worth printing even when it is
        /// vanilla, because the three numbers behind it can disagree - a crossplay lobby stuck at
        /// 10 refuses console players while Steam players keep joining, and nothing else in the
        /// game surfaces that.
        /// </summary>
        private static void AppendPlayerLimit(StringBuilder sb) {
            if (!NpsEnv.IsHost()) { return; }

            sb.AppendLine();
            sb.AppendLine("Player limit:");
            if (!PlayerLimit.Active) {
                sb.AppendLine(PlayerLimit.DeferredToValheimPlus
                    ? $"  set by Valheim Plus ({ZNet.instance.GetNrOfPlayers()} connected)"
                    : $"  vanilla ({PlayerLimit.VanillaLimit} players)");
                string reason = PatchGuard.GetDisableReason(Mechanism.PlayerLimit);
                if (reason != null) { sb.AppendLine($"  stood down: {reason}"); }
                return;
            }

            sb.AppendLine($"  accepting        {ZNet.instance.GetNrOfPlayers()} of {PlayerLimit.Configured}");

            // The three advertise-side sites, reported as what they actually returned rather than
            // what they should return. A site that reads "not called" on a registered server is
            // the one fact that separates "the patch missed" from "this backend was never used" -
            // a Steam-only server never touches the PlayFab pair, and vice versa, so a blank here
            // is only a fault if it is the backend the server registered on.
            sb.AppendLine($"  browser shows    {Advertised(PlayerLimit.AdvertisedSteamCapacity)} (Steam listing)");
            sb.AppendLine($"                   {Advertised(PlayerLimit.AdvertisedPlayFabCapacity)} (PlayFab lobby members)");
            sb.AppendLine($"  crossplay net    {Advertised(PlayerLimit.AdvertisedPartyCapacity)} (Party network devices)");

            if (PlayerLimit.CrossplayCapacityPinned && PlayerLimit.Configured > PlayerLimit.VanillaLimit) {
                sb.AppendLine($"  WARNING: the crossplay lobby is still capped at {PlayerLimit.VanillaLimit}. Steam players can join");
                sb.AppendLine("  past that; crossplay players are told the server is full. See the warning at startup.");
            }
            if (PlayerLimit.CrossplayNetworkPinned && PlayerLimit.Configured > PlayerLimit.VanillaLimit) {
                sb.AppendLine($"  WARNING: the crossplay Party network is still capped at {PlayerLimit.VanillaLimit} devices. Crossplay");
                sb.AppendLine("  players get past the lobby and then fail to connect. See the warning at startup.");
            }
        }

        /// <summary>
        /// Distinguishes "the site ran and returned this" from "the site never ran", which is the
        /// whole diagnostic value of these three numbers.
        /// </summary>
        private static string Advertised(int capacity) {
            return capacity > 0 ? capacity.ToString() : "not called";
        }

        private static void AppendSyncListCache(StringBuilder sb) {
            if (!NpsEnv.IsHost()) { return; }

            sb.AppendLine();
            sb.AppendLine("Sector scan cache (since start):");
            if (!PatchGuard.IsActive(Mechanism.SyncListCache) || !ValConfig.EnableSyncListCache.Value) {
                sb.AppendLine("  vanilla (full sector scan rebuilt on every send, per peer)");
                return;
            }

            long total = SyncListCache.Hits + SyncListCache.Misses;
            if (total == 0) {
                sb.AppendLine("  no sends yet");
                return;
            }
            sb.AppendLine($"  scans avoided    {SyncListCache.Hits}/{total} sends ({100f * SyncListCache.Hits / total:F0}%)");
            sb.AppendLine($"  cache window     {ValConfig.SyncListCacheMs.Value:F0}ms (invalidated early on zone change or any destroy)");
        }

        /// <summary>The send scheduler's effective output. On a small server this simply confirms
        /// the configured rate; on a large one it is the number that says whether the host is
        /// CPU-bound on the send path - the frame budget trades per-peer rate for frame time, and
        /// this is where that trade shows.</summary>
        private static void AppendScheduler(StringBuilder sb) {
            if (!NpsEnv.IsHost()) { return; }

            sb.AppendLine();
            sb.AppendLine("Send scheduler (last second):");
            if (!PatchGuard.IsActive(Mechanism.SendScheduler) || !ValConfig.EnableSchedulerFix.Value) {
                sb.AppendLine("  vanilla round-robin (one peer per rendered frame)");
                return;
            }

            int peers = ZDOMan.s_instance != null ? ZDOMan.s_instance.m_peers.Count : 0;
            float targetHz = 1f / Mathf.Max(0.01f, ValConfig.SendIntervalSeconds.Value);
            float effectiveHz = peers > 0 ? (float)SendSchedulerPatches.ServicedLastSecond / peers : 0f;

            sb.AppendLine($"  sends/s          {SendSchedulerPatches.ServicedLastSecond} across {peers} peers");
            sb.AppendLine($"  per-peer rate    {effectiveHz:F1} Hz (target {targetHz:F0} Hz)");
            sb.AppendLine($"  last frame       {SendSchedulerPatches.LastFrameServiced} peers");
            sb.AppendLine($"  budget breaks/s  {SendSchedulerPatches.BudgetBreaksLastSecond} (frame budget {ValConfig.SendSchedulerFrameBudgetMs.Value:F1}ms)");
            if (peers > 0 && SendSchedulerPatches.BudgetBreaksLastSecond > 0 && effectiveHz < targetHz * 0.75f) {
                sb.AppendLine("  NOTE: send rate is CPU-bound - the frame budget is cutting rounds short. Raise Frame Budget Ms");
                sb.AppendLine("  to trade server frame time for send rate, or accept the lower rate.");
            }
        }

        /// <summary>What the relay filter is saving. "Suppressed" deliveries are messages the
        /// receiver would have discarded anyway; the percentage is the share of relay traffic
        /// that was pure fan-out waste, which grows with player count.</summary>
        private static void AppendRoutedRpc(StringBuilder sb) {
            if (!NpsEnv.IsHost()) { return; }

            sb.AppendLine();
            sb.AppendLine("Routed RPC relay (since start):");
            if (!PatchGuard.IsActive(Mechanism.RoutedRpcFilter) || !ValConfig.EnableRoutedRpcFilter.Value) {
                sb.AppendLine("  vanilla (every broadcast RPC relayed to every peer)");
                return;
            }

            AppendRelayRow(sb, "ZDO-targeted", RoutedRpcFilter.TargetedEvents, RoutedRpcFilter.TargetedSent, RoutedRpcFilter.TargetedSuppressed);
            AppendOutOfRangeRow(sb);
            AppendRelayRow(sb, "DestroyZDO", RoutedRpcFilter.DestroyEvents, RoutedRpcFilter.DestroySent, RoutedRpcFilter.DestroySuppressed);
            AppendRelayRow(sb, "positional", RoutedRpcFilter.PositionalEvents, RoutedRpcFilter.PositionalSent, RoutedRpcFilter.PositionalSuppressed);
            sb.AppendLine($"  {Pad("global", 13)} {RoutedRpcFilter.GlobalEvents} events (relayed to everyone, by design)");
            sb.AppendLine($"  last second   sent {RoutedRpcFilter.SentLastSecond} msgs, suppressed {RoutedRpcFilter.SuppressedLastSecond} msgs");
        }

        /// <summary>The ZDO-targeted deliveries that went (or would have gone) to a peer holding the
        /// object but too far away to have it loaded. With the limit off this is the answer to
        /// "is Limit Relay By Distance worth turning on" - a share of what was actually sent.</summary>
        private static void AppendOutOfRangeRow(StringBuilder sb) {
            long outOfRange = RoutedRpcFilter.TargetedOutOfRange;
            if (ValConfig.LimitTargetedRelayByDistance.Value) {
                sb.AppendLine($"  {Pad("", 13)} of those suppressed, {outOfRange} were out of range (distance limit on)");
                return;
            }
            long sent = RoutedRpcFilter.TargetedSent;
            string share = sent > 0 ? $"{100f * outOfRange / sent:F0}% of sent" : "-";
            sb.AppendLine($"  {Pad("", 13)} {outOfRange} of those sent were out of range ({share}; Limit Relay By Distance is off)");
        }

        private static void AppendRelayRow(StringBuilder sb, string label, long events, long sent, long suppressed) {
            long total = sent + suppressed;
            string saved = total > 0 ? $"{100f * suppressed / total:F0}% saved" : "-";
            sb.AppendLine($"  {Pad(label, 13)} {events} events, {sent} sent, {suppressed} suppressed ({saved})");
        }

        /// <summary>What the station router had to correct. "Re-targeted" and "claimed" are the
        /// requests vanilla would have lost outright - each one is an item that stayed in the
        /// world. "Held" is the cost of doing it safely: how often the new owner had not yet been
        /// told, and the longest anyone waited for that.</summary>
        private static void AppendOwnerRpc(StringBuilder sb) {
            if (!NpsEnv.IsHost()) { return; }

            bool hooks = PatchGuard.IsActive(Mechanism.RpcOwnerRouting);

            sb.AppendLine();
            sb.AppendLine("Station requests (since start):");
            if (!hooks || !ValConfig.EnableStationRpcRouting.Value) {
                sb.AppendLine("  vanilla (delivered to whoever the sender's copy names as owner)");
            } else {
                RpcOwnerRouter.Counters s = RpcOwnerRouter.Stations;
                sb.AppendLine($"  {Pad("seen", 13)} {s.Seen} owner-addressed requests (add item/ore/fuel/ammo, tap, empty)");
                sb.AppendLine($"  {Pad("re-targeted", 13)} {s.Retargeted} (sender named a stale owner - delivered to the current one)");
                sb.AppendLine($"  {Pad("claimed", 13)} {s.Claimed} (no present owner - handed to the requesting player first)");
                AppendHoldLines(sb, s);
            }

            // Hits are the same machinery with a looser idea of "still there" and of who may take
            // over, so they get their own block: a hit is not an item, and "claimed" here is a
            // creature that nobody was simulating and that would not have taken the hit at all.
            sb.AppendLine();
            sb.AppendLine("Creature hits (since start):");
            if (!hooks || !ValConfig.EnableCreatureHitRouting.Value) {
                sb.AppendLine("  vanilla (delivered to whoever the attacker's copy names as owner, or to nobody if it names none)");
            } else {
                RpcOwnerRouter.Counters c = RpcOwnerRouter.Creatures;
                sb.AppendLine($"  {Pad("seen", 13)} {c.Seen} hits on creatures sent through the server");
                sb.AppendLine($"  {Pad("re-targeted", 13)} {c.Retargeted} (attacker's copy named a stale owner, or nobody - delivered to the one simulating it)");
                sb.AppendLine($"  {Pad("claimed", 13)} {c.Claimed} (nobody was simulating it - handed to the attacker first)");
                AppendHoldLines(sb, c);
            }

            if (RpcOwnerRouter.Waiting > 0) {
                sb.AppendLine($"  {Pad("waiting", 13)} {RpcOwnerRouter.Waiting} (both kinds)");
            }
        }

        private static void AppendHoldLines(StringBuilder sb, RpcOwnerRouter.Counters counters) {
            sb.AppendLine($"  {Pad("held", 13)} {counters.HeldCount} (waited for the owner to be sent its ownership; longest {counters.MaxHoldMs:F0}ms)");
            sb.AppendLine($"  {Pad("expired", 13)} {counters.Expired} (owner not synced within {RpcOwnerRouter.HoldTimeoutSeconds:F0}s - forwarded regardless)");
            sb.AppendLine($"  {Pad("dropped", 13)} {counters.Dropped} (object or player gone while waiting)");
        }

        /// <summary>
        /// M23 - where the host takes each player's position from, and how far off the player's
        /// own report was while the character was being followed. That distance is the error the
        /// host would otherwise have been working with: in what it sends each player, in who owns
        /// what around them, and in which zones it generates.
        /// </summary>
        private static void AppendReferencePositions(StringBuilder sb) {
            if (!NpsEnv.IsHost()) { return; }
            sb.AppendLine();
            sb.AppendLine("Reference positions (where the host thinks each player is):");

            string standDown = PatchGuard.GetDisableReason(Mechanism.LiveRefPos);
            if (standDown != null || !ValConfig.UseCharacterRefPos.Value) {
                sb.AppendLine(standDown != null
                    ? $"  stood down: {standDown}"
                    : "  as each player's own game reports it, every 2s (Use Character Position is off)");
                return;
            }

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            bool any = false;
            for (int i = 0; i < peers.Count; i++) {
                if (!LiveRefPos.TryGetState(peers[i].m_uid, out LiveRefPos.PeerState state)) { continue; }
                if (!any) {
                    sb.AppendLine("  name                 from                         report behind mean/peak   pointed elsewhere");
                    any = true;
                }
                string name = string.IsNullOrEmpty(peers[i].m_playerName) ? "(connecting)" : peers[i].m_playerName;
                string from;
                switch (state.Current) {
                    case LiveRefPos.Source.Character: from = "character"; break;
                    case LiveRefPos.Source.PointsElsewhere: from = "report (pointed elsewhere)"; break;
                    default: from = "report (no character)"; break;
                }
                string behind = state.BehindSamples > 0 ? $"{state.BehindMean:F1}m / {state.BehindPeak:F0}m" : "-";
                sb.AppendLine($"  {Pad(name, 20)} {Pad(from, 28)} {Pad(behind, 24)} {state.ReportsElsewhere}");
            }

            if (!any) {
                sb.AppendLine("  (no players yet)");
                return;
            }

            sb.AppendLine("  report behind = how far the player's own last report was from their character: the host's error");
            sb.AppendLine("  without this. A portal jump shows as a large peak until the next report.");
        }

        private static void AppendOwnership(StringBuilder sb) {
            if (!NpsEnv.IsHost()) { return; }

            sb.AppendLine();
            sb.AppendLine("Ownership (last pass):");
            // Candidates first: on a single-player world it reads 1, which immediately explains
            // why nothing is being optimised and why the per-target cap is the binding constraint.
            sb.AppendLine($"  candidates  {OwnershipArbiter.LastPassCandidates}");
            sb.AppendLine($"  considered  {OwnershipArbiter.LastPassConsidered}");
            sb.AppendLine($"  unowned     {OwnershipArbiter.LastPassUnownedOnEntry} on entry");
            sb.AppendLine($"  rescued     {OwnershipArbiter.LastPassRescued} (had no present owner - never capped)");
            sb.AppendLine($"  released    {OwnershipArbiter.LastPassReleased} (no eligible owner in range)");
            sb.AppendLine($"  optimised   {OwnershipArbiter.LastPassOptimised} (moving objects, given to a better-placed owner; includes the pulls below)");
            sb.AppendLine($"  deferred    {OwnershipArbiter.LastPassDeferred} (moving objects only, hit the per-pass cap of {OwnershipArbiter.LastPassCap})");

            // Creatures first, because until 1.8.0 none of the lines around this reached one.
            if (ValConfig.OwnershipArbitrateCreatures.Value) {
                sb.AppendLine($"  creatures   {OwnershipArbiter.LastPassCreaturesRescued} rescued, {OwnershipArbiter.LastPassCreaturesOptimised} moved (first in the queue, pushed out at once), {OwnershipArbiter.LastPassCreaturesKept} kept by an owner who has stepped away but still has them loaded");
            } else {
                sb.AppendLine("  creatures   not arbitrated (they keep whoever loaded them first, and are released at the edge of that player's area)");
            }

            // Same reasoning as tier 2 below: say "latency only" when it is off rather than say nothing.
            if (ValConfig.EnableCreatureProximityOwnership.Value) {
                sb.AppendLine($"  proximity   {OwnershipArbiter.LastPassProximityKept} kept with the only player near them, {OwnershipArbiter.LastPassProximityPulled} pulled to that player, {OwnershipArbiter.LastPassProximityRescued} rescues sent to that player instead of the lowest-latency one (within {ValConfig.CreatureProximityRadius.Value:F0}m)");
            } else {
                sb.AppendLine("  proximity   off (creatures are placed by latency alone, however far away the lowest-latency player is)");
            }

            // Tier 2 prints a "vanilla" line when it is off rather than nothing at all: a silently
            // missing mechanism is the worst failure mode for a performance mod, and this block is
            // where somebody looks to find out whether it is running.
            if (ValConfig.EnableInteractiveOwnership.Value) {
                sb.AppendLine($"  interactive {OwnershipArbiter.LastPassInteractiveOptimised} (bushes, rocks, trees given to the nearest player; {OwnershipArbiter.LastPassInteractiveDeferred} deferred, cap {OwnershipArbiter.LastPassInteractiveCap})");
                sb.AppendLine($"  int. held   {OwnershipArbiter.LastPassInteractiveHeld} (interactable, a nearer player exists, kept by the hold)");
                sb.AppendLine($"  by distance {OwnershipArbiter.LastPassNearestRescued} of {OwnershipArbiter.LastPassRescued} rescues placed on the nearest player rather than the lowest-latency one");
            } else {
                sb.AppendLine("  interactive vanilla (pickables, rocks and trees keep their owner until that player leaves)");
            }

            sb.AppendLine($"  static held {OwnershipArbiter.LastPassStaticHeld} (present owner is not the lowest-latency one; kept because the object does not move)");
            sb.AppendLine($"  pass time   {OwnershipArbiter.LastPassMs:F1}ms");
            sb.AppendLine($"  total since start  rescued {OwnershipArbiter.TotalRescued} ({OwnershipArbiter.TotalCreaturesRescued} creatures), optimised {OwnershipArbiter.TotalOptimised} ({OwnershipArbiter.TotalCreaturesOptimised} creatures), interactive {OwnershipArbiter.TotalInteractiveOptimised}, proximity pulled {OwnershipArbiter.TotalProximityPulled} / rescued {OwnershipArbiter.TotalProximityRescued}");

            // M25 is not part of the pass - it acts on every update the host receives - but what it
            // protects is exactly what the pass does, so it is reported here.
            if (!PatchGuard.IsActive(Mechanism.OwnerRevisionGuard)) {
                sb.AppendLine($"  stale owner stood down: {PatchGuard.GetDisableReason(Mechanism.OwnerRevisionGuard)}");
            } else if (!ValConfig.RejectStaleOwnerUpdates.Value) {
                sb.AppendLine("  stale owner off (an update written before its sender heard of an ownership change puts the old owner back, as vanilla)");
            } else {
                sb.AppendLine($"  stale owner {OwnerRevisionGuard.Kept} ownership changes kept against an older update that would have undone them ({OwnerRevisionGuard.KeptCreatures} creatures; {OwnerRevisionGuard.Seen} outdated updates in all, since start)");
            }

            // The failure this split exists to make visible: a growing backlog of ZDOs with no
            // simulator is frozen creatures that cannot be damaged, and it is otherwise invisible
            // in-game - the hit plays its effects and simply does nothing.
            if (OwnershipArbiter.LastPassUnownedOnEntry > OwnershipArbiter.LastPassRescued
                && OwnershipArbiter.LastPassUnownedOnEntry * 4 > OwnershipArbiter.LastPassConsidered) {
                sb.AppendLine("  WARNING: unowned backlog exceeds what this pass restored.");
            }
        }

        /// <summary>M20. Shown on every role, because a handoff is made by whichever machine owned
        /// the ship: a client counts its own, and the host's count does not include them. Helm
        /// rescues are the host's arbitration pass and need it to be running.</summary>
        private static void AppendShipHelm(StringBuilder sb) {
            sb.AppendLine();
            sb.AppendLine("Ship helm (since start):");
            if (!ShipHelmOwnership.Enabled) {
                sb.AppendLine("  vanilla (a ship stays with its owner while that player is aboard, whoever is steering)");
                return;
            }

            sb.AppendLine($"  {Pad("handed off", 13)} {ShipHelmOwnership.HandedOff} (ships this machine owned, given to the player who took the helm)");
            if (NpsEnv.IsHost() && PatchGuard.IsActive(Mechanism.Ownership) && ValConfig.EnableOwnershipArbitration.Value) {
                sb.AppendLine($"  {Pad("helm rescues", 13)} {OwnershipArbiter.TotalHelmRescued} (abandoned ship given to the player at its helm)");
            }
        }

        /// <summary>Whether the recorder is running and whether it is keeping up. The recording
        /// itself is in the files; this only answers "is it on, and is anything being lost".</summary>
        private static void AppendMonitoring(StringBuilder sb) {
            sb.AppendLine();
            sb.AppendLine("Network monitoring:");
            if (!Monitoring.Active) {
                sb.AppendLine(NpsEnv.IsHost()
                    ? "  off (Monitoring > Enable Network Monitoring)"
                    : "  off (the server has not asked this client for records, or AllowMonitoringUpload is false)");
                return;
            }

            if (Monitoring.ServerRole && Monitoring.Writer != null) {
                MonitoringWriter writer = Monitoring.Writer;
                sb.AppendLine($"  {Pad("recording to", 15)} {writer.Directory}");
                sb.AppendLine($"  {Pad("records", 15)} {writer.RecordsWritten} written, {writer.RecordsDropped} dropped");
                sb.AppendLine($"  {Pad("on disk", 15)} {writer.StoredBytes / (1024 * 1024)}MB of {ValConfig.MonitoringMaxDiskMB.Value}MB{(writer.DiskFull ? " - FULL, recording has stopped" : "")}");
                sb.AppendLine($"  {Pad("ownership", 15)} {Monitoring.HandoffsRecorded} owner changes of simulated objects, {Monitoring.DragBacksRecorded} undone by the previous owner's packet");
                sb.AppendLine($"  {Pad("misaddressed", 15)} {Monitoring.MisroutedRpcs} creature messages sent to a machine that did not own the target");
                sb.AppendLine($"  {Pad("client batches", 15)} {MonitoringUpload.BatchesAccepted} accepted, {MonitoringUpload.BatchesRejected} rejected, {MonitoringUpload.LinesRejected} lines rejected");
            } else {
                sb.AppendLine("  on - sending this game's records to the server");
                sb.AppendLine($"  {Pad("dropped here", 15)} {MonitoringUpload.LocalRecordsDropped} (over the server's upload budget)");
                sb.AppendLine($"  {Pad("held for link", 15)} {MonitoringUpload.SendsDeferredForLink} sends waited because the connection was already near its send window");
            }
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
            if (!NpsEnv.IsHost() && !LatencyRegistry.HasFreshTable) {
                sb.AppendLine($"  latency table stale ({LatencyRegistry.PublishedTableAgeSeconds:F0}s old) - compensation");
                sb.AppendLine("  suspended until the host publishes again.");
                return;
            }
            if (!NpsEnv.IsHost()) {
                sb.AppendLine($"  table entries       {LatencyRegistry.PublishedEntryCount} (host + measured peers near you)");
            }
            sb.AppendLine($"  entities corrected  {NpsExtrapolate.SamplesThisSecond}");
            sb.AppendLine($"  mean staleness      {NpsExtrapolate.MeanStalenessMs:F0}ms");
            sb.AppendLine($"  mean correction     {NpsExtrapolate.MeanDisplacement:F2}m");
            sb.AppendLine($"  peak correction     {NpsExtrapolate.MaxDisplacement:F2}m");
            sb.AppendLine($"  clamp hits (total)  {NpsExtrapolate.ClampHits}");
        }

        /// <summary>
        /// M15-M19. Counts of operations taken on the fast path, monotonic since startup, with
        /// vanilla's own per-second ZDO counters above them as the denominator - "1.2M deserialise
        /// fast" means nothing without knowing how many ZDOs are arriving, and nothing else on a
        /// server reports those two numbers at all.
        ///
        /// Deliberately counts rather than bytes. Byte figures would be estimates dressed up as
        /// measurements, and the thing the collector responds to is how many objects appear, not
        /// how large they were.
        /// </summary>
        private static void AppendAllocationRelief(StringBuilder sb) {
            sb.AppendLine();
            sb.AppendLine("Allocation relief (totals since start):");

            if (AllocationRelief.TryGetZdoRates(out int sent, out int recv)) {
                sb.AppendLine($"  ZDO throughput   {recv} received/s, {sent} sent/s (the game's own counters)");
            }

            sb.AppendLine($"  {Pad("deserializeFast", 16)} {AllocationRelief.DeserializeFast} " +
                          $"received ZDOs read without the 14 delegates{State(Mechanism.DeserializeAlloc, AllocationRelief.DeserializeWanted, AllocationRelief.DeserializeHookInstalled)}");
            sb.AppendLine($"  {Pad("packetReadFast", 16)} {AllocationRelief.PacketReadFast} " +
                          $"payload reads without the intermediate array{State(Mechanism.PacketReadAlloc, AllocationRelief.PacketReadWanted, AllocationRelief.PacketReadHookInstalled)}");
            sb.AppendLine($"  {Pad("sendPkgReused", 16)} {AllocationRelief.SendPackagesReused} " +
                          $"send ticks using the reused packets{State(Mechanism.SendPacketReuse, AllocationRelief.SendPackageReuseWanted, true)}");
            sb.AppendLine($"  {Pad("rpcFastPath", 16)} {AllocationRelief.RpcFastPath} " +
                          $"inbound RPCs dispatched without reflection{State(Mechanism.RpcInvokeFastPath, AllocationRelief.RpcInvokeWanted, AllocationRelief.RpcInvokeHookInstalled)}");
            if (NpsEnv.IsHost()) {
                sb.AppendLine($"  {Pad("relaySendReused", 16)} {AllocationRelief.RelaySendsReused} " +
                              $"relay deliveries sent from the reused frame{State(Mechanism.RelaySendReuse, AllocationRelief.RelaySendWanted, true)}");
            }

            sb.AppendLine("  These cut how OFTEN the collector runs, not how much is live at once. On a very large");
            sb.AppendLine("  world the live object count is what the memory ceiling is a function of, and no mod");
            sb.AppendLine("  changes that - expect longer between incidents and less CPU spent collecting, not immunity.");
        }

        /// <summary>
        /// The suffix on each counter line. Three things can be true and they are not the same
        /// thing: the mechanism stood down at patch time, the admin has not asked for it, or the
        /// admin asked for it after startup and the hook that would serve it was never installed.
        /// That last one is the whole reason this says anything at all - the counter would
        /// otherwise sit at zero with the setting reading "true" and no explanation anywhere.
        /// </summary>
        private static string State(Mechanism mechanism, bool wanted, bool installed) {
            string reason = PatchGuard.GetDisableReason(mechanism);
            if (reason != null) { return $"  [stood down: {reason}]"; }
            if (!wanted) { return "  [off]"; }
            if (!installed) { return "  [on in config, but not installed this session - restart to apply]"; }
            return "";
        }

        internal static string BuildOverlay() {
            if (!NpsEnv.IsHost() && !LatencyRegistry.HasPublishedTable) {
                return "NPS: no latency table (host not running this mod) - rendering is vanilla";
            }
            if (!NpsEnv.IsHost() && !LatencyRegistry.HasFreshTable) {
                return $"NPS: latency table stale ({LatencyRegistry.PublishedTableAgeSeconds:F0}s old) - compensation suspended";
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

        internal static void ForgetPeer(long uid) {
            Stats.Remove(uid);
        }

        internal static void Reset() {
            Stats.Clear();
            Collecting = false;
        }
    }
}
