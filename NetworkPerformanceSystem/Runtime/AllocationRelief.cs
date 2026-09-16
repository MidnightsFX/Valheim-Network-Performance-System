namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M15-M19 - shared state for the network-path allocation removals, and the counters
    /// <c>nps_stats</c> reports them with.
    ///
    /// None of them changes a single byte on the wire or a single value in a ZDO. All any of
    /// them does is stop the game creating short-lived objects on the hottest paths a server
    /// has: one per received ZDO (M15, M16), one per send tick per peer (M17), one per inbound
    /// RPC (M18), one per relayed RPC per recipient (M19).
    ///
    /// WHAT THAT IS AND IS NOT WORTH, because it is easy to oversell:
    ///
    /// Mono's collector walks live objects, and the mark stack it walks them with holds a fixed
    /// number of ENTRIES, not bytes. A large world is millions of live field tables - one or two
    /// per ZDO - and that live set is a function of how much world has been generated. Nothing
    /// here reduces it by a single object. What these reduce is the RATE at which new objects
    /// appear, which is what decides how OFTEN the collector runs. Fewer collections is less CPU
    /// spent marking and fewer chances to hit the ceiling, so the interval between incidents gets
    /// longer. The ceiling itself does not move.
    ///
    /// The counters are therefore counts of operations taken on the fast path, not bytes saved.
    /// Bytes would be an estimate dressed up as a measurement; counts are the thing the collector
    /// actually responds to. They are monotonic since startup - sample twice and difference them
    /// for a rate. <c>long</c> rather than <c>int</c> on purpose: at a few hundred received ZDOs a
    /// second an int wraps inside a couple of months, which is less than a dedicated server's
    /// uptime between restarts.
    /// </summary>
    internal static class AllocationRelief {

        /// <summary>Received ZDOs whose fields were read without constructing the fourteen
        /// delegates ZDO.Deserialize allocates unconditionally. M15.</summary>
        internal static long DeserializeFast;

        /// <summary>Packet reads that went straight into the target's buffer instead of through a
        /// throwaway intermediate array. One per received ZDO. M16.</summary>
        internal static long PacketReadFast;

        /// <summary>ZDOMan.SendZDOs calls that used the reused packages rather than building two
        /// fresh ones. One per send tick per peer. M17.</summary>
        internal static long SendPackagesReused;

        /// <summary>Inbound RPCs dispatched through the typed call instead of DynamicInvoke. M18.
        /// </summary>
        internal static long RpcFastPath;

        /// <summary>Relayed RoutedRPC deliveries sent from the reused frame instead of through a
        /// per-message package and a per-recipient ZRpc.Invoke. One per recipient. M19.</summary>
        internal static long RelaySendsReused;

        /// <summary>
        /// Whether each hook is actually attached this session.
        ///
        /// M15, M16 and M18 refuse to install at all when their setting is off at startup, because
        /// a Harmony wrapper on a method this hot costs the same whether the body does the work or
        /// returns immediately - and on ZDO.Deserialize that is a wrapper per replicated object in
        /// every packet, forever, on a server that asked for none of this. The price is that
        /// turning one ON needs a restart, which is why the report says so rather than leaving an
        /// admin to wonder why the counter is not moving. Turning one OFF works live: the prefixes
        /// re-check the setting on every call.
        ///
        /// M17 is a transpiler and has no wrapper to avoid, so it follows M2's pattern instead -
        /// always rewritten, live-toggled inside SendPackets. M19 rides on the relay filter's
        /// existing RouteRPC prefix, so it has no wrapper of its own either and toggles live.
        /// </summary>
        internal static bool DeserializeHookInstalled;
        internal static bool PacketReadHookInstalled;
        internal static bool RpcInvokeHookInstalled;

        internal static bool DeserializeWanted =>
            ValConfig.EnableDeserializeFastPath != null && ValConfig.EnableDeserializeFastPath.Value;

        internal static bool PacketReadWanted =>
            ValConfig.EnablePacketReadFastPath != null && ValConfig.EnablePacketReadFastPath.Value;

        internal static bool SendPackageReuseWanted =>
            ValConfig.EnableSendPackageReuse != null && ValConfig.EnableSendPackageReuse.Value;

        internal static bool RpcInvokeWanted =>
            ValConfig.EnableRpcInvokeFastPath != null && ValConfig.EnableRpcInvokeFastPath.Value;

        internal static bool RelaySendWanted =>
            ValConfig.EnableRelaySendReuse != null && ValConfig.EnableRelaySendReuse.Value;

        internal static bool DeserializeActive =>
            PatchGuard.IsActive(Mechanism.DeserializeAlloc) && DeserializeWanted;

        internal static bool PacketReadActive =>
            PatchGuard.IsActive(Mechanism.PacketReadAlloc) && PacketReadWanted;

        internal static bool SendPackageReuseActive =>
            PatchGuard.IsActive(Mechanism.SendPacketReuse) && SendPackageReuseWanted;

        internal static bool RpcInvokeActive =>
            PatchGuard.IsActive(Mechanism.RpcInvokeFastPath) && RpcInvokeWanted;

        internal static bool RelaySendActive =>
            PatchGuard.IsActive(Mechanism.RelaySendReuse) && RelaySendWanted;

        /// <summary>
        /// Vanilla's own per-second ZDO counters, refreshed once a second in ZDOMan.UpdateStats.
        /// They are free int reads and they are the honest denominator for every figure above -
        /// "deserializeFast is climbing" means nothing without knowing how many ZDOs are arriving.
        /// Nothing else on a server reports them.
        /// </summary>
        internal static bool TryGetZdoRates(out int sentPerSecond, out int recvPerSecond) {
            ZDOMan man = ZDOMan.instance;
            if (man == null) {
                sentPerSecond = 0;
                recvPerSecond = 0;
                return false;
            }
            sentPerSecond = man.GetSentZDOs();
            recvPerSecond = man.GetRecvZDOs();
            return true;
        }
    }
}
