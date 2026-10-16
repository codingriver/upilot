// -----------------------------------------------------------------------
// UPilot Editor - first setup runtime selection.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public sealed partial class UPilotMainWindow
    {
        private enum SetupPythonEnvironmentChoice { Automatic, Existing }
        private enum SetupRuntimeContinuation { None, ManagedDownload, PythonEnvironment }

        private SetupPythonEnvironmentChoice _setupPythonEnvironmentChoice;
        private SetupRuntimeContinuation _setupRuntimeContinuation;
        private bool _setupRuntimeSourceChannel;
        private bool _setupPythonInspectionRunning;
        private bool _setupPythonValidationRunning;
        private bool _setupLocalExeValidationRunning;
        private bool _setupRuntimeCompletionSucceeded;
        private int _setupRuntimeGeneration;
        private double _setupRuntimeOperationStartedAt;
        private string _setupExistingPythonPath = "";
        private string _setupPythonValidatedPath = "";
        private string _setupPythonEnvironmentPath = "";
        private string _setupPythonEnvironmentScope = "";
        private string _setupPythonEnvironmentPathError = "";
        private string _setupRuntimeError = "";
        private string _setupRuntimeNotice = "";

        internal static string GetRuntimeSelectionModeLabel(UPilotServerRuntimeMode mode) =>
            mode == UPilotServerRuntimeMode.StandaloneExe ? "本地 EXE" : "本机 Python";

        private void ResetSetupRuntimeState()
        {
            ClearSetupRuntimeContinuation();
            _setupRuntimeSourceChannel = UPilotServerRuntimeService.IsSourceUpdateChannel();
            _setupRuntimeCompletionSucceeded = false;
            _setupManifest = null;
            _setupManifestError = "";
            _setupPythonProbe = null;
            _setupPythonEnvironmentChoice = SetupPythonEnvironmentChoice.Automatic;
            _setupExistingPythonPath = "";
            _setupPythonValidatedPath = "";
            _setupPythonEnvironmentPath = "";
            _setupPythonEnvironmentScope = "";
            _setupPythonEnvironmentPathError = "";
            _setupRuntimeError = "";
            _setupRuntimeNotice = "";
        }

        private void InitializeSetupRuntimeChoice()
        {
            _setupRuntimeSourceChannel = UPilotServerRuntimeService.IsSourceUpdateChannel();
            var config = UPilotProjectConfig.Current.runtime;
            var mode = UPilotServerRuntimeService.Instance.GetConfiguredMode();
            if (mode == UPilotServerRuntimeMode.Python && !string.IsNullOrWhiteSpace(config?.pythonPath))
                SelectSetupPythonEnvironment(config.pythonPath);
            else
                _setupRuntimeChoice = mode == UPilotServerRuntimeMode.StandaloneExe
                    ? SetupRuntimeChoice.Managed
                    : _setupRuntimeSourceChannel ? SetupRuntimeChoice.Python : SetupRuntimeChoice.Managed;
        }

        internal void SelectSetupPythonEnvironment(string interpreterPath)
        {
            ClearSetupRuntimeContinuation();
            _setupRuntimeChoice = SetupRuntimeChoice.Python;
            _setupExistingPythonPath = (interpreterPath ?? "").Trim();
            _setupPythonEnvironmentChoice = _setupExistingPythonPath.Length == 0
                ? SetupPythonEnvironmentChoice.Automatic : SetupPythonEnvironmentChoice.Existing;
            _setupPythonProbe = null;
            _setupPythonValidatedPath = "";
            _setupRuntimeCompletionSucceeded = false;
            _setupRuntimeError = "";
            _setupRuntimeNotice = "";
        }

        private void ClearSetupRuntimeContinuation()
        {
            _setupRuntimeContinuation = SetupRuntimeContinuation.None;
            _setupRuntimeOperationStartedAt = 0;
            _setupRuntimeGeneration++;
            _setupManifestLoading = false;
            _setupPythonInspectionRunning = false;
        }

        private void BeginRuntimeInspection() => _ = InspectSetupRuntimeAsync();

        internal async Task InspectSetupRuntimeAsync(
            Func<Task<UPilotReleaseManifest>> fetchManifest = null,
            Func<UPilotPythonProbeResult> probePython = null)
        {
            var generation = _setupRuntimeGeneration;
            if (_setupRuntimeChoice == SetupRuntimeChoice.Managed)
            {
                if (_setupRuntimeSourceChannel || _setupManifestLoading || _setupManifest != null ||
                    !string.IsNullOrWhiteSpace(_setupManifestError))
                    return;
                _setupManifestLoading = true;
                try
                {
                    var manifest = await (fetchManifest ?? UPilotServerRuntimeService.Instance.FetchReleaseManifestAsync)();
                    if (this != null && generation == _setupRuntimeGeneration)
                        _setupManifest = manifest;
                }
                catch (Exception ex)
                {
                    Debug.LogError("[UPilot] First setup service information failed.\nException=" + ex);
                    if (this != null && generation == _setupRuntimeGeneration)
                        _setupManifestError = ex.Message;
                }
                finally
                {
                    if (this != null && generation == _setupRuntimeGeneration)
                    {
                        _setupManifestLoading = false;
                        Repaint();
                    }
                }
                return;
            }

            if (_setupPythonEnvironmentChoice != SetupPythonEnvironmentChoice.Automatic ||
                _setupPythonInspectionRunning || _setupPythonProbe != null ||
                !string.IsNullOrWhiteSpace(_setupPythonEnvironmentPathError))
                return;
            _setupPythonInspectionRunning = true;
            try
            {
                _setupPythonEnvironmentPath = UPilotServerRuntimeService.GetPythonEnvironmentPath(out _setupPythonEnvironmentScope);
                var probe = await Task.Run(probePython ?? UPilotServerRuntimeService.Instance.ProbePython);
                if (this != null && generation == _setupRuntimeGeneration)
                    _setupPythonProbe = probe;
            }
            catch (Exception ex)
            {
                Debug.LogError("[UPilot] First setup Python inspection failed.\nException=" + ex);
                if (this != null && generation == _setupRuntimeGeneration)
                    _setupPythonEnvironmentPathError = ex.Message;
            }
            finally
            {
                if (this != null && generation == _setupRuntimeGeneration)
                {
                    _setupPythonInspectionRunning = false;
                    Repaint();
                }
            }
        }

        private void DrawSetupRuntimeStep()
        {
            BeginRuntimeInspection();
            if (_setupRuntimeChoice == SetupRuntimeChoice.Managed)
                DrawManagedRuntimeSetup();
            else
                DrawPythonRuntimeSetup();

            if (!string.IsNullOrWhiteSpace(_setupRuntimeError))
                EditorGUILayout.HelpBox(_setupRuntimeError, MessageType.Error);
            else if (!string.IsNullOrWhiteSpace(_setupRuntimeNotice))
                EditorGUILayout.HelpBox(_setupRuntimeNotice, MessageType.Info);

            var canContinue = CanContinueFromRuntimeStep(out var reason);
            if (!canContinue && !string.IsNullOrWhiteSpace(reason))
                EditorGUILayout.HelpBox(reason,
                    IsSetupRuntimeBusy() || _setupManifestLoading || _setupPythonInspectionRunning
                        ? MessageType.Info : MessageType.Warning);
            EditorGUILayout.Space(12);
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawSetupButton("上一步", SetupNeutralColor, 112, () => SetSetupStep(0));
                GUILayout.FlexibleSpace();
                DrawSetupButton(GetSetupRuntimeActionLabel(), canContinue ? SetupPrimaryColor : SetupNeutralColor, 168,
                    () => ContinueFromRuntimeStep(), canContinue);
            }
        }

        private string GetSetupRuntimeActionLabel()
        {
            var runtime = UPilotServerRuntimeService.Instance;
            if (_setupPythonValidationRunning || _setupLocalExeValidationRunning) return "正在验证…";
            if (runtime.DownloadState.IsRunning) return "正在安装…";
            if (runtime.PythonEnvironmentState.IsRunning) return "正在配置…";
            if (_setupRuntimeChoice == SetupRuntimeChoice.Managed)
            {
                if (_setupManifestLoading) return "正在获取服务信息…";
                if (!string.IsNullOrWhiteSpace(_setupManifestError)) return "重试获取信息";
                if (ManagedRuntimeReady(out _)) return "下一步";
                if (_setupRuntimeSourceChannel) return "下一步";
                return string.IsNullOrWhiteSpace(_setupRuntimeError) ? "下一步（下载安装）" : "重试安装";
            }
            if (_setupPythonInspectionRunning) return "正在检测环境…";
            if (SetupRuntimeReady(out _)) return "下一步";
            return _setupPythonEnvironmentChoice == SetupPythonEnvironmentChoice.Existing
                ? "验证并继续" : "配置并继续";
        }

        private void DrawManagedRuntimeSetup()
        {
            var runtime = UPilotServerRuntimeService.Instance;
            var state = runtime.DownloadState;
            EditorGUILayout.HelpBox(_setupRuntimeSourceChannel
                ? "当前为开发/source 安装，自动下载仅随正式 tag 版本发布。请选择本地服务文件。"
                : "UPilot 服务是独立的 MCP 服务程序。点击下一步，将自动下载、校验并安装与当前 UPilot 版本匹配的服务。",
                MessageType.Info);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("由 UPilot 管理服务（推荐）", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("适用平台", UPilotServerRuntimeService.CurrentPlatformDisplayName, EditorStyles.miniLabel);
                if (_setupManifest != null)
                    EditorGUILayout.LabelField("服务版本", _setupManifest.ServerVersion, EditorStyles.miniLabel);
                if (!string.IsNullOrWhiteSpace(_setupManifestError))
                    EditorGUILayout.HelpBox("无法获取服务信息：" + _setupManifestError, MessageType.Error);
                DrawManagedServerLocation(runtime);
                if (ManagedRuntimeReady(out var readyMessage))
                    DrawSetupRuntimeSuccess(readyMessage);
                else if (!_setupRuntimeSourceChannel && _setupManifest != null && string.IsNullOrWhiteSpace(_setupRuntimeError))
                    EditorGUILayout.LabelField("尚未安装时，点击下一步即可下载安装。", EditorStyles.wordWrappedMiniLabel);

                if (state.IsRunning || state.IsComplete || state.IsCancelled || !string.IsNullOrEmpty(state.ErrorMessage))
                {
                    EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(false, 20),
                        UPilotDownloadHelper.GetProgress(state), UPilotUpdateService.FormatDownloadProgressLabel(state));
                    var detail = UPilotUpdateService.FormatDownloadProgressDetail(state);
                    if (!string.IsNullOrWhiteSpace(detail))
                        EditorGUILayout.LabelField(detail, EditorStyles.miniLabel);
                    if (!string.IsNullOrWhiteSpace(state.WarningMessage))
                        EditorGUILayout.HelpBox(state.WarningMessage, MessageType.Warning);
                    if (!string.IsNullOrWhiteSpace(state.ErrorMessage) && _setupRuntimeError != state.ErrorMessage)
                        EditorGUILayout.HelpBox(state.ErrorMessage, MessageType.Error);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(IsSetupRuntimeBusy()))
                    {
                        if (GUILayout.Button("选择本地服务文件", GUILayout.Height(30)))
                        {
                            var selected = EditorUtility.OpenFilePanel("选择 UPilot MCP 服务文件", "",
                                UPilotServerRuntimeService.CurrentPlatformFileExtension);
                            if (!string.IsNullOrWhiteSpace(selected))
                                _ = SelectLocalSetupExeAsync(selected);
                        }
                        if (!_setupRuntimeSourceChannel && ManagedRuntimeReady(out _) &&
                            GUILayout.Button("重新安装", GUILayout.Width(88), GUILayout.Height(30)))
                            StartSetupManagedDownload(false);
                    }
                    using (new EditorGUI.DisabledScope(!state.IsRunning))
                    {
                        if (GUILayout.Button("取消安装", GUILayout.Width(88), GUILayout.Height(30)))
                        {
                            ClearSetupRuntimeContinuation();
                            _setupRuntimeCompletionSucceeded = false;
                            runtime.CancelDownload();
                        }
                    }
                }
            }
        }

        private static void DrawManagedServerLocation(UPilotServerRuntimeService runtime)
        {
            runtime.GetConfiguredStandaloneRuntime(out var path, out var version);
            if (string.IsNullOrWhiteSpace(path))
                EditorGUILayout.LabelField("安装目录", string.IsNullOrWhiteSpace(runtime.RuntimeCacheRoot)
                    ? "当前系统用户目录不可用" : runtime.RuntimeCacheRoot, EditorStyles.miniLabel);
            else
            {
                EditorGUILayout.LabelField("当前服务版本", string.IsNullOrWhiteSpace(version) ? "未知" : version, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("当前服务文件", path, EditorStyles.wordWrappedMiniLabel);
            }
        }

        private static void DrawSetupRuntimeSuccess(string message)
        {
            var previous = GUI.backgroundColor;
            try
            {
                GUI.backgroundColor = SetupReadyColor;
                EditorGUILayout.HelpBox(message, MessageType.Info);
            }
            finally { GUI.backgroundColor = previous; }
        }

        private void DrawPythonRuntimeSetup()
        {
            var runtime = UPilotServerRuntimeService.Instance;
            var state = runtime.PythonEnvironmentState;
            EditorGUILayout.HelpBox("Python 开发环境用于开发和调试 UPilot MCP 服务，需要 Python 3.11 或更高版本。", MessageType.Info);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Python 开发环境", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(IsSetupRuntimeBusy()))
                {
                    var selected = (SetupPythonEnvironmentChoice)GUILayout.Toolbar((int)_setupPythonEnvironmentChoice,
                        new[] { "自动配置环境", "使用已有环境" });
                    if (selected != _setupPythonEnvironmentChoice)
                    {
                        SelectSetupPythonEnvironment(selected == SetupPythonEnvironmentChoice.Automatic ? "" :
                            UPilotProjectConfig.Current.runtime?.pythonPath ?? "");
                        _setupPythonEnvironmentChoice = selected;
                        BeginRuntimeInspection();
                    }
                }

                if (_setupPythonEnvironmentChoice == SetupPythonEnvironmentChoice.Existing)
                {
                    using (new EditorGUI.DisabledScope(IsSetupRuntimeBusy()))
                    {
                        var path = EditorGUILayout.TextField("Python 解释器", _setupExistingPythonPath);
                        if (path != _setupExistingPythonPath)
                        {
                            SelectSetupPythonEnvironment(path);
                            _setupPythonEnvironmentChoice = SetupPythonEnvironmentChoice.Existing;
                        }
                        if (GUILayout.Button("选择 Python 解释器", GUILayout.Height(30)))
                        {
                            var selected = EditorUtility.OpenFilePanel("选择已有 Python 解释器", "",
                                Application.platform == RuntimePlatform.WindowsEditor ? "exe" : "");
                            if (!string.IsNullOrWhiteSpace(selected))
                                SelectSetupPythonEnvironment(selected);
                        }
                    }
                    EditorGUILayout.HelpBox("仅验证所选解释器的版本、依赖和 pip check；不会切换到其它解释器，也不会修改已有环境。验证失败后，可人工修复环境，再点击验证并继续。", MessageType.Info);
                    EditorGUILayout.LabelField("版本与依赖", SetupRuntimeReady(out _) ? "验证通过" : "待验证", EditorStyles.miniLabel);
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(_setupPythonEnvironmentPath))
                    {
                        EditorGUILayout.LabelField("环境目录", _setupPythonEnvironmentPath, EditorStyles.wordWrappedMiniLabel);
                        EditorGUILayout.LabelField("共享范围", _setupPythonEnvironmentScope, EditorStyles.miniLabel);
                    }
                    if (!string.IsNullOrWhiteSpace(_setupPythonEnvironmentPathError))
                        EditorGUILayout.HelpBox(_setupPythonEnvironmentPathError, MessageType.Error);
                    else if (_setupPythonProbe != null && _setupPythonProbe.InterpreterUsable)
                    {
                        EditorGUILayout.LabelField("基础解释器", _setupPythonProbe.PythonPath, EditorStyles.wordWrappedMiniLabel);
                        EditorGUILayout.LabelField("解释器版本", _setupPythonProbe.VersionText, EditorStyles.miniLabel);
                    }
                    EditorGUILayout.HelpBox("自动创建虚拟环境并安装依赖；已有自动环境先验证，验证失败时保留原文件并提示人工修复。", MessageType.Info);
                    using (new EditorGUI.DisabledScope(IsSetupRuntimeBusy()))
                    {
                        if (GUILayout.Button("重新检测", GUILayout.Height(30)))
                        {
                            ClearSetupRuntimeContinuation();
                            _setupPythonProbe = null;
                            _setupPythonValidatedPath = "";
                            _setupPythonEnvironmentPathError = "";
                            _setupRuntimeError = "";
                            BeginRuntimeInspection();
                        }
                    }
                    if (state.IsRunning || state.IsComplete || state.IsCancelled || !string.IsNullOrWhiteSpace(state.ErrorMessage))
                    {
                        EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(false, 20), GetSetupPythonProgress(state), state.Phase);
                        if (!string.IsNullOrWhiteSpace(state.ErrorMessage) && state.ErrorMessage != _setupRuntimeError)
                            EditorGUILayout.HelpBox(state.ErrorMessage, MessageType.Error);
                    }
                    using (new EditorGUI.DisabledScope(!state.IsRunning))
                    {
                        if (GUILayout.Button("取消配置", GUILayout.Height(30)))
                        {
                            ClearSetupRuntimeContinuation();
                            _setupRuntimeCompletionSucceeded = false;
                            runtime.CancelPythonEnvironmentSetup();
                        }
                    }
                }
                if (SetupRuntimeReady(out var readyMessage))
                    DrawSetupRuntimeSuccess(readyMessage);
            }
        }

        private bool IsSetupRuntimeBusy() => _setupPythonValidationRunning || _setupLocalExeValidationRunning ||
            UPilotServerRuntimeService.Instance.DownloadState.IsRunning ||
            UPilotServerRuntimeService.Instance.PythonEnvironmentState.IsRunning;

        internal bool CanContinueFromRuntimeStep(out string reason)
        {
            if (IsSetupRuntimeBusy())
            {
                reason = "请等待当前安装或验证完成。";
                return false;
            }
            if (_setupRuntimeChoice == SetupRuntimeChoice.Managed)
            {
                if (ManagedRuntimeReady(out reason)) return true;
                if (!string.IsNullOrWhiteSpace(_setupManifestError)) return true;
                return ManagedSetupDownloadAvailable(out reason);
            }
            if (_setupPythonEnvironmentChoice == SetupPythonEnvironmentChoice.Existing)
            {
                reason = "请先选择存在的 Python 解释器绝对路径，再验证环境。";
                return Path.IsPathRooted(_setupExistingPythonPath) && File.Exists(_setupExistingPythonPath);
            }
            if (_setupPythonInspectionRunning)
            {
                reason = "正在检测 Python 环境…";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(_setupPythonEnvironmentPathError))
            {
                reason = "请先解决环境目录问题，再重新检测。";
                return false;
            }
            if ((_setupPythonProbe?.InterpreterUsable ?? false) || HasAutomaticSetupPython())
            {
                reason = "";
                return true;
            }
            reason = "未找到可用的 Python 3.11+。请先安装 Python，然后点击重新检测。";
            return false;
        }

        private bool HasAutomaticSetupPython() => !string.IsNullOrWhiteSpace(_setupPythonEnvironmentPath) &&
            File.Exists(Path.Combine(_setupPythonEnvironmentPath,
                Application.platform == RuntimePlatform.WindowsEditor ? "Scripts/python.exe" : "bin/python"));

        private bool ManagedSetupDownloadAvailable(out string reason)
        {
            if (_setupRuntimeSourceChannel)
            {
                reason = "当前安装不提供自动下载，请先选择本地服务文件。";
                return false;
            }
            if (_setupManifestLoading || _setupManifest == null)
            {
                reason = "正在获取适用于当前平台的服务信息…";
                return false;
            }
            if (!UPilotServerRuntimeService.IsVersionAtLeast(UPilotServerRuntimeService.UpmVersion, _setupManifest.MinCompatibleUpm))
            {
                reason = $"请先更新 UPilot 至 {_setupManifest.MinCompatibleUpm} 或更高版本。";
                return false;
            }
            var download = UPilotServerRuntimeService.PickCurrentPlatformDownload(_setupManifest);
            if (download == null || string.IsNullOrWhiteSpace(download.Sha256))
            {
                reason = "当前版本没有适用于此平台且带校验信息的服务文件。";
                return false;
            }
            reason = "";
            return true;
        }

        internal void ContinueFromRuntimeStep(Action startDownload = null, Func<UPilotDownloadState> readDownloadState = null)
        {
            if (!CanContinueFromRuntimeStep(out _)) return;
            if (SetupRuntimeReady(out _))
            {
                ClearSetupRuntimeContinuation();
                SetSetupStep(2);
                return;
            }
            if (_setupRuntimeChoice == SetupRuntimeChoice.Python)
            {
                if (_setupPythonEnvironmentChoice == SetupPythonEnvironmentChoice.Existing)
                    _ = SelectExistingSetupPythonAsync(_setupExistingPythonPath, true);
                else
                    StartAutomaticSetupPython(true);
                return;
            }
            if (!string.IsNullOrWhiteSpace(_setupManifestError))
            {
                ClearSetupRuntimeContinuation();
                _setupManifest = null;
                _setupManifestError = "";
                BeginRuntimeInspection();
                return;
            }
            StartSetupManagedDownload(true, startDownload, readDownloadState);
        }

        private void StartSetupManagedDownload(bool continueAfter, Action startDownload = null,
            Func<UPilotDownloadState> readDownloadState = null)
        {
            var runtime = UPilotServerRuntimeService.Instance;
            var readState = readDownloadState ?? (() => runtime.DownloadState);
            if (IsSetupRuntimeBusy() || readState().IsRunning || !ManagedSetupDownloadAvailable(out _)) return;
            ClearSetupRuntimeContinuation();
            _setupRuntimeCompletionSucceeded = false;
            _setupRuntimeError = "";
            _setupRuntimeNotice = "";
            var previousStartedAt = readState().StartedAt;
            try
            {
                (startDownload ?? runtime.StartDownloadLatestServerExe)();
                var state = readState();
                if (continueAfter && state.StartedAt > 0 && state.StartedAt != previousStartedAt)
                {
                    _setupRuntimeContinuation = SetupRuntimeContinuation.ManagedDownload;
                    _setupRuntimeOperationStartedAt = state.StartedAt;
                }
            }
            catch (Exception ex)
            {
                _setupRuntimeError = ex.Message;
                Debug.LogError("[UPilot] First setup service installation failed.\nException=" + ex);
                ClearSetupRuntimeContinuation();
            }
        }

        private void StartAutomaticSetupPython(bool continueAfter)
        {
            var runtime = UPilotServerRuntimeService.Instance;
            if (IsSetupRuntimeBusy()) return;
            ClearSetupRuntimeContinuation();
            _setupRuntimeCompletionSucceeded = false;
            _setupPythonValidatedPath = "";
            _setupRuntimeError = "";
            _setupRuntimeNotice = "";
            var previousStartedAt = runtime.PythonEnvironmentState.StartedAt;
            try
            {
                runtime.StartAutoConfigurePythonEnvironment();
                var state = runtime.PythonEnvironmentState;
                if (continueAfter && state.StartedAt > 0 && state.StartedAt != previousStartedAt)
                {
                    _setupRuntimeContinuation = SetupRuntimeContinuation.PythonEnvironment;
                    _setupRuntimeOperationStartedAt = state.StartedAt;
                }
            }
            catch (Exception ex)
            {
                _setupRuntimeError = ex.Message;
                Debug.LogError("[UPilot] First setup Python environment configuration failed.\nException=" + ex);
                ClearSetupRuntimeContinuation();
            }
        }

        internal void SelectOrConfigurePythonRuntime(Action<string> selectPython = null)
        {
            if (_setupPythonEnvironmentChoice == SetupPythonEnvironmentChoice.Existing ||
                UPilotServerRuntimeService.Instance.IsPythonRuntimeConfigured(out _))
            {
                var path = string.IsNullOrWhiteSpace(_setupExistingPythonPath)
                    ? UPilotProjectConfig.Current.runtime?.pythonPath ?? "" : _setupExistingPythonPath;
                _ = SelectExistingSetupPythonAsync(path, false,
                    selectPython == null ? null : config => selectPython(config.runtime.pythonPath));
            }
            else if (CanContinueFromRuntimeStep(out _))
                StartAutomaticSetupPython(false);
        }

        internal async Task SelectExistingSetupPythonAsync(string path, bool continueAfter,
            Action<UPilotProjectConfigData> save = null, Func<string, Task> validatePython = null)
        {
            if (IsSetupRuntimeBusy()) return;
            SelectSetupPythonEnvironment(path);
            var generation = _setupRuntimeGeneration;
            _setupPythonValidationRunning = true;
            try
            {
                await UPilotServerRuntimeService.Instance.ApplyRuntimeSelectionAsync(UPilotServerRuntimeMode.Python,
                    _setupExistingPythonPath, config =>
                    {
                        if (this == null || generation != _setupRuntimeGeneration)
                            throw new OperationCanceledException("已离开当前环境验证，未保存运行方式。");
                        (save ?? (value => UPilotProjectConfig.Save(value)))(config);
                    }, validatePython);
                if (this == null || generation != _setupRuntimeGeneration) return;
                _setupPythonValidatedPath = _setupExistingPythonPath;
                _setupRuntimeNotice = "所选解释器的版本、依赖和 pip check 已通过验证。";
                if (continueAfter && _mainView == UPilotMainView.Setup && _setupStep == 1)
                {
                    if (!SetupRuntimeReady(out var reason))
                        throw new InvalidOperationException("环境验证完成，但运行配置尚未保存：" + reason);
                    CompleteSetupRuntimeOperation(true);
                }
            }
            catch (OperationCanceledException)
            {
                if (this != null && generation == _setupRuntimeGeneration)
                    _setupRuntimeNotice = "已取消环境验证，未保存运行方式。";
            }
            catch (Exception ex)
            {
                Debug.LogError("[UPilot] First setup Python validation failed.\nPath=" + path + "\nException=" + ex);
                if (this != null && generation == _setupRuntimeGeneration)
                    _setupRuntimeError = ex.Message;
            }
            finally
            {
                if (this != null)
                {
                    _setupPythonValidationRunning = false;
                    Repaint();
                }
            }
        }

        internal async Task SelectLocalSetupExeAsync(string path, Action<string, string> save = null,
            Func<string, string> computeSha256 = null)
        {
            if (IsSetupRuntimeBusy()) return;
            ClearSetupRuntimeContinuation();
            _setupRuntimeCompletionSucceeded = false;
            _setupRuntimeError = "";
            _setupRuntimeNotice = "";
            _setupLocalExeValidationRunning = true;
            var generation = _setupRuntimeGeneration;
            try
            {
                path = (path ?? "").Trim();
                if (!Path.IsPathRooted(path) || !File.Exists(path))
                    throw new FileNotFoundException("请选择存在的服务文件绝对路径。", path);
                if (_setupRuntimeSourceChannel)
                {
                    await UPilotServerRuntimeService.Instance.ApplyRuntimeSelectionAsync(UPilotServerRuntimeMode.StandaloneExe,
                        path, config =>
                        {
                            if (this == null || generation != _setupRuntimeGeneration)
                                throw new OperationCanceledException("已离开当前服务选择，未保存运行方式。");
                            if (save != null) save(config.runtime.serverExePath, config.runtime.serverVersion);
                            else UPilotProjectConfig.Save(config);
                        });
                    _setupRuntimeNotice = "已选择本地服务文件，部署身份将在启动后验证。";
                }
                else
                {
                    if (!ManagedSetupDownloadAvailable(out var reason))
                        throw new InvalidOperationException(reason);
                    var manifest = _setupManifest;
                    var expected = UPilotServerRuntimeService.PickCurrentPlatformDownload(manifest).Sha256;
                    var actual = await Task.Run(() => (computeSha256 ?? UPilotServerRuntimeService.ComputeSha256)(path));
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"本地服务文件 SHA256 与推荐版本不匹配。\nPath={path}\nExpected={expected}\nActual={actual}");
                    if (this == null || generation != _setupRuntimeGeneration) return;
                    (save ?? UPilotServerRuntimeService.Instance.SetStandaloneExeRuntime)(path, manifest.ServerVersion);
                    _setupRuntimeNotice = "本地服务文件已通过推荐版本 SHA256 校验。";
                }
                if (this != null && generation == _setupRuntimeGeneration)
                    _setupRuntimeChoice = SetupRuntimeChoice.Managed;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.LogError("[UPilot] First setup local service validation failed.\nPath=" + path + "\nException=" + ex);
                if (this != null && generation == _setupRuntimeGeneration)
                    _setupRuntimeError = ex.Message;
            }
            finally
            {
                if (this != null)
                {
                    _setupLocalExeValidationRunning = false;
                    Repaint();
                }
            }
        }

        private void RefreshSetupRuntimeContinuation() => ObserveSetupRuntimeOperation(
            UPilotServerRuntimeService.Instance.DownloadState, UPilotServerRuntimeService.Instance.PythonEnvironmentState);

        internal void ObserveSetupRuntimeOperation(UPilotDownloadState download, UPilotPythonEnvironmentState python)
        {
            if (_setupRuntimeContinuation == SetupRuntimeContinuation.None) return;
            if (_mainView != UPilotMainView.Setup || _setupStep != 1)
            {
                ClearSetupRuntimeContinuation();
                return;
            }
            var isPython = _setupRuntimeContinuation == SetupRuntimeContinuation.PythonEnvironment;
            if (isPython != (_setupRuntimeChoice == SetupRuntimeChoice.Python) ||
                (isPython ? python?.StartedAt : download?.StartedAt) != _setupRuntimeOperationStartedAt)
            {
                ClearSetupRuntimeContinuation();
                return;
            }
            if (isPython ? python.IsRunning : download.IsRunning) return;
            var error = isPython ? python.ErrorMessage : download.ErrorMessage;
            var cancelled = isPython ? python.IsCancelled : download.IsCancelled;
            var complete = isPython ? python.IsComplete : download.IsComplete;
            if (cancelled || !complete || !string.IsNullOrWhiteSpace(error))
            {
                _setupRuntimeCompletionSucceeded = false;
                _setupRuntimeError = error ?? "";
                _setupRuntimeNotice = cancelled ? "操作已取消，可以重试。" : "";
                if (!cancelled && !complete && string.IsNullOrWhiteSpace(error))
                {
                    _setupRuntimeError = "操作已结束，但安装或配置结果未确认，请重试。";
                    Debug.LogError("[UPilot] First setup runtime outcome is unconfirmed.\nStartedAt=" +
                                   _setupRuntimeOperationStartedAt + "\nOperation=" + _setupRuntimeContinuation);
                }
                ClearSetupRuntimeContinuation();
                return;
            }
            if (isPython)
                _setupPythonValidatedPath = python.InterpreterPath;
            if (!SetupRuntimeReady(out var reason))
            {
                _setupRuntimeCompletionSucceeded = false;
                _setupRuntimeError = "操作已结束，但运行配置尚未验证：" + reason;
                Debug.LogError("[UPilot] First setup runtime completion could not be verified.\n" + _setupRuntimeError);
                ClearSetupRuntimeContinuation();
                return;
            }
            CompleteSetupRuntimeOperation(isPython);
        }

        private void CompleteSetupRuntimeOperation(bool isPython)
        {
            ClearSetupRuntimeContinuation();
            _setupRuntimeCompletionSucceeded = true;
            _setupRuntimeError = "";
            _setupCompletionMessage = isPython ? "Python 开发环境配置完成" : "服务安装完成";
            _setupCompletionMessageType = MessageType.Info;
            SetSetupStep(2);
        }

        private bool SetupRuntimeReady(out string reason)
        {
            if (_setupRuntimeChoice != SetupRuntimeChoice.Python)
                return ManagedRuntimeReady(out reason);
            var config = UPilotProjectConfig.Current.runtime;
            var selectedPath = _setupPythonEnvironmentChoice == SetupPythonEnvironmentChoice.Existing
                ? _setupExistingPythonPath : _setupPythonValidatedPath;
            var ready = !string.IsNullOrWhiteSpace(_setupPythonValidatedPath) &&
                        UPilotDeploymentDiagnostics.SamePath(_setupPythonValidatedPath, selectedPath) &&
                        UPilotDeploymentDiagnostics.SamePath(_setupPythonValidatedPath, config?.pythonPath) &&
                        UPilotServerRuntimeService.Instance.GetConfiguredMode() == UPilotServerRuntimeMode.Python &&
                        File.Exists(_setupPythonValidatedPath);
            reason = ready ? "Python 环境已验证并保存。" : "Python 环境尚未完成验证和保存。";
            return ready;
        }

        private bool ManagedRuntimeReady(out string reason)
        {
            var runtime = UPilotServerRuntimeService.Instance;
            var configured = runtime.GetConfiguredMode() == UPilotServerRuntimeMode.StandaloneExe &&
                             runtime.IsStandaloneExeConfigured(out _);
            if (_setupRuntimeSourceChannel)
            {
                reason = configured ? "已配置本地服务文件，部署身份将在启动后验证。" : "请先选择本地服务文件。";
                return configured;
            }
            if (_setupManifest == null)
            {
                reason = "尚未获取服务信息。";
                return false;
            }
            if (!UPilotServerRuntimeService.IsVersionAtLeast(UPilotServerRuntimeService.UpmVersion, _setupManifest.MinCompatibleUpm))
            {
                reason = $"请先更新 UPilot 至 {_setupManifest.MinCompatibleUpm} 或更高版本。";
                return false;
            }
            var version = UPilotProjectConfig.Current.runtime?.serverVersion ?? "";
            if (!configured || string.IsNullOrWhiteSpace(version) ||
                UPilotServerRuntimeService.CompareVersions(version, _setupManifest.ServerVersion) != 0)
            {
                reason = "尚未安装与当前 UPilot 版本匹配的服务。";
                return false;
            }
            reason = $"已配置匹配的服务版本 {_setupManifest.ServerVersion}。";
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
