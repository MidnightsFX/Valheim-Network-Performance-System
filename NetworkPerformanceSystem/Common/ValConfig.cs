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

        // M3 tier 2 - interactable-but-stationary objects follow the nearest player
        public static ConfigEntry<bool> EnableInteractiveOwnership;
        public static ConfigEntry<float> OwnershipInteractiveClaimRadius;
        public static ConfigEntry<float> OwnershipInteractiveChallengeMargin;
        public static ConfigEntry<float> OwnershipInteractiveMinHoldSeconds;
        public static ConfigEntry<int> OwnershipInteractiveMaxReassignsPerPass;

        // M3 proximity layer - a simulated object with exactly one player near it belongs to that player
        public static ConfigEntry<bool> EnableCreatureProximityOwnership;
        public static ConfigEntry<float> CreatureProximityRadius;

        // M20 - ship ownership follows the helmsman
        public static ConfigEntry<bool> ShipOwnershipFollowsHelmsman;

        // M6 - fast reference position channel
        public static ConfigEntry<bool> EnableFastRefPos;
        public static ConfigEntry<float> RefPosSendHz;
        public static ConfigEntry<float> RefPosMinMoveDistance;

        // M7 - routed RPC relay filter
        public static ConfigEntry<bool> EnableRoutedRpcFilter;
        public static ConfigEntry<bool> LimitTargetedRelayByDistance;

        // M12 - station item requests delivered to the current owner
        public static ConfigEntry<bool> EnableStationRpcRouting;

        // M9 - per-peer sector scan cache
        public static ConfigEntry<bool> EnableSyncListCache;
        public static ConfigEntry<float> SyncListCacheMs;

        // M8 - Steam transport configuration
        public static ConfigEntry<bool> EnableSteamTransportTuning;
        public static ConfigEntry<int> SteamSendRateMaxKBps;
        public static ConfigEntry<int> SteamSendRateMinKBps;
        public static ConfigEntry<int> SteamNagleMicros;

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
        // the wire; each is opt-in for its first release.
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

            // --- M22: client ghost watchdog (client-local) ---------------------------------
            // The game already leaves a dead session, but only once ZRpc's ping timeout closes the
            // socket - and that timeout is a process-wide static that one accepted crossplay
            // socket raises to 90 seconds, and that this mod's own Connection Timeout setting can
            // raise further. That is the right allowance for a join that is still working and the
            // wrong one for "how long may I keep playing a world that is gone".
            EnableGhostWatchdog = Config.Bind("Client config", "EnableGhostWatchdog", true,
                new ConfigDescription("Warn when the server stops answering, and return to the menu once it is certain rather than leaving you playing a world the server is no longer part of. The warning appears halfway to the timeout actually in force, and clears itself if the connection comes back. No new timeout of its own: it follows the same deadline the game is using, so it cannot disagree with the server's setting. Stands down automatically if ClientGhostWatchdog is installed, which does the same job."));

            // --- Network monitoring, the player's half (client-local) ----------------------
            // Nothing is recorded or sent unless the server has monitoring switched on and has
            // asked this client for its records. This is the player's veto over that request.
            AllowMonitoringUpload = Config.Bind("Client config", "AllowMonitoringUpload", true,
                new ConfigDescription("When the server has network monitoring switched on, let this game send it what it sees: when creatures change owner, hits that were sent to a player who no longer owned the target, creatures jumping on screen, frame rate. No player names, platform ids, addresses or chat - only session ids, object ids, positions to the metre and timings, at up to 2KB per second and never ahead of the game's own traffic. Does nothing at all unless the server asks. Set to false to refuse."));

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
            SendIntervalSeconds = BindServerConfig("Send Scheduler", "Send Interval Seconds", 0.033f,
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
                "Assign ZDO ownership to minimise how stale the object looks to the players who can actually see it, instead of vanilla's first-peer-wins ordering. Simulated, moving objects (creatures and other Prioritized ZDOs) are placed on whoever minimises staleness for everyone watching; buildings, containers, crafting stations, pieces and portals keep their owner until that player leaves, exactly as in vanilla. Pickables, ore deposits, rocks and trees are handled separately and by a different rule - see Interactive Object Ownership below.");
            OwnershipAllowHostOwner = BindServerConfig("Ownership", "Allow Host As Owner", true,
                "Let the host compete for ownership of contested ZDOs in the zones it has loaded. The host is zero hops from everyone, so host-owned is the lowest possible staleness for every viewer, and whenever two or more players share a zone the host has loaded it will win those objects and keep them. A listen host has loaded the zones around its own player; a dedicated server has loaded only the zones around the world origin, so in practice this means a dedicated server owns and simulates the contested objects at the spawn hub whenever players gather there - intended, and worth knowing when budgeting server CPU. Disable to always place on the lowest-latency player present instead.");
            OwnershipMinHoldSeconds = BindServerConfig("Ownership", "Min Hold Seconds", 5f,
                "Minimum time an owner keeps a ZDO before it can be challenged. Hysteresis against ownership thrash. Applies only when moving a simulated, moving object (a creature or other Prioritized ZDO) away from an owner that is still present - buildings, containers, stations and pieces are never moved off a present owner, interactables have their own hold below, and a ZDO whose owner has left the area or the session is re-owned immediately, at any setting.", false, 0f, 60f);
            OwnershipChallengeMarginMs = BindServerConfig("Ownership", "Challenge Margin Ms", 25,
                "A challenger must improve estimated staleness by at least this many milliseconds to take ownership. Prevents ping jitter from ping-ponging ownership between similar peers. Applies only to challenges against a present owner, and also bounds how much the Load Penalty below may shift a decision.", false, 0, 250);
            OwnershipMaxReassignsPerPass = BindServerConfig("Ownership", "Max Reassigns Per Pass", 8,
                "Minimum cap on latency-driven ownership transfers of moving objects per arbitration pass (buildings, containers, stations and pieces are never transferred off a present owner; interactables have their own budget below). Each transfer costs a ZDO resend, so this bounds the burst when a group arrives in a new area. The effective cap is the larger of this value and the number of connected players, so a full server converges at the same per-player rate as a small group rather than linearly slower. Restoring an owner to a ZDO that has none is never deferred by this: an unowned creature does not move and cannot be damaged.", true, 1, 128);
            OwnershipLoadPenaltyMs = BindServerConfig("Ownership", "Load Penalty Ms", 0.02f,
                "Cost added per simulated object (creatures, ships - not walls or trees) a candidate already owns nearby, in milliseconds. Spreads simulation and upload load across peers instead of concentrating every contested object on the lowest-ping player. The total handicap is capped at half of Challenge Margin Ms, so load can shade a close decision but can never on its own amount to the staleness difference that justifies a transfer. 0 disables load spreading and places purely by staleness.", true, 0f, 0.5f);
            // Crossplay/PlayFab sockets never report a round-trip time and a Steam peer has none
            // for its first second or two. Scoring those at 0ms handed them every contested object
            // in range; this is what they are scored at instead. High enough to lose to any
            // measured peer with a normal ping, irrelevant when the peer is the only viewer
            // (which it wins at any RTT).
            OwnershipUnmeasuredRttMs = BindServerConfig("Ownership", "Unmeasured Peer RTT Ms", 150,
                "Round-trip time assumed for a peer the host has no measurement for - crossplay/PlayFab connections never report one, and every Steam peer is unmeasured for its first seconds. Such a peer still wins objects only it can see, but loses contested ones to any measured peer with a lower ping. Treating unmeasured as 0ms instead would hand them everything in range.", true, 0, 1000);

            // --- M3 tier 2: interactable-but-stationary objects -----------------------------
            // Picking a berry or swinging at an ore vein asks the object's OWNER to do the work -
            // ZNetView.InvokeRPC(string, ...) addresses m_zdo.GetOwner() - and every handler begins
            // with an IsOwner check. Clients only ever peer with the host, so an object owned by
            // another player costs four network legs per keypress. Staleness is meaningless for
            // these (their ZDO changes only when somebody touches them), so they are placed by
            // distance instead: whoever is nearest is whoever is about to touch it.
            EnableInteractiveOwnership = BindServerConfig("Ownership", "Interactive Object Ownership", true,
                "Also place interactable-but-stationary objects - berry bushes and other pickables, ore deposits, rocks, trees, logs and destructibles - on whoever is standing nearest them, instead of leaving them with whoever happened to touch one first. Picking a berry or swinging at a rock asks the object's owner to do the work, and if that owner is another player the request travels from you to the server to them, and the result comes back the same way: four network legs for one keypress, which is what makes a shared berry patch or mine feel sluggish. Placement here is by distance rather than by ping, because nothing about a berry bush changes between interactions - the only thing that matters is whether the person about to touch it is the one simulating it. Buildings, containers, crafting stations, pieces and anything a player has open are still never moved off a player who is present.");
            OwnershipInteractiveClaimRadius = BindServerConfig("Ownership", "Interactive Claim Radius", 32f,
                "How close a player has to be, in metres, before an interactable object is placed on them. Objects further than this from everybody are left exactly where they are - nobody is about to touch them, so moving them would cost a network update and buy nothing. The default is half a zone: far enough that ownership has usually settled before you walk into range and start picking, close enough that walking past somebody's base does not churn every bush, rock and tree around it.", false, 4f, 64f);
            OwnershipInteractiveChallengeMargin = BindServerConfig("Ownership", "Interactive Challenge Margin", 4f,
                "How much nearer, in metres, a player must be than the current owner before an interactable object moves to them. This is the distance equivalent of Challenge Margin Ms and it does the same job: without it, two players drifting around the same berry patch would trade every bush in it back and forth. Note that an object is never taken from an owner standing within reach of it, whatever this is set to - that owner may be mid-swing, and the moment their copy of the owner id goes stale is the moment a swing can be lost.", false, 0f, 32f);
            OwnershipInteractiveMinHoldSeconds = BindServerConfig("Ownership", "Interactive Min Hold Seconds", 15f,
                "Minimum time before an interactable object that has already been moved can move again. Unlike Min Hold Seconds above, this is measured from the last move rather than from when the owner was first seen: nothing in the game hands these objects over by itself, so there is no player-initiated claim to wait out, and charging a wait would mean the first berry you pick after walking into a patch is still the slow one. So the first placement is immediate and only repeat moves are damped. Raise it if a busy shared base shows a lot of ownership churn in nps_stats.", false, 0f, 120f);
            OwnershipInteractiveMaxReassignsPerPass = BindServerConfig("Ownership", "Interactive Max Reassigns Per Pass", 16,
                "Minimum cap on how many interactable objects may be placed on a nearer player per arbitration pass. It is a separate budget from Max Reassigns Per Pass on purpose, so a zone full of contested creatures cannot starve the handful of moves that make a berry patch local, or be starved by them. The nearest objects are done first, so walking into a patch converts the bushes you are about to reach before the ones at its far edge. As with Max Reassigns Per Pass the effective cap is the larger of this value and the number of connected players. Each move costs one small object update to each player nearby: raise it to convert a large patch or ore face in a single pass, lower it on a bandwidth-constrained host.", true, 1, 256);

            // --- M3 proximity layer: one player near it, that player owns it ----------------
            // Latency placement prices a whole zone and weighs everyone in range of it equally,
            // which is right for a fight they are all watching and wrong for two players a hundred
            // metres apart with a fight each: being in range of each other is then enough to put
            // one player's creatures on the other's machine. This sits in front of the cost
            // function and takes the decision away from it whenever exactly one player is near.
            EnableCreatureProximityOwnership = BindServerConfig("Ownership", "Creature Proximity Ownership", true,
                "When exactly one player is near a creature, that player owns it, whatever anyone else's latency is. It is never taken from them for a lower-latency player who is merely in loading range; a creature nobody is simulating goes to them rather than to the lowest-latency player present; and one owned by a player who has moved well away is handed to them. When two or more players are near the same creature it is a shared fight and latency decides, exactly as before, and the same when nobody is near it. Applies to everything simulated, so an unattended cart or an idle tame follows the same rule; a ship, a ridden mount and an attached cart are never taken from a present owner, as before. Host-side only, no client install needed. Applies immediately, no restart needed.");
            CreatureProximityRadius = BindServerConfig("Ownership", "Creature Proximity Radius", 48f,
                "How close, in metres, a player has to be to a creature to count as near it for Creature Proximity Ownership. Two players both inside this distance of one creature are sharing a fight; two players further apart than about twice this are not, for anything close to either of them. A creature is only taken from its owner for another player once the owner is 8m beyond this. Smaller is stricter about who is really in a fight and protects less at bow range; larger protects more and treats players who are merely close as fighting together.", false, 16f, 96f);

            // --- M20: ship ownership follows the helmsman -----------------------------------
            // Vanilla hands a saddle or a cart to whoever takes control of it, but never a ship:
            // the helm only records who is steering, and the ship stays with whoever owned it
            // while that player is aboard. The helmsman then steers a ship simulated on someone
            // else's machine and rides its relayed motion, which is the choppiness.
            ShipOwnershipFollowsHelmsman = BindServerConfig("Ownership", "Ship Ownership Follows Helmsman", true,
                "Hand a ship to whoever takes its helm, so the player steering simulates it on their own machine instead of watching it relayed through the host from another player. The handoff is made by the machine that currently owns the ship, the same way the game already hands over saddles and carts, so it happens wherever that machine runs this mod - a listen host, a dedicated server, or a client with the mod. A ship owned by a player without the mod behaves as in vanilla. The ship stays with that player while they remain aboard after letting go of the helm. The host also gives an abandoned ship - its owner left or disconnected - to the player at its helm first.");

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

            // The filter above sends a ZDO-targeted RPC to every peer that has been sent that ZDO.
            // The host only forgets that when the object moves to another zone or is destroyed, so
            // a building piece or ward stays "held" by every player who has ever been near it,
            // and its RPCs follow them across the map for the rest of the session. A client can
            // only act on one inside the area it has loaded, which is bounded by its negotiated
            // simulation distance; one zone of slack covers the host's copy of its position being
            // up to two seconds old. Counted either way, so nps_stats shows what this would save
            // before anyone turns it on.
            LimitTargetedRelayByDistance = BindServerConfig("Routed RPC", "Limit Relay By Distance", true,
                "Also stop relaying object RPCs (building damage and fragments, ward flashes, animation triggers, footsteps) to players who are too far away to have that object loaded, even if they visited it earlier in the session. The distance used is each player's own simulation distance plus one zone of slack. nps_stats counts how many deliveries this would remove while it is off, so you can see whether it is worth it first. Needs Enable Relay Filtering. A client-side mod that loads more of the world than the server agreed to could miss effects at the edge of its view. Applies immediately.");

            // --- M12: station item requests delivered to the current owner ------------------
            // Fermenters, smelters, cooking stations, fireplaces, shield generators and ballistas
            // take the item out of the inventory and then ask "the owner" to account for it - the
            // owner as the sender's copy of the world names it, with no acknowledgement and no
            // rollback when the receiver is not in fact the owner. The host always knows who is.
            EnableStationRpcRouting = BindServerConfig("Routed RPC", "Route Station Requests To Owner", true,
                "Deliver fermenter, smelter, cooking station, fireplace, shield generator and ballista item requests (add item / ore / fuel / ammo, tap, empty) to whoever owns the object right now, rather than to whoever the player's copy of the world still says owns it. The game removes the item from your inventory before sending, and the receiver silently discards the request unless it is the owner - so any moment the two disagree loses the item: the player who owned the station just walked away or logged off, or ownership has only just been handed over. If nobody present owns the object, ownership is handed to the requesting player first. Host-side only; players do not need the mod for it.");

            // --- M9: per-peer sector scan cache --------------------------------------------
            // ZDOMan.CreateSyncList runs FindSectorObjects - a (2*activeArea+1)^2 bucket walk plus
            // the distant ring - once per peer per send. Vanilla sent to one peer per frame behind
            // a 50ms gate, so that ran about 4x/sec per peer; the scheduler above services every
            // peer every interval, which is 20x/sec per peer. Frame Budget Ms currently absorbs
            // that by cutting the send rate, which trades away the thing the scheduler exists to
            // deliver. Caching the scan removes the cost instead.
            EnableSyncListCache = BindServerConfig("Sync List Cache", "Enable Sector Scan Cache", true,
                "Reuse each peer's sector scan across the send sweep instead of rebuilding it on every send. The recipient filter and the priority sort still run every single send, so exactly the same ZDOs go out in the same order - only the scan that produces the candidate list is shared. Invalidated immediately whenever the peer changes zone or any object is destroyed.");
            SyncListCacheMs = BindServerConfig("Sync List Cache", "Cache Ms", 100f,
                "How long a peer's sector scan may be reused, in milliseconds. The cost is that an object newly arriving in a peer's area can wait this long before it is first considered - bounded, and small next to the send interval. Destroyed objects are never affected: any destruction invalidates the scan immediately, at any setting. 0 disables the cache and rebuilds the scan every send, as vanilla.",
                false, 0f, 500f);

            // --- M8: Steam transport configuration -----------------------------------------
            // ZSteamSocket.RegisterGlobalCallbacks pins SendRateMin AND SendRateMax to the same
            // 153600 B/s at Global scope. Clamped from both sides, Steam's bandwidth estimator
            // has no range to work in at all - vanilla Valheim effectively runs with congestion
            // control switched off and a hard 150 KB/s ceiling underneath everything this mod
            // does. Sizing a send window above that just moves the queue one layer down.
            EnableSteamTransportTuning = BindServerConfig("Steam Transport", "Enable Transport Tuning", true,
                "Let this mod write Steam's global networking config (send-rate bounds and Nagle). Every value below ships at its vanilla setting, so enabling this on its own changes nothing - it only makes the settings reachable and logs a before/after readback of what the transport is actually doing. Requires the Steam backend; on crossplay-only processes it stands down quietly.");
            SteamSendRateMaxKBps = BindServerConfig("Steam Transport", "Send Rate Max KBps", 0,
                "Ceiling on Steam's per-connection bandwidth estimate, in kilobytes/sec. 0 leaves vanilla's 150. This is a ceiling, not a target: raising it lets the estimator climb during a burst, it does not push traffic. Until it is raised, Send Window sizing above 150 KBps cannot do anything - the transport meters at 150 regardless and the surplus becomes standing queue. Raise this and Target Rate KBps together, and provision the uplink for the result: 10 players at 500 KBps is 40 Mbit/s of upload worst case.",
                false, 0, 4096);
            SteamSendRateMinKBps = BindServerConfig("Steam Transport", "Send Rate Min KBps", 0,
                "Floor under Steam's bandwidth estimate, in kilobytes/sec. 0 leaves vanilla's 150. Vanilla sets this equal to the ceiling, which is why the estimator never moves; LOWERING it is the useful direction, because it lets congestion control actually back off for a peer on a weak downlink instead of overdriving the link into loss. This setting cannot be raised above vanilla - that direction converts congestion into buffering and is never what you want.",
                true, 0, 150);
            SteamNagleMicros = BindServerConfig("Steam Transport", "Nagle Micros", 0,
                "Microseconds Steam may hold a small reliable message back to coalesce it with the next one. Vanilla and Steam both default to 5000 (5ms), which is up to 5ms added in each direction on every update for a saving that mattered on a modem. 0 sends immediately. This mod already batches at the ZDO layer, so there is very little left for Nagle to coalesce - which is why 0 is the default here rather than vanilla's 5000.",
                false, 0, 100000);

            // --- M10: configurable player limit --------------------------------------------
            // Vanilla hard-codes 10 in four places that do not read each other: the check that
            // actually turns the 11th peer away (ZNet.RPC_PeerInfo), the Steam lobby size that
            // the server browser prints as "3 / 10", the PlayFab lobby size - which is both the
            // browser's number for a crossplay client and a real ceiling, because crossplay
            // clients join that lobby before ZNet ever sees them - and the PlayFab Party network
            // that carries those clients once they are past the lobby. Raising one and not the
            // others produces a server that is full at a different number than it advertises, or
            // full for console players only, or one that admits console players and then drops
            // them.
            EnablePlayerLimitOverride = BindServerConfig("Player Limit", "Enable Player Limit Override", true,
                "Let this mod decide how many players the server accepts, instead of the game's hard-coded 10. Max Players below ships at 10, so enabling this on its own changes nothing - it only makes the number reachable. Applies on the host; a client has no say in it. Has no effect when Valheim Plus is installed - V+ sets the limit itself, and its maxPlayers setting is the one in force.");
            MaxPlayers = BindServerConfig("Player Limit", "Max Players", 60,
                "How many players the server accepts. 10 is vanilla. This counts the same players the game counts: on a player-hosted game the host is one of them, on a dedicated server it is not. The number is enforced the moment it changes, but the limit shown in the server browser - and the crossplay capacity, which is a real ceiling rather than a label - are set when the server registers, so lower it live if you must and restart to raise it cleanly. Nothing about raising it makes the traffic free: every player added costs the host upload and CPU against every other player, so treat the rest of this config (Send Scheduler's frame budget, Steam Transport's rate ceiling) as the things that decide whether a larger number is actually playable. Crossplay servers cannot exceed 128 whatever is set here - PlayFab's lobbies do not go higher.",
                false, 1, 255);

            // --- M11: connection timeouts --------------------------------------------------
            // Vanilla decides a peer is gone at two layers that do not know about each other -
            // ZRpc's 30s application ping timeout and Steam's 30s TimeoutConnected - and a peer
            // dies at whichever fires first, so these move together and are not offered as
            // separate numbers. Raising them does not make a slow link faster; it stops both ends
            // giving up on a join that is still working.
            EnableConnectionTimeoutTuning = BindServerConfig("Connection Timeout", "Enable Timeout Tuning", true,
                "Let this mod set how long a connection may go quiet before either end hangs up, instead of the game's fixed 30 seconds. Every value below ships at its vanilla setting, so enabling this on its own changes nothing - it only makes the settings reachable and logs a before/after readback of what is actually in force. Turning it back off restores vanilla's values immediately rather than leaving the last-written ones in place.");
            ConnectTimeoutSeconds = BindServerConfig("Connection Timeout", "Connect Timeout Seconds", 10,
                "How long a connection attempt may take before Steam abandons it, in seconds. 10 is Steam's own default, which the game never changes. This covers only the handshake, before the connection exists - NAT traversal between two awkward home routers is the usual reason it is not enough, and it is the one timeout the server cannot decide for a client, because nothing has been synced to that client yet: whoever is failing to connect has to raise it in their own config.",
                false, 5, 600);
            ConnectionTimeoutSeconds = BindServerConfig("Connection Timeout", "Connection Timeout Seconds", 30,
                "How long an established connection may go without a packet before it is dropped, in seconds. 30 is vanilla. This is the setting for players who get disconnected mid-join or during a hitch on a weak link - it is written to BOTH layers the game times out at (ZRpc's ping timeout and Steam's TimeoutConnected), because the effective timeout is the lower of the two and raising one alone achieves nothing. The cost falls on the host: a player who is genuinely gone now holds their slot, and keeps ownership of everything they were simulating, for this long instead of 30 seconds - and objects an absent owner holds do not move. Size it to the worst connection you actually want to keep.",
                false, 10, 600);
            LoadingTimeoutSeconds = BindServerConfig("Connection Timeout", "Loading Timeout Seconds", 90,
                "The longer allowance the game already gives itself while a crossplay peer is joining and the world is being transferred, in seconds. 90 is vanilla. A slow client can spend minutes here on a large world, and this is the timeout that ends the join when it does. Never applied below 'Connection Timeout Seconds' - a loading peer is not given less slack than an idle one, whatever this is set to.",
                true, 30, 900);

            // --- M21: ghost owners ----------------------------------------------------------
            // "Stop trusting this peer to simulate" and "give up on this peer entirely" are
            // different questions, and vanilla only ever asks the second one. That is what made
            // the setting above a trade: a peer that is gone keeps ownership of everything it was
            // simulating for the whole timeout, and objects an absent owner holds do not move.
            // They do not have to be the same number.
            EvictGhostOwners = BindServerConfig("Connection Timeout", "Evict Ghost Owners", true,
                "Stop giving objects to a player who has stopped answering, without disconnecting them. A peer that goes quiet keeps its slot for the full 'Connection Timeout Seconds' so it can come back, but the things it was simulating - creatures especially - are handed to players who are actually there, instead of standing frozen and unkillable until the timeout expires. This is what makes raising the timeout above safe: the wait costs the absent player nothing and no longer costs everyone else a frozen world. Applies on the host; players do not need the mod for it.");
            GhostOwnerEvictSeconds = BindServerConfig("Connection Timeout", "Ghost Owner Evict Seconds", 10f,
                "How long a player may be silent before their objects are given to someone else, in seconds. The game pings every peer once a second, so ten seconds is ten missed replies - well past any ordinary hitch and well short of the timeout that actually disconnects them. Independent of that timeout and always held below it, since evicting a peer the game has already hung up on would be answering a question nobody is still asking. When the transport reports the connection dead outright, that is acted on immediately and this value is not consulted.",
                true, 3f, 60f);

            // --- M13/M14: fixed third-party send queue thresholds ---------------------------
            // Jotunn's CustomRPC, ServerSync and every mod bundling it, ConditionalConfigSync and
            // others all wait, before sending, for the peer's socket send queue to fall under a
            // fixed 10000-20000 bytes, and disconnect the peer after 30 seconds if it never does.
            // That was sized against vanilla, which never lets the queue past ~10 KB. On a Steam
            // socket the queue figure includes bytes in flight, so the M2 window IS the standing
            // queue for a backlogged peer, and past ~100ms RTT it sits above their threshold for
            // as long as the backlog lasts. Jotunn's threshold is a static field and is raised to
            // sit above the window (JotunnSendQueue). The others have theirs compiled in, so what
            // they read is changed instead: the part of the queue the window adds above vanilla is
            // taken off (SendQueueView). That covers Jotunn too, which leaves M13 as the backstop
            // for when this is off or stood down.
            //
            // It replaces the 1.4.2-1.6.0 drain, which held backlogged peers at 8 KB for a round
            // trip every eight seconds so the real figure would dip - a periodic stall for every
            // distant player. Its two settings are no longer bound and are left in old config
            // files as orphans, which BepInEx carries along without reading.
            ReportVanillaQueueSize = BindServerConfig("Compatibility", "Report Vanilla Queue Size", true,
                "Some mods - ServerSync and every mod that bundles it, ConditionalConfigSync, ServerCharacters, Jotunn - wait for a player's send queue to fall under a fixed 10000-20000 bytes before sending, and disconnect that player after 30 seconds if it never does. That figure counts data already on its way, so a latency-sized send window keeps it above those numbers for as long as a distant player has updates to receive. With this on, those mods are shown the queue less the part this mod's window adds above vanilla's, which is the range they would see without this mod, so they wait exactly as long as they otherwise would. Nothing is held back and nothing sent changes; this mod's own send path keeps reading the real figure. Turn it off only if another mod misbehaves with it, and expect those mods to time out distant players when it is. Applies immediately.");

            // ================================================================================
            // M15-M18: allocation removals on the ZDO network path
            //
            // Four separate changes that share one property: none of them alters a single byte on
            // the wire, a single value in a ZDO, or a single decision the game makes. Each one
            // removes short-lived objects the game creates and immediately drops on the paths it
            // runs most often.
            //
            // What that buys, stated honestly, because the temptation is to oversell it: Mono's
            // collector runs more often the faster objects are created. These make it run less
            // often, which is less CPU spent collecting and a longer interval between the memory
            // incidents a very large world eventually hits. They do NOT reduce how much is live at
            // once, which is what that ceiling is actually a function of - that is decided by how
            // much world has been generated, and no mod changes it. Expect smoother, not immune.
            //
            // All four ship OFF. They are byte-identical by construction, but they are IL-level
            // changes to the hottest paths in the game and they deserve an opt-in soak before
            // anyone runs them unattended. Turning one on needs a restart for the three that are
            // wrappers (the mod does not install a hook on a method this hot for a server that
            // asked for none of it); turning one off applies immediately.
            // ================================================================================

            // --- M15: ZDO.Deserialize without the per-ZDO delegates -------------------------
            // ZDO.Deserialize hands seven typed read/write pairs to a generic helper, and
            // constructs all fourteen delegates before the helper looks at whether that type is
            // even present in the packet - so a ZDO carrying one float pays for all fourteen.
            // This is the largest of the four by volume, and a CLIENT feels it more than a server:
            // a client receives the server's whole stream, a server receives each peer's much
            // smaller delta.
            EnableDeserializeFastPath = BindServerConfig("Allocation", "Enable ZDO Deserialize Fast Path", true,
                "Read a received ZDO's fields directly instead of through the fourteen delegates the game allocates for every single one, whether or not the packet contains that field type. Identical result: the same fields land in the same tables with the same reserved capacities. This is the biggest of the four allocation settings and the one clients benefit from most. Needs a restart to turn on; turns off immediately.");

            // --- M16: ZPackage.ReadPackage(ref) straight into the target --------------------
            // Reads a fresh byte[] and then copies it into the target's stream. One throwaway
            // array and one redundant copy, at the single call site in ZDOMan.RPC_ZDOData - which
            // is once per received ZDO, the same rate as M15.
            EnablePacketReadFastPath = BindServerConfig("Allocation", "Enable Packet Read Fast Path", true,
                "Read each incoming ZDO's payload straight into the buffer that is about to hold it, instead of into a temporary array that is copied across and thrown away. Same bytes, same length, same read position - one array and one copy fewer per received ZDO. Pairs with the ZDO Deserialize setting above; both sit in the same method. Needs a restart to turn on; turns off immediately.");

            // --- M17: reuse the two packages ZDOMan.SendZDOs builds -------------------------
            // A ZPackage is a MemoryStream, a BinaryWriter and a BinaryReader, and the outer one
            // then grows to packet size by doubling - so two per call is roughly twenty objects,
            // most of them discarded buffers. Both are fully copied out before the call returns
            // (into ZRpc's own package, then again into the socket's send queue), so nothing
            // downstream can see that the instance was reused.
            //
            // The one assumption in the whole set lives here: that no other mod hooks ZRpc.Invoke
            // and keeps the package for a later frame. Nothing known does, and it is an odd thing
            // to do, but it is why this one is separately switchable.
            EnableSendPackageReuse = BindServerConfig("Allocation", "Enable Send Package Reuse", true,
                "Reuse the two packet buffers the send path builds, instead of constructing and discarding both on every send to every peer. The bytes sent are identical - both buffers are copied out before the send returns. Turn this off if another networking mod holds on to an outgoing packet past the frame it was sent in; nothing known does. Applies immediately, no restart needed.");

            // --- M18: typed RPC dispatch instead of DynamicInvoke ---------------------------
            // Every inbound RPC is delivered through DynamicInvoke, which walks the signature
            // reflectively and boxes its arguments, after GetParameters() has allocated a fresh
            // array and the argument list has been built and copied. The common shape by a wide
            // margin - ZDOData, RoutedRPC and most mod RPCs - is (ZRpc, ZPackage), which can be
            // called directly. Anything else falls through to the game's own path untouched.
            //
            // This is the widest of the four: it is on the delivery path of every RPC handler in
            // the game, including ones registered by Jotunn and by other mods. It is also the
            // largest CPU saving of the four, since a reflective invoke is not cheap.
            EnableRpcInvokeFastPath = BindServerConfig("Allocation", "Enable RPC Invoke Fast Path", true,
                "Call an incoming RPC's handler directly when its signature is the common one, instead of going through reflection for every message. Handlers with any other shape are untouched and keep using the game's own path. Handler exceptions are still reported exactly as before. This is the widest-reaching of the four settings - it is on the delivery path of every RPC in the game, mods' included - and also the largest CPU saving. Needs a restart to turn on; turns off immediately.");

            // --- M19: routed RPC relay written once per message -----------------------------
            // The host relays every broadcast RPC by building a fresh package for the message and
            // then calling ZRpc.Invoke per recipient, which copies the whole message again for each
            // one before the socket takes its own copy. The frame Invoke builds is identical for
            // every recipient, so it is written once and handed to each socket. The cost scales
            // with events x players, the same as the relay traffic the filter above is about.
            EnableRelaySendReuse = BindServerConfig("Allocation", "Enable Relay Send Reuse", true,
                "Write each relayed RPC (footsteps, hits, damage numbers, chat and the rest) once and hand the same bytes to every player it goes to, instead of rebuilding and re-copying the whole message for each one. The bytes sent are identical. Host-side only. Relayed messages no longer pass through ZRpc.Invoke, so a mod that watches Invoke to count traffic will not see them; nothing known does. Stands down alongside EnRoute or BetterZeeRouter. Applies immediately, no restart needed.");

            // --- Network monitoring ---------------------------------------------------------
            // A recorder for when something is wrong and counters cannot say what. Off by default,
            // and off means its hooks are not applied at all - see Runtime/Monitoring/Monitoring.cs.
            // Every setting here is read live, so it can be switched on for an evening and off
            // again without a restart.
            EnableMonitoring = BindServerConfig("Monitoring", "Enable Network Monitoring", false,
                "Record what the network is doing to BepInEx/NpsMonitoring, for sending to the mod author with a bug report. Every change of a creature's owner and whether it held, messages delivered to a player who no longer owned the target (a hit that does nothing), how regularly each player's creatures report in, and each player's ping and connection quality once a second. No player names, platform ids, addresses or chat - only session ids, object ids, positions to the metre and timings. Costs nothing while off: the hooks it needs are only applied while it is on. Switch it on, play through the problem, switch it off, send the folder. Applies immediately, no restart needed.");
            MonitoringCollectFromClients = BindServerConfig("Monitoring", "Collect From Clients", true,
                "While monitoring is on, also ask every player's game for what only it can see - what a creature was doing when it changed owner, who it was fighting, how long it took to pick its target back up, how far it jumped on screen, frame rate - and write that into the same files. Needs this mod on the client; clients without it are unaffected. A player can refuse with AllowMonitoringUpload in their own config.");
            MonitoringClientBytesPerSecond = BindServerConfig("Monitoring", "Client Upload Bytes Per Second", 2048,
                "The most each client may send the server in monitoring records, averaged over time. Records beyond it are dropped on the client and counted. Sent in 4KB batches that are held back whenever the client's link to the server is already near its send window, so they never delay the game's own traffic. For scale, the Send Window section's Target Rate is 150KB per second by default.", true, 256, 4096);
            MonitoringMaxDiskMB = BindServerConfig("Monitoring", "Max Disk MB", 4096,
                "The most BepInEx/NpsMonitoring may hold, across every session in it. Recording stops when it is reached and says so in the log. Nothing already recorded is ever deleted to make room. Files are compressed as they are closed.", false, 64, 65536);

            // Any server-side setting changing mid-session is worth a record of its own while
            // monitoring is on: it is what separates the two halves of an on/off comparison.
            Config.SettingChanged += OnAnySettingChanged;

            // Steam's networking config is process-global and re-writable at any time, so these
            // four take effect on edit rather than needing a restart. The two Send Window entries
            // are here as well because the coupling warning compares them against the transport
            // ceiling - changing either can turn that warning on or off without any Steam value
            // itself changing.
            EnableSteamTransportTuning.SettingChanged += OnSteamTransportSettingChanged;
            SteamSendRateMaxKBps.SettingChanged += OnSteamTransportSettingChanged;
            SteamSendRateMinKBps.SettingChanged += OnSteamTransportSettingChanged;
            SteamNagleMicros.SettingChanged += OnSteamTransportSettingChanged;
            EnableSendWindowSizing.SettingChanged += OnSteamTransportSettingChanged;
            SendWindowTargetRateKBps.SettingChanged += OnSteamTransportSettingChanged;

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
