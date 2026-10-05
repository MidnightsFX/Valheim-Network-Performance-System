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
    /// have room (OwnershipArbiter's steering and shedding). The rules are LossBackoff's, applied
    /// to creatures instead of bytes - CreatureLoadRules.Step:
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
            internal AllowanceState State = AllowanceState.Fresh();
        }

        internal struct View {
            internal bool HasReport;
            internal FrameReport Last;
            internal float AgeSeconds;
            internal bool HasFps;
            internal float Fps;
            internal int Owned;
            internal int Steps;
            internal int Allowance;                  // int.MaxValue when none
            internal int OwnedAtStart;
            internal float FpsAtStart;
            internal bool Low;                       // under Min Owner FPS right now
            internal bool Exempt;
            internal float ExemptLeftSeconds;
            internal bool Receiver;                  // may take creatures moved off somebody else
        }

        private static readonly Dictionary<long, Entry> Peers = new Dictionary<long, Entry>();
        private static readonly List<long> Scratch = new List<long>();

        internal static long ReportsAccepted;
        internal static long ReportsRejected;
        internal static long TotalStepsDown;
        internal static long TotalStepsUp;
        internal static long TotalCleared;
        internal static long TotalExempted;

        /// <summary>Everything the allowance needs: the mechanism, its switch, and creature
        /// arbitration, which is what puts creatures in the arbiter's hands at all.</summary>
        internal static bool Balancing =>
            PatchGuard.IsActive(Mechanism.CreatureAllowance)
            && ValConfig.BalanceCreaturesByFrameRate != null
            && ValConfig.BalanceCreaturesByFrameRate.Value
            && ValConfig.OwnershipArbitrateCreatures.Value;

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

            OnReport(peer.m_uid, report, Time.realtimeSinceStartup);
        }

        /// <summary>A report, from a client or from a listen host's own sampler.</summary>
        internal static void OnReport(long uid, FrameReport report, float now) {
            if (uid == 0L) { return; }

            if (!Peers.TryGetValue(uid, out Entry e)) {
                e = new Entry();
                Peers[uid] = e;
            } else if (e.HasReport && now - e.LastAt < MinReportIntervalSeconds) {
                ReportsRejected++;
                return;
            }

            e.Last = report;
            e.HasReport = true;
            e.LastAt = now;
            ReportsAccepted++;

            // A window spent loading or in the background says nothing about how this machine
            // copes with what it runs; it is kept for display and left out of the average.
            if (report.Unrepresentative) { return; }
            float fps = report.Fps;
            e.FpsEwma = e.HasFps ? e.FpsEwma + (fps - e.FpsEwma) * FpsAlpha : fps;
            e.HasFps = true;
        }

        // -- the decision ------------------------------------------------------------------

        /// <summary>Once per ownership pass, after the arbiter has counted who owns which
        /// creatures.</summary>
        internal static void OnPassCompleted(float now) {
            if (Peers.Count == 0) { return; }

            bool balancing = Balancing;
            AllowanceSettings settings = Settings();

            Scratch.Clear();
            foreach (KeyValuePair<long, Entry> pair in Peers) {
                Entry e = pair.Value;
                if (now - e.LastAt > ForgetSeconds) {
                    Scratch.Add(pair.Key);
                    continue;
                }

                e.Owned = OwnershipArbiter.OwnedCreaturesFor(pair.Key);
                if (!balancing) { continue; }

                AllowanceInputs input = new AllowanceInputs {
                    Now = now,
                    Usable = e.HasFps && now - e.LastAt <= FreshSeconds && !e.Last.Unrepresentative,
                    Fps = e.FpsEwma,
                    Owned = e.Owned,
                };

                int stepsBefore = e.State.Steps;
                int allowanceBefore = AllowanceOf(e, settings.MinAllowance);
                int ownedAtStartBefore = e.State.OwnedAtStart;
                float fpsAtStartBefore = e.State.FpsAtStart;

                AllowanceAction action = CreatureLoadRules.Step(ref e.State, input, settings);
                if (action == AllowanceAction.None) { continue; }

                Announce(pair.Key, e, action, stepsBefore, allowanceBefore, ownedAtStartBefore, fpsAtStartBefore, settings);
            }

            for (int i = 0; i < Scratch.Count; i++) {
                if (Peers.TryGetValue(Scratch[i], out Entry gone) && gone.State.Steps > 0) {
                    Logger.LogInfo($"Creature load: {Name(Scratch[i])} has not reported for {ForgetSeconds:F0}s; their creature allowance is dropped.");
                }
                Peers.Remove(Scratch[i]);
            }
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

        private static int AllowanceOf(Entry e, int minAllowance) {
            return CreatureLoadRules.AllowanceFor(e.State.OwnedAtStart, e.State.Steps, minAllowance);
        }

        private static void Announce(long uid, Entry e, AllowanceAction action, int stepsBefore, int allowanceBefore,
                                     int ownedAtStartBefore, float fpsAtStartBefore, AllowanceSettings settings) {
            string name = Name(uid);
            int allowance = AllowanceOf(e, settings.MinAllowance);

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
                                               action == AllowanceAction.Exempt ? fpsAtStartBefore : e.State.FpsAtStart);
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

        /// <summary>The settings changed. Switched off, every allowance goes at once so creatures
        /// return to the ordinary rules without waiting out a recover time.</summary>
        internal static void OnConfigChanged() {
            if (Balancing) { return; }

            int dropped = 0;
            foreach (Entry e in Peers.Values) {
                if (e.State.Steps > 0) { dropped++; }
                e.State = AllowanceState.Fresh();
            }
            if (dropped > 0) {
                Logger.LogInfo($"Creature load: balancing is off; {dropped} creature allowance(s) dropped.");
            }
        }

        // -- views -------------------------------------------------------------------------

        /// <summary>The most creatures this player may own: int.MaxValue unless they are held to
        /// an allowance right now.</summary>
        internal static int AllowanceFor(long uid) {
            if (!Peers.TryGetValue(uid, out Entry e) || e.State.Steps == 0) { return int.MaxValue; }
            if (!Balancing) { return int.MaxValue; }
            return AllowanceOf(e, ValConfig.CreatureLoadMinAllowance.Value);
        }

        /// <summary>Whether this player may take creatures moved off somebody else: a fresh,
        /// representative report, comfortably above Min Owner FPS, and not held to an allowance
        /// of their own. A player who has never reported - no mod, or a dedicated server - never
        /// is: there is no telling whether their game has room.</summary>
        internal static bool IsShedReceiver(long uid) {
            if (!Peers.TryGetValue(uid, out Entry e)) { return false; }
            if (!e.HasFps || e.Last.Unrepresentative) { return false; }
            if (Time.realtimeSinceStartup - e.LastAt > FreshSeconds) { return false; }
            if (e.State.Steps > 0 || e.State.Run == AllowanceRun.Low) { return false; }
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
                Steps = e.State.Steps,
                Allowance = AllowanceOf(e, min),
                OwnedAtStart = e.State.OwnedAtStart,
                FpsAtStart = e.State.FpsAtStart,
                Low = e.HasFps && e.FpsEwma < ValConfig.CreatureLoadMinOwnerFps.Value,
                Exempt = e.State.Exempt && now < e.State.ExemptUntil,
                ExemptLeftSeconds = e.State.Exempt ? e.State.ExemptUntil - now : 0f,
                Receiver = Balancing && IsShedReceiver(uid),
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
                if (e.State.Steps > 0) { limited++; }
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
