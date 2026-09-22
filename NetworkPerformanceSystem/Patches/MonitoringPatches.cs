using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System;
using System.Reflection;
using UnityEngine;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// The hooks network monitoring needs, applied only while it is running.
    ///
    /// Deliberately carries no [HarmonyPatch] attribute, on the class or on any method: the
    /// plugin's PatchAll must not see it. Several of these sit on paths that run per received ZDO
    /// or per synced object per physics tick, and a prefix that returns on its first line still
    /// costs a call. So they are applied by hand, through a Harmony instance of their own, when
    /// monitoring starts and taken off again when it stops. A server that never enables
    /// monitoring never has them.
    ///
    /// Every hook observes and none alters: all are void, none touches an argument or a result,
    /// and Harmony runs a void prefix whether or not another mod's prefix has skipped the
    /// original. A target that cannot be found is reported and skipped, and the rest still apply -
    /// a partial recording is more use than none.
    /// </summary>
    internal static class MonitoringPatches {

        private static Harmony _harmony;

        /// <summary>Skip marker for the SetOwner pair. Not a session id anyone can have: those
        /// come from Utils.GenerateUID, which never produces long.MinValue.</summary>
        private const long NotCaptured = long.MinValue;

        internal static void Apply(bool hostHooks, bool viewerHooks) {
            if (_harmony != null) { return; }
            _harmony = new Harmony(NetworkPerformanceSystem.PluginGUID + ".monitoring");

            // Ownership, from both directions: a change made on this machine, and one that
            // arrived in a packet. Every role wants these.
            Patch(typeof(ZDO), nameof(ZDO.SetOwner), nameof(SetOwnerPrefix), nameof(SetOwnerPostfix));
            Patch(typeof(ZDO), nameof(ZDO.SetOwnerInternal), nameof(SetOwnerInternalPrefix), null);
            Patch(typeof(ZDOMan), "RPC_ZDOData", nameof(ZdoDataPrefix), nameof(ZdoDataPostfix));

            // Hits, as the machine that sent one and as the machine one was delivered to. Every
            // role again, a dedicated server included: it simulates whatever it owns around the
            // world origin, and a hit on one of those is handled on the spot rather than relayed,
            // so the relay hook below never sees it.
            Patch(typeof(Character), "RPC_Damage", nameof(RpcDamagePrefix), null);
            Patch(typeof(Character), nameof(Character.Damage), nameof(DamagePrefix), null);

            if (hostHooks) {
                // First, so it sees where the sender addressed the message before the station
                // router or the relay filter has had a say.
                Patch(typeof(ZRoutedRpc), "RouteRPC", nameof(RouteRpcPrefix), null, Priority.First);
            }

            if (viewerHooks) {
                // How far a creature jumps on screen. Nobody is watching a dedicated server's.
                Patch(typeof(ZSyncTransform), "SyncPosition", nameof(SyncPositionPrefix), nameof(SyncPositionPostfix));
            }
        }

        internal static void Remove() {
            if (_harmony == null) { return; }
            try {
                _harmony.UnpatchSelf();
            } catch (Exception e) {
                Logger.LogWarning($"Network monitoring could not remove its hooks cleanly: {e.GetType().Name}: {e.Message}");
            }
            _harmony = null;
        }

        private static void Patch(Type type, string method, string prefix, string postfix, int priority = Priority.Normal) {
            try {
                MethodInfo original = AccessTools.Method(type, method);
                if (original == null) {
                    Logger.LogWarning($"Network monitoring: {type.Name}.{method} was not found, so what it would have recorded is missing from this session.");
                    return;
                }

                _harmony.Patch(original,
                    prefix: prefix != null ? Hook(prefix, priority) : null,
                    postfix: postfix != null ? Hook(postfix, priority) : null);
            } catch (Exception e) {
                Logger.LogWarning($"Network monitoring: could not hook {type.Name}.{method} ({e.GetType().Name}: {e.Message}). What it would have recorded is missing from this session.");
            }
        }

        private static HarmonyMethod Hook(string name, int priority) {
            return new HarmonyMethod(AccessTools.Method(typeof(MonitoringPatches), name)) { priority = priority };
        }

        // -- ZDO.SetOwner ------------------------------------------------------------------

        private static void SetOwnerPrefix(ZDO __instance, out long __state) {
            // A flag read, and it retires nearly every call: walls, trees and bushes change hands
            // far more often than creatures do.
            if (__instance.Type != ZDO.ObjectType.Prioritized) {
                __state = NotCaptured;
                return;
            }
            __state = __instance.GetOwner();
            Monitoring.InSetOwner = true;
        }

        private static void SetOwnerPostfix(ZDO __instance, long uid, long __state) {
            if (__state == NotCaptured) { return; }

            Monitoring.InSetOwner = false;
            if (__state == uid) {
                Monitoring.ClearPendingCause();                               // no change; nothing to explain
                return;
            }
            Monitoring.OnLocalSetOwner(__instance, __state, uid);
        }

        // -- ZDOMan.RPC_ZDOData + ZDO.SetOwnerInternal -------------------------------------
        // RPC_ZDOData calls SetOwnerInternal exactly once for each ZDO it applies, which makes
        // that call the per-ZDO hook a packet otherwise does not have. Bracketing the packet is
        // what tells such a call apart from the one SetOwner makes.

        private static void ZdoDataPrefix(ZRpc rpc) {
            Monitoring.InZdoData = true;

            ZNetPeer peer = NetworkChannelPatches.FindPeerByRpc(rpc);
            Monitoring.PacketPeerUid = peer != null ? peer.m_uid : 0L;
        }

        private static void ZdoDataPostfix() {
            Monitoring.InZdoData = false;
        }

        private static void SetOwnerInternalPrefix(ZDO __instance, long uid) {
            if (!Monitoring.InZdoData || Monitoring.InSetOwner) { return; }
            // A ZDO being created from this packet still has its default type here - its flags
            // arrive with Deserialize, which comes after - so new objects fall out on this line
            // too, and only an existing creature's update goes further.
            if (__instance.Type != ZDO.ObjectType.Prioritized) { return; }

            Monitoring.OnPacketZdo(__instance, uid);
        }

        // -- ZRoutedRpc.RouteRPC -----------------------------------------------------------

        private static void RouteRpcPrefix(ZRoutedRpc.RoutedRPCData rpcData) {
            if (rpcData == null || rpcData.m_targetZDO.IsNone()) { return; }
            if (!Monitoring.IsWatchedRpc(rpcData.m_methodHash)) { return; }

            Monitoring.OnRoutedRpc(rpcData);
        }

        // -- Character ---------------------------------------------------------------------

        private static void RpcDamagePrefix(Character __instance, long sender) {
            MonitoringClient.OnDamageReceived(__instance, sender);
        }

        private static void DamagePrefix(Character __instance) {
            MonitoringClient.OnDamageSent(__instance);
        }

        // -- ZSyncTransform.SyncPosition ---------------------------------------------------
        // Only ever runs for objects this machine does not own. The prefix remembers where the
        // object was, the postfix sees how far vanilla (and M4, whose transpiler lives in the
        // same method and is untouched by this) moved it.

        private static void SyncPositionPrefix(ZSyncTransform __instance, out Vector3 __state) {
            // Reference comparison on purpose: Unity's == is a native call, and this runs for
            // every synced object every physics tick.
            Character character = __instance.m_character;
            if ((object)character == null || character.IsPlayer()) {
                __state = new Vector3(float.NaN, 0f, 0f);
                return;
            }
            __state = __instance.transform.position;
        }

        private static void SyncPositionPostfix(ZSyncTransform __instance, ZDO zdo, Vector3 __state) {
            if (float.IsNaN(__state.x)) { return; }

            float stepSq = (__instance.transform.position - __state).sqrMagnitude;
            if (stepSq < MonitoringClient.SnapStepMetresSq) { return; }

            MonitoringClient.OnSnap(__instance, zdo, Mathf.Sqrt(stepSq));
        }
    }
}
