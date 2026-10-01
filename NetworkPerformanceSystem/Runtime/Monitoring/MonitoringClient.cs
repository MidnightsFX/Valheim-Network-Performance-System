using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// What only the machine involved can see. The host knows who it thinks owns a creature; it
    /// does not know when each client's copy caught up, whether the new owner had the creature
    /// loaded, what the creature was doing when it was taken away, who it was fighting, or how
    /// far it jumped on somebody else's screen. Those are the things players actually complain
    /// about, and they are all here.
    ///
    /// "Client" in the name means these are the c_ records, not that only clients produce them.
    /// A host is a simulator too - a dedicated server owns whatever is contested around the world
    /// origin - so it records the same things about the creatures it owns, and hands them to its
    /// own writer through the same batch path, minus the network. Only the on-screen record
    /// (c_snap) is limited to machines somebody is actually looking through.
    ///
    /// Every record kind starts "c_" and carries "ct", this machine's own clock in ms. The host
    /// stamps each upload with its receive time and the sender's RTT, and the two clocks are
    /// reconciled offline.
    ///
    /// The owner-change records are built a moment late on purpose. The hook that notices the
    /// change runs inside packet deserialisation, before the packet's position has been applied,
    /// which is no place to go looking up GameObjects. It queues the change, and Tick - the same
    /// frame, still ahead of the LateUpdate in which ZSyncTransform.OwnerSync snaps a newly
    /// owned object to its ZDO - fills in the detail.
    /// </summary>
    internal static class MonitoringClient {

        private const double ReacquirePollMs = 100d;
        private const double ReacquireGiveUpMs = 10000d;
        private const double TargetPollMs = 1000d;
        private const double SelfReportMs = 5000d;
        private const double SnapRepeatMs = 1000d;
        private const double SnapTableTtlMs = 60000d;

        /// <summary>
        /// How the position corrections are recorded, and why in two shapes.
        ///
        /// A correction is worth a record of its own only when it can be tied to a cause, and the
        /// one cause this module can name is a change of owner: for a few seconds after a creature
        /// changes hands, every jump it makes on this screen is part of that handoff's cost. So
        /// corrections within SnapDetailAfterFlipMs of the creature's last owner change are kept
        /// singly, with the time since the change, one per creature per second.
        ///
        /// Every other correction is the ordinary texture of remote simulation, and what the
        /// analysis wants from those is a rate per owner - how often creatures simulated by a
        /// given machine jump on other people's screens, against that machine's RTT, jitter and
        /// frame rate. Recorded singly they were the largest thing a client produced (one line per
        /// remote creature per second in a busy fight, well over the upload budget), so they are
        /// summed per owner over SnapWindowMs and written as one line per owner per window,
        /// only for windows in which something jumped.
        /// </summary>
        private const double SnapDetailAfterFlipMs = 10000d;
        private const double SnapWindowMs = 5000d;

        /// <summary>One FixedUpdate of vanilla's smoothing moves a fifth of the error, so a step
        /// this size means the object was about 1.5m from where it should have been - beyond what
        /// any creature covers in a tick by running.</summary>
        internal const float SnapStepMetres = 0.3f;
        internal const float SnapStepMetresSq = SnapStepMetres * SnapStepMetres;

        /// <summary>At and beyond this vanilla stops smoothing and teleports.</summary>
        private const float TeleportMetres = 5f;

        private struct Flip {
            internal ZDOID Uid;
            internal long From;
            internal long To;
            internal bool ViaPacket;
            internal double AtMs;
        }

        private sealed class Reacquire {
            internal double GainedMs;
            internal double InstanceMs = -1d;
        }

        private struct TargetSeen {
            internal long TargetUser;
            internal int Generation;
        }

        private sealed class SnapWindow {
            internal int Count;
            internal int Teleports;
            internal float SumMetres;
            internal float MaxMetres;
            internal readonly HashSet<ZDOID> Creatures = new HashSet<ZDOID>();

            internal void Clear() {
                Count = 0;
                Teleports = 0;
                SumMetres = 0f;
                MaxMetres = 0f;
                Creatures.Clear();
            }
        }

        private static readonly List<Flip> Flips = new List<Flip>();
        private static readonly Dictionary<ZDOID, Reacquire> Reacquiring = new Dictionary<ZDOID, Reacquire>();
        private static readonly Dictionary<ZDOID, TargetSeen> Targets = new Dictionary<ZDOID, TargetSeen>();
        private static readonly Dictionary<ZDOID, double> LastSnapMs = new Dictionary<ZDOID, double>();
        private static readonly List<ZDOID> Scratch = new List<ZDOID>();

        /// <summary>When this client last saw each creature change owner, whatever the change
        /// was. Kept for every flip, including the ones that get no record of their own: it is
        /// the join key that decides whether a correction is part of a handoff.</summary>
        private static readonly Dictionary<ZDOID, double> LastFlipMs = new Dictionary<ZDOID, double>();
        private static readonly Dictionary<long, SnapWindow> SnapsByOwner = new Dictionary<long, SnapWindow>();

        private static double _lastReacquirePollMs;
        private static double _lastTargetPollMs;
        private static double _lastSelfReportMs;
        private static double _lastSnapPruneMs;
        private static double _lastSnapWindowMs;
        private static int _targetGeneration;
        private static int _ownedCreatures;

        private static int _frames;
        private static float _frameSeconds;
        private static float _worstFrameSeconds;

        /// <summary>
        /// How often the creatures simulated here change, which is how often each of them is sent
        /// whole to the host and on to everyone in range. Sampled once a second: each owned
        /// creature's DataRevision against the previous sample, summed over the creatures owned at
        /// both. Two tables swapped each sample, so it allocates nothing once warm.
        /// </summary>
        private static Dictionary<ZDOID, uint> _revisionsBefore = new Dictionary<ZDOID, uint>();
        private static Dictionary<ZDOID, uint> _revisionsNow = new Dictionary<ZDOID, uint>();
        private static long _creatureRevisionSteps;
        private static double _creatureRevisionMs;
        private static double _lastRevisionSampleMs;
        private static int _creaturesSampled;

        // QuietCreatures' running totals at the last c_self, so each record carries its window.
        private static long _quietPositionAtReport;
        private static long _quietVelocityAtReport;
        private static long _quietRigidbodyAtReport;
        private static long _quietBodyAtReport;
        private static long _quietTiltAtReport;
        private static long _quietRotationAtReport;
        private static long _quietAngularAtReport;
        private static long _quietAnimatorAtReport;
        private static long _quietSkippedAtReport;
        private static long _fightingMovedAtReport;
        private static long _wildHeldAtReport;
        private static long _wildPassedAtReport;

        internal static void OnStarted() {
            Monitoring.EmitClient(Monitoring.Line.Begin("c_session")
                .Num("ct", Monitoring.NowMs, "0.#")
                .Str("utc", System.DateTime.UtcNow.ToString("o"))
                .Str("mod", NetworkPerformanceSystem.PluginVersion)
                .Id("uid", NpsEnv.LocalSessionId())
                .Flag("host", Monitoring.ServerRole)
                // Both change what rubber-banding looks like from here, so a reader has to know.
                .Flag("comp", ValConfig.EnableLatencyCompensation.Value)
                .Num("compStrength", ValConfig.LatencyCompensationStrength.Value, "0.##")
                .End());
            MarkQuietTotals();
            TrafficNames.ReportAllAgain();
        }

        // -- hooks -------------------------------------------------------------------------

        internal static void OnOwnerFlip(ZDO zdo, long from, long to, bool viaPacket) {
            Flips.Add(new Flip { Uid = zdo.m_uid, From = from, To = to, ViaPacket = viaPacket, AtMs = Monitoring.NowMs });
        }

        /// <summary>Character.RPC_Damage arrived here. Applied if this machine owns the target,
        /// silently discarded by the game if not - which is the record this exists for.</summary>
        internal static void OnDamageReceived(Character target, long sender) {
            ZNetView view = target.m_nview;
            ZDO zdo = view != null ? view.GetZDO() : null;
            if (zdo == null) { return; }

            long self = NpsEnv.LocalSessionId();
            bool applied = zdo.IsOwner();
            Monitoring.EmitClient(Monitoring.Line.Begin("c_hit")
                .Num("ct", Monitoring.NowMs, "0.#")
                .Str("zdo", Monitoring.ZdoId(zdo.m_uid))
                .Id("sender", sender)
                .Flag("applied", applied)
                .Flag("local", sender == self)
                .Flag("player", target.IsPlayer())
                .Id("owner", applied ? self : zdo.GetOwner())
                .End());
        }

        /// <summary>Character.Damage was called here: this machine detected a hit and is about to
        /// address it to whoever its copy of the ZDO says is the owner.</summary>
        internal static void OnDamageSent(Character target) {
            ZNetView view = target.m_nview;
            ZDO zdo = view != null ? view.GetZDO() : null;
            if (zdo == null) { return; }

            Monitoring.EmitClient(Monitoring.Line.Begin("c_dmg")
                .Num("ct", Monitoring.NowMs, "0.#")
                .Str("zdo", Monitoring.ZdoId(zdo.m_uid))
                .Id("to", zdo.GetOwner())
                .Flag("local", zdo.IsOwner())
                .Flag("player", target.IsPlayer())
                .End());
        }

        /// <summary>
        /// A creature this machine simulates has died. Only the owner can say how: the killing
        /// hit is kept in Character.m_lastHit on this machine and nowhere else, and the owner
        /// destroys the ZDO straight afterwards, so every other machine - the host included -
        /// only ever sees the creature vanish. A creature that despawns or grows up vanishes the
        /// same way, which is why the host cannot tell a death from its own records.
        ///
        /// The game also calls OnDeath from the death animation on machines that are only
        /// watching; those return here. "hitType" is the game's own label for the killing hit -
        /// a burning tick reads Burning, while a fire's own damage area reads EnemyHit with no
        /// attacker, so "dmg" and "se" are what tell that one apart. "sinceFlipMs" is present
        /// when the creature changed owner within the last minute, as this machine saw it.
        /// </summary>
        internal static void OnDeath(Character target) {
            ZNetView view = target.m_nview;
            if (view == null || !view.IsValid() || !view.IsOwner()) { return; }

            ZDO zdo = view.GetZDO();
            double now = Monitoring.NowMs;
            Vector3 pos = target.transform.position;
            HitData hit = target.m_lastHit;

            JsonLine line = Monitoring.Line.Begin("c_death")
                .Num("ct", now, "0.#")
                .Str("zdo", Monitoring.ZdoId(zdo.m_uid))
                .Str("prefab", Monitoring.PrefabName(zdo))
                .Num("x", Mathf.Round(pos.x), "0")
                .Num("y", Mathf.Round(pos.y), "0")
                .Num("z", Mathf.Round(pos.z), "0")
                .Flag("tamed", target.IsTamed());
            if (LastFlipMs.TryGetValue(zdo.m_uid, out double flippedAt)) {
                line.Num("sinceFlipMs", now - flippedAt, "0");
            }

            if (hit == null) {
                line.Str("hitType", "None");                                 // health went to 0 without a hit
            } else {
                line.Str("hitType", hit.m_hitType.ToString())
                    .Raw("dmg", DescribeDamage(hit.m_damage));

                if (!hit.m_attacker.IsNone()) {
                    line.Str("attacker", Monitoring.ZdoId(hit.m_attacker));
                    ZDO attacker = ZDOMan.instance.GetZDO(hit.m_attacker);
                    if (attacker != null) { line.Str("attackerPrefab", Monitoring.PrefabName(attacker)); }
                }

                StatusEffect effect = hit.m_statusEffectHash != 0 && ObjectDB.instance != null
                    ? ObjectDB.instance.GetStatusEffect(hit.m_statusEffectHash)
                    : null;
                if (effect != null) { line.Str("se", effect.name); }
            }

            Monitoring.EmitClient(line.End());
        }

        private static readonly System.Text.StringBuilder DamageScratch = new System.Text.StringBuilder(128);

        /// <summary>The killing hit's damage as applied, one member per type that did any. Pure.</summary>
        internal static string DescribeDamage(HitData.DamageTypes damage) {
            System.Text.StringBuilder sb = DamageScratch;
            sb.Length = 0;
            sb.Append('{');
            AppendDamage(sb, "damage", damage.m_damage);
            AppendDamage(sb, "blunt", damage.m_blunt);
            AppendDamage(sb, "slash", damage.m_slash);
            AppendDamage(sb, "pierce", damage.m_pierce);
            AppendDamage(sb, "chop", damage.m_chop);
            AppendDamage(sb, "pickaxe", damage.m_pickaxe);
            AppendDamage(sb, "fire", damage.m_fire);
            AppendDamage(sb, "frost", damage.m_frost);
            AppendDamage(sb, "lightning", damage.m_lightning);
            AppendDamage(sb, "poison", damage.m_poison);
            AppendDamage(sb, "spirit", damage.m_spirit);
            AppendDamage(sb, "nonPlayer", damage.m_nonPlayer);
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendDamage(System.Text.StringBuilder sb, string key, float value) {
            if (!(value > 0f) || float.IsInfinity(value)) { return; }       // NaN fails the first test
            if (sb.Length > 1) { sb.Append(','); }
            sb.Append('"').Append(key).Append("\":")
              .Append(value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>A creature somebody else owns moved further in one tick than smoothing
        /// accounts for. Singly if it changed hands recently, otherwise into its owner's window;
        /// see SnapDetailAfterFlipMs.</summary>
        internal static void OnSnap(ZSyncTransform sync, ZDO zdo, float stepMetres) {
            double now = Monitoring.NowMs;
            ZDOID uid = zdo.m_uid;
            long owner = zdo.GetOwner();

            if (LastFlipMs.TryGetValue(uid, out double flippedAt) && now - flippedAt < SnapDetailAfterFlipMs) {
                if (LastSnapMs.TryGetValue(uid, out double last) && now - last < SnapRepeatMs) { return; }
                LastSnapMs[uid] = now;

                Monitoring.EmitClient(Monitoring.Line.Begin("c_snap")
                    .Num("ct", now, "0.#")
                    .Str("zdo", Monitoring.ZdoId(uid))
                    .Id("owner", owner)
                    .Num("stepM", stepMetres, "0.##")
                    .Flag("teleport", stepMetres >= TeleportMetres)
                    .Num("timer", sync.m_targetPosTimer, "0.##")
                    .Num("sinceFlipMs", now - flippedAt, "0")
                    .End());
                return;
            }

            if (!SnapsByOwner.TryGetValue(owner, out SnapWindow window)) {
                window = new SnapWindow();
                SnapsByOwner[owner] = window;
            }
            window.Count++;
            window.SumMetres += stepMetres;
            if (stepMetres > window.MaxMetres) { window.MaxMetres = stepMetres; }
            if (stepMetres >= TeleportMetres) { window.Teleports++; }
            window.Creatures.Add(uid);
        }

        /// <summary>One line per owner whose creatures jumped on this screen this window.</summary>
        private static void FlushSnapWindows(double now) {
            foreach (KeyValuePair<long, SnapWindow> pair in SnapsByOwner) {
                SnapWindow window = pair.Value;
                if (window.Count == 0) { continue; }

                Monitoring.EmitClient(Monitoring.Line.Begin("c_snaps")
                    .Num("ct", now, "0.#")
                    .Id("owner", pair.Key)
                    .Num("windowMs", SnapWindowMs, "0")
                    .Int("n", window.Count)
                    .Int("creatures", window.Creatures.Count)
                    .Int("teleports", window.Teleports)
                    .Num("meanM", window.SumMetres / window.Count, "0.##")
                    .Num("maxM", window.MaxMetres, "0.##")
                    .End());
                window.Clear();
            }
        }

        // -- per frame ---------------------------------------------------------------------

        internal static void Tick(double now) {
            float dt = Time.unscaledDeltaTime;
            _frames++;
            _frameSeconds += dt;
            if (dt > _worstFrameSeconds) { _worstFrameSeconds = dt; }

            if (Flips.Count > 0) {
                for (int i = 0; i < Flips.Count; i++) { DescribeFlip(Flips[i]); }
                Flips.Clear();
            }

            if (Reacquiring.Count > 0 && now - _lastReacquirePollMs >= ReacquirePollMs) {
                _lastReacquirePollMs = now;
                PollReacquire(now);
            }

            if (now - _lastTargetPollMs >= TargetPollMs) {
                _lastTargetPollMs = now;
                PollTargets(now);
                SampleCreatureRevisions(now);
            }

            if (now - _lastSelfReportMs >= SelfReportMs) {
                _lastSelfReportMs = now;
                ReportSelf(now);
                TrafficNames.ReportLearned(now);
            }

            if (now - _lastSnapWindowMs >= SnapWindowMs) {
                _lastSnapWindowMs = now;
                FlushSnapWindows(now);
            }

            if (now - _lastSnapPruneMs >= SnapTableTtlMs) {
                _lastSnapPruneMs = now;
                Prune(LastSnapMs, now);
                Prune(LastFlipMs, now);
            }
        }

        private static void Prune(Dictionary<ZDOID, double> table, double now) {
            Scratch.Clear();
            foreach (KeyValuePair<ZDOID, double> pair in table) {
                if (now - pair.Value >= SnapTableTtlMs) { Scratch.Add(pair.Key); }
            }
            for (int i = 0; i < Scratch.Count; i++) { table.Remove(Scratch[i]); }
        }

        /// <summary>
        /// A record for an owner change this client saw, when it is one worth a record. Those
        /// are the changes this machine is a party to - it is gaining the creature and about to
        /// snap it and restart its AI, or losing it and is the only machine that knows what the
        /// creature was doing - and changes to a creature that is awake, because an alert
        /// creature is somebody's fight and when each viewer's copy caught up is part of what
        /// that handoff cost. A sleeping deer changing hands two zones away is not, and on a full
        /// server those were most of the flips a client saw.
        ///
        /// Every flip still marks LastFlipMs, so a correction on any creature can be tied to its
        /// last change of owner whether or not that change got a line of its own.
        /// </summary>
        private static void DescribeFlip(Flip flip) {
            LastFlipMs[flip.Uid] = flip.AtMs;

            long self = NpsEnv.LocalSessionId();
            ZDO zdo = ZDOMan.instance.GetZDO(flip.Uid);
            bool party = flip.To == self || flip.From == self;
            if (!party && (zdo == null || !(zdo.GetBool(ZDOVars.s_alert) || zdo.GetBool(ZDOVars.s_haveTargetHash)))) {
                return;
            }

            ZNetView view = zdo != null && ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
            Character character = view != null ? view.GetComponent<Character>() : null;

            JsonLine line = Monitoring.Line.Begin("c_owner")
                .Num("ct", flip.AtMs, "0.#")
                .Str("zdo", Monitoring.ZdoId(flip.Uid))
                .Id("from", flip.From)
                .Id("to", flip.To)
                .Flag("packet", flip.ViaPacket)
                .Flag("inst", view != null);
            if (zdo != null) {
                line.Str("prefab", Monitoring.PrefabName(zdo))
                    .Int("rev", zdo.OwnerRevision)
                    .Flag("alert", zdo.GetBool(ZDOVars.s_alert))
                    .Flag("tgt", zdo.GetBool(ZDOVars.s_haveTargetHash));
            }

            if (flip.To == self && zdo != null) {
                // How far OwnerSync is about to move it: it snaps a newly owned object to the
                // ZDO's position, discarding wherever this machine had extrapolated it to.
                if (view != null) {
                    line.Num("snapM", Vector3.Distance(view.transform.position, zdo.GetPosition()), "0.##");
                }
                if (zdo.GetBool(ZDOVars.s_haveTargetHash) || zdo.GetBool(ZDOVars.s_alert)) {
                    Reacquiring[flip.Uid] = new Reacquire { GainedMs = flip.AtMs, InstanceMs = view != null ? 0d : -1d };
                }
            } else if (flip.From == self && character != null) {
                // Everything below is local to the machine that was simulating and is not in the
                // ZDO, so the new owner starts without it. This is the only place it can be read.
                line.Flag("inAttack", character.InAttack())
                    .Flag("staggering", character.IsStaggering())
                    .Flag("grounded", character.IsOnGround())
                    .Flag("flying", character.IsFlying())
                    .Flag("swimming", character.IsSwimming())
                    .Num("speed", character.GetVelocity().magnitude, "0.#")
                    .Num("stagger", character.GetStaggerPercentage(), "0.##")
                    .Num("push", character.m_pushForce.magnitude, "0.#")
                    .Num("hp", character.GetHealthPercentage(), "0.##");

                MonsterAI ai = view.GetComponent<MonsterAI>();
                Character target = ai != null ? ai.m_targetCreature : null;
                if (target != null) {
                    line.Id("targetUser", target.GetZDOID().UserID)
                        .Flag("targetPlayer", target.IsPlayer());
                }
                Targets.Remove(flip.Uid);
                Reacquiring.Remove(flip.Uid);
            }

            Monitoring.EmitClient(line.End());
        }

        /// <summary>A creature that had a target when it arrived: how long until its AI, now
        /// running here from a standing start, has one again.</summary>
        private static void PollReacquire(double now) {
            Scratch.Clear();
            foreach (KeyValuePair<ZDOID, Reacquire> pair in Reacquiring) {
                Reacquire state = pair.Value;
                double elapsed = now - state.GainedMs;

                ZDO zdo = ZDOMan.instance.GetZDO(pair.Key);
                if (zdo == null || !zdo.IsOwner()) {
                    EmitReacquire(pair.Key, state, -1d, zdo == null ? "gone" : "lost", null);
                    Scratch.Add(pair.Key);
                    continue;
                }

                ZNetView view = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
                if (view != null && state.InstanceMs < 0d) { state.InstanceMs = elapsed; }

                MonsterAI ai = view != null ? view.GetComponent<MonsterAI>() : null;
                if (view != null && ai == null) {
                    Scratch.Add(pair.Key);                                    // not a monster; nothing to wait for
                    continue;
                }

                if (ai != null && (ai.m_targetCreature != null || ai.m_targetStatic != null)) {
                    EmitReacquire(pair.Key, state, elapsed, "ok", ai.m_targetCreature);
                    Scratch.Add(pair.Key);
                } else if (elapsed >= ReacquireGiveUpMs) {
                    EmitReacquire(pair.Key, state, -1d, view == null ? "noInstance" : "timeout", null);
                    Scratch.Add(pair.Key);
                }
            }
            for (int i = 0; i < Scratch.Count; i++) { Reacquiring.Remove(Scratch[i]); }
        }

        private static void EmitReacquire(ZDOID uid, Reacquire state, double ms, string outcome, Character target) {
            JsonLine line = Monitoring.Line.Begin("c_reacq")
                .Num("ct", Monitoring.NowMs, "0.#")
                .Str("zdo", Monitoring.ZdoId(uid))
                .Str("outcome", outcome)
                .Num("ms", ms, "0")
                .Num("instMs", state.InstanceMs, "0");
            if (target != null) {
                line.Id("targetUser", target.GetZDOID().UserID)
                    .Flag("targetPlayer", target.IsPlayer());
            }
            Monitoring.EmitClient(line.End());
        }

        /// <summary>Which player each creature simulated here is after, recorded when it changes.
        /// The owner is the only machine that knows - the ZDO carries a haveTarget flag and
        /// nothing about who.</summary>
        private static void PollTargets(double now) {
            _targetGeneration++;
            int owned = 0;

            List<BaseAI> instances = BaseAI.BaseAIInstances;
            for (int i = 0; i < instances.Count; i++) {
                MonsterAI ai = instances[i] as MonsterAI;
                if (ai == null) { continue; }

                ZNetView view = ai.m_nview;
                if (view == null || !view.IsValid() || !view.IsOwner()) { continue; }
                owned++;

                Character target = ai.m_targetCreature;
                bool targetsPlayer = target != null && target.IsPlayer();
                long targetUser = targetsPlayer ? target.GetZDOID().UserID : 0L;

                ZDOID uid = view.GetZDO().m_uid;
                bool known = Targets.TryGetValue(uid, out TargetSeen seen);
                if (!known || seen.TargetUser != targetUser) {
                    // A creature first seen with no player target says nothing worth a record.
                    if (known || targetUser != 0L) {
                        Monitoring.EmitClient(Monitoring.Line.Begin("c_target")
                            .Num("ct", now, "0.#")
                            .Str("zdo", Monitoring.ZdoId(uid))
                            .Id("targetUser", targetUser)
                            .End());
                    }
                }
                Targets[uid] = new TargetSeen { TargetUser = targetUser, Generation = _targetGeneration };
            }
            _ownedCreatures = owned;

            Scratch.Clear();
            foreach (KeyValuePair<ZDOID, TargetSeen> pair in Targets) {
                if (pair.Value.Generation != _targetGeneration) { Scratch.Add(pair.Key); }
            }
            for (int i = 0; i < Scratch.Count; i++) { Targets.Remove(Scratch[i]); }
        }

        /// <summary>Every owned AI creature, tame or wild - wider than PollTargets, which only
        /// looks at MonsterAI because only those hunt players.</summary>
        private static void SampleCreatureRevisions(double now) {
            long steps = 0;
            _revisionsNow.Clear();

            List<BaseAI> instances = BaseAI.BaseAIInstances;
            for (int i = 0; i < instances.Count; i++) {
                BaseAI ai = instances[i];
                if (ai == null) { continue; }
                ZNetView view = ai.m_nview;
                if (view == null || !view.IsValid() || !view.IsOwner()) { continue; }

                ZDO zdo = view.GetZDO();
                uint revision = zdo.DataRevision;
                if (_revisionsBefore.TryGetValue(zdo.m_uid, out uint before) && revision >= before) {
                    steps += revision - before;
                }
                _revisionsNow[zdo.m_uid] = revision;
            }

            if (_lastRevisionSampleMs > 0d) {
                _creatureRevisionSteps += steps;
                _creatureRevisionMs += now - _lastRevisionSampleMs;
            }
            _lastRevisionSampleMs = now;
            _creaturesSampled = _revisionsNow.Count;

            Dictionary<ZDOID, uint> swap = _revisionsBefore;
            _revisionsBefore = _revisionsNow;
            _revisionsNow = swap;
        }

        private static void ReportSelf(double now) {
            Vector2s zone = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());

            JsonLine line = Monitoring.Line.Begin("c_self")
                .Num("ct", now, "0.#")
                .Num("fps", _frameSeconds > 0f ? _frames / _frameSeconds : 0f, "0.#")
                .Num("worstFrameMs", _worstFrameSeconds * 1000f, "0.#")
                .Int("ownedCreatures", _ownedCreatures)
                .Int("characters", Character.GetAllCharacters().Count)
                .Int("changeQueue", ZDOMan.instance.GetClientChangeQueue())
                .Int("zx", zone.x)
                .Int("zy", zone.y);

            // Creature churn: revision steps per second over every creature simulated here, and
            // how many there were. crRevs / crN is the per-creature update rate this machine
            // offers the network.
            line.Num("crRevs", _creatureRevisionMs > 0d ? _creatureRevisionSteps * 1000d / _creatureRevisionMs : 0d, "0.#")
                .Int("crN", _creaturesSampled);
            if (AllocationRelief.TryGetZdoRates(out int zdoSent, out int zdoRecv)) {
                line.Int("zdoSent", zdoSent)
                    .Int("zdoRecv", zdoRecv);
            }

            // What QuietCreatures let through this window, per field, and what it skipped. The
            // writes it let through are a subset of crRevs; the rest of crRevs is every other
            // writer (health, noise, world time, animation...).
            line.Flag("quiet", QuietCreatures.Active)
                .Int("qpPos", Window(QuietCreatures.PositionWritten, _quietPositionAtReport))
                .Int("qpVel", Window(QuietCreatures.VelocityWritten, _quietVelocityAtReport))
                .Int("qpRb", Window(QuietCreatures.RigidbodyWritten, _quietRigidbodyAtReport))
                .Int("qpBody", Window(QuietCreatures.BodyWritten, _quietBodyAtReport))
                .Int("qpTilt", Window(QuietCreatures.TiltWritten, _quietTiltAtReport))
                .Int("qpRot", Window(QuietCreatures.RotationWritten, _quietRotationAtReport))
                .Int("qpAVel", Window(QuietCreatures.AngularWritten, _quietAngularAtReport))
                .Int("qpAnim", Window(QuietCreatures.AnimatorWritten, _quietAnimatorAtReport))
                .Int("qSkip", Window(QuietCreatures.TotalSkipped, _quietSkippedAtReport));

            // How many of this machine's fighting creatures FightingCreaturesFirst moved ahead of
            // something else in its uploads this window: non-zero means the order was contested.
            line.Int("ffMoved", Window(FightingCreaturesFirst.CreaturesMoved, _fightingMovedAtReport));

            // QuietWildlife's owner half this window: frames of fish and birds simulated here
            // that were skipped, and that went through to the game's own write path.
            line.Flag("wild", QuietWildlife.OwnerActive)
                .Int("qwHeld", Window(QuietWildlife.FramesHeld, _wildHeldAtReport))
                .Int("qwPass", Window(QuietWildlife.FramesPassed, _wildPassedAtReport));

            Monitoring.EmitClient(line.End());

            _frames = 0;
            _frameSeconds = 0f;
            _worstFrameSeconds = 0f;
            _creatureRevisionSteps = 0;
            _creatureRevisionMs = 0d;
            MarkQuietTotals();
        }

        /// <summary>A running total's growth since the last report. A total below its mark was
        /// reset in between (session end), and all of it is this window's.</summary>
        private static long Window(long total, long atReport) {
            return total >= atReport ? total - atReport : total;
        }

        private static void MarkQuietTotals() {
            _quietPositionAtReport = QuietCreatures.PositionWritten;
            _quietVelocityAtReport = QuietCreatures.VelocityWritten;
            _quietRigidbodyAtReport = QuietCreatures.RigidbodyWritten;
            _quietBodyAtReport = QuietCreatures.BodyWritten;
            _quietTiltAtReport = QuietCreatures.TiltWritten;
            _quietRotationAtReport = QuietCreatures.RotationWritten;
            _quietAngularAtReport = QuietCreatures.AngularWritten;
            _quietAnimatorAtReport = QuietCreatures.AnimatorWritten;
            _quietSkippedAtReport = QuietCreatures.TotalSkipped;
            _fightingMovedAtReport = FightingCreaturesFirst.CreaturesMoved;
            _wildHeldAtReport = QuietWildlife.FramesHeld;
            _wildPassedAtReport = QuietWildlife.FramesPassed;
        }

        internal static void Reset() {
            Flips.Clear();
            Reacquiring.Clear();
            Targets.Clear();
            LastSnapMs.Clear();
            LastFlipMs.Clear();
            SnapsByOwner.Clear();
            Scratch.Clear();
            _lastReacquirePollMs = 0d;
            _lastTargetPollMs = 0d;
            _lastSelfReportMs = 0d;
            _lastSnapPruneMs = 0d;
            _lastSnapWindowMs = 0d;
            _ownedCreatures = 0;
            _frames = 0;
            _frameSeconds = 0f;
            _worstFrameSeconds = 0f;
            _revisionsBefore.Clear();
            _revisionsNow.Clear();
            _creatureRevisionSteps = 0;
            _creatureRevisionMs = 0d;
            _lastRevisionSampleMs = 0d;
            _creaturesSampled = 0;
            // Monitoring can start mid-session with QuietCreatures' totals already running, so the
            // first window starts from wherever they stand now.
            MarkQuietTotals();
        }
    }
}
