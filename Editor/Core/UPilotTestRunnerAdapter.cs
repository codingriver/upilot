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
        private readonly PropertyInfo _holder;
        private readonly MethodInfo _getRunner;
        private readonly PropertyInfo _runsProperty;
        private readonly FieldInfo _runsField;
        internal readonly MethodInfo Cancel;
        internal readonly MethodInfo Unregister;
        internal readonly string Identity;

        private UPilotTestRunnerAdapter(Type api)
        {
            Identity = api.Assembly.FullName;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            _holder = api.GetProperty("m_testJobDataHolder", flags)
                ?? throw BindingFailure(api, "m_testJobDataHolder static property");
            if (_holder.GetMethod == null || !_holder.GetMethod.IsStatic || _holder.GetIndexParameters().Length != 0)
                throw BindingFailure(api, "readable m_testJobDataHolder static property");
            _getRunner = _holder.PropertyType.GetMethod("GetRunner", new[] { typeof(string) })
                ?? throw BindingFailure(api, "GetRunner(string)", _holder.PropertyType);
            Cancel = UPilotTestService.ResolveCancelMethod(api);
            Unregister = api.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .SingleOrDefault(method => method.Name == "UnregisterTestCallback" && method.IsGenericMethodDefinition
                    && method.GetParameters().Length == 1)
                ?? throw BindingFailure(api, "UnregisterTestCallback<T>(T)");
            // TestRuns is on the concrete holder, not its interface.
            var holderType = api.Assembly.GetType("UnityEditor.TestTools.TestRunner.TestRun.TestJobDataHolder");
            _runsField = holderType?.GetField("TestRuns", BindingFlags.Public | BindingFlags.Instance);
            _runsProperty = holderType?.GetProperty("TestRuns", BindingFlags.Public | BindingFlags.Instance);
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
                var holder = _holder.GetValue(null);
                if (holder == null) throw new InvalidOperationException("UTF holder is not initialized.");
                runner = _getRunner.Invoke(holder, new object[] { runGuid });
                if (runner != null) return "active";
                var runs = (_runsField?.GetValue(holder) ?? _runsProperty?.GetValue(holder)) as IEnumerable;
                if (runs == null) throw new InvalidOperationException("Serialized UTF run inventory is unavailable.");
                foreach (var run in runs)
                {
                    if (run == null) continue;
                    var guidField = run.GetType().GetField("guid")
                        ?? throw new MissingFieldException(run.GetType().FullName, "guid");
                    var guid = guidField.GetValue(run) as string;
                    if (string.IsNullOrWhiteSpace(guid))
                        throw new InvalidOperationException("A serialized UTF run has no verified string guid.");
                    if (guid == runGuid)
                    {
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
