using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    /// <summary>Single main-thread owner of advancement. State inspection never calls step code.</summary>
    public sealed class AutomationStepExecutor : IDisposable
    {
        private readonly AutomationStepRegistry _registry;
        private readonly AutomationStepRunStore _store;
        private readonly Func<long> _clock;
        private AutomationStepRun _run;
        private IAutomationStep _step;
        private AutomationEvidenceSession _evidence;
        private AutomationRunCapture _ownedCapture;
        private readonly string _captureStorePath;
        private AutomationConsoleCollector _collector;
        private AutomationReportWriter _report;
        private long _nextPoll;
        private bool _storageFailed;
        private bool _ticking;
        private readonly int _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        private Exception _writeFailure;
        internal AutomationStepExecutor(AutomationStepRegistry registry, string storePath, Func<long> clock = null)
        {
            _registry = registry; _store = new AutomationStepRunStore(storePath); _clock = clock ?? (() => AutomationStepJson.Now);
            _captureStorePath = storePath + ".capture";
        }
        public AutomationStepRun State => _run == null ? null : AutomationStepJson.Copy(_run);
        internal string OperationId => _run?.operationId ?? "";
        public bool Busy => _storageFailed || (_run != null && (!_run.terminal || _run.recoveryRequired));
        public AutomationStepValidation Validate(AutomationStepPlan plan) => AutomationStepPlanValidator.Validate(plan, _registry);

        public AutomationStepRun Start(AutomationStepPlan plan, string operationId, string captureSessionId = "")
        {
            if (Busy) throw new InvalidOperationException("STEP_EXECUTOR_BUSY");
            var validation = Validate(plan);
            if (!validation.ok) throw new InvalidOperationException("STEP_PLAN_INVALID: " + JsonUtility.ToJson(validation));
            bool ownsCapture = plan.steps.Any(s => s.stepId == "upilot.console_capture_start");
            if (ownsCapture && !string.IsNullOrEmpty(captureSessionId))
                throw new InvalidOperationException("STEP_CAPTURE_OWNERSHIP_CONFLICT");
            if (plan.logPolicy?.Length > 0 && string.IsNullOrEmpty(captureSessionId) && !ownsCapture)
                throw new InvalidOperationException("STEP_CAPTURE_REQUIRED: Log policy requires a Capture session.");
            if (string.IsNullOrWhiteSpace(operationId)) throw new ArgumentException("Operation identity required.");
            var frozen = AutomationStepJson.Copy(plan);
            foreach (var item in frozen.steps)
            {
                _registry.TryGet(item.stepId, out var descriptor);
                if (item.timeoutSeconds == -1) item.timeoutSeconds = descriptor.timeoutSeconds;
                if (item.pollIntervalSeconds == -1) item.pollIntervalSeconds = descriptor.pollIntervalSeconds;
            }
            _run = new AutomationStepRun
            {
                runId = Guid.NewGuid().ToString("N"), operationId = operationId,
                editorIdentity = AutomationStepRunStore.EditorIdentity, plan = frozen,
                planHash = AutomationStepRunStore.Hash(frozen), startedAtUtcMs = _clock(), captureSessionId = captureSessionId ?? "",
                captureOwned = ownsCapture,
            };
            foreach (var item in frozen.steps)
            {
                _registry.TryGet(item.stepId, out var descriptor);
                _run.steps.Add(new AutomationStepRecord { instanceId = item.instanceId, typeIdentity = descriptor.typeIdentity });
            }
            _step = null; _evidence = null; _ownedCapture = null; _collector?.Dispose(); _collector = null; _nextPoll = 0;
            _run.reportDirectory = "Log/UPilotSteps/" + _run.runId;
            Save();
            try
            {
                if (_run.captureSessionId != "")
                {
                    _evidence = AutomationEvidenceSession.Borrow(_run.captureSessionId);
                    _run.captureStartSequence = _evidence.Boundary();
                    _run.intervalStartSequence = _run.captureStartSequence;
                }
                _report = AutomationReportWriter.Create(new AutomationReportCreateRequest
                { runId = _run.runId, outputDirectory = _run.reportDirectory, startedAtUtcMs = _run.startedAtUtcMs });
                Save();
            }
            catch (Exception ex) { Recovery("STEP_EVIDENCE_START_FAILED", ex.Message); }
            return State;
        }

        internal void RestoreStored()
        {
            try
            {
                _run = _store.Load();
                if (_run == null || _run.terminal || _run.status == "RecoveryRequired") return;
                if (_run.editorIdentity != AutomationStepRunStore.EditorIdentity)
                { Recovery("STEP_EDITOR_RESTARTED", "Editor restart requires manual recovery; execution was not resumed."); return; }
                for (int index = 0; index < _run.steps.Count; index++)
                {
                    if (!_registry.TryGet(_run.plan.steps[index].stepId, out var descriptor)
                        || descriptor.typeIdentity != _run.steps[index].typeIdentity)
                    { Recovery("STEP_REGISTRY_CHANGED", "Frozen registration is no longer available."); return; }
                }
                _report = AutomationReportWriter.OpenExisting(_run.reportDirectory, _run.runId);
                if (_run.captureOwned && _run.captureStartIntent)
                {
                    try
                    {
                        _ownedCapture = new AutomationRunCapture(_captureStorePath, _run.runId, true);
                        if (_run.captureSessionId != "" && _ownedCapture.SessionId != _run.captureSessionId)
                            throw new InvalidDataException("STEP_CAPTURE_IDENTITY_INVALID");
                        var capture = _ownedCapture.Observe();
                        // The private identity may have committed immediately before Domain Reload.
                        if (_run.captureSessionId == "")
                        {
                            _run.captureSessionId = capture.sessionId;
                            _run.captureStartSequence = 0;
                            _run.intervalStartSequence = 0;
                            Save();
                        }
                        if (capture.active) _evidence = AutomationEvidenceSession.Borrow(_run.captureSessionId);
                        else if (_run.captureStopVerified) AutomationRunCapture.VerifyStopped(capture);
                    }
                    catch (Exception ex)
                    {
                        RecordError("STEP_CAPTURE_RECOVERY_FAILED", ex.Message);
                        _run.recoveryRequired = true; _run.cleanupPending = true;
                        _ownedCapture = null; _evidence = null;
                        Save(); // Preserve safe business Finally work, without adopting unknown ownership.
                    }
                }
                else if (_run.captureSessionId != "") _evidence = AutomationEvidenceSession.Borrow(_run.captureSessionId);
                if (_run.stage == "Finalizing") return;
                if (_run.cursor >= _run.steps.Count) return;
                var record = Current;
                if (record.stage == "Pending" || record.stage == "Completed" || record.stage == "ResourceCleaning") return;
                _step = _registry.Create(Item.stepId);
                string restored, restoreCode = "";
                try { restored = AutomationStepJsonCodec.Restore(Invoke(_step.Restore), out restoreCode); }
                catch (Exception ex)
                {
                    restoreCode = ex is FormatException ? "STEP_RESTORE_INVALID" : "STEP_RESTORE_EXCEPTION";
                    RecordError(restoreCode, ex.Message);
                    restored = "Unsupported";
                }
                if (restored != "Restored")
                {
                    if (string.IsNullOrEmpty(restoreCode)) restoreCode = "STEP_RESTORE_UNSUPPORTED";
                    record.error = RecordError(restoreCode, "Step cannot prove recovery. Execute was not replayed.");
                    if (restored == "Failed") ReadError(record.error);
                    _run.recoveryRequired = true; _run.cleanupPending = true;
                    record.outcome = AutomationStepStatus.Failed;
                    CompleteCurrent();
                }
                else Save();
            }
            catch (Exception ex) { Recovery("STEP_STORE_RECOVERY_FAILED", ex.Message); }
        }

        public void RequestCancel()
        {
            if (_run == null || _run.terminal || _run.status == "RecoveryRequired") return;
            _run.cancelRequested = true;
            RecordError("STEP_CANCELED", "Operation cancellation requested.");
            Save();
        }

        internal void Tick()
        {
            if (_ticking || _storageFailed || _run == null || _run.terminal || _run.status == "RecoveryRequired") return;
            _ticking = true;
            try
            {
                if (_run.stage == "Finalizing") { FinalizeEvidence(); return; }
                if (_run.cursor >= _run.steps.Count) { _run.stage = "Finalizing"; Save(); return; }
                var record = Current;
                if (record.stage == "ResourceCleaning") { CompleteCurrent(); return; }
                if (record.stage == "Completed") { Advance(); return; }
                if (record.stage == "Pending")
                {
                    if (HasError && Item.phase == "Normal")
                    {
                        record.outcome = AutomationStepStatus.Skipped; record.stage = "Completed";
                        record.finishedAtUtcMs = _clock(); Advance(); return;
                    }
                    try { _step = _registry.Create(Item.stepId); }
                    catch (Exception ex) { RecordError("STEP_CONSTRUCTION_FAILED", ex.GetBaseException().Message); record.outcome = AutomationStepStatus.Failed; record.stage = "Completed"; Advance(); return; }
                    record.stage = "Executing"; record.startedAtUtcMs = _clock();
                    Save(); // Durable intent precedes the only Execute call.
                    _report.Append(new AutomationReportEvent
                    { eventType = "step.execute_intent", phaseId = Item.phase, caseId = Item.instanceId });
                    try { Invoke((run, instance, context, arguments) => { _step.Execute(run, instance, context, arguments); return ""; }); }
                    catch (Exception ex) { FailCurrent(AutomationStepStatus.Failed,
                        ex is AutomationStepException stepError ? stepError.Code : "STEP_EXECUTE_EXCEPTION", ex.GetBaseException().Message); }
                    return;
                }
                if (record.stage == "Executing")
                {
                    if (_run.cancelRequested && Item.phase == "Normal")
                    { FailCurrent(AutomationStepStatus.Canceled, "STEP_CANCELED", "Cancellation requested."); return; }
                    if (_clock() - record.startedAtUtcMs >= Item.timeoutSeconds * 1000)
                    { FailCurrent(AutomationStepStatus.TimedOut, "STEP_TIMEOUT", "Step execution deadline exceeded."); return; }
                    if (_clock() < _nextPoll) return;
                    _nextPoll = _clock() + (long)(Item.pollIntervalSeconds * 1000);
                    AutomationStepResult result;
                    try { result = AutomationStepJsonCodec.Result(Invoke(_step.Poll)); }
                    catch (FormatException ex) { FailCurrent(AutomationStepStatus.Failed, "STEP_RESULT_INVALID", ex.Message); return; }
                    catch (Exception ex) { FailCurrent(AutomationStepStatus.Failed,
                        ex is AutomationStepException stepError ? stepError.Code : "STEP_POLL_EXCEPTION", ex.GetBaseException().Message); return; }
                    if (!ValidResult(result))
                    { FailCurrent(AutomationStepStatus.Failed, "STEP_RESULT_INVALID", "Invalid Poll result."); return; }
                    if (result.status == AutomationStepStatus.Running) return;
                    if (result.status == AutomationStepStatus.Succeeded || result.status == AutomationStepStatus.SucceededWithWarnings
                        || (result.status == AutomationStepStatus.Skipped && Item.allowSkipped))
                    {
                        record.outcome = result.status; BeginCleanup(); return;
                    }
                    string code = result.status == AutomationStepStatus.Skipped ? "STEP_SKIP_NOT_ALLOWED" : result.errorCode;
                    if (string.IsNullOrWhiteSpace(code)) code = "STEP_ERROR_CODE_REQUIRED";
                    FailCurrent(result.status == AutomationStepStatus.Skipped ? AutomationStepStatus.Failed : result.status,
                        code, code, readError: true);
                    return;
                }
                if (record.stage == "Cleaning")
                {
                    if (_clock() - record.cleanupStartedAtUtcMs >= Item.cleanupTimeoutSeconds * 1000)
                    { CleanupFailed("STEP_CLEANUP_TIMEOUT", "Cleanup deadline exceeded."); return; }
                    if (_clock() < _nextPoll) return;
                    _nextPoll = _clock() + (long)(Item.pollIntervalSeconds * 1000);
                    AutomationStepResult result;
                    try { result = AutomationStepJsonCodec.Result(Invoke(_step.Cleanup), true); }
                    catch (FormatException ex) { CleanupFailed("STEP_CLEANUP_RESULT_INVALID", ex.Message); return; }
                    catch (Exception ex) { CleanupFailed("STEP_CLEANUP_EXCEPTION", ex.GetBaseException().Message); return; }
                    if (result?.status == AutomationStepStatus.Running) return;
                    if (result == null || (result.status != AutomationStepStatus.Succeeded && result.status != AutomationStepStatus.SucceededWithWarnings))
                    { CleanupFailed(result?.errorCode ?? "STEP_CLEANUP_FAILED", "Cleanup did not confirm resource release.", true); return; }
                    if (result.status == AutomationStepStatus.SucceededWithWarnings && Current.outcome == AutomationStepStatus.Succeeded)
                        Current.outcome = AutomationStepStatus.SucceededWithWarnings;
                    CompleteCurrent();
                }
            }
            catch (Exception ex) { Recovery("STEP_EXECUTOR_EXCEPTION", ex.GetBaseException().Message); }
            finally { _ticking = false; }
        }
        private AutomationStepRecord Current => _run.steps[_run.cursor];
        private AutomationStepItem Item => _run.plan.steps[_run.cursor];
        private string Invoke(Func<string, string, string, string, string> callback, bool writable = true)
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                throw new InvalidOperationException("STEP_MAIN_THREAD_REQUIRED");
            _writeFailure = null;
            using (UPilotAutomationStepService.EnterCallback(this, writable))
            {
                string result = callback(_run.runId, Item.instanceId, AutomationStepContextJson.Create(_run, _run.cursor), Item.arguments);
                if (_writeFailure != null) throw _writeFailure;
                return result;
            }
        }
        internal void SaveCheckpoint(string runId, string instanceId, string checkpointJson) => WriteCheckpoint(runId, instanceId, () =>
        {
            AutomationStepJsonCodec.ParseObject(checkpointJson);
            Current.checkpointJson = checkpointJson;
        });
        internal void StartRunCapture(string runId, string instanceId)
        {
            WriteCheckpoint(runId, instanceId, () =>
            {
                if (!_run.captureOwned || _run.captureStartIntent || _run.cursor != 0)
                    throw new InvalidOperationException("STEP_CAPTURE_OWNERSHIP_CONFLICT");
                _run.captureStartIntent = true;
            });
            _ownedCapture = new AutomationRunCapture(_captureStorePath, runId, false);
            _ownedCapture.Start();
            _run.captureSessionId = _ownedCapture.SessionId;
            _evidence = AutomationEvidenceSession.Borrow(_run.captureSessionId);
            _run.captureStartSequence = _evidence.Boundary();
            _run.intervalStartSequence = _run.captureStartSequence;
            Save();
        }
        internal void ObserveRunCapture(string runId, string instanceId) =>
            WriteCheckpoint(runId, instanceId, () =>
            {
                if (_ownedCapture == null || !_ownedCapture.Observe().active)
                    throw new InvalidOperationException("STEP_CAPTURE_START_UNCONFIRMED");
            });
        private AutomationSnapshotEvidence Snapshots => new(_run, Save, _clock);
        internal string Snapshot(string runId, string instanceId, string evidenceKey, string arguments, bool cancel)
        {
            string result = "";
            WriteCheckpoint(runId, instanceId, () =>
            {
                result = arguments == null ? Snapshots.Poll(instanceId, evidenceKey, cancel)
                    : Snapshots.Begin(instanceId, evidenceKey, arguments);
            }, "STEP_SNAPSHOT_SAVE_FAILED");
            return result;
        }
        internal string SnapshotError(string runId, string instanceId, string evidenceKey)
        {
            if (_run == null || _run.runId != runId || _run.cursor >= _run.steps.Count || Item.instanceId != instanceId)
                throw new InvalidOperationException("STEP_SNAPSHOT_IDENTITY_INVALID");
            return Snapshots.Error(instanceId, evidenceKey);
        }
        internal void SaveSharedValue(string runId, string instanceId, string key, string valueJson) => WriteCheckpoint(runId, instanceId, () =>
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Shared key must be nonempty.");
            var value = AutomationStepJsonCodec.ParseJson(valueJson);
            var shared = AutomationStepJsonCodec.ParseObject(_run.sharedCheckpointJson);
            AutomationStepJsonCodec.SetProperty(shared, key, value);
            _run.sharedCheckpointJson = AutomationStepJsonCodec.WriteJson(shared);
        });
        internal void RegisterArtifact(string runId, string instanceId, string kind, string path) =>
            WriteCheckpoint(runId, instanceId, () =>
            {
                if (_run.status == "RecoveryRequired" || _run.stage == "Finalizing" || _report == null || _report.IsComplete)
                    throw new InvalidOperationException("STEP_ARTIFACT_REGISTRATION_CLOSED");
                if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("Artifact kind is required.");
                var artifact = AutomationReportWriter.GetArtifactMetadata(kind, path);
                artifact.instanceId = instanceId;
                string reportPath = Path.GetFullPath(_run.reportDirectory);
                if (SamePath(artifact.path, Path.Combine(reportPath, "events.jsonl"))
                    || SamePath(artifact.path, Path.Combine(reportPath, "summary.json"))
                    || SamePath(artifact.path, Path.Combine(reportPath, AutomationReportWriter.TextReportFileName))
                    || SamePath(artifact.path, Path.Combine(reportPath, AutomationReportWriter.TimingFileName))
                    || SamePath(artifact.path, Path.Combine(reportPath, AutomationReportWriter.ConsolePolicyFileName)))
                    throw new InvalidOperationException("Step cannot register the executor's own report files.");
                _run.registeredArtifacts ??= new();
                var existing = _run.registeredArtifacts.FirstOrDefault(a => SamePath(a.path, artifact.path));
                if (existing != null)
                {
                    if (existing.kind != kind || existing.instanceId != instanceId
                        || existing.bytes != artifact.bytes || existing.sha256 != artifact.sha256)
                        throw new InvalidOperationException("Artifact registration conflicts with the saved owner or content.");
                    return;
                }
                _run.registeredArtifacts.Add(artifact);
            }, "STEP_ARTIFACT_REGISTER_FAILED");

        private static bool SamePath(string left, string right) => string.Equals(left, right,
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        private void WriteCheckpoint(string runId, string instanceId, Action write, string failureCode = "STEP_CHECKPOINT_SAVE_FAILED")
        {
            try
            {
                if (_storageFailed || _run == null || _run.terminal || _run.cursor >= _run.steps.Count
                    || _run.runId != runId || Item.instanceId != instanceId
                    || System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                    throw new InvalidOperationException("STEP_CHECKPOINT_IDENTITY_MISMATCH");
                write();
                Save();
            }
            catch (Exception ex)
            {
                _writeFailure = new AutomationStepException(failureCode, ex.Message);
                throw _writeFailure;
            }
        }
        private static bool ValidResult(AutomationStepResult result) => result != null
            && Enum.IsDefined(typeof(AutomationStepStatus), result.status) && result.status != AutomationStepStatus.TimedOut;
        private void BeginCleanup()
        { Current.stage = "Cleaning"; Current.cleanupStartedAtUtcMs = _clock(); _nextPoll = 0; Save(); }
        private void FailCurrent(AutomationStepStatus status, string code, string message, string diagnostic = "", bool readError = false)
        {
            Current.outcome = status;
            Current.error = RecordError(code, message, diagnostic);
            Save(); // Freeze the machine-readable failure before calling project diagnostic code.
            if (readError) ReadError(Current.error);
            BeginCleanup();
            if (!Current.cancelSent)
            {
                Current.cancelSent = true; Save();
                try { Invoke((run, instance, context, arguments) => { _step.Cancel(run, instance, context, arguments); return ""; }); }
                catch (Exception ex)
                {
                    RecordError("STEP_CANCEL_EXCEPTION", ex.GetBaseException().Message);
                    // Cleanup can still authoritatively establish release.
                }
            }
        }
        private void ReadError(AutomationStepError frozen)
        {
            try
            {
                var error = AutomationStepJsonCodec.Error(Invoke((run, instance, context, arguments) =>
                    _step.GetError(run, instance, context, frozen.code, arguments), false), frozen.code);
                frozen.message = error.message; frozen.diagnostic = error.diagnostic;
            }
            catch (Exception ex) { RecordError(ex is FormatException ? "STEP_ERROR_INVALID" : "STEP_ERROR_EXCEPTION", ex.Message); }
            Save();
        }
        private void CleanupFailed(string code, string message, bool readError = false)
        {
            var error = RecordError(string.IsNullOrWhiteSpace(code) ? "STEP_CLEANUP_FAILED" : code, message);
            Save();
            if (readError) ReadError(error);
            if (string.IsNullOrEmpty(Current.error?.code))
            { Current.error = error; Current.outcome = AutomationStepStatus.Failed; }
            _run.recoveryRequired = true; _run.cleanupPending = true;
            CompleteCurrent();
        }
        private void CompleteCurrent()
        {
            if (!Snapshots.CompleteItem(Item.instanceId))
            { Current.stage = "ResourceCleaning"; Save(); return; }
            foreach (var snapshot in _run.snapshots.Where(s => s.instanceId == Item.instanceId))
            {
                if (snapshot.status == "Failed")
                {
                    var error = RecordError(snapshot.errorCode, snapshot.message);
                    if (string.IsNullOrEmpty(Current.error?.code)) Current.error = error;
                    Current.outcome = AutomationStepStatus.Failed;
                }
                else if (snapshot.status == "SucceededWithWarnings" && Current.outcome == AutomationStepStatus.Succeeded)
                    Current.outcome = AutomationStepStatus.SucceededWithWarnings;
                if (snapshot.unresolved) { _run.recoveryRequired = true; _run.cleanupPending = true; }
            }
            Current.stage = "Completed"; Current.finishedAtUtcMs = _clock();
            if (_evidence != null)
            {
                try
                {
                    long boundary = _evidence.Boundary();
                    AutomationEvidenceSession.AddInterval(_run.intervals, Item.phase, Item.instanceId, _run.intervalStartSequence, boundary);
                    _run.intervalStartSequence = boundary;
                }
                catch (Exception ex) { RecordError("STEP_CAPTURE_BOUNDARY_FAILED", ex.Message); }
            }
            _report.Append(new AutomationReportEvent
            { eventType = "step.completed", phaseId = Item.phase, caseId = Item.instanceId, outcome = Current.outcome.ToString(), detail = Current.error?.message });
            Advance();
        }
        private void Advance()
        {
            _run.cursor++; _step = null; _nextPoll = 0;
            if (_run.cursor >= _run.steps.Count) _run.stage = "Finalizing";
            Save();
        }
        private void FinalizeEvidence()
        {
            if (!string.IsNullOrEmpty(_run.reportCommitJson)) { CommitReport(); return; }
            AutomationLogPolicyResult policy = null;
            bool captureReady = true;
            if (_run.captureOwned && _run.captureStartIntent && !_run.captureStopVerified)
            {
                try
                {
                    if (_ownedCapture == null) throw new InvalidDataException("STEP_CAPTURE_OWNERSHIP_INVALID");
                    if (!_ownedCapture.StopAndVerify(_clock(), out var manifest, out _, artifacts =>
                    {
                        foreach (var artifact in artifacts)
                            if (!_run.registeredArtifacts.Any(a => SamePath(a.path, artifact.path)))
                                _run.registeredArtifacts.Add(artifact);
                        Save();
                    })) return;
                    _run.captureStopVerified = true;
                    _run.captureEndSequence = manifest.nextSequence;
                    Save();
                }
                catch (Exception ex)
                {
                    RecordError("STEP_CAPTURE_RELEASE_UNCONFIRMED", ex.Message);
                    _run.recoveryRequired = true; _run.cleanupPending = true; captureReady = false;
                }
            }
            if (captureReady && (_evidence != null || _run.captureStopVerified))
            {
                if (_run.captureEndSequence < 0)
                { _run.captureEndSequence = _evidence.Boundary(); Save(); }
                _collector ??= new AutomationConsoleCollector(_run.captureSessionId, _run.captureStartSequence, _run.captureEndSequence);
                _collector.Poll();
                if (!_collector.Complete) return;
                policy = AutomationLogPolicy.Evaluate(_collector.Records, _run.intervals, _run.plan.logPolicy, _collector.Evidence);
                _run.logSummary = _report.WriteConsolePolicy(_run.captureSessionId, _run.captureStartSequence,
                    _run.captureEndSequence, policy, _collector.Evidence.lostRecordCount);
                if (!policy.passed) RecordError(policy.evidenceComplete ? "STEP_CONSOLE_POLICY_FAILED" : "STEP_EVIDENCE_INCOMPLETE",
                    "Console policy or evidence completeness failed.");
            }
            var references = new System.Collections.Generic.List<AutomationReportArtifactReference>();
            foreach (var artifact in _report.GetArtifacts().Where(a => a.kind == "consolePolicy"))
                references.Add(new AutomationReportArtifactReference
                { kind = artifact.kind, path = artifact.projectRelativePath, bytes = artifact.bytes, sha256 = artifact.sha256 });
            foreach (var artifact in _run.registeredArtifacts ?? new())
            {
                var reference = new AutomationReportArtifactReference
                { kind = artifact.kind, instanceId = artifact.instanceId, path = artifact.projectRelativePath,
                    bytes = artifact.bytes, sha256 = artifact.sha256 };
                try
                {
                    var actual = AutomationReportWriter.GetArtifactMetadata(artifact.kind, artifact.path);
                    if (actual.bytes != artifact.bytes || actual.sha256 != artifact.sha256)
                        throw new IOException("Registered artifact content changed.");
                }
                catch (Exception ex)
                {
                    reference.diagnostic = ex.Message;
                    RecordError("STEP_ARTIFACT_VERIFICATION_FAILED", artifact.projectRelativePath + ": " + ex.Message);
                }
                references.Add(reference);
            }
            if (_evidence != null && !_run.captureOwned) references.Add(new AutomationReportArtifactReference
            {
                kind = "consoleCapture", path = UPilotConsoleCaptureApi.Status(_run.captureSessionId).session?.manifestPath,
                external = true, diagnostic = "sessionId=" + _run.captureSessionId + "; range=["
                    + _run.captureStartSequence + "," + _run.captureEndSequence
                    + "); Operation owns stop and final raw artifact hashes."
            });
            _run.status = _run.recoveryRequired ? "RecoveryRequired" : !HasError
                ? (_run.steps.Any(s => s.outcome == AutomationStepStatus.Skipped || s.outcome == AutomationStepStatus.SucceededWithWarnings)
                    ? "SucceededWithWarnings" : "Succeeded")
                : _run.error.code == "STEP_CANCELED" ? "Canceled" : _run.error.code == "STEP_TIMEOUT" ? "TimedOut" : "Failed";
            _run.finishedAtUtcMs = _clock();
            _run.reportCommitJson = JsonUtility.ToJson(new AutomationReportSummary
            {
                runId = _run.runId, startedAtUtcMs = _run.startedAtUtcMs,
                outcome = _run.status, failureSignature = _run.error?.code, detail = _run.error?.message,
                finishedAtUtcMs = _run.finishedAtUtcMs,
                cases = _run.steps.Select((s, index) => new AutomationReportCase
                {
                    id = s.instanceId, stepId = _run.plan.steps[index].stepId, phaseId = _run.plan.steps[index].phase,
                    stage = s.stage, startedAtUtcMs = s.startedAtUtcMs, cleanupStartedAtUtcMs = s.cleanupStartedAtUtcMs,
                    finishedAtUtcMs = s.finishedAtUtcMs, outcome = s.outcome.ToString(),
                    errorCode = s.error?.code, detail = s.error?.message
                }).ToArray(),
                logSummary = _run.logSummary,
                artifacts = references.ToArray(),
            });
            Save(); // Freeze the exact export content and timestamp before creating any terminal files.
            CommitReport();
        }
        private void CommitReport()
        {
            var summary = JsonUtility.FromJson<AutomationReportSummary>(_run.reportCommitJson);
            if (summary == null || summary.runId != _run.runId || summary.outcome != _run.status
                || summary.startedAtUtcMs != _run.startedAtUtcMs || summary.finishedAtUtcMs != _run.finishedAtUtcMs
                || summary.cases == null || summary.cases.Length != _run.steps.Count)
                throw new InvalidDataException("STEP_REPORT_COMMIT_INVALID");
            foreach (var reference in summary.artifacts ?? Array.Empty<AutomationReportArtifactReference>())
            {
                // Known-invalid evidence is already reported as a failure in the frozen summary.
                if (reference.external || !string.IsNullOrEmpty(reference.diagnostic)) continue;
                try
                {
                    var actual = AutomationReportWriter.GetArtifactMetadata(reference.kind, reference.path);
                    if (actual.bytes != reference.bytes || actual.sha256 != reference.sha256)
                        throw new IOException("Frozen artifact content changed.");
                }
                catch (Exception ex)
                {
                    Recovery("STEP_ARTIFACT_VERIFICATION_FAILED", reference.path + ": " + ex.Message);
                    return;
                }
            }
            _report.Complete(summary);
            _run.artifacts = _report.GetArtifacts().Concat(_run.registeredArtifacts ?? new()).ToArray();
            _run.stage = "Completed"; _run.terminal = !_run.recoveryRequired;
            _collector?.Dispose(); _collector = null; Save();
        }
        private AutomationStepError RecordError(string code, string message, string diagnostic = "")
        {
            var error = new AutomationStepError { code = code, message = message, diagnostic = diagnostic };
            if (!HasError) _run.error = error;
            else _run.secondaryErrors.Add(error);
            return error;
        }
        // JsonUtility materializes null serializable objects during reload; code is the presence marker.
        private bool HasError => !string.IsNullOrEmpty(_run.error?.code);
        private void Recovery(string code, string message)
        {
            if (_run == null) { _storageFailed = true; return; }
            RecordError(code, message); _run.status = "RecoveryRequired"; _run.recoveryRequired = true;
            _run.cleanupPending = true; _run.terminal = false;
            try { Save(); } catch { _storageFailed = true; }
        }
        private void Save()
        {
            try { _store.Save(_run); }
            catch { _storageFailed = true; throw; }
        }
        public void Dispose() { _collector?.Dispose(); }
    }
}
