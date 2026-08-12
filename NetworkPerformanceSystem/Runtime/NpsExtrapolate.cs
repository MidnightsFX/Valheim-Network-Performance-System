using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M4 - corrects rendered positions for the time the data spent in flight.
    ///
    /// ZSyncTransform already extrapolates: it advances a non-owned entity by
    /// `velocity * m_targetPosTimer`, where the timer counts up from the last packet. But the
    /// timer is reset to zero the moment a packet arrives - and at that instant the contents are
    /// already one full owner-&gt;host-&gt;viewer path old. So vanilla compensates for the gap
    /// *between* packets while leaving the transit latency itself permanently uncorrected. At
    /// 250ms that is a fixed offset of roughly 135ms, and it is exactly why you swing at where a
    /// creature used to be.
    ///
    /// (There is a second, unrelated vanilla quirk here: m_targetPosTimer resets on any
    /// DataRevision change, including health and animation writes that say nothing about
    /// movement. That makes the gap compensation intermittently too small. Our correction is a
    /// constant transit offset and is unaffected by it either way.)
    /// </summary>
    internal static class NpsExtrapolate {

        /// <summary>Vanilla's extrapolation ceiling. We stay under it rather than extending it -
        /// the goal is to use the existing budget correctly, not to predict further.</summary>
        private const float MaxExtrapolationSeconds = 2f;

        // Tuning telemetry. This mechanism is the only one whose failure mode is visual rather
        // than numeric, so it needs to be observable while picking a strength.
        internal static int SamplesThisSecond;
        internal static float MeanStalenessMs;
        internal static float MeanDisplacement;
        internal static float MaxDisplacement;
        internal static int ClampHits;

        private static float _stalenessAccum;
        private static float _displacementAccum;
        private static int _accumCount;
        private static float _windowStart;

        /// <summary>
        /// Replaces the `velocity * m_targetPosTimer` multiply inside ZSyncTransform.SyncPosition.
        ///
        /// Returns exactly the vanilla product whenever compensation is unavailable or disabled,
        /// so the patch is inert rather than merely "off" - a client with no latency table from
        /// the host behaves bit-identically to stock.
        /// </summary>
        internal static Vector3 Offset(Vector3 velocity, float rawTimer, ZDO zdo) {
            Vector3 baseline = velocity * rawTimer;

            if (!PatchGuard.IsActive(Mechanism.Extrapolation)) { return baseline; }
            if (!ValConfig.EnableLatencyCompensation.Value) { return baseline; }
            if (zdo == null) { return baseline; }

            float strength = ValConfig.LatencyCompensationStrength.Value;
            if (strength <= 0f) { return baseline; }

            float staleness = LatencyRegistry.PathStalenessSeconds(zdo.GetOwner()) * strength;
            if (staleness <= 0f) { return baseline; }

            // Respect vanilla's overall extrapolation ceiling: the correction competes for the
            // same 2 second budget rather than being added on top of it.
            float total = Mathf.Min(rawTimer + staleness, MaxExtrapolationSeconds);
            float added = total - rawTimer;
            if (added <= 0f) { return baseline; }

            Vector3 extra = velocity * added;

            // Clamp displacement, not just time. The extrapolated position feeds vanilla's 5m
            // snap test, and a fast projectile at ~40m/s would cross that in 135ms and start
            // teleporting. Slow movers never come near this limit.
            float maxMeters = ValConfig.LatencyCompensationMaxMeters.Value;
            float sqrMax = maxMeters * maxMeters;
            if (extra.sqrMagnitude > sqrMax) {
                extra = extra.normalized * maxMeters;
                ClampHits++;
            }

            Record(staleness, extra.magnitude);
            return baseline + extra;
        }

        private static void Record(float stalenessSeconds, float displacement) {
            _stalenessAccum += stalenessSeconds * 1000f;
            _displacementAccum += displacement;
            _accumCount++;
            if (displacement > MaxDisplacement) { MaxDisplacement = displacement; }

            float now = Time.realtimeSinceStartup;
            if (now - _windowStart < 1f) { return; }

            SamplesThisSecond = _accumCount;
            MeanStalenessMs = _accumCount > 0 ? _stalenessAccum / _accumCount : 0f;
            MeanDisplacement = _accumCount > 0 ? _displacementAccum / _accumCount : 0f;

            _windowStart = now;
            _accumCount = 0;
            _stalenessAccum = 0f;
            _displacementAccum = 0f;
            MaxDisplacement = 0f;
        }

        internal static void Reset() {
            SamplesThisSecond = 0;
            MeanStalenessMs = 0f;
            MeanDisplacement = 0f;
            MaxDisplacement = 0f;
            ClampHits = 0;
            _accumCount = 0;
            _stalenessAccum = 0f;
            _displacementAccum = 0f;
        }
    }
}
