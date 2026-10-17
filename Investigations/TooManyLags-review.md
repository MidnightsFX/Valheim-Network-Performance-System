# TooManyLags: what it does, and what NPS could take from it

Reviewed 2026-10-13 from `Valheim_Stuff/OtherModders/TooManyLags`, version 1.0.24. That is the
FiresGhettoNetworking addon, by Botaniclas. It also contains the code of its Standalone edition as
its "player simulation" mode. TooManyLags paths below are relative to that folder.

## In short

- **What it does.** TooManyLags (TML) gives each player ownership of the interactive objects right
  around them and in the direction they are looking. Doors, chests, stations and pickables then
  answer at once.
  - Where two players' areas overlap, the server owns those objects instead.
  - A hostile creature is owned by the player it is attacking.
- **What it depends on.** The main mode needs FiresGhettoNetworking (FGN) with server-side
  simulation: the dedicated server loads and runs everything around every player. NPS refuses to
  load next to FGN, so this edition can never run alongside NPS.
- **How far it is proven.** It is carefully built and heavily tested offline. No version claims a
  live multiplayer test; the only in-game evidence is user bug reports. Its extra handoff
  protocol left doors and harvesting stuck across several versions, and has been opt-in since
  1.0.23.
- **The grid does not suit NPS.** It moves exactly the objects NPS deliberately leaves alone:
  containers, stations, doors and portals. Doing that safely took consent messages, leases,
  fences, relays, and the mod on every client.
- **What is worth taking:**
  - two small server-side fixes: faster item pick-ups, and more owner-addressed requests through
    the router;
  - better creature handoffs: keep the target, never move a creature mid-swing, and prefer the
    attacked player in shared fights;
  - ping measured inside the game rather than read from the socket;
  - several of its release-verification habits.

## How it works

### The ownership grid

The grid covers doors, chests, stations, pickables and idle tames. All of it runs on the
dedicated server only (`src/AuthorityController.cs:80`).

**Cells and coverage**
- The world is cut into 4 m cells. 6.4, 8 and 16 m are also allowed.
- Each player covers a rounded footprint around their cell, about 14 m across at the defaults, minus
  most of what is behind them (`src/AuthorityGrid.cs:117-185`).

**Who gets a cell** (`src/AuthorityGrid.cs:358`):

| Players covering the cell | Owner |
|---|---|
| One | That player |
| Two or more | The server; without server simulation, the lowest-ping player, with a 30 ms margin for the current owner |
| None | Nobody changes anything |

**Delays by distance.** A new claim waits longer the further out the cell is:

| Cells | Delay |
|---|---|
| Six inner cells | 0 s |
| Row ahead | 0.2 s |
| Everything else | 3 s |

- After a shared cell goes to the server, new decisions for it are ignored for 1 s. When that
  time runs out nothing happens; only the next real movement looks at the cell again
  (`src/AuthorityGrid.cs:310-314,338-343`).

**Direction comes from the camera, not the character.**
- The client samples the camera in a `GameCamera.LateUpdate` postfix every 0.05 s.
- It writes one of 56 precomputed coverage shapes as an int into a session-only key on its own
  player ZDO, and calls `ForceSendZDO` when that int changes (`src/CameraPublisher.cs:29-55`).
- The key is registered with `ZDOExtraData.AddSessionHash`, so it is never saved to the world.
- The shapes come from eight directions of ±37.5°, so one or two directions are active at once.
  Turning predicts 0.1 s ahead and adds one more direction (`src/CameraPrediction.cs`).

**The server does work only when something changes.**
- It polls every 0.05 s, from a prefix on `ZDOMan.ReleaseZDOS`.
- A player's footprint is recomputed only when their cell or direction changes.
- Objects sit in buckets per cell, so a cell that stays put never walks its objects
  (`src/AuthorityGrid.cs:392-445`, `AuthorityWorld.Tick` at `:529-569`).

**Players on their own are left to vanilla.**
- Two players count as sharing if any of their reference or character zones are within 4 zones of
  each other.
- Only zones within 2 of a sharing player get the grid.
- With one connection, every lookup is skipped (`src/AuthorityController.cs:122-135`,
  `src/RegionContentionGate.cs`).

