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

        // M22 - client ghost watchdog. Client-local because it decides when to take THIS player
        // out of a dead session, which is not the server's call to make - and on a server that has
        // already stopped answering, could not be pushed down anyway.
        public static ConfigEntry<bool> EnableGhostWatchdog;

        // Network monitoring, the player's half. Client-local because it is the player's say over
        // what their own game reports, whatever the server has asked for.
        public static ConfigEntry<bool> AllowMonitoringUpload;

        // M24 - early ZDOData guard. Client-local because the decision is made when the connection
        // opens, before the server's config has arrived.
        public static ConfigEntry<bool> EnableEarlyZdoDataGuard;

        // Add Server synced config entries under here

        // M2/M2c - bandwidth-delay-product send window
        public static ConfigEntry<bool> EnableSendWindowSizing;
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

        // M3 tier 2 - interactable-but-stationary objects follow the nearest player
        public static ConfigEntry<bool> EnableInteractiveOwnership;
        public static ConfigEntry<float> OwnershipInteractiveClaimRadius;
        public static ConfigEntry<float> OwnershipInteractiveChallengeMargin;
        public static ConfigEntry<float> OwnershipInteractiveMinHoldSeconds;
        public static ConfigEntry<int> OwnershipInteractiveMaxReassignsPerPass;

        // M3 proximity layer - a simulated object with exactly one player near it belongs to that player
        public static ConfigEntry<bool> EnableCreatureProximityOwnership;
        public static ConfigEntry<float> CreatureProximityRadius;

        // M3 creatures - creatures are arbitrated as simulated objects, first, and kept while loaded
        public static ConfigEntry<bool> OwnershipArbitrateCreatures;

        // M25 - the host's ownership changes are not undone by a peer's older update
        public static ConfigEntry<bool> RejectStaleOwnerUpdates;

        // M20 - ship ownership follows the helmsman
        public static ConfigEntry<bool> ShipOwnershipFollowsHelmsman;

        // M23 - host reads each peer's reference position from its character
        public static ConfigEntry<bool> UseCharacterRefPos;

        // M7 - routed RPC relay filter
        public static ConfigEntry<bool> EnableRoutedRpcFilter;
        public static ConfigEntry<bool> LimitTargetedRelayByDistance;

        // M12 - station item requests delivered to the current owner
        public static ConfigEntry<bool> EnableStationRpcRouting;

        // M12 creatures - hits on a creature delivered to whoever simulates it now
        public static ConfigEntry<bool> EnableCreatureHitRouting;

        // M9 - per-peer sector scan cache
        public static ConfigEntry<bool> EnableSyncListCache;
        public static ConfigEntry<float> SyncListCacheMs;

        // M8 - Steam transport configuration
        public static ConfigEntry<bool> EnableSteamTransportTuning;
        public static ConfigEntry<int> SteamSendRateKBps;
        public static ConfigEntry<int> SteamNagleMicros;

        // M26 - per-player send-rate back-off for connections that lose packets
        public static ConfigEntry<bool> EnableLossBackoff;
        public static ConfigEntry<float> LossBackoffThreshold;
        public static ConfigEntry<int> LossBackoffFloorKBps;
        public static ConfigEntry<int> LossBackoffHoldSeconds;
        public static ConfigEntry<int> LossBackoffRecoverSeconds;

        // M10 - configurable player limit
        public static ConfigEntry<bool> EnablePlayerLimitOverride;
        public static ConfigEntry<int> MaxPlayers;

        // M11 - connection timeouts
        public static ConfigEntry<bool> EnableConnectionTimeoutTuning;
        public static ConfigEntry<int> ConnectTimeoutSeconds;
        public static ConfigEntry<int> ConnectionTimeoutSeconds;
        public static ConfigEntry<int> LoadingTimeoutSeconds;

        // M21 - ghost peers stop being trusted to simulate long before they are hung up on
        public static ConfigEntry<bool> EvictGhostOwners;
        public static ConfigEntry<float> GhostOwnerEvictSeconds;

        // M14 - other mods read the send queue as vanilla's window would leave it
        public static ConfigEntry<bool> ReportVanillaQueueSize;

        // M15-M18 - allocation removals on the ZDO network path. All four are byte-identical on
        // the wire; each ships on and has its own switch.
        public static ConfigEntry<bool> EnableDeserializeFastPath;
        public static ConfigEntry<bool> EnablePacketReadFastPath;
        public static ConfigEntry<bool> EnableSendPackageReuse;
        public static ConfigEntry<bool> EnableRpcInvokeFastPath;
        // M19 - routed RPC relay written once per message
        public static ConfigEntry<bool> EnableRelaySendReuse;

        // Network monitoring - an off-by-default recorder for diagnosing a server's network problems
        public static ConfigEntry<bool> EnableMonitoring;
        public static ConfigEntry<bool> MonitoringCollectFromClients;
        public static ConfigEntry<int> MonitoringClientBytesPerSecond;
        public static ConfigEntry<int> MonitoringMaxDiskMB;

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
            // Client configs
            EnableLatencyCompensation = Config.Bind("Client config", "EnableLatencyCompensation", true,
                new ConfigDescription("Render entities owned by other players at their estimated current position rather than their last-received one. Requires the server to be running this mod."));
            LatencyCompensationStrength = Config.Bind("Client config", "LatencyCompensationStrength", 1f,
                new ConfigDescription("How much of the measured path latency to correct for. 1.0 corrects fully. Lower values trade accuracy for less overshooting. 0 Disables it.",
                new AcceptableValueRange<float>(0f, 1f)));
            LatencyCompensationMaxMeters = Config.Bind("Client config", "LatencyCompensationMaxMeters", 3f,
                new ConfigDescription("How far objects can be displaced through latency compensation.",
                new AcceptableValueRange<float>(0.5f, 4.5f)));
            EnableDebugOverlay = Config.Bind("Client config", "EnableDebugOverlay", false,
                new ConfigDescription("Show the per-entity latency compensation overlay (owner, estimated staleness, applied displacement).", null,
                new ConfigurationManagerAttributes { IsAdvanced = true }));
            EnableGhostWatchdog = Config.Bind("Client config", "EnableGhostWatchdog", true,
                new ConfigDescription("Warn when the server stops answering, and return to the menu once it is certain. Otherwise you can continue to play on a disconnected world and any changes made would be lost."));
            AllowMonitoringUpload = Config.Bind("Client config", "AllowMonitoringUpload", true,
                new ConfigDescription("When the server turns on monitoring, this allows your client to upload data. As the client, this is your hard-opt out, server can't override it."));
            EnableEarlyZdoDataGuard = Config.Bind("Client config", "EnableEarlyZdoDataGuard", true,
                new ConfigDescription("Buffers updates that land before their associated receiving RCPs, prevents data loss on connection.", null,
                new ConfigurationManagerAttributes { IsAdvanced = true }));

            // Bandwidth sizing
            EnableSendWindowSizing = BindServerConfig("Send Window", "Enable BDP Window Sizing", true,
                "Size each peer's in-flight ZDO window from their measured round-trip time and the rate Steam sends to them at. Low-latency peers are unaffected; high-latency peers stop being throttled by their distance.");
            SendWindowBdpFactor = BindServerConfig("Send Window", "BDP Factor", 1.25f,
                "Multiplier on the bandwidth-delay 1.0 allows exactly one round-trip of data in flight at Steam's send rate.", false, 1f, 3f);
            SendWindowMaxBytes = BindServerConfig("Send Window", "Max Window Bytes", 65536,
                "Upper bound on the send window. A player needs send rate x round-trip time x BDP Factor to receive at the full rate: at the default 150 KB/s this cap covers about 340ms of ping. Raise it together with Steam Transport's Send Rate KBps.", true, 10240, 262144);

            // Scheduler
            EnableSchedulerFix = BindServerConfig("Send Scheduler", "Enable Scheduler Fix", true,
                "Send to every peer each tick instead of one peer per rendered frame. Without this the effective per-peer send rate degrades linearly with player count.");
            SendIntervalSeconds = BindServerConfig("Send Scheduler", "Send Interval Seconds", 0.033f,
                "Seconds between ZDO send rounds. Vanilla is 0.05, lower = more updates, higher = slower updates.", true, 0.02f, 0.2f);
            SendSchedulerFrameBudgetMs = BindServerConfig("Send Scheduler", "Frame Budget Ms", 4f,
                "Maximum milliseconds per frame the host spends sending ZDOs to peers. Peers are serviced in round-robin order until the budget runs out and the remainder is owed to the next frame, so nobody is starved. On a busy server this is what keeps the frame time bounded: the effective per-peer send rate becomes min(1/interval, budget/cost) - raise it to trade server frame time for send rate, lower it on a CPU-constrained host. nps_stats shows the effective rate and how often the budget is hit.", true, 0.5f, 16f);

            // Arbiter
            EnableOwnershipArbitration = BindServerConfig("Ownership", "Enable Latency-Aware Ownership", true,
                "Assign ZDO ownership to minimise how stale the object looks to the players who can actually see it, instead of vanilla's first-peer-wins ordering. This is what makes it 'feel like singleplayer'");
            OwnershipAllowHostOwner = BindServerConfig("Ownership", "Allow Host As Owner", true,
                "Let the host compete for ownership of contested ZDOs in the zones it has loaded. (This is ONLY around spawn by default).");
            OwnershipMinHoldSeconds = BindServerConfig("Ownership", "Min Hold Seconds", 2f,
                "Minimum time an owner keeps a ZDO before it can be challenged. This prevents constant ZDO change thrash.", false, 0f, 60f);
            OwnershipChallengeMarginMs = BindServerConfig("Ownership", "Challenge Margin Ms", 25,
                "A challenger must improve estimated staleness by at least this many milliseconds to take ownership.", false, 0, 250);
            OwnershipMaxReassignsPerPass = BindServerConfig("Ownership", "Max Reassigns Per Pass", 8,
                "Cap on latency-driven ownership transfers of moving objects per arbitration pass (boats, creatures, carts etc). Each transfer costs a ZDO resend.", true, 1, 128);
            OwnershipLoadPenaltyMs = BindServerConfig("Ownership", "Load Penalty Ms", 0.02f,
                "Cost added per simulated object (creatures, ships - not walls or trees) a candidate already owns nearby, in milliseconds. This prevents one user from simulating a whole area even if they have the best connection. Low priority objects will be shuffled to others.", true, 0f, 0.5f);

            // Playfab compatibility | TODO more
            OwnershipUnmeasuredRttMs = BindServerConfig("Ownership", "Unmeasured Peer RTT Ms", 150,
                "Round-trip time assumed for a peer the host has no measurement for - crossplay/PlayFab connections never report one, and every Steam peer is unmeasured for its first seconds. Such a peer still wins objects only it can see, but loses contested ones to any measured peer with a lower ping. Treating unmeasured as 0ms instead would hand them everything in range.", true, 0, 1000);

            // Lower priority Arbitration
            EnableInteractiveOwnership = BindServerConfig("Ownership", "Interactive Object Ownership", true,
                "Also place interactable-but-stationary objects - berry bushes and other pickables, ore deposits, rocks, trees, logs and destructibles - on whoever is standing nearest them. This allows most interactables to respond to the user instantly instead of waiting on a delayed RPC.");
            OwnershipInteractiveClaimRadius = BindServerConfig("Ownership", "Interactive Claim Radius", 32f,
                "How close a player has to be, in metres, before an interactable object is placed on them.", false, 4f, 64f);
            OwnershipInteractiveChallengeMargin = BindServerConfig("Ownership", "Interactive Challenge Margin", 4f,
                "How much nearer, in metres, a player must be than the current owner before an interactable object moves to them. This is the distance equivalent of Challenge Margin Ms.", false, 0f, 32f);
            OwnershipInteractiveMinHoldSeconds = BindServerConfig("Ownership", "Interactive Min Hold Seconds", 15f,
                "Minimum time before an interactable object that has changed ownership can change again. Nothing in the game hands these objects over by itself, so there is no player-initiated claim to wait out", false, 0f, 120f);
            OwnershipInteractiveMaxReassignsPerPass = BindServerConfig("Ownership", "Interactive Max Reassigns Per Pass", 16,
                "Minimum cap on how many interactable objects may be placed on a nearer player per arbitration pass. It is a separate budget from Max Reassigns Per Pass on purpose, so a zone full of contested creatures cannot starve the handful of moves that make a berry patch local.", true, 1, 256);

            // Creature arbitration
            EnableCreatureProximityOwnership = BindServerConfig("Ownership", "Creature Proximity Ownership", true,
                "When exactly one player is near a creature, that player owns it, whatever anyone else's latency is. Prevents lag when two players are within network distance but not fighting the same creatures.");
            CreatureProximityRadius = BindServerConfig("Ownership", "Creature Proximity Radius", 48f,
                "How close, in metres, a player has to be to a creature to count as near it for Creature Proximity Ownership. Two players both inside this distance of one creature are sharing a fight; two players further apart than about twice this are not.", false, 16f, 96f);
            OwnershipArbitrateCreatures = BindServerConfig("Ownership", "Arbitrate Creatures", true,
                "Give creatures first call on ownership arbitration. Game defaults to creatures having low priority for network updates.");

            // Arbitration persistance
            RejectStaleOwnerUpdates = BindServerConfig("Ownership", "Reject Stale Owner Updates", true,
                "Prevents previous owners network updates immediately taking an owned object back, and tells that player straight away who owns it now. A creature's movement from its previous owner is dropped rather than shown to everyone else. Disabling this effectively neuters the arbiter.");
            ShipOwnershipFollowsHelmsman = BindServerConfig("Ownership", "Ship Ownership Follows Helmsman", true,
                "Hand a ship to whoever takes its helm, so the player steering simulates it on their own machine instead of watching it relayed through the host from another player.");

            // Fast player position tracking 
            UseCharacterRefPos = BindServerConfig("Reference Position", "Use Character Position", true,
                "Host-side. Take each player's position from their character, as it arrives with the game's own object updates, instead of waiting for the position their game reports every 2 seconds.");

            // Routed Relay
            EnableRoutedRpcFilter = BindServerConfig("Routed RPC", "Enable Relay Filtering", true,
                "Relay broadcast RPCs (animation triggers, footsteps, damage numbers, object-destroyed notices, building damage and the like) only to the players that can actually use them, instead of to everyone on the server.");
            LimitTargetedRelayByDistance = BindServerConfig("Routed RPC", "Limit Relay By Distance", true,
                "Also stop relaying object RPCs (building damage and fragments, ward flashes, animation triggers, footsteps) to players who are too far away to have that object loaded, even if they visited it earlier in the session.");
            EnableStationRpcRouting = BindServerConfig("Routed RPC", "Route Station Requests To Owner", true,
                "Deliver fermenter, smelter, cooking station, fireplace, shield generator and ballista item requests (add item / ore / fuel / ammo, tap, empty) to whoever owns the object right now. Prevents RPCs being dropped and items being eaten.");
            EnableCreatureHitRouting = BindServerConfig("Routed RPC", "Route Creature Hits To Owner", true,
                "Deliver hits on creatures to whoever is simulating the creature right now, prevents silently dropping hits.");

            // Cache list optimization
            EnableSyncListCache = BindServerConfig("Sync List Cache", "Enable Sector Scan Cache", true,
                "Reuse each peer's sector scan across the send sweep instead of rebuilding it on every send.");
            SyncListCacheMs = BindServerConfig("Sync List Cache", "Cache Ms", 100f,
                "How long a peer's sector scan may be reused, in milliseconds. This is the longest delay an object moving sectors from one player to another would have before being considered for ownership change etc. Vanilla rebuilds this every frame.",
                false, 0f, 500f);

            // Steam Socket
            EnableSteamTransportTuning = BindServerConfig("Steam Transport", "Enable Transport Tuning", true,
                "Let this mod write Steam's global networking config (send rate and Nagle) and log a before/after readback of what the transport is actually doing. Send Rate KBps ships at the game's value; Nagle Micros ships at 0 rather than the game's 5000, so turning this on by itself only removes Nagle's hold-back delay. Requires the Steam backend; on crossplay-only processes it stands down quietly.");
            SteamSendRateKBps = BindServerConfig("Steam Transport", "Send Rate KBps", 0,
                "The rate Steam sends at on every connection from the host, in kilobytes/sec. 0 leaves the game's 150. Steam has no congestion control here: this is a fixed pace, not a ceiling it adapts under, and it never slows down for a player whose connection cannot keep up - that player gets packet loss and resends instead (nps_stats flags it as LOSSY). Budget the host's upload for players x this value: 10 players at 500 is 40 Mbit/s worst case. Written to both of Steam's rate bounds, on the host only - players' own uploads stay at the game's rate. Values from 1 to 31 are raised to 32. Applies immediately, no restart needed.",
                false, 0, 4096);
            SteamNagleMicros = BindServerConfig("Steam Transport", "Nagle Micros", 0,
                "Microseconds Steam may hold a small reliable message back to coalesce it with the next one. Vanilla and Steam both default to 5000 (5ms), which is up to 5ms added in each direction on every update for a saving that mattered on a modem. 0 sends immediately. This mod already batches at the ZDO layer, so there is very little left for Nagle to coalesce - which is why 0 is the default here rather than vanilla's 5000.",
                false, 0, 100000);

            // Packet loss backoff for poor connections
            EnableLossBackoff = BindServerConfig("Steam Transport", "Enable Loss Backoff", true,
                "Slow the server down for one player whose connection is losing what it is sent, without touching anyone else's rate. A player still losing as much at Loss Backoff Floor KBps goes back to full speed and is not slowed down again until the server restarts.");
            LossBackoffThreshold = BindServerConfig("Steam Transport", "Loss Backoff Threshold", 0.95f,
                "The share of the server's packets that must reach a player, as a fraction. Below it for Loss Backoff Hold Seconds, that player's connection is stepped down. This is also the line nps_stats marks as LOSSY.",
                false, 0.5f, 0.999f);
            LossBackoffFloorKBps = BindServerConfig("Steam Transport", "Loss Backoff Floor KBps", 32,
                "Never step a player's rate below this, in kilobytes/sec. 32 is the same floor Send Rate KBps has. A floor at or above Send Rate KBps leaves nothing to step down to, which switches the back-off off in effect.",
                false, 32, 4096);
            LossBackoffHoldSeconds = BindServerConfig("Steam Transport", "Loss Backoff Hold Seconds", 10,
                "How long a player must stay under the threshold before each step down, in seconds. Measured from the last step, so a connection is never lowered faster than this.",
                true, 3, 120);
            LossBackoffRecoverSeconds = BindServerConfig("Steam Transport", "Loss Backoff Recover Seconds", 60,
                "How long a player must stay clean - two points above the threshold - before each step back up, in seconds. Measured from the last step, so a connection is never raised faster than this.",
                true, 10, 600);

            // configurable player limit
            EnablePlayerLimitOverride = BindServerConfig("Player Limit", "Enable Player Limit Override", true,
                "Enables changing the player limit from vanillas 10.");
            MaxPlayers = BindServerConfig("Player Limit", "Max Players", 60,
                "How many players the server accepts. 10 is vanilla. Crossplay servers cant go above 128, steam can. Setting above 128 for Crossplay caps it at 128.",
                false, 1, 255);

            // Timeout Limits
            EnableConnectionTimeoutTuning = BindServerConfig("Connection Timeout", "Enable Timeout Tuning", true,
                "Let this mod set how long a connection may go quiet before either end hangs up, instead of the game's fixed 30 seconds.");
            ConnectTimeoutSeconds = BindServerConfig("Connection Timeout", "Connect Timeout Seconds", 10,
                "How long a connection attempt may take before Steam abandons it, in seconds. 10 is Steam's own default, which the game never changes. This covers only the handshake, before the connection exists.",
                false, 5, 600);
            ConnectionTimeoutSeconds = BindServerConfig("Connection Timeout", "Connection Timeout Seconds", 30,
                "How long an established connection may go without a packet before it is dropped, in seconds. 30 is vanilla. This is the setting for players who get disconnected mid-join or during a hitch on a weak link.",
                false, 10, 600);
            LoadingTimeoutSeconds = BindServerConfig("Connection Timeout", "Loading Timeout Seconds", 90,
                "The longer allowance the game already gives itself while a crossplay peer is joining and the world is being transferred, in seconds. 90 is vanilla. A slow client can spend minutes here loading on a large world.",
                true, 30, 900);
            EvictGhostOwners = BindServerConfig("Connection Timeout", "Evict Ghost Owners", true,
                "Stop giving objects to a player who has stopped answering. A peer that goes quiet keeps its slot for the full 'Connection Timeout Seconds' so it can come back, but the things it was simulating - creatures especially - are handed to players who are actually there.");
            GhostOwnerEvictSeconds = BindServerConfig("Connection Timeout", "Ghost Owner Evict Seconds", 10f,
                "How long a player may be silent before their objects are given to someone else, in seconds. The game pings every peer once a second, so ten seconds is ten missed replies.",
                true, 3f, 60f);

            // Compatibility with ServerSync and other sync frameworks
            ReportVanillaQueueSize = BindServerConfig("Compatibility", "Report Vanilla Queue Size", true,
                "Some mods - ServerSync and every mod that bundles it, ConditionalConfigSync, ServerCharacters, Jotunn - wait for a player's send queue to fall under a fixed 10000-20000 bytes before sending, and disconnect that player after 30 seconds if it never does. This adjusts that to handled changes made by this mod.");

            // Memory allocation fixes
            EnableDeserializeFastPath = BindServerConfig("Allocation", "Enable ZDO Deserialize Fast Path", true,
                "Read a received ZDO's fields directly instead of through the fourteen delegates the game allocates for every single one.");
            EnablePacketReadFastPath = BindServerConfig("Allocation", "Enable Packet Read Fast Path", true,
                "Read each incoming ZDO's payload straight into the buffer that is about to hold it instead of copying twice.");
            EnableSendPackageReuse = BindServerConfig("Allocation", "Enable Send Package Reuse", true,
                "Reuse the two packet buffers the send path builds, instead of constructing and discarding both on every send to every peer.");
            EnableRpcInvokeFastPath = BindServerConfig("Allocation", "Enable RPC Invoke Fast Path", true,
                "Call an incoming RPC's handler directly when its signature is the common one, instead of going through reflection for every message.");
            EnableRelaySendReuse = BindServerConfig("Allocation", "Enable Relay Send Reuse", true,
                "Write each relayed RPC (footsteps, hits, damage numbers, chat and the rest) once and hand the same bytes to every player it goes to, instead of rebuilding and re-copying the whole message for each one. The bytes sent are identical. Host-side only.");

            // Monitoring
            EnableMonitoring = BindServerConfig("Monitoring", "Enable Network Monitoring", false,
                "Record what the network is doing to BepInEx/NpsMonitoring, for sending to the mod author with a bug or optimization report.");
            MonitoringCollectFromClients = BindServerConfig("Monitoring", "Collect From Clients", true,
                "While monitoring is on, also ask every player's game for its network related stats");
            MonitoringClientBytesPerSecond = BindServerConfig("Monitoring", "Client Upload Bytes Per Second", 2048,
                "The most each client may send the server in monitoring records, averaged over time. Records beyond it are dropped on the client and counted. Sent in 4KB batches that are held back whenever the client's link to the server is already near its send window, so they never delay the game's own traffic.", true, 256, 4096);
            MonitoringMaxDiskMB = BindServerConfig("Monitoring", "Max Disk MB", 4096,
                "The most BepInEx/NpsMonitoring may hold, across every session in it. Recording stops when it is reached and says so in the log. Nothing already recorded is ever deleted to make room. Files are compressed as they are closed.", false, 64, 65536);

            // Any server-side setting changing mid-session is worth a record of its own while
            // monitoring is on: it is what separates the two halves of an on/off comparison.
            Config.SettingChanged += OnAnySettingChanged;

            // Steam's networking config is process-global and re-writable at any time, so these
            // take effect on edit rather than needing a restart. Steam re-clamps a live
            // connection's rate every time it services it, so a new rate applies at once.
            EnableSteamTransportTuning.SettingChanged += OnSteamTransportSettingChanged;
            SteamSendRateKBps.SettingChanged += OnSteamTransportSettingChanged;
            SteamNagleMicros.SettingChanged += OnSteamTransportSettingChanged;

            // M26's live overrides follow the global rate through SteamTransport.Apply already;
            // its own settings only need those overrides re-derived, without re-running the global
            // write and its readback line for a threshold edit.
            EnableLossBackoff.SettingChanged += OnLossBackoffSettingChanged;
            LossBackoffThreshold.SettingChanged += OnLossBackoffSettingChanged;
            LossBackoffFloorKBps.SettingChanged += OnLossBackoffSettingChanged;
            LossBackoffHoldSeconds.SettingChanged += OnLossBackoffSettingChanged;
            LossBackoffRecoverSeconds.SettingChanged += OnLossBackoffSettingChanged;

            // Both timeout layers are process-global and re-writable at any time as well, so these
            // apply on edit too - including the edit Jotunn performs on a client when the server
            // pushes its own values down at join time.
            EnableConnectionTimeoutTuning.SettingChanged += OnConnectionTimeoutSettingChanged;
            ConnectTimeoutSeconds.SettingChanged += OnConnectionTimeoutSettingChanged;
            ConnectionTimeoutSeconds.SettingChanged += OnConnectionTimeoutSettingChanged;
            LoadingTimeoutSeconds.SettingChanged += OnConnectionTimeoutSettingChanged;

            // Jotunn's CustomRPC limit is derived from the window ceiling, so it follows these two
            // - including the edit Jotunn makes on a client when the server's values arrive at join.
            EnableSendWindowSizing.SettingChanged += OnJotunnQueueSettingChanged;
            SendWindowMaxBytes.SettingChanged += OnJotunnQueueSettingChanged;
        }

        private static void OnSteamTransportSettingChanged(object sender, EventArgs e) {
            Runtime.SteamTransport.OnConfigChanged();
        }

        private static void OnLossBackoffSettingChanged(object sender, EventArgs e) {
            Runtime.LossBackoff.OnConfigChanged();
        }

        private static void OnJotunnQueueSettingChanged(object sender, EventArgs e) {
            Runtime.JotunnSendQueue.OnConfigChanged();
        }

        private static void OnConnectionTimeoutSettingChanged(object sender, EventArgs e) {
            Runtime.ConnectionTimeout.OnConfigChanged();
        }

        private static void OnAnySettingChanged(object sender, SettingChangedEventArgs e) {
            Runtime.Monitoring.OnSettingChanged(e.ChangedSetting);
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
