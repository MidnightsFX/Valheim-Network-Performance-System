using UnityEngine;
using UnityEngine.Rendering;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// Which side of the connection we are on, and how far along startup we are.
    ///
    /// Deliberately does not use ZNet.IsDedicated() - in the current game that method is a stub
    /// that unconditionally returns false, so anything built on it (including Jotunn's
    /// IsServerInstance()) silently reports "not dedicated" on a real dedicated server.
    /// </summary>
    internal static class NpsEnv {

        private static bool? _isDedicatedCache;

        /// <summary>
        /// True on a dedicated server AND on a listen-server host. This is the gate for every
        /// host-side mechanism (M2, M2b, M3) because both need to arbitrate for multiple peers.
        /// </summary>
        internal static bool IsHost() {
            return ZNet.instance != null && ZNet.instance.IsServer();
        }

        /// <summary>
        /// True only on a headless dedicated server. Used solely for the M3 CPU trade-off: a
        /// dedicated host taking ownership of a contested ZDO must actually simulate it, whereas
        /// a listen host is already simulating its own surroundings anyway.
        /// </summary>
        internal static bool IsDedicated() {
            if (_isDedicatedCache.HasValue) { return _isDedicatedCache.Value; }

            bool dedicated = Application.isBatchMode
                             || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
            _isDedicatedCache = dedicated;
            return dedicated;
        }

        /// <summary>True when we are a client connected to someone else's host.</summary>
        internal static bool IsClient() {
            return ZNet.instance != null && !ZNet.instance.IsServer();
        }

        /// <summary>
        /// Our own session id - the value ZDOs carry as their owner when we are simulating them.
        /// Note this is the LOCAL id on whichever side calls it, not the host's; on a client it is
        /// that client's id. It only equals the host's id when called on the host.
        /// </summary>
        internal static long LocalSessionId() {
            return ZDOMan.s_instance != null ? ZDOMan.GetSessionID() : 0L;
        }

        /// <summary>Networking is only meaningful once ZNet and ZDOMan both exist.</summary>
        internal static bool NetReady() {
            return ZNet.instance != null && ZDOMan.s_instance != null;
        }
    }
}
