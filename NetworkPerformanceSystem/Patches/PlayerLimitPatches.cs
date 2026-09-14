using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M10 - rewrites the four hard-coded 10s that together decide how many players a server
    /// holds. See <see cref="PlayerLimit"/> for what each site actually controls; only the first
    /// enforces anything, the second and third are what the server browser prints, and the third
    /// and fourth are what actually admit and carry a crossplay client.
    ///
    /// Host-side by construction: ZNet.RPC_PeerInfo runs the limit check inside its own
    /// <c>m_isServer</c> branch, and the lobbies and the Party network are only ever created by
    /// the process that registers the server. Nothing here needs a side gate of its own.
    /// </summary>
    [HarmonyPatch]
    internal static class PlayerLimitPatches {

        // --- Valheim Plus -----------------------------------------------------------------------

        /// <summary>
        /// Skips the two attribute-driven sites below when Valheim Plus owns the player limit. The
        /// PlayFab pair is applied by hand and checks the same thing in
        /// <see cref="ApplyPlayFabCapacityPatch"/>.
        /// </summary>
        [HarmonyPrepare]
        private static bool Prepare() {
            return !DeferToValheimPlus();
        }

        /// <summary>
        /// Valheim Plus transpiles ZNet.RPC_PeerInfo, ZPlayFabMatchmaking.CreateLobby and
        /// CreateAndJoinNetwork for its own <c>maxPlayers</c>, and the two cannot share them. Its
        /// RPC_PeerInfo transpiler overwrites whatever operand follows GetNrOfPlayers without
        /// looking at the opcode: running after ours it turns our call into a call with an int
        /// operand, which is invalid IL. Running before ours it leaves an int where Ldc_I4_S
        /// carries an sbyte, our anchor misses, and M10 stands down anyway - but with a warning
        /// that blames a game update.
        ///
        /// So M10 stands down up front and says why. The Steam lobby site is skipped with the rest
        /// even though V+ leaves it alone: with M10 off it could only return vanilla's 10, which is
        /// what the unpatched method already passes.
        /// </summary>
        private static bool DeferToValheimPlus() {
            if (!PatchGuard.IsPluginLoaded(PatchGuard.ValheimPlusGUID)) { return false; }

            PlayerLimit.DeferredToValheimPlus = true;
            PatchGuard.Disable(Mechanism.PlayerLimit,
                "Valheim Plus is installed and sets the player limit itself. Its maxPlayers setting ([Server] in " +
                "valheim_plus.cfg) is the one in force; this mod's Player Limit settings are ignored.");
            return true;
        }

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
            if (DeferToValheimPlus()) { return; }

            try {
                PatchPlayFabLobby(harmony);
            } catch (Exception ex) {
                PlayerLimit.CrossplayCapacityPinned = true;
                Logger.LogWarning("Could not reach ZPlayFabMatchmaking.CreateLobby to raise the crossplay lobby capacity " +
                    $"({ex.GetType().Name}: {ex.Message}). Crossplay clients will still be refused past 10; " +
                    "Steam clients are unaffected.");
            }

            // Caught separately from the lobby above: the two sites fail for different reasons and
            // a server with one of them raised is still better off than a server with neither.
            try {
                PatchPartyNetwork(harmony);
            } catch (Exception ex) {
                PlayerLimit.CrossplayNetworkPinned = true;
                Logger.LogWarning("Could not reach ZPlayFabMatchmaking.CreateAndJoinNetwork to raise the crossplay Party " +
                    $"network capacity ({ex.GetType().Name}: {ex.Message}). Crossplay connections will stop at 10 " +
                    "devices whatever the lobby advertises; Steam clients are unaffected.");
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

        // --- real capacity, PlayFab Party network -----------------------------------------------

        /// <summary>
        /// Kept out of its caller for the same reason as <see cref="PatchPlayFabLobby"/>: the type
        /// load has to happen at a call the caller can wrap.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PatchPartyNetwork(Harmony harmony) {
            MethodInfo target = AccessTools.Method(typeof(ZPlayFabMatchmaking), "CreateAndJoinNetwork");
            if (target == null) {
                PlayerLimit.CrossplayNetworkPinned = true;
                Logger.LogWarning("ZPlayFabMatchmaking.CreateAndJoinNetwork not found; the crossplay Party network stays at " +
                    "10 devices. Crossplay players will be admitted by the lobby and then fail to connect past 10.");
                return;
            }

            harmony.Patch(target, transpiler: new HarmonyMethod(
                AccessTools.Method(typeof(PlayerLimitPatches), nameof(RaisePartyCapacity))));
        }

        /// <summary>
        /// <c>MaxPlayerCount = 10u</c> in ZPlayFabMatchmaking.CreateAndJoinNetwork. Sizes the
        /// PlayFab Party network that crossplay peers are carried over - the lowest of M10's four
        /// ceilings and the only one with no UI at all, so leaving it behind produces a server
        /// that advertises its real limit, lets crossplay players through the lobby, and then
        /// cannot carry more than ten of them.
        ///
        /// The sibling assignment in this method is
        /// <c>DirectPeerConnectivityOptions = (..)15u</c>, so the constant is not ambiguous, but
        /// the anchor still pairs the 10 with the member it is assigned to rather than trusting
        /// that to stay true.
        ///
        /// MaxPlayerCount is a property whose setter rejects anything outside 1..128 by logging
        /// and keeping its own default of 32 - a silent shrink, not a throw - which is why
        /// <see cref="PlayerLimit.PartyNetworkCapacity"/> clamps before returning. Matched on the
        /// member name alone so this method carries no reference to a PlayFab type.
        /// </summary>
        private static IEnumerable<CodeInstruction> RaisePartyCapacity(IEnumerable<CodeInstruction> instructions) {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);

            MethodInfo capacity = AccessTools.Method(typeof(PlayerLimit), nameof(PlayerLimit.PartyNetworkCapacity));

            int site = -1;
            int matches = 0;
            for (int i = 0; i + 1 < codes.Count; i++) {
                if (!IlMatch.IsInt32Constant(codes[i], PlayerLimit.VanillaLimit)) { continue; }
                if (!IlMatch.TargetsMemberNamed(codes[i + 1], "MaxPlayerCount")
                    && !IlMatch.TargetsMemberNamed(codes[i + 1], "set_MaxPlayerCount")) { continue; }
                site = i;
                matches++;
            }

            if (matches != 1) {
                PlayerLimit.CrossplayNetworkPinned = true;
                Logger.LogWarning($"ZPlayFabMatchmaking.CreateAndJoinNetwork does not have the expected IL shape (found " +
                    $"{matches} MaxPlayerCount = {PlayerLimit.VanillaLimit} assignments, expected 1). The crossplay Party " +
                    "network stays at 10 devices, so crossplay players will be admitted by the lobby and then fail to " +
                    "connect past 10. Steam clients are unaffected.");
                return codes;
            }

            IlMatch.ReplaceInPlace(codes, site, new CodeInstruction(OpCodes.Call, capacity));
            return codes;
        }
    }
}
