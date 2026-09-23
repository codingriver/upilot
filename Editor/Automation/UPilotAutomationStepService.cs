using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    /// <summary>Internal Bridge routes; public long-running control remains unity_operation_*.</summary>
    [InitializeOnLoad]
    public sealed class UPilotAutomationStepService
    {
        [Serializable] private sealed class Message { public Request payload; }
        [Serializable] private sealed class Request
        {
            public string planJson;
            public string operationId;
            public string runId;
            public string captureSessionId;
        }
        [Serializable] private sealed class CatalogResult
        { public AutomationStepDescriptor[] steps; public AutomationDiagnostic[] diagnostics; }
        [Serializable] internal sealed class FileArtifact
        {
            public string kind = "file";
            public string path;
            public long bytes;
            public string sha256;
            public string instanceId;
            public string artifactKind;
        }
        [Serializable] internal sealed class ArtifactSet
        {
            public FileArtifact events;
            public FileArtifact summary;
            public FileArtifact[] attachments = Array.Empty<FileArtifact>();
        }
        [Serializable] internal sealed class StateResult
        {
            public bool ok = true;
            public string runId, operationId, status, phase, error, detail, failureSignature;
            public bool terminal, cleanupPending;
            public float progress;
            public ArtifactSet artifacts;
            public AutomationStepRun domain;
        }
        private static AutomationStepExecutor s_executor;
        private static AutomationStepRegistry s_registry;
        [ThreadStatic] private static CallbackScope s_callback;
        private static readonly int MainThreadId = Thread.CurrentThread.ManagedThreadId;
        private readonly UPilotBridge _bridge;
        static UPilotAutomationStepService() { EditorApplication.delayCall += Initialize; }
        private static void Initialize()
        {
            if (s_executor != null) return;
            RequireMainThread();
            s_executor = new AutomationStepExecutor(Registry, Path.Combine(Application.dataPath, "../Library/UPilot/step-run.json"));
            s_executor.RestoreStored();
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += () => s_executor.Dispose();
        }
        private static void Update() => s_executor?.Tick();
        private static AutomationStepRegistry Registry
        {
            get { RequireMainThread(); return s_registry ??= new AutomationStepRegistry(); }
        }
        private static void RequireMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != MainThreadId)
                throw new InvalidOperationException("STEP_MAIN_THREAD_REQUIRED");
        }
        internal sealed class CallbackScope : IDisposable
        {
            internal readonly AutomationStepExecutor Executor;
            internal readonly bool Writable;
            internal CallbackScope(AutomationStepExecutor executor, bool writable)
            { Executor = executor; Writable = writable; }
            public void Dispose() { s_callback = null; }
        }
        internal static CallbackScope EnterCallback(AutomationStepExecutor executor, bool writable)
        {
            RequireMainThread();
            if (s_callback != null) throw new InvalidOperationException("STEP_CALLBACK_REENTRANT");
            return s_callback = new CallbackScope(executor, writable);
        }
        private static AutomationStepExecutor WritableExecutor()
        {
            RequireMainThread();
            if (s_callback == null || !s_callback.Writable || s_callback.Executor == null)
                throw new InvalidOperationException("STEP_CHECKPOINT_WRITE_FORBIDDEN");
            return s_callback.Executor;
        }
        internal static string CallbackOperationId => s_callback?.Executor?.OperationId ?? "";
        internal static void StartRunCapture(string runId, string instanceId) =>
            WritableExecutor().StartRunCapture(runId, instanceId);
        internal static void ObserveRunCapture(string runId, string instanceId) =>
            WritableExecutor().ObserveRunCapture(runId, instanceId);
        public static string BeginSnapshotJson(string runId, string instanceId, string evidenceKey, string arguments) =>
            WritableExecutor().Snapshot(runId, instanceId, evidenceKey, arguments, false);
        public static string PollSnapshotJson(string runId, string instanceId, string evidenceKey) =>
            WritableExecutor().Snapshot(runId, instanceId, evidenceKey, null, false);
        public static string CancelSnapshotJson(string runId, string instanceId, string evidenceKey) =>
            WritableExecutor().Snapshot(runId, instanceId, evidenceKey, null, true);
        public static string SnapshotErrorJson(string runId, string instanceId, string evidenceKey)
        {
            RequireMainThread();
            if (s_callback?.Executor == null) throw new InvalidOperationException("STEP_CALLBACK_REQUIRED");
            return s_callback.Executor.SnapshotError(runId, instanceId, evidenceKey);
        }
        /// <summary>Durably replace the active item's JSON object inside a writable lifecycle callback.</summary>
        public static void SaveCheckpoint(string runId, string instanceId, string checkpointJson) =>
            WritableExecutor().SaveCheckpoint(runId, instanceId, checkpointJson);
        /// <summary>Durably replace one run-shared key, without replacing any other project's values.</summary>
        public static void SaveSharedValue(string runId, string instanceId, string key, string valueJson) =>
            WritableExecutor().SaveSharedValue(runId, instanceId, key, valueJson);
        /// <summary>Durably register an immutable project file during the current writable Step callback.</summary>
        public static void RegisterArtifact(string runId, string instanceId, string kind, string path) =>
            WritableExecutor().RegisterArtifact(runId, instanceId, kind, path);
        /// <summary>Read-only discovered directory, including registration diagnostics.</summary>
        public static string CatalogJson()
        {
            var registry = Registry;
            return JsonUtility.ToJson(new CatalogResult { steps = registry.Catalog(),
                diagnostics = System.Linq.Enumerable.ToArray(registry.Diagnostics) });
        }
        public static string ValidateJson(string planJson) => JsonUtility.ToJson(ValidatePlanJson(planJson, out _));
        private static AutomationStepValidation ValidatePlanJson(string planJson, out AutomationStepPlan plan)
        {
            var parsed = AutomationStepPlanValidator.Parse(planJson, out plan);
            if (plan == null) return parsed;
            var validation = AutomationStepPlanValidator.Validate(plan, Registry);
            parsed.diagnostics.AddRange(validation.diagnostics);
            parsed.ok &= validation.ok;
            parsed.budgetSeconds = validation.budgetSeconds;
            return parsed;
        }
        /// <summary>Start once after full preflight; the caller retains any supplied Capture ownership.</summary>
        public static string StartJson(string operationId, string captureSessionId, string planJson)
        {
            var validation = ValidatePlanJson(planJson, out var plan);
            if (!validation.ok) throw new InvalidOperationException("STEP_PLAN_INVALID: " + JsonUtility.ToJson(validation));
            Initialize();
            return JsonUtility.ToJson(Public(s_executor.Start(plan, operationId, captureSessionId)));
        }
        private static AutomationStepRun State(string runId)
        {
            RequireMainThread();
            return ReadState(s_executor, runId);
        }
        internal static AutomationStepRun ReadState(AutomationStepExecutor executor, string runId, string operationId = null)
        {
            // Observation must not initialize/restore the executor or advance a lifecycle callback.
            if (executor == null) throw new InvalidOperationException("STEP_SERVICE_INITIALIZING");
            var state = executor.State;
            if (state == null || string.IsNullOrEmpty(runId) || state.runId != runId
                || (operationId != null && state.operationId != operationId))
                throw new InvalidOperationException("STEP_RUN_IDENTITY_MISMATCH");
            return state;
        }
        public static string StateJson(string runId) => JsonUtility.ToJson(Public(State(runId)));
        public static string CancelJson(string runId)
        { State(runId); s_executor.RequestCancel(); return StateJson(runId); }
        public static string ArtifactsJson(string runId) => JsonUtility.ToJson(PublicArtifacts(State(runId)));
        public UPilotAutomationStepService(UPilotBridge bridge) { _bridge = bridge; }
        public void RegisterCommands()
        {
            foreach (string action in new[] { "catalog", "validate", "start", "state", "cancel", "artifacts" })
            {
                string route = "automation.steps." + action;
                _bridge.Router.Register(UPilotCommandRouter.AutomationStepDescriptor(route, action),
                    (id, json, token) => Handle(route, action, id, json, token));
            }
        }
        private async Task Handle(string route, string action, string id, string json, CancellationToken token)
        {
            var request = JsonUtility.FromJson<Message>(json)?.payload ?? new Request();
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    if (action == "catalog")
                    {
                        completion.SetResult(new CatalogResult { steps = Registry.Catalog(),
                            diagnostics = System.Linq.Enumerable.ToArray(Registry.Diagnostics) }); return;
                    }
                    if (action == "validate" || action == "start")
                    {
                        var validation = ValidatePlanJson(request.planJson, out var plan);
                        if (action == "validate") { completion.SetResult(validation); return; }
                        if (!validation.ok) throw new InvalidOperationException("STEP_PLAN_INVALID: " + JsonUtility.ToJson(validation));
                        Initialize();
                        completion.SetResult(Public(s_executor.Start(plan, request.operationId, request.captureSessionId))); return;
                    }
                    ReadState(s_executor, request.runId, request.operationId ?? "");
                    if (action == "cancel") s_executor.RequestCancel();
                    completion.SetResult(Public(s_executor.State));
                }
                catch (Exception ex) { completion.SetException(ex); }
            });
            try
            {
                // JsonUtility serializes generic fields by declared type; object would erase the payload.
                var result = await completion.Task;
                if (result is CatalogResult catalog) await _bridge.SendResultAsync(id, route, catalog, token);
                else if (result is AutomationStepValidation validation) await _bridge.SendResultAsync(id, route, validation, token);
                else if (result is StateResult state) await _bridge.SendResultAsync(id, route, state, token);
                else throw new InvalidOperationException("STEP_RESPONSE_INVALID");
            }
            catch (Exception ex)
            {
                string code = ex is InvalidOperationException && ex.Message == "STEP_SERVICE_INITIALIZING"
                    ? "STEP_SERVICE_INITIALIZING" : "STEP_REQUEST_FAILED";
                await _bridge.SendErrorAsync(id, code, ex.Message, token, route);
            }
        }
        internal static StateResult Public(AutomationStepRun run) => new()
        {
            runId = run.runId, operationId = run.operationId, status = run.status, phase = run.stage,
            error = run.error?.code ?? "", failureSignature = run.error?.code ?? "", detail = run.error?.message ?? "",
            terminal = run.terminal, cleanupPending = run.cleanupPending || !run.terminal,
            progress = run.steps.Count == 0 ? 0 : (float)run.cursor / run.steps.Count,
            artifacts = PublicArtifacts(run), domain = run,
        };
        private static ArtifactSet PublicArtifacts(AutomationStepRun run)
        {
            var result = new ArtifactSet();
            var attachments = new System.Collections.Generic.List<FileArtifact>();
            var artifacts = (run.artifacts?.Length ?? 0) > 0 ? run.artifacts
                : run.registeredArtifacts?.ToArray() ?? Array.Empty<AutomationReportArtifact>();
            foreach (var artifact in artifacts)
            {
                var file = new FileArtifact
                { path = artifact.projectRelativePath, bytes = artifact.bytes, sha256 = artifact.sha256,
                    instanceId = artifact.instanceId, artifactKind = artifact.kind };
                if (artifact.kind == "events" && string.IsNullOrEmpty(artifact.instanceId)) result.events = file;
                else if (artifact.kind == "summary" && string.IsNullOrEmpty(artifact.instanceId)) result.summary = file;
                else attachments.Add(file);
            }
            result.attachments = attachments.ToArray();
            return result;
        }
    }
}
