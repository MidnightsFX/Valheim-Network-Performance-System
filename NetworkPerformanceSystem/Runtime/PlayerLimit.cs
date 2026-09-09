namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M10 - the configured player limit, and the two lobby capacities that have to agree with it.
    ///
    /// Vanilla hard-codes 10 in three unrelated places and none of them reads the other two:
    /// <list type="bullet">
    /// <item>ZNet.RPC_PeerInfo rejects the 11th peer. This is the only one that actually enforces.</item>
    /// <item>ZSteamMatchmaking.RegisterServer sizes the Steam lobby, which is purely the "x / y"
    /// the server browser prints - Steam clients read the lobby's data and then connect straight
    /// to the host, they never join the lobby itself.</item>
    /// <item>ZPlayFabMatchmaking.CreateLobby sizes the PlayFab lobby, and crossplay clients
    /// <i>do</i> join that, so PlayFab refuses the join with LobbyNotJoinable before ZNet ever
    /// sees a peer. Raising the ZNet limit without raising this one moves the wall, it does not
    /// remove it.</item>
    /// </list>
    /// Every value here collapses to vanilla's 10 when the mechanism is off, so a failed anchor
    /// or a disabled config leaves all three sites saying exactly what they said before.
    /// </summary>
    internal static class PlayerLimit {

        /// <summary>What the game hard-codes, and what every accessor here falls back to.</summary>
        internal const int VanillaLimit = 10;

        /// <summary>Steam's own ceiling on lobby members (SteamMatchmaking.CreateLobby).</summary>
        private const int SteamLobbyMemberCeiling = 250;

        /// <summary>PlayFab's documented ceiling on CreateLobbyRequest.MaxPlayers.</summary>
        private const int PlayFabLobbyMemberCeiling = 128;

        /// <summary>
        /// Set when the PlayFab lobby anchor did not match, so crossplay capacity is stuck at
        /// vanilla's 10 even though ZNet is accepting more. Reported by nps_stats, because the
        /// symptom - "the server says full at 10 but only for the Xbox players" - is otherwise
        /// impossible to attribute.
        /// </summary>
        internal static bool CrossplayCapacityPinned;

        private static bool _warnedPlayFabClamp;
        private static bool _warnedSteamClamp;

        /// <summary>
        /// True when the mechanism survived patching and the admin has left it switched on.
        /// </summary>
        internal static bool Active {
            get {
                return PatchGuard.IsActive(Mechanism.PlayerLimit)
                       && ValConfig.EnablePlayerLimitOverride != null
                       && ValConfig.MaxPlayers != null
                       && ValConfig.EnablePlayerLimitOverride.Value;
            }
        }

        /// <summary>
        /// The limit in force. Counts the same set of players vanilla counts - a listen host
        /// counts itself, a dedicated server does not count itself.
        /// </summary>
        internal static int Configured {
            get { return Active ? ValConfig.MaxPlayers.Value : VanillaLimit; }
        }

        /// <summary>
        /// Called from IL in ZNet.RPC_PeerInfo, in place of the hard-coded 10. Must never throw
        /// and must never be expensive: it runs once per join attempt, on the host, inside the
        /// RPC handler.
        /// </summary>
        internal static int Current() {
            return Configured;
        }

        /// <summary>
        /// Member capacity for the Steam lobby, which is what the server browser renders as the
        /// right-hand side of "3 / 10". Display only - nothing joins through it - so this is the
        /// limit exactly, with no allowance for the server's own lobby membership.
        /// </summary>
        internal static int SteamLobbyCapacity() {
            int limit = Configured;
            if (limit > SteamLobbyMemberCeiling) {
                if (!_warnedSteamClamp) {
                    _warnedSteamClamp = true;
                    Logger.LogWarning($"Max Players is {limit}, above Steam's {SteamLobbyMemberCeiling}-member lobby ceiling. " +
                        $"The server browser will advertise {SteamLobbyMemberCeiling}; the limit the server actually enforces is unaffected.");
                }
                return SteamLobbyMemberCeiling;
            }
            return limit;
        }

        /// <summary>
        /// Member capacity for the PlayFab lobby. Unlike the Steam one this is load-bearing:
        /// crossplay clients join the lobby before they reach ZNet, so this is the real ceiling
        /// on a crossplay server.
        ///
        /// The server process is itself a lobby member. On a listen host that member is also a
        /// player and is already inside the limit; on a dedicated server it is a member that is
        /// not a player, so the lobby needs one slot more than the limit. Vanilla asks for 10 in
        /// both cases, which is why a dedicated crossplay server fills up at nine players.
        /// </summary>
        internal static uint PlayFabLobbyCapacity() {
            int members = Configured + (NpsEnv.IsDedicated() ? 1 : 0);

            if (members > PlayFabLobbyMemberCeiling) {
                if (!_warnedPlayFabClamp) {
                    _warnedPlayFabClamp = true;
                    Logger.LogWarning($"Max Players is {Configured}, which needs {members} PlayFab lobby slots - above PlayFab's " +
                        $"{PlayFabLobbyMemberCeiling}-member ceiling. Crossplay joins will stop at {PlayFabLobbyMemberCeiling} " +
                        "even though the server itself would accept more. Steam-only servers are unaffected.");
                }
                members = PlayFabLobbyMemberCeiling;
            }

            return (uint)members;
        }
    }
}
