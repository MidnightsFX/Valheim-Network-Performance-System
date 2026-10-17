# LeNouveauMonde: "loads part of the base, then nothing happens"

Logs: `message (19).txt` (client Awa, 14 Oct), `LogOutput (97).log` (client Zhuu, 15 Oct), `LogOutput (96).log` (the host's mod list; it has no Unity lines, so no timestamps or network events). Analysis date: 14 October 2026.

## In short

- **Awa's hang is not a network fault and not NPS.** Elite Creatures Reborn's built-in mod check ("Charter") gives a joining player 20 seconds to answer it. Awa's game was frozen for about 24 seconds building the world and its map while joining, so it could not answer. The host decided Awa had "no copy" of the mod (Awa runs the same 3.28.0) and disconnected them a second later.
- **Crossplay hides the disconnect.** The server is a crossplay (PlayFab) listen host. When a PlayFab host drops a player, vanilla sends the player nothing. Awa's game kept the part of the base that had arrived, spawned into it, and then waited. After 26 seconds of silence it started vanilla's "reset party" loop, which cannot recover (see below).
- **Zhuu's failed join (15 Oct) is a different problem.** The host's crossplay lobby could not be found. The logs we have cannot say why; the host's `Player.log` from that session can.

## Awa's join, second by second (Awa's clock)

| Time | What happened |
|---|---|
| 20:25:48 | Accepts the Steam invite; the lobby says the server is PlayFab-backed (crossplay on, `Dedicated: False`). |
| 20:26:01-02 | Joins the host's PlayFab Party network and connects. |
| 20:26:05 | Config sync, Charter's config push received, `Network version check` → the game starts generating the world. |
| 20:26:05-29 | **About 24 s with no frames.** World pre-generation and then the minimap (`Generating new world minimap done [12283ms]`). Both run on the main thread in vanilla. |
| ~20:26:25 | (host, estimated) Charter's 20 s deadline expires; the host sends the refusal and drops Awa 1 s later. |
| 20:26:30-31 | Awa's game catches up on what was waiting: global keys, map icons, configs, then `This server runs Elite Creatures Reborn 3.28.0; you have no copy`, refusal code `jcs6t4`. |
| 20:26:39 | Spawns at the logout point with whatever had arrived ("about 25% of the base"). |
| 20:26:57 | `Do a reset party as nothing seems to be received`. Vanilla fires this after 26 s with nothing at all from the host, so the last data arrived around 20:26:31. |
| 20:27:00-08 | Four more resets, each immediately after reconnecting. Awa quits at 20:27:08. |

Awa's PC is slow at this: SeneaL UI's JIT warm-up took 4.5 s for Awa, 1.3 s for Zhuu and 0.6 s on the host. Every join to this world freezes Awa's game for about as long, so **Awa will be refused every time** while the deadline stays at 20 s.

## Why: Charter's family check

Source: `ValheimModLibs/Charter` in [geraldjglasgow/ValheimMods](https://github.com/geraldjglasgow/ValheimMods/tree/main/ValheimModLibs/Charter) (current `main`; the copy inside ECR 3.28.0 may differ slightly).

- `GameHooks`: the server sends its mod list (`FamilyRpc.Greet`) from a postfix on `ZNet.RPC_PeerInfo`, i.e. at the moment the client's world generation starts.
- `FamilyRpc`: it resends every 5 s (`RetryInterval = 5f`) and gives up at 20 s (`MaxWait = 20f`). With any mandatory mod, giving up means comparing against an **empty** client list, so every mandatory mod comes out as "no copy".
- On refusal it sends `Charter_Refuse`, then calls `ZNet.instance.Disconnect(peer)` one second later (`Grace = 1f`).
- A late answer is still compared, but by then the player is gone.
- There is no admin setting to turn the check off or lengthen the wait (README: only `charter status/diff/versions/trace`).

A frozen game cannot answer: PlayFab sends are compressed on a worker thread and only handed to PlayFab in `LateUpdate`, so nothing leaves the client until the freeze ends. This would happen on Steam too; there the player would at least see the disconnect.

## Why the client then hangs (vanilla PlayFab)

From `ZPlayFabSocket` (game 1.0.17):

1. **A host-side drop is silent.** `ZNet.Disconnect` disposes the socket; a PlayFab socket sends nothing on close, and the host stays in the Party network. The client only notices through its own timeouts. (On Steam the client is told the connection closed.)
2. **Any final message can be lost.** `Dispose` throws away whatever is still waiting in the compression queue. Charter's refusal got through only because of its 1 s grace. A mod that sends an error and disconnects in the same frame never reaches a crossplay client.
3. **The client's recovery loops forever.** After 26 s of silence the client resets the Party connection. When it reconnects, `Connect` only refreshes the "last received" time for a *new* socket, so the very next update sees the same 26+ s and resets again. The log shows "Resume TX" and the next reset in the same second, four times. Nothing from the host can arrive in that window.
4. Even without (3) there was nothing to resume: the host had already discarded Awa's socket. A rejoin makes the host accept a new connection that waits for a handshake Awa's game will never send.

## What NPS did and did not do

- **Not involved in the drop.** NPS never disconnects peers. PlayFab players get vanilla's 10240-byte send window (no transport RTT on PlayFab, so `SendWindow` stands down). The host's join timeout is 90 s, and Awa had not spawned yet. Awa does not run NPS.
- **Mostly idle on crossplay.** Send window sizing, the send rate and RTT-based features need a Steam connection. On a crossplay server every connection is PlayFab, even between Steam friends.
- SteadyFrame's `[portal] the round budget in SendZDOs ...` error on the host only means SteadyFrame turned off its own send boost because NPS already rewrites that method. Harmless.

## Zhuu, 15 Oct 05:00 (Zhuu's clock)

- Steam invite at 04:59:56. The game then searched for the host's crossplay lobby (`string_key1 = host id and string_key2 = True`) four times over 7 s and got nothing: `Failed to locate network session` → `ErrorConnectFailed`.
- So at 05:00 the host's PlayFab lobby either did not exist or was not active. In vanilla that happens while a newly started world is still registering, after the host's PlayFab registration fails (`PlayFab ... failed` then `Unregister PlayFab server`), or while the host recovers from a Party network error.
- The host log we have is BepInEx-only (`[Logging.Disk] WriteUnityLog` off), so it shows none of that. **Needed:** the host's `%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\Player.log` (or `Player-prev.log`) from a session where someone could not join. Look for `PlayFab`, `lobby`, `Unregister`, `network error`, `Charter`.
- Zhuu runs NPS 1.17.0 too; nothing NPS-related happens before the lookup fails.

## Recommendations

For the group, now:

1. **If nobody plays on Xbox or Game Pass, host with crossplay off.** Everyone in these logs is on Steam. Without crossplay there is no PlayFab lobby lookup, drops are visible instead of hangs, there is no reset loop, and NPS's transport features apply.
2. **Awa cannot get past ECR's check from a slow join** until its deadline changes. Workarounds: anything that shortens Awa's join freeze (other programs closed), or removing Elite Creatures Reborn from the server and all clients.
3. Send the host's `Player.log` for refusal code `jcs6t4` (it should contain `refusing ... refusal code jcs6t4`) and for Zhuu's failed lookup.

For the Elite Creatures Reborn author (MilkyTeam / geraldjglasgow):

- Start Charter's 20 s wait when the client can answer (for example after its first message following the greeting), or make it at least as long as the game's own join timeout (90 s for crossplay). Joining freezes the client's main thread for world and minimap generation, which is 20-25 s on slower PCs.
- When the deadline expires with no answer, say "no answer in time" rather than "you have no copy".

Possible NPS changes (not built):

- **Host:** tell a crossplay player when the server drops them. Send vanilla's `Disconnect` RPC, compressed and handed to PlayFab synchronously, before the socket is disposed. Vanilla clients already handle it (back to the menu, where Charter's refusal text is shown). Helps every mod that refuses or kicks players.
- **Client (NPS installed):** break the reset-party loop by counting a successful reconnect as data received, so the host gets the normal 26 s to resume.
