using HarmonyLib;
using NetworkPerformanceSystem.Runtime;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace NetworkPerformanceSystem.Patches {

    /// <summary>
    /// M18 - delivers an inbound RPC by calling its handler, instead of going through reflection.
    ///
    /// ZRpc.RpcMethod&lt;T&gt;.Invoke is one line:
    ///
    ///     m_action.DynamicInvoke(ZRpc.Deserialize(rpc, m_action.Method.GetParameters(), pkg));
    ///
    /// which per inbound RPC costs a freshly allocated ParameterInfo[] from GetParameters, a
    /// List&lt;object&gt; and its backing array and the ToArray copy of it inside Deserialize, a
    /// reflective signature walk, and a box per value-type argument - about five allocations plus
    /// the reflective invoke, before the handler has run at all. ZDOData is the hot one, at one
    /// per peer per that peer's send rotation, with RoutedRPC and everything else carrying a
    /// package behind it.
    ///
    /// The fast path handles the (ZRpc, ZPackage) shape, which is ZDOData, RoutedRPC and the large
    /// majority of mod-registered handlers. Everything else returns true and runs vanilla.
    ///
    /// THE MONO TRAP. Mono compiles one body for all reference-type instantiations of a generic
    /// type, so a patch aimed at RpcMethod&lt;ZPackage&gt; also executes for RpcMethod&lt;string&gt;
    /// and RpcMethod&lt;List&lt;string&gt;&gt;. That is why the guard below is a runtime type test on
    /// the actual delegate rather than a comparison against typeof(T), and why the cached verdict
    /// is keyed on the RpcMethod instance rather than on any generic context. If a given runtime
    /// does NOT share the body, the only consequence is that the other instantiations keep using
    /// vanilla's path - correct either way, which is the point of testing the delegate.
    ///
    /// RpcMethod&lt;ZDOID&gt; (ZDOMan.RPC_RequestZDO) has a value-type argument, so Mono compiles it
    /// separately and it is not reached from here. Its rate is far lower; it is only worth its own
    /// hook if nps_stats ever suggests otherwise.
    /// </summary>
    [HarmonyPatch]
    internal static class RpcInvokePatches {

        private static MethodBase invokeTarget;

        /// <summary>
        /// Stored against a rejected RpcMethod so the "run vanilla" answer is cached too, rather
        /// than re-derived on every message. Never called; compared by reference only.
        /// </summary>
        private static readonly Action<ZRpc, ZPackage> NotEligible = (rpc, pkg) => { };

        /// <summary>
        /// The verdict per RpcMethod instance: either the handler to call directly, or NotEligible.
        ///
        /// A ConditionalWeakTable rather than a Dictionary, because RpcMethod instances are created
        /// per registration PER PEER - every connection builds its own set - so a strong-keyed
        /// cache would pin every handler and its target from every peer that ever connected, for
        /// the life of the process. That is a memory leak, which would be an absurd way for an
        /// allocation-reduction mechanism to fail. Weak keys let a disconnected peer's entries go
        /// with it, and the classification cost is paid once per instance either way.
        /// </summary>
        private static readonly ConditionalWeakTable<object, Action<ZRpc, ZPackage>> Verdicts =
            new ConditionalWeakTable<object, Action<ZRpc, ZPackage>>();

        private static readonly ConditionalWeakTable<object, Action<ZRpc, ZPackage>>.CreateValueCallback Classifier =
            Classify;

        /// <summary>
        /// Resolves the private nested generic and its private delegate field, and declines to
        /// install unless someone asked for this. The wrapper argument is the same one M15 makes:
        /// this sits on the delivery path of every RPC in the game, so a server that has not
        /// enabled it should not carry a Harmony wrapper there. Turning the setting on therefore
        /// needs a restart; turning it off is immediate.
        /// </summary>
        [HarmonyPrepare]
        private static bool Prepare() {
            if (!AllocationRelief.RpcInvokeWanted) {
                Logger.LogInfo("RPC invoke fast path is off, so its hook was not installed. " +
                               "Turning it on takes effect after a restart.");
                return false;
            }

            Type generic = AccessTools.Inner(typeof(ZRpc), "RpcMethod`1");
            if (generic == null || !generic.IsGenericTypeDefinition) {
                PatchGuard.Disable(Mechanism.RpcInvokeFastPath,
                    "ZRpc has no nested generic RpcMethod<T>. Either the game updated or another mod rewrote ZRpc's " +
                    "dispatch first. Inbound RPCs are delivered exactly as vanilla.");
                return false;
            }

            // Patch one closed instantiation. Under Mono's shared generic code that reaches the
            // other reference-type ones as well; where it does not, they simply stay vanilla.
            Type closed = generic.MakeGenericType(typeof(ZPackage));
            FieldInfo action = AccessTools.Field(closed, "m_action");
            MethodBase invoke = AccessTools.Method(closed, "Invoke", new[] { typeof(ZRpc), typeof(ZPackage) });

            if (action == null || invoke == null || action.FieldType != typeof(Action<ZRpc, ZPackage>)) {
                PatchGuard.Disable(Mechanism.RpcInvokeFastPath,
                    "ZRpc.RpcMethod<T> does not have the expected Invoke(ZRpc, ZPackage) and Action<ZRpc, T> m_action. " +
                    "Either the game updated or another mod rewrote ZRpc's dispatch first. Inbound RPCs are delivered " +
                    "exactly as vanilla.");
                return false;
            }

            invokeTarget = invoke;
            AllocationRelief.RpcInvokeHookInstalled = true;
            return true;
        }

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() => invokeTarget;

        /// <summary>
        /// true  -> vanilla Invoke runs (stood down, switched off, an unrecognised handler shape,
        ///          or something ahead of us already cancelled)
        /// false -> the handler has been called with exactly the argument vanilla would have built
        ///
        /// __instance is typed as object deliberately: under shared generic code the real instance
        /// may be an RpcMethod of some other reference type, and naming a concrete one here would
        /// be a lie the runtime is under no obligation to honour.
        /// </summary>
        [HarmonyPrefix]
        private static bool InvokeTypedHandler(object __instance, ZRpc rpc, ZPackage pkg, bool __runOriginal) {
            if (!__runOriginal) { return true; }
            if (!AllocationRelief.RpcInvokeActive) { return true; }
            if (__instance == null || pkg == null) { return true; }

            Action<ZRpc, ZPackage> handler = Verdicts.GetValue(__instance, Classifier);
            if (ReferenceEquals(handler, NotEligible)) { return true; }

            // Read the argument OUTSIDE the try. Vanilla deserialises before it invokes, so a
            // malformed or truncated packet throws from the read, unwrapped; only a fault inside
            // the handler is wrapped. Reading in here would re-label the first kind as the second.
            ZPackage argument = pkg.ReadPackage();

            AllocationRelief.RpcFastPath++;

            // DynamicInvoke wraps whatever the handler throws in a TargetInvocationException, and
            // ZRpc.Update logs the exception's full ToString. Letting one through bare would change
            // the text of every handler-fault line in the log, which is the sort of difference that
            // costs somebody an afternoon. A try/catch costs nothing until it fires.
            try {
                handler(rpc, argument);
            } catch (Exception ex) {
                throw new TargetInvocationException(ex);
            }

            return false;
        }

        /// <summary>
        /// Decides once, per RpcMethod instance, whether its handler can be called directly.
        ///
        /// The signature check is not redundant with the type test. A delegate built over a static
        /// method with a bound first argument is still an Action&lt;ZRpc, ZPackage&gt;, but its
        /// MethodInfo reports three parameters - and vanilla would then read TWO values out of the
        /// package and hand three arguments to a two-argument delegate. That is broken in vanilla
        /// too, but it is broken in a specific way, and reproducing the game's behaviour beats
        /// quietly improving on it. Anything that is not the plain two-parameter shape falls
        /// through to vanilla, which is the safe direction. Closed instance delegates and lambdas -
        /// the shapes the game and mods actually register - both report exactly two.
        ///
        /// This is the call the whole mechanism exists to avoid, so it must happen once and never
        /// again for that instance.
        /// </summary>
        private static Action<ZRpc, ZPackage> Classify(object rpcMethod) {
            FieldInfo field = AccessTools.Field(rpcMethod.GetType(), "m_action");
            if (field == null) { return NotEligible; }

            if (!(field.GetValue(rpcMethod) is Action<ZRpc, ZPackage> typed)) { return NotEligible; }

            ParameterInfo[] parameters = typed.Method.GetParameters();
            if (parameters.Length != 2
                || parameters[0].ParameterType != typeof(ZRpc)
                || parameters[1].ParameterType != typeof(ZPackage)) {
                return NotEligible;
            }

            return typed;
        }
    }
}
