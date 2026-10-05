using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M36 - who is sent the floating damage numbers.
    ///
    /// The game makes a damage number on whichever machine simulates the thing that was hit - the
    /// owner of the creature, tree or wall, or the player who was hurt - and broadcasts it to
    /// everybody (DamageText.ShowText, RPC_DamageText to ZRoutedRpc.Everybody). Each receiver throws
    /// it away unless it is within 30 m of their camera. So when one player's game runs a fight's
    /// creatures, every hit anybody lands is a message from that game to the server and on to each
    /// player nearby. Measured over 28 player-hours it was 2-3% of the routed messages and well
    /// under 1% of all traffic - small, but pure presentation, and it bunches up in exactly the
    /// fights where the link is busiest.
    ///
    /// Routed RPC / Damage Numbers:
    ///
    ///   * Everyone Nearby - as the game does it. The server's relay filter (M7) now sends a number
    ///     only to players within DamageNumberRelayMetres of it - the game's 30 m text distance plus
    ///     slack for camera offset and a position a moment old - rather than to everyone within a
    ///     zone of their loaded area.
    ///   * Attacker Only - a hit number on a creature, tree, rock or building goes only to the player
    ///     whose hit caused it: the machine that applied the damage is handling that player's
    ///     RPC_Damage when it makes the number, so the routed message's sender is the attacker.
    ///     Everything else - damage a player or their tame takes, blocks, heals, crafting and picking
    ///     bonuses, burning and poison ticks - stays on the machine that made it.
    ///   * Off - never sent over the network. Each player sees only the numbers their own game makes.
    ///
    /// The choice is applied on every machine that has this mod (the setting is the server's), and
    /// on the server for the rest: with Off it stops relaying numbers from players without the mod;
    /// with Attacker Only it relays theirs as Everyone Nearby, because it cannot tell who hit what.
    /// </summary>
    internal static class DamageNumbers {

        internal enum Mode : byte { EveryoneNearby, AttackerOnly, Off }

        internal const string EveryoneNearbyName = "Everyone Nearby";
        internal const string AttackerOnlyName = "Attacker Only";
        internal const string OffName = "Off";

        internal static readonly int DamageTextHash = "RPC_DamageText".GetStableHashCode();

        /// <summary>The game's own text distance (DamageText.m_maxTextDistance) plus 20 m: a
        /// third-person camera sits a few metres off the character, and a reference position can
        /// be a moment old. Past this a receiver would throw the number away.</summary>
        internal const float RelaySlackMetres = 20f;
        private const float VanillaTextDistance = 30f;

        private static Mode _mode = Mode.EveryoneNearby;
        private static bool _modeRead;

        // The senders of the routed RPCs this machine is handling right now, innermost last. A
        // handler can invoke another routed RPC locally, so it is a stack; nesting deeper than this
        // is only counted, and the innermost tracked sender stands for the rest.
        private const int MaxTrackedDepth = 16;
        private static readonly long[] RoutedSenders = new long[MaxTrackedDepth];
        private static int _routedDepth;

        // For nps_stats.
        internal static long KeptLocal;
        internal static long SentToAttacker;
        internal static long RelayDropped;
        internal static long RelayOutOfRange;

        internal static bool Active => PatchGuard.IsActive(Mechanism.DamageNumbers);

        internal static Mode Current {
            get {
                if (!_modeRead) { ReadMode(); }
                return _mode;
            }
        }

        internal static string CurrentName => NameOf(Current);

        /// <summary>The setting changed: re-read it.</summary>
        internal static void OnConfigChanged() {
            ReadMode();
        }

        private static void ReadMode() {
            _mode = Parse(ValConfig.DamageNumbers != null ? ValConfig.DamageNumbers.Value : EveryoneNearbyName);
            _modeRead = true;
        }

        internal static Mode Parse(string value) {
            if (string.Equals(value, AttackerOnlyName, System.StringComparison.OrdinalIgnoreCase)) { return Mode.AttackerOnly; }
            if (string.Equals(value, OffName, System.StringComparison.OrdinalIgnoreCase)) { return Mode.Off; }
            return Mode.EveryoneNearby;
        }

        internal static string NameOf(Mode mode) {
            switch (mode) {
                case Mode.AttackerOnly: return AttackerOnlyName;
                case Mode.Off: return OffName;
                default: return EveryoneNearbyName;
            }
        }

        // -- routed handler tracking ------------------------------------------------------

        internal static void EnterRouted(long sender) {
            if (_routedDepth < MaxTrackedDepth) { RoutedSenders[_routedDepth] = sender; }
            _routedDepth++;
        }

        internal static void ExitRouted() {
            if (_routedDepth > 0) { _routedDepth--; }
        }

        /// <summary>The sender of the innermost routed RPC being handled; 0 when none.</summary>
        private static long CurrentRoutedSender() {
            if (_routedDepth <= 0) { return 0L; }
            return RoutedSenders[System.Math.Min(_routedDepth, MaxTrackedDepth) - 1];
        }

        // -- the sender's side --------------------------------------------------------------

        /// <summary>
        /// Who a number made here should go to under Attacker Only: the remote player whose routed
        /// message this machine is handling, when the number is a hit on something that is not a
        /// player or a tame (the game's "player" flag is set for those, and for bonuses); 0 for
        /// "show it here only". Pure.
        /// </summary>
        internal static long AttackerFor(int textType, bool playerFlag, int routedDepth, long routedSender, long self) {
            if (playerFlag) { return 0L; }
            if (!IsHitNumber(textType)) { return 0L; }
            if (routedDepth <= 0 || routedSender == 0L || routedSender == self) { return 0L; }
            return routedSender;
        }

        /// <summary>Normal, Resistant, Weak, Immune and TooHard - the numbers a hit makes. Heal,
        /// Blocked and Bonus are about the player on this machine.</summary>
        internal static bool IsHitNumber(int textType) {
            switch ((DamageText.TextType)textType) {
                case DamageText.TextType.Normal:
                case DamageText.TextType.Resistant:
                case DamageText.TextType.Weak:
                case DamageText.TextType.Immune:
                case DamageText.TextType.TooHard:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Prefix body for DamageText.ShowText(TextType, Vector3, string, bool). True lets the game
        /// broadcast as usual; false means the number was dealt with here.
        /// </summary>
        internal static bool OnShowText(DamageText text, DamageText.TextType type, Vector3 pos, string value, bool player) {
            if (!Active) { return true; }
            Mode mode = Current;
            if (mode == Mode.EveryoneNearby) { return true; }

            ZRoutedRpc router = ZRoutedRpc.instance;
            if (router == null || text == null) { return true; }

            long target = mode == Mode.AttackerOnly
                ? AttackerFor((int)type, player, _routedDepth, CurrentRoutedSender(), ZNet.GetUID())
                : 0L;

            ZPackage pkg = new ZPackage();
            pkg.Write((int)type);
            pkg.Write(pos);
            pkg.Write(value);
            pkg.Write(player);

            if (target != 0L) {
                router.InvokeRoutedRPC(target, "RPC_DamageText", pkg);
                SentToAttacker++;
                return false;
            }

            // What the game's own local delivery does: the number goes through RPC_DamageText on
            // this machine, as from itself, which applies the camera distance and the "my own
            // damage" colour exactly as before.
            pkg.SetPos(0);
            text.RPC_DamageText(ZNet.GetUID(), pkg);
            KeptLocal++;
            return false;
        }

        // -- the server's relay --------------------------------------------------------------

        /// <summary>The relay drops this message entirely (Off).</summary>
        internal static bool DropAtRelay(ZRoutedRpc.RoutedRPCData data) {
            if (!Active || data == null || data.m_methodHash != DamageTextHash) { return false; }
            if (Current != Mode.Off) { return false; }
            RelayDropped++;
            return true;
        }

        /// <summary>Squared distance within which a relayed number can still be seen.</summary>
        internal static float RelayDistanceSq() {
            float distance = VanillaTextDistance;
            DamageText text = DamageText.instance;
            if (text != null && text.m_maxTextDistance > 0f) { distance = text.m_maxTextDistance; }
            distance += RelaySlackMetres;
            return distance * distance;
        }

        internal static void Reset() {
            _routedDepth = 0;
        }
    }
}
