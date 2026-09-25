using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>Why a tracked ZDO - a creature, or a Prioritized one - changed owner, as far as
    /// the host can tell.</summary>
    internal enum HandoffCause {
        Gameplay,        // game code on this machine called SetOwner - a saddle, a cart, a claim
        VanillaPass,     // vanilla's own ReleaseNearbyZDOS, running because arbitration is off
        Release,         // arbiter: nobody covers the zone any more
        Rescue,          // arbiter: no owner, or an owner who has left
        Optimise,        // arbiter: a better-placed candidate than a healthy, present owner
        Proximity,       // arbiter: pulled to the one player near it, from an owner who has moved away
        Helm,            // M20: a ship handed to the player who took its helm
        Remote,          // arrived in a packet - some other machine changed the owner
        DragBack,        // arrived in a packet and undid a change this host had just made
    }

    /// <summary>
    /// Network monitoring: an off-by-default recorder a server owner switches on when something
    /// is wrong, plays through the problem, and sends the resulting folder.
    ///
    /// It exists because the questions that matter about ownership cannot be answered from
    /// counters. Whether a handoff held, how long nobody was simulating the creature, whose hits
    /// went to a machine that no longer owned it, whether the new owner's AI picked its target
    /// back up - each of those is a join across the host and two or three clients, around one
    /// event, in time. So the module records events rather than totals, on every machine that
    /// has the mod, and the clients send theirs to the host, which writes one set of files.
    ///
    /// Off means off. The hooks live in MonitoringPatches and are applied through a second
    /// Harmony instance only while monitoring is running, then removed again; nothing here is
    /// picked up by the plugin's PatchAll. The taps inside the mod's own code are a read of
    /// <see cref="Active"/>. A server that never enables this pays for that field and nothing else.
    ///
    /// What is recorded: session ids (random per session), ZDO ids, prefab names, positions to
    /// the metre, timings and connection figures. What is not: player names, platform ids,
    /// addresses, chat.
    /// </summary>
    internal static class Monitoring {

        internal const string FolderName = "NpsMonitoring";

        /// <summary>A field, not a property: it is read on the arbiter's per-ZDO paths.</summary>
        internal static bool Active;

        /// <summary>This machine is the host, so it writes the files and records the host's view.</summary>
        internal static bool ServerRole;

        /// <summary>Somebody is looking at the world through this machine, so how far a creature
        /// jumps on screen means something here. False only on a dedicated server.
        ///
        /// This gates the on-screen record and nothing else. Every machine that can own a
        /// creature records what it sees as a simulator - hits it received, what a creature was
        /// doing when it lost it - and that includes a dedicated server, which owns whatever is
        /// contested around the world origin. Hits on a creature the host owns are addressed to
        /// the host and handled there, so they never pass through the relay the host watches;
        /// without the simulator's own record they would not be recorded at all.</summary>
        internal static bool ViewerRole;

        // Set and cleared by MonitoringPatches around the vanilla calls they bracket. Tick clears
        // them again every frame, so a throw inside the bracketed call cannot leave one stuck.
        internal static bool InSetOwner;
        internal static bool InZdoData;
        internal static long PacketPeerUid;

        /// <summary>The line under construction. One instance is enough because records are built
        /// on the main thread and never nested - callers finish a line before calling anything
        /// that might start another.</summary>
        internal static readonly JsonLine Line = new JsonLine();

        /// <summary>Started once and never reset, so every timer in the module survives
        /// monitoring being switched off and on again within one process. Record times are
        /// therefore ms since the process started; the session record ties that to UTC.</summary>
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly Dictionary<int, string> PrefabNames = new Dictionary<int, string>();
        private static readonly HashSet<int> WatchedRpcs = new HashSet<int>();
        private static readonly Dictionary<int, string> WatchedRpcNames = new Dictionary<int, string>();
        private static readonly int PlayerPrefabHash = "Player".GetStableHashCode();

        private static MonitoringWriter _writer;
        private static bool _startFailed;
        private static bool _reportedStop;
        private static long _nextHandoffId;
        private static double _lastPeerSampleMs;

        private static HandoffCause _pendingCause;
        private static bool _hasPendingCause;
        private static float _pendingImprovementMs;
        private static string _pendingCandidates;
        private static HandoffCause _ambientCause = HandoffCause.Gameplay;

        // For nps_stats.
        internal static long HandoffsRecorded;
        internal static long DragBacksRecorded;
        internal static long MisroutedRpcs;

        internal static double NowMs => Clock.Elapsed.TotalMilliseconds;
        internal static MonitoringWriter Writer => _writer;

        static Monitoring() {
            WatchRpc("RPC_Damage");
            WatchRpc("RPC_Stagger");
            WatchRpc("Alert");
            WatchRpc("OnNearProjectileHit");
        }

        private static void WatchRpc(string name) {
            int hash = name.GetStableHashCode();
            WatchedRpcs.Add(hash);
            WatchedRpcNames[hash] = name;
        }

        internal static bool IsWatchedRpc(int methodHash) => WatchedRpcs.Contains(methodHash);

        /// <summary>
        /// The objects this module follows: creatures, and Prioritized ZDOs (ships, carts,
        /// players). The type flag alone was the test in 1.7.0, and it silently excluded every
        /// creature - the game leaves them Default - so the first recording from a real server
        /// held two hours of ships and not one creature. Flag first: it answers the ships and
        /// players for free, and only a Default ZDO pays for the prefab lookup.
        /// </summary>
        internal static bool IsTracked(ZDO zdo) {
            return zdo.Type == ZDO.ObjectType.Prioritized || OwnershipPolicy.IsCreature(zdo);
        }

        // -- lifecycle ---------------------------------------------------------------------

        /// <summary>
        /// Once per frame, from the ZNet.Update postfix that drives the rest of the mod's periodic
        /// work. Deciding whether to run is done here rather than from config events because the
        /// answer depends on more than config - on a client it depends on what the host has asked
        /// for - and a comparison per frame is cheaper than getting that wiring wrong.
        /// </summary>
        internal static void Tick() {
            InSetOwner = false;
            InZdoData = false;
            _ambientCause = HandoffCause.Gameplay;

            bool host = NpsEnv.IsHost();
            bool want = host
                ? ValConfig.EnableMonitoring.Value
                : MonitoringUpload.HostWantsData && ValConfig.AllowMonitoringUpload.Value;

            if (!want) { _startFailed = false; }
            if (want != Active && !(want && _startFailed)) {
                if (want) { Start(host); } else { Stop(); }
            }

            if (host) { MonitoringUpload.TickHost(); }
            if (!Active) { return; }

            double now = NowMs;

            if (ServerRole) {
                HandoffWatch.Tick(now);
                if (now - _lastPeerSampleMs >= PeerSampleIntervalMs()) {
                    _lastPeerSampleMs = now;
                    SamplePeers(now);
                }
                ReportWriterState();
            }

            MonitoringClient.Tick(now);
            MonitoringUpload.TickClient(now);
        }

        private static void Start(bool host) {
            ServerRole = host;
            ViewerRole = !NpsEnv.IsDedicated();

            if (host && !OpenWriter()) {
                _startFailed = true;
                return;
            }

            _nextHandoffId = 0L;
            _lastPeerSampleMs = 0d;
            _reportedStop = false;
            HandoffsRecorded = 0L;
            DragBacksRecorded = 0L;
            MisroutedRpcs = 0L;

            Patches.MonitoringPatches.Apply(ServerRole, ViewerRole);
            Active = true;

            if (ServerRole) {
                EmitSession();
                Logger.LogInfo($"Network monitoring is on. Recording to {_writer.Directory}");
            }
            MonitoringClient.OnStarted();
        }

        /// <summary>Also the session-end path: ZNet.StopAll and the plugin's OnDestroy call this.</summary>
        internal static void Stop() {
            if (!Active) { return; }

            Active = false;
            Patches.MonitoringPatches.Remove();

            // Before the closing record, so a listen host's last client batch lands inside the
            // session it belongs to.
            MonitoringUpload.FlushOnStop();
            if (ServerRole) {
                EmitServer(Line.Begin("session_end").Num("t", NowMs, "0.#").End());
            }

            if (_writer != null) {
                _writer.Stop();
                Logger.LogInfo($"Network monitoring is off. {_writer.RecordsWritten} records written to {_writer.Directory}");
                _writer = null;
            }

            HandoffWatch.Reset();
            MonitoringClient.Reset();
            _hasPendingCause = false;
            _pendingCandidates = null;
        }

        /// <summary>The session is over. Stops, and also forgets what the host asked for - the
        /// next server may not be running this mod at all.</summary>
        internal static void Shutdown() {
            Stop();
            MonitoringUpload.Reset();
            _startFailed = false;
        }

        private static bool OpenWriter() {
            try {
                string root = Path.Combine(BepInEx.Paths.BepInExRootPath, FolderName);
                long cap = (long)ValConfig.MonitoringMaxDiskMB.Value * 1024L * 1024L;
                long stored = MonitoringWriter.MeasureDirectory(root);
                if (stored >= cap) {
                    Logger.LogWarning($"Network monitoring did not start: {root} already holds {stored / (1024 * 1024)}MB, which is the Max Disk MB limit. Move or remove old sessions, or raise the limit, then switch monitoring off and on again.");
                    return false;
                }

                string session = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
                _writer = new MonitoringWriter(session, cap, stored);
                _writer.Start();
                return true;
            } catch (Exception e) {
                Logger.LogWarning($"Network monitoring did not start: {e.GetType().Name}: {e.Message}");
                _writer = null;
                return false;
            }
        }

        /// <summary>The writer cannot log from its own thread, so its two terminal states are
        /// reported from here, once.</summary>
        private static void ReportWriterState() {
            if (_reportedStop || _writer == null) { return; }

            if (_writer.Fault != null) {
                _reportedStop = true;
                Logger.LogWarning($"Network monitoring stopped recording: {_writer.Fault}");
            } else if (_writer.DiskFull) {
                _reportedStop = true;
                Logger.LogWarning($"Network monitoring stopped recording: {FolderName} has reached the Max Disk MB limit. Nothing already recorded was removed.");
            }
        }

        // -- output ------------------------------------------------------------------------

        internal static void EmitServer(string line) {
            _writer?.Enqueue(line);
        }

        internal static void EmitClient(string line) {
            MonitoringUpload.Buffer(line);
        }

        internal static string ZdoId(ZDOID uid) {
            return uid.UserID.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                   uid.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static string PrefabName(ZDO zdo) {
            int hash = zdo.GetPrefab();
            if (PrefabNames.TryGetValue(hash, out string name)) { return name; }

            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
            name = prefab != null ? prefab.name : hash.ToString(System.Globalization.CultureInfo.InvariantCulture);
            PrefabNames[hash] = name;
            return name;
        }

        // -- session and config ------------------------------------------------------------

        private static void EmitSession() {
            List<string> live = new List<string>();
            foreach (Mechanism mechanism in Enum.GetValues(typeof(Mechanism))) {
                if (PatchGuard.IsActive(mechanism)) { live.Add(mechanism.ToString()); }
            }

            // The host's own distance caps every peer's, and stands in for a peer that has not
            // reported one; see AppendSimulationDistance.
            SimulationDistance hostDistance = ZoneCompat.Local();
            EmitServer(Line.Begin("session")
                .Num("t", NowMs, "0.#")
                .Str("utc", DateTime.UtcNow.ToString("o"))
                .Str("mod", NetworkPerformanceSystem.PluginVersion)
                .Str("game", Version.GetVersionString())
                .Flag("dedicated", NpsEnv.IsDedicated())
                .Id("host", NpsEnv.LocalSessionId())
                .Int("simNear", hostDistance.NearSimulationDistance)
                .Int("simFar", hostDistance.FarSimulationDistance)
                .Flag("simClassic", hostDistance.IsClassic)
                .Str("mechanisms", string.Join(",", live.ToArray()))
                .Raw("config", DescribeConfig())
                .End());
        }

        /// <summary>Every server-side setting, because the point of a report is that whoever reads
        /// it does not have to ask what the server was set to.</summary>
        private static string DescribeConfig() {
            StringBuilder sb = new StringBuilder(2048);
            sb.Append('{');
            bool first = true;
            foreach (ConfigDefinition definition in ValConfig.cfg.Keys) {
                if (definition.Section == "Client config") { continue; }

                if (!first) { sb.Append(','); }
                first = false;
                JsonLine.AppendString(sb, definition.Section + "/" + definition.Key);
                sb.Append(':');
                JsonLine.AppendString(sb, ValConfig.cfg[definition].GetSerializedValue());
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>A setting changed mid-session. This is what labels the two halves of an
        /// on/off comparison without anyone having to write down when they flipped the switch.</summary>
        internal static void OnSettingChanged(ConfigEntryBase entry) {
            if (!Active || !ServerRole || entry == null) { return; }
            if (entry.Definition.Section == "Client config") { return; }

            EmitServer(Line.Begin("config")
                .Num("t", NowMs, "0.#")
                .Str("key", entry.Definition.Section + "/" + entry.Definition.Key)
                .Str("value", entry.GetSerializedValue())
                .End());
        }

        // -- ownership ---------------------------------------------------------------------

        /// <summary>
        /// The arbiter is about to call SetOwner on this ZDO, and this is why. Consumed by the
        /// SetOwner hook that follows immediately; filtered to tracked objects here so that the
        /// much commoner moves of static objects set nothing that could be left behind.
        /// </summary>
        internal static void NoteCause(ZDO zdo, HandoffCause cause, float improvementMs = 0f, string candidates = null) {
            if (!IsTracked(zdo)) { return; }

            _pendingCause = cause;
            _pendingImprovementMs = improvementMs;
            _pendingCandidates = candidates;
            _hasPendingCause = true;
        }

        /// <summary>Vanilla's own ownership pass is about to run this frame. Anything it moves
        /// would otherwise read as gameplay.</summary>
        internal static void NoteVanillaPass() {
            _ambientCause = HandoffCause.VanillaPass;
        }

        /// <summary>SetOwner ran on this machine and changed the owner of a tracked ZDO.</summary>
        internal static void OnLocalSetOwner(ZDO zdo, long from, long to) {
            HandoffCause cause = _hasPendingCause ? _pendingCause : _ambientCause;
            float improvement = _hasPendingCause ? _pendingImprovementMs : 0f;
            string candidates = _hasPendingCause ? _pendingCandidates : null;
            ClearPendingCause();

            // Releases are followed too. In 1.7.0 they were not, and the old owner's next update
            // putting itself back - 95% of the ship releases in the first recording - was written
            // down as an ordinary Remote change instead of the drag-back it was.
            if (ServerRole) { EmitHandoff(zdo, from, to, cause, improvement, candidates, 0L, watch: true); }
            if (WantsSimulatorRecord(from, to)) { MonitoringClient.OnOwnerFlip(zdo, from, to, viaPacket: false); }
        }

        /// <summary>
        /// A client records every owner change it sees, because when each client's copy caught up
        /// is half of what a handoff cost. The host's own record of the change already says when
        /// the host's copy changed, so the host adds a simulator record only when it is itself
        /// the machine gaining or losing the object - which is the part only it can describe.
        /// </summary>
        private static bool WantsSimulatorRecord(long from, long to) {
            if (!ServerRole) { return true; }
            long self = NpsEnv.LocalSessionId();
            return from == self || to == self;
        }

        internal static void ClearPendingCause() {
            _hasPendingCause = false;
            _pendingCandidates = null;
        }

        /// <summary>
        /// A tracked ZDO is being applied from a packet. Runs from the SetOwnerInternal hook,
        /// so before the packet's position and fields have landed.
        /// </summary>
        internal static void OnPacketZdo(ZDO zdo, long packetOwner) {
            long current = zdo.GetOwner();

            if (ServerRole) {
                bool dragBack = HandoffWatch.NotePacket(zdo, PacketPeerUid, packetOwner, NowMs);
                if (current != packetOwner) {
                    EmitHandoff(zdo, current, packetOwner, dragBack ? HandoffCause.DragBack : HandoffCause.Remote,
                                0f, null, PacketPeerUid, watch: false);
                }
            }

            if (current != packetOwner && WantsSimulatorRecord(current, packetOwner)) {
                MonitoringClient.OnOwnerFlip(zdo, current, packetOwner, viaPacket: true);
            }
        }

        private static void EmitHandoff(ZDO zdo, long from, long to, HandoffCause cause, float improvementMs,
                                        string candidates, long viaPeer, bool watch) {
            double now = NowMs;
            long id = ++_nextHandoffId;
            Vector3 pos = zdo.GetPosition();

            JsonLine line = Line.Begin("handoff")
                .Num("t", now, "0.#")
                .Int("id", id)
                .Str("zdo", ZdoId(zdo.m_uid))
                .Str("prefab", PrefabName(zdo))
                .Id("from", from)
                .Id("to", to)
                .Int("rev", zdo.OwnerRevision)
                .Str("cause", cause.ToString())
                .Num("x", Math.Round(pos.x), "0")
                .Num("z", Math.Round(pos.z), "0")
                .Flag("alert", zdo.GetBool(ZDOVars.s_alert))
                .Flag("tgt", zdo.GetBool(ZDOVars.s_haveTargetHash))
                .Num("speed", zdo.GetVec3(ZDOVars.s_velHash, Vector3.zero).magnitude, "0.#");
            if (viaPeer != 0L) { line.Id("via", viaPeer); }
            // Both kinds of arbiter move carry who was weighed, and their distances - which the
            // cost function ignores and the proximity layer decides on, so one listing shows why
            // either of them did what it did.
            if (cause == HandoffCause.Optimise || cause == HandoffCause.Proximity) {
                line.Num("gainMs", improvementMs, "0.#");
                line.Raw("cands", candidates);
            }
            EmitServer(line.End());

            HandoffsRecorded++;
            if (cause == HandoffCause.DragBack) { DragBacksRecorded++; }

            // After the line above is finished: opening a watch can close out an earlier one,
            // and that writes a record of its own through the same builder.
            if (watch) { HandoffWatch.Open(id, zdo, from, to, now); }
        }

        /// <summary>The arbiter finished a pass. Its counters are the denominators for everything
        /// else: how much of the nearby world was contested at all.</summary>
        internal static void OnPassCompleted(int zones, int solePrioritized, int contestedPrioritized) {
            if (!ServerRole) { return; }

            EmitServer(Line.Begin("pass")
                .Num("t", NowMs, "0.#")
                .Int("candidates", OwnershipArbiter.LastPassCandidates)
                .Int("zones", zones)
                .Int("considered", OwnershipArbiter.LastPassConsidered)
                .Int("unowned", OwnershipArbiter.LastPassUnownedOnEntry)
                .Int("rescued", OwnershipArbiter.LastPassRescued)
                .Int("released", OwnershipArbiter.LastPassReleased)
                .Int("optimised", OwnershipArbiter.LastPassOptimised)
                .Int("deferred", OwnershipArbiter.LastPassDeferred)
                .Int("cap", OwnershipArbiter.LastPassCap)
                .Int("ghosts", OwnershipArbiter.LastPassGhostsExcluded)
                .Int("proxKept", OwnershipArbiter.LastPassProximityKept)
                .Int("proxPulled", OwnershipArbiter.LastPassProximityPulled)
                .Int("proxRescued", OwnershipArbiter.LastPassProximityRescued)
                .Int("soleCreatures", solePrioritized)
                .Int("contestedCreatures", contestedPrioritized)
                .Num("ms", OwnershipArbiter.LastPassMs, "0.##")
                .End());
        }

        // -- routed RPCs -------------------------------------------------------------------

        /// <summary>
        /// The host is relaying (or originating) an owner-addressed RPC. The sender addressed it
        /// using its own copy of the ZDO; if that copy names a machine the host no longer
        /// considers the owner, the receiver will drop it at its IsOwner check, silently. This is
        /// the host's count of those, and it needs nothing from the clients.
        /// </summary>
        internal static void OnRoutedRpc(ZRoutedRpc.RoutedRPCData data) {
            OnRoutedRpc(data, data.m_targetPeerID, routed: false);
        }

        /// <summary>The same record, for a message the owner router has already delivered: the
        /// peer the sender addressed it to is passed in, because the message now names the one it
        /// went to, and "routed" says the router corrected it - so a miss here is a hit that
        /// LANDED, which is the opposite of what a miss meant before the router took creature
        /// hits.</summary>
        internal static void OnRoutedRpc(ZRoutedRpc.RoutedRPCData data, long addressedTo, bool routed) {
            if (!WatchedRpcs.Contains(data.m_methodHash)) { return; }

            ZDO zdo = ZDOMan.instance.GetZDO(data.m_targetZDO);
            if (zdo != null && !IsTracked(zdo) && zdo.GetPrefab() != PlayerPrefabHash) {
                return;                                                       // a tree, a rock, a wall
            }

            long owner = zdo != null ? zdo.GetOwner() : 0L;
            bool broadcast = addressedTo == 0L;
            bool misrouted = zdo != null && !broadcast && addressedTo != owner;
            if (!routed && (misrouted || (broadcast && zdo != null))) { MisroutedRpcs++; }

            JsonLine line = Line.Begin("rpc")
                .Num("t", NowMs, "0.#")
                .Str("m", WatchedRpcNames[data.m_methodHash])
                .Str("zdo", ZdoId(data.m_targetZDO))
                .Id("sender", data.m_senderPeerID)
                .Id("to", addressedTo)
                .Id("owner", owner)
                .Flag("miss", misrouted)
                .Flag("bcast", broadcast)
                .Flag("gone", zdo == null);
            if (routed) { line.Flag("routed", true); }
            if (zdo != null) { line.Str("prefab", PrefabName(zdo)); }
            EmitServer(line.End());
        }

        /// <summary>
        /// OwnerRevisionGuard kept the host's owner against a packet that would have put an older
        /// one back - a drag-back that did not happen. Tracked objects only, like everything here.
        /// The watched handoff, if there is one, counts it as blocked rather than dragged back.
        /// "refused" says the packet's data was dropped too (creatures), rather than applied under
        /// the host's owner; either way the object was force-sent back to the sender.
        /// </summary>
        internal static void OnStaleOwnerRejected(ZDO zdo, long staleOwner, ushort staleRevision, bool dataRefused) {
            if (!ServerRole || !IsTracked(zdo)) { return; }

            HandoffWatch.NoteBlocked(zdo.m_uid);
            Vector3 pos = zdo.GetPosition();
            EmitServer(Line.Begin("stale_owner")
                .Num("t", NowMs, "0.#")
                .Str("zdo", ZdoId(zdo.m_uid))
                .Str("prefab", PrefabName(zdo))
                .Id("owner", zdo.GetOwner())
                .Int("rev", zdo.OwnerRevision)
                .Id("stale", staleOwner)
                .Int("staleRev", staleRevision)
                .Id("via", PacketPeerUid)
                .Num("x", Math.Round(pos.x), "0")
                .Num("z", Math.Round(pos.z), "0")
                .Flag("refused", dataRefused)
                .End());
        }

        // -- peers -------------------------------------------------------------------------

        /// <summary>Peer records per second stay at about this many however full the server
        /// is: one a second per peer up to twelve peers, then the interval stretches - every
        /// five seconds at sixty. Nothing is lost by that. The RTT in the record is the
        /// registry's smoothed value, whose time constant is three to four seconds, so sampling
        /// it every second was oversampling it; the reason it was ever per second was small
        /// servers, where it costs nothing and reads well.</summary>
        private const int PeerRecordsPerSecond = 12;

        private static double PeerSampleIntervalMs() {
            int peers = ZNet.instance != null ? ZNet.instance.GetPeers().Count : 0;
            int seconds = (peers + PeerRecordsPerSecond - 1) / PeerRecordsPerSecond;
            return 1000d * Math.Max(1, seconds);
        }

        private static void SamplePeers(double now) {
            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (!peer.IsReady()) { continue; }

                Vector3 refPos = peer.GetRefPos();
                Vector2s zone = ZoneSystem.GetZone(refPos);

                JsonLine line = Line.Begin("peer")
                    .Num("t", now, "0.#")
                    .Id("uid", peer.m_uid)
                    .Num("x", Math.Round(refPos.x), "0")
                    .Num("z", Math.Round(refPos.z), "0")
                    .Int("zx", zone.x)
                    .Int("zy", zone.y)
                    .Int("owned", OwnershipArbiter.OwnedCountFor(peer.m_uid))
                    .Flag("ghost", PeerLiveness.IsGhost(peer.m_uid));
                AppendSimulationDistance(line, peer.m_simulationDistance);

                if (LatencyRegistry.TryGetState(peer.m_uid, out LatencyRegistry.PeerLatency latency)) {
                    line.Int("rttLast", latency.LastMs)
                        .Num("rtt", latency.EwmaMs, "0.#")
                        .Num("jitter", latency.JitterMs, "0.#");
                }

                if (RttProbe.TryGetLinkStatus(peer.m_socket, out RttProbe.LinkStatus link)) {
                    line.Int("pending", link.PendingBytes)
                        .Int("inFlight", link.InFlightBytes)
                        .Int("sendRate", link.SendRateBytesPerSec)
                        .Num("qLocal", link.QualityLocal, "0.###")
                        .Num("qRemote", link.QualityRemote, "0.###");
                }

                EmitServer(line.End());
            }
        }

        /// <summary>
        /// The simulation distance the game negotiated for this peer, exactly as stored - how far
        /// it loads, and so which creatures it can be writing. It decides the ring the host sends
        /// it (CreateSyncList) and the ring the arbiter believes it holds (OwnerStillLoads), and
        /// the first 1.8.0 recording could not tell whether the two ever disagree without it.
        /// A peer that has not finished the handshake reads (0, 0), which ZoneCompat.For replaces
        /// with the host's own value; recorded raw so that case is visible.
        /// </summary>
        private static void AppendSimulationDistance(JsonLine line, SimulationDistance distance) {
            line.Int("simNear", distance.NearSimulationDistance)
                .Int("simFar", distance.FarSimulationDistance)
                .Flag("simClassic", distance.IsClassic);
        }

        /// <summary>
        /// Loss backoff (M26) changed one player's send rate: "down", "up", "clear" (back at the
        /// global rate after a clean run), or "exempt" (back at the global rate because stepping
        /// down did not improve their delivery; never stepped again this session). "delivered" is
        /// the smoothed share of packets reaching them now, "atStart" what it was at their first
        /// step down, and "rate" the rate they are on after the change.
        /// </summary>
        internal static void OnLossBackoff(long uid, string action, int steps, int rateBytesPerSec, float delivered, float deliveredAtStart) {
            if (!ServerRole) { return; }

            EmitServer(Line.Begin("loss_backoff")
                .Num("t", NowMs, "0.#")
                .Id("uid", uid)
                .Str("action", action)
                .Int("steps", steps)
                .Int("rate", rateBytesPerSec)
                .Num("delivered", delivered, "0.###")
                .Num("atStart", deliveredAtStart, "0.###")
                .End());
        }

        internal static void ForgetPeer(long uid) {
            HandoffWatch.ForgetPeer(uid);
            MonitoringUpload.ForgetPeer(uid);
        }
    }
}
