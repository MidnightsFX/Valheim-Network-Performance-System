# NetworkPerformanceSystem

Network Performance Systems is a general networking overhaul, designed to tackle the problem created by high ping.

In Valheim, most objects are simulated by users. So a single high ping user in an area can make everything or all sorts of things feel terrible.
This mod aims to address that in a number of ways.

## Why this is different from the other networking mods

Every other Valheim networking mod treats multiplayer performance as a **bandwidth** problem —
compression, bigger buffers, higher send rates, filtered broadcasts. That is the right diagnosis for
ten people in one base saturating a shared cap. It does not handle a larger distributed server, or multiple players from
vastly different goegraphic locations.

NPS addresses these issues in a number of ways

- Each peer gets their own Round Trip timing (RTT), Healthcheck and bandwidth estimates
	- This allows dynamic adjustments to route around a poor connection
	- This higher ping players with decent bandwidth allow the server to send more aggressively to these players in compensation
	- Object ownership can be dynamically adjusted around players with poor connections to provide the best experience for all players in the area
- Objects which need network calls to interact with get ownership transferred ahead of time, delay scenarios much less likely
- Objects that change ownership during active requests get their requests properly re-targeted against the new owner
- Objects which are locally important (footsteps for example) are only network broadcast locally, instead of globally around the server
- Server Garbage collection churn is significantly reduced compared to vanilla

## Installing

**Server-only works.** Vanilla clients get the send window, ownership, scheduler, station-request and creature-hit
fixes with nothing installed on their end, and the server follows where they actually are rather than where they
were two seconds ago.

**Installing on clients too** adds latency compensation, live position reporting and the clean exit
from a dead session for those clients, and lets a ship they own pass to whoever takes its helm.
Mixed groups are fine — benefits are per-player, and a client without the mod behaves
exactly as vanilla. There is **no version lock**: nobody gets kicked for not having it.

Works on dedicated servers and on player-hosted games.

## Diagnosing and Reporting a problem

An easy way to start is enabling the nps_stats display Run `nps_stats_collect` (needs `devcommands`, since sampling costs a Steam call per peer per tick), play, then `nps_stats`.

A player the link-pressure table marks as `LOSSY` is already being dealt with: the server steps that one player's send rate down until their connection stops losing packets, and back up once it is clean, without changing anyone else's rate (`Steam Transport / Enable Loss Backoff`). The "Loss backoff" block shows who is backed off and by how much.


> **Lag, rubber-banding, hits not landing? Send a report.**
>
> **Server owners:** in `BepInEx/config/MidnightsFX.NetworkPerformanceSystem.cfg`, set
> `Enable Network Monitoring = true` under `[Monitoring]`. You do not need to restart, and an admin
> can also change it in game. Play through the problem, set it back to `false`, and zip the newest
> folder in `BepInEx/NpsMonitoring/`.
>
> **Players:** your game sends nothing unless the server asks, and never sends names, platform ids,
> addresses or chat. To opt out anyway, set `AllowMonitoringUpload = false` under `[Client config]`
> in your own copy of that file. The server cannot override it. It only stops your game uploading:
> the server still logs its own side of the connection, such as your ping, under an anonymous
> session number.
>
> **Then** post the zip as a [GitHub issue](https://github.com/MidnightsFX/Valheim-Network-Performance-System/issues)
> or on the [Discord](https://discord.gg/Dmr9PQTy9m), with a description of what you saw: what
> happened, how often, roughly when, and how many players were on. See
> [Reporting a network problem](#reporting-a-network-problem) for what is recorded.

If creatures rubber-band, hits do nothing, or things freeze when players come and go, the server
can record what the network was doing so it can be diagnosed rather than guessed at.

1. On the server, set `Enable Network Monitoring` to `true` in the `Monitoring` section. No restart
   is needed, and it can be changed in game by an admin.
2. Play through the problem. An evening with the usual players is ideal.
3. Set it back to `false`.
4. Zip the newest folder under `BepInEx/NpsMonitoring/` on the server and send it, with a sentence
   about what players saw and roughly when, as a
   [GitHub issue](https://github.com/MidnightsFX/Valheim-Network-Performance-System/issues) or on
   the [Discord](https://discord.gg/Dmr9PQTy9m).

**What is recorded:** each time a creature changes owner and whether the change held, messages
delivered to a player who no longer owned the target, how regularly each player's creatures report
in, and each player's ping and connection quality once a second. Clients that have the mod add
what only they can see: what a creature was doing when it changed hands, who it was fighting, how
far it jumped on screen, and frame rate. That costs each client up to 2 KB per second of upload,
sent in small batches that are held back whenever the connection is busy with the game's own
traffic, so recording never delays it.

**What is not:** player names, Steam or platform ids, IP addresses, chat. Players appear only as
the random session number the game gives them for that connection, and positions are rounded to
the metre. Any player can refuse to send anything by setting `AllowMonitoringUpload` to `false` in
their own config.

While it is off it costs nothing: the hooks it uses are only installed while it is on. The folder
is capped by `Max Disk MB` (4096 by default, across all sessions). When the cap is reached recording
stops and the log says so; nothing already recorded is deleted, so clear out old sessions yourself.
`nps_stats` shows whether it is running and whether anything is being dropped.

## Incompatible with

Other networking mods that rewrite the same code: FiresGhettoNetworking, VBNetTweaks, SkadiNet,
BetterNetworking, Smoothbrain's Network, NetworkTweaks, WarheimNetwork, TimeoutLimit. BepInEx will
refuse to load this mod alongside them rather than leave you half-patched. TimeoutLimit in
particular is no longer something you give up: the `Connection Timeout` settings cover the same
ground, and unlike a timeout raised at one layer only, they move both of the places vanilla times
out at.

**Verified compatible** (different layers, no overlap): LeanNet, Compress, Scenic. ReturnToSender is
fine too — it already fixes the scheduler, so that one mechanism stands down and the rest keeps
working. EnRoute and BetterZeeRouter are fine in the same way: both rework the routed-RPC relay, so
relay filtering and relay send reuse stand down when either is present and everything else keeps
working.
Valheim Plus is fine too: it has its own player limit, so this mod's `Player Limit` settings are
ignored when it is installed and V+'s `maxPlayers` is the one in force.
ClientGhostWatchdog is fine as well: it does the client-side half of the ghost handling itself and
against its own timeout, so this mod's client watchdog stands down and leaves that mod's behaviour
in charge. The host-side half — evicting a ghost from ownership, which that mod does not do — keeps
running either way.

## Credits

- Disconnect ghosting from **[ClientGhostWatchdog](https://github.com/dreamwraith/Valheim-ClientGhostWatchdog)
by DreamWraith**.
- ZDO Redirect from BetterZeeRouter