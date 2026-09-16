using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M14 - a periodic dip in the send queue for mods that wait on a fixed threshold.
    ///
    /// ServerSync (and every mod that bundles it), ConditionalConfigSync and others carry the same
    /// loop Jotunn's CustomRPC does: before sending, wait until GetSendQueueSize() is under a
    /// hard-coded 10000-20000 bytes, and disconnect the peer after 30 seconds if it never is. Unlike
    /// Jotunn's, their threshold is a compile-time constant folded into every copy, so there is
    /// nothing to write to (see JotunnSendQueue for the Jotunn half). What can be done is on this
    /// side: a peer whose queue has sat above their floor for long enough gets its ZDO sends held
    /// at that floor for a fraction of a second - long enough for whoever is waiting to see the dip
    /// and get their packet out - and then the window opens again.
    ///
    /// The drain is a change in what SendWindow.For returns, not a separate gate, so it goes
    /// through the same two rewritten sites in ZDOMan.SendZDOs as the window itself, and it is fed
    /// by the queue read SendZDOs already makes (SendWindowPatches hands the value over before
    /// either site runs) rather than by a second Steam call of its own. It is held for one tick
    /// past the moment the queue is seen under the floor, because the waiting coroutine runs after
    /// ZNet.Update: releasing on the observation would let that same send refill the queue to the
    /// window before anyone looked at it.
    ///
    /// Only peers whose window is actually above vanilla are tracked. Everyone else - low-RTT
    /// peers, crossplay peers with no RTT measurement, an idle peer whose queue is already under
    /// the floor - costs a dictionary lookup here and nothing more.
    /// </summary>
    internal static class QueueDrain {

        /// <summary>Longest a drain may hold a peer. A 64KB window empties in well under a second
        /// at 150 KB/s plus one round trip; a queue that will not fall under the floor in this long
        /// is being held up by something other than ZDO data, and starving ZDOs on top would only
        /// make the peer worse off.</summary>
        internal const float MaxDrainSeconds = 3f;

        private const int DefaultFloorBytes = 8192;

        internal sealed class State {
            internal float LastBelowFloorAt;
            internal float DrainStartedAt;
            internal bool Draining;
            internal bool ReleaseOnNextObserve;
            internal int Drains;
            internal int HeldTicks;
            internal int Aborted;
            internal float LastDrainSeconds;
        }

        private static readonly Dictionary<long, State> Peers = new Dictionary<long, State>();

        internal static bool Enabled {
            get {
                return PatchGuard.IsActive(Mechanism.QueueDrain)
                       && PatchGuard.IsActive(Mechanism.SendWindow)
                       && ValConfig.EnableSendWindowSizing != null
                       && ValConfig.EnableSendWindowSizing.Value
                       && ValConfig.QueueDrainIntervalSeconds != null
                       && ValConfig.QueueDrainIntervalSeconds.Value > 0f;
            }
        }

        internal static int FloorBytes =>
            ValConfig.QueueDrainFloorBytes != null ? ValConfig.QueueDrainFloorBytes.Value : DefaultFloorBytes;

        /// <summary>Read by SendWindow.For, two or three times per send attempt. Pure.</summary>
        internal static bool IsDraining(long peerUid) {
            return Peers.TryGetValue(peerUid, out State state) && state.Draining;
        }

        internal static bool TryGetState(long peerUid, out State state) {
            return Peers.TryGetValue(peerUid, out state);
        }

        /// <summary>
        /// The only mutator. Called from the rewritten IL in ZDOMan.SendZDOs with the queue size
        /// that method has just read, once per peer per send attempt and before either window site
        /// runs. Sits directly in the send path: must be cheap and must never throw.
        /// </summary>
        internal static void Observe(int queue, ZDOMan.ZDOPeer peer, bool flush) {
            if (flush) { return; }              // SendAllZDOs on disconnect - not a steady-state send

            ZNetPeer netPeer = peer?.m_peer;
            if (netPeer == null || netPeer.m_uid == 0L) { return; }
            long uid = netPeer.m_uid;

            // LastWindow is only ever written for a measured peer, so an absent or vanilla entry
            // is a peer the window is not opening for - and one whose queue vanilla already keeps
            // under every threshold in the wild.
            if (!Enabled
                || !SendWindow.TryGetLastWindow(uid, out int window)
                || window <= SendWindow.VanillaWindowBytes) {
                Peers.Remove(uid);
                return;
            }

            float now = Time.realtimeSinceStartup;
            int floor = FloorBytes;
            float interval = ValConfig.QueueDrainIntervalSeconds.Value;

            if (!Peers.TryGetValue(uid, out State s)) {
                s = new State { LastBelowFloorAt = now };
                Peers[uid] = s;
            }

            if (s.ReleaseOnNextObserve) {
                s.Draining = false;
                s.ReleaseOnNextObserve = false;
                s.LastDrainSeconds = now - s.DrainStartedAt;
            }

            if (queue <= floor) {
                s.LastBelowFloorAt = now;
                if (s.Draining) {
                    // Hold this tick at the floor so the queue is still under it when the waiting
                    // coroutine looks, after Update; open the window again on the next attempt.
                    s.ReleaseOnNextObserve = true;
                }
                return;
            }

            if (s.Draining) {
                s.HeldTicks++;
                if (now - s.DrainStartedAt > MaxDrainSeconds) {
                    s.Draining = false;
                    s.Aborted++;
                    s.LastBelowFloorAt = now;
                    Logger.LogDebug($"Queue drain: gave up on {Name(netPeer)} after {MaxDrainSeconds:F0}s, queue still {queue} bytes - something other than ZDO data is holding the socket");
                }
                return;
            }

            if (now - s.LastBelowFloorAt >= interval) {
                s.Draining = true;
                s.DrainStartedAt = now;
                s.Drains++;
                Logger.LogDebug($"Queue drain: holding ZDO sends to {Name(netPeer)} at {floor} bytes (queue {queue}, window {window})");
            }
        }

        private static string Name(ZNetPeer peer) {
            return string.IsNullOrEmpty(peer.m_playerName) ? peer.m_uid.ToString() : peer.m_playerName;
        }

        internal static void Forget(long peerUid) {
            Peers.Remove(peerUid);
        }

        internal static void Reset() {
            Peers.Clear();
        }
    }
}