**Vanilla stays out of managed objects.** A transpiler replaces the `zdo.Persistent` read in
`ZDOMan.ReleaseNearbyZDOS`. While an object is managed, protected or mid-transfer, that read
returns false, so vanilla neither releases nor claims it (`src/OwnershipTranspiler.cs`,
`src/AuthorityController.cs:289-305`).

**Object kinds.** There are 24 categories, each classified once per prefab from its components
(`src/ObjectCatalog.cs`).
- Loose item drops are deliberately left to vanilla.
- Optional server-owned modes (off by default):
  - plain building pieces (Piece, WearNTear and ZNetView only, no Rigidbody);
  - refill stations, permanently;
  - every interactive object outside player coverage.

### Hostile creatures

TML has two separate implementations:
- **With FGN's server simulation**, it rewrites FGN's own creature handoff.
- **Without it (the Standalone code)**, it runs its own two-phase handoff.

**The owner is the player the creature is attacking.**
- On the client, every store to `MonsterAI.m_targetCreature` is transpiled into a call. Changes
  are combined per creature and sent on the next frame (`src/FgnTargetEvents.cs:52-159`).
- Each report is stamped with the creature's `OwnerRevision`. It is written to session-only ZDO
  keys and also sent as an RPC.
- The server accepts a report only from the current owner at the current revision, and moves the
  creature straight away rather than waiting for its 2 s pass
  (`src/FgnCombatOwnership.cs:514-533`).

**Never mid-swing.**
- A handoff is refused while the creature is attacking or staggering.
- A one-shot retry is attached to that attack, keyed on its `Attack` object, and fires from an
  `Attack.Stop` postfix. The creature moves the frame after the swing ends.
- FGN's 6 s cooldown for a refused handoff is cancelled in this case only
  (`src/FgnAttackRetry.cs`, `src/FgnCombatOwnership.cs:545-585,970-1022`).

**High-ping exception.**
- It applies when the target's ping is over 200 ms and another modded player is within 6 m.
- The creature then goes to the server, or without server simulation to a player at least 30 ms
  better.
- If nobody else is close, it stays with the target.

**The new owner keeps the creature's aim** (Standalone path).
- It sets `m_targetCreature`, resets `m_timeSinceSensedTargetCreature` and calls `SetAlerted(true)`
  (`src/PlayerSimulation/LocalAgent.cs:187-194`).
- Hits that still arrive at the old owner are forwarded for 5 s.

**Before the server takes a creature**, it checks:
- area readiness at both the ZDO position and the live transform position;
- if PUP FPS is installed, that PUP's merged colliders are built there
  (`src/FgnCombatOwnership.cs:900-916`, `src/CellReadiness.cs`).

**Excluded:** bosses, tames, anything ridden, and anything whose faction is `Players` (magic
summons, roots). Nothing happens unless at least two eligible players have the creature loaded.

### Keeping handoffs safe

**Never moved while in use:**
- an open container (`s_inUse`);
- a container for 5 s after an open request;
- a station for 10 s after a fuel or item request, tied to the owner and revision at that time;
- `s_user`, a cart in use, a ridden or following tame;
- private or multi-inventory containers, and incinerators
  (`src/AuthorityController.cs:460-494`).

**Before any owner write**, the target player must have the whole cell inside their active area,
and the server's copy of the area must be loaded (`:313-332`).
- **Chests** move through vanilla's own `RPC_RequestOpen` at the old owner, so vanilla's in-use
  check does the vetting (`src/NativeInventoryHandoff.cs`).
- **Other player-owned objects:** the old owner is asked to give the object up.
- **Server-owned objects:** the server calls `ForceSendZDO(target)` and then `SetOwner`.
- A timeout never lets the server simply take an object.

**Request router** (`src/RequestRouter.cs`). Owner-addressed RPCs on a per-category list
(`src/ObjectCatalog.cs:131-148`) are re-aimed:
- If the addressed owner is stale, a copy goes to the current owner.
- The copy is held until the server has sent that owner its current owner revision, checked from
  `ZDOPeer.m_zdos`, and is flushed in a `SendZDOs` postfix.
