using System.Globalization;
using System.Text;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// Builds one monitoring record: a single-line JSON object whose first member is its kind.
    ///
    /// Written by hand rather than through a serialiser for two reasons. The records are built on
    /// the main thread inside network handlers, so the cost has to be a StringBuilder append and
    /// nothing else; and the output has to be plain ASCII, because lines built on a client are
    /// validated byte-for-byte by the host before they reach a file (see MonitoringBatch).
    ///
    /// No Unity or game types, on purpose - that is what lets the format be checked offline.
    ///
    /// Session ids and ZDO ids are written as strings. They are 64-bit, and a JSON reader that
    /// goes through a double silently corrupts anything above 2^53.
    /// </summary>
    internal sealed class JsonLine {

        private readonly StringBuilder _sb = new StringBuilder(256);

        internal JsonLine Begin(string kind) {
            _sb.Length = 0;
            _sb.Append("{\"k\":\"").Append(kind).Append('"');
            return this;
        }

        internal JsonLine Int(string key, long value) {
            Key(key);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        /// <summary>A 64-bit id, quoted. See the class remarks.</summary>
        internal JsonLine Id(string key, long value) {
            Key(key);
            _sb.Append('"').Append(value.ToString(CultureInfo.InvariantCulture)).Append('"');
            return this;
        }

        internal JsonLine Num(string key, double value, string format = "0.###") {
            Key(key);
            if (double.IsNaN(value) || double.IsInfinity(value)) {
                _sb.Append("null");
            } else {
                _sb.Append(value.ToString(format, CultureInfo.InvariantCulture));
            }
            return this;
        }

        internal JsonLine Flag(string key, bool value) {
            Key(key);
            _sb.Append(value ? "true" : "false");
            return this;
        }

        internal JsonLine Str(string key, string value) {
            Key(key);
            AppendString(_sb, value);
            return this;
        }

        /// <summary>A member whose value is already JSON - an array or object built elsewhere.</summary>
        internal JsonLine Raw(string key, string json) {
            Key(key);
            _sb.Append(string.IsNullOrEmpty(json) ? "null" : json);
            return this;
        }

        internal string End() {
            _sb.Append('}');
            return _sb.ToString();
        }

        private void Key(string key) {
            _sb.Append(",\"").Append(key).Append("\":");
        }

        /// <summary>
        /// Quoted and escaped down to printable ASCII. Everything outside 0x20-0x7E becomes a
        /// \uXXXX escape - a modded prefab can be named in any script, and the host rejects a
        /// client line containing anything else.
        /// </summary>
        internal static void AppendString(StringBuilder sb, string value) {
            if (value == null) { sb.Append("null"); return; }

            sb.Append('"');
            for (int i = 0; i < value.Length; i++) {
                char c = value[i];
                if (c == '"' || c == '\\') {
                    sb.Append('\\').Append(c);
                } else if (c < 0x20 || c > 0x7E) {
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                } else {
                    sb.Append(c);
                }
            }
            sb.Append('"');
        }
    }
}
