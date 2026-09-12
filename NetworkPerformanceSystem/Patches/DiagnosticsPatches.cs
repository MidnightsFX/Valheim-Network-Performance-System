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
        private static void SampleSendAttempt(ZDOMan.ZDOPeer peer, bool flush) {
            if (!NetworkStats.Collecting) { return; }
            NetworkStats.RecordSendAttempt(peer, flush);
        }
    }
}
