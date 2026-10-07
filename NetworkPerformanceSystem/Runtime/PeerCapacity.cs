using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M35 - the host's record of how smoothly each player's game runs, and the creature
    /// allowance that keeps a struggling one lightly loaded.
    ///
    /// The arbiter places creatures by latency. That is right for everyone who watches them and
    /// wrong for whoever ends up running them, when that is the weakest machine in the group: in
    /// the 2026-10-03 dungeon recording the player on the server's own network won almost every
    /// creature on ping, and ran 27 of them at 16-24 fps with frames of 100-300 ms, while the
    /// other two had frame rate to spare. A creature's updates leave its owner once per frame, so
    /// a slow owner is staleness for every viewer too - the cost function just cannot see it.
    ///
    /// So each player's game reports its frame rate (FrameSampler, M34), and a player whose frame
    /// rate stays under Min Owner FPS while they run more than Min Creature Allowance creatures
    /// is given an allowance: three quarters of what they own. The arbiter stops giving them
    /// creatures while they are at it and moves the excess, a few a pass, to players whose games
    /// have room (OwnershipArbiter's steering and shedding). An allowance is only ever about
    /// handing creatures to somebody else: a player who is alone with their creatures is never
    /// given one, and where nobody else present can take a creature it goes to them whatever
    /// their allowance says. The rules are LossBackoff's, applied to creatures instead of bytes -
    /// CreatureLoadRules.Step:
    ///
    ///   * still struggling a full hold after the allowance was reached -> another step;
    ///   * at the minimum, or at half of what they started with, and no faster than when it
    ///     started -> creatures are not what slows that game: the allowance is cleared and the
    ///     player is left alone for ten minutes;
    ///   * comfortably fast for a minute -> one step back up.
    ///
    /// Players without the mod never report, so nothing here applies to them and the arbiter
    /// treats them exactly as before. Reports are tiny (30 bytes every two seconds) and travel
    /// on a direct peer RPC an older or vanilla host drops unread.
    ///
    /// A second allowance, run by the same rules, watches each player's upload to the host
    /// (Balance Creatures By Upload). Every creature a player runs is sent from their machine, at
    /// a few KB/s each in a fight, and a player's upload is paced at 150 KB/s unless M37 grants
    /// more. In the 2026-10-06 plains fight one player ran 40-55 goblins with his upload pinned
    /// at 150 KB/s for five minutes; eleven of fifteen sampled stalls in the fight - 1.4 to 12.6
    /// seconds with no update while moving - were his goblins, while two other players uploaded
    /// 50-60 KB/s. Every game ran at 58-254 fps, so the frame-rate allowance never moved one.
    /// M37's grant helps where the player's line has room, but took two and a half minutes to
    /// reach 366 KB/s in a replay, and cannot help where the line itself is the limit.
    ///
    /// So a player whose upload is full - by M37's own test of the upload, which reads what their
    /// machine still has queued when their report carries it - and whom a grant will not fix
    /// soon (CreatureLoadRules.UploadStuck) is stepped down the same way, and creatures moved off
    /// them go to the players with the most upload room left. Only players whose uploads have
    /// room take creatures from anybody, for either reason. It gives up only at the minimum: an
    /// upload still full there is full of something else. The tighter of the two allowances is
    /// the one the arbiter sees.
    /// </summary>
    internal static class PeerCapacity {

        internal const string RpcClientLoad = "Nps.ClientLoad";

        /// <summary>Reports arrive every two seconds; anything much faster is not this mod.</summary>
        private const float MinReportIntervalSeconds = 0.75f;

        /// <summary>A report older than this decides nothing; the allowance stays as it is.</summary>
        private const float FreshSeconds = 10f;

        /// <summary>A player who has sent nothing for this long is forgotten, allowance and all.</summary>
        private const float ForgetSeconds = 60f;

        /// <summary>Two reports' weight against the run of earlier ones: settles in about four
        /// reports, so one hitchy window does not start a step and one smooth one does not end
        /// a run.</summary>
        private const float FpsAlpha = 0.3f;

        internal const float HoldSeconds = 10f;
        internal const float RecoverSeconds = 60f;
        internal const float HealthyMarginFps = 5f;
        internal const float ExemptSeconds = 600f;

        /// <summary>The report plus ZPackage's length prefix, with room to spare.</summary>
        private const int MaxPackageBytes = CreatureLoadRules.MaxReportBytes + 16;

        private sealed class Entry {
            internal FrameReport Last;
            internal bool HasReport;
            internal float LastAt;
            internal float FpsEwma;
            internal bool HasFps;
            internal int Owned;
            internal int Shared;
            internal AllowanceState State = AllowanceState.Fresh();

            // The upload allowance. UploadNow is the last pass's reading, kept whether or not
            // anything was decided on it: it says whether this player has room to take creatures.
            internal AllowanceState Upload = AllowanceState.Fresh();
            internal UploadReading UploadNow;
            internal float UploadFullSince = float.NegativeInfinity;
            internal bool UploadStuck;
            internal float UploadLossyAt = float.NegativeInfinity;   // the last pass it lost too much
        }

        internal struct View {
            internal bool HasReport;
            internal FrameReport Last;
            internal float AgeSeconds;
            internal bool HasFps;
            internal float Fps;
            internal int Owned;
            internal int Shared;                     // of Owned, where somebody else could run them
            internal int Steps;
            internal int Allowance;                  // int.MaxValue when none
            internal int OwnedAtStart;
            internal float FpsAtStart;
            internal bool Low;                       // under Min Owner FPS right now
            internal bool Exempt;
            internal float ExemptLeftSeconds;
            internal bool Receiver;                  // may take creatures moved off somebody else

            internal UploadReading Upload;
            internal bool UploadStuck;               // full, and no grant will fix it soon
            internal int UploadSteps;
            internal int UploadAllowance;            // int.MaxValue when none
            internal int UploadOwnedAtStart;
            internal float UploadAtStart;            // bytes/sec arriving when the first step was taken
            internal bool UploadExempt;
            internal float UploadExemptLeftSeconds;
        }

        private static readonly Dictionary<long, Entry> Peers = new Dictionary<long, Entry>();
        private static readonly List<long> Scratch = new List<long>();

        internal static long ReportsAccepted;
        internal static long ReportsRejected;
        internal static long TotalStepsDown;
        internal static long TotalStepsUp;
        internal static long TotalCleared;
        internal static long TotalExempted;
        internal static long TotalUploadStepsDown;
        internal static long TotalUploadStepsUp;
        internal static long TotalUploadCleared;
        internal static long TotalUploadExempted;

        /// <summary>What either allowance needs: the mechanism, and creature arbitration, which
        /// is what puts creatures in the arbiter's hands at all.</summary>
        private static bool Arbitrating =>
            PatchGuard.IsActive(Mechanism.CreatureAllowance)
            && ValConfig.OwnershipArbitrateCreatures != null
            && ValConfig.OwnershipArbitrateCreatures.Value;

        internal static bool BalancingFrameRate =>
            Arbitrating
            && ValConfig.BalanceCreaturesByFrameRate != null
            && ValConfig.BalanceCreaturesByFrameRate.Value;

        internal static bool BalancingUploads =>
            Arbitrating
            && ValConfig.BalanceCreaturesByUpload != null
            && ValConfig.BalanceCreaturesByUpload.Value;

        /// <summary>Either allowance is on: the arbiter counts creatures and asks for room.</summary>
        internal static bool Balancing => BalancingFrameRate || BalancingUploads;

        // -- intake ------------------------------------------------------------------------

        /// <summary>A player's frame report. Untrusted input: size-checked before it is read,
        /// rate-limited, and decoded into clamped numbers.</summary>
        internal static void RPC_ClientLoad(ZRpc rpc, ZPackage pkg) {
            if (!NpsEnv.IsHost()) { return; }

            ZNetPeer peer = Patches.NetworkChannelPatches.FindPeerByRpc(rpc);
            if (peer == null || peer.m_uid == 0L) { return; }
            if (pkg == null || pkg.Size() > MaxPackageBytes) { ReportsRejected++; return; }

            byte[] bytes;
            try {
                bytes = pkg.ReadByteArray();
            } catch (System.Exception) {
                ReportsRejected++;
                return;
            }
            if (!CreatureLoadRules.TryDecode(bytes, out FrameReport report)) { ReportsRejected++; return; }

            if (!OnReport(peer.m_uid, report, Time.realtimeSinceStartup)) { return; }

            // Whether their game is the active window decides how long they may go quiet before
            // they are dropped (Keep Players In The Background).
            ConnectionTimeout.NoteFocus(rpc, report.InBackground);
        }

        /// <summary>A report, from a client or from a listen host's own sampler. False when it
        /// came too soon after the last one and was dropped.</summary>
        internal static bool OnReport(long uid, FrameReport report, float now) {
            if (uid == 0L) { return false; }

            if (!Peers.TryGetValue(uid, out Entry e)) {
                e = new Entry();
                Peers[uid] = e;
            } else if (e.HasReport) {
                // One that says the game has just gone into, or come back from, the background
                // is sent the moment it happens (FrameSampler), so it is taken early - at half the
                // client's own spacing, since the two can arrive closer together than they left.
                float minInterval = report.InBackground != e.Last.InBackground
                    ? CreatureLoadRules.MinFocusReportSeconds * 0.5f
                    : MinReportIntervalSeconds;
                if (now - e.LastAt < minInterval) {
                    ReportsRejected++;
                    return false;
                }
            }

            e.Last = report;
            e.HasReport = true;
            e.LastAt = now;
            ReportsAccepted++;

            // A window spent loading or in the background says nothing about how this machine
            // copes with what it runs; it is kept for display and left out of the average.
            if (report.Unrepresentative) { return true; }
            float fps = report.Fps;
            e.FpsEwma = e.HasFps ? e.FpsEwma + (fps - e.FpsEwma) * FpsAlpha : fps;
            e.HasFps = true;
            return true;
        }

        // -- the decision ------------------------------------------------------------------

        /// <summary>Once per ownership pass, after the arbiter has counted who owns which
        /// creatures.</summary>
        internal static void OnPassCompleted(float now) {
            if (Peers.Count == 0) { return; }

            bool byFps = BalancingFrameRate;
            bool byUpload = BalancingUploads;
            AllowanceSettings settings = Settings();

            Scratch.Clear();
            foreach (KeyValuePair<long, Entry> pair in Peers) {
                Entry e = pair.Value;
                if (now - e.LastAt > ForgetSeconds) {
                    Scratch.Add(pair.Key);
                    continue;
                }

                e.Owned = OwnershipArbiter.OwnedCreaturesFor(pair.Key);
                e.Shared = OwnershipArbiter.SharedCreaturesFor(pair.Key);
                ReadUpload(pair.Key, e, now, byUpload);

                if (byFps) { StepFrameRate(pair.Key, e, now, settings); }
                if (byUpload) { StepUpload(pair.Key, e, now, settings); }
            }

            for (int i = 0; i < Scratch.Count; i++) {
                if (Peers.TryGetValue(Scratch[i], out Entry gone) && (gone.State.Steps > 0 || gone.Upload.Steps > 0)) {
                    Logger.LogInfo($"Creature load: {Name(Scratch[i])} has not reported for {ForgetSeconds:F0}s; their creature allowance is dropped.");
                }
                Peers.Remove(Scratch[i]);
            }
        }

        private static void StepFrameRate(long uid, Entry e, float now, AllowanceSettings settings) {
            AllowanceInputs input = new AllowanceInputs {
                Now = now,
                Usable = e.HasFps && now - e.LastAt <= FreshSeconds && !e.Last.Unrepresentative,
                Fps = e.FpsEwma,
                Owned = e.Owned,
                Shared = e.Shared,
            };

            int stepsBefore = e.State.Steps;
            int allowanceBefore = AllowanceOf(e.State, settings.MinAllowance);
            int ownedAtStartBefore = e.State.OwnedAtStart;
            float fpsAtStartBefore = e.State.ValueAtStart;

            AllowanceAction action = CreatureLoadRules.Step(ref e.State, input, settings);
            if (action == AllowanceAction.None) { return; }

            Announce(uid, e, action, stepsBefore, allowanceBefore, ownedAtStartBefore, fpsAtStartBefore, settings);
        }

        /// <summary>This pass's reading of the player's upload, and how long it has been full.
        /// Off, or not read, it is unknown - which counts as room to take creatures.</summary>
        private static void ReadUpload(long uid, Entry e, float now, bool byUpload) {
            UploadReading reading = default;
            if (byUpload) { AutoSendRate.TryGetUpload(uid, now, out reading); }
            e.UploadNow = reading;
            if (reading.Full) {
                if (e.UploadFullSince == float.NegativeInfinity) { e.UploadFullSince = now; }
            } else {
                e.UploadFullSince = float.NegativeInfinity;
            }
            if (reading.Known && reading.Quality >= 0f && reading.Quality < CreatureLoadRules.UploadRoomQuality) { e.UploadLossyAt = now; }
            e.UploadStuck = CreatureLoadRules.UploadStuck(reading, e.UploadFullSince, now);
        }

        private static void StepUpload(long uid, Entry e, float now, AllowanceSettings settings) {
            UploadReading up = e.UploadNow;
            bool loading = e.HasReport && (e.Last.Flags & CreatureLoadRules.FlagLoading) != 0;
            UploadAllowanceInputs input = new UploadAllowanceInputs {
                Now = now,
                Usable = up.Known && !loading,
                Stuck = e.UploadStuck,
                Room = CreatureLoadRules.UploadRoomForStepUp(up, e.Owned, e.Upload, settings.MinAllowance),
                Delivered = up.Delivered,
                Owned = e.Owned,
                Shared = e.Shared,
            };

            int stepsBefore = e.Upload.Steps;
            int allowanceBefore = AllowanceOf(e.Upload, settings.MinAllowance);
            int ownedAtStartBefore = e.Upload.OwnedAtStart;
            float atStartBefore = e.Upload.ValueAtStart;

            AllowanceAction action = CreatureLoadRules.StepUpload(ref e.Upload, input, settings);
            if (action == AllowanceAction.None) { return; }

            AnnounceUpload(uid, e, action, stepsBefore, allowanceBefore, ownedAtStartBefore, atStartBefore, settings);
        }

        private static AllowanceSettings Settings() {
            return new AllowanceSettings {
                MinFps = ValConfig.CreatureLoadMinOwnerFps.Value,
                MinAllowance = ValConfig.CreatureLoadMinAllowance.Value,
                HoldSeconds = HoldSeconds,
                RecoverSeconds = RecoverSeconds,
                HealthyMarginFps = HealthyMarginFps,
                ExemptSeconds = ExemptSeconds,
            };
        }

        private static int AllowanceOf(AllowanceState state, int minAllowance) {
            return CreatureLoadRules.AllowanceFor(state.OwnedAtStart, state.Steps, minAllowance);
        }

        private static void Announce(long uid, Entry e, AllowanceAction action, int stepsBefore, int allowanceBefore,
                                     int ownedAtStartBefore, float fpsAtStartBefore, AllowanceSettings settings) {
            string name = Name(uid);
            int allowance = AllowanceOf(e.State, settings.MinAllowance);

            switch (action) {
                case AllowanceAction.Down:
                    TotalStepsDown++;
                    if (stepsBefore == 0) {
                        Logger.LogInfo($"Creature load: {name} is running at {e.FpsEwma:F0} fps while simulating {e.Owned} creatures; " +
                                       $"they get no new creatures above {allowance}, and the rest move a few at a time to players with room.");
                    } else {
                        Logger.LogInfo($"Creature load: {name} is still at {e.FpsEwma:F0} fps with {e.Owned} creatures; " +
                                       $"allowance {allowanceBefore} -> {allowance} (step {e.State.Steps}).");
                    }
                    break;
                case AllowanceAction.Up:
                    TotalStepsUp++;
                    Logger.LogInfo($"Creature load: {name} at {e.FpsEwma:F0} fps; allowance {allowanceBefore} -> {allowance} (step {e.State.Steps}).");
                    break;
                case AllowanceAction.Clear:
                    TotalCleared++;
                    Logger.LogInfo($"Creature load: {name} at {e.FpsEwma:F0} fps; no creature allowance any more.");
                    break;
                case AllowanceAction.Exempt:
                    TotalExempted++;
                    Logger.LogWarning($"Creature load: {name} is still at {e.FpsEwma:F0} fps after being held to {allowanceBefore} creatures " +
                                      $"({ownedAtStartBefore} and {fpsAtStartBefore:F0} fps when it started), so creatures are not what slows their game. " +
                                      $"Their allowance is dropped and they are left alone for {ExemptSeconds / 60f:F0} minutes.");
                    break;
            }

            if (Monitoring.Active) {
                Monitoring.OnCreatureAllowance(uid, ActionName(action), e.State.Steps,
                                               allowance == int.MaxValue ? -1 : allowance, e.Owned,
                                               action == AllowanceAction.Exempt ? ownedAtStartBefore : e.State.OwnedAtStart,
                                               e.FpsEwma,
                                               action == AllowanceAction.Exempt ? fpsAtStartBefore : e.State.ValueAtStart);
            }
        }

        private static void AnnounceUpload(long uid, Entry e, AllowanceAction action, int stepsBefore, int allowanceBefore,
                                           int ownedAtStartBefore, float atStartBefore, AllowanceSettings settings) {
            string name = Name(uid);
            int allowance = AllowanceOf(e.Upload, settings.MinAllowance);
            UploadReading up = e.UploadNow;
            string upload = $"{AutoSendRate.Kb(up.Delivered)} of {AutoSendRate.Kb(up.Rate)}";

            switch (action) {
                case AllowanceAction.Down:
                    TotalUploadStepsDown++;
                    if (stepsBefore == 0) {
                        string why = up.GrantMayRise
                            ? $"still full {CreatureLoadRules.UploadWaitSeconds:F0}s on, faster than its rate can be raised"
                            : "and a higher upload rate cannot fix it";
                        Logger.LogInfo($"Creature load: {name}'s upload to the server is full ({upload}, {why}) while they run {e.Owned} creatures; " +
                                       $"they get no new creatures above {allowance}, and the rest move a few at a time to players whose uploads have room.");
                    } else {
                        Logger.LogInfo($"Creature load: {name}'s upload is still full ({upload}) with {e.Owned} creatures; " +
                                       $"upload allowance {allowanceBefore} -> {allowance} (step {e.Upload.Steps}).");
                    }
                    break;
                case AllowanceAction.Up:
                    TotalUploadStepsUp++;
                    Logger.LogInfo($"Creature load: {name}'s upload has room ({upload}); upload allowance {allowanceBefore} -> {allowance} (step {e.Upload.Steps}).");
                    break;
                case AllowanceAction.Clear:
                    TotalUploadCleared++;
                    Logger.LogInfo($"Creature load: {name}'s upload has room ({upload}); no upload allowance any more.");
                    break;
                case AllowanceAction.Exempt:
                    TotalUploadExempted++;
                    Logger.LogWarning($"Creature load: {name}'s upload is still full ({upload}) after being held to {allowanceBefore} creatures " +
                                      $"({ownedAtStartBefore} when it started), so creatures are not what fills it. " +
                                      $"Their upload allowance is dropped and they are left alone for {ExemptSeconds / 60f:F0} minutes.");
                    break;
            }

            if (Monitoring.Active) {
                Monitoring.OnCreatureAllowanceUpload(uid, ActionName(action), e.Upload.Steps,
                                                     allowance == int.MaxValue ? -1 : allowance, e.Owned,
                                                     action == AllowanceAction.Exempt ? ownedAtStartBefore : e.Upload.OwnedAtStart,
                                                     up.Delivered,
                                                     action == AllowanceAction.Exempt ? atStartBefore : e.Upload.ValueAtStart,
                                                     up.Rate, up.GrantMayRise);
            }
        }

        private static string ActionName(AllowanceAction action) {
            switch (action) {
                case AllowanceAction.Down: return "down";
                case AllowanceAction.Up: return "up";
                case AllowanceAction.Clear: return "clear";
                case AllowanceAction.Exempt: return "exempt";
                default: return "none";
            }
        }

        /// <summary>The settings changed. Either allowance switched off goes at once, so creatures
        /// return to the ordinary rules without waiting out a recover time.</summary>
        internal static void OnConfigChanged() {
            bool byFps = BalancingFrameRate;
            bool byUpload = BalancingUploads;
            if (byFps && byUpload) { return; }

            int dropped = 0;
            foreach (Entry e in Peers.Values) {
                if (!byFps) {
                    if (e.State.Steps > 0) { dropped++; }
                    e.State = AllowanceState.Fresh();
                }
                if (!byUpload) {
                    if (e.Upload.Steps > 0) { dropped++; }
                    e.Upload = AllowanceState.Fresh();
                    e.UploadNow = default;
                    e.UploadFullSince = float.NegativeInfinity;
                    e.UploadStuck = false;
                    e.UploadLossyAt = float.NegativeInfinity;
                }
            }
            if (dropped > 0) {
                Logger.LogInfo($"Creature load: balancing is off; {dropped} creature allowance(s) dropped.");
            }
        }

        // -- views -------------------------------------------------------------------------

        /// <summary>The most creatures this player may own: int.MaxValue unless they are held to
        /// an allowance right now - the tighter one when they are held to both.</summary>
        internal static int AllowanceFor(long uid) {
            if (!Peers.TryGetValue(uid, out Entry e)) { return int.MaxValue; }
            int min = ValConfig.CreatureLoadMinAllowance.Value;
            int allowance = int.MaxValue;
            if (e.State.Steps > 0 && BalancingFrameRate) { allowance = AllowanceOf(e.State, min); }
            if (e.Upload.Steps > 0 && BalancingUploads) { allowance = System.Math.Min(allowance, AllowanceOf(e.Upload, min)); }
            return allowance;
        }

        /// <summary>The upload allowance is the one this player is held to (it is no looser than
        /// the frame-rate one): creatures moved off them go where uploads have room, and the
        /// handoff is recorded as Upload rather than Capacity.</summary>
        internal static bool IsUploadLimited(long uid) {
            if (!BalancingUploads || !Peers.TryGetValue(uid, out Entry e) || e.Upload.Steps == 0) { return false; }
            int min = ValConfig.CreatureLoadMinAllowance.Value;
            int upload = AllowanceOf(e.Upload, min);
            return !(e.State.Steps > 0 && BalancingFrameRate) || upload <= AllowanceOf(e.State, min);
        }

        /// <summary>What one of this player's creatures costs their upload, bytes/sec
        /// (CreatureLoadRules.UploadPerCreature): what the creatures moved off them will cost
        /// whoever takes them. 0 when unknown.</summary>
        internal static float UploadPerCreature(long uid) {
            if (!Peers.TryGetValue(uid, out Entry e)) { return 0f; }
            return CreatureLoadRules.UploadPerCreature(e.UploadNow, e.Owned, e.Upload);
        }

        /// <summary>Bytes/sec of upload room this player has left (CreatureLoadRules.UploadRoomBytes);
        /// +inf when it is not read - the host, a player just joined, or the upload rule off.</summary>
        internal static float UploadRoomFor(long uid) {
            if (!Peers.TryGetValue(uid, out Entry e)) { return float.PositiveInfinity; }
            return CreatureLoadRules.UploadRoomBytes(e.UploadNow);
        }

        /// <summary>Whether this player may take creatures moved off somebody else: a fresh,
        /// representative report, comfortably above Min Owner FPS, not held to an allowance of
        /// their own, and an upload with room that has not lost packets for UploadCleanSeconds
        /// (CreatureLoadRules; always, while the upload rule is off). A player who has never
        /// reported - no mod, or a dedicated server - never is: there is no telling whether their
        /// game has room.</summary>
        internal static bool IsShedReceiver(long uid, float now) {
            if (!Peers.TryGetValue(uid, out Entry e)) { return false; }
            if (!e.HasFps || e.Last.Unrepresentative) { return false; }
            if (now - e.LastAt > FreshSeconds) { return false; }
            if (e.State.Steps > 0 || e.State.Run == AllowanceRun.Low) { return false; }
            if (e.Upload.Steps > 0 || e.UploadStuck || !CreatureLoadRules.UploadHasRoom(e.UploadNow)) { return false; }
            if (now - e.UploadLossyAt < CreatureLoadRules.UploadCleanSeconds) { return false; }
            return e.FpsEwma >= ValConfig.CreatureLoadMinOwnerFps.Value + HealthyMarginFps;
        }

        internal static bool TryGetView(long uid, out View view) {
            view = default;
            if (!Peers.TryGetValue(uid, out Entry e)) { return false; }

            float now = Time.realtimeSinceStartup;
            int min = ValConfig.CreatureLoadMinAllowance.Value;
            view = new View {
                HasReport = e.HasReport,
                Last = e.Last,
                AgeSeconds = now - e.LastAt,
                HasFps = e.HasFps,
                Fps = e.FpsEwma,
                Owned = e.Owned,
                Shared = e.Shared,
                Steps = e.State.Steps,
                Allowance = AllowanceOf(e.State, min),
                OwnedAtStart = e.State.OwnedAtStart,
                FpsAtStart = e.State.ValueAtStart,
                Low = e.HasFps && e.FpsEwma < ValConfig.CreatureLoadMinOwnerFps.Value,
                Exempt = e.State.Exempt && now < e.State.ExemptUntil,
                ExemptLeftSeconds = e.State.Exempt ? e.State.ExemptUntil - now : 0f,
                Receiver = Balancing && IsShedReceiver(uid, now),
                Upload = e.UploadNow,
                UploadStuck = e.UploadStuck,
                UploadSteps = e.Upload.Steps,
                UploadAllowance = AllowanceOf(e.Upload, min),
                UploadOwnedAtStart = e.Upload.OwnedAtStart,
                UploadAtStart = e.Upload.ValueAtStart,
                UploadExempt = e.Upload.Exempt && now < e.Upload.ExemptUntil,
                UploadExemptLeftSeconds = e.Upload.Exempt ? e.Upload.ExemptUntil - now : 0f,
            };
            return true;
        }

        /// <summary>Every player with a report, for nps_stats.</summary>
        internal static void CollectReporting(List<long> into) {
            foreach (long uid in Peers.Keys) { into.Add(uid); }
        }

        internal static int LimitedNow() {
            int limited = 0;
            foreach (Entry e in Peers.Values) {
                if (e.State.Steps > 0 || e.Upload.Steps > 0) { limited++; }
            }
            return limited;
        }

        internal static string Name(long uid) {
            if (uid == NpsEnv.LocalSessionId()) {
                Player local = Player.m_localPlayer;
                return local != null ? local.GetPlayerName() + " (host)" : "host";
            }
            ZNetPeer peer = ZNet.instance != null ? ZNet.instance.GetPeer(uid) : null;
            return peer != null && !string.IsNullOrEmpty(peer.m_playerName) ? peer.m_playerName : $"peer {uid}";
        }

        // -- lifecycle ---------------------------------------------------------------------

        internal static void ForgetPeer(long uid) {
            Peers.Remove(uid);
        }

        internal static void Reset() {
            Peers.Clear();
            Scratch.Clear();
        }
    }
}
