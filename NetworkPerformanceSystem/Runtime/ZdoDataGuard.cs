using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M24 - a client holds world updates that arrive before it can accept them.
    ///
    /// ZDOMan.AddPeer registers the "ZDOData" handler, and on a client it runs inside
    /// ZNet.RPC_PeerInfo - the client's half of the handshake. ZRpc.HandlePackage drops a message
    /// whose handler does not exist yet, without a word. In the unmodded game that gap never
    /// opens: the server sends its PeerInfo before it starts sending objects, over one ordered
    /// connection, and the client handles PeerInfo synchronously. It opens when a mod holds the
    /// client's RPC_PeerInfo back to run first and re-invokes it later. Every update that lands in
    /// between is then lost for good, because the server recorded each object as sent to this
    /// peer at send time (ZDOMan.SendZDOs) and only sends it again once it changes - walls,
    /// chests and trees that do not change are simply missing until something touches them.
    ///
    /// So from the moment the connection opens until AddPeer, a handler of ours holds them, and
    /// AddPeer's postfix hands them to the real one in arrival order. AddPeer's own Register
    /// replaces ours (Register removes before adding), so after that the game's handler sees
    /// everything directly and this costs nothing. Holding a package is safe: both vanilla's
    /// dispatch and M18's hand the handler a fresh ReadPackage() copy that nothing else keeps.
    ///
    /// If RPC_PeerInfo throws before reaching AddPeer, nothing is ever replayed - the handshake is
    /// broken and the connection with it. The guard can only say so, which it does once the
    /// updates have been held for StuckWarnSeconds.
    /// </summary>
    internal static class ZdoDataGuard {

        /// <summary>Caps per connection. A healthy delay is a second or two of a few packets per
        /// frame; anything near these is a handshake that is not coming back, and past them the
        /// excess is dropped exactly as vanilla would drop all of it.</summary>
        internal const int MaxHeldPackages = 512;
        internal const long MaxHeldBytes = 16L * 1024 * 1024;

        /// <summary>How long updates may sit here before the log says the handshake looks stuck.</summary>
        internal const float StuckWarnSeconds = 10f;

        internal enum HoldOutcome { Held, Dropped }

        internal sealed class Held {
            internal readonly List<ZPackage> Packages = new List<ZPackage>();
            internal long Bytes;
            internal int Dropped;
            internal float FirstAt = -1f;
            internal bool WarnedOverflow;
            internal bool WarnedStuck;
        }

        private static readonly Dictionary<ZRpc, Held> Pending = new Dictionary<ZRpc, Held>();

        // Process lifetime, for nps_stats. Survive Reset on purpose: "did this ever happen" is the
        // question, and the answer should not vanish when the player goes back to the menu.
        internal static int HandshakesDelayed { get; private set; }
        internal static long PackagesReplayed { get; private set; }
        internal static long BytesReplayed { get; private set; }
        internal static long PackagesDropped { get; private set; }
        internal static int ReplayFailures { get; private set; }
        internal static float LastDelaySeconds { get; private set; }

        internal static bool Wanted =>
            PatchGuard.IsActive(Mechanism.EarlyZdoData) && ValConfig.EnableEarlyZdoDataGuard.Value;

        /// <summary>Starts holding for a new connection. Called with our handler already
        /// registered on it.</summary>
        internal static void Arm(ZRpc rpc) {
            if (rpc == null) { return; }
            Pending[rpc] = new Held();
        }

        /// <summary>The temporary "ZDOData" handler. Static and two-parameter, so M18's direct
        /// dispatch still applies and nothing is allocated per connection.</summary>
        internal static void RPC_HoldZDOData(ZRpc rpc, ZPackage pkg) {
            if (rpc == null || pkg == null) { return; }
            if (!Pending.TryGetValue(rpc, out Held held)) { return; }       // not ours to hold: dropped, as vanilla

            float now = Time.realtimeSinceStartup;
            if (Hold(held, pkg, pkg.Size(), now) == HoldOutcome.Dropped && !held.WarnedOverflow) {
                held.WarnedOverflow = true;
                Logger.LogWarning(
                    $"Holding {held.Packages.Count} world updates ({held.Bytes / 1024} KB) that arrived before this game finished its " +
                    $"side of the connection handshake; that is the limit, so further ones are being dropped as the game itself would. " +
                    $"Objects in them stay missing until they next change. Another mod is holding ZNet.RPC_PeerInfo back for a long time.");
            }

            if (!held.WarnedStuck && held.FirstAt >= 0f && now - held.FirstAt > StuckWarnSeconds) {
                held.WarnedStuck = true;
                Logger.LogWarning(
                    $"World updates have been arriving for {now - held.FirstAt:F0}s before this game finished its side of the connection " +
                    $"handshake. Some mod has delayed ZNet.RPC_PeerInfo, or it failed part-way - check the log above for an error there. " +
                    $"They are being held and will be applied if the handshake completes.");
            }
        }

        /// <summary>Adds one package within the caps. No Unity calls, so it runs offline. </summary>
        internal static HoldOutcome Hold(Held held, ZPackage pkg, int sizeBytes, float now) {
            if (held.Packages.Count >= MaxHeldPackages || held.Bytes + sizeBytes > MaxHeldBytes) {
                held.Dropped++;
                return HoldOutcome.Dropped;
            }
            if (held.FirstAt < 0f) { held.FirstAt = now; }
            held.Packages.Add(pkg);
            held.Bytes += sizeBytes;
            return HoldOutcome.Held;
        }

        /// <summary>
        /// From the ZDOMan.AddPeer postfix: the game's handler exists now, so everything held for
        /// this connection goes to it in the order it arrived. Each package is applied on its own:
        /// this runs inside RPC_PeerInfo, ahead of ZRoutedRpc.AddPeer, and an exception escaping
        /// here would leave the whole session without routed RPCs.
        /// </summary>
        internal static void Replay(ZDOMan man, ZNetPeer netPeer) {
            ZRpc rpc = netPeer?.m_rpc;
            if (man == null || rpc == null) { return; }
            if (!Pending.TryGetValue(rpc, out Held held)) { return; }
            Pending.Remove(rpc);

            if (held.Packages.Count == 0 && held.Dropped == 0) { return; }  // the normal case: nothing came early

            float waited = held.FirstAt >= 0f ? Time.realtimeSinceStartup - held.FirstAt : 0f;
            int applied = 0;
            int failed = 0;
            Exception firstError = null;

            for (int i = 0; i < held.Packages.Count; i++) {
                try {
                    man.RPC_ZDOData(rpc, held.Packages[i]);
                    applied++;
                } catch (Exception e) {
                    failed++;
                    if (firstError == null) { firstError = e; }
                }
            }

            HandshakesDelayed++;
            PackagesReplayed += applied;
            BytesReplayed += held.Bytes;
            PackagesDropped += held.Dropped;
            ReplayFailures += failed;
            LastDelaySeconds = waited;

            Logger.LogInfo(
                $"{applied} world update packets ({held.Bytes / 1024} KB) arrived {waited:F1}s before this game finished its side of the " +
                $"connection handshake and were applied once it had. Without this they would have been lost until each object next " +
                $"changed. Another mod delayed ZNet.RPC_PeerInfo on this client." +
                (held.Dropped > 0 ? $" {held.Dropped} more arrived past the holding limit and were lost." : ""));

            if (firstError != null) {
                Logger.LogWarning($"{failed} of the held world updates could not be applied. First error: {firstError}");
            }
        }

        /// <summary>The connection went away before AddPeer. What it held goes with it.</summary>
        internal static void Forget(ZRpc rpc) {
            if (rpc == null) { return; }
            if (Pending.TryGetValue(rpc, out Held held)) {
                PackagesDropped += held.Packages.Count + held.Dropped;
                Pending.Remove(rpc);
            }
        }

        internal static void Reset() {
            Pending.Clear();
        }
    }
}
