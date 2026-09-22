using System.Collections.Generic;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// What the host will accept from a client, and how fast.
    ///
    /// A client's records are written into the host's files verbatim, so they are untrusted
    /// input headed for disk. The host never parses them; it checks their shape instead: a line
    /// must be a client record (every client kind starts "c_", and no host kind does, so an
    /// upload can never impersonate the host's own records), must be short, must end where a JSON
    /// object ends, and must be printable ASCII with no line breaks. Anything else is dropped and
    /// counted. Disk use is bounded separately, by the byte budget here and the writer's cap.
    ///
    /// No Unity or game types, so all of it can be exercised offline.
    /// </summary>
    internal static class MonitoringBatch {

        internal const string ClientLinePrefix = "{\"k\":\"c_";

        internal const int MaxLineChars = 2048;
        internal const int MaxBatchChars = 32768;
        internal const int MaxBatchLines = 2000;

        /// <summary>The package carries a few fixed fields besides the payload, and a string is
        /// length-prefixed; anything past this is not a batch this mod built.</summary>
        internal const int MaxPackageBytes = MaxBatchChars + 256;

        internal static bool IsValidClientLine(string payload, int start, int length) {
            if (length <= ClientLinePrefix.Length || length > MaxLineChars) { return false; }
            if (string.CompareOrdinal(payload, start, ClientLinePrefix, 0, ClientLinePrefix.Length) != 0) { return false; }
            if (payload[start + length - 1] != '}') { return false; }

            int end = start + length;
            for (int i = start; i < end; i++) {
                char c = payload[i];
                if (c < 0x20 || c > 0x7E) { return false; }
            }
            return true;
        }

        /// <summary>
        /// Split a newline-joined payload into its valid lines. Returns how many were rejected.
        /// A payload over the size or line limits is rejected whole: a client built by this mod
        /// never produces one, so there is nothing in it worth salvaging.
        /// </summary>
        internal static int Split(string payload, List<string> valid) {
            if (string.IsNullOrEmpty(payload)) { return 0; }
            if (payload.Length > MaxBatchChars) { return 1; }

            int rejected = 0;
            int lines = 0;
            int start = 0;
            while (start < payload.Length) {
                int end = payload.IndexOf('\n', start);
                if (end < 0) { end = payload.Length; }

                int length = end - start;
                if (length > 0) {
                    if (++lines > MaxBatchLines) { return rejected + 1; }
                    if (IsValidClientLine(payload, start, length)) {
                        valid.Add(payload.Substring(start, length));
                    } else {
                        rejected++;
                    }
                }
                start = end + 1;
            }
            return rejected;
        }
    }

    /// <summary>
    /// A token bucket in bytes. The client uses one to stay under the upload rate the host asked
    /// for; the host uses one per peer, several times looser, so that a client ignoring the
    /// request still cannot fill the disk.
    /// </summary>
    internal sealed class ByteBudget {

        private readonly double _bytesPerSecond;
        private readonly double _capacity;
        private double _tokens;
        private double _lastSeconds;

        internal ByteBudget(double bytesPerSecond, double burstSeconds, double minimumCapacity, double nowSeconds) {
            _bytesPerSecond = bytesPerSecond;
            // A bucket smaller than one full batch could never pay for one, and the sender would
            // stall forever rather than merely slowly.
            _capacity = System.Math.Max(bytesPerSecond * burstSeconds, minimumCapacity);
            _tokens = _capacity;
            _lastSeconds = nowSeconds;
        }

        internal bool TryTake(int bytes, double nowSeconds) {
            double elapsed = nowSeconds - _lastSeconds;
            if (elapsed > 0d) {
                _tokens = System.Math.Min(_capacity, _tokens + elapsed * _bytesPerSecond);
                _lastSeconds = nowSeconds;
            }

            if (bytes > _tokens) { return false; }
            _tokens -= bytes;
            return true;
        }
    }
}
