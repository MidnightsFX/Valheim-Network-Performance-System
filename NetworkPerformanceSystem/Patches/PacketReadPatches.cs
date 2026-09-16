using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System.IO;
using System.Reflection;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M16 - reads an incoming ZDO payload straight into the buffer that is about to hold it.
    ///
    /// Vanilla:
    ///
    ///     byte[] buffer = m_reader.ReadBytes(m_reader.ReadInt32());
    ///     pkg.Clear();
    ///     pkg.m_stream.Write(buffer, 0, buffer.Length);
    ///     pkg.m_stream.Position = 0;
    ///
    /// ReadBytes allocates a fresh array, which is copied into the target and dropped: two copies
    /// and one throwaway array where one copy and no array will do.
    ///
    /// This overload has exactly one caller in the whole game - ZDOMan.RPC_ZDOData, once per
    /// received ZDO - so it runs at the same rate as M15 and in the same method, which is why the
    /// two are meant to be soaked together. It is also the only one of the four with no other mod
    /// anywhere near it: nobody else patches ZPackage.
    ///
    /// The parameterless ReadPackage() overload is deliberately left alone. It allocates a whole
    /// ZPackage as well as the same intermediate array, but the package it returns escapes to an
    /// RPC handler that may retain it. M18 touches that call site instead, and keeps the
    /// allocation on purpose.
    /// </summary>
    [HarmonyPatch]
    internal static class PacketReadPatches {

        [HarmonyPrepare]
        private static bool Prepare() {
            if (!AllocationRelief.PacketReadWanted) {
                Logger.LogInfo("Packet read fast path is off, so its hook was not installed. " +
                               "Turning it on takes effect after a restart.");
                return false;
            }

            FieldInfo reader = AccessTools.Field(typeof(ZPackage), "m_reader");
            FieldInfo stream = AccessTools.Field(typeof(ZPackage), "m_stream");

            if (reader == null || stream == null
                || !typeof(BinaryReader).IsAssignableFrom(reader.FieldType)
                || !typeof(MemoryStream).IsAssignableFrom(stream.FieldType)) {
                PatchGuard.Disable(Mechanism.PacketReadAlloc,
                    "ZPackage does not have the expected m_reader (BinaryReader) and m_stream (MemoryStream) fields. " +
                    "Either the game updated or another mod rewrote it first. Packet reads stay at vanilla.");
                return false;
            }

            AllocationRelief.PacketReadHookInstalled = true;
            return true;
        }

        /// <summary>
        /// true  -> vanilla ReadPackage runs
        /// false -> the target already holds exactly the bytes vanilla would have given it
        ///
        /// GetBuffer() cannot throw here. Every ZPackage constructor writes into an expandable
        /// MemoryStream the ZPackage created for itself and never one wrapping a caller's array,
        /// so the buffer is always publicly exposable. SetLength grows the capacity and zero-fills
        /// the region we are about to overwrite, and leaves Position where Clear() put it.
        ///
        /// The short-read branch is not a safety net, it is fidelity: BinaryReader.ReadBytes
        /// returns a SHORTER array at end of stream rather than throwing, so vanilla leaves the
        /// target holding however many bytes actually arrived. Trimming to what we read reproduces
        /// that exactly, so a truncated packet behaves the same as it does today.
        /// </summary>
        [HarmonyPatch(typeof(ZPackage), nameof(ZPackage.ReadPackage), new[] { typeof(ZPackage) }, new[] { ArgumentType.Ref })]
        [HarmonyPrefix]
        private static bool ReadIntoTargetBuffer(ZPackage __instance, ref ZPackage pkg, bool __runOriginal) {
            if (!__runOriginal) { return true; }
            if (!AllocationRelief.PacketReadActive) { return true; }
            if (pkg == null) { return true; }

            int length = __instance.m_reader.ReadInt32();
            pkg.Clear();

            if (length > 0) {
                pkg.m_stream.SetLength(length);
                byte[] destination = pkg.m_stream.GetBuffer();

                int read = 0;
                while (read < length) {
                    int got = __instance.m_reader.Read(destination, read, length - read);
                    if (got <= 0) { break; }
                    read += got;
                }

                if (read < length) { pkg.m_stream.SetLength(read); }
            }

            pkg.m_stream.Position = 0L;
            AllocationRelief.PacketReadFast++;
            return false;
        }
    }
}
