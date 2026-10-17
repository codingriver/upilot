using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    /// <summary>Official Step plan validation and execution; lifecycle and ownership remain executor-controlled.</summary>
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
        [Serializable] internal sealed class AdmissionResult
        {
            public bool ok = true, started;
            public string code = "", message = "", currentMode = "unknown", instanceId = "";
        }
        internal sealed class AdmissionObservation
        {
            internal EditorContextPayload context;
            internal string currentMode;
            internal bool isCompiling, isUpdating;
            internal CurrentSnapshot executor;
        }
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
            public bool started;
            public string code = "", message = "", currentMode = "", instanceId = "";
            public string runId, operationId, status, phase, error, detail, failureSignature;
            public bool terminal, cleanupPending;
            public float progress;
            public ArtifactSet artifacts;
            public AutomationReportLogSummary logSummary;
            public AutomationStepRun domain;
            public AutomationStepReleaseProof releaseProof;
            public AutomationStepDisposition disposition;
            public bool businessTerminal;
            public string outcome;
            // Empty outcomes retain unknown semantics for legacy runs; interpret success only with this contract.
            public string cleanupOutcome = "", reportOutcome = "";
            public string[] unresolvedResources = Array.Empty<string>();
            public bool cleanupSucceeded;
        }
        private static AutomationStepExecutor s_executor;
        private static AutomationStepExecutor s_initializingExecutor;
        private static bool s_updateSubscribed;
        private static AutomationStepRegistry s_registry;
        private static string s_storePath = Path.Combine(Application.dataPath, "../Library/UPilot/step-run.json");
        private static string s_initializationError = "";
        private static Func<AdmissionObservation> s_readAdmission = ReadAdmission;
        [ThreadStatic] private static CallbackScope s_callback;
        private static readonly int MainThreadId = Thread.CurrentThread.ManagedThreadId;
        private readonly UPilotBridge _bridge;
        static UPilotAutomationStepService()
        {
            // The service owns one Tick subscription only while executor work remains.
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            EditorApplication.quitting += BeforeEditorQuit;
            EditorApplication.delayCall += Initialize;
        }
        private static void Initialize()
        {
            RequireMainThread();
            try { if (s_executor == null) CreateExecutor(true); }
            finally { RefreshUpdateSubscription(); }
        }
        private static void CreateExecutor(bool restoreStored)
        {
            AutomationStepExecutor executor = null;
            try
            {
                executor = new AutomationStepExecutor(Registry, s_storePath, gizmosAccess: new UnityAutomationGizmosAccess());
                s_initializingExecutor = executor;
                if (restoreStored) executor.RestoreStored();
                // Publish only a fully initialized executor.
                s_executor = executor;
                s_initializationError = "";
            }
            catch (Exception ex)
            {
                s_initializationError = "STEP_SERVICE_INITIALIZATION_FAILED: " + ex.Message;
                try { executor?.Dispose(); }
                catch (Exception disposeError) { s_initializationError += "; " + disposeError.Message; }
                throw;
            }
            finally { s_initializingExecutor = null; }
        }
        internal static void ResetActive()
        {
            RequireMainThread();
            var previous = s_executor;
            s_executor = null;
            RefreshUpdateSubscription();
            var errors = new System.Collections.Generic.List<Exception>();
            previous?.TraceInterruption("run.service_stopping", "服务硬停止");
            try { previous?.Finish("Aborted", "SERVICE_HARD_STOP", "UPilot service lifetime ended."); }
            catch (Exception ex) { errors.Add(ex); }
            try { previous?.Dispose(); }
            catch (Exception ex) { errors.Add(ex); }
            previous?.FinishExecutionLog("服务硬停止，资源释放结果未验证");
            // Hard reset creates an empty executor; stored work belongs to the old lifetime.
            // Old finalization failures are diagnostic, not a prerequisite for rebuilding.
            try { CreateExecutor(false); }
            catch (Exception ex) { errors.Add(ex); }
            finally { RefreshUpdateSubscription(); }
            if (errors.Count > 0) throw new AggregateException("STEP_EXECUTOR_RESET_FAILED", errors);
        }
        private static void OnCompilationStarted(object context)
        {
            if (UPilotDiagnosticModuleService.IsCompilationContext(context)) return;
            // Restore callbacks can run before a newly constructed executor is published.
            var executor = s_callback?.Executor ?? s_initializingExecutor ?? s_executor;
            try { executor?.InterruptForCompilation(); }
            finally { RefreshUpdateSubscription(); }
        }
        private static void BeforeAssemblyReload()
        {
            s_executor?.TraceInterruption("run.reloading", "脚本域重载，暂停观察");
            EditorApplication.delayCall -= Initialize;
            RemoveUpdateSubscription();
            s_executor?.Dispose();
        }
        private static void BeforeEditorQuit()
        {
            s_executor?.TraceInterruption("run.editor_quitting", "Editor 正在退出，任务结束未验证");
            EditorApplication.delayCall -= Initialize;
            RemoveUpdateSubscription();
            s_executor?.Dispose();
        }
        private static void Update()
        {
            try { s_executor?.Tick(); }
            finally { RefreshUpdateSubscription(); }
        }
        private static void RefreshUpdateSubscription()
        {
            RequireMainThread();
            // Busy also covers bounded resource release after the business result is terminal.
            if (s_executor?.Busy != true)
            {
                RemoveUpdateSubscription();
                return;
            }
            if (s_updateSubscribed) return;
            EditorApplication.update += Update;
            s_updateSubscribed = true;
        }
        private static void RemoveUpdateSubscription()
        {
            EditorApplication.update -= Update;
            s_updateSubscribed = false;
        }
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
        /// <summary>安全写入并持久登记文本附件；项目负责内容的结构与序列化。</summary>
        public static string WriteTextArtifact(string runId, string instanceId, string kind, string fileName, string content) =>
            WritableExecutor().WriteTextArtifact(runId, instanceId, kind, fileName, content);
        /// <summary>Validate one JSON value before publishing the original UTF-8 text.</summary>
        public static string WriteJsonArtifact(string runId, string instanceId, string kind, string fileName, string rawJson) =>
            WritableExecutor().WriteJsonArtifact(runId, instanceId, kind, fileName, rawJson);
        /// <summary>Read-only discovered directory, including registration diagnostics.</summary>
        public static string CatalogJson()
        {
            var registry = Registry;
            return JsonUtility.ToJson(new CatalogResult { steps = registry.Catalog(),
                diagnostics = System.Linq.Enumerable.ToArray(registry.Diagnostics) });
        }
        internal static bool IsNewRunAction(string action) => action == "start" || action == "validate";
        public static string ValidateJson(string planJson) => JsonUtility.ToJson(ValidatePlanJson(planJson, out _));
        private static AutomationStepValidation ValidatePlanJson(string planJson, out AutomationStepPlan plan)
        {
            RequireMainThread();
            var parsed = AutomationStepPlanValidator.Parse(planJson, out plan, out var timingInputs);
            if (plan == null) return parsed;
            var validation = AutomationStepPlanValidator.Validate(plan, Registry, timingInputs);
            parsed.diagnostics.AddRange(validation.diagnostics);
            parsed.ok &= validation.ok;
            parsed.budgetSeconds = validation.budgetSeconds;
            parsed.budgetPreview = validation.budgetPreview;
            return parsed;
        }
        /// <summary>Read-only runtime preflight. This does not reserve a slot or start any resource.</summary>
        public static string AdmissionJson(string planJson) => JsonUtility.ToJson(AdmissionPlanJson(planJson, out _));
        private static AdmissionObservation ReadAdmission() => new()
        {
            context = UPilotBridge.Instance.GetEditorExecutionContext("automation.steps.admission"),
            currentMode = UPilotPlayModeCompilePolicy.CurrentMode,
            isCompiling = EditorApplication.isCompiling,
            isUpdating = EditorApplication.isUpdating,
            executor = GetCurrentSnapshot(),
        };
        private static AdmissionResult AdmissionPlanJson(string planJson, out AutomationStepPlan plan)
        {
            RequireMainThread();
            var validation = ValidatePlanJson(planJson, out plan);
            if (!validation.ok) return new AdmissionResult { ok = false, code = "STEP_PLAN_INVALID",
                message = JsonUtility.ToJson(validation), currentMode = UPilotPlayModeCompilePolicy.CurrentMode };
            return CheckAdmission(plan, s_readAdmission());
        }
        // Main-thread facts are gathered once above; the shared decision has no Editor mutations or lifecycle calls.
        internal static AdmissionResult CheckAdmission(AutomationStepPlan plan, AdmissionObservation observation)
        {
            var result = new AdmissionResult { currentMode = observation?.currentMode ?? "unknown" };
            AdmissionResult Reject(string code, string message, string instanceId = "")
            {
                result.ok = false; result.code = code; result.message = message; result.instanceId = instanceId;
                return result;
            }
            var context = observation?.context;
            if (context == null || !context.connected || !context.authoritative || context.isStale)
                return Reject("STEP_EDITOR_STATE_UNAVAILABLE", "Wait for a fresh authoritative connected Editor state before starting a Step plan.");
            string phase = context.compilePhase;
            if (observation.isCompiling || context.isCompiling || context.verificationPending
                || phase == "queued" || phase == "compiling" || phase == "compiler_finished"
                || phase == "domain_reload" || phase == "verifying")
                return Reject("STEP_EDITOR_COMPILING", "Compilation or verification is still in progress; observe its original compile before starting a Step plan.");
            if (phase != "idle" && phase != "completed" && phase != "failed")
                return Reject("STEP_EDITOR_STATE_UNAVAILABLE", "The current compile state is unknown; inspect Editor and compile diagnostics.");
            if (phase == "failed" || context.blockedReason == "CompileErrors")
                return Reject("STEP_EDITOR_COMPILE_FAILED", "Resolve the reported Unity compile errors before starting a Step plan.");
            if (observation.isUpdating || context.isUpdating)
                return Reject("STEP_EDITOR_UPDATING", "Wait for Unity asset updating to finish before starting a Step plan.");
            string mode = result.currentMode;
            if (mode == "enteringPlay" || mode == "exitingPlay"
                || context.playModeState == "enteringPlay" || context.playModeState == "exitingPlay")
                return Reject("STEP_MODE_TRANSITION_BUSY", "Wait for the current mode transition to reach a stable Editor mode.");
            if ((mode != "edit" && mode != "play" && mode != "pause")
                || context.playModeState != mode)
                return Reject("STEP_EDITOR_STATE_UNAVAILABLE", "Wait for matching authoritative stable Editor mode observations.");
            if (observation.executor?.initialized != true)
                return Reject("STEP_SERVICE_INITIALIZING", "Wait for the Step executor to finish initializing.");
            if (observation.executor.busy)
                return Reject("STEP_EXECUTOR_BUSY", "The current Step task or its bounded framework cleanup is still active.");
            foreach (var item in plan.steps)
            {
                if (item.phase != "Normal") break;
                if (item.stepId == "upilot.console_capture_start" || item.stepId == "upilot.wait_seconds") continue;
                if (item.stepId == "upilot.enter_edit_mode") break;
                if (item.stepId == "upilot.open_scene"
                    || item.stepId == "upilot.enter_play_mode" && item.arguments != "")
                {
                    if (mode != "edit") return Reject("STEP_EDIT_MODE_REQUIRED",
                        "Step " + item.instanceId + " requires EditMode. Add an explicit upilot.enter_edit_mode before this step.", item.instanceId);
                    if (item.stepId == "upilot.open_scene") continue;
                }
                // Explicit play transitions control subsequent mode; custom/Snapshot steps retain their own runtime checks.
                break;
            }
            return result;
        }
        private static StateResult RejectedStart(AdmissionResult rejection, string operationId, Exception exception = null)
        {
            Logger.ReportFailure("STEP", rejection.code + " operationId=" + operationId + " " + rejection.message,
                exception, scope: operationId ?? "", terminal: true);
            return new StateResult { ok = false, started = false, operationId = operationId,
                code = rejection.code, message = rejection.message, currentMode = rejection.currentMode,
                instanceId = rejection.instanceId, error = rejection.code, detail = rejection.message };
        }
        /// <summary>Start once after full preflight; the caller retains any supplied Capture ownership.</summary>
        public static string StartJson(string operationId, string captureSessionId, string planJson) =>
            JsonUtility.ToJson(StartPlan(operationId, captureSessionId, planJson));
        // Both the public C# facade and Bridge use this admission path and the same executor.
        internal static StateResult StartPlan(string operationId, string captureSessionId, string planJson)
        {
            RequireMainThread();
            var admission = AdmissionPlanJson(planJson, out var plan);
            if (!admission.ok) return RejectedStart(admission, operationId);
            try { return Public(s_executor.Start(plan, operationId, captureSessionId)); }
            catch (InvalidOperationException ex) when (ex.Message == "STEP_EDITOR_COMPILING" || ex.Message == "STEP_EXECUTOR_BUSY")
            {
                return RejectedStart(new AdmissionResult { ok = false, code = ex.Message, message = ex.Message,
                    currentMode = UPilotPlayModeCompilePolicy.CurrentMode }, operationId, ex);
            }
            finally { RefreshUpdateSubscription(); }
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
            if (state?.runId != runId || !string.IsNullOrEmpty(state?.disposition?.requestId))
                state = executor.Historical(runId, operationId);
            if (state == null || string.IsNullOrEmpty(runId) || state.runId != runId
                || (operationId != null && state.operationId != operationId))
                throw new InvalidOperationException("STEP_RUN_IDENTITY_MISMATCH");
            return state;
        }
        [Serializable] internal sealed class CurrentSnapshot
        {
            public bool initialized, busy;
            public string error = "";
            public AutomationStepRun run;
        }
        internal static CurrentSnapshot GetCurrentSnapshot()
        {
            RequireMainThread();
            var snapshot = ObserveCurrent(s_executor);
            if (!snapshot.initialized && !string.IsNullOrEmpty(s_initializationError))
                snapshot.error = s_initializationError;
            return snapshot;
        }
        // Memory only: no Initialize, historical lookup, restore, or executor advancement.
        internal static CurrentSnapshot ObserveCurrent(AutomationStepExecutor executor) => executor == null
            ? new CurrentSnapshot { error = "STEP_SERVICE_INITIALIZING" }
            : new CurrentSnapshot { initialized = true, busy = executor.Busy, run = executor.State };

        internal static StopReply StopCurrentImmediately(string runId, string operationId, string serviceLifecycleId)
        {
            RequireMainThread();
            var run = s_executor?.State;
            if (run == null || string.IsNullOrEmpty(runId) || run.runId != runId || run.operationId != operationId
                || run.serviceLifecycleId != serviceLifecycleId || serviceLifecycleId != UPilotServiceLifetime.Id)
                return new StopReply { status = "not_found" };
            if (run.terminal)
                return new StopReply { status = s_executor.ReleasingTerminalResources ? "ending" : "not_found" };
            try { return new StopReply { changed = s_executor.StopImmediately(), status = "stopped" }; }
            finally { RefreshUpdateSubscription(); }
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
            try
            {
                s_executor.RequestCancel();
                return new StopReply { status = "ending", changed = true };
            }
            finally { RefreshUpdateSubscription(); }
        }
        public static string CancelJson(string runId) => JsonUtility.ToJson(Stop(runId));
        public static string ArtifactsJson(string runId) => JsonUtility.ToJson(PublicArtifacts(State(runId)));
        public UPilotAutomationStepService(UPilotBridge bridge) { _bridge = bridge; }
        public void RegisterCommands()
        {
            foreach (string action in new[] { "catalog", "validate", "admission", "start", "state", "cancel", "recover", "release_preview", "release", "artifacts" })
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
                    if (action == "admission") { completion.SetResult(AdmissionPlanJson(request.planJson, out _)); return; }
                    if (IsNewRunAction(action))
                    {
                        if (action == "validate") completion.SetResult(ValidatePlanJson(request.planJson, out _));
                        else completion.SetResult(StartPlan(request.operationId, request.captureSessionId, request.planJson));
                        return;
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
                else if (result is AdmissionResult admission) await _bridge.SendResultAsync(id, route, admission, token);
                else if (result is StopReply stopped) await _bridge.SendResultAsync(id, route, stopped, token);
                else if (result is StateResult state) await _bridge.SendResultAsync(id, route, state, token);
                else throw new InvalidOperationException("STEP_RESPONSE_INVALID");
            }
            catch (Exception ex)
            {
                string code = ex is InvalidOperationException && ex.Message == "STEP_SERVICE_INITIALIZING"
                    ? "STEP_SERVICE_INITIALIZING" : "STEP_REQUEST_FAILED";
                await _bridge.SendErrorAsync(id, code, ex.Message, token, route, errorDetail: null, exception: ex);
            }
        }
        internal static StateResult Public(AutomationStepRun run) => new()
        {
            started = true,
            runId = run.runId, operationId = run.operationId, status = run.status, phase = run.stage,
            error = run.error?.code ?? "", failureSignature = run.error?.code ?? "", detail = run.error?.message ?? "",
            terminal = run.terminal, cleanupPending = run.cleanupPending || !run.terminal,
            progress = run.steps.Count == 0 ? 0 : (float)run.cursor / run.steps.Count,
            artifacts = PublicArtifacts(run), logSummary = run.logSummary, domain = run, disposition = run.disposition,
            businessTerminal = run.terminal && string.IsNullOrEmpty(run.disposition?.requestId),
            outcome = string.IsNullOrEmpty(run.disposition?.requestId) ? run.status : "unknown",
            cleanupOutcome = run.cleanupOutcome ?? "", reportOutcome = run.reportOutcome ?? "",
            unresolvedResources = run.unresolvedResources ?? Array.Empty<string>(),
            cleanupSucceeded = (run.cleanupOutcome == "Verified" || run.cleanupOutcome == "NotRequired")
                && !run.cleanupPending && (run.unresolvedResources?.Length ?? 0) == 0
                && System.Linq.Enumerable.All(run.steps, step => step.cleanupState == "Verified" || step.cleanupState == "NotRequired"),
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
