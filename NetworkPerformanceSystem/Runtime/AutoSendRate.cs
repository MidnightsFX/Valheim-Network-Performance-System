using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M37 - the host finds each player's rate by itself, in both directions, between the game's
    /// 150 KB/s and Auto Send Rate Max KBps.
    ///
    /// The game paces every connection at a fixed 150 KB/s, and Steam has no congestion control to
    /// move it. Two recordings put that in the way: the 2026-10-03 dungeon fight and the
    /// 2026-10-07 Jotun invasion had every player's link at 140-150 KB/s for the whole fight, with
    /// only 35-60% of creature and player updates reaching each player. Send Rate KBps could
    /// always raise it, but only by hand, for everybody, against an upload the admin has to size
    /// - and almost nobody does.
    ///
    /// The server's sends are one global rate, written through SteamTransport like Send Rate KBps
    /// (which, when set, still wins). M26 holds any player whose own connection needs less below
    /// it, and is what makes a global raise safe: it is required, and it remembers the rate a
    /// player lost packets at (see LossBackoff). Each player's own upload is a grant the host
    /// sends them (UploadGrant), decided here from what arrives from them and what their frame
    /// report says is still waiting on their machine.
    ///
    /// The decisions are ThroughputRules', which say why each one is shaped as it is; this class
    /// feeds them, once a second, from what the ping postfix reads off every connection, and
    /// carries out what they decide. Measurements are kept per player (platform address), so a
    /// reconnect keeps its ping baseline.
    ///
    /// Nothing is decided across a hitch, a world save, or an outage at this end, and nothing is
    /// raised while the host's frames run long: a rate the host cannot fill is just a bigger
    /// burst waiting to happen.
    /// </summary>
    internal static class AutoSendRate {

        private const float EvaluateSeconds = 1f;

        /// <summary>A peer not heard from in this long is not a link this second.</summary>
        private const float StaleSampleSeconds = 5f;

        /// <summary>A frame this long marks the evaluations around it unusable.</summary>
        private const float HitchSeconds = 0.5f;
        private const float HitchGraceSeconds = 15f;

        /// <summary>Mean frame time, over the last FrameWindowSeconds, the host must be under for
        /// a step up, and over which - for as long again - it gives some back.</summary>
        private const float HealthyFrameMs = 50f;
        private const float OverloadedFrameMs = 66f;
        private const int FrameWindowSeconds = 10;

        /// <summary>A frame report this old no longer says what the player's upload is doing.</summary>
        private const float ReportFreshSeconds = 10f;

        private const float GrantTtlSeconds = 40f;
        private const float GrantRefreshSeconds = 10f;

        /// <summary>A link whose ping stays this long over its best by the rules' margin, while
        /// full at a raised rate, is stepped down by M26 (StepDownForQueue).</summary>
        private const float QueueHoldSeconds = 10f;

        /// <summary>While the server's own line looks congested M26 is paused this long, renewed
        /// every evaluation; after a congestion step, for this long.</summary>
        private const float PauseWhileVotingSeconds = 3f;
        private const float PauseAfterDownSeconds = 30f;

        private const int BytesPerKB = 1024;

        private sealed class Player {
            internal string Key;
            internal long Uid;
            internal uint Handle;
            internal string Name;
            internal float LastSampleAt = float.NegativeInfinity;
            internal readonly LinkTrack Out = new LinkTrack();
            internal readonly LinkTrack In = new LinkTrack();
            internal readonly GrantState Grant = new GrantState();
            internal int SentGrant;
            internal float SentAt = float.NegativeInfinity;
            internal float QueueSince = float.NegativeInfinity;
            internal bool GrantCapable;
        }

        /// <summary>What nps_stats shows for one player.</summary>
        internal struct View {
            internal bool Full;
            internal float Quality;
            internal float PingMs;
            internal float BaselineMs;
            internal bool UploadFull;
            internal float UploadQuality;
            internal int GrantBytesPerSec;
            internal int GrantCeilingBytesPerSec;
            internal bool GrantCapable;
        }

        private static readonly Dictionary<string, Player> Players = new Dictionary<string, Player>();
        private static readonly Dictionary<long, Player> ByUid = new Dictionary<long, Player>();
        private static readonly OutState Out = new OutState();
        private static readonly InState In = new InState();

        private static readonly List<Player> Active = new List<Player>();
        private static readonly List<LinkVote> OutVotes = new List<LinkVote>();
        private static readonly List<LinkVote> InVotes = new List<LinkVote>();
        private static readonly List<GrantState> Grants = new List<GrantState>();
        private static readonly List<GrantChange> Changes = new List<GrantChange>();

        private static readonly float[] FrameSeconds = new float[FrameWindowSeconds];
        private static readonly int[] FrameCounts = new int[FrameWindowSeconds];
        private static int _frameSlot;
        private static float _slotSeconds;
        private static float _evalTimer;
        private static float _lastHitchAt = float.NegativeInfinity;
        private static float _overloadedSince = float.NegativeInfinity;

        /// <summary>Whether grants were being decided at the last evaluation, and the per-player
        /// cap they were decided under: what TryGetUpload needs to say whether a grant may rise.</summary>
        private static bool _uploadsLive;
        private static int _grantCap = ThroughputRules.BaseRate;

        internal static RateDecision LastOut { get; private set; }
        internal static float MeanFrameMs { get; private set; }
        internal static bool LastUsable { get; private set; }
        internal static bool LastHealthy { get; private set; }
        internal static long TotalUps { get; private set; }
        internal static long TotalDowns { get; private set; }
        internal static long TotalGrantChanges { get; private set; }

        /// <summary>The server's sends: host, hooks in, tuning on, this switch on, and no fixed
        /// Send Rate KBps - a fixed rate is the admin's own sizing and wins.</summary>
        internal static bool Wanted =>
            NpsEnv.IsHost()
            && PatchGuard.IsActive(Mechanism.AutoSendRate)
            && PatchGuard.IsActive(Mechanism.SteamTransport)
            && ValConfig.EnableSteamTransportTuning.Value
            && ValConfig.AutoSendRate.Value
            && ValConfig.SteamSendRateKBps.Value <= 0;

        /// <summary>A raised global rate needs M26 under it, holding back any player whose own
        /// connection cannot take it.</summary>
        internal static bool RaiseWanted => Wanted && LossBackoff.Wanted;

        internal static bool UploadsWanted => Wanted && ValConfig.AutoPlayerUploads.Value;

        /// <summary>Links are read whenever this runs, and - reading only, no rate or grant is
        /// changed - whenever the creature allowance looks at players' uploads, which it does
        /// with this off or a fixed Send Rate KBps too: players upload at the game's rate then.</summary>
        internal static bool Measuring =>
            Wanted
            || (NpsEnv.IsHost() && PatchGuard.IsActive(Mechanism.AutoSendRate) && PeerCapacity.BalancingUploads);

        internal static int MaxBytesPerSec => Mathf.Max(ThroughputRules.BaseRate, ValConfig.AutoSendRateMaxKBps.Value * BytesPerKB);

        internal static OutState OutSnapshot => Out;
        internal static InState InSnapshot => In;

        // -- feeding -----------------------------------------------------------------------

        /// <summary>One reading of one connection, from the ping postfix (~2/s per peer).</summary>
        internal static void Observe(ZNetPeer peer, RttProbe.LinkStatus link, int pingMs) {
            if (peer == null || peer.m_uid == 0L || !Measuring) { return; }
            if (!RttProbe.TryGetConnectionHandle(peer.m_socket, out uint handle)) { return; }

            float now = Time.realtimeSinceStartup;
            Player p = PlayerFor(peer, handle);
            p.LastSampleAt = now;

            ThroughputRules.Observe(p.Out, now, link.OutBytesPerSec, link.QualityRemote, link.PendingBytes,
                                    link.SendRateBytesPerSec, pingMs);

            // What waits on the player's machine only they can see; their frame report carries it.
            int pending = -1;
            p.GrantCapable = false;
            if (PeerCapacity.TryGetView(peer.m_uid, out PeerCapacity.View cap) && cap.HasReport
                && cap.AgeSeconds < ReportFreshSeconds && cap.Last.HasUpload) {
                pending = cap.Last.UploadPending;
                p.GrantCapable = true;
            }
            ThroughputRules.Observe(p.In, now, link.InBytesPerSec, link.QualityLocal, pending, p.Grant.Grant, pingMs);
        }

        private static Player PlayerFor(ZNetPeer peer, uint handle) {
            if (ByUid.TryGetValue(peer.m_uid, out Player known) && known.Handle == handle) { return known; }

            string key = PlayerKey(peer);
            if (!Players.TryGetValue(key, out Player p)) {
                p = new Player { Key = key };
                Players[key] = p;
            } else if (p.Uid != peer.m_uid || p.Handle != handle) {
                // The same player on a new connection: its figures start over, its baseline and
                // its grant ceiling stand. The grant itself died with the old connection.
                ThroughputRules.Reconnected(p.Out);
                ThroughputRules.Reconnected(p.In);
                if (p.Uid != 0L) { ByUid.Remove(p.Uid); }
                p.Grant.Grant = ThroughputRules.BaseRate;
                p.Grant.Previous = ThroughputRules.BaseRate;
                p.SentGrant = 0;
                p.SentAt = float.NegativeInfinity;
                p.QueueSince = float.NegativeInfinity;
            }
            p.Uid = peer.m_uid;
            p.Handle = handle;
            p.Name = string.IsNullOrEmpty(peer.m_playerName) ? $"peer {peer.m_uid}" : peer.m_playerName;
            ByUid[peer.m_uid] = p;
            return p;
        }

        // -- the clock ---------------------------------------------------------------------

        /// <summary>Every host frame, from NetworkChannelPatches.Tick.</summary>
        internal static void Tick(float dt) {
            float now = Time.realtimeSinceStartup;
            NoteFrame(dt, now);
            if (!Wanted) {
                _uploadsLive = false;
                if (Out.Rate != ThroughputRules.BaseRate || SteamTransport.AutoRateBytesPerSec != 0) { StandDown("switched off"); }
                return;
            }

            _evalTimer += dt;
            if (_evalTimer < EvaluateSeconds) { return; }
            _evalTimer = 0f;
            Evaluate(now);
        }

        private static void NoteFrame(float dt, float now) {
            if (dt >= HitchSeconds) { _lastHitchAt = now; }
            FrameSeconds[_frameSlot] += dt;
            FrameCounts[_frameSlot]++;
            _slotSeconds += dt;
            if (_slotSeconds < 1f) { return; }
            _slotSeconds = 0f;

            float seconds = 0f;
            int frames = 0;
            for (int i = 0; i < FrameWindowSeconds; i++) {
                seconds += FrameSeconds[i];
                frames += FrameCounts[i];
            }
            MeanFrameMs = frames > 0 ? seconds * 1000f / frames : 0f;
            _frameSlot = (_frameSlot + 1) % FrameWindowSeconds;
            FrameSeconds[_frameSlot] = 0f;
            FrameCounts[_frameSlot] = 0;
        }

        // -- deciding ----------------------------------------------------------------------

        private static void Evaluate(float now) {
            ZNet net = ZNet.instance;
            if (net == null) { return; }

            Active.Clear();
            OutVotes.Clear();
            InVotes.Clear();
            Grants.Clear();
            List<ZNetPeer> peers = net.GetPeers();
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (peer == null || !ByUid.TryGetValue(peer.m_uid, out Player p)) { continue; }
                if (now - p.LastSampleAt > StaleSampleSeconds || PeerLiveness.IsGhost(peer.m_uid)) { continue; }

                bool loading = !peer.IsReady() || Loading(peer.m_uid);
                LossBackoff.TryGetView(peer.m_uid, out LossBackoff.View lb);
                bool backedOff = lb.OverrideBytesPerSec > 0 && !lb.HeldAtBase;

                Active.Add(p);
                OutVotes.Add(new LinkVote {
                    Eligible = !loading && !lb.Exempt && !backedOff && ThroughputRules.Settled(p.Out) && p.Out.Baseline > 0f,
                    Full = ThroughputRules.IsFull(p.Out),
                    Held = backedOff || lb.HeldAtBase,
                    Loss = ThroughputRules.LossVote(p.Out),
                    Rtt = ThroughputRules.RttVote(p.Out),
                    Quality = p.Out.Quality,
                    Bps = p.Out.Bps,
                    Delivered = p.Out.Delivered,
                });
                InVotes.Add(new LinkVote {
                    Eligible = !loading && ThroughputRules.Settled(p.In) && p.In.Baseline > 0f,
                    Full = p.GrantCapable && ThroughputRules.IsFull(p.In),
                    Held = !p.GrantCapable,
                    Loss = ThroughputRules.LossVote(p.In),
                    Rtt = ThroughputRules.RttVote(p.In),
                    Quality = p.In.Quality,
                    Bps = p.In.Bps,
                    Delivered = p.In.Delivered,
                });
                Grants.Add(p.Grant);
            }

            bool usable = now - _lastHitchAt > HitchGraceSeconds
                          && !net.IsSaving()
                          && !PeerLiveness.LocalFaultWithin(LossBackoff.LocalFaultGraceSeconds);
            bool healthy = MeanFrameMs > 0f && MeanFrameMs < HealthyFrameMs;
            if (MeanFrameMs >= OverloadedFrameMs) {
                if (_overloadedSince == float.NegativeInfinity) { _overloadedSince = now; }
            } else {
                _overloadedSince = float.NegativeInfinity;
            }
            bool overloaded = _overloadedSince > float.NegativeInfinity && now - _overloadedSince >= FrameWindowSeconds;
            LastUsable = usable;
            LastHealthy = healthy;

            if (Active.Count == 0) { return; }

            var input = new RateInputs {
                Now = now,
                Usable = usable,
                HostHealthy = healthy && RaiseWanted,
                HostOverloaded = overloaded,
                Max = MaxBytesPerSec,
                AdminLimit = ValConfig.ServerUploadLimitKBps.Value * (float)BytesPerKB,
            };
            DecideOut(input, now);

            _uploadsLive = UploadsWanted;
            if (_uploadsLive) {
                // Grants carry their own per-player back-off (ThroughputRules.StepIn); only the
                // global rate needs M26 under it.
                input.HostHealthy = healthy;
                input.AdminLimit = ValConfig.ServerDownloadLimitKBps.Value * (float)BytesPerKB;
                DecideIn(input, now);
                _grantCap = ThroughputRules.PerPlayerLimit(ThroughputRules.LimitTotal(In.Limits, input.AdminLimit),
                                                           Active.Count, input.Max);
            }
        }

        private static void DecideOut(RateInputs input, float now) {
            RateDecision d = ThroughputRules.StepOut(Out, input, OutVotes);
            LastOut = d;

            // The server's own line looks congested: every player's loss is that, not theirs.
            if (d.Vote.Shared && d.Busy) { LossBackoff.PauseFor(PauseWhileVotingSeconds); }

            if (d.Action != RateAction.None) {
                if (d.Action == RateAction.Down) { LossBackoff.PauseFor(PauseAfterDownSeconds); }
                for (int i = 0; i < Active.Count; i++) {
                    ThroughputRules.NoteRateChange(Active[i].Out, now, d.Action == RateAction.Up);
                }
                if (d.Action == RateAction.Up) { TotalUps++; } else { TotalDowns++; }
                SteamTransport.SetAutoRate(d.Rate, "auto send rate");
                Announce("Auto send rate", d, Out.Limits, OutVotes);
                Record("out", d, Out.Limits, 0L);
            } else {
                for (int i = 0; i < Active.Count; i++) {
                    ThroughputRules.RefreshReference(Active[i].Out, now, d.Vote.Shared);
                }
            }

            // A player whose own path queues at a raised rate - ping well over its best, full,
            // and not the whole line - is M26's to step down: it loses nothing, so M26 never sees it.
            for (int i = 0; i < Active.Count; i++) {
                Player p = Active[i];
                LinkVote v = OutVotes[i];
                bool queueing = v.Full && !v.Held && v.Rtt && !d.Vote.Shared && Out.Rate > ThroughputRules.BaseRate;
                if (!queueing) {
                    p.QueueSince = float.NegativeInfinity;
                    continue;
                }
                if (p.QueueSince == float.NegativeInfinity) { p.QueueSince = now; }
                if (now - p.QueueSince < QueueHoldSeconds) { continue; }
                p.QueueSince = float.NegativeInfinity;
                if (ZNet.instance.GetPeer(p.Uid) is ZNetPeer peer) {
                    LossBackoff.StepDownForQueue(peer, p.Out.Ping, p.Out.Baseline);
                }
            }
        }

        private static void DecideIn(RateInputs input, float now) {
            Changes.Clear();
            ThroughputRules.StepIn(In, input, InVotes, Grants, Changes);

            for (int i = 0; i < Changes.Count; i++) {
                GrantChange c = Changes[i];
                Player p = Active[c.Index];
                ThroughputRules.NoteRateChange(p.In, now, c.To > c.From);
                TotalGrantChanges++;
                Logger.LogInfo($"Auto send rate: {p.Name}'s upload {Kb(c.From)} -> {Kb(c.To)} ({c.Reason}; delivering {Pct(p.In.Quality)}).");
                if (Monitoring.Active) {
                    Monitoring.OnAutoRate("in", c.To > c.From ? "up" : "down", c.From, c.To, Active.Count, 0,
                                          p.In.Delivered, In.Limits.CommittedPeak, In.Limits.Ceiling, c.Reason, p.Uid);
                }
            }
            for (int i = 0; i < Active.Count; i++) {
                if (Changes.Count == 0) { ThroughputRules.RefreshReference(Active[i].In, now, false); }
                SendGrant(Active[i], now);
            }
        }

        /// <summary>Tell a player their grant: at once when it changed, every GrantRefreshSeconds
        /// while it is above the game's rate, and once more when it goes back to it.</summary>
        private static void SendGrant(Player p, float now) {
            int grant = p.Grant.Grant;
            bool raised = grant > ThroughputRules.BaseRate;
            bool changed = grant != p.SentGrant;
            if (!changed && (!raised || now - p.SentAt < GrantRefreshSeconds)) { return; }
            if (!raised && p.SentGrant == 0) { return; }                     // never raised: nothing to say

            ZNetPeer peer = ZNet.instance.GetPeer(p.Uid);
            if (peer?.m_rpc == null) { return; }
            peer.m_rpc.Invoke(UploadGrant.RpcUploadRate, UploadGrant.BuildPackage(grant, GrantTtlSeconds));
            p.SentGrant = raised ? grant : 0;
            p.SentAt = now;
        }

        private static bool Loading(long uid) {
            return PeerCapacity.TryGetView(uid, out PeerCapacity.View v) && v.HasReport
                   && (v.Last.Flags & CreatureLoadRules.FlagLoading) != 0;
        }

        // -- reporting ---------------------------------------------------------------------

        private static void Announce(string what, RateDecision d, RateLimits l, List<LinkVote> votes) {
            int full = 0;
            for (int i = 0; i < votes.Count; i++) { if (votes[i].Full && !votes[i].Held) { full++; } }
            string why;
            switch (d.Action) {
                case RateAction.Up:
                    why = $"{full} of {votes.Count} players full, line clean at {Kb(d.Total)}";
                    break;
                case RateAction.Down:
                    why = $"the server's line is congested: {d.Vote.Voters} of {d.Vote.Eligible} players losing packets or queueing at {Kb(d.Total)}";
                    break;
                case RateAction.Limit:
                    why = $"{votes.Count} players share {Kb(d.LimitTotal)}";
                    break;
                case RateAction.Overload:
                    why = $"the server's frames average {MeanFrameMs:F0}ms";
                    break;
                case RateAction.NoGain:
                    why = "the last step did not raise what full players receive - something else limits them";
                    break;
                default:
                    why = d.Action.ToString();
                    break;
            }
            string line = $"{what}: {Kb(d.From)} -> {Kb(d.Rate)} per player ({why}).";
            if (d.LearnedCeiling) {
                Logger.LogWarning(line + $" The server's upload looks to carry about {Kb(l.Ceiling)}; rates stay under that share for {l.CeilingHold / 60f:F0} minutes.");
            } else {
                Logger.LogInfo(line);
            }
        }

        private static void Record(string dir, RateDecision d, RateLimits l, long uid) {
            if (!Monitoring.Active) { return; }
            int full = 0;
            for (int i = 0; i < OutVotes.Count; i++) { if (OutVotes[i].Full && !OutVotes[i].Held) { full++; } }
            Monitoring.OnAutoRate(dir, d.Action.ToString().ToLowerInvariant(), d.From, d.Rate, OutVotes.Count, full,
                                  d.Total, l.CommittedPeak, l.Ceiling, d.Vote.Shared ? "shared" : "", uid);
        }

        internal static bool TryGetView(long uid, out View view) {
            view = default;
            if (!ByUid.TryGetValue(uid, out Player p)) { return false; }
            view = new View {
                Full = ThroughputRules.IsFull(p.Out),
                Quality = p.Out.Quality,
                PingMs = p.Out.Ping,
                BaselineMs = p.Out.Baseline,
                UploadFull = p.GrantCapable && ThroughputRules.IsFull(p.In),
                UploadQuality = p.In.Quality,
                GrantBytesPerSec = p.Grant.Grant,
                GrantCeilingBytesPerSec = p.Grant.Ceiling,
                GrantCapable = p.GrantCapable,
            };
            return true;
        }

        /// <summary>
        /// This player's upload to the host, for the creature allowance's upload rule
        /// (PeerCapacity): whether it is full, what arrives, the rate they may send at, and
        /// whether a grant could still raise that. Known is false - and false is returned -
        /// without a fresh, settled reading. No clock or engine call, so it runs offline.
        /// </summary>
        internal static bool TryGetUpload(long uid, float now, out UploadReading reading) {
            reading = default;
            if (!ByUid.TryGetValue(uid, out Player p)) { return false; }
            if (now - p.LastSampleAt > StaleSampleSeconds || p.In.Samples < ThroughputRules.MinVoteSamples) { return false; }

            reading = new UploadReading {
                Known = true,
                Full = ThroughputRules.IsFull(p.In),
                Delivered = p.In.Delivered,
                Rate = p.Grant.Grant,
                Quality = p.In.Quality,
                GrantMayRise = _uploadsLive && p.GrantCapable && ThroughputRules.GrantMayRise(p.Grant, p.In, _grantCap),
                GrantChangedAt = p.Grant.LastChangeAt,
            };
            return true;
        }

        // -- lifecycle ---------------------------------------------------------------------

        /// <summary>Switched off, or Send Rate KBps set: the game's rate (or the fixed one) again,
        /// and every grant lapses on its own TTL.</summary>
        private static void StandDown(string why) {
            if (Out.Rate != ThroughputRules.BaseRate) {
                Logger.LogInfo($"Auto send rate: {Kb(Out.Rate)} -> the game's {Kb(ThroughputRules.BaseRate)} ({why}).");
            }
            Out.Rate = ThroughputRules.BaseRate;
            SteamTransport.SetAutoRate(0, why);
            foreach (Player p in Players.Values) {
                p.Grant.Grant = ThroughputRules.BaseRate;
            }
        }

        internal static void ForgetPeer(long uid) {
            ByUid.Remove(uid);                                       // the player entry, and its baseline, stay
        }

        internal static void Reset() {
            Players.Clear();
            ByUid.Clear();
            Active.Clear();
            Out.Rate = ThroughputRules.BaseRate;
            Out.PreviousRate = ThroughputRules.BaseRate;
            Out.LastChangeAt = float.NegativeInfinity;
            Out.LastUpAt = float.NegativeInfinity;
            Out.UpCheckAt = float.NegativeInfinity;
            Out.NoGainUntil = float.NegativeInfinity;
            Out.NoGainHold = ThroughputRules.NoGainHoldSeconds;
            ResetLimits(Out.Limits);
            In.LastUpAt = float.NegativeInfinity;
            ResetLimits(In.Limits);
            LastOut = default;
            _evalTimer = 0f;
            _lastHitchAt = float.NegativeInfinity;
            _overloadedSince = float.NegativeInfinity;
            _uploadsLive = false;
            _grantCap = ThroughputRules.BaseRate;
        }

        private static void ResetLimits(RateLimits l) {
            l.WindowSum = 0f;
            l.WindowCount = 0;
            l.WindowStart = float.NegativeInfinity;
            l.CandidatePeak = 0f;
            l.CandidateAt = float.NegativeInfinity;
            l.CommittedPeak = 0f;
            l.Ceiling = -1f;
            l.CeilingUntil = float.NegativeInfinity;
            l.CeilingHold = ThroughputRules.CeilingHoldSeconds;
            l.Reprobing = false;
            l.ReprobeFrom = -1f;
            l.LastCongestionAt = float.NegativeInfinity;
            l.Votes = 0;
        }

        private static string PlayerKey(ZNetPeer peer) {
            string address = null;
            try { address = peer.m_socket?.GetHostName(); } catch (System.Exception) { }
            return string.IsNullOrEmpty(address) ? "peer:" + peer.m_uid : address;
        }

        internal static string Kb(float bytesPerSec) => $"{bytesPerSec / BytesPerKB:F0} KB/s";
        private static string Pct(float share) => share < 0f ? "-" : $"{share * 100f:F1}%";
    }
}
