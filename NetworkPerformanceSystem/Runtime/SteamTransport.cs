using Steamworks;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M8 - the send rate and Nagle timer underneath everything else this mod does.
    ///
    /// ZSteamSocket.RegisterGlobalCallbacks writes 153600 B/s into both SendRateMin and SendRateMax
    /// at Global scope. It is tempting to read those as the bounds of a bandwidth estimator, and
    /// this module used to: raise the ceiling to let the estimate climb, lower the floor to let it
    /// back off. Steam's networking library has no such estimator. In GameNetworkingSockets
    /// (steamnetworkingsockets_snp.cpp) a connection's rate is assigned in exactly two places:
    ///   * SNP_InitializeConnection sets it once, at connect, to 4380 bytes / RTT (RFC 3390's
    ///     initial TCP rate), and
    ///   * SNP_ClampSendRate clamps it into [Min, Max] - pinning it to Min when Min == Max - every
    ///     time the connection is serviced.
    /// Nothing moves it for loss, delay or acknowledgements. So a connection is paced at a fixed
    /// max(Min, 4380 / RTT-at-connect), capped at Max. At vanilla's 150 KB/s floor that is simply
    /// Min for anyone more than ~28ms away: raising Max alone changes nothing for them, and
    /// lowering Min cuts their rate to 4380 / RTT (44 KB/s at 100ms).
    ///
    /// The only setting that moves throughput is the rate itself, so that is the one offered, and
    /// it is written to both bounds. There is no congestion control behind it - it is a fixed pace
    /// that never slows for a link that cannot keep up, which then shows up as loss and resends
    /// rather than as a slower stream. Hence the host-only write: the setting is server-synced and
    /// the config is process-global, so writing it everywhere would also pace every modded
    /// player's upload at whatever the admin chose for the host's.
    ///
    /// Nagle is the cheap half. Steam holds a small reliable message for NagleTime microseconds to
    /// coalesce it with the next one - 5000us by default, and vanilla never changes it. That is up
    /// to 5ms in each direction on every update. With M2b servicing each peer once per send
    /// interval there is almost nothing left for Nagle to coalesce anyway, so the 5ms is close to
    /// pure cost, which is why 0 is the default here. It is written on both sides.
    ///
    /// NoteObservedRate is the empirical check on all of the above: the Steam client ships its
    /// own build of this library, and if that build ever reports a per-connection rate other
    /// than the one pinned here, the log says so once.
    /// </summary>
    internal static class SteamTransport {

        /// <summary>What vanilla writes into both SendRateMin and SendRateMax, in bytes/sec.</summary>
        internal const int VanillaSendRateBytesPerSec = 153600;

        /// <summary>Steam's own NagleTime default, which vanilla leaves alone. Microseconds.</summary>
        internal const int VanillaNagleMicros = 5000;

        /// <summary>Below this a pinned rate starves every connection; Steam's own clamp stops at
        /// 1 KB/s, which is not a rate anyone means to set.</summary>
        internal const int MinSendRateKBps = 32;

        private const int BytesPerKB = 1024;

        /// <summary>The rate Steam paces every connection at, when Min and Max agree (they do
        /// unless another mod wrote one of them), from the last readback. 0 when they disagree or
        /// we have not read them yet - callers then fall back to vanilla's.</summary>
        internal static int PinnedSendRateBytesPerSec { get; private set; }

        /// <summary>The readback from the last apply, for nps_stats. Null until we have run.</summary>
        internal static int ReadbackMinBytesPerSec { get; private set; }
        internal static int ReadbackMaxBytesPerSec { get; private set; }
        internal static int ReadbackNagleMicros { get; private set; } = VanillaNagleMicros;
        internal static string LastReadback { get; private set; }

        /// <summary>Set once RegisterGlobalCallbacks has run at least once, so a config edit that
        /// arrives before Steam is up defers instead of writing into nothing.</summary>
        private static bool _steamUp;

        /// <summary>True while a rate we wrote is in Steam. Going back to 0, switching tuning off,
        /// or this process becoming a client must then put vanilla's value back: "0 leaves the
        /// game's rate" is only true if nothing of ours is still sitting there.</summary>
        private static bool _wroteRate;

        /// <summary>Suppresses repeats of the unequal-bounds warning. Apply runs on every Steam
        /// socket construction, so it has to be said once per distinct pairing, not per join.</summary>
        private static long _lastWarnedPairing = -1;

        /// <summary>Realtime of the last Apply, so a readback taken before Steam has re-clamped a
        /// connection is not mistaken for the library adapting the rate by itself.</summary>
        private static float _lastApplyRealtime;

        /// <summary>Consecutive samples per peer that disagreed with the pinned rate, and whether
        /// that has been reported. Reported once per process: one line is the finding.</summary>
        private static readonly Dictionary<long, int> MismatchStreak = new Dictionary<long, int>();
        private static bool _reportedMismatch;
        private const int MismatchSamplesToReport = 3;
        private const float MismatchSettleSeconds = 5f;

        /// <summary>Called from the RegisterGlobalCallbacks postfix - the same seam vanilla uses to
        /// write its own values, on both builds, before any socket exists.</summary>
        internal static void OnGlobalCallbacksRegistered() {
            _steamUp = true;
            Apply("RegisterGlobalCallbacks");
        }

        /// <summary>Called from the SettingChanged handlers. Steam config is process-global and
        /// re-writable at any time, so a live edit takes effect without a restart.</summary>
        internal static void OnConfigChanged() {
            if (!_steamUp) { return; }   // the postfix will apply the new values when Steam comes up
            Apply("config changed");
        }

        internal static void Reset() {
            _lastWarnedPairing = -1;
            MismatchStreak.Clear();
        }

        /// <summary>The configured rate in bytes/sec, or 0 for "the game's". Pure.</summary>
        internal static int WantedBytes(int kbps) {
            if (kbps <= 0) { return 0; }
            return Mathf.Max(kbps, MinSendRateKBps) * BytesPerKB;
        }

        /// <summary>The rate every connection is paced at, given the two bounds as read back -
        /// or 0 when they differ and the rate depends on each connection's ping at connect. Pure.</summary>
        internal static int PinnedFrom(int minBytesPerSec, int maxBytesPerSec) {
            return minBytesPerSec > 0 && minBytesPerSec == maxBytesPerSec ? minBytesPerSec : 0;
        }

        private static void Apply(string reason) {
            if (!PatchGuard.IsActive(Mechanism.SteamTransport)) { return; }
            if (!SteamNetConfig.Available) { return; }                       // reports itself once, then goes quiet

            bool tuning = ValConfig.EnableSteamTransportTuning.Value;

            // The rate is the host's to set, for its own sends. A client leaves its upload at the
            // game's pace whatever the server's config says.
            int wantRate = tuning && NpsEnv.IsHost() ? WantedBytes(ValConfig.SteamSendRateKBps.Value) : 0;
            int wantNagle = tuning ? ValConfig.SteamNagleMicros.Value : VanillaNagleMicros;

            int beforeMax = ReadOr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, VanillaSendRateBytesPerSec);
            int beforeMin = ReadOr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, VanillaSendRateBytesPerSec);
            int beforeNagle = ReadOr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_NagleTime, VanillaNagleMicros);

            if (wantRate > 0) {
                WriteRate(wantRate);
                _wroteRate = true;
            } else if (_wroteRate) {
                WriteRate(VanillaSendRateBytesPerSec);
                _wroteRate = false;
            }

            // Nagle has no "leave it" value - 0 is meaningful for it - so while tuning is on it is
            // always written, and switching tuning off puts the game's value back.
            if (tuning || beforeNagle != VanillaNagleMicros) {
                SteamNetConfig.TryWrite(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_NagleTime, wantNagle);
            }

            int afterMax = ReadOr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, beforeMax);
            int afterMin = ReadOr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, beforeMin);
            int afterNagle = ReadOr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_NagleTime, beforeNagle);

            ReadbackMinBytesPerSec = afterMin;
            ReadbackMaxBytesPerSec = afterMax;
            ReadbackNagleMicros = afterNagle;
            PinnedSendRateBytesPerSec = PinnedFrom(afterMin, afterMax);
            _lastApplyRealtime = Time.realtimeSinceStartup;
            MismatchStreak.Clear();

            // M26 holds some connections below the global rate, and a connection-scope value does
            // not follow a Global write: every live override is re-derived from the new rate here,
            // or removed if tuning just went off.
            LossBackoff.Reconcile(reason);

            // Read back rather than trust the write. A silently ignored SetConfigValue would
            // otherwise look identical to a working one.
            LastReadback =
                $"SendRateMin {Kb(beforeMin)} -> {Kb(afterMin)}, " +
                $"SendRateMax {Kb(beforeMax)} -> {Kb(afterMax)}, " +
                $"Nagle {beforeNagle} -> {afterNagle}us";

            Logger.LogInfo($"Steam transport ({SteamNetConfig.InterfaceName}, {reason}): {LastReadback}" +
                           (afterNagle == 0 ? " [Nagle off]" : ""));

            WarnIfUnpinned(afterMin, afterMax);
        }

        private static void WriteRate(int bytesPerSec) {
            // Max first when lowering and Min first when raising would keep Min <= Max at every
            // step, but Steam clamps Max up to Min itself (SNP_ClampSendRate), so the order cannot
            // leave a connection in a bad state and both are written back to back regardless.
            SteamNetConfig.TryWrite(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, bytesPerSec);
            SteamNetConfig.TryWrite(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, bytesPerSec);
        }

        /// <summary>
        /// Min and Max disagree - another mod has written one of them. That is the configuration
        /// this module used to recommend and it does not do what it looks like, so say what it
        /// actually does: each connection runs at max(Min, 4380 / its ping at connect), capped at
        /// Max, for as long as it stays connected.
        /// </summary>
        private static void WarnIfUnpinned(int minBytesPerSec, int maxBytesPerSec) {
            if (minBytesPerSec == maxBytesPerSec) {
                _lastWarnedPairing = -1;
                return;
            }

            long pairing = ((long)minBytesPerSec << 32) | (uint)maxBytesPerSec;
            if (pairing == _lastWarnedPairing) { return; }
            _lastWarnedPairing = pairing;

            float fastPingMs = minBytesPerSec > 0 ? 4380f * 1000f / minBytesPerSec : 0f;
            Logger.LogWarning(
                $"Steam's send-rate bounds are unequal (SendRateMin {Kb(minBytesPerSec)}, SendRateMax {Kb(maxBytesPerSec)}) - " +
                $"another mod has written one of them. Steam does not adapt a connection's rate between these bounds: each " +
                $"connection is paced at a fixed max(SendRateMin, 4380 bytes / its ping when it connected), capped at SendRateMax. " +
                $"For every player more than {fastPingMs:F0}ms away that is simply {Kb(minBytesPerSec)}. To change the rate, set " +
                $"'Steam Transport / Send Rate KBps', which writes both bounds.");
        }

        /// <summary>
        /// Called at the RTT sampling cadence with the rate Steam reports for one connection.
        /// Under the model above this always equals the pinned rate - or the lower one M26 has
        /// pinned that connection to - once Steam has re-clamped the connection after an edit. If
        /// it does not - steadily, after the edit has settled - the Steam client's library adapts
        /// rates after all, and that is worth one line in the log.
        /// </summary>
        internal static void NoteObservedRate(long peerUid, int observedBytesPerSec) {
            if (_reportedMismatch || peerUid == 0L || observedBytesPerSec <= 0) { return; }

            int pinned = PinnedSendRateBytesPerSec;
            if (pinned <= 0) { return; }
            if (Time.realtimeSinceStartup - _lastApplyRealtime < MismatchSettleSeconds) { return; }

            int expected = LossBackoff.ExpectedRate(peerUid, pinned, out float sinceChange);
            if (sinceChange < MismatchSettleSeconds) { return; }             // Steam has not re-clamped that connection yet

            if (observedBytesPerSec == expected) {
                MismatchStreak.Remove(peerUid);
                return;
            }

            MismatchStreak.TryGetValue(peerUid, out int streak);
            MismatchStreak[peerUid] = ++streak;
            if (streak < MismatchSamplesToReport) { return; }

            _reportedMismatch = true;
            Logger.LogWarning(
                $"Steam reports a send rate of {Kb(observedBytesPerSec)} for a connection whose rate should be {Kb(expected)}. " +
                $"This build of Steam's networking library adjusts rates by itself, which this mod does not expect. Send windows " +
                $"follow the reported rate, so nothing breaks; please mention this line in a bug report. Not repeated this session.");
        }

        internal static void ForgetPeer(long peerUid) {
            MismatchStreak.Remove(peerUid);
        }

        private static int ReadOr(ESteamNetworkingConfigValue key, int fallback) {
            return SteamNetConfig.TryRead(key, out int value) ? value : fallback;
        }

        private static string Kb(int bytesPerSec) {
            return $"{bytesPerSec / BytesPerKB} KB/s";
        }
    }
}
