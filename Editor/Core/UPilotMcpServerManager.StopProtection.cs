// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;

namespace CodingRiver.UPilot
{
    public sealed partial class UPilotMcpServerManager
    {
        internal readonly struct ServiceShutdownSnapshot
        {
            internal readonly long Generation, DeadlineUtcMs, StartedTick;
            internal readonly string OwnerId, Phase;
            internal readonly bool Stopping, ReplacementEligible;
            internal ServiceShutdownSnapshot(long generation, string ownerId, string phase, long deadline,
                bool stopping, bool replacementEligible, long startedTick = 0)
            { Generation = generation; OwnerId = ownerId ?? ""; Phase = phase ?? "idle"; DeadlineUtcMs = deadline;
                Stopping = stopping; ReplacementEligible = replacementEligible; StartedTick = startedTick; }
            internal string Detail => $"shutdownGeneration={Generation} ownerId={(string.IsNullOrEmpty(OwnerId) ? "unknown" : OwnerId)} phase={Phase} stopping={Stopping} replacementEligible={ReplacementEligible} " +
                $"deadlineUtcMs={(DeadlineUtcMs > 0 ? DeadlineUtcMs.ToString() : "unknown")} " +
                $"remainingBudgetMs={(DeadlineUtcMs > 0 ? Math.Max(0, DeadlineUtcMs - StopNow).ToString() : "unknown")} " +
                $"shutdownElapsedMs={(StartedTick > 0 ? Logger.LifecycleElapsedMs(StartedTick).ToString() : "unknown")}";
        }

        internal readonly struct ServiceWorkResetReceipt
        {
            internal readonly bool Performed;
            internal readonly string Error;
            internal ServiceWorkResetReceipt(bool performed, string error) { Performed = performed; Error = error ?? ""; }
        }

        // This lock never depends on Bridge connectivity or the main-thread control queue.
        private object _shutdownLock;
        private object ShutdownLock => System.Threading.LazyInitializer.EnsureInitialized(ref _shutdownLock);
        private long _shutdownGeneration, _shutdownDeadlineUtcMs, _shutdownStartedTick;
        private string _shutdownOwnerId = "", _shutdownPhase = "idle";
        private bool _shutdownStopping, _shutdownReplacementEligible, _shutdownResetPerformed;
        private string _shutdownResetError = "";
        private ServerLaunchState _shutdownLaunch;
        private int _shutdownLaunchStartGeneration;
        private string _restartShutdownOwnerId = "";
        internal Func<string, string> ServiceWorkResetOverride;

        internal ServiceShutdownSnapshot CaptureServiceShutdown()
        {
            lock (ShutdownLock) return new ServiceShutdownSnapshot(_shutdownGeneration, _shutdownOwnerId,
                _shutdownPhase, _shutdownDeadlineUtcMs, _shutdownStopping, _shutdownReplacementEligible, _shutdownStartedTick);
        }

        internal ServiceShutdownSnapshot BeginServiceShutdown(string ownerId, string phase, long deadlineUtcMs = 0)
        {
            if (string.IsNullOrEmpty(ownerId)) throw new ArgumentException("A shutdown owner is required.", nameof(ownerId));
            lock (ShutdownLock)
            {
                var newOwner = _shutdownOwnerId != ownerId || !_shutdownStopping;
                if (newOwner)
                {
                    ++_shutdownGeneration;
                    _shutdownStartedTick = Stopwatch.GetTimestamp();
                    _shutdownResetPerformed = false; _shutdownResetError = "";
                    _shutdownLaunch = null; _shutdownReplacementEligible = false;
                }
                _shutdownOwnerId = ownerId; _shutdownPhase = phase ?? "stopping";
                _shutdownStopping = true;
                if (newOwner || _shutdownDeadlineUtcMs <= 0) _shutdownDeadlineUtcMs = deadlineUtcMs;
                var snapshot = CaptureServiceShutdown();
                Logger.Lifecycle("shutdown_protection_started", snapshot.Detail);
                return snapshot;
            }
        }

