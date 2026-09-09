using Steamworks;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M8 - the send-rate bounds and Nagle timer underneath everything else this mod does.
    ///
    /// ZSteamSocket.RegisterGlobalCallbacks pins SendRateMin and SendRateMax to the SAME value,
    /// 153600 B/s, at Global scope. Those are the bounds Steam's per-connection bandwidth
    /// estimator is allowed to move between, so setting them equal does not merely cap the link -
    /// it clamps the estimator shut. Vanilla Valheim runs with no working congestion control and a
    /// flat 150 KB/s ceiling, and that ceiling sits UNDER the send window this mod resizes: M2 can
    /// hand a distant peer a 64 KB window all it likes, the transport still meters at 150 KB/s and
    /// the surplus turns into exactly the standing queue M2 exists to avoid.
    ///
    /// So the two directions are not symmetric and are not offered as if they were:
    ///   * raising MAX gives the estimator somewhere to climb to during a burst. It is a ceiling,
    ///     not a target - nothing pushes traffic into it - and it is the setting that makes the
    ///     upper half of Send Window's own Target Rate KBps range reachable at all.
    ///   * lowering MIN lets the estimator back down for a peer whose downlink genuinely cannot
    ///     take 150 KB/s, which vanilla forbids.
    ///   * raising MIN is the one move that is always wrong - it forbids backing off and converts
    ///     congestion into buffering and loss. BetterNetworking couples Min to Max so you cannot
    ///     raise one without the other; here the config range simply stops at vanilla, so the
    ///     harmful direction is not representable.
    ///
    /// Nagle is the cheap half. Steam holds a small reliable message for NagleTime microseconds to
    /// coalesce it with the next one - 5000us by default, and vanilla never changes it. That is up
    /// to 5ms in each direction on every update. With M2b servicing each peer once per send
    /// interval there is almost nothing left for Nagle to coalesce anyway, so the 5ms is close to
    /// pure cost, which is why 0 is the default here.
    ///
    /// All of it is Global scope, so it applies to every socket this process opens, in both
    /// directions - a client running the mod tunes its own uplink to the host the same way.
    /// </summary>
    internal static class SteamTransport {

        /// <summary>What vanilla writes into both SendRateMin and SendRateMax, in bytes/sec.</summary>
        internal const int VanillaSendRateBytesPerSec = 153600;

        /// <summary>Steam's own NagleTime default, which vanilla leaves alone. Microseconds.</summary>
        private const int VanillaNagleMicros = 5000;

        private const int BytesPerKB = 1024;

        /// <summary>Last value we successfully put into SendRateMax, or vanilla's when we have not
        /// changed it. Read by the coupling check and by nps_stats.</summary>
        internal static int EffectiveSendRateMaxBytesPerSec { get; private set; } = VanillaSendRateBytesPerSec;

        /// <summary>The readback from the last apply, for nps_stats. Null until we have run.</summary>
        internal static string LastReadback { get; private set; }

        /// <summary>Set once RegisterGlobalCallbacks has run at least once, so a config edit that
        /// arrives before Steam is up defers instead of writing into nothing.</summary>
        private static bool _steamUp;

        /// <summary>Suppresses repeats of the target-rate coupling warning: it is a config mistake,
        /// so it should be said clearly once per distinct pairing rather than on every re-apply.</summary>
        private static int _lastWarnedPairing = -1;

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
        }

        private static void Apply(string reason) {
            if (!PatchGuard.IsActive(Mechanism.SteamTransport)) { return; }
            if (!ValConfig.EnableSteamTransportTuning.Value) { return; }
            if (!SteamNetConfig.Available) { return; }                       // reports itself once, then goes quiet

            int wantMax = ValConfig.SteamSendRateMaxKBps.Value * BytesPerKB;
            int wantMin = ValConfig.SteamSendRateMinKBps.Value * BytesPerKB;
            int wantNagle = ValConfig.SteamNagleMicros.Value;

            int beforeMax = ReadOr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, VanillaSendRateBytesPerSec);
            int beforeMin = ReadOr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, VanillaSendRateBytesPerSec);
            int beforeNagle = ReadOr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_NagleTime, VanillaNagleMicros);

            // 0 means "leave whatever is there" for the rate bounds, so an operator who has not
            // opted in keeps vanilla's transport exactly. Nagle has no such sentinel - 0 is a
            // meaningful value for it - so it is always written.
            if (wantMax > 0) {
                SteamNetConfig.TryWrite(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, wantMax);
            }
            if (wantMin > 0) {
                SteamNetConfig.TryWrite(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, wantMin);
            }
            SteamNetConfig.TryWrite(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_NagleTime, wantNagle);

            int afterMax = ReadOr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, beforeMax);
            int afterMin = ReadOr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, beforeMin);
            int afterNagle = ReadOr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_NagleTime, beforeNagle);

            EffectiveSendRateMaxBytesPerSec = afterMax;

            // Read back rather than trust the write. A silently ignored SetConfigValue would
            // otherwise look identical to a working one, which for a mechanism whose entire job is
            // to lift a ceiling is the worst possible failure mode.
            LastReadback =
                $"SendRateMax {Kb(beforeMax)} -> {Kb(afterMax)}, " +
                $"SendRateMin {Kb(beforeMin)} -> {Kb(afterMin)}, " +
                $"Nagle {beforeNagle} -> {afterNagle}us";

            Logger.LogInfo($"Steam transport ({SteamNetConfig.InterfaceName}, {reason}): {LastReadback}" +
                           (afterNagle == 0 ? " [Nagle off]" : ""));

            WarnIfWindowOutrunsTransport(afterMax);
        }

        /// <summary>
        /// The one config mistake this mechanism exists to make impossible to miss: a Send Window
        /// target rate above what the transport will actually pass. Before this module there was
        /// no way to raise the ceiling, so the upper end of that setting's own range was dead - and
        /// silently so, because the window really does open, the bytes really are queued, and the
        /// only symptom is the bufferbloat the mod is supposed to be preventing.
        /// </summary>
        private static void WarnIfWindowOutrunsTransport(int effectiveMaxBytesPerSec) {
            if (!ValConfig.EnableSendWindowSizing.Value) { return; }

            int targetKBps = ValConfig.SendWindowTargetRateKBps.Value;
            int capKBps = effectiveMaxBytesPerSec / BytesPerKB;
            if (targetKBps <= capKBps) {
                _lastWarnedPairing = -1;
                return;
            }

            // One warning per distinct (target, cap) pairing: repeating it on every re-apply would
            // bury the readback line it is attached to.
            int pairing = (targetKBps * 10000) + capKBps;
            if (pairing == _lastWarnedPairing) { return; }
            _lastWarnedPairing = pairing;

            Logger.LogWarning(
                $"Send Window 'Target Rate KBps' is {targetKBps} but Steam will not pass more than {capKBps} KB/s " +
                $"per connection, so windows are being sized for throughput the transport cannot deliver and the " +
                $"difference becomes queueing delay. Either raise 'Steam Transport / Send Rate Max KBps' to at " +
                $"least {targetKBps}, or lower the target to {capKBps}.");
        }

        private static int ReadOr(ESteamNetworkingConfigValue key, int fallback) {
            return SteamNetConfig.TryRead(key, out int value) ? value : fallback;
        }

        private static string Kb(int bytesPerSec) {
            return $"{bytesPerSec / BytesPerKB} KB/s";
        }
    }
}
