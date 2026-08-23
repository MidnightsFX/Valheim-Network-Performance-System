using BepInEx;
using BepInEx.Configuration;
using Jotunn.Entities;
using Jotunn.Managers;
using Jotunn.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

#pragma warning disable IDE0130
namespace NetworkPerformanceSystem {
#pragma warning restore IDE0130
    internal class ValConfig {
        public static ConfigFile cfg;
        
        // Add Client sided config entries under here
        public static ConfigEntry<bool> EnableDebugMode;

        // M4 - latency compensation. Client-local on purpose: this changes what the player sees,
        // and the right strength depends on personal tolerance for extrapolation overshoot.
        public static ConfigEntry<bool> EnableLatencyCompensation;
        public static ConfigEntry<float> LatencyCompensationStrength;
        public static ConfigEntry<float> LatencyCompensationMaxMeters;
        public static ConfigEntry<bool> EnableDebugOverlay;

        // Add Server synced config entries under here

        // M2/M2c - bandwidth-delay-product send window
        public static ConfigEntry<bool> EnableSendWindowSizing;
        public static ConfigEntry<int> SendWindowTargetRateKBps;
        public static ConfigEntry<float> SendWindowBdpFactor;
        public static ConfigEntry<int> SendWindowMaxBytes;

        // M2b - send scheduler
        public static ConfigEntry<bool> EnableSchedulerFix;
        public static ConfigEntry<float> SendIntervalSeconds;
        public static ConfigEntry<float> SendSchedulerFrameBudgetMs;

        // M3 - latency-aware ownership arbitration
        public static ConfigEntry<bool> EnableOwnershipArbitration;
        public static ConfigEntry<bool> OwnershipAllowHostOwner;
        public static ConfigEntry<float> OwnershipMinHoldSeconds;
        public static ConfigEntry<int> OwnershipChallengeMarginMs;
        public static ConfigEntry<int> OwnershipMaxReassignsPerPass;
        public static ConfigEntry<float> OwnershipLoadPenaltyMs;
        public static ConfigEntry<int> OwnershipUnmeasuredRttMs;

        // M6 - fast reference position channel
        public static ConfigEntry<bool> EnableFastRefPos;
        public static ConfigEntry<float> RefPosSendHz;
        public static ConfigEntry<float> RefPosMinMoveDistance;

        // M7 - routed RPC relay filter
        public static ConfigEntry<bool> EnableRoutedRpcFilter;

        public const string cfgFolder = "NetworkPerformanceSystem";

        public ValConfig(ConfigFile cf) {
            // ensure all the config values are created
            cfg = cf;
            // Deferred until every entry is bound: Awake calls SaveOnSet(true) afterwards, which
            // flushes the whole file once instead of once per Bind.
            cfg.SaveOnConfigSet = false;
            CreateConfigValues(cf);
            Logger.SetDebugLogging(EnableDebugMode.Value);
        }

        public static void SaveOnSet(bool enabled) {
            cfg.SaveOnConfigSet = enabled;
            cfg.Save();
        }

