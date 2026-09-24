using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace CodingRiver.UPilot
{
    internal sealed class UPilotTestRunnerAdapter
    {
        private const BindingFlags DeclaredStatic = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.DeclaredOnly;
        private const BindingFlags DeclaredInstance = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        private static readonly Dictionary<Type, UPilotTestRunnerAdapter> Cache = new Dictionary<Type, UPilotTestRunnerAdapter>();
        private static readonly object CacheLock = new object();

        private readonly Type _api;
        private readonly Lazy<MethodInfo> _cancel;
        private readonly Lazy<MethodInfo> _unregister;
        private readonly PropertyInfo _apiHolderProperty;
        private readonly FieldInfo _apiHolderField;
        private readonly Type _fallbackHolderType;
        private readonly PropertyInfo _fallbackSingletonProperty;
        private readonly FieldInfo _fallbackSingletonField;
        private readonly object _bindingLock = new object();
        private readonly string _initialProbeBindingFailure;

        private Type _boundHolderType;
        private MethodInfo _getRunner;
        private PropertyInfo _testRunsProperty;
        private FieldInfo _testRunsField;
        private string _holderBindingFailure;
        private Type _boundRunType;
        private PropertyInfo _runGuidProperty;
        private FieldInfo _runGuidField;
        private PropertyInfo _runStateProperty;
        private FieldInfo _runStateField;
        private string _runBindingFailure;

        internal readonly string Identity;

        private UPilotTestRunnerAdapter(Type api)
        {
            _api = api;
            Identity = api.Assembly.FullName;
            _cancel = new Lazy<MethodInfo>(() => UPilotTestService.ResolveCancelMethod(_api));
            _unregister = new Lazy<MethodInfo>(ResolveUnregister);
            _apiHolderProperty = FindStaticProperty(api, "m_testJobDataHolder");
            _apiHolderField = FindStaticField(api, "m_testJobDataHolder");
            _fallbackHolderType = api.Assembly.GetType("UnityEditor.TestTools.TestRunner.TestRun.TestJobDataHolder");
            if (_fallbackHolderType != null)
            {
                // ScriptableSingleton<T>.instance is inherited. Walking base types is required for
                // UTF 1.1.x where TestRunnerApi has no m_testJobDataHolder member of its own.
                _fallbackSingletonProperty = FindStaticProperty(_fallbackHolderType, "instance");
                _fallbackSingletonField = FindStaticField(_fallbackHolderType, "instance");
            }

            if (_apiHolderProperty == null && _apiHolderField == null
                && (_fallbackHolderType == null
                    || (_fallbackSingletonProperty == null && _fallbackSingletonField == null)))
                _initialProbeBindingFailure = BindingFailure(_api,
                    "m_testJobDataHolder or inherited TestJobDataHolder.instance",
                    _fallbackHolderType ?? _api).Message;
        }

        internal MethodInfo Cancel => _cancel.Value;

        internal MethodInfo Unregister => _unregister.Value;

        internal static UPilotTestRunnerAdapter Get(Type api)
        {
            if (api == null)
                throw new MissingMemberException("TEST_RUNNER_BINDING_UNAVAILABLE: TestRunnerApi type is unavailable.");
            lock (CacheLock)
            {
                if (!Cache.TryGetValue(api, out var adapter))
                    Cache[api] = adapter = new UPilotTestRunnerAdapter(api);
                return adapter;
            }
        }

        internal static PropertyInfo FindStaticProperty(Type type, string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                var property = current.GetProperty(name, DeclaredStatic);
                if (property?.GetMethod != null && property.GetMethod.IsStatic
                    && property.GetIndexParameters().Length == 0)
                    return property;
            }
            return null;
        }

        private static FieldInfo FindStaticField(Type type, string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                var field = current.GetField(name, DeclaredStatic);
                if (field?.IsStatic == true) return field;
            }
            return null;
        }

        private static PropertyInfo FindInstanceProperty(Type type, params string[] names)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                foreach (string name in names)
                {
                    var property = current.GetProperty(name, DeclaredInstance);
                    if (property?.GetMethod != null && !property.GetMethod.IsStatic
                        && property.GetIndexParameters().Length == 0)
                        return property;
                }
            }
            return null;
        }

        private static FieldInfo FindInstanceField(Type type, params string[] names)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                foreach (string name in names)
                {
                    var field = current.GetField(name, DeclaredInstance);
                    if (field != null && !field.IsStatic) return field;
                }
            }
            return null;
        }

        private static MethodInfo FindInstanceMethod(Type type, string name, Type[] parameters)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                var method = current.GetMethod(name, DeclaredInstance, null, parameters, null);
                if (method != null && !method.IsStatic) return method;
            }
            return null;
        }

        private MethodInfo ResolveUnregister()
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

        private static MissingMemberException BindingFailure(Type api, string required, Type candidateType = null)
        {
            Type inspectedType = candidateType ?? api;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.Instance;
            string candidates = string.Join("; ", inspectedType.GetMembers(flags)
                .Where(member => member.Name.IndexOf("Runner", StringComparison.OrdinalIgnoreCase) >= 0
                    || member.Name.IndexOf("Run", StringComparison.OrdinalIgnoreCase) >= 0
                    || member.Name.IndexOf("Callback", StringComparison.OrdinalIgnoreCase) >= 0
                    || member.Name.IndexOf("Job", StringComparison.OrdinalIgnoreCase) >= 0
                    || member.Name.IndexOf("instance", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(member => member.MemberType + " " + member)
                .Take(24));
            return new MissingMemberException("TEST_RUNNER_BINDING_UNAVAILABLE: assembly=" + api.Assembly.FullName
                + "; version=" + api.Assembly.GetName().Version
                + "; type=" + api.FullName
                + "; inspectedType=" + inspectedType.FullName
                + "; required=" + required
                + "; candidates=" + (string.IsNullOrEmpty(candidates) ? "(none)" : candidates));
        }

        private object GetHolder()
        {
            object holder = _apiHolderProperty?.GetValue(null) ?? _apiHolderField?.GetValue(null);
            if (holder != null) return holder;
            return _fallbackSingletonProperty?.GetValue(null) ?? _fallbackSingletonField?.GetValue(null);
        }

        private void EnsureHolderBinding(Type holderType)
        {
            if (_boundHolderType == holderType) return;
            lock (_bindingLock)
            {
                if (_boundHolderType == holderType) return;
                _boundHolderType = holderType;
                _getRunner = FindInstanceMethod(holderType, "GetRunner", new[] { typeof(string) });
                _testRunsProperty = FindInstanceProperty(holderType, "TestRuns");
                _testRunsField = FindInstanceField(holderType, "TestRuns");
                _holderBindingFailure = _getRunner == null && _testRunsProperty == null && _testRunsField == null
                    ? BindingFailure(_api, "GetRunner(string) or TestRuns", holderType).Message
                    : null;
            }
        }

        private void EnsureRunBinding(Type runType)
        {
            if (_boundRunType == runType) return;
            lock (_bindingLock)
            {
                if (_boundRunType == runType) return;
                _boundRunType = runType;
                _runGuidProperty = FindInstanceProperty(runType, "guid", "runGuid", "Guid");
                _runGuidField = FindInstanceField(runType, "guid", "runGuid", "Guid");
                _runStateProperty = FindInstanceProperty(runType, "isRunning", "IsRunning");
                _runStateField = FindInstanceField(runType, "isRunning", "IsRunning");
                bool hasGuid = (_runGuidProperty?.PropertyType == typeof(string))
                    || (_runGuidField?.FieldType == typeof(string));
                bool hasState = (_runStateProperty?.PropertyType == typeof(bool))
                    || (_runStateField?.FieldType == typeof(bool));
                _runBindingFailure = !hasGuid
                    ? BindingFailure(_api, "serialized run guid", runType).Message
                    : !hasState ? BindingFailure(_api, "serialized run isRunning", runType).Message : null;
            }
        }

        internal string Probe(string runGuid, out object runner, out string diagnostic)
        {
            runner = null;
            diagnostic = Identity;
            if (!string.IsNullOrEmpty(_initialProbeBindingFailure))
            {
                diagnostic = _initialProbeBindingFailure;
                return "unknown";
            }

            try
            {
                if (string.IsNullOrWhiteSpace(runGuid)) throw new InvalidOperationException("Missing runGuid.");
                object holder = GetHolder();
                if (holder == null) throw new InvalidOperationException("UTF holder is not initialized.");
                EnsureHolderBinding(holder.GetType());
                if (!string.IsNullOrEmpty(_holderBindingFailure))
                {
                    diagnostic = _holderBindingFailure;
                    return "unknown";
                }

                if (_getRunner != null)
                {
                    runner = _getRunner.Invoke(holder, new object[] { runGuid });
                    if (runner != null) return "active";
                }

                var runs = (_testRunsField?.GetValue(holder) ?? _testRunsProperty?.GetValue(holder)) as IEnumerable;
                if (runs == null) throw new InvalidOperationException("Serialized UTF run inventory is unavailable.");
                foreach (var run in runs)
                {
                    if (run == null) throw new InvalidOperationException("Serialized UTF run inventory contains a null record.");
                    EnsureRunBinding(run.GetType());
                    if (!string.IsNullOrEmpty(_runBindingFailure))
                    {
                        diagnostic = _runBindingFailure;
                        return "unknown";
                    }

                    string guid = (_runGuidField?.GetValue(run) ?? _runGuidProperty?.GetValue(run)) as string;
                    if (string.IsNullOrWhiteSpace(guid))
                        throw new InvalidOperationException("A serialized UTF run has no verified string guid.");
                    if (guid != runGuid) continue;

                    object runningValue = _runStateField?.GetValue(run) ?? _runStateProperty?.GetValue(run);
                    if (runningValue is bool isRunning && isRunning) return "active";
                    diagnostic += ": serialized job is awaiting Runner restoration.";
                    return "unknown";
                }
                return "inactive";
            }
            catch (Exception ex)
            {
                var root = ex is TargetInvocationException invocation ? invocation.InnerException ?? ex : ex;
                diagnostic = Identity + ": " + root.GetType().Name + ": " + root.Message;
                return "unknown";
            }
        }
    }
}
