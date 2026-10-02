using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M33 - the host holds back an update to a building, tree or rock a player already has, when
    /// nothing about it that player could see has changed.
    ///
    /// The game sends a ZDO to a peer whenever its owner or data revision is newer than the copy
    /// the peer was sent (ZDOPeer.ShouldSend), always as the whole object, and the host sorts each
    /// peer's list by object type before distance: Terrain, then Solid, then Prioritized, then
    /// Default (ZDOMan.ServerSendCompare). Building pieces are Solid and creatures are Default, so
    /// every pending piece update goes out ahead of every creature update. That order is right for
    /// a FIRST copy - a floor must reach a player before the animals standing on it (issue #5) -
    /// and buys nothing for an update to a piece the player already has.
    ///
    /// Most such updates carry nothing a player can see. The 2026-10-01 Ashlands recording had
    /// building pieces at 51% of everything the server sent while a support loop ran on one
    /// player's game (vanilla's out-of-area support stamp undone by another mod every second), and
    /// at 8% with no loop: almost all of that was objects whose only change was a new owner, 2,000
    /// to 10,000 a minute while three players walked through a fortress, each one re-sent to every
    /// player who had it.
    ///
    /// So, per player, per object:
    ///
    ///   * Sent at once, as the game would: a first copy; a force-send; anything this player owns;
    ///     anything within 16 m of them; a change of health, support or any other stored value
    ///     except snow; a change the host made itself; and an owner change to a player who owned
    ///     it before (they must stop simulating it) or while owner changes may not wait (below).
    ///   * Otherwise PARKED: the game is told the player already has it (the peer's send record is
    ///     rewritten with the object's current revisions, its real one kept here), so it costs
    ///     nothing on any tick until it changes again. A later change is judged against the real
    ///     record; a parked object goes out when its hold expires, only into room the player's
    ///     connection has spare, and never later than twice the hold.
    ///
    /// What counts as a change is decided where the host applies a player's update (the postfix
    /// on ZDO.Deserialize, which runs whether or not M15 replaced the body): a fingerprint over the
    /// object's position, rotation, type, flags, prefab, connection and every stored value except
    /// snow buildup. Health and support are in it, as are MineRock's per-section health keys and
    /// MineRock5's health string, and any key a mod adds. A value rewritten to what it was - the
    /// support loop's max-then-real inside one visit - fingerprints the same. An owner change alone
    /// never reaches Deserialize.
    ///
    /// Owner changes may only wait while the two things that make a stale owner id harmless are
    /// live: M12's structure family, which re-addresses a stale copy's hits to the real owner, and
    /// M25, which refuses a stale owner's writes. Without them an owner change is always sent.
    ///
    /// Which objects: a prefab whose root carries WearNTear, TreeBase, MineRock, MineRock5 or
    /// Destructible, whose ZNetView is not Prioritized, and every one of whose MonoBehaviours is on
    /// a list of components known to keep no networked state of their own. Anything else - a door,
    /// a chest, a station, a fire, a portal, a ship, a cart, placed food, and any component this
    /// mod does not know, a content mod's included - is never held. Its own cache, deliberately not
    /// an OwnershipPolicy class, for the reason QuietWildlife gives: reclassifying would change who
    /// owns things.
    ///
    /// Host only, main thread only.
    /// </summary>
    internal static class StructureUpdates {

        internal const float NearMetres = 16f;
        internal const float NearSq = NearMetres * NearMetres;

        /// <summary>Expired holds released into one send to one player, at most.</summary>
        internal const int ReleasePerTick = 64;

        internal const float MinHoldSeconds = 1f;
        internal const float MaxHoldSecondsLimit = 120f;

        private const float RecordTtlSeconds = 600f;
        private const float PruneIntervalSeconds = 30f;

        // -- activation ------------------------------------------------------------------------

        /// <summary>Read on every call, so the setting can be switched mid-session. The filter
        /// rides on M28's CreateSyncList postfix, so it stands down with that hook too.</summary>
        internal static bool Active =>
            PatchGuard.IsActive(Mechanism.StructureUpdates)
            && PatchGuard.IsActive(Mechanism.CreaturePacing)
            && ValConfig.HoldUnchangedStructures != null
            && ValConfig.HoldUnchangedStructures.Value;

        internal static float MaxHoldSeconds {
            get {
                float value = ValConfig.StructureMaxHoldSeconds != null ? ValConfig.StructureMaxHoldSeconds.Value : 30f;
                return Mathf.Clamp(value, MinHoldSeconds, MaxHoldSecondsLimit);
            }
        }

        /// <summary>
        /// Whether an owner change may wait. A player whose copy names an old owner sends its hits
        /// there, where they are dropped, unless M12's structure family re-addresses them; and an
        /// old owner that has not heard keeps writing, which only M25 refuses.
        /// </summary>
        internal static bool OwnerChangesMayWait =>
            PatchGuard.IsActive(Mechanism.RpcOwnerRouting)
            && ValConfig.EnableStructureHitRouting != null && ValConfig.EnableStructureHitRouting.Value
            && PatchGuard.IsActive(Mechanism.OwnerRevisionGuard)
            && ValConfig.RejectStaleOwnerUpdates != null && ValConfig.RejectStaleOwnerUpdates.Value;

        // -- counters, since start -------------------------------------------------------------

        internal static long Listed;          // held-kind objects the decision was asked about
        internal static long Parked;          // newly parked
        internal static long Reparked;        // changed again while parked, still nothing to see
        internal static long Released;        // hold expired, sent into spare room
        internal static long ReleasedAtCeiling; // hold at twice its length, sent regardless
        internal static long ForceRestored;   // a force-send found it parked
        internal static long Dropped;         // something else rewrote or removed the peer's record
        internal static long Stamped;         // applied updates whose fingerprint changed
        internal static readonly long[] SentBy = new long[(int)Reason.Count];

        internal enum Reason : byte {
            Park,
            ForceSend,
            Near,
            NoRecord,
            HostWrote,
            Changed,
            OwnerChange,
            FormerOwner,
            OwnsIt,
            Count,
        }

        internal static int ParkedNow {
            get {
                int total = 0;
                foreach (PeerState state in Peers.Values) { total += state.Parked.Count - state.ReleasedIds.Count; }
                return total;
            }
        }

        internal static int RecordCount => Records.Count;

        // -- which prefabs ---------------------------------------------------------------------

        private static readonly Dictionary<int, bool> HeldCache = new Dictionary<int, bool>();

        /// <summary>The direct-mapped front cache QuietWildlife and OwnershipPolicy keep: asked of
        /// every listed object and every owner change. Prefab 0 is never stored.</summary>
        private const int FastSlots = 256;
        private static readonly int[] FastPrefab = new int[FastSlots];
        private static readonly bool[] FastHeld = new bool[FastSlots];

        internal static int PrefabsHeld;
        internal static int PrefabsRefused;

        /// <summary>Components that kept a candidate prefab from being held, by type name, with how
        /// many prefabs each one kept out. For nps_stats.</summary>
        internal static readonly Dictionary<string, int> RefusedBy = new Dictionary<string, int>();

        /// <summary>
        /// MonoBehaviours known to keep no networked state of their own, resolved by name so a type
        /// missing from one game build cannot stop this class loading. Taken from the 2026-09-09
        /// prefab data: the held kinds and their bookkeeping; things that settle or turn once, whose
        /// move shows in the fingerprint; static behaviours; and visual or audio components. The
        /// two that are not the game's are mesh combining and cloth.
        /// </summary>
        private static readonly string[] PassiveTypeNames = {
            "ZNetView", "Piece", "WearNTear", "TreeBase", "MineRock", "MineRock5", "Destructible",
            "DropOnDestroyed", "SpawnOnDamaged", "TriggerPersistentEventOnDestroy",
            "StaticPhysics", "RandomPieceRotation", "SnapToGround", "RandomSpawn",
            "SpawnArea", "StaticTarget", "TerrainModifier", "EffectArea", "Aoe", "Chair", "Ladder", "Ledge",
            "AutoJumpLedge", "StationExtension", "GuidePoint", "Beacon", "DisableInPlacementGhost",
            "FootStepCollider", "ConditionalObject", "ProximityState", "WaterTrigger", "Radiator",
            "HoverText", "LightLod", "LightFlicker", "LodFadeInOut", "ZSFX", "RandomMaterialValues",
            "MaterialVariationWorld", "GlobalWind", "SmokeSpawner", "CinderSpawner", "CircleProjector",
            "ImpactEffect", "LineAttach", "ObjectFlicker", "EffectFade", "AnimationEffect", "VortexParticles",
            "Billboard", "RandomSpeak",
            "SimpleMeshCombine",
            "MagicaCloth2.MagicaCloth", "MagicaCloth2.MagicaCapsuleCollider", "MagicaCloth2.MagicaSphereCollider",
            "MagicaCloth2.MagicaWindZone",
        };

        private static HashSet<Type> _passive;

        internal static bool IsHeldStructure(ZDO zdo) => IsHeldPrefab(zdo.GetPrefab());

        internal static bool IsHeldPrefab(int prefab) {
            int slot = (prefab ^ (prefab >> 16)) & (FastSlots - 1);
            if (prefab != 0 && FastPrefab[slot] == prefab) { return FastHeld[slot]; }
            if (HeldCache.TryGetValue(prefab, out bool cached)) { return Remember(slot, prefab, cached); }

            // No verdict cached that could not be justified: an id not deserialized yet, or a
            // prefab ZNetScene does not know (yet).
            if (prefab == 0 || ZNetScene.instance == null) { return false; }
            GameObject go = ZNetScene.instance.GetPrefab(prefab);
            if (go == null) { return false; }

            bool held = Lookup(go);
            HeldCache[prefab] = held;
            return Remember(slot, prefab, held);
        }

        private static bool Remember(int slot, int prefab, bool held) {
            FastPrefab[slot] = prefab;
            FastHeld[slot] = held;
            return held;
        }

        private static bool Lookup(GameObject go) {
            ZNetView view = go.GetComponent<ZNetView>();
            if (view == null || view.m_type == ZDO.ObjectType.Prioritized) { return false; }
            if (go.GetComponent<WearNTear>() == null && go.GetComponent<TreeBase>() == null
                && go.GetComponent<MineRock>() == null && go.GetComponent<MineRock5>() == null
                && go.GetComponent<Destructible>() == null) {
                return false;
            }

            HashSet<Type> passive = Passive();
            MonoBehaviour[] behaviours = go.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++) {
                if (behaviours[i] == null) { continue; }               // a missing script does nothing
                Type type = behaviours[i].GetType();
                if (passive.Contains(type)) { continue; }

                RefusedBy.TryGetValue(type.Name, out int count);
                RefusedBy[type.Name] = count + 1;
                PrefabsRefused++;
                return false;
            }
            PrefabsHeld++;
            return true;
        }

        private static HashSet<Type> Passive() {
            if (_passive != null) { return _passive; }
            HashSet<Type> types = new HashSet<Type>();
            System.Reflection.Assembly game = typeof(ZNetView).Assembly;
            System.Reflection.Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < PassiveTypeNames.Length; i++) {
                Type type = game.GetType(PassiveTypeNames[i], false);
                for (int j = 0; type == null && j < loaded.Length; j++) {
                    try { type = loaded[j].GetType(PassiveTypeNames[i], false); } catch (Exception) { type = null; }
                }
                if (type != null) { types.Add(type); }
            }
            _passive = types;
            return types;
        }

        // -- what changed: one record per held object the host has applied or re-owned ---------

        private sealed class Record {
            internal uint AppliedRevision;    // data revision as the last applied update left it
            internal uint ChangedRevision;    // data revision of the last update that changed the fingerprint
            internal ulong Fingerprint;
            internal bool HasFingerprint;
            internal long Former0, Former1, Former2;   // the last three owners it was taken from
            internal float Touched;

            internal void AddFormer(long owner) {
                if (owner == 0L || owner == Former0) { return; }
                if (owner == Former1) { Former1 = Former0; Former0 = owner; return; }
                Former2 = Former1;
                Former1 = Former0;
                Former0 = owner;
            }

            internal bool IsFormer(long uid) => uid != 0L && (uid == Former0 || uid == Former1 || uid == Former2);
        }

        private static readonly Dictionary<ZDOID, Record> Records = new Dictionary<ZDOID, Record>();
        private static readonly List<ZDOID> Stale = new List<ZDOID>();
        private static float _lastPrune;

        private static Record RecordFor(ZDOID id, float now) {
            if (!Records.TryGetValue(id, out Record record)) {
                record = new Record();
                Records[id] = record;
            }
            record.Touched = now;
            return record;
        }

        /// <summary>The host has just applied a player's update to this object (Deserialize
        /// postfix). A first sighting counts as a change: there is nothing to compare it with.</summary>
        internal static void OnApplied(ZDO zdo, float now) {
            if (!IsHeldStructure(zdo)) { return; }
            ulong fingerprint = Fingerprint(zdo);
            Record record = RecordFor(zdo.m_uid, now);
            if (!record.HasFingerprint || record.Fingerprint != fingerprint) {
                record.ChangedRevision = zdo.DataRevision;
                Stamped++;
            }
            record.Fingerprint = fingerprint;
            record.HasFingerprint = true;
            record.AppliedRevision = zdo.DataRevision;
        }

        /// <summary>The object's owner is about to be set (SetOwnerInternal prefix): remember who
        /// it is being taken from, who must hear at once.</summary>
        internal static void OnOwnerChanging(ZDO zdo, long newOwner, float now) {
            if (!IsHeldStructure(zdo)) { return; }
            long owner = zdo.GetOwner();
            if (owner == newOwner || owner == 0L) { return; }
            RecordFor(zdo.m_uid, now).AddFormer(owner);
        }

        // -- the fingerprint -------------------------------------------------------------------

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits {
            [FieldOffset(0)] internal float Float;
            [FieldOffset(0)] internal int Int;
        }

        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        private static ulong Mix(ulong hash, int value) {
            hash = (hash ^ (uint)value) * FnvPrime;
            return hash;
        }

        private static ulong Mix(ulong hash, long value) {
            hash = Mix(hash, (int)value);
            return Mix(hash, (int)(value >> 32));
        }

        private static ulong Mix(ulong hash, float value) {
            FloatBits bits = default;
            bits.Float = value;
            return Mix(hash, bits.Int);
        }

        private static ulong Mix(ulong hash, Vector3 value) {
            return Mix(Mix(Mix(hash, value.x), value.y), value.z);
        }

        /// <summary>
        /// Everything about the object as the host now holds it that another player could see:
        /// placement, type, flags, prefab, connection and every stored value, read straight out of
        /// ZDOExtraData's tables with no allocation. Snow buildup (s_snow, s_preSnow) is left out;
        /// it changes every visit while it snows, and is cosmetic. Each table mixes its own marker
        /// and the count of what it contributed, after its entries - so a value moving from one
        /// type to another changes it, and a table holding nothing but snow is as if absent.
        /// </summary>
        internal static ulong Fingerprint(ZDO zdo) {
            ZDOID id = zdo.m_uid;
            ulong hash = FnvOffset;
            hash = Mix(hash, zdo.GetPosition());
            hash = Mix(hash, zdo.m_rotation);
            hash = Mix(hash, (int)zdo.Type);
            hash = Mix(hash, (zdo.Persistent ? 1 : 0) | (zdo.Distant ? 2 : 0));
            hash = Mix(hash, zdo.m_prefab);

            ZDOConnection connection = ZDOExtraData.GetConnection(id);
            if (connection != null) {
                hash = Mix(hash, (int)connection.m_type);
                hash = Mix(hash, connection.m_target.UserID);
                hash = Mix(hash, (int)connection.m_target.ID);
            }

            int n = 0;
            if (ZDOExtraData.s_floats.TryGetValue(id, out BinarySearchDictionary<int, float> floats)) {
                for (int i = 0; i < floats.m_length; i++) {
                    if (floats.m_keys[i] == ZDOVars.s_snow) { continue; }
                    hash = Mix(Mix(hash, floats.m_keys[i]), floats.m_values[i]);
                    n++;
                }
            }
            hash = EndTable(hash, 1, ref n);
            if (ZDOExtraData.s_vec3.TryGetValue(id, out BinarySearchDictionary<int, Vector3> vectors)) {
                for (int i = 0; i < vectors.m_length; i++) {
                    hash = Mix(Mix(hash, vectors.m_keys[i]), vectors.m_values[i]);
                    n++;
                }
            }
            hash = EndTable(hash, 2, ref n);
            if (ZDOExtraData.s_quats.TryGetValue(id, out BinarySearchDictionary<int, Quaternion> quats)) {
                for (int i = 0; i < quats.m_length; i++) {
                    Quaternion q = quats.m_values[i];
                    hash = Mix(Mix(Mix(Mix(Mix(hash, quats.m_keys[i]), q.x), q.y), q.z), q.w);
                    n++;
                }
            }
            hash = EndTable(hash, 3, ref n);
            if (ZDOExtraData.s_ints.TryGetValue(id, out BinarySearchDictionary<int, int> ints)) {
                for (int i = 0; i < ints.m_length; i++) {
                    if (ints.m_keys[i] == ZDOVars.s_preSnow) { continue; }
                    hash = Mix(Mix(hash, ints.m_keys[i]), ints.m_values[i]);
                    n++;
                }
            }
            hash = EndTable(hash, 4, ref n);
            if (ZDOExtraData.s_longs.TryGetValue(id, out BinarySearchDictionary<int, long> longs)) {
                for (int i = 0; i < longs.m_length; i++) {
                    hash = Mix(Mix(hash, longs.m_keys[i]), longs.m_values[i]);
                    n++;
                }
            }
            hash = EndTable(hash, 5, ref n);
            if (ZDOExtraData.s_strings.TryGetValue(id, out BinarySearchDictionary<int, string> strings)) {
                for (int i = 0; i < strings.m_length; i++) {
                    hash = Mix(hash, strings.m_keys[i]);
                    string value = strings.m_values[i];
                    n++;
                    if (value == null) { hash = Mix(hash, -1); continue; }
                    hash = Mix(hash, value.Length);
                    for (int c = 0; c < value.Length; c++) { hash = Mix(hash, (int)value[c]); }
                }
            }
            hash = EndTable(hash, 6, ref n);
            if (ZDOExtraData.s_byteArrays.TryGetValue(id, out BinarySearchDictionary<int, byte[]> arrays)) {
                for (int i = 0; i < arrays.m_length; i++) {
                    hash = Mix(hash, arrays.m_keys[i]);
                    byte[] value = arrays.m_values[i];
                    n++;
                    if (value == null) { hash = Mix(hash, -1); continue; }
                    hash = Mix(hash, value.Length);
                    for (int b = 0; b < value.Length; b++) { hash = Mix(hash, (int)value[b]); }
                }
            }
            return EndTable(hash, 7, ref n);
        }

        /// <summary>Close one table: its marker and how many entries it gave, nothing if none.</summary>
        private static ulong EndTable(ulong hash, int table, ref int count) {
            if (count > 0) { hash = Mix(hash, (table << 24) | count); }
            count = 0;
            return hash;
        }

        // -- the decision ----------------------------------------------------------------------

        /// <summary>
        /// Send now, or park. Pure, so the offline harness can table it. Cheapest rules first; the
        /// first that matches is the reason given.
        /// </summary>
        internal static Reason Decide(bool forceSend, float distanceSq, bool dataNewer, bool ownerNewer,
                                      bool hasRecord, bool hostWrote, bool changedSinceCopy,
                                      bool ownerMayWait, bool formerOwner, bool ownsIt) {
            if (forceSend) { return Reason.ForceSend; }
            if (distanceSq <= NearSq) { return Reason.Near; }
            if (dataNewer) {
                if (!hasRecord) { return Reason.NoRecord; }
                if (hostWrote) { return Reason.HostWrote; }
                if (changedSinceCopy) { return Reason.Changed; }
            }
            if (ownerNewer) {
                if (!ownerMayWait) { return Reason.OwnerChange; }
                if (formerOwner) { return Reason.FormerOwner; }
            }
            if (ownsIt) { return Reason.OwnsIt; }
            return Reason.Park;
        }

        // -- parking ---------------------------------------------------------------------------

        private struct ParkedZdo {
            internal ZDOMan.ZDOPeer.PeerZDOInfo Real;   // what the peer was really sent
            internal ZDOMan.ZDOPeer.PeerZDOInfo Fake;   // what the peer's record says instead
            internal float ParkedAt;
            internal bool Released;                     // real record put back; send it, then forget it
        }

        private struct Due {
            internal ZDOID Id;
            internal float ParkedAt;
        }

        private sealed class PeerState {
            internal ZDOMan.ZDOPeer Peer;
            internal readonly Dictionary<ZDOID, ParkedZdo> Parked = new Dictionary<ZDOID, ParkedZdo>();
            internal readonly Queue<Due> Queue = new Queue<Due>();   // park order, so due order
            internal readonly List<ZDOID> ReleasedIds = new List<ZDOID>();
            internal bool LastSendComplete;
            internal long Listed;
            internal long ParkedTotal;
        }

        private static readonly Dictionary<long, PeerState> Peers = new Dictionary<long, PeerState>();

        /// <summary>The state for this peer, or a fresh one if it is new or reconnected (a new
        /// ZDOPeer has a new send record, so nothing parked in the old one means anything).</summary>
        private static PeerState StateFor(ZDOMan.ZDOPeer peer) {
            long uid = peer.m_peer.m_uid;
            if (Peers.TryGetValue(uid, out PeerState state) && ReferenceEquals(state.Peer, peer)) { return state; }
            state = new PeerState { Peer = peer };
            Peers[uid] = state;
            return state;
        }

        private static bool Same(ZDOMan.ZDOPeer.PeerZDOInfo a, ZDOMan.ZDOPeer.PeerZDOInfo b) {
            return a.m_dataRevision == b.m_dataRevision
                && a.m_ownerRevision == b.m_ownerRevision
                && a.m_syncTime.Equals(b.m_syncTime);
        }

        /// <summary>
        /// The filter's question for one listed held-kind object (CreaturePacing.Filter): true sends
        /// it now, false has parked it and the caller drops it from the list.
        /// </summary>
        internal static bool ShouldSendNow(ZDOMan.ZDOPeer peer, ZDO zdo, Vector3 refPos, float now, bool ownerMayWait) {
            PeerState state = StateFor(peer);
            state.Listed++;
            Listed++;

            ZDOID id = zdo.m_uid;
            ZDOMan.ZDOPeer.PeerZDOInfo real;
            bool parked = state.Parked.TryGetValue(id, out ParkedZdo entry);
            if (parked) {
                if (entry.Released) { return true; }
                if (!peer.m_zdos.TryGetValue(id, out ZDOMan.ZDOPeer.PeerZDOInfo current) || !Same(current, entry.Fake)) {
                    // Something else wrote or removed the record - a send, the peer's own upload, a
                    // sector change. Its value is the truth now, and nothing parked here may come back.
                    state.Parked.Remove(id);
                    Dropped++;
                    parked = false;
                    if (!peer.m_zdos.TryGetValue(id, out real)) { return true; }
                } else {
                    real = entry.Real;
                }
            } else if (!peer.m_zdos.TryGetValue(id, out real)) {
                return true;                                                   // first copy: the game's order
            }

            Vector3 position = zdo.GetPosition();
            float dx = position.x - refPos.x;
            float dy = position.y - refPos.y;
            float dz = position.z - refPos.z;
            bool dataNewer = zdo.DataRevision > real.m_dataRevision;
            bool ownerNewer = zdo.OwnerRevision > real.m_ownerRevision;
            bool hasRecord = Records.TryGetValue(id, out Record record);
            // A record an owner change made has no fingerprint yet, and says nothing about data.
            bool hasPrint = hasRecord && record.HasFingerprint;
            long self = peer.m_peer.m_uid;

            Reason reason = Decide(
                forceSend: peer.m_forceSend.Contains(id),
                distanceSq: dx * dx + dy * dy + dz * dz,
                dataNewer: dataNewer,
                ownerNewer: ownerNewer,
                hasRecord: hasPrint,
                hostWrote: hasPrint && zdo.DataRevision != record.AppliedRevision,
                changedSinceCopy: hasPrint && record.ChangedRevision > real.m_dataRevision,
                ownerMayWait: ownerMayWait,
                formerOwner: hasRecord && record.IsFormer(self),
                ownsIt: zdo.GetOwner() == self);

            if (reason != Reason.Park) {
                SentBy[(int)reason]++;
                if (parked) {
                    peer.m_zdos[id] = entry.Real;                              // so a cut-off send leaves the game's own record
                    state.Parked.Remove(id);
                }
                return true;
            }

            ZDOMan.ZDOPeer.PeerZDOInfo fake = new ZDOMan.ZDOPeer.PeerZDOInfo(zdo.DataRevision, zdo.OwnerRevision, real.m_syncTime);
            peer.m_zdos[id] = fake;
            if (parked) {
                entry.Fake = fake;
                state.Parked[id] = entry;
                Reparked++;
            } else {
                state.Parked[id] = new ParkedZdo { Real = real, Fake = fake, ParkedAt = now };
                state.Queue.Enqueue(new Due { Id = id, ParkedAt = now });
                state.ParkedTotal++;
                Parked++;
            }
            return false;
        }

        /// <summary>
        /// Before this peer's list is built (CreateSyncList prefix, ahead of M9's): put back what a
        /// force-send now wants, and release expired holds into the room the last send left. A
        /// released object has its real record back, so the game lists it again on this very tick.
        /// </summary>
        internal static void BeforeSyncList(ZDOMan.ZDOPeer peer, float now) {
            _listBuiltFor = peer;
            if (!Peers.TryGetValue(peer.m_peer.m_uid, out PeerState state)) { return; }
            if (!ReferenceEquals(state.Peer, peer)) {
                Peers.Remove(peer.m_peer.m_uid);
                return;
            }
            if (state.Parked.Count == 0) {
                state.Queue.Clear();
                state.ReleasedIds.Clear();
                return;
            }

            // A released object is forgotten once the send that carried it has rewritten its record.
            for (int i = state.ReleasedIds.Count - 1; i >= 0; i--) {
                ZDOID id = state.ReleasedIds[i];
                if (state.Parked.TryGetValue(id, out ParkedZdo entry) && entry.Released
                    && peer.m_zdos.TryGetValue(id, out ZDOMan.ZDOPeer.PeerZDOInfo current) && Same(current, entry.Real)) {
                    continue;
                }
                if (state.Parked.TryGetValue(id, out entry) && entry.Released) { state.Parked.Remove(id); }
                state.ReleasedIds.RemoveAt(i);
            }

            // AddForceSendZdos drops anything ShouldSend refuses, which a parked record would.
            if (peer.m_forceSend.Count > 0) {
                foreach (ZDOID id in peer.m_forceSend) {
                    if (!state.Parked.TryGetValue(id, out ParkedZdo entry) || entry.Released) { continue; }
                    Restore(peer, id, entry);
                    state.Parked.Remove(id);
                    ForceRestored++;
                }
            }

            float hold = MaxHoldSeconds;
            bool room = state.LastSendComplete;
            int released = 0;
            while (state.Queue.Count > 0) {
                Due due = state.Queue.Peek();
                if (!state.Parked.TryGetValue(due.Id, out ParkedZdo entry) || entry.Released || !entry.ParkedAt.Equals(due.ParkedAt)) {
                    state.Queue.Dequeue();                                     // sent, dropped or parked again since
                    continue;
                }
                float waited = now - entry.ParkedAt;
                if (waited < hold) { break; }                                  // the queue is in due order
                bool ceiling = waited >= 2f * hold;
                if (!ceiling && (!room || released >= ReleasePerTick)) { break; }

                state.Queue.Dequeue();
                if (!Restore(peer, due.Id, entry)) {
                    state.Parked.Remove(due.Id);
                    Dropped++;
                    continue;
                }
                entry.Released = true;
                state.Parked[due.Id] = entry;
                state.ReleasedIds.Add(due.Id);
                released++;
                if (ceiling) { ReleasedAtCeiling++; } else { Released++; }
            }
            // The room one send left is spent on one batch; the next send says whether there is more.
            if (released > 0) { state.LastSendComplete = false; }
        }

        /// <summary>Put the real record back, only if the fake is still what is there.</summary>
        private static bool Restore(ZDOMan.ZDOPeer peer, ZDOID id, ParkedZdo entry) {
            if (!peer.m_zdos.TryGetValue(id, out ZDOMan.ZDOPeer.PeerZDOInfo current) || !Same(current, entry.Fake)) { return false; }
            peer.m_zdos[id] = entry.Real;
            return true;
        }

        // -- room on the connection: did the last send get through its whole list? -------------

        private static ZDOMan.ZDOPeer _listBuiltFor;
        private static int _sentBefore;

        internal static void OnSendStart(ZDOMan man) {
            _listBuiltFor = null;
            _sentBefore = man.m_zdosSent;
        }

        /// <summary>A send whose backpressure check returned before building a list left no room;
        /// one that built a list had room if it wrote all of it.</summary>
        internal static void OnSendEnd(ZDOMan man, ZDOMan.ZDOPeer peer) {
            if (peer?.m_peer == null || !Peers.TryGetValue(peer.m_peer.m_uid, out PeerState state) || !ReferenceEquals(state.Peer, peer)) { return; }
            state.LastSendComplete = ReferenceEquals(_listBuiltFor, peer) && man.m_zdosSent - _sentBefore >= man.m_tempToSync.Count;
            _listBuiltFor = null;
        }

        // -- lifecycle -------------------------------------------------------------------------

        private static bool _lastOwnerMayWait;

        /// <summary>
        /// Before every list the host builds, from the always-installed prefix, so it runs with the
        /// setting off too. Turning the setting off, or losing what lets owner changes wait, puts
        /// every parked record back, so nothing waits on a rule that no longer holds.
        /// </summary>
        internal static void Tick(float now, bool active, bool ownerMayWait) {
            if ((!active || (_lastOwnerMayWait && !ownerMayWait)) && Peers.Count > 0) { RestoreAll(); }
            _lastOwnerMayWait = ownerMayWait;
            if (!active) { return; }
            PruneIfDue(now);
        }

        internal static void RestoreAll() {
            foreach (PeerState state in Peers.Values) {
                foreach (KeyValuePair<ZDOID, ParkedZdo> entry in state.Parked) {
                    if (!entry.Value.Released) { Restore(state.Peer, entry.Key, entry.Value); }
                }
            }
            Peers.Clear();
        }

        private static void PruneIfDue(float now) {
            if (now - _lastPrune < PruneIntervalSeconds) { return; }
            _lastPrune = now;
            if (Records.Count == 0) { return; }

            ZDOMan man = ZDOMan.instance;
            Stale.Clear();
            foreach (KeyValuePair<ZDOID, Record> entry in Records) {
                if (now - entry.Value.Touched > RecordTtlSeconds || (man != null && man.GetZDO(entry.Key) == null)) {
                    Stale.Add(entry.Key);
                }
            }
            for (int i = 0; i < Stale.Count; i++) { Records.Remove(Stale[i]); }
            Stale.Clear();
        }

        /// <summary>A peer's running totals, for the monitoring peer record.</summary>
        internal static bool TryGetPeerCounts(long uid, out long parked, out long listed, out int parkedNow) {
            if (Peers.TryGetValue(uid, out PeerState state)) {
                parked = state.ParkedTotal;
                listed = state.Listed;
                parkedNow = state.Parked.Count - state.ReleasedIds.Count;
                return true;
            }
            parked = 0;
            listed = 0;
            parkedNow = 0;
            return false;
        }

        /// <summary>The peer has gone, and its send record with it.</summary>
        internal static void ForgetPeer(long uid) {
            Peers.Remove(uid);
        }

        internal static void Reset() {
            Peers.Clear();
            Records.Clear();
            Stale.Clear();
            HeldCache.Clear();
            RefusedBy.Clear();
            Array.Clear(FastPrefab, 0, FastSlots);
            Array.Clear(FastHeld, 0, FastSlots);
            Array.Clear(SentBy, 0, SentBy.Length);
            _passive = null;
            _listBuiltFor = null;
            _lastPrune = 0f;
            _lastOwnerMayWait = false;
            PrefabsHeld = 0;
            PrefabsRefused = 0;
            Listed = 0;
            Parked = 0;
            Reparked = 0;
            Released = 0;
            ReleasedAtCeiling = 0;
            ForceRestored = 0;
            Dropped = 0;
            Stamped = 0;
        }
    }
}
