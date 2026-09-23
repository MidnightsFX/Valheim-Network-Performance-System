using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using UnityEngine;

namespace NetworkPerformanceSystem
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    // Load order only for Valheim Plus, to prevent transpilers colliding
    [BepInDependency(PatchGuard.ValheimPlusGUID, BepInDependency.DependencyFlags.SoftDependency)]

    // Networking mods that collide head-on with our patch sites. Each of these either
    // rewrites the same ZDOMan.SendZDOs constants, replaces SendZDOToPeers2, or runs a
    // competing ownership policy. A half-patched state is worse than not loading, so we
    // let BepInEx refuse us outright rather than trying to coexist.
    [BepInIncompatibility("com.Fire.FiresGhettoNetworkMod")]  // FiresGhettoNetworking / VAGhetto
    [BepInIncompatibility("VitByr.VBNetTweaks")]
    [BepInIncompatibility("sighsorry.SkadiNet")]
    [BepInIncompatibility("CW_Jesse.BetterNetworking")]
    [BepInIncompatibility("org.bepinex.plugins.network")]     // Smoothbrain - Network
    [BepInIncompatibility("Searica.Valheim.NetworkTweaks")]
    [BepInIncompatibility("dzk.warheimnetwork")]
    [BepInIncompatibility("com.maxsch.valheim.TimeoutLimit")]
    // Deliberately NOT blocked - verified to have no patch-site overlap:
    //   CacoFFF.valheim.LeanNet            (gates ZDO.IncreaseDataRevision, one layer above us)
    //   redseiko.valheim.compress          (transpiles SendZDOs at ZRpc.Invoke, not our constants)
    //   redseiko.valheim.enroute           (ZRoutedRpc only)
    //   redseiko.valheim.betterzeerouter   (ZRoutedRpc only)
    //   redseiko.valheim.scenic            (ZNetScene.RemoveObjects only)
    //   redseiko.valheim.returntosender    (it overrides SendSchedulerPatch, allowed)
    //   org.bepinex.plugins.valheim_plus   (owns the player limit, see PlayerLimitPatches)
    internal class NetworkPerformanceSystem : BaseUnityPlugin
    {
        public const string PluginGUID = "MidnightsFX.NetworkPerformanceSystem";
        public const string PluginName = "NetworkPerformanceSystem";
        public const string PluginVersion = "1.8.0";

        internal static ManualLogSource Log;
        internal static Harmony HarmonyInstance;
        internal ValConfig cfg;

        public void Awake() {
            Log = this.Logger;
            cfg = new ValConfig(Config);

            // All startup hooks should go after the config & Logger have been wired up
            HarmonyInstance = new Harmony(PluginGUID);
            HarmonyInstance.PatchAll(typeof(NetworkPerformanceSystem).Assembly);
            // M10's crossplay half is applied by hand: binding ZPlayFabMatchmaking.CreateLobby
            // loads the PlayFab assemblies, and a failure there inside PatchAll would take the
            // rest of the assembly's patches with it. See PlayerLimitPatches.
            Patches.PlayerLimitPatches.ApplyPlayFabCapacityPatch(HarmonyInstance);
            // M13 is a reflection write into Jotunn rather than a patch, but the value it writes
            // depends on whether M2 survived PatchAll - so it goes after Harmony and before the
            // summary in Start, where a stand-down is listed with the rest.
            JotunnSendQueue.OnStartup();

            // Configs are not written until after they are all wired up, they exist in memory before this.
            // Flushing all of the configs at once is a significant speedup in mod load time
            ValConfig.SaveOnSet(true);
        }

        public void Start() {
            // Stand-downs for mods that already do a mechanism's job. BepInEx adds each plugin to
            // Chainloader.PluginInfos as it loads it, so a mod ordered after us is invisible from
            // Awake. Unity does not call Start until the chainloader has finished loading every
            // plugin, and it is still before any session exists. Checking from inside the patched
            // methods instead fails when the other mod keeps them from running at all.
            Patches.SendSchedulerPatches.CheckForReturnToSender();
            Patches.RoutedRpcPatches.CheckForOverlappingMods();
            Patches.RpcOwnerRouterPatches.CheckForOverlappingMods();
            GhostWatchdog.CheckForRival();
            // After the stand-downs, so the summary lists what is actually running.
            PatchGuard.VerifyAfterPatching();
        }

        public void OnGUI() {
            if (ValConfig.EnableDebugOverlay == null || !ValConfig.EnableDebugOverlay.Value) { return; }
            if (!NpsEnv.NetReady() || NpsEnv.IsDedicated()) { return; }

            GUI.Label(new Rect(10f, 10f, 1400f, 24f), Runtime.NetworkStats.BuildOverlay());
        }

        public void OnDestroy() {
            // Closes its files and removes its own hooks; a no-op when monitoring was never on.
            Monitoring.Shutdown();
            JotunnSendQueue.Restore();
            HarmonyInstance?.UnpatchSelf();
        }
    }
}
