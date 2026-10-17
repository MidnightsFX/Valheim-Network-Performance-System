using System;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M41's arithmetic, kept free of game types so it can be driven offline: pairing the host's
    /// pings with their answers, deciding which round trips are measurements, and turning those
    /// into the figures ownership and latency compensation use. GamePing feeds it; LatencyRegistry
    /// keeps the results.
    /// </summary>
    internal static class PingRules {

        /// <summary>A round trip longer than this is still a measurement - of a terrible link - and
        /// is counted at this value rather than thrown away, so such a link reads as bad instead of
        /// keeping its last good figure.</summary>
        internal const int MaxPingMs = 2000;

        /// <summary>A figure with no sample for this long is not used: the player's answers have
        /// stopped, and whatever it said is no longer true.</summary>
        internal const double FreshSeconds = 10d;

        /// <summary>A player's figure is published, and used without the baseline, only once it
        /// rests on this many round trips.</summary>
        internal const int MinSamples = 3;

        /// <summary>A host frame this long or longer (a world save, a large garbage collection)
        /// is a stall: a round trip that spans one measures the stall, not the link.</summary>
        internal const double StallMs = 250d;

        /// <summary>
        /// The host's pings to one player, oldest first, waiting for their answers. The game's ping
        /// and its answer carry no sequence number, but both travel the same ordered, reliable
        /// stream, and every ping gets exactly one answer, so the next answer always belongs to
        /// the oldest ping still waiting. When pings back up past capacity (a link stalled for
        /// eight seconds) the oldest is dropped and its answer, which still comes first, is skipped,
        /// so the pairing stays right. An answer nobody asked for - another mod's - pairs with the
        /// wrong ping until the waiting ones are used up; on a link under a second that is the next
        /// answer, and Judge discards the impossible results in between.
        /// </summary>
        internal sealed class PingFifo {
            internal const int Capacity = 8;

            private readonly long[] _sent = new long[Capacity];
            private readonly bool[] _valid = new bool[Capacity];
            private readonly object[] _socket = new object[Capacity];
            private int _head;
            private int _count;

            /// <summary>Answers still to arrive for pings that were dropped from the front.</summary>
            internal int Skip;

            internal long Overflows;

            internal int Count => _count;

            internal void Push(long sentTicks, bool valid, object socket) {
                if (_count == Capacity) {
                    _socket[_head] = null;
                    _head = (_head + 1) % Capacity;
                    _count--;
                    Skip++;
                    Overflows++;
                }
                int slot = (_head + _count) % Capacity;
                _sent[slot] = sentTicks;
                _valid[slot] = valid;
                _socket[slot] = socket;
                _count++;
            }

            /// <summary>The ping this answer belongs to. False with skipped set when the answer is
            /// for a ping already dropped; false without it when no ping is waiting.</summary>
            internal bool TryPop(out long sentTicks, out bool valid, out object socket, out bool skipped) {
                sentTicks = 0L;
                valid = false;
                socket = null;
                skipped = false;
                if (Skip > 0) {
                    Skip--;
                    skipped = true;
                    return false;
                }
                if (_count == 0) { return false; }

                sentTicks = _sent[_head];
                valid = _valid[_head];
                socket = _socket[_head];
                _socket[_head] = null;
                _head = (_head + 1) % Capacity;
                _count--;
                return true;
            }

            internal void Clear() {
                Array.Clear(_socket, 0, Capacity);
                _head = 0;
                _count = 0;
                Skip = 0;
            }
        }

        /// <summary>What one answer turned out to be. Only Sample is a measurement.</summary>
        internal enum PingVerdict : byte {
            Sample,
            /// <summary>An answer with no ping waiting for it.</summary>
            Unmatched,
            /// <summary>The answer to a ping that was dropped from a full queue.</summary>
            Skipped,
            /// <summary>The ping went out before the player was ready (joining, or another mod
            /// holding the connection for its own handshake).</summary>
            NotValid,
            /// <summary>The round trip spans a stall of the host's own frames.</summary>
            SpansStall,
            /// <summary>The connection the ping went out on was replaced before the answer.</summary>
            SocketChanged,
            /// <summary>The player's game said it was in the background, where it may not run.</summary>
            InBackground,
            /// <summary>Far below what the transport itself measures - not this ping's answer.</summary>
            BelowTransport,
            /// <summary>Zero or negative: a clock fault.</summary>
            Implausible,
        }

        internal const int VerdictCount = 9;

        /// <summary>
        /// Whether an answer is a measurement, and of how many milliseconds.
        /// </summary>
        /// <param name="popped">A ping was waiting for this answer.</param>
        /// <param name="skipped">The answer belongs to a ping dropped from a full queue.</param>
        /// <param name="valid">The ping was sent once the player was ready.</param>
        /// <param name="lastStallEndTicks">When the host's last stall ended; a ping sent before
        /// that has the stall inside its round trip.</param>
        /// <param name="socketSame">The connection is the one the ping went out on.</param>
        /// <param name="transportMs">The transport's own figure for this link, when it has a fresh
        /// one (Steam); 0 otherwise.</param>
        internal static PingVerdict Judge(bool popped, bool skipped, bool valid, long sentTicks, long nowTicks, long ticksPerSecond,
                                          long lastStallEndTicks, bool socketSame, bool inBackground, float transportMs, out int rttMs) {
            rttMs = 0;
            if (skipped) { return PingVerdict.Skipped; }
            if (!popped) { return PingVerdict.Unmatched; }
            if (!valid) { return PingVerdict.NotValid; }
            if (!socketSame) { return PingVerdict.SocketChanged; }
            if (inBackground) { return PingVerdict.InBackground; }
            if (sentTicks < lastStallEndTicks) { return PingVerdict.SpansStall; }

            double ms = ticksPerSecond > 0 ? (nowTicks - sentTicks) * 1000d / ticksPerSecond : 0d;
            if (ms <= 0d || double.IsNaN(ms)) { return PingVerdict.Implausible; }

            // The game's round trip includes the transport's and adds frames and queues to it, so
            // it can come in under it by a little (the transport's figure is smoothed) but never
            // by half. One that does is the answer to a different ping.
            if (transportMs > 0f && ms < transportMs * 0.5d - 5d) { return PingVerdict.BelowTransport; }

            rttMs = (int)Math.Round(Math.Min(ms, MaxPingMs));
            if (rttMs < 1) { rttMs = 1; }
            return PingVerdict.Sample;
        }

        /// <summary>
        /// One player's in-game round trip, as the host measures it from the game's own once-a-second
        /// ping. The smoothed figure is what their updates are actually waiting on now - transport,
        /// queues on both sides, and a frame at each end - which is what latency compensation needs.
        /// The baseline is that figure at its best over the last five minutes, which is what
        /// ownership needs: see OwnershipFigure.
        /// </summary>
        internal sealed class PingEstimate {
            /// <summary>Weight of each new sample: settles in a few seconds at one sample a second.</summary>
            internal const float Alpha = 0.25f;

            internal float EwmaMs;
            internal float JitterMs;
            internal int LastMs;
            internal int MinMs = int.MaxValue;
            internal int Samples;
            internal double LastAt = double.NegativeInfinity;
            internal readonly PingBaseline Best = new PingBaseline();

            internal void Add(double now, int ms) {
                if (ms <= 0) { return; }
                if (Samples == 0) {
                    EwmaMs = ms;
                    JitterMs = 0f;
                } else {
                    JitterMs += (Math.Abs(ms - LastMs) - JitterMs) / 16f;
                    EwmaMs += (ms - EwmaMs) * Alpha;
                }
                LastMs = ms;
                if (ms < MinMs) { MinMs = ms; }
                Samples++;
                LastAt = now;
                Best.Add((float)now, ms);
            }

            internal bool IsFresh(double now) {
                return Samples > 0 && now - LastAt <= FreshSeconds;
            }

            internal float BaselineMs => Best.Value;
        }

        /// <summary>
        /// The figure the ownership pass prices a player at: the in-game baseline once there is
        /// one, the best single round trip before that, and the configured stand-in while there is
        /// nothing fresh to go on.
        ///
        /// Not the smoothed figure, on purpose. A player who runs a fight fills their own upload,
        /// and their answers then wait behind it; priced by the smoothed figure they would lose
        /// the creatures they run because they run them, and the next player would fill up in
        /// turn. The baseline is the lowest ten-second median of five minutes, which a queue does
        /// not move. The best single trip stands in for the first half minute, while the join's
        /// own download is still queued ahead of the pings.
        /// </summary>
        internal static float OwnershipFigure(PingEstimate game, double now, float unmeasuredMs) {
            if (game == null || !game.IsFresh(now) || game.Samples < MinSamples) { return unmeasuredMs; }
            if (game.BaselineMs > 0f) { return game.BaselineMs; }
            return game.MinMs;
        }

        /// <summary>The figure published for latency compensation: the smoothed round trip, once it
        /// rests on enough samples and is fresh; -1 when there is none to publish, which leaves
        /// the player out of the table (compensation then stays off for them, as before).</summary>
        internal static float PublishFigure(PingEstimate game, double now) {
            if (game == null || !game.IsFresh(now) || game.Samples < MinSamples) { return -1f; }
            return game.EwmaMs;
        }
    }
}
