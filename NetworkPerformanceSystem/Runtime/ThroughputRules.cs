using System;
using System.Collections.Generic;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// A connection's ping at its best: the lowest ten-second median of the last five minutes.
    /// What the path costs with nothing queued on it, so a ping well above it is a queue - at the
    /// sender, a router, or the far end - and a window sized from it does not grow with the
    /// queue it is filling. A median per bucket so one lucky sample is not the baseline; the
    /// lowest bucket so a fight that queues for minutes does not become it either.
    /// </summary>
    internal sealed class PingBaseline {
        internal const float BucketSeconds = 10f;
        internal const int Buckets = 30;
        internal const int MinBuckets = 3;

        private readonly List<float> _bucket = new List<float>(32);
        private float _bucketStart = float.NegativeInfinity;
        private readonly float[] _medians = new float[Buckets];
        private int _count;
        private int _next;

        /// <summary>Milliseconds; -1 until MinBuckets buckets have closed.</summary>
        internal float Value { get; private set; } = -1f;

        internal void Add(float now, float pingMs) {
            if (pingMs <= 0f || float.IsNaN(pingMs) || float.IsInfinity(pingMs)) { return; }
            if (_bucketStart == float.NegativeInfinity || now < _bucketStart) { _bucketStart = now; }
            _bucket.Add(pingMs);
            if (now - _bucketStart < BucketSeconds) { return; }

            float median = ThroughputRules.Median(_bucket);
            _bucket.Clear();
            _bucketStart = now;
            _medians[_next] = median;
            _next = (_next + 1) % Buckets;
            if (_count < Buckets) { _count++; }
            if (_count < MinBuckets) { return; }

            float min = float.PositiveInfinity;
            for (int i = 0; i < _count; i++) {
                if (_medians[i] < min) { min = _medians[i]; }
            }
            Value = min;
        }

        /// <summary>A new connection: the bucket in progress belonged to the old one. The closed
        /// buckets are the same player's path and are kept.</summary>
        internal void Restart() {
            _bucket.Clear();
            _bucketStart = float.NegativeInfinity;
        }
    }

    /// <summary>
    /// One connection's measurements as M37 keeps them, in one direction: what is sent on it, how
    /// much of that arrives, how long its ping is against its own best, and how much of the last
    /// twenty seconds it spent full. Kept per player rather than per peer id, so a reconnect keeps
    /// its ping baseline (the connection-level figures start over; see Reconnected).
    /// </summary>
    internal sealed class LinkTrack {
        internal float LastSampleAt = float.NegativeInfinity;
        internal int Samples;
        internal int SamplesSinceChange;

        /// <summary>Time-weighted share of the last ~FullWindowSeconds the link was full.</summary>
        internal float FullShare;
        internal bool LastFull;

        /// <summary>EWMA of the share of packets delivered; -1 before the first figure.</summary>
        internal float Quality = -1f;

        /// <summary>Quality when the rate in force last changed (or once the link settled): what a
        /// drop is measured from. -1 until there is one.</summary>
        internal float QualityRef = -1f;
        internal float QualityRefAt = float.NegativeInfinity;

        /// <summary>EWMA of bytes/sec sent x share delivered.</summary>
        internal float Delivered;
        internal float Bps;
        internal int Rate;

        /// <summary>EWMA of Steam's transport ping; -1 before the first figure.</summary>
        internal float Ping = -1f;

        internal readonly PingBaseline PingBest = new PingBaseline();

        /// <summary>See PingBaseline; -1 until it has one.</summary>
        internal float Baseline => PingBest.Value;
    }

    /// <summary>One link as a decision sees it. Built by the caller from a LinkTrack plus what only
    /// the game knows: whether the player is loading, exempt from loss backoff, or held below the
    /// rate in force.</summary>
    internal struct LinkVote {
        /// <summary>May vote: settled figures, a ping baseline, and nothing else explains its
        /// state (not loading, not exempt, not held by a per-player back-off).</summary>
        internal bool Eligible;

        /// <summary>Asking for more at the rate in force.</summary>
        internal bool Full;

        /// <summary>Held below the rate in force by a per-player back-off or ceiling: whatever it
        /// asks for, raising the shared rate does not reach it.</summary>
        internal bool Held;

        internal bool Loss;
        internal bool Rtt;
        internal float Quality;
        internal float Bps;
        internal float Delivered;
    }

    internal struct VoteResult {
        internal int Eligible;
        internal int Voters;
        internal int Quorum;
        internal bool NonFullVoter;

        /// <summary>The line itself is the bottleneck: enough links lost packets or queued at
        /// once, and either one of them was not even full or every one of them is voting.</summary>
        internal bool Shared;
    }

    /// <summary>What the line has been seen to carry, and what it has been seen not to. One per
    /// direction.</summary>
    internal sealed class RateLimits {
        internal float WindowSum;
        internal int WindowCount;
        internal float WindowStart = float.NegativeInfinity;

        /// <summary>A ten-second total above the committed peak, waiting out PeakCommitSeconds
        /// without congestion before it counts.</summary>
        internal float CandidatePeak;
        internal float CandidateAt = float.NegativeInfinity;

        /// <summary>The highest ten-second delivered total seen clean, bytes/sec. 0 until one has
        /// been committed, which holds every step up until the line has been measured.</summary>
        internal float CommittedPeak;

        /// <summary>Learned from congestion, bytes/sec; -1 = none yet.</summary>
        internal float Ceiling = -1f;

        /// <summary>No probing above Ceiling until then.</summary>
        internal float CeilingUntil = float.NegativeInfinity;
        internal float CeilingHold = ThroughputRules.CeilingHoldSeconds;

        /// <summary>The ceiling was raised to look for more, from ReprobeFrom.</summary>
        internal bool Reprobing;
        internal float ReprobeFrom = -1f;

        internal float LastCongestionAt = float.NegativeInfinity;

        /// <summary>Consecutive evaluations on which the vote said Shared.</summary>
        internal int Votes;
    }

    internal enum RateAction : byte {
        None,
        Up,         // one step up
        Down,       // the line is congested: a step down, and a ceiling learned
        Limit,      // over the per-player share of a known limit (a join, a new ceiling, an admin limit)
        Overload,   // the host cannot keep up
        NoGain,     // the last step up did not raise what full links deliver: back, and held
    }

    internal sealed class OutState {
        internal int Rate = ThroughputRules.BaseRate;
        internal int PreviousRate = ThroughputRules.BaseRate;
        internal float LastChangeAt = float.NegativeInfinity;
        internal float LastUpAt = float.NegativeInfinity;
        internal readonly RateLimits Limits = new RateLimits();

        /// <summary>What a full link delivered on average when the last step up was taken, and
        /// when to look at what it delivers now.</summary>
        internal float UpFullMeanBefore;
        internal float UpCheckAt = float.NegativeInfinity;

        /// <summary>No step up until then: the last one bought nothing.</summary>
        internal float NoGainUntil = float.NegativeInfinity;
        internal float NoGainHold = ThroughputRules.NoGainHoldSeconds;
    }

    internal struct RateInputs {
        internal float Now;

        /// <summary>Nothing in this evaluation's window makes the figures unreliable: no hitch, no
        /// save, no outage at this end. When false nothing is decided and no peak is recorded.</summary>
        internal bool Usable;

        /// <summary>The host has room for more sends: frame time and the send scheduler are fine.</summary>
        internal bool HostHealthy;

        /// <summary>The host has been struggling long enough to give some back.</summary>
        internal bool HostOverloaded;

        internal int Max;

        /// <summary>The admin's figure for the line in this direction, bytes/sec; 0 = learn it.</summary>
        internal float AdminLimit;
    }

    internal struct RateDecision {
        internal RateAction Action;
        internal int From;
        internal int Rate;
        internal float Total;
        internal float LimitTotal;
        internal int PerPlayer;
        internal VoteResult Vote;

        /// <summary>Somebody is full and the line is carrying at least BusyPeakShare of what it
        /// has carried cleanly: only then can the rate be what congests it.</summary>
        internal bool Busy;
        internal bool LearnedCeiling;
    }

    /// <summary>One player's upload grant: the rate their own game may send to the host at.</summary>
    internal sealed class GrantState {
        internal int Grant = ThroughputRules.BaseRate;
        internal int Previous = ThroughputRules.BaseRate;
        internal float LastChangeAt = float.NegativeInfinity;
        internal float LastUpAt = float.NegativeInfinity;
        internal float LowSince = float.NegativeInfinity;
        internal float CleanSince = float.NegativeInfinity;

        /// <summary>A rate this player's link was shown not to carry; 0 = none. Grants stay under
        /// it until CeilingUntil.</summary>
        internal int Ceiling;
        internal float CeilingUntil = float.NegativeInfinity;
        internal float CeilingHold = ThroughputRules.PlayerCeilingSeconds;
        internal int LastCeiling;
    }

    internal sealed class InState {
        internal float LastUpAt = float.NegativeInfinity;
        internal readonly RateLimits Limits = new RateLimits();
    }

    internal struct GrantChange {
        internal int Index;
        internal int From;
        internal int To;
        internal string Reason;
    }

    /// <summary>
    /// M37's decisions with nothing of the game in them, so all of it can be tabled offline and
    /// replayed against a recording: when a link is full, when the line is congested, what it has
    /// been seen to carry, and the rate - or grant - that follows.
    ///
    /// Steam paces every connection at a fixed rate and never adapts it (see SteamTransport), so
    /// nothing below Valheim finds out what a line carries. Too high a rate shows up in only two
    /// ways: packets lost, or a router queue that lengthens everybody's ping. Both are read per
    /// link and voted on. A limit of one player's own connection hurts only that player, and
    /// only while they are full; the server's line hurts everyone at once, full or not. So a
    /// shared verdict needs a quorum and at least one voter who was not full - anything less is
    /// one player's link, which is M26's to deal with.
    ///
    /// Steps up are taken one at a time, only while somebody is full, never more than a quarter
    /// above what the line has been seen to carry cleanly (CommittedPeak), and never above a known
    /// limit's per-player share - which is what brings the rate back toward the game's 150 KB/s as
    /// players join. Congestion takes a quarter off and learns a ceiling, which is probed again
    /// only after a hold, and the hold doubles while probing keeps finding the same ceiling.
    /// </summary>
    internal static class ThroughputRules {

        /// <summary>The game's own rate: never go below it.</summary>
        internal const int BaseRate = 153600;

        internal const float FullRateShare = 0.85f;

        /// <summary>Without a queue figure a link only counts as full this close to its rate.</summary>
        internal const float FullRateShareNoQueue = 0.90f;
        internal const int FullPendingBytes = 4096;
        internal const float FullWindowSeconds = 20f;
        internal const float FullShareNeeded = 0.60f;

        internal const float QualityAlpha = 0.25f;
        internal const float PingAlpha = 0.25f;
        internal const float DeliveredAlpha = 0.25f;

        internal const int MinVoteSamples = 4;
        internal const int RefSamples = 8;
        internal const float RefRefreshSeconds = 300f;

        internal const float LossDrop = 0.02f;

        /// <summary>A link delivering this much never votes loss, whatever it delivered before.</summary>
        internal const float LossVoteBelow = 0.99f;
        internal const float RttRiseMs = 30f;
        internal const float RttRiseShare = 0.5f;
        internal const int CongestionEvaluations = 3;

        internal const float UpFactor = 1.25f;
        internal const float DownFactor = 0.75f;
        internal const float MinStepGain = 1.05f;
        internal const float UpGapSeconds = 45f;
        internal const float UpQuietSeconds = 90f;
        internal const float OverloadGapSeconds = 10f;

        /// <summary>Steam's delivery figure carries loss for some seconds after it stops, so a
        /// second congestion step waits this long after the first.</summary>
        internal const float DownGapSeconds = 20f;
        internal const float PostStepSeconds = 20f;

        /// <summary>
        /// A shared verdict only counts while the line carries at least this share of its
        /// committed peak, with somebody full. Loss and ping rising everywhere at once while the
        /// line is half idle is not this rate's doing - an outage at either end, players loading
        /// or leaving together (the 2026-10-07 recording had one at a logout wave) - and lowering
        /// the rate would not touch it; learning a ceiling from it would hold every player at the
        /// game's rate for ten minutes.
        /// </summary>
        internal const float BusyPeakShare = 0.5f;

        internal const float CeilingShare = 0.9f;
        internal const float AdminShare = 0.9f;
        internal const float BudgetHeadroom = 1.25f;
        internal const float PeakWindowSeconds = 10f;
        internal const float PeakCommitSeconds = 30f;
        internal const float CeilingHoldSeconds = 600f;
        internal const float MaxCeilingHoldSeconds = 2400f;
        internal const float ReprobeFactor = 1.10f;
        internal const float ReprobeNearShare = 0.10f;

        /// <summary>
        /// A step up has to show in what full links deliver within NoGainCheckSeconds: at least
        /// NoGainShare of what they delivered before it. A rate is only one of the things that
        /// can hold a link back - the host's send work (the 2026-10-07 server spent its whole
        /// four-millisecond scheduler budget on two sends a frame), the window, a player's own
        /// connection queueing without loss - and when it is something else a higher rate buys
        /// nothing and only waits to burst. Such a step is undone and no step is tried for
        /// NoGainHoldSeconds, doubled each time it happens again (up to MaxCeilingHoldSeconds).
        /// </summary>
        internal const float NoGainCheckSeconds = 30f;
        internal const float NoGainShare = 1.08f;
        internal const float NoGainHoldSeconds = 600f;

        internal const float PlayerCeilingSeconds = 600f;
        internal const float GrantDownQuality = 0.95f;
        internal const float GrantDownSeconds = 10f;
        internal const float GrantUpQuality = 0.98f;
        internal const float GrantUpSeconds = 60f;

        /// <summary>A sample this long after the last is a stall, not a measurement of the gap.</summary>
        private const float MaxSampleGapSeconds = 5f;

        // -- per-link measurement ----------------------------------------------------------

        /// <summary>
        /// One sample of one link. bps is what is sent on it in the managed direction, quality the
        /// share of that delivered (negative = Steam has no figure yet), pending what waits at the
        /// sender (negative = unknown), rate what the link is paced at, pingMs Steam's transport
        /// ping (0 or less = unknown).
        /// </summary>
        internal static void Observe(LinkTrack t, float now, float bps, float quality, int pending, int rate, float pingMs) {
            float dt = t.LastSampleAt > float.NegativeInfinity ? now - t.LastSampleAt : 0f;
            if (dt < 0f || dt > MaxSampleGapSeconds) { dt = 0f; }
            t.LastSampleAt = now;

            bps = Finite(bps);
            t.Bps = bps;
            t.Rate = rate;

            float share = quality >= 0f ? Math.Min(1f, quality) : 1f;
            float delivered = bps * share;
            t.Delivered = t.Samples > 0 ? t.Delivered + (delivered - t.Delivered) * DeliveredAlpha : delivered;

            t.LastFull = IsFullSample(delivered, pending, rate);
            if (dt > 0f) {
                float a = 1f - (float)Math.Exp(-dt / FullWindowSeconds);
                t.FullShare += ((t.LastFull ? 1f : 0f) - t.FullShare) * a;
            }

            if (quality >= 0f) {
                t.Quality = t.Quality >= 0f ? t.Quality + (quality - t.Quality) * QualityAlpha : quality;
            }

            if (pingMs > 0f) {
                t.Ping = t.Ping > 0f ? t.Ping + (pingMs - t.Ping) * PingAlpha : pingMs;
                t.PingBest.Add(now, pingMs);
            }

            t.Samples++;
            t.SamplesSinceChange++;
            if (t.QualityRef < 0f && t.Quality >= 0f && t.Samples >= RefSamples) {
                t.QualityRef = t.Quality;
                t.QualityRefAt = now;
            }
        }

        /// <summary>Whether one sample shows a full link: delivering close to its rate with a
        /// queue behind it, or - with no queue figure - very close to its rate. Pure.</summary>
        internal static bool IsFullSample(float delivered, int pending, int rate) {
            if (rate <= 0) { return false; }
            if (pending < 0) { return delivered >= FullRateShareNoQueue * rate; }
            return delivered >= FullRateShare * rate && pending >= FullPendingBytes;
        }

        /// <summary>Median of the list, which it sorts. Pure apart from that.</summary>
        internal static float Median(List<float> values) {
            if (values.Count == 0) { return 0f; }
            values.Sort();
            int mid = values.Count / 2;
            return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) * 0.5f;
        }

        /// <summary>The rate in force on this link changed. A step up is measured against what the
        /// link delivered just before it; a step down keeps the reference it had, which was taken
        /// before the trouble, so a line still congested at the lower rate goes on voting.</summary>
        internal static void NoteRateChange(LinkTrack t, float now, bool up) {
            if (up && t.Quality >= 0f) {
                t.QualityRef = t.Quality;
                t.QualityRefAt = now;
            }
            t.SamplesSinceChange = 0;
        }

        /// <summary>A long quiet stretch with no change: take the current level as the reference,
        /// so a slow drift is not voted on forever.</summary>
        internal static void RefreshReference(LinkTrack t, float now, bool congested) {
            if (congested || t.Quality < 0f || t.QualityRef < 0f) { return; }
            if (now - t.QualityRefAt < RefRefreshSeconds) { return; }
            t.QualityRef = t.Quality;
            t.QualityRefAt = now;
        }

        /// <summary>A new connection for the same player: its figures start over, its ping
        /// baseline is kept.</summary>
        internal static void Reconnected(LinkTrack t) {
            t.LastSampleAt = float.NegativeInfinity;
            t.Samples = 0;
            t.SamplesSinceChange = 0;
            t.FullShare = 0f;
            t.LastFull = false;
            t.Quality = -1f;
            t.QualityRef = -1f;
            t.QualityRefAt = float.NegativeInfinity;
            t.Delivered = 0f;
            t.Bps = 0f;
            t.Ping = -1f;
            t.PingBest.Restart();
        }

        internal static bool IsFull(LinkTrack t) => t.FullShare >= FullShareNeeded;

        internal static bool Settled(LinkTrack t) =>
            t.SamplesSinceChange >= MinVoteSamples && t.Quality >= 0f && t.QualityRef >= 0f;

        /// <summary>Delivering at least LossDrop less than before the last change, and not
        /// delivering nearly everything anyway. Pure.</summary>
        internal static bool LossVote(LinkTrack t) =>
            Settled(t) && t.Quality <= t.QualityRef - LossDrop && t.Quality < LossVoteBelow;

        /// <summary>Ping above its own best by more than max(RttRiseMs, half the best). Pure.</summary>
        internal static bool RttVote(LinkTrack t) =>
            t.Baseline > 0f && t.Ping > 0f && t.SamplesSinceChange >= MinVoteSamples
            && t.Ping > t.Baseline + Math.Max(RttRiseMs, RttRiseShare * t.Baseline);

        // -- the vote ----------------------------------------------------------------------

        /// <summary>Whether the line itself is congested. Pure.</summary>
        internal static VoteResult Vote(IList<LinkVote> links) {
            VoteResult r = default;
            for (int i = 0; i < links.Count; i++) {
                LinkVote v = links[i];
                if (!v.Eligible) { continue; }
                r.Eligible++;
                if (!v.Loss && !v.Rtt) { continue; }
                r.Voters++;
                if (!v.Full) { r.NonFullVoter = true; }
            }
            r.Quorum = Math.Max(2, (r.Eligible + 1) / 2);
            r.Shared = r.Eligible >= 2 && r.Voters >= r.Quorum && (r.NonFullVoter || r.Voters == r.Eligible);
            return r;
        }

        // -- what the line carries ---------------------------------------------------------

        /// <summary>
        /// Feeds one evaluation's delivered total into the peak. Ten-second averages; one above
        /// the committed peak waits PeakCommitSeconds with no congestion before it counts, because
        /// the delivery figure lags (Steam's quality carries loss for some seconds), and a window
        /// that was in fact congested must not be committed as clean.
        /// </summary>
        internal static void UpdatePeak(RateLimits l, float now, float total, bool usable, bool congested) {
            if (!usable || congested) {
                l.WindowSum = 0f;
                l.WindowCount = 0;
                l.WindowStart = now;
                if (congested) {
                    l.CandidatePeak = 0f;
                    l.CandidateAt = float.NegativeInfinity;
                }
                return;
            }

            if (l.WindowStart == float.NegativeInfinity) { l.WindowStart = now; }
            l.WindowSum += Finite(total);
            l.WindowCount++;
            if (now - l.WindowStart >= PeakWindowSeconds && l.WindowCount > 0) {
                float avg = l.WindowSum / l.WindowCount;
                if (avg > l.CommittedPeak && avg > l.CandidatePeak) {
                    l.CandidatePeak = avg;
                    l.CandidateAt = now;
                }
                l.WindowSum = 0f;
                l.WindowCount = 0;
                l.WindowStart = now;
            }

            if (l.CandidatePeak > 0f && now - l.CandidateAt >= PeakCommitSeconds && now - l.LastCongestionAt >= PeakCommitSeconds) {
                float peak = l.CandidatePeak;
                if (l.Ceiling > 0f && peak > l.Ceiling) { peak = l.Ceiling; }
                if (peak > l.CommittedPeak) { l.CommittedPeak = peak; }
                l.CandidatePeak = 0f;
                l.CandidateAt = float.NegativeInfinity;
            }
        }

        /// <summary>
        /// The line was congested at this delivered total: a ceiling at CeilingShare of it, held
        /// CeilingHold. A re-probe that runs into much the same ceiling again doubles the hold (up
        /// to MaxCeilingHoldSeconds); anything else starts it over.
        /// </summary>
        internal static void LearnCeiling(RateLimits l, float now, float total) {
            float learned = CeilingShare * Finite(total);
            if (l.Reprobing && l.ReprobeFrom > 0f && learned >= l.ReprobeFrom * (1f - ReprobeNearShare)) {
                l.CeilingHold = Math.Min(MaxCeilingHoldSeconds, l.CeilingHold * 2f);
            } else {
                l.CeilingHold = CeilingHoldSeconds;
            }
            l.Ceiling = learned;
            l.CeilingUntil = now + l.CeilingHold;
            l.Reprobing = false;
            l.ReprobeFrom = -1f;
            l.LastCongestionAt = now;
            l.CandidatePeak = 0f;
            l.CandidateAt = float.NegativeInfinity;
            if (l.CommittedPeak > learned) { l.CommittedPeak = learned; }
        }

        /// <summary>The total this direction may be planned against: the learned ceiling, and the
        /// admin's figure less headroom, whichever is lower. +inf when neither is known. Pure.</summary>
        internal static float LimitTotal(RateLimits l, float adminLimit) {
            float limit = float.PositiveInfinity;
            if (adminLimit > 0f) { limit = AdminShare * adminLimit; }
            if (l.Ceiling > 0f && l.Ceiling < limit) { limit = l.Ceiling; }
            return limit;
        }

        /// <summary>Each of n players' share of a known limit, within [BaseRate, max]. max when no
        /// limit is known. Pure.</summary>
        internal static int PerPlayerLimit(float limitTotal, int n, int max) {
            if (float.IsPositiveInfinity(limitTotal) || n <= 0) { return max; }
            double share = limitTotal / n;
            return (int)Math.Max(BaseRate, Math.Min(max, Math.Round(share)));
        }

        // -- the server's sends: one rate for everyone -------------------------------------

        /// <summary>
        /// One evaluation of the global send rate, once a second:
        ///
        ///   * the line congested for CongestionEvaluations in a row -> a quarter off (never below
        ///     BaseRate) and a ceiling learned from what it was delivering; a second one no sooner
        ///     than DownGapSeconds after;
        ///   * the rate over a known limit's per-player share - somebody joined, a ceiling was
        ///     learned, the admin set one -> down to that share at once;
        ///   * the host struggling -> a quarter off, at most every OverloadGapSeconds;
        ///   * the last step up bought nothing within NoGainCheckSeconds -> undone, and held
        ///     (see NoGainShare);
        ///   * somebody full, the host healthy, nothing congested for UpQuietSeconds and no change
        ///     for UpGapSeconds -> a quarter more, as far as the per-player limit and the
        ///     projected budget allow (BudgetHeadroom x CommittedPeak, with full links at the new
        ///     rate and the rest at what they send now). A ceiling whose hold has run out is
        ///     raised by ReprobeFactor when it is what stands in the way.
        /// </summary>
        internal static RateDecision StepOut(OutState s, RateInputs input, IList<LinkVote> links) {
            float now = input.Now;
            RateLimits l = s.Limits;
            int max = Math.Max(BaseRate, input.Max);

            RateDecision d = default;
            d.From = s.Rate;
            d.Rate = s.Rate;
            d.Vote = Vote(links);
            d.Total = TotalDelivered(links);

            UpdatePeak(l, now, d.Total, input.Usable, input.Usable && d.Vote.Shared);
            if (!input.Usable) {
                l.Votes = 0;
                return d;
            }

            d.Busy = IsBusy(l, links, d.Total);
            l.Votes = d.Vote.Shared && d.Busy ? l.Votes + 1 : 0;
            int n = links.Count;
            d.LimitTotal = LimitTotal(l, input.AdminLimit);
            d.PerPlayer = PerPlayerLimit(d.LimitTotal, n, max);

            if (l.Votes >= CongestionEvaluations && now - l.LastCongestionAt >= DownGapSeconds) {
                l.Votes = 0;
                LearnCeiling(l, now, d.Total);
                d.LearnedCeiling = true;
                d.LimitTotal = LimitTotal(l, input.AdminLimit);
                d.PerPlayer = PerPlayerLimit(d.LimitTotal, n, max);
                int down = Math.Max(BaseRate, (int)Math.Round(s.Rate * DownFactor));
                return Change(s, ref d, down, RateAction.Down, now);
            }

            if (s.Rate > d.PerPlayer) {
                return Change(s, ref d, d.PerPlayer, RateAction.Limit, now);
            }

            if (input.HostOverloaded && s.Rate > BaseRate && now - s.LastChangeAt >= OverloadGapSeconds) {
                int down = Math.Max(BaseRate, (int)Math.Round(s.Rate * DownFactor));
                return Change(s, ref d, down, RateAction.Overload, now);
            }

            if (s.UpCheckAt > float.NegativeInfinity && now >= s.UpCheckAt) {
                s.UpCheckAt = float.NegativeInfinity;
                float meanNow = MeanFullDelivered(links);
                if (meanNow > 0f && s.Rate > s.PreviousRate) {
                    if (meanNow < s.UpFullMeanBefore * NoGainShare) {
                        s.NoGainUntil = now + s.NoGainHold;
                        s.NoGainHold = Math.Min(MaxCeilingHoldSeconds, s.NoGainHold * 2f);
                        return Change(s, ref d, Math.Max(BaseRate, s.PreviousRate), RateAction.NoGain, now);
                    }
                    s.NoGainHold = NoGainHoldSeconds;
                }
            }

            if (!input.HostHealthy || s.Rate >= max) { return d; }
            if (now - s.LastChangeAt < UpGapSeconds || now - l.LastCongestionAt < UpQuietSeconds) { return d; }
            if (now < s.NoGainUntil) { return d; }

            int full = 0;
            float others = 0f;
            for (int i = 0; i < links.Count; i++) {
                LinkVote v = links[i];
                if (v.Full && !v.Held) { full++; } else { others += Finite(v.Bps); }
            }
            if (full == 0) { return d; }

            int next = Math.Min(max, (int)Math.Round(s.Rate * UpFactor));
            if (next > d.PerPlayer && l.Ceiling > 0f && now >= l.CeilingUntil && d.LimitTotal >= l.Ceiling) {
                // The learned ceiling is what stands in the way and its hold is over: look again.
                l.ReprobeFrom = l.Ceiling;
                l.Ceiling *= ReprobeFactor;
                l.CeilingUntil = now + l.CeilingHold;
                l.Reprobing = true;
                d.LimitTotal = LimitTotal(l, input.AdminLimit);
                d.PerPlayer = PerPlayerLimit(d.LimitTotal, n, max);
            }
            next = Math.Min(next, d.PerPlayer);

            float budget = BudgetHeadroom * l.CommittedPeak;
            if (others + (float)full * next > budget) {
                next = (int)Math.Floor((budget - others) / full);
            }
            if (next < s.Rate * MinStepGain) { return d; }

            s.UpFullMeanBefore = MeanFullDelivered(links);
            s.UpCheckAt = now + NoGainCheckSeconds;
            return Change(s, ref d, next, RateAction.Up, now);
        }

        /// <summary>What the links asking for more deliver on average, bytes/sec; 0 when none
        /// is. Pure.</summary>
        internal static float MeanFullDelivered(IList<LinkVote> links) {
            float sum = 0f;
            int n = 0;
            for (int i = 0; i < links.Count; i++) {
                if (!links[i].Full || links[i].Held) { continue; }
                sum += Finite(links[i].Delivered);
                n++;
            }
            return n > 0 ? sum / n : 0f;
        }

        private static RateDecision Change(OutState s, ref RateDecision d, int rate, RateAction action, float now) {
            if (rate == s.Rate) { return d; }
            if (action == RateAction.Up) {
                s.PreviousRate = s.Rate;
                s.LastUpAt = now;
            }
            s.Rate = rate;
            s.LastChangeAt = now;
            d.Action = action;
            d.Rate = rate;
            return d;
        }

        /// <summary>Whether a congested line could be this rate's doing: somebody is full, and
        /// the line carries at least BusyPeakShare of its committed peak (or none is committed
        /// yet). Pure.</summary>
        internal static bool IsBusy(RateLimits l, IList<LinkVote> links, float total) {
            bool full = false;
            for (int i = 0; i < links.Count; i++) {
                if (links[i].Full && !links[i].Held) { full = true; break; }
            }
            if (!full) { return false; }
            return l.CommittedPeak <= 0f || total >= BusyPeakShare * l.CommittedPeak;
        }

        internal static float TotalDelivered(IList<LinkVote> links) {
            float total = 0f;
            for (int i = 0; i < links.Count; i++) { total += Finite(links[i].Delivered); }
            return total;
        }

        // -- players' sends: one grant each ------------------------------------------------

        /// <summary>
        /// One evaluation of every player's upload grant, once a second. links and grants are
        /// parallel; changes is filled with what moved (the caller clears it).
        ///
        ///   * the host's receiving line congested -> every raised grant a quarter down, a
        ///     receive ceiling learned;
        ///   * a grant over its per-player limit (a known receive limit's share, or that
        ///     player's own ceiling) -> down to it;
        ///   * a grant raised in the last PostStepSeconds whose link then lost packets or
        ///     queued -> back to where it was, and that becomes the player's ceiling (see
        ///     SetPlayerCeiling);
        ///   * delivering under GrantDownQuality for GrantDownSeconds -> a quarter down, and the
        ///     new grant becomes their ceiling;
        ///   * otherwise at most one player a time steps up: full, delivering at least
        ///     GrantUpQuality for GrantUpSeconds, unchanged for UpGapSeconds, within the budget -
        ///     the one with the lowest grant first.
        /// </summary>
        internal static void StepIn(InState s, RateInputs input, IList<LinkVote> links, IList<GrantState> grants, List<GrantChange> changes) {
            float now = input.Now;
            RateLimits l = s.Limits;
            int max = Math.Max(BaseRate, input.Max);

            VoteResult vote = Vote(links);
            float total = TotalDelivered(links);
            UpdatePeak(l, now, total, input.Usable, input.Usable && vote.Shared);
            if (!input.Usable) {
                l.Votes = 0;
                return;
            }

            l.Votes = vote.Shared && IsBusy(l, links, total) ? l.Votes + 1 : 0;
            int n = links.Count;
            float limitTotal = LimitTotal(l, input.AdminLimit);

            if (l.Votes >= CongestionEvaluations && now - l.LastCongestionAt >= DownGapSeconds) {
                l.Votes = 0;
                LearnCeiling(l, now, total);
                for (int i = 0; i < grants.Count; i++) {
                    GrantState g = grants[i];
                    if (g.Grant <= BaseRate) { continue; }
                    SetGrant(g, i, Math.Max(BaseRate, (int)Math.Round(g.Grant * DownFactor)), "receive congested", now, changes);
                }
                return;
            }

            int perPlayer = PerPlayerLimit(limitTotal, n, max);
            for (int i = 0; i < grants.Count; i++) {
                GrantState g = grants[i];
                LinkVote v = links[i];

                if (v.Quality >= 0f) {
                    if (v.Quality < GrantDownQuality) {
                        if (g.LowSince == float.NegativeInfinity) { g.LowSince = now; }
                    } else {
                        g.LowSince = float.NegativeInfinity;
                    }
                    if (v.Quality >= GrantUpQuality) {
                        if (g.CleanSince == float.NegativeInfinity) { g.CleanSince = now; }
                    } else {
                        g.CleanSince = float.NegativeInfinity;
                    }
                }
                if (g.Ceiling > 0 && now >= g.CeilingUntil) { g.Ceiling = 0; }

                int cap = CapFor(g, perPlayer);
                if (g.Grant > cap) {
                    SetGrant(g, i, cap, "limit", now, changes);
                } else if (g.Grant > BaseRate && now - g.LastUpAt <= PostStepSeconds && (v.Loss || v.Rtt)) {
                    int back = Math.Max(BaseRate, g.Previous);
                    SetPlayerCeiling(g, back, now);
                    SetGrant(g, i, back, v.Loss ? "lost packets after a raise" : "ping rose after a raise", now, changes);
                } else if (g.Grant > BaseRate && g.LowSince > float.NegativeInfinity && now - g.LowSince >= GrantDownSeconds) {
                    int down = Math.Max(BaseRate, (int)Math.Round(g.Grant * DownFactor));
                    SetPlayerCeiling(g, down, now);
                    g.LowSince = now;
                    SetGrant(g, i, down, "losing packets", now, changes);
                }
            }

            if (!input.HostHealthy) { return; }
            if (now - l.LastCongestionAt < UpQuietSeconds) { return; }

            int pick = -1;
            for (int i = 0; i < grants.Count; i++) {
                GrantState g = grants[i];
                LinkVote v = links[i];
                if (!v.Full || v.Held) { continue; }
                if (g.CleanSince == float.NegativeInfinity || now - g.CleanSince < GrantUpSeconds) { continue; }
                if (now - g.LastChangeAt < UpGapSeconds) { continue; }
                if (g.Grant >= CapFor(g, perPlayer)) { continue; }
                if (pick < 0 || g.Grant < grants[pick].Grant) { pick = i; }
            }
            if (pick < 0) { return; }

            GrantState chosen = grants[pick];
            int next = Math.Min(CapFor(chosen, perPlayer), (int)Math.Round(chosen.Grant * UpFactor));
            float others = 0f;
            for (int i = 0; i < grants.Count; i++) {
                if (i == pick) { continue; }
                others += links[i].Full ? grants[i].Grant : Finite(links[i].Bps);
            }
            float budget = BudgetHeadroom * l.CommittedPeak;
            if (others + next > budget) { next = (int)Math.Floor(budget - others); }
            if (next < chosen.Grant * MinStepGain) { return; }

            chosen.Previous = chosen.Grant;
            chosen.LastUpAt = now;
            s.LastUpAt = now;
            SetGrant(chosen, pick, next, "full and clean", now, changes);
        }

        /// <summary>
        /// A rate this player's link did not carry. Held PlayerCeilingSeconds, doubled (up to
        /// MaxCeilingHoldSeconds) each time a probe fails again at or under the last one - a
        /// player whose uplink loses packets at the game's own rate is otherwise probed every
        /// ten minutes for the whole session (the 2026-10-07 replay).
        /// </summary>
        internal static void SetPlayerCeiling(GrantState g, int ceiling, float now) {
            g.CeilingHold = g.LastCeiling > 0 && ceiling <= g.LastCeiling
                ? Math.Min(MaxCeilingHoldSeconds, g.CeilingHold * 2f)
                : PlayerCeilingSeconds;
            g.Ceiling = ceiling;
            g.LastCeiling = ceiling;
            g.CeilingUntil = now + g.CeilingHold;
        }

        private static int CapFor(GrantState g, int perPlayer) {
            return g.Ceiling > 0 ? Math.Max(BaseRate, Math.Min(perPlayer, g.Ceiling)) : perPlayer;
        }

        /// <summary>Whether StepIn could still raise this grant: the link delivers cleanly enough
        /// to be raised (a lossy one is held where it is), and the grant is under its cap - the
        /// per-player share and the player's own ceiling. Says nothing about how soon. Pure.</summary>
        internal static bool GrantMayRise(GrantState g, LinkTrack t, int perPlayer) {
            if (t.Quality < GrantUpQuality) { return false; }
            return g.Grant < CapFor(g, perPlayer);
        }

        private static void SetGrant(GrantState g, int index, int grant, string reason, float now, List<GrantChange> changes) {
            if (grant == g.Grant) { return; }
            changes?.Add(new GrantChange { Index = index, From = g.Grant, To = grant, Reason = reason });
            g.Grant = grant;
            g.LastChangeAt = now;
        }

        // -- M26 under a moving global rate ------------------------------------------------

        /// <summary>
        /// What a per-player back-off override becomes when the global rate changes: an override
        /// is a rate that player's connection was shown to need, so it stays where it is when the
        /// global rate rises, and when the global rate falls to or below it there is nothing left
        /// to hold - 0, meaning clear it and inherit. Pure.
        /// </summary>
        internal static int OverrideAfterGlobalChange(int overrideBytes, int globalBytes) {
            if (overrideBytes <= 0 || globalBytes <= 0) { return 0; }
            return overrideBytes >= globalBytes ? 0 : overrideBytes;
        }

        /// <summary>One step back up from an override: a third more, never past the global rate.
        /// A step that would reach a ceiling the player is under (a rate they lost packets at;
        /// 0 = none) is not taken, and the override itself comes back. The global rate coming
        /// back means clear. Pure.</summary>
        internal static int StepUpOverride(int overrideBytes, int globalBytes, int ceilingBytes) {
            int next = (int)Math.Round(overrideBytes / DownFactor);
            if (next > globalBytes) { next = globalBytes; }
            if (ceilingBytes > 0 && next >= ceilingBytes) { return overrideBytes; }
            return next;
        }

        private static float Finite(float value) {
            return float.IsNaN(value) || float.IsInfinity(value) || value < 0f ? 0f : value;
        }
    }
}
