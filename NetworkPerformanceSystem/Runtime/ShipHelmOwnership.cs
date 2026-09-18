using System.Collections.Generic;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M20 - a ship is simulated by whoever is steering it.
    ///
    /// Only a ZDO's owner simulates it; everyone else sees it relayed owner -> host -> them. The
    /// helmsman is attached to the ship, so when somebody else owns it their camera rides that
    /// relayed motion and every throttle change takes a round trip. Vanilla never corrects this:
    /// ShipControlls.RPC_RequestControl only records who is steering in s_user, and
    /// Ship.UpdateOwner hands the ship on only when its owner's own player has left the boat -
    /// and then to whoever boarded first, not to the helmsman. A dedicated server that owns a
    /// ship never hands it on at all, because that check needs a local player.
    ///
    /// Vanilla already does the right thing for the other two things a player drives: Sadle and
    /// Vagon both call SetOwner(sender) on the owner, right after granting control. This applies
    /// that same pattern to the helm.
    ///
    /// The handoff is made by the CURRENT OWNER and never by anyone else, and that is
    /// load-bearing. A moving ship's owner writes its ZDO every send tick. If the host or the
    /// helmsman changed the owner instead, the old owner's writes already on the wire would carry
    /// a higher data revision and an older owner revision, and ZDOMan.RPC_ZDOData on the host
    /// applies such a packet in full - dragging the owner back while the old owner has already
    /// stopped simulating. When the owner hands off itself, it stops writing in the same frame,
    /// so nothing older can still be in flight. The cost is that a ship owned by a player
    /// without this mod behaves as in vanilla.
    /// </summary>
    internal static class ShipHelmOwnership {

        // Diagnostics. Counted on whichever machine owned the ship, so a client's count is its
        // own and is not included in the host's.
        internal static long HandedOff;

        internal static bool Enabled => PatchGuard.IsActive(Mechanism.ShipHelmOwnership)
            && ValConfig.ShipOwnershipFollowsHelmsman.Value;

        /// <summary>
        /// If this machine owns the ship and someone else is validly at its helm, give the ship
        /// to them. Returns true when ownership was handed over.
        /// </summary>
        internal static bool TryHandToHelmsman(Ship ship) {
            if (ship == null || ship.m_shipControlls == null) { return false; }

            ZNetView nview = ship.m_nview;
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) { return false; }

            // "User set and standing in this boat" - vanilla's own test, so a helmsman who has
            // disconnected or gone overboard is never handed a ship.
            ShipControlls controls = ship.m_shipControlls;
            if (!controls.HaveValidUser()) { return false; }

            long helmsman = HelmsmanPeer(ship, controls.GetUser());
            if (helmsman == 0L || helmsman == ZDOMan.GetSessionID()) { return false; }

            ZDO zdo = nview.GetZDO();
            zdo.SetOwner(helmsman);
            // Ownership travels with the next ZDO send while the grant already went out as an
            // RPC. Put it at the front of that send so the helmsman's first throttle press has
            // the best chance of reaching the right machine. On the host this targets the
            // helmsman's peer; on a client it targets the server, which relays it.
            ZDOMan.instance.ForceSendZDO(helmsman, zdo.m_uid);
            HandedOff++;

            if (Logger.Level >= BepInEx.Logging.LogLevel.Debug) {
                Logger.LogDebug($"Ship helm: handed {ship.name} {zdo.m_uid} to its helmsman {helmsman}.");
            }
            return true;
        }

        /// <summary>
        /// The session id of the player at the helm, read from their character as this machine
        /// sees it - the same lookup Ship.GetNewOwnerID uses. A Player ZDO is always owned by its
        /// own player's session, which is what SetOwner takes.
        /// </summary>
        private static long HelmsmanPeer(Ship ship, long playerId) {
            List<Player> players = ship.m_players;
            for (int i = 0; i < players.Count; i++) {
                Player player = players[i];
                if (player == null || player.GetPlayerID() != playerId) { continue; }
                return player.GetOwner();
            }
            return 0L;
        }

        internal static void Reset() {
            HandedOff = 0;
        }
    }
}
