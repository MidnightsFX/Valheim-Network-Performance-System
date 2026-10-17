using System;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// The decisions RpcOwnerRouter makes for its item pick-up and interaction families, kept free
    /// of game types so they can be driven offline. RpcOwnerRouter gathers the facts - who owns
    /// the object in the host's copy, whether each peer has been sent that, who is standing where -
    /// and asks these what to do; it is the only caller.
    /// </summary>
    internal static class RouterRules {

        /// <summary>
        /// How one request is treated beyond the basic "deliver it to whoever owns the object now".
        /// The four families that shipped before 1.18 use ClaimWhenAbsent (and CreaturePresence for
        /// hits on creatures) and nothing else, which is exactly how they behaved before these
        /// flags existed.
        /// </summary>
        [Flags]
        internal enum Rule : ushort {
            None = 0,

            /// <summary>An owner who is not there is replaced by the requester, who is standing at
            /// the object - the decision the ownership pass would reach within two seconds.</summary>
            ClaimWhenAbsent = 1,

            /// <summary>The request asks for ownership itself (an item's RPC_RequestOwn). Decided by
            /// DecideOwnershipRequest rather than the forward path.</summary>
            OwnershipRequest = 2,

            /// <summary>A sender the host names as owner, who knows it, addressed somebody else: it
            /// has just handed the object over itself, and the host has not heard yet. Leave the
            /// request alone. See LeaveToFutureOwner.</summary>
            FutureOwnerGuard = 4,

            /// <summary>The handler has no owner check, so a request sent to nobody (target 0) has
            /// already run on its sender. Never delivered again.</summary>
            RefuseTargetZero = 8,

            /// <summary>The owner answers by handing the object to the requester (a chest opened,
            /// an item stand taken). The requester is told the moment that reaches the host.</summary>
            GrantsToSender = 16,

            /// <summary>The handler cannot run on a dedicated server (it reads the local player), so
            /// a request for an object the server owns hands the object to the requester first.</summary>
            HandOverFromDedicatedHost = 32,

            /// <summary>Requests for one object are delivered in the order they arrived.</summary>
            KeepOrder = 64,

            /// <summary>The object is a creature: presence is judged by what each player loads, as
            /// for hits on creatures, not by the presence square.</summary>
            CreaturePresence = 128,
        }

        /// <summary>What to do with a request for ownership of an object.</summary>
        internal enum OwnReqAction : byte {
            /// <summary>Not the router's: vanilla carries on with the message as it arrived.</summary>
            LeaveAlone,
            /// <summary>On a retry, do what vanilla would have done with it when it arrived.</summary>
            DeliverAsAddressed,
            /// <summary>The requester already owns it in the host's copy and has not been told:
            /// send it the object, and the request is answered.</summary>
            PushToSender,
            /// <summary>Nobody owns it and every copy has heard so: the host gives it to the
            /// requester.</summary>
            ClaimForSender,
            /// <summary>The host owns it: the host gives it to the requester, as the owner's own
            /// handler would.</summary>
            GrantFromHost,
            /// <summary>Re-address it to the current owner, who is there to answer it.</summary>
            Forward,
            /// <summary>Hold it, and look again next frame.</summary>
            Wait,
        }

        /// <summary>
        /// The decision for a request for ownership (RPC_RequestOwn) of a dropped item or an item
        /// stand. It never gives an object away from a player who may be picking it up: the host
        /// itself only hands over an object nobody owns, or one it owns.
        /// </summary>
        /// <param name="owner">Owner in the host's copy.</param>
        /// <param name="sender">The requester.</param>
        /// <param name="self">The host's own session id.</param>
        /// <param name="senderCaughtUp">The requester has been sent (or wrote) the host's current
        /// owner revision.</param>
        /// <param name="senderPresent">The requester is somewhere it could be holding the object.</param>
        /// <param name="settled">Every peer that loads the object and holds a copy has been sent the
        /// current owner revision (only asked about an unowned object).</param>
        /// <param name="ownerPresent">The owner is somewhere it can answer the request.</param>
        /// <param name="retry">This is a held request being looked at again.</param>
        /// <param name="expired">The hold has run out.</param>
        internal static OwnReqAction DecideOwnershipRequest(long owner, long sender, long self,
                                                            bool senderCaughtUp, bool senderPresent, bool settled,
                                                            bool ownerPresent, bool retry, bool expired) {
            if (owner == sender) {
                // The requester owns it. If its copy says so, it is not asking for itself - it
                // handed the object on locally and is passing the request along (see
                // LeaveToFutureOwner). Otherwise its copy is behind, and the object is the answer.
                if (senderCaughtUp) { return retry ? OwnReqAction.DeliverAsAddressed : OwnReqAction.LeaveAlone; }
                return OwnReqAction.PushToSender;
            }

            if (owner == 0L) {
                // Released because nobody had it loaded. Once every copy has heard that, nobody can
                // be holding it, so the requester may have it now rather than at the next pass.
                if (senderPresent && settled) { return OwnReqAction.ClaimForSender; }
                return Expired(retry, expired);
            }

            if (owner == self) { return OwnReqAction.GrantFromHost; }

            if (ownerPresent) { return OwnReqAction.Forward; }

            // The owner has left. The ownership pass gives the object to a player who is there
            // within two seconds; wait for that rather than take it from the owner here.
            return Expired(retry, expired);
        }

        private static OwnReqAction Expired(bool retry, bool expired) {
            return retry && expired ? OwnReqAction.DeliverAsAddressed : OwnReqAction.Wait;
        }

        /// <summary>
        /// A request from the object's owner in the host's copy, who has been told it owns it,
        /// addressed to somebody else. A client never routes a request while its copy names itself
        /// - that runs locally - so it has handed the object over itself (SetOwner) and its
        /// update has not reached the host yet. Re-aiming the request at the host's owner would
        /// send it straight back to a player who no longer owns it. Asked on the first pass only;
        /// a held request is never handed back to vanilla.
        /// </summary>
        internal static bool LeaveToFutureOwner(long owner, long sender, long self, long addressedTo,
                                                bool senderCaughtUp, bool retry) {
            return !retry
                && owner == sender
                && sender != self
                && addressedTo != owner
                && senderCaughtUp;
        }

        /// <summary>What to do about one remembered request for ownership.</summary>
        internal enum GrantAction : byte {
            /// <summary>Not theirs yet; keep watching.</summary>
            Keep,
            /// <summary>Theirs, and their copy does not say so: send it now.</summary>
            Push,
            /// <summary>Theirs, and they know (or cannot be sent it): done.</summary>
            Done,
            /// <summary>Not theirs within the time allowed: forget it.</summary>
            Expire,
        }

        /// <summary>
        /// A player asked for an object. Once the host's copy names them owner - whoever made the
        /// change, and by whichever path it arrived - they are sent the object at once instead of
        /// waiting their turn in the host's send order.
        /// </summary>
        /// <param name="requesterHolds">The requester has a copy of the object at all. A player who
        /// has never been sent it is not sent it here; see RpcOwnerRouter.PushIfHeld.</param>
        internal static GrantAction ServiceGrant(long owner, long requester, bool requesterHolds,
                                                 int requesterOwnerRev, int currentOwnerRev,
                                                 double now, double expiresAt) {
            if (owner == requester) {
                if (requesterHolds && requesterOwnerRev < currentOwnerRev) { return GrantAction.Push; }
                return GrantAction.Done;
            }
            return now >= expiresAt ? GrantAction.Expire : GrantAction.Keep;
        }

        /// <summary>
        /// How long players waited, from the first request the host saw to the object being theirs
        /// in the host's copy. Fixed buckets, so the record costs nothing to keep and can be
        /// subtracted for a window.
        /// </summary>
        internal sealed class WaitHistogram {
            internal static readonly double[] Bounds = { 100d, 250d, 500d, 1000d, 2000d, 5000d, 10000d, 30000d };

            internal readonly long[] Counts = new long[Bounds.Length + 1];
            internal long Total;
            internal double MaxMs;

            internal void Add(double ms) {
                if (ms < 0d) { ms = 0d; }
                int i = 0;
                while (i < Bounds.Length && ms > Bounds[i]) { i++; }
                Counts[i]++;
                Total++;
                if (ms > MaxMs) { MaxMs = ms; }
            }

            /// <summary>The upper bound of the bucket holding the given share of waits, in ms;
            /// -1 when there are none, and +inf for the open last bucket.</summary>
            internal double Percentile(double share) {
                return Percentile(Counts, Total, share);
            }

            internal static double Percentile(long[] counts, long total, double share) {
                if (total <= 0) { return -1d; }
                long rank = (long)Math.Ceiling(share * total);
                if (rank < 1) { rank = 1; }
                long seen = 0;
                for (int i = 0; i < counts.Length; i++) {
                    seen += counts[i];
                    if (seen >= rank) { return i < Bounds.Length ? Bounds[i] : double.PositiveInfinity; }
                }
                return double.PositiveInfinity;
            }

            internal void CopyTo(long[] into) {
                Array.Copy(Counts, into, Counts.Length);
            }

            internal void Clear() {
                Array.Clear(Counts, 0, Counts.Length);
                Total = 0;
                MaxMs = 0d;
            }
        }
    }
}
