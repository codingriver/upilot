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

        public async void CheckForUpdates(Action<string, MessageType> notice)
        {
            _notice = notice;
            try
            {
                var manifest = await UPilotServerRuntimeService.Instance.FetchReleaseManifestAsync();
                var compatibility = UPilotServerRuntimeService.Instance.EvaluateManifestCompatibility(manifest);
                var message =
                    $"UPM {UPilotServerRuntimeService.UpmVersion} -> {manifest.UpmVersion}\n" +
                    $"MCP Server {(string.IsNullOrEmpty(UPilotMcpServerManager.Instance.GetStatus().ServerVersion) ? "unknown" : UPilotMcpServerManager.Instance.GetStatus().ServerVersion)} -> {manifest.ServerVersion}\n" +
                    $"Channel: {manifest.Channel}\n" +
                    $"Compatibility: {(compatibility.IsCompatible ? "OK" : "Blocked")} - {compatibility.Reason}";
                EditorUtility.DisplayDialog("UPilot 更新信息", message, "OK");
            }
            catch (Exception ex)
            {
                notice?.Invoke("检查更新失败：" + ex.Message, MessageType.Error);
            }
        }

        public async void UpdateServerExeAndRestart(Action<string, MessageType> notice)
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

            var compatibility = UPilotServerRuntimeService.Instance.EvaluateManifestCompatibility(manifest);
            if (!compatibility.IsCompatible)
            {
                notice?.Invoke("Server 更新被兼容校验阻止：" + compatibility.Reason, MessageType.Error);
                return;
            }

            UPilotServerRuntimeService.Instance.StartDownloadLatestServerExe();
            notice?.Invoke("正在下载 MCP Server exe…", MessageType.Info);
            await WaitForDownloadAsync();
            var state = UPilotServerRuntimeService.Instance.DownloadState;
            if (!state.IsComplete)
            {
                notice?.Invoke(string.IsNullOrEmpty(state.ErrorMessage) ? "Server 更新未完成" : state.ErrorMessage, MessageType.Error);
                return;
            }

            UPilotMcpServerManager.Instance.RestartServer();
            notice?.Invoke("MCP Server 已更新并重启", MessageType.Info);
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

            var compatibility = UPilotServerRuntimeService.Instance.EvaluateManifestCompatibility(manifest);
            if (!compatibility.IsCompatible)
            {
                notice?.Invoke("UPM 更新被兼容校验阻止：" + compatibility.Reason, MessageType.Error);
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
                    return;
            }

            var identifier = $"https://github.com/codingriver/upilot.git#v{manifest.UpmVersion}";
            _upmRequest = Client.Add(identifier);
            EditorApplication.update += PollUpmUpdate;
            notice?.Invoke("正在更新 UPilot UPM 包…", MessageType.Info);
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
                _notice?.Invoke("UPM 更新失败：" + (_upmRequest.Error?.message ?? "unknown"), MessageType.Error);
                _upmRequest = null;
                return;
            }

            _notice?.Invoke("UPM 已更新，等待 Unity Domain Reload 后重启 Bridge", MessageType.Info);
            _upmRequest = null;
            EditorApplication.delayCall += () =>
            {
                UPilotBridge.Instance.Restart();
                UPilotMcpServerManager.Instance.RestartServer();
            };
        }
    }
}
