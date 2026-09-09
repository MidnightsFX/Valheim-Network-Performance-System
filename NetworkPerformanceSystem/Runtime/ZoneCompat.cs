using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// The zone and sector surface this mod reads, in one place.
    ///
    /// The game replaced the two global ints ZoneSystem.m_activeArea / m_activeDistantArea with a
    /// per-peer SimulationDistance, dropped the zone-vs-zone ZNetScene.InActiveArea overload,
    /// moved SectorToIndex from ZDOMan to ZoneSystem, and split portal ZDOs into a store of their
    /// own. Every mechanism here used at least one of those, so they are wrapped once instead of
    /// being adapted at a dozen call sites.
    ///
    /// The field mapping is 1:1 - m_activeArea is NearSimulationDistance, m_activeDistantArea is
    /// FarSimulationDistance. ZoneSystem.CreateLocalZones and CreateGhostZones used to read those
    /// two fields and now read exactly these two properties, and the game's own
    /// SimulationDistance.OriginalDistance is (2, 2), which is what those fields held at runtime.
    ///
    /// What genuinely changed is that the value is no longer global: every peer negotiates its
    /// own, capped at the server's, and can change it mid-session from the graphics settings. So
    /// anything measured against "how far this peer simulates" has to read that peer's value
    /// rather than one number for the whole server.
    /// </summary>
    internal static class ZoneCompat {

        /// <summary>
        /// Our own simulation distance - the server's, when called on the host.
        ///
        /// ZNet.GetSyncedSimulationDistance() reaches through GraphicsSettingsManager.Instance,
        /// which is a plain static field with no guarantee of existing on a headless server, so
        /// the value ZNet last applied is preferred when it is absent. OriginalDistance is the
        /// game's own name for the pre-update behaviour and is the right thing to degrade to.
        /// </summary>
        internal static SimulationDistance Local() {
            ZNet net = ZNet.instance;
            if (net == null) { return SimulationDistance.OriginalDistance; }

            if (GraphicsSettingsManager.Instance != null) {
                return net.GetSyncedSimulationDistance();
            }

            SimulationDistance applied = net.m_simulationDistance;
            return applied.TotalSimulationDistance > 0 ? applied : SimulationDistance.OriginalDistance;
        }

        /// <summary>
        /// A peer's negotiated simulation distance.
        ///
        /// A peer that has not finished the PeerInfo handshake still carries the zero-initialised
        /// struct. Taken at face value that reads as "simulates nothing" and collapses every
        /// radius built from it to zero, which would quietly exclude a peer that is in fact
        /// loading the world around itself - so fall back to our own value until it reports one.
        /// </summary>
        internal static SimulationDistance For(ZNetPeer peer) {
            if (peer == null) { return Local(); }
            SimulationDistance distance = peer.m_simulationDistance;
            return distance.TotalSimulationDistance > 0 ? distance : Local();
        }

        /// <summary>The old ZoneSystem.m_activeArea for this peer: how far it loads zones.</summary>
        internal static int NearFor(ZNetPeer peer) {
            return Mathf.Max(1, For(peer).NearSimulationDistance);
        }

        /// <summary>
        /// The zone radius around a peer that counts as its active area - the zones it has
        /// actually instantiated, and so the only ones it can own and simulate in.
        ///
        /// This is deliberately NOT derived from simulation distance. ZNetScene.PointInsideActiveArea
        /// is a Chebyshev distance of 1.5 zones from the peer's zone centre, dropping to 1.0 only
        /// at a near distance of 1, and it does not otherwise widen as simulation distance grows -
        /// raising the setting loads and syncs more of the world without extending what the peer
        /// actively simulates. At zone granularity 1.5 zones is exactly the 3x3 square, which is
        /// the same radius the pre-update code derived from m_activeArea - 1.
        ///
        /// Scaling this with the peer's near distance instead, which is the tempting reading of
        /// the rename, would hand a long-distance peer ownership of objects it has synced but not
        /// instantiated - the frozen-creature failure this whole mechanism exists to avoid.
        /// </summary>
        internal const int ActiveZoneRadius = 1;

        /// <summary>
        /// Stands in for the ZNetScene.InActiveArea(zone, centerZone, area) overload the update
        /// removed. The surviving overloads take a world position and read the *local* peer's
        /// simulation distance, which is the wrong question on a host deciding what some other
        /// peer can see - so the radius stays an explicit argument here.
        /// </summary>
        internal static bool InActiveArea(Vector2s zone, Vector2s centerZone, int radius) {
            return Mathf.Abs(zone.x - centerZone.x) <= radius
                && Mathf.Abs(zone.y - centerZone.y) <= radius;
        }

        /// <summary>
        /// A zone's live sector list, or null when the zone holds nothing.
        ///
        /// Mirrors ZDOMan.FindObjects: the index is clamped rather than bounds-checked, with
        /// everything outside the 512x512 sector grid landing in bucket 0, so no separate
        /// "outside sector" lookup is needed any more.
        /// </summary>
        internal static List<ZDO> SectorObjects(ZDOMan man, Vector2s zone) {
            return man.m_objectsBySector[ZoneSystem.SectorToIndex(zone).Sector];
        }

        /// <summary>
        /// A zone's portal ZDOs, which no longer live in the sector lists. Kept separate rather
        /// than merged into SectorObjects so callers keep reading the live lists in place instead
        /// of allocating a joined copy per zone.
        /// </summary>
        internal static List<ZDO> PortalObjects(ZDOMan man, Vector2s zone) {
            return man.m_portalObjects.TryGetValue(ZoneSystem.SectorToIndex(zone), out List<ZDO> portals)
                ? portals
                : null;
        }
    }
}
