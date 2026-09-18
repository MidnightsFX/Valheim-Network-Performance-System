using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System;
using System.Reflection;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M20 - wiring for ShipHelmOwnership, which hands a ship to whoever is steering it. See that
    /// class for the rules; this file is only the hooks.
    ///
    /// Two hooks, both acting only on the machine that owns the ship:
    ///   * ShipControlls.RPC_RequestControl (postfix) - runs on the owner when someone asks for the
    ///     helm. Vanilla only writes s_user on a grant, so a valid user after it returns is the
    ///     grant, and the ship is handed over at once - after the response, the same order
    ///     Sadle.RPC_RequestControl uses.
    ///   * Ship.UpdateOwner (prefix) - vanilla's own two-second owner check. Re-checking here
    ///     catches a ship that reached a passenger after the helm was taken (a rescue, or
    ///     vanilla's first-to-board pick), a ship owned by a dedicated server (where vanilla
    ///     returns before doing anything), and a grant made before this machine owned the ship.
    ///     When no handoff is due, vanilla runs unchanged.
    /// </summary>
    [HarmonyPatch]
    internal static class ShipHelmPatches {

        /// <summary>
        /// Verify the anchors before Harmony touches anything. A game update that renames or
        /// reshapes any of them disables this mechanism loudly and leaves ships as vanilla.
        /// </summary>
        [HarmonyPrepare]
        private static bool Prepare() {
            MethodInfo requestControl = AccessTools.Method(typeof(ShipControlls), "RPC_RequestControl", new[] { typeof(long), typeof(long) });
            MethodInfo updateOwner = AccessTools.Method(typeof(Ship), "UpdateOwner", Type.EmptyTypes);
            FieldInfo players = AccessTools.Field(typeof(Ship), "m_players");
            FieldInfo controls = AccessTools.Field(typeof(Ship), "m_shipControlls");
            FieldInfo nview = AccessTools.Field(typeof(Ship), "m_nview");
            FieldInfo ship = AccessTools.Field(typeof(ShipControlls), "m_ship");

            if (requestControl == null || updateOwner == null || players == null || controls == null || nview == null || ship == null) {
                PatchGuard.Disable(Mechanism.ShipHelmOwnership,
                    "ShipControlls.RPC_RequestControl / Ship.UpdateOwner or the fields linking a ship to its helm do not have the expected shape. " +
                    "Either the game updated or another mod rewrote them first. Ships keep their owner as in vanilla.");
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(ShipControlls), "RPC_RequestControl")]
        [HarmonyPostfix]
        private static void HandOverOnGrant(ShipControlls __instance) {
            if (!ShipHelmOwnership.Enabled) { return; }
            ShipHelmOwnership.TryHandToHelmsman(__instance.m_ship);
        }

        [HarmonyPatch(typeof(Ship), "UpdateOwner")]
        [HarmonyPrefix]
        private static bool HandOverToHelmsman(Ship __instance) {
            if (!ShipHelmOwnership.Enabled) { return true; }
            // true  -> no handoff was due; vanilla's own check runs as normal
            // false -> the ship went to its helmsman, which is the only owner that should win
            return !ShipHelmOwnership.TryHandToHelmsman(__instance);
        }
    }
}