- Container requests that wait 5 s get an explicit "no" reply (`RPC_OpenResponse(false)` and the
  others).

**Opt-in since 1.0.23**, the "experimental interaction protection":
- tokenised commits, fences and status probes;
- the new owner confirming it holds the object at the right revision, with its area loaded;
- clients relaying back requests they cannot run.

### Settings and operations

**Every setting except logging is decided by the server.**
- Clients do nothing until the server has sent its startup flags and camera settings.
- Clients put everything back on disconnect (`src/ServerSession.cs`, `src/ConfigConsole.cs`).
- Each version of the settings RPC has its own name (`_1` to `_4`), so old and new clients are
  both served.

**`tml_config` console** (`src/ConfigConsole.cs`, `src/ConfigCommands.cs`):
- `list`, read, and set.
- `list` shows "restart pending" next to settings that only take effect after a restart.
- Admins come from vanilla's adminlist. Requests are limited to 4 per second, 256 characters.
- A failed save is rolled back.

**Overrides FGN's settings for the session.** It writes non-saving replacement `ConfigEntry`
objects into FGN's static fields. On the way out it restores a field only if it still holds TML's
own entry (`src/FgnSessionSettings.cs`).

### Testing and release checks

- **Offline Harmony audit with Cecil** against the real `assembly_valheim` and the real FGN DLLs
  (`tests/NativeReleaseAudit.cs`):
  - every patch target must resolve to exactly one method;
  - every injected parameter must match its real type;
  - the total must be exactly 50, as a tripwire.
- **Transpiler checks:**
  - each runs on the real IL;
  - running one twice must fail;
  - two transpilers on the same method must work in either order;
  - production code also runs its transpilers on the original IL before creating any Harmony
    instance, and a failure turns that feature off.
- **Fixtures** compile production files against stubs. Some also run on Valheim's own Mono, via
  `mono_jit_init` from `MonoBleedingEdge/EmbedRuntime` (`tests/CombatMonoFixtureHost.cs`).
- **Release gate.** `verify.ps1` hashes the DLL, runs 18 test groups, and records the hash in
  `verification.json` along with `liveMultiplayerTested=false`. Packaging refuses a DLL whose hash
  differs.

## Side by side with NPS

| | TooManyLags | NPS |
|---|---|---|
| Doors, chests, stations, portals near a player | Moved to that player; to the server when shared | Stay with whoever has them ([OwnershipArbiter.cs](../NetworkPerformanceSystem/Runtime/OwnershipArbiter.cs) tier comments); requests re-aimed and items refunded instead |
| Pickables, rocks, trees | Pickables moved by the grid (cells about 14 m out) | Tier 2, nearest player within 32 m, 15 s hold, 8 m stand-off |
| Contested objects | Server (needs FGN server simulation), or lowest ping + 30 ms | Lowest staleness cost (tier 1), nearest (tier 2) |
| How often it decides | Every 0.05 s, only on a cell or direction change | Every 2 s, over every zone near a player |
| Hostile creatures | The player they attack | Lowest staleness; sole nearby player; followers with their leader; FPS and upload allowance |
| Who needs the mod | Everyone, for handoffs; players without it keep vanilla ownership | Server-only works; vanilla clients get most of it |
| Ping | FGN app-level echo, socket as fallback; 0 means unknown | Socket RTT; unmeasured priced at 150 ms |
| Requests sent to a stale owner | Re-aimed, held until the new owner has been sent its ownership | Same idea (M12), plus item refunds and creature, structure and terrain families |

Both mods landed independently on the same rule for re-aimed requests: hold a request until
`ZDOPeer.m_zdos` shows the new owner has been sent its ownership. That is a good sign for M12's
design.

## Worth borrowing

### Small, server-only

**1. Send a dropped item to its new owner at once.**
- **What TML does.** When the owner of a loose `ItemDrop` changes to a connected player, the server
  force-sends that drop to them (`src/AuthorityController.cs:262-268`).
- **Why it matters.** In vanilla, the requester sends `RPC_RequestOwn`, and the old owner hands
  ownership over. The change then travels old owner → host → requester in the host's normal send
  order. Until it arrives, the requester spins on "Im still nto the owner".
