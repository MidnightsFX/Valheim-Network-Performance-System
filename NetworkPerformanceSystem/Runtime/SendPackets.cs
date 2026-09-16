namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// M17 - the two packages ZDOMan.SendZDOs builds, kept between calls instead of rebuilt.
    ///
    /// A ZPackage is a MemoryStream plus a BinaryWriter plus a BinaryReader, each with its own
    /// internal buffer and encoder, so one construction is four objects certainly and closer to
    /// ten in practice. The outer one then grows to packet size by doubling - 256, 512, ... 16384
    /// for a 10 KB packet - which is another seven arrays discarded on the way up. Two of these
    /// per call, at a call per peer per send tick.
    ///
    /// WHY REUSE IS SAFE. Nothing retains either package past the call:
    ///
    ///   * the outer one goes to ZRpc.Invoke, which copies it into ZRpc's own package via
    ///     ZPackage.Write(ZPackage) - and that reads through GetArray(), which honours Length, so
    ///     a reused buffer with spare capacity contributes nothing extra;
    ///   * ZSteamSocket.Send then copies again into the socket's send queue;
    ///   * the inner one is already Clear()ed per ZDO by vanilla, so it never held anything across
    ///     iterations in the first place.
    ///
    /// And SendZDOs is not re-entrant: the frame update drives it once per peer sequentially,
    /// whether through vanilla's round-robin or this mod's own scheduler, and the save-time
    /// SendAllZDOs loops it sequentially too.
    ///
    /// THE ONE ASSUMPTION is that no other mod hooks ZRpc.Invoke and stashes the outgoing package
    /// for a later frame; that mod would see it overwritten underneath it. It is an odd thing to
    /// do and nothing known does it, but it is unverifiable from here, and it is why this
    /// mechanism has its own switch and is the last of the four.
    ///
    /// Two separate instances because the outer packet and the per-ZDO scratch are both live at
    /// the same time - the scratch is serialised into and then written into the outer one.
    /// </summary>
    internal static class SendPackets {

        private static readonly ZPackage OuterPacket = new ZPackage();
        private static readonly ZPackage ScratchPacket = new ZPackage();

        /// <summary>
        /// Called from the rewritten IL in ZDOMan.SendZDOs, once per send attempt. Sits directly
        /// in the send path: must be cheap and must never throw.
        ///
        /// The disabled branch hands back a fresh package, which is byte-for-byte what the
        /// instruction we replaced did - so this mechanism can be switched off live without
        /// unpatching anything, the same way M2's rewritten window sites fall back to vanilla's
        /// constant. That is also why the transpiler runs even when the setting starts off: a
        /// predictable branch on a method that already does a Steam round trip is not a cost worth
        /// a restart, unlike the wrapper the other three avoid.
        /// </summary>
        internal static ZPackage Outer() {
            if (!AllocationRelief.SendPackageReuseActive) { return new ZPackage(); }

            // Counted here rather than in Scratch(): both are reached exactly once per call, so
            // one of them has to do it and the outer one comes first.
            AllocationRelief.SendPackagesReused++;
            OuterPacket.Clear();
            return OuterPacket;
        }

        /// <summary>The per-ZDO scratch buffer. Same contract as Outer().</summary>
        internal static ZPackage Scratch() {
            if (!AllocationRelief.SendPackageReuseActive) { return new ZPackage(); }

            ScratchPacket.Clear();
            return ScratchPacket;
        }
    }
}
