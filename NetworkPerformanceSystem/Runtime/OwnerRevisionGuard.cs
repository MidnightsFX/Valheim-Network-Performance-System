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
    /// The fix is the rule the game already applies on its other path. When an update carries no
    /// newer data, RPC_ZDOData takes its owner only if its owner revision is HIGHER. On the
    /// full-apply path it never asks. This asks: if the update's owner revision is lower than the
    /// host's, the host keeps its owner and revision, applies everything else, and records the
    /// update's revision as what the sender holds - so ShouldSend sees the sender behind and sends
    /// it the current owner, which the sender applies on that owner-only path. One round trip, and
    /// the old owner stops writing.
    ///
    /// Only on the host, only on the full-apply path, only when the revision is strictly lower. A
    /// new ZDO starts at revision 0 and can never trip it. Equal revisions - two machines changing
    /// the owner at the same moment from the same starting point - are left to the game exactly as
    /// before. The revision is compared as the game compares it, as a plain ushort, so at the
    /// 65536-change wrap this behaves as vanilla does there.
    ///
    /// Wiring: OwnerRevisionGuardPatches replaces three instructions of RPC_ZDOData's full-apply
    /// block with the three methods below, which run in order for one ZDO before the next is read.
    /// </summary>
    internal static class OwnerRevisionGuard {

        /// <summary>Set by the RPC_ZDOData prefix for the duration of one call: this machine is the
        /// host and the guard is wanted. Read by the three replacements, so a client - where the
        /// host's updates are authoritative and must keep applying in full - never trips.</summary>
        internal static bool Armed;

        // The ZDO whose owner revision the first replacement just refused, and what it refused.
        // RPC_ZDOData runs the three replacements for one ZDO back to back on the main thread, so
        // one slot is enough; the prefix clears it in case a throw left it set.
        private static ZDO _held;
        private static ushort _heldPacketRevision;
        private static long _heldHostOwner;

        // -- diagnostics -------------------------------------------------------------------

        /// <summary>Updates whose owner revision was older than the host's. Kept is the number
        /// that named a different owner - an ownership change the game would have undone.</summary>
        internal static long Seen;
        internal static long Kept;
        internal static long KeptCreatures;

        // -- the three replacements --------------------------------------------------------

        /// <summary>Stands in for `zdo.OwnerRevision = packetRevision` at the top of the full-apply
        /// block. Same stack in, nothing out.</summary>
        internal static void StoreOwnerRevision(ZDO zdo, ushort packetRevision) {
            _held = null;
            if (Armed && packetRevision < zdo.OwnerRevision) {
                _held = zdo;
                _heldPacketRevision = packetRevision;
                _heldHostOwner = zdo.GetOwner();
                Seen++;
                return;                                                       // the host's revision stays
            }
            zdo.OwnerRevision = packetRevision;
        }

        /// <summary>Stands in for `zdo.SetOwnerInternal(packetOwner)`. For a refused update it
        /// still makes the call, with the owner the host already has, rather than skipping it:
        /// it changes nothing, and network monitoring's hook on SetOwnerInternal is how that sees
        /// which peer is still writing an object it has just been moved off.</summary>
        internal static void ApplyOwner(ZDO zdo, long packetOwner) {
            if (_held != zdo) {
                zdo.SetOwnerInternal(packetOwner);
                return;
            }

            zdo.SetOwnerInternal(_heldHostOwner);
            if (packetOwner != _heldHostOwner) { NoteKept(zdo, packetOwner); }
        }

        /// <summary>The counting half of ApplyOwner, apart so that the owner is settled before
        /// anything here can go wrong - and so the offline harness, which cannot compile the
        /// prefab lookup, can still check that it is.</summary>
        private static void NoteKept(ZDO zdo, long packetOwner) {
            Kept++;
            if (OwnershipPolicy.IsCreature(zdo)) { KeptCreatures++; }
            if (Monitoring.Active) {
                Monitoring.OnStaleOwnerRejected(zdo, packetOwner, _heldPacketRevision);
            }
        }

        /// <summary>Stands in for the `zdo.OwnerRevision` read that goes into the sender's
        /// ZDOPeer.m_zdos entry. For a refused update that is the revision the SENDER holds, not
        /// the host's: recording the host's would say the sender is up to date, and it is the one
        /// peer that is not.</summary>
        internal static ushort RevisionHeldBySender(ZDO zdo) {
            if (_held == zdo) {
                _held = null;
                return _heldPacketRevision;
            }
            return zdo.OwnerRevision;
        }

        // -- per call ----------------------------------------------------------------------

        internal static void Arm() {
            _held = null;
            Armed = PatchGuard.IsActive(Mechanism.OwnerRevisionGuard)
                    && ValConfig.RejectStaleOwnerUpdates.Value
                    && NpsEnv.IsHost();
        }

        internal static void Disarm() {
            Armed = false;
            _held = null;
        }

        internal static void Reset() {
            Disarm();
            Seen = 0;
            Kept = 0;
            KeptCreatures = 0;
        }
    }
}
