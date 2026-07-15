// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace CodingRiver.UPilot
{
    /*
     * reflection.eval / reflection_eval is a bounded, expression-only C# evaluator
     * for Unity Editor automation. It parses one C# expression statement, evaluates
     * it with reflection, and returns a formatted result.
     *
     * Supported:
     * - Static type paths and existing object access, for example TypeName.Inst.Member.
     * - Chained member access, indexers, dictionary/list/array indexes, and method calls.
     * - JSON-provided variables as readable expression inputs.
     * - Literals: null, bool, string/char, integer/floating numeric values with common suffixes.
     * - Typed arrays: new uint[]{1,2}, new uint[2]{1,2}, and uint[]{1,2}.
     * - Whitelisted Unity value constructors: Vector2, Vector3, Vector4, Quaternion.
     * - Operators: unary ! + - ~; binary * / % + - << >> < <= > >= == != & ^ | && ||.
     * - Ternary ?:, casts, is/as, null-conditional access ?., and parentheses.
     * - Assignment to reflected members or indexers with =, +=, and -=.
     * - Options for result mode, timeout, max token/call limits, namespace allow-list,
     *   deny-method list, non-public member access, and trace output.
     *
     * Not supported:
     * - Full C# statements or blocks; only one expression statement with an optional semicolon.
     * - if/for/foreach/while/do/switch control flow.
     * - lambda expressions, LINQ query syntax, async/await, or direct delegate invocation.
     * - ref/out/in arguments.
     * - Creating arbitrary new objects or classes that do not already exist in loaded assemblies.
     * - Defining types, methods, local functions, variables, using directives, or namespaces.
     * - Persistently writing back to the variables JSON object; variables are read inputs.
     */
    [Serializable] public class ReflectionEvalMessage { public ReflectionEvalPayload payload; }

    [Serializable]
    public class ReflectionEvalPayload
    {
        public string code = "";
        public string variablesJson = "";
        public string optionsJson = "";
    }

    [Serializable]
    public class ReflectionEvalResultPayload
    {
        public string code;
        public string result;
        public string resultType;
        public string trace;
    }

    public sealed class ReflectionEvalService
    {
        private readonly UPilotBridge _bridge;

        public ReflectionEvalService(UPilotBridge bridge)
        {
            _bridge = bridge;
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("reflection.eval", HandleEvalAsync);
        }

        private async Task HandleEvalAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ReflectionEvalMessage>(json);
            var p = msg?.payload ?? new ReflectionEvalPayload();

            if (string.IsNullOrWhiteSpace(p.code))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "code is required.", token, "reflection.eval");
                return;
            }

            var tcs = new TaskCompletionSource<ReflectionEvalResultPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var options = ReflectionEvalOptions.FromJson(p.optionsJson);
                    var variables = ReflectionEvalVariables.FromJson(p.variablesJson);
                    var evaluator = new Evaluator(options, variables);
                    var parser = new Parser(p.code, evaluator);
                    var value = parser.ParseStatementExpression();
                    tcs.SetResult(new ReflectionEvalResultPayload
                    {
                        code = p.code,
                        result = FormatResult(value, options),
                        resultType = value?.GetType().FullName ?? "(null)",
                        trace = options.Trace ? evaluator.TraceText : "",
                    });
                }
                catch (TargetInvocationException tie)
                {
                    tcs.SetException(tie.InnerException ?? tie);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "reflection.eval", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "REFLECTION_EVAL_FAILED", ex.Message, token, "reflection.eval");
            }
        }

        private static string FormatResult(object value, ReflectionEvalOptions options)
        {
            var mode = (options.ResultMode ?? "string").Trim().ToLowerInvariant();
            if (mode == "type") return value?.GetType().FullName ?? "(null)";
            if (mode == "instanceid")
            {
                return value is UnityEngine.Object obj ? UPilotEntityIds.ToWireId(obj).ToString(CultureInfo.InvariantCulture) : "";
            }
            if (mode == "json") return ToJsonLike(value, options.MaxResultItems);
            if (mode == "full")
            {
                return $"type={value?.GetType().FullName ?? "(null)"}; value={FormatResult(value, new ReflectionEvalOptions { ResultMode = "string", MaxResultItems = options.MaxResultItems })}";
            }

            if (value == null) return "(null)";
            if (value is string s) return s;
            if (value is IEnumerable enumerable && !(value is UnityEngine.Object))
            {
                var items = new List<string>();
                foreach (var item in enumerable)
                {
                    items.Add(item?.ToString() ?? "null");
                    if (items.Count >= options.MaxResultItems) break;
                }
                return "[" + string.Join(", ", items) + "]";
            }
            return value.ToString();
        }

        private static string ToJsonLike(object value, int maxItems)
        {
            if (value == null) return "null";
            if (value is string s) return "\"" + EscapeJsonString(s) + "\"";
            if (value is bool b) return b ? "true" : "false";
            if (IsNumeric(value)) return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (value is UnityEngine.Object obj)
            {
                return "{\"type\":\"" + EscapeJsonString(obj.GetType().FullName) + "\",\"name\":\"" + EscapeJsonString(obj.name) + "\",\"instanceId\":" + UPilotEntityIds.ToWireId(obj).ToString(CultureInfo.InvariantCulture) + "}";
            }
            if (value is IDictionary dict)
            {
                var pairs = new List<string>();
                foreach (DictionaryEntry entry in dict)
                {
                    pairs.Add(ToJsonLike(entry.Key, maxItems) + ":" + ToJsonLike(entry.Value, maxItems));
                    if (pairs.Count >= maxItems) break;
                }
                return "{" + string.Join(",", pairs) + "}";
            }
            if (value is IEnumerable enumerable)
            {
                var items = new List<string>();
                foreach (var item in enumerable)
                {
                    items.Add(ToJsonLike(item, maxItems));
                    if (items.Count >= maxItems) break;
                }
                return "[" + string.Join(",", items) + "]";
            }
            return "\"" + EscapeJsonString(value.ToString()) + "\"";
        }

        private static string EscapeJsonString(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private sealed class ReflectionEvalOptions
        {
            public bool AllowNonPublic = true;
            public bool Trace = false;
            public int MaxTokens = 512;
            public int MaxCallDepth = 64;
            public int TimeoutMs = 3000;
            public int MaxResultItems = 64;
            public string ResultMode = "string";
            public string AllowNamespacePrefixes = "";
            public string DenyMethods = "Destroy,DestroyImmediate,LoadScene,ExitPlayMode,Quit";
            public long DeadlineUtcTicks;

            public static ReflectionEvalOptions FromJson(string json)
            {
                var options = new ReflectionEvalOptions();
                if (string.IsNullOrWhiteSpace(json) || json == "null")
                {
                    options.DeadlineUtcTicks = DateTime.UtcNow.AddMilliseconds(Math.Max(1, options.TimeoutMs)).Ticks;
                    return options;
                }
                options.AllowNonPublic = ReadBool(json, "allowNonPublic", options.AllowNonPublic);
                options.Trace = ReadBool(json, "trace", options.Trace);
                options.MaxTokens = ReadInt(json, "maxTokens", options.MaxTokens);
                options.MaxCallDepth = ReadInt(json, "maxCallDepth", options.MaxCallDepth);
                options.TimeoutMs = ReadInt(json, "timeoutMs", options.TimeoutMs);
                options.MaxResultItems = ReadInt(json, "maxResultItems", options.MaxResultItems);
                options.ResultMode = ReadJsonStringProperty(json, "resultMode");
                if (string.IsNullOrEmpty(options.ResultMode)) options.ResultMode = "string";
                options.AllowNamespacePrefixes = ReadJsonStringOrArrayProperty(json, "allowNamespacePrefixes");
                var deny = ReadJsonStringOrArrayProperty(json, "denyMethods");
                if (!string.IsNullOrEmpty(deny)) options.DenyMethods = deny;
                options.DeadlineUtcTicks = DateTime.UtcNow.AddMilliseconds(Math.Max(1, options.TimeoutMs)).Ticks;
                return options;
            }

            public bool IsMethodDenied(string methodName)
            {
                if (string.IsNullOrEmpty(methodName)) return false;
                foreach (var item in (DenyMethods ?? "").Split(','))
                {
                    if (string.Equals(item.Trim(), methodName, StringComparison.Ordinal)) return true;
                }
                return false;
            }

            public bool IsTypeAllowed(Type type)
            {
                if (type == null || string.IsNullOrWhiteSpace(AllowNamespacePrefixes)) return true;
                var name = type.FullName ?? type.Name;
                foreach (var item in AllowNamespacePrefixes.Split(','))
                {
                    var prefix = item.Trim();
                    if (prefix.Length > 0 && name.StartsWith(prefix, StringComparison.Ordinal)) return true;
                }
                return false;
            }

            public void ThrowIfTimedOut()
            {
                if (DeadlineUtcTicks > 0 && DateTime.UtcNow.Ticks > DeadlineUtcTicks)
                {
                    throw new TimeoutException($"reflection.eval timed out after {TimeoutMs}ms.");
                }
            }
        }

        private sealed class ReflectionEvalVariables
        {
            private readonly Dictionary<string, TypedJsonValue> _items = new(StringComparer.Ordinal);

            public static ReflectionEvalVariables FromJson(string json)
            {
                var variables = new ReflectionEvalVariables();
                if (string.IsNullOrWhiteSpace(json) || json == "null") return variables;

                foreach (var entry in ReadTopLevelObjectMembers(json))
                {
                    var raw = entry.Value.Trim();
                    if (raw.StartsWith("{", StringComparison.Ordinal))
                    {
                        var type = ReadJsonStringProperty(raw, "type");
                        var value = ReadJsonPropertyRaw(raw, "value");
                        variables._items[entry.Key] = new TypedJsonValue(type, value);
                    }
                    else
                    {
                        variables._items[entry.Key] = new TypedJsonValue("", raw);
                    }
                }

                return variables;
            }

            public bool TryGet(string name, out object value)
            {
                value = null;
                if (!string.IsNullOrEmpty(name) && name[0] == '$') name = name.Substring(1);
                if (!_items.TryGetValue(name, out var item)) return false;
                value = ConvertTypedJsonValue(item);
                return true;
            }
        }

        private readonly struct TypedJsonValue
        {
            public readonly string TypeName;
            public readonly string ValueJson;

            public TypedJsonValue(string typeName, string valueJson)
            {
                TypeName = typeName ?? "";
                ValueJson = valueJson ?? "null";
            }
        }

        private sealed class Evaluator
        {
            private readonly ReflectionEvalOptions _options;
            private readonly ReflectionEvalVariables _variables;
            private readonly StringBuilder _trace = new();
            private int _callDepth;

            public Evaluator(ReflectionEvalOptions options, ReflectionEvalVariables variables)
            {
                _options = options;
                _variables = variables;
            }

            public bool AllowNonPublic => _options.AllowNonPublic;
            public string TraceText => _trace.ToString();
            public ReflectionEvalOptions Options => _options;

            public void CheckBudget()
            {
                _options.ThrowIfTimedOut();
            }

            public object ResolveIdentifierOrStaticPath(List<string> segments)
            {
                CheckBudget();
                if (_variables.TryGet(segments[0], out var variableValue))
                {
                    object current = variableValue;
                    Trace($"var {segments[0]} => {Describe(variableValue)}");
                    for (int i = 1; i < segments.Count; i++)
                    {
                        current = GetMember(current, segments[i]);
                    }
                    return current;
                }

                int typeEnd = FindLongestTypePrefix(segments);
                if (typeEnd >= 0)
                {
                    var typeName = string.Join(".", segments.Take(typeEnd + 1));
                    var type = FindType(typeName);
                    if (!_options.IsTypeAllowed(type)) throw new UnauthorizedAccessException($"Type is not allowed by allowNamespacePrefixes: {type.FullName}");
                    object current = type;
                    Trace($"type {type.FullName}");
                    for (int i = typeEnd + 1; i < segments.Count; i++)
                    {
                        current = GetMember(current, segments[i]);
                    }
                    return current;
                }

                throw new InvalidOperationException($"Unknown identifier or type path: {string.Join(".", segments)}");
            }

            public object GetMember(object target, string member)
            {
                CheckBudget();
                if (target == null) throw new NullReferenceException($"Cannot access member '{member}' on null.");

                var typeTarget = target as Type;
                var type = typeTarget ?? target.GetType();
                var flags = MemberFlags(typeTarget != null);
                for (var t = type; t != null; t = t.BaseType)
                {
                    var prop = t.GetProperty(member, flags | BindingFlags.DeclaredOnly);
                    if (prop != null && prop.GetIndexParameters().Length == 0)
                    {
                        var value = prop.GetValue(typeTarget != null ? null : target, null);
                        Trace($".{member} => {Describe(value)}");
                        return value;
                    }

                    var field = t.GetField(member, flags | BindingFlags.DeclaredOnly);
                    if (field != null)
                    {
                        var value = field.GetValue(typeTarget != null ? null : target);
                        Trace($".{member} => {Describe(value)}");
                        return value;
                    }
                }

                throw new MissingMemberException(type.FullName, member);
            }

            public object SetMember(object target, string member, object value)
            {
                CheckBudget();
                if (target == null) throw new NullReferenceException($"Cannot assign member '{member}' on null.");

                var typeTarget = target as Type;
                var type = typeTarget ?? target.GetType();
                if (!_options.IsTypeAllowed(type)) throw new UnauthorizedAccessException($"Type is not allowed by allowNamespacePrefixes: {type.FullName}");
                var flags = MemberFlags(typeTarget != null);
                for (var t = type; t != null; t = t.BaseType)
                {
                    var prop = t.GetProperty(member, flags | BindingFlags.DeclaredOnly);
                    if (prop != null && prop.GetIndexParameters().Length == 0)
                    {
                        if (!prop.CanWrite) throw new InvalidOperationException($"Property {type.FullName}.{member} is not writable.");
                        if (!TryConvertValue(value, prop.PropertyType, out var converted))
                        {
                            throw new InvalidCastException($"Cannot assign {Describe(value)} to {GetFriendlyTypeName(prop.PropertyType)} {member}.");
                        }
                        prop.SetValue(typeTarget != null ? null : target, converted, null);
                        Trace($".{member} = {Describe(converted)}");
                        return converted;
                    }

                    var field = t.GetField(member, flags | BindingFlags.DeclaredOnly);
                    if (field != null)
                    {
                        if (field.IsInitOnly || field.IsLiteral) throw new InvalidOperationException($"Field {type.FullName}.{member} is readonly.");
                        if (!TryConvertValue(value, field.FieldType, out var converted))
                        {
                            throw new InvalidCastException($"Cannot assign {Describe(value)} to {GetFriendlyTypeName(field.FieldType)} {member}.");
                        }
                        field.SetValue(typeTarget != null ? null : target, converted);
                        Trace($".{member} = {Describe(converted)}");
                        return converted;
                    }
                }

                throw new MissingMemberException(type.FullName, member);
            }

            public object GetIndex(object target, object index)
            {
                CheckBudget();
                if (target == null) throw new NullReferenceException("Cannot index null.");

                if (target is Array array)
                {
                    var value = array.GetValue(Convert.ToInt32(index, CultureInfo.InvariantCulture));
                    Trace($"[{index}] => {Describe(value)}");
                    return value;
                }

                if (target is IList list)
                {
                    var value = list[Convert.ToInt32(index, CultureInfo.InvariantCulture)];
                    Trace($"[{index}] => {Describe(value)}");
                    return value;
                }

                if (target is IDictionary dict)
                {
                    var key = CoerceDictionaryKey(dict, index);
                    var value = dict[key];
                    Trace($"[{index}] => {Describe(value)}");
                    return value;
                }

                var type = target.GetType();
                var indexers = type.GetDefaultMembers().OfType<PropertyInfo>().ToArray();
                foreach (var prop in indexers)
                {
                    var parameters = prop.GetIndexParameters();
                    if (parameters.Length != 1) continue;
                    if (!TryConvertValue(index, parameters[0].ParameterType, out var converted)) continue;
                    var value = prop.GetValue(target, new[] { converted });
                    Trace($"[{index}] => {Describe(value)}");
                    return value;
                }

                throw new InvalidOperationException($"Type {type.FullName} does not support indexing.");
            }

            public object SetIndex(object target, object index, object value)
            {
                CheckBudget();
                if (target == null) throw new NullReferenceException("Cannot assign index on null.");

                if (target is Array array)
                {
                    var elementType = array.GetType().GetElementType();
                    if (!TryConvertValue(value, elementType, out var converted))
                    {
                        throw new InvalidCastException($"Cannot assign {Describe(value)} to {GetFriendlyTypeName(elementType)} array element.");
                    }
                    array.SetValue(converted, Convert.ToInt32(index, CultureInfo.InvariantCulture));
                    Trace($"[{index}] = {Describe(converted)}");
                    return converted;
                }

                if (target is IList list)
                {
                    int itemIndex = Convert.ToInt32(index, CultureInfo.InvariantCulture);
                    var elementType = GetListElementType(target.GetType()) ?? list[itemIndex]?.GetType();
                    object converted = value;
                    if (elementType != null && !TryConvertValue(value, elementType, out converted))
                    {
                        throw new InvalidCastException($"Cannot assign {Describe(value)} to {GetFriendlyTypeName(elementType)} list element.");
                    }
                    list[itemIndex] = converted;
                    Trace($"[{index}] = {Describe(converted)}");
                    return converted;
                }

                if (target is IDictionary dict)
                {
                    var key = CoerceDictionaryKey(dict, index);
                    var types = GetDictionaryTypes(target.GetType());
                    if (types.KeyType != null && !TryConvertValue(key, types.KeyType, out key))
                    {
                        throw new InvalidCastException($"Cannot convert dictionary key {Describe(index)} to {GetFriendlyTypeName(types.KeyType)}.");
                    }

                    var valueType = types.ValueType ?? (dict.Contains(key) ? dict[key]?.GetType() : null);
                    object converted = value;
                    if (valueType != null && !TryConvertValue(value, valueType, out converted))
                    {
                        throw new InvalidCastException($"Cannot assign {Describe(value)} to {GetFriendlyTypeName(valueType)} dictionary value.");
                    }
                    dict[key] = converted;
                    Trace($"[{index}] = {Describe(converted)}");
                    return converted;
                }

                var type = target.GetType();
                var indexers = type.GetDefaultMembers().OfType<PropertyInfo>().ToArray();
                foreach (var prop in indexers)
                {
                    if (!prop.CanWrite) continue;
                    var parameters = prop.GetIndexParameters();
                    if (parameters.Length != 1) continue;
                    if (!TryConvertValue(index, parameters[0].ParameterType, out var convertedIndex)) continue;
                    if (!TryConvertValue(value, prop.PropertyType, out var convertedValue)) continue;
                    prop.SetValue(target, convertedValue, new[] { convertedIndex });
                    Trace($"[{index}] = {Describe(convertedValue)}");
                    return convertedValue;
                }

                throw new InvalidOperationException($"Type {type.FullName} does not support writable indexing.");
            }

            public object Call(object target, string methodName, List<object> args)
            {
                CheckBudget();
                if (target == null) throw new NullReferenceException($"Cannot call '{methodName}' on null.");
                if (_options.IsMethodDenied(methodName)) throw new UnauthorizedAccessException($"Method is denied by reflection.eval options: {methodName}");
                if (_callDepth >= _options.MaxCallDepth)
                {
                    throw new InvalidOperationException($"Max call depth exceeded: {_options.MaxCallDepth}");
                }
                _callDepth++;

                try
                {
                    var typeTarget = target as Type;
                    var type = typeTarget ?? target.GetType();
                    if (!_options.IsTypeAllowed(type)) throw new UnauthorizedAccessException($"Type is not allowed by allowNamespacePrefixes: {type.FullName}");
                    var flags = MemberFlags(typeTarget != null);
                    var candidates = type.GetMethods(flags).Where(m => m.Name == methodName).ToArray();
                    var failures = new List<string>();
                    BoundMethod best = null;

                    foreach (var method in candidates)
                    {
                        var parameters = method.GetParameters();
                        if (method.ContainsGenericParameters)
                        {
                            failures.Add($"{FormatSignature(method)}: generic methods are not supported");
                            continue;
                        }
                        if (parameters.Any(p => p.ParameterType.IsByRef))
                        {
                            failures.Add($"{FormatSignature(method)}: ref/out/in parameters are not supported");
                            continue;
                        }
                        int required = parameters.Count(p => !p.IsOptional && !p.HasDefaultValue);
                        if (args.Count < required || args.Count > parameters.Length)
                        {
                            failures.Add($"{FormatSignature(method)}: expected {required}-{parameters.Length} args, got {args.Count}");
                            continue;
                        }

                        var boundArgs = new object[parameters.Length];
                        int score = 0;
                        bool ok = true;
                        for (int i = 0; i < args.Count; i++)
                        {
                            if (!TryConvertValue(args[i], parameters[i].ParameterType, out var converted))
                            {
                                ok = false;
                                failures.Add($"{FormatSignature(method)}: cannot bind arg {i} to {GetFriendlyTypeName(parameters[i].ParameterType)}");
                                break;
                            }
                            boundArgs[i] = converted;
                            score += ScoreConversion(args[i], parameters[i].ParameterType);
                        }

                        if (!ok) continue;
                        for (int i = args.Count; i < parameters.Length; i++)
                        {
                            boundArgs[i] = GetDefaultParameterValue(parameters[i]);
                        }

                        var bound = new BoundMethod(method, boundArgs, score);
                        if (best == null || bound.Score > best.Score) best = bound;
                    }

                    if (best == null)
                    {
                        if (typeTarget == null && TryBindExtensionMethod(type, target, methodName, args, out best))
                        {
                            var extResult = best.Method.Invoke(null, best.Args);
                            CheckBudget();
                            Trace($".{methodName}(extension ...) => {Describe(extResult)}");
                            return extResult;
                        }

                        var sigs = candidates.Length == 0 ? "(no candidates)" : string.Join("\n- ", candidates.Select(FormatSignature));
                        var detail = failures.Count == 0 ? "" : "\nBinding failures:\n- " + string.Join("\n- ", failures);
                        throw new MissingMethodException($"Cannot bind method {type.FullName}.{methodName}({args.Count} args).\nCandidates:\n- {sigs}{detail}");
                    }

                    var result = best.Method.Invoke(typeTarget != null ? null : target, best.Args);
                    CheckBudget();
                    Trace($".{methodName}(...) => {Describe(result)}");
                    return result;
                }
                finally
                {
                    _callDepth--;
                }
            }

            private bool TryBindExtensionMethod(Type receiverType, object receiver, string methodName, List<object> args, out BoundMethod best)
            {
                best = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    CheckBudget();
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch { continue; }

                    foreach (var type in types)
                    {
                        CheckBudget();
                        if (!type.IsSealed || !type.IsAbstract) continue;
                        if (!_options.IsTypeAllowed(type)) continue;

                        MethodInfo[] methods;
                        try
                        {
                            methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        }
                        catch { continue; }

                        foreach (var method in methods)
                        {
                            CheckBudget();
                            if (method.Name != methodName) continue;
                            if (method.ContainsGenericParameters) continue;
                            if (!method.IsDefined(typeof(ExtensionAttribute), false)) continue;

                            var parameters = method.GetParameters();
                            if (parameters.Length == 0) continue;
                            if (parameters.Any(p => p.ParameterType.IsByRef)) continue;
                            if (!TryConvertValue(receiver, parameters[0].ParameterType, out var convertedReceiver)) continue;

                            int required = parameters.Count(p => !p.IsOptional && !p.HasDefaultValue);
                            int suppliedTotal = args.Count + 1;
                            if (suppliedTotal < required || suppliedTotal > parameters.Length) continue;

                            var boundArgs = new object[parameters.Length];
                            boundArgs[0] = convertedReceiver;
                            int score = 1 + ScoreConversion(receiver, parameters[0].ParameterType);
                            bool ok = true;
                            for (int i = 0; i < args.Count; i++)
                            {
                                if (!TryConvertValue(args[i], parameters[i + 1].ParameterType, out var converted))
                                {
                                    ok = false;
                                    break;
                                }
                                boundArgs[i + 1] = converted;
                                score += ScoreConversion(args[i], parameters[i + 1].ParameterType);
                            }
                            if (!ok) continue;

                            for (int i = suppliedTotal; i < parameters.Length; i++)
                            {
                                boundArgs[i] = GetDefaultParameterValue(parameters[i]);
                            }

                            var bound = new BoundMethod(method, boundArgs, score);
                            if (best == null || bound.Score > best.Score) best = bound;
                        }
                    }
                }

                return best != null;
            }

            public object ApplyUnary(string op, object value)
            {
                return op switch
                {
                    "!" => !Convert.ToBoolean(value, CultureInfo.InvariantCulture),
                    "+" => PromoteNumeric(value),
                    "-" => Negate(value),
                    "~" => ~Convert.ToInt64(value, CultureInfo.InvariantCulture),
                    _ => throw new NotSupportedException($"Unsupported unary operator: {op}"),
                };
            }

            public object ApplyBinary(string op, object left, object right)
            {
                if (op == "&&") return Convert.ToBoolean(left, CultureInfo.InvariantCulture) && Convert.ToBoolean(right, CultureInfo.InvariantCulture);
                if (op == "||") return Convert.ToBoolean(left, CultureInfo.InvariantCulture) || Convert.ToBoolean(right, CultureInfo.InvariantCulture);
                if (op == "==" || op == "!=")
                {
                    bool equals = EqualsWithNumericCoercion(left, right);
                    return op == "==" ? equals : !equals;
                }

                if (op is ">" or ">=" or "<" or "<=")
                {
                    int cmp = CompareValues(left, right);
                    return op switch
                    {
                        ">" => cmp > 0,
                        ">=" => cmp >= 0,
                        "<" => cmp < 0,
                        "<=" => cmp <= 0,
                        _ => false,
                    };
                }

                if (op == "+" && (left is string || right is string))
                {
                    return Convert.ToString(left, CultureInfo.InvariantCulture) + Convert.ToString(right, CultureInfo.InvariantCulture);
                }

                if (IsFloating(left) || IsFloating(right))
                {
                    double a = Convert.ToDouble(left, CultureInfo.InvariantCulture);
                    double b = Convert.ToDouble(right, CultureInfo.InvariantCulture);
                    return op switch
                    {
                        "+" => a + b,
                        "-" => a - b,
                        "*" => a * b,
                        "/" => a / b,
                        "%" => a % b,
                        _ => throw new NotSupportedException($"Unsupported floating operator: {op}"),
                    };
                }

                long la = Convert.ToInt64(left, CultureInfo.InvariantCulture);
                long lb = Convert.ToInt64(right, CultureInfo.InvariantCulture);
                return op switch
                {
                    "+" => la + lb,
                    "-" => la - lb,
                    "*" => la * lb,
                    "/" => la / lb,
                    "%" => la % lb,
                    "&" => la & lb,
                    "|" => la | lb,
                    "^" => la ^ lb,
                    "<<" => la << Convert.ToInt32(lb, CultureInfo.InvariantCulture),
                    ">>" => la >> Convert.ToInt32(lb, CultureInfo.InvariantCulture),
                    _ => throw new NotSupportedException($"Unsupported binary operator: {op}"),
                };
            }

            public object ApplyCast(Type targetType, object value)
            {
                CheckBudget();
                if (!TryConvertValue(value, targetType, out var converted))
                {
                    throw new InvalidCastException($"Cannot cast {Describe(value)} to {GetFriendlyTypeName(targetType)}.");
                }
                return converted;
            }

            public object ApplyIsAs(string op, object value, Type targetType)
            {
                CheckBudget();
                if (op == "is") return value != null && targetType.IsInstanceOfType(value);
                if (op == "as")
                {
                    if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                    {
                        throw new NotSupportedException("'as' requires a reference type or nullable value type.");
                    }
                    return value != null && targetType.IsInstanceOfType(value) ? value : null;
                }
                throw new NotSupportedException($"Unsupported type operator: {op}");
            }

            public object ApplyAssignment(IAssignable assignable, string op, object right)
            {
                CheckBudget();
                if (assignable == null) throw new InvalidOperationException("Left side of assignment is not assignable.");
                var value = right;
                if (op == "+=") value = ApplyBinary("+", assignable.GetValue(), right);
                else if (op == "-=") value = ApplyBinary("-", assignable.GetValue(), right);
                else if (op != "=") throw new NotSupportedException($"Unsupported assignment operator: {op}");
                return assignable.SetValue(value);
            }

            private BindingFlags MemberFlags(bool isStatic)
            {
                var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.FlattenHierarchy;
                if (_options.AllowNonPublic) flags |= BindingFlags.NonPublic;
                return flags;
            }

            private void Trace(string line)
            {
                if (_options.Trace) _trace.AppendLine(line);
            }
        }

        private interface IAssignable
        {
            object GetValue();
            object SetValue(object value);
        }

        private sealed class MemberAssignable : IAssignable
        {
            private readonly Evaluator _evaluator;
            private readonly object _target;
            private readonly string _member;

            public MemberAssignable(Evaluator evaluator, object target, string member)
            {
                _evaluator = evaluator;
                _target = target;
                _member = member;
            }

            public object GetValue() => _evaluator.GetMember(_target, _member);
            public object SetValue(object value) => _evaluator.SetMember(_target, _member, value);
        }

        private sealed class IndexAssignable : IAssignable
        {
            private readonly Evaluator _evaluator;
            private readonly object _target;
            private readonly object _index;

            public IndexAssignable(Evaluator evaluator, object target, object index)
            {
                _evaluator = evaluator;
                _target = target;
                _index = index;
            }

            public object GetValue() => _evaluator.GetIndex(_target, _index);
            public object SetValue(object value) => _evaluator.SetIndex(_target, _index, value);
        }

        private sealed class Parser
        {
            private readonly List<Token> _tokens;
            private readonly Evaluator _evaluator;
            private int _pos;
            private int _suppressDepth;
            private static readonly object NullChain = new();

            public Parser(string code, Evaluator evaluator)
            {
                _tokens = new Tokenizer(code).Tokenize();
                if (_tokens.Count > evaluator.Options.MaxTokens)
                {
                    throw new InvalidOperationException($"Max token count exceeded: {evaluator.Options.MaxTokens}");
                }
                _evaluator = evaluator;
            }

            public object ParseStatementExpression()
            {
                var value = ParseAssignmentExpression();
                Match(";");
                Expect(TokenKind.Eof);
                return value;
            }

            private object ParseAssignmentExpression()
            {
                _evaluator.CheckBudget();
                if (IsSuppressed && TryParseAssignableSuppressed())
                {
                    var suppressedOp = Peek().Text;
                    if (IsAssignmentOperator(suppressedOp))
                    {
                        Next();
                        ParseAssignmentExpression();
                        return null;
                    }
                }

                int save = _pos;
                if (TryParseAssignable(out var assignable))
                {
                    var op = Peek().Text;
                    if (IsAssignmentOperator(op))
                    {
                        Next();
                        var right = ParseAssignmentExpression();
                        return IsSuppressed ? null : _evaluator.ApplyAssignment(assignable, op, right);
                    }
                }

                _pos = save;
                return ParseExpression(0);
            }

            private object ParseExpression(int minPrecedence)
            {
                _evaluator.CheckBudget();
                var left = ParseUnary();
                while (true)
                {
                    var token = Peek();
                    if (token.Kind == TokenKind.Identifier && (token.Text == "is" || token.Text == "as"))
                    {
                        int typePrecedence = 7;
                        if (typePrecedence < minPrecedence) break;
                        var opText = Next().Text;
                        var type = ParseTypeReference();
                        left = IsSuppressed ? null : _evaluator.ApplyIsAs(opText, left, type);
                        continue;
                    }

                    if (token.Kind != TokenKind.Operator) break;
                    var op = token.Text;
                    int precedence = GetPrecedence(op);
                    if (precedence < minPrecedence) break;
                    Next();
                    if (op == "&&" && !IsSuppressed && !Convert.ToBoolean(left, CultureInfo.InvariantCulture))
                    {
                        using (Suppress()) ParseExpression(precedence + 1);
                        left = false;
                        continue;
                    }

                    if (op == "||" && !IsSuppressed && Convert.ToBoolean(left, CultureInfo.InvariantCulture))
                    {
                        using (Suppress()) ParseExpression(precedence + 1);
                        left = true;
                        continue;
                    }

                    var right = ParseExpression(precedence + 1);
                    left = IsSuppressed ? null : _evaluator.ApplyBinary(op, left, right);
                }

                if (minPrecedence == 0 && Match("?"))
                {
                    bool takeTrue = !IsSuppressed && Convert.ToBoolean(left, CultureInfo.InvariantCulture);
                    object trueValue;
                    object falseValue;
                    if (takeTrue)
                    {
                        trueValue = ParseAssignmentExpression();
                        Expect(":");
                        using (Suppress()) ParseAssignmentExpression();
                        falseValue = null;
                    }
                    else
                    {
                        using (Suppress()) ParseAssignmentExpression();
                        Expect(":");
                        falseValue = ParseAssignmentExpression();
                        trueValue = null;
                    }

                    left = IsSuppressed ? null : (takeTrue ? trueValue : falseValue);
                }
                return left;
            }

            private object ParseUnary()
            {
                if (TryParseCast(out var castType))
                {
                    var value = ParseUnary();
                    return IsSuppressed ? null : _evaluator.ApplyCast(castType, value);
                }

                if (Peek().Kind == TokenKind.Operator && (Peek().Text == "!" || Peek().Text == "+" || Peek().Text == "-" || Peek().Text == "~"))
                {
                    var op = Next().Text;
                    var value = ParseUnary();
                    return IsSuppressed ? null : _evaluator.ApplyUnary(op, value);
                }
                return ParsePostfix(ParsePrimary());
            }

            private object ParsePostfix(object value)
            {
                while (true)
                {
                    bool isNullChain = ReferenceEquals(value, NullChain);
                    if (Match("?."))
                    {
                        string member = Expect(TokenKind.Identifier).Text;
                        bool suppressSegment = IsSuppressed || value == null || isNullChain;
                        if (Match("("))
                        {
                            var args = suppressSegment ? ParseArgumentListSuppressed() : ParseArgumentList();
                            value = suppressSegment ? NullChain : _evaluator.Call(value, member, args);
                        }
                        else
                        {
                            value = suppressSegment ? NullChain : _evaluator.GetMember(value, member);
                        }
                        continue;
                    }

                    if (Match("."))
                    {
                        string member = Expect(TokenKind.Identifier).Text;
                        if (Match("("))
                        {
                            var args = IsSuppressed || isNullChain ? ParseArgumentListSuppressed() : ParseArgumentList();
                            value = IsSuppressed || isNullChain ? value : _evaluator.Call(value, member, args);
                        }
                        else
                        {
                            value = IsSuppressed || isNullChain ? value : _evaluator.GetMember(value, member);
                        }
                        continue;
                    }

                    if (Match("["))
                    {
                        var index = IsSuppressed || isNullChain ? ParseAssignmentExpressionSuppressed() : ParseAssignmentExpression();
                        Expect("]");
                        value = IsSuppressed || isNullChain ? value : _evaluator.GetIndex(value, index);
                        continue;
                    }

                    if (Match("("))
                    {
                        throw new NotSupportedException("Direct delegate invocation is not supported. Use .Method(...) calls.");
                    }

                    return ReferenceEquals(value, NullChain) ? null : value;
                }
            }

            private object ParsePrimary()
            {
                var token = Next();
                switch (token.Kind)
                {
                    case TokenKind.String:
                        return token.Value;
                    case TokenKind.Number:
                        return token.Value;
                    case TokenKind.Identifier:
                        if (token.Text == "true") return true;
                        if (token.Text == "false") return false;
                        if (token.Text == "null") return null;
                        if (token.Text == "new") return ParseNewExpression();
                        if (IsTypedArrayLiteralStart(token.Text)) return ParseTypedArrayLiteral(token.Text);
                        if (IsSuppressed) return ParseIdentifierPathSuppressed(token.Text);
                        return ParseIdentifierPath(token.Text);
                    case TokenKind.Symbol when token.Text == "(":
                    {
                        var value = ParseAssignmentExpression();
                        Expect(")");
                        return value;
                    }
                    default:
                        throw Error($"Unexpected token: {token.Text}");
                }
            }

            private object ParseIdentifierPath(string first)
            {
                var segments = new List<string> { first };
                int save = _pos;
                while (Match("."))
                {
                    int beforeMember = _pos - 1;
                    if (Peek().Kind != TokenKind.Identifier)
                    {
                        _pos = save;
                        break;
                    }

                    string member = Next().Text;
                    if (Peek().Text == "(")
                    {
                        _pos = beforeMember;
                        break;
                    }

                    segments.Add(member);
                    save = _pos;
                }

                var value = _evaluator.ResolveIdentifierOrStaticPath(segments);
                return ParsePostfix(value);
            }

            private object ParseNewExpression()
            {
                var typeName = ParseTypeName();
                bool isArray = false;
                int declaredLength = -1;

                if (Match("["))
                {
                    isArray = true;
                    if (!Match("]"))
                    {
                        var lengthValue = ParseExpression(0);
                        declaredLength = Convert.ToInt32(lengthValue, CultureInfo.InvariantCulture);
                        Expect("]");
                    }
                }

                if (!isArray)
                {
                    return ParseAllowedValueTypeConstructor(typeName);
                }

                Expect("{");
                var values = new List<object>();
                if (!Match("}"))
                {
                    do
                    {
                        values.Add(ParseAssignmentExpression());
                    } while (Match(","));
                    Expect("}");
                }

                if (declaredLength >= 0 && declaredLength != values.Count)
                {
                    throw Error($"Array length mismatch for {typeName}: declared {declaredLength}, got {values.Count}.");
                }

                return CreateTypedArray(typeName, values);
            }

            private object ParseTypedArrayLiteral(string firstTypeSegment)
            {
                var typeName = ParseTypeNameStarting(firstTypeSegment);
                Expect("[");
                Expect("]");
                Expect("{");
                var values = new List<object>();
                if (!Match("}"))
                {
                    do
                    {
                        values.Add(ParseAssignmentExpression());
                    } while (Match(","));
                    Expect("}");
                }
                return IsSuppressed ? null : CreateTypedArray(typeName, values);
            }

            private object ParseAllowedValueTypeConstructor(string typeName)
            {
                Expect("(");
                var args = ParseArgumentList();
                var type = FindType(typeName);
                if (type == null) throw Error($"Type not found: {typeName}");

                if (type == typeof(Vector2) && args.Count == 2) return new Vector2(Convert.ToSingle(args[0]), Convert.ToSingle(args[1]));
                if (type == typeof(Vector3) && args.Count == 3) return new Vector3(Convert.ToSingle(args[0]), Convert.ToSingle(args[1]), Convert.ToSingle(args[2]));
                if (type == typeof(Vector4) && args.Count == 4) return new Vector4(Convert.ToSingle(args[0]), Convert.ToSingle(args[1]), Convert.ToSingle(args[2]), Convert.ToSingle(args[3]));
                if (type == typeof(Quaternion) && args.Count == 4) return new Quaternion(Convert.ToSingle(args[0]), Convert.ToSingle(args[1]), Convert.ToSingle(args[2]), Convert.ToSingle(args[3]));

                throw new NotSupportedException($"Object construction is not supported for {typeName}. Only typed arrays and whitelisted Unity value types are allowed.");
            }

            private string ParseTypeName()
            {
                var sb = new StringBuilder(Expect(TokenKind.Identifier).Text);
                while (Match("."))
                {
                    sb.Append('.').Append(Expect(TokenKind.Identifier).Text);
                }
                return sb.ToString();
            }

            private string ParseTypeNameStarting(string first)
            {
                var sb = new StringBuilder(first);
                while (Match("."))
                {
                    sb.Append('.').Append(Expect(TokenKind.Identifier).Text);
                }
                return sb.ToString();
            }

            private Type ParseTypeReference()
            {
                var typeName = ParseTypeName();
                var type = FindType(typeName);
                if (type == null) throw Error($"Type not found: {typeName}");
                return type;
            }

            private List<object> ParseArgumentList()
            {
                var args = new List<object>();
                if (Match(")")) return args;
                do
                {
                    args.Add(ParseAssignmentExpression());
                } while (Match(","));
                Expect(")");
                return args;
            }

            private List<object> ParseArgumentListSuppressed()
            {
                using (Suppress())
                {
                    return ParseArgumentList();
                }
            }

            private object ParseExpressionSuppressed(int minPrecedence)
            {
                using (Suppress())
                {
                    return ParseExpression(minPrecedence);
                }
            }

            private object ParseAssignmentExpressionSuppressed()
            {
                using (Suppress())
                {
                    return ParseAssignmentExpression();
                }
            }

            private bool TryParseAssignable(out IAssignable assignable)
            {
                assignable = null;
                int save = _pos;
                try
                {
                    if (Peek().Kind != TokenKind.Identifier) return false;
                    if (Peek().Text == "true" || Peek().Text == "false" || Peek().Text == "null" || Peek().Text == "new") return false;
                    var first = Next().Text;
                    object value;
                    if (!TryParseAssignableIdentifierRoot(first, out value, out assignable))
                    {
                        _pos = save;
                        return false;
                    }

                    if (assignable != null && IsAssignmentOperator(Peek().Text)) return true;

                    while (true)
                    {
                        if (Match("."))
                        {
                            string member = Expect(TokenKind.Identifier).Text;
                            if (Match("("))
                            {
                                var args = ParseArgumentList();
                                value = _evaluator.Call(value, member, args);
                                assignable = null;
                                continue;
                            }

                            assignable = new MemberAssignable(_evaluator, value, member);
                            if (IsAssignmentOperator(Peek().Text)) return true;
                            value = _evaluator.GetMember(value, member);
                            continue;
                        }

                        if (Match("["))
                        {
                            var index = ParseAssignmentExpression();
                            Expect("]");
                            assignable = new IndexAssignable(_evaluator, value, index);
                            if (IsAssignmentOperator(Peek().Text)) return true;
                            value = _evaluator.GetIndex(value, index);
                            continue;
                        }

                        _pos = save;
                        assignable = null;
                        return false;
                    }
                }
                catch
                {
                    _pos = save;
                    assignable = null;
                    return false;
                }
            }

            private bool TryParseAssignableIdentifierRoot(string first, out object value, out IAssignable assignable)
            {
                value = null;
                assignable = null;
                var segments = new List<string> { first };
                int save = _pos;
                while (Match("."))
                {
                    int beforeMember = _pos - 1;
                    if (Peek().Kind != TokenKind.Identifier)
                    {
                        _pos = save;
                        break;
                    }

                    string member = Next().Text;
                    if (Peek().Text == "(")
                    {
                        _pos = beforeMember;
                        break;
                    }

                    segments.Add(member);
                    save = _pos;
                }

                if (segments.Count > 1 && IsAssignmentOperator(Peek().Text))
                {
                    var target = _evaluator.ResolveIdentifierOrStaticPath(segments.Take(segments.Count - 1).ToList());
                    assignable = new MemberAssignable(_evaluator, target, segments[segments.Count - 1]);
                    return true;
                }

                value = _evaluator.ResolveIdentifierOrStaticPath(segments);
                return true;
            }

            private bool TryParseAssignableSuppressed()
            {
                int save = _pos;
                if (Peek().Kind != TokenKind.Identifier) return false;
                Next();
                while (true)
                {
                    if (Match(".") || Match("?."))
                    {
                        if (Peek().Kind != TokenKind.Identifier)
                        {
                            _pos = save;
                            return false;
                        }
                        Next();
                        if (Match("(")) ParseArgumentListSuppressed();
                        continue;
                    }

                    if (Match("["))
                    {
                        ParseAssignmentExpressionSuppressed();
                        Expect("]");
                        continue;
                    }

                    if (IsAssignmentOperator(Peek().Text)) return true;
                    _pos = save;
                    return false;
                }
            }

            private object ParseIdentifierPathSuppressed(string first)
            {
                while (Match("."))
                {
                    if (Peek().Kind != TokenKind.Identifier) break;
                    Next();
                    if (Match("(")) ParseArgumentListSuppressed();
                }
                return null;
            }

            private bool TryParseCast(out Type type)
            {
                type = null;
                int save = _pos;
                if (!Match("(")) return false;
                if (Peek().Kind != TokenKind.Identifier)
                {
                    _pos = save;
                    return false;
                }

                var typeName = ParseTypeName();
                if (!Match(")"))
                {
                    _pos = save;
                    return false;
                }

                type = FindType(typeName);
                if (type == null)
                {
                    _pos = save;
                    return false;
                }

                return true;
            }

            private bool IsTypedArrayLiteralStart(string firstTypeSegment)
            {
                int save = _pos;
                try
                {
                    var typeName = ParseTypeNameStarting(firstTypeSegment);
                    bool ok = FindType(typeName) != null && Match("[") && Match("]") && Peek().Text == "{";
                    _pos = save;
                    return ok;
                }
                catch
                {
                    _pos = save;
                    return false;
                }
            }

            private bool IsSuppressed => _suppressDepth > 0;

            private static bool IsAssignmentOperator(string op) => op == "=" || op == "+=" || op == "-=";

            private IDisposable Suppress()
            {
                _suppressDepth++;
                return new SuppressScope(this);
            }

            private sealed class SuppressScope : IDisposable
            {
                private readonly Parser _parser;
                public SuppressScope(Parser parser) { _parser = parser; }
                public void Dispose() { _parser._suppressDepth--; }
            }

            private bool Match(string text)
            {
                if (Peek().Text != text) return false;
                _pos++;
                return true;
            }

            private Token Expect(string text)
            {
                if (Peek().Text == text) return Next();
                throw Error($"Expected '{text}', got '{Peek().Text}'.");
            }

            private Token Expect(TokenKind kind)
            {
                if (Peek().Kind == kind) return Next();
                throw Error($"Expected {kind}, got '{Peek().Text}'.");
            }

            private Token Peek() => _tokens[Math.Min(_pos, _tokens.Count - 1)];
            private Token Next() => _tokens[Math.Min(_pos++, _tokens.Count - 1)];
            private Exception Error(string message) => new InvalidOperationException($"{message} At token {_pos}, char {Peek().Position}.");
        }

        private sealed class Tokenizer
        {
            private readonly string _s;
            private int _i;

            public Tokenizer(string s)
            {
                _s = s ?? "";
            }

            public List<Token> Tokenize()
            {
                var result = new List<Token>();
                while (true)
                {
                    SkipWs();
                    if (_i >= _s.Length) break;
                    char c = _s[_i];

                    if (char.IsLetter(c) || c == '_' || c == '$')
                    {
                        result.Add(ReadIdentifier());
                        continue;
                    }

                    if (char.IsDigit(c))
                    {
                        result.Add(ReadNumber());
                        continue;
                    }

                    if (c == '"' || c == '\'')
                    {
                        result.Add(ReadString());
                        continue;
                    }

                    var two = _i + 1 < _s.Length ? _s.Substring(_i, 2) : "";
                    if (two == "=>")
                    {
                        throw new NotSupportedException("Lambda expressions are not supported by reflection.eval.");
                    }

                    if (two is "?." or "&&" or "||" or "==" or "!=" or ">=" or "<=" or "<<" or ">>" or "+=" or "-=")
                    {
                        result.Add(new Token(TokenKind.Operator, two, null, _i));
                        _i += 2;
                        continue;
                    }

                    if ("+-*/%><!~&|^=".IndexOf(c) >= 0)
                    {
                        result.Add(new Token(TokenKind.Operator, c.ToString(), null, _i));
                        _i++;
                        continue;
                    }

                    if (".,;()[]{}?:".IndexOf(c) >= 0)
                    {
                        result.Add(new Token(TokenKind.Symbol, c.ToString(), null, _i));
                        _i++;
                        continue;
                    }

                    throw new InvalidOperationException($"Unexpected character '{c}' at {_i}.");
                }

                result.Add(new Token(TokenKind.Eof, "(eof)", null, _i));
                return result;
            }

            private Token ReadIdentifier()
            {
                int start = _i++;
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '$')) _i++;
                var text = _s.Substring(start, _i - start);
                if (text == "async" || text == "await")
                {
                    throw new NotSupportedException("async/await expressions are not supported by reflection.eval.");
                }
                if (text == "ref" || text == "out" || text == "in")
                {
                    throw new NotSupportedException("ref/out/in arguments are not supported by reflection.eval.");
                }
                if (text == "if" || text == "for" || text == "foreach" || text == "while" || text == "do" || text == "switch")
                {
                    throw new NotSupportedException("Control-flow statements are not supported by reflection.eval; pass a single expression statement.");
                }
                return new Token(TokenKind.Identifier, text, null, start);
            }

            private Token ReadNumber()
            {
                int start = _i;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                bool floating = false;
                if (_i < _s.Length && _s[_i] == '.')
                {
                    floating = true;
                    _i++;
                    while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                }

                if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    floating = true;
                    _i++;
                    if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                    while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                }

                string number = _s.Substring(start, _i - start);
                string suffix = "";
                while (_i < _s.Length && char.IsLetter(_s[_i]))
                {
                    suffix += _s[_i++];
                }

                object value;
                suffix = suffix.ToLowerInvariant();
                if (floating || suffix.Contains("f") || suffix.Contains("d"))
                {
                    value = suffix.Contains("f")
                        ? float.Parse(number, CultureInfo.InvariantCulture)
                        : double.Parse(number, CultureInfo.InvariantCulture);
                }
                else if (suffix.Contains("u"))
                {
                    value = suffix.Contains("l")
                        ? ulong.Parse(number, CultureInfo.InvariantCulture)
                        : uint.Parse(number, CultureInfo.InvariantCulture);
                }
                else if (suffix.Contains("l"))
                {
                    value = long.Parse(number, CultureInfo.InvariantCulture);
                }
                else
                {
                    value = int.Parse(number, CultureInfo.InvariantCulture);
                }

                return new Token(TokenKind.Number, number + suffix, value, start);
            }

            private Token ReadString()
            {
                char quote = _s[_i++];
                var sb = new StringBuilder();
                while (_i < _s.Length)
                {
                    char c = _s[_i++];
                    if (c == quote) break;
                    if (c == '\\' && _i < _s.Length)
                    {
                        char esc = _s[_i++];
                        sb.Append(esc switch
                        {
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            '\\' => '\\',
                            '"' => '"',
                            '\'' => '\'',
                            _ => esc,
                        });
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }

                if (quote == '\'')
                {
                    string text = sb.ToString();
                    if (text.Length != 1) throw new InvalidOperationException("Char literal must contain one character.");
                    return new Token(TokenKind.Number, text, text[0], _i - text.Length - 2);
                }

                return new Token(TokenKind.String, sb.ToString(), sb.ToString(), _i - sb.Length - 2);
            }

            private void SkipWs()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }
        }

        private readonly struct Token
        {
            public readonly TokenKind Kind;
            public readonly string Text;
            public readonly object Value;
            public readonly int Position;

            public Token(TokenKind kind, string text, object value, int position = -1)
            {
                Kind = kind;
                Text = text;
                Value = value;
                Position = position;
            }
        }

        private enum TokenKind
        {
            Identifier,
            Number,
            String,
            Operator,
            Symbol,
            Eof,
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

        private static int GetPrecedence(string op)
        {
            return op switch
            {
                "||" => 1,
                "&&" => 2,
                "|" => 3,
                "^" => 4,
                "&" => 5,
                "==" or "!=" => 6,
                ">" or ">=" or "<" or "<=" => 7,
                "<<" or ">>" => 8,
                "+" or "-" => 9,
                "*" or "/" or "%" => 10,
                _ => -1,
            };
        }

        private static int FindLongestTypePrefix(List<string> segments)
        {
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                var typeName = string.Join(".", segments.Take(i + 1));
                if (FindType(typeName) != null) return i;
            }
            return -1;
        }

        private static Type FindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            if (fullName.EndsWith("[]", StringComparison.Ordinal))
            {
                var elementType = FindType(fullName.Substring(0, fullName.Length - 2));
                return elementType?.MakeArrayType();
            }

            var primitive = GetPrimitiveType(fullName);
            if (primitive != null) return primitive;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName);
                    if (t != null) return t;
                }
                catch { }
            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == fullName) return t;
                    }
                }
                catch { }
            }

            return null;
        }

        private static Type GetPrimitiveType(string name)
        {
            return name switch
            {
                "bool" => typeof(bool),
                "byte" => typeof(byte),
                "sbyte" => typeof(sbyte),
                "short" => typeof(short),
                "ushort" => typeof(ushort),
                "int" => typeof(int),
                "uint" => typeof(uint),
                "long" => typeof(long),
                "ulong" => typeof(ulong),
                "float" => typeof(float),
                "double" => typeof(double),
                "decimal" => typeof(decimal),
                "string" => typeof(string),
                "char" => typeof(char),
                "Vector2" => typeof(Vector2),
                "Vector3" => typeof(Vector3),
                "Vector4" => typeof(Vector4),
                "Quaternion" => typeof(Quaternion),
                _ => null,
            };
        }

        private static object CreateTypedArray(string typeName, List<object> values)
        {
            var elementType = FindType(typeName);
            if (elementType == null) throw new InvalidOperationException($"Array element type not found: {typeName}");

            var array = Array.CreateInstance(elementType, values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                if (!TryConvertValue(values[i], elementType, out var converted))
                {
                    throw new InvalidOperationException($"Cannot convert array element {i} to {GetFriendlyTypeName(elementType)}.");
                }
                array.SetValue(converted, i);
            }
            return array;
        }

        private static object ConvertTypedJsonValue(TypedJsonValue typed)
        {
            if (string.IsNullOrEmpty(typed.TypeName))
            {
                return ParseJsonPrimitive(typed.ValueJson);
            }

            var type = FindType(typed.TypeName);
            if (type == null) throw new InvalidOperationException($"Variable type not found: {typed.TypeName}");

            if (type.IsArray)
            {
                var rawValues = SplitJsonArray(typed.ValueJson);
                return CreateTypedArray(GetFriendlyTypeName(type.GetElementType()), rawValues.Select(ParseJsonPrimitive).ToList());
            }

            if (TryConvertValue(ParseJsonPrimitive(typed.ValueJson), type, out var converted))
            {
                return converted;
            }

            return JsonUtility.FromJson(typed.ValueJson, type);
        }

        private static object ParseJsonPrimitive(string raw)
        {
            raw = (raw ?? "null").Trim();
            if (raw == "null") return null;
            if (raw == "true") return true;
            if (raw == "false") return false;
            if (raw.StartsWith("\"", StringComparison.Ordinal) && raw.EndsWith("\"", StringComparison.Ordinal)) return UnquoteJsonString(raw);
            if (raw.StartsWith("[", StringComparison.Ordinal) && raw.EndsWith("]", StringComparison.Ordinal))
            {
                return SplitJsonArray(raw).Select(ParseJsonPrimitive).ToArray();
            }
            if (raw.Contains(".") && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
            return raw;
        }

        private static bool TryConvertValue(object value, Type targetType, out object result)
        {
            result = null;
            var nullable = Nullable.GetUnderlyingType(targetType);
            if (nullable != null)
            {
                if (value == null) return true;
                targetType = nullable;
            }

            if (value == null)
            {
                if (!targetType.IsValueType)
                {
                    result = null;
                    return true;
                }
                return false;
            }

            if (targetType.IsInstanceOfType(value))
            {
                result = value;
                return true;
            }

            try
            {
                if (targetType.IsEnum)
                {
                    result = value is string s ? Enum.Parse(targetType, s, true) : Enum.ToObject(targetType, value);
                    return true;
                }

                if (targetType == typeof(string)) { result = Convert.ToString(value, CultureInfo.InvariantCulture); return true; }
                if (targetType == typeof(bool)) { result = Convert.ToBoolean(value, CultureInfo.InvariantCulture); return true; }
                if (targetType == typeof(byte)) { result = Convert.ToByte(value, CultureInfo.InvariantCulture); return true; }
                if (targetType == typeof(sbyte)) { result = Convert.ToSByte(value, CultureInfo.InvariantCulture); return true; }
                if (targetType == typeof(short)) { result = Convert.ToInt16(value, CultureInfo.InvariantCulture); return true; }
                if (targetType == typeof(ushort)) { result = Convert.ToUInt16(value, CultureInfo.InvariantCulture); return true; }
                if (targetType == typeof(int)) { result = Convert.ToInt32(value, CultureInfo.InvariantCulture); return true; }
                if (targetType == typeof(uint)) { result = Convert.ToUInt32(value, CultureInfo.InvariantCulture); return true; }
                if (targetType == typeof(long)) { result = Convert.ToInt64(value, CultureInfo.InvariantCulture); return true; }
                if (targetType == typeof(ulong)) { result = Convert.ToUInt64(value, CultureInfo.InvariantCulture); return true; }
                if (targetType == typeof(float)) { result = Convert.ToSingle(value, CultureInfo.InvariantCulture); return true; }
                if (targetType == typeof(double)) { result = Convert.ToDouble(value, CultureInfo.InvariantCulture); return true; }
                if (targetType == typeof(decimal)) { result = Convert.ToDecimal(value, CultureInfo.InvariantCulture); return true; }
                if (targetType == typeof(char)) { result = Convert.ToChar(value, CultureInfo.InvariantCulture); return true; }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static int ScoreConversion(object value, Type targetType)
        {
            if (value == null) return targetType.IsValueType ? 0 : 3;
            if (targetType.IsInstanceOfType(value)) return 8;
            if (Nullable.GetUnderlyingType(targetType) != null) return 4;
            if (targetType.IsEnum) return 4;
            return 2;
        }

        private static object GetDefaultParameterValue(ParameterInfo parameter)
        {
            if (parameter.HasDefaultValue)
            {
                var value = parameter.DefaultValue;
                if (value == DBNull.Value) return Type.Missing;
                return value;
            }

            return parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
        }

        private static object CoerceDictionaryKey(IDictionary dict, object index)
        {
            foreach (var key in dict.Keys)
            {
                if (EqualsWithNumericCoercion(key, index)) return key;
                if (key != null && TryConvertValue(index, key.GetType(), out var converted) && Equals(key, converted)) return key;
            }
            return index;
        }

        private static Type GetListElementType(Type type)
        {
            if (type == null) return null;
            if (type.IsArray) return type.GetElementType();
            foreach (var iface in type.GetInterfaces().Concat(new[] { type }))
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IList<>))
                {
                    return iface.GetGenericArguments()[0];
                }
            }
            return null;
        }

        private static (Type KeyType, Type ValueType) GetDictionaryTypes(Type type)
        {
            if (type == null) return (null, null);
            foreach (var iface in type.GetInterfaces().Concat(new[] { type }))
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IDictionary<,>))
                {
                    var args = iface.GetGenericArguments();
                    return (args[0], args[1]);
                }
            }
            return (null, null);
        }

        private static bool EqualsWithNumericCoercion(object left, object right)
        {
            if (left == null || right == null) return left == right;
            if (IsNumeric(left) && IsNumeric(right)) return Convert.ToDecimal(left, CultureInfo.InvariantCulture) == Convert.ToDecimal(right, CultureInfo.InvariantCulture);
            return Equals(left, right);
        }

        private static int CompareValues(object left, object right)
        {
            if (IsNumeric(left) && IsNumeric(right))
            {
                return Convert.ToDecimal(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(right, CultureInfo.InvariantCulture));
            }

            if (left is IComparable comparable) return comparable.CompareTo(right);
            throw new InvalidOperationException($"Values are not comparable: {Describe(left)} and {Describe(right)}");
        }

        private static object PromoteNumeric(object value) => IsFloating(value) ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        private static object Negate(object value) => IsFloating(value) ? -Convert.ToDouble(value, CultureInfo.InvariantCulture) : -Convert.ToInt64(value, CultureInfo.InvariantCulture);
        private static bool IsFloating(object value) => value is float or double or decimal;
        private static bool IsNumeric(object value) => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

        private static string FormatSignature(MethodInfo method)
        {
            return $"{GetFriendlyTypeName(method.ReturnType)} {method.Name}({string.Join(", ", method.GetParameters().Select(p => GetFriendlyTypeName(p.ParameterType) + " " + p.Name))})";
        }

        private static string GetFriendlyTypeName(Type type)
        {
            if (type == null) return "(null)";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(byte)) return "byte";
            if (type == typeof(sbyte)) return "sbyte";
            if (type == typeof(short)) return "short";
            if (type == typeof(ushort)) return "ushort";
            if (type == typeof(int)) return "int";
            if (type == typeof(uint)) return "uint";
            if (type == typeof(long)) return "long";
            if (type == typeof(ulong)) return "ulong";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "double";
            if (type == typeof(decimal)) return "decimal";
            if (type == typeof(string)) return "string";
            if (type == typeof(char)) return "char";
            if (type.IsArray) return GetFriendlyTypeName(type.GetElementType()) + "[]";
            return type.FullName ?? type.Name;
        }

        private static string Describe(object value)
        {
            return value == null ? "null" : $"{value} ({value.GetType().Name})";
        }

        private static bool ReadBool(string json, string key, bool fallback)
        {
            var raw = ReadJsonPropertyRaw(json, key);
            if (string.IsNullOrEmpty(raw)) return fallback;
            return raw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ? true :
                   raw.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) ? false : fallback;
        }

        private static int ReadInt(string json, string key, int fallback)
        {
            var raw = ReadJsonPropertyRaw(json, key);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        }

        private static string ReadJsonStringProperty(string json, string key)
        {
            var raw = ReadJsonPropertyRaw(json, key);
            return string.IsNullOrEmpty(raw) ? "" : UnquoteJsonString(raw);
        }

        private static string ReadJsonStringOrArrayProperty(string json, string key)
        {
            var raw = ReadJsonPropertyRaw(json, key);
            if (string.IsNullOrEmpty(raw)) return "";
            raw = raw.Trim();
            if (raw.StartsWith("[", StringComparison.Ordinal) && raw.EndsWith("]", StringComparison.Ordinal))
            {
                return string.Join(",", SplitJsonArray(raw).Select(UnquoteJsonString));
            }
            return UnquoteJsonString(raw);
        }

        private static string ReadJsonPropertyRaw(string json, string key)
        {
            foreach (var item in ReadTopLevelObjectMembers(json))
            {
                if (item.Key == key) return item.Value;
            }
            return "";
        }

        private static Dictionary<string, string> ReadTopLevelObjectMembers(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            json = (json ?? "").Trim();
            if (!json.StartsWith("{", StringComparison.Ordinal) || !json.EndsWith("}", StringComparison.Ordinal)) return result;
            int i = 1;
            while (i < json.Length - 1)
            {
                i = SkipJsonWs(json, i);
                if (i >= json.Length - 1 || json[i] == '}') break;
                var keyRaw = ReadJsonString(json, i, out i);
                string key = UnquoteJsonString(keyRaw);
                i = SkipJsonWs(json, i);
                if (i >= json.Length || json[i] != ':') break;
                i++;
                i = SkipJsonWs(json, i);
                string value = ReadJsonValue(json, i, out i);
                result[key] = value;
                i = SkipJsonWs(json, i);
                if (i < json.Length && json[i] == ',') i++;
            }
            return result;
        }

        private static List<string> SplitJsonArray(string json)
        {
            json = (json ?? "").Trim();
            if (!json.StartsWith("[", StringComparison.Ordinal) || !json.EndsWith("]", StringComparison.Ordinal)) return new List<string>();
            var result = new List<string>();
            int i = 1;
            while (i < json.Length - 1)
            {
                i = SkipJsonWs(json, i);
                if (i >= json.Length - 1 || json[i] == ']') break;
                result.Add(ReadJsonValue(json, i, out i));
                i = SkipJsonWs(json, i);
                if (i < json.Length && json[i] == ',') i++;
            }
            return result;
        }

        private static string ReadJsonValue(string s, int start, out int end)
        {
            start = SkipJsonWs(s, start);
            if (start >= s.Length) { end = start; return ""; }
            char c = s[start];
            if (c == '"') return ReadJsonString(s, start, out end);
            if (c == '{') return ReadJsonBracket(s, start, out end, '{', '}');
            if (c == '[') return ReadJsonBracket(s, start, out end, '[', ']');
            int i = start;
            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']') i++;
            end = i;
            return s.Substring(start, i - start).Trim();
        }

        private static string ReadJsonString(string s, int start, out int end)
        {
            end = start + 1;
            while (end < s.Length)
            {
                if (s[end] == '\\' && end + 1 < s.Length) { end += 2; continue; }
                if (s[end] == '"') { end++; break; }
                end++;
            }
            return s.Substring(start, end - start);
        }

        private static string ReadJsonBracket(string s, int start, out int end, char open, char close)
        {
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == open) depth++;
                if (c == close && --depth == 0)
                {
                    end = i + 1;
                    return s.Substring(start, end - start);
                }
            }
            end = s.Length;
            return s.Substring(start);
        }

        private static int SkipJsonWs(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        private static string UnquoteJsonString(string value)
        {
            if (value == null) return null;
            value = value.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                value = value.Substring(1, value.Length - 2);
                return value.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
            }
            return value;
        }
    }
}
