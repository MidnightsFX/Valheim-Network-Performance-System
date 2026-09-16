# Changelog

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