        internal void MarkBridgeClosing()
        {
            var snapshot = CaptureServiceShutdown();
            BeginServiceShutdown(snapshot.Stopping ? snapshot.OwnerId : "bridge:" + Guid.NewGuid().ToString("N"),
                "bridge_closing", snapshot.Stopping ? snapshot.DeadlineUtcMs : 0);
        }

        internal void ReleaseBridgeOnlyShutdown()
        {
            var snapshot = CaptureServiceShutdown();
            if (snapshot.OwnerId.StartsWith("bridge:", StringComparison.Ordinal))
                FinishServiceShutdown(snapshot.OwnerId, "bridge_start_requested");
        }

        internal bool CanApplyHello(ServiceShutdownSnapshot captured, out string reason)
        {
            lock (ShutdownLock)
            {
                if (captured.Generation != _shutdownGeneration || captured.OwnerId != (_shutdownOwnerId ?? ""))
                { reason = "shutdown_generation_changed"; return false; }
                if (_shutdownStopping) { reason = "shutdown_in_progress"; return false; }
                if (_shutdownReplacementEligible && _shutdownDeadlineUtcMs > 0 && StopNow >= _shutdownDeadlineUtcMs)
                { reason = "replacement_deadline_elapsed"; return false; }
                reason = "current"; return true;
            }
        }

        internal ServiceWorkResetReceipt ResetActiveWorkForServiceOwner(string ownerId, string source,
            string preservedRecoveryOwnerId = "")
        {
            long generation;
            lock (ShutdownLock)
            {
                if (ownerId != _shutdownOwnerId || !_shutdownStopping)
                    throw new InvalidOperationException("Active-work reset must belong to the current shutdown owner.");
                if (_shutdownResetPerformed)
                {
                    Logger.Lifecycle("service_reset_receipt_reused", $"{CaptureServiceShutdown().Detail} source={source} error={_shutdownResetError}");
                    return new ServiceWorkResetReceipt(true, _shutdownResetError);
                }
                // Record ownership before invoking cleanup; reentrancy cannot execute a second reset.
                _shutdownResetPerformed = true;
                generation = _shutdownGeneration;
            }
            var started = Stopwatch.GetTimestamp();
            Logger.Lifecycle("service_reset_started", $"{CaptureServiceShutdown().Detail} source={source} lifetimeBefore={UPilotServiceLifetime.Id}");
            string error;
            try
            {
                error = ServiceWorkResetOverride != null
                    ? ServiceWorkResetOverride(preservedRecoveryOwnerId) ?? ""
                    : UPilotBridge.Instance.ResetActiveWork(preservedRecoveryOwnerId);
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                Logger.ReportFailure("SERVICE", $"Active-work reset failed ownerId={ownerId} source={source}", ex,
                    ownerId, terminal: true);
            }
            lock (ShutdownLock)
                if (_shutdownOwnerId == ownerId && _shutdownGeneration == generation) _shutdownResetError = error;
            Logger.Lifecycle("service_reset_finished", $"ownerId={ownerId} shutdownGeneration={generation} source={source} lifetimeAfter={UPilotServiceLifetime.Id} elapsedMs={Logger.LifecycleElapsedMs(started)} error={error}");
            return new ServiceWorkResetReceipt(true, error);
        }

        internal bool RestoreServiceWorkResetReceipt(ServiceShutdownSnapshot current, bool performed, string error,
            long originalDeadlineUtcMs, out string reason)
        {
            lock (ShutdownLock)
            {
                if (!performed) { reason = "reset_receipt_unknown"; return false; }
                if (string.IsNullOrEmpty(current.OwnerId) || current.Generation != _shutdownGeneration ||
                    current.OwnerId != _shutdownOwnerId || !current.Stopping || !_shutdownStopping)
                { reason = "shutdown_owner_or_generation_changed"; return false; }
                if (originalDeadlineUtcMs <= StopNow || _shutdownDeadlineUtcMs <= 0 ||
                    _shutdownDeadlineUtcMs > originalDeadlineUtcMs)
                { reason = "reset_receipt_deadline_mismatch"; return false; }
                error = error ?? "";
                if (_shutdownResetPerformed && _shutdownResetError != error)
                { reason = "reset_receipt_result_changed"; return false; }
                // The stop observer keeps its shorter deadline. Restore only the persisted parent's
                // original budget; this neither repeats cleanup nor restores any prior active work.
                _shutdownDeadlineUtcMs = originalDeadlineUtcMs;
                _shutdownResetPerformed = true;
                _shutdownResetError = error;
                reason = "restored";
                Logger.Lifecycle("service_reset_receipt_restored", $"{CaptureServiceShutdown().Detail} error={error}");
                return true;
            }
        }

