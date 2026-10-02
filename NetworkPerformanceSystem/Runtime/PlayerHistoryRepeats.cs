using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M32 - the world's player history goes to a player only when it differs from the copy they
    /// already have.
    ///
    /// The host keeps a list of everyone who has ever joined the world (World.m_playerHistory: ID,
    /// display name, the name the server gave them, PlayFab ID), saved with the world and sent whole
    /// to every player as "HistoricalPlayerList". Vanilla sends it when a player joins and, every 2
    /// seconds from ZNet.SendPeriodicData, whenever m_historicalPlayerListUpdated is set. The flag is
    /// raised by ZNet.UpdatePlayerHistory when a connected player's entry differs from the one on
    /// file, and UpdatePlayerHistory runs twice per update: inside UpdatePlayerList, and again at the
    /// top of SendHistoricalPlayerList.
    ///
    /// Recorded on a 45-player server on 2026-10-01: the flag was raised on every update for twelve
    /// hours, so the list - 340 entries, 20 KB - went to every player every 2 seconds. That was 10.6
    /// of the server's 28 GB of upload. It started with the third player to join, and on another day
    /// with a player who was connected for two seconds; once started it never stopped, not even with
    /// one player online. Between joins every copy sent was the same size to the byte, for up to
    /// twenty minutes at a time: an entry is being rewritten and put back within each update. Vanilla on its
    /// own settles after one update. Two connected players resolving to one ID would do it - every
    /// closed Steam connection reports ID 0, and every unreadable crossplay ID compares equal - and so
    /// would a mod changing the player list between the two UpdatePlayerHistory calls. That server's
    /// saved history has no duplicate IDs, and none of its mods available here touches the list, so
    /// this both removes the cost and names the cause:
    ///
    ///   * SendHistoricalPlayerList's list is compared with the last one it built. A player whose
    ///     connection was last sent exactly these bytes is skipped. Anything that differs goes to
    ///     everyone, and a new connection always gets it, as vanilla. The flag, the history and the
    ///     HistoricalPlayerListSent event are left as vanilla has them.
    ///   * When the game marked the list changed and it came out identical, the log says which entry
    ///     was rewritten, from what to what and in which of the two calls, and whether two players
    ///     online share an ID. Counted and logged with the setting off too.
    ///
    /// A player's copy is only ever replaced whole, so holding back an identical copy leaves them
    /// exactly where the resend would have: their game clears its list, reads the same entries back
    /// and merges in the players it can see, as it did the first time.
    /// </summary>
    internal static class PlayerHistoryRepeats {

        /// <summary>The same finding is logged again no sooner than this.</summary>
        internal const double LogIntervalSeconds = 600d;

        /// <summary>Rewrites kept per update for the log. The churn seen so far is one entry.</summary>
        internal const int MaxRewrites = 4;

        /// <summary>Lists in a row that must come out identical after being marked changed before
        /// it is logged. A player reconnecting while their old connection is still open causes one
        /// or two; the churn this is for causes one on every update.</summary>
        internal const int StreakToReport = 5;

        // Since start. Host only, main thread, like the calls they count.
        internal static long Built;          // lists SendHistoricalPlayerList built and sent out
        internal static long Changed;        // of those, ones that differed from the list built before
        internal static long Sent;           // copies sent to players
        internal static long Held;           // copies not sent: that player already had these bytes
        internal static long SentAnyway;     // copies a player already had, sent because the setting is off
        internal static long BytesHeld;
        internal static int LastBytes;       // size of the last list built
        internal static long Roundtrips;     // lists the game marked changed that came out identical
        internal static string LastFinding;  // the last of those, described, for nps_stats

        /// <summary>The hooks are in. Counting and the log run whenever this is true.</summary>
        internal static bool Hooked => PatchGuard.IsActive(Mechanism.PlayerHistoryRepeats);

        /// <summary>Copies a player already has are being held back as well as counted.</summary>
        internal static bool Limiting =>
            Hooked
            && ValConfig.SendPlayerHistoryOnlyWhenChanged != null
            && ValConfig.SendPlayerHistoryOnlyWhenChanged.Value;

        // -- the send ----------------------------------------------------------------------

        private sealed class Delivered {
            internal object Content;
        }

        /// <summary>What each connection was last sent, as the token of its bytes. An entry goes
        /// when its ZRpc does: a reconnect is a new ZRpc and starts with nothing.</summary>
        private static readonly ConditionalWeakTable<ZRpc, Delivered> ByConnection = new ConditionalWeakTable<ZRpc, Delivered>();

        private static ZPackage _builtPackage;   // the package the current token was taken from
        private static byte[] _builtBytes;       // its bytes
        private static object _content;          // stands for _builtBytes; a new one whenever they change

        /// <summary>
        /// Stands in for SendHistoricalPlayerList's `peer.m_rpc.Invoke("HistoricalPlayerList",
        /// package)`, once for each ready player, with the same arguments. Anything it does not
        /// recognise goes straight to vanilla's Invoke.
        /// </summary>
        internal static void Invoke(ZRpc rpc, string method, object[] parameters) {
            ZPackage package = parameters != null && parameters.Length == 1 ? parameters[0] as ZPackage : null;
            if (rpc == null || package == null || !Hooked) {
                rpc.Invoke(method, parameters);
                return;
            }

            object content;
            try {
                content = ContentOf(package);
            } catch (Exception e) {
                Logger.LogWarning($"Could not compare the player history with the last one sent ({e.GetType().Name}); sending it as vanilla.");
                rpc.Invoke(method, parameters);
                return;
            }

            bool alreadyHas = ByConnection.TryGetValue(rpc, out Delivered delivered) && ReferenceEquals(delivered.Content, content);
            if (alreadyHas && Limiting) {
                Held++;
                BytesHeld += _builtBytes.Length;
                return;
            }

            // ZRpc.Invoke drops the message on a closed connection; only one that went out counts.
            bool connected = rpc.IsConnected();
            rpc.Invoke(method, parameters);
            if (!connected) { return; }

            if (alreadyHas) { SentAnyway++; } else { Sent++; }
            if (delivered == null) { delivered = ByConnection.GetOrCreateValue(rpc); }
            delivered.Content = content;
        }

        /// <summary>
        /// The token for this package's bytes. The first call for a package is a new list: it is
        /// read once, compared with the last list built, and closes the update's record of what
        /// was rewritten. The rest of that send loop gets the same token back by reference.
        /// </summary>
        internal static object ContentOf(ZPackage package) {
            if (ReferenceEquals(package, _builtPackage)) { return _content; }

            byte[] bytes = package.GetArray();
            _builtPackage = package;
            Built++;
            LastBytes = bytes.Length;

            bool same = _builtBytes != null && SameBytes(bytes, _builtBytes);
            if (!same) {
                _builtBytes = bytes;
                _content = new object();
                Changed++;
            }
            OnBuilt(same);
            return _content;
        }

        /// <summary>Pure.</summary>
        internal static bool SameBytes(byte[] a, byte[] b) {
            if (a.Length != b.Length) { return false; }
            for (int i = 0; i < a.Length; i++) {
                if (a[i] != b[i]) { return false; }
            }
            return true;
        }

        // -- what keeps marking it changed -------------------------------------------------

        internal struct Rewrite {
            internal int Index;
            internal ZNet.CrossNetworkUserInfo Before;   // default for an entry that was added
            internal ZNet.CrossNetworkUserInfo After;
            internal bool Added;
            internal bool InSend;   // in SendHistoricalPlayerList's own update, not UpdatePlayerList's
        }

        /// <summary>Set by the prefix on SendHistoricalPlayerList, cleared by its postfix.</summary>
        internal static bool InSend;

        private static readonly List<ZNet.CrossNetworkUserInfo> Snapshot = new List<ZNet.CrossNetworkUserInfo>();
        private static readonly List<Rewrite> Pending = new List<Rewrite>();
        private static bool _flagBefore;
        private static bool _putBack;            // the flag went up with every entry as it was before
        private static string _sharedIds;        // players online sharing an ID, when that was checked
        private static int _streak;              // identical lists in a row that were marked changed
        private static DateTime _loggedAt = DateTime.MinValue;
        private static long _roundtripsAtLog;

        /// <summary>From the prefix on ZNet.UpdatePlayerHistory, on the host.</summary>
        internal static void BeforeUpdate(List<ZNet.CrossNetworkUserInfo> history, bool flag) {
            Snapshot.Clear();
            if (history != null) { Snapshot.AddRange(history); }
            _flagBefore = flag;
        }

        /// <summary>
        /// From the postfix on ZNet.UpdatePlayerHistory, on the host: every entry that differs
        /// from the snapshot, and whether the flag went up with nothing different afterwards -
        /// written and put back inside the one call, which is what two connected players with one
        /// ID look like.
        /// </summary>
        internal static void AfterUpdate(List<ZNet.CrossNetworkUserInfo> history, bool flag, List<ZNet.PlayerInfo> players) {
            if (history == null) { return; }

            int found = 0;
            int common = Math.Min(Snapshot.Count, history.Count);
            for (int i = 0; i < common; i++) {
                if (Snapshot[i].Equals(history[i])) { continue; }
                Note(new Rewrite { Index = i, Before = Snapshot[i], After = history[i], InSend = InSend });
                found++;
            }
            for (int i = common; i < history.Count; i++) {
                Note(new Rewrite { Index = i, After = history[i], Added = true, InSend = InSend });
                found++;
            }

            if (found == 0 && flag && !_flagBefore) { _putBack = true; }
            if ((found > 0 || _putBack) && _sharedIds == null) { _sharedIds = SharedIds(players); }
        }

        private static void Note(Rewrite rewrite) {
            if (Pending.Count < MaxRewrites) { Pending.Add(rewrite); }
        }

        /// <summary>From the postfix on SendHistoricalPlayerList: whatever was rewritten belonged
        /// to the list just built.</summary>
        internal static void OnSendDone() {
            InSend = false;
            ClearPending();
        }

        private static void ClearPending() {
            Pending.Clear();
            _putBack = false;
            _sharedIds = null;
        }

        private static void OnBuilt(bool same) {
            if (!same) {
                _streak = 0;
                return;
            }
            if (Pending.Count == 0 && !_putBack) { return; }

            Roundtrips++;
            _streak++;
            try {
                LastFinding = DescribePending();
                if (_streak < StreakToReport) { return; }

                DateTime now = DateTime.UtcNow;
                if (_roundtripsAtLog > 0 && (now - _loggedAt).TotalSeconds < LogIntervalSeconds) { return; }

                long since = Roundtrips - _roundtripsAtLog;
                _loggedAt = now;
                _roundtripsAtLog = Roundtrips;
                string outcome = Limiting
                    ? "It is not sent again to players who already have it."
                    : "Send Player History Only When Changed is off, so it is sent to every player again each time.";
                Logger.LogWarning(
                    $"The game keeps marking the world's player history changed when it is not ({since} time(s) " +
                    $"{(since == Roundtrips ? "so far" : "since the last report")}). Each time the whole list, {LastBytes / 1024f:F1} KB, " +
                    $"would go to every player. {outcome} What was rewritten: {LastFinding}");
            } catch (Exception e) {
                Logger.LogWarning($"The world's player history keeps being marked changed when it is not ({e.GetType().Name} while describing it).");
            }
        }

        internal static string DescribePending() {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Pending.Count; i++) {
                Rewrite r = Pending[i];
                if (sb.Length > 0) { sb.Append("; "); }
                sb.Append($"entry {r.Index} ");
                sb.Append(r.Added ? "added as " + Show(r.After) : Show(r.Before) + " -> " + Show(r.After));
                sb.Append(r.InSend ? " (in SendHistoricalPlayerList)" : " (in UpdatePlayerList)");
            }
            if (_putBack) {
                if (sb.Length > 0) { sb.Append("; "); }
                sb.Append("an entry was rewritten and put back inside one update");
            }
            sb.Append(". ").Append(_sharedIds ?? "No two players online share an ID.");
            return sb.ToString();
        }

        /// <summary>Players in the list the game builds from its connections who resolve to the
        /// same ID, by the game's own comparison. Pure apart from what it is handed.</summary>
        internal static string SharedIds(List<ZNet.PlayerInfo> players) {
            if (players == null) { return null; }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < players.Count; i++) {
                bool first = true;
                for (int j = 0; j < i; j++) {
                    if (players[j].m_userInfo.m_id == players[i].m_userInfo.m_id) { first = false; break; }
                }
                if (!first) { continue; }

                int count = 0;
                StringBuilder who = new StringBuilder();
                for (int j = i; j < players.Count; j++) {
                    if (players[j].m_userInfo.m_id != players[i].m_userInfo.m_id) { continue; }
                    count++;
                    if (who.Length > 0) { who.Append(", "); }
                    who.Append(Show(players[j].m_userInfo)).Append(" playing '").Append(players[j].m_name).Append('\'');
                }
                if (count < 2) { continue; }

                if (sb.Length > 0) { sb.Append(' '); }
                sb.Append($"{count} players online share the ID {ShowId(players[i].m_userInfo.m_id)}: {who}.");
            }
            return sb.Length > 0 ? sb.ToString() : "No two players online share an ID.";
        }

        private static string Show(ZNet.CrossNetworkUserInfo info) {
            return $"'{info.m_displayName}' / '{info.m_serverAssignedDisplayName}' ({ShowId(info.m_id)}, PlayFab '{info.m_playfabId}')";
        }

        private static string ShowId(Splatform.PlatformUserID id) {
            return id.IsValid ? id.ToString() : "an invalid ID";
        }

        // -- lifecycle ---------------------------------------------------------------------

        /// <summary>Every connection is new in the next session; dropping the token is enough to
        /// make the first list of a session go to everyone even if a stale entry survived.</summary>
        internal static void Reset() {
            _builtPackage = null;
            _builtBytes = null;
            _content = null;
            InSend = false;
            Snapshot.Clear();
            ClearPending();
            _streak = 0;
            _loggedAt = DateTime.MinValue;
            _roundtripsAtLog = 0;
            Built = 0;
            Changed = 0;
            Sent = 0;
            Held = 0;
            SentAnyway = 0;
            BytesHeld = 0;
            LastBytes = 0;
            Roundtrips = 0;
            LastFinding = null;
        }
    }
}
