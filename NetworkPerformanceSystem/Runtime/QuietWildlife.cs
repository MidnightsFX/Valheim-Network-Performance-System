using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M31 - fish and birds send themselves a few times a second instead of on every frame.
    ///
    /// Fish (the game's Fish component - Fish1..Fish12 and anything a content mod builds on it) and
    /// birds (RandomFlyingBird - Seagal, Crow, AshCrow) are synced by an ordinary ZSyncTransform.
    /// Its OwnerSync runs every rendered frame and writes position, rotation and velocity whenever
    /// they differ at all, so a fish that is always swimming changes on every frame and goes out on
    /// every send tick - 15-18 times a second from a client - at about 138 bytes a time, almost all
    /// of it fields that never change. The 2026-09-30 recordings had them at 20-31% of everything
    /// players uploaded and 16-26% of everything the server relayed, up to 280 fish updates a
    /// second from one player on a lake. Neither M27 nor M28 can see them: both act on creatures,
    /// and these are not Characters.
    ///
    /// Two halves, one setting:
    ///
    ///   * Owner side (QuietWildlifePatches). On the machine that simulates one, OwnerSync is
    ///     skipped outright on every frame that is not due: a fish is written 5 times a second, a
    ///     bird 10. Skipping the whole method, rather than single writes, leaves its "last written"
    ///     caches and transform.hasChanged as they were, so the next due frame writes everything
    ///     that changed in between - including where a fish or bird came to rest. Birds get the
    ///     higher rate because the game syncs no velocity for anything moved by its transform, so
    ///     other players chase each new position instead of extrapolating towards it. The frame
    ///     an object becomes ours always runs (the game snaps it to the ZDO then), and a fish on a
    ///     fishing line always runs: its fight is somebody's screen.
    ///   * Host side (CreaturePacing.Filter). The server relays one to each player at most about
    ///     6 (fish) / 12 (birds) times a second within 32 m and 2 / 5 beyond. Never held: a first
    ///     copy, an owner change, a force-send, the player's own objects, a hooked fish. This is
    ///     what covers owners without this mod; for owners with it, the near intervals sit just
    ///     under the owner's so a capped update is not kept waiting for a second host tick.
    ///
    /// Deliberately not a new OwnershipPolicy.PrefabClass: a bird that carries a Destructible
    /// already classifies as Interactive there, and moving it would change who owns it. The
    /// classification below is its own cache and nothing else reads it.
    ///
    /// The schedule is stateless - a phase picked from the ZDO id, so no per-object table to
    /// prune - and runs on a double clock, because the server can own fish too and a float
    /// Time.time only counts in 62 ms steps after a week of uptime. Everything that decides is
    /// pure and names no game type, so the offline harness can table it; the classification and
    /// the hook cannot run offline (Fish, RandomFlyingBird and ZSyncTransform do not load there).
    /// </summary>
    internal static class QuietWildlife {

        internal enum Kind : byte { None, Fish, Bird }

        internal const float FishOwnerInterval = 0.2f;
        internal const float BirdOwnerInterval = 0.1f;

        internal const float RelayNearMetres = 32f;
        internal const float FishRelayNearInterval = 0.17f;
        internal const float FishRelayFarInterval = 0.5f;
        internal const float BirdRelayNearInterval = 0.085f;
        internal const float BirdRelayFarInterval = 0.2f;
        internal const float RelayNearSq = RelayNearMetres * RelayNearMetres;

        private static readonly Dictionary<int, Kind> KindCache = new Dictionary<int, Kind>();

        /// <summary>The same direct-mapped front cache OwnershipPolicy.Classify keeps: the hook
        /// asks this of every owned ZSyncTransform every frame, and almost all of them are
        /// None. A prefab hash of 0 is never stored, so 0 marks an empty slot.</summary>
        private const int FastSlots = 256;
        private static readonly int[] FastPrefab = new int[FastSlots];
        private static readonly Kind[] FastKind = new Kind[FastSlots];

        // Since start. Main thread only, like the game code that calls in.
        internal static long FramesHeld;
        internal static long FramesPassed;
        internal static long RelayListed;
        internal static long RelayHeldNear;
        internal static long RelayHeldFar;

        internal static long RelayHeld => RelayHeldNear + RelayHeldFar;

        /// <summary>Read on every call, so the setting can be switched mid-session.</summary>
        internal static bool OwnerActive =>
            PatchGuard.IsActive(Mechanism.QuietWildlife)
            && ValConfig.QuietWildlifeUpdates != null
            && ValConfig.QuietWildlifeUpdates.Value;

        /// <summary>The host half rides on M28's CreateSyncList postfix, so it stands down with
        /// that hook as well as with its own.</summary>
        internal static bool RelayActive => OwnerActive && PatchGuard.IsActive(Mechanism.CreaturePacing);

        /// <summary>Fish, bird, or neither, from the prefab. Cached; see LookupKind.</summary>
        internal static Kind KindOf(ZDO zdo) {
            int prefab = zdo.GetPrefab();
            int slot = (prefab ^ (prefab >> 16)) & (FastSlots - 1);
            if (prefab != 0 && FastPrefab[slot] == prefab) { return FastKind[slot]; }
            if (KindCache.TryGetValue(prefab, out Kind cached)) { return Remember(slot, prefab, cached); }

            // Same rule as Classify: no verdict is cached that could not be justified - an id not
            // deserialized yet, or a prefab ZNetScene does not know (yet).
            if (prefab == 0 || ZNetScene.instance == null) { return Kind.None; }
            GameObject go = ZNetScene.instance.GetPrefab(prefab);
            if (go == null) { return Kind.None; }

            Kind kind = LookupKind(go);
            KindCache[prefab] = kind;
            return Remember(slot, prefab, kind);
        }

        /// <summary>From the component, not the name, so a content mod's fish or birds built on
        /// the game's own classes are covered.</summary>
        private static Kind LookupKind(GameObject go) {
            if (go.GetComponent<Fish>() != null) { return Kind.Fish; }
            if (go.GetComponent<RandomFlyingBird>() != null) { return Kind.Bird; }
            return Kind.None;
        }

        private static Kind Remember(int slot, int prefab, Kind kind) {
            FastPrefab[slot] = prefab;
            FastKind[slot] = kind;
            return kind;
        }

        /// <summary>
        /// The owner-side decision for one frame of one object this machine owns: true skips
        /// OwnerSync. A frame with the clock stopped (deltaTime 0, a paused single-player game)
        /// is never due and so is held - nothing moves then anyway.
        /// </summary>
        internal static bool HoldThisFrame(ZDO zdo, Kind kind, double now, float deltaTime) {
            if (IsDue(now, deltaTime, PhaseFraction(zdo.m_uid.ID), OwnerIntervalSeconds(kind))
                || (kind == Kind.Fish && IsHooked(zdo))) {
                FramesPassed++;
                return false;
            }
            FramesHeld++;
            return true;
        }

        /// <summary>A fish on a fishing line. Fish.OnHooked writes it on the fisher's machine, which
        /// claims the fish. If the fisher drops out mid-catch it can stay set; that fish is then
        /// simply sent at the game's rate.</summary>
        internal static bool IsHooked(ZDO zdo) => zdo.GetInt(ZDOVars.s_hooked) == 1;

        // -- pure ------------------------------------------------------------------------------

        internal static float OwnerIntervalSeconds(Kind kind) {
            switch (kind) {
                case Kind.Fish: return FishOwnerInterval;
                case Kind.Bird: return BirdOwnerInterval;
                default: return 0f;
            }
        }

        internal static float RelayIntervalSeconds(Kind kind, float distanceSq) {
            bool near = distanceSq < RelayNearSq;
            switch (kind) {
                case Kind.Fish: return near ? FishRelayNearInterval : FishRelayFarInterval;
                case Kind.Bird: return near ? BirdRelayNearInterval : BirdRelayFarInterval;
                default: return 0f;
            }
        }

        /// <summary>Where in its interval an object's writes fall, in [0, 1). Ids are handed out in
        /// sequence, so they are spread by a multiplicative hash rather than used as they are -
        /// and the hash's high bits, so the choice is unrelated to HandoffWatch's low-bit
        /// sampling.</summary>
        internal static double PhaseFraction(uint id) {
            return ((id * 2654435761u) >> 16) / 65536d;
        }

        /// <summary>
        /// Whether this frame crosses one of the object's due points: the instants
        /// (k - phase) x interval. Exactly one frame per interval is due, whatever the frame rate,
        /// and a frame longer than the interval always is.
        /// </summary>
        internal static bool IsDue(double now, float deltaTime, double phase, float interval) {
            if (interval <= 0f || deltaTime >= interval) { return true; }
            double after = now / interval + phase;
            double before = (now - deltaTime) / interval + phase;
            return Math.Floor(after) != Math.Floor(before);
        }

        internal static void Reset() {
            KindCache.Clear();
            Array.Clear(FastPrefab, 0, FastSlots);
            FramesHeld = 0;
            FramesPassed = 0;
            RelayListed = 0;
            RelayHeldNear = 0;
            RelayHeldFar = 0;
        }
    }
}
