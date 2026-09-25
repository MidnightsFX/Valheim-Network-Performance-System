using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M25 - the host's ownership changes are not undone by a peer's older update.
    ///
    /// A host-side SetOwner changes the owner and bumps OwnerRevision; nothing else about the ZDO
    /// moves. The old owner does not know yet - it hears one send tick plus a one-way trip later -
    /// and in the meantime it goes on simulating the object and sending it, each update carrying a
    /// newer DATA revision and the OLD owner revision, naming itself. ZDOMan.RPC_ZDOData on the host
    /// sees the newer data revision and applies the update in full: position and fields, and also
    /// owner and OwnerRevision, straight back to what the old owner had. It then records in the
    /// sender's ZDOPeer.m_zdos that the sender holds exactly that revision pair - which the host's
    /// copy now equals - so ShouldSend never sends the correction. The host has quietly given the
    /// object back, and the peer it meant to give it to may already have heard otherwise: two
    /// players each sure the other owns a creature, nobody simulating it, every hit on it dropped.
    ///
    /// Measured before this existed, on a 25-player server over two hours: 3359 of 3528 host
    /// releases of a ship were undone this way, 49ms after they were made (median), every two
    /// seconds; 362 of 391 hand-offs of a ship between two players went the same way. The
    /// chest-and-fermenter "nobody can open it" reports are the same mechanism on stations.
    ///
    /// The rule is the one the game already applies on its other path. When an update carries no
    /// newer data, RPC_ZDOData takes its owner only if its owner revision is HIGHER. On the
    /// full-apply path it never asks. This asks: if the update's owner revision is lower than the
    /// host's, the host keeps its owner and revision.
    ///
    /// What happens to the rest of the update, and to its sender, is the second half, and the
    /// first recording with 1.8.0 is why it exists. Keeping the owner and applying the data left
    /// two gaps, both measured on a 40-player server over thirteen hours:
    ///
    ///   * The sender did not always hear. The correction relied on ShouldSend seeing the sender
    ///     behind, which it did - but the object also had to come up in the sender's sync list in
    ///     time, and it rarely does. The old owner is at the far edge of its loaded ring (that is
    ///     why it was moved off), vanilla's send order is distance minus age, and every refused
    ///     update restarts that age. 549k updates were refused, 91% from a sender exactly two zones
    ///     away, and the longest stream ran ten and a half minutes. Now the host force-sends the
    ///     object to that sender as soon as it refuses one of its updates: AddForceSendZdos puts it
    ///     at the head of the sender's next send whatever its distance or age, and the sender takes
    ///     the owner on RPC_ZDOData's owner-only path. One round trip, and the old owner stops.
    ///   * Everyone else was shown the old owner's simulation. The host kept its owner but took
    ///     the position and fields, so for as long as the sender kept writing, every viewer saw the
    ///     creature move as the old owner moved it - under an owner that was somebody else (4
    ///     creature-hours of two machines driving one creature) or nobody (15.5 creature-hours of
    ///     a creature the game hides because it has no owner, while one player went on fighting
    ///     it). Worse, the host's data revision rose with the sender's, so the real owner's own
    ///     updates arrived "older" and were dropped. For a creature the refused update is now
    ///     dropped whole: the host's copy, and what it relays, stays the owner's.
    ///
    /// Only a creature's data is refused. Its fields are its owner's simulation and nothing the
    /// sender did in that one round trip needs keeping; the worst case is a hit it landed on its
    /// own stale copy. Anything else can carry player-made state on the same ZDO - a ship's or a
    /// cart's cargo, a chest, a station's queue - and dropping an update there could lose an item
    /// put in during that round trip, so those keep 1.8.0's behaviour: owner kept, data applied,
    /// correction forced.
    ///
    /// Only on the host, only on the full-apply path, only when the revision is strictly lower. A
    /// new ZDO starts at revision 0 and can never trip it. Equal revisions - two machines changing
    /// the owner at the same moment from the same starting point - are left to the game exactly as
    /// before. An older revision naming the owner the host already has is that owner's own
    /// simulation, one ownership change behind; its data is taken. The revision is compared as the
    /// game compares it, as a plain ushort, so at the 65536-change wrap this behaves as vanilla
    /// does there.
    ///
    /// Wiring: OwnerRevisionGuardPatches replaces six instructions of RPC_ZDOData's full-apply
    /// block with the six methods below, which run in order for one ZDO before the next is read:
    ///
    ///     zdo.OwnerRevision = ownerRevision;        StoreOwnerRevision   decides "stale"
    ///     zdo.DataRevision = dataRevision;          StoreDataRevision    deferred while stale
    ///     zdo.SetOwnerInternal(owner);              ApplyOwner           decides "refuse the data"
    ///     zdo.InternalSetPosition(position);        ApplyPosition
    ///     peer.m_zdos[id] = new PeerZDOInfo(..,
    ///                         zdo.OwnerRevision,    RevisionHeldBySender
    ///                         time);
    ///     zdo.Deserialize(data);                    ApplyData            ends the ZDO
    ///
    /// The data revision is stored before the owner is known, so while an update is stale its
    /// data revision is held back and ApplyOwner decides whether it lands.
    /// </summary>
    internal static class OwnerRevisionGuard {

        /// <summary>Set by the RPC_ZDOData prefix for the duration of one call: this machine is the
        /// host and the guard is wanted. Read by the replacements, so a client - where the host's
        /// updates are authoritative and must keep applying in full - never trips.</summary>
        internal static bool Armed;

        // The ZDO whose owner revision the first replacement just refused, and what it refused.
        // RPC_ZDOData runs the replacements for one ZDO back to back on the main thread, so one
        // slot is enough; StoreOwnerRevision clears it for every ZDO, and the prefix clears it in
        // case a throw left it set.
        private static ZDO _held;
        private static ushort _heldPacketRevision;
        private static uint _heldDataRevision;
        private static long _heldHostOwner;
        private static bool _refuseData;

        // Who sent the packet being applied. Resolved on the first refusal in it and not before:
        // most packets refuse nothing, and ZDOMan.FindPeer is a walk of the peer list.
        private static ZRpc _rpc;
        private static ZDOMan.ZDOPeer _sender;
        private static bool _senderResolved;

        // -- diagnostics -------------------------------------------------------------------

        /// <summary>Updates whose owner revision was older than the host's. Kept is the number
        /// that named a different owner - an ownership change the game would have undone.</summary>
        internal static long Seen;
        internal static long Kept;
        internal static long KeptCreatures;

        /// <summary>Of Kept, the creature updates dropped whole rather than applied.</summary>
        internal static long DataRefused;

        /// <summary>Corrections pushed to the head of a stale sender's next send.</summary>
        internal static long CorrectionsForced;

        // -- the six replacements ----------------------------------------------------------

        /// <summary>Stands in for `zdo.OwnerRevision = packetRevision` at the top of the full-apply
        /// block. Same stack in, nothing out.</summary>
        internal static void StoreOwnerRevision(ZDO zdo, ushort packetRevision) {
            _held = null;
            _refuseData = false;
            if (Armed && packetRevision < zdo.OwnerRevision) {
                _held = zdo;
                _heldPacketRevision = packetRevision;
                _heldHostOwner = zdo.GetOwner();
                Seen++;
                return;                                                       // the host's revision stays
            }
            zdo.OwnerRevision = packetRevision;
        }

        /// <summary>Stands in for `zdo.DataRevision = packetDataRevision`. For a stale update the
        /// revision is held until ApplyOwner knows whether the data lands at all: a refused
        /// update that still raised the host's data revision would make the real owner's next
        /// updates read as older, and the game drops those.</summary>
        internal static void StoreDataRevision(ZDO zdo, uint packetDataRevision) {
            if (_held == zdo) {
                _heldDataRevision = packetDataRevision;
                return;
            }
            zdo.DataRevision = packetDataRevision;
        }

        /// <summary>Stands in for `zdo.SetOwnerInternal(packetOwner)`. For a stale update it
        /// still makes the call, with the owner the host already has, rather than skipping it:
        /// it changes nothing, and network monitoring's hook on SetOwnerInternal is how that sees
        /// which peer is still writing an object it has just been moved off.</summary>
        internal static void ApplyOwner(ZDO zdo, long packetOwner) {
            if (_held != zdo) {
                zdo.SetOwnerInternal(packetOwner);
                return;
            }

            zdo.SetOwnerInternal(_heldHostOwner);
            if (packetOwner == _heldHostOwner) {
                // The owner's own simulation, one ownership change behind. Its data is the data.
                zdo.DataRevision = _heldDataRevision;
                return;
            }

            // The sender is writing an object it no longer owns. The owner is settled and the
            // correction queued before anything that could throw.
            ForceCorrection(zdo);
            Refuse(zdo, packetOwner, OwnershipPolicy.IsCreature(zdo));
        }

        /// <summary>The rest of a refusal, once the object is classified: a creature's data is
        /// dropped, anything else's lands under the host's owner. Apart from ApplyOwner so the
        /// offline harness, which cannot compile the prefab lookup, can drive both outcomes.</summary>
        internal static void Refuse(ZDO zdo, long packetOwner, bool creature) {
            _refuseData = creature;
            if (!creature) { zdo.DataRevision = _heldDataRevision; }
            NoteKept(zdo, packetOwner, creature);
        }

        /// <summary>Stands in for `zdo.InternalSetPosition(position)`. A refused update leaves
        /// the host's position alone - the owner's, which is what everyone else is sent.</summary>
        internal static void ApplyPosition(ZDO zdo, Vector3 position) {
            if (_refuseData && _held == zdo) { return; }
            zdo.InternalSetPosition(position);
        }

        /// <summary>Stands in for the `zdo.OwnerRevision` read that goes into the sender's
        /// ZDOPeer.m_zdos entry. For a stale update that is the revision the SENDER holds, not
        /// the host's: recording the host's would say the sender is up to date, and it is the one
        /// peer that is not - ShouldSend would then turn the forced correction away.</summary>
        internal static ushort RevisionHeldBySender(ZDO zdo) {
            return _held == zdo ? _heldPacketRevision : zdo.OwnerRevision;
        }

        /// <summary>Stands in for `zdo.Deserialize(data)`, the last step for one ZDO. The
        /// payload has already been read out of the packet, so skipping it keeps the packet
        /// aligned; the next ZDO starts at StoreOwnerRevision either way.</summary>
        internal static void ApplyData(ZDO zdo, ZPackage data) {
            if (_held == zdo) {
                bool refuse = _refuseData;
                _held = null;
                _refuseData = false;
                if (refuse) { return; }
            }
            zdo.Deserialize(data);
        }

        // -- consequences of a refusal -----------------------------------------------------

        /// <summary>
        /// The sender must hear who owns this now, and soon. ShouldSend already agrees - the
        /// sender is recorded at the revision it sent - so this only has to get the object past
        /// the queue: AddForceSendZdos inserts it at the head of the sender's next sync list,
        /// ahead of the distance-and-age order that kept it at the back.
        ///
        /// Repeated for every stale update that arrives before the correction does, which is one
        /// round trip's worth. Each re-marks the sender behind and costs one resend of one ZDO.
        /// </summary>
        private static void ForceCorrection(ZDO zdo) {
            if (!_senderResolved) {
                _senderResolved = true;
                _sender = _rpc != null && ZDOMan.instance != null ? ZDOMan.instance.FindPeer(_rpc) : null;
            }
            if (_sender == null) { return; }

            _sender.ForceSendZDO(zdo.m_uid);
            CorrectionsForced++;
        }

        private static void NoteKept(ZDO zdo, long packetOwner, bool creature) {
            Kept++;
            if (creature) {
                KeptCreatures++;
                DataRefused++;
            }
            if (Monitoring.Active) {
                Monitoring.OnStaleOwnerRejected(zdo, packetOwner, _heldPacketRevision, creature);
            }
        }

        // -- per call ----------------------------------------------------------------------

        internal static void Arm(ZRpc rpc) {
            _held = null;
            _refuseData = false;
            _sender = null;
            _senderResolved = false;
            Armed = PatchGuard.IsActive(Mechanism.OwnerRevisionGuard)
                    && ValConfig.RejectStaleOwnerUpdates.Value
                    && NpsEnv.IsHost();
            _rpc = Armed ? rpc : null;
        }

        internal static void Disarm() {
            Armed = false;
            _held = null;
            _refuseData = false;
            _rpc = null;
            _sender = null;
            _senderResolved = false;
        }

        internal static void Reset() {
            Disarm();
            Seen = 0;
            Kept = 0;
            KeptCreatures = 0;
            DataRefused = 0;
            CorrectionsForced = 0;
        }
    }
}
