using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// Names for the method hashes on the wire. A package carries only
    /// string.GetStableHashCode() of its method, and the game keeps no reverse table, so names
    /// are collected from the places that still have the string:
    ///
    ///   * every ZRpc.Invoke and ZRoutedRpc.InvokeRoutedRPC on this machine, which take the name
    ///     (MonitoringPatches). ZNetView.InvokeRPC and Jotunn's CustomRPC both end up in the
    ///     latter, so this covers object RPCs and every mod's CustomRPC as soon as it is sent;
    ///   * Jotunn's own list of CustomRPCs, read on the host before each traffic report, so a mod
    ///     that only ever receives on the host is named too.
    ///
    /// A client sends what it learns to the host as "c_names" records - a routed RPC only clients
    /// send is otherwise a bare hash in the host's traffic records. The host writes those records
    /// as it writes any client line, unread, and whoever reads the folder joins them up.
    /// </summary>
    internal static class TrafficNames {

        private static readonly Dictionary<int, string> ByHash = new Dictionary<int, string>();
        private static readonly HashSet<string> Seen = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<KeyValuePair<int, string>> Unreported = new List<KeyValuePair<int, string>>();

        /// <summary>A c_names line stops taking names past this many characters. One more name can
        /// follow the check, at most 6 x MaxNameChars escaped plus its key, which still leaves the
        /// line inside the host's 2048-character client line limit.</summary>
        private const int MaxNamesLineChars = 1300;

        /// <summary>A name longer than this is not a method name anyone chose. Jotunn's
        /// "Mod.GUID!NAME" IDs run to about 50.</summary>
        private const int MaxNameChars = 100;

        private static readonly StringBuilder Scratch = new StringBuilder(MaxNamesLineChars + MaxNameChars + 64);

        private static FieldInfo _jotunnRpcs;
        private static PropertyInfo _jotunnRpcId;
        private static bool _jotunnUnavailable;

        internal static void Learn(string name) {
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameChars) { return; }
            if (!Seen.Add(name)) { return; }

            int hash = name.GetStableHashCode();
            if (ByHash.ContainsKey(hash)) { return; }
            ByHash[hash] = name;
            Unreported.Add(new KeyValuePair<int, string>(hash, name));
        }

        internal static bool TryGet(int hash, out string name) {
            return ByHash.TryGetValue(hash, out name);
        }

        /// <summary>The name, or "#hash" for one nobody has named yet.</summary>
        internal static string Describe(int hash) {
            if (ByHash.TryGetValue(hash, out string name)) { return name; }
            return "#" + hash.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Every CustomRPC Jotunn has registered, by the ID it registers with ZRoutedRpc. Its list
        /// and the ID are internal to Jotunn, so this reads them by reflection and gives up quietly,
        /// for the session, if a Jotunn version has moved them.
        /// </summary>
        internal static void LearnJotunnRpcs() {
            if (_jotunnUnavailable) { return; }
            try {
                if (_jotunnRpcs == null) {
                    _jotunnRpcs = AccessTools.Field(typeof(Jotunn.Managers.NetworkManager), "RPCs");
                    _jotunnRpcId = AccessTools.Property(typeof(Jotunn.Entities.CustomRPC), "ID");
                    if (_jotunnRpcs == null || _jotunnRpcId == null) {
                        _jotunnUnavailable = true;
                        return;
                    }
                }

                if (!(_jotunnRpcs.GetValue(Jotunn.Managers.NetworkManager.Instance) is IEnumerable rpcs)) { return; }
                foreach (object rpc in rpcs) {
                    if (rpc != null) { Learn(_jotunnRpcId.GetValue(rpc, null) as string); }
                }
            } catch (Exception e) {
                _jotunnUnavailable = true;
                Logger.LogDebug($"Network monitoring could not read Jotunn's CustomRPC list ({e.GetType().Name}: {e.Message}); those names come from sends only.");
            }
        }

        /// <summary>Lines per call, so a backlog - every name again, when a recording starts - goes
        /// out over a few reports instead of as one burst over the client's upload budget.</summary>
        private const int MaxLinesPerReport = 2;

        /// <summary>
        /// What this machine has learned and not yet reported, as c_names lines of at most
        /// MaxNamesLineChars each. Called with MonitoringClient's self report, on every role.
        /// </summary>
        internal static void ReportLearned(double nowMs) {
            if (Unreported.Count == 0) { return; }

            int i = 0;
            int lines = 0;
            while (i < Unreported.Count && lines < MaxLinesPerReport) {
                lines++;
                Scratch.Length = 0;
                bool first = true;
                while (i < Unreported.Count && (first || Scratch.Length < MaxNamesLineChars)) {
                    if (!first) { Scratch.Append(','); }
                    first = false;
                    Scratch.Append('"').Append(Unreported[i].Key.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\":");
                    JsonLine.AppendString(Scratch, Unreported[i].Value);
                    i++;
                }

                Monitoring.EmitClient(Monitoring.Line.Begin("c_names")
                    .Num("ct", nowMs, "0.#")
                    .Raw("names", "{" + Scratch + "}")
                    .End());
            }
            Unreported.RemoveRange(0, i);
        }

        /// <summary>A new recording should hear every name again: its reader has none of them.</summary>
        internal static void ReportAllAgain() {
            Unreported.Clear();
            foreach (KeyValuePair<int, string> entry in ByHash) { Unreported.Add(entry); }
        }
    }
}
