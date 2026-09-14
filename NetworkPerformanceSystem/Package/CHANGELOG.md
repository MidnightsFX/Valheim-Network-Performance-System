# Changelog

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
