using HarmonyLib;
using NetworkPerformanceSystem.Runtime;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M3 - replaces vanilla's ownership pass with a latency-aware one.
    ///
    /// Patches ReleaseZDOS (the per-pass driver) rather than ReleaseNearbyZDOS (which vanilla
    /// calls once per candidate and which therefore only ever sees one candidate at a time). One
    /// pass with full visibility is both cheaper - the sector scan is deduped instead of repeated
    /// per peer - and the only way to compare candidates against each other at all.
    ///
    /// It also means we replace the existing arbiter rather than running a second one beside it.
    /// Bolting on a parallel scanner leaves two systems assigning ownership on similar cadences,
    /// which is a race by construction.
    /// </summary>
    [HarmonyPatch]
    internal static class OwnershipPatches {

        /// <summary>Vanilla's pass interval. Kept as-is: ownership hysteresis is measured in
        /// seconds, so there is nothing to gain from arbitrating more often.</summary>
        private const float PassIntervalSeconds = 2f;

        [HarmonyPatch(typeof(ZDOMan), "ReleaseZDOS")]
        [HarmonyPrefix]
        private static bool ArbitrateByLatency(ZDOMan __instance, float dt) {
            if (!PatchGuard.IsActive(Mechanism.Ownership) || !ValConfig.EnableOwnershipArbitration.Value) {
                // Vanilla's pass is about to run instead. With monitoring on, say so, or every
                // owner it moves reads as something gameplay did - and this is exactly the arm an
                // on/off comparison needs labelled.
                if (Monitoring.Active) { Monitoring.NoteVanillaPass(); }
                return true;
            }

            // Vanilla's ReleaseZDOS is already host-only in practice (ZDOMan.Update gates it),
            // but be explicit: a client must never reassign ownership on anyone's behalf.
            if (!NpsEnv.IsHost()) { return true; }

            __instance.m_releaseZDOTimer += dt;
            if (__instance.m_releaseZDOTimer <= PassIntervalSeconds) { return false; }
            __instance.m_releaseZDOTimer = 0f;

            OwnershipArbiter.RunPass(__instance);
            return false;
        }

        /// <summary>StopAll rather than Shutdown: it is the common tail of both Shutdown and
        /// ShutdownWithoutSave, and it is idempotent (m_haveStoped), so this fires exactly once
        /// per session end whichever entry point was used.</summary>
        [HarmonyPatch(typeof(ZNet), "StopAll")]
        [HarmonyPostfix]
        private static void OnStopAll() {
            // First: it closes its files with a record of the session ending, and takes its hooks
            // off before anything below resets the state those hooks read.
            Monitoring.Shutdown();
            OwnershipArbiter.Reset();
            OwnershipPolicy.Reset();
            ShipHelmOwnership.Reset();
            SendWindow.Reset();
            SendQueueView.Reset();
            SendSchedulerPatches.Reset();
            RoutedRpcFilter.Reset();
            RpcOwnerRouter.Reset();
            OwnerRevisionGuard.Reset();
            // Diagnostics are session-scoped too: an nps_stats_collect left running must not
            // silently keep sampling on the next server, and telemetry must not carry over.
            NetworkStats.Reset();
            NpsExtrapolate.Reset();
            SteamTransport.Reset();
            SyncListCache.Reset();
            ConnectionTimeout.Reset();
        }
    }
}
