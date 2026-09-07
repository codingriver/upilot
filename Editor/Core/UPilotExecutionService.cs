using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CodingRiver.UPilot.Execution;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable] public sealed class ExecutionSessionMessage { public ExecutionSessionPayload payload; }
    [Serializable]
    public sealed class ExecutionSessionPayload
    {
        public string action = "status";
        public string sessionId = "";
        public string title = "";
        public int ttlSec = 600;
        public int maxHandles = 256;
        public int maxDynamicTypes = 32;
        public int maxCallbacks = 64;
        public int maxAsyncOperations = 64;
    }

    [Serializable]
    public sealed class ExecutionSessionResultPayload
    {
        public bool ok = true;
        public string action = "";
        public string sessionId = "";
        public string title = "";
        public string status = "Open";
        public string domainGeneration = "";
        public long createdAt;
        public long lastAccessAt;
        public long expiresAt;
        public int handleCount;
        public int variableCount;
        public int dynamicTypeCount;
        public int callbackCount;
        public int subscriptionCount;
        public bool closed;
        public bool alreadyClosed;
        public int releasedHandles;
        public int releasedVariables;
        public int releasedCallbacks;
        public int releasedSubscriptions;
        public int dynamicTypesAwaitDomainReload;
        public string[] cleanupErrors = Array.Empty<string>();
        public int callbackInvocations;
        public int callbackRejected;
        public int callbackErrors;
        public CallbackDiagnosticPayload[] recentDiagnostics = Array.Empty<CallbackDiagnosticPayload>();
        public int activeAsyncOperations;
        public int completedAsyncOperations;
        public int cancelledAsyncOperations;
        public int asyncErrors;
        public string[] recentAsyncDiagnostics = Array.Empty<string>();
        public int releasedAsyncOperations;
        public int asyncOperationsStillRunning;
        public string typeMemoryRelease = "domainReload";
    }

    [Serializable] public sealed class CSharpEvalMessage { public CSharpEvalPayload payload; }
    [Serializable]
    public sealed class CSharpEvalPayload
    {
        public string code = "";
        public string mode = "auto";
        public string sessionId = "";
        public string variablesJson = "";
        public string[] imports = Array.Empty<string>();
        public string executionBackend = "auto";
        public string limitsJson = "";
        public string resultMode = "auto";
        public string languageProfileMode = "";
    }

    [Serializable]
    public sealed class ExecutionLimitsPayload
    {
        public int timeoutMs = 3000;
        public int maxStatements = 10000;
        public int maxLoopIterations = 10000;
        public int maxCalls = 1000;
        public int maxAllocations = 1000;
        public int maxRecursion = 64;
        public int maxResultBytes = 1048576;
        public int maxAwaits = 1000;
        public int maxArrayElements = 100000;
    }

    [Serializable]
    public sealed class ExecutionBudgetResultPayload
    {
        public int statements;
        public int loopIterations;
        public int calls;
        public int allocations;
        public long elapsedMs;
        public int awaits;
        public int arrayElements;
        public int finallyCleanupStatements;
    }

    [Serializable]
    public sealed class CSharpEvalResultPayload
    {
        public string status = "Succeeded";
        public string languageProfile = CSharpSubsetEngine.LanguageProfile;
        public string modeUsed = "";
        public string backendUsed = "interpreter";
        public TypedValueResult resultValue;
        public string result = "";
        public string resultType = "";
        public string resultHandle = "";
        public string sessionId = "";
        public bool sideEffectsMayHaveOccurred;
        public ExecutionBudgetResultPayload budget;
        public string[] diagnostics = Array.Empty<string>();
    }

    [Serializable] public sealed class ReflectionEmitMessage { public ReflectionEmitPayload payload; }
    [Serializable]
    public sealed class ReflectionEmitPayload
    {
        public string sessionId = "";
        public string specJson = "";
        public string specHash = "";
        public string cachePolicy = "specHash";
        public string nameConflictPolicy = "reject";
        public bool createInstance;
        public string constructorArgumentsJson = "";
    }

    [Serializable]
    public sealed class ReflectionEmitResultPayload
    {
        public string status = "Succeeded";
        public string requestedTypeName = "";
        public string generatedTypeName = "";
        public string assemblyName = "";
        public string specHash = "";
        public string typeHandle = "";
        public string instanceHandle = "";
        public bool cacheHit;
        public string[] implementedMembers = Array.Empty<string>();
        public string lifecycle = "session handles; type memory releases on Domain Reload";
    }

    [Serializable]
    public sealed class ExecutionCapabilityPayload
    {
        public string languageProfile = CSharpSubsetEngine.LanguageProfile;
        public bool interpreterSupported = true;
        public bool structuredSourceSpans = true;
        public bool structuredErrorDetails = true;
        public bool legacyJsonErrorDetails = true;
        public bool cooperativeCancellation = true;
        public bool genericCalls = true;
        public bool typedArrays = true;
        public bool sessionOwnedEvents = true;
        public string[] eventSubscriptionCleanup = { "close", "ttl", "playModeInvalidation" };
        public bool customPropertyAccessors = true;
        public bool callbackPolicySupported = true;
        public bool callbackSessionIsolation = true;
        public bool exceptionHandling = true;
        public bool boundedFinallyCleanup = true;
        public bool lexicalClosures = true;
        public bool blockLambdas = true;
        public bool asyncLambdas = true;
        public bool asyncVoidSupported = false;
        public bool nonBlockingAwait = true;
        public string genericInferenceLevel = "practical-v2";
        public bool implicitArrays = true;
        public bool multidimensionalArrays = true;
        public int maxArrayRank = 4;
        public string[] callbackExceptionModes = { "isolate", "propagate" };
        public bool emitSupported;
        public string emitRuntime = "";
        public string emitApiVariant = "";
        public string emitUnavailableReason = "";
        public int defaultTimeoutMs = 3000;
        public int hardTimeoutMs = 30000;
        public int defaultMaxStatements = 10000;
        public int hardMaxStatements = 100000;
        public int defaultMaxLoopIterations = 10000;
        public int hardMaxLoopIterations = 100000;
        public int defaultMaxCalls = 1000;
        public int hardMaxCalls = 100000;
        public int defaultMaxAllocations = 1000;
        public int hardMaxAllocations = 100000;
        public int defaultMaxRecursion = 64;
        public int hardMaxRecursion = 256;
        public int defaultMaxResultBytes = 1048576;
        public int hardMaxCallbackInvocations = 100000;
        public int hardMaxCallbackReentrancy = 64;
        public int hardMaxCallbackDiagnostics = 128;
        public int defaultMaxAwaits = 1000;
        public int hardMaxAwaits = 100000;
        public int defaultMaxArrayElements = 100000;
        public int hardMaxArrayElements = 1000000;
        public int defaultMaxAsyncOperations = 64;
        public int hardMaxAsyncOperations = 256;
        public string[] tools = { "unity_reflection_call", "csharp_eval", "reflection_emit_type", "execution_session" };
    }

    public sealed class UPilotExecutionService
    {
        private readonly UPilotBridge _bridge;
        private readonly ExecutionSessionRegistry _sessions = new ExecutionSessionRegistry();
        private readonly ReflectionEmitEngine _emit = new ReflectionEmitEngine();
        private readonly DynamicEmitCapability _emitCapability;
        private readonly int _mainThreadId;

        public ExecutionSessionRegistry Sessions => _sessions;
        public ExecutionCapabilityPayload Capabilities { get; }

        public UPilotExecutionService(UPilotBridge bridge)
        {
            _bridge = bridge;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _emitCapability = _emit.Probe();
            Capabilities = new ExecutionCapabilityPayload
            {
                emitSupported = _emitCapability.Supported,
                emitRuntime = _emitCapability.Runtime ?? "",
                emitApiVariant = _emitCapability.ApiVariant ?? "",
                emitUnavailableReason = _emitCapability.Error ?? "",
            };
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("execution.session", HandleSessionAsync);
            _bridge.Router.Register("csharp.eval", HandleCSharpEvalAsync);
            _bridge.Router.Register("reflection.emitType", HandleReflectionEmitAsync);
            _bridge.Router.Register("execution.capabilities", HandleCapabilitiesAsync);
        }

        public void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingEditMode && change != PlayModeStateChange.ExitingPlayMode) return;
            _sessions.Invalidate(
                value => value is UnityEngine.Object obj && obj != null && !AssetDatabase.Contains(obj),
                "Unity scene object handle expired during a PlayMode transition.");
        }

        private async Task HandleCapabilitiesAsync(string id, string json, CancellationToken token)
        {
            await _bridge.SendResultAsync(id, "execution.capabilities", Capabilities, token);
        }

        private async Task HandleSessionAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<ExecutionSessionMessage>(json);
            var payload = message?.payload ?? new ExecutionSessionPayload();
            var tcs = new TaskCompletionSource<ExecutionSessionResultPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    string action = (payload.action ?? "status").Trim().ToLowerInvariant();
                    if (action == "open")
                    {
                        var session = _sessions.Open(payload.title, payload.ttlSec <= 0 ? 600 : payload.ttlSec,
                            payload.maxHandles <= 0 ? 256 : payload.maxHandles,
                            payload.maxDynamicTypes <= 0 ? 32 : payload.maxDynamicTypes,
                            payload.maxCallbacks <= 0 ? 64 : payload.maxCallbacks,
                            payload.maxAsyncOperations <= 0 ? 64 : payload.maxAsyncOperations);
                        tcs.SetResult(ToSessionPayload("open", session));
                    }
                    else if (action == "status")
                    {
                        tcs.SetResult(ToSessionPayload("status", _sessions.Get(payload.sessionId)));
                    }
                    else if (action == "close")
                    {
                        var result = _sessions.Close(payload.sessionId);
                        tcs.SetResult(new ExecutionSessionResultPayload
                        {
                            action = "close", sessionId = result.sessionId, status = "Closed", closed = result.closed,
                            alreadyClosed = result.alreadyClosed, releasedHandles = result.releasedHandles,
                            releasedVariables = result.releasedVariables, releasedCallbacks = result.releasedCallbacks,
                            releasedSubscriptions = result.releasedSubscriptions,
                            dynamicTypesAwaitDomainReload = result.dynamicTypesAwaitDomainReload,
                            releasedAsyncOperations = result.releasedAsyncOperations,
                            asyncOperationsStillRunning = result.asyncOperationsStillRunning,
                            activeAsyncOperations = result.activeAsyncOperations,
                            completedAsyncOperations = result.completedAsyncOperations,
                            cancelledAsyncOperations = result.cancelledAsyncOperations,
                            asyncErrors = result.asyncErrors,
                            recentAsyncDiagnostics = result.recentAsyncDiagnostics,
                            cleanupErrors = result.cleanupErrors,
                            domainGeneration = _sessions.DomainGeneration,
                        });
                    }
                    else throw new ExecutionContractException("INVALID_SESSION_ACTION", "action must be open, status, or close.");
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            await SendTaskResult(id, "execution.session", tcs.Task, token);
        }

        private async Task HandleCSharpEvalAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<CSharpEvalMessage>(json);
            var payload = message?.payload ?? new CSharpEvalPayload();
            if (string.IsNullOrWhiteSpace(payload.code))
            {
                await _bridge.SendErrorAsync(id, "CSHARP_PARSE_ERROR", "code is required.", token, "csharp.eval");
                return;
            }
            var tcs = new TaskCompletionSource<CSharpEvalResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                CancellationTokenSource linkedCancellation = null;
                IAsyncExecutionLease operationLease = null;
                try
                {
                    var limits = ParseLimits(payload.limitsJson);
                    var variables = new Dictionary<string, object>(StringComparer.Ordinal);
                    ExecutionSession session = null;
                    if (!string.IsNullOrWhiteSpace(payload.sessionId))
                    {
                        session = _sessions.Get(payload.sessionId);
                        foreach (var pair in session.SnapshotVariables()) variables[pair.Key] = pair.Value;
                    }
                    foreach (var variable in DecodeVariables(payload.variablesJson, payload.sessionId)) variables[variable.Key] = variable.Value;
                    linkedCancellation = session == null
                        ? CancellationTokenSource.CreateLinkedTokenSource(token)
                        : CancellationTokenSource.CreateLinkedTokenSource(token, session.CancellationToken);
                    operationLease = session?.BeginAsyncOperation("csharp_eval");
                    var context = new CSharpEvaluationContext(
                        variables,
                        (payload.imports == null || payload.imports.Length == 0) ? new[] { "System", "UnityEngine", "UnityEditor" } : payload.imports,
                        new ExecutionBudget(limits.timeoutMs, limits.maxStatements, limits.maxLoopIterations, limits.maxCalls, limits.maxAllocations, limits.maxRecursion, linkedCancellation.Token, limits.maxAwaits, limits.maxArrayElements),
                        new RestrictedEvalExecutionPolicy(),
                        action => InvokeOnMainThread(id, action, session?.CancellationToken ?? linkedCancellation.Token),
                        linkedCancellation.Token,
                        session);

                    Task.Run(() => ExecuteEvaluationWorkerAsync(payload, context), linkedCancellation.Token).ContinueWith(workerTask =>
                    {
                        _bridge.EnqueueTracked(id, () =>
                        {
                            try
                            {
                                if (workerTask.IsCanceled) throw new OperationCanceledException(token);
                                if (workerTask.IsFaulted) throw workerTask.Exception?.InnerException ?? workerTask.Exception;
                                var completed = workerTask.Result;
                                if (session != null)
                                    foreach (var pair in completed.Result.Variables) session.SetVariable(pair.Key, pair.Value);
                                var typedResult = EncodeResult(completed.Result.Value, payload.sessionId, payload.resultMode, limits.maxResultBytes);
                                tcs.TrySetResult(new CSharpEvalResultPayload
                                {
                                    modeUsed = completed.Result.ModeUsed,
                                    backendUsed = completed.BackendUsed,
                                    resultValue = typedResult,
                                    result = LegacyString(completed.Result.Value),
                                    resultType = completed.Result.Value?.GetType().FullName ?? "(null)",
                                    resultHandle = typedResult.handle,
                                    sessionId = payload.sessionId ?? "",
                                    sideEffectsMayHaveOccurred = completed.Result.SideEffectsMayHaveOccurred,
                                    budget = ToBudget(completed.Result.Budget),
                                });
                            }
                            catch (Exception ex)
                            {
                                operationLease?.Fail(ex);
                                if (session != null)
                                    foreach (var pair in context.SnapshotVariables()) session.SetVariable(pair.Key, pair.Value);
                                tcs.TrySetException(WrapEvaluationException(ex, context, payload.sessionId));
                            }
                            finally
                            {
                                operationLease?.Dispose();
                                linkedCancellation.Dispose();
                            }
                        });
                    }, TaskScheduler.Default);
                }
                catch (Exception ex)
                {
                    operationLease?.Fail(ex);
                    operationLease?.Dispose();
                    linkedCancellation?.Dispose();
                    tcs.TrySetException(WrapEvaluationException(ex, null, payload.sessionId));
                }
            });
            await SendTaskResult(id, "csharp.eval", tcs.Task, token);
        }

        private sealed class EvaluationWorkerResult
        {
            public CSharpEvaluationResult Result;
            public string BackendUsed;
        }

        private static EvaluationWorkerResult ExecuteEvaluationWorker(CSharpEvalPayload payload, CSharpEvaluationContext context)
        {
            string backend = string.IsNullOrWhiteSpace(payload.executionBackend) ? "auto" : payload.executionBackend.Trim().ToLowerInvariant();
            if (backend != "auto" && backend != "interpret" && backend != "emit")
                throw new ExecutionContractException("INVALID_EXECUTION_BACKEND", "executionBackend must be auto, interpret, or emit.");
            if (string.Equals(payload.languageProfileMode, "reflection-expression", StringComparison.Ordinal))
            {
                var expressionProgram = CSharpSubsetEngine.Parse(payload.code, "expression");
                expressionProgram.ValidateReflectionExpressionProfile();
                return new EvaluationWorkerResult { Result = expressionProgram.Execute(context), BackendUsed = "interpreter" };
            }
            string cacheKey = CSharpEmitBackend.CacheKey(payload.code, payload.mode, context.Imports);
            if (backend == "emit")
                return new EvaluationWorkerResult { Result = CSharpEmitBackend.Compile(cacheKey, payload.code, payload.mode)(context), BackendUsed = "emit" };
            if (backend == "auto" && CSharpEmitBackend.IsCached(cacheKey))
                return new EvaluationWorkerResult { Result = CSharpEmitBackend.Compile(cacheKey, payload.code, payload.mode)(context), BackendUsed = "emit-cache" };
            var result = CSharpSubsetEngine.Evaluate(payload.code, payload.mode, context);
            if (backend == "auto")
            {
                try { CSharpEmitBackend.Compile(cacheKey, payload.code, payload.mode); }
                catch (ExecutionContractException) { /* first successful run remains authoritative */ }
            }
            return new EvaluationWorkerResult { Result = result, BackendUsed = "interpreter" };
        }

        private static async Task<EvaluationWorkerResult> ExecuteEvaluationWorkerAsync(
            CSharpEvalPayload payload,
            CSharpEvaluationContext context)
        {
            string backend = string.IsNullOrWhiteSpace(payload.executionBackend)
                ? "auto"
                : payload.executionBackend.Trim().ToLowerInvariant();
            if (backend != "auto" && backend != "interpret" && backend != "emit")
                throw new ExecutionContractException("INVALID_EXECUTION_BACKEND", "executionBackend must be auto, interpret, or emit.");

            if (string.Equals(payload.languageProfileMode, "reflection-expression", StringComparison.Ordinal))
            {
                var expressionProgram = CSharpSubsetEngine.Parse(payload.code, "expression");
                expressionProgram.ValidateReflectionExpressionProfile();
                return new EvaluationWorkerResult
                {
                    Result = expressionProgram.Execute(context),
                    BackendUsed = "interpreter",
                };
            }

            string cacheKey = CSharpEmitBackend.CacheKey(payload.code, payload.mode, context.Imports);
            if (backend == "emit")
            {
                var emitted = CSharpEmitBackend.CompileAsync(cacheKey, payload.code, payload.mode);
                return new EvaluationWorkerResult
                {
                    Result = await emitted(context).ConfigureAwait(false),
                    BackendUsed = "emit",
                };
            }

            if (backend == "auto" && CSharpEmitBackend.IsCached(cacheKey))
            {
                var cached = CSharpEmitBackend.CompileAsync(cacheKey, payload.code, payload.mode);
                return new EvaluationWorkerResult
                {
                    Result = await cached(context).ConfigureAwait(false),
                    BackendUsed = "emit-cache",
                };
            }

            var result = await CSharpSubsetEngine.EvaluateAsync(payload.code, payload.mode, context).ConfigureAwait(false);
            if (backend == "auto")
            {
                try { CSharpEmitBackend.CompileAsync(cacheKey, payload.code, payload.mode); }
                catch (ExecutionContractException) { /* the completed interpreter result remains authoritative */ }
            }
            return new EvaluationWorkerResult { Result = result, BackendUsed = "interpreter" };
        }

        private object InvokeOnMainThread(string commandId, Func<object> action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId) return action();
            object result = null;
            Exception error = null;
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(commandId, () =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    result = action();
                }
                catch (Exception ex) { error = ex; }
                finally { completed.TrySetResult(true); }
            });
            using (var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                waitCancellation.CancelAfter(30000);
                try { completed.Task.Wait(waitCancellation.Token); }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested) throw;
                    throw new ExecutionContractException("CSHARP_MAIN_THREAD_TIMEOUT", "Unity main-thread invocation did not complete within 30000ms.");
                }
            }
            if (error != null) throw error;
            return result;
        }

        private static ExecutionContractException WrapEvaluationException(Exception ex, CSharpEvaluationContext context, string sessionId)
        {
            if (ex is AggregateException aggregate && aggregate.InnerExceptions.Count == 1) ex = aggregate.InnerExceptions[0];
            if (ex is OperationCanceledException)
                ex = new ExecutionContractException("EXECUTION_CANCELLED", "Execution was cancelled.",
                    new Dictionary<string, object> { { "stage", "cancelled" } });
            var contract = ex as ExecutionContractException;
            var detail = new Dictionary<string, object>(contract?.Detail ?? new Dictionary<string, object>());
            detail["sideEffectsMayHaveOccurred"] = context != null && context.SideEffectsMayHaveOccurred;
            detail["sessionId"] = sessionId ?? "";
            return contract != null
                ? new ExecutionContractException(contract.Code, contract.Message, detail)
                : new ExecutionContractException("CSHARP_RUNTIME_ERROR", ex.GetType().FullName + ": " + ex.Message, detail);
        }

        private async Task HandleReflectionEmitAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<ReflectionEmitMessage>(json);
            var payload = message?.payload ?? new ReflectionEmitPayload();
            if (!_emitCapability.Supported)
            {
                await _bridge.SendErrorAsync(id, "REFLECTION_EMIT_UNAVAILABLE", _emitCapability.Error, token, "reflection.emitType");
                return;
            }
            var tcs = new TaskCompletionSource<ReflectionEmitResultPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var session = _sessions.Get(payload.sessionId);
                    var spec = JsonUtility.FromJson<DynamicTypeSpec>(payload.specJson);
                    var emitted = _emit.Emit(
                        spec,
                        payload.specHash,
                        payload.nameConflictPolicy,
                        payload.sessionId,
                        handle => session.Resolve(handle),
                        session.RegisterCleanup);
                    string typeHandle = session.Store("type", emitted.Type);
                    string instanceHandle = "";
                    if (payload.createInstance)
                    {
                        var arguments = DecodeArguments(payload.constructorArgumentsJson, payload.sessionId).Select(value => value.Value).ToArray();
                        object instance = Activator.CreateInstance(emitted.Type, arguments);
                        DynamicMethodDispatcher.BindInstance(instance, payload.sessionId);
                        instanceHandle = session.Store("object", instance);
                    }
                    tcs.SetResult(new ReflectionEmitResultPayload
                    {
                        requestedTypeName = emitted.RequestedTypeName,
                        generatedTypeName = emitted.GeneratedTypeName,
                        assemblyName = emitted.AssemblyName,
                        specHash = emitted.SpecHash,
                        typeHandle = typeHandle,
                        instanceHandle = instanceHandle,
                        cacheHit = emitted.CacheHit,
                        implementedMembers = emitted.ImplementedMembers,
                    });
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            await SendTaskResult(id, "reflection.emitType", tcs.Task, token);
        }

        internal List<ExecutionValue> DecodeArguments(string json, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<ExecutionValue>();
            var envelope = JsonUtility.FromJson<ExecutionArgumentsEnvelope>(json) ?? new ExecutionArgumentsEnvelope();
            return (envelope.items ?? Array.Empty<ExecutionArgumentSpec>()).Select(item => new ExecutionValue
            {
                Name = item.name ?? "",
                Direction = string.IsNullOrWhiteSpace(item.direction) ? "in" : item.direction,
                DeclaredType = string.IsNullOrWhiteSpace(item.value?.typeName) ? null : ExecutionTypeResolver.Resolve(item.value.typeName),
                Value = DecodeValue(item.value, sessionId),
            }).ToList();
        }

        internal object DecodeValue(TypedValueSpec spec, string sessionId)
        {
            if (spec == null) return null;
            string kind = string.IsNullOrWhiteSpace(spec.kind) ? "literal" : spec.kind.Trim().ToLowerInvariant();
            if (kind == "null") return null;
            if (kind == "handle") return _sessions.Resolve(sessionId, spec.handle);
            if (kind == "type")
            {
                if (!string.IsNullOrWhiteSpace(spec.handle)) return _sessions.Resolve(sessionId, spec.handle, "type");
                return ExecutionTypeResolver.Resolve(spec.typeName);
            }
            if (kind == "unityobject") return ResolveUnityObject(spec);
            if (kind == "array")
            {
                Type arrayType = ExecutionTypeResolver.Resolve(spec.typeName);
                Type elementType = arrayType != null && arrayType.IsArray ? arrayType.GetElementType() : typeof(object);
                var values = spec.items ?? Array.Empty<TypedValueSpec>();
                Array array = Array.CreateInstance(elementType, values.Length);
                for (int i = 0; i < values.Length; i++) array.SetValue(ConvertToType(DecodeValue(values[i], sessionId), elementType, values[i]?.valueJson), i);
                return array;
            }
            Type declared = string.IsNullOrWhiteSpace(spec.typeName) ? null : ExecutionTypeResolver.Resolve(spec.typeName);
            return ParseLiteral(spec.valueJson, declared);
        }

        internal TypedValueResult EncodeResult(object value, string sessionId, string resultMode, int maxBytes = 1048576)
        {
            string mode = string.IsNullOrWhiteSpace(resultMode) ? "auto" : resultMode.Trim().ToLowerInvariant();
            if (value == null) return new TypedValueResult { kind = "null", typeName = "(null)", valueJson = "null", summary = "(null)", serializationStatus = "inline" };
            Type type = value.GetType();
            if (IsInlineType(type))
            {
                string json = ToInlineJson(value);
                bool truncated = json.Length > maxBytes;
                if (truncated) json = json.Substring(0, maxBytes);
                return new TypedValueResult { kind = type.IsEnum ? "enum" : "literal", typeName = type.FullName, valueJson = json, summary = BoundedSummary(value), serializationStatus = truncated ? "truncated" : "inline" };
            }
            if (mode == "legacystring")
                return new TypedValueResult { kind = "object", typeName = type.FullName, valueJson = "null", summary = BoundedSummary(value), serializationStatus = "unsupported" };
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                if (value is LambdaValue)
                    throw new ExecutionContractException("SESSION_REQUIRED", "A closure or async delegate that escapes the current call requires a persistent execution session.");
                return new TypedValueResult { kind = "object", typeName = type.FullName, valueJson = "null", summary = BoundedSummary(value), serializationStatus = "requiresSession" };
            }
            string kind = value is Type ? "type" : value is Delegate ? "delegate" : value is LambdaValue ? "callback" : value is UnityEngine.Object ? "unityObject" : "object";
            string handle = _sessions.Store(sessionId, kind, value);
            return new TypedValueResult { kind = kind, typeName = type.FullName, valueJson = "null", handle = handle, summary = BoundedSummary(value), serializationStatus = "handle" };
        }

        internal Task SendContractErrorAsync(string id, string command, ExecutionContractException ex, CancellationToken token)
        {
            return _bridge.SendErrorAsync(id, ex.Code, ex.Message,
                ex.Code == "EXECUTION_CANCELLED" ? CancellationToken.None : token,
                command, ToErrorDetail(ex, id, command));
        }

        private IDictionary<string, object> DecodeVariables(string json, string sessionId)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(json)) return result;
            var envelope = JsonUtility.FromJson<ExecutionVariablesEnvelope>(json) ?? new ExecutionVariablesEnvelope();
            foreach (var item in envelope.items ?? Array.Empty<ExecutionVariableSpec>())
                if (!string.IsNullOrWhiteSpace(item.name)) result[item.name] = DecodeValue(item.value, sessionId);
            return result;
        }

        private static ExecutionLimitsPayload ParseLimits(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new ExecutionLimitsPayload();
            var value = JsonUtility.FromJson<ExecutionLimitsPayload>(json) ?? new ExecutionLimitsPayload();
            value.timeoutMs = Math.Max(1, Math.Min(30000, value.timeoutMs <= 0 ? 3000 : value.timeoutMs));
            value.maxStatements = Math.Max(1, Math.Min(100000, value.maxStatements <= 0 ? 10000 : value.maxStatements));
            value.maxLoopIterations = Math.Max(1, Math.Min(100000, value.maxLoopIterations <= 0 ? 10000 : value.maxLoopIterations));
            value.maxCalls = Math.Max(1, Math.Min(100000, value.maxCalls <= 0 ? 1000 : value.maxCalls));
            value.maxAllocations = Math.Max(1, Math.Min(100000, value.maxAllocations <= 0 ? 1000 : value.maxAllocations));
            value.maxRecursion = Math.Max(1, Math.Min(256, value.maxRecursion <= 0 ? 64 : value.maxRecursion));
            value.maxResultBytes = Math.Max(1024, Math.Min(1048576, value.maxResultBytes <= 0 ? 1048576 : value.maxResultBytes));
            value.maxAwaits = Math.Max(1, Math.Min(100000, value.maxAwaits <= 0 ? 1000 : value.maxAwaits));
            value.maxArrayElements = Math.Max(1, Math.Min(1000000, value.maxArrayElements <= 0 ? 100000 : value.maxArrayElements));
            return value;
        }

        private static object ResolveUnityObject(TypedValueSpec spec)
        {
            if (!string.IsNullOrWhiteSpace(spec.globalObjectId) && GlobalObjectId.TryParse(spec.globalObjectId, out var globalId))
                return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId);
            if (!string.IsNullOrWhiteSpace(spec.assetGuid))
            {
                string path = AssetDatabase.GUIDToAssetPath(spec.assetGuid);
                if (!string.IsNullOrWhiteSpace(path)) return AssetDatabase.LoadMainAssetAtPath(path);
            }
            if (!string.IsNullOrWhiteSpace(spec.hierarchyPath)) return GameObject.Find(spec.hierarchyPath.TrimStart('/'));
            if (spec.instanceId != 0) return UPilotEntityIds.ObjectFromWireId(unchecked((ulong)(uint)spec.instanceId));
            return null;
        }

        private static object ParseLiteral(string json, Type type)
        {
            string raw = string.IsNullOrWhiteSpace(json) ? "null" : json.Trim();
            if (raw == "null") return null;
            if (type == typeof(string) || (type == null && raw.StartsWith("\"", StringComparison.Ordinal))) return Unquote(raw);
            if (type == typeof(char)) return Unquote(raw).FirstOrDefault();
            if (type == typeof(bool) || (type == null && (raw == "true" || raw == "false"))) return bool.Parse(raw);
            if (type != null && type.IsEnum) return Enum.Parse(type, Unquote(raw), true);
            if (type == typeof(byte)) return byte.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(sbyte)) return sbyte.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(short)) return short.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(ushort)) return ushort.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(int) || type == null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return int.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(uint)) return uint.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(long)) return long.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(ulong)) return ulong.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(float)) return float.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(decimal)) return decimal.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(double) || type == null) return double.Parse(raw, CultureInfo.InvariantCulture);
            try { return JsonUtility.FromJson(raw, type); }
            catch (Exception ex) { throw new ExecutionContractException("TYPED_VALUE_DECODE_FAILED", "Could not decode " + (type?.FullName ?? "value") + ": " + ex.Message); }
        }

        private static object ConvertToType(object value, Type type, string sourceJson)
        {
            if (value == null) return type.IsValueType ? Activator.CreateInstance(type) : null;
            if (type.IsInstanceOfType(value)) return value;
            try { return Convert.ChangeType(value, type, CultureInfo.InvariantCulture); }
            catch { return ParseLiteral(sourceJson, type); }
        }

        private static bool IsInlineType(Type type)
        {
            return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid);
        }

        private static string ToInlineJson(object value)
        {
            if (value == null) return "null";
            if (value is bool boolean) return boolean ? "true" : "false";
            if (value is string || value is char || value is Guid || value.GetType().IsEnum) return "\"" + Escape(Convert.ToString(value, CultureInfo.InvariantCulture)) + "\"";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string LegacyString(object value) { return value?.ToString() ?? "(null)"; }
        private static string BoundedSummary(object value)
        {
            string summary = LegacyString(value);
            return summary.Length <= 1024 ? summary : summary.Substring(0, 1024) + "…";
        }
        private static string Escape(string value) { return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t"); }
        private static string Unquote(string value)
        {
            value = value?.Trim() ?? "";
            if (value.Length >= 2 && ((value[0] == '\"' && value[value.Length - 1] == '\"') || (value[0] == '\'' && value[value.Length - 1] == '\'')))
                value = value.Substring(1, value.Length - 2);
            return value.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t").Replace("\\\\", "\\");
        }

        private static ExecutionSessionResultPayload ToSessionPayload(string action, ExecutionSession session)
        {
            var callbackStats = DynamicMethodDispatcher.GetStatistics(session.Id);
            return new ExecutionSessionResultPayload
            {
                action = action,
                sessionId = session.Id,
                title = session.Title,
                domainGeneration = session.DomainGeneration,
                createdAt = new DateTimeOffset(session.CreatedAtUtc).ToUnixTimeMilliseconds(),
                lastAccessAt = new DateTimeOffset(session.LastAccessAtUtc).ToUnixTimeMilliseconds(),
                expiresAt = session.ExpiresAtUnixMs,
                handleCount = session.HandleCount,
                variableCount = session.VariableCount,
                dynamicTypeCount = session.DynamicTypeCount,
                callbackCount = session.CallbackCount,
                subscriptionCount = session.SubscriptionCount,
                callbackInvocations = callbackStats.Invocations,
                callbackRejected = callbackStats.Rejected,
                callbackErrors = callbackStats.Errors,
                recentDiagnostics = callbackStats.RecentDiagnostics,
                activeAsyncOperations = session.ActiveAsyncOperations,
                completedAsyncOperations = session.CompletedAsyncOperations,
                cancelledAsyncOperations = session.CancelledAsyncOperations,
                asyncErrors = session.AsyncErrors,
                recentAsyncDiagnostics = session.RecentAsyncDiagnostics,
                releasedAsyncOperations = session.ReleasedAsyncOperations,
                asyncOperationsStillRunning = session.AsyncOperationsStillRunning,
            };
        }

        private static ExecutionBudgetResultPayload ToBudget(ExecutionBudget budget)
        {
            return new ExecutionBudgetResultPayload
            {
                statements = budget.Statements,
                loopIterations = budget.LoopIterations,
                calls = budget.Calls,
                allocations = budget.Allocations,
                elapsedMs = budget.ElapsedMs,
                awaits = budget.Awaits,
                arrayElements = budget.ArrayElements,
                finallyCleanupStatements = budget.FinallyCleanupStatements,
            };
        }

        private async Task SendTaskResult<T>(string id, string command, Task<T> task, CancellationToken token)
        {
            try { await _bridge.SendResultAsync(id, command, await task, token); }
            catch (ExecutionContractException ex)
            {
                await _bridge.SendErrorAsync(id, ex.Code, ex.Message,
                    ex.Code == "EXECUTION_CANCELLED" ? CancellationToken.None : token,
                    command, ToErrorDetail(ex, id, command));
            }
            catch (OperationCanceledException)
            {
                var cancelled = new ExecutionContractException("EXECUTION_CANCELLED", "Execution was cancelled.",
                    new Dictionary<string, object> { { "stage", "cancelled" }, { "sideEffectsMayHaveOccurred", true } });
                await _bridge.SendErrorAsync(id, cancelled.Code, cancelled.Message, CancellationToken.None, command, ToErrorDetail(cancelled, id, command));
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                await _bridge.SendErrorAsync(id, "EXECUTION_RUNTIME_ERROR", inner.GetType().FullName + ": " + inner.Message, token, command);
            }
            catch (Exception ex) { await _bridge.SendErrorAsync(id, "EXECUTION_RUNTIME_ERROR", ex.GetType().FullName + ": " + ex.Message, token, command); }
        }

        private static ErrorDetailPayload ToErrorDetail(ExecutionContractException ex, string id, string command)
        {
            var detail = ex.Detail ?? new Dictionary<string, object>();
            string stage = detail.TryGetValue("stage", out var stageValue) ? Convert.ToString(stageValue, CultureInfo.InvariantCulture) :
                ex.Code == "EXECUTION_CANCELLED" ? "cancelled" :
                ex.Code.StartsWith("CSHARP_PARSE", StringComparison.Ordinal) || ex.Code == "CSHARP_UNSUPPORTED_SYNTAX" ? "parse" :
                ex.Code.StartsWith("CSHARP_BIND", StringComparison.Ordinal) || ex.Code.StartsWith("REFLECTION_BIND", StringComparison.Ordinal) ? "bind" :
                ex.Code.StartsWith("EXECUTION_POLICY", StringComparison.Ordinal) ? "policy" :
                ex.Code.StartsWith("EXECUTION_BUDGET", StringComparison.Ordinal) ? "budget" : "runtime";
            string nextAction = stage == "parse" ? "Fix the reported source location and call once with corrected code." :
                stage == "bind" ? "Use explicit type names or parameterTypeNames to select one compatible signature." :
                stage == "policy" ? "Use a dedicated semantic tool or an explicitly compiled project helper." :
                stage == "budget" ? "Inspect partial session state; increase a bounded limit only if the target is known safe." :
                stage == "cancelled" ? "Inspect partial session state, close the session when finished, and do not retry automatically." :
                "Inspect the exception and partial state before deciding whether a new call is safe.";
            bool sideEffects = detail.TryGetValue("sideEffectsMayHaveOccurred", out var sideEffectValue) && sideEffectValue is bool value && value;
            string sessionId = detail.TryGetValue("sessionId", out var sessionValue) ? Convert.ToString(sessionValue, CultureInfo.InvariantCulture) : "";
            SourceSpanPayload span = ToSourceSpan(detail);
            string[] candidateItems = detail.TryGetValue("candidates", out var candidateValue) && candidateValue is IEnumerable enumerable
                ? enumerable.Cast<object>().Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)).Take(32).ToArray()
                : Array.Empty<string>();
            string[] cleanupDiagnostics = detail.TryGetValue("cleanupDiagnostics", out var cleanupValue) && cleanupValue is IEnumerable cleanupEnumerable
                ? cleanupEnumerable.Cast<object>().Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)).Take(32).ToArray()
                : Array.Empty<string>();
            string candidates = ToJsonStringArray(candidateItems);
            string sourceSpan = span == null ? "" : JsonUtility.ToJson(span);
            return new ErrorDetailPayload
            {
                commandId = id,
                commandName = command,
                stage = stage,
                nextAction = nextAction,
                sideEffectsMayHaveOccurred = sideEffects,
                sessionId = sessionId ?? "",
                sourceSpanJson = sourceSpan,
                diagnosticsJson = "[]",
                candidatesJson = candidates,
                sourceSpan = span,
                diagnostics = Array.Empty<ExecutionDiagnosticPayload>(),
                candidates = candidateItems,
                cleanupDiagnostics = cleanupDiagnostics,
                exceptionType = ex.GetType().FullName,
                stackTrace = BoundedStack(ex.StackTrace),
            };
        }

        private static SourceSpanPayload ToSourceSpan(IDictionary<string, object> detail)
        {
            if (detail != null && detail.TryGetValue("sourceSpan", out var spanValue) && spanValue is ExecutionSourceSpan span)
                return new SourceSpanPayload
                {
                    start = span.start, length = span.length, end = span.end,
                    line = span.line, column = span.column, endLine = span.endLine, endColumn = span.endColumn,
                };
            if (detail != null && detail.TryGetValue("position", out var positionValue) && int.TryParse(Convert.ToString(positionValue, CultureInfo.InvariantCulture), out int position))
                return new SourceSpanPayload { start = position, end = position, line = 1, column = position + 1, endLine = 1, endColumn = position + 1 };
            return null;
        }

        private static string ToJsonStringArray(IEnumerable<string> values)
        {
            return "[" + string.Join(",", (values ?? Array.Empty<string>()).Select(value => "\"" + Escape(value ?? "") + "\"")) + "]";
        }

        private static string BoundedStack(string stack)
        {
            stack = stack ?? "";
            return stack.Length <= 4096 ? stack : stack.Substring(0, 4096);
        }
    }
}
