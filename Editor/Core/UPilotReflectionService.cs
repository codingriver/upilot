// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodingRiver.UPilot.Execution;
using UnityEngine;

namespace CodingRiver.UPilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable] public class ReflectionFindMessage  { public ReflectionFindPayload payload; }
    [Serializable] public class TypeExistsMessage { public TypeExistsPayload payload; }
    [Serializable] public class TypeExistsPayload { public string typeName = ""; }
    [Serializable]
    public class ReflectionFindPayload
    {
        public string typeName   = "";
        public string methodName = "";
    }

    [Serializable] public class ReflectionCallMessage  { public ReflectionCallPayload payload; }
    [Serializable]
    public class ReflectionCallPayload
    {
        public string typeName          = "";
        public string methodName        = "";
        public string[] parameters;        // string representations of args
        public bool   isStatic          = true;
        public string targetInstancePath = ""; // hierarchy path for instance methods
        public string targetStaticTypeName = "";
        public string targetStaticMemberPath = "";
        public string argumentsJson = "";
        public string[] parameterTypeNames = Array.Empty<string>();
        public string[] genericTypeArguments = Array.Empty<string>();
        public string targetHandle = "";
        public string sessionId = "";
        public string awaitMode = "auto";
        public int awaitTimeoutMs = 3000;
        public string resultMode = "auto";
    }

    [Serializable]
    public class ReflectionMethodInfoPayload
    {
        public string typeName;
        public string methodName;
        public string returnType;
        public bool   isStatic;
        public List<ReflectionParamInfoPayload> parameters = new();
    }

    [Serializable]
    public class ReflectionParamInfoPayload
    {
        public string name;
        public string type;
    }

    [Serializable]
    public class ReflectionFindResultPayload
    {
        public List<ReflectionMethodInfoPayload> methods = new();
    }

    [Serializable]
    public class TypeExistsResultPayload
    {
        public bool exists;
        public string requestedTypeName;
        public string fullName;
        public string assembly;
        public bool isPublic;
        public bool ambiguous;
        public List<string> candidates = new();
    }

    [Serializable]
    public class ReflectionCallResultPayload
    {
        public string typeName;
        public string methodName;
        public string result;
        public string invokedSignature;
        public TypedValueResult resultValue;
        public List<ReflectionRefOutResultPayload> refOutArguments = new();
        public bool wasAwaitable;
        public string awaitableStatus;
    }

    [Serializable]
    public class ReflectionRefOutResultPayload
    {
        public string name;
        public string direction;
        public TypedValueResult value;
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    public class UPilotReflectionService
    {
        private readonly UPilotBridge _bridge;
        private readonly UPilotExecutionService _execution;

        public UPilotReflectionService(UPilotBridge bridge, UPilotExecutionService execution)
        {
            _bridge = bridge;
            _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("reflection.find", HandleFindAsync);
            _bridge.Router.Register("reflection.call", HandleCallAsync);
            _bridge.Router.Register("reflection.typeExists", HandleTypeExistsAsync);
        }

        private async Task HandleTypeExistsAsync(string id, string json, CancellationToken token)
        {
            var payload = JsonUtility.FromJson<TypeExistsMessage>(json)?.payload ?? new TypeExistsPayload();
            if (string.IsNullOrWhiteSpace(payload.typeName))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "typeName is required.", token, "reflection.typeExists");
                return;
            }

            var tcs = new TaskCompletionSource<TypeExistsResultPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var candidates = FindTypes(payload.typeName);
                    var result = new TypeExistsResultPayload
                    {
                        requestedTypeName = payload.typeName,
                        exists = candidates.Count == 1,
                        ambiguous = candidates.Count > 1,
                        candidates = candidates
                            .Select(type => $"{type.FullName}, {type.Assembly.GetName().Name}")
                            .OrderBy(value => value, StringComparer.Ordinal)
                            .ToList(),
                    };
                    if (candidates.Count == 1)
                    {
                        var type = candidates[0];
                        result.fullName = type.FullName ?? type.Name;
                        result.assembly = type.Assembly.GetName().Name;
                        result.isPublic = type.IsPublic || type.IsNestedPublic;
                    }
                    tcs.SetResult(result);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                await _bridge.SendResultAsync(id, "reflection.typeExists", await tcs.Task, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "TYPE_QUERY_FAILED", ex.Message, token, "reflection.typeExists");
            }
        }

        // ── reflection.find ─────────────────────────────────────────────────────

        private async Task HandleFindAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ReflectionFindMessage>(json);
            var p   = msg?.payload ?? new ReflectionFindPayload();

            if (string.IsNullOrEmpty(p.typeName))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "typeName is required.", token, "reflection.find");
                return;
            }

            var tcs = new TaskCompletionSource<ReflectionFindResultPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var type = FindType(p.typeName);
                    if (type == null)
                    {
                        tcs.SetException(new Exception($"Type not found: {p.typeName}"));
                        return;
                    }

                    var result = new ReflectionFindResultPayload();
                    var flags = BindingFlags.Public | BindingFlags.NonPublic |
                                BindingFlags.Static | BindingFlags.Instance;

                    MethodInfo[] methods;
                    if (!string.IsNullOrEmpty(p.methodName))
                    {
                        // Find specific method(s) by name
                        methods = Array.FindAll(type.GetMethods(flags), m => m.Name == p.methodName);
                    }
                    else
                    {
                        methods = type.GetMethods(flags);
                    }

                    foreach (var m in methods)
                    {
                        // Skip compiler-generated and property accessors for cleaner output
                        if (m.IsSpecialName) continue;

                        var info = new ReflectionMethodInfoPayload
                        {
                            typeName   = type.FullName,
                            methodName = m.Name,
                            returnType = m.ReturnType.FullName ?? m.ReturnType.Name,
                            isStatic   = m.IsStatic,
                        };

                        foreach (var param in m.GetParameters())
                        {
                            info.parameters.Add(new ReflectionParamInfoPayload
                            {
                                name = param.Name,
                                type = param.ParameterType.FullName ?? param.ParameterType.Name,
                            });
                        }

                        result.methods.Add(info);
                    }

                    tcs.SetResult(result);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "reflection.find", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "REFLECTION_FIND_FAILED", ex.Message, token, "reflection.find");
            }
        }

        // ── reflection.call ─────────────────────────────────────────────────────

        private async Task HandleCallAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ReflectionCallMessage>(json);
            var p   = msg?.payload ?? new ReflectionCallPayload();

            if (string.IsNullOrEmpty(p.methodName) ||
                (string.IsNullOrEmpty(p.typeName) && string.IsNullOrEmpty(p.targetHandle)))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "methodName and either typeName or targetHandle are required.", token, "reflection.call");
                return;
            }

            if (!string.IsNullOrWhiteSpace(p.argumentsJson) && p.parameters != null && p.parameters.Length > 0)
            {
                await _bridge.SendErrorAsync(id, "REFLECTION_ARGUMENTS_CONFLICT", "arguments and legacy parameters are mutually exclusive.", token, "reflection.call");
                return;
            }

            if (!string.IsNullOrWhiteSpace(p.targetHandle) &&
                (!string.IsNullOrWhiteSpace(p.targetInstancePath) ||
                 !string.IsNullOrWhiteSpace(p.targetStaticTypeName) ||
                 !string.IsNullOrWhiteSpace(p.targetStaticMemberPath)))
            {
                await _bridge.SendErrorAsync(id, "REFLECTION_TARGET_CONFLICT", "targetHandle cannot be combined with instance/static member paths.", token, "reflection.call");
                return;
            }

            var invokeTcs = new TaskCompletionSource<ReflectionInvocationState>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    object target = null;
                    bool isStatic = p.isStatic;
                    if (!string.IsNullOrWhiteSpace(p.targetHandle))
                    {
                        target = p.targetHandle.StartsWith("window:", StringComparison.Ordinal)
                            ? UPilotWindowInputRegistry.ResolveHandle(p.targetHandle)
                            : _execution.Sessions.Resolve(p.sessionId, p.targetHandle);
                        if (target == null)
                            throw new ExecutionContractException("HANDLE_TARGET_INVALID", "targetHandle resolved to null.");
                        isStatic = false;
                    }

                    var type = target?.GetType() ?? FindType(p.typeName);
                    if (type == null)
                    {
                        invokeTcs.SetException(new ExecutionContractException("TYPE_NOT_FOUND", $"Type not found: {p.typeName}"));
                        return;
                    }

                    var supplied = !string.IsNullOrWhiteSpace(p.argumentsJson)
                        ? _execution.DecodeArguments(p.argumentsJson, p.sessionId)
                        : (p.parameters ?? Array.Empty<string>()).Select(value => new ExecutionValue { Value = value }).ToList();
                    var exactTypes = p.parameterTypeNames ?? Array.Empty<string>();
                    var genericTypes = (p.genericTypeArguments ?? Array.Empty<string>())
                        .Select(name => ExecutionTypeResolver.Resolve(name) ?? throw new ExecutionContractException("TYPE_NOT_FOUND", "Generic type argument not found: " + name))
                        .ToArray();
                    var bound = MethodBinder.Bind(type, p.methodName, isStatic, supplied, exactTypes, genericTypes);

                    if (!isStatic && target == null)
                    {
                        target = ResolveInstance(p, type);
                        if (target == null)
                        {
                            invokeTcs.SetException(new ExecutionContractException("REFLECTION_TARGET_NOT_FOUND", $"Could not resolve instance. targetInstancePath='{p.targetInstancePath}', targetStaticTypeName='{p.targetStaticTypeName}', targetStaticMemberPath='{p.targetStaticMemberPath}'."));
                            return;
                        }
                    }

                    var result = bound.Method.Invoke(target, bound.Arguments);
                    invokeTcs.SetResult(new ReflectionInvocationState
                    {
                        Type = type,
                        Bound = bound,
                        Result = result,
                    });
                }
                catch (TargetInvocationException tie)
                {
                    invokeTcs.SetException(tie.InnerException ?? tie);
                }
                catch (Exception ex) { invokeTcs.SetException(ex); }
            });

            try
            {
                var state = await invokeTcs.Task;
                var awaited = await AwaitableAdapter.AwaitAsync(state.Result, p.awaitMode, p.awaitTimeoutMs, token);
                var typed = _execution.EncodeResult(awaited.Value, p.sessionId, p.resultMode);
                var resultPayload = new ReflectionCallResultPayload
                {
                    typeName = state.Type.FullName ?? p.typeName,
                    methodName = p.methodName,
                    result = awaited.Value?.ToString() ?? "(null)",
                    invokedSignature = state.Bound.Signature,
                    resultValue = typed,
                    wasAwaitable = awaited.WasAwaitable,
                    awaitableStatus = awaited.Status,
                };
                foreach (var pair in state.Bound.SourceArguments)
                {
                    var parameter = state.Bound.Parameters[pair.Key];
                    if (!parameter.ParameterType.IsByRef && !parameter.IsOut) continue;
                    resultPayload.refOutArguments.Add(new ReflectionRefOutResultPayload
                    {
                        name = parameter.Name,
                        direction = parameter.IsOut ? "out" : "ref",
                        value = _execution.EncodeResult(state.Bound.Arguments[pair.Key], p.sessionId, p.resultMode),
                    });
                }
                await _bridge.SendResultAsync(id, "reflection.call", resultPayload, token);
            }
            catch (ExecutionContractException ex)
            {
                await _execution.SendContractErrorAsync(id, "reflection.call", ex, token);
            }
            catch (OperationCanceledException)
            {
                await _execution.SendContractErrorAsync(id, "reflection.call",
                    new ExecutionContractException("EXECUTION_CANCELLED", "Execution was cancelled.",
                        new Dictionary<string, object> { { "stage", "cancelled" }, { "sideEffectsMayHaveOccurred", true } }), token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "REFLECTION_CALL_FAILED", ex.GetType().FullName + ": " + ex.Message, token, "reflection.call");
            }
        }

        private sealed class ReflectionInvocationState
        {
            public Type Type;
            public BoundInvocation Bound;
            public object Result;
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName);
                    if (t != null) return t;
                }
                catch { /* skip */ }
            }
            // Fallback: search by name without namespace
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == fullName) return t;
                    }
                }
                catch { /* skip */ }
            }
            return null;
        }

        private static List<Type> FindTypes(string typeName)
        {
            var exact = new List<Type>();
            var shortMatches = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(type => type != null).ToArray(); }
                catch { continue; }
                foreach (var type in types)
                {
                    if (type == null) continue;
                    if (string.Equals(type.FullName, typeName, StringComparison.Ordinal) ||
                        string.Equals(type.AssemblyQualifiedName, typeName, StringComparison.Ordinal))
                        exact.Add(type);
                    else if (string.Equals(type.Name, typeName, StringComparison.Ordinal))
                        shortMatches.Add(type);
                }
            }
            return exact.Count > 0 ? exact : shortMatches;
        }

        private static bool TryBindMethod(
            Type type,
            ReflectionCallPayload payload,
            out MethodInfo method,
            out object[] args,
            out string error)
        {
            method = null;
            args = null;
            error = null;

            var flags = BindingFlags.Public | BindingFlags.NonPublic |
                        (payload.isStatic ? BindingFlags.Static : BindingFlags.Instance);
            var supplied = payload.parameters ?? Array.Empty<string>();
            var candidates = type.GetMethods(flags)
                .Where(m => m.Name == payload.methodName)
                .ToArray();

            var failures = new List<string>();
            BoundMethod best = null;
            foreach (var candidate in candidates)
            {
                var parameters = candidate.GetParameters();
                int requiredCount = parameters.Count(p => !p.IsOptional && !p.HasDefaultValue);
                if (supplied.Length < requiredCount || supplied.Length > parameters.Length)
                {
                    failures.Add($"{FormatSignature(candidate)}: expected {requiredCount}-{parameters.Length} args, got {supplied.Length}");
                    continue;
                }

                var candidateArgs = new object[parameters.Length];
                int score = 0;
                bool ok = true;
                var conversionErrors = new List<string>();

                for (int i = 0; i < supplied.Length; i++)
                {
                    if (!TryConvertParameter(supplied[i], parameters[i].ParameterType, out candidateArgs[i], out var conversionError, out var conversionScore))
                    {
                        ok = false;
                        conversionErrors.Add($"{parameters[i].Name}: {conversionError}");
                        break;
                    }
                    score += conversionScore;
                }

                if (!ok)
                {
                    failures.Add($"{FormatSignature(candidate)}: {string.Join("; ", conversionErrors)}");
                    continue;
                }

                for (int i = supplied.Length; i < parameters.Length; i++)
                {
                    candidateArgs[i] = GetDefaultParameterValue(parameters[i]);
                }

                var bound = new BoundMethod(candidate, candidateArgs, score);
                if (best == null || bound.Score > best.Score)
                {
                    best = bound;
                }
            }

            if (best != null)
            {
                method = best.Method;
                args = best.Args;
                return true;
            }

            int suppliedCount = supplied.Length;
            var signatures = candidates.Length == 0
                ? "(no same-name candidates)"
                : string.Join("\n- ", candidates.Select(FormatSignature));
            string failureText = failures.Count == 0 ? "" : "\nBinding failures:\n- " + string.Join("\n- ", failures);
            error = $"Method not found or arguments could not bind: {type.FullName}.{payload.methodName} with {suppliedCount} supplied parameters.\nCandidates:\n- {signatures}{failureText}";
            return false;
        }

        private static object GetDefaultParameterValue(ParameterInfo parameter)
        {
            if (parameter.HasDefaultValue)
            {
                var value = parameter.DefaultValue;
                if (value == DBNull.Value) return Type.Missing;
                return value;
            }

            if (parameter.ParameterType.IsValueType)
            {
                return Activator.CreateInstance(parameter.ParameterType);
            }

            return null;
        }

        private static bool TryConvertParameter(string value, Type targetType, out object result, out string error, out int score)
        {
            result = null;
            error = null;
            score = 0;

            Type nullableType = Nullable.GetUnderlyingType(targetType);
            if (nullableType != null)
            {
                if (IsNullLiteral(value))
                {
                    score = 2;
                    return true;
                }
                targetType = nullableType;
            }

            if (IsNullLiteral(value))
            {
                if (!targetType.IsValueType)
                {
                    score = 2;
                    return true;
                }

                error = $"null cannot bind to value type {GetFriendlyTypeName(targetType)}";
                return false;
            }

            try
            {
                if (targetType == typeof(string))
                {
                    result = UnquoteJsonString(value);
                    score = 4;
                    return true;
                }

                if (targetType == typeof(object))
                {
                    result = value;
                    score = 1;
                    return true;
                }

                if (targetType.IsArray)
                {
                    result = ConvertArrayParameter(value, targetType.GetElementType());
                    score = 3;
                    return true;
                }

                if (targetType.IsEnum)
                {
                    result = ConvertEnumParameter(value, targetType);
                    score = 4;
                    return true;
                }

                if (targetType == typeof(bool))   { result = bool.Parse(value); score = 4; return true; }
                if (targetType == typeof(byte))   { result = byte.Parse(value, CultureInfo.InvariantCulture); score = 4; return true; }
                if (targetType == typeof(sbyte))  { result = sbyte.Parse(value, CultureInfo.InvariantCulture); score = 4; return true; }
                if (targetType == typeof(short))  { result = short.Parse(value, CultureInfo.InvariantCulture); score = 4; return true; }
                if (targetType == typeof(ushort)) { result = ushort.Parse(value, CultureInfo.InvariantCulture); score = 4; return true; }
                if (targetType == typeof(int))    { result = int.Parse(value, CultureInfo.InvariantCulture); score = 4; return true; }
                if (targetType == typeof(uint))   { result = uint.Parse(value, CultureInfo.InvariantCulture); score = 4; return true; }
                if (targetType == typeof(long))   { result = long.Parse(value, CultureInfo.InvariantCulture); score = 4; return true; }
                if (targetType == typeof(ulong))  { result = ulong.Parse(value, CultureInfo.InvariantCulture); score = 4; return true; }
                if (targetType == typeof(float))  { result = float.Parse(value, CultureInfo.InvariantCulture); score = 4; return true; }
                if (targetType == typeof(double)) { result = double.Parse(value, CultureInfo.InvariantCulture); score = 4; return true; }
                if (targetType == typeof(decimal)){ result = decimal.Parse(value, CultureInfo.InvariantCulture); score = 4; return true; }
                if (targetType == typeof(char))   { result = UnquoteJsonString(value)[0]; score = 4; return true; }

                try
                {
                    result = JsonUtility.FromJson(value, targetType);
                    score = 2;
                    return true;
                }
                catch
                {
                    result = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
                    score = 1;
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = $"cannot convert '{value}' to {GetFriendlyTypeName(targetType)} ({ex.Message})";
                return false;
            }
        }

        /// <summary>
        /// Resolve an instance from a hierarchy path like "/Canvas/Button" → find GO → get component of type.
        /// </summary>
        private static object ResolveInstance(ReflectionCallPayload payload, Type componentType)
        {
            if (!string.IsNullOrEmpty(payload.targetStaticTypeName) || !string.IsNullOrEmpty(payload.targetStaticMemberPath))
            {
                var staticType = string.IsNullOrEmpty(payload.targetStaticTypeName)
                    ? componentType
                    : FindType(payload.targetStaticTypeName);
                if (staticType != null)
                {
                    var memberTarget = ResolveMemberPath(staticType, payload.targetStaticMemberPath, true);
                    if (memberTarget != null) return memberTarget;
                }
            }

            var targetInstancePath = payload.targetInstancePath;
            if (string.IsNullOrEmpty(targetInstancePath)) return null;

            var staticPathTarget = ResolveStaticExpression(targetInstancePath, componentType);
            if (staticPathTarget != null) return staticPathTarget;

            // Try to find the GameObject by hierarchy path
            var go = GameObject.Find(targetInstancePath);
            if (go == null)
            {
                // Try without leading slash
                string trimmed = targetInstancePath.TrimStart('/');
                go = GameObject.Find(trimmed);
            }

            if (go == null) return null;

            // If the target type is a Component subclass, get it from the GO
            if (typeof(Component).IsAssignableFrom(componentType))
            {
                return go.GetComponent(componentType);
            }

            // If the target type is GameObject itself
            if (componentType == typeof(GameObject))
            {
                return go;
            }

            // Try to find any component of that type
            return go.GetComponent(componentType);
        }

        private static object ConvertArrayParameter(string value, Type elementType)
        {
            var parts = SplitArrayLiteral(value);
            var array = Array.CreateInstance(elementType, parts.Count);
            for (int i = 0; i < parts.Count; i++)
            {
                if (!TryConvertParameter(parts[i], elementType, out var item, out var error, out _))
                {
                    throw new InvalidOperationException($"array element {i}: {error}");
                }
                array.SetValue(item, i);
            }
            return array;
        }

        private static object ConvertEnumParameter(string value, Type enumType)
        {
            value = UnquoteJsonString(value);
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            {
                return Enum.ToObject(enumType, numeric);
            }
            return Enum.Parse(enumType, value, true);
        }

        private static List<string> SplitArrayLiteral(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (!value.StartsWith("[", StringComparison.Ordinal) || !value.EndsWith("]", StringComparison.Ordinal))
            {
                return string.IsNullOrEmpty(value) ? new List<string>() : new List<string> { value };
            }

            string inner = value.Substring(1, value.Length - 2);
            var result = new List<string>();
            var current = new StringBuilder();
            int depth = 0;
            bool inString = false;
            bool escaped = false;

            foreach (char c in inner)
            {
                if (escaped)
                {
                    current.Append(c);
                    escaped = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    current.Append(c);
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    current.Append(c);
                    continue;
                }

                if (!inString)
                {
                    if (c == '[' || c == '{') depth++;
                    if (c == ']' || c == '}') depth--;
                    if (c == ',' && depth == 0)
                    {
                        result.Add(current.ToString().Trim());
                        current.Length = 0;
                        continue;
                    }
                }

                current.Append(c);
            }

            string tail = current.ToString().Trim();
            if (tail.Length > 0) result.Add(tail);
            return result;
        }

        private static object ResolveStaticExpression(string expression, Type fallbackType)
        {
            expression = (expression ?? string.Empty).Trim();
            if (expression.Length == 0) return null;

            if (!expression.Contains("."))
            {
                return ResolveMemberPath(fallbackType, expression, true);
            }

            for (int i = expression.Length - 1; i > 0; i--)
            {
                if (expression[i] != '.') continue;

                string typeName = expression.Substring(0, i);
                var type = FindType(typeName);
                if (type == null) continue;

                string memberPath = expression.Substring(i + 1);
                return ResolveMemberPath(type, memberPath, true);
            }

            return null;
        }

        private static object ResolveMemberPath(Type startType, string memberPath, bool firstMemberIsStatic)
        {
            if (startType == null || string.IsNullOrEmpty(memberPath)) return null;

            object current = null;
            Type currentType = startType;
            bool requireStatic = firstMemberIsStatic;
            foreach (var rawPart in memberPath.Split('.'))
            {
                string part = rawPart.Trim();
                if (part.Length == 0) return null;

                if (!TryGetMemberValue(currentType, current, part, requireStatic, out current))
                {
                    return null;
                }

                if (current == null) return null;
                currentType = current.GetType();
                requireStatic = false;
            }

            return current;
        }

        private static bool TryGetMemberValue(Type type, object target, string memberName, bool requireStatic, out object value)
        {
            value = null;
            var flags = BindingFlags.Public | BindingFlags.NonPublic |
                        (requireStatic ? BindingFlags.Static : BindingFlags.Instance) |
                        BindingFlags.FlattenHierarchy;

            for (var t = type; t != null; t = t.BaseType)
            {
                var property = t.GetProperty(memberName, flags | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    value = property.GetValue(requireStatic ? null : target, null);
                    return true;
                }

                var field = t.GetField(memberName, flags | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    value = field.GetValue(requireStatic ? null : target);
                    return true;
                }
            }

            return false;
        }

        private static bool IsNullLiteral(string value)
        {
            return value == null || string.Equals(value.Trim(), "null", StringComparison.OrdinalIgnoreCase);
        }

        private static string UnquoteJsonString(string value)
        {
            if (value == null) return null;
            value = value.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                value = value.Substring(1, value.Length - 2);
                return value.Replace("\\\"", "\"").Replace("\\\\", "\\");
            }
            return value;
        }

        private static string FormatSignature(MethodInfo method)
        {
            string parameters = string.Join(", ", method.GetParameters().Select(p =>
            {
                string optional = p.IsOptional || p.HasDefaultValue ? " = " + FormatDefaultValue(p) : "";
                return $"{GetFriendlyTypeName(p.ParameterType)} {p.Name}{optional}";
            }));
            return $"{GetFriendlyTypeName(method.ReturnType)} {method.Name}({parameters})";
        }

        private static string FormatDefaultValue(ParameterInfo parameter)
        {
            if (!parameter.HasDefaultValue) return "?";
            var value = parameter.DefaultValue;
            if (value == null) return "null";
            if (value is string s) return "\"" + s + "\"";
            if (value is bool b) return b ? "true" : "false";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string GetFriendlyTypeName(Type type)
        {
            if (type == null) return "(null)";
            if (!type.IsArray) return type.FullName ?? type.Name;
            return GetFriendlyTypeName(type.GetElementType()) + "[]";
        }

        private sealed class BoundMethod
        {
            public readonly MethodInfo Method;
            public readonly object[] Args;
            public readonly int Score;

            public BoundMethod(MethodInfo method, object[] args, int score)
            {
                Method = method;
                Args = args;
                Score = score;
            }
        }
    }
}
