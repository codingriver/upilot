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
    }

    public sealed class UPilotUpdateService
    {
        public static UPilotUpdateService Instance { get; } = new();

        private const string OperationPhaseKey = "CodingRiver.UPilot.UpdateService.Phase";
        private const string OperationMessageKey = "CodingRiver.UPilot.UpdateService.Message";
        private const string OperationTargetUpmKey = "CodingRiver.UPilot.UpdateService.TargetUpm";
        private const string OperationTargetServerKey = "CodingRiver.UPilot.UpdateService.TargetServer";

        private AddRequest _upmRequest;
        private Action<string, MessageType> _notice;

        public void CheckForUpdates(Action<string, MessageType> notice)
        {
            UPilotUpdateWindow.Open(notice);
        }

        internal UPilotUpdateOperationStatus GetOperationStatus()
        {
            var phase = ReadOperationPhase();
            var downloadState = UPilotServerRuntimeService.Instance.DownloadState;
            if (downloadState.IsRunning)
                phase = UPilotUpdateOperationPhase.DownloadingService;

            var message = SessionState.GetString(OperationMessageKey, "");
            if (downloadState.IsRunning)
                message = FormatDownloadProgressLabel(downloadState);

            return new UPilotUpdateOperationStatus(
                phase,
                BuildOperationLabel(phase),
                message,
                SessionState.GetString(OperationTargetUpmKey, ""),
                SessionState.GetString(OperationTargetServerKey, ""));
        }

        public async void UpdateManagedServerAndRestart(Action<string, MessageType> notice)
        {
            _notice = notice;
            SetOperationPhase(UPilotUpdateOperationPhase.StoppingService, "正在准备更新 MCP 服务…");
            UPilotReleaseManifest manifest;
            try
            {
                manifest = await UPilotServerRuntimeService.Instance.FetchReleaseManifestAsync();
            }
            catch (Exception ex)
            {
                SetOperationFailed("读取发布清单失败：" + ex.Message);
                notice?.Invoke("读取发布清单失败：" + ex.Message, MessageType.Error);
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
            _notice = notice;
            SetOperationPhase(UPilotUpdateOperationPhase.StoppingService, "正在读取更新信息…");
            UPilotReleaseManifest manifest;
            try
            {
                manifest = await UPilotServerRuntimeService.Instance.FetchReleaseManifestAsync();
            }
            catch (Exception ex)
            {
                SetOperationFailed("读取发布清单失败：" + ex.Message);
                notice?.Invoke("读取发布清单失败：" + ex.Message, MessageType.Error);
                return;
            }

            SetOperationTargets(manifest.UpmVersion, manifest.ServerVersion);
            if (updatePackage)
            {
                StartPackageUpdate(manifest, updateManagedServer, notice);
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
                    SetOperationFailed("读取发布清单失败：" + ex.Message);
                    notice?.Invoke("读取发布清单失败：" + ex.Message, MessageType.Error);
                    return false;
                }
            }

            return await InstallManagedServerAndRestartAsync(
                expectedServerVersion,
                shouldRestart,
                stopFirst: false,
                notice: notice);
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
            SessionState.SetString(OperationPhaseKey, phase.ToString());
            SessionState.SetString(OperationMessageKey, message ?? "");
            if (targetUpmVersion != null)
                SessionState.SetString(OperationTargetUpmKey, targetUpmVersion);
            if (targetServerVersion != null)
                SessionState.SetString(OperationTargetServerKey, targetServerVersion);
        }

        internal static void SetOperationTargets(string targetUpmVersion, string targetServerVersion)
        {
            if (targetUpmVersion != null)
                SessionState.SetString(OperationTargetUpmKey, targetUpmVersion);
            if (targetServerVersion != null)
                SessionState.SetString(OperationTargetServerKey, targetServerVersion);
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
            SessionState.EraseString(OperationPhaseKey);
            SessionState.EraseString(OperationMessageKey);
            SessionState.EraseString(OperationTargetUpmKey);
            SessionState.EraseString(OperationTargetServerKey);
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

        private async Task<bool> InstallManagedServerAndRestartAsync(
            string expectedServerVersion,
            bool shouldRestart,
            bool stopFirst,
            Action<string, MessageType> notice)
        {
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
                if (shouldRestart)
                    manager.StartServer();
                var message = string.IsNullOrEmpty(state.ErrorMessage) ? "MCP 服务更新未完成" : state.ErrorMessage;
                SetOperationFailed(message);
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
                return false;
            }

            SetOperationCompleted($"MCP 服务已更新并启动，当前版本 {runningVersion}");
            notice?.Invoke($"MCP 服务已更新并启动，当前版本 {runningVersion}", MessageType.Info);
            return true;
        }

        public async void UpdateUpmFromManifest(Action<string, MessageType> notice)
        {
            await UpdateUpmFromManifestAsync(notice);
        }

        private async Task UpdateUpmFromManifestAsync(Action<string, MessageType> notice)
        {
            _notice = notice;
            SetOperationPhase(UPilotUpdateOperationPhase.StoppingService, "正在读取更新信息…");
            UPilotReleaseManifest manifest;
            try
            {
                manifest = await UPilotServerRuntimeService.Instance.FetchReleaseManifestAsync();
            }
            catch (Exception ex)
            {
                SetOperationFailed("读取发布清单失败：" + ex.Message);
                notice?.Invoke("读取发布清单失败：" + ex.Message, MessageType.Error);
                return;
            }

            StartPackageUpdate(manifest, installManagedServerAfterUpdate: false, notice);
        }

        private void StartPackageUpdate(
            UPilotReleaseManifest manifest,
            bool installManagedServerAfterUpdate,
            Action<string, MessageType> notice)
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

            var channel = string.IsNullOrWhiteSpace(manifest.Channel)
                ? UPilotServerRuntimeService.ResolveUpdateChannel()
                : manifest.Channel;
            var revision = channel.IndexOf("main", StringComparison.OrdinalIgnoreCase) >= 0
                ? "main"
                : "v" + manifest.UpmVersion;
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
                    manifest.ServerVersion))
            {
                SetOperationFailed("无法停止 MCP 服务，已取消更新");
                return;
            }

            try
            {
                _upmRequest = Client.Add(identifier);
            }
            catch (Exception ex)
            {
                UPilotPackageUpdateLifecycle.RestoreAfterFailedUpdate();
                SetOperationFailed("无法启动 UPilot 包更新：" + ex.Message);
                notice?.Invoke("无法启动 UPilot 包更新：" + ex.Message, MessageType.Error);
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
            var raw = SessionState.GetString(OperationPhaseKey, UPilotUpdateOperationPhase.None.ToString());
            return Enum.TryParse(raw, out UPilotUpdateOperationPhase phase)
                ? phase
                : UPilotUpdateOperationPhase.None;
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
