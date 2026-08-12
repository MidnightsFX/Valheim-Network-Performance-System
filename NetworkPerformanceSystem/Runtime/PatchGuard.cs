using BepInEx.Bootstrap;
using System.Collections.Generic;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// The mechanisms this mod can apply. Each one verifies its own IL anchor at patch time and
    /// can stand down independently, so a game update or an unexpected mod interaction disables
    /// one feature rather than taking the whole plugin with it.
    /// </summary>
    internal enum Mechanism {
        SendWindow,     // M2/M2c - ZDOMan.SendZDOs BDP window
        SendScheduler,  // M2b    - ZDOMan.SendZDOToPeers2 round-robin fix
        Ownership,      // M3     - ZDOMan.ReleaseNearbyZDOS arbitration
        RefPos,         // M6     - Nps.RefPos fast reference position channel
        Extrapolation,  // M4     - ZSyncTransform.SyncPosition latency compensation
    }

    /// <summary>
    /// Tracks which mechanisms are live. A mechanism starts enabled and is switched off the
    /// moment its anchor check fails or a mod that already does its job is detected.
    /// </summary>
    internal static class PatchGuard {

        /// <summary>ReturnToSender redirects ZDOMan.Update away from SendZDOToPeers2, so our
        /// scheduler prefix would never fire. It already does the same job - stand down rather
        /// than ship dead code.</summary>
        internal const string ReturnToSenderGUID = "redseiko.valheim.returntosender";

        private static readonly HashSet<Mechanism> Disabled = new HashSet<Mechanism>();
        private static readonly Dictionary<Mechanism, string> DisableReasons = new Dictionary<Mechanism, string>();

        internal static bool IsActive(Mechanism mechanism) => !Disabled.Contains(mechanism);

        /// <summary>
        /// Switch a mechanism off for the rest of the session. Logged at warning level because a
        /// silently missing feature is the worst possible failure mode for a performance mod -
        /// the user would otherwise believe it is working.
        /// </summary>
        internal static void Disable(Mechanism mechanism, string reason) {
            if (Disabled.Add(mechanism)) {
                DisableReasons[mechanism] = reason;
                Logger.LogWarning($"{mechanism} disabled: {reason}");
            }
        }

        internal static string GetDisableReason(Mechanism mechanism) {
            return DisableReasons.TryGetValue(mechanism, out string reason) ? reason : null;
        }

        internal static bool IsPluginLoaded(string guid) {
            return Chainloader.PluginInfos != null && Chainloader.PluginInfos.ContainsKey(guid);
        }

        /// <summary>
        /// Called once after Harmony has run. Anchor failures have already reported themselves
        /// from inside their transpilers by this point; this just summarises the outcome so the
        /// log makes it obvious what is actually running.
        /// </summary>
        internal static void VerifyAfterPatching() {
            List<string> live = new List<string>();
            foreach (Mechanism mechanism in System.Enum.GetValues(typeof(Mechanism))) {
                if (IsActive(mechanism)) { live.Add(mechanism.ToString()); }
            }

            if (live.Count == 0) {
                Logger.LogError("No mechanisms are active - the mod is loaded but doing nothing. See the warnings above.");
                return;
            }

            Logger.LogInfo($"Active mechanisms: {string.Join(", ", live.ToArray())}");
            if (Disabled.Count > 0) {
                foreach (KeyValuePair<Mechanism, string> entry in DisableReasons) {
                    Logger.LogInfo($"  inactive - {entry.Key}: {entry.Value}");
                }
            }
        }
    }
}
