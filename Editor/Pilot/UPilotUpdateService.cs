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
    public sealed class UPilotUpdateService
    {
        public static UPilotUpdateService Instance { get; } = new();

        private AddRequest _upmRequest;
        private Action<string, MessageType> _notice;

        public void CheckForUpdates(Action<string, MessageType> notice)
        {
            UPilotUpdateWindow.Open(notice);
        }

        public async void UpdateManagedServerAndRestart(Action<string, MessageType> notice)
        {
            _notice = notice;
            UPilotReleaseManifest manifest;
            try
            {
                manifest = await UPilotServerRuntimeService.Instance.FetchReleaseManifestAsync();
            }
            catch (Exception ex)
            {
                notice?.Invoke("读取发布清单失败：" + ex.Message, MessageType.Error);
                return;
            }

            var currentUpm = UPilotServerRuntimeService.UpmVersion;
            if (!UPilotServerRuntimeService.IsVersionAtLeast(currentUpm, manifest.MinCompatibleUpm))
            {
                notice?.Invoke($"请先将 UPilot 包更新到 {manifest.MinCompatibleUpm} 或更高版本", MessageType.Warning);
                return;
            }

            var manager = UPilotMcpServerManager.Instance;
            var shouldRestart = UPilotSetupState.IsCompleted;
            notice?.Invoke("正在停止 MCP 服务…", MessageType.Info);
            if (!manager.StopServerAndWaitForExit())
            {
                notice?.Invoke("无法停止 MCP 服务，已取消更新以避免文件占用", MessageType.Error);
                return;
            }

            UPilotServerRuntimeService.Instance.StartDownloadLatestServerExe();
            notice?.Invoke("正在更新 MCP 服务…", MessageType.Info);
            await WaitForDownloadAsync();
            var state = UPilotServerRuntimeService.Instance.DownloadState;
            if (!state.IsComplete)
            {
                if (shouldRestart)
                    manager.StartServer();
                notice?.Invoke(string.IsNullOrEmpty(state.ErrorMessage) ? "MCP 服务更新未完成" : state.ErrorMessage, MessageType.Error);
                return;
            }

            if (!shouldRestart)
            {
                notice?.Invoke("MCP 服务已更新，将在完成首次设置后启动", MessageType.Info);
                return;
            }

            notice?.Invoke("MCP 服务已更新，正在重新启动…", MessageType.Info);
            manager.StartServer();
            var runningVersion = await manager.WaitForServerVersionAsync(manifest.ServerVersion);
            if (string.IsNullOrWhiteSpace(runningVersion) ||
                UPilotServerRuntimeService.CompareVersions(runningVersion, manifest.ServerVersion) < 0)
            {
                var versionText = string.IsNullOrWhiteSpace(runningVersion) ? "未能读取版本" : runningVersion;
                notice?.Invoke(
                    $"MCP 服务已更新，但启动确认失败（当前：{versionText}，期望：{manifest.ServerVersion}），请检查服务日志",
                    MessageType.Error);
                return;
            }

            notice?.Invoke($"MCP 服务已更新并启动，当前版本 {runningVersion}", MessageType.Info);
        }

        public async void UpdateUpmFromManifest(Action<string, MessageType> notice)
        {
            _notice = notice;
            UPilotReleaseManifest manifest;
            try
            {
                manifest = await UPilotServerRuntimeService.Instance.FetchReleaseManifestAsync();
            }
            catch (Exception ex)
            {
                notice?.Invoke("读取发布清单失败：" + ex.Message, MessageType.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(manifest.UpmVersion))
            {
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

            if (!UPilotPackageUpdateLifecycle.PrepareForPackageUpdate(manifest.UpmVersion, notice))
                return;

            try
            {
                _upmRequest = Client.Add(identifier);
            }
            catch (Exception ex)
            {
                UPilotPackageUpdateLifecycle.RestoreAfterFailedUpdate();
                notice?.Invoke("无法启动 UPilot 包更新：" + ex.Message, MessageType.Error);
                return;
            }

            EditorApplication.update += PollUpmUpdate;
            notice?.Invoke("正在更新 UPilot 包…", MessageType.Info);
        }

        private static async Task WaitForDownloadAsync()
        {
            while (UPilotServerRuntimeService.Instance.DownloadState.IsRunning)
                await Task.Delay(200);
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
                return;
            }

            _notice?.Invoke("UPilot 包已更新，Unity 重载后将自动恢复服务", MessageType.Info);
            _upmRequest = null;
            UPilotPackageUpdateLifecycle.MarkPackageUpdateCompleted();
        }
    }
}