        private void CreateConfigValues(ConfigFile Config) {
            // Debugmode
            EnableDebugMode = Config.Bind("Client config", "EnableDebugMode", false,
                new ConfigDescription("Enables Debug logging.",
                null,
                new ConfigurationManagerAttributes { IsAdvanced = true }));
            EnableDebugMode.SettingChanged += Logger.EnableDebugLogging;
            Logger.CheckEnableDebugLogging();

            // --- M4: latency-compensated extrapolation (client-local) ----------------------
            // ZSyncTransform already extrapolates by velocity * m_targetPosTimer, but that timer
            // starts at zero the instant a packet arrives - even though the data in it is already
            // one full owner->viewer path old. Seeding the timer with that path latency is the
            // difference between rendering where the entity was and where it is.
            EnableLatencyCompensation = Config.Bind("Client config", "EnableLatencyCompensation", true,
                new ConfigDescription("Render entities owned by other players at their estimated current position rather than their last-received one. Requires the server to be running this mod; without it this setting has no effect and behaviour is exactly vanilla."));
            LatencyCompensationStrength = Config.Bind("Client config", "LatencyCompensationStrength", 1f,
                new ConfigDescription("How much of the measured path latency to correct for. 1.0 corrects fully. Lower values trade accuracy for less overshoot when things stop abruptly. 0 disables the correction while leaving the patch in place.",
                new AcceptableValueRange<float>(0f, 1f)));
            LatencyCompensationMaxMeters = Config.Bind("Client config", "LatencyCompensationMaxMeters", 3f,
                new ConfigDescription("Hard cap on the total extrapolated displacement (the game's own gap extrapolation plus this correction). The correction only ever uses whatever headroom remains under the cap, so fast objects such as projectiles stay below the 5m threshold at which the game gives up smoothing and teleports them.",
                new AcceptableValueRange<float>(0.5f, 4.5f)));
            EnableDebugOverlay = Config.Bind("Client config", "EnableDebugOverlay", false,
                new ConfigDescription("Show the per-entity latency compensation overlay (owner, estimated staleness, applied displacement).", null,
                new ConfigurationManagerAttributes { IsAdvanced = true }));

            // --- M2/M2c: bandwidth-delay-product send window -------------------------------
            // Vanilla allows a fixed 10240 bytes of in-flight reliable ZDO data per peer.
            // Throughput through a fixed window is window/RTT, so vanilla is correctly sized
            // up to ~67ms RTT and starves every peer beyond it (~41 KB/s at 250ms) regardless
            // of their actual connection. Sizing by RTT is a no-op for local players by
            // construction, which is the point - a big static window instead adds standing
            // queue delay to the peers that were already fine.
            EnableSendWindowSizing = BindServerConfig("Send Window", "Enable BDP Window Sizing", true,
                "Size each peer's in-flight ZDO window from their measured round-trip time instead of vanilla's fixed 10240 bytes. Low-latency peers are unaffected; distant peers stop being throttled by their distance.");
            SendWindowTargetRateKBps = BindServerConfig("Send Window", "Target Rate KBps", 150,
                "Per-peer ZDO throughput to size the window for, in kilobytes/sec. 150 matches the rate Valheim pins its Steam sockets to, so the default asks for exactly what the transport already allows.", false, 32, 1024);
            SendWindowBdpFactor = BindServerConfig("Send Window", "BDP Factor", 1.25f,
                "Multiplier on the bandwidth-delay product. 1.0 allows roughly one round-trip of buffering. Raising this adds throughput headroom at the cost of standing queue delay (bufferbloat).", false, 1f, 3f);
            SendWindowMaxBytes = BindServerConfig("Send Window", "Max Window Bytes", 65536,
                "Upper bound on the computed window. Prevents a peer with a pathological ping reading from being handed an unbounded buffer. The lower bound is always vanilla's 10240 and is not configurable.", true, 10240, 262144);

            // --- M2b: send scheduler -------------------------------------------------------
            // Vanilla SendZDOToPeers2 services one peer per rendered frame, and the frame that
            // starts a round sends to nobody. A round therefore costs N+1 frames: at 10 players
            // and 60fps the advertised 20Hz becomes ~5.5Hz, adding up to ~180ms of staleness
            // that has nothing to do with the network.
            EnableSchedulerFix = BindServerConfig("Send Scheduler", "Enable Scheduler Fix", true,
                "Send to every peer each tick instead of one peer per rendered frame. Without this the effective per-peer send rate degrades linearly with player count.");
            SendIntervalSeconds = BindServerConfig("Send Scheduler", "Send Interval Seconds", 0.05f,
                "Seconds between ZDO send rounds. Vanilla is 0.05 (20Hz).", true, 0.02f, 0.2f);
            // Vanilla's one-peer-per-frame is accidentally self-limiting on CPU; servicing every
            // peer per interval is not, and on a large server the send path can eat the whole
            // frame. This is the wall-clock ceiling per frame: the per-peer rate becomes
            // min(1/interval, budget/cost) instead of the frame time growing without bound.
            SendSchedulerFrameBudgetMs = BindServerConfig("Send Scheduler", "Frame Budget Ms", 4f,
                "Maximum milliseconds per frame the host spends sending ZDOs to peers. Peers are serviced in round-robin order until the budget runs out and the remainder is owed to the next frame, so nobody is starved. On a busy server this is what keeps the frame time bounded: the effective per-peer send rate becomes min(1/interval, budget/cost) - raise it to trade server frame time for send rate, lower it on a CPU-constrained host. nps_stats shows the effective rate and how often the budget is hit.", true, 0.5f, 16f);

            // --- M3: latency-aware ownership arbitration -----------------------------------
            // Valheim is distributed-authority: whichever peer owns a ZDO simulates it, and
            // everyone else sees it via owner->host->viewer. Vanilla grants ownership to the
            // first peer in list order, which is uncorrelated with both engagement and latency.
            EnableOwnershipArbitration = BindServerConfig("Ownership", "Enable Latency-Aware Ownership", true,
                "Assign ZDO ownership to minimise how stale the object looks to the players who can actually see it, instead of vanilla's first-peer-wins ordering.");
            OwnershipAllowHostOwner = BindServerConfig("Ownership", "Allow Host As Owner", true,
                "Let the host compete for ownership of contested ZDOs in the zones it has loaded. The host is zero hops from everyone, so host-owned is the lowest possible staleness for every viewer, and whenever two or more players share a zone the host has loaded it will win those objects and keep them. A listen host has loaded the zones around its own player; a dedicated server has loaded only the zones around the world origin, so in practice this means a dedicated server owns and simulates the contested objects at the spawn hub whenever players gather there - intended, and worth knowing when budgeting server CPU. Disable to always place on the lowest-latency player present instead.");
            OwnershipMinHoldSeconds = BindServerConfig("Ownership", "Min Hold Seconds", 5f,
                "Minimum time an owner keeps a ZDO before it can be challenged. Hysteresis against ownership thrash. Applies only when moving a ZDO away from an owner that is still present - a ZDO whose owner has left the area or the session is re-owned immediately, at any setting.", false, 0f, 60f);
            OwnershipChallengeMarginMs = BindServerConfig("Ownership", "Challenge Margin Ms", 25,
                "A challenger must improve estimated staleness by at least this many milliseconds to take ownership. Prevents ping jitter from ping-ponging ownership between similar peers. Applies only to challenges against a present owner, and also bounds how much the Load Penalty below may shift a decision.", false, 0, 250);
            OwnershipMaxReassignsPerPass = BindServerConfig("Ownership", "Max Reassigns Per Pass", 8,
                "Minimum cap on latency-driven ownership transfers per arbitration pass. Each transfer costs a ZDO resend, so this bounds the burst when a group arrives in a new area. The effective cap is the larger of this value and the number of connected players, so a full server converges at the same per-player rate as a small group rather than linearly slower. Restoring an owner to a ZDO that has none is never deferred by this: an unowned creature does not move and cannot be damaged.", true, 1, 128);
            OwnershipLoadPenaltyMs = BindServerConfig("Ownership", "Load Penalty Ms", 0.02f,
                "Cost added per simulated object (creatures, ships - not walls or trees) a candidate already owns nearby, in milliseconds. Spreads simulation and upload load across peers instead of concentrating every contested object on the lowest-ping player. The total handicap is capped at half of Challenge Margin Ms, so load can shade a close decision but can never on its own amount to the staleness difference that justifies a transfer. 0 disables load spreading and places purely by staleness.", true, 0f, 0.5f);
            // Crossplay/PlayFab sockets never report a round-trip time and a Steam peer has none
            // for its first second or two. Scoring those at 0ms handed them every contested object
            // in range; this is what they are scored at instead. High enough to lose to any
            // measured peer with a normal ping, irrelevant when the peer is the only viewer
            // (which it wins at any RTT).
            OwnershipUnmeasuredRttMs = BindServerConfig("Ownership", "Unmeasured Peer RTT Ms", 150,
                "Round-trip time assumed for a peer the host has no measurement for - crossplay/PlayFab connections never report one, and every Steam peer is unmeasured for its first seconds. Such a peer still wins objects only it can see, but loses contested ones to any measured peer with a lower ping. Treating unmeasured as 0ms instead would hand them everything in range.", true, 0, 1000);

            // --- M6: fast reference position channel ---------------------------------------
            // ZNet.SendPeriodicData gates client reference positions behind a single 2 second
            // timer shared with SendNetTime/SendPlayerList. The server uses that position for
            // both the interest set and ownership arbitration, so it can be arbitrating against
            // a position a whole 64m zone out of date.
            EnableFastRefPos = BindServerConfig("Reference Position", "Enable Fast Reference Position", true,
                "Send a lightweight 12-byte position update on a fast timer so the server arbitrates ownership and interest against live positions rather than up-to-2-second-old ones. Vanilla's 2 second path is left intact as a fallback.");
            RefPosSendHz = BindServerConfig("Reference Position", "Send Hz", 5f,
                "How many times per second a moving client reports its position. At 12 bytes per update this costs about 60 bytes/sec.", false, 1f, 20f);
            RefPosMinMoveDistance = BindServerConfig("Reference Position", "Min Move Distance", 0.5f,
                "Skip the update when the player has moved less than this many metres since the last one. A standing player sends nothing.", false, 0f, 5f);

            // --- M7: routed RPC relay filter -----------------------------------------------
            // Every "broadcast" RPC - footsteps, animation triggers, damage numbers, destroy
            // notices, building damage - is sent once to the host and relayed by the host to
            // every other player, so its cost is events x players. A receiver discards a
            // ZDO-targeted one unless it has that object loaded, which only happens inside its
            // own active area; the host knows exactly which peers that is and can stop relaying
            // to the rest. On a full server this is most of the relay traffic, and it all counts
            // against the same per-peer send window as ZDO updates.
            EnableRoutedRpcFilter = BindServerConfig("Routed RPC", "Enable Relay Filtering", true,
                "Relay broadcast RPCs (animation triggers, footsteps, damage numbers, object-destroyed notices, building damage and the like) only to the players that can actually use them, instead of to everyone on the server. A receiving client discards these unless it has the object loaded, so nothing visible changes; on a busy server this removes most of the host's relay traffic and stops it from crowding out ZDO updates. Global messages (chat, pings, events, sleep, server messages) are never filtered.");
        }

