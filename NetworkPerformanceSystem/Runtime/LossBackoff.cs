using System.Collections.Generic;
using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M26 - the host slows down for one player whose connection is losing what it is sent.
    ///
    /// Steam paces every connection at a fixed rate (Send Rate KBps, see SteamTransport) and never
    /// slows for loss: a player whose connection cannot take that rate gets packet loss and
    /// resends, at the same rate, for as long as they stay. Until now the only lever was the
    /// global rate, which is everybody's. This one is per player. Steam accepts SendRateMin and
    /// SendRateMax at Connection scope as well as Global, a connection-scope value overrides the
    /// global one for that connection alone, and removing it puts the connection back to
    /// inheriting.
    ///
    /// The signal is QualityRemote from the connection's real-time status - the share of our
    /// packets that reached the peer - which the ping postfix already reads twice a second per
    /// peer. It is smoothed with the same EWMA the RTT uses, and then:
    ///
    ///   * under Loss Backoff Threshold for Loss Backoff Hold Seconds -> the connection is stepped
    ///     down: rate = global x 0.75^steps, never below Loss Backoff Floor KBps. Another step
    ///     every Hold seconds while it stays under.
    ///   * at or above the threshold plus a two-point dead band for Loss Backoff Recover Seconds
    ///     -> one step back up. At step 0 the override is removed and the connection follows Send
    ///     Rate KBps again.
    ///
    /// Both timers run from the last step, so a connection never moves faster than Hold down or
    /// Recover up, and the dead band keeps a player hovering at the threshold from cycling.
    ///
    /// Some players lose packets whatever the rate - a bad WiFi link, a congested route - and for
    /// them the step-down is all cost: slower updates, the same loss. The first 1.8.0 recording
    /// (40 players, 13 hours) had 20 players step all the way to the 32 KB/s floor and stay there
    /// still losing - 96% of their samples under the threshold, median 80% delivered, most of them
    /// on 275-300ms links - and Recover never fired for them because the loss never cleared. So
    /// the floor is now a test. A player who reaches it and is still under the threshold a full
    /// Hold later, and delivering no better than when the back-off started (within
    /// ImprovementMargin), has shown their loss is not the rate's doing: they are put back on the
    /// global rate at once, a warning says so, and they are not stepped down again for the rest of
    /// the server's session - remembered by their platform address, because these are the players
    /// who reconnect most, and each reconnect is a new peer id. A player the floor did help, even
    /// if not all the way to the threshold, keeps it.
    ///
    /// An override is a fraction of the global rate at the moment it was written, and a
    /// connection-scope value stops following Global writes - so SteamTransport.Apply calls
    /// Reconcile after any global change and every live override is rewritten from the new rate,
    /// or removed when the mechanism is switched off. The window sizer needs nothing: it already
    /// sizes from the rate each connection reports (SendWindow.PickRate), which is the override.
    ///
    /// Host only, Steam peers only (crossplay reports no status), and only while Enable Transport
    /// Tuning is on - that setting promises vanilla transport when off, overrides included. The
    /// game-server half of Steamworks has not yet been seen to accept a connection-scope write;
    /// the first refusal stands the mechanism down for the process rather than retrying at every
    /// ping.
    /// </summary>
    internal static class LossBackoff {

        /// <summary>Each step multiplies the rate by this. A quarter at a time is coarse enough to
        /// show in delivery within a couple of holds and fine enough not to overshoot.</summary>
        internal const float StepFactor = 0.75f;

        /// <summary>Dead band above the threshold before a run counts as clean.</summary>
        internal const float RecoverMargin = 0.02f;

        /// <summary>A timer is only honoured once the run has this many samples. A stalled main
        /// thread keeps the wall clock running while no pings arrive, and its first sample back
        /// must not satisfy Hold or Recover by itself.</summary>
        internal const int MinRunSamples = 4;

        /// <summary>How much better than at the first step a player must be delivering at the floor
        /// for the back-off to count as having helped. The same two points as the recovery dead
        /// band: the smoothed figure wanders by about that much on a steady link.</summary>
        internal const float ImprovementMargin = 0.02f;

        /// <summary>Same smoothing as LatencyRegistry: settles in three or four seconds at two
        /// samples a second.</summary>
        private const float SampleAlpha = 0.25f;

        private const int BytesPerKB = 1024;

        private enum Run : byte { None, Lossy, Clean }

        private sealed class Entry {
            internal uint Handle;                                    // connection the override was written to; 0 = none
            internal float Delivered;                                // EWMA of QualityRemote
            internal bool HasSample;
            internal int Steps;                                      // 0 = inheriting the global rate
            internal int OverrideBytes;                              // 0 when Steps == 0
            internal Run RunKind;
            internal float RunSince;
            internal int RunSamples;
            internal float LastChangeAt = float.NegativeInfinity;    // any write: down, up, clear, rewrite
            internal float BackedOffSince;
            internal float DeliveredAtStart;                         // Delivered when the first step was taken
            internal bool FloorLogged;
            internal bool Exempt;                                    // loss shown not to be the rate's; never stepped again
            internal float ExemptSince;
        }

        /// <summary>What nps_stats shows for one peer.</summary>
        internal struct View {
            internal float Delivered;
            internal bool HasSample;
            internal bool Lossy;                                     // a lossy run is in progress; a step may be pending
            internal int Steps;
            internal int OverrideBytesPerSec;
            internal bool AtFloor;
            internal float BackedOffSince;
            internal bool Exempt;
            internal float ExemptSince;
        }

        private static readonly Dictionary<long, Entry> Peers = new Dictionary<long, Entry>();

        /// <summary>Platform addresses of players whose loss the floor did not help. Held for the
        /// server's session - through reconnects, which give a player a new peer id, and through the
        /// mechanism being switched off and on - and cleared only when the session ends. Kept in
        /// memory only; never logged or recorded.</summary>
        private static readonly HashSet<string> ExemptPlayers = new HashSet<string>();

        internal static int BackedOffNow { get; private set; }
        internal static long TotalStepsDown { get; private set; }
        internal static long TotalStepsUp { get; private set; }
        internal static long TotalCleared { get; private set; }
        internal static long TotalExempted { get; private set; }
        internal static int ExemptPlayerCount => ExemptPlayers.Count;

        /// <summary>Everything this needs to act: host, hooks in, tuning and this switch on, and
        /// one global rate to step down from (unequal bounds written by another mod leave none).</summary>
        internal static bool Wanted =>
            NpsEnv.IsHost()
            && PatchGuard.IsActive(Mechanism.LossBackoff)
            && PatchGuard.IsActive(Mechanism.SteamTransport)
            && ValConfig.EnableSteamTransportTuning.Value
            && ValConfig.EnableLossBackoff.Value
            && SteamTransport.PinnedSendRateBytesPerSec > 0;

        /// <summary>The configured floor in bytes/sec, never under the floor Send Rate KBps has.</summary>
        internal static int FloorBytesPerSec =>
            Mathf.Max(ValConfig.LossBackoffFloorKBps.Value, SteamTransport.MinSendRateKBps) * BytesPerKB;

        /// <summary>The rate after this many steps down from the global one: global x 0.75^steps,
        /// never below the floor - and never above the global rate, so a floor set at or over it
        /// leaves nothing to step to. Pure.</summary>
        internal static int RateFor(int pinnedBytesPerSec, int steps, int floorBytesPerSec) {
            if (pinnedBytesPerSec <= 0) { return 0; }
            float rate = pinnedBytesPerSec;
            for (int i = 0; i < steps; i++) { rate *= StepFactor; }
            int floor = Mathf.Min(floorBytesPerSec, pinnedBytesPerSec);
            return Mathf.Max(floor, Mathf.RoundToInt(rate));
        }

        /// <summary>Whether a player at the floor is delivering meaningfully more than when the
        /// back-off began - the step-downs did something, so the floor is kept. Pure.</summary>
        internal static bool Improved(float deliveredNow, float deliveredAtStart) {
            return deliveredNow >= deliveredAtStart + ImprovementMargin;
        }

        // -- the decision ------------------------------------------------------------------

        /// <summary>One delivery sample for one peer, from the ping postfix (~2/s). Negative means
        /// Steam has no figure yet and is not a sample.</summary>
        internal static void Observe(ZNetPeer peer, float qualityRemote) {
            if (peer == null || peer.m_uid == 0L || qualityRemote < 0f) { return; }
            if (!Wanted) { return; }
            if (!RttProbe.TryGetConnectionHandle(peer.m_socket, out uint handle)) { return; }

            float now = Time.realtimeSinceStartup;
            Entry e = EntryFor(peer, now);

            // A different connection than the override was written to: the peer reconnected, and
            // the old value died with the old connection. Nothing to clear.
            if (e.Handle != 0 && e.Handle != handle) { Drop(e); }

            e.Delivered = e.HasSample ? e.Delivered + (qualityRemote - e.Delivered) * SampleAlpha : qualityRemote;
            e.HasSample = true;

            float threshold = ValConfig.LossBackoffThreshold.Value;
            Run kind = e.Delivered < threshold ? Run.Lossy
                     : e.Delivered >= threshold + RecoverMargin ? Run.Clean
                     : Run.None;
            if (kind != e.RunKind) {
                e.RunKind = kind;
                e.RunSince = now;
                e.RunSamples = 0;
            }
            e.RunSamples++;

            // Still measured, so nps_stats can show what they get at full rate; never acted on.
            if (e.Exempt) { return; }
            if (kind == Run.None || e.RunSamples < MinRunSamples) { return; }

            float wait = kind == Run.Lossy ? ValConfig.LossBackoffHoldSeconds.Value : ValConfig.LossBackoffRecoverSeconds.Value;
            if (now - Mathf.Max(e.RunSince, e.LastChangeAt) < wait) { return; }

            int pinned = SteamTransport.PinnedSendRateBytesPerSec;
            int floor = FloorBytesPerSec;
            if (kind == Run.Lossy) {
                StepDown(e, peer, handle, pinned, floor, threshold, wait, now);
            } else if (e.Steps > 0) {
                StepUp(e, peer, handle, pinned, floor, wait, now);
            }
        }

        private static void StepDown(Entry e, ZNetPeer peer, uint handle, int pinned, int floor, float threshold, float hold, float now) {
            int current = e.Steps == 0 ? pinned : e.OverrideBytes;
            int next = RateFor(pinned, e.Steps + 1, floor);
            if (next >= current) {
                // At the floor and still lossy a full Hold after the last step. Asked on every
                // sample from here on, so a player the floor helped at first and then stopped
                // helping is let go as soon as their delivery falls back.
                if (e.Steps > 0 && !Improved(e.Delivered, e.DeliveredAtStart)) {
                    GiveUp(e, peer, handle, pinned, floor, now);
                    return;
                }
                if (e.FloorLogged) { return; }
                e.FloorLogged = true;
                Logger.LogInfo(e.Steps == 0
                    ? $"Loss backoff: {Name(peer)} is delivering {Pct(e.Delivered)} but the floor ({Kb(floor)}) is not below the send rate ({Kb(pinned)}), so there is nothing to step down to."
                    : $"Loss backoff: {Name(peer)} is at the {Kb(floor)} floor and still delivering {Pct(e.Delivered)}; holding there.");
                return;
            }

            if (!Write(e, peer, handle, next)) { return; }
            e.Steps++;
            e.LastChangeAt = now;
            TotalStepsDown++;
            if (e.Steps == 1) {
                e.BackedOffSince = now;
                e.DeliveredAtStart = e.Delivered;
                BackedOffNow++;
            }
            Logger.LogInfo($"Loss backoff: {Name(peer)} delivering {Pct(e.Delivered)} (under {Pct(threshold)} for {hold:F0}s); " +
                           $"send rate {Kb(current)} -> {Kb(next)} (step {e.Steps}, floor {Kb(floor)}).");
            Record(peer, "down", e, next);
        }

        /// <summary>
        /// Stepping down did not help: the player is at the floor, still under the threshold, and
        /// delivering no better than when it started. Their loss is not the rate's doing, so the
        /// override is removed - full rate again, the same loss, but faster updates - and they are
        /// exempt for the rest of the session. A refused clear is handled by Refused like any
        /// other write, and nothing here is marked until the clear has gone through.
        /// </summary>
        private static void GiveUp(Entry e, ZNetPeer peer, uint handle, int pinned, int floor, float now) {
            if (!Clear(e, peer, handle)) { return; }

            int steps = e.Steps;
            float backedOffFor = now - e.BackedOffSince;
            e.Steps = 0;
            e.LastChangeAt = now;
            e.FloorLogged = false;
            e.Exempt = true;
            e.ExemptSince = now;
            BackedOffNow--;
            TotalExempted++;
            ExemptPlayers.Add(PlayerKey(peer));

            Logger.LogWarning($"Loss backoff: {Name(peer)} has packet loss that slowing down did not improve - delivering {Pct(e.Delivered)} " +
                              $"at the {Kb(floor)} floor after {steps} steps over {backedOffFor:F0}s, against {Pct(e.DeliveredAtStart)} when the back-off started. " +
                              $"Back at the global {Kb(pinned)}, and their rate will not be lowered again this session.");
            Record(peer, "exempt", e, pinned);
        }

        private static void StepUp(Entry e, ZNetPeer peer, uint handle, int pinned, int floor, float recover, float now) {
            int before = e.OverrideBytes;
            if (e.Steps == 1) {
                if (!Clear(e, peer, handle)) { return; }
                e.Steps = 0;
                e.LastChangeAt = now;
                e.FloorLogged = false;
                BackedOffNow--;
                TotalCleared++;
                Logger.LogInfo($"Loss backoff: {Name(peer)} delivering {Pct(e.Delivered)}, clean for {recover:F0}s; back at the global {Kb(pinned)}.");
                Record(peer, "clear", e, pinned);
                return;
            }

            // Several steps can sit on the floor together; climbing through them changes nothing
            // in Steam until the rate actually rises.
            int next = RateFor(pinned, e.Steps - 1, floor);
            if (next != before && !Write(e, peer, handle, next)) { return; }
            e.Steps--;
            e.LastChangeAt = now;
            e.FloorLogged = false;
            TotalStepsUp++;
            Logger.LogInfo($"Loss backoff: {Name(peer)} delivering {Pct(e.Delivered)}, clean for {recover:F0}s; " +
                           $"send rate {Kb(before)} -> {Kb(next)} (step {e.Steps}).");
            Record(peer, "up", e, next);
        }

        /// <summary>One line in network monitoring per change this makes, so a recording shows
        /// each player's steps and exemption exactly instead of leaving them to be inferred from
        /// periodic rate samples.</summary>
        private static void Record(ZNetPeer peer, string action, Entry e, int rateBytesPerSec) {
            if (!Monitoring.Active) { return; }
            Monitoring.OnLossBackoff(peer.m_uid, action, e.Steps, rateBytesPerSec, e.Delivered, e.DeliveredAtStart);
        }

        // -- propagation -------------------------------------------------------------------

        /// <summary>
        /// The global rate, tuning, or this mechanism's own settings changed. A connection-scope
        /// value does not follow Global writes, so every live override is re-derived from the new
        /// global rate and floor and rewritten - or removed, when the mechanism no longer applies or
        /// the new numbers leave nothing below the global rate to hold the peer at.
        /// </summary>
        internal static void Reconcile(string reason) {
            if (Peers.Count == 0) { return; }
            if (!Wanted) { ClearAll(reason); return; }

            ZNet net = ZNet.instance;
            if (net == null) { Reset(); return; }

            int pinned = SteamTransport.PinnedSendRateBytesPerSec;
            int floor = FloorBytesPerSec;
            float now = Time.realtimeSinceStartup;

            List<ZNetPeer> peers = net.GetPeers();
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (peer == null || !Peers.TryGetValue(peer.m_uid, out Entry e) || e.Steps == 0) { continue; }

                if (!RttProbe.TryGetConnectionHandle(peer.m_socket, out uint handle) || handle != e.Handle) {
                    Drop(e);                                                 // the override went with its connection
                    continue;
                }

                int next = RateFor(pinned, e.Steps, floor);
                if (next >= pinned) {
                    if (!Clear(e, peer, handle)) { return; }
                    e.Steps = 0;
                    e.LastChangeAt = now;
                    e.FloorLogged = false;
                    BackedOffNow--;
                    TotalCleared++;
                    Logger.LogInfo($"Loss backoff: {Name(peer)} back at the global {Kb(pinned)} - nothing below it to step to ({reason}).");
                } else if (next != e.OverrideBytes) {
                    int before = e.OverrideBytes;
                    if (!Write(e, peer, handle, next)) { return; }
                    e.LastChangeAt = now;
                    Logger.LogInfo($"Loss backoff: {Name(peer)} send rate {Kb(before)} -> {Kb(next)} (step {e.Steps}, {reason}).");
                }
            }
        }

        internal static void OnConfigChanged() {
            Reconcile("config changed");
        }

        /// <summary>Best-effort removal of every live override, then a clean slate. Used when the
        /// mechanism stops applying: a refused write is not acted on here, because there is nothing
        /// left to do about it.</summary>
        private static void ClearAll(string reason) {
            int cleared = 0;
            ZNet net = ZNet.instance;
            if (net != null) {
                List<ZNetPeer> peers = net.GetPeers();
                for (int i = 0; i < peers.Count; i++) {
                    ZNetPeer peer = peers[i];
                    if (peer == null || !Peers.TryGetValue(peer.m_uid, out Entry e) || e.Steps == 0) { continue; }
                    if (RttProbe.TryGetConnectionHandle(peer.m_socket, out uint handle) && handle == e.Handle) {
                        SteamNetConfig.TryClearConnectionRate(handle);
                        cleared++;
                    }
                }
            }
            ClearPeers();                                            // exemptions outlive this; see ExemptPlayers
            if (cleared > 0) {
                Logger.LogInfo($"Loss backoff: removed {cleared} per-player override(s) ({reason}); every connection is back at the global rate.");
            }
        }

        // -- the writes --------------------------------------------------------------------

        private static bool Write(Entry e, ZNetPeer peer, uint handle, int bytesPerSec) {
            SteamNetConfig.ConnectionResult result = SteamNetConfig.TrySetConnectionRate(handle, bytesPerSec);
            if (result == SteamNetConfig.ConnectionResult.Ok) {
                e.Handle = handle;
                e.OverrideBytes = bytesPerSec;
                return true;
            }
            Refused(e, peer, handle, result, "write");
            return false;
        }

        private static bool Clear(Entry e, ZNetPeer peer, uint handle) {
            SteamNetConfig.ConnectionResult result = SteamNetConfig.TryClearConnectionRate(handle);
            if (result == SteamNetConfig.ConnectionResult.Ok) {
                e.Handle = 0;
                e.OverrideBytes = 0;
                return true;
            }
            Refused(e, peer, handle, result, "clear");
            return false;
        }

        /// <summary>Steam did not take a connection-scope write. If the connection closed under us
        /// between the status read and the write, that is the ordinary race and the entry is
        /// dropped. Otherwise the interface does not do what this needs, that cannot change
        /// mid-process, and it stands down once rather than retrying at every ping.</summary>
        private static void Refused(Entry e, ZNetPeer peer, uint handle, SteamNetConfig.ConnectionResult result, string what) {
            if (!RttProbe.TryGetConnectionHandle(peer.m_socket, out uint current) || current != handle) {
                Drop(e);
                return;
            }

            PatchGuard.Disable(Mechanism.LossBackoff,
                $"Steam refused a per-connection send-rate {what} on {SteamNetConfig.InterfaceName} ({result}). " +
                "Per-player back-off is off for the rest of this process; Send Rate KBps still applies to every connection.");
            ClearAll("stood down");
        }

        // -- views -------------------------------------------------------------------------

        /// <summary>The rate this peer's connection should report - its override, or the global
        /// rate - and how long ago this mechanism last changed it (infinite when it never has).
        /// SteamTransport's "Steam adapts rates by itself" check reads both: a connection is
        /// re-clamped within about a second of a write, and until then a mismatch is expected.</summary>
        internal static int ExpectedRate(long uid, int pinnedBytesPerSec, out float secondsSinceChange) {
            secondsSinceChange = float.PositiveInfinity;
            if (!Peers.TryGetValue(uid, out Entry e)) { return pinnedBytesPerSec; }
            secondsSinceChange = Time.realtimeSinceStartup - e.LastChangeAt;
            return e.Steps > 0 ? e.OverrideBytes : pinnedBytesPerSec;
        }

        internal static bool TryGetView(long uid, out View view) {
            view = default;
            if (!Peers.TryGetValue(uid, out Entry e)) { return false; }

            int pinned = SteamTransport.PinnedSendRateBytesPerSec;
            int floor = FloorBytesPerSec;
            view = new View {
                Delivered = e.Delivered,
                HasSample = e.HasSample,
                Lossy = e.RunKind == Run.Lossy,
                Steps = e.Steps,
                OverrideBytesPerSec = e.OverrideBytes,
                AtFloor = e.Steps > 0 && RateFor(pinned, e.Steps + 1, floor) >= e.OverrideBytes,
                BackedOffSince = e.BackedOffSince,
                Exempt = e.Exempt,
                ExemptSince = e.ExemptSince,
            };
            return true;
        }

        // -- lifecycle ---------------------------------------------------------------------

        /// <summary>The peer is gone. Steam drops a connection-scope value with the connection, so
        /// there is nothing to write.</summary>
        internal static void ForgetPeer(long uid) {
            if (!Peers.TryGetValue(uid, out Entry e)) { return; }
            if (e.Steps > 0) { BackedOffNow--; }
            Peers.Remove(uid);
        }

        /// <summary>Session end, which is also where exemptions end. The totals are
        /// process-lifetime, like PeerLiveness's.</summary>
        internal static void Reset() {
            ClearPeers();
            ExemptPlayers.Clear();
        }

        private static void ClearPeers() {
            Peers.Clear();
            BackedOffNow = 0;
        }

        /// <summary>A peer seen for the first time starts exempt if the same player was exempted
        /// earlier this session under another peer id.</summary>
        private static Entry EntryFor(ZNetPeer peer, float now) {
            if (!Peers.TryGetValue(peer.m_uid, out Entry e)) {
                e = new Entry();
                if (ExemptPlayers.Count > 0 && ExemptPlayers.Contains(PlayerKey(peer))) {
                    e.Exempt = true;
                    e.ExemptSince = now;
                }
                Peers[peer.m_uid] = e;
            }
            return e;
        }

        /// <summary>
        /// Who the player is across reconnects: the platform address of their connection (a
        /// SteamID for the Steam peers this mechanism acts on). A peer id is not enough - it is new
        /// on every connection, and the players this exists for are the ones who drop and rejoin.
        /// Falls back to the peer id when the socket cannot say.
        /// </summary>
        private static string PlayerKey(ZNetPeer peer) {
            string address = null;
            try { address = peer.m_socket?.GetHostName(); } catch (System.Exception) { }
            return string.IsNullOrEmpty(address) ? "peer:" + peer.m_uid : address;
        }

        /// <summary>Forget the override without a Steam call: the connection it was on is gone.</summary>
        private static void Drop(Entry e) {
            if (e.Steps > 0) { BackedOffNow--; }
            e.Steps = 0;
            e.OverrideBytes = 0;
            e.Handle = 0;
            e.RunKind = Run.None;
            e.RunSamples = 0;
            e.FloorLogged = false;
        }

        private static string Name(ZNetPeer peer) {
            return string.IsNullOrEmpty(peer.m_playerName) ? $"peer {peer.m_uid}" : peer.m_playerName;
        }

        private static string Kb(int bytesPerSec) {
            return $"{bytesPerSec / BytesPerKB} KB/s";
        }

        private static string Pct(float share) {
            return $"{share * 100f:F1}%";
        }
    }
}
