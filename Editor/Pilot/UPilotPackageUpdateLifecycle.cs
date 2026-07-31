// -----------------------------------------------------------------------
// UPilot Editor - coordinates service shutdown across UPM package updates.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
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

        private static bool _restartScheduled;

        static UPilotPackageUpdateLifecycle()
        {
            Events.registeringPackages -= OnRegisteringPackages;
            Events.registeringPackages += OnRegisteringPackages;
            Events.registeredPackages -= OnRegisteredPackages;
            Events.registeredPackages += OnRegisteredPackages;
            EditorApplication.delayCall += TryRestoreAfterUpdate;
        }

        public static bool PrepareForPackageUpdate(
            string targetVersion,
            Action<string, MessageType> notice = null,
            bool installManagedServerAfterUpdate = false,
            string targetServerVersion = "",
            string preparedServerPath = "")
        {
            if (SessionState.GetBool(UpdateInProgressKey, false))
            {
                if (installManagedServerAfterUpdate)
                {
                    SessionState.SetBool(ManagedServerPendingKey, true);
                    if (!string.IsNullOrWhiteSpace(targetServerVersion))
                        SessionState.SetString(TargetServerVersionKey, targetServerVersion);
                    if (!string.IsNullOrWhiteSpace(preparedServerPath))
                        SessionState.SetString(PreparedServerPathKey, preparedServerPath);
                }

                return true;
            }

            var manager = UPilotMcpServerManager.Instance;
            var status = manager.GetStatus();
            var wasRunning = status.IsRunning || status.HttpPortListening || status.WsPortListening ||
                             !UPilotPortAllocator.IsPortAvailable(manager.HttpPort) ||
                             !UPilotPortAllocator.IsPortAvailable(manager.WsPort);
            var shouldRestart = UPilotSetupState.IsCompleted;

            SessionState.SetBool(UpdateInProgressKey, true);
            SessionState.SetBool(RestartPendingKey, shouldRestart);
            SessionState.SetBool(UpdateCompletedKey, false);
            SessionState.SetString(TargetVersionKey, targetVersion ?? "");
            SessionState.SetBool(ManagedServerPendingKey, installManagedServerAfterUpdate);
            SessionState.SetString(TargetServerVersionKey, targetServerVersion ?? "");
            SessionState.SetString(PreparedServerPathKey, preparedServerPath ?? "");
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
            if (manager.StopServerAndWaitForExit())
                return true;

            ClearUpdateState();
            UPilotBridge.Instance.EnsureStarted();
            manager.StartServer();
            UPilotUpdateService.SetOperationFailed("无法停止 MCP 服务，已取消包更新以避免文件占用");
            notice?.Invoke("无法停止 MCP 服务，已取消包更新以避免文件占用", MessageType.Error);
            return false;
        }

        public static void MarkPackageUpdateCompleted()
        {
            if (!SessionState.GetBool(UpdateInProgressKey, false))
                return;

            SessionState.SetBool(UpdateCompletedKey, true);
            var managedPending = SessionState.GetBool(ManagedServerPendingKey, false);
            UPilotUpdateService.SetOperationPhase(
                UPilotUpdateOperationPhase.WaitingForReload,
                managedPending
                    ? "UPilot 包已更新，等待 Unity 重载后继续更新 MCP 服务…"
                    : "UPilot 包已更新，等待 Unity 重载后恢复服务…");
            ScheduleRestore();
        }

        public static bool IsManagedServerUpdatePending =>
            SessionState.GetBool(ManagedServerPendingKey, false);

        public static bool HasPendingManagedPackageUpdate =>
            EditorPrefs.GetBool(ProjectPersistentKey(PendingPackageRetryKey), false);

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
            var shouldRestart = SessionState.GetBool(RestartPendingKey, false);
            var hasPendingManagedPackageUpdate = HasPendingManagedPackageUpdate;
            ClearUpdateState();
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
            UPilotBridge.Instance.EnsureStarted();
            UPilotMcpServerManager.Instance.RestartServer();
        }

        private static void OnRegisteringPackages(PackageRegistrationEventArgs args)
        {
            if (!ContainsUPilot(args.changedFrom))
                return;

            var targetVersion = FindUPilotVersion(args.changedTo);
            var installManagedServer = ShouldInstallManagedServerAfterPackageUpdate();
            if (!PrepareForPackageUpdate(
                    targetVersion,
                    installManagedServerAfterUpdate: installManagedServer,
                    targetServerVersion: ""))
            {
                Debug.LogError(
                    "[UPilot] Package Manager is updating UPilot, but the MCP service could not be stopped before package registration.");
            }
        }

        private static void OnRegisteredPackages(PackageRegistrationEventArgs args)
        {
            if (!ContainsUPilot(args.changedTo))
                return;

            MarkPackageUpdateCompleted();
        }

        private static bool ContainsUPilot(System.Collections.Generic.IEnumerable<PackageInfo> packages)
        {
            if (packages == null)
                return false;

            foreach (var package in packages)
            {
                if (string.Equals(package?.name, PackageName, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static string FindUPilotVersion(System.Collections.Generic.IEnumerable<PackageInfo> packages)
        {
            if (packages == null)
                return "";

            foreach (var package in packages)
            {
                if (string.Equals(package?.name, PackageName, StringComparison.Ordinal))
                    return package.version ?? "";
            }

            return "";
        }

        private static void ScheduleRestore()
        {
            if (_restartScheduled)
                return;

            _restartScheduled = true;
            EditorApplication.update += WaitForEditorReady;
        }

        private static void WaitForEditorReady()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            EditorApplication.update -= WaitForEditorReady;
            _restartScheduled = false;
            TryRestoreAfterUpdate();
        }

        private static void TryRestoreAfterUpdate()
        {
            if (!SessionState.GetBool(UpdateCompletedKey, false))
                return;

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                ScheduleRestore();
                return;
            }

            var shouldRestart = SessionState.GetBool(RestartPendingKey, false);
            var targetVersion = SessionState.GetString(TargetVersionKey, "");
            var installManagedServer = SessionState.GetBool(ManagedServerPendingKey, false);
            var targetServerVersion = SessionState.GetString(TargetServerVersionKey, "");
            var preparedServerPath = SessionState.GetString(PreparedServerPathKey, "");
            if (!shouldRestart || !UPilotSetupState.IsCompleted)
            {
                ClearUpdateState();
                ClearPendingManagedPackageUpdate();
                UPilotUpdateService.SetOperationCompleted("UPilot 包已更新");
                return;
            }

            UPilotProjectConfig.Reload();
            UPilotProjectConfig.ApplyEndpoints(UPilotBridge.Instance);
            if (installManagedServer && UPilotServerRuntimeService.Instance.GetConfiguredMode() ==
                UPilotServerRuntimeMode.StandaloneExe)
            {
                ContinueManagedServerUpdateAfterPackage(
                    targetVersion,
                    targetServerVersion,
                    preparedServerPath,
                    shouldRestart);
                return;
            }

            ClearUpdateState();
            ClearPendingManagedPackageUpdate();
            var manager = UPilotMcpServerManager.Instance;
            manager.ValidateAndAutoFixPath();
            manager.RestartServer();
            UPilotBridge.Instance.EnsureStarted();
            UPilotUpdateService.SetOperationCompleted("UPilot 包已更新并已恢复服务");
            Debug.Log($"[UPilot] Package update completed ({targetVersion}); MCP service restart scheduled.");
        }

        private static async void ContinueManagedServerUpdateAfterPackage(
            string targetVersion,
            string targetServerVersion,
            string preparedServerPath,
            bool shouldRestart)
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
                        shouldRestart)
                    : await UPilotUpdateService.Instance.ActivatePreparedManagedServerAfterPackageUpdateAsync(
                        preparedServerPath,
                        targetServerVersion,
                        shouldRestart);
                ClearUpdateState();
                ClearPendingManagedPackageUpdate();
                if (updated)
                    Debug.Log($"[UPilot] Package update completed ({targetVersion}); managed MCP service updated.");
            }
            catch (Exception ex)
            {
                ClearUpdateState();
                UPilotUpdateService.SetOperationFailed("MCP 服务更新失败：" + ex.Message);
                Debug.LogError("[UPilot] Managed MCP service update after package update failed: " + ex);
                if (!shouldRestart || !UPilotSetupState.IsCompleted)
                    return;

                UPilotProjectConfig.Reload();
                UPilotProjectConfig.ApplyEndpoints(UPilotBridge.Instance);
                UPilotBridge.Instance.EnsureStarted();
                UPilotMcpServerManager.Instance.RestartServer();
            }
        }

        private static bool ShouldInstallManagedServerAfterPackageUpdate()
        {
            return UPilotSetupState.IsCompleted &&
                   UPilotServerRuntimeService.Instance.GetConfiguredMode() ==
                   UPilotServerRuntimeMode.StandaloneExe;
        }

        private static void ClearUpdateState()
        {
            SessionState.EraseBool(UpdateInProgressKey);
            SessionState.EraseBool(RestartPendingKey);
            SessionState.EraseBool(UpdateCompletedKey);
            SessionState.EraseString(TargetVersionKey);
            SessionState.EraseBool(ManagedServerPendingKey);
            SessionState.EraseString(TargetServerVersionKey);
            SessionState.EraseString(PreparedServerPathKey);
        }

        private static string ProjectPersistentKey(string key)
        {
            return key + "." + UPilotBridge.WsEndpointEditorPrefsKeySuffix;
        }
    }
}
