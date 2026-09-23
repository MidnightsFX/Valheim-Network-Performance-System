using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M23 - the host reads each player's reference position from their character.
    ///
    /// peer.m_refPos is where the host believes a player is, and nearly everything that decides
    /// what that player experiences reads it: which objects are sent to them (CreateSyncList), who
    /// is given the unowned objects around them (ReleaseNearbyZDOS, and M3's candidates - creature
    /// proximity and interactive placement both measure from it), which zones get generated
    /// (ZoneSystem.CreateGhostZones), which relayed RPCs reach them (M7) and where their pin sits
    /// on everyone's map. Vanilla refreshes it from ServerSyncedPlayerData, every 2 seconds - at
    /// sprint speed that is a position 10m out of date, on a longship 20m or more.
    ///
    /// Every client's character, though, already reaches the host with each of that client's own
    /// object updates - twenty or thirty times a second - whether or not it runs anything. So the
    /// host has a live position for every player and only needs to know when not to use it:
    ///
    ///   * No character. Before the first spawn m_characterID is None, and on respawn the game
    ///     destroys the old body and sends None (Game.RequestRespawn) while it points the reference
    ///     at the spawn point it is loading. The report is the only position there is.
    ///   * The client deliberately points somewhere else. Game.FindSpawnPoint, Tracker and camera
    ///     mods all set a reference that is not the body. That is decided when a report arrives,
    ///     by comparing it with the body at that moment - the two travel on the same ordered
    ///     connection, so they are close in time and any real difference is deliberate - and the
    ///     verdict holds until the next report. Deciding it per frame instead would read ordinary
    ///     movement since the last report as "pointing elsewhere": anything faster than ~30 m/s
    ///     would flip between the two, and a portal jump would throw the live position away at
    ///     exactly the moment it matters.
    ///   * The id is not really theirs. RPC_CharacterID stores whatever the client sends, so the
    ///     body must be owned by that peer - a client always simulates its own character.
    ///
    /// Anything that changes m_refPos between our writes - vanilla's report, another mod's - is
    /// treated as a report, so the last word stays with whoever set it on purpose.
    /// </summary>
    internal static class LiveRefPos {

        internal enum Source : byte {
            /// <summary>Following the character.</summary>
            Character,
            /// <summary>Using the player's report: they have no usable character.</summary>
            NoCharacter,
            /// <summary>Using the player's report: when it arrived it pointed away from the character.</summary>
            PointsElsewhere,
        }

        /// <summary>One zone. A client reports its reference position at least every 2 seconds, and
        /// nothing in the vanilla game moves a player 64m in that time except a teleport - which
        /// moves the body too, so body and report agree again by the next report.</summary>
        internal const float DivergenceLimitMeters = 64f;

        /// <summary>Anything further from the origin than this is not a position in a Valheim
        /// world (radius 10 000m plus margin). A NaN or an out-of-range float turns into an
        /// int.MinValue zone and a sector scan over overflowed coordinates.</summary>
        internal const float MaxCoordinate = 12000f;

        internal sealed class PeerState {
            internal Vector3 Reported;
            internal bool DivergedAtReport;
            internal ZDOID CharacterAtReport;
            internal Vector3 LastWritten;
            internal bool HasWritten;
            internal Source Current;
            /// <summary>Distance between the character and the player's last report, sampled every
            /// frame the character is followed: how far off the host's position would otherwise
            /// have been.</summary>
            internal double BehindSum;
            internal int BehindSamples;
            internal float BehindPeak;
            internal int ReportsElsewhere;

            internal float BehindMean => BehindSamples > 0 ? (float)(BehindSum / BehindSamples) : 0f;
        }

        private static readonly Dictionary<long, PeerState> States = new Dictionary<long, PeerState>();

        /// <summary>Whether the last Refresh was running, so switching it off can hand every peer
        /// its own report back at once instead of leaving a live position frozen in place.</summary>
        private static bool _wasActive;

        internal static bool Active =>
            PatchGuard.IsActive(Mechanism.LiveRefPos) && ValConfig.UseCharacterRefPos.Value;

        internal static bool TryGetState(long peerUid, out PeerState state) {
            return States.TryGetValue(peerUid, out state);
        }

        // -- pure --------------------------------------------------------------------------

        /// <summary>A usable world position: finite and inside the world. Client-supplied values
        /// are checked with this before they can reach ZoneSystem.GetZone.</summary>
        internal static bool IsPlausible(Vector3 p) {
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)) { return false; }
            if (float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z)) { return false; }
            return Mathf.Abs(p.x) <= MaxCoordinate
                && Mathf.Abs(p.y) <= MaxCoordinate
                && Mathf.Abs(p.z) <= MaxCoordinate;
        }

        /// <summary>Plausible, and not exactly the origin - the "no position yet" value the game
        /// starts every peer at. A body is never there; a report can be, before the spawn point is
        /// chosen, and then it means "nothing yet" rather than a place.</summary>
        internal static bool IsPlausibleBody(Vector3 p) {
            return IsPlausible(p) && !(p.x == 0f && p.y == 0f && p.z == 0f);
        }

        /// <summary>Whether a report points away from the body. A report that is not a real
        /// position (zero, garbage) never counts: it is "no position", not "somewhere else".</summary>
        internal static bool Diverged(bool hasBody, Vector3 body, Vector3 reported, float limitMeters) {
            if (!hasBody || !IsPlausibleBody(body)) { return false; }
            if (!IsPlausibleBody(reported)) { return false; }
            return (body - reported).sqrMagnitude > limitMeters * limitMeters;
        }

        /// <summary>Exactly the same position, bit for bit. Not Vector3 ==, which is approximate and
        /// would miss a small deliberate change, and not Vector3.Equals, which compares with == per
        /// component and so never matches a NaN - a garbage report would then read as a fresh one
        /// every frame.</summary>
        internal static bool Same(Vector3 a, Vector3 b) {
            return a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z);
        }

        /// <summary>The position the host should use.</summary>
        internal static Vector3 Choose(bool hasBody, Vector3 body, Vector3 reported, bool divergedAtReport, out Source source) {
            if (!hasBody || !IsPlausibleBody(body)) {
                source = Source.NoCharacter;
                return reported;
            }
            if (divergedAtReport) {
                source = Source.PointsElsewhere;
                return reported;
            }
            source = Source.Character;
            return body;
        }

        // -- live --------------------------------------------------------------------------

        /// <summary>
        /// Once per frame on the host, from a ZDOMan.Update prefix: after UpdatePeers has handled
        /// this frame's messages (reports and object updates alike), before ReleaseZDOS and the
        /// sends that read the result.
        /// </summary>
        internal static void Refresh() {
            ZNet net = ZNet.instance;
            if (net == null) { return; }
            Refresh(ZDOMan.s_instance, net.GetPeers());
        }

        /// <summary>The refresh itself, given the peer list. Split from the ZNet lookup so it runs
        /// without a live ZNet - a Unity object, which cannot exist outside the game.</summary>
        internal static void Refresh(ZDOMan man, List<ZNetPeer> peers) {
            if (peers == null) { return; }
            if (!Active) {
                if (_wasActive) { RestoreAll(peers); }
                _wasActive = false;
                return;
            }
            _wasActive = true;

            if (man == null) { return; }

            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (peer == null || !peer.IsReady()) { continue; }

                PeerState state = GetOrCreate(man, peer);
                if (state.HasWritten && !Same(peer.m_refPos, state.LastWritten)) {
                    // Somebody set it since our last write - vanilla's 2 second report arriving
                    // through a path we do not hook, or another mod.
                    NoteReport(man, peer, state);
                }
                Apply(man, peer, state);
            }
        }

        /// <summary>
        /// A report has just been written into peer.m_refPos (vanilla's ServerSyncedPlayerData).
        /// Recorded, and the choice re-applied straight away so the raw report is never what the
        /// rest of the frame sees - SendPlayerList runs before ZDOMan.Update.
        /// </summary>
        internal static void NoteReported(ZNetPeer peer) {
            if (!Active || peer == null || !peer.IsReady()) { return; }
            ZDOMan man = ZDOMan.s_instance;
            if (man == null) { return; }

            bool existed = States.ContainsKey(peer.m_uid);
            PeerState state = GetOrCreate(man, peer);                          // a new state has just noted this report
            if (existed) { NoteReport(man, peer, state); }
            Apply(man, peer, state);
        }

        private static PeerState GetOrCreate(ZDOMan man, ZNetPeer peer) {
            if (!States.TryGetValue(peer.m_uid, out PeerState state)) {
                state = new PeerState();
                States[peer.m_uid] = state;
                // First sight: whatever is in m_refPos now is the client's own - the PeerInfo
                // value, or a report - because we have never written it.
                NoteReport(man, peer, state);
            }
            return state;
        }

        private static void NoteReport(ZDOMan man, ZNetPeer peer, PeerState state) {
            state.Reported = peer.m_refPos;
            bool hasBody = TryGetBody(man, peer, out Vector3 body);
            state.DivergedAtReport = Diverged(hasBody, body, state.Reported, DivergenceLimitMeters);
            state.CharacterAtReport = peer.m_characterID;
            if (state.DivergedAtReport) { state.ReportsElsewhere++; }
        }

        private static void Apply(ZDOMan man, ZNetPeer peer, PeerState state) {
            // A different character than the one the last report was judged against (a respawn,
            // most often): that verdict was about a body that no longer exists.
            if (!peer.m_characterID.Equals(state.CharacterAtReport)) {
                state.DivergedAtReport = false;
                state.CharacterAtReport = peer.m_characterID;
            }

            bool hasBody = TryGetBody(man, peer, out Vector3 body);
            Vector3 chosen = Choose(hasBody, body, state.Reported, state.DivergedAtReport, out Source source);
            state.Current = source;

            if (source == Source.Character && IsPlausibleBody(state.Reported)) {
                float behind = Vector3.Distance(body, state.Reported);
                state.BehindSum += behind;
                state.BehindSamples++;
                if (behind > state.BehindPeak) { state.BehindPeak = behind; }
            }

            peer.m_refPos = chosen;
            state.LastWritten = chosen;
            state.HasWritten = true;
        }

        private static bool TryGetBody(ZDOMan man, ZNetPeer peer, out Vector3 body) {
            body = Vector3.zero;
            ZDOID id = peer.m_characterID;
            if (id.IsNone()) { return false; }

            ZDO zdo = man.GetZDO(id);
            if (zdo == null) { return false; }
            if (zdo.GetOwner() != peer.m_uid) { return false; }                // not this peer's own character

            body = zdo.GetPosition();
            return IsPlausibleBody(body);
        }

        /// <summary>Hands every peer its own last report back, where we are the last writer.</summary>
        private static void RestoreAll(List<ZNetPeer> peers) {
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (peer == null || !States.TryGetValue(peer.m_uid, out PeerState state)) { continue; }
                if (state.HasWritten && Same(peer.m_refPos, state.LastWritten)) {
                    peer.m_refPos = state.Reported;
                }
            }
            States.Clear();
        }

        internal static void ForgetPeer(long peerUid) {
            States.Remove(peerUid);
        }

        internal static void Reset() {
            States.Clear();
            _wasActive = false;
        }
    }
}
