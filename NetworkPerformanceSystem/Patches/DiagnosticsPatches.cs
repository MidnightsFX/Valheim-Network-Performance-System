using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Collections.Generic;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M5 - the nps_stats console command and the (opt-in) send-path sampling behind it. The
    /// report is open to anyone; the sampling switch is a cheat, because it is the half that
    /// costs something.
    /// </summary>
    [HarmonyPatch]
    internal static class DiagnosticsPatches {

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        [HarmonyPostfix]
        private static void RegisterCommands() {
            // Reading the report costs nothing - it prints counters that have already been
            // gathered - so it is open to anyone. Nothing it prints lets a player change what the
            // server is doing, and a player who can see their own RTT is a player who can tell you
            // something useful about a bad connection.
            new Terminal.ConsoleCommand("nps_stats",
                "Network performance: per-peer RTT, send window and backpressure. " +
                "Run 'nps_stats_collect' first to gather samples.",
                args => args.Context.AddString(NetworkStats.BuildReport()));

            // Turning sampling on is the half with a price - a Steam API call per peer per send
            // tick - so it belongs behind devcommands like any other switch that changes what the
            // game is doing. It is its own command rather than a subcommand because isCheat is a
            // per-command flag, and it is the flag the game's own help listing, autocomplete and
            // cheat tracking read; a hand-rolled check inside the action reaches none of those.
            new Terminal.ConsoleCommand("nps_stats_collect",
                "Start sampling the send path for 'nps_stats'. 'nps_stats_collect stop' ends it.",
                args => {
                    bool stop = args.Length > 1 && args[1].ToLowerInvariant() == "stop";
                    NetworkStats.SetCollecting(!stop);
                    args.Context.AddString(stop
                        ? "Stopped sampling."
                        : "Sampling the send path. Play for a while, then run 'nps_stats'.");
                },
                isCheat: true,
                optionsFetcher: () => new List<string> { "stop" });
        }

        /// <summary>
        /// Sampling hook. Kept behind NetworkStats.Collecting because reading GetSendQueueSize
        /// costs a Steam API call per peer per send tick - acceptable while measuring, wasteful
        /// the rest of the time. Notably this is the mistake SkadiNet makes unconditionally, and
        /// it does it per peer per *frame* rather than per send tick.
        /// </summary>
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.SendZDOs))]
        [HarmonyPrefix]
        private static void SampleSendAttempt(ZDOMan __instance, ZDOMan.ZDOPeer peer, bool flush, out int __state) {
            __state = -1;
            if (!NetworkStats.Collecting) { return; }
            // The game's own count of objects written, so the postfix can tell how many of this
            // send's list actually went out. UpdateStats zeroes it once a second, but only after
            // SendZDOToPeers2 has finished, so it cannot reset between the two halves.
            __state = __instance.m_zdosSent;
            NetworkStats.RecordSendAttempt(peer, flush);
        }

        /// <summary>
        /// The other half of the same sample: how much of the list this send got through. It must
        /// live in this class - Harmony hands __state only between a prefix and postfix declared
        /// together. Only a send that actually went out is read, because SendZDOs' backpressure
        /// returns leave m_tempToSync holding whichever peer was served before.
        /// </summary>
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.SendZDOs))]
        [HarmonyPostfix]
        private static void SampleSendResult(ZDOMan __instance, ZDOMan.ZDOPeer peer, bool flush, bool __result, int __state) {
            if (__state < 0 || flush || !__result || !NetworkStats.Collecting) { return; }
            NetworkStats.RecordSendResult(__instance, peer, __instance.m_zdosSent - __state);
        }
    }
}
