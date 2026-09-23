using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace CodingRiver.UPilot
{
    internal sealed class UPilotTestRunnerAdapter
    {
        private static readonly Dictionary<Type, UPilotTestRunnerAdapter> Cache = new Dictionary<Type, UPilotTestRunnerAdapter>();
        private readonly Type _api;
        internal readonly string Identity;

        private UPilotTestRunnerAdapter(Type api)
        {
            _api = api;
            Identity = api.Assembly.FullName;
        }

        internal MethodInfo Cancel => UPilotTestService.ResolveCancelMethod(_api);

        internal MethodInfo Unregister
        {
            get
            {
                var methods = _api.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Static | BindingFlags.Instance);
                var method = methods.FirstOrDefault(candidate => candidate.Name == "UnregisterTestCallback"
                    && candidate.IsPublic && candidate.IsStatic && candidate.IsGenericMethodDefinition
                    && candidate.GetGenericArguments().Length == 1 && candidate.GetParameters().Length == 1
                    && candidate.GetParameters()[0].ParameterType == candidate.GetGenericArguments()[0]);
                if (method != null) return method;
                return methods.FirstOrDefault(candidate => candidate.Name == "UnregisterCallbacks"
                    && candidate.IsPublic && !candidate.IsStatic && candidate.IsGenericMethodDefinition
                    && candidate.GetGenericArguments().Length == 1 && candidate.GetParameters().Length == 1
                    && candidate.GetParameters()[0].ParameterType == candidate.GetGenericArguments()[0])
                    ?? throw BindingFailure(_api, "UnregisterTestCallback<T>(T) or UnregisterCallbacks<T>(T)");
            }
        }

        internal static UPilotTestRunnerAdapter Get(Type api)
        {
            if (api == null)
                throw new MissingMemberException("TEST_RUNNER_BINDING_UNAVAILABLE: TestRunnerApi type is unavailable.");
            if (!Cache.TryGetValue(api, out var adapter)) Cache[api] = adapter = new UPilotTestRunnerAdapter(api);
            return adapter;
        }

        private static MissingMemberException BindingFailure(Type api, string required, Type candidateType = null)
        {
            Type inspectedType = candidateType ?? api;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.Instance;
            string candidates = string.Join("; ", inspectedType.GetMembers(flags)
                .Where(member => member.Name.IndexOf("Runner", StringComparison.OrdinalIgnoreCase) >= 0
                    || member.Name.IndexOf("Run", StringComparison.OrdinalIgnoreCase) >= 0
                    || member.Name.IndexOf("Callback", StringComparison.OrdinalIgnoreCase) >= 0
                    || member.Name.IndexOf("Job", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(member => member.MemberType + " " + member)
                .Take(24));
            return new MissingMemberException("TEST_RUNNER_BINDING_UNAVAILABLE: assembly=" + api.Assembly.FullName
                + "; version=" + api.Assembly.GetName().Version
                + "; type=" + api.FullName
                + "; inspectedType=" + inspectedType.FullName
                + "; required=" + required
                + "; candidates=" + (string.IsNullOrEmpty(candidates) ? "(none)" : candidates));
        }

        internal string Probe(string runGuid, out object runner, out string diagnostic)
        {
            runner = null;
            diagnostic = Identity;
            try
            {
                if (string.IsNullOrWhiteSpace(runGuid)) throw new InvalidOperationException("Missing runGuid.");
                const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                const BindingFlags instanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var holderProperty = _api.GetProperty("m_testJobDataHolder", staticFlags);
                object holder = holderProperty != null && holderProperty.GetMethod != null
                    && holderProperty.GetMethod.IsStatic && holderProperty.GetIndexParameters().Length == 0
                    ? holderProperty.GetValue(null) : null;
                if (holder == null)
                {
                    var holderType = _api.Assembly.GetType("UnityEditor.TestTools.TestRunner.TestRun.TestJobDataHolder")
                        ?? throw BindingFailure(_api, "TestJobDataHolder type");
                    var singleton = holderType.GetProperty("instance", staticFlags);
                    holder = singleton != null && singleton.GetIndexParameters().Length == 0
                        ? singleton.GetValue(null) : holderType.GetField("instance", staticFlags)?.GetValue(null);
                }
                if (holder == null) throw new InvalidOperationException("UTF holder is not initialized.");
                var getRunner = holder.GetType().GetMethod("GetRunner", instanceFlags, null,
                    new[] { typeof(string) }, null);
                if (getRunner != null)
                {
                    runner = getRunner.Invoke(holder, new object[] { runGuid });
                    if (runner != null) return "active";
                }
                var runs = (holder.GetType().GetField("TestRuns", instanceFlags)?.GetValue(holder)
                    ?? holder.GetType().GetProperty("TestRuns", instanceFlags)?.GetValue(holder)) as IEnumerable;
                if (runs == null) throw new InvalidOperationException("Serialized UTF run inventory is unavailable.");
                foreach (var run in runs)
                {
                    if (run == null) throw new InvalidOperationException("Serialized UTF run inventory contains a null record.");
                    var guidField = run.GetType().GetField("guid", instanceFlags)
                        ?? throw new MissingFieldException(run.GetType().FullName, "guid");
                    var guid = guidField.GetValue(run) as string;
                    if (string.IsNullOrWhiteSpace(guid))
                        throw new InvalidOperationException("A serialized UTF run has no verified string guid.");
                    if (guid == runGuid)
                    {
                        var runningField = run.GetType().GetField("isRunning", instanceFlags);
                        if (runningField?.FieldType == typeof(bool) && (bool)runningField.GetValue(run))
                            return "active";
                        diagnostic += ": serialized job is awaiting Runner restoration.";
                        return "unknown";
                    }
                }
                return "inactive";
            }
            catch (Exception ex)
            {
                var root = ex is TargetInvocationException invocation ? invocation.InnerException ?? ex : ex;
                diagnostic += ": " + root.GetType().Name + ": " + root.Message;
                return "unknown";
            }
        }
    }
}
