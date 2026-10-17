using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    public sealed partial class AutomationStepExecutor
    {
        [Serializable] private sealed class CompilationDiagnostic
        {
            public string operationId, instanceId, phase, businessCleanup, resourceError;
            public long compilationStartedAtUtcMs;
            public AutomationStepError[] secondaryErrors;
        }
        private string CompilationDiagnosticJson() => JsonUtility.ToJson(new CompilationDiagnostic
        {
            operationId = _run.operationId, instanceId = _run.compilationInstanceId, phase = _run.compilationPhase,
            compilationStartedAtUtcMs = _run.compilationStartedAtUtcMs,
            businessCleanup = "SkippedByCompilation", resourceError = _run.resourceRecoveryError,
            secondaryErrors = _run.secondaryErrors.ToArray(),
        });
        internal const long ImmediateStopReleaseTimeoutMs = 10000;
        private long ForcedStopRequestedAtUtcMs => _run.compilationStartedAtUtcMs > 0
            ? _run.compilationStartedAtUtcMs : _run.immediateStopRequestedAtUtcMs;
        internal bool ReleasingImmediateStopResources => _run != null && _run.terminal
            && ForcedStopRequestedAtUtcMs > 0 && _run.stage == "ResourceReleasing" && _run.cleanupPending;

        /// <summary>真实脚本编译冻结原任务；仅框架资源释放可继续，不调用业务恢复。</summary>
        internal bool InterruptForCompilation()
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                throw new InvalidOperationException("STEP_MAIN_THREAD_REQUIRED");
            if (_run == null || _run.terminal) return false;
            long now = _clock();
            _run.compilationStartedAtUtcMs = now;
            _run.compilationPhase = _run.stage + (_run.cursor < _run.steps.Count ? "/" + Item.phase + "/" + Current.stage : "");
            _run.compilationInstanceId = _run.cursor < _run.steps.Count ? Item.instanceId : "";
            if (HasError) _run.secondaryErrors.Add(_run.error);
            _run.error = new AutomationStepError
            {
                code = "STEP_COMPILATION_STARTED", message = "任务执行期间触发 Unity 脚本编译，任务已强制终止。",
                diagnostic = "stage=" + _run.compilationPhase + "; compilationStartedAtUtcMs=" + now,
            };
            _run.status = "Failed"; _run.terminal = true; _run.recoveryRequired = false;
            _run.finishedAtUtcMs = now; _run.reportCommitJson = ""; _run.compilationReportState = "Pending";
            foreach (var record in _run.steps.Where(s => s.stage != "Completed"))
            {
                record.outcome = record.startedAtUtcMs > 0 ? AutomationStepStatus.Failed : AutomationStepStatus.Skipped;
                if (record.startedAtUtcMs > 0) record.error = _run.error;
                if (record.cleanupState != "Verified") record.cleanupState = "SkippedByCompilation";
                record.stage = "Completed"; record.finishedAtUtcMs = now;
            }
            _step = null;
            // Even a resource-free interruption needs one framework-only turn after an active callback returns.
            _run.cleanupPending = true;
            _run.stage = "ResourceReleasing";
            // The session fence is written before diagnostics, Editor resource calls and any archive writes.
            _store.FenceCompilation(_run);
            Trace("run.compilation_started", "编译中断任务", () => _run.error.diagnostic, now);
            ReportStepError(_run.error);
            try
            {
                _report?.Append(new AutomationReportEvent { eventType = "run.compilation_started", caseId = _run.compilationInstanceId,
                    phaseId = _run.compilationPhase, timestampUtcMs = now, outcome = "Failed", detail = CompilationDiagnosticJson() });
            }
            catch (Exception ex) { RecordImmediateStopResourceError("STEP_REPORT_EVENT_FAILED", ex.Message, ex.ToString()); }
            PersistImmediateStop();
            try { _collector?.Dispose(); }
            catch (Exception ex) { RecordImmediateStopResourceError("STEP_DISPOSE_FAILED", ex.Message, ex.ToString()); }
            _collector = null; _evidence = null;
            if (!_ticking)
            {
                _ticking = true;
                try
                {
                    if (ReleasingImmediateStopResources) TickImmediateStopRelease();
                    else CompleteCompilationInterruption();
                }
                finally { _ticking = false; }
            }
            return true;
        }

        /// <summary>Freeze business execution once. Only executor-owned resource release may follow.</summary>
        internal bool StopImmediately()
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                throw new InvalidOperationException("STEP_MAIN_THREAD_REQUIRED");
            if (_run == null || _run.terminal) return false;
            try { FinalizeGizmos(); }
            catch (Exception ex) { RecordError("STEP_STORE_FAILED", ex.Message, ex.ToString()); }
            if (_run.terminal) return false;
            var now = _clock();
            Trace("run.stop_requested", "立即停止任务", at: now);
            RecordError("STEP_STOPPED_IMMEDIATELY", "Immediately stopped; remaining business callbacks and Finally were skipped.");
            if (_run.terminal) return false;
            _run.immediateStopRequestedAtUtcMs = now;
            _run.finishedAtUtcMs = now;
            _run.status = "Aborted";
            _run.terminal = true;
            _run.recoveryRequired = false;
            _run.reportCommitJson = ""; // Never resume a frozen success/report commit.
            foreach (var record in _run.steps.Where(s => s.stage != "Completed"))
            {
                record.outcome = record.startedAtUtcMs > 0 ? AutomationStepStatus.Canceled : AutomationStepStatus.Skipped;
                if (record.cleanupState != "Verified") record.cleanupState = "SkippedByImmediateStop";
                record.stage = "Completed";
                record.finishedAtUtcMs = now;
            }
            _step = null;
            _run.cleanupPending = HasImmediateStopResources;
            _run.stage = _run.cleanupPending ? "ResourceReleasing" : "Completed";
            foreach (var record in _run.steps.Where(s => s.cleanupState == "SkippedByImmediateStop"))
                TraceStepFinished(_run.steps.IndexOf(record), now, record.startedAtUtcMs > 0 ? "Canceled" : "未执行");
            if (_run.cleanupPending) Trace("run.resources_started", "任务框架资源释放开始", at: now);
            PersistImmediateStop(); // Failure to archive never reopens business execution.
            try { _collector?.Dispose(); }
            catch (Exception ex) { RecordImmediateStopResourceError("STEP_DISPOSE_FAILED", ex.Message, ex.ToString()); }
            _collector = null;
            _evidence = null;
            // A callback may reenter the Editor. Wait until its stack unwinds before releasing resources.
            if (ReleasingImmediateStopResources && !_ticking)
            {
                _ticking = true;
                try { TickImmediateStopRelease(); }
                finally { _ticking = false; }
            }
            else PersistImmediateStop();
            if (!ReleasingImmediateStopResources && !_ticking) FinishExecutionLog();
            return true;
        }

        private bool HasImmediateStopResources => (_run.captureOwned && _run.captureStartIntent && !_run.captureStopVerified)
            || (_run.snapshots?.Any(NeedsImmediateStopRelease) ?? false);
        private bool HasUnverifiedForcedStopResources => HasImmediateStopResources
            || (_run.compilationStartedAtUtcMs > 0 && _run.gizmosPolicyVersion == 1 && _run.gizmosState?.finalCloseVerified != true);
        private static bool NeedsImmediateStopRelease(AutomationSnapshotRecord record) =>
            record != null && (record.status == "Running" || record.unresolved);

        private void TickImmediateStopRelease()
        {
            if (!ReleasingImmediateStopResources) return;
            // One fixed deadline, persisted through the original stop timestamp. Never renew it.
            if (_clock() - ForcedStopRequestedAtUtcMs >= ImmediateStopReleaseTimeoutMs)
            {
                RecordImmediateStopResourceError(_run.compilationStartedAtUtcMs > 0 ? "STEP_COMPILATION_RELEASE_TIMEOUT" : "STEP_IMMEDIATE_STOP_RELEASE_TIMEOUT",
                    "Immediate-stop resource release was not verified within 10 seconds; inspect the original resources.");
                CompleteImmediateStopRelease();
                return;
            }
            if (_run.compilationStartedAtUtcMs > 0)
            {
                try { FinalizeGizmos(); }
                catch (Exception ex) { RecordImmediateStopResourceError("STEP_GIZMOS_RELEASE_UNCONFIRMED", ex.Message, ex.ToString()); }
            }
            ReleaseOwnedResources();
            if (!HasImmediateStopResources)
            {
                // Historical errors remain; do not label a subsequently verified release unverified.
                if (_run.resourceRecoveryError.StartsWith("STEP_CAPTURE_RELEASE_UNCONFIRMED:", StringComparison.Ordinal)
                    || _run.resourceRecoveryError.StartsWith("STEP_SNAPSHOT_RELEASE_UNCONFIRMED:", StringComparison.Ordinal))
                    _run.resourceRecoveryError = "";
                CompleteImmediateStopRelease();
            }
            else PersistImmediateStop();
        }

        private void ReleaseOwnedResources(Func<bool> withinDeadline = null)
        {
            if (withinDeadline != null && !withinDeadline()) return;
            if (_run.captureOwned && _run.captureStartIntent && !_run.captureStopVerified)
            {
                try
                {
                    _ownedCapture ??= new AutomationRunCapture(_captureStorePath, _run.runId, true);
                    if (string.IsNullOrEmpty(_run.captureSessionId) && !string.IsNullOrEmpty(_ownedCapture.SessionId))
                        _run.captureSessionId = _ownedCapture.Observe().sessionId;
                    if (string.IsNullOrEmpty(_run.captureSessionId) || _ownedCapture.SessionId != _run.captureSessionId)
                        throw new InvalidOperationException("STEP_CAPTURE_IDENTITY_INVALID");
                    if (_ownedCapture.StopAndVerify(_clock(), out _, out _, AddImmediateStopArtifacts, withinDeadline))
                        _run.captureStopVerified = true;
                }
                catch (Exception ex) { RecordImmediateStopResourceError("STEP_CAPTURE_RELEASE_UNCONFIRMED", ex.Message, ex.ToString()); }
            }
            // Persisting the native cancellation intent must succeed before dispatch. Do not use Save(),
            // whose ordinary failure path resets terminal/cleanup fields.
            var snapshots = new AutomationSnapshotEvidence(_run, () => _store.Save(_run), _clock);
            foreach (var record in _run.snapshots.Where(NeedsImmediateStopRelease).ToArray())
            {
                if (withinDeadline != null && !withinDeadline()) break;
                try { snapshots.StopAndObserveRelease(record, withinDeadline: withinDeadline); }
                catch (Exception ex)
                {
                    record.unresolved = true;
                    RecordImmediateStopResourceError("STEP_SNAPSHOT_RELEASE_UNCONFIRMED", record.snapshotId + ": " + ex.Message, ex.ToString());
                }
            }
        }

        private void AddImmediateStopArtifacts(AutomationReportArtifact[] artifacts)
        {
            foreach (var artifact in artifacts)
                if (!_run.registeredArtifacts.Any(a => SamePath(a.path, artifact.path)))
                    _run.registeredArtifacts.Add(artifact);
        }

        private void CompleteImmediateStopRelease()
        {
            // Ending the bounded release attempt is NOT proof that all resources were released.
            _run.cleanupPending = false;
            _run.stage = "Completed";
            if (_run.compilationStartedAtUtcMs > 0) CompleteCompilationInterruption();
            PersistImmediateStop();
            Trace("run.resources_finished", "任务框架资源释放结束", () => "状态="
                + (HasUnverifiedForcedStopResources ? "未验证" : "已验证") + " 详情=" + AutomationStepExecutionLog.Quote(_run.resourceRecoveryError));
            FinishExecutionLog(HasUnverifiedForcedStopResources ? "未验证" : "已验证");
        }

        private void RecordImmediateStopResourceError(string code, string message, string diagnostic = "")
        {
            _run.resourceRecoveryError = code + ": " + message;
            if (_run.error?.code != code && !_run.secondaryErrors.Any(e => e.code == code && e.message == message))
                RecordError(code, message, diagnostic);
        }

        private void PersistImmediateStop()
        {
            try
            {
                if (_run.compilationStartedAtUtcMs > 0) _store.FenceCompilation(_run);
                _store.Save(_run);
                if (_run.compilationStartedAtUtcMs > 0 && !_run.cleanupPending
                    && (_run.compilationReportState == "Written" || _run.compilationReportState == "Failed"))
                    _store.ClearCompilationFence(_run);
            }
            catch (Exception ex)
            {
                RecordImmediateStopResourceError("STEP_STORE_FAILED", ex.Message, ex.ToString());
            }
        }

        private void CompleteCompilationInterruption()
        {
            if (_run.compilationReportState == "Pending")
            {
                try
                {
                    _report ??= Directory.Exists(_run.reportDirectory)
                        ? AutomationReportWriter.OpenExisting(_run.reportDirectory, _run.runId)
                        : AutomationReportWriter.Create(new AutomationReportCreateRequest
                        { runId = _run.runId, outputDirectory = _run.reportDirectory, startedAtUtcMs = _run.startedAtUtcMs });
                    // Preserve immutable or partially published exports from the interrupted normal commit.
                    if (string.IsNullOrEmpty(_run.reportCommitJson)
                        && (_report.IsComplete || File.Exists(Path.Combine(_run.reportDirectory, AutomationReportWriter.TextReportFileName))
                            || File.Exists(Path.Combine(_run.reportDirectory, AutomationReportWriter.TimingFileName))))
                    {
                        string previous = _run.reportDirectory;
                        _run.reportDirectory = Path.Combine(previous, "compilation-interruption");
                        _report = Directory.Exists(_run.reportDirectory)
                            ? AutomationReportWriter.OpenExisting(_run.reportDirectory, _run.runId)
                            : AutomationReportWriter.Create(new AutomationReportCreateRequest
                            { runId = _run.runId, outputDirectory = _run.reportDirectory, startedAtUtcMs = _run.startedAtUtcMs });
                        if (File.Exists(Path.Combine(previous, "events.jsonl")))
                            AddImmediateStopArtifacts(new[] { AutomationReportWriter.GetArtifactMetadata("interruptedEvents", Path.Combine(previous, "events.jsonl")) });
                    }
                    if (string.IsNullOrEmpty(_run.reportCommitJson))
                    {
                        _report.Append(new AutomationReportEvent { eventType = "run.compilation_terminated", outcome = "Failed",
                            caseId = _run.compilationInstanceId, phaseId = _run.compilationPhase, detail = CompilationDiagnosticJson() });
                        _run.reportCommitJson = JsonUtility.ToJson(new AutomationReportSummary
                        {
                            runId = _run.runId, startedAtUtcMs = _run.startedAtUtcMs, finishedAtUtcMs = _run.finishedAtUtcMs,
                            outcome = "Failed", failureSignature = _run.error.code, detail = _run.error.message,
                            gizmos = _run.gizmosState,
                            phases = new[]
                            {
                                new AutomationReportPhase { id = "businessCleanup", outcome = "SkippedByCompilation",
                                    detail = "保留已有验证结果；其余业务清理因编译跳过，未验证恢复。" },
                                new AutomationReportPhase { id = "frameworkRelease", outcome = HasUnverifiedForcedStopResources ? "Unverified" : "Verified",
                                    detail = _run.resourceRecoveryError },
                            },
                            cases = _run.steps.Select((s, i) => new AutomationReportCase
                            {
                                id = s.instanceId, stepId = _run.plan.steps[i].stepId, phaseId = _run.plan.steps[i].phase,
                                stage = s.stage, outcome = s.outcome.ToString(), errorCode = s.error?.code,
                                detail = s.error?.message, cleanupState = s.cleanupState,
                                startedAtUtcMs = s.startedAtUtcMs, cleanupStartedAtUtcMs = s.cleanupStartedAtUtcMs, finishedAtUtcMs = s.finishedAtUtcMs,
                            }).ToArray(),
                            artifacts = _run.registeredArtifacts.Select(a => new AutomationReportArtifactReference
                            { kind = a.kind, instanceId = a.instanceId, path = a.path, bytes = a.bytes, sha256 = a.sha256 }).ToArray(),
                        });
                        PersistImmediateStop(); // Freeze export bytes in the session fence even when disk saving fails.
                    }
                    _report.Complete(JsonUtility.FromJson<AutomationReportSummary>(_run.reportCommitJson));
                    _run.artifacts = _report.GetArtifacts().Concat(_run.registeredArtifacts).ToArray();
                    _run.compilationReportState = "Written";
                }
                catch (Exception ex)
                {
                    _run.compilationReportState = "Failed";
                    RecordImmediateStopResourceError("STEP_COMPILATION_REPORT_FAILED", ex.Message, ex.ToString());
                }
            }
            PersistImmediateStop();
            FinishExecutionLog("业务清理因编译跳过；仅处理框架资源");
        }
    }
}
