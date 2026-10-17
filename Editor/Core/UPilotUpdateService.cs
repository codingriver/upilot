// -----------------------------------------------------------------------
// UPilot Editor - release manifest update helpers.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace CodingRiver.UPilot
{
    internal enum UPilotUpdateOperationPhase
    {
        None,
        StoppingService,
        UpdatingPackage,
        WaitingForReload,
        DownloadingService,
        RestartingService,
        Completed,
        Failed,
    }

    internal enum UPilotOccupancyRetryKind
    {
        None,
        Package,
        ManagedServer,
        PackageAndManagedServer,
        ExternalPackageManager,
    }

    internal readonly struct UPilotUpdateOperationStatus
    {
        public UPilotUpdateOperationStatus(
            UPilotUpdateOperationPhase phase,
            string label,
            string message,
            string targetUpmVersion,
            string targetServerVersion, string operationId = "")
        {
            Phase = phase;
            Label = label ?? "";
            Message = message ?? "";
            TargetUpmVersion = targetUpmVersion ?? "";
            TargetServerVersion = targetServerVersion ?? "";
            OperationId = operationId ?? "";
        }

        public UPilotUpdateOperationPhase Phase { get; }
        public string Label { get; }
        public string Message { get; }
        public string TargetUpmVersion { get; }
        public string TargetServerVersion { get; }
        public string OperationId { get; }

        public bool IsRunning =>
            Phase != UPilotUpdateOperationPhase.None &&
            Phase != UPilotUpdateOperationPhase.Completed &&
            Phase != UPilotUpdateOperationPhase.Failed;

        public bool BlocksServiceStart => UPilotUpdateService.IsServiceStartBlockedPhase(Phase);
    }

    internal readonly struct UPilotUpdateReloadGuardStatus
    {
        public UPilotUpdateReloadGuardStatus(
            bool isActive,
            bool isCurrentRuntime,
            string phase,
            string failureMessage,
            long sinceUnixMs)
        {
            IsActive = isActive;
            IsCurrentRuntime = isCurrentRuntime;
            Phase = phase ?? "";
            FailureMessage = failureMessage ?? "";
            SinceUnixMs = sinceUnixMs;
        }

        public bool IsActive { get; }
        public bool IsCurrentRuntime { get; }
        public string Phase { get; }
        public string FailureMessage { get; }
        public long SinceUnixMs { get; }
        public bool HasFailure => !string.IsNullOrWhiteSpace(FailureMessage);
        public bool NeedsRepair => HasFailure || (IsActive && !IsCurrentRuntime);
    }

    internal sealed class UPilotReleaseUpdateCheckStatus
    {
        public bool IsChecking;
        public bool HasUpdate;
        public bool IsSuppressed;
        public string LatestVersion = "";
        public string CurrentUpmVersion = "";
        public string CurrentServerVersion = "";
        public string Message = "";
        public string ErrorMessage = "";
    }

    public sealed class UPilotUpdateService
    {
        public static UPilotUpdateService Instance { get; } = new();

        internal const string ServiceStartBlockedMessage = "UPilot 正在更新，服务暂不可启动";
        internal const string ServicePausedForUpdateMessage = "UPilot 正在更新，MCP 服务已临时暂停，完成后会自动恢复。";
        internal const string ForceStoppingServiceMessage = "UPilot 正在更新，检测到服务仍在运行，正在强制停止服务…";
        internal const string ForceStopFailedMessage = "更新期间服务未能停止，请稍候或查看更新中心。";

        private const string OperationPhaseKey = "CodingRiver.UPilot.UpdateService.Phase";
        private const string OperationMessageKey = "CodingRiver.UPilot.UpdateService.Message";
        private const string OperationTargetUpmKey = "CodingRiver.UPilot.UpdateService.TargetUpm";
        private const string OperationTargetServerKey = "CodingRiver.UPilot.UpdateService.TargetServer";
        private const string OperationRuntimeIdKey = "CodingRiver.UPilot.UpdateService.RuntimeId";
        private const string OperationIdKey = "CodingRiver.UPilot.UpdateService.OperationId";
        internal static event Action OperationStatusChanged;
        private const string ReloadGuardActiveKey = "CodingRiver.UPilot.UpdateService.ReloadGuardActive";
        private const string ReloadGuardRuntimeIdKey = "CodingRiver.UPilot.UpdateService.ReloadGuardRuntimeId";
        private const string ReloadGuardPhaseKey = "CodingRiver.UPilot.UpdateService.ReloadGuardPhase";
        private const string ReloadGuardSinceUnixMsKey = "CodingRiver.UPilot.UpdateService.ReloadGuardSinceUnixMs";
        private const string ReloadGuardFailureKey = "CodingRiver.UPilot.UpdateService.ReloadGuardFailure";
        private const string ReloadGuardAutoRefreshBlockedKey = "CodingRiver.UPilot.UpdateService.ReloadGuardAutoRefreshBlocked";
        private const string ReloadGuardAssemblyReloadLockedKey = "CodingRiver.UPilot.UpdateService.ReloadGuardAssemblyReloadLocked";
        private const string ExternalPackageManagerAbortKey = "CodingRiver.UPilot.UpdateService.ExternalPackageManagerAbort";
        private const string OccupancyRecoveryPendingKey = "CodingRiver.UPilot.UpdateService.OccupancyRecoveryPending";
        private const string OccupancyRecoveryKindKey = "CodingRiver.UPilot.UpdateService.OccupancyRecoveryKind";
        private const string OccupancyRecoveryIdentifierKey = "CodingRiver.UPilot.UpdateService.OccupancyRecoveryIdentifier";
        private const string OccupancyRecoveryMessageKey = "CodingRiver.UPilot.UpdateService.OccupancyRecoveryMessage";
        private const string SkipReleaseReminderVersionKey = "CodingRiver.UPilot.UpdateService.SkipReleaseReminderVersion";
        private static readonly string RuntimeId = Guid.NewGuid().ToString("N");
        private static bool _reloadGuardActive;
        private static bool _reloadGuardAutoRefreshBlocked;
        private static bool _reloadGuardAssemblyReloadLocked;

        private UPilotUpmRequestObservation _upmObservation;
        private Action<string, MessageType> _notice;
        private bool _releaseCheckRunning;
        private bool _releaseCheckCompleted;
        private UPilotReleaseUpdateCheckStatus _releaseCheckStatus = new();
        private static UPilotOccupancyScanResult _occupancyRecoveryScan;

        static UPilotUpdateService()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting -= OnEditorQuitting;
            EditorApplication.quitting += OnEditorQuitting;
        }

        internal static string[] GetPreferenceKeysForCurrentProject()
        {
            return new[] { ProjectKey(SkipReleaseReminderVersionKey) };
        }

        public void CheckForUpdates(Action<string, MessageType> notice)
        {
            try
            {
                if (UPilotServerRuntimeService.IsSourceUpdateChannel())
                    ResetSourceChannelState();

                if (IsUpdateRunning)
                {
                    // Do not start a release manifest check while an update is active; show progress only.
                    if (UPilotUpdateWindow.OpenActiveUpdate(notice))
                        notice?.Invoke("UPilot 正在更新，已显示当前进度，未重新检查更新。", MessageType.Info);
                    return;
                }

                UPilotUpdateWindow.Open(notice);
            }
            catch (Exception ex)
            {
                var message = "打开更新中心失败：" + ex.Message;
                ReportUpdateFailure(message, ex);
                try
                {
                    notice?.Invoke(message, MessageType.Error);
                }
                catch (Exception noticeEx)
                {
                    ReportUpdateFailure("更新错误通知失败", noticeEx);
                }
            }
        }

        internal bool IsUpdateRunning =>
            !UPilotServerRuntimeService.IsSourceUpdateChannel() &&
            GetOperationStatus().IsRunning;

        internal bool IsServiceStartBlocked =>
            !UPilotServerRuntimeService.IsSourceUpdateChannel() &&
            GetOperationStatus().BlocksServiceStart;

        internal bool HasActiveUpdateWorkForExternalPackageManagerUpdate()
        {
            if (UPilotServerRuntimeService.IsSourceUpdateChannel())
                return false;

            var status = GetOperationStatus();
            var guard = GetReloadGuardStatus();
            var downloadState = UPilotServerRuntimeService.Instance.DownloadState;
            return status.IsRunning ||
                   downloadState.IsRunning ||
                   guard.IsActive ||
                   guard.NeedsRepair;
        }

        internal string AbortForExternalPackageManagerUpdate(string targetVersion)
        {
            var status = GetOperationStatus();
            var guard = GetReloadGuardStatus();
            var downloadState = UPilotServerRuntimeService.Instance.DownloadState;
            var targetText = string.IsNullOrWhiteSpace(targetVersion) ? "未知版本" : targetVersion;
            var message =
                $"检测到你在 UPilot 更新过程中通过 Unity Package Manager 更新 UPilot 包（目标：{targetText}）。" +
                "已取消当前 UPilot 更新任务并释放更新保护，正在尝试切换为 Package Manager 更新恢复流程。";

            CancelUpmObservation("external_package_manager_takeover");
            SessionState.SetBool(ProjectKey(ExternalPackageManagerAbortKey), true);
            if (downloadState.IsRunning)
            {
                Debug.LogWarning("[UPilot] Cancelling active MCP download because Package Manager started an UPilot package update.");
                UPilotServerRuntimeService.Instance.CancelDownload();
            }

            ReportUpdateFailure(
                "[UPilot] External Package Manager update started during an active UPilot update." +
                $"\nTargetVersion={targetText}; phase={status.Phase}; label={status.Label}; downloadRunning={downloadState.IsRunning}" +
                $"\nreloadGuardActive={guard.IsActive}; reloadGuardCurrentRuntime={guard.IsCurrentRuntime}; reloadGuardPhase={guard.Phase}" +
                "\n" + message);
            SetOperationFailed(message);
            return message;
        }

        internal static bool IsExternalPackageManagerAbortActive()
        {
            return SessionState.GetBool(ProjectKey(ExternalPackageManagerAbortKey), false);
        }

        internal static void ClearExternalPackageManagerAbort()
        {
            SessionState.EraseBool(ProjectKey(ExternalPackageManagerAbortKey));
        }

        internal bool HasOccupancyRecovery =>
            SessionState.GetBool(ProjectKey(OccupancyRecoveryPendingKey), false);

        internal UPilotOccupancyScanResult GetOccupancyRecoveryScan(bool refresh = false)
        {
            if (!HasOccupancyRecovery)
                return null;
            if (refresh || _occupancyRecoveryScan == null)
            {
                _occupancyRecoveryScan = UPilotProcessOccupancyService.Scan(
                    UPilotMcpServerManager.Instance.GetUpdateOccupancyResourcePaths());
            }
            return _occupancyRecoveryScan;
        }

        internal bool BeginOccupancyRecovery(
            UPilotOccupancyRetryKind retryKind,
            string retryIdentifier,
            string failureMessage)
        {
            var scan = UPilotProcessOccupancyService.Scan(
                UPilotMcpServerManager.Instance.GetUpdateOccupancyResourcePaths());
            if (scan.Processes.Count == 0)
                return false;

            _occupancyRecoveryScan = scan;
            SessionState.SetBool(ProjectKey(OccupancyRecoveryPendingKey), true);
            SessionState.SetString(ProjectKey(OccupancyRecoveryKindKey), retryKind.ToString());
            SessionState.SetString(ProjectKey(OccupancyRecoveryIdentifierKey), retryIdentifier ?? "");
            SessionState.SetString(ProjectKey(OccupancyRecoveryMessageKey), failureMessage ?? "");
            ReportUpdateFailure(
                "[UPilot] 更新被占用进程阻止，等待用户二次确认后统一结束并重试。" +
                $"\nReason={failureMessage}\nRetry={retryKind}" +
                "\nProcesses=" + string.Join(", ", scan.Processes.Select(process =>
                    $"{process.DisplayName}(PID {process.ProcessId})")));
            return true;
        }

        internal string GetOccupancyRecoveryMessage()
        {
            return SessionState.GetString(ProjectKey(OccupancyRecoveryMessageKey), "更新被占用进程阻止。");
        }

        internal void CancelOccupancyRecovery()
        {
            var reason = GetOccupancyRecoveryMessage();
            ClearOccupancyRecovery();
            SetOperationFailed("已取消更新：" + reason);
            TryRestoreServiceAfterOccupancyCancellation();
            Debug.LogWarning("[UPilot] 用户取消占用进程恢复。\nReason=" + reason);
        }

        internal bool ConfirmTerminateOccupiersAndRetry(out string message)
        {
            message = "";
            var scan = GetOccupancyRecoveryScan(refresh: true);
            if (scan == null || scan.Processes.Count == 0)
            {
                message = "未再检测到占用进程；请重新执行更新。";
                ClearOccupancyRecovery();
                SetOperationFailed(message);
                return false;
            }
            if (!scan.HasTerminableProcesses)
            {
                message = "检测到占用进程，但当前用户没有权限结束它们。请关闭相关程序后重新更新。";
                SetOperationFailed(message);
                ReportUpdateFailure("[UPilot] " + message + "\n" + UPilotProcessOccupancyService.BuildConfirmationMessage(scan));
                return false;
            }

            if (!EditorUtility.DisplayDialog(
                    "结束全部占用进程并重试？",
                    UPilotProcessOccupancyService.BuildConfirmationMessage(scan),
                    "结束全部并重试",
                    "取消"))
            {
                message = "已取消结束占用进程。";
                return false;
            }

            var termination = UPilotProcessOccupancyService.TerminateAll(scan);
            _occupancyRecoveryScan = UPilotProcessOccupancyService.Scan(
                UPilotMcpServerManager.Instance.GetUpdateOccupancyResourcePaths());
            if (termination.Failed.Count > 0 || _occupancyRecoveryScan.Processes.Count > 0)
            {
                var stillRunning = _occupancyRecoveryScan.Processes
                    .Select(process => process.DisplayName + " (PID " + process.ProcessId + ") 仍在运行");
                message = "结束占用进程后仍无法继续更新：" +
                          string.Join("；", termination.Failed.Concat(stillRunning));
                SetOperationFailed(message);
                ReportUpdateFailure("[UPilot] " + message);
                return false;
            }

            var retryKind = ReadOccupancyRetryKind();
            var identifier = SessionState.GetString(ProjectKey(OccupancyRecoveryIdentifierKey), "");
            ClearOccupancyRecovery();
            Debug.LogWarning("[UPilot] 已结束全部占用进程，自动重试一次更新。\nRetry=" + retryKind);
            StartOccupancyRecoveryRetry(retryKind, identifier);
            message = "已结束全部占用进程，正在自动重试更新…";
            return true;
        }

        private void StartOccupancyRecoveryRetry(UPilotOccupancyRetryKind retryKind, string identifier)
        {
            switch (retryKind)
            {
                case UPilotOccupancyRetryKind.Package:
                    UpdateUpmFromManifest(null);
                    break;
                case UPilotOccupancyRetryKind.ManagedServer:
                    UpdateManagedServerAndRestart(null);
                    break;
                case UPilotOccupancyRetryKind.PackageAndManagedServer:
                    UpdateFromManifest(true, true, null);
                    break;
                case UPilotOccupancyRetryKind.ExternalPackageManager:
                    RetryExternalPackageManagerUpdate(identifier);
                    break;
                default:
                    SetOperationFailed("无法确定占用恢复后的更新目标，请重新执行更新。");
                    break;
            }
        }

        private async void RetryExternalPackageManagerUpdate(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                SetOperationFailed("无法读取 Unity Package Manager 的原始更新目标，请在 Package Manager 中重新更新。");
                return;
            }
            if (!await UPilotPackageUpdateLifecycle.PrepareForPackageUpdateAsync(
                    "", null, false, "", "",
                    UPilotOccupancyRetryKind.ExternalPackageManager, identifier))
                return;
            try
            {
                SetOperationPhase(UPilotUpdateOperationPhase.UpdatingPackage, "正在重试 Unity Package Manager 更新…");
                BeginUpmObservation(identifier);
            }
            catch (Exception ex)
            {
                SetOperationFailed("重试 Unity Package Manager 更新失败：" + ex.Message);
                ReportUpdateFailure("Retry external Package Manager update failed.", ex);
            }
        }

        private static void TryRestoreServiceAfterOccupancyCancellation()
        {
            try
            {
                if (UPilotSetupState.IsCompleted)
                {
                    UPilotMcpServerManager.Instance.StartServer();
                    UPilotBridge.Instance.EnsureStarted();
                }
            }
            catch (Exception ex)
            {
                ReportUpdateFailure("取消占用恢复后恢复服务失败。", ex);
            }
        }

        private static UPilotOccupancyRetryKind ReadOccupancyRetryKind()
        {
            var raw = SessionState.GetString(ProjectKey(OccupancyRecoveryKindKey), "");
            return Enum.TryParse(raw, out UPilotOccupancyRetryKind kind) ? kind : UPilotOccupancyRetryKind.None;
        }

        internal static void ClearOccupancyRecovery()
        {
            _occupancyRecoveryScan = null;
            SessionState.EraseBool(ProjectKey(OccupancyRecoveryPendingKey));
            SessionState.EraseString(ProjectKey(OccupancyRecoveryKindKey));
            SessionState.EraseString(ProjectKey(OccupancyRecoveryIdentifierKey));
            SessionState.EraseString(ProjectKey(OccupancyRecoveryMessageKey));
        }

        internal void EnsureLatestReleaseCheck(bool force = false)
        {
            if (UPilotServerRuntimeService.IsSourceUpdateChannel())
            {
                ResetSourceChannelState();
                return;
            }

            if (!force && (_releaseCheckRunning || _releaseCheckCompleted))
                return;
            if (GetOperationStatus().IsRunning || UPilotServerRuntimeService.Instance.DownloadState.IsRunning)
                return;

            _ = CheckLatestReleaseSilentlyAsync();
        }

        internal UPilotReleaseUpdateCheckStatus GetLatestReleaseCheckStatus()
        {
            return new UPilotReleaseUpdateCheckStatus
            {
                IsChecking = _releaseCheckStatus.IsChecking,
                HasUpdate = _releaseCheckStatus.HasUpdate,
                IsSuppressed = IsReleaseReminderSuppressed(_releaseCheckStatus.LatestVersion),
                LatestVersion = _releaseCheckStatus.LatestVersion,
                CurrentUpmVersion = _releaseCheckStatus.CurrentUpmVersion,
                CurrentServerVersion = _releaseCheckStatus.CurrentServerVersion,
                Message = _releaseCheckStatus.Message,
                ErrorMessage = _releaseCheckStatus.ErrorMessage,
            };
        }

        internal void SuppressLatestReleaseReminder(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return;

            EditorPrefs.SetString(ProjectKey(SkipReleaseReminderVersionKey), version.Trim());
            if (string.Equals(_releaseCheckStatus.LatestVersion, version.Trim(), StringComparison.OrdinalIgnoreCase))
                _releaseCheckStatus.IsSuppressed = true;
        }

        internal UPilotUpdateOperationStatus GetOperationStatus()
        {
            var phase = ReadOperationPhase();
            RecoverInterruptedReloadGuardIfNeeded(phase);
            var downloadState = UPilotServerRuntimeService.Instance.DownloadState;
            if (downloadState.IsRunning)
                phase = UPilotUpdateOperationPhase.DownloadingService;
            else if (IsMemoryBoundUpdatePhase(phase) && IsOperationFromPreviousRuntime())
            {
                FailStaleMemoryBoundOperation(phase);
                phase = UPilotUpdateOperationPhase.Failed;
            }

            var message = SessionState.GetString(ProjectKey(OperationMessageKey), "");
            if (downloadState.IsRunning)
                message = FormatDownloadProgressLabel(downloadState);

            return new UPilotUpdateOperationStatus(
                phase,
                BuildOperationLabel(phase),
                message,
                SessionState.GetString(ProjectKey(OperationTargetUpmKey), ""),
                SessionState.GetString(ProjectKey(OperationTargetServerKey), ""),
                SessionState.GetString(ProjectKey(OperationIdKey), ""));
        }

        internal static UPilotUpdateReloadGuardStatus GetReloadGuardStatus()
        {
            var active = _reloadGuardActive || SessionState.GetBool(ProjectKey(ReloadGuardActiveKey), false);
            var runtimeId = SessionState.GetString(ProjectKey(ReloadGuardRuntimeIdKey), "");
            var phase = SessionState.GetString(ProjectKey(ReloadGuardPhaseKey), "");
            var failure = SessionState.GetString(ProjectKey(ReloadGuardFailureKey), "");
            var sinceText = SessionState.GetString(ProjectKey(ReloadGuardSinceUnixMsKey), "0");
            long.TryParse(sinceText, out var sinceUnixMs);
            return new UPilotUpdateReloadGuardStatus(
                active,
                !string.IsNullOrWhiteSpace(runtimeId) &&
                string.Equals(runtimeId, RuntimeId, StringComparison.Ordinal),
                phase,
                failure,
                sinceUnixMs);
        }

        internal static string GetReloadGuardNotice(UPilotUpdateReloadGuardStatus status, bool compact)
        {
            if (status.HasFailure)
                return status.FailureMessage;
            if (!status.IsActive)
                return "";
            return compact
                ? "正在保护更新流程：Unity 自动刷新/脚本重载已暂时拦截，更新完成后会自动恢复。"
                : "正在保护更新流程：Unity 自动刷新/脚本重载已暂时拦截。修改代码会在更新完成后自动刷新/重载，请暂时不要手动刷新。";
        }

        internal static bool ShouldShowRepairUpdateStateButton(
            UPilotUpdateOperationStatus status,
            UPilotUpdateReloadGuardStatus guardStatus)
        {
            if (guardStatus.NeedsRepair)
                return true;
            return status.Phase == UPilotUpdateOperationPhase.Failed &&
                   status.Message.IndexOf("重载", StringComparison.Ordinal) >= 0;
        }

        internal static bool IsServiceStartBlockedPhase(UPilotUpdateOperationPhase phase)
        {
            return phase == UPilotUpdateOperationPhase.StoppingService ||
                   phase == UPilotUpdateOperationPhase.DownloadingService ||
                   phase == UPilotUpdateOperationPhase.UpdatingPackage ||
                   phase == UPilotUpdateOperationPhase.WaitingForReload;
        }

        private async Task CheckLatestReleaseSilentlyAsync()
        {
            if (_releaseCheckRunning)
                return;

            _releaseCheckRunning = true;
            _releaseCheckStatus = new UPilotReleaseUpdateCheckStatus { IsChecking = true };
            try
            {
                if (UPilotServerRuntimeService.IsSourceUpdateChannel())
                {
                    _releaseCheckStatus = new UPilotReleaseUpdateCheckStatus();
                    return;
                }

                var manifest = await UPilotServerRuntimeService.Instance.FetchReleaseManifestAsync();
                var currentUpm = UPilotServerRuntimeService.UpmVersion;
                var mode = UPilotServerRuntimeService.Instance.GetConfiguredMode();
                var currentServer = GetCurrentServerVersion(mode);
                var packageNeedsUpdate = UPilotServerRuntimeService.IsVersionNewer(manifest.UpmVersion, currentUpm);
                var serverNeedsUpdate = mode == UPilotServerRuntimeMode.StandaloneExe &&
                                        UPilotServerRuntimeService.IsVersionNewer(manifest.ServerVersion, currentServer);
                var latestVersion = packageNeedsUpdate && !string.IsNullOrWhiteSpace(manifest.UpmVersion)
                    ? manifest.UpmVersion
                    : manifest.ServerVersion;

                _releaseCheckStatus = new UPilotReleaseUpdateCheckStatus
                {
                    HasUpdate = packageNeedsUpdate || serverNeedsUpdate,
                    IsSuppressed = IsReleaseReminderSuppressed(latestVersion),
                    LatestVersion = latestVersion,
                    CurrentUpmVersion = currentUpm,
                    CurrentServerVersion = currentServer,
                    Message = BuildReleaseReminderMessage(
                        latestVersion,
                        currentUpm,
                        currentServer,
                        packageNeedsUpdate,
                        serverNeedsUpdate),
                };
            }
            catch (Exception ex)
            {
                LogUpdateError("自动检查更新失败：" + ex.Message, ex);
                _releaseCheckStatus = new UPilotReleaseUpdateCheckStatus
                {
                    ErrorMessage = ex.Message,
                };
            }
            finally
            {
                _releaseCheckStatus.IsChecking = false;
                _releaseCheckRunning = false;
                _releaseCheckCompleted = true;
            }
        }

        private static string GetCurrentServerVersion(UPilotServerRuntimeMode mode)
        {
            var statusVersion = UPilotMcpServerManager.Instance.GetStatus().ServerVersion;
            if (!string.IsNullOrWhiteSpace(statusVersion))
                return statusVersion;

            if (mode == UPilotServerRuntimeMode.StandaloneExe)
                return UPilotProjectConfig.Current.runtime?.serverVersion ?? "";
            return UPilotServerRuntimeService.UpmVersion;
        }

        private static bool IsReleaseReminderSuppressed(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return false;
            var skipped = EditorPrefs.GetString(ProjectKey(SkipReleaseReminderVersionKey), "");
            return string.Equals(skipped, version.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildReleaseReminderMessage(
            string latestVersion,
            string currentUpm,
            string currentServer,
            bool packageNeedsUpdate,
            bool serverNeedsUpdate)
        {
            if (packageNeedsUpdate && serverNeedsUpdate)
                return $"当前 {currentUpm}，最新 {latestVersion}，UPilot 包和 MCP 服务都有更新。";
            if (packageNeedsUpdate)
                return $"当前 {currentUpm}，最新 {latestVersion}，正式版可用。";
            if (serverNeedsUpdate)
                return $"当前服务 {currentServer}，最新 {latestVersion}，MCP 服务可更新。";
            return "";
        }

        private static bool CompleteIfSourceUpdateChannel(Action<string, MessageType> notice)
        {
            if (!UPilotServerRuntimeService.IsSourceUpdateChannel())
                return false;

            var message = "开发/source 通道仅使用本机 Python，不执行自动管理服务或包更新。正式对外版本请通过 v* tag 发布。";
            ResetSourceChannelState();
            notice?.Invoke(message, MessageType.Info);
            return true;
        }

        private static void HandleUnexpectedUpdateException(
            string context,
            Exception ex,
            Action<string, MessageType> notice)
        {
            var message = context + "：" + ex.Message;
            SetOperationFailed(message);
            LogUpdateError(message, ex);
            try
            {
                notice?.Invoke(message, MessageType.Error);
            }
            catch (Exception noticeEx)
            {
                ReportUpdateFailure("更新错误通知失败", noticeEx);
            }
        }

        private static void LogUpdateError(string message, Exception ex)
        {
            ReportUpdateFailure(message, ex);
        }

        private static string UpdateDiagnosticScope => "update|" + SessionState.GetString(ProjectKey(OperationIdKey), "");

        private static void ReportUpdateFailure(string message, Exception exception = null)
        {
            string operationId = SessionState.GetString(ProjectKey(OperationIdKey), "");
            var phase = ReadOperationPhase();
            Logger.ReportFailure("UPDATE", (operationId.Length == 0 ? "" : "operationId=" + operationId + " ") + message,
                exception, UpdateDiagnosticScope,
                terminal: phase == UPilotUpdateOperationPhase.Completed || phase == UPilotUpdateOperationPhase.Failed);
        }

        public async void UpdateManagedServerAndRestart(Action<string, MessageType> notice)
        {
            try
            {
                await UpdateManagedServerAndRestartAsync(notice);
            }
            catch (Exception ex)
            {
                HandleUnexpectedUpdateException("更新 MCP 服务失败", ex, notice);
            }
        }

        private async Task UpdateManagedServerAndRestartAsync(Action<string, MessageType> notice)
        {
            _notice = notice;
            if (CompleteIfSourceUpdateChannel(notice))
                return;

            SetOperationPhase(UPilotUpdateOperationPhase.StoppingService, "正在准备更新 MCP 服务…");
            UPilotReleaseManifest manifest;
            try
            {
                manifest = await UPilotServerRuntimeService.Instance.FetchReleaseManifestAsync();
            }
            catch (Exception ex)
            {
                var message = "读取发布清单失败：" + ex.Message;
                SetOperationFailed(message);
                LogUpdateError(message, ex);
                notice?.Invoke(message, MessageType.Error);
                return;
            }

            var currentUpm = UPilotServerRuntimeService.UpmVersion;
            if (!UPilotServerRuntimeService.IsVersionAtLeast(currentUpm, manifest.MinCompatibleUpm))
            {
                var message = $"请先将 UPilot 包更新到 {manifest.MinCompatibleUpm} 或更高版本";
                SetOperationFailed(message);
                notice?.Invoke(message, MessageType.Warning);
                return;
            }

            await InstallManagedServerAndRestartAsync(
                manifest.ServerVersion,
                shouldRestart: UPilotSetupState.IsCompleted,
                stopFirst: true,
                notice: notice);
        }

        public async void UpdateFromManifest(
            bool updatePackage,
            bool updateManagedServer,
            Action<string, MessageType> notice)
        {
            try
            {
                await UpdateFromManifestAsync(updatePackage, updateManagedServer, notice);
            }
            catch (Exception ex)
            {
                HandleUnexpectedUpdateException("更新失败", ex, notice);
            }
        }

        private async Task UpdateFromManifestAsync(
            bool updatePackage,
            bool updateManagedServer,
            Action<string, MessageType> notice)
        {
            _notice = notice;
            if (CompleteIfSourceUpdateChannel(notice))
                return;

            SetOperationPhase(UPilotUpdateOperationPhase.StoppingService, "正在读取更新信息…");
            UPilotReleaseManifest manifest;
            try
            {
                manifest = await UPilotServerRuntimeService.Instance.FetchReleaseManifestAsync();
            }
            catch (Exception ex)
            {
                var message = "读取发布清单失败：" + ex.Message;
                SetOperationFailed(message);
                LogUpdateError(message, ex);
                notice?.Invoke(message, MessageType.Error);
                return;
            }

            SetOperationTargets(manifest.UpmVersion, manifest.ServerVersion);
            if (updatePackage)
            {
                var preparedServerPath = "";
                if (updateManagedServer)
                {
                    var prepared = await InstallManagedServerBeforePackageUpdateAsync(manifest, notice);
                    if (prepared == null)
                        return;
                    preparedServerPath = prepared.TargetPath;
                }

                await StartPackageUpdateAsync(manifest, updateManagedServer, notice, preparedServerPath);
                return;
            }

            if (updateManagedServer)
            {
                var currentUpm = UPilotServerRuntimeService.UpmVersion;
                if (!UPilotServerRuntimeService.IsVersionAtLeast(currentUpm, manifest.MinCompatibleUpm))
                {
                    var message = $"请先将 UPilot 包更新到 {manifest.MinCompatibleUpm} 或更高版本";
                    SetOperationFailed(message);
                    notice?.Invoke(message, MessageType.Warning);
                    return;
                }

                await InstallManagedServerAndRestartAsync(
                    manifest.ServerVersion,
                    shouldRestart: UPilotSetupState.IsCompleted,
                    stopFirst: true,
                    notice: notice);
                return;
            }

            SetOperationCompleted("已是最新版本");
            notice?.Invoke("已是最新版本", MessageType.Info);
        }

        internal async Task<bool> InstallManagedServerAfterPackageUpdateAsync(
            string expectedServerVersion,
            bool shouldRestart,
            Action<string, MessageType> notice = null,
            long startupDeadlineUtcMs = 0)
        {
            if (CompleteIfSourceUpdateChannel(notice))
                return false;

            if (string.IsNullOrWhiteSpace(expectedServerVersion))
            {
                try
                {
                    var manifest = await UPilotServerRuntimeService.Instance.FetchReleaseManifestAsync();
                    expectedServerVersion = manifest.ServerVersion;
                    SetOperationTargets(UPilotServerRuntimeService.UpmVersion, manifest.ServerVersion);
                }
                catch (Exception ex)
                {
                    var message = "读取发布清单失败：" + ex.Message;
                    SetOperationFailed(message);
                    LogUpdateError(message, ex);
                    notice?.Invoke(message, MessageType.Error);
                    return false;
                }
            }

            return await InstallManagedServerAndRestartAsync(
                expectedServerVersion,
                shouldRestart,
                stopFirst: false,
                notice: notice,
                startupDeadlineUtcMs: startupDeadlineUtcMs);
        }

        internal async Task<bool> ActivatePreparedManagedServerAfterPackageUpdateAsync(
            string preparedServerPath,
            string expectedServerVersion,
            bool shouldRestart,
            Action<string, MessageType> notice = null,
            long startupDeadlineUtcMs = 0)
        {
            if (CompleteIfSourceUpdateChannel(notice))
                return false;

            if (string.IsNullOrWhiteSpace(expectedServerVersion))
            {
                try
                {
                    var manifest = await UPilotServerRuntimeService.Instance.FetchReleaseManifestAsync();
                    expectedServerVersion = manifest.ServerVersion;
                    SetOperationTargets(UPilotServerRuntimeService.UpmVersion, manifest.ServerVersion);
                }
                catch (Exception ex)
                {
                    var message = "读取发布清单失败：" + ex.Message;
                    SetOperationFailed(message);
                    LogUpdateError(message, ex);
                    notice?.Invoke(message, MessageType.Error);
                    UPilotMainWindow.OpenWithNotice(
                        "UPilot 包已更新，但服务版本确认失败。请在主窗口手动启动或重启服务。",
                        MessageType.Warning);
                    return false;
                }
            }

            SetOperationPhase(
                UPilotUpdateOperationPhase.RestartingService,
                "正在启用已准备好的 MCP 服务…",
                targetServerVersion: expectedServerVersion);

            if (!UPilotServerRuntimeService.Instance.ActivatePreparedStandaloneExe(
                    preparedServerPath,
                    expectedServerVersion,
                    out var activationError))
            {
                var message = activationError;
                SetOperationFailed(message);
                notice?.Invoke(message, MessageType.Error);
                UPilotMainWindow.OpenWithNotice(
                    "UPilot 包已更新，但服务文件启用失败。请在主窗口手动启动或重启服务。",
                    MessageType.Error);
                return false;
            }

            if (!shouldRestart)
            {
                var message = "MCP 服务文件已准备好，将在完成首次设置后启动";
                SetOperationCompleted(message);
                notice?.Invoke(message, MessageType.Info);
                return true;
            }

            return await RestartAndConfirmManagedServerAsync(
                expectedServerVersion,
                notice,
                failureNotice: "UPilot 包已更新，但服务未能自动启动。请在主窗口点击启动或重启服务。",
                startupDeadlineUtcMs: startupDeadlineUtcMs);
        }

        private async Task<UPilotPreparedServerDownload> InstallManagedServerBeforePackageUpdateAsync(
            UPilotReleaseManifest manifest,
            Action<string, MessageType> notice)
        {
            if (CompleteIfSourceUpdateChannel(notice))
                return null;

            SetOperationPhase(
                UPilotUpdateOperationPhase.DownloadingService,
                "正在下载并验证 MCP 服务，完成后将更新 UPilot 包…",
                manifest.UpmVersion,
                manifest.ServerVersion);
            notice?.Invoke("正在下载 MCP 服务…", MessageType.Info);

            try
            {
                UPilotServerRuntimeService.Instance.GetConfiguredStandaloneRuntime(
                    out var oldServerPath,
                    out var oldServerVersion);
                var prepared = await UPilotServerRuntimeService.Instance.PrepareLatestServerExeAsync(manifest);
                if (prepared == null || string.IsNullOrWhiteSpace(prepared.TargetPath))
                    throw new InvalidOperationException("MCP 服务文件未准备完成。");

                SetOperationPhase(
                    UPilotUpdateOperationPhase.RestartingService,
                    "MCP 服务已下载，正在切换到新服务…",
                    manifest.UpmVersion,
                    manifest.ServerVersion);
                if (!UPilotServerRuntimeService.Instance.ActivatePreparedStandaloneExe(
                        prepared.TargetPath,
                        manifest.ServerVersion,
                        out var activationError))
                {
                    throw new InvalidOperationException(activationError);
                }

                UPilotPackageUpdateLifecycle.MarkManagedPackageUpdatePending(
                    manifest.UpmVersion,
                    manifest.ServerVersion,
                    prepared.TargetPath,
                    oldServerPath,
                    oldServerVersion);

                SetOperationPhase(
                    UPilotUpdateOperationPhase.StoppingService,
                    "MCP 服务已更新，正在准备更新 UPilot 包…",
                    manifest.UpmVersion,
                    manifest.ServerVersion);
                notice?.Invoke("MCP 服务已更新，正在更新 UPilot 包…", MessageType.Info);
                return prepared;
            }
            catch (OperationCanceledException)
            {
                if (IsExternalPackageManagerAbortActive())
                {
                    Debug.LogWarning("[UPilot] MCP 服务下载已因 Package Manager 外部包更新而取消。");
                    notice?.Invoke("已取消 MCP 服务下载，转由 Package Manager 更新 UPilot 包。", MessageType.Warning);
                    return null;
                }

                SetOperationCompleted("已取消 MCP 服务下载");
                notice?.Invoke("已取消 MCP 服务下载", MessageType.Info);
                return null;
            }
            catch (Exception ex)
            {
                var message = "MCP 服务更新失败：" + ex.Message;
                SetOperationFailed(message);
                BeginOccupancyRecovery(
                    UPilotOccupancyRetryKind.PackageAndManagedServer,
                    "",
                    message);
                LogUpdateError(message, ex);
                notice?.Invoke(message, MessageType.Error);
                return null;
            }
        }

        internal static async Task WaitForDownloadCompletionAsync()
        {
            while (UPilotServerRuntimeService.Instance.DownloadState.IsRunning)
                await Task.Delay(200);
        }

        internal static void SetOperationPhase(
            UPilotUpdateOperationPhase phase,
            string message = "",
            string targetUpmVersion = null,
            string targetServerVersion = null)
        {
            SetOperationPhaseCore(phase, message, targetUpmVersion, targetServerVersion, applyReloadGuard: true);
        }

        private static void SetOperationPhaseCore(
            UPilotUpdateOperationPhase phase,
            string message,
            string targetUpmVersion,
            string targetServerVersion,
            bool applyReloadGuard)
        {
            var previous = ReadOperationPhase();
            if (phase != UPilotUpdateOperationPhase.None &&
                (previous == UPilotUpdateOperationPhase.None ||
                 ((previous == UPilotUpdateOperationPhase.Completed || previous == UPilotUpdateOperationPhase.Failed) &&
                  phase != UPilotUpdateOperationPhase.Completed && phase != UPilotUpdateOperationPhase.Failed)))
            {
                Logger.CloseDiagnostics(UpdateDiagnosticScope);
                SessionState.SetString(ProjectKey(OperationIdKey), Guid.NewGuid().ToString("N"));
            }
            SessionState.SetString(ProjectKey(OperationPhaseKey), phase.ToString());
            SessionState.SetString(ProjectKey(OperationMessageKey), message ?? "");
            if (targetUpmVersion != null)
                SessionState.SetString(ProjectKey(OperationTargetUpmKey), targetUpmVersion);
            if (targetServerVersion != null)
                SessionState.SetString(ProjectKey(OperationTargetServerKey), targetServerVersion);
            if (IsMemoryBoundUpdatePhase(phase))
                SessionState.SetString(ProjectKey(OperationRuntimeIdKey), RuntimeId);
            if (phase == UPilotUpdateOperationPhase.Completed || phase == UPilotUpdateOperationPhase.Failed)
                Logger.CloseDiagnostics(UpdateDiagnosticScope);
            OperationStatusChanged?.Invoke();

            if (!applyReloadGuard)
                return;

            var guardError = ApplyReloadGuardForPhase(phase);
            if (string.IsNullOrWhiteSpace(guardError))
                return;

            var failure = "拦截 Unity 脚本重载失败：" + guardError + "。已取消更新，请重新更新。";
            SetReloadGuardFailure(failure);
            ReportUpdateFailure("[UPilot] " + failure);
            UPilotPackageUpdateLifecycle.ResetUpdateStateForRepair(clearPendingPackageRetry: false);
            EndUpdateReloadGuard("guard enable failed", force: true, clearFailure: false);
            SetOperationPhaseCore(
                UPilotUpdateOperationPhase.Failed,
                failure,
                targetUpmVersion,
                targetServerVersion,
                applyReloadGuard: false);
        }

        internal static void SetOperationTargets(string targetUpmVersion, string targetServerVersion)
        {
            if (targetUpmVersion != null)
                SessionState.SetString(ProjectKey(OperationTargetUpmKey), targetUpmVersion);
            if (targetServerVersion != null)
                SessionState.SetString(ProjectKey(OperationTargetServerKey), targetServerVersion);
        }

        internal static void SetOperationCompleted(string message)
        {
            SetOperationPhase(UPilotUpdateOperationPhase.Completed, message);
        }

        internal static void SetOperationFailed(string message)
        {
            SetOperationPhase(UPilotUpdateOperationPhase.Failed, message);
        }

        internal static void ClearOperationStatus()
        {
            string diagnosticScope = UpdateDiagnosticScope;
            SessionState.EraseString(ProjectKey(OperationPhaseKey));
            SessionState.EraseString(ProjectKey(OperationMessageKey));
            SessionState.EraseString(ProjectKey(OperationTargetUpmKey));
            SessionState.EraseString(ProjectKey(OperationTargetServerKey));
            SessionState.EraseString(ProjectKey(OperationRuntimeIdKey));
            OperationStatusChanged?.Invoke();
            SessionState.EraseBool(ProjectKey(ExternalPackageManagerAbortKey));
            EndUpdateReloadGuard("operation status cleared", force: false, clearFailure: true);
            Logger.CloseDiagnostics(diagnosticScope);
            SessionState.EraseString(ProjectKey(OperationIdKey));
        }

        internal static void ResetSourceChannelState()
        {
            var phase = ReadOperationPhase();
            var guard = GetReloadGuardStatus();
            var downloadRunning = UPilotServerRuntimeService.Instance.DownloadState.IsRunning;
            var hadState = phase != UPilotUpdateOperationPhase.None ||
                           guard.IsActive ||
                           guard.HasFailure ||
                           downloadRunning ||
                           IsExternalPackageManagerAbortActive();

            if (downloadRunning)
                UPilotServerRuntimeService.Instance.CancelDownload();

            Instance.CancelUpmObservation("source_channel_reset");
            Instance._releaseCheckRunning = false;
            Instance._releaseCheckCompleted = false;
            Instance._releaseCheckStatus = new UPilotReleaseUpdateCheckStatus();
            if (guard.IsActive || guard.HasFailure)
                EndUpdateReloadGuard("source channel reset", force: true, clearFailure: true);
            ClearOperationStatus();
            UPilotPackageUpdateLifecycle.ResetUpdateStateForSourceChannel();

            if (hadState)
            {
                Debug.Log(
                    "[UPilot] Cleared update operation state for source channel." +
                    $" phase={phase}; guardActive={guard.IsActive}; downloadRunning={downloadRunning}");
            }
        }

        internal string RepairUpdateState(Action<string, MessageType> notice = null)
        {
            Debug.LogWarning("[UPilot] Repairing update state and reload guard.");
            CancelUpmObservation("manual_repair");
            EndUpdateReloadGuard("manual repair", force: true, clearFailure: true);
            ClearOperationStatus();
            UPilotPackageUpdateLifecycle.ResetUpdateStateForRepair(clearPendingPackageRetry: true);

            if (UPilotSetupState.IsCompleted)
            {
                try
                {
                    UPilotProjectConfig.Reload();
                    UPilotProjectConfig.ApplyEndpoints(UPilotBridge.Instance);
                    UPilotMcpServerManager.Instance.ValidateAndAutoFixPath();
                    UPilotMcpServerManager.Instance.StartServer();
                    UPilotBridge.Instance.EnsureStarted();
                }
                catch (Exception ex)
                {
                    var warning = "更新状态已修复，但服务恢复失败：" + ex.Message;
                    ReportUpdateFailure(warning, ex);
                    notice?.Invoke(warning, MessageType.Warning);
                    return warning;
                }
            }

            var message = "更新状态已修复，请重新检查更新后再次更新。";
            notice?.Invoke(message, MessageType.Info);
            return message;
        }

        internal static string FormatDownloadProgressLabel(UPilotDownloadState state)
        {
            var phase = string.IsNullOrWhiteSpace(state.Phase) ? "正在更新服务" : state.Phase;
            if (phase.IndexOf("下载", StringComparison.Ordinal) < 0)
                return phase;

            if (state.SegmentCount > 1)
            {
                var completed = UPilotDownloadHelper.GetCompletedSegmentCount(state);
                var active = UPilotDownloadHelper.GetActiveSegmentCount(state);
                var concurrency = state.MaxConcurrentSegmentRequests > 0
                    ? $"，最多 {Math.Min(state.SegmentCount, state.MaxConcurrentSegmentRequests)} 并发" : "";
                return $"{phase}（{state.SegmentCount} 分片，正在下载 {active}{concurrency}，已完成 {completed}/{state.SegmentCount}）";
            }
            if (state.SegmentCount == 1)
                return $"{phase}（单流下载）";
            return phase;
        }

        internal static string FormatDownloadProgressDetail(UPilotDownloadState state)
        {
            var phase = string.IsNullOrWhiteSpace(state.Phase) ? "正在更新服务" : state.Phase;
            if (phase.IndexOf("下载", StringComparison.Ordinal) < 0)
                return "";

            var sizeText = state.TotalBytes > 0
                ? $"{FormatBytes(state.BytesReceived)} / {FormatBytes(state.TotalBytes)}"
                : FormatBytes(state.BytesReceived);
            if (state.SegmentCount > 1)
            {
                var completed = UPilotDownloadHelper.GetCompletedSegmentCount(state);
                var active = UPilotDownloadHelper.GetActiveSegmentCount(state);
                var concurrency = state.MaxConcurrentSegmentRequests > 0
                    ? $" · 最多 {Math.Min(state.SegmentCount, state.MaxConcurrentSegmentRequests)} 并发" : "";
                var segmentSize = state.MaxSegmentSizeBytes > 0
                    ? $" · 每片上限 {state.MaxSegmentSizeBytes / 1024d:0.###} KiB" : "";
                return $"{sizeText} · {state.SegmentCount} 分片下载 · 正在下载 {active}{concurrency} · 已完成 {completed}/{state.SegmentCount}{segmentSize}";
            }
            if (state.SegmentCount == 1)
                return $"{sizeText} · 单流下载";
            return sizeText;
        }

        internal static float EstimateOperationProgress(UPilotUpdateOperationPhase phase)
        {
            return phase switch
            {
                UPilotUpdateOperationPhase.StoppingService => 0.12f,
                UPilotUpdateOperationPhase.DownloadingService => 0.35f,
                UPilotUpdateOperationPhase.UpdatingPackage => 0.58f,
                UPilotUpdateOperationPhase.WaitingForReload => 0.78f,
                UPilotUpdateOperationPhase.RestartingService => 0.9f,
                UPilotUpdateOperationPhase.Completed => 1f,
                _ => 0.05f,
            };
        }

        private async Task<bool> InstallManagedServerAndRestartAsync(
            string expectedServerVersion,
            bool shouldRestart,
            bool stopFirst,
            Action<string, MessageType> notice,
            long startupDeadlineUtcMs = 0)
        {
            if (CompleteIfSourceUpdateChannel(notice))
                return false;

            var manager = UPilotMcpServerManager.Instance;
            if (stopFirst)
            {
                SetOperationPhase(
                    UPilotUpdateOperationPhase.StoppingService,
                    "正在停止 MCP 服务，避免更新时文件被占用…",
                    targetServerVersion: expectedServerVersion);
                notice?.Invoke("正在停止 MCP 服务…", MessageType.Info);
                UPilotBridge.Instance.Stop();
                var stopLifetime = UPilotServiceLifetime.Id;
                var stopOperationId = GetOperationStatus().OperationId;
                var stopResult = await manager.StopServerAndObserveExitAsync(UPilotServerStopOrigin.ManagedServerUpdate);
                if (stopLifetime != UPilotServiceLifetime.Id || stopOperationId != GetOperationStatus().OperationId) return false;
                if (!stopResult.StopConfirmed || !stopResult.PortsAvailable || !manager.IsStopResultCurrent(stopResult))
                {
                    var message = "无法停止 MCP 服务，已取消更新以避免文件占用";
                    SetOperationFailed(message);
                    var recoveryStarted = BeginOccupancyRecovery(
                        UPilotOccupancyRetryKind.ManagedServer,
                        "",
                        message);
                    notice?.Invoke(
                        recoveryStarted ? message + "。请在更新中心结束全部占用进程并重试。" : message,
                        MessageType.Error);
                    return false;
                }
            }

            SetOperationPhase(
                UPilotUpdateOperationPhase.DownloadingService,
                "正在下载并安装 MCP 服务…",
                targetServerVersion: expectedServerVersion);
            UPilotServerRuntimeService.Instance.StartDownloadLatestServerExe();
            notice?.Invoke("正在更新 MCP 服务…", MessageType.Info);
            await WaitForDownloadCompletionAsync();
            var state = UPilotServerRuntimeService.Instance.DownloadState;
            if (!state.IsComplete)
            {
                var message = string.IsNullOrEmpty(state.ErrorMessage) ? "MCP 服务更新未完成" : state.ErrorMessage;
                SetOperationFailed(message);
                BeginOccupancyRecovery(UPilotOccupancyRetryKind.ManagedServer, "", message);
                if (shouldRestart)
                    manager.StartServer();
                notice?.Invoke(message, MessageType.Error);
                return false;
            }

            if (!shouldRestart)
            {
                var message = "MCP 服务已更新，将在完成首次设置后启动";
                SetOperationCompleted(message);
                notice?.Invoke(message, MessageType.Info);
                return true;
            }

            SetOperationPhase(
                UPilotUpdateOperationPhase.RestartingService,
                "MCP 服务已更新，正在自动重启…",
                targetServerVersion: expectedServerVersion);
            notice?.Invoke("MCP 服务已更新，正在重新启动…", MessageType.Info);
            return await RestartAndConfirmManagedServerAsync(
                expectedServerVersion,
                notice,
                failureNotice: "MCP 服务已更新，但未能自动启动。请在主窗口点击启动或重启服务。",
                startupDeadlineUtcMs: startupDeadlineUtcMs);
        }

        private static async Task<bool> RestartAndConfirmManagedServerAsync(
            string expectedServerVersion,
            Action<string, MessageType> notice,
            string failureNotice,
            long startupDeadlineUtcMs = 0)
        {
            var operationId = Instance.GetOperationStatus().OperationId;
            var manager = UPilotMcpServerManager.Instance;
            try
            {
                if (startupDeadlineUtcMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= startupDeadlineUtcMs)
                    throw new TimeoutException("包更新恢复的原定期限已结束，未再次启动服务。");
                manager.ValidateAndAutoFixPath();
                manager.StartServer();
                UPilotBridge.Instance.EnsureStarted();
                var status = await manager.WaitForVerifiedStartupAsync(expectedServerVersion, startupDeadlineUtcMs);
                var current = Instance.GetOperationStatus();
                if (current.OperationId != operationId || !current.IsRunning) return false;
                SetOperationCompleted($"MCP 服务已更新并通过启动验证，当前版本 {status.ServerVersion}");
                notice?.Invoke($"MCP 服务已更新并通过启动验证，当前版本 {status.ServerVersion}", MessageType.Info);
                return true;
            }
            catch (OperationCanceledException)
            {
                var current = Instance.GetOperationStatus();
                if (current.OperationId == operationId && current.IsRunning)
                {
                    SetOperationCompleted("MCP 服务启动验证已取消");
                    notice?.Invoke("MCP 服务启动验证已取消", MessageType.Info);
                }
                return false;
            }
            catch (Exception ex)
            {
                var message = $"MCP 服务已更新，但启动验证失败（期望版本：{expectedServerVersion}）：{ex.Message}";
                var current = Instance.GetOperationStatus();
                if (current.OperationId == operationId && current.IsRunning)
                {
                    SetOperationFailed(message);
                    ReportUpdateFailure(message, ex);
                    notice?.Invoke(message, MessageType.Error);
                    UPilotMainWindow.OpenWithNotice(failureNotice, MessageType.Error);
                }
                else
                    Logger.ReportFailure("UPDATE", $"operationId={operationId} {message}", ex,
                        "update|" + operationId, terminal: true);
                return false;
            }
        }

        public async void UpdateUpmFromManifest(Action<string, MessageType> notice)
        {
            try
            {
                await UpdateUpmFromManifestAsync(notice);
            }
            catch (Exception ex)
            {
                HandleUnexpectedUpdateException("更新 UPilot 包失败", ex, notice);
            }
        }

        private async Task UpdateUpmFromManifestAsync(Action<string, MessageType> notice)
        {
            _notice = notice;
            if (CompleteIfSourceUpdateChannel(notice))
                return;

            SetOperationPhase(UPilotUpdateOperationPhase.StoppingService, "正在读取更新信息…");
            UPilotReleaseManifest manifest;
            try
            {
                manifest = await UPilotServerRuntimeService.Instance.FetchReleaseManifestAsync();
            }
            catch (Exception ex)
            {
                var message = "读取发布清单失败：" + ex.Message;
                SetOperationFailed(message);
                LogUpdateError(message, ex);
                notice?.Invoke(message, MessageType.Error);
                return;
            }

            await StartPackageUpdateAsync(
                manifest,
                installManagedServerAfterUpdate: false,
                notice,
                preparedServerPath: "");
        }

        private async Task StartPackageUpdateAsync(
            UPilotReleaseManifest manifest,
            bool installManagedServerAfterUpdate,
            Action<string, MessageType> notice,
            string preparedServerPath)
        {
            if (string.IsNullOrWhiteSpace(manifest.UpmVersion))
            {
                SetOperationFailed("发布清单缺少 upmVersion");
                notice?.Invoke("发布清单缺少 upmVersion", MessageType.Error);
                return;
            }

            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UPilotBridge).Assembly);
            if (package != null && package.source == PackageSource.Local)
            {
                var confirmed = EditorUtility.DisplayDialog(
                    "本地开发包",
                    "当前 UPilot 是 file: 本地包。自动 UPM 更新通常适用于 registry/git 安装。是否仍尝试按 GitHub tag 更新？",
                    "尝试更新",
                    "取消");
                if (!confirmed)
                {
                    SetOperationCompleted("已取消 UPilot 包更新");
                    notice?.Invoke("已取消 UPilot 包更新", MessageType.Info);
                    return;
                }
            }

            var revision = "v" + manifest.UpmVersion;
            var identifier = $"https://github.com/codingriver/upilot.git#{revision}";

            SetOperationPhase(
                UPilotUpdateOperationPhase.StoppingService,
                installManagedServerAfterUpdate
                    ? "正在停止服务，准备更新 UPilot 和 MCP 服务…"
                    : "正在停止服务，准备更新 UPilot 包…",
                manifest.UpmVersion,
                installManagedServerAfterUpdate ? manifest.ServerVersion : "");
            if (!await UPilotPackageUpdateLifecycle.PrepareForPackageUpdateAsync(
                    manifest.UpmVersion,
                    notice,
                    installManagedServerAfterUpdate,
                    manifest.ServerVersion,
                    preparedServerPath,
                    installManagedServerAfterUpdate
                        ? UPilotOccupancyRetryKind.PackageAndManagedServer
                        : UPilotOccupancyRetryKind.Package,
                    identifier))
            {
                if (UPilotPackageUpdateLifecycle.TryGetPendingPackageUpdateNotice(out var pendingNotice))
                {
                    SetOperationFailed("UPilot 包更新未完成，请继续更新");
                    UPilotMainWindow.OpenWithNotice(pendingNotice, MessageType.Warning);
                }
                else
                {
                    SetOperationFailed("无法停止 MCP 服务，已取消更新");
                }

                return;
            }

            try
            {
                SetOperationPhase(UPilotUpdateOperationPhase.UpdatingPackage, "正在更新 UPilot 包…", manifest.UpmVersion, installManagedServerAfterUpdate ? manifest.ServerVersion : "");
                BeginUpmObservation(identifier);
            }
            catch (Exception ex)
            {
                UPilotPackageUpdateLifecycle.RestoreAfterFailedUpdate();
                var message = "无法启动 UPilot 包更新：" + ex.Message;
                SetOperationFailed(message);
                LogUpdateError(message, ex);
                notice?.Invoke(message, MessageType.Error);
                return;
            }

            if (_upmObservation != null) notice?.Invoke("正在更新 UPilot 包…", MessageType.Info);
        }

        internal void CompleteObservedUpmUpdateFromRegistration() => _upmObservation?.CompleteFromRegistration();

        private void CancelUpmObservation(string reason)
        {
            var observation = _upmObservation;
            observation?.Cancel(reason);
            if (ReferenceEquals(_upmObservation, observation)) _upmObservation = null;
        }

        private void BeginUpmObservation(string identifier)
        {
            if (_upmObservation != null && !_upmObservation.IsEnded)
                throw new InvalidOperationException("An UPM update observation is already active.");
            AddRequest request = null;
            var notice = _notice;
            UPilotUpmRequestObservation observation = null;
            observation = UPilotUpmRequestObservation.Create(this, Guid.NewGuid().ToString("N"), "upilot.update",
                () => request = Client.Add(identifier), () => request != null && request.IsCompleted,
                () => request.Status == StatusCode.Failure
                    ? new UPilotUpmRequestException("UPM_REQUEST_FAILED", request.Error?.message ?? "unknown") : null,
                () =>
                {
                    request = null;
                    if (!ReferenceEquals(_upmObservation, observation)) return;
                    _upmObservation = null;
                    var managedPending = UPilotPackageUpdateLifecycle.IsManagedServerUpdatePending;
                    if (ReadOperationPhase() != UPilotUpdateOperationPhase.WaitingForReload)
                        UPilotPackageUpdateLifecycle.MarkPackageUpdateCompleted();
                    notice?.Invoke(managedPending ? "UPilot 包已更新，Unity 重载后将继续更新 MCP 服务"
                        : "UPilot 包已更新，Unity 重载后将自动恢复服务", MessageType.Info);
                },
                ex =>
                {
                    request = null;
                    if (!ReferenceEquals(_upmObservation, observation)) return;
                    _upmObservation = null;
                    SetOperationFailed("UPilot 包更新观察结束：" + ex.Message);
                    if (observation.EndReason == "start_failed" || ex is UPilotUpmRequestException upm && upm.Code == "UPM_REQUEST_FAILED")
                        UPilotPackageUpdateLifecycle.RestoreAfterFailedUpdate();
                    else UPilotPackageUpdateLifecycle.ResetUpdateStateForRepair(clearPendingPackageRetry: false);
                    if (!(ex is OperationCanceledException)) notice?.Invoke("UPilot 包更新失败：" + ex.Message, MessageType.Error);
                });
            _upmObservation = observation;
            observation.Start();
        }

        private static UPilotUpdateOperationPhase ReadOperationPhase()
        {
            var raw = SessionState.GetString(ProjectKey(OperationPhaseKey), UPilotUpdateOperationPhase.None.ToString());
            return Enum.TryParse(raw, out UPilotUpdateOperationPhase phase)
                ? phase
                : UPilotUpdateOperationPhase.None;
        }

        private static bool IsMemoryBoundUpdatePhase(UPilotUpdateOperationPhase phase)
        {
            return phase == UPilotUpdateOperationPhase.StoppingService ||
                   phase == UPilotUpdateOperationPhase.DownloadingService ||
                   phase == UPilotUpdateOperationPhase.UpdatingPackage ||
                   phase == UPilotUpdateOperationPhase.RestartingService;
        }

        private static bool IsReloadGuardedPhase(UPilotUpdateOperationPhase phase)
        {
            return phase == UPilotUpdateOperationPhase.StoppingService ||
                   phase == UPilotUpdateOperationPhase.DownloadingService ||
                   phase == UPilotUpdateOperationPhase.RestartingService;
        }

        private static string ApplyReloadGuardForPhase(UPilotUpdateOperationPhase phase)
        {
            if (IsReloadGuardedPhase(phase))
            {
                return BeginUpdateReloadGuard(phase.ToString());
            }

            EndUpdateReloadGuard(
                phase.ToString(),
                force: phase == UPilotUpdateOperationPhase.Completed ||
                       phase == UPilotUpdateOperationPhase.Failed,
                clearFailure: phase != UPilotUpdateOperationPhase.Failed);
            return "";
        }

        private static string BeginUpdateReloadGuard(string reason)
        {
            SessionState.SetBool(ProjectKey(ReloadGuardActiveKey), true);
            SessionState.SetString(ProjectKey(ReloadGuardRuntimeIdKey), RuntimeId);
            SessionState.SetString(ProjectKey(ReloadGuardPhaseKey), reason ?? "");
            if (SessionState.GetString(ProjectKey(ReloadGuardSinceUnixMsKey), "").Length == 0)
            {
                SessionState.SetString(
                    ProjectKey(ReloadGuardSinceUnixMsKey),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            }
            SessionState.EraseString(ProjectKey(ReloadGuardFailureKey));

            if (_reloadGuardActive)
            {
                Debug.Log(
                    "[UPilot] Update reload guard still active." +
                    $" phase={reason}; runtime={RuntimeId}");
                return "";
            }

            var autoRefreshBlocked = false;
            var reloadLocked = false;
            try
            {
                AssetDatabase.DisallowAutoRefresh();
                autoRefreshBlocked = true;
                SessionState.SetBool(ProjectKey(ReloadGuardAutoRefreshBlockedKey), true);
                EditorApplication.LockReloadAssemblies();
                reloadLocked = true;
                SessionState.SetBool(ProjectKey(ReloadGuardAssemblyReloadLockedKey), true);
                _reloadGuardActive = true;
                _reloadGuardAutoRefreshBlocked = true;
                _reloadGuardAssemblyReloadLocked = true;
                Debug.Log(
                    "[UPilot] Update reload guard enabled." +
                    $" phase={reason}; runtime={RuntimeId}; autoRefresh=blocked; assemblyReload=locked");
                return "";
            }
            catch (Exception ex)
            {
                var message = ex.Message;
                ReportUpdateFailure(
                    "[UPilot] Failed to enable update reload guard." +
                    $" autoRefreshBlocked={autoRefreshBlocked}; assemblyReloadLocked={reloadLocked}\n" + ex);
                SetReloadGuardFailure(message);
                ReleaseReloadGuardHandles(
                    "guard enable failed cleanup",
                    autoRefreshBlocked,
                    reloadLocked);
                _reloadGuardActive = false;
                _reloadGuardAutoRefreshBlocked = false;
                _reloadGuardAssemblyReloadLocked = false;
                ClearReloadGuardActiveState(clearFailure: false);
                return message;
            }
        }

        private static void EndUpdateReloadGuard(string reason, bool force, bool clearFailure)
        {
            var persistedActive = SessionState.GetBool(ProjectKey(ReloadGuardActiveKey), false);
            var persistedAutoRefreshBlocked =
                SessionState.GetBool(ProjectKey(ReloadGuardAutoRefreshBlockedKey), false);
            var persistedAssemblyReloadLocked =
                SessionState.GetBool(ProjectKey(ReloadGuardAssemblyReloadLockedKey), false);
            if (!_reloadGuardActive && !force && !persistedActive &&
                !persistedAutoRefreshBlocked && !persistedAssemblyReloadLocked)
                return;

            var legacyPersistedGuard = persistedActive &&
                                       !persistedAutoRefreshBlocked &&
                                       !persistedAssemblyReloadLocked;
            var releaseAssemblyReload = _reloadGuardAssemblyReloadLocked ||
                                        persistedAssemblyReloadLocked ||
                                        (force && legacyPersistedGuard);
            var releaseAutoRefresh = _reloadGuardAutoRefreshBlocked ||
                                     persistedAutoRefreshBlocked ||
                                     (force && legacyPersistedGuard);

            ReleaseReloadGuardHandles(reason, releaseAutoRefresh, releaseAssemblyReload);

            Debug.Log(
                "[UPilot] Update reload guard disabled." +
                $" reason={reason}; runtime={RuntimeId}; force={force}; persistedActive={persistedActive}" +
                $"; autoRefreshReleased={releaseAutoRefresh}; assemblyReloadReleased={releaseAssemblyReload}");
            _reloadGuardActive = false;
            _reloadGuardAutoRefreshBlocked = false;
            _reloadGuardAssemblyReloadLocked = false;
            ClearReloadGuardActiveState(clearFailure);
        }

        private static void ReleaseReloadGuardHandles(
            string reason,
            bool releaseAutoRefresh,
            bool releaseAssemblyReload)
        {
            if (releaseAssemblyReload)
            {
                try
                {
                    EditorApplication.UnlockReloadAssemblies();
                }
                catch (Exception ex)
                {
                    ReportUpdateFailure("Failed to unlock assemblies after update guard. reason=" + reason, ex);
                }
            }

            if (!releaseAutoRefresh)
                return;

            try
            {
                AssetDatabase.AllowAutoRefresh();
            }
            catch (Exception ex)
            {
                ReportUpdateFailure("Failed to allow auto refresh after update guard. reason=" + reason, ex);
            }
        }

        private static void ClearReloadGuardActiveState(bool clearFailure)
        {
            SessionState.EraseBool(ProjectKey(ReloadGuardActiveKey));
            SessionState.EraseString(ProjectKey(ReloadGuardRuntimeIdKey));
            SessionState.EraseString(ProjectKey(ReloadGuardPhaseKey));
            SessionState.EraseString(ProjectKey(ReloadGuardSinceUnixMsKey));
            SessionState.EraseBool(ProjectKey(ReloadGuardAutoRefreshBlockedKey));
            SessionState.EraseBool(ProjectKey(ReloadGuardAssemblyReloadLockedKey));
            if (clearFailure)
                SessionState.EraseString(ProjectKey(ReloadGuardFailureKey));
        }

        private static void RecoverInterruptedReloadGuardIfNeeded(UPilotUpdateOperationPhase phase)
        {
            if (UPilotServerRuntimeService.IsSourceUpdateChannel())
                return;

            var guard = GetReloadGuardStatus();
            if (!guard.IsActive || guard.IsCurrentRuntime)
                return;

            var phaseText = BuildOperationPhaseText(phase);
            var message = guard.HasFailure
                ? guard.FailureMessage
                : $"更新任务在 {phaseText} 阶段被 Unity 脚本重载中断，已解除更新保护，请修复更新状态后重新更新。";
            SetReloadGuardFailure(message);
            ReportUpdateFailure(
                "[UPilot] Recovering stale update reload guard after domain reload." +
                $"\nphase={phase}; guardPhase={guard.Phase}; runtime={RuntimeId}; sinceUnixMs={guard.SinceUnixMs}" +
                "\n" + message);
            EndUpdateReloadGuard("domain reload recovery", force: true, clearFailure: false);
        }

        private static string FailStaleMemoryBoundOperation(UPilotUpdateOperationPhase phase)
        {
            var guard = GetReloadGuardStatus();
            var phaseText = BuildOperationPhaseText(phase);
            var message = guard.HasFailure
                ? guard.FailureMessage
                : $"更新任务在 {phaseText} 阶段被 Unity 脚本重载中断，请修复更新状态后重新更新。";
            SetReloadGuardFailure(message);
            ReportUpdateFailure(
                "[UPilot] " + message +
                $"\nphase={phase}; guardActive={guard.IsActive}; guardPhase={guard.Phase}; guardRuntimeCurrent={guard.IsCurrentRuntime}");
            UPilotPackageUpdateLifecycle.ResetUpdateStateForRepair(clearPendingPackageRetry: false);
            EndUpdateReloadGuard("stale memory-bound update", force: true, clearFailure: false);
            SetOperationPhaseCore(
                UPilotUpdateOperationPhase.Failed,
                message,
                SessionState.GetString(ProjectKey(OperationTargetUpmKey), ""),
                SessionState.GetString(ProjectKey(OperationTargetServerKey), ""),
                applyReloadGuard: false);
            return message;
        }

        private static void OnEditorQuitting()
        {
            Instance.CancelUpmObservation("editor_quitting");
            UPilotServerRuntimeService.Instance.CancelDownload();
        }

        private static void OnBeforeAssemblyReload()
        {
            Instance.CancelUpmObservation("domain_reload");
            UPilotServerRuntimeService.Instance.CancelDownload();
            if (UPilotServerRuntimeService.IsSourceUpdateChannel())
                return;

            var guard = GetReloadGuardStatus();
            if (!guard.IsActive)
                return;

            var message =
                "检测到 Unity 在 UPilot 更新保护期间执行脚本重载。更新已中断，请修复更新状态后重新更新。";
            SetReloadGuardFailure(message);
            SetOperationPhaseCore(
                UPilotUpdateOperationPhase.Failed,
                message,
                SessionState.GetString(ProjectKey(OperationTargetUpmKey), ""),
                SessionState.GetString(ProjectKey(OperationTargetServerKey), ""),
                applyReloadGuard: false);
            ReportUpdateFailure(
                "[UPilot] Domain reload occurred while update reload guard was active." +
                $"\nphase={guard.Phase}; runtime={RuntimeId}; guardRuntimeCurrent={guard.IsCurrentRuntime}" +
                "\n" + message);
        }

        private static void SetReloadGuardFailure(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            SessionState.SetString(ProjectKey(ReloadGuardFailureKey), message);
        }

        private static bool IsOperationFromPreviousRuntime()
        {
            var runtimeId = SessionState.GetString(ProjectKey(OperationRuntimeIdKey), "");
            return string.IsNullOrWhiteSpace(runtimeId) ||
                   !string.Equals(runtimeId, RuntimeId, StringComparison.Ordinal);
        }

        private static string BuildOperationPhaseText(UPilotUpdateOperationPhase phase)
        {
            var label = BuildOperationLabel(phase);
            return string.IsNullOrWhiteSpace(label) ? phase.ToString() : label;
        }

        private static string ProjectKey(string key)
        {
            return UPilotPreferences.ProjectKey(key);
        }

        private static string BuildOperationLabel(UPilotUpdateOperationPhase phase)
        {
            return phase switch
            {
                UPilotUpdateOperationPhase.StoppingService => "正在停止服务",
                UPilotUpdateOperationPhase.UpdatingPackage => "正在更新 UPilot",
                UPilotUpdateOperationPhase.WaitingForReload => "等待更新完成",
                UPilotUpdateOperationPhase.DownloadingService => "正在更新服务",
                UPilotUpdateOperationPhase.RestartingService => "正在重启服务",
                UPilotUpdateOperationPhase.Completed => "更新完成",
                UPilotUpdateOperationPhase.Failed => "更新失败",
                _ => "",
            };
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            var kb = bytes / 1024d;
            if (kb < 1024) return kb.ToString("0.0") + " KB";
            var mb = kb / 1024d;
            if (mb < 1024) return mb.ToString("0.0") + " MB";
            return (mb / 1024d).ToString("0.0") + " GB";
        }
    }
}