- **NPS evidence.** The 2026-09-30 hosted-server log had pick-up waits of 11 s and 51 s.
- **How it would fit.** The requester already holds the drop, so this meets `ForceSendIfHeld`'s
  rule. It would hook where the host applies the incoming owner, next to M25. It pairs with the
  still-open idea of routing `RPC_RequestOwn` through the router. Cost: one force-send per
  pick-up.

**2. Re-aim more owner-addressed requests in RpcOwnerRouter.** TML's list
(`src/ObjectCatalog.cs:131-148`) is a ready-made starting point for what M12 does not cover yet:

| Object | Requests |
|---|---|
| Door | `UseDoor` |
| Pickable | `RPC_Pick` |
| Beehive, sap collector | `RPC_Extract` |
| Container | `RPC_RequestOpen`, `RPC_RequestStack`, `RPC_RequestTakeAll` |
| Item and armour stand | `RPC_RequestOwn`, `RPC_DropItem` |
| Portal | `RPC_SetTag` |
| Bed | `SetOwner` |
| Ward | `ToggleEnabled`, `TogglePermitted` |

- **Why it matters for NPS:**
  - Tier 2 moves pickables between players, so `RPC_Pick` has the same stale-copy window that
    station items had.
  - Containers, doors and item stands still have vanilla's walk-away window, which the router's
    own comments describe for stations.
- **Shape of the change.** None of these carry an item, so this would be a re-aim-only family with
  no refunds. The safety argument is the stations' one:
  - each handler is gated on `IsOwner`;
  - tier 2's 8 m stand-off keeps the old owner out of reach.
- **Before adding each one,** check its handler's owner gate in the decompiled source, as was done
  for stations.
- **Optional.** When a held container request runs out, reply `RPC_OpenResponse(false)` as TML
  does (`src/RequestRouter.cs:170-202`). Check what the client shows for that reply first.

**3. Cap owner writes at the top revision.**
- **The problem.** `OwnerRevision` is a ushort that resets at each server start. An update that
  only changes the owner is accepted only with a higher revision (decompiled `ZDOMan.cs:1162-1170`).
  An object stuck in an ownership loop for a whole session would wrap to 0, and peers would then
  ignore its owner-only updates.
- **What TML does.** It refuses to write at 65535 (`src/AuthorityController.cs:559,590`,
  `src/StaticBuildings.cs:70`).
- **For NPS.** One check in the arbiter's write path. Low priority, but the zone-edge and
  support-stamp loops were exactly this kind of object.

### Needs the mod on clients

**4. Keep a creature's target across a handoff.**
- **The problem.** A MonsterAI's target exists only on the owner's machine; the ZDO says that
  there is a target, not who it is. The new owner starts blind and has to sense again.
- **What TML does.** The old owner writes the target's ZDOID into a session-only key whenever it
  changes. The new owner adopts it.
- **What NPS already has.** Client monitoring records `c_reacq`: how long a creature gained with a
  target takes to have one again ([MonitoringClient.cs:482](../NetworkPerformanceSystem/Runtime/Monitoring/MonitoringClient.cs)).
- **Next step.** Look at that data first. If fights show long gaps, this is a client-only change.

**5. Prefer the attacked player in shared fights.** The creature's swing and the player's dodge
or block then run on the same machine, with nothing in between.
- **What NPS already covers.** The proximity rule gives a player on their own their creatures; this
  only changes shared fights, where NPS picks by staleness.
- **What NPS can measure first:**
  - `c_target` records each creature's target. Joined with ownership flips, it shows how often in
    shared fights the owner is not the target.
  - Outgoing flips already flag `inAttack` and `staggering`
    ([MonitoringClient.cs:457](../NetworkPerformanceSystem/Runtime/Monitoring/MonitoringClient.cs)),
    which counts handoffs made mid-swing.
- **Pieces to take from TML:**
  - the `m_targetCreature` store transpiler, which needs no polling;
  - reports stamped with `OwnerRevision`;
  - the move-after-`Attack.Stop` retry.
- **Watch out.** TML has no dwell time for target changes, so creatures can bounce while players
  trade aggro. NPS's min hold would cover that.

