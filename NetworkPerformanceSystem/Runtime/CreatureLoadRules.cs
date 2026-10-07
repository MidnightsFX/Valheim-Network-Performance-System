using System.Collections.Generic;
using System.IO;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// One player's frame report: how smoothly their game ran over the last couple of seconds, and
    /// how much it was simulating. A client sends one to the host every two seconds (M34); a
    /// listen host files its own the same way.
    ///
    /// Frame rate is what decisions are made on. Simulation time and fixed steps are recorded for
    /// diagnosis only: the simulation figure is the wall time of MonoUpdaters.FixedUpdate, which
    /// runs every Character - owned or not - and leaves out PhysX, and fixed steps stay at 50 a
    /// second until a game is below about 3 fps.
    /// </summary>
    internal struct FrameReport {
        internal byte Version;
        internal byte Flags;
        internal float WindowSeconds;
        internal int Frames;
        internal float MeanFrameMs;
        internal float WorstFrameMs;

        /// <summary>Share of the window spent in frames longer than SlowFrameSeconds.</summary>
        internal float SlowShare;

        /// <summary>Milliseconds per second spent in MonoUpdaters.FixedUpdate; -1 when unknown.</summary>
        internal float SimMsPerSecond;
        internal int FixedSteps;

        /// <summary>Creature AIs this machine was simulating when the report was taken.</summary>
        internal int OwnedAi;

        /// <summary>Application.targetFrameRate, -1 for none.</summary>
        internal int FrameCap;

        // Version 2 (M37): this player's upload to the host, from their side of the connection.
        // -1 where unknown - a version 1 report, a listen host's own, or no reading in the window.

        /// <summary>The most bytes waiting in Steam to go to the host at any reading in the window.</summary>
        internal int UploadPending;

        /// <summary>Steam's share of this player's packets that reached the host, last reading.</summary>
        internal float UploadQuality;

        /// <summary>Share of the window's readings at which the upload was full: delivering close
        /// to its rate with a queue behind it (ThroughputRules.IsFullSample).</summary>
        internal float UploadFullShare;

        internal bool HasUpload => UploadFullShare >= 0f;

        internal float Fps => WindowSeconds > 0f ? Frames / WindowSeconds : 0f;
        internal float FixedHz => WindowSeconds > 0f ? FixedSteps / WindowSeconds : 0f;

        /// <summary>The window does not say how this machine copes with what it simulates: the
        /// game was in the background for some of it, or loading - a teleport, a dungeon, a
        /// respawn.</summary>
        internal bool Unrepresentative =>
            (Flags & (CreatureLoadRules.FlagUnfocused | CreatureLoadRules.FlagLoading | CreatureLoadRules.FlagUnfocusedFrames)) != 0;

        /// <summary>The game was not the active window when this report was sent - alt-tabbed or
        /// minimised. Every version of the report means this by FlagUnfocused.</summary>
        internal bool InBackground => (Flags & CreatureLoadRules.FlagUnfocused) != 0;
    }

    /// <summary>What happened to a player's creature allowance on one evaluation.</summary>
    internal enum AllowanceAction : byte {
        None,
        Down,       // allowance set, or lowered by a step
        Up,         // one step back up
        Clear,      // no allowance any more
        Exempt,     // lowering it did not help; cleared, and not set again for a while
    }

    internal enum AllowanceRun : byte { None, Low, Healthy }

    /// <summary>
    /// Everything the host keeps per player to decide one of their creature allowances - the
    /// frame-rate one or the upload one. A plain struct so the rules below can be run offline
    /// against a recording.
    /// </summary>
    internal struct AllowanceState {
        internal int Steps;                     // 0 = no allowance
        internal int OwnedAtStart;              // creatures owned when the first step was taken
        internal float ValueAtStart;            // fps, or upload bytes/sec arriving, at the first step
        internal AllowanceRun Run;
        internal float RunSince;
        internal int RunSamples;
        internal float LastChangeAt;            // any step, up or down
        internal float ReachedAt;               // when owned first fell to the allowance after the last step; -1 = not yet
        internal float LastUpAt;                // last step up or clear; -inf when never
        internal float RecoverScale;            // 1, doubled when a step down follows a step up too soon (cap 8)
        internal bool Exempt;
        internal float ExemptUntil;

        internal static AllowanceState Fresh() {
            return new AllowanceState {
                LastChangeAt = float.NegativeInfinity,
                ReachedAt = -1f,
                LastUpAt = float.NegativeInfinity,
                RecoverScale = 1f,
            };
        }
    }

    internal struct AllowanceSettings {
        internal float MinFps;
        internal int MinAllowance;
        internal float HoldSeconds;
        internal float RecoverSeconds;
        internal float HealthyMarginFps;
        internal float ExemptSeconds;
    }

    internal struct AllowanceInputs {
        internal float Now;

        /// <summary>A fresh, representative report stands behind Fps. When false the run is
        /// reset and nothing is decided: an old figure, or one taken while loading, says nothing
        /// about how the machine copes now.</summary>
        internal bool Usable;
        internal float Fps;

        /// <summary>Creatures this player owns as of the last ownership pass.</summary>
        internal int Owned;

        /// <summary>How many of those somebody else could run: a player with no allowance of
        /// their own, or the host, has the creature's zone in their active area. A player for
        /// whom this is 0 is the only option for every creature they run.</summary>
        internal int Shared;
    }

    /// <summary>
    /// One player's upload to the host as the upload allowance reads it: M37's measurement of
    /// what arrives from them (AutoSendRate.TryGetUpload). Known is false when there is no fresh
    /// one - a player just joined, or this host is not reading links.
    /// </summary>
    internal struct UploadReading {
        internal bool Known;

        /// <summary>ThroughputRules.IsFull on their upload: delivering close to the rate they
        /// may send at - with a queue behind it, when their frame report says what waits on
        /// their machine - for most of the last twenty seconds.</summary>
        internal bool Full;

        /// <summary>Bytes/sec arriving from them, smoothed over a couple of seconds.</summary>
        internal float Delivered;

        /// <summary>The rate their game may upload at: the game's 150 KB/s, or M37's grant.</summary>
        internal int Rate;

        /// <summary>Share of their packets that arrive; -1 while Steam has no figure.</summary>
        internal float Quality;

        /// <summary>M37 could still raise their grant: it is running, their game takes grants,
        /// their link delivers cleanly enough to be raised, and the grant is under its cap.</summary>
        internal bool GrantMayRise;

        /// <summary>When their grant last changed; -inf when never.</summary>
        internal float GrantChangedAt;
    }

    internal struct UploadAllowanceInputs {
        internal float Now;

        /// <summary>A fresh reading stands behind Stuck and Room, and the player is not loading.</summary>
        internal bool Usable;

        /// <summary>Their upload is full and a grant will not fix it soon (UploadStuck).</summary>
        internal bool Stuck;

        /// <summary>Their upload has room to spare, even for what one step back up would return
        /// (UploadRoomForStepUp).</summary>
        internal bool Room;
        internal float Delivered;
        internal int Owned;
        internal int Shared;
    }

    /// <summary>A candidate owner as the receiver pick sees it. Built by the arbiter from a sector
    /// verdict.</summary>
    internal struct ReceiverOption {
        internal int Index;                     // into the arbiter's candidate list
        internal float Total;
        internal float Worst;
        internal float Rtt;
        internal int Room;                      // creatures it may still take; int.MaxValue = no allowance
        internal int Load;                      // creatures it runs, counting ones given to it this pass
        internal bool Eligible;                 // may own, and (for sheds) is a healthy receiver

        /// <summary>Bytes/sec of upload room it has left, counting the creatures given to it this
        /// pass at the shedding player's cost per creature; +inf when unknown (the host, whose
        /// creatures cost no player's upload).</summary>
        internal float UploadRoom;
    }

    /// <summary>
    /// M34/M35's decisions with nothing of the game in them, so all of it can be checked offline:
    /// the frame report's wire format, the per-frame window that fills it, the allowance a slow
    /// player is held to, and which player takes a creature that has to move.
    /// </summary>
    internal static class CreatureLoadRules {

        /// <summary>The game was in the background when the report was sent.</summary>
        internal const byte FlagUnfocused = 1;
        internal const byte FlagLoading = 2;
        internal const byte FlagNoSimTiming = 4;

        /// <summary>Some frames in the window ran in the background, whatever the game was doing
        /// when it was sent - so the first report after alt-tabbing back is not averaged in at
        /// the background frame cap. A 1.15.0 host does not know this bit and ignores it.</summary>
        internal const byte FlagUnfocusedFrames = 8;

        /// <summary>A report that says the game has just gone into, or come back from, the
        /// background goes out at once rather than waiting for the next two-second report, and
        /// the host takes it this soon after the last one. A game that stops running as soon as
        /// it is not the active window never sends that next report.</summary>
        internal const float MinFocusReportSeconds = 0.1f;

        internal const byte CurrentVersion = 2;

        /// <summary>Version 1's fields. A newer report is read this far and the rest ignored, so
        /// fields are only ever appended - a 1.15 host reads a version 2 report as version 1.</summary>
        internal const int Version1Bytes = 30;

        /// <summary>Version 2 appends the upload figures: pending (int), quality and full share
        /// (floats).</summary>
        internal const int Version2Bytes = Version1Bytes + 12;

        /// <summary>Anything longer is not a report this mod built.</summary>
        internal const int MaxReportBytes = 128;

        /// <summary>A frame this long or longer is a hitch rather than a slow frame rate.</summary>
        internal const float SlowFrameSeconds = 0.05f;

        /// <summary>Each step keeps three quarters of the creatures the player had at the start.</summary>
        internal const float StepFactor = 0.75f;

        /// <summary>How much better than at the first step fps must be for the allowance to count
        /// as having helped.</summary>
        internal const float ImprovementFactor = 1.10f;

        internal const int MinRunSamples = 3;
        internal const float MaxRecoverScale = 8f;

        /// <summary>A step down this long after the last step up starts the recover backoff over.</summary>
        internal const float RecoverScaleResetSeconds = 600f;

        /// <summary>At most this many creatures are moved off one player per ownership pass.</summary>
        internal const int MaxShedsPerOwnerPerPass = 4;

        /// <summary>While M37 could still raise a full upload's grant, it gets this long after the
        /// grant last changed (or the upload filled) before creatures move. Raising a grant takes a
        /// minute or more a step - the 2026-10-06 replay needed two and a half minutes to reach
        /// 366 KB/s - and the owner's creatures stall for every viewer meanwhile.</summary>
        internal const float UploadWaitSeconds = 30f;

        /// <summary>An upload with room: arriving at no more than this share of its rate...</summary>
        internal const float UploadRoomShare = 0.75f;

        /// <summary>...and losing no more than this share of its packets. A lossy upload is held at
        /// the game's rate by M37 and has no room to give whatever it carries now.</summary>
        internal const float UploadRoomQuality = 0.95f;

        /// <summary>A player only takes creatures moved off somebody else once their upload has
        /// lost no more than UploadRoomQuality allows for this long. Loss that comes and goes
        /// passes a test of the last few seconds: in the 2026-10-06 replay one player's upload
        /// lost 3-12% most of the night, cleared for a few seconds at a time, and picked up a
        /// dozen creatures in those gaps.</summary>
        internal const float UploadCleanSeconds = 60f;

        // -- wire --------------------------------------------------------------------------

        internal static byte[] Encode(FrameReport report) {
            using (MemoryStream stream = new MemoryStream(Version1Bytes))
            using (BinaryWriter writer = new BinaryWriter(stream)) {
                writer.Write(CurrentVersion);
                writer.Write(report.Flags);
                writer.Write(report.WindowSeconds);
                writer.Write(ClampUShort(report.Frames));
                writer.Write(report.MeanFrameMs);
                writer.Write(report.WorstFrameMs);
                writer.Write(report.SlowShare);
                writer.Write(report.SimMsPerSecond);
                writer.Write(ClampUShort(report.FixedSteps));
                writer.Write(ClampUShort(report.OwnedAi));
                writer.Write((short)System.Math.Max(short.MinValue, System.Math.Min(short.MaxValue, report.FrameCap)));
                writer.Write(report.UploadPending);
                writer.Write(report.UploadQuality);
                writer.Write(report.UploadFullShare);
                writer.Flush();
                return stream.ToArray();
            }
        }

        internal static bool TryDecode(byte[] bytes, out FrameReport report) {
            report = default;
            if (bytes == null || bytes.Length < Version1Bytes || bytes.Length > MaxReportBytes) { return false; }

            try {
                using (MemoryStream stream = new MemoryStream(bytes, false))
                using (BinaryReader reader = new BinaryReader(stream)) {
                    byte version = reader.ReadByte();
                    if (version < 1) { return false; }
                    report.Version = version;
                    report.Flags = reader.ReadByte();
                    report.WindowSeconds = reader.ReadSingle();
                    report.Frames = reader.ReadUInt16();
                    report.MeanFrameMs = reader.ReadSingle();
                    report.WorstFrameMs = reader.ReadSingle();
                    report.SlowShare = reader.ReadSingle();
                    report.SimMsPerSecond = reader.ReadSingle();
                    report.FixedSteps = reader.ReadUInt16();
                    report.OwnedAi = reader.ReadUInt16();
                    report.FrameCap = reader.ReadInt16();
                    if (version >= 2 && bytes.Length >= Version2Bytes) {
                        report.UploadPending = reader.ReadInt32();
                        report.UploadQuality = reader.ReadSingle();
                        report.UploadFullShare = reader.ReadSingle();
                    } else {
                        report.UploadPending = -1;
                        report.UploadQuality = -1f;
                        report.UploadFullShare = -1f;
                    }
                }
            } catch (System.Exception) {
                return false;
            }

            report = Sanitize(report);
            return report.WindowSeconds > 0f && report.Frames > 0;
        }

        /// <summary>Whatever arrived, the numbers the host acts on are finite and in range.</summary>
        internal static FrameReport Sanitize(FrameReport report) {
            report.WindowSeconds = Finite(report.WindowSeconds, 0f, 0f, 60f);
            report.Frames = System.Math.Max(0, System.Math.Min(ushort.MaxValue, report.Frames));
            report.MeanFrameMs = Finite(report.MeanFrameMs, 0f, 0f, 60000f);
            report.WorstFrameMs = Finite(report.WorstFrameMs, 0f, 0f, 60000f);
            report.SlowShare = Finite(report.SlowShare, 0f, 0f, 1f);
            report.SimMsPerSecond = report.SimMsPerSecond < 0f ? -1f : Finite(report.SimMsPerSecond, -1f, 0f, 1000f);
            report.FixedSteps = System.Math.Max(0, System.Math.Min(ushort.MaxValue, report.FixedSteps));
            report.OwnedAi = System.Math.Max(0, System.Math.Min(ushort.MaxValue, report.OwnedAi));
            if (report.FrameCap < -1) { report.FrameCap = -1; }
            if (report.UploadPending < -1) { report.UploadPending = -1; }
            report.UploadQuality = report.UploadQuality < 0f ? -1f : Finite(report.UploadQuality, -1f, 0f, 1f);
            report.UploadFullShare = report.UploadFullShare < 0f ? -1f : Finite(report.UploadFullShare, -1f, 0f, 1f);
            return report;
        }

        private static float Finite(float value, float fallback, float min, float max) {
            if (float.IsNaN(value) || float.IsInfinity(value)) { return fallback; }
            return value < min ? min : value > max ? max : value;
        }

        private static ushort ClampUShort(int value) {
            return (ushort)System.Math.Max(0, System.Math.Min(ushort.MaxValue, value));
        }

        // -- allowance ---------------------------------------------------------------------

        /// <summary>The creatures a player may own after this many steps: three quarters of what
        /// they had at the start per step, never below the minimum. int.MaxValue at step 0.</summary>
        internal static int AllowanceFor(int ownedAtStart, int steps, int minAllowance) {
            if (steps <= 0) { return int.MaxValue; }
            double allowed = ownedAtStart;
            for (int i = 0; i < steps; i++) { allowed *= StepFactor; }
            return System.Math.Max(minAllowance, (int)System.Math.Round(allowed));
        }

        internal static bool Improved(float fpsNow, float fpsAtStart) {
            return fpsNow >= fpsAtStart * ImprovementFactor;
        }

        /// <summary>
        /// Whether taking creatures away has been shown not to help: the player is at the minimum,
        /// or down to half of what they started with, and their frame rate is no better than when
        /// it started. Asked no earlier, because one step moves a quarter of their creatures, and
        /// when creatures are a quarter of their frame time that is only a few percent of fps -
        /// under the improvement bar even when it is working.
        /// </summary>
        internal static bool ShouldGiveUp(int steps, int allowance, int ownedAtStart, int minAllowance, float fps, float fpsAtStart) {
            return GivesUp(steps, allowance, ownedAtStart, minAllowance, Improved(fps, fpsAtStart), halvedGivesUp: true);
        }

        /// <summary>ShouldGiveUp with the verdict on whether it helped already made. halvedGivesUp
        /// false asks only at the minimum: an upload that is still full says nothing about whether
        /// fewer creatures helped - it reads the same at any demand over its rate - so it is
        /// stepped as far as the minimum before it is called creatures' fault or not.</summary>
        internal static bool GivesUp(int steps, int allowance, int ownedAtStart, int minAllowance, bool improved, bool halvedGivesUp) {
            if (steps <= 0) { return false; }
            if (improved) { return false; }
            bool atFloor = allowance <= minAllowance;
            bool halved = halvedGivesUp && allowance * 2 <= ownedAtStart;
            return atFloor || halved;
        }

        /// <summary>
        /// One evaluation of a player's allowance, once per ownership pass.
        ///
        ///   * frame rate under MinFps for HoldSeconds while owning more than MinAllowance, some of
        ///     it where somebody else could run it (Shared) -> an allowance of three quarters of
        ///     what they own. A player who is the only option for every creature they run is
        ///     never given one: there is nobody to give the rest to, and holding them under it
        ///     would only leave creatures with nobody running them;
        ///   * still under, a full HoldSeconds after the allowance was REACHED (owned at or below
        ///     it) -> another step, or, once ShouldGiveUp says so, cleared and exempt for
        ///     ExemptSeconds. While creatures are still being moved off it waits: there is no
        ///     verdict yet, and a player nobody can take creatures from never reaches it;
        ///   * at or above MinFps + HealthyMarginFps for RecoverSeconds -> one step back up, the
        ///     last one clearing it. A step down soon after a step up doubles that player's
        ///     recover time (up to 8x), so a machine that only copes with fewer creatures is not
        ///     handed them back every minute.
        /// </summary>
        internal static AllowanceAction Step(ref AllowanceState state, AllowanceInputs input, AllowanceSettings settings) {
            AllowanceRun kind = input.Fps < settings.MinFps ? AllowanceRun.Low
                              : input.Fps >= settings.MinFps + settings.HealthyMarginFps ? AllowanceRun.Healthy
                              : AllowanceRun.None;
            return Advance(ref state, input.Now, input.Usable, kind, input.Fps, Improved(input.Fps, state.ValueAtStart),
                           halvedGivesUp: true, input.Owned, input.Shared, settings);
        }

        // -- the upload allowance ----------------------------------------------------------

        /// <summary>
        /// Whether a player's upload is full with no grant coming to fix it soon: full, and either
        /// M37 cannot raise their grant (it is off, their game takes no grants, the grant is at
        /// its cap or held under a ceiling their link failed at, or their link loses too much to
        /// be raised), or the upload has stayed full UploadWaitSeconds since the grant last
        /// changed. fullSince is when the reading first said full in this run.
        /// </summary>
        internal static bool UploadStuck(UploadReading r, float fullSince, float now) {
            if (!r.Known || !r.Full) { return false; }
            if (!r.GrantMayRise) { return true; }
            return now - System.Math.Max(fullSince, r.GrantChangedAt) >= UploadWaitSeconds;
        }

        /// <summary>Whether a player's upload can take more: not full, arriving at no more than
        /// UploadRoomShare of its rate, and not losing more than UploadRoomQuality allows. A player
        /// whose upload is not read (the host itself) has room: creatures it runs cost no
        /// player's upload.</summary>
        internal static bool UploadHasRoom(UploadReading r) {
            if (!r.Known) { return true; }
            if (r.Full || r.Rate <= 0) { return false; }
            if (r.Quality >= 0f && r.Quality < UploadRoomQuality) { return false; }
            return r.Delivered <= UploadRoomShare * r.Rate;
        }

        /// <summary>Bytes/sec an upload can still take before it stops having room; +inf when it
        /// is not read.</summary>
        internal static float UploadRoomBytes(UploadReading r) {
            if (!r.Known) { return float.PositiveInfinity; }
            return UploadRoomShare * r.Rate - r.Delivered;
        }

        /// <summary>
        /// What one of a player's creatures costs their upload, bytes/sec: what arrives from them
        /// over what they run, and - once they are held - no more than it was when the first step
        /// was taken (the upload then over the creatures then). Everything a player sends is
        /// counted against their creatures, so with only a few left the first figure is mostly
        /// their own character; the one from the start was taken with the creatures dominating.
        /// 0 when there is nothing to go on.
        /// </summary>
        internal static float UploadPerCreature(UploadReading r, int owned, AllowanceState state) {
            if (!r.Known) { return 0f; }
            float each = owned > 0 ? r.Delivered / owned : float.PositiveInfinity;
            if (state.Steps > 0 && state.OwnedAtStart > 0) {
                each = System.Math.Min(each, state.ValueAtStart / state.OwnedAtStart);
            }
            return float.IsPositiveInfinity(each) ? 0f : each;
        }

        /// <summary>
        /// Whether a held player's upload has room for what one step back up would return: it has
        /// room now (UploadHasRoom), and still would with that step's creatures at what each costs
        /// (UploadPerCreature). Without the second half a player at 70% of their rate on the
        /// allowance is handed back a third more creatures, fills, and is stepped down again half
        /// a minute later.
        /// </summary>
        internal static bool UploadRoomForStepUp(UploadReading r, int owned, AllowanceState state, int minAllowance) {
            if (!r.Known || !UploadHasRoom(r)) { return false; }
            float back = CreaturesBackOneStep(state, minAllowance) * UploadPerCreature(r, owned, state);
            return r.Delivered + back <= UploadRoomShare * r.Rate;
        }

        /// <summary>How many creatures one step back up from this state hands back: the next
        /// allowance up less this one, and for the last step what they ran when it started.</summary>
        internal static int CreaturesBackOneStep(AllowanceState state, int minAllowance) {
            if (state.Steps <= 0) { return 0; }
            int now = AllowanceFor(state.OwnedAtStart, state.Steps, minAllowance);
            int up = state.Steps == 1 ? state.OwnedAtStart : AllowanceFor(state.OwnedAtStart, state.Steps - 1, minAllowance);
            return System.Math.Max(0, up - now);
        }

        /// <summary>
        /// One evaluation of a player's upload allowance: Step's rules with "stuck" in place of a
        /// low frame rate and "room" in place of a healthy one. The one difference is when it gives
        /// up: only at the minimum (see GivesUp), and then the player is left alone for
        /// ExemptSeconds - their upload is full of something other than creatures.
        /// </summary>
        internal static AllowanceAction StepUpload(ref AllowanceState state, UploadAllowanceInputs input, AllowanceSettings settings) {
            AllowanceRun kind = input.Stuck ? AllowanceRun.Low
                              : input.Room ? AllowanceRun.Healthy
                              : AllowanceRun.None;
            return Advance(ref state, input.Now, input.Usable, kind, input.Delivered, improved: false,
                           halvedGivesUp: false, input.Owned, input.Shared, settings);
        }

        /// <summary>The rules both allowances share; see Step. kind is this evaluation's verdict:
        /// Low (struggling), Healthy (room to spare) or None (neither).</summary>
        private static AllowanceAction Advance(ref AllowanceState state, float now, bool usable, AllowanceRun kind, float value,
                                               bool improved, bool halvedGivesUp, int owned, int shared, AllowanceSettings settings) {
            if (state.Exempt) {
                if (now < state.ExemptUntil) { return AllowanceAction.None; }
                state.Exempt = false;
            }

            if (!usable) {
                state.Run = AllowanceRun.None;
                state.RunSince = now;
                state.RunSamples = 0;
                return AllowanceAction.None;
            }

            if (kind != state.Run) {
                state.Run = kind;
                state.RunSince = now;
                state.RunSamples = 0;
            }
            state.RunSamples++;

            int allowance = AllowanceFor(state.OwnedAtStart, state.Steps, settings.MinAllowance);
            if (state.Steps > 0) {
                if (owned <= allowance) {
                    if (state.ReachedAt < 0f) { state.ReachedAt = now; }
                } else {
                    state.ReachedAt = -1f;
                }
            }

            if (kind == AllowanceRun.None || state.RunSamples < MinRunSamples) { return AllowanceAction.None; }

            if (kind == AllowanceRun.Low) {
                if (state.Steps == 0) {
                    if (now - System.Math.Max(state.RunSince, state.LastChangeAt) < settings.HoldSeconds) { return AllowanceAction.None; }
                    if (owned <= settings.MinAllowance) { return AllowanceAction.None; }
                    // Alone with their creatures. The run keeps going, so the step comes at once
                    // if somebody who could take some arrives while this player is still slow.
                    if (shared <= 0) { return AllowanceAction.None; }

                    NoteStepDown(ref state, now, settings);
                    state.Steps = 1;
                    state.OwnedAtStart = owned;
                    state.ValueAtStart = value;
                    state.LastChangeAt = now;
                    state.ReachedAt = -1f;
                    return AllowanceAction.Down;
                }

                if (state.ReachedAt < 0f) { return AllowanceAction.None; }
                if (now - System.Math.Max(state.ReachedAt, state.LastChangeAt) < settings.HoldSeconds) { return AllowanceAction.None; }

                if (GivesUp(state.Steps, allowance, state.OwnedAtStart, settings.MinAllowance, improved, halvedGivesUp)) {
                    state.Steps = 0;
                    state.OwnedAtStart = 0;
                    state.LastChangeAt = now;
                    state.ReachedAt = -1f;
                    state.RecoverScale = 1f;
                    state.Exempt = true;
                    state.ExemptUntil = now + settings.ExemptSeconds;
                    return AllowanceAction.Exempt;
                }

                int next = AllowanceFor(state.OwnedAtStart, state.Steps + 1, settings.MinAllowance);
                if (next >= allowance) { return AllowanceAction.None; }   // at the minimum, and it helped: hold there
                NoteStepDown(ref state, now, settings);
                state.Steps++;
                state.LastChangeAt = now;
                state.ReachedAt = -1f;
                return AllowanceAction.Down;
            }

            // Healthy.
            if (state.Steps == 0) { return AllowanceAction.None; }
            if (now - System.Math.Max(state.RunSince, state.LastChangeAt) < settings.RecoverSeconds * state.RecoverScale) {
                return AllowanceAction.None;
            }
            state.Steps--;
            state.LastChangeAt = now;
            state.LastUpAt = now;
            state.ReachedAt = -1f;
            if (state.Steps == 0) {
                state.OwnedAtStart = 0;
                return AllowanceAction.Clear;
            }
            return AllowanceAction.Up;
        }

        /// <summary>A step down soon after a step up means the machine only coped with fewer
        /// creatures: the next climb waits twice as long. A step down long after the last climb
        /// starts the backoff over.</summary>
        private static void NoteStepDown(ref AllowanceState state, float now, AllowanceSettings settings) {
            float sinceUp = now - state.LastUpAt;
            if (sinceUp < settings.RecoverSeconds * state.RecoverScale) {
                state.RecoverScale = System.Math.Min(MaxRecoverScale, state.RecoverScale * 2f);
            } else if (sinceUp >= RecoverScaleResetSeconds) {
                state.RecoverScale = 1f;
            }
        }

        // -- who takes a creature ----------------------------------------------------------

        /// <summary>The arbiter's ranking - lowest total staleness, then lowest worst case, then
        /// lowest RTT - with Unity's Mathf.Approximately written out so this file stays free of
        /// game types.</summary>
        internal static bool IsBetter(float total, float worst, float rtt, float otherTotal, float otherWorst, float otherRtt) {
            if (!Approximately(total, otherTotal)) { return total < otherTotal; }
            if (!Approximately(worst, otherWorst)) { return worst < otherWorst; }
            return rtt < otherRtt;
        }

        internal static bool Approximately(float a, float b) {
            return System.Math.Abs(b - a) < System.Math.Max(1E-06f * System.Math.Max(System.Math.Abs(a), System.Math.Abs(b)), float.Epsilon * 8f);
        }

        /// <summary>The best eligible option with room, excluding one candidate index; -1 when
        /// there is none. Returns a position in the list, not a candidate index.</summary>
        internal static int PickWithRoom(List<ReceiverOption> options, int excludeIndex) {
            int best = -1;
            for (int i = 0; i < options.Count; i++) {
                ReceiverOption option = options[i];
                if (!option.Eligible || option.Room <= 0 || option.Index == excludeIndex) { continue; }
                if (best < 0) { best = i; continue; }
                ReceiverOption incumbent = options[best];
                if (IsBetter(option.Total, option.Worst, option.Rtt, incumbent.Total, incumbent.Worst, incumbent.Rtt)) { best = i; }
            }
            return best;
        }

        /// <summary>
        /// Where a creature being moved off a slow player goes: among the eligible options with
        /// room, the ones whose total cost is within the challenge margin of the best are all
        /// good enough, and of those the one running the fewest creatures takes it - counting
        /// the ones it has been given this pass - so creatures moved off one player spread over
        /// everyone healthy instead of piling onto whoever has the next-lowest ping. Room cannot
        /// do that job: a player healthy enough to take creatures never has an allowance of their
        /// own, so every receiver has unlimited room. Ties go to the better-ranked one.
        ///
        /// byUploadRoom is for a creature moved off a player whose upload is full: there it is the
        /// one with the most upload room left (UploadRoom) that takes it, then the fewest
        /// creatures - so the upload a creature costs lands where there is room for it, rather
        /// than on a player who runs few creatures but whose own upload is close to full.
        /// </summary>
        internal static int PickReceiver(List<ReceiverOption> options, int excludeIndex, float marginMs, bool byUploadRoom = false) {
            int best = PickWithRoom(options, excludeIndex);
            if (best < 0) { return -1; }

            float bar = options[best].Total + marginMs;
            int chosen = best;
            for (int i = 0; i < options.Count; i++) {
                ReceiverOption option = options[i];
                if (i == best || !option.Eligible || option.Room <= 0 || option.Index == excludeIndex) { continue; }
                if (option.Total > bar) { continue; }
                ReceiverOption current = options[chosen];
                if (byUploadRoom && option.UploadRoom != current.UploadRoom) {
                    if (option.UploadRoom > current.UploadRoom) { chosen = i; }
                    continue;
                }
                if (option.Load < current.Load
                    || (option.Load == current.Load
                        && IsBetter(option.Total, option.Worst, option.Rtt, current.Total, current.Worst, current.Rtt))) {
                    chosen = i;
                }
            }
            return chosen;
        }

        // -- which creatures move first ------------------------------------------------------

        /// <summary>How many creatures to move off a player this pass.</summary>
        internal static int ShedsThisPass(int overage, int maxPerPass) {
            if (overage <= 0) { return 0; }
            return overage < maxPerPass ? overage : maxPerPass;
        }

        /// <summary>Who is near the creature, from the proximity scan.</summary>
        internal const int NearNobody = 0;
        internal const int NearOne = 1;
        internal const int NearShared = 2;

        /// <summary>
        /// Whether a creature may be moved off its owner for capacity at all:
        ///   * two or more players near it -> yes; it is a shared fight and anyone there can run it;
        ///   * exactly one, and it is the owner -> no; the one player who can see it keeps it;
        ///   * exactly one, somebody else -> yes;
        ///   * nobody near -> only when the owner is beyond the proximity pull distance, or the
        ///     proximity layer would pull it straight back the moment they stepped closer.
        /// Distances are squared, on the ground plane, as the proximity layer measures them.
        /// </summary>
        internal static bool ShedEligible(int nearState, bool ownerIsSoleNear, float ownerSq, float pullSq) {
            switch (nearState) {
                case NearShared: return true;
                case NearOne: return !ownerIsSoleNear;
                default: return ownerSq > pullSq;
            }
        }

        /// <summary>The order creatures leave a player in: ones not fighting first, then the ones
        /// furthest from that player - measured in 3D, so the animals on the surface above a
        /// dungeon go before anything in it - then by id, so the order is stable.</summary>
        internal static int CompareShed(bool alertA, float distSqA, uint idA, bool alertB, float distSqB, uint idB) {
            if (alertA != alertB) { return alertA ? 1 : -1; }
            int byDistance = distSqB.CompareTo(distSqA);
            if (byDistance != 0) { return byDistance; }
            return idA.CompareTo(idB);
        }
    }

    /// <summary>
    /// The frames of one report window, added one at a time. Nothing in here reads a clock, so it
    /// can be fed recorded frame times offline.
    /// </summary>
    internal sealed class FrameWindow {
        private int _frames;
        private double _seconds;
        private float _worst;
        private double _slowSeconds;
        private double _simMs;
        private int _fixedSteps;
        private byte _flags;
        private int _uploadSamples;
        private int _uploadFull;
        private int _uploadPending = -1;
        private float _uploadQuality = -1f;

        internal int Frames => _frames;
        internal double Seconds => _seconds;

        internal void Add(float deltaSeconds) {
            if (!(deltaSeconds > 0f) || float.IsInfinity(deltaSeconds)) { return; }
            _frames++;
            _seconds += deltaSeconds;
            if (deltaSeconds > _worst) { _worst = deltaSeconds; }
            if (deltaSeconds >= CreatureLoadRules.SlowFrameSeconds) { _slowSeconds += deltaSeconds; }
        }

        internal void AddSim(double milliseconds) {
            if (milliseconds > 0d) { _simMs += milliseconds; }
            _fixedSteps++;
        }

        internal void MarkFlags(byte flags) {
            _flags |= flags;
        }

        /// <summary>One reading of this player's upload to the host (M37), at the ping cadence.</summary>
        internal void AddUpload(int pendingBytes, float quality, bool full) {
            _uploadSamples++;
            if (full) { _uploadFull++; }
            if (pendingBytes > _uploadPending) { _uploadPending = pendingBytes; }
            if (quality >= 0f) { _uploadQuality = quality; }
        }

        internal FrameReport Snapshot(int ownedAi, byte flags, int frameCap, bool simTimed) {
            float seconds = (float)_seconds;
            byte all = (byte)(_flags | flags | (simTimed ? 0 : CreatureLoadRules.FlagNoSimTiming));
            return new FrameReport {
                Version = CreatureLoadRules.CurrentVersion,
                Flags = all,
                WindowSeconds = seconds,
                Frames = _frames,
                MeanFrameMs = _frames > 0 ? (float)(_seconds * 1000d / _frames) : 0f,
                WorstFrameMs = _worst * 1000f,
                SlowShare = seconds > 0f ? (float)(_slowSeconds / _seconds) : 0f,
                SimMsPerSecond = simTimed && seconds > 0f ? (float)(_simMs / _seconds) : -1f,
                FixedSteps = _fixedSteps,
                OwnedAi = ownedAi,
                FrameCap = frameCap,
                UploadPending = _uploadSamples > 0 ? _uploadPending : -1,
                UploadQuality = _uploadSamples > 0 ? _uploadQuality : -1f,
                UploadFullShare = _uploadSamples > 0 ? (float)_uploadFull / _uploadSamples : -1f,
            };
        }

        internal void Reset() {
            _frames = 0;
            _seconds = 0d;
            _worst = 0f;
            _slowSeconds = 0d;
            _simMs = 0d;
            _fixedSteps = 0;
            _flags = 0;
            _uploadSamples = 0;
            _uploadFull = 0;
            _uploadPending = -1;
            _uploadQuality = -1f;
        }
    }
}
