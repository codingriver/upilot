using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    public sealed partial class AutomationStepExecutor
    {
        internal bool ReleasingTerminalResources => ReleasingImmediateStopResources || ReleasingFinallyFailureResources;
        private bool ReleasingFinallyFailureResources => _run != null && _run.terminal
            && _run.finallyFailureAtUtcMs > 0 && _run.finallyCleanupDeadlineUtcMs > 0
            && !string.IsNullOrEmpty(_run.cleanupOutcome) && !string.IsNullOrEmpty(_run.reportOutcome)
            && _run.stage == "ResourceReleasing" && _run.cleanupPending;
        [Serializable] private sealed class FinallyFailureDiagnostic
        {
            public string triggerInstanceId, cleanupOutcome;
            public long cleanupDeadlineUtcMs;
            public string[] unresolvedResources;
            public AutomationStepError primaryError;
            public AutomationStepError[] secondaryErrors;
        }
        private string FinallyFailureDiagnosticJson() => JsonUtility.ToJson(new FinallyFailureDiagnostic
        {
            triggerInstanceId = Item.instanceId, cleanupOutcome = _run.cleanupOutcome,
            cleanupDeadlineUtcMs = _run.finallyCleanupDeadlineUtcMs, unresolvedResources = _run.unresolvedResources,
            primaryError = _run.error, secondaryErrors = _run.secondaryErrors.ToArray(),
        });

        private void FreezeFinallyFailure(AutomationStepStatus outcome, AutomationStepError error)
        {
            if (_run == null || _run.terminal) return;
            long now = _clock();
            bool cleaning = Current.stage == "Cleaning" || Current.stage == "ResourceCleaning";
            if (!cleaning) Current.cleanupStartedAtUtcMs = now;
            Current.outcome = outcome;
            if (string.IsNullOrEmpty(Current.error?.code) || Current.error.code == "STEP_CANCELED") Current.error = error;
            if (!cleaning || Current.cleanupState == "Pending" || Current.cleanupState == "NotStarted")
                Current.cleanupState = "SkippedByFinallyFailure";
            Current.stage = "Completed"; Current.finishedAtUtcMs = now;
            for (int i = _run.cursor + 1; i < _run.steps.Count; i++)
            {
                var record = _run.steps[i];
                if (record.stage == "Completed") continue;
                record.outcome = AutomationStepStatus.Skipped;
                record.error = new AutomationStepError { code = "STEP_SKIPPED_BY_FINALLY_FAILURE",
                    message = "Skipped because Finally instance '" + Item.instanceId + "' failed (" + error.code + ")." };
                record.cleanupState = "SkippedByFinallyFailure";
                record.stage = "Completed"; record.finishedAtUtcMs = now;
            }
            _run.finallyFailureAtUtcMs = now;
            _run.finallyCleanupDeadlineUtcMs = Current.cleanupStartedAtUtcMs + (long)(Item.cleanupTimeoutSeconds * 1000);
            _run.finishedAtUtcMs = now;
            _run.status = _run.steps.Any(s => s.outcome == AutomationStepStatus.TimedOut) ? "TimedOut" : "Failed";
            _run.terminal = true; _run.cleanupPending = true; _run.recoveryRequired = false;
            _run.stage = "ResourceReleasing"; _run.cleanupOutcome = "Pending"; _run.reportOutcome = "Pending";
            _run.unresolvedResources = UnresolvedFinallyResources();
            _run.reportCommitJson = "";
            _step = null;
            // Commit the same-session terminal fence before any diagnostic or resource callback can reenter.
            bool persisted = PersistFinallyFailure();
            ReportStepError(error);
            if (outcome == AutomationStepStatus.TimedOut) TraceStep("step.timed_out", "步骤执行超时", () => "错误码=" + AutomationStepExecutionLog.Quote(error.code));
            if (!cleaning && Current.startedAtUtcMs > 0) TraceExecutionFinished(now);
            else if (cleaning && Current.cleanupState != "Verified") TraceCleanupFinished(outcome.ToString());
            TraceStepFinished(_run.cursor, now);
            for (int i = _run.cursor + 1; i < _run.steps.Count; i++)
                TraceStepFinished(i, now, "未执行（Skipped，前序 Finally 失败）");
            try { _collector?.Dispose(); }
            catch (Exception ex) { RecordImmediateStopResourceError("STEP_DISPOSE_FAILED", ex.Message, ex.ToString()); }
            _collector = null; _evidence = null;
            Trace("run.finally_failed", "Finally 失败，业务执行已冻结", () => "框架收尾截止=" + _run.finallyCleanupDeadlineUtcMs, now);
            if (!persisted) { CompleteFinallyFailureRelease(); return; }
            TickFinallyFailureRelease();
        }

        private string[] UnresolvedFinallyResources()
        {
            var resources = new List<string>();
            if (_run.captureOwned && _run.captureStartIntent && !_run.captureStopVerified)
                resources.Add("capture:" + (string.IsNullOrEmpty(_run.captureSessionId) ? "unknown-owner:" + _run.runId : _run.captureSessionId));
            foreach (var snapshot in _run.snapshots.Where(NeedsImmediateStopRelease))
                resources.Add("snapshot:" + (string.IsNullOrEmpty(snapshot.snapshotId) ? snapshot.requestKey : snapshot.snapshotId));
            if (_run.gizmosPolicyVersion == 1 && _run.gizmosState?.finalCloseVerified != true)
                resources.Add("gizmos:final-close");
            return resources.Distinct().ToArray();
        }

        private void TickFinallyFailureRelease()
        {
            if (!ReleasingFinallyFailureResources) return;
            // A reload between the resource decision and report commit resumes only that commit.
            if (_run.cleanupOutcome != "Pending") { CompleteFinallyFailureRelease(); return; }
            // The current item's one cleanup deadline also bounds framework release. Never renew it.
            if (_clock() >= _run.finallyCleanupDeadlineUtcMs) { CompleteFinallyFailureRelease(); return; }
            try { FinalizeGizmos(() => _clock() < _run.finallyCleanupDeadlineUtcMs); }
            catch (Exception ex) { RecordImmediateStopResourceError("STEP_GIZMOS_RELEASE_UNCONFIRMED", ex.Message, ex.ToString()); }
            if (_clock() >= _run.finallyCleanupDeadlineUtcMs) { CompleteFinallyFailureRelease(); return; }
            ReleaseOwnedResources(() => _clock() < _run.finallyCleanupDeadlineUtcMs);
            _run.unresolvedResources = UnresolvedFinallyResources();
            if (_run.unresolvedResources.Length == 0 || _clock() >= _run.finallyCleanupDeadlineUtcMs)
                CompleteFinallyFailureRelease();
            else if (!PersistFinallyFailure()) CompleteFinallyFailureRelease();
        }

        private bool PersistFinallyFailure()
        {
            try
            {
                _store.FenceFinallyFailure(_run); // Reload cannot reopen business if the following disk save fails.
                _store.Save(_run);
                if (!_run.cleanupPending && (_run.reportOutcome == "Written" || _run.reportOutcome == "Failed"))
                    _store.ClearFinallyFailureFence(_run);
                return true;
            }
            catch (Exception ex)
            {
                RecordImmediateStopResourceError("STEP_STORE_FAILED", ex.Message, ex.ToString());
                return false;
            }
        }

        private void CompleteFinallyFailureRelease()
        {
            _run.unresolvedResources = UnresolvedFinallyResources();
            bool required = _run.captureOwned && _run.captureStartIntent || _run.snapshots.Count > 0 || _run.gizmosPolicyVersion == 1;
            _run.cleanupOutcome = _run.unresolvedResources.Length > 0 ? "Unverified" : required ? "Verified" : "NotRequired";
            _run.recoveryRequired = _run.unresolvedResources.Length > 0;
            // Business is already terminal. This report attempt never enters the ordinary evidence wait.
            CompleteFinallyFailureReport();
            _run.cleanupPending = false; _run.stage = "Completed";
            PersistFinallyFailure();
            Trace("run.resources_finished", "Finally 失败后的框架收尾结束", () => "状态=" + _run.cleanupOutcome
                + " 未验证资源=" + string.Join(",", _run.unresolvedResources) + " 报告=" + _run.reportOutcome);
            FinishExecutionLog("业务已冻结；框架资源=" + _run.cleanupOutcome + "；报告=" + _run.reportOutcome);
        }

        private void CompleteFinallyFailureReport()
        {
            if (_run.reportOutcome != "Pending") return;
            try
            {
                _report ??= Directory.Exists(_run.reportDirectory)
                    ? AutomationReportWriter.OpenExisting(_run.reportDirectory, _run.runId)
                    : AutomationReportWriter.Create(new AutomationReportCreateRequest
                    { runId = _run.runId, outputDirectory = _run.reportDirectory, startedAtUtcMs = _run.startedAtUtcMs });
                if (string.IsNullOrEmpty(_run.reportCommitJson))
                {
                    _report.Append(new AutomationReportEvent { eventType = "run.finally_failed", phaseId = "Finally",
                        caseId = Item.instanceId, outcome = _run.status, timestampUtcMs = _run.finishedAtUtcMs,
                        detail = FinallyFailureDiagnosticJson() });
                    var logSummary = _run.logSummary ?? new AutomationReportLogSummary
                    {
                        sessionId = _run.captureSessionId, fromSequenceInclusive = _run.captureStartSequence,
                        toSequenceExclusive = _run.captureEndSequence, evidenceComplete = false,
                    };
                    _run.reportCommitJson = JsonUtility.ToJson(new AutomationReportSummary
                    {
                        runId = _run.runId, startedAtUtcMs = _run.startedAtUtcMs, finishedAtUtcMs = _run.finishedAtUtcMs,
                        outcome = _run.status, failureSignature = _run.error?.code, detail = FinallyFailureDiagnosticJson(),
                        logSummary = logSummary, gizmos = _run.gizmosState,
                        phases = new[]
                        {
                            new AutomationReportPhase { id = "businessCleanup", outcome = "FrozenAfterFinallyFailure",
                                detail = "保留已验证业务清理；其余 Cleanup、Restore 和后续 Finally 未执行。\n" + FinallyFailureDiagnosticJson() },
                            new AutomationReportPhase { id = "frameworkRelease", outcome = _run.cleanupOutcome,
                                startedAtUtcMs = _run.finallyFailureAtUtcMs, finishedAtUtcMs = _clock(),
                                detail = "deadlineUtcMs=" + _run.finallyCleanupDeadlineUtcMs + "; unresolved="
                                    + string.Join(",", _run.unresolvedResources) + "; " + _run.resourceRecoveryError },
                        },
                        cases = _run.steps.Select((s, i) => new AutomationReportCase
                        {
                            id = s.instanceId, stepId = _run.plan.steps[i].stepId, phaseId = _run.plan.steps[i].phase,
                            stage = s.stage, outcome = s.outcome.ToString(), errorCode = s.error?.code,
                            detail = s.error?.message, cleanupState = s.cleanupState,
                            startedAtUtcMs = s.startedAtUtcMs, cleanupStartedAtUtcMs = s.cleanupStartedAtUtcMs,
                            finishedAtUtcMs = s.finishedAtUtcMs,
                        }).ToArray(),
                        artifacts = _run.registeredArtifacts.Select(a => new AutomationReportArtifactReference
                        { kind = a.kind, instanceId = a.instanceId, path = a.path, bytes = a.bytes, sha256 = a.sha256 }).ToArray(),
                    });
                    PersistFinallyFailure();
                }
                _report.Complete(JsonUtility.FromJson<AutomationReportSummary>(_run.reportCommitJson));
                _run.artifacts = _report.GetArtifacts().Concat(_run.registeredArtifacts).ToArray();
                _run.reportOutcome = "Written";
            }
            catch (Exception ex)
            {
                _run.reportOutcome = "Failed";
                RecordImmediateStopResourceError("STEP_FINALLY_FAILURE_REPORT_FAILED", ex.Message, ex.ToString());
                try { _run.artifacts = (_report?.GetArtifacts() ?? Array.Empty<AutomationReportArtifact>()).Concat(_run.registeredArtifacts).ToArray(); }
                catch (Exception artifactsError) { RecordImmediateStopResourceError("STEP_REPORT_ARTIFACTS_UNVERIFIED", artifactsError.Message, artifactsError.ToString()); }
            }
        }
    }
}
