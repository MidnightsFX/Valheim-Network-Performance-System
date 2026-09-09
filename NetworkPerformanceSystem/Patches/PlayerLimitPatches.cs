using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M10 - rewrites the three hard-coded 10s that together decide how many players a server
    /// holds. See <see cref="PlayerLimit"/> for what each site actually controls; only the first
    /// enforces anything, and only the third can turn a crossplay client away.
    ///
    /// Host-side by construction: ZNet.RPC_PeerInfo runs the limit check inside its own
    /// <c>m_isServer</c> branch, and both lobbies are only ever created by the process that
    /// registers the server. Nothing here needs a side gate of its own.
    /// </summary>
    [HarmonyPatch]
    internal static class PlayerLimitPatches {

        // --- enforcement ----------------------------------------------------------------------

        /// <summary>
        /// <c>if (this.GetNrOfPlayers() &gt;= 10)</c> in ZNet.RPC_PeerInfo. Anchored on the call
        /// rather than on the constant alone, because RPC_PeerInfo contains an unrelated
        /// <c>ldc.i4.s 10</c> - the last index of the string array the version-mismatch log builds
        /// - and rewriting that one would corrupt the error path instead of the limit.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> ConfigurableLimit(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);

            MethodInfo getNrOfPlayers = AccessTools.Method(typeof(ZNet), nameof(ZNet.GetNrOfPlayers));
            MethodInfo current = AccessTools.Method(typeof(PlayerLimit), nameof(PlayerLimit.Current));

            int site = -1;
            int matches = 0;
            for (int i = 0; i + 1 < codes.Count; i++) {
                if (!codes[i].Calls(getNrOfPlayers)) { continue; }
                if (!IlMatch.IsInt32Constant(codes[i + 1], PlayerLimit.VanillaLimit)) { continue; }
                site = i + 1;
                matches++;
            }

            // Anything other than exactly one match means the method is not the shape we read.
            // Standing down leaves vanilla's 10 in place everywhere, which is a working server;
            // guessing at which constant to rewrite is not.
            if (matches != 1) {
                PatchGuard.Disable(Mechanism.PlayerLimit,
                    $"ZNet.RPC_PeerInfo does not have the expected IL shape (found {matches} GetNrOfPlayers() >= " +
                    $"{PlayerLimit.VanillaLimit} comparisons, expected 1). Either the game updated or another mod " +
                    "rewrote this method first. The player limit stays at vanilla's 10.");
                return codes;
            }

            IlMatch.ReplaceInPlace(codes, site, new CodeInstruction(OpCodes.Call, current));

            Logger.LogInfo("Player limit is configurable (ZNet.RPC_PeerInfo rewritten).");
            return codes;
        }

        // --- advertised capacity, Steam lobby ---------------------------------------------------

        /// <summary>
        /// <c>SteamMatchmaking.CreateLobby(type, 10)</c> in ZSteamMatchmaking.RegisterServer.
        /// Cosmetic: this member limit is what the server browser prints after the slash. Steam
        /// clients read the lobby's data and then connect straight to the host, so nothing is
        /// gated on it - but leaving it at 10 while the server accepts 30 tells every player
        /// looking at the list that a server with room is full.
        /// </summary>
        [HarmonyPatch(typeof(ZSteamMatchmaking), nameof(ZSteamMatchmaking.RegisterServer))]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> AdvertiseSteamCapacity(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);

            MethodInfo capacity = AccessTools.Method(typeof(PlayerLimit), nameof(PlayerLimit.SteamLobbyCapacity));

            int site = -1;
            int matches = 0;
            for (int i = 0; i + 1 < codes.Count; i++) {
                if (!IlMatch.IsInt32Constant(codes[i], PlayerLimit.VanillaLimit)) { continue; }
                if (!IlMatch.TargetsMemberNamed(codes[i + 1], "CreateLobby")) { continue; }
                site = i;
                matches++;
            }

            // A miss here costs the browser display and nothing else, so it warns rather than
            // disabling the mechanism - the limit itself is still enforced at the value set.
            if (matches != 1) {
                Logger.LogWarning($"ZSteamMatchmaking.RegisterServer does not have the expected IL shape (found {matches} " +
                    $"CreateLobby(.., {PlayerLimit.VanillaLimit}) calls, expected 1). The server browser will keep " +
                    "advertising a limit of 10. Joining is unaffected - the limit the server enforces is the configured one.");
                return codes;
            }

            IlMatch.ReplaceInPlace(codes, site, new CodeInstruction(OpCodes.Call, capacity));
            return codes;
        }

        // --- real capacity, PlayFab lobby -------------------------------------------------------

        /// <summary>
        /// Applied by hand rather than through PatchAll. Resolving ZPlayFabMatchmaking.CreateLobby
        /// pulls in the PlayFab assemblies to bind its parameter types; if that ever fails, an
        /// exception out of PatchAll would take every remaining mechanism in the assembly with it.
        /// Called from Awake after PatchAll for exactly that reason.
        /// </summary>
        internal static void ApplyPlayFabCapacityPatch(Harmony harmony) {
            try {
                PatchPlayFabLobby(harmony);
            } catch (Exception ex) {
                PlayerLimit.CrossplayCapacityPinned = true;
                Logger.LogWarning("Could not reach ZPlayFabMatchmaking.CreateLobby to raise the crossplay lobby capacity " +
                    $"({ex.GetType().Name}: {ex.Message}). Crossplay clients will still be refused past 10; " +
                    "Steam clients are unaffected.");
            }
        }

        /// <summary>
        /// Kept out of its caller so the type load happens at a call the caller can catch. A
        /// TypeLoadException is raised when the method referencing the missing type is JIT-ed,
        /// which for an inlined body would be before the try block is ever entered.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PatchPlayFabLobby(Harmony harmony) {
            MethodInfo target = AccessTools.Method(typeof(ZPlayFabMatchmaking), "CreateLobby");
            if (target == null) {
                PlayerLimit.CrossplayCapacityPinned = true;
                Logger.LogWarning("ZPlayFabMatchmaking.CreateLobby not found; the crossplay lobby stays at 10 members. " +
                    "Crossplay clients will be refused past 10 even though the server would accept them.");
                return;
            }

            harmony.Patch(target, transpiler: new HarmonyMethod(
                AccessTools.Method(typeof(PlayerLimitPatches), nameof(RaisePlayFabCapacity))));
        }

        /// <summary>
        /// <c>request.MaxPlayers = 10U</c> in ZPlayFabMatchmaking.CreateLobby. This one is load
        /// bearing: crossplay clients join the PlayFab lobby before ZNet ever sees them, and
        /// PlayFab answers LobbyNotJoinable once it is full, which the client reports as "server
        /// is full".
        ///
        /// Matched on the member name alone so this method carries no reference to a PlayFab type
        /// and can be JIT-ed regardless of whether those assemblies loaded.
        /// </summary>
        private static IEnumerable<CodeInstruction> RaisePlayFabCapacity(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);

            MethodInfo capacity = AccessTools.Method(typeof(PlayerLimit), nameof(PlayerLimit.PlayFabLobbyCapacity));

            int site = -1;
            int matches = 0;
            for (int i = 0; i + 1 < codes.Count; i++) {
                if (!IlMatch.IsInt32Constant(codes[i], PlayerLimit.VanillaLimit)) { continue; }
                if (!IlMatch.TargetsMemberNamed(codes[i + 1], "MaxPlayers")
                    && !IlMatch.TargetsMemberNamed(codes[i + 1], "set_MaxPlayers")) { continue; }
                site = i;
                matches++;
            }

            if (matches != 1) {
                PlayerLimit.CrossplayCapacityPinned = true;
                Logger.LogWarning($"ZPlayFabMatchmaking.CreateLobby does not have the expected IL shape (found {matches} " +
                    $"MaxPlayers = {PlayerLimit.VanillaLimit} assignments, expected 1). The crossplay lobby stays at 10 " +
                    "members, so crossplay clients will be refused past 10. Steam clients are unaffected.");
                return codes;
            }

            IlMatch.ReplaceInPlace(codes, site, new CodeInstruction(OpCodes.Call, capacity));
            return codes;
        }
    }
}
