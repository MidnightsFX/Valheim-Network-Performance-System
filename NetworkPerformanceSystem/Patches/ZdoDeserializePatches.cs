using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Reflection;
using UnityEngine;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M15 - reads a received ZDO's fields without the fourteen delegates vanilla allocates for
    /// every single one.
    ///
    /// ZDO.Deserialize ends with seven calls to a generic ZDODataHelper.ReadData&lt;T&gt;, each
    /// taking a Func&lt;T&gt; reader and an Action&lt;ZDOID,int,T&gt; writer. It reads like
    /// method-group conversion that a compiler might cache; it is not cached, and the IL confirms
    /// fourteen newobj per call with no caching pattern anywhere in the body. Worse, all fourteen
    /// are constructed BEFORE ReadData looks at its `read` flag, so a ZDO carrying a single float
    /// pays for all of them.
    ///
    /// Rate: once per ZDO accepted in ZDOMan.RPC_ZDOData, which is ZDOMan.GetRecvZDOs() per
    /// second. Note that a client feels this more than a server does - a client receives the
    /// server's full stream, a server only each peer's owned-object delta.
    ///
    /// The replacement reproduces vanilla's body exactly, including two things that look like
    /// oversights and are not:
    ///
    ///   * Reserve() is called even for a zero-length read, which creates the field table from the
    ///     pool. Skipping it would leave a different field-table population than vanilla.
    ///   * ReadData deliberately does NOT call RemoveIfEmpty afterwards - only the disk-load path
    ///     does. Adding it here would drop tables vanilla keeps.
    ///
    /// Reproducing both is what keeps the resulting ZDO state, and the pool accounting behind it,
    /// identical. Nothing is written to the wire or to disk from this method.
    /// </summary>
    [HarmonyPatch]
    internal static class ZdoDeserializePatches {

        /// <summary>
        /// Two jobs. First, refuse to install at all unless someone asked for this: ZDO.Deserialize
        /// is the hottest method a Valheim server runs - called for every replicated object in
        /// every packet - and a Harmony wrapper on it costs the same whether the body does the
        /// work or returns on its first line. A server that never enables this should not pay a
        /// wrapper per received ZDO forever. The price of that is that turning the setting on
        /// needs a restart, which the report says out loud rather than leaving an admin to wonder.
        ///
        /// Second, verify every method the replacement calls into. There is no IL shape to assert
        /// here, but the prefix reimplements vanilla's body, so it must not run at all if a piece
        /// of that body has moved - the failure mode would be silently mis-parsed ZDO payloads,
        /// which is unrecoverable corruption rather than a visible fault.
        /// </summary>
        [HarmonyPrepare]
        private static bool Prepare() {
            if (!AllocationRelief.DeserializeWanted) {
                Logger.LogInfo("ZDO deserialize fast path is off, so its hook was not installed. " +
                               "Turning it on takes effect after a restart.");
                return false;
            }

            MethodInfo readNumItems = AccessTools.Method(typeof(ZDO), nameof(ZDO.ReadNumItems), new[] { typeof(ZPackage) });
            MethodInfo reserve = AccessTools.Method(typeof(ZDOExtraData), nameof(ZDOExtraData.Reserve),
                new[] { typeof(ZDOID), typeof(ZDOExtraData.Type), typeof(int) });
            MethodInfo setConnection = AccessTools.Method(typeof(ZDOExtraData), nameof(ZDOExtraData.SetConnection),
                new[] { typeof(ZDOID), typeof(ZDOExtraData.ConnectionType), typeof(ZDOID) });

            if (readNumItems == null || reserve == null || setConnection == null) {
                PatchGuard.Disable(Mechanism.DeserializeAlloc,
                    "ZDO.ReadNumItems, ZDOExtraData.Reserve or ZDOExtraData.SetConnection is not the expected shape. " +
                    "Either the game updated or another mod rewrote them first. Received ZDOs are read exactly as vanilla.");
                return false;
            }

            // The seven typed writers, checked by value type rather than by name: they are
            // overloads, so a missing one resolves to null while the others still answer.
            System.Type[] valueTypes = {
                typeof(float), typeof(Vector3), typeof(Quaternion),
                typeof(int), typeof(long), typeof(string), typeof(byte[]),
            };
            foreach (System.Type valueType in valueTypes) {
                if (AccessTools.Method(typeof(ZDOExtraData), nameof(ZDOExtraData.Add),
                        new[] { typeof(ZDOID), typeof(int), valueType }) != null) {
                    continue;
                }
                PatchGuard.Disable(Mechanism.DeserializeAlloc,
                    $"ZDOExtraData.Add has no overload taking {valueType.Name}, so a received ZDO's fields cannot be " +
                    "written the way vanilla writes them. Either the game updated or another mod rewrote it first. " +
                    "Received ZDOs are read exactly as vanilla.");
                return false;
            }

            AllocationRelief.DeserializeHookInstalled = true;
            return true;
        }

        /// <summary>
        /// true  -> vanilla ZDO.Deserialize runs (stood down, switched off, or already cancelled)
        /// false -> the fields have been read, with the same result and no delegates
        ///
        /// HarmonyPriority(Priority.Last) is a requirement here, not a style choice. This is the
        /// most crowded method on a modded server, and ValheimEnforcer's structure validator puts
        /// a prefix/postfix pair sharing __state on it: its prefix captures a ZDO's health BEFORE
        /// the client's write, so an over-limit value that was already there is not blamed on
        /// whoever owns it now. Run ahead of that capture and Enforcer records the post-write
        /// value, its comparison then always finds no change, and its excessive-health check stops
        /// detecting anything - a security check failing silently. Priority.Last puts us after it.
        ///
        /// Taking __runOriginal and returning true on false is the same courtesy this mod's routed
        /// RPC prefixes extend: if something ahead of us has already cancelled, we neither redo the
        /// work nor override the decision. Postfixes still run either way - Harmony guarantees that
        /// even when a prefix cancels - which three other mods depend on here: VCP dirty-marks
        /// received ZDOs so they get saved, AsyncSave marks the uid dirty mid-snapshot, and
        /// Enforcer audits containers. None of them reads __runOriginal, so all three still fire.
        /// </summary>
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool ReadFieldsWithoutDelegates(ZDO __instance, ZPackage pkg, bool __runOriginal) {
            if (!__runOriginal) { return true; }
            if (!AllocationRelief.DeserializeActive) { return true; }

            ZDO.ExtraDataFlags flags = (ZDO.ExtraDataFlags)pkg.ReadUShort();
            __instance.Persistent = (flags & ZDO.ExtraDataFlags.Persistent) != 0;
            __instance.Distant = (flags & ZDO.ExtraDataFlags.Distant) != 0;
            __instance.Type = (ZDO.ObjectType)(((int)flags >> 10) & 3);
            __instance.m_prefab = pkg.ReadInt();
            if ((flags & ZDO.ExtraDataFlags.Rotation) != 0) {
                __instance.m_rotation = pkg.ReadVector3();
            }

            AllocationRelief.DeserializeFast++;

            // Nothing in the low byte means no field data follows at all. Vanilla returns here,
            // before reserving anything.
            if ((flags & ZDO.ExtraDataFlags.AnyLow8Bits) == 0) { return false; }

            ZDOID uid = __instance.m_uid;

            // Connection data comes first and is a byte then a ZDOID, in that order. Getting the
            // two the wrong way round would desynchronise the read for everything after it.
            if ((flags & ZDO.ExtraDataFlags.Connections) != 0) {
                ZDOExtraData.ConnectionType connectionType = (ZDOExtraData.ConnectionType)pkg.ReadByte();
                ZDOID target = pkg.ReadZDOID();
                ZDOExtraData.SetConnection(uid, connectionType, target);
            }

            // Then the seven types in their fixed order. Each block is vanilla's ReadData<T> with
            // the two delegates inlined: read the count, reserve, then count x (hash, value, add).
            // The hash is read before the value in every one of them, as vanilla does.
            if ((flags & ZDO.ExtraDataFlags.Floats) != 0) {
                int size = ZDO.ReadNumItems(pkg);
                ZDOExtraData.Reserve(uid, ZDOExtraData.Type.Float, size);
                for (int i = 0; i < size; i++) {
                    int hash = pkg.ReadInt();
                    float value = pkg.ReadSingle();
                    ZDOExtraData.Add(uid, hash, value);
                }
            }

            if ((flags & ZDO.ExtraDataFlags.Vec3) != 0) {
                int size = ZDO.ReadNumItems(pkg);
                ZDOExtraData.Reserve(uid, ZDOExtraData.Type.Vec3, size);
                for (int i = 0; i < size; i++) {
                    int hash = pkg.ReadInt();
                    Vector3 value = pkg.ReadVector3();
                    ZDOExtraData.Add(uid, hash, value);
                }
            }

            if ((flags & ZDO.ExtraDataFlags.Quaternions) != 0) {
                int size = ZDO.ReadNumItems(pkg);
                ZDOExtraData.Reserve(uid, ZDOExtraData.Type.Quat, size);
                for (int i = 0; i < size; i++) {
                    int hash = pkg.ReadInt();
                    Quaternion value = pkg.ReadQuaternion();
                    ZDOExtraData.Add(uid, hash, value);
                }
            }

            if ((flags & ZDO.ExtraDataFlags.Ints) != 0) {
                int size = ZDO.ReadNumItems(pkg);
                ZDOExtraData.Reserve(uid, ZDOExtraData.Type.Int, size);
                for (int i = 0; i < size; i++) {
                    int hash = pkg.ReadInt();
                    int value = pkg.ReadInt();
                    ZDOExtraData.Add(uid, hash, value);
                }
            }

            if ((flags & ZDO.ExtraDataFlags.Longs) != 0) {
                int size = ZDO.ReadNumItems(pkg);
                ZDOExtraData.Reserve(uid, ZDOExtraData.Type.Long, size);
                for (int i = 0; i < size; i++) {
                    int hash = pkg.ReadInt();
                    long value = pkg.ReadLong();
                    ZDOExtraData.Add(uid, hash, value);
                }
            }

            if ((flags & ZDO.ExtraDataFlags.Strings) != 0) {
                int size = ZDO.ReadNumItems(pkg);
                ZDOExtraData.Reserve(uid, ZDOExtraData.Type.String, size);
                for (int i = 0; i < size; i++) {
                    int hash = pkg.ReadInt();
                    string value = pkg.ReadString();
                    ZDOExtraData.Add(uid, hash, value);
                }
            }

            if ((flags & ZDO.ExtraDataFlags.ByteArrays) != 0) {
                int size = ZDO.ReadNumItems(pkg);
                ZDOExtraData.Reserve(uid, ZDOExtraData.Type.ByteArray, size);
                for (int i = 0; i < size; i++) {
                    int hash = pkg.ReadInt();
                    byte[] value = pkg.ReadByteArray();
                    ZDOExtraData.Add(uid, hash, value);
                }
            }

            return false;
        }
    }
}
