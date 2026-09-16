using HarmonyLib;
using Jotunn.Entities;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M13 - keeps Jotunn's CustomRPC send-queue limit above the M2 window ceiling.
    ///
    /// Jotunn's CustomRPC (its own config sync, and every mod that sends data through it) waits,
    /// before each send, for the peer's socket send queue to fall to at most MaximumSendQueueSize
    /// (20000 bytes), and after Timeout (30s) of waiting invokes "Error" on the peer and
    /// disconnects it. That threshold was sized against vanilla, which never lets the queue past
    /// ~10240 bytes plus one ZDO. On a Steam socket GetSendQueueSize includes bytes already on the
    /// wire but not yet acked, so the M2 window IS the standing queue for a peer with a backlog -
    /// and past ~104ms RTT at the default rate the window is above 20000. The wait then never
    /// ends, and 30 seconds later Jotunn hangs up on a connection that was working (issue #3).
    /// Both sides: a client sizes its own upstream window the same way, and the report was a
    /// client's Jotunn disconnecting the server.
    ///
    /// The field is a plain private static (not const) that the wait loop re-reads on every
    /// iteration, so it is simply written to sit above whatever M2 can hand out, with Jotunn's own
    /// original margin carried on top for routed RPCs, periodic data and the one-ZDO overshoot.
    /// Its Timeout is left alone: a queue that stays above window + headroom for 30 seconds is a
    /// socket that is genuinely stuck, which is exactly what Jotunn's check is for.
    ///
    /// The wait loop itself is a compiler-generated nested iterator, which is why this is a
    /// reflection write rather than a Harmony patch. Jotunn is a hard dependency, so the type is
    /// always present; the field name is what could change between Jotunn versions, and a miss
    /// stands the mechanism down loudly rather than throwing out of Awake.
    /// </summary>
    internal static class JotunnSendQueue {

        /// <summary>Jotunn's own margin above vanilla's window, carried whole on top of the M2
        /// ceiling. Not configurable: the value is derived from Max Window Bytes, and the only
        /// thing a knob could add is a way to put the limit back underneath the window.</summary>
        internal const int HeadroomBytes = 20000;

        private const string FieldName = "MaximumSendQueueSize";

        private static FieldInfo _field;
        private static int _original;
        private static bool _started;
        private static int _lastLogged = -1;

        /// <summary>What Jotunn is enforcing now, for nps_stats.</summary>
        internal static int EffectiveLimit { get; private set; }

        /// <summary>The value Jotunn shipped with, read from the field rather than assumed.</summary>
        internal static int JotunnDefault => _original;

        internal static bool Active => PatchGuard.IsActive(Mechanism.JotunnQueueLimit) && _field != null;

        /// <summary>
        /// Called once from Awake, after Harmony has run, so the SendWindow mechanism's state is
        /// final by the time the first value is derived from it. Never throws.
        /// </summary>
        internal static void OnStartup() {
            try {
                Type customRpc = typeof(CustomRPC);

                // Make sure Jotunn's static initialiser has run before we write, so it cannot run
                // later and put 20000 back over the top of our value.
                RuntimeHelpers.RunClassConstructor(customRpc.TypeHandle);

                FieldInfo field = AccessTools.Field(customRpc, FieldName);
                if (field == null || !field.IsStatic || field.FieldType != typeof(int)) {
                    StandDown(customRpc, field == null ? "is missing" : "is not a static int");
                    return;
                }

                int original = (int)field.GetValue(null);
                if (original <= 0) {
                    StandDown(customRpc, $"reads {original}, which is not a usable limit");
                    return;
                }

                _field = field;
                _original = original;
                EffectiveLimit = original;
                _started = true;
                Apply("startup");
            } catch (Exception e) {
                _field = null;
                PatchGuard.Disable(Mechanism.JotunnQueueLimit,
                    $"could not reach Jotunn.Entities.CustomRPC.{FieldName}: {e.GetType().Name}: {e.Message}. " +
                    "Jotunn's own 20000-byte send queue limit stays in force; Jotunn CustomRPC senders may time " +
                    "out against a BDP-sized window past ~100ms RTT.");
            }
        }

        private static void StandDown(Type customRpc, string what) {
            _field = null;
            PatchGuard.Disable(Mechanism.JotunnQueueLimit,
                $"Jotunn.Entities.CustomRPC.{FieldName} {what} in the installed Jotunn " +
                $"({customRpc.Assembly.GetName().Version}). Jotunn's own 20000-byte send queue limit stays in " +
                "force; Jotunn CustomRPC senders may time out against a BDP-sized window past ~100ms RTT.");
        }

        /// <summary>
        /// Called from the SettingChanged handlers for the two Send Window entries the limit is
        /// derived from - including the edit Jotunn makes on a client when the server pushes its
        /// own values down at join time. A change that arrives before Awake has finished patching
        /// is deferred to the startup apply, which sees the final mechanism state.
        /// </summary>
        internal static void OnConfigChanged() {
            if (!_started) { return; }
            Apply("config changed");
        }

        /// <summary>Puts Jotunn's own value back. Only used when the plugin is torn down.</summary>
        internal static void Restore() {
            if (_field == null) { return; }
            try {
                _field.SetValue(null, _original);
                EffectiveLimit = _original;
            } catch (Exception e) {
                Logger.LogWarning($"Could not restore Jotunn CustomRPC send queue limit: {e.Message}");
            }
        }

        private static bool WindowSizingOn() {
            return PatchGuard.IsActive(Mechanism.SendWindow)
                   && ValConfig.EnableSendWindowSizing != null
                   && ValConfig.EnableSendWindowSizing.Value;
        }

        /// <summary>
        /// The M2 ceiling plus headroom while windows are being sized, Jotunn's own value when
        /// they are not - so switching the window off live puts Jotunn back exactly as it was.
        /// Never below Jotunn's original: this only ever opens the limit.
        /// </summary>
        private static int Desired() {
            if (!WindowSizingOn()) { return _original; }
            int configured = ValConfig.SendWindowMaxBytes != null
                ? ValConfig.SendWindowMaxBytes.Value
                : SendWindow.VanillaWindowBytes;
            int ceiling = Math.Max(SendWindow.VanillaWindowBytes, configured);
            return Math.Max(_original, ceiling + HeadroomBytes);
        }

        /// <summary>
        /// Writes the field and reads it back, like ConnectionTimeout does with ZRpc's timeout:
        /// there is nothing here to refuse the write, but the readback is what would show a
        /// silently ignored one.
        /// </summary>
        private static void Apply(string reason) {
            if (!Active) { return; }

            try {
                int before = (int)_field.GetValue(null);
                int want = Desired();
                _field.SetValue(null, want);
                EffectiveLimit = (int)_field.GetValue(null);

                if (EffectiveLimit == _lastLogged) { return; }
                _lastLogged = EffectiveLimit;

                string derivation = WindowSizingOn()
                    ? $"Max Window Bytes {ValConfig.SendWindowMaxBytes.Value} + {HeadroomBytes} headroom"
                    : "send window sizing is off; Jotunn's own value";
                Logger.LogInfo($"Jotunn CustomRPC send queue limit ({reason}): {before} -> {EffectiveLimit} bytes ({derivation})");
            } catch (Exception e) {
                _field = null;
                PatchGuard.Disable(Mechanism.JotunnQueueLimit,
                    $"writing Jotunn.Entities.CustomRPC.{FieldName} failed: {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
