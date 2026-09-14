namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M10 - the configured player limit, and the capacities that have to agree with it.
    ///
    /// Vanilla hard-codes 10 in four unrelated places and none of them reads the others:
    /// <list type="bullet">
    /// <item>ZNet.RPC_PeerInfo rejects the 11th peer. This is the only one that actually enforces.</item>
    /// <item>ZSteamMatchmaking.RegisterServer sizes the Steam lobby, which is purely the "x / y"
    /// the server browser prints - Steam clients read the lobby's data and then connect straight
    /// to the host, they never join the lobby itself.</item>
    /// <item>ZPlayFabMatchmaking.CreateLobby sizes the PlayFab lobby, and crossplay clients
    /// <i>do</i> join that, so PlayFab refuses the join with LobbyNotJoinable before ZNet ever
    /// sees a peer. Raising the ZNet limit without raising this one moves the wall, it does not
    /// remove it. It is also the "x / y" a crossplay client's browser prints.</item>
    /// <item>ZPlayFabMatchmaking.CreateAndJoinNetwork sizes the PlayFab Party <i>network</i>,
    /// which is the transport crossplay peers actually connect over. It is invisible in every UI
    /// and is the lowest ceiling of the four, so a server whose lobby says 60 still stops dead at
    /// 10 connected crossplay devices until this one moves too.</item>
    /// </list>
    /// Every value here collapses to vanilla's 10 when the mechanism is off, so a failed anchor
    /// or a disabled config leaves all four sites saying exactly what they said before.
    /// </summary>
    internal static class PlayerLimit {

        /// <summary>What the game hard-codes, and what every accessor here falls back to.</summary>
        internal const int VanillaLimit = 10;

        /// <summary>Steam's own ceiling on lobby members (SteamMatchmaking.CreateLobby).</summary>
        private const int SteamLobbyMemberCeiling = 250;

        /// <summary>
        /// PlayFab's ceiling, and it binds twice over. CreateLobbyRequest.MaxPlayers accepts up to
        /// 128; PlayFabNetworkConfiguration.MaxPlayerCount refuses anything outside 1..128 in its
        /// setter, logging an error and leaving the field at its own constructor default of 32.
        /// That failure mode is why this is clamped here rather than passed through - a value over
        /// the ceiling would silently <i>shrink</i> the Party network to 32, not widen it.
        /// </summary>
        private const int PlayFabMemberCeiling = 128;

        /// <summary>
        /// Set when the PlayFab lobby anchor did not match, so crossplay capacity is stuck at
        /// vanilla's 10 even though ZNet is accepting more. Reported by nps_stats, because the
        /// symptom - "the server says full at 10 but only for the Xbox players" - is otherwise
        /// impossible to attribute.
        /// </summary>
        internal static bool CrossplayCapacityPinned;

        /// <summary>
        /// The same, for the Party network behind the lobby. Kept separate from
        /// <see cref="CrossplayCapacityPinned"/> because the two fail independently and present
        /// differently: a pinned lobby turns crossplay players away at the browser with "server is
        /// full", a pinned network lets them past the lobby and then fails the connection.
        /// </summary>
        internal static bool CrossplayNetworkPinned;

        /// <summary>
        /// Set when Valheim Plus is installed and none of the four sites were patched. The limit
        /// in force is then V+'s, not vanilla's 10, so nps_stats has to say whose it is rather
        /// than report the fallback.
        /// </summary>
        internal static bool DeferredToValheimPlus;

        /// <summary>
        /// What each advertise-side site actually returned when the game called it, or 0 if it
        /// never did. These are the only direct evidence that a transpiler's replacement call is
        /// live: an anchor can match, the patch can apply, and the site can still never run
        /// because the server registered on the other backend. nps_stats prints them.
        /// </summary>
        internal static int AdvertisedSteamCapacity;
        internal static int AdvertisedPlayFabCapacity;
        internal static int AdvertisedPartyCapacity;

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
                limit = SteamLobbyMemberCeiling;
            }

            // Logged rather than counted silently: this line firing is the proof the browser will
            // print the configured number, and its absence is the proof it will not.
            if (AdvertisedSteamCapacity != limit) {
                AdvertisedSteamCapacity = limit;
                Logger.LogInfo($"Steam lobby registered advertising {limit} player slots in the server browser.");
            }
            return limit;
        }

        /// <summary>
        /// Member capacity for the PlayFab lobby. Unlike the Steam one this does two jobs: it is
        /// the "x / y" a crossplay client's browser prints, and it is load-bearing, because
        /// crossplay clients join the lobby before they reach ZNet.
        ///
        /// See <see cref="PlayFabMembers"/> for why this is not simply the limit.
        /// </summary>
        internal static uint PlayFabLobbyCapacity() {
            int members = PlayFabMembers();

            if (AdvertisedPlayFabCapacity != members) {
                AdvertisedPlayFabCapacity = members;
                // The browser subtracts the server's own membership back off for a dedicated
                // server (ZPlayFabLobbySearch), so the slots a crossplay player sees are the
                // limit either way - which is what makes this worth stating rather than implying.
                Logger.LogInfo($"PlayFab lobby registered with {members} member slots, advertising {Configured} " +
                    "player slots in the server browser.");
            }

            return (uint)members;
        }

        /// <summary>
        /// Member capacity for the PlayFab Party network - the transport crossplay peers actually
        /// connect over, sized once by ZPlayFabMatchmaking.CreateAndJoinNetwork when the server
        /// registers. Nothing renders this number, and that is exactly why it is worth setting:
        /// it is the lowest of the four ceilings, so leaving it at 10 produces a server that
        /// advertises 60 slots, admits crossplay players past the lobby, and then cannot carry
        /// them.
        ///
        /// Counts devices, not players (PlayFabNetworkConfiguration.MaxPlayerCount feeds both
        /// MaxDeviceCount and MaxUserCount), so the server's own membership is counted exactly the
        /// way the lobby counts it.
        /// </summary>
        internal static uint PartyNetworkCapacity() {
            int members = PlayFabMembers();

            if (AdvertisedPartyCapacity != members) {
                AdvertisedPartyCapacity = members;
                Logger.LogInfo($"PlayFab Party network created with {members} device slots.");
            }

            return (uint)members;
        }

        /// <summary>
        /// The member count both PlayFab sites need, clamped to the ceiling they share.
        ///
        /// The server process is itself a member. On a listen host that member is also a player
        /// and is already inside the limit; on a dedicated server it is a member that is not a
        /// player, so PlayFab needs one slot more than the limit. Vanilla asks for 10 in both
        /// cases, which is why a dedicated crossplay server fills up at nine players.
        /// </summary>
        private static int PlayFabMembers() {
            int members = Configured + (NpsEnv.IsDedicated() ? 1 : 0);

            if (members > PlayFabMemberCeiling) {
                if (!_warnedPlayFabClamp) {
                    _warnedPlayFabClamp = true;
                    Logger.LogWarning($"Max Players is {Configured}, which needs {members} PlayFab slots - above PlayFab's " +
                        $"{PlayFabMemberCeiling}-member ceiling. Crossplay joins will stop at {PlayFabMemberCeiling} " +
                        "even though the server itself would accept more. Steam-only servers are unaffected.");
                }
                members = PlayFabMemberCeiling;
            }

            return members;
        }
    }
}
