using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Steamworks;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// Reads and writes Steam's global networking configuration, through whichever half of
    /// Steamworks this process actually initialised.
    ///
    /// Same split RttProbe deals with, one interface family over: the dedicated-server build of
    /// assembly_valheim is compiled against SteamGameServerNetworkingUtils and the client build
    /// against SteamNetworkingUtils, only the matching half is initialised in each process, and
    /// the other one throws "Steamworks is not initialized." on every call. So we probe the
    /// build's own interface first, fall back to the other once, cache the answer for the process,
    /// and never touch the dead one again. Unlike a ping read, a Global-scope config read has no
    /// "not measured yet" state - vanilla has already written these keys by the time we run - so
    /// a clean read is a definitive probe and one failure per interface is enough to decide.
    ///
    /// Everything here is best-effort and silent on failure past the first report: this sits in
    /// startup and in a config-changed handler, and a transport we cannot configure must leave the
    /// game running exactly as vanilla rather than throw into either.
    /// </summary>
    internal static class SteamNetConfig {

        private enum UtilsApi { Unresolved, Client, GameServer, Absent }

        /// <summary>Which interface answered. Fixed for the process lifetime once decided: which
        /// half of Steamworks was initialised cannot change, so there is nothing to re-probe.</summary>
        private static UtilsApi _resolved = UtilsApi.Unresolved;

        /// <summary>True when this process has an interface we can read and write.</summary>
        internal static bool Available => Resolve();

        /// <summary>Name of the resolved interface, for the readback log line.</summary>
        internal static string InterfaceName {
            get {
                switch (_resolved) {
                    case UtilsApi.GameServer: return "SteamGameServerNetworkingUtils";
                    case UtilsApi.Client: return "SteamNetworkingUtils";
                    default: return "none";
                }
            }
        }

        /// <summary>
        /// Current value of a Global-scope int32 config key. False means we could not read it -
        /// no interface, or Steam refused the key.
        /// </summary>
        internal static bool TryRead(ESteamNetworkingConfigValue key, out int value) {
            value = 0;
            if (!Resolve()) { return false; }
            return TryReadVia(_resolved, key, out value) ;
        }

        /// <summary>
        /// Writes a Global-scope int32 config key. False means the write did not happen, in which
        /// case the transport keeps whatever vanilla left there.
        /// </summary>
        internal static bool TryWrite(ESteamNetworkingConfigValue key, int value) {
            if (!Resolve()) { return false; }

            try {
                return WriteVia(_resolved, key, value);
            } catch (Exception e) {
                Logger.LogWarning($"Steam transport: writing {key} failed ({e.GetType().Name}: {e.Message}). " +
                                  "That value keeps its vanilla setting.");
                return false;
            }
        }

        /// <summary>
        /// Decides once which interface this process has. A failure to find either disables the
        /// mechanism rather than being retried - the answer cannot change mid-process, and this is
        /// called from a config-changed handler that would otherwise pay an interop exception per
        /// edit forever.
        /// </summary>
        private static bool Resolve() {
            if (_resolved == UtilsApi.Absent) { return false; }
            if (_resolved != UtilsApi.Unresolved) { return true; }

            UtilsApi first = NpsEnv.IsDedicated() ? UtilsApi.GameServer : UtilsApi.Client;
            UtilsApi other = first == UtilsApi.Client ? UtilsApi.GameServer : UtilsApi.Client;

            if (Probe(first)) {
                _resolved = first;
                return true;
            }

            // The build's own interface is not initialised here - a server build started without
            // -batchmode, a client build run headless, or a game update that moved the dedicated
            // server onto the client context. Try the other one before giving up.
            if (Probe(other)) {
                _resolved = other;
                Logger.LogInfo($"Steam transport: the {first} networking interface is not initialised in this " +
                               $"process; using {other} instead.");
                return true;
            }

            _resolved = UtilsApi.Absent;
            PatchGuard.Disable(Mechanism.SteamTransport,
                "neither Steamworks networking-utils interface is initialised in this process " +
                "(crossplay-only, or Steamworks failed to start). Send-rate bounds and Nagle stay at vanilla.");
            return false;
        }

        /// <summary>Reading a key vanilla has already written is the probe: it answers only on the
        /// interface this process owns.</summary>
        private static bool Probe(UtilsApi api) {
            try {
                return TryReadVia(api, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, out int _);
            } catch (Exception) {
                // InvalidOperationException for the interface this process does not have, or a
                // type-load failure if Steamworks.NET itself could not be resolved.
                return false;
            }
        }

        private static bool TryReadVia(UtilsApi api, ESteamNetworkingConfigValue key, out int value) {
            value = 0;
            IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
            try {
                Marshal.WriteInt32(buffer, 0);
                ulong size = sizeof(int);
                ESteamNetworkingGetConfigValueResult result = api == UtilsApi.GameServer
                    ? ReadGameServer(key, buffer, ref size)
                    : ReadClient(key, buffer, ref size);

                if (result != ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OK &&
                    result != ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OKInherited) {
                    return false;
                }

                value = Marshal.ReadInt32(buffer);
                return true;
            } finally {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // The four interop calls live in their own non-inlined methods so the Steamworks types are
        // only resolved when one is actually invoked, and anything they throw - including a
        // type-load failure - lands in the caller's catch rather than escaping into the patch.

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ESteamNetworkingGetConfigValueResult ReadGameServer(
            ESteamNetworkingConfigValue key, IntPtr buffer, ref ulong size) {
            return SteamGameServerNetworkingUtils.GetConfigValue(key,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                out ESteamNetworkingConfigDataType _, buffer, ref size);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ESteamNetworkingGetConfigValueResult ReadClient(
            ESteamNetworkingConfigValue key, IntPtr buffer, ref ulong size) {
            return SteamNetworkingUtils.GetConfigValue(key,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                out ESteamNetworkingConfigDataType _, buffer, ref size);
        }

        private static bool WriteVia(UtilsApi api, ESteamNetworkingConfigValue key, int value) {
            GCHandle pinned = GCHandle.Alloc(value, GCHandleType.Pinned);
            try {
                return api == UtilsApi.GameServer
                    ? WriteGameServer(key, pinned.AddrOfPinnedObject())
                    : WriteClient(key, pinned.AddrOfPinnedObject());
            } finally {
                pinned.Free();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool WriteGameServer(ESteamNetworkingConfigValue key, IntPtr value) {
            return SteamGameServerNetworkingUtils.SetConfigValue(key,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool WriteClient(ESteamNetworkingConfigValue key, IntPtr value) {
            return SteamNetworkingUtils.SetConfigValue(key,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, value);
        }
    }
}
