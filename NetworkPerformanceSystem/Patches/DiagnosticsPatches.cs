using HarmonyLib;
using NetworkPerformanceSystem.Runtime;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M5 - the nps_stats console command and the (opt-in) send-path sampling behind it.
    /// </summary>
    [HarmonyPatch]
    internal static class DiagnosticsPatches {

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        [HarmonyPostfix]
        private static void RegisterCommands() {
            new Terminal.ConsoleCommand("nps_stats",
                "Network performance: per-peer RTT, send window and backpressure. " +
                "'nps_stats collect' starts sampling the send path, 'nps_stats stop' ends it.",
                args => {
                    // Admin-gated rather than hidden behind devcommands. Sampling the send path
                    // costs a Steam API call per peer per tick, so it is not something any player
                    // on a server should be able to switch on. A solo player or host always passes;
                    // a client only when it is on the server's admin list, which the server syncs.
                    if (ZNet.instance == null || !ZNet.instance.LocalPlayerIsAdminOrHost()) {
                        args.Context.AddString("'nps_stats' requires admin.");
                        return;
                    }

                    string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";

                    switch (sub) {
                        case "collect":
                            NetworkStats.SetCollecting(true);
                            args.Context.AddString("Sampling the send path. Play for a while, then run 'nps_stats'.");
                            return;
                        case "stop":
                            NetworkStats.SetCollecting(false);
                            args.Context.AddString("Stopped sampling.");
                            return;
                        default:
                            args.Context.AddString(NetworkStats.BuildReport());
                            return;
                    }
                });
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
