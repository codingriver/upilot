using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    /// <summary>Historical Step control. New generic orchestration is suspended at every public entry.</summary>
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
            public string recoveryRequestId;
            public string dispositionRequestId, expectedStateHash, reason;
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
            public AutomationStepReleaseProof releaseProof;
            public AutomationStepDisposition disposition;
            public bool businessTerminal;
            public string outcome;
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
            AssemblyReloadEvents.beforeAssemblyReload += () => s_executor?.Dispose();
        }
        internal static void ResetActive()
        {
            s_executor?.Finish("Aborted", "SERVICE_HARD_STOP", "UPilot service lifetime ended.");
            s_executor?.Dispose();
            s_executor = null;
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
        internal const string OrchestrationDisabledCode = "GENERIC_ORCHESTRATION_DISABLED";
        internal const string OrchestrationDisabledReason = "通用编排新启动已暂停；请使用测试、验收、编译、Capture、Snapshot 或构建专用工具。";
        internal static bool IsNewRunAction(string action) => action == "start" || action == "validate";
        public static string ValidateJson(string planJson) => JsonUtility.ToJson(
            AutomationStepValidation.Invalid(OrchestrationDisabledCode, OrchestrationDisabledReason));
        /// <summary>Fixed product suspension; no validation, allocation, persistence or test bypass.</summary>
        public static string StartJson(string operationId, string captureSessionId, string planJson) =>
            throw new InvalidOperationException(OrchestrationDisabledCode + ": " + OrchestrationDisabledReason);
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
            if (state?.runId != runId || !string.IsNullOrEmpty(state?.disposition?.requestId))
                state = executor.Historical(runId, operationId);
            if (state == null || string.IsNullOrEmpty(runId) || state.runId != runId
                || (operationId != null && state.operationId != operationId))
                throw new InvalidOperationException("STEP_RUN_IDENTITY_MISMATCH");
            return state;
        }
        public static string StateJson(string runId) => JsonUtility.ToJson(Public(State(runId)));
        [Serializable] internal sealed class StopReply
        {
            public bool ok = true, changed;
            public string status;
        }
        internal static StopReply Stop(string runId)
        {
            RequireMainThread();
            var run = s_executor?.State;
            if (run == null || run.terminal || run.runId != runId)
                return new StopReply { status = "not_found" };
            if (run.cancelRequested || run.stage == "Finalizing")
                return new StopReply { status = "ending" };
            s_executor.RequestCancel();
            return new StopReply { status = "ending", changed = true };
        }
        public static string CancelJson(string runId) => JsonUtility.ToJson(Stop(runId));
        public static string ArtifactsJson(string runId) => JsonUtility.ToJson(PublicArtifacts(State(runId)));
        public UPilotAutomationStepService(UPilotBridge bridge) { _bridge = bridge; }
        public void RegisterCommands()
        {
            foreach (string action in new[] { "catalog", "validate", "start", "state", "cancel", "recover", "release_preview", "release", "artifacts" })
            {
                string route = "automation.steps." + action;
                _bridge.Router.Register(UPilotCommandRouter.AutomationStepDescriptor(route, action),
                    (id, json, token) => Handle(route, action, id, json, token));
            }
        }
        private async Task Handle(string route, string action, string id, string json, CancellationToken token)
        {
            // Reject even malformed plans before parsing, enqueueing or invoking project callbacks.
            if (IsNewRunAction(action))
            {
                await _bridge.SendErrorAsync(id, OrchestrationDisabledCode, OrchestrationDisabledReason, token, route);
                return;
            }
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
                    if (action == "cancel") { completion.SetResult(Stop(request.runId)); return; }
                    if (action == "recover" || action == "release" || action == "release_preview")
                        throw new InvalidOperationException("STEP_RECOVERY_REMOVED_USE_STOP");
                    var original = ReadState(s_executor, request.runId, request.operationId ?? "");
                    if (!string.IsNullOrEmpty(original.disposition?.requestId)) { completion.SetResult(Public(original)); return; }
                    completion.SetResult(Public(original));
                }
                catch (Exception ex) { completion.SetException(ex); }
            });
            try
            {
                // JsonUtility serializes generic fields by declared type; object would erase the payload.
                var result = await completion.Task;
                if (result is CatalogResult catalog) await _bridge.SendResultAsync(id, route, catalog, token);
                else if (result is AutomationStepValidation validation) await _bridge.SendResultAsync(id, route, validation, token);
                else if (result is StopReply stopped) await _bridge.SendResultAsync(id, route, stopped, token);
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
            artifacts = PublicArtifacts(run), domain = run, disposition = run.disposition,
            businessTerminal = run.terminal && string.IsNullOrEmpty(run.disposition?.requestId),
            outcome = string.IsNullOrEmpty(run.disposition?.requestId) ? run.status : "unknown",
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
