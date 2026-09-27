using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M28 - the host sends a settled or distant creature to each player less often.
    ///
    /// The game sends a ZDO to a peer on every send tick in which its revision has moved
    /// (ZDOPeer.ShouldSend), and every send is the whole ZDO. A creature whose owner writes it on
    /// every frame therefore goes to everybody in range at the full send rate - 30 times a second
    /// with the scheduler - whether it is charging at someone or standing in a pen. QuietCreatures
    /// stops that at the source, but only on owners running this mod; this is the host's half,
    /// which covers every owner, and also thins out movement too far away to see in detail.
    ///
    /// Per peer, per creature, from the time the game itself records for the last send to that
    /// peer (PeerZDOInfo.m_syncTime):
    ///
    ///   * moving (host's copy of s_velHash at 0.25 m/s or more, or within SettleSeconds of it):
    ///     every tick inside 64 m, at most 15 a second beyond. At 8 m/s the extra 33 ms is under
    ///     0.3 m at a distance where that is a fraction of a degree of view.
    ///   * settled: at most 10 a second inside 32 m, 5 beyond. A settled creature's updates are
    ///     sub-centimetre noise; starting to move is never delayed near a player, because the
    ///     host's copy then reads as moving.
    ///
    /// The settle window is what keeps the "I have stopped" update from being held: without it,
    /// the first stationary update could wait 0.2 s while every viewer went on extrapolating the
    /// last running velocity, over a metre at a run.
    ///
    /// Never held: a peer's first copy, an owner change, anything in the peer's force-send set
    /// (the arbiter's, the RPC router's and the stale-owner guard's corrections all go through
    /// it), anything owned by that peer, a ridden mount, and anything alert or with a target.
    /// A held ZDO keeps its old PeerZDOInfo, so it qualifies again on the next tick with its age
    /// priority still growing; nothing is lost, because every send carries the full state.
    ///
    /// Applied as a filter over the finished sync list (CreaturePacingPatches) rather than inside
    /// ShouldSend: AddForceSendZdos drops an id from the force-send set when ShouldSend says no,
    /// which would silently cancel those corrections.
    /// </summary>
    internal static class CreaturePacing {

        internal const float NearMetres = 32f;
        internal const float FarMetres = 64f;
        internal const float MovingSpeed = 0.25f;
        internal const float SettleSeconds = 0.5f;
        internal const float IdleNearInterval = 0.10f;
        internal const float IdleFarInterval = 0.20f;
        internal const float MovingFarInterval = 1f / 15f;

        private const float NearSq = NearMetres * NearMetres;
        private const float FarSq = FarMetres * FarMetres;
        private const float MovingSpeedSq = MovingSpeed * MovingSpeed;
        private const float PruneIntervalSeconds = 10f;

        internal enum Hold : byte { Send, IdleNear, IdleFar, MovingFar }

        /// <summary>Until when each creature counts as moving. Keyed by ZDOID, which the game never
        /// reuses, so a pooled ZDO handed out again cannot inherit an entry.</summary>
        private static readonly Dictionary<ZDOID, float> MovingUntil = new Dictionary<ZDOID, float>();
        private static readonly List<ZDOID> Expired = new List<ZDOID>();
        private static float _lastPrune;

        private sealed class PeerCounts {
            internal long Deferred;
            internal long Listed;
        }
        private static readonly Dictionary<long, PeerCounts> ByPeer = new Dictionary<long, PeerCounts>();

        // Since start.
        internal static long DeferredIdleNear;
        internal static long DeferredIdleFar;
        internal static long DeferredMovingFar;
        internal static long Listed;

        internal static long Deferred => DeferredIdleNear + DeferredIdleFar + DeferredMovingFar;

        /// <summary>Creatures counted as moving right now. For nps_stats only: it walks the table.</summary>
        internal static int MovingAt(float now) {
            int moving = 0;
            foreach (KeyValuePair<ZDOID, float> entry in MovingUntil) {
                if (now < entry.Value) { moving++; }
            }
            return moving;
        }

        internal static bool Active =>
            PatchGuard.IsActive(Mechanism.CreaturePacing)
            && ValConfig.PaceCreatureSends != null
            && ValConfig.PaceCreatureSends.Value;

        /// <summary>
        /// Drops the creatures this peer is not due for from a finished sync list, in place and in
        /// order, so whatever AddForceSendZdos put at the head stays there.
        /// </summary>
        internal static void Filter(ZDOMan.ZDOPeer peer, List<ZDO> toSync, Vector3 refPos, float now) {
            if (peer == null || toSync == null || toSync.Count == 0) { return; }

            PruneIfDue(now);

            int kept = 0;
            int listed = 0;
            int deferred = 0;
            for (int i = 0; i < toSync.Count; i++) {
                ZDO zdo = toSync[i];
                if (zdo != null && OwnershipPolicy.IsCreature(zdo)) {
                    listed++;
                    Hold hold = Decide(peer, zdo, refPos, now, OwnershipPolicy.IsDirectlyControlled(zdo));
                    if (hold != Hold.Send) {
                        deferred++;
                        switch (hold) {
                            case Hold.IdleNear: DeferredIdleNear++; break;
                            case Hold.IdleFar: DeferredIdleFar++; break;
                            default: DeferredMovingFar++; break;
                        }
                        continue;
                    }
                }
                toSync[kept++] = zdo;
            }
            if (kept < toSync.Count) { toSync.RemoveRange(kept, toSync.Count - kept); }

            Listed += listed;
            if (listed > 0 && peer.m_peer != null) {
                if (!ByPeer.TryGetValue(peer.m_peer.m_uid, out PeerCounts counts)) {
                    counts = new PeerCounts();
                    ByPeer[peer.m_peer.m_uid] = counts;
                }
                counts.Listed += listed;
                counts.Deferred += deferred;
            }
        }

        /// <summary>
        /// Whether this peer gets this creature now. Only asked about creatures. Everything that
        /// must go out at once is ruled in before any timing is looked at.
        /// </summary>
        internal static Hold Decide(ZDOMan.ZDOPeer peer, ZDO zdo, Vector3 refPos, float now, bool controlled) {
            if (!peer.m_zdos.TryGetValue(zdo.m_uid, out ZDOMan.ZDOPeer.PeerZDOInfo info)) { return Hold.Send; }
            if (zdo.OwnerRevision > info.m_ownerRevision) { return Hold.Send; }
            if (peer.m_forceSend.Contains(zdo.m_uid)) { return Hold.Send; }
            if (peer.m_peer != null && zdo.GetOwner() == peer.m_peer.m_uid) { return Hold.Send; }
            if (controlled) { return Hold.Send; }
            if (zdo.GetBool(ZDOVars.s_alert) || zdo.GetBool(ZDOVars.s_haveTargetHash)) { return Hold.Send; }

            Vector3 velocity = zdo.GetVec3(ZDOVars.s_velHash, Vector3.zero);
            bool moving = IsMoving(zdo.m_uid, velocity.x * velocity.x + velocity.y * velocity.y + velocity.z * velocity.z, now);

            Vector3 position = zdo.GetPosition();
            float dx = position.x - refPos.x;
            float dy = position.y - refPos.y;
            float dz = position.z - refPos.z;
            float distanceSq = dx * dx + dy * dy + dz * dz;

            float interval = MinIntervalSeconds(distanceSq, moving);
            if (interval <= 0f || now - info.m_syncTime >= interval) { return Hold.Send; }
            if (moving) { return Hold.MovingFar; }
            return distanceSq < NearSq ? Hold.IdleNear : Hold.IdleFar;
        }

        /// <summary>The shortest gap between two sends of one creature to one peer. Pure.</summary>
        internal static float MinIntervalSeconds(float distanceSq, bool moving) {
            if (moving) { return distanceSq < FarSq ? 0f : MovingFarInterval; }
            return distanceSq < NearSq ? IdleNearInterval : IdleFarInterval;
        }

        /// <summary>Moving now, or stopped less than SettleSeconds ago.</summary>
        internal static bool IsMoving(ZDOID id, float speedSq, float now) {
            if (speedSq >= MovingSpeedSq) {
                MovingUntil[id] = now + SettleSeconds;
                return true;
            }
            return MovingUntil.TryGetValue(id, out float until) && now < until;
        }

        private static void PruneIfDue(float now) {
            if (now - _lastPrune < PruneIntervalSeconds) { return; }
            _lastPrune = now;
            if (MovingUntil.Count == 0) { return; }

            Expired.Clear();
            foreach (KeyValuePair<ZDOID, float> entry in MovingUntil) {
                if (entry.Value <= now) { Expired.Add(entry.Key); }
            }
            for (int i = 0; i < Expired.Count; i++) { MovingUntil.Remove(Expired[i]); }
        }

        /// <summary>A peer's running totals, for the monitoring peer record. False for a peer this
        /// has not listed anything for.</summary>
        internal static bool TryGetPeerCounts(long uid, out long deferred, out long listed) {
            if (ByPeer.TryGetValue(uid, out PeerCounts counts)) {
                deferred = counts.Deferred;
                listed = counts.Listed;
                return true;
            }
            deferred = 0;
            listed = 0;
            return false;
        }

        internal static void ForgetPeer(long uid) {
            ByPeer.Remove(uid);
        }

        internal static void Reset() {
            MovingUntil.Clear();
            Expired.Clear();
            ByPeer.Clear();
            _lastPrune = 0f;
            DeferredIdleNear = 0;
            DeferredIdleFar = 0;
            DeferredMovingFar = 0;
            Listed = 0;
        }
    }
}
