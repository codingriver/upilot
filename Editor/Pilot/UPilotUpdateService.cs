// -----------------------------------------------------------------------
// UPilot Editor - release manifest update helpers.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
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

    internal readonly struct UPilotUpdateOperationStatus
    {
        public UPilotUpdateOperationStatus(
            UPilotUpdateOperationPhase phase,
            string label,
            string message,
            string targetUpmVersion,
            string targetServerVersion)
        {
            Phase = phase;
            Label = label ?? "";
            Message = message ?? "";
            TargetUpmVersion = targetUpmVersion ?? "";
            TargetServerVersion = targetServerVersion ?? "";
        }

        public UPilotUpdateOperationPhase Phase { get; }
        public string Label { get; }
        public string Message { get; }
        public string TargetUpmVersion { get; }
        public string TargetServerVersion { get; }

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
        private const string ReloadGuardActiveKey = "CodingRiver.UPilot.UpdateService.ReloadGuardActive";
        private const string ReloadGuardRuntimeIdKey = "CodingRiver.UPilot.UpdateService.ReloadGuardRuntimeId";
        private const string ReloadGuardPhaseKey = "CodingRiver.UPilot.UpdateService.ReloadGuardPhase";
        private const string ReloadGuardSinceUnixMsKey = "CodingRiver.UPilot.UpdateService.ReloadGuardSinceUnixMs";
        private const string ReloadGuardFailureKey = "CodingRiver.UPilot.UpdateService.ReloadGuardFailure";
        private const string ReloadGuardAutoRefreshBlockedKey = "CodingRiver.UPilot.UpdateService.ReloadGuardAutoRefreshBlocked";
        private const string ReloadGuardAssemblyReloadLockedKey = "CodingRiver.UPilot.UpdateService.ReloadGuardAssemblyReloadLocked";
        private const string SkipReleaseReminderVersionKey = "CodingRiver.UPilot.UpdateService.SkipReleaseReminderVersion";
        private static readonly string RuntimeId = Guid.NewGuid().ToString("N");
        private static bool _reloadGuardActive;
        private static bool _reloadGuardAutoRefreshBlocked;
        private static bool _reloadGuardAssemblyReloadLocked;

        private AddRequest _upmRequest;
        private Action<string, MessageType> _notice;
        private bool _releaseCheckRunning;
        private bool _releaseCheckCompleted;
        private UPilotReleaseUpdateCheckStatus _releaseCheckStatus = new();

        static UPilotUpdateService()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        internal static string[] GetPreferenceKeysForCurrentProject()
        {
            return new[] { ProjectKey(SkipReleaseReminderVersionKey) };
        }

        public void CheckForUpdates(Action<string, MessageType> notice)
        {
            try
            {
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
                Debug.LogError("[UPilot] " + message + "\n" + ex);
                try
                {
                    notice?.Invoke(message, MessageType.Error);
                }
                catch (Exception noticeEx)
                {
                    Debug.LogError("[UPilot] 更新错误通知失败：" + noticeEx);
                }
            }
        }

        internal bool IsUpdateRunning => GetOperationStatus().IsRunning;

        internal bool IsServiceStartBlocked => GetOperationStatus().BlocksServiceStart;

        internal void EnsureLatestReleaseCheck(bool force = false)
        {
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
                var message = FailStaleMemoryBoundOperation(phase);
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
                SessionState.GetString(ProjectKey(OperationTargetServerKey), ""));
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
            SetOperationCompleted(message);
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
                Debug.LogError("[UPilot] 更新错误通知失败：" + noticeEx);
            }
        }

        private static void LogUpdateError(string message, Exception ex)
        {
            Debug.LogError("[UPilot] " + message + "\n" + ex);
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

                StartPackageUpdate(manifest, updateManagedServer, notice, preparedServerPath);
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
            Action<string, MessageType> notice = null)
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
                notice: notice);
        }

        internal async Task<bool> ActivatePreparedManagedServerAfterPackageUpdateAsync(
            string preparedServerPath,
            string expectedServerVersion,
            bool shouldRestart,
            Action<string, MessageType> notice = null)
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
                failureNotice: "UPilot 包已更新，但服务未能自动启动。请在主窗口点击启动或重启服务。");
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
                SetOperationCompleted("已取消 MCP 服务下载");
                notice?.Invoke("已取消 MCP 服务下载", MessageType.Info);
                return null;
            }
            catch (Exception ex)
            {
                var message = "MCP 服务更新失败：" + ex.Message;
                SetOperationFailed(message);
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
            SessionState.SetString(ProjectKey(OperationPhaseKey), phase.ToString());
            SessionState.SetString(ProjectKey(OperationMessageKey), message ?? "");
            if (targetUpmVersion != null)
                SessionState.SetString(ProjectKey(OperationTargetUpmKey), targetUpmVersion);
            if (targetServerVersion != null)
                SessionState.SetString(ProjectKey(OperationTargetServerKey), targetServerVersion);
            if (IsMemoryBoundUpdatePhase(phase))
                SessionState.SetString(ProjectKey(OperationRuntimeIdKey), RuntimeId);

            if (!applyReloadGuard)
                return;

            var guardError = ApplyReloadGuardForPhase(phase);
            if (string.IsNullOrWhiteSpace(guardError))
                return;

            var failure = "拦截 Unity 脚本重载失败：" + guardError + "。已取消更新，请重新更新。";
            SetReloadGuardFailure(failure);
            Debug.LogError("[UPilot] " + failure);
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
            SessionState.EraseString(ProjectKey(OperationPhaseKey));
            SessionState.EraseString(ProjectKey(OperationMessageKey));
            SessionState.EraseString(ProjectKey(OperationTargetUpmKey));
            SessionState.EraseString(ProjectKey(OperationTargetServerKey));
            SessionState.EraseString(ProjectKey(OperationRuntimeIdKey));
            EndUpdateReloadGuard("operation status cleared", force: false, clearFailure: true);
        }

        internal string RepairUpdateState(Action<string, MessageType> notice = null)
        {
            Debug.LogWarning("[UPilot] Repairing update state and reload guard.");
            EndUpdateReloadGuard("manual repair", force: true, clearFailure: true);
            ClearOperationStatus();
            UPilotPackageUpdateLifecycle.ResetUpdateStateForRepair(clearPendingPackageRetry: true);
            _upmRequest = null;

            if (UPilotSetupState.IsCompleted)
            {
                try
                {
                    UPilotProjectConfig.Reload();
                    UPilotProjectConfig.ApplyEndpoints(UPilotBridge.Instance);
                    UPilotMcpServerManager.Instance.ValidateAndAutoFixPath();
                    UPilotBridge.Instance.EnsureStarted();
                    UPilotMcpServerManager.Instance.StartServer();
                }
                catch (Exception ex)
                {
                    var warning = "更新状态已修复，但服务恢复失败：" + ex.Message;
                    Debug.LogWarning("[UPilot] " + warning + "\n" + ex);
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
                return $"{phase}（{state.SegmentCount} 线程，已完成 {state.CompletedSegments}/{state.SegmentCount}）";
            if (state.SegmentCount == 1)
                return $"{phase}（单线程）";
            return phase;
        }

        internal static string FormatDownloadProgressDetail(UPilotDownloadState state)
        {
            var sizeText = state.TotalBytes > 0
                ? $"{FormatBytes(state.BytesReceived)} / {FormatBytes(state.TotalBytes)}"
                : FormatBytes(state.BytesReceived);
            if (state.SegmentCount > 1)
                return $"{sizeText} · {state.SegmentCount} 线程下载 · 已完成 {state.CompletedSegments}/{state.SegmentCount}";
            if (state.SegmentCount == 1)
                return $"{sizeText} · 单线程下载";
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
            Action<string, MessageType> notice)
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
                if (!manager.StopServerAndWaitForExit())
                {
                    var message = "无法停止 MCP 服务，已取消更新以避免文件占用";
                    SetOperationFailed(message);
                    notice?.Invoke(message, MessageType.Error);
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
                failureNotice: "MCP 服务已更新，但未能自动启动。请在主窗口点击启动或重启服务。");
        }

        private static async Task<bool> RestartAndConfirmManagedServerAsync(
            string expectedServerVersion,
            Action<string, MessageType> notice,
            string failureNotice)
        {
            var manager = UPilotMcpServerManager.Instance;
            manager.ValidateAndAutoFixPath();
            manager.StartServer();
            UPilotBridge.Instance.EnsureStarted();
            var runningVersion = await manager.WaitForServerVersionAsync(expectedServerVersion);
            if (string.IsNullOrWhiteSpace(runningVersion) ||
                UPilotServerRuntimeService.CompareVersions(runningVersion, expectedServerVersion) < 0)
            {
                var versionText = string.IsNullOrWhiteSpace(runningVersion) ? "未能读取版本" : runningVersion;
                var message = $"MCP 服务已更新，但启动确认失败（当前：{versionText}，期望：{expectedServerVersion}），请检查服务日志";
                SetOperationFailed(message);
                notice?.Invoke(message, MessageType.Error);
                UPilotMainWindow.OpenWithNotice(failureNotice, MessageType.Warning);
                return false;
            }

            SetOperationCompleted($"MCP 服务已更新并启动，当前版本 {runningVersion}");
            notice?.Invoke($"MCP 服务已更新并启动，当前版本 {runningVersion}", MessageType.Info);
            return true;
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

            StartPackageUpdate(
                manifest,
                installManagedServerAfterUpdate: false,
                notice,
                preparedServerPath: "");
        }

        private void StartPackageUpdate(
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
            if (!UPilotPackageUpdateLifecycle.PrepareForPackageUpdate(
                    manifest.UpmVersion,
                    notice,
                    installManagedServerAfterUpdate,
                    manifest.ServerVersion,
                    preparedServerPath))
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
                _upmRequest = Client.Add(identifier);
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

            EditorApplication.update += PollUpmUpdate;
            SetOperationPhase(
                UPilotUpdateOperationPhase.UpdatingPackage,
                installManagedServerAfterUpdate
                    ? "正在更新 UPilot 包，完成后会继续更新 MCP 服务…"
                    : "正在更新 UPilot 包…",
                manifest.UpmVersion,
                installManagedServerAfterUpdate ? manifest.ServerVersion : "");
            notice?.Invoke("正在更新 UPilot 包…", MessageType.Info);
        }

        private void PollUpmUpdate()
        {
            if (_upmRequest == null || !_upmRequest.IsCompleted)
                return;

            EditorApplication.update -= PollUpmUpdate;
            if (_upmRequest.Status == StatusCode.Failure)
            {
                _notice?.Invoke("UPilot 包更新失败：" + (_upmRequest.Error?.message ?? "unknown"), MessageType.Error);
                _upmRequest = null;
                UPilotPackageUpdateLifecycle.RestoreAfterFailedUpdate();
                SetOperationFailed("UPilot 包更新失败");
                return;
            }

            var managedPending = UPilotPackageUpdateLifecycle.IsManagedServerUpdatePending;
            SetOperationPhase(
                UPilotUpdateOperationPhase.WaitingForReload,
                managedPending
                    ? "UPilot 包已更新，等待 Unity 重载后继续更新 MCP 服务…"
                    : "UPilot 包已更新，等待 Unity 重载后恢复服务…");
            _notice?.Invoke(
                managedPending
                    ? "UPilot 包已更新，Unity 重载后将继续更新 MCP 服务"
                    : "UPilot 包已更新，Unity 重载后将自动恢复服务",
                MessageType.Info);
            _upmRequest = null;
            UPilotPackageUpdateLifecycle.MarkPackageUpdateCompleted();
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
                Debug.LogError(
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
                    Debug.LogWarning(
                        "[UPilot] Failed to unlock assemblies after update guard." +
                        $" reason={reason}; error={ex.Message}");
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
                Debug.LogWarning(
                    "[UPilot] Failed to allow auto refresh after update guard." +
                    $" reason={reason}; error={ex.Message}");
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
            var guard = GetReloadGuardStatus();
            if (!guard.IsActive || guard.IsCurrentRuntime)
                return;

            var phaseText = BuildOperationPhaseText(phase);
            var message = guard.HasFailure
                ? guard.FailureMessage
                : $"更新任务在 {phaseText} 阶段被 Unity 脚本重载中断，已解除更新保护，请修复更新状态后重新更新。";
            SetReloadGuardFailure(message);
            Debug.LogError(
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
            Debug.LogError(
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

        private static void OnBeforeAssemblyReload()
        {
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
            Debug.LogError(
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
