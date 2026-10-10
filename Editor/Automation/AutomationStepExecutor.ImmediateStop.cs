using System;
using System.Linq;

namespace CodingRiver.UPilot.Automation
{
    public sealed partial class AutomationStepExecutor
    {
        internal const long ImmediateStopReleaseTimeoutMs = 10000;
        internal bool ReleasingImmediateStopResources => _run != null && _run.terminal
            && _run.immediateStopRequestedAtUtcMs > 0 && _run.stage == "ResourceReleasing" && _run.cleanupPending;

        /// <summary>Freeze business execution once. Only executor-owned resource release may follow.</summary>
        internal bool StopImmediately()
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                throw new InvalidOperationException("STEP_MAIN_THREAD_REQUIRED");
            if (_run == null || _run.terminal) return false;
            var now = _clock();
            RecordError("STEP_STOPPED_IMMEDIATELY", "Immediately stopped; remaining business callbacks and Finally were skipped.");
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
            PersistImmediateStop(); // Failure to archive never reopens business execution.
            try { _collector?.Dispose(); }
            catch (Exception ex) { RecordImmediateStopResourceError("STEP_DISPOSE_FAILED", ex.Message); }
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
            return true;
        }

        private bool HasImmediateStopResources => (_run.captureOwned && _run.captureStartIntent && !_run.captureStopVerified)
            || (_run.snapshots?.Any(NeedsImmediateStopRelease) ?? false);
        private static bool NeedsImmediateStopRelease(AutomationSnapshotRecord record) =>
            record != null && (record.status == "Running" || record.unresolved);

        private void TickImmediateStopRelease()
        {
            if (!ReleasingImmediateStopResources) return;
            // One fixed deadline, persisted through the original stop timestamp. Never renew it.
            if (_clock() - _run.immediateStopRequestedAtUtcMs >= ImmediateStopReleaseTimeoutMs)
            {
                RecordImmediateStopResourceError("STEP_IMMEDIATE_STOP_RELEASE_TIMEOUT",
                    "Immediate-stop resource release was not verified within 10 seconds; inspect the original resources.");
                CompleteImmediateStopRelease();
                return;
            }
            if (_run.captureOwned && _run.captureStartIntent && !_run.captureStopVerified)
            {
                try
                {
                    _ownedCapture ??= new AutomationRunCapture(_captureStorePath, _run.runId, true);
                    if (string.IsNullOrEmpty(_run.captureSessionId) && !string.IsNullOrEmpty(_ownedCapture.SessionId))
                        _run.captureSessionId = _ownedCapture.Observe().sessionId;
                    if (string.IsNullOrEmpty(_run.captureSessionId) || _ownedCapture.SessionId != _run.captureSessionId)
                        throw new InvalidOperationException("STEP_CAPTURE_IDENTITY_INVALID");
                    if (_ownedCapture.StopAndVerify(_clock(), out _, out _, AddImmediateStopArtifacts))
                        _run.captureStopVerified = true;
                }
                catch (Exception ex) { RecordImmediateStopResourceError("STEP_CAPTURE_RELEASE_UNCONFIRMED", ex.Message); }
            }
            // Persisting the native cancellation intent must succeed before dispatch. Do not use Save(),
            // whose ordinary failure path resets terminal/cleanup fields.
            var snapshots = new AutomationSnapshotEvidence(_run, () => _store.Save(_run), _clock);
            foreach (var record in _run.snapshots.Where(NeedsImmediateStopRelease).ToArray())
            {
                try { snapshots.StopAndObserveRelease(record); }
                catch (Exception ex)
                {
                    record.unresolved = true;
                    RecordImmediateStopResourceError("STEP_SNAPSHOT_RELEASE_UNCONFIRMED", record.snapshotId + ": " + ex.Message);
                }
            }
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
            PersistImmediateStop();
        }

        private void RecordImmediateStopResourceError(string code, string message)
        {
            _run.resourceRecoveryError = code + ": " + message;
            if (_run.error?.code != code && !_run.secondaryErrors.Any(e => e.code == code && e.message == message))
                RecordError(code, message);
        }

        private void PersistImmediateStop()
        {
            try { _store.Save(_run); }
            catch (Exception ex)
            {
                RecordImmediateStopResourceError("STEP_STORE_FAILED", ex.Message);
            }
        }
    }
}