        private void AllowServiceReplacement(string ownerId)
        {
            lock (ShutdownLock)
            {
                if (string.IsNullOrEmpty(ownerId) || ownerId != _shutdownOwnerId || !_shutdownStopping || _launchState == null)
                    return;
                // All pre-stop ACKs become permanently obsolete, including those already queued.
                ++_shutdownGeneration;
                _shutdownStopping = false; _shutdownReplacementEligible = true; _shutdownPhase = "replacement_verifying";
                _shutdownLaunch = _launchState; _shutdownLaunchStartGeneration = _startAttemptGeneration;
                Logger.Lifecycle("replacement_handshake_allowed", $"{CaptureServiceShutdown().Detail} launcherPid={_launchState.launcherPid} startGeneration={_shutdownLaunchStartGeneration}");
            }
        }

        private bool MatchesOwnedResetLaunch(ServiceShutdownSnapshot captured)
        {
            return captured.Generation == _shutdownGeneration && captured.OwnerId == _shutdownOwnerId &&
                !_shutdownStopping && _shutdownReplacementEligible && _shutdownResetPerformed &&
                _shutdownLaunch != null && ReferenceEquals(_shutdownLaunch, _launchState) &&
                _shutdownLaunchStartGeneration == _startAttemptGeneration && !_shutdownLaunch.terminal &&
                (_shutdownDeadlineUtcMs <= 0 || StopNow < _shutdownDeadlineUtcMs);
        }

        internal bool IsOwnedResetVerificationPending(ServiceShutdownSnapshot captured)
        {
            lock (ShutdownLock) return MatchesOwnedResetLaunch(captured) &&
                (!_shutdownLaunch.verified || _launchNeedsRevalidation);
        }

        internal bool CanReuseOwnedReset(ServiceShutdownSnapshot captured, string serverLifetime, string serverInstanceId, out string ownerId)
        {
            lock (ShutdownLock)
            {
                ownerId = "";
                if (!MatchesOwnedResetLaunch(captured) || !_shutdownLaunch.verified || _launchNeedsRevalidation ||
                    string.IsNullOrEmpty(serverLifetime) || string.IsNullOrEmpty(serverInstanceId) ||
                    serverInstanceId != _shutdownLaunch.instanceId ||
                    _shutdownLaunch.servicePid <= 0 || _shutdownLaunch.serviceCreatedAt <= 0 || _trackedProcess == null)
                    return false;
                try
                {
                    if (_trackedProcess.HasExited || _trackedProcess.Id != _shutdownLaunch.servicePid ||
                        _trackedProcess.StartTime.ToUniversalTime().Ticks != _shutdownLaunch.serviceCreatedAt) return false;
                }
                catch (InvalidOperationException) { return false; }
                catch (System.ComponentModel.Win32Exception) { return false; }
                ownerId = _shutdownOwnerId;
                return true;
            }
        }

        internal void FinishServiceShutdown(string ownerId, string reason)
        {
            lock (ShutdownLock)
            {
                if (string.IsNullOrEmpty(ownerId) || ownerId != _shutdownOwnerId) return;
                Logger.Lifecycle("shutdown_protection_finished", $"{CaptureServiceShutdown().Detail} reason={reason}");
                ++_shutdownGeneration;
                _shutdownOwnerId = ""; _shutdownPhase = "idle"; _shutdownDeadlineUtcMs = 0;
                _shutdownStartedTick = 0;
                _shutdownStopping = false; _shutdownReplacementEligible = false;
                _shutdownResetPerformed = false; _shutdownResetError = ""; _shutdownLaunch = null;
            }
        }
    }
}
