# Changelog

**1.2.0**

Configurable connection timeouts. Vanilla drops a quiet connection after 30 seconds, which is not
enough for a slow or distant link mid-join, and it enforces that at two layers independently - so
raising only one of them changes nothing. The new `Connection Timeout` section writes both, plus
the handshake timeout Steam applies before a connection exists and the longer allowance the game
already gives itself during a crossplay world transfer. Everything ships at its vanilla value, so
enabling the section on its own changes nothing; `nps_stats` shows what is actually in force.

**1.1.0**

Initial release!
