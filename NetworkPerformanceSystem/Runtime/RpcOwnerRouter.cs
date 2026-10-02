using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M12 - delivers owner-addressed RPCs to whoever owns the object NOW: a station's item
    /// requests, and hits on a creature (the second family, below).
    ///
    /// Fermenter.AddItem, Smelter.OnAddOre, CookingStation.CookItem, Fireplace.Interact,
    /// ShieldGenerator.UseItem and Turret.UseItem all take the item out of the player's inventory
    /// first and then call ZNetView.InvokeRPC("RPC_AddItem" / "RPC_AddOre" / "RPC_AddFuel" /
    /// "RPC_AddAmmo", ...). That overload addresses the RPC to m_zdo.GetOwner() - the SENDER'S
    /// copy of the owner id - and all but one handler on the far end begins
    /// "if (m_nview.IsOwner())" and otherwise returns without a word. Nothing acknowledges, nothing rolls back. So any
    /// moment at which the sender's owner id disagrees with the host's is a moment at which the
    /// item is simply gone.
    ///
    /// Vanilla has that moment on every walk-away: the owner leaves the station's active area,
    /// ZDOMan.ReleaseNearbyZDOS hands the station to someone present, and until that ZDO update
    /// reaches the remaining players their copies still name the old owner - who no longer even
    /// has an instance to run the RPC on. Between reference-position lag, the two-second release
    /// pass and the ZDO send, that window is seconds wide, and a player who logs off leaves it
    /// open for as long as the connection timeout.
    ///
    /// The host is the one place that always knows the current owner, and every routed RPC a
    /// client sends passes through it. So the host re-addresses these:
    ///
    ///   * Addressed to a stale owner -> forwarded to the current owner instead.
    ///   * Current owner is the host  -> handled here, exactly as if it had been addressed here.
    ///   * Current owner is absent    -> left the area or the session, or the station is unowned.
    ///     Ownership is handed to the requesting player, who is demonstrably present and holding
    ///     the instance, and the request is forwarded to them. That is the same decision the
    ///     ownership pass would reach within two seconds, taken now.
    ///
    /// Forwarding is gated on one more fact: the target must already have been SENT the ZDO
    /// update that names it owner, or the RPC arrives first and fails the same IsOwner check on
    /// the correct machine. ZDOPeer.m_zdos records the owner revision each peer has been sent,
    /// and ZDOData and RoutedRPC share one reliable, ordered stream per peer, so "sent before" on
    /// this side is "received before" on that one. A request whose target is not yet caught up is
    /// held and re-examined from a postfix on ZDOMan.Update - after that frame's sends - then
    /// forwarded the moment the recorded revision catches up. A short timeout forwards it
    /// regardless, which is no worse than vanilla - unless it carries an item, below.
    ///
    /// Why double delivery cannot happen: the sender only executes a routed RPC locally before
    /// sending it when its target is 0 (unowned). Every handler in RequestHashes but one is gated
    /// on IsOwner, so that local run was a no-op on a non-owner. The exception is
    /// CookingStation.RPC_AddItem, which fills a slot on whatever copy runs it. Vanilla's CookItem
    /// claims an unowned station before sending, so it never sends that with target 0; a caller
    /// that does has already filled the slot at home, and TryTake leaves the message to vanilla
    /// rather than deliver it a second time. RPC_RemoveDoneItem is left out for the same reason -
    /// it has no gate either, and a dropped one costs a second press of E, not an item.
    ///
    /// ITEMS THAT CANNOT BE DELIVERED ARE HANDED BACK. Some requests still find nobody to take
    /// them: nobody the host can see at the station (the owner has gone, and the host has not yet
    /// caught up with the requester arriving), the station destroyed while the request waited, or
    /// an owner still not sent its ownership when the hold runs out. Delivered anyway, each of
    /// those lands on a machine whose IsOwner check fails, and the item the caller has already
    /// taken out of the inventory is gone. Where the request says which item, the host makes it
    /// again instead, as a world drop where the station stands:
    ///
    ///   * RPC_AddOre (Smelter) and RPC_AddItem (Fermenter, CookingStation) carry the item's
    ///     prefab name - its hash, for the fermenter - and whether it was cheated. RPC_AddAmmo
    ///     (Turret) carries the name.
    ///   * RPC_AddFuel carries nothing, but a station burns one fuel: m_fuelItem on a Smelter,
    ///     CookingStation or Fireplace, and m_fuelItems on a ShieldGenerator when that list names
    ///     one item. A generator with a choice of fuels is not refunded; which one was spent is
    ///     not in the message.
    ///   * Only an item the station would have accepted, looked up in the station's own lists, so
    ///     a request the owner would have turned away never becomes an item it never was.
    ///
    /// A refund is safe from doubling for the same reason a forward is. The router only refunds a
    /// message it has taken over, which no other machine has received and which it will never
    /// deliver, and whose sender's local run, if there was one, was a no-op. The host creates the
    /// drop, so it works for vanilla clients; on a dedicated server the ownership pass hands it to
    /// the player standing there, as it would any other object.
    ///
    /// An item request that finds nobody at the station is not handed back at once. It is held
    /// like any other, for up to HoldTimeoutSeconds, for somebody to be there - usually the
    /// requester, whose position the host has not caught up with - and delivered the moment they
    /// are. A request without an item (tap, empty, toggle, a fuel amount), or with one the host
    /// cannot name, gains nothing from waiting, and goes to whoever the sender named, as vanilla.
    ///
    /// NOT GUARDED: capacity. Smelter.RPC_AddOre / RPC_AddFuel, CookingStation.RPC_AddFuel,
    /// ShieldGenerator.RPC_AddFuel and Turret.RPC_AddAmmo never check m_maxOre / m_maxFuel /
    /// m_maxAmmo on the owner, so a room check made on a stale copy overfills by one. Nothing is
    /// lost; the station works it off. The host could refuse from its own copy, but that copy lags
    /// the owner's: it would turn over-fills into refunds where the owner had room, and still miss
    /// two adds that arrive together. Fireplace.RPC_AddFuel, Fermenter.RPC_AddItem and
    /// CookingStation.RPC_AddItem do turn a full station away on the owner, and that item is lost
    /// - but after a correct delivery, where the router cannot see it.
    ///
    /// Only the components below, only their owner-addressed requests, only on the host. A
    /// client with this mod installed does nothing here, and a vanilla client benefits in full.
    ///
    /// CREATURE HITS take the same road, as a second family with its own switch and counters.
    /// Character.Damage addresses RPC_Damage exactly the way a station request is addressed, and
    /// Character.RPC_Damage drops it on a non-owner exactly as silently - but a creature changes
    /// hands far more often than a station, and while it has no owner at all a hit is sent to
    /// everybody and discarded by everybody. Measured on a 25-player server before this existed:
    /// 2.8% of all hits, 412 of them on creatures whose owner the attacker's copy gave as nobody,
    /// one of which took 87 hits over two minutes without any landing. Two rules differ:
    ///
    ///   * "The owner is present" means the owner still has the creature LOADED, not that it is in
    ///     the 3x3 presence square - the arbiter keeps a creature with an owner in the outer ring
    ///     of what that owner loads (OwnershipArbiter.OwnerStillLoads), and the router must agree
    ///     with it or it would take creatures off the players simulating them.
    ///   * An absent owner is replaced by the ATTACKER, anywhere in what they load. The attacker
    ///     hit the creature, so its game has the instance; a bow shot from the outer ring is as
    ///     good a claim as a sword swing. The claim is judged by the same near-ring test as the
    ///     owner's presence, so it is one the pass will keep rather than undo two seconds later.
    ///
    /// Double delivery is as impossible for hits as for station requests, with one cosmetic
    /// exception: RPC_Damage counts the attacker's hit statistic before its IsOwner check, so a
    /// hit the attacker sent to nobody (target 0), ran locally as a no-op, and is then forwarded
    /// back to it as the new owner, counts twice in that player's stats. The damage lands once.
    /// RPC_Stagger is deliberately NOT routed: it has no IsOwner check at all and runs on every
    /// receiver, so re-addressing it would take the stagger away from the others.
    ///
    /// STRUCTURE HITS are the third family: hits, repairs, removals and snow changes on the
    /// buildings, trees and rocks M33 holds updates for (StructureUpdates.IsHeldStructure).
    /// WearNTear's RPC_Damage, RPC_Remove, RPC_Repair and RPC_SetSnow, TreeBase's, MineRock5's and
    /// Destructible's RPC_Damage, and MineRock's "Hit" are all addressed through the sender's copy
    /// of the owner and dropped by a non-owner's IsOwner check; RPC_ClearCachedSupport names the
    /// owner from the sender's copy too. M33 lets a player's copy name an old owner for longer,
    /// so this family is what makes that safe, and it catches vanilla's own stale window besides.
    /// Presence is judged by the station test - the 3x3 square the ownership pass uses for "owner
    /// present" on anything that is not a creature - so a claim made here is one the pass keeps.
    /// No item rides on any of these, so nothing is refunded.
    /// </summary>
    internal static class RpcOwnerRouter {

        private const string RoutedRpcMethod = "RoutedRPC";

        internal enum Family : byte {
            Station,
            Creature,
            Structure,
        }

        /// <summary>What one family of requests needed correcting. "Re-targeted" and "claimed" are
        /// the deliveries vanilla would have lost outright; "held" is the cost of doing it safely.</summary>
        internal sealed class Counters {
            internal long Seen;         // owner-addressed requests inspected
            internal long Retargeted;   // target id rewritten to the current owner
            internal long Claimed;      // owner absent or none - ownership handed to the requester first
            internal long HeldCount;    // forwarded only after the target had been sent its ownership
            internal long Parked;       // item request with nobody at the station - waited for someone
            internal long Expired;      // hold timed out; item handed back, anything else forwarded regardless
            internal long Refunded;     // item made again as a world drop at the station instead of lost
            internal long Dropped;      // object or target gone while waiting, and no item to hand back
            internal float MaxHoldMs;

            internal void Clear() {
                Seen = 0;
                Retargeted = 0;
                Claimed = 0;
                HeldCount = 0;
                Parked = 0;
                Expired = 0;
                Refunded = 0;
                Dropped = 0;
                MaxHoldMs = 0f;
            }
        }

        internal static readonly Counters Stations = new Counters();
        internal static readonly Counters Creatures = new Counters();
        internal static readonly Counters Structures = new Counters();

        /// <summary>The owner-addressed creature requests that must land. Only RPC_Damage - see the
        /// class comment for why RPC_Stagger is not one of them.</summary>
        private static readonly HashSet<int> CreatureRequestHashes = new HashSet<int> {
            "RPC_Damage".GetStableHashCode(),
        };

        /// <summary>The owner-addressed requests on buildings, trees and rocks. RPC_HealthChanged,
        /// RPC_CreateFragments, MineRock's "Hide" and MineRock5's RPC_SetAreaHealth go to everybody
        /// and are left alone.</summary>
        private static readonly HashSet<int> StructureRequestHashes = new HashSet<int> {
            "RPC_Damage".GetStableHashCode(),              // WearNTear, TreeBase, MineRock5, Destructible
            "Hit".GetStableHashCode(),                     // MineRock
            "RPC_Remove".GetStableHashCode(),              // WearNTear
            "RPC_Repair".GetStableHashCode(),              // WearNTear
            "RPC_SetSnow".GetStableHashCode(),             // WearNTear
            "RPC_ClearCachedSupport".GetStableHashCode(),  // WearNTear, addressed by the sender's copy of a neighbour's owner
        };

        /// <summary>
        /// The owner-addressed requests the station components make. Anything they send to
        /// ZNetView.Everybody or to a specific non-owner (RPC_SetSlotVisual, RPC_SetFuelAmount,
        /// RPC_SetFuel) is deliberately absent: those are addressed correctly already. Matched by
        /// name hash, so a mod's own "RPC_AddItem" on some other prefab is out of scope until
        /// Station has confirmed the ZDO carries one of these components.
        /// </summary>
        // The requests that carry an item - see RefundItem.
        private static readonly int AddItemHash = "RPC_AddItem".GetStableHashCode();
        private static readonly int AddOreHash = "RPC_AddOre".GetStableHashCode();
        private static readonly int AddFuelHash = "RPC_AddFuel".GetStableHashCode();
        private static readonly int AddAmmoHash = "RPC_AddAmmo".GetStableHashCode();

        private static readonly HashSet<int> RequestHashes = new HashSet<int> {
            AddItemHash,                              // Fermenter, CookingStation
            "RPC_Tap".GetStableHashCode(),            // Fermenter
            AddOreHash,                               // Smelter
            AddFuelHash,                              // Smelter, CookingStation, Fireplace, ShieldGenerator
            "RPC_EmptyProcessed".GetStableHashCode(), // Smelter
            "RPC_AddFuelAmount".GetStableHashCode(),  // Fireplace
            "RPC_ToggleOn".GetStableHashCode(),       // Fireplace
            AddAmmoHash,                              // Turret
        };

        /// <summary>A station prefab's components, read once per prefab rather than once per
        /// request. Prefab fields - the conversion lists, the fuel and ammo items - are all a
        /// refund needs, so the host does not need an instance of the station.</summary>
        private sealed class StationKind {
            internal Fermenter Fermenter;
            internal Smelter Smelter;
            internal CookingStation CookingStation;
            internal Fireplace Fireplace;
            internal ShieldGenerator ShieldGenerator;
            internal Turret Turret;
            internal ItemDrop FuelItem;   // what an RPC_AddFuel here took from the inventory, if that is one item
        }

        /// <summary>Per-prefab verdict: its station components, or null for "not a station".</summary>
        private static readonly Dictionary<int, StationKind> StationPrefabs = new Dictionary<int, StationKind>();

        /// <summary>
        /// How long a forward waits for its target to be sent the ownership update before going
        /// out regardless. The update is force-sent, and AddForceSendZdos puts it at the front of
        /// the very next send to that peer, so in practice the wait is one send tick; the timeout
        /// only matters for a peer whose window is so saturated that nothing is going out at all,
        /// and then the request is no worse off than vanilla would have left it - or, if it
        /// carries an item, that item is handed back. An item request with nobody at the station
        /// waits as long for somebody to arrive.
        /// </summary>
        internal const float HoldTimeoutSeconds = 3f;

        /// <summary>A request the router has taken, with what it needs to deliver it later - or
        /// to hand its item back if it cannot be delivered.</summary>
        private struct Request {
            internal ZRoutedRpc.RoutedRPCData Data;
            internal Family Family;
            internal float QueuedAt;
            internal ItemDrop Refund;     // the item it took from the inventory, when the host can make it again
            internal bool Cheated;
            internal Vector3 At;          // where the station stood when the request arrived
            internal long ForcedFor;      // the owner its ZDO has been force-sent to, 0 if none yet
            internal bool Retargeted;     // already counted in Retargeted
        }

        private static readonly List<Request> Held = new List<Request>();

        private enum Outcome {
            Vanilla,   // nothing was changed; vanilla should carry on with the message as it was
            Done,      // forwarded, handled here, or put in Held
            Wait,      // still waiting for an owner, or for the owner to be sent its ownership (retry only)
            Refunded,  // not delivered; its item was handed back at the station instead (retry only)
        }

        // -- diagnostics -----------------------------------------------------------------------

        internal static int Waiting => Held.Count;

        private static Counters For(Family family) {
            switch (family) {
                case Family.Station: return Stations;
                case Family.Creature: return Creatures;
                default: return Structures;
            }
        }

        // -- entry points ----------------------------------------------------------------------

        /// <summary>
        /// Prefix on ZRoutedRpc.RPC_RoutedRPC, the host's entry point for every routed RPC a
        /// client sends. Peeks at the header without allocating; only a request of a family this
        /// router takes - a station request, or a hit on something that is a creature - is
        /// deserialized and taken over. Returns true when the message has been dealt with here
        /// and vanilla must not run; on false the package is rewound and untouched.
        /// </summary>
        internal static bool TryRouteIncoming(ZRoutedRpc router, ZPackage pkg) {
            if (!HooksLive(router) || pkg == null) { return false; }

            int saved = pkg.GetPos();
            try {
                pkg.ReadLong();                                              // m_msgID
                pkg.ReadLong();                                              // m_senderPeerID
                pkg.ReadLong();                                              // m_targetPeerID
                ZDOID targetZdo = pkg.ReadZDOID();
                int methodHash = pkg.ReadInt();
                if (!TryClassify(targetZdo, methodHash, out ZDO zdo, out Family family)) {
                    pkg.SetPos(saved);
                    return false;
                }

                pkg.SetPos(saved);
                ZRoutedRpc.RoutedRPCData data = new ZRoutedRpc.RoutedRPCData();
                data.Deserialize(pkg);

                For(family).Seen++;
                if (!TryTake(data, zdo, family, out Request request)) {
                    pkg.SetPos(saved);
                    return false;
                }
                long addressedTo = data.m_targetPeerID;
                if (Route(router, ref request, zdo, retry: false, expired: false) != Outcome.Vanilla) {
                    // A client's message taken over here never reaches RouteRPC, where network
                    // monitoring watches, so a creature hit is shown to it from here instead, with
                    // the peer the sender addressed it to rather than the one it went to.
                    if (family == Family.Creature && Monitoring.Active) { Monitoring.OnRoutedRpc(data, addressedTo, routed: true); }
                    return true;
                }
            } catch (Exception e) {
                // A throw here would land in ZRpc.Update's catch-all, logged without our name,
                // and the message would be lost. Fall back to vanilla instead.
                Logger.LogWarning($"Owner RPC routing fell back to vanilla: {e.GetType().Name}: {e.Message}");
            }

            pkg.SetPos(saved);
            return false;
        }

        /// <summary>
        /// Prefix on ZRoutedRpc.RouteRPC for the host's own sends. Returns true when the message
        /// has been dealt with here and vanilla must not run.
        /// </summary>
        internal static bool TryRoute(ZRoutedRpc router, ZRoutedRpc.RoutedRPCData data) {
            if (!HooksLive(router) || data == null) { return false; }

            try {
                if (!TryClassify(data.m_targetZDO, data.m_methodHash, out ZDO zdo, out Family family)) { return false; }

                For(family).Seen++;
                if (!TryTake(data, zdo, family, out Request request)) { return false; }
                return Route(router, ref request, zdo, retry: false, expired: false) != Outcome.Vanilla;
            } catch (Exception e) {
                Logger.LogWarning($"Owner RPC routing fell back to vanilla: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Is this a request one of the enabled families takes, and on what object? The method
        /// hash is checked first because it retires nearly every message for the price of two set
        /// probes; only a matching hash pays for the ZDO lookup and the prefab check. A hit on a
        /// player matches the hash and fails every prefab check, and is left alone. The families
        /// are tried in order - station, creature, structure - so an RPC_Damage on a creature is
        /// always a creature hit, and one on a wall or a tree is a structure hit.
        /// </summary>
        private static bool TryClassify(ZDOID target, int methodHash, out ZDO zdo, out Family family) {
            zdo = null;
            family = Family.Station;
            if (target.IsNone()) { return false; }

            bool station = RequestHashes.Contains(methodHash) && ValConfig.EnableStationRpcRouting.Value;
            bool creature = !station && CreatureRequestHashes.Contains(methodHash) && ValConfig.EnableCreatureHitRouting.Value;
            bool structure = !station && StructureRequestHashes.Contains(methodHash)
                && ValConfig.EnableStructureHitRouting != null && ValConfig.EnableStructureHitRouting.Value;
            if (!station && !creature && !structure) { return false; }

            zdo = ZDOMan.instance?.GetZDO(target);
            if (zdo == null) { return false; }

            if (station) {
                family = Family.Station;
                return Station(zdo) != null;
            }
            if (creature && OwnershipPolicy.IsCreature(zdo)) {
                family = Family.Creature;
                return true;
            }
            family = Family.Structure;
            return structure && StructureUpdates.IsHeldStructure(zdo);
        }

        /// <summary>
        /// The request the router will carry for this message, or false to leave it to vanilla.
        /// A station request is told here what it carries, while its station is certainly still
        /// there to be read. The one refusal: a CookingStation.RPC_AddItem sent to nobody (target
        /// 0) has already filled a slot on its sender, which ran it before sending - see the class
        /// comment - so it is not the router's to deliver again, or to refund.
        /// </summary>
        private static bool TryTake(ZRoutedRpc.RoutedRPCData data, ZDO zdo, Family family, out Request request) {
            request = new Request { Data = data, Family = family };
            if (family != Family.Station) { return true; }

            StationKind station = Station(zdo);
            if (station == null) { return false; }
            if (data.m_targetPeerID == 0L && data.m_methodHash == AddItemHash && station.CookingStation != null) { return false; }

            request.Refund = RefundItem(station, data, out bool cheated);
            request.Cheated = cheated;
            request.At = zdo.GetPosition();
            return true;
        }

        /// <summary>
        /// Postfix on ZDOMan.Update, so it runs after this frame's SendZDOs have both recorded
        /// what each peer was sent and queued it on the socket. Anything forwarded from here
        /// therefore lands behind the ownership update it was waiting for.
        /// </summary>
        internal static void FlushHeld() {
            if (Held.Count == 0) { return; }

            ZRoutedRpc router = ZRoutedRpc.instance;
            if (router == null || !router.m_server || ZDOMan.instance == null) {
                // The session is closing; there is no world left to hand anything back into.
                for (int i = 0; i < Held.Count; i++) { For(Held[i].Family).Dropped++; }
                Held.Clear();
                return;
            }

            float now = Time.realtimeSinceStartup;
            int kept = 0;
            for (int i = 0; i < Held.Count; i++) {
                Request held = Held[i];
                Counters counters = For(held.Family);
                bool keep = false;
                try {
                    ZDO zdo = ZDOMan.instance.GetZDO(held.Data.m_targetZDO);
                    if (zdo == null) {
                        // The host keeps every ZDO, so this station was destroyed, and its owner
                        // dropped what was in it. What this request carried never got there.
                        if (!Refund(held, counters, "the station was destroyed while it waited")) { counters.Dropped++; }
                    } else {
                        float waitedMs = (now - held.QueuedAt) * 1000f;
                        bool expired = waitedMs > HoldTimeoutSeconds * 1000f;
                        switch (Route(router, ref held, zdo, retry: true, expired: expired)) {
                            case Outcome.Wait:
                                keep = true;
                                break;
                            case Outcome.Done:
                                if (waitedMs > counters.MaxHoldMs) { counters.MaxHoldMs = waitedMs; }
                                break;
                            case Outcome.Refunded:
                                break;
                            default:
                                counters.Dropped++;                      // nobody credible left to give it to
                                break;
                        }
                    }
                } catch (Exception e) {
                    Logger.LogWarning($"Owner RPC routing dropped a held request: {e.GetType().Name}: {e.Message}");
                    counters.Dropped++;
                }
                if (keep) { Held[kept++] = held; }
            }
            Held.RemoveRange(kept, Held.Count - kept);
        }

        // -- the decision ----------------------------------------------------------------------

        /// <summary>The hooks are in and this is the host. Each family's own switch is read in
        /// TryClassify, so either can be turned off without the other.</summary>
        private static bool HooksLive(ZRoutedRpc router) {
            return PatchGuard.IsActive(Mechanism.RpcOwnerRouting)
                && router != null
                && router.m_server;
        }

        /// <summary>
        /// One routing decision for one request. The order is load-bearing: nothing is mutated -
        /// not the message, not the ZDO - until it is certain the message will be dealt with from
        /// here, so a Vanilla outcome hands back exactly what arrived.
        /// </summary>
        private static Outcome Route(ZRoutedRpc router, ref Request request, ZDO zdo, bool retry, bool expired) {
            ZRoutedRpc.RoutedRPCData data = request.Data;
            Family family = request.Family;
            ZDOMan zdoMan = ZDOMan.instance;
            long self = router.m_id;
            Counters counters = For(family);

            long owner = zdo.GetOwner();
            ZDOMan.ZDOPeer ownerPeer = null;
            bool claim = false;

            if (owner == 0L || !Holds(family, zdoMan, owner, self, zdo, out ownerPeer)) {
                // Nobody present is simulating this. The requester is standing at it - they have
                // the instance, or they could not have interacted - so they are the right owner,
                // and the ownership pass would agree within two seconds.
                long sender = data.m_senderPeerID;
                if (sender != self && !Holds(family, zdoMan, sender, self, zdo, out ownerPeer)) {
                    // The host's view of the requester does not put them there either, so there
                    // is no credible owner to name. With no item at stake vanilla's delivery is
                    // the honest one (on a retry, the request is dropped). With one, vanilla's
                    // delivery would lose it: wait for somebody to be there - the requester,
                    // usually, once the host catches up with them - and hand it back if nobody is.
                    if (request.Refund == null) { return Outcome.Vanilla; }
                    if (!retry) {
                        Hold(ref request, counters, parked: true);
                        return Outcome.Done;
                    }
                    if (!expired) { return Outcome.Wait; }
                    counters.Expired++;
                    return Refund(request, counters, "nobody was at the station to take it") ? Outcome.Refunded : Outcome.Vanilla;
                }
                owner = sender;
                claim = zdo.GetOwner() != owner;
            }

            // From here on the message is ours.

            if (claim) {
                zdo.SetOwner(owner);
                counters.Claimed++;
            }

            if (owner != data.m_targetPeerID) {
                if (!request.Retargeted) {
                    counters.Retargeted++;
                    request.Retargeted = true;
                }
                data.m_targetPeerID = owner;
            }

            if (owner == self) {
                // The host's own copy is the authoritative one, so it is the owner the moment
                // SetOwner returns; no sync to wait for. Rewind the parameters: when the host
                // itself sent this with target 0, InvokeRoutedRPC already ran it locally (a
                // no-op, see the class comment) and left the read position at the end.
                data.m_parameters.SetPos(0);
                router.HandleRoutedRPC(data);
                return Outcome.Done;
            }

            if (IsCaughtUp(ownerPeer, zdo)) {
                Forward(ownerPeer.m_peer, data);
                return Outcome.Done;
            }

            // The target has not yet been sent the revision that names it owner. Make sure that
            // update is at the front of its next send - again if the owner changes while this
            // waits - then wait for it; FlushHeld will forward this behind it.
            if (request.ForcedFor != owner) {
                zdoMan.ForceSendZDO(owner, zdo.m_uid);
                request.ForcedFor = owner;
            }

            if (!retry) {
                Hold(ref request, counters, parked: false);
                return Outcome.Done;
            }
            if (!expired) { return Outcome.Wait; }

            // Out of time, and the ownership update has still not gone out. Forwarded now, the
            // request would arrive ahead of it and fail the very IsOwner check it waited for, so
            // an item is handed back instead; anything else is forwarded regardless, which is no
            // worse than vanilla.
            counters.Expired++;
            if (Refund(request, counters, "its new owner was not sent its ownership in time")) { return Outcome.Refunded; }
            Forward(ownerPeer.m_peer, data);
            return Outcome.Done;
        }

        private static void Hold(ref Request request, Counters counters, bool parked) {
            request.QueuedAt = Time.realtimeSinceStartup;
            Held.Add(request);
            if (parked) { counters.Parked++; } else { counters.HeldCount++; }
        }

        /// <summary>
        /// Is this peer, as the host currently sees it, somewhere it can handle a request on this
        /// object - and, for the forward, which ZDOPeer is it? Asked of the current owner ("still
        /// there to take this?") and, when the owner is not, of the requester ("may it be handed
        /// the object?"). The same test both times, on purpose: whoever passes it is who the
        /// ownership pass would leave the object with, so a claim made here is one the pass will
        /// not undo two seconds later.
        ///
        ///   * A station: the peer's 3x3 presence square covers the station's zone - the test the
        ///     pass uses for "owner present", against the same reference position, so the two can
        ///     never disagree about who is here.
        ///   * A creature: the creature's zone is in the peer's near ring - what its ZNetScene
        ///     instantiates - which is the test the pass uses to keep a creature with an owner who
        ///     has stepped out of the presence square (OwnershipArbiter.OwnerStillLoads). Exact,
        ///     not an upper bound: a claimant judged by a looser test would be taken off the
        ///     creature by the next pass, and the hit forwarded to it would find no instance. A
        ///     peer that has stopped answering is excluded here as it is there (M21).
        ///
        /// A peer with no position yet (the (0,0,0) sentinel) is nowhere.
        /// </summary>
        private static bool Holds(Family family, ZDOMan zdoMan, long uid, long self, ZDO zdo, out ZDOMan.ZDOPeer peer) {
            peer = null;
            Vector3 refPos;
            if (uid == self) {
                refPos = ZNet.instance.GetReferencePosition();
            } else if (!TryPositionedPeer(zdoMan, uid, out peer, out refPos)) {
                return false;
            }

            Vector2s zone = ZoneSystem.GetZone(zdo.GetPosition());
            Vector2s peerZone = ZoneSystem.GetZone(refPos);
            if (family != Family.Creature) {
                // Stations and structures: the pass's "owner present" test for anything that is
                // not a creature, so it rescues nothing the router has just given away.
                return ZoneCompat.InActiveArea(zone, peerZone, ZoneCompat.ActiveZoneRadius);
            }

            if (peer != null && IsGhost(uid)) { return false; }
            SimulationDistance distance = peer != null ? ZoneCompat.For(peer.m_peer) : ZoneCompat.Local();
            return ZoneCompat.NearRingLoaded(peerZone, zone, Mathf.Max(1, distance.NearSimulationDistance), distance.IsClassic);
        }

        /// <summary>The peer's ZDOPeer and reference position, when it is connected, has finished
        /// the handshake and has told us where it is.</summary>
        private static bool TryPositionedPeer(ZDOMan zdoMan, long uid, out ZDOMan.ZDOPeer peer, out Vector3 refPos) {
            refPos = Vector3.zero;
            peer = zdoMan.GetPeer(uid);
            if (peer?.m_peer == null || !peer.m_peer.IsReady()) { return false; }
            refPos = peer.m_peer.GetRefPos();
            return refPos != Vector3.zero;
        }

        /// <summary>A peer that has stopped answering is no owner for a creature, the same as in
        /// the ownership pass (M21) - when that pass is excluding ghosts at all.</summary>
        private static bool IsGhost(long uid) {
            return ValConfig.EvictGhostOwners.Value
                && PatchGuard.IsActive(Mechanism.PeerLiveness)
                && PeerLiveness.IsGhost(uid);
        }

        /// <summary>
        /// Has this peer been sent the ZDO at (or past) the owner revision that names the current
        /// owner? m_zdos is written as a ZDO is queued to the peer's socket, and it is also
        /// written with the peer's own revision when the peer sends the ZDO to us - so a peer that
        /// authored the current ownership reads as caught up too, which it is.
        /// </summary>
        private static bool IsCaughtUp(ZDOMan.ZDOPeer peer, ZDO zdo) {
            return peer.m_zdos.TryGetValue(zdo.m_uid, out ZDOMan.ZDOPeer.PeerZDOInfo info)
                && info.m_ownerRevision >= zdo.OwnerRevision;
        }

        private static void Forward(ZNetPeer peer, ZRoutedRpc.RoutedRPCData data) {
            ZPackage pkg = new ZPackage();
            data.Serialize(pkg);
            peer.m_rpc.Invoke(RoutedRpcMethod, pkg);
        }

        /// <summary>
        /// The components whose requests we route, if this ZDO's prefab carries any; null if it
        /// is not a station. Answered from the prefab, not the name, so content mods that build
        /// stations on the vanilla components are covered and unrelated prefabs that happen to
        /// register an "RPC_AddItem" are not. The components read their ZNetView from the same
        /// GameObject, so the root is the right place to look.
        /// </summary>
        private static StationKind Station(ZDO zdo) {
            int prefab = zdo.GetPrefab();
            if (StationPrefabs.TryGetValue(prefab, out StationKind cached)) { return cached; }

            if (ZNetScene.instance == null) { return null; }
            GameObject go = ZNetScene.instance.GetPrefab(prefab);
            if (go == null) { return null; }           // unknown here - do not cache a verdict we cannot justify

            StationKind station = new StationKind {
                Fermenter = go.GetComponent<Fermenter>(),
                Smelter = go.GetComponent<Smelter>(),
                CookingStation = go.GetComponent<CookingStation>(),
                Fireplace = go.GetComponent<Fireplace>(),
                ShieldGenerator = go.GetComponent<ShieldGenerator>(),
                Turret = go.GetComponent<Turret>(),
            };
            if (station.Fermenter == null && station.Smelter == null && station.CookingStation == null
                && station.Fireplace == null && station.ShieldGenerator == null && station.Turret == null) {
                station = null;
            } else {
                station.FuelItem = FuelOf(station);
            }
            StationPrefabs[prefab] = station;
            return station;
        }

        /// <summary>
        /// What an RPC_AddFuel to this station took from the caller's inventory, when that is one
        /// item and vanilla would have sent it at all; null otherwise. Only one component on an
        /// object can register RPC_AddFuel (ZNetView.Register throws on a second), so a prefab
        /// carrying two of them is not a station anyone could have fuelled.
        /// </summary>
        private static ItemDrop FuelOf(StationKind station) {
            int burners = (station.Smelter != null ? 1 : 0) + (station.CookingStation != null ? 1 : 0)
                        + (station.Fireplace != null ? 1 : 0) + (station.ShieldGenerator != null ? 1 : 0);
            if (burners != 1) { return null; }

            // A smelter with no fuel capacity is always "full" to OnAddFuel; a cooking station
            // that does not use fuel has no switch to add it; a fireplace that cannot be refilled,
            // or burns forever, takes nothing out of the inventory.
            if (station.Smelter != null) { return station.Smelter.m_maxFuel > 0 ? station.Smelter.m_fuelItem : null; }
            if (station.CookingStation != null) { return station.CookingStation.m_useFuel ? station.CookingStation.m_fuelItem : null; }
            if (station.Fireplace != null) {
                return station.Fireplace.m_canRefill && !station.Fireplace.m_infiniteFuel ? station.Fireplace.m_fuelItem : null;
            }

            // A shield generator names a list, and RPC_AddFuel does not say which was spent.
            if (station.ShieldGenerator.m_fuelItems == null) { return null; }
            ItemDrop only = null;
            foreach (ItemDrop fuel in station.ShieldGenerator.m_fuelItems) {
                if (fuel == null) { continue; }
                if (only != null && only.name != fuel.name) { return null; }
                only = fuel;
            }
            return only;
        }

        /// <summary>
        /// The item this request took out of the caller's inventory, as the prefab to make it
        /// from again: when the message says which, and the station would have accepted it - the
        /// same test its handler makes, against the same lists. Null when it carries no item, or
        /// the host cannot be sure which. Reads the parameters without moving their position.
        /// </summary>
        private static ItemDrop RefundItem(StationKind station, ZRoutedRpc.RoutedRPCData data, out bool cheated) {
            cheated = false;
            int method = data.m_methodHash;
            if (method == AddFuelHash) { return station.FuelItem; }
            if (method != AddOreHash && method != AddItemHash && method != AddAmmoHash) { return null; }

            ZPackage args = data.m_parameters;
            if (args == null) { return null; }
            int saved = args.GetPos();
            try {
                args.SetPos(0);
                if (method == AddOreHash) {
                    // Smelter.RPC_AddOre(string name, bool cheated)
                    if (station.Smelter == null) { return null; }
                    string name = args.ReadString();
                    cheated = args.ReadBool();
                    foreach (Smelter.ItemConversion conversion in station.Smelter.m_conversion) {
                        if (conversion.m_from != null && conversion.m_from.gameObject.name == name) { return conversion.m_from; }
                    }
                    return null;
                }

                if (method == AddAmmoHash) {
                    // Turret.RPC_AddAmmo(string name)
                    if (station.Turret == null) { return null; }
                    string name = args.ReadString();
                    foreach (Turret.AmmoType ammo in station.Turret.m_allowedAmmo) {
                        if (ammo.m_ammo != null && ammo.m_ammo.name == name) { return ammo.m_ammo; }
                    }
                    return null;
                }

                // RPC_AddItem: the fermenter's is (int nameHash, bool cheated), the cooking
                // station's (string name, bool cheated). One object cannot register both.
                if (station.Fermenter != null && station.CookingStation == null) {
                    int nameHash = args.ReadInt();
                    cheated = args.ReadBool();
                    foreach (Fermenter.ItemConversion conversion in station.Fermenter.m_conversion) {
                        if (conversion.m_from != null && conversion.m_from.gameObject.name.GetStableHashCode() == nameHash) { return conversion.m_from; }
                    }
                    return null;
                }
                if (station.CookingStation != null && station.Fermenter == null) {
                    string name = args.ReadString();
                    cheated = args.ReadBool();
                    foreach (CookingStation.ItemConversion conversion in station.CookingStation.m_conversion) {
                        if (conversion.m_from != null && conversion.m_from.gameObject.name == name) { return conversion.m_from; }
                    }
                }
                return null;
            } catch (Exception) {
                // Not the vanilla parameters - another mod's request under the same name. Route
                // it, but do not make items on its behalf.
                cheated = false;
                return null;
            } finally {
                args.SetPos(saved);
            }
        }

        /// <summary>
        /// Makes the item a request took out of the caller's inventory again, as a world drop
        /// where its station stood - the way the stations drop their own contents when destroyed.
        /// Only ever called for a message the router has taken over and will not deliver (see
        /// the class comment). Returns false, having made nothing, when there is no item to make
        /// or making it failed.
        /// </summary>
        private static bool Refund(in Request request, Counters counters, string why) {
            if (request.Refund == null) { return false; }

            GameObject go;
            try {
                Vector3 at = request.At + Vector3.up + UnityEngine.Random.insideUnitSphere * 0.3f;
                Quaternion rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0, 360), 0f);
                go = UnityEngine.Object.Instantiate(request.Refund.gameObject, at, rotation);
            } catch (Exception e) {
                Logger.LogWarning($"Owner RPC routing could not hand back {request.Refund.name}: {e.GetType().Name}: {e.Message}");
                return false;
            }

            // The item exists from here on, so the request counts as refunded whatever follows;
            // the caller must not deliver it as well.
            counters.Refunded++;
            try {
                ItemDrop drop = go.GetComponent<ItemDrop>();
                ItemDrop.OnCreateNew(drop, request.Cheated);
                // ItemDrop saves itself in Start, a frame from now. A dedicated server removes the
                // instance before then when the station is outside its own area - the ZDO stays,
                // it is persistent - so save it now.
                ZNetView nview = go.GetComponent<ZNetView>();
                if (drop != null && nview != null && nview.IsValid()) { ItemDrop.SaveToZDO(drop.m_itemData, nview.GetZDO()); }
            } catch (Exception e) {
                Logger.LogWarning($"Owner RPC routing handed back {request.Refund.name} but could not finish it: {e.GetType().Name}: {e.Message}");
            }
            Logger.LogInfo($"Handed back 1 {request.Refund.name} at {request.At.ToString("F0")}: {why}.");
            return true;
        }

        internal static void Reset() {
            Held.Clear();
            StationPrefabs.Clear();
            Stations.Clear();
            Creatures.Clear();
            Structures.Clear();
        }
    }
}
