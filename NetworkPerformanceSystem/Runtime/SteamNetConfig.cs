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
                return WriteVia(_resolved, key, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, value);
            } catch (Exception e) {
                Logger.LogWarning($"Steam transport: writing {key} failed ({e.GetType().Name}: {e.Message}). " +
                                  "That value keeps its vanilla setting.");
                return false;
            }
        }

        // -- one connection ----------------------------------------------------------------
        // The same keys exist at Connection scope, where the scope object is the connection
        // handle itself (an intptr_t carrying the value, not a pointer to it). A value set there
        // overrides Global for that connection alone, stops following later Global writes, and
        // dies with the connection. Writing a null value at a non-global scope removes it, so the
        // connection inherits again. Every write is read back: Steam returns OK for a value set on
        // the connection itself, OKInherited when none is, and BadScopeObj for a handle it does
        // not know - which is how an interface that cannot do this at all announces itself.

        /// <summary>Outcome of a connection-scope rate write or clear, verified by reading the
        /// connection back. BadHandle is Steam not recognising the connection - closed under us,
        /// or an interface that does not resolve handles the other half created. Failed is
        /// anything else, including a readback that did not show the value written.</summary>
        internal enum ConnectionResult { Ok, BadHandle, Failed }

        /// <summary>Pins one connection's send rate: SendRateMax then SendRateMin at Connection
        /// scope, the order SteamTransport.WriteRate uses for the global pair, then reads Min
        /// back.</summary>
        internal static ConnectionResult TrySetConnectionRate(uint hConn, int bytesPerSec) {
            if (!Resolve()) { return ConnectionResult.Failed; }

            try {
                IntPtr scopeObj = new IntPtr((long)hConn);
                const ESteamNetworkingConfigScope scope = ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection;
                if (!WriteVia(_resolved, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, scope, scopeObj, bytesPerSec)) { return ConnectionResult.Failed; }
                if (!WriteVia(_resolved, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, scope, scopeObj, bytesPerSec)) { return ConnectionResult.Failed; }
                return VerifyConnection(scopeObj, bytesPerSec);
            } catch (Exception e) {
                Logger.LogWarning($"Steam transport: per-connection send-rate write failed ({e.GetType().Name}: {e.Message}).");
                return ConnectionResult.Failed;
            }
        }

        /// <summary>Removes one connection's send-rate override so it inherits the global rate
        /// again, then reads back that nothing is set on it any more.</summary>
        internal static ConnectionResult TryClearConnectionRate(uint hConn) {
            if (!Resolve()) { return ConnectionResult.Failed; }

            try {
                IntPtr scopeObj = new IntPtr((long)hConn);
                const ESteamNetworkingConfigScope scope = ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection;
                if (!ClearVia(_resolved, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, scope, scopeObj)) { return ConnectionResult.Failed; }
                if (!ClearVia(_resolved, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, scope, scopeObj)) { return ConnectionResult.Failed; }
                return VerifyConnection(scopeObj, 0);
            } catch (Exception e) {
                Logger.LogWarning($"Steam transport: per-connection send-rate clear failed ({e.GetType().Name}: {e.Message}).");
                return ConnectionResult.Failed;
            }
        }

        /// <summary>Reads SendRateMin back at Connection scope. expected == 0 means a clear: the
        /// value must now be inherited. Anything else must be set on the connection, at that
        /// value.</summary>
        private static ConnectionResult VerifyConnection(IntPtr scopeObj, int expected) {
            ESteamNetworkingGetConfigValueResult result = ReadRaw(_resolved,
                ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, scopeObj, out int value);
            switch (result) {
                case ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OK:
                    return expected != 0 && value == expected ? ConnectionResult.Ok : ConnectionResult.Failed;
                case ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OKInherited:
                    return expected == 0 ? ConnectionResult.Ok : ConnectionResult.Failed;
                case ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_BadScopeObj:
                    return ConnectionResult.BadHandle;
                default:
                    return ConnectionResult.Failed;
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
            const string reason = "neither Steamworks networking-utils interface is initialised in this process " +
                                  "(crossplay-only, or Steamworks failed to start). Send-rate bounds and Nagle stay at vanilla.";
            PatchGuard.Disable(Mechanism.SteamTransport, reason);
            PatchGuard.Disable(Mechanism.LossBackoff, reason);
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
            ESteamNetworkingGetConfigValueResult result = ReadRaw(api, key,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, out value);
            return result == ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OK
                || result == ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OKInherited;
        }

        /// <summary>One int32 read at any scope, with Steam's own verdict returned as-is. The value
        /// is only meaningful on OK or OKInherited.</summary>
        private static ESteamNetworkingGetConfigValueResult ReadRaw(UtilsApi api, ESteamNetworkingConfigValue key,
                                                                  ESteamNetworkingConfigScope scope, IntPtr scopeObj, out int value) {
            value = 0;
            IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
            try {
                Marshal.WriteInt32(buffer, 0);
                ulong size = sizeof(int);
                ESteamNetworkingGetConfigValueResult result = api == UtilsApi.GameServer
                    ? ReadGameServer(key, scope, scopeObj, buffer, ref size)
                    : ReadClient(key, scope, scopeObj, buffer, ref size);

                if (result == ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OK ||
                    result == ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OKInherited) {
                    value = Marshal.ReadInt32(buffer);
                }
                return result;
            } finally {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // The four interop calls live in their own non-inlined methods so the Steamworks types are
        // only resolved when one is actually invoked, and anything they throw - including a
        // type-load failure - lands in the caller's catch rather than escaping into the patch.
        // Scope and scope object are arguments: Global with a zero object for the process-wide
        // keys, Connection with the handle for one connection's.

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ESteamNetworkingGetConfigValueResult ReadGameServer(
            ESteamNetworkingConfigValue key, ESteamNetworkingConfigScope scope, IntPtr scopeObj, IntPtr buffer, ref ulong size) {
            return SteamGameServerNetworkingUtils.GetConfigValue(key, scope, scopeObj,
                out ESteamNetworkingConfigDataType _, buffer, ref size);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ESteamNetworkingGetConfigValueResult ReadClient(
            ESteamNetworkingConfigValue key, ESteamNetworkingConfigScope scope, IntPtr scopeObj, IntPtr buffer, ref ulong size) {
            return SteamNetworkingUtils.GetConfigValue(key, scope, scopeObj,
                out ESteamNetworkingConfigDataType _, buffer, ref size);
        }

        private static bool WriteVia(UtilsApi api, ESteamNetworkingConfigValue key,
                                     ESteamNetworkingConfigScope scope, IntPtr scopeObj, int value) {
            GCHandle pinned = GCHandle.Alloc(value, GCHandleType.Pinned);
            try {
                return api == UtilsApi.GameServer
                    ? WriteGameServer(key, scope, scopeObj, pinned.AddrOfPinnedObject())
                    : WriteClient(key, scope, scopeObj, pinned.AddrOfPinnedObject());
            } finally {
                pinned.Free();
            }
        }

        /// <summary>A null value at a non-global scope removes the entry, so the object inherits.</summary>
        private static bool ClearVia(UtilsApi api, ESteamNetworkingConfigValue key,
                                     ESteamNetworkingConfigScope scope, IntPtr scopeObj) {
            return api == UtilsApi.GameServer
                ? WriteGameServer(key, scope, scopeObj, IntPtr.Zero)
                : WriteClient(key, scope, scopeObj, IntPtr.Zero);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool WriteGameServer(ESteamNetworkingConfigValue key, ESteamNetworkingConfigScope scope, IntPtr scopeObj, IntPtr value) {
            return SteamGameServerNetworkingUtils.SetConfigValue(key, scope, scopeObj,
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool WriteClient(ESteamNetworkingConfigValue key, ESteamNetworkingConfigScope scope, IntPtr scopeObj, IntPtr value) {
            return SteamNetworkingUtils.SetConfigValue(key, scope, scopeObj,
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, value);
        }
    }
}
