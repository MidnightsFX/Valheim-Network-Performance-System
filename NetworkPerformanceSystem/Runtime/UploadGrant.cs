using UnityEngine;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M37's client half: the rate this player's own game may upload to the host at.
    ///
    /// The game paces a client's one connection at the same fixed 150 KB/s as the host's, and the
    /// player who owns the creatures in a big fight is the one whose upload fills first - the
    /// 2026-10-07 recording had the heaviest owner at 150 KB/s for most of the fight. Only the host
    /// sees enough to decide a higher rate (what every player uploads, how much of it arrives, and
    /// what its own line can take in), so it decides, and sends each player a grant over
    /// "Nps.UploadRate". This applies it, and nothing else.
    ///
    /// A grant is written at Connection scope on the connection to the host, the same write M26
    /// uses on the host's side, so it touches nothing else and dies with the connection. It
    /// carries a time to live: the host refreshes it every ten seconds while it stands, and a
    /// host that stops - switched off, gone, or this player dropped and back on a new connection
    /// - lets it lapse back to the game's rate rather than leaving it in force. Grants outside
    /// [the game's rate, MaxGrantBytesPerSec] are clamped; at or under the game's rate the
    /// override is removed.
    ///
    /// The payload is a ZPackage with a version byte in front, read inside a try: a typed RPC
    /// whose parameters change later throws EndOfStream, which ZRpc reports as an incompatible
    /// version and ZNet answers by disconnecting. An older client has no handler for the method
    /// at all, and vanilla ZRpc drops a method it does not know.
    /// </summary>
    internal static class UploadGrant {

        internal const string RpcUploadRate = "Nps.UploadRate";
        internal const byte WireVersion = 1;

        /// <summary>Whatever the host asks, never above this.</summary>
        internal const int MaxGrantBytesPerSec = 1000 * 1024;

        private const float MinTtlSeconds = 5f;
        private const float MaxTtlSeconds = 120f;

        /// <summary>The grant in force, bytes/sec; 0 = none, the connection runs at the game's rate.</summary>
        internal static int GrantBytesPerSec { get; private set; }

        private static uint _handle;
        private static float _expiresAt;
        private static float _changedAt = float.NegativeInfinity;
        private static bool _stoodDown;

        // -- host side ---------------------------------------------------------------------

        internal static ZPackage BuildPackage(int bytesPerSec, float ttlSeconds) {
            ZPackage pkg = new ZPackage();
            pkg.Write(WireVersion);
            pkg.Write(bytesPerSec);
            pkg.Write(ttlSeconds);
            return pkg;
        }

        // -- client side -------------------------------------------------------------------

        internal static void RPC_UploadRate(ZRpc rpc, ZPackage pkg) {
            if (NpsEnv.IsHost() || _stoodDown || rpc == null || pkg == null) { return; }

            int rate;
            float ttl;
            try {
                byte version = pkg.ReadByte();
                if (version < 1) { return; }
                rate = pkg.ReadInt();
                ttl = pkg.ReadSingle();
            } catch (System.Exception) {
                return;
            }

            if (!RttProbe.TryGetConnectionHandle(rpc.GetSocket(), out uint handle)) { return; }
            float now = Time.realtimeSinceStartup;
            int vanilla = SteamTransport.VanillaSendRateBytesPerSec;

            if (rate <= vanilla) {
                ClearGrant(handle, "the host's grant is back at the game's rate");
                return;
            }

            rate = Mathf.Min(rate, MaxGrantBytesPerSec);
            ttl = float.IsNaN(ttl) ? MinTtlSeconds : Mathf.Clamp(ttl, MinTtlSeconds, MaxTtlSeconds);
            _expiresAt = now + ttl;
            if (rate == GrantBytesPerSec && handle == _handle) { return; }   // a refresh

            SteamNetConfig.ConnectionResult result = SteamNetConfig.TrySetConnectionRate(handle, rate);
            if (result == SteamNetConfig.ConnectionResult.Ok) {
                int before = GrantBytesPerSec > 0 && handle == _handle ? GrantBytesPerSec : vanilla;
                GrantBytesPerSec = rate;
                _handle = handle;
                _changedAt = now;
                Logger.LogInfo($"Upload rate: {before / 1024} -> {rate / 1024} KB/s, granted by the host.");
                return;
            }
            if (result == SteamNetConfig.ConnectionResult.BadHandle) { return; }   // the connection closed under us

            _stoodDown = true;
            Forget();
            Logger.LogWarning($"Upload rate: Steam refused a per-connection send-rate write on {SteamNetConfig.InterfaceName}; " +
                              "this game uploads at the game's rate for the rest of the session.");
        }

        /// <summary>Client tick: let a grant the host stopped refreshing lapse, and forget one whose
        /// connection is gone (Steam dropped the value with it).</summary>
        internal static void Tick() {
            if (GrantBytesPerSec == 0) { return; }
            ZNet net = ZNet.instance;
            ZNetPeer server = net != null ? net.GetServerPeer() : null;
            if (server == null || !RttProbe.TryGetConnectionHandle(server.m_socket, out uint handle) || handle != _handle) {
                Forget();
                return;
            }
            if (Time.realtimeSinceStartup >= _expiresAt) {
                ClearGrant(handle, "the host stopped renewing it");
            }
        }

        private static void ClearGrant(uint handle, string why) {
            if (GrantBytesPerSec == 0) { return; }
            int before = GrantBytesPerSec;
            if (handle == _handle) { SteamNetConfig.TryClearConnectionRate(handle); }
            Forget();
            _changedAt = Time.realtimeSinceStartup;
            Logger.LogInfo($"Upload rate: {before / 1024} -> {SteamTransport.VanillaSendRateBytesPerSec / 1024} KB/s ({why}).");
        }

        /// <summary>The rate this client's connection should report - for SteamTransport's check
        /// that Steam does not adapt rates by itself - and how long since it last changed.</summary>
        internal static int ExpectedRate(int pinnedBytesPerSec, out float secondsSinceChange) {
            secondsSinceChange = Time.realtimeSinceStartup - _changedAt;
            return GrantBytesPerSec > 0 ? GrantBytesPerSec : pinnedBytesPerSec;
        }

        private static void Forget() {
            GrantBytesPerSec = 0;
            _handle = 0;
            _expiresAt = 0f;
        }

        /// <summary>Session end: the connection, and the value on it, are gone.</summary>
        internal static void Reset() {
            Forget();
            _changedAt = float.NegativeInfinity;
            _stoodDown = false;
        }
    }
}
