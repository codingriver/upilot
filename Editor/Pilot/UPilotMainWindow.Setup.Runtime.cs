// -----------------------------------------------------------------------
// UPilot Editor - first setup runtime selection.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public sealed partial class UPilotMainWindow
    {
        private async void BeginRuntimeInspection()
        {
            _setupPythonProbe ??= UPilotServerRuntimeService.Instance.ProbePython();
            if (UPilotServerRuntimeService.IsSourceUpdateChannel())
            {
                _setupRuntimeChoice = SetupRuntimeChoice.Python;
                _showPythonAdvanced = true;
                _setupManifest = null;
                _setupManifestError = "";
                _setupManifestLoading = false;
                return;
            }

            if (_setupManifestLoading || _setupManifest != null || !string.IsNullOrWhiteSpace(_setupManifestError))
                return;

            _setupManifestLoading = true;
            _setupManifestError = "";
            try
            {
                _setupManifest = await UPilotServerRuntimeService.Instance.FetchReleaseManifestAsync();
            }
            catch (Exception ex)
            {
                _setupManifestError = ex.Message;
            }
            finally
            {
                _setupManifestLoading = false;
                Repaint();
            }
        }

        private void DrawSetupRuntimeStep()
        {
            var sourceChannel = UPilotServerRuntimeService.IsSourceUpdateChannel();
            BeginRuntimeInspection();
            if (sourceChannel)
            {
                EditorGUILayout.HelpBox(
                    "当前是开发/source 安装，UPilot 将使用本机 Python 运行 MCP 服务；自动管理服务只随正式 tag 版本发布。",
                    MessageType.Info);
                _setupRuntimeChoice = SetupRuntimeChoice.Python;
                _showPythonAdvanced = true;
                DrawPythonRuntimeSetup();
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "推荐由 UPilot 管理 MCP 服务。UPilot 会安装与当前版本匹配的服务，并在后续更新时保持版本一致。",
                    MessageType.Info);

                DrawManagedRuntimeSetup();
                EditorGUILayout.Space(6);
                DrawPythonRuntimeSetup();
            }
            EditorGUILayout.Space(12);

            var ready = SetupRuntimeReady(out _);
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawSetupButton("上一步", SetupNeutralColor, 112, () => SetSetupStep(0));
                GUILayout.FlexibleSpace();
                DrawSetupButton(
                    "下一步",
                    ready ? SetupReadyColor : SetupBlockedColor,
                    132,
                    ContinueFromRuntimeStep,
                    !UPilotServerRuntimeService.Instance.DownloadState.IsRunning &&
                    !UPilotServerRuntimeService.Instance.PythonEnvironmentState.IsRunning);
            }
        }

        private void DrawManagedRuntimeSetup()
        {
            if (UPilotServerRuntimeService.IsSourceUpdateChannel())
                return;

            var runtime = UPilotServerRuntimeService.Instance;
            var state = runtime.DownloadState;
            var ready = ManagedRuntimeReady(out var statusMessage);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("推荐：由 UPilot 管理服务", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField(
                        UPilotServerRuntimeService.CurrentPlatformDisplayName,
                        EditorStyles.miniLabel,
                        GUILayout.Width(150));
                }

                if (_setupManifestLoading)
                    EditorGUILayout.HelpBox("正在获取适用于当前平台的服务信息…", MessageType.Info);
                else if (!string.IsNullOrWhiteSpace(_setupManifestError))
                    EditorGUILayout.HelpBox("无法获取服务信息：" + _setupManifestError, MessageType.Error);
                else
                {
                    if (_setupManifest != null)
                        EditorGUILayout.LabelField("推荐版本", _setupManifest.ServerVersion, EditorStyles.miniLabel);
                    EditorGUILayout.HelpBox(statusMessage, ready ? MessageType.Info : MessageType.Warning);
                }

                if (state.IsRunning || state.IsComplete || !string.IsNullOrEmpty(state.ErrorMessage))
                {
                    var progressLabel = string.IsNullOrWhiteSpace(state.Phase) ? "准备中" : state.Phase;
                    EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(false, 20), state.Progress, progressLabel);
                    var sizeText = state.TotalBytes > 0
                        ? $"{FormatSetupBytes(state.BytesReceived)} / {FormatSetupBytes(state.TotalBytes)}"
                        : FormatSetupBytes(state.BytesReceived);
                    var segmentText = state.SegmentCount > 1
                        ? $" · {state.CompletedSegments}/{state.SegmentCount} 个下载任务"
                        : "";
                    EditorGUILayout.LabelField(sizeText + segmentText, EditorStyles.miniLabel);
                    if (!string.IsNullOrEmpty(state.ErrorMessage))
                        EditorGUILayout.HelpBox(state.ErrorMessage, MessageType.Error);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(state.IsRunning || _setupManifestLoading))
                    {
                        var previous = GUI.backgroundColor;
                        GUI.backgroundColor = ready ? SetupNeutralColor : SetupBlockedColor;
                        if (GUILayout.Button(ready ? "重新安装推荐服务" : "安装推荐服务", GUILayout.Height(30)))
                        {
                            _setupRuntimeChoice = SetupRuntimeChoice.Managed;
                            runtime.StartDownloadLatestServerExe();
                        }
                        GUI.backgroundColor = previous;
                    }

                    using (new EditorGUI.DisabledScope(!state.IsRunning))
                    {
                        if (GUILayout.Button("取消", GUILayout.Width(64), GUILayout.Height(30)))
                            runtime.CancelDownload();
                    }

                    if (GUILayout.Button("选择本地服务文件", GUILayout.Width(128), GUILayout.Height(30)))
                    {
                        var selected = EditorUtility.OpenFilePanel(
                            "选择 UPilot MCP 服务文件",
                            "",
                            UPilotServerRuntimeService.CurrentPlatformFileExtension);
                        if (!string.IsNullOrEmpty(selected) && File.Exists(selected))
                        {
                            runtime.SetStandaloneExeRuntime(selected, _setupManifest?.ServerVersion ?? "");
                            _setupRuntimeChoice = SetupRuntimeChoice.Managed;
                        }
                    }
                }
            }
        }

        private void DrawPythonRuntimeSetup()
        {
            var sourceChannel = UPilotServerRuntimeService.IsSourceUpdateChannel();
            if (sourceChannel)
            {
                EditorGUILayout.LabelField("使用本机 Python", EditorStyles.boldLabel);
            }
            else
            {
                _showPythonAdvanced = EditorGUILayout.Foldout(
                    _showPythonAdvanced,
                    "高级：使用本机 Python",
                    true);
                if (!_showPythonAdvanced)
                    return;
            }

            _setupPythonProbe ??= UPilotServerRuntimeService.Instance.ProbePython();
            var runtime = UPilotServerRuntimeService.Instance;
            var envState = runtime.PythonEnvironmentState;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var messageType = _setupPythonProbe.InterpreterUsable ? MessageType.Info : MessageType.Warning;
                EditorGUILayout.HelpBox(_setupPythonProbe.Message, messageType);
                if (!string.IsNullOrWhiteSpace(_setupPythonProbe.PythonPath))
                    EditorGUILayout.SelectableLabel(
                        _setupPythonProbe.PythonPath,
                        EditorStyles.textField,
                        GUILayout.Height(EditorGUIUtility.singleLineHeight));

                if (_setupPythonProbe.Dependencies.Count > 0)
                {
                    foreach (var dependency in _setupPythonProbe.Dependencies)
                        EditorGUILayout.LabelField(dependency.Key, dependency.Value ? "已安装" : "缺失", EditorStyles.miniLabel);
                }

                if (envState.IsRunning || envState.IsComplete || !string.IsNullOrEmpty(envState.ErrorMessage))
                {
                    var phase = string.IsNullOrWhiteSpace(envState.Phase) ? "准备中" : envState.Phase;
                    EditorGUI.ProgressBar(
                        EditorGUILayout.GetControlRect(false, 20),
                        GetSetupPythonProgress(envState),
                        phase);
                    if (!string.IsNullOrEmpty(envState.ErrorMessage))
                        EditorGUILayout.HelpBox(envState.ErrorMessage, MessageType.Error);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("重新检测", GUILayout.Width(82), GUILayout.Height(30)))
                        _setupPythonProbe = runtime.ProbePython();

                    var pythonReady = _setupPythonProbe.IsUsable ||
                                      runtime.IsPythonRuntimeConfigured(out _);
                    var pythonLabel = pythonReady
                        ? "使用本机 Python"
                        : _setupPythonProbe.InterpreterUsable
                            ? "配置并使用 Python"
                            : "需要 Python 3.11+";
                    var previous = GUI.backgroundColor;
                    GUI.backgroundColor = pythonReady ? SetupReadyColor : SetupBlockedColor;
                    using (new EditorGUI.DisabledScope(envState.IsRunning))
                    {
                        if (GUILayout.Button(pythonLabel, GUILayout.Height(30)))
                            SelectOrConfigurePythonRuntime();
                    }
                    GUI.backgroundColor = previous;

                    using (new EditorGUI.DisabledScope(!envState.IsRunning))
                    {
                        if (GUILayout.Button("取消", GUILayout.Width(64), GUILayout.Height(30)))
                            runtime.CancelPythonEnvironmentSetup();
                    }
                }
            }
        }

        private void SelectOrConfigurePythonRuntime()
        {
            var runtime = UPilotServerRuntimeService.Instance;
            if (runtime.IsPythonRuntimeConfigured(out _))
            {
                _setupRuntimeChoice = SetupRuntimeChoice.Python;
                return;
            }

            if (_setupPythonProbe == null || !_setupPythonProbe.InterpreterUsable)
            {
                EditorUtility.DisplayDialog(
                    "需要 Python 3.11+",
                    "未找到可用的 Python 3.11 或更高版本。请先安装 Python，然后返回此处重新检测。",
                    "知道了");
                return;
            }

            _setupRuntimeChoice = SetupRuntimeChoice.Python;
            if (_setupPythonProbe.IsUsable)
                runtime.SetPythonRuntime(_setupPythonProbe.PythonPath);
            else
                runtime.StartAutoConfigurePythonEnvironment();
        }

        private void ContinueFromRuntimeStep()
        {
            if (SetupRuntimeReady(out _))
            {
                SetSetupStep(2);
                return;
            }

            if (_setupRuntimeChoice == SetupRuntimeChoice.Python)
            {
                _showPythonAdvanced = true;
                SelectOrConfigurePythonRuntime();
                return;
            }

            if (_setupManifestLoading)
                return;
            if (!string.IsNullOrWhiteSpace(_setupManifestError))
            {
                _setupManifest = null;
                BeginRuntimeInspection();
                return;
            }

            var version = _setupManifest?.ServerVersion ?? "当前版本";
            if (!EditorUtility.DisplayDialog(
                    "安装推荐服务？",
                    $"当前没有安装与 UPilot 匹配的服务。将下载并安装适用于 {UPilotServerRuntimeService.CurrentPlatformDisplayName} 的 {version} 服务。",
                    "开始安装",
                    "取消"))
                return;

            UPilotServerRuntimeService.Instance.StartDownloadLatestServerExe();
        }

        private bool SetupRuntimeReady(out string reason)
        {
            if (_setupRuntimeChoice == SetupRuntimeChoice.Python)
            {
                var ready = UPilotServerRuntimeService.Instance.IsPythonRuntimeConfigured(out _);
                reason = ready ? "本机 Python 已配置。" : "本机 Python 尚未配置完成。";
                return ready;
            }

            return ManagedRuntimeReady(out reason);
        }

        private bool ManagedRuntimeReady(out string reason)
        {
            if (UPilotServerRuntimeService.IsSourceUpdateChannel())
            {
                reason = "开发/source 安装仅支持本机 Python。";
                return false;
            }

            if (_setupManifest == null)
            {
                reason = _setupManifestLoading ? "正在获取服务信息…" : "尚未获取服务信息。";
                return false;
            }

            if (!UPilotServerRuntimeService.IsVersionAtLeast(
                    UPilotServerRuntimeService.UpmVersion,
                    _setupManifest.MinCompatibleUpm))
            {
                reason = $"当前 UPilot 包版本过低，需要 {_setupManifest.MinCompatibleUpm} 或更高版本。";
                return false;
            }

            var runtime = UPilotServerRuntimeService.Instance;
            if (runtime.GetConfiguredMode() != UPilotServerRuntimeMode.StandaloneExe ||
                !runtime.IsStandaloneExeConfigured(out _))
            {
                reason = $"尚未安装适用于 {UPilotServerRuntimeService.CurrentPlatformDisplayName} 的推荐服务。";
                return false;
            }

            var configuredVersion = UPilotProjectConfig.Current.runtime?.serverVersion ?? "";
            if (string.IsNullOrWhiteSpace(configuredVersion) ||
                UPilotServerRuntimeService.CompareVersions(configuredVersion, _setupManifest.ServerVersion) != 0)
            {
                reason = $"当前服务版本 {configuredVersion} 与推荐版本 {_setupManifest.ServerVersion} 不匹配。";
                return false;
            }

            reason = $"推荐服务 {_setupManifest.ServerVersion} 已安装并通过验证。";
            return true;
        }

        private static float GetSetupPythonProgress(UPilotPythonEnvironmentState state)
        {
            if (state.IsComplete) return 1f;
            if (state.IsCancelled || !string.IsNullOrEmpty(state.ErrorMessage)) return 0f;
            var phase = state.Phase ?? "";
            if (phase.IndexOf("检测", StringComparison.OrdinalIgnoreCase) >= 0) return 0.1f;
            if (phase.IndexOf("创建", StringComparison.OrdinalIgnoreCase) >= 0) return 0.3f;
            if (phase.IndexOf("升级", StringComparison.OrdinalIgnoreCase) >= 0) return 0.55f;
            if (phase.IndexOf("安装", StringComparison.OrdinalIgnoreCase) >= 0) return 0.8f;
            return state.IsRunning ? 0.2f : 0f;
        }
    }
}
