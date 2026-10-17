using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M42 / M43 - wiring for CrossplayDisconnects. See it for why; this file is only the hooks.
    ///
    ///   * ZNet.Disconnect, prefix (M42, host): runs before vanilla clears the peer and disposes
    ///     its socket.
    ///   * ZNet.RPC_Disconnect, prefix + finalizer (M42): marks the connection whose own goodbye is
    ///     being handled, so the host does not answer it.
    ///   * ZPlayFabSocket.Connect, prefix + postfix (M43, a player's game): a reconnect of a socket
    ///     the game already had refreshes the time it last heard from the host.
    ///
    /// Applied by hand from Awake, after PatchAll, like M10's crossplay half: binding
    /// ZPlayFabSocket.Connect(PlayFabPlayer) and checking ZPlayFabSocket's members pulls in the
    /// PlayFab assemblies, and an exception inside PatchAll would take every remaining mechanism with
    /// it. Each mechanism is caught on its own.
    /// </summary>
    internal static class CrossplayDisconnectsPatches {

        internal static void Apply(Harmony harmony) {
            try {
                ApplyDropNotice(harmony);
            } catch (Exception ex) {
                PatchGuard.Disable(Mechanism.CrossplayDropNotice,
                    $"could not hook ZNet.Disconnect ({ex.GetType().Name}: {ex.Message}). A crossplay player the server drops is not told, as vanilla.");
            }

            try {
                ApplyResume(harmony);
            } catch (Exception ex) {
                PatchGuard.Disable(Mechanism.CrossplayResume,
                    $"could not hook ZPlayFabSocket.Connect ({ex.GetType().Name}: {ex.Message}). A crossplay connection that comes back after a reset is reset again at once, as vanilla.");
            }
        }

        // --- M42 --------------------------------------------------------------------------------

        /// <summary>
        /// Every member the notice uses, checked before anything is patched: the publicized reads in
        /// CrossplayDisconnects would otherwise fail only when the first crossplay player is dropped.
        /// InternalSend must still queue for compression and send through InternalSendCont, since
        /// that queue is what has to be emptied.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyDropNotice(Harmony harmony) {
            MethodInfo disconnect = AccessTools.Method(typeof(ZNet), nameof(ZNet.Disconnect), new[] { typeof(ZNetPeer) });
            MethodInfo goodbye = AccessTools.Method(typeof(ZNet), "RPC_Disconnect", new[] { typeof(ZRpc) });
            FieldInfo kicks = AccessTools.Field(typeof(ZNet), "PeersToDisconnectAfterKick");
            MethodInfo internalSend = AccessTools.Method(typeof(ZPlayFabSocket), "InternalSend", new[] { typeof(byte[]) });
            MethodInfo internalSendCont = AccessTools.Method(typeof(ZPlayFabSocket), "InternalSendCont", new[] { typeof(byte[]) });
            MethodInfo resetInProgress = AccessTools.Method(typeof(ZPlayFabSocket), "PartyResetInProgress", Type.EmptyTypes);
            FieldInfo inFlight = AccessTools.Field(typeof(ZPlayFabSocket), "m_inFlightQueue");
            FieldInfo zlib = AccessTools.Field(typeof(ZPlayFabSocket), "m_zlibWorkQueue");
            FieldInfo gate = AccessTools.Field(typeof(PlayFabZLibWorkQueue), "m_lock");
            FieldInfo inCompress = AccessTools.Field(typeof(PlayFabZLibWorkQueue), "m_inCompress");
            FieldInfo outCompress = AccessTools.Field(typeof(PlayFabZLibWorkQueue), "m_outCompress");
            MethodInfo compress = AccessTools.Method(typeof(PlayFabZLibWorkQueue), nameof(PlayFabZLibWorkQueue.Compress), new[] { typeof(byte[]) });
            MethodInfo compressHere = AccessTools.Method(typeof(PlayFabZLibWorkQueue), "CompressOnThisThread", new[] { typeof(byte[]) });

            if (disconnect == null || goodbye == null || kicks == null || !kicks.IsStatic || kicks.FieldType != typeof(Dictionary<ZNetPeer, float>)
                || internalSend == null || internalSendCont == null || resetInProgress == null || resetInProgress.ReturnType != typeof(bool)
                || inFlight == null || inFlight.FieldType != typeof(ZPlayFabSocket.InFlightQueue)
                || zlib == null || zlib.FieldType != typeof(PlayFabZLibWorkQueue)
                || gate == null || !gate.IsStatic || inCompress == null || inCompress.FieldType != typeof(Queue<byte[]>)
                || outCompress == null || outCompress.FieldType != typeof(Queue<byte[]>)
                || compress == null || compressHere == null || compressHere.ReturnType != typeof(byte[])
                || !Calls(internalSend, compress) || !Calls(internalSend, internalSendCont)) {
                PatchGuard.Disable(Mechanism.CrossplayDropNotice,
                    "ZNet.Disconnect / RPC_Disconnect, ZPlayFabSocket's send path or PlayFabZLibWorkQueue do not have the expected shape. " +
                    "Either the game updated or another mod rewrote them. A crossplay player the server drops is not told, as vanilla.");
                return;
            }

            harmony.Patch(disconnect, prefix: new HarmonyMethod(AccessTools.Method(typeof(CrossplayDisconnectsPatches), nameof(TellDroppedPlayer))));
            harmony.Patch(goodbye,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(CrossplayDisconnectsPatches), nameof(EnterGoodbye))),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(CrossplayDisconnectsPatches), nameof(ExitGoodbye))));
            IlMatch.LogOnce("Dropped crossplay players are told (ZNet.Disconnect).");
        }

        private static bool Calls(MethodInfo method, MethodInfo callee) {
            foreach (CodeInstruction code in PatchProcessor.GetOriginalInstructions(method)) {
                if ((code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt) && code.operand is MethodInfo called && called == callee) {
                    return true;
                }
            }
            return false;
        }

        private static void TellDroppedPlayer(ZNet __instance, ZNetPeer peer) {
            try {
                Notify(__instance, peer);
            } catch (Exception e) {
                PatchGuard.Disable(Mechanism.CrossplayDropNotice,
                    $"telling a dropped crossplay player failed ({e.GetType().Name}: {e.Message}). Crossplay players the server drops are not told, as vanilla.");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Notify(ZNet net, ZNetPeer peer) {
            if (!CrossplayDisconnects.NoticeHooked) { return; }
            bool kickPending = peer != null && ZNet.PeersToDisconnectAfterKick.ContainsKey(peer);
            CrossplayDisconnects.BeforeDisconnect(net.IsServer(), net.HaveStopped, peer, kickPending);
        }

        private static void EnterGoodbye(ZRpc rpc) {
            CrossplayDisconnects.EnterGoodbye(rpc);
        }

        private static Exception ExitGoodbye(Exception __exception) {
            CrossplayDisconnects.ExitGoodbye();
            return __exception;
        }

        // --- M43 --------------------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyResume(Harmony harmony) {
            MethodInfo connect = AccessTools.Method(typeof(ZPlayFabSocket), "Connect");
            FieldInfo lastReception = AccessTools.Field(typeof(ZPlayFabSocket), "s_lastReception");
            FieldInfo sockets = AccessTools.Field(typeof(ZPlayFabSocket), "s_connectSockets");
            FieldInfo isClient = AccessTools.Field(typeof(ZPlayFabSocket), "m_isClient");

            if (connect == null || connect.GetParameters().Length != 1
                || lastReception == null || !lastReception.IsStatic || lastReception.FieldType != typeof(DateTime)
                || sockets == null || !sockets.IsStatic || sockets.FieldType != typeof(Dictionary<string, ZPlayFabSocket>)
                || isClient == null || isClient.FieldType != typeof(bool)) {
                PatchGuard.Disable(Mechanism.CrossplayResume,
                    "ZPlayFabSocket.Connect / s_lastReception / s_connectSockets do not have the expected shape. Either the game updated or " +
                    "another mod rewrote them. A crossplay connection that comes back after a reset is reset again at once, as vanilla.");
                return;
            }

            harmony.Patch(connect,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(CrossplayDisconnectsPatches), nameof(BeforeConnect))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(CrossplayDisconnectsPatches), nameof(AfterConnect))));
            IlMatch.LogOnce("Crossplay reconnects get the full time to resume (ZPlayFabSocket.Connect).");
        }

        private static void BeforeConnect(ZPlayFabSocket __instance, out bool __state) {
            __state = false;
            try {
                __state = CrossplayDisconnects.ResumeHooked && CrossplayDisconnects.IsReconnect(__instance);
            } catch (Exception e) {
                PatchGuard.Disable(Mechanism.CrossplayResume,
                    $"checking a crossplay reconnect failed ({e.GetType().Name}: {e.Message}). A crossplay connection that comes back after a reset is reset again at once, as vanilla.");
            }
        }

        private static void AfterConnect(bool __state) {
            try {
                CrossplayDisconnects.AfterConnect(__state);
            } catch (Exception e) {
                PatchGuard.Disable(Mechanism.CrossplayResume,
                    $"refreshing a crossplay reconnect failed ({e.GetType().Name}: {e.Message}). A crossplay connection that comes back after a reset is reset again at once, as vanilla.");
            }
        }
    }
}
