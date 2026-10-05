using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M30 - a status effect asked for on a creature somebody else simulates is sent a few times
    /// a second, not every time it is asked for.
    ///
    /// SEMan.AddStatusEffect(hash, ...) adds the effect when the caller owns the character and
    /// otherwise sends RPC_AddStatusEffect to whoever does - one message per call, with nothing
    /// to stop the next call sending another. Its callers are written as if the first case were
    /// the only one. EffectArea is the one that matters: it lists a character when it walks in,
    /// and only if this machine owns it then, and from there applies its effect to everything on
    /// the list on every physics tick without asking again. Vanilla sets an effect on one area
    /// in the whole game, so this goes unseen until a mod sets one on something common.
    ///
    /// What turns it into a flood is the creature changing owner while both players still have
    /// it loaded - which vanilla rarely does and M3 does all the time. Every area the old owner
    /// has the creature listed in then sends fifty messages a second, per creature. Recorded on
    /// a two-player server on 2026-09-30: four hens handed to a player who had just arrived,
    /// about 7000 RPC_AddStatusEffect a second from the first player's game, which is the whole
    /// 150 KB/s of their uplink, relayed by the host onto the whole downlink of the second. No
    /// world data moved in either direction for as long as it lasted - nothing loaded around the
    /// second player - and the first player's socket closed with 1.39 million messages queued.
    ///
    /// A request that repeats the last one exactly - same character, same effect, same
    /// arguments - inside the hold time is not sent. On the owner it would do what the one before
    /// it did: start the effect, or reset a timer that was reset a moment ago. The first request
    /// always goes, and so does anything that differs.
    ///
    /// The hold time follows the effect (HoldSecondsFor). A repeated request for an effect the
    /// character already has does nothing on the owner but restart its timer
    /// (SEMan.Internal_AddStatusEffect), and an effect area always asks for exactly that. So an
    /// effect that lasts only needs refreshing well inside its duration: a third of it, between
    /// RepeatSeconds and MaxRepeatSeconds, and MaxRepeatSeconds for one that never runs out - like
    /// SafetyStatus's, whose repeats were 40-48% of every routed message the 2026-10-03 recordings
    /// carried, still about 100 a second at four a second per creature. An effect this machine
    /// does not know, or an add-only request that does not restart the timer, keeps RepeatSeconds,
    /// so nothing that held before can lapse now.
    ///
    /// Two places, one rule:
    ///
    ///   * Where the request is made, on every machine with this mod, before it becomes a
    ///     message. This is the half that gives the sender its uplink back.
    ///   * On the host, for requests arriving from players without the mod. It cannot unblock
    ///     their uplink, but the repeats are not relayed, so nobody else's downlink pays.
    ///
    /// Both halves count requests per effect per second whether or not the limit is switched
    /// on, and say so in the log when one passes FloodPerSecond: which effect by name, on what,
    /// and - where the request is made - which effect areas carry it and what called for it.
    /// That names the mod.
    /// </summary>
    internal static class StatusEffectRepeats {

        /// <summary>The shortest hold: four a second for one effect on one character. Used for an
        /// effect this machine cannot look up, and for requests that do not restart the timer.</summary>
        internal const float RepeatSeconds = 0.25f;

        /// <summary>The longest hold: an effect that lasts, or never runs out, is refreshed at
        /// least this often, so one removed on the owner comes back within it.</summary>
        internal const float MaxRepeatSeconds = 2f;

        /// <summary>A lasting effect is refreshed once in this share of its duration, leaving the
        /// rest as headroom for the message's trip.</summary>
        internal const float RefreshShareOfDuration = 1f / 3f;

        /// <summary>An effect hash this machine could not look up is tried again after this long:
        /// mods add theirs when the item database is built, which can come after the first ask.</summary>
        private const float UnknownRetrySeconds = 30f;

        /// <summary>Requests for one effect from one machine in one second that count as a flood.
        /// A guardian power or a hit lands a handful; an effect area at fault lands thousands.</summary>
        internal const int FloodPerSecond = 100;

        /// <summary>The same effect from the same machine is logged again no sooner than this.</summary>
        internal const float LogIntervalSeconds = 60f;

        /// <summary>RPC_AddStatusEffect's arguments on the wire: name hash (4), reset time (1),
        /// item level (4), skill level (4), variant (4). A message under that name with any
        /// other length is another mod's, and is left alone.</summary>
        internal const int ArgumentBytes = 17;

        internal static readonly int AddStatusEffectHash = "RPC_AddStatusEffect".GetStableHashCode();

        private const float PruneIntervalSeconds = 10f;
        private const float ForgetAfterSeconds = 5f;
        private const float ForgetFloodAfterSeconds = 300f;

        // -- pure --------------------------------------------------------------------------

        /// <summary>
        /// How long an exact repeat of a request is held back. Pure.
        ///   * a request that does not restart the timer, or an effect this machine does not know
        ///     -> RepeatSeconds, as before;
        ///   * an effect that never runs out (duration 0) -> MaxRepeatSeconds;
        ///   * otherwise a third of its duration, between RepeatSeconds and MaxRepeatSeconds.
        /// </summary>
        internal static float HoldSecondsFor(bool resetTime, bool known, float durationSeconds) {
            if (!resetTime || !known) { return RepeatSeconds; }
            if (!(durationSeconds > 0f) || float.IsInfinity(durationSeconds)) { return MaxRepeatSeconds; }
            float hold = durationSeconds * RefreshShareOfDuration;
            return hold < RepeatSeconds ? RepeatSeconds : hold > MaxRepeatSeconds ? MaxRepeatSeconds : hold;
        }

        internal struct Key : IEquatable<Key> {
            internal long Sender;      // 0 for requests made on this machine
            internal ZDOID Target;
            internal int Effect;

            internal Key(long sender, ZDOID target, int effect) {
                Sender = sender;
                Target = target;
                Effect = effect;
            }

            public bool Equals(Key other) {
                return Sender == other.Sender && Effect == other.Effect && Target == other.Target;
            }

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode() {
                unchecked {
                    int hash = Effect;
                    hash = hash * 397 ^ Sender.GetHashCode();
                    hash = hash * 397 ^ Target.UserID.GetHashCode();
                    hash = hash * 397 ^ (int)Target.ID;
                    return hash;
                }
            }
        }

        private struct Last {
            internal float At;
            internal bool ResetTime;
            internal int ItemLevel;
            internal float SkillLevel;
            internal int Variant;
        }

        /// <summary>The last request that went out for each character and effect.</summary>
        internal sealed class Table {
            private readonly Dictionary<Key, Last> _last = new Dictionary<Key, Last>();
            private readonly List<Key> _expired = new List<Key>();
            private float _lastPrune;

            internal int Count => _last.Count;

            /// <summary>
            /// False when this request repeats the last one that passed for its key, exactly, less
            /// than holdSeconds after it. A held repeat does not move the clock, so the next one
            /// to pass is holdSeconds after the last that did.
            /// </summary>
            internal bool Pass(Key key, bool resetTime, int itemLevel, float skillLevel, int variant, float now, float holdSeconds) {
                PruneIfDue(now);

                if (_last.TryGetValue(key, out Last last)) {
                    float since = now - last.At;
                    if (since >= 0f && since < holdSeconds
                        && last.ResetTime == resetTime && last.ItemLevel == itemLevel
                        && last.SkillLevel.Equals(skillLevel) && last.Variant == variant) {
                        return false;
                    }
                }

                _last[key] = new Last { At = now, ResetTime = resetTime, ItemLevel = itemLevel, SkillLevel = skillLevel, Variant = variant };
                return true;
            }

            private void PruneIfDue(float now) {
                if (now >= _lastPrune && now - _lastPrune < PruneIntervalSeconds) { return; }
                _lastPrune = now;
                if (_last.Count == 0) { return; }

                _expired.Clear();
                foreach (KeyValuePair<Key, Last> entry in _last) {
                    float age = now - entry.Value.At;
                    if (age > ForgetAfterSeconds || age < 0f) { _expired.Add(entry.Key); }
                }
                for (int i = 0; i < _expired.Count; i++) { _last.Remove(_expired[i]); }
            }

            internal void Forget(long sender) {
                _expired.Clear();
                foreach (KeyValuePair<Key, Last> entry in _last) {
                    if (entry.Key.Sender == sender) { _expired.Add(entry.Key); }
                }
                for (int i = 0; i < _expired.Count; i++) { _last.Remove(_expired[i]); }
            }

            internal void Clear() {
                _last.Clear();
                _expired.Clear();
                _lastPrune = 0f;
            }
        }

        /// <summary>One second of one effect from one machine, as it is reported.</summary>
        internal struct Report {
            internal long Sender;
            internal int Effect;
            internal ZDOID Target;     // the last character it was asked for
            internal int Asked;
            internal int Held;
        }

        private sealed class Flood {
            internal float WindowStart;
            internal float LoggedAt = float.NegativeInfinity;
            internal int Asked;
            internal int Held;
            internal ZDOID Target;
        }

        /// <summary>Requests per effect per sender, a second at a time.</summary>
        internal sealed class Meter {
            private readonly Dictionary<Key, Flood> _floods = new Dictionary<Key, Flood>();
            private readonly List<Key> _expired = new List<Key>();
            private float _lastPrune;

            /// <summary>
            /// Counts one request. True, with the figures for the second that has just ended, when
            /// that second held FloodPerSecond requests or more and this effect from this sender
            /// has not been reported in the last LogIntervalSeconds. A second is closed by the
            /// first request to arrive after it, so a flood that stops dead is not reported for
            /// its last part-second.
            /// </summary>
            internal bool Note(long sender, int effect, ZDOID target, bool held, float now, out Report report) {
                report = default;
                PruneIfDue(now);

                Key key = new Key(sender, ZDOID.None, effect);
                if (!_floods.TryGetValue(key, out Flood flood)) {
                    flood = new Flood { WindowStart = now };
                    _floods[key] = flood;
                }

                bool due = false;
                float open = now - flood.WindowStart;
                if (open >= 1f || open < 0f) {
                    float sinceLogged = now - flood.LoggedAt;
                    if (flood.Asked >= FloodPerSecond && (sinceLogged >= LogIntervalSeconds || sinceLogged < 0f)) {
                        report = new Report { Sender = sender, Effect = effect, Target = flood.Target, Asked = flood.Asked, Held = flood.Held };
                        flood.LoggedAt = now;
                        due = true;
                    }
                    flood.WindowStart = now;
                    flood.Asked = 0;
                    flood.Held = 0;
                }

                flood.Asked++;
                if (held) { flood.Held++; }
                flood.Target = target;
                return due;
            }

            private void PruneIfDue(float now) {
                if (now >= _lastPrune && now - _lastPrune < LogIntervalSeconds) { return; }
                _lastPrune = now;
                if (_floods.Count == 0) { return; }

                _expired.Clear();
                foreach (KeyValuePair<Key, Flood> entry in _floods) {
                    float idle = now - entry.Value.WindowStart;
                    if (idle > ForgetFloodAfterSeconds || idle < 0f) { _expired.Add(entry.Key); }
                }
                for (int i = 0; i < _expired.Count; i++) { _floods.Remove(_expired[i]); }
            }

            internal void Forget(long sender) {
                _expired.Clear();
                foreach (KeyValuePair<Key, Flood> entry in _floods) {
                    if (entry.Key.Sender == sender) { _expired.Add(entry.Key); }
                }
                for (int i = 0; i < _expired.Count; i++) { _floods.Remove(_expired[i]); }
            }

            internal void Clear() {
                _floods.Clear();
                _expired.Clear();
                _lastPrune = 0f;
            }
        }

        // -- live --------------------------------------------------------------------------

        private static readonly Table Local = new Table();
        private static readonly Meter LocalMeter = new Meter();
        private static readonly Table Relayed = new Table();
        private static readonly Meter RelayMeter = new Meter();

        // Since start. Main thread only, like the calls they count.
        internal static long LocalAsked;     // requests made here for a character this machine does not own
        internal static long LocalHeld;      // of those, repeats that were not sent
        internal static long RelayAsked;     // host: requests arriving from players
        internal static long RelayHeld;      // host: of those, repeats that were not passed on
        internal static int FloodsLogged;
        internal static string LastFlood;    // the last one logged, for nps_stats

        /// <summary>The hooks are in. Counting and the flood log run whenever this is true.</summary>
        internal static bool Hooked => PatchGuard.IsActive(Mechanism.StatusEffectRepeats);

        /// <summary>Repeats are being held back as well as counted.</summary>
        internal static bool Limiting =>
            Hooked
            && ValConfig.LimitRepeatedStatusEffects != null
            && ValConfig.LimitRepeatedStatusEffects.Value;

        /// <summary>
        /// From the prefix on SEMan.AddStatusEffect, for a character this machine does not own:
        /// whether the request may go out. Never throws - it sits under every status effect any
        /// mod applies.
        /// </summary>
        internal static bool AllowLocal(ZDOID target, int effect, bool resetTime, int itemLevel, float skillLevel, int variant, float now) {
            LocalAsked++;
            bool repeat = !Local.Pass(new Key(0L, target, effect), resetTime, itemLevel, skillLevel, variant, now, HoldFor(effect, resetTime, now));
            bool held = repeat && Limiting;
            if (held) { LocalHeld++; }

            if (LocalMeter.Note(0L, effect, target, held, now, out Report report)) { ReportLocal(report); }
            return !held;
        }

        /// <summary>
        /// From the prefix on ZRoutedRpc.RPC_RoutedRPC, on the host: whether a routed RPC a player
        /// has sent is a repeated RPC_AddStatusEffect that should go no further. Reads the
        /// message where it lies and puts the read position back; anything it cannot read as the
        /// game's own RPC_AddStatusEffect is not its business.
        /// </summary>
        internal static bool ShouldDropIncoming(ZPackage pkg) {
            if (!Hooked || pkg == null) { return false; }
            if (!TryReadRequest(pkg, out long sender, out ZDOID target, out int effect, out bool resetTime,
                                out int itemLevel, out float skillLevel, out int variant)) {
                return false;
            }
            return !AllowRelay(sender, target, effect, resetTime, itemLevel, skillLevel, variant, UnityEngine.Time.time);
        }

        /// <summary>
        /// Reads a routed RPC as the game's RPC_AddStatusEffect, if that is what it is, and leaves
        /// the package's read position where it found it. No Unity calls, so it runs offline.
        /// </summary>
        internal static bool TryReadRequest(ZPackage pkg, out long sender, out ZDOID target, out int effect, out bool resetTime,
                                            out int itemLevel, out float skillLevel, out int variant) {
            sender = 0L;
            target = ZDOID.None;
            effect = 0;
            resetTime = false;
            itemLevel = 0;
            skillLevel = 0f;
            variant = 0;

            int saved = pkg.GetPos();
            try {
                pkg.ReadLong();                                              // m_msgID
                sender = pkg.ReadLong();
                pkg.ReadLong();                                              // m_targetPeerID
                target = pkg.ReadZDOID();
                if (pkg.ReadInt() != AddStatusEffectHash || target.IsNone()) { return false; }
                if (pkg.ReadInt() != ArgumentBytes) { return false; }

                effect = pkg.ReadInt();
                resetTime = pkg.ReadBool();
                itemLevel = pkg.ReadInt();
                skillLevel = pkg.ReadSingle();
                variant = pkg.ReadInt();
                return effect != 0;
            } catch (Exception) {
                // Shorter than its header says, or not laid out as expected. Vanilla's to handle.
                return false;
            } finally {
                pkg.SetPos(saved);
            }
        }

        /// <summary>The host's half of the rule, apart from the package read so it runs offline.</summary>
        internal static bool AllowRelay(long sender, ZDOID target, int effect, bool resetTime, int itemLevel, float skillLevel, int variant, float now) {
            RelayAsked++;
            bool repeat = !Relayed.Pass(new Key(sender, target, effect), resetTime, itemLevel, skillLevel, variant, now, HoldFor(effect, resetTime, now));
            bool held = repeat && Limiting;
            if (held) { RelayHeld++; }

            if (RelayMeter.Note(sender, effect, target, held, now, out Report report)) { ReportRelay(report); }
            return !held;
        }

        // -- effect durations ----------------------------------------------------------------

        /// <summary>Each effect's duration as this machine's item database gives it, looked up
        /// once: ObjectDB.GetStatusEffect walks the whole list, and this runs for every request in
        /// a flood. An effect it does not have is asked about again after UnknownRetrySeconds.</summary>
        private static readonly Dictionary<int, float> KnownDurations = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> UnknownSince = new Dictionary<int, float>();

        private static float HoldFor(int effect, bool resetTime, float now) {
            if (!resetTime) { return RepeatSeconds; }
            bool known = TryGetDuration(effect, now, out float duration);
            return HoldSecondsFor(true, known, duration);
        }

        private static bool TryGetDuration(int effect, float now, out float duration) {
            if (KnownDurations.TryGetValue(effect, out duration)) { return true; }
            duration = 0f;
            if (UnknownSince.TryGetValue(effect, out float since) && now >= since && now - since < UnknownRetrySeconds) { return false; }

            try {
                StatusEffect found = ObjectDB.instance != null ? ObjectDB.instance.GetStatusEffect(effect) : null;
                if (found != null) {
                    duration = found.m_ttl;
                    KnownDurations[effect] = duration;
                    UnknownSince.Remove(effect);
                    return true;
                }
            } catch (Exception) {
                // treated as unknown, which keeps the short hold
            }
            UnknownSince[effect] = now;
            return false;
        }

        /// <summary>The effects asked for on other players' creatures so far and how long their
        /// repeats are held, for nps_stats. Up to four, longest hold first.</summary>
        internal static string DescribeHolds() {
            if (KnownDurations.Count == 0) { return null; }
            List<KeyValuePair<int, float>> entries = new List<KeyValuePair<int, float>>(KnownDurations);
            entries.Sort((a, b) => HoldSecondsFor(true, true, b.Value).CompareTo(HoldSecondsFor(true, true, a.Value)));
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < entries.Count && i < 4; i++) {
                if (i > 0) { sb.Append(", "); }
                float hold = HoldSecondsFor(true, true, entries[i].Value);
                string lasts = entries[i].Value > 0f ? $"lasts {entries[i].Value:0.#}s" : "never runs out";
                sb.Append($"{EffectName(entries[i].Key)} every {hold:0.##}s ({lasts})");
            }
            if (entries.Count > 4) { sb.Append($", and {entries.Count - 4} more"); }
            return sb.ToString();
        }

        // -- the log -----------------------------------------------------------------------

        private static void ReportLocal(Report report) {
            FloodsLogged++;
            try {
                string effect = EffectName(report.Effect);
                string target = TargetName(report.Target);
                string outcome = report.Held > 0
                    ? $"{report.Held} were repeats and were not sent"
                    : "all were sent (Limit Repeated Status Effects is off)";
                string areas = AreasCarrying(report.Effect);
                LastFlood = $"{effect} on {target}: {report.Asked} in one second";

                Logger.LogWarning(
                    $"This game asked for the status effect {effect} on {target} {report.Asked} times in one second. Another player " +
                    $"simulates it, so each request is a message to them through the server; {outcome}." +
                    (areas.Length > 0 ? " " + areas : "") +
                    $" Asked from: {Callers()}");
            } catch (Exception e) {
                Logger.LogWarning($"A status effect is being asked for over {FloodPerSecond} times a second on a character this game does not own ({e.GetType().Name} while describing it).");
            }
        }

        private static void ReportRelay(Report report) {
            FloodsLogged++;
            try {
                string effect = EffectName(report.Effect);
                string target = TargetName(report.Target);
                string who = PlayerName(report.Sender);
                string outcome = report.Held > 0
                    ? $"{report.Held} were repeats and were not passed on"
                    : "all were passed on (Limit Repeated Status Effects is off)";
                LastFlood = $"{who}: {effect} on {target}, {report.Asked} in one second";

                Logger.LogWarning(
                    $"{who} sent {report.Asked} requests in one second for the status effect {effect} on {target}, which another player " +
                    $"simulates; {outcome}. Something on their game keeps applying it to a creature it no longer owns - usually an effect " +
                    $"area a mod has given a status effect. Their own upload is full of these until they run a version of this mod that holds them back.");

                if (Monitoring.Active) { Monitoring.OnStatusEffectFlood(report.Sender, effect, report.Target, report.Asked, report.Held); }
            } catch (Exception e) {
                Logger.LogWarning($"A player is sending over {FloodPerSecond} status effect requests a second ({e.GetType().Name} while describing it).");
            }
        }

        // Each name is looked up on its own, and falls back to the number it stands for: a report
        // that could not name the prefab must still say which effect, and the other way round.

        /// <summary>The effect's name from the game's own table, which holds every mod's that is
        /// installed on this machine. One the host cannot name belongs to a mod only that player runs.</summary>
        private static string EffectName(int hash) {
            try {
                StatusEffect effect = ObjectDB.instance != null ? ObjectDB.instance.GetStatusEffect(hash) : null;
                return effect != null
                    ? $"'{effect.name}'"
                    : $"#{hash} (not a status effect this machine knows, so it comes from a mod that is not installed here)";
            } catch (Exception) {
                return $"#{hash}";
            }
        }

        private static string TargetName(ZDOID target) {
            try {
                ZDO zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(target) : null;
                if (zdo != null) { return $"{Monitoring.PrefabName(zdo)} {Monitoring.ZdoId(target)}"; }
            } catch (Exception) {
                // falls through to the id alone
            }
            return $"object {Monitoring.ZdoId(target)}";
        }

        private static string PlayerName(long uid) {
            try {
                ZNetPeer peer = ZNet.instance != null ? ZNet.instance.GetPeer(uid) : null;
                if (peer != null && !string.IsNullOrEmpty(peer.m_playerName)) { return peer.m_playerName; }
            } catch (Exception) {
                // falls through to the id alone
            }
            return $"Peer {uid}";
        }

        /// <summary>Which loaded effect areas apply this effect, and what they are on. Not inlined:
        /// it reads EffectArea's private fields, and a build without them must fail here, into the
        /// catch, rather than fail the report.</summary>
        private static string AreasCarrying(int effect) {
            try {
                return AreasCarryingUnchecked(effect);
            } catch (Exception) {
                return "";
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string AreasCarryingUnchecked(int effect) {
            List<EffectArea> areas = EffectArea.s_allAreas;
            int count = 0;
            List<string> on = new List<string>();
            for (int i = 0; i < areas.Count; i++) {
                EffectArea area = areas[i];
                if (area == null || area.m_statusEffectHash != effect) { continue; }
                count++;
                if (on.Count >= 4) { continue; }

                ZNetView view = area.GetComponentInParent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                string name = zdo != null ? Monitoring.PrefabName(zdo) : area.transform.root.name;
                if (!on.Contains(name)) { on.Add(name); }
            }

            if (count == 0) { return "No effect area loaded here carries it, so it is being applied directly by a mod."; }
            return $"{count} effect area(s) loaded here carry it, on: {string.Join(", ", on.ToArray())}.";
        }

        /// <summary>What called SEMan.AddStatusEffect, nearest first, past this mod's own frames.</summary>
        private static string Callers() {
            try {
                return CallersUnchecked();
            } catch (Exception) {
                return "(could not read the call stack)";
            }
        }

        private static string CallersUnchecked() {
            StackTrace trace = new StackTrace(false);
            StringBuilder sb = new StringBuilder();
            int listed = 0;
            for (int i = 0; i < trace.FrameCount && listed < 5; i++) {
                System.Reflection.MethodBase method = trace.GetFrame(i)?.GetMethod();
                if (method == null) { continue; }

                string type = method.DeclaringType != null ? method.DeclaringType.FullName : null;
                string name = type != null ? type + "." + method.Name : method.Name;
                if (name.Contains("StatusEffectRepeats") || name.Contains("SEMan")) { continue; }

                if (listed > 0) { sb.Append(" < "); }
                sb.Append(name);
                listed++;
            }
            return listed > 0 ? sb.ToString() : "(no caller on the stack)";
        }

        // -- lifecycle ---------------------------------------------------------------------

        internal static void ForgetPeer(long uid) {
            Relayed.Forget(uid);
            RelayMeter.Forget(uid);
        }

        internal static void Reset() {
            Local.Clear();
            LocalMeter.Clear();
            Relayed.Clear();
            RelayMeter.Clear();
            KnownDurations.Clear();
            UnknownSince.Clear();
            LocalAsked = 0;
            LocalHeld = 0;
            RelayAsked = 0;
            RelayHeld = 0;
            FloodsLogged = 0;
            LastFlood = null;
        }
    }
}
