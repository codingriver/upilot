using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
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
    public sealed class CSharpExecutionDiagnosticsPayload
    {
        public long resolveMs;
        public long bindMs;
        public long invokeMs;
        public long encodeMs;
        public int getterCallCount;
        public int methodCallCount;
        public string[] completedBoundaries = Array.Empty<string>();
        public int droppedDiagnosticCount;
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
        public CSharpExecutionDiagnosticsPayload executionDiagnostics;
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
            _bridge.Router.Register("csharp.objectDump", HandleObjectDumpAsync);
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

        private Task HandleObjectDumpAsync(string id, string json, CancellationToken token) =>
            HandleObjectDumpAsync(id, json, token, action => _bridge.EnqueueTracked(id, action),
                result => _bridge.SendResultAsync(id, "csharp.objectDump", result, token),
                error => SendContractErrorAsync(id, "csharp.objectDump", error, token));

        internal async Task HandleObjectDumpAsync(string id, string json, CancellationToken token,
            Action<Action> enqueue, Func<ObjectDumpResultPayload, Task> sendResult,
            Func<ExecutionContractException, Task> sendError)
        {
            ObjectDumpPayload payload = null;
            try
            {
                var message = JsonUtility.FromJson<ObjectDumpMessage>(json);
                payload = message?.payload ?? new ObjectDumpPayload();

                if (string.IsNullOrWhiteSpace(payload.sessionId) || string.IsNullOrWhiteSpace(payload.handle))
                    throw new ExecutionContractException("INVALID_PARAMS",
                        "Both sessionId and handle are required.");

                payload.maxDepth = Math.Max(0, Math.Min(64, payload.maxDepth));
                payload.maxFieldsPerNode = Math.Max(1, Math.Min(500, payload.maxFieldsPerNode));
                payload.maxTotalNodes = Math.Max(1, Math.Min(20000, payload.maxTotalNodes));
                payload.outputFormat = (payload.outputFormat ?? "json").Trim().ToLowerInvariant();
                if (payload.outputFormat != "json" && payload.outputFormat != "text")
                    payload.outputFormat = "json";

                var ignoreSet = new HashSet<string>(payload.ignoreTypes ?? Array.Empty<string>());
                ignoreSet.UnionWith(ObjectDumper.DefaultSkipTypes);

                var tcs = new TaskCompletionSource<ObjectDumpResultPayload>();
                enqueue(() =>
                {
                    try
                    {
                        object target = _sessions.Resolve(payload.sessionId, payload.handle);

                        int totalNodes = 0;
                        var root = ObjectDumper.Dump(
                            target,
                            payload.maxDepth,
                            payload.maxFieldsPerNode,
                            payload.maxTotalNodes,
                            payload.includeStatic,
                            ignoreSet,
                            ref totalNodes,
                            payload.expandUnityValueTypes,
                            payload.expandReflectionTypes);

                        var result = new ObjectDumpResultPayload
                        {
                            typeName = target.GetType().FullName ?? target.GetType().Name,
                            root = root,
                            totalNodes = totalNodes,
                            truncated = totalNodes >= payload.maxTotalNodes,
                            unityValueTypesExpanded = payload.expandUnityValueTypes,
                        };
                        if (result.truncated)
                            result.truncateReason = "Reached maxTotalNodes limit (" + payload.maxTotalNodes + ")";

                        result.text = ObjectDumper.FormatAsText(
                            root,
                            payload.indentation,
                            payload.includeTypeNames);
                        tcs.SetResult(result);
                    }
                    catch (Exception ex) { tcs.SetException(ex); }
                });
                await sendResult(await tcs.Task);
            }
            catch (ExecutionContractException ex)
            {
                await sendError(ex);
            }
            catch (Exception ex)
            {
                await sendError(new ExecutionContractException("INTERNAL_ERROR",
                    ex.GetType().FullName + ": " + ex.Message));
            }
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

        private Task HandleCSharpEvalAsync(string id, string json, CancellationToken token) =>
            HandleCSharpEvalAsync(id, json, token, action => _bridge.EnqueueTracked(id, action),
                result => _bridge.SendResultAsync(id, "csharp.eval", result, token),
                error => SendContractErrorAsync(id, "csharp.eval", error, token));

        internal async Task HandleCSharpEvalAsync(string id, string json, CancellationToken token,
            Action<Action> enqueue, Func<CSharpEvalResultPayload, Task> sendResult,
            Func<ExecutionContractException, Task> sendError,
            Func<Func<object>, object> invocationScheduler = null)
        {
            var boundary = new UPilotReflectionService.CallExecutionBoundary();
            CSharpEvalPayload payload = null;
            try
            {
                payload = JsonUtility.FromJson<CSharpEvalMessage>(json)?.payload ?? new CSharpEvalPayload();
                ValidateEvaluationPayload(payload);
                var result = await ExecuteEvaluationAsync(id, payload, token, enqueue, boundary, invocationScheduler);
                await sendResult(result);
            }
            catch (Exception ex)
            {
                await sendError(WrapEvaluationException(ex, null, payload?.sessionId, boundary));
            }
        }

        private Task<CSharpEvalResultPayload> ExecuteEvaluationAsync(string id, CSharpEvalPayload payload,
            CancellationToken token, Action<Action> enqueue,
            UPilotReflectionService.CallExecutionBoundary boundary,
            Func<Func<object>, object> invocationScheduler)
        {
            var tcs = new TaskCompletionSource<CSharpEvalResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            enqueue(() =>
            {
                CancellationTokenSource linkedCancellation = null;
                IAsyncExecutionLease operationLease = null;
                try
                {
                    token.ThrowIfCancellationRequested();
                    var limits = ParseLimits(payload.limitsJson, payload.languageProfileMode == "reflection-expression");
                    var variables = new Dictionary<string, object>(StringComparer.Ordinal);
                    ExecutionSession session = null;
                    if (!string.IsNullOrWhiteSpace(payload.sessionId))
                    {
                        session = _sessions.Get(payload.sessionId);
                        foreach (var pair in session.SnapshotVariables()) variables[pair.Key] = pair.Value;
                    }
                    foreach (var variable in DecodeVariables(payload.variablesJson, payload.sessionId, boundary.Enter, limits.maxArrayElements)) variables[variable.Key] = variable.Value;
                    linkedCancellation = session == null
                        ? CancellationTokenSource.CreateLinkedTokenSource(token)
                        : CancellationTokenSource.CreateLinkedTokenSource(token, session.CancellationToken);
                    operationLease = session?.BeginAsyncOperation("csharp_eval");
                    var context = new CSharpEvaluationContext(
                        variables,
                        (payload.imports == null || payload.imports.Length == 0) ? new[] { "System", "UnityEngine", "UnityEditor" } : payload.imports,
                        new ExecutionBudget(limits.timeoutMs, limits.maxStatements, limits.maxLoopIterations, limits.maxCalls, limits.maxAllocations, limits.maxRecursion, linkedCancellation.Token, limits.maxAwaits, limits.maxArrayElements),
                        new RestrictedEvalExecutionPolicy(),
                        invocationScheduler ?? (action => InvokeOnMainThread(id, action, session?.CancellationToken ?? linkedCancellation.Token)),
                        linkedCancellation.Token,
                        session);

                    Task.Run(() => ExecuteEvaluationWorkerAsync(payload, context), linkedCancellation.Token).ContinueWith(workerTask =>
                    {
                        enqueue(() =>
                        {
                            try
                            {
                                if (context.SideEffectsMayHaveOccurred) boundary.Enter();
                                if (workerTask.IsCanceled) throw new OperationCanceledException(token);
                                if (workerTask.IsFaulted) throw workerTask.Exception?.InnerException ?? workerTask.Exception;
                                var completed = workerTask.Result;
                                if (session != null)
                                    foreach (var pair in completed.Result.Variables) session.SetVariable(pair.Key, pair.Value);
                                // Encoding may execute a user-defined ToString even for a read-only expression.
                                if (completed.Result.Value != null && !IsInlineType(completed.Result.Value.GetType()))
                                    boundary.Enter();
                                var typedResult = completed.Result.Diagnostics.MeasureEncode(() =>
                                    EncodeResult(completed.Result.Value, payload.sessionId, payload.resultMode, limits.maxResultBytes));
                                tcs.TrySetResult(new CSharpEvalResultPayload
                                {
                                    modeUsed = completed.Result.ModeUsed,
                                    backendUsed = completed.BackendUsed,
                                    resultValue = typedResult,
                                    result = LegacyString(completed.Result.Value),
                                    resultType = completed.Result.Value?.GetType().FullName ?? "(null)",
                                    resultHandle = typedResult.handle,
                                    sessionId = payload.sessionId ?? "",
                                    sideEffectsMayHaveOccurred = boundary.SideEffectsMayHaveOccurred,
                                    budget = ToBudget(completed.Result.Budget),
                                    executionDiagnostics = ToExecutionDiagnostics(completed.Result.Diagnostics),
                                    diagnostics = completed.Result.Diagnostics.CompletedBoundaries,
                                });
                            }
                            catch (Exception ex)
                            {
                                operationLease?.Fail(ex);
                                if (session != null)
                                    foreach (var pair in context.SnapshotVariables()) session.SetVariable(pair.Key, pair.Value);
                                tcs.TrySetException(WrapEvaluationException(ex, context, payload.sessionId, boundary));
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
                    tcs.TrySetException(WrapEvaluationException(ex, null, payload.sessionId, boundary));
                }
            });
            return tcs.Task;
        }

        internal static void ValidateEvaluationPayload(CSharpEvalPayload payload)
        {
            if (string.IsNullOrWhiteSpace(payload.code))
                throw new ExecutionContractException("CSHARP_PARSE_ERROR", "code is required.");
            payload.mode = string.IsNullOrWhiteSpace(payload.mode) ? "auto" : payload.mode.Trim().ToLowerInvariant();
            payload.executionBackend = string.IsNullOrWhiteSpace(payload.executionBackend) ? "auto" : payload.executionBackend.Trim().ToLowerInvariant();
            payload.resultMode = string.IsNullOrWhiteSpace(payload.resultMode) ? "auto" : payload.resultMode.Trim().ToLowerInvariant();
            if (payload.mode != "auto" && payload.mode != "expression" && payload.mode != "statements")
                throw new ExecutionContractException("INVALID_EVAL_MODE", "mode must be auto, expression or statements.");
            if (payload.executionBackend != "auto" && payload.executionBackend != "interpret" && payload.executionBackend != "emit")
                throw new ExecutionContractException("INVALID_EXECUTION_BACKEND", "executionBackend must be auto, interpret or emit.");
            if (payload.resultMode != "auto" && payload.resultMode != "inline" && payload.resultMode != "handle" && payload.resultMode != "legacystring")
                throw new ExecutionContractException("INVALID_RESULT_MODE", "resultMode must be auto, inline, handle or legacyString.");
            if (payload.resultMode == "handle" && string.IsNullOrWhiteSpace(payload.sessionId))
                throw new ExecutionContractException("SESSION_REQUIRED", "resultMode=handle requires a persistent session.");
            if (!string.IsNullOrEmpty(payload.languageProfileMode) && payload.languageProfileMode != "reflection-expression")
                throw new ExecutionContractException("INVALID_PARAMS", "Unknown languageProfileMode.");
            ParseLimits(payload.limitsJson, payload.languageProfileMode == "reflection-expression");
            ValidateVariableStructure(payload.variablesJson);
            var program = CSharpSubsetEngine.Parse(payload.code,
                payload.languageProfileMode == "reflection-expression" ? "expression" : payload.mode);
            if (payload.languageProfileMode == "reflection-expression")
                program.ValidateReflectionExpressionProfile();
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

        private static ExecutionContractException WrapEvaluationException(Exception ex, CSharpEvaluationContext context,
            string sessionId, UPilotReflectionService.CallExecutionBoundary boundary = null)
        {
            ex = UnwrapInvocationException(ex);
            if (ex is OperationCanceledException)
                ex = new ExecutionContractException("EXECUTION_CANCELLED", "Execution was cancelled.",
                    new Dictionary<string, object> { { "stage", "cancelled" } });
            var contract = ex as ExecutionContractException;
            var detail = new Dictionary<string, object>(contract?.Detail ?? new Dictionary<string, object>());
            PreserveExceptionEvidence(detail, ex);
            if (context != null && context.SideEffectsMayHaveOccurred) boundary?.Enter();
            bool sideEffects = (boundary?.SideEffectsMayHaveOccurred ?? false)
                || (context != null && context.SideEffectsMayHaveOccurred)
                || (detail.TryGetValue("sideEffectsMayHaveOccurred", out var prior) && prior is bool occurred && occurred);
            detail["sideEffectsMayHaveOccurred"] = sideEffects;
            detail["sessionId"] = sessionId ?? "";
            if (context != null)
                detail["executionDiagnostics"] = ToExecutionDiagnostics(context.Diagnostics);
            detail["nextAction"] = sideEffects
                ? "Inspect the original request and actual state; do not replay the target."
                : "Correct the request and call once; the target has not executed.";
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
                    // Constructor values are part of the request validation boundary.
                    // Decode them before emitting so a malformed typed value cannot
                    // leave a new dynamic type behind after the request is rejected.
                    var constructorArguments = DecodeArguments(payload.constructorArgumentsJson, payload.sessionId)
                        .Select(value => value.Value).ToArray();
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
                        object instance = Activator.CreateInstance(emitted.Type, constructorArguments);
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

        internal static void ValidateArgumentStructure(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            var envelope = ParseArguments(json);
            foreach (var item in envelope.items)
            {
                if (item == null || (!string.IsNullOrEmpty(item.direction) &&
                    item.direction != "in" && item.direction != "ref" && item.direction != "out"))
                    throw new ExecutionContractException("INVALID_PARAMS", "Argument direction must be in, ref or out.");
                ValidateValueStructure(item.value, 0);
            }
        }

        private static void ValidateValueStructure(TypedValueSpec value, int depth)
        {
            if (value == null) return;
            if (depth > 64)
                throw new ExecutionContractException("INVALID_PARAMS", "Typed argument nesting exceeds 64.");
            var kind = string.IsNullOrWhiteSpace(value.kind) ? "literal" : value.kind.Trim().ToLowerInvariant();
            if (kind != "literal" && kind != "null" && kind != "handle" && kind != "type" && kind != "array" && kind != "unityobject")
                throw new ExecutionContractException("INVALID_PARAMS", "Unsupported typed argument kind: " + kind);
            if (kind == "handle" && string.IsNullOrWhiteSpace(value.handle))
                throw new ExecutionContractException("INVALID_PARAMS", "A handle argument requires a handle.");
            foreach (var child in value.items ?? Array.Empty<TypedValueSpec>())
                ValidateValueStructure(child, depth + 1);
        }

        internal void ValidateArgumentBindings(string json, string sessionId, int maxArrayElements = 100000)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            ValidateArgumentBindings(ParseArguments(json), sessionId, maxArrayElements);
        }

        internal void ValidateArgumentBindings(ExecutionArgumentsEnvelope envelope, string sessionId, int maxArrayElements = 100000)
        {
            var arrayBudget = new ExecutionBudget(maxArrayElements: maxArrayElements);
            foreach (var argument in envelope?.items ?? Array.Empty<ExecutionArgumentSpec>())
                ValidateValueBinding(argument.value, sessionId, arrayBudget);
        }

        private void ValidateValueBinding(TypedValueSpec value, string sessionId, ExecutionBudget arrayBudget = null)
        {
            if (value == null) return;
            string kind = string.IsNullOrWhiteSpace(value.kind) ? "literal" : value.kind.Trim().ToLowerInvariant();
            Type declared = string.IsNullOrWhiteSpace(value.typeName) ? null : ExecutionTypeResolver.Resolve(value.typeName);
            if (!string.IsNullOrWhiteSpace(value.typeName) && declared == null)
                throw new ExecutionContractException("TYPE_NOT_FOUND", "Argument type not found: " + value.typeName);
            if (kind == "handle") _sessions.Resolve(sessionId, value.handle);
            if (kind == "type")
            {
                if (!string.IsNullOrWhiteSpace(value.handle)) _sessions.Resolve(sessionId, value.handle, "type");
                else if (declared == null)
                    throw new ExecutionContractException("TYPE_NOT_FOUND", "A type argument requires a valid typeName or type handle.");
            }
            if (kind == "array" && (declared == null || !declared.IsArray || declared.GetArrayRank() != 1))
                throw new ExecutionContractException("INVALID_PARAMS", "A typed array argument requires a supported one-dimensional array type.");
            if (kind == "array")
            {
                Type elementType = declared.GetElementType();
                if (!IsSupportedInlineArrayElement(elementType))
                    throw new ExecutionContractException("INVALID_PARAMS", "The typed array element type is not supported: " + elementType.FullName);
                arrayBudget?.CountArrayElements((value.items ?? Array.Empty<TypedValueSpec>()).Length);
                foreach (var item in value.items ?? Array.Empty<TypedValueSpec>())
                {
                    string itemKind = string.IsNullOrWhiteSpace(item?.kind) ? "literal" : item.kind.Trim().ToLowerInvariant();
                    if (itemKind != "literal" && itemKind != "null")
                        throw new ExecutionContractException("INVALID_PARAMS", "Typed primitive array elements must be literal or null values.");
                    bool nullElement = itemKind == "null" || (itemKind == "literal"
                        && string.Equals(string.IsNullOrWhiteSpace(item?.valueJson) ? "null" : item.valueJson.Trim(), "null", StringComparison.Ordinal));
                    if (nullElement && elementType != typeof(string))
                        throw new ExecutionContractException("INVALID_PARAMS", "Only System.String[] accepts null array elements.");
                }
            }
            foreach (var item in value.items ?? Array.Empty<TypedValueSpec>())
                ValidateValueBinding(item, sessionId, arrayBudget);
        }

        internal List<ExecutionValue> DecodeArguments(string json, string sessionId, Action beforeUserCode = null, int maxArrayElements = 100000)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<ExecutionValue>();
            return DecodeArguments(ParseArguments(json), sessionId, beforeUserCode, maxArrayElements);
        }

        internal List<ExecutionValue> DecodeArguments(ExecutionArgumentsEnvelope envelope, string sessionId,
            Action beforeUserCode = null, int maxArrayElements = 100000)
        {
            envelope = envelope ?? new ExecutionArgumentsEnvelope();
            ValidateArgumentBindings(envelope, sessionId, maxArrayElements);
            return (envelope.items ?? Array.Empty<ExecutionArgumentSpec>()).Select(item => new ExecutionValue
            {
                Name = item.name ?? "",
                Direction = string.IsNullOrWhiteSpace(item.direction) ? "in" : item.direction,
                DeclaredType = string.IsNullOrWhiteSpace(item.value?.typeName) ? null : ExecutionTypeResolver.Resolve(item.value.typeName),
                Value = DecodeValue(item.value, sessionId, beforeUserCode),
            }).ToList();
        }

        internal object DecodeValue(TypedValueSpec spec, string sessionId, Action beforeUserCode = null)
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
            if (kind == "unityobject")
            {
                beforeUserCode?.Invoke();
                return ResolveUnityObject(spec);
            }
            if (kind == "array")
            {
                Type arrayType = ExecutionTypeResolver.Resolve(spec.typeName);
                Type elementType = arrayType != null && arrayType.IsArray ? arrayType.GetElementType() : typeof(object);
                var values = spec.items ?? Array.Empty<TypedValueSpec>();
                Array array = Array.CreateInstance(elementType, values.Length);
                for (int i = 0; i < values.Length; i++)
                {
                    var item = DecodeValue(values[i], sessionId, beforeUserCode);
                    if (!IsInlineType(elementType) && (item == null || !elementType.IsInstanceOfType(item)))
                        beforeUserCode?.Invoke();
                    array.SetValue(ConvertToType(item, elementType, values[i]?.valueJson), i);
                }
                return array;
            }
            Type declared = string.IsNullOrWhiteSpace(spec.typeName) ? null : ExecutionTypeResolver.Resolve(spec.typeName);
            if (!string.IsNullOrWhiteSpace(spec.typeName) && declared == null)
                throw new ExecutionContractException("TYPE_NOT_FOUND", "Argument type not found: " + spec.typeName);
            // JsonUtility DTO decoding does not invoke user constructors or members. The
            // boundary is crossed later if binding, invocation, or result encoding runs user code.
            return ParseLiteral(spec.valueJson, declared);
        }

        internal TypedValueResult EncodeResult(object value, string sessionId, string resultMode, int maxBytes = 1048576)
        {
            string mode = string.IsNullOrWhiteSpace(resultMode) ? "auto" : resultMode.Trim().ToLowerInvariant();
            if (mode != "auto" && mode != "inline" && mode != "handle" && mode != "legacystring")
                throw new ExecutionContractException("INVALID_RESULT_MODE", "resultMode must be auto, inline, handle or legacyString.");
            maxBytes = Math.Max(1024, Math.Min(1048576, maxBytes));
            if (value == null)
                return new TypedValueResult
                {
                    kind = "null", typeName = "(null)", valueJson = "null", summary = "(null)",
                    serializationStatus = mode == "handle" ? "null" : "inline",
                };
            Type type = value.GetType();
            if (mode == "handle")
            {
                if (string.IsNullOrWhiteSpace(sessionId))
                    throw new ExecutionContractException("SESSION_REQUIRED", "resultMode=handle requires a persistent session.");
                return StoreResultHandle(value, sessionId, type);
            }
            if (mode == "legacystring")
            {
                if (TryEncodeInlineResult(value, maxBytes, out var legacyInline, out _, out _))
                    return legacyInline;
                return new TypedValueResult { kind = "object", typeName = type.FullName, valueJson = "null", summary = BoundedSummary(value), serializationStatus = "unsupported" };
            }
            if (TryEncodeInlineResult(value, maxBytes, out var inline, out var actualBytes, out var unsupported))
                return inline;
            if (mode == "inline")
            {
                string code = unsupported ? "RESULT_NOT_INLINEABLE" : "RESULT_TOO_LARGE";
                throw new ExecutionContractException(code, unsupported
                    ? "The result cannot be represented as a supported inline typed value."
                    : "The complete result exceeds maxResultBytes.", new Dictionary<string, object>
                    {
                        { "actualBytes", actualBytes }, { "limitBytes", maxBytes }, { "sideEffectsMayHaveOccurred", true },
                    });
            }
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                if (value is LambdaValue)
                    throw new ExecutionContractException("SESSION_REQUIRED", "A closure or async delegate that escapes the current call requires a persistent execution session.");
                return new TypedValueResult
                {
                    kind = "object", typeName = type.FullName, valueJson = "null", summary = BoundedSummary(value),
                    serializationStatus = "requiresSession", diagnosticCode = unsupported ? "RESULT_NOT_INLINEABLE" : "RESULT_TOO_LARGE",
                    actualBytes = actualBytes, limitBytes = maxBytes,
                };
            }
            return StoreResultHandle(value, sessionId, type);
        }

        private TypedValueResult StoreResultHandle(object value, string sessionId, Type type)
        {
            string kind = value is Type ? "type" : value is Delegate ? "delegate" : value is LambdaValue ? "callback" : value is UnityEngine.Object ? "unityObject" : "object";
            string handle = _sessions.Store(sessionId, kind, value);
            return new TypedValueResult { kind = kind, typeName = type.FullName, valueJson = "null", handle = handle, summary = BoundedSummary(value), serializationStatus = "handle" };
        }

        private static bool TryEncodeInlineResult(object value, int maxBytes, out TypedValueResult result, out int actualBytes, out bool unsupported)
        {
            result = null;
            actualBytes = 0;
            unsupported = false;
            Type type = value.GetType();
            string json;
            string kind;
            if (IsInlineType(type))
            {
                if (!TryInlineJson(value, out json))
                {
                    unsupported = true;
                    return false;
                }
                kind = type.IsEnum ? "enum" : "literal";
            }
            else if (type.IsArray && type.GetArrayRank() == 1 && IsSupportedInlineArrayElement(type.GetElementType()))
            {
                var array = (Array)value;
                var builder = new StringBuilder();
                builder.Append('[');
                actualBytes = 1;
                for (int index = 0; index < array.Length; index++)
                {
                    if (index > 0 && !TryAppendUtf8(builder, ",", maxBytes, ref actualBytes)) return false;
                    object item = array.GetValue(index);
                    if (item == null)
                    {
                        if (type.GetElementType() != typeof(string)) { unsupported = true; return false; }
                        if (!TryAppendUtf8(builder, "null", maxBytes, ref actualBytes)) return false;
                    }
                    else if (!TryInlineJson(item, out var itemJson))
                    {
                        unsupported = true;
                        return false;
                    }
                    else if (!TryAppendUtf8(builder, itemJson, maxBytes, ref actualBytes)) return false;
                }
                if (!TryAppendUtf8(builder, "]", maxBytes, ref actualBytes)) return false;
                json = builder.ToString();
                kind = "array";
            }
            else
            {
                unsupported = true;
                return false;
            }
            if (kind != "array") actualBytes = Encoding.UTF8.GetByteCount(json);
            if (actualBytes > maxBytes) return false;
            result = new TypedValueResult
            {
                kind = kind, typeName = type.FullName, valueJson = json, summary = BoundedSummary(value),
                serializationStatus = "inline", actualBytes = actualBytes, limitBytes = maxBytes,
            };
            return true;
        }

        private static bool TryAppendUtf8(StringBuilder builder, string value, int maxBytes, ref int actualBytes)
        {
            int bytes = Encoding.UTF8.GetByteCount(value);
            long next = (long)actualBytes + bytes;
            if (next > maxBytes)
            {
                actualBytes = next > int.MaxValue ? int.MaxValue : (int)next;
                return false;
            }
            builder.Append(value);
            actualBytes = (int)next;
            return true;
        }

        private static bool IsSupportedInlineArrayElement(Type type)
        {
            return type == typeof(bool) || type == typeof(char) || type == typeof(string)
                || type == typeof(sbyte) || type == typeof(byte) || type == typeof(short) || type == typeof(ushort)
                || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
                || type == typeof(float) || type == typeof(double) || type == typeof(decimal);
        }

        private static bool TryInlineJson(object value, out string json)
        {
            if (value is float single && (float.IsNaN(single) || float.IsInfinity(single))) { json = ""; return false; }
            if (value is double number && (double.IsNaN(number) || double.IsInfinity(number))) { json = ""; return false; }
            json = ToInlineJson(value);
            return true;
        }

        internal Task SendContractErrorAsync(string id, string command, ExecutionContractException ex, CancellationToken token)
        {
            return _bridge.SendErrorAsync(id, ex.Code, ex.Message,
                ex.Code == "EXECUTION_CANCELLED" ? CancellationToken.None : token,
                command, ToErrorDetail(ex, id, command));
        }

        private static ExecutionVariablesEnvelope ValidateVariableStructure(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new ExecutionVariablesEnvelope();
            var envelope = ParseVariables(json);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in envelope.items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.name) || !names.Add(item.name))
                    throw new ExecutionContractException("INVALID_PARAMS", "Variable names must be nonempty and unique.");
                ValidateValueStructure(item.value, 0);
            }
            return envelope;
        }

        private IDictionary<string, object> DecodeVariables(string json, string sessionId, Action beforeUserCode = null, int maxArrayElements = 100000)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            var envelope = ValidateVariableStructure(json);
            var arrayBudget = new ExecutionBudget(maxArrayElements: maxArrayElements);
            foreach (var item in envelope.items)
                ValidateValueBinding(item.value, sessionId, arrayBudget);
            foreach (var item in envelope.items)
                result[item.name] = DecodeValue(item.value, sessionId, beforeUserCode);
            return result;
        }

        // JsonUtility intentionally ignores unknown and duplicate fields. Validate the typed wire
        // shape first, while it is still possible to reject before DTO construction or conversion.
        internal static ExecutionArgumentsEnvelope ParseArguments(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new ExecutionArgumentsEnvelope();
            var root = ValidateTypedEnvelopeJson(json, false);
            var rootFields = ReadObjectFields(root, "$");
            var result = new List<ExecutionArgumentSpec>();
            int index = 0;
            foreach (var item in ReadArrayItems(rootFields["items"], "$.items"))
            {
                string path = "$.items[" + index++ + "]";
                var fields = ReadObjectFields(item, path);
                result.Add(new ExecutionArgumentSpec
                {
                    name = fields.TryGetValue("name", out var name) ? ReadString(name, path + ".name") : string.Empty,
                    direction = fields.TryGetValue("direction", out var direction)
                        ? ReadString(direction, path + ".direction") : "in",
                    value = BuildTypedValue(fields["value"], path + ".value"),
                });
            }
            return new ExecutionArgumentsEnvelope { items = result.ToArray() };
        }

        private static ExecutionVariablesEnvelope ParseVariables(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new ExecutionVariablesEnvelope();
            var root = ValidateTypedEnvelopeJson(json, true);
            var rootFields = ReadObjectFields(root, "$");
            var result = new List<ExecutionVariableSpec>();
            int index = 0;
            foreach (var item in ReadArrayItems(rootFields["items"], "$.items"))
            {
                string path = "$.items[" + index++ + "]";
                var fields = ReadObjectFields(item, path);
                result.Add(new ExecutionVariableSpec
                {
                    name = ReadString(fields["name"], path + ".name"),
                    value = BuildTypedValue(fields["value"], path + ".value"),
                });
            }
            return new ExecutionVariablesEnvelope { items = result.ToArray() };
        }

        private static TypedValueSpec BuildTypedValue(System.Xml.XmlElement node, string path)
        {
            var fields = ReadObjectFields(node, path);
            var value = new TypedValueSpec();
            if (fields.TryGetValue("kind", out var kind)) value.kind = ReadString(kind, path + ".kind");
            if (fields.TryGetValue("typeName", out var typeName)) value.typeName = ReadString(typeName, path + ".typeName");
            if (fields.TryGetValue("valueJson", out var valueJson)) value.valueJson = ReadString(valueJson, path + ".valueJson");
            if (fields.TryGetValue("handle", out var handle)) value.handle = ReadString(handle, path + ".handle");
            if (fields.TryGetValue("instanceId", out var instanceId)) value.instanceId = ReadInt32(instanceId, path + ".instanceId");
            if (fields.TryGetValue("globalObjectId", out var globalObjectId)) value.globalObjectId = ReadString(globalObjectId, path + ".globalObjectId");
            if (fields.TryGetValue("assetGuid", out var assetGuid)) value.assetGuid = ReadString(assetGuid, path + ".assetGuid");
            if (fields.TryGetValue("hierarchyPath", out var hierarchyPath)) value.hierarchyPath = ReadString(hierarchyPath, path + ".hierarchyPath");
            if (fields.TryGetValue("items", out var items))
            {
                var children = new List<TypedValueSpec>();
                int index = 0;
                foreach (var child in ReadArrayItems(items, path + ".items"))
                    children.Add(BuildTypedValue(child, path + ".items[" + index++ + "]"));
                value.items = children.ToArray();
            }
            return value;
        }

        private static System.Xml.XmlElement ValidateTypedEnvelopeJson(string json, bool variables)
        {
            int byteCount = Encoding.UTF8.GetByteCount(json);
            if (byteCount > 1048576)
                throw TypedJsonError("$", "typed JSON exceeds the 1 MiB UTF-8 limit.", byteCount, 1048576);
            var document = new System.Xml.XmlDocument();
            try
            {
                using (var reader = System.Runtime.Serialization.Json.JsonReaderWriterFactory.CreateJsonReader(
                    Encoding.UTF8.GetBytes(json), new System.Xml.XmlDictionaryReaderQuotas
                    {
                        MaxDepth = 256, MaxStringContentLength = 1048576, MaxArrayLength = 1048576,
                    }))
                    document.Load(reader);
            }
            catch (Exception ex)
            {
                throw TypedJsonError("$", "typed JSON is malformed: " + ex.Message);
            }
            var root = document.DocumentElement;
            if (root == null || root.GetAttribute("type") != "object")
                throw TypedJsonError("$", "typed JSON must be an object.");
            var rootFields = ReadObjectFields(root, "$");
            EnsureOnlyFields(rootFields, new[] { "items" }, "$");
            if (!rootFields.TryGetValue("items", out var items) || items.GetAttribute("type") != "array")
                throw TypedJsonError("$.items", "typed JSON must contain an items array.");
            int index = 0;
            foreach (var item in ReadArrayItems(items, "$.items"))
            {
                string path = "$.items[" + index++ + "]";
                var fields = ReadObjectFields(item, path);
                if (variables)
                {
                    EnsureOnlyFields(fields, new[] { "name", "value" }, path);
                    RequireString(fields, "name", path);
                    if (!fields.TryGetValue("value", out var value))
                        throw TypedJsonError(path + ".value", "variable value is required.");
                    ValidateTypedValueJson(value, path + ".value", 0);
                }
                else
                {
                    EnsureOnlyFields(fields, new[] { "name", "direction", "value" }, path);
                    if (fields.TryGetValue("name", out var name)) ReadString(name, path + ".name");
                    if (fields.TryGetValue("direction", out var direction))
                    {
                        string directionValue = ReadString(direction, path + ".direction");
                        if (directionValue != "in" && directionValue != "ref" && directionValue != "out")
                            throw TypedJsonError(path + ".direction", "argument direction must be in, ref or out.");
                    }
                    if (!fields.TryGetValue("value", out var value))
                        throw TypedJsonError(path + ".value", "argument value is required.");
                    ValidateTypedValueJson(value, path + ".value", 0);
                }
            }
            return root;
        }

        private static void ValidateTypedValueJson(System.Xml.XmlElement node, string path, int depth)
        {
            if (depth > 64)
                throw TypedJsonError(path, "typed value nesting exceeds 64.");
            if (node.GetAttribute("type") != "object")
                throw TypedJsonError(path, "typed value must be an object.");
            var fields = ReadObjectFields(node, path);
            EnsureOnlyFields(fields, new[]
            {
                "kind", "typeName", "valueJson", "handle", "instanceId",
                "globalObjectId", "assetGuid", "hierarchyPath", "items",
            }, path);
            string kind = fields.TryGetValue("kind", out var kindField) ? ReadString(kindField, path + ".kind").Trim().ToLowerInvariant() : "literal";
            if (kind != "literal" && kind != "null" && kind != "handle" && kind != "type" && kind != "array" && kind != "unityobject")
                throw TypedJsonError(path + ".kind", "unsupported typed value kind.");
            if (fields.TryGetValue("typeName", out var typeName)) ReadString(typeName, path + ".typeName");
            if (fields.TryGetValue("valueJson", out var valueJson)) ReadString(valueJson, path + ".valueJson");
            if (fields.TryGetValue("handle", out var presentHandle)) ReadString(presentHandle, path + ".handle");
            if (fields.TryGetValue("globalObjectId", out var globalObjectId)) ReadString(globalObjectId, path + ".globalObjectId");
            if (fields.TryGetValue("assetGuid", out var assetGuid)) ReadString(assetGuid, path + ".assetGuid");
            if (fields.TryGetValue("hierarchyPath", out var hierarchyPath)) ReadString(hierarchyPath, path + ".hierarchyPath");
            if (fields.TryGetValue("instanceId", out var instanceId)) ReadInt32(instanceId, path + ".instanceId");
            if (kind == "handle" && (!fields.TryGetValue("handle", out var handle) || string.IsNullOrWhiteSpace(ReadString(handle, path + ".handle"))))
                throw TypedJsonError(path + ".handle", "a handle string is required.");
            if (kind == "array")
            {
                if (!fields.TryGetValue("typeName", out var arrayType) || string.IsNullOrWhiteSpace(ReadString(arrayType, path + ".typeName")))
                    throw TypedJsonError(path + ".typeName", "an array typeName is required.");
                if (!fields.TryGetValue("items", out var items) || items.GetAttribute("type") != "array")
                    throw TypedJsonError(path + ".items", "an array items value is required.");
                int index = 0;
                foreach (var child in ReadArrayItems(items, path + ".items"))
                    ValidateTypedValueJson(child, path + ".items[" + index++ + "]", depth + 1);
            }
            else if (fields.TryGetValue("items", out var nonArrayItems))
            {
                // JsonUtility serializes a default TypedValueSpec.items as [], even when the
                // value is not an array. Accept only that DTO round-trip shape; any supplied
                // content or a non-array wire type remains an input-shape error before DTO
                // construction and conversion.
                if (nonArrayItems.GetAttribute("type") != "array")
                    throw TypedJsonError(path + ".items", "items is valid only for kind=array.");
                foreach (var ignored in ReadArrayItems(nonArrayItems, path + ".items"))
                    throw TypedJsonError(path + ".items", "items is valid only for kind=array.");
            }
        }

        private static Dictionary<string, System.Xml.XmlElement> ReadObjectFields(System.Xml.XmlElement node, string path)
        {
            if (node.GetAttribute("type") != "object")
                throw TypedJsonError(path, "value must be an object.");
            var fields = new Dictionary<string, System.Xml.XmlElement>(StringComparer.Ordinal);
            foreach (System.Xml.XmlNode child in node.ChildNodes)
            {
                if (!(child is System.Xml.XmlElement element) || fields.ContainsKey(element.LocalName))
                    throw TypedJsonError(path, "object contains duplicate or invalid fields.");
                fields.Add(element.LocalName, element);
            }
            return fields;
        }

        private static IEnumerable<System.Xml.XmlElement> ReadArrayItems(System.Xml.XmlElement node, string path)
        {
            if (node.GetAttribute("type") != "array")
                throw TypedJsonError(path, "value must be an array.");
            foreach (System.Xml.XmlNode child in node.ChildNodes)
            {
                if (!(child is System.Xml.XmlElement element) || element.LocalName != "item")
                    throw TypedJsonError(path, "array contains an invalid item.");
                yield return element;
            }
        }

        private static void EnsureOnlyFields(
            Dictionary<string, System.Xml.XmlElement> fields,
            IEnumerable<string> allowed,
            string path)
        {
            var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
            string unknown = fields.Keys.FirstOrDefault(name => !allowedSet.Contains(name));
            if (unknown != null)
                throw TypedJsonError(path + "." + unknown, "unknown typed JSON field.");
        }

        private static string RequireString(Dictionary<string, System.Xml.XmlElement> fields, string name, string path)
        {
            if (!fields.TryGetValue(name, out var field))
                throw TypedJsonError(path + "." + name, "a string value is required.");
            return ReadString(field, path + "." + name);
        }

        private static string ReadString(System.Xml.XmlElement field, string path)
        {
            if (field.GetAttribute("type") != "string")
                throw TypedJsonError(path, "value must be a string.");
            return field.InnerText;
        }

        private static int ReadInt32(System.Xml.XmlElement field, string path)
        {
            if (field.GetAttribute("type") != "number"
                || !int.TryParse(field.InnerText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                throw TypedJsonError(path, "value must be a 32-bit integer.");
            return value;
        }

        private static ExecutionContractException TypedJsonError(string path, string message, int actualBytes = 0, int limitBytes = 0)
        {
            var detail = new Dictionary<string, object> { { "path", path }, { "sideEffectsMayHaveOccurred", false } };
            if (actualBytes > 0) detail["actualBytes"] = actualBytes;
            if (limitBytes > 0) detail["limitBytes"] = limitBytes;
            return new ExecutionContractException("INVALID_PARAMS", message, detail);
        }

        private static ExecutionLimitsPayload ParseLimits(string json, bool allowLegacyResultMode = false)
        {
            if (string.IsNullOrWhiteSpace(json)) return new ExecutionLimitsPayload();
            // Validate the wire types before JsonUtility drops unknown fields or coerces values.
            if (json.Length > 65536)
                throw new ExecutionContractException("INVALID_PARAMS", "limitsJson exceeds the bounded options size.");
            var document = new System.Xml.XmlDocument();
            using (var reader = System.Runtime.Serialization.Json.JsonReaderWriterFactory.CreateJsonReader(
                System.Text.Encoding.UTF8.GetBytes(json),
                new System.Xml.XmlDictionaryReaderQuotas { MaxDepth = 64, MaxStringContentLength = 65536 }))
                document.Load(reader);
            var root = document.DocumentElement;
            if (root == null || root.GetAttribute("type") != "object")
                throw new ExecutionContractException("INVALID_PARAMS", "limitsJson must be an object.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Xml.XmlNode child in root.ChildNodes)
            {
                if (!(child is System.Xml.XmlElement field) || !names.Add(field.LocalName))
                    throw new ExecutionContractException("INVALID_PARAMS", "limitsJson contains duplicate or invalid fields.");
                if (allowLegacyResultMode && field.LocalName == "resultMode")
                {
                    string mode = field.InnerText.Trim().ToLowerInvariant();
                    if (field.GetAttribute("type") == "string" && (mode == "string" || mode == "json" || mode == "type"))
                        continue;
                    throw new ExecutionContractException("INVALID_PARAMS", "options.resultMode must be string, json or type.");
                }
                var member = typeof(ExecutionLimitsPayload).GetField(field.LocalName, BindingFlags.Public | BindingFlags.Instance);
                if (member == null || member.FieldType != typeof(int) || field.GetAttribute("type") != "number"
                    || !int.TryParse(field.InnerText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                    throw new ExecutionContractException("INVALID_PARAMS", "Unknown or non-integer execution budget: " + field.LocalName);
            }
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
            if (type == typeof(string) || (type == null && raw.StartsWith("\"", StringComparison.Ordinal))) return DecodeJsonStringLiteral(raw);
            if (type == typeof(char))
            {
                string character = DecodeJsonStringLiteral(raw);
                if (character.Length != 1)
                    throw new ExecutionContractException("TYPED_VALUE_DECODE_FAILED", "System.Char requires a single-character JSON string.");
                return character[0];
            }
            if (type == typeof(bool) || (type == null && (raw == "true" || raw == "false"))) return bool.Parse(raw);
            if (type != null && type.IsEnum) return Enum.Parse(type, DecodeJsonStringLiteral(raw), true);
            if (type == typeof(byte)) return byte.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(sbyte)) return sbyte.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(short)) return short.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(ushort)) return ushort.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(int) || type == null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return int.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(uint)) return uint.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(long)) return long.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(ulong)) return ulong.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(float))
            {
                float value = float.Parse(raw, CultureInfo.InvariantCulture);
                if (float.IsNaN(value) || float.IsInfinity(value))
                    throw new ExecutionContractException("TYPED_VALUE_DECODE_FAILED", "System.Single must be finite.");
                return value;
            }
            if (type == typeof(decimal)) return decimal.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(double) || type == null)
            {
                double value = double.Parse(raw, CultureInfo.InvariantCulture);
                if (double.IsNaN(value) || double.IsInfinity(value))
                    throw new ExecutionContractException("TYPED_VALUE_DECODE_FAILED", "System.Double must be finite.");
                return value;
            }
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
            if (value is string || value is char || value is Guid || value.GetType().IsEnum)
                return EncodeJsonStringLiteral(Convert.ToString(value, CultureInfo.InvariantCulture));
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string LegacyString(object value) { return value?.ToString() ?? "(null)"; }
        private static string BoundedSummary(object value)
        {
            string summary = LegacyString(value);
            return summary.Length <= 1024 ? summary : summary.Substring(0, 1024) + "…";
        }
        private static string DecodeJsonStringLiteral(string value)
        {
            try
            {
                var document = new System.Xml.XmlDocument();
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(
                    Encoding.UTF8.GetBytes(value ?? ""), new System.Xml.XmlDictionaryReaderQuotas
                    {
                        MaxDepth = 16, MaxStringContentLength = 1048576, MaxArrayLength = 1048576,
                    }))
                    document.Load(reader);
                var root = document.DocumentElement;
                if (root == null || root.GetAttribute("type") != "string")
                    throw new ExecutionContractException("TYPED_VALUE_DECODE_FAILED", "Expected a JSON string literal.");
                return root.InnerText;
            }
            catch (ExecutionContractException) { throw; }
            catch (Exception ex)
            {
                throw new ExecutionContractException("TYPED_VALUE_DECODE_FAILED", "Invalid JSON string literal: " + ex.Message);
            }
        }

        private static string EncodeJsonStringLiteral(string value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(string)).WriteObject(stream, value ?? "");
                return Encoding.UTF8.GetString(stream.ToArray());
            }
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

        private static CSharpExecutionDiagnosticsPayload ToExecutionDiagnostics(CSharpExecutionDiagnostics diagnostics)
        {
            return new CSharpExecutionDiagnosticsPayload
            {
                resolveMs = diagnostics.ResolveMs,
                bindMs = diagnostics.BindMs,
                invokeMs = diagnostics.InvokeMs,
                encodeMs = diagnostics.EncodeMs,
                getterCallCount = diagnostics.GetterCallCount,
                methodCallCount = diagnostics.MethodCallCount,
                completedBoundaries = diagnostics.CompletedBoundaries,
                droppedDiagnosticCount = diagnostics.DroppedDiagnosticCount,
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

        internal static ErrorDetailPayload ToErrorDetail(ExecutionContractException ex, string id, string command)
        {
            var detail = ex.Detail ?? new Dictionary<string, object>();
            PreserveExceptionEvidence(detail, ex);
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
            var executionDiagnostics = detail.TryGetValue("executionDiagnostics", out var diagnosticsValue)
                ? diagnosticsValue as CSharpExecutionDiagnosticsPayload
                : null;
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
                executionDiagnosticsJson = executionDiagnostics == null ? "" : JsonUtility.ToJson(executionDiagnostics),
                sourceSpan = span,
                diagnostics = Array.Empty<ExecutionDiagnosticPayload>(),
                candidates = candidateItems,
                executionDiagnostics = executionDiagnostics,
                cleanupDiagnostics = cleanupDiagnostics,
                exceptionType = DetailString(detail, "exceptionType", ex.GetType().FullName),
                exceptionMessage = DetailString(detail, "exceptionMessage", ex.Message),
                wrapperExceptionType = DetailString(detail, "wrapperExceptionType", ""),
                stackTrace = BoundedStack(DetailString(detail, "stackTrace", ex.StackTrace)),
            };
        }

        internal static Exception UnwrapInvocationException(Exception ex)
        {
            while (ex != null)
            {
                if (ex is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
                {
                    ex = aggregate.InnerExceptions[0];
                    continue;
                }
                if (ex is TargetInvocationException invocation && invocation.InnerException != null)
                {
                    ex = invocation.InnerException;
                    continue;
                }
                break;
            }
            return ex ?? new InvalidOperationException("Unknown execution failure.");
        }

        internal static void PreserveExceptionEvidence(IDictionary<string, object> detail, Exception ex)
        {
            if (detail == null || ex == null) return;
            var original = UnwrapInvocationException(ex);
            if (!detail.ContainsKey("exceptionType")) detail["exceptionType"] = original.GetType().FullName;
            if (!detail.ContainsKey("exceptionMessage")) detail["exceptionMessage"] = original.Message ?? "";
            if (!detail.ContainsKey("stackTrace")) detail["stackTrace"] = BoundedStack(original.StackTrace);
            string originalType = Convert.ToString(detail["exceptionType"], CultureInfo.InvariantCulture);
            string wrapperType = ex.GetType().FullName;
            if (!string.Equals(originalType, wrapperType, StringComparison.Ordinal)
                && !detail.ContainsKey("wrapperExceptionType"))
                detail["wrapperExceptionType"] = wrapperType;
        }

        private static string DetailString(IDictionary<string, object> detail, string key, string fallback)
        {
            if (detail != null && detail.TryGetValue(key, out var value) && value != null)
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback ?? "";
            return fallback ?? "";
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
            return "[" + string.Join(",", (values ?? Array.Empty<string>()).Select(value => EncodeJsonStringLiteral(value ?? ""))) + "]";
        }

        private static string BoundedStack(string stack)
        {
            stack = stack ?? "";
            return stack.Length <= 4096 ? stack : stack.Substring(0, 4096);
        }
    }
}
