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

            internal float MeanPendingBytes => StatusSamples > 0 ? (float)PendingByteSum / StatusSamples : 0f;
            internal float MeanInFlightBytes => StatusSamples > 0 ? (float)InFlightByteSum / StatusSamples : 0f;
            internal float PendingSampleShare => StatusSamples > 0 ? (float)SamplesWithPending / StatusSamples : 0f;
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

            // M8. One more transport read per peer per send tick, on the same opt-in budget as
            // GetSendQueueSize above. A socket that will not answer simply contributes no sample,
            // which is why StatusSamples is counted separately from SendAttempts.
            if (RttProbe.TryGetLinkStatus(netPeer.m_socket, out RttProbe.LinkStatus status)) {
                entry.StatusSamples++;
                entry.PendingByteSum += status.PendingBytes;
                entry.InFlightByteSum += status.InFlightBytes;
                entry.LastSendRateBytesPerSec = status.SendRateBytesPerSec;
                if (status.PendingBytes > entry.PendingBytesPeak) { entry.PendingBytesPeak = status.PendingBytes; }
                if (status.InFlightBytes > entry.InFlightBytesPeak) { entry.InFlightBytesPeak = status.InFlightBytes; }
                if (status.PendingBytes > 0) { entry.SamplesWithPending++; }
            }
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
            AppendTransport(sb);
            AppendTimeouts(sb);
            AppendPeerLiveness(sb);
            AppendThirdPartyThresholds(sb);
            AppendScheduler(sb);
            AppendSyncListCache(sb);
            AppendRoutedRpc(sb);
            AppendStationRpc(sb);
            AppendOwnership(sb);
            AppendShipHelm(sb);
            AppendExtrapolation(sb);
            AppendAllocationRelief(sb);

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
                case Mechanism.RefPos: return ValConfig.EnableFastRefPos.Value;
                case Mechanism.Extrapolation: return ValConfig.EnableLatencyCompensation.Value;
                case Mechanism.RoutedRpcFilter: return ValConfig.EnableRoutedRpcFilter.Value;
                case Mechanism.SteamTransport: return ValConfig.EnableSteamTransportTuning.Value;
                case Mechanism.SyncListCache: return ValConfig.EnableSyncListCache.Value;
                case Mechanism.ConnectionTimeout: return ValConfig.EnableConnectionTimeoutTuning.Value;
                case Mechanism.StationRpcRouting: return ValConfig.EnableStationRpcRouting.Value;
                case Mechanism.JotunnQueueLimit: return ValConfig.EnableSendWindowSizing.Value;
                case Mechanism.QueueDrain: return ValConfig.EnableSendWindowSizing.Value && ValConfig.QueueDrainIntervalSeconds.Value > 0f;
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

        /// <summary>
        /// M8 - the question the peer table above cannot answer.
        ///
        /// Its "queue" column is GetSendQueueSize, which sums bytes on the wire with bytes Steam
        /// is holding back. Those mean opposite things: in-flight is the bandwidth-delay product
        /// being filled, which is what the M2 window is sized to do, while pending is standing
        /// queue that adds delay to everything behind it. A peer whose link is working perfectly
        /// and one being overdriven into bufferbloat produce the same summed number.
        ///
        /// The window is currently open-loop - RTT times a configured target rate - so this table
        /// is how you find out whether that target is set above what a link will actually carry.
        /// Read it before changing Target Rate KBps in either direction.
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

            sb.AppendLine("  name                 in-flight  pending   pending%  steam est   fill   verdict");
            for (int i = 0; i < peers.Count; i++) {
                long uid = peers[i].m_uid;
                if (!Stats.TryGetValue(uid, out PeerStats entry) || entry.StatusSamples == 0) { continue; }
                anySamples = true;

                string name = string.IsNullOrEmpty(peers[i].m_playerName) ? "(connecting)" : peers[i].m_playerName;
                int window = entry.LastWindowBytes > 0 ? entry.LastWindowBytes : SendWindow.VanillaWindowBytes;
                float fill = entry.MeanInFlightBytes / window;
                float pendingShare = entry.PendingSampleShare;

                sb.AppendLine(
                    $"  {Pad(name, 20)} " +
                    $"{Pad($"{entry.MeanInFlightBytes / 1024f:F1}KB", 10)} " +
                    $"{Pad($"{entry.MeanPendingBytes / 1024f:F1}KB", 9)} " +
                    $"{Pad($"{pendingShare * 100f:F0}%", 9)} " +
                    $"{Pad($"{entry.LastSendRateBytesPerSec / 1024f:F0}KB/s", 11)} " +
                    $"{Pad($"{fill * 100f:F0}%", 6)} " +
                    Verdict(pendingShare, fill));
            }

            if (!anySamples) {
                sb.AppendLine("  (no transport samples - Steam sockets only; crossplay peers report nothing here)");
                return;
            }

            sb.AppendLine("  in-flight = on the wire, unacked (the window working). pending = Steam holding it back (congestion).");
            sb.AppendLine("  fill = in-flight as a share of the sized window. steam est = Steam's own rate estimate for the link.");
        }

        /// <summary>
        /// Two independent signals, and the pair is what identifies the state:
        ///   pending high              -> the link cannot take what it is being given, whatever
        ///                                the window says. Target Rate KBps is too high for it.
        ///   pending low, fill high    -> the window is the binding constraint and is doing its
        ///                                job. Raising the target would buy more throughput.
        ///   pending low, fill low     -> neither the window nor the link is the limit; there is
        ///                                simply not that much to send, or the limit is upstream.
        /// </summary>
        private static string Verdict(float pendingShare, float fill) {
            if (pendingShare > 0.25f) { return "CONGESTED - target rate above what this link carries"; }
            if (pendingShare > 0.05f) { return "some queueing"; }
            if (fill > 0.8f) { return "window-bound (working)"; }
            if (fill > 0.25f) { return "healthy"; }
            return "idle - nothing to send";
        }

        /// <summary>The transport ceiling underneath everything else, and whether we moved it.</summary>
        private static void AppendTransport(StringBuilder sb) {
            sb.AppendLine();
            sb.AppendLine("Steam transport:");
            if (SteamTransport.LastReadback == null) {
                sb.AppendLine("  not applied (disabled, or no Steam networking interface in this process)");
                return;
            }
            sb.AppendLine($"  {SteamTransport.LastReadback}");

            int capKBps = SteamTransport.EffectiveSendRateMaxBytesPerSec / 1024;
            int targetKBps = ValConfig.SendWindowTargetRateKBps.Value;
            sb.AppendLine($"  window target    {targetKBps} KB/s against a transport ceiling of {capKBps} KB/s");
            if (targetKBps > capKBps) {
                sb.AppendLine("  WARNING: sizing windows for throughput the transport will not pass. The surplus becomes");
                sb.AppendLine("  queueing delay. Raise 'Steam Transport / Send Rate Max KBps' or lower the target.");
            }
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
        /// M13/M14 - the two answers to mods that wait on a fixed send queue size. Jotunn's limit
        /// is printed as read back from its field, so a Jotunn update that renamed it shows here
        /// as "not applied" rather than as a mystery 30-second disconnect. The drain rows are the
        /// cost side: how often a backlogged peer was briefly held, and for how long.
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

            if (!QueueDrain.Enabled) {
                string drainReason = PatchGuard.GetDisableReason(Mechanism.QueueDrain);
                bool sizing = PatchGuard.IsActive(Mechanism.SendWindow) && ValConfig.EnableSendWindowSizing.Value;
                if (drainReason != null) {
                    sb.AppendLine($"  queue drain        stood down: {drainReason}");
                } else {
                    sb.AppendLine(sizing
                        ? "  queue drain        off (Queue Drain Interval Seconds is 0)"
                        : "  queue drain        off (vanilla window - nothing to drain)");
                }
                return;
            }

            sb.AppendLine($"  queue drain        every {ValConfig.QueueDrainIntervalSeconds.Value:F0}s to {QueueDrain.FloorBytes / 1024f:F1}KB, for mods with the threshold compiled in (ServerSync, ConditionalConfigSync)");

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            bool any = false;
            for (int i = 0; i < peers.Count; i++) {
                long uid = peers[i].m_uid;
                if (!QueueDrain.TryGetState(uid, out QueueDrain.State state)) { continue; }
                if (!any) {
                    sb.AppendLine("  name                 drains  held ticks  aborted  last drain");
                    any = true;
                }
                string name = string.IsNullOrEmpty(peers[i].m_playerName) ? "(connecting)" : peers[i].m_playerName;
                string last = state.Drains > 0 && state.LastDrainSeconds > 0f ? $"{state.LastDrainSeconds:F2}s" : "-";
                string now = state.Draining ? " (draining)" : "";
                sb.AppendLine($"  {Pad(name, 20)} {Pad(state.Drains.ToString(), 7)} {Pad(state.HeldTicks.ToString(), 11)} {Pad(state.Aborted.ToString(), 8)} {last}{now}");
            }
            if (!any) {
                sb.AppendLine("  (no peer has held a window above vanilla yet - nothing to drain)");
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
        private static void AppendStationRpc(StringBuilder sb) {
            if (!NpsEnv.IsHost()) { return; }

            sb.AppendLine();
            sb.AppendLine("Station requests (since start):");
            if (!PatchGuard.IsActive(Mechanism.StationRpcRouting) || !ValConfig.EnableStationRpcRouting.Value) {
                sb.AppendLine("  vanilla (delivered to whoever the sender's copy names as owner)");
                return;
            }

            sb.AppendLine($"  {Pad("seen", 13)} {StationRpcRouter.Seen} owner-addressed requests (add item/ore/fuel/ammo, tap, empty)");
            sb.AppendLine($"  {Pad("re-targeted", 13)} {StationRpcRouter.Retargeted} (sender named a stale owner - delivered to the current one)");
            sb.AppendLine($"  {Pad("claimed", 13)} {StationRpcRouter.Claimed} (no present owner - handed to the requesting player first)");
            sb.AppendLine($"  {Pad("held", 13)} {StationRpcRouter.HeldCount} (waited for the owner to be sent its ownership; longest {StationRpcRouter.MaxHoldMs:F0}ms)");
            sb.AppendLine($"  {Pad("expired", 13)} {StationRpcRouter.Expired} (owner not synced within {StationRpcRouter.HoldTimeoutSeconds:F0}s - forwarded regardless)");
            sb.AppendLine($"  {Pad("dropped", 13)} {StationRpcRouter.Dropped} (object or player gone while waiting)");
            if (StationRpcRouter.Waiting > 0) {
                sb.AppendLine($"  {Pad("waiting", 13)} {StationRpcRouter.Waiting}");
            }
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
            sb.AppendLine($"  optimised   {OwnershipArbiter.LastPassOptimised} (moving objects, given to a lower-latency owner)");
            sb.AppendLine($"  deferred    {OwnershipArbiter.LastPassDeferred} (moving objects only, hit the per-pass cap of {OwnershipArbiter.LastPassCap})");

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
            sb.AppendLine($"  total since start  rescued {OwnershipArbiter.TotalRescued}, optimised {OwnershipArbiter.TotalOptimised}, interactive {OwnershipArbiter.TotalInteractiveOptimised}");

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
