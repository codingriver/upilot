// -----------------------------------------------------------------------
// UPilot Editor - user-facing update center.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace CodingRiver.UPilot
{
    public sealed class UPilotUpdateWindow : EditorWindow
    {
        private UPilotReleaseManifest _manifest;
        private Action<string, MessageType> _externalNotice;
        private bool _isChecking;
        private bool _operationRunning;
        private bool _pendingUpdateAfterPlayModeExit;
        private bool _pendingUpdatePackage;
        private bool _pendingUpdateManagedServer;
        private bool _showDetails;
        private bool _sourceChannel;
        private UPilotUpdateOperationPhase _lastObservedOperationPhase = UPilotUpdateOperationPhase.None;
        private string _error = "";
        private string _notice = "";
        private MessageType _noticeType = MessageType.Info;
        private static bool _packageRevisionReflectionErrorLogged;

        public static void Open(Action<string, MessageType> notice = null)
        {
            try
            {
                var window = GetWindow<UPilotUpdateWindow>(true, "UPilot 更新中心", true);
                window.minSize = new Vector2(440, 360);
                window._externalNotice = notice;
                window.Show();
                window.Focus();
                if (window.HasActiveUpdate())
                {
                    window.ShowActiveUpdate();
                }
                else if (UPilotUpdateService.Instance.GetOperationStatus().Phase ==
                         UPilotUpdateOperationPhase.Completed)
                {
                    window.ShowCompletedUpdate();
                }
                else
                {
                    window.CheckForUpdates();
                }
            }
            catch (Exception ex)
            {
                ReportOpenError("打开 UPilot 更新中心失败", ex, notice);
            }
        }

        internal static bool OpenActiveUpdate(Action<string, MessageType> notice = null)
        {
            try
            {
                var window = GetWindow<UPilotUpdateWindow>(true, "UPilot 更新中心", true);
                window.minSize = new Vector2(440, 360);
                window._externalNotice = notice;
                window.Show();
                window.Focus();
                window.ShowActiveUpdate();
                return true;
            }
            catch (Exception ex)
            {
                ReportOpenError("打开 UPilot 更新进度失败", ex, notice);
                return false;
            }
        }

        private static void ReportOpenError(string message, Exception ex, Action<string, MessageType> notice)
        {
            var fullMessage = message + "：" + ex.Message;
            ReportUpdateWindowException(fullMessage, ex);
            try
            {
                notice?.Invoke(fullMessage, MessageType.Error);
            }
            catch (Exception noticeEx)
            {
                Debug.LogError("[UPilot] 更新错误通知失败：" + noticeEx);
            }
        }

        private static void ReportUpdateWindowException(string context, Exception ex)
        {
            Debug.LogError("[UPilot] " + context + "\n" + ex);
        }

        private static void DrawExceptionFallback(string context, Exception ex)
        {
            try
            {
                EditorGUILayout.HelpBox(context + "：" + ex.Message, MessageType.Error);
            }
            catch (Exception fallbackEx)
            {
                Debug.LogError("[UPilot] 更新中心错误提示绘制失败：" + fallbackEx.Message + "\n" + fallbackEx);
            }
        }

        private void ShowActiveUpdate()
        {
            _isChecking = false;
            _error = "";
            _operationRunning = true;
            Repaint();
        }

        private void ShowCompletedUpdate()
        {
            _isChecking = false;
            _error = "";
            _operationRunning = false;
            Repaint();
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            try
            {
                if (_pendingUpdateAfterPlayModeExit)
                {
                    TryStartPendingUpdateAfterPlayModeExit();
                    Repaint();
                    return;
                }

                var status = UPilotUpdateService.Instance.GetOperationStatus();
                var downloadRunning = UPilotServerRuntimeService.Instance.DownloadState.IsRunning;
                var previousPhase = _lastObservedOperationPhase;
                var operationWasRunning = _operationRunning;
                _lastObservedOperationPhase = status.Phase;
                if (_operationRunning && !status.IsRunning && !downloadRunning)
                    _operationRunning = false;

                if (status.Phase == UPilotUpdateOperationPhase.Completed &&
                    previousPhase != UPilotUpdateOperationPhase.Completed)
                {
                    _isChecking = false;
                    _error = "";
                    _notice = string.IsNullOrWhiteSpace(status.Message) ? "更新成功" : status.Message;
                    _noticeType = MessageType.Info;
                }

                if (ShouldRefreshForOperationStatus(
                        previousPhase,
                        status.Phase,
                        operationWasRunning,
                        _operationRunning,
                        status.IsRunning,
                        downloadRunning))
                    Repaint();
            }
            catch (Exception ex)
            {
                EditorApplication.update -= OnEditorUpdate;
                ReportUpdateWindowException("UPilot 更新中心刷新回调失败：" + ex.Message, ex);
            }
        }

        private void OnGUI()
        {
            try
            {
                DrawUpdateWindowGui();
            }
            catch (Exception ex)
            {
                ReportUpdateWindowException("UPilot 更新中心绘制失败：" + ex.Message, ex);
                DrawExceptionFallback("UPilot 更新中心绘制失败", ex);
            }
        }

        private void DrawUpdateWindowGui()
        {
            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(10);
                EditorGUILayout.LabelField("UPilot 更新中心", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_isChecking || IsUpdateBusy()))
                {
                    if (GUILayout.Button("重新检查", EditorStyles.miniButton, GUILayout.Width(72)))
                        RecheckForUpdates();
                }
                GUILayout.Space(10);
            }

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(new GUIStyle { padding = new RectOffset(12, 12, 0, 8) }))
            {
                if (HasActiveUpdate())
                {
                    _isChecking = false;
                    _error = "";
                    DrawActiveUpdateContent();
                    DrawFooter();
                    return;
                }

                var operationStatus = UPilotUpdateService.Instance.GetOperationStatus();
                if (operationStatus.Phase == UPilotUpdateOperationPhase.Completed)
                {
                    _isChecking = false;
                    _error = "";
                    DrawCompletedUpdateContent(operationStatus);
                    DrawFooter();
                    return;
                }

                if (_isChecking)
                {
                    EditorGUILayout.HelpBox("正在检查更新…", MessageType.Info);
                    return;
                }

                if (!string.IsNullOrEmpty(_error))
                {
                    EditorGUILayout.HelpBox("检查更新失败：" + _error, MessageType.Error);
                    DrawFooter();
                    return;
                }

                if (_sourceChannel)
                {
                    DrawSourceUpdateContent();
                    DrawFooter();
                    return;
                }

                if (_manifest == null)
                {
                    EditorGUILayout.HelpBox("尚未获取更新信息。", MessageType.Info);
                    DrawFooter();
                    return;
                }

                DrawUpdateContent();
                DrawFooter();
            }
        }

        private void DrawActiveUpdateContent()
        {
            var status = UPilotUpdateService.Instance.GetOperationStatus();
            var download = UPilotServerRuntimeService.Instance.DownloadState;
            var label = download.IsRunning
                ? UPilotUpdateService.FormatDownloadProgressLabel(download)
                : string.IsNullOrWhiteSpace(status.Label) ? "正在准备更新" : status.Label;
            var detail = download.IsRunning
                ? UPilotUpdateService.FormatDownloadProgressDetail(download)
                : status.Message;
            var progress = download.IsRunning && download.TotalBytes > 0
                ? download.Progress
                : UPilotUpdateService.EstimateOperationProgress(status.Phase);

            DrawInfoRow("更新状态", label);
            EditorGUILayout.Space(4);
            var rect = EditorGUILayout.GetControlRect(false, 18f);
            EditorGUI.ProgressBar(rect, progress, label);
            if (!string.IsNullOrWhiteSpace(detail))
                EditorGUILayout.LabelField(detail, EditorStyles.miniLabel);
            if (!string.IsNullOrWhiteSpace(download.WarningMessage))
                EditorGUILayout.HelpBox(download.WarningMessage, MessageType.Warning);

            var operationStatus = UPilotUpdateService.Instance.GetOperationStatus();
            if (!string.IsNullOrWhiteSpace(operationStatus.TargetUpmVersion) ||
                !string.IsNullOrWhiteSpace(operationStatus.TargetServerVersion))
            {
                EditorGUILayout.Space(6);
                if (!string.IsNullOrWhiteSpace(operationStatus.TargetUpmVersion))
                    DrawInfoRow("目标包", operationStatus.TargetUpmVersion);
                if (!string.IsNullOrWhiteSpace(operationStatus.TargetServerVersion))
                    DrawInfoRow("目标服务", operationStatus.TargetServerVersion);
            }

            DrawReloadGuardNotice(operationStatus);

            if (!string.IsNullOrWhiteSpace(_notice))
                EditorGUILayout.HelpBox(_notice, _noticeType);
        }

        private void DrawCompletedUpdateContent(UPilotUpdateOperationStatus status)
        {
            EditorGUILayout.Space(16);
            var successStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 24,
            };
            successStyle.normal.textColor = EditorGUIUtility.isProSkin
                ? new Color(0.38f, 0.9f, 0.46f)
                : new Color(0.08f, 0.5f, 0.16f);
            EditorGUILayout.LabelField("更新成功", successStyle, GUILayout.Height(36f));

            var messageStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                alignment = TextAnchor.MiddleCenter,
            };
            var message = string.IsNullOrWhiteSpace(status.Message)
                ? "UPilot 和 MCP 服务已完成更新。"
                : status.Message;
            EditorGUILayout.LabelField(message, messageStyle, GUILayout.MinHeight(32f));

            EditorGUILayout.Space(8);
            var progressRect = EditorGUILayout.GetControlRect(false, 18f);
            EditorGUI.ProgressBar(progressRect, 1f, "更新成功");

            EditorGUILayout.Space(10);
            var mode = UPilotServerRuntimeService.Instance.GetConfiguredMode();
            var currentUpm = UPilotServerRuntimeService.UpmVersion;
            var currentServer = GetCurrentServerVersion(mode);
            DrawInfoRow("当前包", currentUpm);
            DrawInfoRow(
                "当前服务",
                string.IsNullOrWhiteSpace(currentServer) ? status.TargetServerVersion : currentServer);

            if (_noticeType == MessageType.Warning || _noticeType == MessageType.Error)
                EditorGUILayout.HelpBox(_notice, _noticeType);
        }

        private void DrawSourceUpdateContent()
        {
            var runtime = UPilotServerRuntimeService.Instance;
            var package = PackageInfo.FindForAssembly(typeof(UPilotBridge).Assembly);
            var currentUpm = UPilotServerRuntimeService.UpmVersion;
            var pythonConfigured = runtime.IsPythonRuntimeConfigured(out var pythonPath);

            DrawInfoRow("更新通道", "开发版（本机 Python）");
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "当前安装来源是本地、嵌入或 main/source。开发版不下载自动管理 MCP 服务，也不执行远程版本比较；正式对外版本通过 v* tag 发布。",
                MessageType.Info);

            DrawSectionTitle("UPilot 包");
            DrawInfoRow("当前版本", currentUpm + BuildSourcePackageSuffix(package));
            DrawStatusAction("更新状态", "开发版不自动检查", null, null, false);

            DrawSeparator();
            DrawSectionTitle("MCP 服务");
            DrawInfoRow("运行方式", "本机 Python");
            DrawInfoRow("Python", pythonConfigured ? pythonPath : "尚未配置");
            DrawStatusAction(
                "状态",
                pythonConfigured ? "已按开发模式配置" : "请在首次设置中配置 Python 3.11+",
                null,
                null,
                false);

            if (!string.IsNullOrWhiteSpace(_notice))
                EditorGUILayout.HelpBox(_notice, _noticeType);
        }

        private void DrawUpdateContent()
        {
            var runtime = UPilotServerRuntimeService.Instance;
            var operationStatus = UPilotUpdateService.Instance.GetOperationStatus();
            var mode = runtime.GetConfiguredMode();
            var package = PackageInfo.FindForAssembly(typeof(UPilotBridge).Assembly);
            var currentUpm = UPilotServerRuntimeService.UpmVersion;
            var managedRuntime = mode == UPilotServerRuntimeMode.StandaloneExe;
            var currentServer = managedRuntime ? GetCurrentServerVersion(mode) : currentUpm;
            var mainChannel = false;
            var localPackage = package != null &&
                               (package.source == PackageSource.Local || package.source == PackageSource.Embedded);
            var upmNeedsUpdate = NeedsPackageUpdate(package, currentUpm, mainChannel, localPackage);
            var serverVersionUnknown = managedRuntime && string.IsNullOrWhiteSpace(currentServer);
            var serverNeedsUpdate = managedRuntime && !string.IsNullOrWhiteSpace(currentServer) &&
                                    UPilotServerRuntimeService.IsVersionNewer(_manifest.ServerVersion, currentServer);
            var serverBlockedByPackage = managedRuntime && (serverNeedsUpdate || serverVersionUnknown) &&
                                         !UPilotServerRuntimeService.IsVersionAtLeast(
                                             currentUpm,
                                             _manifest.MinCompatibleUpm);
            var managedServerNeedsAction = managedRuntime && (serverNeedsUpdate || serverVersionUnknown);
            var includeManagedServerInPrimaryAction = managedServerNeedsAction &&
                                                      (!serverBlockedByPackage || upmNeedsUpdate);
            var primaryActionAvailable = upmNeedsUpdate || includeManagedServerInPrimaryAction;
            var primaryActionBlocked = managedServerNeedsAction && serverBlockedByPackage && !upmNeedsUpdate;
            var updateBusy = IsUpdateBusy() || operationStatus.IsRunning;

            DrawInfoRow("更新通道", "正式版");
            EditorGUILayout.Space(4);

            var hasAction = primaryActionAvailable || primaryActionBlocked;
            if (localPackage && !managedRuntime)
            {
                EditorGUILayout.HelpBox("当前使用本地开发包，未执行远程版本比较", MessageType.Info);
            }
            else
            {
                var summary = hasAction
                    ? BuildSummary(
                        upmNeedsUpdate,
                        managedServerNeedsAction,
                        serverBlockedByPackage,
                        serverVersionUnknown)
                    : localPackage
                        ? "当前使用本地开发包，MCP 服务已是最新"
                        : "已是最新版本";
                EditorGUILayout.HelpBox(summary, hasAction ? MessageType.Warning : MessageType.Info);
            }

            DrawSectionTitle("UPilot 包");
            if (localPackage)
            {
                DrawInfoRow("当前版本", currentUpm + "（本地开发）");
                DrawStatusAction("更新状态", "本地开发包，不自动检查", null, null, false);
            }
            else
            {
                DrawInfoRow("当前版本", BuildPackageVersion(currentUpm, package, mainChannel));
                DrawInfoRow("最新版本", BuildLatestPackageVersion(mainChannel));
                if (upmNeedsUpdate)
                {
                    DrawStatusAction(
                        "状态",
                        "有新版本可用",
                        null,
                        null,
                        false);
                }
                else
                {
                    DrawStatusAction("状态", "已是最新", null, null, false);
                }
            }

            DrawSeparator();
            DrawSectionTitle("MCP 服务");
            DrawInfoRow("运行方式", managedRuntime ? "自动管理（推荐）" : "本机 Python");

            if (!managedRuntime)
            {
                DrawInfoRow("服务来源", "当前 UPilot 包");
                DrawInfoRow("当前版本", currentUpm);
                DrawInfoRow("更新方式", "随 UPilot 包更新");
                DrawStatusAction("状态", "无需单独更新", null, null, false);
            }
            else
            {
                DrawInfoRow("当前版本", string.IsNullOrWhiteSpace(currentServer) ? "暂未获取" : currentServer);
                DrawInfoRow("最新版本", _manifest.ServerVersion);

                if (serverBlockedByPackage)
                {
                    DrawStatusAction("状态", "请先更新 UPilot 包", null, null, false);
                }
                else if (serverNeedsUpdate)
                {
                    DrawStatusAction("状态", "有新版本可用", null, null, false);
                }
                else if (serverVersionUnknown)
                {
                    DrawStatusAction("状态", "暂无法确认当前版本", null, null, false);
                }
                else
                {
                    DrawStatusAction("状态", "已是最新", null, null, false);
                }
            }

            DrawPrimaryUpdateAction(
                upmNeedsUpdate,
                includeManagedServerInPrimaryAction,
                primaryActionBlocked,
                mainChannel,
                updateBusy);
            DrawOperationStatus(operationStatus);
            DrawDownloadProgress();
            DrawReloadGuardNotice(operationStatus);

            if (!string.IsNullOrWhiteSpace(_notice))
                EditorGUILayout.HelpBox(_notice, _noticeType);

            EditorGUILayout.Space(3);
            _showDetails = EditorGUILayout.Foldout(_showDetails, "详细信息", true);
            if (_showDetails)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawInfoRow("最低包版本", EmptyAsDash(_manifest.MinCompatibleUpm));
                    DrawInfoRow("最低服务版本", EmptyAsDash(_manifest.MinCompatibleServer));
                    DrawInfoRow("协议版本", EmptyAsDash(_manifest.ProtocolVersion));
                    DrawInfoRow("构建提交", ShortCommit(_manifest.CommitSha));
                    DrawCopyableAddressRow();
                }
            }
        }

        private void DrawDownloadProgress()
        {
            var state = UPilotServerRuntimeService.Instance.DownloadState;
            if (!state.IsRunning)
                return;

            EditorGUILayout.Space(4);
            var rect = EditorGUILayout.GetControlRect(false, 18);
            EditorGUI.ProgressBar(rect, state.Progress, UPilotUpdateService.FormatDownloadProgressLabel(state));
            EditorGUILayout.LabelField(UPilotUpdateService.FormatDownloadProgressDetail(state), EditorStyles.miniLabel);
            if (!string.IsNullOrWhiteSpace(state.WarningMessage))
                EditorGUILayout.HelpBox(state.WarningMessage, MessageType.Warning);
        }

        private void DrawFooter()
        {
            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("关闭", GUILayout.Width(72)))
                    Close();
            }
        }

        private async void CheckForUpdates()
        {
            if (_isChecking)
                return;
            if (HasActiveUpdate())
            {
                _isChecking = false;
                _error = "";
                _operationRunning = true;
                Repaint();
                return;
            }

            _isChecking = true;
            _error = "";
            _notice = "";
            _sourceChannel = false;
            Repaint();
            try
            {
                var channel = UPilotServerRuntimeService.ResolveUpdateChannel();
                if (UPilotServerRuntimeService.IsSourceChannel(channel))
                {
                    Debug.Log($"[UPilot] CheckForUpdates channel={channel}, source mode uses local Python.");
                    _manifest = null;
                    _sourceChannel = true;
                    return;
                }

                var manifestUrl = UPilotServerRuntimeService.ResolveManifestUrl();
                Debug.Log($"[UPilot] CheckForUpdates channel={channel}, manifestUrl={manifestUrl}");
                _manifest = await UPilotServerRuntimeService.Instance.FetchReleaseManifestAsync(manifestUrl);
            }
            catch (Exception ex)
            {
                _manifest = null;
                _error = ex.Message;
                ReportUpdateWindowException("检查 UPilot 更新失败：" + ex.Message, ex);
            }
            finally
            {
                _isChecking = false;
                Repaint();
            }
        }

        private void RecheckForUpdates()
        {
            if (UPilotUpdateService.Instance.GetOperationStatus().Phase ==
                UPilotUpdateOperationPhase.Completed)
            {
                UPilotUpdateService.ClearOperationStatus();
                _lastObservedOperationPhase = UPilotUpdateOperationPhase.None;
            }

            CheckForUpdates();
        }

        internal static bool ShouldRefreshForOperationStatus(
            UPilotUpdateOperationPhase previousPhase,
            UPilotUpdateOperationPhase currentPhase,
            bool operationWasRunning,
            bool operationRunning,
            bool statusIsRunning,
            bool downloadRunning)
        {
            return previousPhase != currentPhase ||
                   operationWasRunning != operationRunning ||
                   operationRunning ||
                   statusIsRunning ||
                   downloadRunning;
        }

        private void UpdatePackage()
        {
            if (!PrepareUpdateInEditMode(updatePackage: true, updateManagedServer: false))
                return;

            try
            {
                _operationRunning = true;
                UPilotUpdateService.Instance.UpdateUpmFromManifest(HandleOperationNotice);
            }
            catch (Exception ex)
            {
                _operationRunning = false;
                _notice = "UPilot 包更新启动失败：" + ex.Message;
                _noticeType = MessageType.Error;
                ReportUpdateWindowException(_notice, ex);
            }
        }

        private void UpdateManagedService()
        {
            if (!PrepareUpdateInEditMode(updatePackage: false, updateManagedServer: true))
                return;

            try
            {
                _operationRunning = true;
                UPilotUpdateService.Instance.UpdateManagedServerAndRestart(HandleOperationNotice);
            }
            catch (Exception ex)
            {
                _operationRunning = false;
                _notice = "MCP 服务更新启动失败：" + ex.Message;
                _noticeType = MessageType.Error;
                ReportUpdateWindowException(_notice, ex);
            }
        }

        private void UpdateSelected(bool updatePackage, bool updateManagedServer)
        {
            if (!PrepareUpdateInEditMode(updatePackage, updateManagedServer))
                return;

            StartUpdateSelected(updatePackage, updateManagedServer);
        }

        private void StartUpdateSelected(bool updatePackage, bool updateManagedServer)
        {
            try
            {
                _operationRunning = true;
                UPilotUpdateService.Instance.UpdateFromManifest(
                    updatePackage,
                    updateManagedServer,
                    HandleOperationNotice);
            }
            catch (Exception ex)
            {
                _operationRunning = false;
                _notice = "UPilot 更新启动失败：" + ex.Message;
                _noticeType = MessageType.Error;
                ReportUpdateWindowException(_notice, ex);
            }
        }

        private bool PrepareUpdateInEditMode(bool updatePackage, bool updateManagedServer)
        {
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
                return true;

            var confirmed = EditorUtility.DisplayDialog(
                "退出 Play Mode 后更新？",
                "Unity 当前正在 Play Mode 或正在切换 Play Mode。UPilot 更新需要在 Edit Mode 下执行，将先退出 Play Mode，退出完成后自动开始更新。",
                "退出并更新",
                "取消");
            if (!confirmed)
            {
                _notice = "已取消更新：Unity 仍在 Play Mode。";
                _noticeType = MessageType.Info;
                return false;
            }

            _pendingUpdateAfterPlayModeExit = true;
            _pendingUpdatePackage = updatePackage;
            _pendingUpdateManagedServer = updateManagedServer;
            _operationRunning = true;
            _notice = "正在退出 Unity Play Mode，退出后开始更新…";
            _noticeType = MessageType.Warning;
            Debug.LogWarning("[UPilot] Unity is in Play Mode or changing Play Mode. Exiting Play Mode before starting update.");
            EditorApplication.isPlaying = false;
            Repaint();
            return false;
        }

        private void TryStartPendingUpdateAfterPlayModeExit()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            var updatePackage = _pendingUpdatePackage;
            var updateManagedServer = _pendingUpdateManagedServer;
            _pendingUpdateAfterPlayModeExit = false;
            _pendingUpdatePackage = false;
            _pendingUpdateManagedServer = false;
            Debug.Log("[UPilot] Unity returned to Edit Mode. Starting pending update.");
            StartUpdateSelected(updatePackage, updateManagedServer);
        }

        private void HandleOperationNotice(string message, MessageType type)
        {
            _notice = message ?? "";
            _noticeType = type;
            try
            {
                _externalNotice?.Invoke(message, type);
            }
            catch (Exception ex)
            {
                ReportUpdateWindowException("更新中心外部通知失败：" + ex.Message, ex);
            }

            var status = UPilotUpdateService.Instance.GetOperationStatus();
            if (type == MessageType.Error ||
                (type == MessageType.Warning && !status.IsRunning) ||
                (!string.IsNullOrWhiteSpace(message) &&
                 !status.IsRunning &&
                 (message.IndexOf("已更新", StringComparison.Ordinal) >= 0 ||
                  message.IndexOf("已取消", StringComparison.Ordinal) >= 0)))
            {
                _operationRunning = false;
                if (type != MessageType.Error && message.IndexOf("已取消", StringComparison.Ordinal) < 0)
                    _ = RefreshAfterOperationAsync();
            }

            Repaint();
        }

        private async Task RefreshAfterOperationAsync()
        {
            await Task.Delay(10000);
            CheckForUpdates();
        }

        private bool NeedsPackageUpdate(
            PackageInfo package,
            string currentVersion,
            bool mainChannel,
            bool localPackage)
        {
            if (localPackage)
                return false;
            if (!mainChannel)
                return UPilotServerRuntimeService.IsVersionNewer(_manifest.UpmVersion, currentVersion);

            var currentRevision = GetPackageRevision(package);
            if (string.IsNullOrWhiteSpace(currentRevision) || string.IsNullOrWhiteSpace(_manifest.CommitSha))
                return true;
            return !RevisionsMatch(currentRevision, _manifest.CommitSha);
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

        private string BuildPackageVersion(string version, PackageInfo package, bool mainChannel)
        {
            return version;
        }

        private static string BuildSourcePackageSuffix(PackageInfo package)
        {
            if (package == null)
                return "（source）";
            if (package.source == PackageSource.Local)
                return "（本地开发）";
            if (package.source == PackageSource.Embedded)
                return "（嵌入开发）";
            var revision = GetPackageRevision(package);
            return string.IsNullOrWhiteSpace(revision) ? "（source）" : " · " + ShortCommit(revision);
        }

        private string BuildLatestPackageVersion(bool mainChannel)
        {
            return _manifest.UpmVersion;
        }

        private static string BuildSummary(
            bool packageUpdate,
            bool managedServerAction,
            bool serverBlocked,
            bool serverVersionUnknown)
        {
            if (packageUpdate && managedServerAction)
                return "检查完成，UPilot 包和 MCP 服务都有新版本可更新";
            if (serverBlocked)
                return "有新版本可用，请先更新 UPilot 包";
            if (packageUpdate)
                return "有新版本需要更新";
            if (serverVersionUnknown)
                return "暂无法确认 MCP 服务版本，可安装最新服务";
            return "MCP 服务有新版本可用";
        }

        private void DrawPrimaryUpdateAction(
            bool updatePackage,
            bool updateManagedServer,
            bool blocked,
            bool mainChannel,
            bool busy)
        {
            if (!updatePackage && !updateManagedServer && !blocked)
                return;

            EditorGUILayout.Space(10);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                var label = BuildPrimaryUpdateLabel(updatePackage, updateManagedServer, mainChannel);
                var enabled = !busy && !blocked;
                var previous = GUI.backgroundColor;
                try
                {
                    GUI.backgroundColor = enabled
                        ? new Color(0.32f, 0.78f, 0.36f)
                        : new Color(0.45f, 0.45f, 0.45f);
                    using (new EditorGUI.DisabledScope(!enabled))
                    {
                        if (GUILayout.Button(label, GUILayout.Width(178), GUILayout.Height(48)))
                            UpdateSelected(updatePackage, updateManagedServer);
                    }
                }
                finally
                {
                    GUI.backgroundColor = previous;
                }
            }

            if (blocked)
                EditorGUILayout.HelpBox("当前 UPilot 包版本过低，请先更新 UPilot 包后再更新 MCP 服务。", MessageType.Warning);
        }

        private static string BuildPrimaryUpdateLabel(bool updatePackage, bool updateManagedServer, bool mainChannel)
        {
            if (updatePackage || updateManagedServer)
                return "更新";
            return "已是最新";
        }

        private static void DrawOperationStatus(UPilotUpdateOperationStatus status)
        {
            if (!status.IsRunning && status.Phase != UPilotUpdateOperationPhase.Failed)
                return;

            var message = string.IsNullOrWhiteSpace(status.Message)
                ? "请等待更新完成…"
                : status.Message;
            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(
                message,
                status.Phase == UPilotUpdateOperationPhase.Failed ? MessageType.Error : MessageType.Info);
        }

        private void DrawReloadGuardNotice(UPilotUpdateOperationStatus status)
        {
            var guardStatus = UPilotUpdateService.GetReloadGuardStatus();
            var message = UPilotUpdateService.GetReloadGuardNotice(guardStatus, compact: false);
            if (!string.IsNullOrWhiteSpace(message))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(
                    message,
                    guardStatus.HasFailure ? MessageType.Error : MessageType.Warning);
            }

            if (!UPilotUpdateService.ShouldShowRepairUpdateStateButton(status, guardStatus))
                return;

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("修复更新状态", GUILayout.Width(128f), GUILayout.Height(26f)))
                    RepairUpdateState();
            }
        }

        private void RepairUpdateState()
        {
            try
            {
                _operationRunning = false;
                _pendingUpdateAfterPlayModeExit = false;
                _pendingUpdatePackage = false;
                _pendingUpdateManagedServer = false;
                var message = UPilotUpdateService.Instance.RepairUpdateState(HandleOperationNotice);
                _notice = message;
                _noticeType = MessageType.Info;
                Repaint();
            }
            catch (Exception ex)
            {
                _notice = "修复更新状态失败：" + ex.Message;
                _noticeType = MessageType.Error;
                ReportUpdateWindowException(_notice, ex);
            }
        }

        private bool IsUpdateBusy()
        {
            return _pendingUpdateAfterPlayModeExit || _operationRunning || HasActiveUpdate();
        }

        private bool HasActiveUpdate()
        {
            return UPilotUpdateService.Instance.GetOperationStatus().IsRunning ||
                   UPilotServerRuntimeService.Instance.DownloadState.IsRunning;
        }

        private void DrawCopyableAddressRow()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("更新地址", EditorStyles.miniLabel, GUILayout.Width(86));
                EditorGUILayout.LabelField("查看更新清单", EditorStyles.label);
                if (GUILayout.Button("复制地址", GUILayout.Width(88)))
                {
                    EditorGUIUtility.systemCopyBuffer = UPilotServerRuntimeService.ResolveManifestUrl();
                    _notice = "更新地址已复制";
                    _noticeType = MessageType.Info;
                }
            }
        }

        private static void DrawSectionTitle(string title)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        private static void DrawInfoRow(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(86));
                EditorGUILayout.SelectableLabel(value ?? "", EditorStyles.label, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
        }

        private static void DrawStatusAction(
            string label,
            string value,
            string buttonLabel,
            Action action,
            bool enabled)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(86));
                EditorGUILayout.LabelField(value, EditorStyles.label);
                if (!string.IsNullOrWhiteSpace(buttonLabel))
                {
                    using (new EditorGUI.DisabledScope(!enabled))
                    {
                        var buttonWidth = buttonLabel.Length > 5 ? 112f : 88f;
                        if (GUILayout.Button(buttonLabel, GUILayout.Width(buttonWidth)))
                            action?.Invoke();
                    }
                }
            }
        }

        private static void DrawSeparator()
        {
            EditorGUILayout.Space(6);
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(
                rect,
                EditorGUIUtility.isProSkin
                    ? new Color(1f, 1f, 1f, 0.1f)
                    : new Color(0f, 0f, 0f, 0.14f));
        }

        private static string GetPackageRevision(PackageInfo package)
        {
            if (package == null)
                return "";
            try
            {
                var git = package.GetType().GetProperty("git", BindingFlags.Instance | BindingFlags.Public)?.GetValue(package);
                return git?.GetType().GetProperty("hash", BindingFlags.Instance | BindingFlags.Public)?.GetValue(git) as string ?? "";
            }
            catch (Exception ex)
            {
                if (!_packageRevisionReflectionErrorLogged)
                {
                    _packageRevisionReflectionErrorLogged = true;
                    ReportUpdateWindowException("读取 UPilot 包 Git revision 失败：" + ex.Message, ex);
                }
                return "";
            }
        }

        private static bool RevisionsMatch(string left, string right)
        {
            return left.StartsWith(right, StringComparison.OrdinalIgnoreCase) ||
                   right.StartsWith(left, StringComparison.OrdinalIgnoreCase);
        }

        private static string ShortCommit(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "-";
            return value.Length <= 7 ? value : value.Substring(0, 7);
        }

        private static string EmptyAsDash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }
    }
}