**6. Measure ping inside the game.**
- **What FGN and TML do:**
  - an app-level echo first, socket ping as fallback, and a socket ping of 0 treated as unknown;
  - smoothed with weight 0.3, and unknown again after 10 s of silence;
  - players re-ranked only when a value crosses a 10 ms step, with a 30 ms margin for the current
    owner.
- **For NPS.** This fits two open problems: LAN peers that report 0 ms and are priced at 150 ms,
  and PlayFab peers with no measurement. It backs the app-level RTT already proposed on 2026-09-27.
- **Caveat.** The FGN source in `OtherModders` is 1.1.0 and has no `ConnectionEcho`; reading it
  would need a 1.5.x DLL.

### Tooling

**7. Release checks NPS does not have yet.** NPS already lets each mechanism stand down on its
own (`PatchGuard`, `IlMatch`). Additions worth considering:

| Check | Why |
|---|---|
| Run transpilers on the original IL at startup, before any patch is made | A mismatch never leaves a feature half-applied |
| An offline Cecil audit of every patch target and injected parameter, with an exact count | Run against both the client and the dedicated-server DLLs, which differ |
| A composition test: two transpilers on one method, in both orders | The ZenMap `RPC_ZDOData` collision was this kind of bug |
| Run the offline harness on Valheim's embedded Mono (`mono_jit_init`) | Runtime and class libraries then match the game; may avoid the PowerShell 5.1 / net8 split |
| Record the DLL hash at verification and refuse to package anything else | The shipped DLL is the tested one |

**8. An admin console for settings.** An `nps_config` built like `tml_config`:
- list, read and set, showing restart-pending values;
- admins from vanilla's adminlist, with a rate limit;
- rollback on a failed save.

### Techniques to keep in mind

- **Session-only keys on the player's own ZDO as a hint channel.** Register with
  `ZDOExtraData.AddSessionHash`, then call `ForceSendZDO` when the value changes. There is no new
  RPC, and it rides an object that is sent constantly anyway. It reaches every peer, which is fine
  for a small int.
- **TML's "sharing" rule.** Any reference or character zone within 4 of another player's is a
  close cousin of M40's planned "alone" verdict. So is its shortcut for a single connection.
- **Weighting by facing.** Objects ahead of the player are what they are about to reach. Tier 2
  could use facing to avoid moving things behind the player. Low priority.
- **Read owner and revision as a pair.** TML defers its owner-change logic until `RPC_ZDOData`
  has finished, so it never acts on half an update. M25 reads both from the packet and does not
  need this, but any NPS postfix on `SetOwnerInternal` would.

## Not worth copying

- **Moving containers, stations, doors and portals between players and the server.** It took
  consent messages, leases, fences and relays. It caused stuck interactions, and its extra
  protection is now opt-in. NPS's approach is cheaper and works for vanilla clients: leave the
  owner in place, and fix the requests on the host.
- **Server-side simulation.** FGN's `ZNetScene.CreateDestroyObjects` prefix makes the dedicated
  server instantiate everything around every player, which is a large CPU cost. Without it, TML's
  server-owned modes make no sense: nobody would run those objects.
- **Ownership driven by the camera every 0.05 s.** Turning the camera can change owners, and TML
  added a 1 s cooldown to damp it. NPS's 27 Sep data showed what owner changes cost: 38k from
  players standing on zone edges.
- **Hard pins.** Examples:
  - exact IL instruction counts in transpilers;
  - another mod's DLL pinned by hash (any PUP other than 1.0.34 turns its server fallback off);
  - FGN's refusal reasons matched as strings.

  Any update elsewhere switches features off.
- **Every client needing the mod.** NPS working server-only is a strength to keep.

## Compatibility

- **The FGN edition** cannot run next to NPS, because NPS refuses to load with FGN.
- **TooManyLags Standalone** (`Botaniclas/TooManyLags_Standalone`, not in `OtherModders`) has no
  FGN dependency.
  - It hands doors, chests, tames and hostile creatures from player to player, through its own
    messages, so it would fight NPS's arbiter and M25 over the same objects.
  - Worth adding to NPS's incompatible list once its plugin id is confirmed from the package. The
    embedded copy here uses `botanic.toomanylags.fgnaddon.playersimulation` only as a Harmony id.
