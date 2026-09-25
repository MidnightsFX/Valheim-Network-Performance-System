# Changelog

**1.9.0**
- Creatures no longer get stuck with a player who walked away and came back.
- Fixed objects outside the normal world area changing owner several times every two seconds.
- A player whose packet loss does not improve when the server slows down for them goes back to full speed, and is not slowed down again until the server restarts.
- Peer simulation distance is now included in ownership calculation decisions

**1.8.0**
- Improves how tightly a players position is tracked to better arbitrate ownership
	- The server follows each player's character directly, as it arrives with the game's own object updates. New `Reference Position` setting **Use Character Position** (default on). Works for players without the mod and costs nothing on the wire.
- `Steam Transport`: **Send Rate Max KBps** and **Send Rate Min KBps** are replaced by a single **Send Rate KBps** (default 0, the game's 150).
	- The new setting is the exact rate the server sends to each player, and it is applied on the server only. Steam never slows down for a connection that cannot keep up, so budget the server's upload for players × rate. A player whose connection cannot take it loses packets instead of getting a slower stream.
	- `Send Window` **Target Rate KBps** is also no longer read: each player's window is now sized from the rate Steam actually uses for their connection.
- The server now slows down for a player whose connection is losing packets, one player at a time. New `Steam Transport` setting **Enable Loss Backoff** (default on), with **Loss Backoff Threshold** (default 0.95) and **Loss Backoff Floor KBps** (default 32).
	- Steam sends to every player at one fixed pace and never slows down for a player who cannot keep up; that player got packet loss.
	- When a player has received under 95% of what was sent for 10 seconds, their own connection's rate is stepped down by a quarter, again every 10 seconds while it stays lossy, never below the floor. Once clean for 60 seconds it is stepped back up one step at a time, until it follows Send Rate KBps again. Change rate is isolated to the impacted user.
	- Server-side, and needs Enable Transport Tuning. `nps_stats` gains a "Loss backoff" block, and the LOSSY verdict in the link-pressure table now says what has been done about it.
- `nps_stats_collect` also measures how often a send runs out of room before the end of its list, and whether creatures or players were among what was left for later. See the new "Send truncation" block.
- Creatures are now arbitrated, and before anything else. New `Ownership` setting **Arbitrate Creatures** (default on).
	- The game marks ships and players as moving objects but not creatures, so earlier prioritization of this caused them to be deprioritized instead.
	- `nps_stats` gains a "creatures" line in the Ownership block.
- Hits on creatures are delivered to whoever is simulating the creature. New `Routed RPC` setting **Route Creature Hits To Owner** (default on).
	- A hit sent to a player who no longer owned the creature, or to nobody because the attacker's game thought it had no owner, did nothing.
	- A creature nobody is simulating is handed to the player who hit it, and the hit follows.
	- Server-side, and works for players without the mod. `nps_stats` gains a "Creature hits" block.
- Ownership changes made by the server now stick. New `Ownership` setting **Reject Stale Owner Updates** (default on).
	- Server-side. `nps_stats` gains a "stale owner" line in the Ownership block.
- Network monitoring now records creatures.

**1.7.0**
- Players on a high-ping connection no longer see the world pause every 8 seconds while there is a lot going on.
	- `Queue Drain Interval Seconds` and `Queue Drain Floor Bytes` are no longer read and can be deleted from the config.
	- `nps_stats` "Third-party send queue thresholds" shows how many outside reads were adjusted and how much is kept out of them for each player.
- A creature with exactly one player near it now belongs to that player, whatever anyone else's latency is. New `Ownership` setting **Creature Proximity Ownership** (default on), with **Creature Proximity Radius** (default 48 m).
	- `nps_stats` gains a "proximity" line: kept, pulled, and rescues it redirected.
- Network monitoring: a recorder a server owner can switch on when something is wrong, to send with a bug report. New `Monitoring` section, **Enable Network Monitoring** (default off). See "Reporting a network problem" in the README.
	- Records each change of a creature's owner and whether it held, messages delivered to a player who no longer owned the target, how regularly each player's creatures report in, and each player's ping and connection quality. Clients with the mod add what only they can see, at up to 2 KB per second in small batches that always wait for the game's own traffic, and a player can refuse with `AllowMonitoringUpload`.
	- No player names, platform ids, addresses or chat. Players appear only as the session number the game gives them.
	- Costs nothing while off: its hooks are installed when it is switched on and removed when it is switched off, with no restart either way.
	- Written to `BepInEx/NpsMonitoring`, compressed, and capped by **Max Disk MB** (default 4096). Recording stops at the cap; nothing already recorded is deleted.
	- `nps_stats` gains a "Network monitoring" block.

**1.6.0**
- A player who has stopped answering no longer freezes everything they were simulating. New `Connection Timeout` setting **Evict Ghost Owners** (default on), with **Ghost Owner Evict Seconds** (default 10).
	- "Stop trusting this peer to simulate" is a different question and is now asked separately. A peer that goes quiet loses its objects to the players who are actually there, while keeping its slot for the full timeout so it can come back. Nothing is disconnected any sooner, and if it returns it competes for ownership again on the next pass.
	- Detection asks Steam directly - `SteamNetConnectionRealTimeStatus_t.m_eState`, which reports a dead link before anything closes the socket - and falls back to a silence timer for crossplay peers, which report no state. The timer discounts main-thread stalls rather than counting a frozen frame as the peers going quiet.
	- If every peer goes quiet at once, that is treated as a fault at the host's end and nobody is evicted until one answers.
	- This is what makes raising `Connection Timeout Seconds` safe: the wait now costs only the slot.
- Clients are no longer left playing a world the server has dropped them from. New `Client config` setting **EnableGhostWatchdog** (default on).
	- A warning with a countdown appears halfway to the timeout actually in force and clears itself if the connection comes back; at the deadline, or as soon as the transport calls the link dead, the character is saved and the game returns to the menu with an explanation rather than the generic "disconnected".
	- No timeout of its own - it follows the same deadline the game is using, so it cannot disagree with the server's setting.
	- Stands down automatically when ClientGhostWatchdog is installed.
- `nps_stats` gains a "Peer liveness" block: who is quiet, for how long, what the transport says, and the eviction and watchdog counters.
- Credit: the ghost handling started from [ClientGhostWatchdog](https://github.com/dreamwraith/Valheim-ClientGhostWatchdog) by DreamWraith. No code is shared; the observation that the game needs a second opinion on whether a peer is still there is theirs.
- Picking berries, mining ore and chopping trees no longer lag when another player owns the object. Ownership arbitration is now tiered: moving objects are placed by latency as before, and interactable-but-stationary ones by distance. New `Ownership` setting **Interactive Object Ownership** (default on).
	- Pickables, ore deposits, rocks, trees, logs and destructibles go to whoever is standing nearest them. These ask their owner to do the work and every handler checks it is the owner, so an object owned by another player costs four network legs per keypress - and placing by ping rather than by distance would not have helped, because nothing about a berry bush changes between interactions.
	- Building pieces, containers, crafting stations and portals are still never moved off a player who is present, which is what keeps the fermenter and smelter fix above intact.
	- An object is never taken from an owner still within reach of it, never taken for a player further away than `Interactive Claim Radius`, and each move is pushed to nearby players immediately.
	- An unowned patch - one nobody has visited - now goes to the nearest player rather than the lowest-ping one, on the first pass and with no delay.
	- Tuning: `Interactive Claim Radius`, `Interactive Challenge Margin`, `Interactive Min Hold Seconds`, `Interactive Max Reassigns Per Pass`. The tier has its own budget so a zone full of creatures cannot starve it, or be starved by it.
	- `nps_stats` gains per-tier ownership counters.
- Ships are now simulated by the player at the helm, so steering is no longer choppy when someone else on board owned the ship. New `Ownership` setting **Ship Ownership Follows Helmsman** (default on).
	- The ship's current owner hands it over when the helm is taken, the same way the game already hands over saddles and carts. This needs the mod on whichever machine owns the ship; a ship owned by a player without it behaves as in vanilla.
	- The host gives an abandoned ship (its owner left or disconnected) to the player at its helm first.
	- `nps_stats` gains a "Ship helm" block with the handoff and helm rescue counts.

**1.5.0**
- Updates default send timing to prevent alternating delays from the dedicated server
- New `Allocation` section: four opt-in changes that remove short-lived objects from the ZDO network path, reducing how often the garbage collector runs. None of them alters a byte on the wire or a value in a ZDO.
	- **ZDO Deserialize Fast Path** (default on) - reads a received ZDO's fields directly instead of through the fourteen delegates the game allocates for every one.
	- **Packet Read Fast Path** (default on) - reads each incoming ZDO payload straight into the buffer that is about to hold it.
	- **Send Package Reuse** (default on) - reuses the two packet buffers the send path builds, instead of constructing and discarding both on every send to every peer.
	- **RPC Invoke Fast Path** (default on) - calls an incoming RPC's handler directly when its signature is the common one, instead of going through reflection for every message.
	-  **Relay Send Reuse** (default on): the host writes each relayed RPC once and hands the same bytes to every player it goes to, instead of rebuilding and re-copying the whole message per player.
	- `nps_stats` gains an "Allocation relief" block with a counter per mechanism and the game's own per-second ZDO send/receive rates to read them against.
- New `Routed RPC` setting **Limit Relay By Distance** (default on): stops relaying object RPCs such as building damage, fragments and ward flashes to players who visited that object earlier but are now too far away to have it loaded.

**1.4.2**
- Fixes Jotunn-based mods (Jotunn's own config sync included) disconnecting a peer with a 30 second "sending timeout" on higher-latency links (#3)
- New `Compatibility` section for mods with that threshold compiled in (ServerSync and mods bundling it, ConditionalConfigSync): a peer in sustained backlog has its queue briefly brought under the threshold every `Queue Drain Interval Seconds` so they can get their packet out.
	- `nps_stats` gains a "Third-party send queue thresholds" block showing Jotunn's limit as read back from Jotunn and the drain counters per peer.

**1.4.1**
- Fixes the player limit not applying to crossplay on dedicated servers.
- The Steam server browser now shows the configured limit for dedicated servers.

**1.4.0**
- Fixes items vanishing when inserted into a fermenter, smelter, cooking station, fireplace or similar in multiplayer. This is a vanilla bug made worse with more players.
	- Station item requests are now delivered to the object's current owner. This is best paired with the Valheim Community Patch, which addresses the other half of this.
- `nps_stats` ownership block gains a `static held` line: objects left with a present but not lowest-latency owner because they do not move.
- Valheim Plus compatibility: when V+ is installed its player limit is the one in force and this mod's `Player Limit` settings stand down instead of fighting over the same patch sites.

**1.3.0**
- Removes admin requirement from server commands
	-  nps_stats_collect requires devcommands
- Adds support for Playfab servers to increase connection limits beyond vanilla 10

**1.2.1**
- Bumps the maximum connection timeout limit to 600s


**1.2.0**

Configurable connection timeouts. Vanilla drops a quiet connection after 30 seconds, which is not
enough for a slow or distant link mid-join, and it enforces that at two layers independently - so
raising only one of them changes nothing. The new `Connection Timeout` section writes both, plus
the handshake timeout Steam applies before a connection exists and the longer allowance the game
already gives itself during a crossplay world transfer. Everything ships at its vanilla value, so
enabling the section on its own changes nothing; `nps_stats` shows what is actually in force.

**1.1.0**

Initial release!
