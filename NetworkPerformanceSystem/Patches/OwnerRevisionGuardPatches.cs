using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M25 - wiring for OwnerRevisionGuard. See the guard for why; this file is only the hooks.
    ///
    /// ZDOMan.RPC_ZDOData applies each ZDO of a packet in one of three ways - owner only (the
    /// update has no newer data), full (newer data), or create - and the full-apply block is
    /// shared by the last two:
    ///
    ///     zdo.OwnerRevision = ownerRevision;                 -> OwnerRevisionGuard.StoreOwnerRevision
    ///     zdo.DataRevision = dataRevision;                   -> OwnerRevisionGuard.StoreDataRevision
    ///     zdo.SetOwnerInternal(owner);                       -> OwnerRevisionGuard.ApplyOwner
    ///     zdo.InternalSetPosition(position);                 -> OwnerRevisionGuard.ApplyPosition
    ///     peer.m_zdos[id] = new PeerZDOInfo(zdo.DataRevision,
    ///                                       zdo.OwnerRevision,   -> OwnerRevisionGuard.RevisionHeldBySender
    ///                                       time);
    ///     zdo.Deserialize(data);                             -> OwnerRevisionGuard.ApplyData
    ///
    /// Each replacement takes exactly what the instruction it replaces took off the stack and
    /// leaves exactly what it left, so nothing around them changes. The owner-only block writes
    /// the same members in a different order and is left alone: it is already guarded, by the
    /// game, and it is what delivers the correction to the peer whose update was refused.
    ///
    /// Anchored on shape rather than position: the one OwnerRevision store followed three
    /// instructions later by a DataRevision store, the first SetOwnerInternal after it, the first
    /// InternalSetPosition after that, the first OwnerRevision read after that which feeds a
    /// PeerZDOInfo constructor, and the first Deserialize after the read. The client and
    /// dedicated-server builds of game 1.0.14 compile this method identically. Anything else - a
    /// game update, or another mod having rewritten the block first - stands the guard down and
    /// leaves the method untouched.
    ///
    /// The prefix and postfix bracket each call, arming the guard only on the host; a client
    /// applies the host's updates in full, as it must.
    /// </summary>
    [HarmonyPatch]
    internal static class OwnerRevisionGuardPatches {

        private const string OwnerRevisionSetter = "set_OwnerRevision";
        private const string OwnerRevisionGetter = "get_OwnerRevision";
        private const string DataRevisionSetter = "set_DataRevision";
        private const string SetOwnerInternalName = "SetOwnerInternal";
        private const string InternalSetPositionName = "InternalSetPosition";
        private const string DeserializeName = "Deserialize";
        private const string PeerInfoTypeName = "PeerZDOInfo";

        /// <summary>How far apart the store and the SetOwnerInternal call may be - two stores and
        /// their loads in vanilla. Generous enough for a harmless reordering, tight enough that it
        /// cannot wander into the next block.</summary>
        private const int MaxStoreToOwnerGap = 8;

        /// <summary>How far apart consecutive calls further down the block may be. In vanilla the
        /// position store is three instructions after SetOwnerInternal and Deserialize four after
        /// the PeerZDOInfo read; the same margin as above keeps a match inside this block.</summary>
        private const int MaxNeighbourGap = 8;

        [HarmonyPrepare]
        private static bool Prepare() {
            MethodInfo zdoData = AccessTools.Method(typeof(ZDOMan), "RPC_ZDOData", new[] { typeof(ZRpc), typeof(ZPackage) });
            if (zdoData == null) {
                PatchGuard.Disable(Mechanism.OwnerRevisionGuard,
                    "ZDOMan.RPC_ZDOData(ZRpc, ZPackage) was not found. Either the game updated or another mod replaced it. " +
                    "An update written before its sender heard of an ownership change still puts the old owner back, as vanilla.");
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> GuardOwnerRevision(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);

            if (!TryFindSites(codes, out Sites sites, out string miss)) {
                PatchGuard.Disable(Mechanism.OwnerRevisionGuard,
                    $"ZDOMan.RPC_ZDOData does not have the expected shape ({miss}). Either the game updated or another mod rewrote " +
                    "this method first. An update written before its sender heard of an ownership change still puts the old owner back, as vanilla. " +
                    $"OwnerRevision sites: {IlMatch.DescribeNeighbours(codes, OwnerRevisionSetter, OwnerRevisionGetter)}");
                return codes;
            }

            Replace(codes, sites.Store, nameof(OwnerRevisionGuard.StoreOwnerRevision));
            Replace(codes, sites.DataStore, nameof(OwnerRevisionGuard.StoreDataRevision));
            Replace(codes, sites.SetOwner, nameof(OwnerRevisionGuard.ApplyOwner));
            Replace(codes, sites.Position, nameof(OwnerRevisionGuard.ApplyPosition));
            Replace(codes, sites.Read, nameof(OwnerRevisionGuard.RevisionHeldBySender));
            Replace(codes, sites.Deserialize, nameof(OwnerRevisionGuard.ApplyData));

            Logger.LogInfo("Stale owner updates guard active (6 sites rewritten in ZDOMan.RPC_ZDOData).");
            return codes;
        }

        private static void Replace(List<CodeInstruction> codes, int index, string guardMethod) {
            IlMatch.ReplaceInPlace(codes, index,
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(OwnerRevisionGuard), guardMethod)));
        }

        /// <summary>Indices of the six instructions to replace, in block order.</summary>
        internal struct Sites {
            internal int Store;
            internal int DataStore;
            internal int SetOwner;
            internal int Position;
            internal int Read;
            internal int Deserialize;
        }

        /// <summary>
        /// Finds the six instructions to replace, or says which one is missing. Internal so the
        /// offline harness can run it against the real method's instruction list from both builds.
        /// </summary>
        internal static bool TryFindSites(List<CodeInstruction> codes, out Sites sites, out string miss) {
            sites = default;
            int store = -1;
            int setOwner = -1;
            int position = -1;
            int read = -1;
            int deserialize = -1;

            // The full-apply store: an OwnerRevision store with a DataRevision store three
            // instructions later. The owner-only block stores OwnerRevision after SetOwnerInternal
            // and never touches DataRevision, so it cannot match.
            int matches = 0;
            for (int i = 0; i + 3 < codes.Count; i++) {
                if (IsCall(codes[i], OwnerRevisionSetter) && IsCall(codes[i + 3], DataRevisionSetter)) {
                    if (store < 0) { store = i; }
                    matches++;
                }
            }
            if (matches != 1) {
                miss = $"{matches} full-apply OwnerRevision stores, expected 1";
                return false;
            }

            for (int i = store + 1; i < codes.Count && i <= store + MaxStoreToOwnerGap; i++) {
                if (IsCall(codes[i], SetOwnerInternalName)) { setOwner = i; break; }
            }
            if (setOwner < 0) {
                miss = $"no SetOwnerInternal within {MaxStoreToOwnerGap} instructions of the full-apply store";
                return false;
            }

            position = NextCall(codes, setOwner, InternalSetPositionName);
            if (position < 0) {
                miss = $"no InternalSetPosition within {MaxNeighbourGap} instructions of the full-apply SetOwnerInternal";
                return false;
            }

            // The revision read that feeds the sender's PeerZDOInfo: an OwnerRevision read, then
            // the time local, then the constructor.
            for (int i = position + 1; i + 2 < codes.Count; i++) {
                if (IsCall(codes[i], OwnerRevisionGetter) && IsPeerInfoCtor(codes[i + 2])) { read = i; break; }
            }
            if (read < 0) {
                miss = "no OwnerRevision read feeding a PeerZDOInfo after the full-apply InternalSetPosition";
                return false;
            }

            deserialize = NextCall(codes, read, DeserializeName);
            if (deserialize < 0) {
                miss = $"no Deserialize within {MaxNeighbourGap} instructions of the PeerZDOInfo read";
                return false;
            }

            sites = new Sites {
                Store = store,
                DataStore = store + 3,
                SetOwner = setOwner,
                Position = position,
                Read = read,
                Deserialize = deserialize,
            };
            miss = null;
            return true;
        }

        private static int NextCall(List<CodeInstruction> codes, int after, string memberName) {
            for (int i = after + 1; i < codes.Count && i <= after + MaxNeighbourGap; i++) {
                if (IsCall(codes[i], memberName)) { return i; }
            }
            return -1;
        }

        private static bool IsCall(CodeInstruction code, string memberName) {
            return (code.opcode == OpCodes.Callvirt || code.opcode == OpCodes.Call)
                   && code.operand is MethodInfo method
                   && method.DeclaringType == typeof(ZDO)
                   && method.Name == memberName;
        }

        private static bool IsPeerInfoCtor(CodeInstruction code) {
            return code.opcode == OpCodes.Newobj
                   && code.operand is ConstructorInfo ctor
                   && ctor.DeclaringType != null
                   && ctor.DeclaringType.Name == PeerInfoTypeName
                   && ctor.GetParameters().Length == 3;
        }

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData")]
        [HarmonyPrefix]
        private static void ArmGuard(ZRpc rpc) {
            OwnerRevisionGuard.Arm(rpc);
        }

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData")]
        [HarmonyPostfix]
        private static void DisarmGuard() {
            OwnerRevisionGuard.Disarm();
        }
    }
}
