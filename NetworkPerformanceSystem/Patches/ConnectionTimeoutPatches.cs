using System.Collections.Generic;
using HarmonyLib;
using NetworkPerformanceSystem.Runtime;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M11 - writes the connection timeouts after vanilla has written its own, at both layers.
    ///
    /// ZRpc.SetLongTimeout is the only place vanilla assigns ZRpc's static timeout, and ZNet.Start
    /// calls it unconditionally before either a client connects or a server loads, so a postfix
    /// there lands ahead of every peer on both builds. ZSteamSocket.RegisterGlobalCallbacks is the
    /// same seam M8 uses, and vanilla pins TimeoutConnected inside it. ZRpc.Update is where the
    /// static is read, once per peer per frame, and the two character-id seams are where a
    /// connection stops joining.
    ///
    /// No transpiler and no IL assertions on either: one is a whole method whose absence Harmony
    /// reports at patch time, the other is a set of Steamworks calls at Global scope with no game
    /// constants in it. What replaces that check is the readback in ConnectionTimeout - both
    /// layers are read back after the write and logged, so a refused write is visible rather than
    /// assumed.
    /// </summary>
    [HarmonyPatch]
    internal static class ConnectionTimeoutPatches {

        [HarmonyPatch(typeof(ZRpc), nameof(ZRpc.SetLongTimeout))]
        [HarmonyPostfix]
        private static void ApplyRpcTimeout(bool enable) {
            ConnectionTimeout.OnVanillaRpcTimeoutSet(enable);
        }

        /// <summary>Separate from M8's postfix on the same method on purpose: the two mechanisms
        /// have their own config toggles and either can be off while the other applies.</summary>
        [HarmonyPatch(typeof(ZSteamSocket), "RegisterGlobalCallbacks")]
        [HarmonyPostfix]
        private static void ApplySteamTimeouts() {
            ConnectionTimeout.OnGlobalCallbacksRegistered();
        }

        /// <summary>Points ZRpc's static deadline at this connection's own - joining or in the
        /// world - before vanilla's UpdatePing compares the silence against it.</summary>
        [HarmonyPatch(typeof(ZRpc), nameof(ZRpc.Update))]
        [HarmonyPrefix]
        private static void ApplyPeerDeadline(ZRpc __instance) {
            ConnectionTimeout.BeforeRpcUpdate(__instance);
        }

        /// <summary>Host: a client sends its character id once its player has spawned, which is
        /// the end of its join. A respawn sends ZDOID.None first; that is not a new join.</summary>
        [HarmonyPatch(typeof(ZNet), "RPC_CharacterID")]
        [HarmonyPostfix]
        private static void NotePeerInWorld(ZRpc rpc, ZDOID characterID) {
            if (characterID.IsNone()) { return; }
            ConnectionTimeout.NoteInWorld(rpc);
        }

        /// <summary>Client: our own player has spawned, so the server connection is past its
        /// join. The first peer is the one vanilla has just sent the id to. A host calls this for
        /// its own player too, and has no server connection.</summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.SetCharacterID))]
        [HarmonyPostfix]
        private static void NoteSelfInWorld(ZNet __instance, ZDOID id) {
            if (id.IsNone() || __instance.IsServer()) { return; }
            List<ZNetPeer> peers = __instance.GetPeers();
            if (peers.Count > 0) { ConnectionTimeout.NoteInWorld(peers[0].m_rpc); }
        }
    }
}