        /// <summary>
        /// Helper to bind configs for float types
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="category"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="advanced"></param>
        /// <param name="valMin"></param>
        /// <param name="valMax"></param>
        /// <returns></returns>
        public static ConfigEntry<float[]> BindServerConfig(string category, string key, float[] value, string description, bool advanced = false, float valMin = 0, float valMax = 150) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description,
                new AcceptableValueRange<float>(valMin, valMax),
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        ///  Helper to bind configs for bool types
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="category"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="acceptableValues"></param>>
        /// <param name="advanced"></param>
        /// <returns></returns>
        public static ConfigEntry<bool> BindServerConfig(string category, string key, bool value, string description, AcceptableValueBase acceptableValues = null, bool advanced = false) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description,
                    acceptableValues,
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Helper to bind configs for int types
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="category"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="advanced"></param>
        /// <param name="valMin"></param>
        /// <param name="valMax"></param>
        /// <returns></returns>
        public static ConfigEntry<int> BindServerConfig(string category, string key, int value, string description, bool advanced = false, int valMin = 0, int valMax = 150) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description,
                new AcceptableValueRange<int>(valMin, valMax),
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Helper to bind configs for float types
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="category"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="advanced"></param>
        /// <param name="valMin"></param>
        /// <param name="valMax"></param>
        /// <returns></returns>
        public static ConfigEntry<float> BindServerConfig(string category, string key, float value, string description, bool advanced = false, float valMin = 0, float valMax = 150) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description,
                new AcceptableValueRange<float>(valMin, valMax),
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Helper to bind configs for strings
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="category"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="advanced"></param>
        /// <returns></returns>
        public static ConfigEntry<string> BindServerConfig(string category, string key, string value, string description, AcceptableValueList<string> acceptableValues = null, bool advanced = false) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(
                    description,
                    acceptableValues,
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }
    }
}
