// -----------------------------------------------------------------------
// UPilot Editor - coordinates service shutdown across UPM package updates.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace CodingRiver.UPilot
{
    [InitializeOnLoad]
    internal static class UPilotPackageUpdateLifecycle
    {
        private const string PackageName = "io.github.codingriver.upilot";
        private const string UpdateInProgressKey = "CodingRiver.UPilot.PackageUpdate.InProgress";
        private const string RestartPendingKey = "CodingRiver.UPilot.PackageUpdate.RestartPending";
        private const string UpdateCompletedKey = "CodingRiver.UPilot.PackageUpdate.Completed";
        private const string TargetVersionKey = "CodingRiver.UPilot.PackageUpdate.TargetVersion";
        private const string ManagedServerPendingKey = "CodingRiver.UPilot.PackageUpdate.ManagedServerPending";
        private const string TargetServerVersionKey = "CodingRiver.UPilot.PackageUpdate.TargetServerVersion";
        private const string PreparedServerPathKey = "CodingRiver.UPilot.PackageUpdate.PreparedServerPath";
        private const string PendingPackageRetryKey = "CodingRiver.UPilot.PackageUpdate.PendingPackageRetry";
        private const string PendingTargetVersionKey = "CodingRiver.UPilot.PackageUpdate.PendingTargetVersion";
        private const string PendingTargetServerVersionKey = "CodingRiver.UPilot.PackageUpdate.PendingTargetServerVersion";
        private const string PendingNewServerPathKey = "CodingRiver.UPilot.PackageUpdate.PendingNewServerPath";
        private const string PendingOldServerPathKey = "CodingRiver.UPilot.PackageUpdate.PendingOldServerPath";
        private const string PendingOldServerVersionKey = "CodingRiver.UPilot.PackageUpdate.PendingOldServerVersion";
        private const string DeploymentRepairPendingKey = "CodingRiver.UPilot.PackageUpdate.DeploymentRepairPending";

        private const string DeploymentSupersededOperationKey = "CodingRiver.UPilot.PackageUpdate.SupersededRestart";
        private const string DeploymentRepairAttemptedKey = "CodingRiver.UPilot.PackageUpdate.RepairAttempted";
        private static bool _restartScheduled;
        private static bool _registrationInProgress;
        private static bool _deferredRepairScheduled;
        private static bool _deferredRepairRunning;
        private const string RestoreWaitKey = "CodingRiver.UPilot.PackageUpdate.RestoreWait";
        private const string DeferredRepairWaitKey = "CodingRiver.UPilot.PackageUpdate.RepairWait";
        private static RecoveryWait _restoreWait;
        private static RecoveryWait _deferredRepairWait;

        [Serializable]
        private sealed class RecoveryWait
        {
            public string operationId;
            public int timeoutSeconds;
            public long deadlineUtcMs;
        }

        private static RecoveryWait GetOrCreateRecoveryWait(string key)
        {
            var saved = GetSessionString(key, "");
            var wait = string.IsNullOrEmpty(saved) ? null : JsonUtility.FromJson<RecoveryWait>(saved);
            if (wait != null && !string.IsNullOrEmpty(wait.operationId) && wait.deadlineUtcMs > 0)
                return wait;
            int seconds = UPilotTimingSettings.PackageUpdateRecoveryTimeoutSeconds;
            wait = new RecoveryWait
            {
                operationId = Guid.NewGuid().ToString("N"),
                timeoutSeconds = seconds,
                deadlineUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + seconds * 1000L,
            };
            SetSessionString(key, JsonUtility.ToJson(wait));
            return wait;
        }

        private static bool IsCurrentRecoveryWait(string key, RecoveryWait wait)
        {
            if (wait == null) return false;
            var saved = GetSessionString(key, "");
            if (string.IsNullOrEmpty(saved)) return false;
            return JsonUtility.FromJson<RecoveryWait>(saved)?.operationId == wait.operationId;
        }

        private static bool HasRecoveryWaitExpired(RecoveryWait wait) =>
            wait == null || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= wait.deadlineUtcMs;

        static UPilotPackageUpdateLifecycle()
        {
            Events.registeringPackages -= OnRegisteringPackages;
            Events.registeringPackages += OnRegisteringPackages;
            Events.registeredPackages -= OnRegisteredPackages;
            Events.registeredPackages += OnRegisteredPackages;
            EditorApplication.delayCall += RestoreReleaseOrResetSourceState;
        }

        private static void RestoreReleaseOrResetSourceState()
        {
            if (GetSessionBool(DeploymentRepairPendingKey, false))
            {
                RepairPackageDeployment();
                return;
            }
            if (UPilotServerRuntimeService.IsSourceUpdateChannel())
            {
                UPilotUpdateService.ResetSourceChannelState();
                return;
            }

            TryRestoreAfterUpdate();
        }

        private const string PackageStopKey = "UPilot.PackageStopObservationId";

        public static Task<bool> PrepareForPackageUpdateAsync(
            string targetVersion,
            Action<string, MessageType> notice = null,
            bool installManagedServerAfterUpdate = false,
            string targetServerVersion = "",
            string preparedServerPath = "")
        {
            return PrepareForPackageUpdateAsync(
                targetVersion,
                notice,
                installManagedServerAfterUpdate,
                targetServerVersion,
                preparedServerPath,
                UPilotOccupancyRetryKind.Package,
                "");
        }

        internal static async Task<bool> PrepareForPackageUpdateAsync(
            string targetVersion,
            Action<string, MessageType> notice,
            bool installManagedServerAfterUpdate,
            string targetServerVersion,
            string preparedServerPath,
            UPilotOccupancyRetryKind occupancyRetryKind,
            string occupancyRetryIdentifier)
        {
            if (GetSessionBool(UpdateInProgressKey, false))
            {
                if (installManagedServerAfterUpdate)
                {
                    SetSessionBool(ManagedServerPendingKey, true);
                    if (!string.IsNullOrWhiteSpace(targetServerVersion))
                        SetSessionString(TargetServerVersionKey, targetServerVersion);
                    if (!string.IsNullOrWhiteSpace(preparedServerPath))
                        SetSessionString(PreparedServerPathKey, preparedServerPath);
                }

                if (UPilotMcpServerManager.Instance.HasActiveStopObservation)
                {
                    var pending = await UPilotMcpServerManager.Instance.StopServerAndObserveExitAsync(UPilotServerStopOrigin.PackageUpdate);
                    return pending.StopConfirmed && pending.PortsAvailable && UPilotMcpServerManager.Instance.IsStopResultCurrent(pending);
                }
                return PackageStopAllowsRecovery(out _);
            }

            var manager = UPilotMcpServerManager.Instance;
            var status = manager.GetStatus();
            var wasRunning = status.IsRunning || status.HttpPortListening || status.WsPortListening ||
                             !UPilotPortAllocator.IsPortAvailable(manager.HttpPort) ||
                             !UPilotPortAllocator.IsPortAvailable(manager.WsPort);
            var shouldRestart = UPilotSetupState.IsCompleted;

            SetSessionBool(UpdateInProgressKey, true);
            EraseSessionString(PackageStopKey);
            SetSessionBool(RestartPendingKey, shouldRestart);
            SetSessionBool(UpdateCompletedKey, false);
            SetSessionString(TargetVersionKey, targetVersion ?? "");
            SetSessionBool(ManagedServerPendingKey, installManagedServerAfterUpdate);
            SetSessionString(TargetServerVersionKey, targetServerVersion ?? "");
            SetSessionString(PreparedServerPathKey, preparedServerPath ?? "");
            UPilotUpdateService.SetOperationPhase(
                UPilotUpdateOperationPhase.StoppingService,
                installManagedServerAfterUpdate
                    ? "正在停止服务，准备更新 UPilot 和 MCP 服务…"
                    : "正在停止服务，准备更新 UPilot 包…",
                targetVersion,
                targetServerVersion);

            if (!wasRunning)
                return true;

            notice?.Invoke("正在停止 MCP 服务以更新 UPilot 包…", MessageType.Info);
            UPilotBridge.Instance.Stop();
            var lifetime = UPilotServiceLifetime.Id;
            var operationId = UPilotUpdateService.Instance.GetOperationStatus().OperationId;
            var stopTask = manager.StopServerAndObserveExitAsync(UPilotServerStopOrigin.PackageUpdate);
            SetSessionString(PackageStopKey, manager.StopObservation.id);
            var stop = await stopTask;
            if (lifetime != UPilotServiceLifetime.Id || operationId != UPilotUpdateService.Instance.GetOperationStatus().OperationId) return false;
            if (stop.StopConfirmed && stop.PortsAvailable && manager.IsStopResultCurrent(stop))
                return true;

            ClearUpdateState();
            var message = "无法停止 MCP 服务，已取消包更新以避免文件占用";
            var recoveryStarted = UPilotUpdateService.Instance.BeginOccupancyRecovery(
                occupancyRetryKind,
                occupancyRetryIdentifier,
                message);
            UPilotUpdateService.SetOperationFailed(message);
            notice?.Invoke(
                recoveryStarted ? message + "。请在更新中心结束全部占用进程并重试。" : message,
                MessageType.Error);
            return false;
        }

        public static void MarkPackageUpdateCompleted()
        {
            if (!GetSessionBool(UpdateInProgressKey, false))
                return;

            SetSessionBool(UpdateCompletedKey, true);
            var managedPending = GetSessionBool(ManagedServerPendingKey, false);
            UPilotUpdateService.SetOperationPhase(
                UPilotUpdateOperationPhase.WaitingForReload,
                managedPending
                    ? "UPilot 包已更新，等待 Unity 重载后继续更新 MCP 服务…"
                    : "UPilot 包已更新，等待 Unity 重载后恢复服务…");
            UPilotUpdateService.Instance.CompleteObservedUpmUpdateFromRegistration();
            ScheduleRestore();
        }

        public static bool IsManagedServerUpdatePending =>
            GetSessionBool(ManagedServerPendingKey, false);

        public static bool HasPendingManagedPackageUpdate =>
            EditorPrefs.GetBool(ProjectPersistentKey(PendingPackageRetryKey), false);

        public static string[] GetPreferenceKeysForCurrentProject()
        {
            return new[]
            {
                ProjectPersistentKey(PendingPackageRetryKey),
                ProjectPersistentKey(PendingTargetVersionKey),
                ProjectPersistentKey(PendingTargetServerVersionKey),
                ProjectPersistentKey(PendingNewServerPathKey),
                ProjectPersistentKey(PendingOldServerPathKey),
                ProjectPersistentKey(PendingOldServerVersionKey),
            };
        }

        public static void MarkManagedPackageUpdatePending(
            string targetVersion,
            string targetServerVersion,
            string newServerPath,
            string oldServerPath,
            string oldServerVersion)
        {
            EditorPrefs.SetBool(ProjectPersistentKey(PendingPackageRetryKey), true);
            EditorPrefs.SetString(ProjectPersistentKey(PendingTargetVersionKey), targetVersion ?? "");
            EditorPrefs.SetString(ProjectPersistentKey(PendingTargetServerVersionKey), targetServerVersion ?? "");
            EditorPrefs.SetString(ProjectPersistentKey(PendingNewServerPathKey), newServerPath ?? "");
            EditorPrefs.SetString(ProjectPersistentKey(PendingOldServerPathKey), oldServerPath ?? "");
            EditorPrefs.SetString(ProjectPersistentKey(PendingOldServerVersionKey), oldServerVersion ?? "");
        }

        public static void ClearPendingManagedPackageUpdate()
        {
            EditorPrefs.DeleteKey(ProjectPersistentKey(PendingPackageRetryKey));
            EditorPrefs.DeleteKey(ProjectPersistentKey(PendingTargetVersionKey));
            EditorPrefs.DeleteKey(ProjectPersistentKey(PendingTargetServerVersionKey));
            EditorPrefs.DeleteKey(ProjectPersistentKey(PendingNewServerPathKey));
            EditorPrefs.DeleteKey(ProjectPersistentKey(PendingOldServerPathKey));
            EditorPrefs.DeleteKey(ProjectPersistentKey(PendingOldServerVersionKey));
        }

        public static void ResetUpdateStateForRepair(bool clearPendingPackageRetry)
        {
            ClearDeferredRepair();
            ClearUpdateState();
            if (clearPendingPackageRetry)
                ClearPendingManagedPackageUpdate();
            Debug.Log(
                "[UPilot] Package update lifecycle state reset for repair." +
                $" clearPendingPackageRetry={clearPendingPackageRetry}");
        }

        internal static void ResetUpdateStateForSourceChannel()
        {
            var hadSessionState = GetSessionBool(UpdateInProgressKey, false) ||
                                  GetSessionBool(RestartPendingKey, false) ||
                                  GetSessionBool(UpdateCompletedKey, false) ||
                                  GetSessionBool(ManagedServerPendingKey, false);
            var hadPersistentState = HasPendingManagedPackageUpdate;
            ClearUpdateState();
            ClearPendingManagedPackageUpdate();
            if (hadSessionState || hadPersistentState)
            {
                Debug.Log(
                    "[UPilot] Cleared package update lifecycle state for source channel." +
                    $" sessionState={hadSessionState}; persistentState={hadPersistentState}");
            }
        }

        public static bool TryGetPendingPackageUpdateNotice(out string message)
        {
            message = "";
            if (!HasPendingManagedPackageUpdate)
                return false;

            var targetVersion = EditorPrefs.GetString(ProjectPersistentKey(PendingTargetVersionKey), "");
            var targetServerVersion = EditorPrefs.GetString(ProjectPersistentKey(PendingTargetServerVersionKey), "");
            var currentUpm = UPilotServerRuntimeService.UpmVersion;
            if (!string.IsNullOrWhiteSpace(targetVersion) &&
                UPilotServerRuntimeService.IsVersionAtLeast(currentUpm, targetVersion))
            {
                ClearPendingManagedPackageUpdate();
                return false;
            }

            var versionText = string.IsNullOrWhiteSpace(targetVersion) ? "最新版本" : targetVersion;
            var serverText = string.IsNullOrWhiteSpace(targetServerVersion) ? "新版本" : targetServerVersion;
            message = $"自动管理服务已更新到 {serverText}，但 UPilot 包还未完成更新。请打开更新中心继续更新到 {versionText}。";
            return true;
        }

        public static void RestoreAfterFailedUpdate()
        {
            var shouldRestart = GetSessionBool(RestartPendingKey, false);
            var hasPendingManagedPackageUpdate = HasPendingManagedPackageUpdate;
            ClearUpdateState();
            UPilotUpdateService.ClearExternalPackageManagerAbort();
            if (hasPendingManagedPackageUpdate)
            {
                var message = TryGetPendingPackageUpdateNotice(out var notice)
                    ? notice
                    : "UPilot 包更新未完成，请在更新中心重新执行更新。";
                UPilotUpdateService.SetOperationFailed("UPilot 包更新未完成，请继续更新");
                UPilotMainWindow.OpenWithNotice(message, MessageType.Warning);
            }
            else
            {
                UPilotUpdateService.SetOperationFailed("UPilot 包更新失败，已恢复服务");
            }

            if (!shouldRestart || !UPilotSetupState.IsCompleted)
                return;

            UPilotProjectConfig.Reload();
            UPilotProjectConfig.ApplyEndpoints(UPilotBridge.Instance);
            UPilotMcpServerManager.Instance.RestartServer(() => UPilotBridge.Instance.EnsureStarted());
        }

        private static void OnRegisteringPackages(PackageRegistrationEventArgs args)
        {
            try
            {
                var currentPackage = FindUPilotPackage(args.changedFrom);
                var removedPackage = FindUPilotPackage(args.removed);
                var targetPackage = FindUPilotPackage(args.changedTo) ?? FindUPilotPackage(args.added);
                if (removedPackage != null || targetPackage != null) _registrationInProgress = true;
                if (UPilotScriptingDefineManager.ShouldRemoveForPackageRegistration(
                        removedPackage != null,
                        targetPackage != null))
                {
                    UPilotScriptingDefineManager.RemoveOwnedDefines();
                }
                if (removedPackage != null || (targetPackage != null &&
                    (currentPackage == null || !ShouldManagePackageUpdate(targetPackage) ||
                     UPilotServerRuntimeService.IsSourcePackage(currentPackage))))
                {
                    ClearDeferredRepair();
                    SetSessionBool(DeploymentRepairPendingKey, true);
                    EraseSessionBool(DeploymentRepairAttemptedKey);
                    var activeRestart = UPilotServerRestartDiagnostics.Current;
                    if (activeRestart?.status == "running")
                        SetSessionString(DeploymentSupersededOperationKey, activeRestart.operationId);
                    // No business-idle gate. Keep intent across removal and the next installation.
                    UPilotBridge.Instance.Stop();
                    _ = ObserveExternalStopAsync();
                    return;
                }
                if (currentPackage == null || targetPackage == null)
                    return;

                if (!ShouldManagePackageUpdate(targetPackage))
                {
                    UPilotUpdateService.ResetSourceChannelState();
                    return;
                }

                var targetVersion = targetPackage.version ?? "";
                var externalConflictMessage = "";
                if (ShouldHandleExternalPackageManagerConflict())
                {
                    externalConflictMessage =
                        UPilotUpdateService.Instance.AbortForExternalPackageManagerUpdate(targetVersion);
                    UPilotMainWindow.OpenWithNotice(externalConflictMessage, MessageType.Warning);
                }

                var installManagedServer = ShouldInstallManagedServerAfterPackageUpdate();
                _ = ObserveExternalPackagePreparationAsync(targetVersion, installManagedServer, targetPackage.packageId ?? "");
            }
            catch (Exception ex)
            {
                _registrationInProgress = false;
                ReportLifecycleError("UPilot 包注册前处理失败", ex);
            }
        }

        private static async Task ObserveExternalStopAsync()
        {
            try
            {
                var manager = UPilotMcpServerManager.Instance;
                var task = manager.StopServerAndObserveExitAsync(UPilotServerStopOrigin.PackageRegistration);
                SetSessionString(PackageStopKey, manager.StopObservation.id);
                await task; // External registration cannot be paused; the recovery gate reads this result.
            }
            catch (Exception ex) { ReportLifecycleError("外部包注册停止观察失败", ex); }
        }

        private static async Task ObserveExternalPackagePreparationAsync(string version, bool managed, string identifier)
        {
            try { await PrepareForPackageUpdateAsync(version, null, managed, "", "", UPilotOccupancyRetryKind.ExternalPackageManager, identifier); }
            catch (Exception ex) { ReportLifecycleError("外部包更新停止观察失败", ex); }
        }

        private static bool PackageStopAllowsRecovery(out bool pending)
        {
            var id = GetSessionString(PackageStopKey, "");
            var manager = UPilotMcpServerManager.Instance;
            manager.EnsureInitialServiceRecovery();
            var record = manager.StopObservation;
            pending = id.Length > 0 && record?.id == id && record.Active;
            return id.Length == 0 || record?.id == id && record.status == "succeeded";
        }

        private static void OnRegisteredPackages(PackageRegistrationEventArgs args)
        {
            try
            {
                var targetPackage = FindUPilotPackage(args.changedTo) ?? FindUPilotPackage(args.added);
                if (targetPackage == null)
                    return;
                UPilotScriptingDefineManager.EnsureForActiveBuildTarget();
                if (GetSessionBool(DeploymentRepairPendingKey, false))
                {
                    EditorApplication.delayCall += RepairPackageDeployment;
                    return;
                }

                if (!ShouldManagePackageUpdate(targetPackage))
                {
                    UPilotUpdateService.ResetSourceChannelState();
                    return;
                }

                UPilotUpdateService.ClearOccupancyRecovery();
                MarkPackageUpdateCompleted();
            }
            catch (Exception ex)
            {
                ReportLifecycleError("UPilot 包注册完成处理失败", ex);
            }
            finally { _registrationInProgress = false; }
        }

        private static void RepairPackageDeployment()
        {
            if (!GetSessionBool(DeploymentRepairPendingKey, false) || !UPilotSetupState.IsCompleted ||
                _deferredRepairScheduled || _deferredRepairRunning) return;
            _deferredRepairWait = GetOrCreateRecoveryWait(DeferredRepairWaitKey);
            if (HasRecoveryWaitExpired(_deferredRepairWait))
            {
                FailDeferredRepairTimeout();
                return;
            }
            _deferredRepairScheduled = true;
            EditorApplication.update += PollDeferredPackageRepair;
        }

        private static void PollDeferredPackageRepair()
        {
            if (!IsCurrentRecoveryWait(DeferredRepairWaitKey, _deferredRepairWait) ||
                !GetSessionBool(DeploymentRepairPendingKey, false) || UPilotQuickStart.IsExplicitlyStopped)
            {
                ClearDeferredRepair();
                return;
            }
            if (HasRecoveryWaitExpired(_deferredRepairWait))
            {
                FailDeferredRepairTimeout();
                return;
            }
            // Keep the same bounded observer during awaits so stop and deadline do not
            // depend on a potentially late network/repair continuation.
            if (!PackageStopAllowsRecovery(out var stopPending))
            {
                if (!stopPending) { ClearDeferredRepair(); UPilotUpdateService.SetOperationFailed("旧服务停止未确认，包修复已结束，请检查原停止记录。"); }
                return;
            }
            if (_deferredRepairRunning || _registrationInProgress || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode || UPilotQuickStart.IsRepairing ||
                UPilotServiceMaintenance.IsActive) return;
            _deferredRepairRunning = true;
            _ = CompleteDeferredPackageRepairAsync(_deferredRepairWait);
        }

        private static bool CanContinueDeferredRepair(RecoveryWait wait)
        {
            if (!IsCurrentRecoveryWait(DeferredRepairWaitKey, wait)) return false;
            if (UPilotQuickStart.IsExplicitlyStopped)
            {
                ClearDeferredRepair();
                return false;
            }
            if (!HasRecoveryWaitExpired(wait)) return true;
            FailDeferredRepairTimeout();
            return false;
        }

        private static void FailDeferredRepairTimeout()
        {
            var originalId = GetSessionString(DeploymentSupersededOperationKey, "");
            ClearDeferredRepair();
            const string message = "Package deployment recovery timed out while waiting for the Editor. Inspect current service state before requesting another repair.";
            UPilotServerRestartDiagnostics.RecordSupersededRecoveryFailure(originalId, message);
            UPilotUpdateService.SetOperationFailed(message);
        }

        private static async Task CompleteDeferredPackageRepairAsync(RecoveryWait wait)
        {
            var originalId = GetSessionString(DeploymentSupersededOperationKey, "");
            try
            {
                if (!CanContinueDeferredRepair(wait)) return;
                if (UPilotServerRuntimeService.IsSourceUpdateChannel())
                    UPilotUpdateService.ResetSourceChannelState();
                var verified = await VerifyCurrentDeploymentAsync(originalId, wait);
                if (!CanContinueDeferredRepair(wait)) return;
                if (verified)
                {
                    ClearDeferredRepair();
                    return;
                }
                if (GetSessionBool(DeploymentRepairAttemptedKey, false))
                {
                    ClearDeferredRepair();
                    UPilotServerRestartDiagnostics.RecordSupersededRecoveryFailure(originalId,
                        "The previous deferred repair was interrupted; inspect current service state before retrying.");
                    return;
                }
                if (UPilotQuickStart.IsRepairing || _registrationInProgress || EditorApplication.isCompiling ||
                    EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                    return;
                SetSessionBool(DeploymentRepairAttemptedKey, true);
                var result = await UPilotQuickStart.AutoRepairAsync(null);
                if (!CanContinueDeferredRepair(wait)) return;
                var successor = UPilotServerRestartDiagnostics.Current;
                if (successor?.operationId != originalId && !string.IsNullOrEmpty(originalId))
                    UPilotServerRestartDiagnostics.RecordSupersedingOperation(originalId, successor?.operationId);
                verified = UPilotQuickStart.LastRepairSucceeded && await VerifyCurrentDeploymentAsync(originalId, wait);
                if (!CanContinueDeferredRepair(wait)) return;
                ClearDeferredRepair();
                if (!verified) UPilotServerRestartDiagnostics.RecordSupersededRecoveryFailure(originalId, result);
            }
            catch (Exception ex)
            {
                if (!CanContinueDeferredRepair(wait)) return;
                ClearDeferredRepair();
                UPilotServerRestartDiagnostics.RecordSupersededRecoveryFailure(originalId, ex.Message);
                UnityEngine.Debug.LogError("[UPilot] Deferred package repair failed: " + ex);
            }
            finally
            {
                // A stopped/reset operation cannot clear or reschedule a successor.
                if (IsCurrentRecoveryWait(DeferredRepairWaitKey, wait))
                {
                    _deferredRepairRunning = false;
                    // The original observer remains attached; a busy continuation is
                    // retried there with the same identity and absolute deadline.
                }
            }
        }

        private static async Task<bool> VerifyCurrentDeploymentAsync(string originalId, RecoveryWait wait)
        {
            var manager = UPilotMcpServerManager.Instance;
            var status = await manager.GetFreshStatusAsync();
            if (!CanContinueDeferredRepair(wait)) return false;
            var bridge = UPilotBridge.Instance.GetStatus();
            if (!status.IsRunning || !status.HttpPortListening || !status.WsPortListening ||
                !UPilotMcpServerManager.IsVerifiedRestartHealth(status, status.ProcessId ?? 0,
                    UPilotProjectConfig.ProjectRoot) || !bridge.IsAuthenticated || !bridge.IsWsOpen ||
                string.IsNullOrEmpty(bridge.SessionId) || string.IsNullOrEmpty(status.Identity?.server_instance_id) ||
                UPilotDeploymentDiagnostics.Observe(bridge, status).Any(issue => issue.Blocking)) return false;
            try
            {
                using (var process = System.Diagnostics.Process.GetProcessById(status.ProcessId.Value))
                    if (process.HasExited || process.StartTime.ToUniversalTime().Ticks <= 0) return false;
                await manager.VerifyReadOnlyRoundTripAsync(status, bridge.SessionId);
            }
            catch (Exception) { return false; }
            if (!CanContinueDeferredRepair(wait) || UPilotBridge.Instance.GetStatus().SessionId != bridge.SessionId) return false;
            UPilotServerRestartDiagnostics.ObserveSupersededRecovery(originalId, status.ProcessId.Value,
                status.Identity.server_instance_id, status.IdentityProjectPath, true);
            return true;
        }

        internal static void CancelDeferredRepairForUserStop()
        {
            ClearDeferredRepair();
            ClearUpdateState();
            EditorApplication.delayCall -= RestoreReleaseOrResetSourceState;
        }

        private static void ClearDeferredRepair()
        {
            EditorApplication.update -= PollDeferredPackageRepair;
            EditorApplication.delayCall -= RepairPackageDeployment;
            _deferredRepairScheduled = false;
            _deferredRepairRunning = false;
            _deferredRepairWait = null;
            EraseSessionString(DeferredRepairWaitKey);
            EraseSessionBool(DeploymentRepairPendingKey);
            EraseSessionBool(DeploymentRepairAttemptedKey);
            EraseSessionString(DeploymentSupersededOperationKey);
        }

        private static bool ShouldHandleExternalPackageManagerConflict()
        {
            if (GetSessionBool(UpdateInProgressKey, false))
                return false;

            return UPilotUpdateService.Instance.HasActiveUpdateWorkForExternalPackageManagerUpdate();
        }

        internal static bool ShouldManagePackageUpdate(PackageInfo targetPackage)
        {
            return targetPackage != null &&
                   !UPilotServerRuntimeService.IsSourcePackage(targetPackage);
        }

        internal static bool ShouldManagePackageUpdate(PackageSource source, string packageId)
        {
            return !UPilotServerRuntimeService.IsSourcePackage(source, packageId);
        }

        private static PackageInfo FindUPilotPackage(
            System.Collections.Generic.IEnumerable<PackageInfo> packages)
        {
            if (packages == null)
                return null;

            foreach (var package in packages)
            {
                if (string.Equals(package?.name, PackageName, StringComparison.Ordinal))
                    return package;
            }

            return null;
        }

        private static void ScheduleRestore()
        {
            if (_restartScheduled)
                return;

            _restoreWait = GetOrCreateRecoveryWait(RestoreWaitKey);
            _restartScheduled = true;
            EditorApplication.update += WaitForEditorReady;
        }

        private static void WaitForEditorReady()
        {
            if (!IsCurrentRecoveryWait(RestoreWaitKey, _restoreWait) ||
                !GetSessionBool(UpdateCompletedKey, false) || UPilotQuickStart.IsExplicitlyStopped)
            {
                ClearUpdateState();
                return;
            }
            if (HasRecoveryWaitExpired(_restoreWait))
            {
                ClearUpdateState();
                UPilotUpdateService.SetOperationFailed("Package update recovery timed out while waiting for the Editor. Inspect the current package and service state before retrying.");
                return;
            }
            if (!PackageStopAllowsRecovery(out var stopPending))
            {
                if (!stopPending) { ClearUpdateState(); UPilotUpdateService.SetOperationFailed("旧服务停止未确认，包恢复已结束，请检查原停止记录。"); }
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            EditorApplication.update -= WaitForEditorReady;
            _restartScheduled = false;
            TryRestoreAfterUpdate();
        }

        private static void TryRestoreAfterUpdate()
        {
            try
            {
                TryRestoreAfterUpdateCore();
            }
            catch (Exception ex)
            {
                ClearUpdateState();
                ReportLifecycleError("UPilot 包更新恢复失败", ex);
            }
        }

        private static void TryRestoreAfterUpdateCore()
        {
            if (!GetSessionBool(UpdateCompletedKey, false))
                return;

            _restoreWait = GetOrCreateRecoveryWait(RestoreWaitKey);
            if (UPilotQuickStart.IsExplicitlyStopped || HasRecoveryWaitExpired(_restoreWait))
            {
                bool expired = HasRecoveryWaitExpired(_restoreWait);
                ClearUpdateState();
                if (expired) UPilotUpdateService.SetOperationFailed("Package update recovery timed out while waiting for the Editor.");
                return;
            }
            if (!PackageStopAllowsRecovery(out var stopPending))
            {
                if (stopPending) ScheduleRestore();
                else { ClearUpdateState(); UPilotUpdateService.SetOperationFailed("旧服务停止未确认，包恢复已结束，请检查原停止记录。"); }
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                ScheduleRestore();
                return;
            }

            var shouldRestart = GetSessionBool(RestartPendingKey, false);
            var targetVersion = GetSessionString(TargetVersionKey, "");
            var installManagedServer = GetSessionBool(ManagedServerPendingKey, false);
            var targetServerVersion = GetSessionString(TargetServerVersionKey, "");
            var preparedServerPath = GetSessionString(PreparedServerPathKey, "");
            if (!shouldRestart || !UPilotSetupState.IsCompleted)
            {
                ClearUpdateState();
                ClearPendingManagedPackageUpdate();
                UPilotUpdateService.ClearExternalPackageManagerAbort();
                UPilotUpdateService.SetOperationCompleted("UPilot 包已更新");
                return;
            }

            // 先消费恢复请求，避免 update 与已排队的 delayCall 在异步服务验证期间重复启动。
            var recovery = _restoreWait;
            ClearUpdateState(preserveRecoveryWait: true);
            UPilotProjectConfig.Reload();
            UPilotProjectConfig.ApplyEndpoints(UPilotBridge.Instance);
            var agentConfigurationRefresh = UPilotAgentSetup.RefreshManagedConfigurationAfterPackageUpdate();
            if (!string.Equals(agentConfigurationRefresh, "No changes needed.", StringComparison.Ordinal))
                Debug.Log("[UPilot] 已自动刷新更新后的 Agent 配置：\n" + agentConfigurationRefresh);
            if (installManagedServer && UPilotServerRuntimeService.Instance.GetConfiguredMode() ==
                UPilotServerRuntimeMode.StandaloneExe)
            {
                ContinueManagedServerUpdateAfterPackage(
                    targetVersion,
                    targetServerVersion,
                    preparedServerPath,
                    shouldRestart,
                    recovery);
                return;
            }

            ClearPendingManagedPackageUpdate();
            UPilotUpdateService.ClearExternalPackageManagerAbort();
            var manager = UPilotMcpServerManager.Instance;
            manager.ValidateAndAutoFixPath();
            RestoreUpdatedServicesAsync(targetVersion, recovery);
        }

        private static async void RestoreUpdatedServicesAsync(string targetVersion, RecoveryWait recovery)
        {
            UPilotUpdateService.SetOperationPhase(UPilotUpdateOperationPhase.RestartingService,
                "UPilot 包已更新，正在校验并恢复配套服务", targetVersion);
            var result = await UPilotQuickStart.AutoRepairAsync(null);
            if (!IsCurrentRecoveryWait(RestoreWaitKey, recovery) || UPilotQuickStart.IsExplicitlyStopped) return;
            ClearUpdateState();
            if (UPilotQuickStart.LastRepairSucceeded)
                UPilotUpdateService.SetOperationCompleted("UPilot 包已更新并已恢复服务");
            else
                UPilotUpdateService.SetOperationFailed(result);
            Debug.Log($"[UPilot] Package update ({targetVersion}): {result}");
        }

        private static void ReportLifecycleError(string context, Exception ex)
        {
            var message = context + "：" + ex.Message;
            UPilotUpdateService.SetOperationFailed(message);
            Debug.LogError("[UPilot] " + message + "\n" + ex);
        }

        private static async void ContinueManagedServerUpdateAfterPackage(
            string targetVersion,
            string targetServerVersion,
            string preparedServerPath,
            bool shouldRestart,
            RecoveryWait recovery)
        {
            try
            {
                UPilotUpdateService.SetOperationPhase(
                    UPilotUpdateOperationPhase.DownloadingService,
                    string.IsNullOrWhiteSpace(preparedServerPath)
                        ? "UPilot 包已更新，正在下载匹配的 MCP 服务…"
                        : "UPilot 包已更新，正在启用已准备好的 MCP 服务…",
                    targetVersion,
                    targetServerVersion);
                var updated = string.IsNullOrWhiteSpace(preparedServerPath)
                    ? await UPilotUpdateService.Instance.InstallManagedServerAfterPackageUpdateAsync(
                        targetServerVersion,
                        shouldRestart,
                        startupDeadlineUtcMs: recovery.deadlineUtcMs)
                    : await UPilotUpdateService.Instance.ActivatePreparedManagedServerAfterPackageUpdateAsync(
                        preparedServerPath,
                        targetServerVersion,
                        shouldRestart,
                        startupDeadlineUtcMs: recovery.deadlineUtcMs);
                if (!IsCurrentRecoveryWait(RestoreWaitKey, recovery) || UPilotQuickStart.IsExplicitlyStopped) return;
                ClearUpdateState();
                ClearPendingManagedPackageUpdate();
                UPilotUpdateService.ClearExternalPackageManagerAbort();
                if (updated)
                    Debug.Log($"[UPilot] Package update completed ({targetVersion}); managed MCP service updated.");
            }
            catch (Exception ex)
            {
                if (!IsCurrentRecoveryWait(RestoreWaitKey, recovery) || UPilotQuickStart.IsExplicitlyStopped) return;
                ClearUpdateState();
                UPilotUpdateService.ClearExternalPackageManagerAbort();
                UPilotUpdateService.SetOperationFailed("MCP 服务更新失败：" + ex.Message);
                Debug.LogError("[UPilot] Managed MCP service update after package update failed: " + ex);
            }
        }

        private static bool ShouldInstallManagedServerAfterPackageUpdate()
        {
            return UPilotSetupState.IsCompleted &&
                   UPilotServerRuntimeService.Instance.GetConfiguredMode() ==
                   UPilotServerRuntimeMode.StandaloneExe;
        }

        private static void ClearUpdateState(bool preserveRecoveryWait = false)
        {
            EraseSessionString(PackageStopKey);
            EditorApplication.update -= WaitForEditorReady;
            _restartScheduled = false;
            if (!preserveRecoveryWait)
            {
                _restoreWait = null;
                EraseSessionString(RestoreWaitKey);
            }
            EraseSessionBool(UpdateInProgressKey);
            EraseSessionBool(RestartPendingKey);
            EraseSessionBool(UpdateCompletedKey);
            EraseSessionString(TargetVersionKey);
            EraseSessionBool(ManagedServerPendingKey);
            EraseSessionString(TargetServerVersionKey);
            EraseSessionString(PreparedServerPathKey);
        }

        private static string ProjectPersistentKey(string key)
        {
            return UPilotPreferences.ProjectKey(key);
        }

        private static bool GetSessionBool(string key, bool defaultValue)
        {
            return SessionState.GetBool(ProjectSessionKey(key), defaultValue);
        }

        private static void SetSessionBool(string key, bool value)
        {
            SessionState.SetBool(ProjectSessionKey(key), value);
        }

        private static void EraseSessionBool(string key)
        {
            SessionState.EraseBool(ProjectSessionKey(key));
        }

        private static string GetSessionString(string key, string defaultValue)
        {
            return SessionState.GetString(ProjectSessionKey(key), defaultValue);
        }

        private static void SetSessionString(string key, string value)
        {
            SessionState.SetString(ProjectSessionKey(key), value);
        }

        private static void EraseSessionString(string key)
        {
            SessionState.EraseString(ProjectSessionKey(key));
        }

        private static string ProjectSessionKey(string key)
        {
            return UPilotPreferences.ProjectKey(key);
        }
    }
}
