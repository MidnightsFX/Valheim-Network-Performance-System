using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using Steamworks;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M11 - the listen host's half of Keep Players In The Background for players who do not have
    /// this mod: while the host's game has stopped running in the background, answer their
    /// connection checks from a thread of our own.
    ///
    /// <para>WHY. A client's ZRpc pings the host once a second and resets its deadline only when a
    /// pong comes back (ZRpc.ReceivePing), and only the host's main thread sends pongs. When the
    /// host's game stops running behind another window, every client hangs up at its in-world
    /// deadline. Players with this mod are told to wait for the host (Nps.HostFocus); a vanilla
    /// client, or an older version of this mod, never hears that and keeps its own 30s. Steam's
    /// own thread keeps the transport up all the while - it is only the game's ping that goes
    /// unanswered - so a pong sent from a thread that is still running is all those players
    /// need.</para>
    ///
    /// <para>WHAT IS SENT. Byte for byte what vanilla's pong is - int 0 (the ping's method id) and
    /// bool false, written by ZPackage itself - handed straight to Steam on the player's
    /// connection, reliable like everything the game sends. ReceivePing does nothing with a pong
    /// but zero m_timeSinceLastPing: no sequence number, no reply, nothing counted. So an extra
    /// one, anywhere in the stream, has no other effect on a vanilla client.</para>
    ///
    /// <para>WHY IT IS SAFE OFF THE MAIN THREAD. Steam's sockets library is built to be sent on
    /// from more than one thread: SendMessageToConnection takes only that connection's own lock,
    /// deliberately not the library's global one (GameNetworkingSockets, the open-source build of
    /// the same library, says as much in csteamnetworkingsockets.cpp and
    /// steamnetworkingsockets_lowlevel.h), so this thread and ZSteamSocket.SendQueuedPackages on
    /// the main thread are serialised by Steam, each message whole. Nothing of the game's is
    /// touched from here - not ZSteamSocket, whose queue is a plain Queue, not ZRpc, not Unity.
    /// The connection handles are read on the main thread and handed over as a snapshot that is
    /// never changed after. A handle stays valid until the game calls CloseConnection, which only
    /// the main thread does, and while this thread is sending the main thread is not running; a
    /// player who left after the snapshot was taken just gets a refused send.</para>
    ///
    /// <para>WHEN. Only while the host's players have been told to wait for it
    /// (ConnectionTimeout.HostToldWait - its game is in the background and may have stopped
    /// there), so players without the mod get exactly the wait players with it get; and only once
    /// this game's main thread has not ticked for a whole interval - while it runs, its own pongs
    /// go out and nothing is sent from here. Never for a game in front: a host whose game hangs
    /// while it is the active window is dropped by its players exactly as before, because the
    /// wait flag can only be raised by a frame that ran in the background. And never past
    /// Background Timeout Seconds of stall, the longest anyone with the mod would wait either -
    /// with Enable Background Timeout off, for as long as the stall lasts, as they would.
    /// A host that hangs in the background, rather than being paused there, is waited for that
    /// long - the same cost Keep Players In The Background already carries for players with the
    /// mod. A host whose game crashes takes this thread with it, and its players' Steam
    /// connections notice as they always did.</para>
    ///
    /// <para>HOW OFTEN. A third of the shortest deadline any player can have: vanilla's 30s, or
    /// the host's in-world deadline, which older versions of this mod take from its config - at
    /// most every 10s. That leaves two thirds of the deadline for each one to arrive, while a
    /// vanilla client's own bad-connection icon (shown after 5s without a pong) still shows most
    /// of the time, the only sign it gets that the host's game has stopped. It also stays under
    /// half the deadline, where an older version of this mod would start warning and then say
    /// contact was restored, over and over.</para>
    ///
    /// <para>WHO. Only players whose socket is the game's own ZSteamSocket, and only while no other
    /// mod patches the host's send path: a mod that compresses or re-frames what the game sends
    /// has a counterpart on the client expecting that, and raw bytes are not what it expects.
    /// Crossplay players have no Steam connection to send on. Players with this mod get the
    /// pongs too, and while they are waiting for the host they do not count a pong as the host
    /// answering (NetworkChannelPatches.SampleRtt) - its pings come only from its main thread -
    /// so GhostWatchdog still tells them why the world has stopped.</para>
    /// </summary>
    internal static class HostKeepalive {

        /// <summary>Longest gap between two answers. See the class summary for the third.</summary>
        private const float MaxIntervalSeconds = 10f;

        /// <summary>Floor on the gap, for a deadline shorter than any the settings allow today.</summary>
        private const float MinIntervalSeconds = 1f;

        /// <summary>How often the thread looks. Far below the interval, so the first answer goes
        /// out within a quarter of a second of being due, and costs nothing while it waits.</summary>
        private const int PollMillis = 250;

        /// <summary>How often the main thread re-reads its players' connections while they are
        /// waiting for it. A player who joined or left in the last second before the game
        /// stopped is the most this can miss.</summary>
        private const float RefreshSeconds = 1f;

        /// <summary>The methods a mod that changes what the host puts on the wire would have to
        /// patch. Anything else is patched elsewhere and leaves the bytes alone.</summary>
        private static readonly string[] SendPathSteam = { "Send", "SendQueuedPackages", "Flush", "Recv" };
        private const string SendPathRpc = "SendPackage";

        /// <summary>What one pass of the thread needs, built on the main thread and never changed
        /// after - the thread reads whichever one is current.</summary>
        private sealed class Plan {
            internal readonly uint[] Handles;
            internal readonly long IntervalTicks;
            internal readonly long GiveUpTicks;

            internal Plan(uint[] handles, long intervalTicks, long giveUpTicks) {
                Handles = handles;
                IntervalTicks = intervalTicks;
                GiveUpTicks = giveUpTicks;
            }
        }

        /// <summary>One thread and what it shares with the main thread. One per session: a new
        /// session gets a new one, so a thread that has not yet noticed it was stopped can only
        /// ever read its own, emptied, plan.</summary>
        private sealed class Worker {
            internal volatile bool Stop;
            internal readonly AutoResetEvent Wake = new AutoResetEvent(false);
            internal readonly byte[] Pong;
            internal Plan Current;                  // Volatile.Read/Write

            // Written by the thread, read by the main thread.
            internal long Stalls;                   // stretches it answered for at least once
            internal long Rounds;                   // answers, to every player at once
            internal long Sends;
            internal long Refused;
            internal long GaveUp;                   // stretches that outlasted Background Timeout Seconds
            internal volatile string LastRefusal;   // the last result Steam gave that was not OK
            internal volatile string Broken;        // set when a send threw: nothing more is sent this session

            internal Worker(byte[] pong) { Pong = pong; }
        }

        private static Worker _worker;

        /// <summary>When the main thread last ticked, on the Stopwatch clock. The one thing the
        /// thread reads that the main thread writes every frame.</summary>
        private static long _lastTick;

        /// <summary>The last gap between two frames long enough to be a stall, in seconds.</summary>
        private static float _lastStallSeconds;

        private static long _refreshAt;
        private static readonly List<uint> Scratch = new List<uint>();

        /// <summary>Why it is not running this session, when it cannot: another mod on the send
        /// path. Checked once per session, when it is first needed.</summary>
        internal static string StoodDownReason { get; private set; }
        private static bool _checkedSendPath;

        // -- diagnostics, main thread ---------------------------------------------------------

        internal static int PlayersCovered { get; private set; }
        internal static int PlayersWrapped { get; private set; }
        internal static long StallsAnswered { get; private set; }
        internal static long StallsOutlasted { get; private set; }
        internal static float LongestAnsweredSeconds { get; private set; }

        private static long _seenRounds;
        private static long _seenSends;
        private static long _seenRefused;
        private static long _seenGaveUp;
        private static bool _reportedBroken;

        /// <summary>The admin's switch, on top of Keep Players In The Background (which raises the
        /// wait flag at all).</summary>
        private static bool Enabled =>
            ValConfig.KeepPlayersWithoutTheMod == null || ValConfig.KeepPlayersWithoutTheMod.Value;

        /// <summary>
        /// Listen host, once per frame from the ZNet.Update postfix, after the host has decided
        /// whether its players wait for it. Stamps the frame for the thread, says what the thread
        /// did if this frame ends a stall it answered for, and keeps the thread's list of
        /// connections current while the players are waiting.
        /// </summary>
        internal static void Tick(bool playersWaiting) {
            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Exchange(ref _lastTick, now);

            // The frame that ends a stall is not always the one that reports it: the thread can
            // finish a round just after this frame read its counters. So the stall's length is
            // kept until the next one replaces it.
            float gap = previous > 0L ? Seconds(now - previous) : 0f;
            if (gap >= MinIntervalSeconds) { _lastStallSeconds = gap; }

            Worker worker = _worker;
            if (worker != null) { ReportStall(worker, _lastStallSeconds); }

            List<ZNetPeer> peers = playersWaiting && Enabled ? ZNet.instance?.GetPeers() : null;
            if (peers == null) {
                if (worker != null && Volatile.Read(ref worker.Current) != null) { Volatile.Write(ref worker.Current, null); }
                _refreshAt = 0L;
                return;
            }
            if (now < _refreshAt) { return; }
            _refreshAt = now + Ticks(RefreshSeconds);

            if (worker == null) {
                worker = Start();
                if (worker == null) { return; }
            }

            CollectHandles(peers, Scratch, out int wrapped);
            PlayersCovered = Scratch.Count;
            PlayersWrapped = wrapped;
            Publish(worker, Scratch, IntervalSeconds(ConnectionTimeout.InWorldTimeoutSeconds),
                    ConnectionTimeout.BackgroundDeadlineSeconds);
        }

        /// <summary>Session end, from ZNet.StopAll. The thread is told to stop and wakes at once;
        /// nothing waits for it, because there is nothing it can still do - its plan is gone, and
        /// it only ever sends once this thread has stopped ticking.</summary>
        internal static void Stop() {
            Worker worker = _worker;
            _worker = null;
            if (worker != null) {
                Volatile.Write(ref worker.Current, null);
                worker.Stop = true;
                try { worker.Wake.Set(); } catch (ObjectDisposedException) { }
            }
            _refreshAt = 0L;
            _lastStallSeconds = 0f;
            _checkedSendPath = false;
            StoodDownReason = null;
            PlayersCovered = 0;
            PlayersWrapped = 0;
            StallsAnswered = 0L;
            StallsOutlasted = 0L;
            LongestAnsweredSeconds = 0f;
            _seenRounds = 0L;
            _seenSends = 0L;
            _seenRefused = 0L;
            _seenGaveUp = 0L;
            _reportedBroken = false;
        }

        /// <summary>How long between answers for a host whose in-world deadline is this. Pure, for
        /// the offline harness.</summary>
        internal static float IntervalSeconds(float inWorldDeadlineSeconds) {
            float shortest = Math.Min(ConnectionTimeout.VanillaRpcTimeoutSeconds, inWorldDeadlineSeconds);
            return Math.Max(MinIntervalSeconds, Math.Min(MaxIntervalSeconds, shortest / 3f));
        }

        /// <summary>The pong itself, written the way ZRpc.ReceivePing writes it.</summary>
        internal static byte[] BuildPong() {
            ZPackage pkg = new ZPackage();
            pkg.Write(0);
            pkg.Write(false);
            return pkg.GetArray();
        }

        /// <summary>
        /// Client: whether a ping message from the host, just read by ZRpc.ReceivePing, shows the
        /// host's game is running. While we are waiting for the host a pong does not: it may be
        /// this class answering while that game is stopped. Its pings do, always - ZRpc.UpdatePing
        /// sends one a second from the host's main thread and this class never sends one - so a
        /// host that has been waited for since it once stalled, and is running normally now, is
        /// still heard every second and GhostWatchdog stays quiet.
        /// </summary>
        internal static bool CountsAsHostAnswering(bool waitingForHost, ZPackage package) {
            return !waitingForHost || !WasPong(package);
        }

        /// <summary>
        /// Client: whether the ping message ZRpc.ReceivePing has just read was a pong. The flag is
        /// the byte it read last, so it is read again and the read position ends where it was.
        /// </summary>
        internal static bool WasPong(ZPackage package) {
            if (package == null) { return false; }
            int pos = package.GetPos();
            if (pos < 1) { return false; }
            package.SetPos(pos - 1);
            return !package.ReadBool();
        }

        /// <summary>The Steam connection of every player this can answer for: those whose socket
        /// is the game's own, still connected. Wrapped ones are counted, not answered for - what
        /// a wrapper does to the bytes is not known here.</summary>
        internal static void CollectHandles(List<ZNetPeer> peers, List<uint> into, out int wrapped) {
            into.Clear();
            wrapped = 0;
            if (peers == null) { return; }
            for (int i = 0; i < peers.Count; i++) {
                ZNetPeer peer = peers[i];
                if (peer?.m_socket == null) { continue; }
                ZSteamSocket steam = RttProbe.ResolveSteam(peer.m_socket, out RttProbe.SocketPath path);
                if (steam == null) { continue; }                         // crossplay, or nothing to read
                if (path != RttProbe.SocketPath.Direct) { wrapped++; continue; }
                if (!steam.IsConnected()) { continue; }
                uint handle = steam.m_con.m_HSteamNetConnection;
                if (handle != 0u) { into.Add(handle); }
            }
        }

        /// <summary>Hands the thread a new plan when anything in it changed. Allocates only then:
        /// the same players at the same settings, refreshed once a second, cost nothing. An
        /// infinite give-up (no background limit) is one the thread never reaches.</summary>
        private static void Publish(Worker worker, List<uint> handles, float intervalSeconds, float giveUpSeconds) {
            long interval = Ticks(intervalSeconds);
            long giveUp = float.IsPositiveInfinity(giveUpSeconds) ? long.MaxValue : Ticks(giveUpSeconds);
            Plan current = Volatile.Read(ref worker.Current);
            if (current != null && current.IntervalTicks == interval && current.GiveUpTicks == giveUp
                && SameHandles(current.Handles, handles)) {
                return;
            }
            Volatile.Write(ref worker.Current, new Plan(handles.ToArray(), interval, giveUp));
        }

        private static bool SameHandles(uint[] a, List<uint> b) {
            if (a.Length != b.Count) { return false; }
            for (int i = 0; i < a.Length; i++) {
                if (a[i] != b[i]) { return false; }
            }
            return true;
        }

        private static Worker Start() {
            if (!_checkedSendPath) {
                _checkedSendPath = true;
                StoodDownReason = ForeignSendPathPatch();
                if (StoodDownReason != null) {
                    Logger.LogInfo($"Players without this mod will not be kept connected while this game is stopped in the background: {StoodDownReason}. " +
                                   "Players with this mod still wait for it (Keep Players In The Background).");
                }
            }
            if (StoodDownReason != null) { return null; }

            Worker worker = new Worker(BuildPong());
            try {
                Thread thread = new Thread(Run) { IsBackground = true, Name = "NPS host keepalive" };
                thread.Start(worker);
            } catch (Exception e) {
                StoodDownReason = $"its thread could not be started ({e.GetType().Name})";
                Logger.LogWarning($"Players without this mod will not be kept connected while this game is stopped in the background: {StoodDownReason}.");
                return null;
            }
            _worker = worker;
            return worker;
        }

        /// <summary>
        /// The other mod patching what the host sends, by its Harmony id, or null for none. A mod
        /// that compresses or re-frames the stream patches one of these on both ends, and its
        /// clients would not read a raw pong; nothing else here changes the bytes. This mod's own
        /// patches (monitoring counts packages on SendPackage) are left out.
        /// </summary>
        internal static string ForeignSendPathPatch() {
            try {
                for (int i = 0; i < SendPathSteam.Length; i++) {
                    string owner = ForeignOwner(AccessTools.Method(typeof(ZSteamSocket), SendPathSteam[i]));
                    if (owner != null) { return $"{owner} patches ZSteamSocket.{SendPathSteam[i]}"; }
                }
                string rpcOwner = ForeignOwner(AccessTools.Method(typeof(ZRpc), SendPathRpc));
                return rpcOwner != null ? $"{rpcOwner} patches ZRpc.{SendPathRpc}" : null;
            } catch (Exception e) {
                return $"the game's send path could not be checked ({e.GetType().Name})";
            }
        }

        private static string ForeignOwner(MethodBase method) {
            if (method == null) { return null; }
            HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
            if (info == null) { return null; }
            foreach (string owner in info.Owners) {
                if (owner == null || owner.StartsWith(NetworkPerformanceSystem.PluginGUID, StringComparison.Ordinal)) { continue; }
                return owner;
            }
            return null;
        }

        // -- the thread ------------------------------------------------------------------------

        private static void Run(object state) {
            Worker worker = (Worker)state;
            IntPtr pong = IntPtr.Zero;
            try {
                pong = Marshal.AllocHGlobal(worker.Pong.Length);
                Marshal.Copy(worker.Pong, 0, pong, worker.Pong.Length);

                bool inStall = false;
                bool answered = false;
                bool gaveUp = false;
                long lastRound = 0L;
                while (!worker.Stop && worker.Broken == null) {
                    worker.Wake.WaitOne(PollMillis);
                    if (worker.Stop) { break; }

                    Plan plan = Volatile.Read(ref worker.Current);
                    long now = Stopwatch.GetTimestamp();
                    long stalled = now - Interlocked.Read(ref _lastTick);
                    if (plan == null || stalled < plan.IntervalTicks) {
                        inStall = false;
                        continue;
                    }

                    if (!inStall) {
                        inStall = true;
                        answered = false;
                        gaveUp = false;
                        lastRound = 0L;
                    }
                    if (stalled >= plan.GiveUpTicks) {
                        if (!gaveUp) {
                            gaveUp = true;
                            Interlocked.Increment(ref worker.GaveUp);
                        }
                        continue;
                    }
                    if (lastRound != 0L && now - lastRound < plan.IntervalTicks) { continue; }
                    lastRound = now;

                    if (!answered) {
                        answered = true;
                        Interlocked.Increment(ref worker.Stalls);
                    }
                    SendRound(worker, plan, pong);
                }
            } catch (Exception e) {
                worker.Broken = $"{e.GetType().Name}: {e.Message}";
            } finally {
                if (pong != IntPtr.Zero) { Marshal.FreeHGlobal(pong); }
                worker.Wake.Close();
            }
        }

        private static void SendRound(Worker worker, Plan plan, IntPtr pong) {
            long refused = 0L;
            for (int i = 0; i < plan.Handles.Length; i++) {
                EResult result;
                try {
                    result = SendPong(plan.Handles[i], pong, (uint)worker.Pong.Length);
                } catch (Exception e) {
                    // Steam not initialised in this process, or Steamworks.NET missing an entry
                    // point - a property of the build, not of this moment.
                    worker.Broken = $"{e.GetType().Name}: {e.Message}";
                    return;
                }
                if (result != EResult.k_EResultOK) {
                    refused++;
                    worker.LastRefusal = result.ToString();
                }
            }
            Interlocked.Add(ref worker.Sends, plan.Handles.Length);
            Interlocked.Add(ref worker.Refused, refused);
            Interlocked.Increment(ref worker.Rounds);
        }

        /// <summary>Not inlined, so a Steamworks type that fails to load throws into the caller's
        /// catch rather than when Run is compiled. Reliable like every game message; no Nagle
        /// delay, so it does not wait on a timer for company that is not coming.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static EResult SendPong(uint handle, IntPtr data, uint length) {
            return SteamNetworkingSockets.SendMessageToConnection(new HSteamNetConnection(handle), data, length,
                Constants.k_nSteamNetworkingSend_ReliableNoNagle, out long _);
        }

        // -- reporting, main thread -----------------------------------------------------------

        /// <summary>The first frame after a stall the thread answered for: say so once, with how
        /// long the game was gone. Nothing for a stall it did not answer for - that is an
        /// ordinary hitch.</summary>
        private static void ReportStall(Worker worker, float gapSeconds) {
            if (worker.Broken != null && !_reportedBroken) {
                _reportedBroken = true;
                Logger.LogWarning($"Players without this mod can no longer be kept connected while this game is stopped in the background: " +
                                  $"Steam refused the thread that answers for it ({worker.Broken}).");
            }

            long rounds = Interlocked.Read(ref worker.Rounds);
            long gaveUp = Interlocked.Read(ref worker.GaveUp);
            if (rounds == _seenRounds && gaveUp == _seenGaveUp) { return; }

            long sends = Interlocked.Read(ref worker.Sends);
            long refused = Interlocked.Read(ref worker.Refused);
            long newRounds = rounds - _seenRounds;
            long newSends = sends - _seenSends;
            long newRefused = refused - _seenRefused;
            bool outlasted = gaveUp != _seenGaveUp;
            _seenRounds = rounds;
            _seenSends = sends;
            _seenRefused = refused;
            _seenGaveUp = gaveUp;

            StallsAnswered = Interlocked.Read(ref worker.Stalls);
            if (outlasted) { StallsOutlasted++; }
            if (gapSeconds > LongestAnsweredSeconds) { LongestAnsweredSeconds = gapSeconds; }

            string line = $"This game was not running for {gapSeconds:F0}s in the background. Players without this mod were answered for it " +
                          $"{newRounds} time(s), so they stayed connected (Keep Players In The Background).";
            if (outlasted) {
                line += $" It stopped answering after {ConnectionTimeout.BackgroundTimeoutSeconds:F0}s, so they may have been dropped since.";
            }
            if (newRefused > 0L) {
                line += $" Steam refused {newRefused} of {newSends} answers ({worker.LastRefusal}) - a player who had already left.";
            }
            Logger.LogInfo(line);
            if (Monitoring.Active) { Monitoring.OnHostKeepalive(gapSeconds, newRounds, newSends, newRefused, outlasted); }
        }

        /// <summary>The nps_stats line, or null when there is nothing to say.</summary>
        internal static string StatsLine() {
            if (!Enabled) { return "players without the mod are dropped at their own deadline while it is stopped (Keep Players Without The Mod is off)"; }
            if (StoodDownReason != null) { return $"players without the mod are not answered for: {StoodDownReason}"; }
            Worker worker = _worker;
            if (worker?.Broken != null) { return $"players without the mod are not answered for: Steam refused ({worker.Broken})"; }
            if (worker == null) { return null; }
            string wrapped = PlayersWrapped > 0 ? $", {PlayersWrapped} more behind another mod's connection wrapper" : "";
            return $"answered for players without the mod through {StallsAnswered} stall(s) this session, longest {LongestAnsweredSeconds:F0}s, " +
                   $"{StallsOutlasted} outlasted the wait; {PlayersCovered} player(s) covered{wrapped}";
        }

        private static long Ticks(float seconds) {
            return (long)(seconds * Stopwatch.Frequency);
        }

        private static float Seconds(long ticks) {
            return ticks / (float)Stopwatch.Frequency;
        }
    }
}
