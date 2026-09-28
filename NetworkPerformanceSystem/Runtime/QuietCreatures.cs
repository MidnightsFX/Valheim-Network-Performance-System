using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M27 - a creature standing still stops re-sending itself.
    ///
    /// Every time anything in a ZDO changes, the whole ZDO goes out again: to the host, and from
    /// the host to every player in range - some 300-480 bytes for a tamed wolf, uncompressed. The
    /// game writes a creature's position, velocity and ground tilt whenever they differ at all
    /// from last frame, compared bit for bit, and a character's rigidbody never goes to sleep, so
    /// physics noise changes all three on nearly every frame of an animal that is not moving. The
    /// 2026-09-27 recording had idle pens uploaded on every 30 Hz send tick until the owner's
    /// uplink ran out (about 27 updates a second per animal, and 290-380 a second in total for
    /// owners of 30-54 animals), and the two players standing by those pens each receiving about
    /// 118 KB/s.
    ///
    /// The fix is a dead band against the value already in the ZDO - what everybody else holds,
    /// not what the owner had last frame - so a slow drift still goes out once it adds up, and the
    /// error anyone else sees is bounded by the band:
    ///
    ///   * position: 2 cm, and only while the stored velocity is zero. Viewers extrapolate the
    ///     stored velocity for up to two seconds past the last update, so a creature that has
    ///     told everybody it is moving must keep telling them where it is.
    ///   * velocity (s_velHash, the rigidbody velocity some prefabs sync, s_bodyVelocity): under
    ///     0.05 m/s is written as exactly zero, so stopping always lands; from a stored zero it
    ///     must reach 0.10 m/s before it is written, so noise around the snap threshold cannot
    ///     toggle it every tick; otherwise 0.05 m/s of change.
    ///   * ground tilt (s_tiltrot): 1 degree.
    ///   * facing (the ZDO's rotation): 1 degree, and only while the stored velocity is zero, as
    ///     for position. A grounded character re-applies its facing every frame and the game
    ///     stores it whenever the euler angles differ at all.
    ///   * the rigidbody's angular velocity (s_bodyAVelHash), written every frame alongside the
    ///     linear one for the prefabs that sync their rigidbody: the velocity rules, in rad/s.
    ///   * animator floats (forward, sideways and turn speed, and anything else ZSyncAnimation
    ///     .SetFloat carries): 0.05, and under 0.05 written as exactly zero. The game only asks
    ///     for a 0.01 change against the local animator, then stores the value bit for bit, and
    ///     forward speed is taken from the same never-sleeping rigidbody.
    ///
    /// The last three came from the 2026-09-28 recording: the first five sites were skipping
    /// 81-88% of what they were asked to write, yet each creature was still changing 52-80 times
    /// a second, 45-65 of them through writes nothing here covered.
    ///
    /// Creatures only (OwnershipPolicy.IsCreature - tames, summons and wild ones; never players,
    /// ships, carts or items), and never one that is alert or has a target: a fight keeps every
    /// update, exactly as before. A moving creature crosses these bands on every frame anyway.
    ///
    /// Owner-side, so it runs on whichever machine simulates the creature - a client with this
    /// mod, or the host - under the server's setting. It needs nothing from the server and the
    /// receivers are unchanged vanilla: they simply get fewer updates.
    ///
    /// The eight entry points replace the game's own ZDO calls at their call sites
    /// (QuietCreaturesPatches), taking exactly what those calls took off the stack. Everything
    /// that decides lives in the pure functions below, which name no Unity component, so the
    /// offline harness can table them.
    /// </summary>
    internal static class QuietCreatures {

        internal const float PositionDeadbandMetres = 0.02f;
        internal const float SnapSpeed = 0.05f;
        internal const float StartSpeed = 0.10f;
        internal const float VelocityDeadband = 0.05f;
        internal const float AnimatorSnap = 0.05f;
        internal const float AnimatorDeadband = 0.05f;

        /// <summary>cos(0.5 degrees). The angle between two rotations is 2 x acos(|dot|), so they
        /// are within one degree of each other exactly when |dot| is at least this.</summary>
        internal const float TiltDeadbandDot = 0.99996192f;

        private const float PositionDeadbandSq = PositionDeadbandMetres * PositionDeadbandMetres;
        private const float SnapSpeedSq = SnapSpeed * SnapSpeed;
        private const float StartSpeedSq = StartSpeed * StartSpeed;
        private const float VelocityDeadbandSq = VelocityDeadband * VelocityDeadband;

        // Since start. Main thread only, like the game code that calls in.
        internal static long PositionWritten;
        internal static long PositionSkipped;
        internal static long VelocityWritten;
        internal static long VelocitySkipped;
        internal static long RigidbodyWritten;
        internal static long RigidbodySkipped;
        internal static long BodyWritten;
        internal static long BodySkipped;
        internal static long TiltWritten;
        internal static long TiltSkipped;
        internal static long RotationWritten;
        internal static long RotationSkipped;
        internal static long AngularWritten;
        internal static long AngularSkipped;
        internal static long AnimatorWritten;
        internal static long AnimatorSkipped;

        internal static long TotalWritten => PositionWritten + VelocityWritten + RigidbodyWritten + BodyWritten + TiltWritten
                                             + RotationWritten + AngularWritten + AnimatorWritten;
        internal static long TotalSkipped => PositionSkipped + VelocitySkipped + RigidbodySkipped + BodySkipped + TiltSkipped
                                             + RotationSkipped + AngularSkipped + AnimatorSkipped;

        /// <summary>Read on every call, so the setting can be switched mid-session.</summary>
        internal static bool Active =>
            PatchGuard.IsActive(Mechanism.QuietCreatures)
            && ValConfig.QuietIdleCreatures != null
            && ValConfig.QuietIdleCreatures.Value;

        // -- the call-site replacements ------------------------------------------------------

        /// <summary>Replaces ZDO.SetPosition(Vector3) in ZSyncTransform.OwnerSync.</summary>
        internal static void SetPosition(ZDO zdo, Vector3 position) {
            if (Applies(zdo)) {
                Vector3 storedVelocity = zdo.GetVec3(ZDOVars.s_velHash, Vector3.zero);
                if (!ShouldWritePosition(zdo.GetPosition(), position, storedVelocity)) {
                    PositionSkipped++;
                    return;
                }
                PositionWritten++;
            }
            zdo.SetPosition(position);
        }

        /// <summary>Replaces the s_velHash write in ZSyncTransform.OwnerSync.</summary>
        internal static void SetTransformVelocity(ZDO zdo, int hash, Vector3 velocity) {
            if (Applies(zdo)) {
                if (!DecideVelocity(zdo.GetVec3(hash, Vector3.zero), velocity, out Vector3 write)) {
                    VelocitySkipped++;
                    return;
                }
                VelocityWritten++;
                velocity = write;
            }
            zdo.Set(hash, velocity);
        }

        /// <summary>Replaces the s_bodyVelHash write in ZSyncTransform.OwnerSync - made every frame,
        /// unconditionally, for the prefabs that sync their rigidbody (Lox, Asksvin, Bjorn...).</summary>
        internal static void SetRigidbodyVelocity(ZDO zdo, int hash, Vector3 velocity) {
            if (Applies(zdo)) {
                if (!DecideVelocity(zdo.GetVec3(hash, Vector3.zero), velocity, out Vector3 write)) {
                    RigidbodySkipped++;
                    return;
                }
                RigidbodyWritten++;
                velocity = write;
            }
            zdo.Set(hash, velocity);
        }

        /// <summary>Replaces the s_bodyVelocity write in Character.SyncVelocity.</summary>
        internal static void SetBodyVelocity(ZDO zdo, int hash, Vector3 velocity) {
            if (Applies(zdo)) {
                if (!DecideVelocity(zdo.GetVec3(hash, Vector3.zero), velocity, out Vector3 write)) {
                    BodySkipped++;
                    return;
                }
                BodyWritten++;
                velocity = write;
            }
            zdo.Set(hash, velocity);
        }

        /// <summary>Replaces the s_tiltrot writes in Character.UpdateGroundTilt. The second of the
        /// two is the wall-running branch, which only players reach; the creature test leaves it
        /// on the game's own behaviour.</summary>
        internal static void SetTilt(ZDO zdo, int hash, Quaternion tilt) {
            if (Applies(zdo)) {
                if (!TiltChanged(zdo.GetQuaternion(hash, Quaternion.identity), tilt)) {
                    TiltSkipped++;
                    return;
                }
                TiltWritten++;
            }
            zdo.Set(hash, tilt);
        }

        /// <summary>Replaces ZDO.SetRotation(Quaternion) in ZSyncTransform.OwnerSync.</summary>
        internal static void SetRotation(ZDO zdo, Quaternion rotation) {
            if (Applies(zdo)) {
                Vector3 storedVelocity = zdo.GetVec3(ZDOVars.s_velHash, Vector3.zero);
                if (!ShouldWriteRotation(storedVelocity, zdo.GetRotation(), rotation)) {
                    RotationSkipped++;
                    return;
                }
                RotationWritten++;
            }
            zdo.SetRotation(rotation);
        }

        /// <summary>Replaces the s_bodyAVelHash write in ZSyncTransform.OwnerSync - the angular half
        /// of the unconditional per-frame rigidbody write.</summary>
        internal static void SetRigidbodyAngularVelocity(ZDO zdo, int hash, Vector3 angularVelocity) {
            if (Applies(zdo)) {
                if (!DecideVelocity(zdo.GetVec3(hash, Vector3.zero), angularVelocity, out Vector3 write)) {
                    AngularSkipped++;
                    return;
                }
                AngularWritten++;
                angularVelocity = write;
            }
            zdo.Set(hash, angularVelocity);
        }

        /// <summary>Replaces the ZDO write in ZSyncAnimation.SetFloat(int, float). The key is the
        /// animator parameter's hash offset by the game's 438569, not a ZDOVars field.</summary>
        internal static void SetAnimatorFloat(ZDO zdo, int key, float value) {
            if (Applies(zdo)) {
                if (!DecideScalar(zdo.GetFloat(key, float.NaN), value, out float write)) {
                    AnimatorSkipped++;
                    return;
                }
                AnimatorWritten++;
                value = write;
            }
            zdo.Set(key, value);
        }

        /// <summary>
        /// Whether this write is one to consider at all. Active first: it is the cheap test, and
        /// every changed write through these call sites - items and projectiles included - pays
        /// for whatever comes before the answer. A null ZDO falls through to the game's own call,
        /// which fails exactly as it would have.
        /// </summary>
        private static bool Applies(ZDO zdo) {
            if (zdo == null || !Active) { return false; }
            if (!OwnershipPolicy.IsCreature(zdo)) { return false; }
            return !zdo.GetBool(ZDOVars.s_alert) && !zdo.GetBool(ZDOVars.s_haveTargetHash);
        }

        // -- the decisions (pure) ------------------------------------------------------------

        /// <summary>
        /// Whether a new position goes out. Always, while the stored velocity says the creature is
        /// moving: viewers are extrapolating that velocity, and only a new revision resets their
        /// timer. Otherwise only once it is PositionDeadbandMetres from the stored position.
        /// </summary>
        internal static bool ShouldWritePosition(Vector3 stored, Vector3 current, Vector3 storedVelocity) {
            if (!IsZero(storedVelocity)) { return true; }
            float dx = current.x - stored.x;
            float dy = current.y - stored.y;
            float dz = current.z - stored.z;
            return dx * dx + dy * dy + dz * dz >= PositionDeadbandSq;
        }

        /// <summary>
        /// Whether a new velocity goes out, and what is written. From a stored zero it has to reach
        /// StartSpeed; under SnapSpeed it is written as exactly zero (and skipped when that is
        /// already what is stored); otherwise it has to differ from the stored one by
        /// VelocityDeadband.
        /// </summary>
        internal static bool DecideVelocity(Vector3 stored, Vector3 current, out Vector3 write) {
            float speedSq = current.x * current.x + current.y * current.y + current.z * current.z;

            if (IsZero(stored)) {
                if (speedSq < StartSpeedSq) {
                    write = stored;
                    return false;
                }
                write = current;
                return true;
            }

            if (speedSq < SnapSpeedSq) {
                write = new Vector3(0f, 0f, 0f);
                return true;
            }

            float dx = current.x - stored.x;
            float dy = current.y - stored.y;
            float dz = current.z - stored.z;
            if (dx * dx + dy * dy + dz * dz < VelocityDeadbandSq) {
                write = stored;
                return false;
            }
            write = current;
            return true;
        }

        /// <summary>Whether two tilts are a degree or more apart. q and -q are the same rotation,
        /// which the absolute value takes care of.</summary>
        internal static bool TiltChanged(Quaternion stored, Quaternion current) {
            float dot = stored.x * current.x + stored.y * current.y + stored.z * current.z + stored.w * current.w;
            if (dot < 0f) { dot = -dot; }
            return dot < TiltDeadbandDot;
        }

        /// <summary>Whether a new facing goes out: always while the stored velocity says the
        /// creature is moving, otherwise once it has turned a degree from the stored one.</summary>
        internal static bool ShouldWriteRotation(Vector3 storedVelocity, Quaternion stored, Quaternion current) {
            return !IsZero(storedVelocity) || TiltChanged(stored, current);
        }

        /// <summary>
        /// Whether a new animator float goes out, and what is written. Nothing stored yet (NaN)
        /// always goes out. Under AnimatorSnap it is written as exactly zero, and skipped when
        /// that is already what is stored, so a creature coming to a halt always lands on zero;
        /// otherwise it has to differ from the stored value by AnimatorDeadband.
        /// </summary>
        internal static bool DecideScalar(float stored, float current, out float write) {
            if (float.IsNaN(stored)) {
                write = current;
                return true;
            }
            if (current > -AnimatorSnap && current < AnimatorSnap) {
                write = 0f;
                return stored != 0f;
            }
            float delta = current - stored;
            if (delta > -AnimatorDeadband && delta < AnimatorDeadband) {
                write = stored;
                return false;
            }
            write = current;
            return true;
        }

        private static bool IsZero(Vector3 v) {
            return v.x == 0f && v.y == 0f && v.z == 0f;
        }

        internal static void Reset() {
            PositionWritten = 0;
            PositionSkipped = 0;
            VelocityWritten = 0;
            VelocitySkipped = 0;
            RigidbodyWritten = 0;
            RigidbodySkipped = 0;
            BodyWritten = 0;
            BodySkipped = 0;
            TiltWritten = 0;
            TiltSkipped = 0;
            RotationWritten = 0;
            RotationSkipped = 0;
            AngularWritten = 0;
            AngularSkipped = 0;
            AnimatorWritten = 0;
            AnimatorSkipped = 0;
        }
    }
}
