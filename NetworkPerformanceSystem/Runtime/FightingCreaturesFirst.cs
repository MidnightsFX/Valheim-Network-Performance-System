using System.Collections.Generic;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M29 - a player's game sends the creatures it is fighting before the rest of its changes.
    ///
    /// A client uploads whatever it has changed since its last send, in the order the game's
    /// ClientSortSendZDOS gives it: players and ships first (ObjectType.Prioritized), then
    /// everything else, longest-unsent first. Creatures are not Prioritized. While the uplink has
    /// room the order does not matter; once it is full, a creature in the middle of a fight waits
    /// its turn behind every fire, smelter and dropped item that player also owns. In the
    /// 2026-09-28 recording, alert creatures went a second or more without an update 12-27 times a
    /// player-hour on 1.10.0, against 1-6 on 1.9.0. One LAN player's gaps all came while it was
    /// uploading 134 KB/s (median) against 69 KB/s over the whole session, and most lasted 1.5-3 s:
    /// one full turn of the backlog.
    ///
    /// So the list is re-ordered after the game has built it. What AddForceSendZdos put at the
    /// head and the Prioritized objects stay first; then come the creatures this machine owns that
    /// are alert or have a target; then everything else. Each group keeps the game's own order.
    /// Nothing is added or dropped, so a tick whose list fits the window sends exactly what it
    /// would have.
    ///
    /// The client's list only. The host sorts each player's list by distance from that player,
    /// which already puts a fight next to them ahead of most of a backlog. Runs on each player's
    /// game that has this mod, under the server's setting.
    /// </summary>
    internal static class FightingCreaturesFirst {

        private static readonly List<ZDO> Fighting = new List<ZDO>();
        private static readonly List<ZDO> Rest = new List<ZDO>();
        private static readonly System.Predicate<ZDO> FightingTest = IsFighting;

        // Since start. Main thread only.
        internal static long ListsReordered;
        internal static long CreaturesMoved;

        internal static bool Active =>
            PatchGuard.IsActive(Mechanism.FightingCreaturesFirst)
            && ValConfig.SendFightingCreaturesFirst != null
            && ValConfig.SendFightingCreaturesFirst.Value;

        /// <summary>
        /// Moves this machine's fighting creatures up to just behind the head of a finished client
        /// sync list, in place. Counts a creature only when something that is not a fighting
        /// creature was ahead of it, which is when the order actually changed.
        /// </summary>
        internal static void Reorder(ZDOMan.ZDOPeer peer, List<ZDO> toSync) {
            Reorder(peer, toSync, FightingTest);
        }

        /// <summary>The same, with the fighting test passed in: IsFighting's creature check reaches
        /// game types the offline harness cannot load, and the ordering is what it tests.</summary>
        internal static void Reorder(ZDOMan.ZDOPeer peer, List<ZDO> toSync, System.Predicate<ZDO> fighting) {
            if (peer == null || toSync == null || toSync.Count < 2) { return; }

            int head = 0;
            while (head < toSync.Count && IsHead(peer, toSync[head])) { head++; }

            // Most sends have nothing to move, and pay for one pass with no copying.
            int firstOther = -1;
            int moved = 0;
            for (int i = head; i < toSync.Count; i++) {
                if (fighting(toSync[i])) {
                    if (firstOther >= 0) { moved++; }
                } else if (firstOther < 0) {
                    firstOther = i;
                }
            }
            if (moved == 0) { return; }

            Fighting.Clear();
            Rest.Clear();
            for (int i = firstOther; i < toSync.Count; i++) {
                ZDO zdo = toSync[i];
                if (fighting(zdo)) { Fighting.Add(zdo); } else { Rest.Add(zdo); }
            }

            int at = firstOther;
            for (int i = 0; i < Fighting.Count; i++) { toSync[at++] = Fighting[i]; }
            for (int i = 0; i < Rest.Count; i++) { toSync[at++] = Rest[i]; }
            Fighting.Clear();
            Rest.Clear();

            ListsReordered++;
            CreaturesMoved += moved;
        }

        private static bool IsHead(ZDOMan.ZDOPeer peer, ZDO zdo) {
            return zdo == null
                || zdo.Type == ZDO.ObjectType.Prioritized
                || peer.m_forceSend.Contains(zdo.m_uid);
        }

        /// <summary>Owned here, a creature, and alert or with a target. The owner flag first: it is
        /// a field read, and a client's change list is mostly its own objects anyway.</summary>
        internal static bool IsFighting(ZDO zdo) {
            if (zdo == null || !zdo.IsOwner()) { return false; }
            if (!OwnershipPolicy.IsCreature(zdo)) { return false; }
            return zdo.GetBool(ZDOVars.s_alert) || zdo.GetBool(ZDOVars.s_haveTargetHash);
        }

        internal static void Reset() {
            Fighting.Clear();
            Rest.Clear();
            ListsReordered = 0;
            CreaturesMoved = 0;
        }
    }
}
