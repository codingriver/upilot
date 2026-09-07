using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace CodingRiver.UPilot.Execution
{
    [Serializable]
    public sealed class DynamicParameterSpec
    {
        public string name = "";
        public string typeName = "System.Object";
    }

    [Serializable]
    public sealed class DynamicFieldSpec
    {
        public string name = "";
        public string typeName = "System.Object";
        public string visibility = "private";
        public bool isStatic;
        public bool isReadonly;
    }

    [Serializable]
    public sealed class DynamicPropertySpec
    {
        public string name = "";
        public string typeName = "System.Object";
        public string visibility = "public";
        public bool hasGetter = true;
        public bool hasSetter = true;
        public string backingField = "";
        public string getterBody = "";
        public string setterBody = "";
    }

    [Serializable]
    public sealed class DynamicConstructorSpec
    {
        public string visibility = "public";
        public DynamicParameterSpec[] parameters = Array.Empty<DynamicParameterSpec>();
        public string[] baseConstructorParameterTypeNames = Array.Empty<string>();
        public string[] baseArgumentNames = Array.Empty<string>();
        public string body = "";
    }

    [Serializable]
    public sealed class DynamicMethodSpec
    {
        public string name = "";
        public string visibility = "public";
        public string returnType = "System.Void";
        public DynamicParameterSpec[] parameters = Array.Empty<DynamicParameterSpec>();
        public bool isStatic;
        public bool isVirtual;
        public bool isFinal;
        public string implements = "";
        public string overrides = "";
        public string body = "";
        public string callbackHandle = "";
        public bool isolateCallbackExceptions = true;
        public DynamicCallbackPolicySpec callbackPolicy;
    }

    [Serializable]
    public sealed class DynamicCallbackPolicySpec
    {
        public string exceptionMode = "isolate";
        public int maxInvocations = 10000;
        public int maxReentrancy = 8;
        public int diagnosticsCapacity = 32;
    }

    [Serializable]
    public sealed class CallbackDiagnosticPayload
    {
        public long timestamp;
        public string code = "";
        public string registrationKey = "";
        public string exceptionType = "";
        public string message = "";
        public string stackTrace = "";
    }

    public sealed class DynamicCallbackStatistics
    {
        public int Invocations;
        public int Rejected;
        public int Errors;
        public CallbackDiagnosticPayload[] RecentDiagnostics = Array.Empty<CallbackDiagnosticPayload>();
    }

    [Serializable]
    public sealed class DynamicTypeSpec
    {
        public string typeName = "";
        public string visibility = "public";
        public string baseType = "System.Object";
        public string[] interfaces = Array.Empty<string>();
        public bool isSealed = true;
        public DynamicFieldSpec[] fields = Array.Empty<DynamicFieldSpec>();
        public DynamicPropertySpec[] properties = Array.Empty<DynamicPropertySpec>();
        public DynamicConstructorSpec[] constructors = Array.Empty<DynamicConstructorSpec>();
        public DynamicMethodSpec[] methods = Array.Empty<DynamicMethodSpec>();
    }

    public sealed class DynamicEmitCapability
    {
        public bool Supported { get; internal set; }
        public string Runtime { get; internal set; }
        public string ApiVariant { get; internal set; }
        public string Error { get; internal set; }
    }

    public sealed class DynamicEmitResult
    {
        public string RequestedTypeName { get; internal set; }
        public string GeneratedTypeName { get; internal set; }
        public string AssemblyName { get; internal set; }
        public string SpecHash { get; internal set; }
        public Type Type { get; internal set; }
        public bool CacheHit { get; internal set; }
        public string[] ImplementedMembers { get; internal set; }
        internal KeyValuePair<string, DynamicMethodRegistration>[] Registrations { get; set; } = Array.Empty<KeyValuePair<string, DynamicMethodRegistration>>();
        internal string RegistrationPrefix { get; set; } = "";
    }

    internal sealed class DynamicMethodRegistration
    {
        public string SessionId;
        public CSharpProgram Program;
        public string[] ParameterNames;
        public Delegate Callback;
        public bool IsolateCallbackExceptions;
        public Type ReturnType;
        public string RegistrationKey;
        public string ExceptionMode = "propagate";
        public int MaxInvocations = 10000;
        public int MaxReentrancy = 8;
        public int DiagnosticsCapacity = 32;
        public int InvocationCount;
        public int RejectedCount;
        public int ErrorCount;
        private readonly object _diagnosticGate = new object();
        private readonly Queue<CallbackDiagnosticPayload> _diagnostics = new Queue<CallbackDiagnosticPayload>();

        public DynamicMethodRegistration CloneForSession(string sessionId)
        {
            return new DynamicMethodRegistration
            {
                SessionId = sessionId, Program = Program, ParameterNames = ParameterNames,
                Callback = Callback, IsolateCallbackExceptions = IsolateCallbackExceptions,
                ReturnType = ReturnType, RegistrationKey = RegistrationKey,
                ExceptionMode = ExceptionMode, MaxInvocations = MaxInvocations,
                MaxReentrancy = MaxReentrancy, DiagnosticsCapacity = DiagnosticsCapacity,
            };
        }

        public void Record(string code, Exception exception)
        {
            var diagnostic = new CallbackDiagnosticPayload
            {
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                code = code ?? "",
                registrationKey = RegistrationKey ?? "",
                exceptionType = exception?.GetType().FullName ?? "",
                message = Bound(exception?.Message ?? code, 1024),
                stackTrace = Bound(exception?.StackTrace, 4096),
            };
            lock (_diagnosticGate)
            {
                while (_diagnostics.Count >= Math.Max(1, DiagnosticsCapacity)) _diagnostics.Dequeue();
                _diagnostics.Enqueue(diagnostic);
            }
        }

        public CallbackDiagnosticPayload[] SnapshotDiagnostics()
        {
            lock (_diagnosticGate) return _diagnostics.ToArray();
        }

        private static string Bound(string value, int limit)
        { value = value ?? ""; return value.Length <= limit ? value : value.Substring(0, limit); }
    }

    public static class DynamicMethodDispatcher
    {
        [ThreadStatic] private static Dictionary<string, int> _reentrancy;
        private sealed class RegistrationLease
        {
            public readonly List<DynamicMethodRegistration> Registrations = new List<DynamicMethodRegistration>();
        }
        private sealed class InstanceBinding
        {
            public string SessionId;
        }
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, RegistrationLease> Registrations = new Dictionary<string, RegistrationLease>(StringComparer.Ordinal);
        private static readonly ConditionalWeakTable<object, InstanceBinding> InstanceBindings = new ConditionalWeakTable<object, InstanceBinding>();

        internal static void Register(string key, DynamicMethodRegistration registration)
        {
            lock (Gate)
            {
                if (!Registrations.TryGetValue(key, out var lease))
                {
                    lease = new RegistrationLease();
                    Registrations[key] = lease;
                }
                lease.Registrations.Add(registration);
            }
        }

        public static void BindInstance(object instance, string sessionId)
        {
            if (instance == null || string.IsNullOrWhiteSpace(sessionId)) return;
            InstanceBindings.GetOrCreateValue(instance).SessionId = sessionId;
        }

        public static void UnregisterPrefix(string prefix)
        {
            lock (Gate)
            {
                foreach (var key in Registrations.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                    Registrations.Remove(key);
            }
        }

        internal static void UnregisterSession(string sessionId, IEnumerable<string> keys)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            lock (Gate)
            {
                foreach (string key in (keys ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray())
                {
                    if (!Registrations.TryGetValue(key, out var lease)) continue;
                    lease.Registrations.RemoveAll(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal));
                    if (lease.Registrations.Count == 0) Registrations.Remove(key);
                }
            }
        }

        public static object Invoke(object instance, string registrationKey, object[] arguments)
        {
            DynamicMethodRegistration registration;
            lock (Gate)
            {
                if (!Registrations.TryGetValue(registrationKey, out var lease))
                    throw new ExecutionContractException("EMIT_CALLBACK_EXPIRED", "Dynamic method registration is no longer active.");
                string boundSession = null;
                if (instance != null && InstanceBindings.TryGetValue(instance, out var binding))
                    boundSession = binding.SessionId;
                registration = string.IsNullOrWhiteSpace(boundSession)
                    ? lease.Registrations.LastOrDefault()
                    : lease.Registrations.LastOrDefault(item => string.Equals(item.SessionId, boundSession, StringComparison.Ordinal));
                if (registration == null)
                    throw new ExecutionContractException("EMIT_CALLBACK_EXPIRED", "Dynamic method registration is no longer active for this execution session.");
                if (instance != null && string.IsNullOrWhiteSpace(boundSession))
                    InstanceBindings.GetOrCreateValue(instance).SessionId = registration.SessionId;
            }
            int invocation = Interlocked.Increment(ref registration.InvocationCount);
            if (registration.Callback != null && invocation > registration.MaxInvocations)
            {
                Interlocked.Increment(ref registration.RejectedCount);
                registration.Record("CALLBACK_LIMIT_EXCEEDED", null);
                if (registration.ExceptionMode == "propagate")
                    throw new ExecutionContractException("CALLBACK_LIMIT_EXCEEDED", "Callback invocation limit was exceeded.");
                return DefaultValue(registration.ReturnType);
            }
            _reentrancy = _reentrancy ?? new Dictionary<string, int>(StringComparer.Ordinal);
            _reentrancy.TryGetValue(registrationKey, out int depth);
            if (registration.Callback != null && depth >= registration.MaxReentrancy)
            {
                Interlocked.Increment(ref registration.RejectedCount);
                registration.Record("CALLBACK_LIMIT_EXCEEDED", null);
                if (registration.ExceptionMode == "propagate")
                    throw new ExecutionContractException("CALLBACK_LIMIT_EXCEEDED", "Callback reentrancy limit was exceeded.");
                return DefaultValue(registration.ReturnType);
            }
            _reentrancy[registrationKey] = depth + 1;
            try
            {
                if (registration.Callback != null)
                    return registration.Callback.DynamicInvoke(arguments ?? Array.Empty<object>());
                var variables = new Dictionary<string, object>(StringComparer.Ordinal) { { "this", instance } };
                for (int i = 0; i < registration.ParameterNames.Length; i++)
                    variables[registration.ParameterNames[i]] = arguments != null && i < arguments.Length ? arguments[i] : null;
                var context = new CSharpEvaluationContext(variables, new[] { "System" }, new ExecutionBudget(3000, 10000, 10000, 1000, 1000, 64), new RestrictedEvalExecutionPolicy());
                return registration.Program == null ? null : registration.Program.Execute(context).Value;
            }
            catch (Exception error)
            {
                if (error is TargetInvocationException targetInvocation && targetInvocation.InnerException != null) error = targetInvocation.InnerException;
                Interlocked.Increment(ref registration.ErrorCount);
                registration.Record("CALLBACK_EXCEPTION", error);
                if (registration.Callback == null || registration.ExceptionMode != "isolate")
                {
                    ExceptionDispatchInfo.Capture(error).Throw();
                    throw error;
                }
                return DefaultValue(registration.ReturnType);
            }
            finally
            {
                if (depth == 0) _reentrancy.Remove(registrationKey); else _reentrancy[registrationKey] = depth;
            }
        }

        public static DynamicCallbackStatistics GetStatistics(string sessionId)
        {
            var registrations = new List<DynamicMethodRegistration>();
            lock (Gate)
                registrations.AddRange(Registrations.Values.SelectMany(value => value.Registrations)
                    .Where(registration => registration.Callback != null && string.Equals(registration.SessionId, sessionId, StringComparison.Ordinal)).Distinct());
            return new DynamicCallbackStatistics
            {
                Invocations = registrations.Sum(item => Volatile.Read(ref item.InvocationCount)),
                Rejected = registrations.Sum(item => Volatile.Read(ref item.RejectedCount)),
                Errors = registrations.Sum(item => Volatile.Read(ref item.ErrorCount)),
                RecentDiagnostics = registrations.SelectMany(item => item.SnapshotDiagnostics()).OrderByDescending(item => item.timestamp).Take(32).ToArray(),
            };
        }

        private static object DefaultValue(Type type)
        { return type != null && type != typeof(void) && type.IsValueType ? Activator.CreateInstance(type) : null; }
    }

    public sealed class ReflectionEmitEngine
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, DynamicEmitResult> _cacheByHash = new Dictionary<string, DynamicEmitResult>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _hashByRequestedName = new Dictionary<string, string>(StringComparer.Ordinal);
        private AssemblyBuilder _assembly;
        private ModuleBuilder _module;

        public DynamicEmitCapability Probe()
        {
            try
            {
                EnsureModule();
                string name = "UPilot.Dynamic.Probe_" + Guid.NewGuid().ToString("N");
                var builder = _module.DefineType(name, TypeAttributes.NotPublic | TypeAttributes.Sealed, typeof(object));
                builder.DefineDefaultConstructor(MethodAttributes.Public);
                CreateType(builder);
                return new DynamicEmitCapability
                {
                    Supported = true,
                    Runtime = Environment.Version.ToString(),
                    ApiVariant = "System.Reflection.Emit.AssemblyBuilderAccess.Run",
                    Error = "",
                };
            }
            catch (Exception ex)
            {
                return new DynamicEmitCapability
                {
                    Supported = false,
                    Runtime = Environment.Version.ToString(),
                    ApiVariant = "unavailable",
                    Error = ex.GetType().FullName + ": " + ex.Message,
                };
            }
        }

        public DynamicEmitResult Emit(
            DynamicTypeSpec spec,
            string specHash,
            string nameConflictPolicy,
            string sessionId,
            Func<string, object> callbackResolver,
            Action<Action> registerCleanup)
        {
            if (spec == null || string.IsNullOrWhiteSpace(spec.typeName))
                throw new ExecutionContractException("EMIT_INVALID_SPEC", "spec.typeName is required.");
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ExecutionContractException("SESSION_REQUIRED", "reflection_emit_type requires a persistent execution session.");
            specHash = string.IsNullOrWhiteSpace(specHash) ? Guid.NewGuid().ToString("N") : specHash.Trim().ToLowerInvariant();
            nameConflictPolicy = string.IsNullOrWhiteSpace(nameConflictPolicy) ? "reject" : nameConflictPolicy.Trim().ToLowerInvariant();

            lock (_gate)
            {
                if (_cacheByHash.TryGetValue(specHash, out var cached))
                {
                    var clonedRegistrations = cached.Registrations.Select(pair => new KeyValuePair<string, DynamicMethodRegistration>(
                        pair.Key,
                        CloneRegistrationForSession(pair.Key, pair.Value, spec, sessionId, callbackResolver))).ToArray();
                    foreach (var pair in clonedRegistrations) DynamicMethodDispatcher.Register(pair.Key, pair.Value);
                    string[] cachedKeys = clonedRegistrations.Select(pair => pair.Key).ToArray();
                    registerCleanup?.Invoke(() => DynamicMethodDispatcher.UnregisterSession(sessionId, cachedKeys));
                    return new DynamicEmitResult
                    {
                        RequestedTypeName = cached.RequestedTypeName,
                        GeneratedTypeName = cached.GeneratedTypeName,
                        AssemblyName = cached.AssemblyName,
                        SpecHash = cached.SpecHash,
                        Type = cached.Type,
                        CacheHit = true,
                        ImplementedMembers = cached.ImplementedMembers,
                        Registrations = cached.Registrations,
                        RegistrationPrefix = cached.RegistrationPrefix,
                    };
                }

                string requestedName = spec.typeName.Trim();
                string generatedName = requestedName;
                if (_hashByRequestedName.TryGetValue(requestedName, out var existingHash) && existingHash != specHash)
                {
                    if (nameConflictPolicy != "hashsuffix")
                        throw new ExecutionContractException(
                            "EMIT_TYPE_NAME_CONFLICT",
                            "A different dynamic type spec already uses the requested name.",
                            new Dictionary<string, object> { { "requestedTypeName", requestedName }, { "existingSpecHash", existingHash }, { "newSpecHash", specHash } });
                    generatedName = requestedName + "__" + specHash.Substring(0, Math.Min(8, specHash.Length));
                }

                EnsureModule();
                Type baseType = ExecutionTypeResolver.Resolve(string.IsNullOrWhiteSpace(spec.baseType) ? "System.Object" : spec.baseType) ?? typeof(object);
                var interfaceTypes = (spec.interfaces ?? Array.Empty<string>()).Select(name =>
                {
                    var type = ExecutionTypeResolver.Resolve(name);
                    if (type == null || !type.IsInterface) throw new ExecutionContractException("EMIT_INVALID_SPEC", "Interface type was not found: " + name);
                    return type;
                }).ToArray();
                TypeAttributes attributes = spec.visibility == "internal" ? TypeAttributes.NotPublic : TypeAttributes.Public;
                if (spec.isSealed) attributes |= TypeAttributes.Sealed;
                var typeBuilder = _module.DefineType(generatedName, attributes, baseType, interfaceTypes);
                var fields = DefineFields(typeBuilder, spec.fields);
                var pendingRegistrations = new List<KeyValuePair<string, DynamicMethodRegistration>>();
                DefineProperties(typeBuilder, spec.properties, fields, sessionId, specHash, pendingRegistrations);
                DefineConstructors(typeBuilder, baseType, spec.constructors, fields, sessionId, specHash, pendingRegistrations);
                var implemented = DefineMethods(typeBuilder, spec, sessionId, specHash, callbackResolver, pendingRegistrations);
                Type created = CreateType(typeBuilder);
                foreach (var pair in pendingRegistrations) DynamicMethodDispatcher.Register(pair.Key, pair.Value);
                string cleanupPrefix = sessionId + ":" + specHash + ":";
                string[] registrationKeys = pendingRegistrations.Select(pair => pair.Key).ToArray();
                registerCleanup?.Invoke(() => DynamicMethodDispatcher.UnregisterSession(sessionId, registrationKeys));

                var result = new DynamicEmitResult
                {
                    RequestedTypeName = requestedName,
                    GeneratedTypeName = created.FullName,
                    AssemblyName = created.Assembly.GetName().Name,
                    SpecHash = specHash,
                    Type = created,
                    CacheHit = false,
                    ImplementedMembers = implemented,
                    Registrations = pendingRegistrations.ToArray(),
                    RegistrationPrefix = cleanupPrefix,
                };
                _cacheByHash[specHash] = result;
                _hashByRequestedName[requestedName] = specHash;
                return result;
            }
        }

        private Dictionary<string, FieldBuilder> DefineFields(TypeBuilder builder, IEnumerable<DynamicFieldSpec> specs)
        {
            var result = new Dictionary<string, FieldBuilder>(StringComparer.Ordinal);
            foreach (var spec in specs ?? Array.Empty<DynamicFieldSpec>())
            {
                if (string.IsNullOrWhiteSpace(spec.name)) throw new ExecutionContractException("EMIT_INVALID_SPEC", "Field name is required.");
                Type type = ExecutionTypeResolver.Resolve(spec.typeName);
                if (type == null) throw new ExecutionContractException("EMIT_INVALID_SPEC", "Field type was not found: " + spec.typeName);
                FieldAttributes attributes = FieldVisibility(spec.visibility);
                if (spec.isStatic) attributes |= FieldAttributes.Static;
                if (spec.isReadonly) attributes |= FieldAttributes.InitOnly;
                result[spec.name] = builder.DefineField(spec.name, type, attributes);
            }
            return result;
        }

        private void DefineProperties(
            TypeBuilder builder,
            IEnumerable<DynamicPropertySpec> specs,
            IDictionary<string, FieldBuilder> fields,
            string sessionId,
            string specHash,
            IList<KeyValuePair<string, DynamicMethodRegistration>> registrations)
        {
            int propertyIndex = 0;
            foreach (var spec in specs ?? Array.Empty<DynamicPropertySpec>())
            {
                int currentIndex = propertyIndex++;
                if (string.IsNullOrWhiteSpace(spec.name)) throw new ExecutionContractException("EMIT_INVALID_SPEC", "Property name is required.");
                if (!spec.hasGetter && !string.IsNullOrWhiteSpace(spec.getterBody))
                    throw new ExecutionContractException("EMIT_INVALID_SPEC", "getterBody requires hasGetter=true: " + spec.name);
                if (!spec.hasSetter && !string.IsNullOrWhiteSpace(spec.setterBody))
                    throw new ExecutionContractException("EMIT_INVALID_SPEC", "setterBody requires hasSetter=true: " + spec.name);
                Type type = ExecutionTypeResolver.Resolve(spec.typeName);
                if (type == null) throw new ExecutionContractException("EMIT_INVALID_SPEC", "Property type was not found: " + spec.typeName);
                bool automaticGetter = spec.hasGetter && string.IsNullOrWhiteSpace(spec.getterBody);
                bool automaticSetter = spec.hasSetter && string.IsNullOrWhiteSpace(spec.setterBody);
                FieldBuilder field = null;
                if (automaticGetter || automaticSetter)
                {
                    string backingName = string.IsNullOrWhiteSpace(spec.backingField) ? "<" + spec.name + ">k__BackingField" : spec.backingField;
                    if (!fields.TryGetValue(backingName, out field))
                    {
                        field = builder.DefineField(backingName, type, FieldAttributes.Private);
                        fields[backingName] = field;
                    }
                    if (field.FieldType != type)
                        throw new ExecutionContractException("EMIT_INVALID_SPEC", "Property backing field type does not match: " + spec.name);
                }
                var property = builder.DefineProperty(spec.name, PropertyAttributes.None, type, Type.EmptyTypes);
                MethodAttributes attributes = MethodVisibility(spec.visibility) | MethodAttributes.HideBySig | MethodAttributes.SpecialName;
                if (spec.hasGetter)
                {
                    var getter = builder.DefineMethod("get_" + spec.name, attributes, type, Type.EmptyTypes);
                    var il = getter.GetILGenerator();
                    if (automaticGetter) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); }
                    else
                    {
                        string key = sessionId + ":" + specHash + ":property:get:" + currentIndex;
                        EmitDispatchCall(il, key, Type.EmptyTypes, type, false);
                        registrations.Add(new KeyValuePair<string, DynamicMethodRegistration>(key,
                            CreateProgramRegistration(key, sessionId, spec.getterBody, Array.Empty<string>(), type)));
                    }
                    il.Emit(OpCodes.Ret);
                    property.SetGetMethod(getter);
                }
                if (spec.hasSetter)
                {
                    var setter = builder.DefineMethod("set_" + spec.name, attributes, typeof(void), new[] { type });
                    setter.DefineParameter(1, ParameterAttributes.None, "value");
                    var il = setter.GetILGenerator();
                    if (automaticSetter) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, field); }
                    else
                    {
                        string key = sessionId + ":" + specHash + ":property:set:" + currentIndex;
                        EmitDispatchCall(il, key, new[] { type }, typeof(void), false);
                        registrations.Add(new KeyValuePair<string, DynamicMethodRegistration>(key,
                            CreateProgramRegistration(key, sessionId, spec.setterBody, new[] { "value" }, typeof(void))));
                    }
                    il.Emit(OpCodes.Ret);
                    property.SetSetMethod(setter);
                }
            }
        }

        private static DynamicMethodRegistration CreateProgramRegistration(
            string key, string sessionId, string body, string[] parameterNames, Type returnType)
        {
            CSharpEmitBackend.ValidateEmitSubset(body);
            return new DynamicMethodRegistration
            {
                SessionId = sessionId,
                RegistrationKey = key,
                Program = CSharpSubsetEngine.Parse(body, "statements"),
                ParameterNames = parameterNames ?? Array.Empty<string>(),
                ReturnType = returnType,
                ExceptionMode = "propagate",
            };
        }

        private void DefineConstructors(
            TypeBuilder builder,
            Type baseType,
            DynamicConstructorSpec[] specs,
            IDictionary<string, FieldBuilder> fields,
            string sessionId,
            string specHash,
            IList<KeyValuePair<string, DynamicMethodRegistration>> registrations)
        {
            specs = specs ?? Array.Empty<DynamicConstructorSpec>();
            if (specs.Length == 0)
            {
                var baseCtor = baseType.GetConstructor(Type.EmptyTypes);
                if (baseCtor == null) throw new ExecutionContractException("EMIT_INVALID_SPEC", "Base type has no parameterless constructor; provide an explicit constructor spec.");
                builder.DefineDefaultConstructor(MethodAttributes.Public);
                return;
            }
            for (int index = 0; index < specs.Length; index++)
            {
                var spec = specs[index];
                var parameterTypes = (spec.parameters ?? Array.Empty<DynamicParameterSpec>()).Select(p => RequireType(p.typeName, "constructor parameter")).ToArray();
                var ctor = builder.DefineConstructor(MethodVisibility(spec.visibility) | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, CallingConventions.Standard, parameterTypes);
                for (int i = 0; i < parameterTypes.Length; i++) ctor.DefineParameter(i + 1, ParameterAttributes.None, spec.parameters[i].name);
                var baseParameterTypes = (spec.baseConstructorParameterTypeNames ?? Array.Empty<string>()).Select(name => RequireType(name, "base constructor parameter")).ToArray();
                var baseCtor = baseType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, baseParameterTypes, null);
                if (baseCtor == null) throw new ExecutionContractException("EMIT_INVALID_SPEC", "Base constructor was not found.");
                var il = ctor.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                foreach (string baseName in spec.baseArgumentNames ?? Array.Empty<string>())
                {
                    int parameterIndex = Array.FindIndex(spec.parameters, p => p.name == baseName);
                    if (parameterIndex < 0) throw new ExecutionContractException("EMIT_INVALID_SPEC", "Base argument was not found: " + baseName);
                    EmitLoadArgument(il, parameterIndex + 1);
                }
                il.Emit(OpCodes.Call, baseCtor);
                foreach (var parameter in spec.parameters.Select((value, position) => new { value, position }))
                {
                    if (!fields.TryGetValue(parameter.value.name, out var field) || field.IsStatic) continue;
                    il.Emit(OpCodes.Ldarg_0); EmitLoadArgument(il, parameter.position + 1); il.Emit(OpCodes.Stfld, field);
                }
                if (!string.IsNullOrWhiteSpace(spec.body))
                {
                    string key = sessionId + ":" + specHash + ":ctor:" + index;
                    EmitDispatchCall(il, key, parameterTypes, typeof(void), false);
                    registrations.Add(new KeyValuePair<string, DynamicMethodRegistration>(key,
                        CreateProgramRegistration(key, sessionId, spec.body, spec.parameters.Select(p => p.name).ToArray(), typeof(void))));
                }
                il.Emit(OpCodes.Ret);
            }
        }

        private string[] DefineMethods(
            TypeBuilder builder,
            DynamicTypeSpec typeSpec,
            string sessionId,
            string specHash,
            Func<string, object> callbackResolver,
            IList<KeyValuePair<string, DynamicMethodRegistration>> registrations)
        {
            var implemented = new List<string>();
            int index = 0;
            foreach (var spec in typeSpec.methods ?? Array.Empty<DynamicMethodSpec>())
            {
                Type returnType = RequireType(spec.returnType, "method return", true);
                Type[] parameterTypes = (spec.parameters ?? Array.Empty<DynamicParameterSpec>()).Select(p => RequireType(p.typeName, "method parameter")).ToArray();
                MethodAttributes attributes = MethodVisibility(spec.visibility) | MethodAttributes.HideBySig;
                if (spec.isStatic) attributes |= MethodAttributes.Static;
                if (spec.isVirtual || !string.IsNullOrWhiteSpace(spec.implements) || !string.IsNullOrWhiteSpace(spec.overrides)) attributes |= MethodAttributes.Virtual;
                if (spec.isFinal) attributes |= MethodAttributes.Final;
                var method = builder.DefineMethod(spec.name, attributes, returnType, parameterTypes);
                for (int p = 0; p < parameterTypes.Length; p++) method.DefineParameter(p + 1, ParameterAttributes.None, spec.parameters[p].name);
                string key = sessionId + ":" + specHash + ":method:" + index++;
                var il = method.GetILGenerator();
                EmitDispatchCall(il, key, parameterTypes, returnType, spec.isStatic);
                il.Emit(OpCodes.Ret);

                Delegate callback = null;
                CSharpProgram program = null;
                if (!string.IsNullOrWhiteSpace(spec.callbackHandle))
                    callback = ResolveCallback(spec, parameterTypes, returnType, callbackResolver);
                else if (!string.IsNullOrWhiteSpace(spec.body))
                {
                    CSharpEmitBackend.ValidateEmitSubset(spec.body);
                    program = CSharpSubsetEngine.Parse(spec.body, "statements");
                }
                DynamicCallbackPolicySpec callbackPolicy = callback == null ? null : NormalizeCallbackPolicy(spec);
                registrations.Add(new KeyValuePair<string, DynamicMethodRegistration>(key, new DynamicMethodRegistration
                {
                    SessionId = sessionId,
                    RegistrationKey = key,
                    Program = program,
                    Callback = callback,
                    ParameterNames = spec.parameters.Select(p => p.name).ToArray(),
                    IsolateCallbackExceptions = callback != null && (callbackPolicy?.exceptionMode ?? "propagate") == "isolate",
                    ExceptionMode = callbackPolicy?.exceptionMode ?? "propagate",
                    MaxInvocations = callbackPolicy?.maxInvocations ?? 10000,
                    MaxReentrancy = callbackPolicy?.maxReentrancy ?? 8,
                    DiagnosticsCapacity = callbackPolicy?.diagnosticsCapacity ?? 32,
                    ReturnType = returnType,
                }));

                MethodInfo contractMethod = ResolveContractMethod(typeSpec, spec, parameterTypes);
                if (contractMethod != null) builder.DefineMethodOverride(method, contractMethod);
                implemented.Add(contractMethod != null
                    ? MethodBinder.FormatSignature(contractMethod)
                    : FormatDeclaredMethodSignature(typeSpec.typeName, spec, returnType, parameterTypes));
            }
            return implemented.ToArray();
        }

        private static string FormatDeclaredMethodSignature(
            string declaringTypeName,
            DynamicMethodSpec spec,
            Type returnType,
            Type[] parameterTypes)
        {
            string parameters = string.Join(", ", parameterTypes.Select((type, index) =>
                (type?.FullName ?? (spec.parameters?[index]?.typeName ?? "System.Object")) + " " +
                (spec.parameters?[index]?.name ?? ("arg" + index))));
            return (returnType?.FullName ?? spec.returnType ?? "System.Void") + " " +
                   (declaringTypeName ?? "(dynamic)") + "." + (spec.name ?? "") + "(" + parameters + ")";
        }

        private static DynamicMethodRegistration CloneRegistrationForSession(
            string key,
            DynamicMethodRegistration source,
            DynamicTypeSpec typeSpec,
            string sessionId,
            Func<string, object> callbackResolver)
        {
            var clone = source.CloneForSession(sessionId);
            if (source.Callback == null) return clone;
            const string marker = ":method:";
            int markerIndex = key.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0 || !int.TryParse(key.Substring(markerIndex + marker.Length), out int methodIndex) ||
                methodIndex < 0 || methodIndex >= (typeSpec.methods ?? Array.Empty<DynamicMethodSpec>()).Length)
                throw new ExecutionContractException("EMIT_CALLBACK_INVALID", "Cached callback registration could not be matched to its method spec.");
            var methodSpec = typeSpec.methods[methodIndex];
            Type returnType = RequireType(methodSpec.returnType, "method return", true);
            Type[] parameterTypes = (methodSpec.parameters ?? Array.Empty<DynamicParameterSpec>())
                .Select(parameter => RequireType(parameter.typeName, "method parameter")).ToArray();
            clone.Callback = ResolveCallback(methodSpec, parameterTypes, returnType, callbackResolver);
            return clone;
        }

        private static Delegate ResolveCallback(
            DynamicMethodSpec spec,
            Type[] parameterTypes,
            Type returnType,
            Func<string, object> callbackResolver)
        {
            object callbackValue = callbackResolver?.Invoke(spec.callbackHandle);
            Delegate callback = callbackValue as Delegate;
            if (callback == null && callbackValue is LambdaValue lambda)
            {
                var delegateSignature = parameterTypes.Concat(new[] { returnType }).ToArray();
                callback = lambda.ToDelegate(Expression.GetDelegateType(delegateSignature));
            }
            if (callback == null)
                throw new ExecutionContractException("EMIT_CALLBACK_INVALID", "callbackHandle must resolve to a delegate or C# subset lambda.");
            return callback;
        }

        private static DynamicCallbackPolicySpec NormalizeCallbackPolicy(DynamicMethodSpec spec)
        {
            var policy = spec.callbackPolicy ?? new DynamicCallbackPolicySpec
            {
                exceptionMode = spec.isolateCallbackExceptions ? "isolate" : "propagate",
            };
            policy.exceptionMode = string.IsNullOrWhiteSpace(policy.exceptionMode) ? "isolate" : policy.exceptionMode.Trim().ToLowerInvariant();
            if (policy.exceptionMode != "isolate" && policy.exceptionMode != "propagate")
                throw new ExecutionContractException("EMIT_INVALID_SPEC", "callbackPolicy.exceptionMode must be isolate or propagate.");
            policy.maxInvocations = Math.Max(1, Math.Min(100000, policy.maxInvocations <= 0 ? 10000 : policy.maxInvocations));
            policy.maxReentrancy = Math.Max(1, Math.Min(64, policy.maxReentrancy <= 0 ? 8 : policy.maxReentrancy));
            policy.diagnosticsCapacity = Math.Max(1, Math.Min(128, policy.diagnosticsCapacity <= 0 ? 32 : policy.diagnosticsCapacity));
            return policy;
        }

        private static MethodInfo ResolveContractMethod(DynamicTypeSpec typeSpec, DynamicMethodSpec spec, Type[] parameterTypes)
        {
            string contract = !string.IsNullOrWhiteSpace(spec.implements) ? spec.implements : spec.overrides;
            if (string.IsNullOrWhiteSpace(contract)) return null;
            int split = contract.LastIndexOf('.');
            if (split <= 0) throw new ExecutionContractException("EMIT_INVALID_SPEC", "Contract method must be fully qualified: " + contract);
            string typeName = contract.Substring(0, split), methodName = contract.Substring(split + 1);
            Type type = ExecutionTypeResolver.Resolve(typeName);
            if (type == null) throw new ExecutionContractException("EMIT_INVALID_SPEC", "Contract type was not found: " + typeName);
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, parameterTypes, null);
            if (method == null) throw new ExecutionContractException("EMIT_INVALID_SPEC", "Contract method was not found: " + contract);
            return method;
        }

        private static void EmitDispatchCall(ILGenerator il, string key, Type[] parameterTypes, Type returnType, bool isStatic)
        {
            if (isStatic) il.Emit(OpCodes.Ldnull); else il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldstr, key);
            EmitLoadInt(il, parameterTypes.Length);
            il.Emit(OpCodes.Newarr, typeof(object));
            for (int i = 0; i < parameterTypes.Length; i++)
            {
                il.Emit(OpCodes.Dup); EmitLoadInt(il, i); EmitLoadArgument(il, i + (isStatic ? 0 : 1));
                if (parameterTypes[i].IsValueType) il.Emit(OpCodes.Box, parameterTypes[i]);
                il.Emit(OpCodes.Stelem_Ref);
            }
            il.Emit(OpCodes.Call, typeof(DynamicMethodDispatcher).GetMethod(nameof(DynamicMethodDispatcher.Invoke), BindingFlags.Public | BindingFlags.Static));
            if (returnType == typeof(void)) il.Emit(OpCodes.Pop);
            else if (returnType.IsValueType) il.Emit(OpCodes.Unbox_Any, returnType);
            else il.Emit(OpCodes.Castclass, returnType);
        }

        private void EnsureModule()
        {
            if (_module != null) return;
            var name = new AssemblyName("UPilot.Dynamic.Runtime");
            _assembly = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
            _module = _assembly.DefineDynamicModule(name.Name);
        }

        private static Type CreateType(TypeBuilder builder)
        {
            var createTypeInfo = typeof(TypeBuilder).GetMethod("CreateTypeInfo", Type.EmptyTypes);
            if (createTypeInfo != null)
            {
                var info = createTypeInfo.Invoke(builder, null) as TypeInfo;
                if (info != null) return info.AsType();
            }
            var createType = typeof(TypeBuilder).GetMethod("CreateType", Type.EmptyTypes);
            if (createType == null) throw new PlatformNotSupportedException("TypeBuilder has no supported CreateType API.");
            return (Type)createType.Invoke(builder, null);
        }

        private static Type RequireType(string name, string label, bool allowVoid = false)
        {
            Type type = ExecutionTypeResolver.Resolve(string.IsNullOrWhiteSpace(name) && allowVoid ? "void" : name, null, allowVoid);
            if (type == null) throw new ExecutionContractException("EMIT_INVALID_SPEC", label + " type was not found: " + name);
            return type;
        }
        private static FieldAttributes FieldVisibility(string value)
        {
            if (value == "public") return FieldAttributes.Public;
            if (value == "protected") return FieldAttributes.Family;
            if (value == "internal") return FieldAttributes.Assembly;
            return FieldAttributes.Private;
        }
        private static MethodAttributes MethodVisibility(string value)
        {
            if (value == "private") return MethodAttributes.Private;
            if (value == "protected") return MethodAttributes.Family;
            if (value == "internal") return MethodAttributes.Assembly;
            return MethodAttributes.Public;
        }
        private static void EmitLoadArgument(ILGenerator il, int index)
        {
            if (index == 0) il.Emit(OpCodes.Ldarg_0);
            else if (index == 1) il.Emit(OpCodes.Ldarg_1);
            else if (index == 2) il.Emit(OpCodes.Ldarg_2);
            else if (index == 3) il.Emit(OpCodes.Ldarg_3);
            else il.Emit(OpCodes.Ldarg, index);
        }
        private static void EmitLoadInt(ILGenerator il, int value)
        {
            switch (value)
            {
                case 0: il.Emit(OpCodes.Ldc_I4_0); break;
                case 1: il.Emit(OpCodes.Ldc_I4_1); break;
                case 2: il.Emit(OpCodes.Ldc_I4_2); break;
                case 3: il.Emit(OpCodes.Ldc_I4_3); break;
                case 4: il.Emit(OpCodes.Ldc_I4_4); break;
                case 5: il.Emit(OpCodes.Ldc_I4_5); break;
                case 6: il.Emit(OpCodes.Ldc_I4_6); break;
                case 7: il.Emit(OpCodes.Ldc_I4_7); break;
                case 8: il.Emit(OpCodes.Ldc_I4_8); break;
                default: il.Emit(OpCodes.Ldc_I4, value); break;
            }
        }
    }
}
