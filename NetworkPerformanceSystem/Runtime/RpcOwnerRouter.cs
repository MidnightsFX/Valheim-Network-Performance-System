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
    /// copy of the owner id - and every handler on the far end begins "if (m_nview.IsOwner())"
    /// and otherwise returns without a word. Nothing acknowledges, nothing rolls back. So any
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
    /// regardless, which is no worse than vanilla.
    ///
    /// Why double delivery cannot happen: the sender only executes a routed RPC locally before
    /// sending it when its target is 0 (unowned), and every handler in RequestHashes is gated on
    /// IsOwner, so that local run was a no-op on a non-owner. RPC_RemoveDoneItem is left out for
    /// precisely this reason - it has no such gate, and a dropped one costs a second press of E,
    /// not an item.
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
    /// </summary>
    internal static class RpcOwnerRouter {

        private const string RoutedRpcMethod = "RoutedRPC";

        internal enum Family : byte {
            Station,
            Creature,
        }

        /// <summary>What one family of requests needed correcting. "Re-targeted" and "claimed" are
        /// the deliveries vanilla would have lost outright; "held" is the cost of doing it safely.</summary>
        internal sealed class Counters {
            internal long Seen;         // owner-addressed requests inspected
            internal long Retargeted;   // target id rewritten to the current owner
            internal long Claimed;      // owner absent or none - ownership handed to the requester first
            internal long HeldCount;    // forwarded only after the target had been sent its ownership
            internal long Expired;      // hold timed out; forwarded regardless
            internal long Dropped;      // object or target gone while waiting
            internal float MaxHoldMs;

            internal void Clear() {
                Seen = 0;
                Retargeted = 0;
                Claimed = 0;
                HeldCount = 0;
                Expired = 0;
                Dropped = 0;
                MaxHoldMs = 0f;
            }
        }

        internal static readonly Counters Stations = new Counters();
        internal static readonly Counters Creatures = new Counters();

        /// <summary>The owner-addressed creature requests that must land. Only RPC_Damage - see the
        /// class comment for why RPC_Stagger is not one of them.</summary>
        private static readonly HashSet<int> CreatureRequestHashes = new HashSet<int> {
            "RPC_Damage".GetStableHashCode(),
        };

        /// <summary>
        /// The owner-addressed requests the station components make. Anything they send to
        /// ZNetView.Everybody or to a specific non-owner (RPC_SetSlotVisual, RPC_SetFuelAmount,
        /// RPC_SetFuel) is deliberately absent: those are addressed correctly already. Matched by
        /// name hash, so a mod's own "RPC_AddItem" on some other prefab is out of scope until
        /// IsStation has confirmed the ZDO carries one of these components.
        /// </summary>
        private static readonly HashSet<int> RequestHashes = new HashSet<int> {
            "RPC_AddItem".GetStableHashCode(),        // Fermenter, CookingStation
            "RPC_Tap".GetStableHashCode(),            // Fermenter
            "RPC_AddOre".GetStableHashCode(),         // Smelter
            "RPC_AddFuel".GetStableHashCode(),        // Smelter, CookingStation, Fireplace, ShieldGenerator
            "RPC_EmptyProcessed".GetStableHashCode(), // Smelter
            "RPC_AddFuelAmount".GetStableHashCode(),  // Fireplace
            "RPC_ToggleOn".GetStableHashCode(),       // Fireplace
            "RPC_AddAmmo".GetStableHashCode(),        // Turret
        };

        /// <summary>Per-prefab verdict on "carries one of the station components", so the
        /// component lookup runs once per prefab rather than once per request.</summary>
        private static readonly Dictionary<int, bool> StationPrefabs = new Dictionary<int, bool>();

        /// <summary>
        /// How long a forward waits for its target to be sent the ownership update before going
        /// out regardless. The update is force-sent, and AddForceSendZdos puts it at the front of
        /// the very next send to that peer, so in practice the wait is one send tick; the timeout
        /// only matters for a peer whose window is so saturated that nothing is going out at all,
        /// and then the request is no worse off than vanilla would have left it.
        /// </summary>
        internal const float HoldTimeoutSeconds = 3f;

        private struct HeldRpc {
            internal ZRoutedRpc.RoutedRPCData Data;
            internal float QueuedAt;
            internal Family Family;
        }

        private static readonly List<HeldRpc> Held = new List<HeldRpc>();

        private enum Outcome {
            Vanilla,   // nothing was changed; vanilla should carry on with the message as it was
            Done,      // forwarded, handled here, or parked in Held
            Wait,      // still waiting for the target to be sent its ownership (retry only)
        }

        // -- diagnostics -----------------------------------------------------------------------

        internal static int Waiting => Held.Count;

        private static Counters For(Family family) => family == Family.Station ? Stations : Creatures;

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
                long addressedTo = data.m_targetPeerID;
                if (Route(router, data, zdo, family, retry: false, expired: false) == Outcome.Done) {
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
                return Route(router, data, zdo, family, retry: false, expired: false) == Outcome.Done;
            } catch (Exception e) {
                Logger.LogWarning($"Owner RPC routing fell back to vanilla: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Is this a request one of the enabled families takes, and on what object? The method
        /// hash is checked first because it retires nearly every message for the price of two set
        /// probes; only a matching hash pays for the ZDO lookup and the prefab check. A hit on a
        /// player or a building matches the hash and fails the prefab check, and is left alone.
        /// </summary>
        private static bool TryClassify(ZDOID target, int methodHash, out ZDO zdo, out Family family) {
            zdo = null;
            family = Family.Station;
            if (target.IsNone()) { return false; }

            bool station = RequestHashes.Contains(methodHash) && ValConfig.EnableStationRpcRouting.Value;
            bool creature = !station && CreatureRequestHashes.Contains(methodHash) && ValConfig.EnableCreatureHitRouting.Value;
            if (!station && !creature) { return false; }

            zdo = ZDOMan.instance?.GetZDO(target);
            if (zdo == null) { return false; }

            if (station) {
                family = Family.Station;
                return IsStation(zdo);
            }
            family = Family.Creature;
            return OwnershipPolicy.IsCreature(zdo);
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
                for (int i = 0; i < Held.Count; i++) { For(Held[i].Family).Dropped++; }
                Held.Clear();
                return;
            }

            float now = Time.realtimeSinceStartup;
            int kept = 0;
            for (int i = 0; i < Held.Count; i++) {
                HeldRpc held = Held[i];
                Counters counters = For(held.Family);
                bool keep = false;
                try {
                    ZDO zdo = ZDOMan.instance.GetZDO(held.Data.m_targetZDO);
                    if (zdo == null) {
                        counters.Dropped++;
                    } else {
                        float waitedMs = (now - held.QueuedAt) * 1000f;
                        bool expired = waitedMs > HoldTimeoutSeconds * 1000f;
                        switch (Route(router, held.Data, zdo, held.Family, retry: true, expired: expired)) {
                            case Outcome.Wait:
                                keep = true;
                                break;
                            case Outcome.Done:
                                if (waitedMs > counters.MaxHoldMs) { counters.MaxHoldMs = waitedMs; }
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
        /// not the message, not the ZDO - until it is certain the message will be delivered from
        /// here, so a Vanilla outcome hands back exactly what arrived.
        /// </summary>
        private static Outcome Route(ZRoutedRpc router, ZRoutedRpc.RoutedRPCData data, ZDO zdo, Family family, bool retry, bool expired) {
            ZDOMan zdoMan = ZDOMan.instance;
            long self = router.m_id;
            Counters counters = For(family);

            long owner = zdo.GetOwner();
            ZDOMan.ZDOPeer ownerPeer = null;
            bool claim = false;

            if (owner == 0L || !Holds(family, zdoMan, owner, self, zdo, out ownerPeer)) {
                // Nobody present is simulating this. The requester is standing at it - they have
                // the instance, or they could not have interacted - so they are the right owner,
                // and the ownership pass would agree within two seconds. Refuse only when the
                // host's view of the requester does not put them there either: then there is no
                // credible owner to name, and vanilla's behaviour is the honest one.
                long sender = data.m_senderPeerID;
                if (sender != self) {
                    if (!Holds(family, zdoMan, sender, self, zdo, out ownerPeer)) { return Outcome.Vanilla; }
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
                if (!retry) { counters.Retargeted++; }
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

            if (retry) {
                if (!expired) { return Outcome.Wait; }
                counters.Expired++;
                Forward(ownerPeer.m_peer, data);
                return Outcome.Done;
            }

            // The target has not yet been sent the revision that names it owner. Make sure that
            // update is at the front of its next send, then wait for it - FlushHeld will forward
            // this behind it.
            zdoMan.ForceSendZDO(owner, zdo.m_uid);
            Held.Add(new HeldRpc { Data = data, QueuedAt = Time.realtimeSinceStartup, Family = family });
            counters.HeldCount++;
            return Outcome.Done;
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
            if (family == Family.Station) {
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
        /// Does this ZDO's prefab carry one of the components whose requests we route? Answered
        /// from the prefab, not the name, so content mods that build stations on the vanilla
        /// components are covered and unrelated prefabs that happen to register an "RPC_AddItem"
        /// are not. The components read their ZNetView from the same GameObject, so the root is
        /// the right place to look.
        /// </summary>
        private static bool IsStation(ZDO zdo) {
            int prefab = zdo.GetPrefab();
            if (StationPrefabs.TryGetValue(prefab, out bool cached)) { return cached; }

            if (ZNetScene.instance == null) { return false; }
            GameObject go = ZNetScene.instance.GetPrefab(prefab);
            if (go == null) { return false; }          // unknown here - do not cache a verdict we cannot justify

            bool station = go.GetComponent<Fermenter>() != null
                        || go.GetComponent<Smelter>() != null
                        || go.GetComponent<CookingStation>() != null
                        || go.GetComponent<Fireplace>() != null
                        || go.GetComponent<ShieldGenerator>() != null
                        || go.GetComponent<Turret>() != null;
            StationPrefabs[prefab] = station;
            return station;
        }

        internal static void Reset() {
            Held.Clear();
            StationPrefabs.Clear();
            Stations.Clear();
            Creatures.Clear();
        }
    }
}
