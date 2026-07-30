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
            Action<string, MessageType> notice = null)
        {
            if (SessionState.GetBool(UpdateInProgressKey, false))
                return true;

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

            if (!wasRunning)
                return true;

            notice?.Invoke("正在停止 MCP 服务以更新 UPilot 包…", MessageType.Info);
            UPilotBridge.Instance.Stop();
            if (manager.StopServerAndWaitForExit())
                return true;

            ClearUpdateState();
            UPilotBridge.Instance.EnsureStarted();
            manager.StartServer();
            notice?.Invoke("无法停止 MCP 服务，已取消包更新以避免文件占用", MessageType.Error);
            return false;
        }

        public static void MarkPackageUpdateCompleted()
        {
            if (!SessionState.GetBool(UpdateInProgressKey, false))
                return;

            SessionState.SetBool(UpdateCompletedKey, true);
            ScheduleRestore();
        }

        public static void RestoreAfterFailedUpdate()
        {
            var shouldRestart = SessionState.GetBool(RestartPendingKey, false);
            ClearUpdateState();
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
            if (!PrepareForPackageUpdate(targetVersion))
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
            ClearUpdateState();
            if (!shouldRestart || !UPilotSetupState.IsCompleted)
                return;

            UPilotProjectConfig.Reload();
            UPilotProjectConfig.ApplyEndpoints(UPilotBridge.Instance);
            var manager = UPilotMcpServerManager.Instance;
            manager.ValidateAndAutoFixPath();
            manager.RestartServer();
            UPilotBridge.Instance.EnsureStarted();
            Debug.Log($"[UPilot] Package update completed ({targetVersion}); MCP service restart scheduled.");
        }

        private static void ClearUpdateState()
        {
            SessionState.EraseBool(UpdateInProgressKey);
            SessionState.EraseBool(RestartPendingKey);
            SessionState.EraseBool(UpdateCompletedKey);
            SessionState.EraseString(TargetVersionKey);
        }
    }
}
