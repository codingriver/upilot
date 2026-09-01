// -----------------------------------------------------------------------
// UPilot Editor - simple user-facing entry window.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public sealed partial class UPilotMainWindow : EditorWindow
    {
        private BridgeStatus _bridgeStatus;
        private McpServerStatus _mcpStatus;
        private AgentMcpConfigStatus[] _agentConfigs = Array.Empty<AgentMcpConfigStatus>();
        private AgentRuleConfigStatus[] _ruleConfigs = Array.Empty<AgentRuleConfigStatus>();
        private AgentSkillConfigStatus[] _skillConfigs = Array.Empty<AgentSkillConfigStatus>();
        private UPilotMainSnapshot _snapshot;
        private UPilotMainState _lastState;
        private double _stateChangedAt;
        private double _lastAgentRefresh;
        private double _lastRepaint;

        private bool _agentAdviceNoticeShown;

        private string _notice = "";
        private MessageType _noticeType = MessageType.Info;
        private double _noticeUntil;
        private bool _restartRequested;
        private bool _updateStopScheduled;
        private bool _updateStopInProgress;
        private bool _updateStopFailed;
        private bool _runtimeDetailsExpanded;
        private bool _repairInProgress;
        private bool _statusRefreshInProgress;
        private string _expandedAgentClient = "";
        private double _lastUpdateStopAttempt;
        private Vector2 _mainScroll;

        private GUIStyle _cardStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _messageStyle;
        private GUIStyle _sectionTitleStyle;
        private bool _stylesInitialized;

        private const double UpdateStopRetryCooldownSeconds = 2d;
        internal const string McpPortLabel = "MCP 端口";
        internal const string UnityBridgePortLabel = "Unity Bridge 端口";

        [MenuItem("UPilot/UPilot", false, 200)]
        public static void Open()
        {
            try
            {
                var window = GetWindow<UPilotMainWindow>("UPilot");
                window.minSize = new Vector2(440, 400);
                window.Show();
                UPilotUpdateService.Instance.EnsureLatestReleaseCheck();
            }
            catch (Exception ex)
            {
                ReportMainWindowException("打开 UPilot 主界面失败", ex);
            }
        }

        public static void OpenSetup()
        {
            try
            {
                var window = GetWindow<UPilotMainWindow>("UPilot");
                window.minSize = new Vector2(440, 400);
                window.EnterSetupView();
                window.Show();
                window.Focus();
            }
            catch (Exception ex)
            {
                ReportMainWindowException("打开 UPilot 首次设置失败", ex);
            }
        }

        public static void OpenWithNotice(
            string message,
            MessageType type = MessageType.Warning,
            double durationSeconds = 12d)
        {
            try
            {
                var window = GetWindow<UPilotMainWindow>("UPilot");
                window.minSize = new Vector2(440, 400);
                window._mainView = UPilotMainView.Dashboard;
                window.ShowNoticeForDuration(message, type, durationSeconds);
                window.Show();
                window.Focus();
            }
            catch (Exception ex)
            {
                ReportMainWindowException("打开 UPilot 主界面提示失败", ex);
            }
        }

        private void OnEnable()
        {
            try
            {
                minSize = new Vector2(440, 400);
                if (UPilotServerRuntimeService.IsSourceUpdateChannel())
                    UPilotUpdateService.ResetSourceChannelState();
                RefreshAgentConfigs(force: true);
                RefreshSnapshot();
                EnforceUpdateMaintenanceStop();
                if (UPilotPackageUpdateLifecycle.TryGetPendingPackageUpdateNotice(out var pendingUpdateNotice))
                    ShowNoticeForDuration(pendingUpdateNotice, MessageType.Warning, 12d);
                UPilotUpdateService.Instance.EnsureLatestReleaseCheck();
            }
            catch (Exception ex)
            {
                ReportMainWindowException("UPilot 主界面初始化失败", ex);
                ShowExceptionNotice("UPilot 主界面初始化失败", ex);
            }
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            try
            {
                EditorApplication.update -= OnEditorUpdate;
            }
            catch (Exception ex)
            {
                ReportMainWindowException("UPilot 主界面关闭处理失败", ex);
            }
        }

        private void OnEditorUpdate()
        {
            try
            {
                if (EditorApplication.timeSinceStartup - _lastRepaint < 0.4d)
                    return;

                _lastRepaint = EditorApplication.timeSinceStartup;
                Repaint();
            }
            catch (Exception ex)
            {
                EditorApplication.update -= OnEditorUpdate;
                ReportMainWindowException("UPilot 主界面刷新回调失败", ex);
            }
        }

        private void OnGUI()
        {
            try
            {
                DrawMainWindowGui();
            }
            catch (Exception ex)
            {
                ReportMainWindowException("UPilot 主界面绘制失败", ex);
                DrawExceptionFallback("UPilot 主界面绘制失败", ex);
            }
        }

        private void DrawMainWindowGui()
        {
            InitializeStyles();
            if (_mainView == UPilotMainView.Setup)
            {
                DrawSetupView();
                return;
            }

            if (Event.current.type == EventType.Layout)
            {
                RefreshAgentConfigs(force: false);
                RefreshSnapshot();
                EnforceUpdateMaintenanceStop();
            }

            var displaySnapshot = GetDisplaySnapshot();
            _mainScroll = EditorGUILayout.BeginScrollView(
                _mainScroll,
                false,
                false,
                GUIStyle.none,
                GUI.skin.verticalScrollbar,
                GUI.skin.scrollView);
            try
            {
                DrawNotice(displaySnapshot.State);
                DrawReleaseUpdateReminder(displaySnapshot);
                EditorGUILayout.Space(4);
                DrawMainCard(displaySnapshot);
                EditorGUILayout.Space(4);
            }
            finally
            {
                EditorGUILayout.EndScrollView();
            }
        }

        private void InitializeStyles()
        {
            if (_stylesInitialized) return;
            _stylesInitialized = true;

            _cardStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(10, 10, 10, 10),
                margin = new RectOffset(8, 8, 1, 1),
            };
            _titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 15,
                wordWrap = false,
            };
            _messageStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 11,
                wordWrap = true,
                normal =
                {
                    textColor = EditorGUIUtility.isProSkin
                        ? new Color(0.76f, 0.76f, 0.76f)
                        : new Color(0.25f, 0.25f, 0.25f),
                },
            };
            _sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 11,
            };
        }

        private void RefreshSnapshot()
        {
            _bridgeStatus = UPilotBridge.Instance.GetStatus();
            _mcpStatus = UPilotMcpServerManager.Instance.GetStatus();
            var next = UPilotQuickStart.Evaluate(_bridgeStatus, _mcpStatus, _agentConfigs);
            if (next.State != _lastState)
            {
                if (_restartRequested && next.State == UPilotMainState.Ready)
                {
                    _restartRequested = false;
                    ShowNotice("UPilot 已重新启动");
                }
                else if (_restartRequested && next.State == UPilotMainState.NeedsRepair)
                {
                    _restartRequested = false;
                    ShowNotice("UPilot 重启未完成", MessageType.Error);
                }

                _lastState = next.State;
                _stateChangedAt = EditorApplication.timeSinceStartup;
                if (next.State == UPilotMainState.NeedsRepair)
                    _runtimeDetailsExpanded = true;
            }
            _snapshot = next;
        }

        private async void RefreshRuntimeStatus()
        {
            if (_statusRefreshInProgress)
                return;

            _statusRefreshInProgress = true;
            Repaint();
            try
            {
                await UPilotMcpServerManager.Instance.GetFreshStatusAsync();
                if (this == null)
                    return;
                RefreshSnapshot();
                ShowNotice("运行状态已刷新");
            }
            catch (Exception ex)
            {
                ReportMainWindowException("刷新 UPilot 运行状态失败", ex);
                ShowExceptionNotice("刷新 UPilot 运行状态失败", ex);
            }
            finally
            {
                _statusRefreshInProgress = false;
                if (this != null)
                    Repaint();
            }
        }

        private UPilotMainSnapshot GetDisplaySnapshot()
        {
            var updateStatus = UPilotUpdateService.Instance.GetOperationStatus();
            if (!UPilotServerRuntimeService.IsSourceUpdateChannel() && updateStatus.IsRunning)
            {
                var title = (_updateStopScheduled || _updateStopInProgress)
                    ? "正在强制停止服务"
                    : string.IsNullOrWhiteSpace(updateStatus.Label) ? "等待更新完成" : updateStatus.Label;
                return new UPilotMainSnapshot(
                    UPilotMainState.Updating,
                    title,
                    BuildUpdateSnapshotMessage(updateStatus),
                    _bridgeStatus.IsStarted,
                    _mcpStatus.IsRunning);
            }

            return _snapshot;
        }

        private void EnforceUpdateMaintenanceStop()
        {
            var updateService = UPilotUpdateService.Instance;
            if (!updateService.IsServiceStartBlocked)
            {
                _updateStopScheduled = false;
                _updateStopInProgress = false;
                _updateStopFailed = false;
                return;
            }

            if (!IsServiceActive(_bridgeStatus, _mcpStatus))
            {
                _updateStopScheduled = false;
                _updateStopInProgress = false;
                _updateStopFailed = false;
                return;
            }

            if (_updateStopScheduled || _updateStopInProgress)
                return;

            var now = EditorApplication.timeSinceStartup;
            if (now - _lastUpdateStopAttempt < UpdateStopRetryCooldownSeconds)
                return;

            _lastUpdateStopAttempt = now;
            _updateStopScheduled = true;
            _updateStopInProgress = true;
            _updateStopFailed = false;
            ShowNoticeForDuration(UPilotUpdateService.ForceStoppingServiceMessage, MessageType.Warning, 4d);
            EditorApplication.delayCall += ForceStopServicesForUpdate;
        }

        private void ForceStopServicesForUpdate()
        {
            _updateStopScheduled = false;
            if (!UPilotUpdateService.Instance.IsServiceStartBlocked)
            {
                _updateStopInProgress = false;
                _updateStopFailed = false;
                Repaint();
                return;
            }

            var stopped = false;
            try
            {
                UPilotBridge.Instance.Stop();
                var portsReleased = UPilotMcpServerManager.Instance.StopServerAndWaitForExit();
                UPilotMcpServerManager.Instance.InvalidateStatusCache();
                RefreshSnapshot();
                stopped = !IsServiceActive(_bridgeStatus, _mcpStatus);
                if (!portsReleased && stopped)
                    Debug.LogWarning("[UPilot] Service was stopped during update, but configured ports are still unavailable.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[UPilot] 更新期间强制停止服务失败：" + ex.Message + "\n" + ex);
            }

            _updateStopInProgress = false;
            _updateStopFailed = !stopped;
            ShowNoticeForDuration(
                stopped
                    ? UPilotUpdateService.ServicePausedForUpdateMessage
                    : UPilotUpdateService.ForceStopFailedMessage,
                stopped ? MessageType.Info : MessageType.Error,
                stopped ? 3.5d : 6d);
            Repaint();
        }

        private string BuildUpdateSnapshotMessage(UPilotUpdateOperationStatus status)
        {
            if (_updateStopScheduled || _updateStopInProgress)
                return UPilotUpdateService.ForceStoppingServiceMessage;
            if (_updateStopFailed)
                return UPilotUpdateService.ForceStopFailedMessage;
            if (status.BlocksServiceStart)
                return UPilotUpdateService.ServicePausedForUpdateMessage;
            return string.IsNullOrWhiteSpace(status.Message)
                ? "正在更新 UPilot，完成后会自动恢复服务。"
                : status.Message;
        }

        private static bool IsServiceActive(BridgeStatus bridgeStatus, McpServerStatus mcpStatus)
        {
            return bridgeStatus.IsStarted || mcpStatus.IsRunning;
        }

        private void RefreshAgentConfigs(bool force)
        {
            if (!force && EditorApplication.timeSinceStartup - _lastAgentRefresh < 2d)
                return;

            _lastAgentRefresh = EditorApplication.timeSinceStartup;
            _agentConfigs = UPilotAgentSetup.GetMcpConfigStatuses();
            _ruleConfigs = UPilotAgentSetup.GetRuleConfigStatuses();
            _skillConfigs = UPilotAgentSetup.GetSkillConfigStatuses();

            if (force && !_agentAdviceNoticeShown && HasAgentIntegrationIssues())
            {
                _agentAdviceNoticeShown = true;
                ShowNotice("检测到 Agent/MCP/Skill 配置不一致，可在 Agent 配置区域更新。", MessageType.Warning);
            }

        }

        private void DrawReleaseUpdateReminder(UPilotMainSnapshot snapshot)
        {
            if (snapshot.State == UPilotMainState.Updating)
                return;

            var status = UPilotUpdateService.Instance.GetLatestReleaseCheckStatus();
            if (!status.HasUpdate ||
                status.IsSuppressed ||
                string.IsNullOrWhiteSpace(status.LatestVersion))
            {
                return;
            }

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope(GUILayout.MinWidth(160f)))
                    {
                        EditorGUILayout.LabelField("发现新版本 " + status.LatestVersion, EditorStyles.boldLabel);
                        if (!string.IsNullOrWhiteSpace(status.Message))
                            EditorGUILayout.LabelField(status.Message, _messageStyle);
                    }

                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("更新中心", GUILayout.Width(76f), GUILayout.Height(28f)))
                        UPilotUpdateWindow.Open(ShowNotice);

                    var skip = EditorGUILayout.ToggleLeft("本版本不再提醒", false, GUILayout.Width(126f));
                    if (skip)
                    {
                        UPilotUpdateService.Instance.SuppressLatestReleaseReminder(status.LatestVersion);
                        ShowNoticeForDuration("已跳过 " + status.LatestVersion + " 更新提醒", MessageType.Info, 3.5d);
                        Repaint();
                    }
                }
            }
        }

        private void DrawMainCard(UPilotMainSnapshot snapshot)
        {
            using (new EditorGUILayout.VerticalScope(_cardStyle))
            {
                DrawStatusActionBar(snapshot);
                if (snapshot.State == UPilotMainState.Updating)
                    DrawUpdateProgressPanel();

                if (snapshot.State == UPilotMainState.SetupRequired)
                {
                    EditorGUILayout.Space(5);
                    DrawSetupControls();
                }
                else
                {
                    EditorGUILayout.Space(12);
                    DrawOperationsDashboard(snapshot);
                }

                DrawAdvancedEntry();
            }
        }

        private void DrawUpdateProgressPanel()
        {
            var status = UPilotUpdateService.Instance.GetOperationStatus();
            if (!status.IsRunning)
                return;

            var download = UPilotServerRuntimeService.Instance.DownloadState;
            var progress = download.IsRunning && download.TotalBytes > 0
                ? download.Progress
                : UPilotUpdateService.EstimateOperationProgress(status.Phase);
            var label = download.IsRunning
                ? UPilotUpdateService.FormatDownloadProgressLabel(download)
                : string.IsNullOrWhiteSpace(status.Label) ? "正在更新" : status.Label;
            var detail = (_updateStopScheduled || _updateStopInProgress || _updateStopFailed)
                ? BuildUpdateSnapshotMessage(status)
                : download.IsRunning
                ? UPilotUpdateService.FormatDownloadProgressDetail(download)
                : status.Message;

            EditorGUILayout.Space(6);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var rect = EditorGUILayout.GetControlRect(false, 18f);
                EditorGUI.ProgressBar(rect, progress, label);
                if (!string.IsNullOrWhiteSpace(detail))
                    EditorGUILayout.LabelField(detail, EditorStyles.miniLabel);
                if (!string.IsNullOrWhiteSpace(download.WarningMessage))
                    EditorGUILayout.HelpBox(download.WarningMessage, MessageType.Warning);

                var targetText = BuildUpdateTargetText(status);
                if (!string.IsNullOrWhiteSpace(targetText))
                    EditorGUILayout.LabelField(targetText, EditorStyles.miniLabel);

                var guardStatus = UPilotUpdateService.GetReloadGuardStatus();
                var guardMessage = UPilotUpdateService.GetReloadGuardNotice(guardStatus, compact: true);
                if (!string.IsNullOrWhiteSpace(guardMessage))
                    EditorGUILayout.HelpBox(
                        guardMessage,
                        guardStatus.HasFailure ? MessageType.Error : MessageType.Warning);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("查看更新详情", GUILayout.Width(108f), GUILayout.Height(24f)))
                        UPilotUpdateWindow.Open(ShowNotice);
                }
            }
        }

        private static string BuildUpdateTargetText(UPilotUpdateOperationStatus status)
        {
            var hasUpm = !string.IsNullOrWhiteSpace(status.TargetUpmVersion);
            var hasServer = !string.IsNullOrWhiteSpace(status.TargetServerVersion);
            if (hasUpm && hasServer)
                return $"目标版本：UPilot {status.TargetUpmVersion} · MCP {status.TargetServerVersion}";
            if (hasUpm)
                return "目标版本：UPilot " + status.TargetUpmVersion;
            if (hasServer)
                return "目标版本：MCP " + status.TargetServerVersion;
            return "";
        }

        private void DrawSetupControls()
        {
            EditorGUILayout.LabelField("完成端口、服务和 Agent 配置后即可开始使用。", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.Space(8);
            DrawColoredButton("配置并启动", SetupReadyColor, 48f, ConfigureAndStart);
        }

        private void DrawOperationsDashboard(UPilotMainSnapshot snapshot)
        {
            DrawWriteAccessBanner();
            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(IsServiceTransitioning(snapshot.State)))
                DrawAgentConfigurationList();
            EditorGUILayout.Space(10);
            DrawMcpEndpoint();
            EditorGUILayout.Space(10);
            DrawRuntimeDetails(snapshot);
        }

        private void DrawStatusActionBar(UPilotMainSnapshot snapshot)
        {
            var rect = EditorGUILayout.GetControlRect(false, 54f);
            DrawBandBackground(rect);

            const float horizontalPadding = 10f;
            const float buttonGap = 6f;
            var menuRect = new Rect(rect.xMax - horizontalPadding - 28f, rect.y + 6f, 28f, 24f);
            var actionWidth = 82f;
            if (snapshot.State == UPilotMainState.Updating)
                actionWidth = 116f;
            else if (snapshot.State == UPilotMainState.CheckingStatus)
                actionWidth = 102f;
            var actionRect = new Rect(menuRect.x - buttonGap - actionWidth, rect.y + 6f, actionWidth, 24f);
            var titleRect = new Rect(rect.x + horizontalPadding, rect.y + 6f, 58f, 24f);
            var dotRect = new Rect(titleRect.xMax, titleRect.y, 14f, titleRect.height);
            var stateRect = new Rect(dotRect.xMax, titleRect.y, 78f, titleRect.height);

            EditorGUI.LabelField(titleRect, "UPilot", _titleStyle);
            var previous = GUI.color;
            GUI.color = GetStateColor(snapshot.State);
            EditorGUI.LabelField(dotRect, "●");
            GUI.color = previous;
            EditorGUI.LabelField(stateRect, GetStateLabel(snapshot.State), EditorStyles.boldLabel);

            if (snapshot.State != UPilotMainState.SetupRequired &&
                snapshot.State != UPilotMainState.Ready)
                DrawServiceAction(actionRect, snapshot);
            if (GUI.Button(menuRect, "⋮"))
                ShowMoreMenu(snapshot);

            var messageRect = new Rect(
                rect.x + horizontalPadding,
                rect.y + 32f,
                rect.width - horizontalPadding * 2f,
                18f);
            EditorGUI.LabelField(messageRect, snapshot.Message, _messageStyle);
        }

        private void DrawServiceAction(Rect rect, UPilotMainSnapshot snapshot)
        {
            if (_repairInProgress)
            {
                using (new EditorGUI.DisabledScope(true))
                    GUI.Button(rect, "检查中…");
                return;
            }

            if (snapshot.State == UPilotMainState.Stopped)
            {
                if (GUI.Button(rect, "启动"))
                    StartUPilot();
                return;
            }

            if (snapshot.State == UPilotMainState.CheckingStatus ||
                snapshot.State == UPilotMainState.Starting ||
                snapshot.State == UPilotMainState.Restarting ||
                snapshot.State == UPilotMainState.Stopping ||
                snapshot.State == UPilotMainState.Updating)
            {
                var label = snapshot.State switch
                {
                    UPilotMainState.CheckingStatus => "获取状态中…",
                    UPilotMainState.Restarting => "重启中…",
                    UPilotMainState.Stopping => "停止中…",
                    UPilotMainState.Updating => "等待更新完成",
                    _ => "启动中…",
                };
                using (new EditorGUI.DisabledScope(true))
                    GUI.Button(rect, label);
                return;
            }

            var primaryLabel = snapshot.State == UPilotMainState.Ready ? "重启" : "自动修复";
            if (GUI.Button(rect, primaryLabel))
            {
                if (snapshot.State == UPilotMainState.Ready)
                {
                    RequestRestart();
                }
                else
                {
                    RepairUPilot();
                }
            }
        }

        private void RequestRestart()
        {
            try
            {
                if (UPilotUpdateService.Instance.IsServiceStartBlocked)
                {
                    ShowNotice(UPilotUpdateService.ServiceStartBlockedMessage, MessageType.Warning);
                    return;
                }

                _restartRequested = true;
                UPilotQuickStart.Restart();
                _stateChangedAt = EditorApplication.timeSinceStartup;
                RefreshSnapshot();
            }
            catch (Exception ex)
            {
                ReportMainWindowException("重启 UPilot 失败", ex);
                ShowExceptionNotice("重启 UPilot 失败", ex);
            }
        }

        private static bool IsServiceTransitioning(UPilotMainState state)
        {
            return state == UPilotMainState.CheckingStatus ||
                   state == UPilotMainState.Starting ||
                   state == UPilotMainState.Restarting ||
                   state == UPilotMainState.Stopping ||
                   state == UPilotMainState.Updating;
        }

        private void DrawMcpEndpoint()
        {
            var mcpUrl = UPilotAgentSetup.McpUrl;
            var row = EditorGUILayout.GetControlRect(false, 24f);
            const float labelWidth = 72f;
            const float buttonWidth = 64f;
            const float gap = 6f;
            var labelRect = new Rect(row.x, row.y, labelWidth, row.height);
            var buttonRect = new Rect(row.xMax - buttonWidth, row.y, buttonWidth, row.height);
            var fieldRect = new Rect(
                labelRect.xMax + gap,
                row.y,
                Mathf.Max(40f, buttonRect.x - labelRect.xMax - gap * 2f),
                row.height);

            EditorGUI.LabelField(labelRect, "MCP 地址", _sectionTitleStyle);
            EditorGUI.SelectableLabel(fieldRect, mcpUrl, EditorStyles.textField);
            if (GUI.Button(buttonRect, "复制"))
            {
                EditorGUIUtility.systemCopyBuffer = mcpUrl;
                ShowNotice("已复制 MCP 地址");
            }
        }

        private void DrawWriteAccessBanner()
        {
            var safety = UPilotProjectConfig.Current.safety;
            if (!ShouldShowWriteAccessBanner(safety))
                return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var icon = EditorGUIUtility.IconContent("console.warnicon.sml");
                    GUILayout.Label(icon, GUILayout.Width(24f), GUILayout.Height(24f));
                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUILayout.LabelField("需要授权", EditorStyles.boldLabel);
                        EditorGUILayout.LabelField(
                            "Agent 当前只能查看项目。允许授权后，才能修改脚本、资源和项目设置。",
                            _messageStyle);
                    }

                    GUILayout.Space(8f);
                    var change = UPilotWriteAccessUi.DrawActionButton(false, 88f, 28f);
                    if (change != UPilotWriteAccessChange.None)
                    {
                        ShowNotice(UPilotWriteAccessUi.GetSuccessMessage(change));
                        RefreshSnapshot();
                        Repaint();
                    }
                }
            }
        }

        internal static bool ShouldShowWriteAccessBanner(UPilotSafetyConfig safety)
        {
            return safety?.writeAccessApproved != true;
        }

        private void DrawRuntimeDetails(UPilotMainSnapshot snapshot)
        {
            var headerRect = EditorGUILayout.GetControlRect(false, 28f);
            DrawBandBackground(headerRect);
            DrawBandAccent(headerRect);
            var foldoutRect = new Rect(headerRect.x + 8f, headerRect.y + 3f, headerRect.width - 16f, 22f);
            _runtimeDetailsExpanded = EditorGUI.Foldout(
                foldoutRect,
                _runtimeDetailsExpanded,
                "运行详情",
                true,
                EditorStyles.foldoutHeader);
            if (!_runtimeDetailsExpanded)
                return;

            var serverVersion = string.IsNullOrEmpty(_mcpStatus.ServerVersion) ? "未启动/旧版" : _mcpStatus.ServerVersion;
            var upmVersion = UPilotServerRuntimeService.UpmVersion;
            var bridge = UPilotBridge.Instance;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                DrawRuntimeDetailRow("运行状态", GetStateLabel(snapshot.State));
                DrawRuntimeDetailRow(McpPortLabel, bridge.HttpPort.ToString());
                DrawRuntimeDetailRow(UnityBridgePortLabel, bridge.WsPort.ToString());
                DrawRuntimeDetailRow("Unity Bridge 状态", GetBridgeStatusLabel(_bridgeStatus));
                DrawRuntimeDetailRow("MCP HTTP 监听", GetListeningStateLabel(_mcpStatus.HttpPortListening));
                DrawRuntimeDetailRow("MCP WS 监听", GetListeningStateLabel(_mcpStatus.WsPortListening));
                DrawRuntimeDetailRow("进程归属", GetProcessOwnershipLabel(_mcpStatus.ProcessOwnership));
                DrawRuntimeDetailRow("进程 PID", _mcpStatus.ProcessId?.ToString() ?? "未识别");
                DrawRuntimeDetailRow("健康检查", GetHealthStatusLabel(_mcpStatus));
                if (!string.IsNullOrWhiteSpace(_mcpStatus.ProcessOwnershipEvidence))
                    DrawRuntimeDetailRow("身份依据", _mcpStatus.ProcessOwnershipEvidence);
                if (_mcpStatus.DiagnosisPending)
                    DrawRuntimeDetailRow("确认进度", $"第 {_mcpStatus.ConsecutiveIdentityMisses} 次探测");
                EditorGUILayout.Space(3f);
                if (string.Equals(upmVersion, serverVersion, StringComparison.OrdinalIgnoreCase))
                {
                    DrawRuntimeDetailRow("版本", upmVersion);
                }
                else
                {
                    DrawRuntimeDetailRow("UPM 版本", upmVersion);
                    DrawRuntimeDetailRow("服务版本", serverVersion);
                }

                DrawRuntimeDetailRow("运行方式", GetRuntimeModeLabel(snapshot.State));
                DrawRuntimeDetailRow("发布通道", UPilotServerRuntimeService.ResolveUpdateChannel());

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(_statusRefreshInProgress))
                    {
                        var label = _statusRefreshInProgress ? "刷新中…" : "刷新状态";
                        if (GUILayout.Button(label, GUILayout.Width(76f), GUILayout.Height(22f)))
                            RefreshRuntimeStatus();
                    }
                }
            }
        }

        private static void DrawRuntimeDetailRow(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(120f));
                EditorGUILayout.LabelField(value, EditorStyles.label);
            }
        }

        internal static string GetBridgeStatusLabel(BridgeStatus status)
        {
            if (status.IsAuthenticated)
                return "已连接";
            if (status.IsStarted)
                return "连接中";
            return "未启动";
        }

        internal static string GetProcessOwnershipLabel(McpProcessOwnership ownership)
        {
            if (ownership == McpProcessOwnership.CurrentUPilot)
                return "当前 UPilot";
            if (ownership == McpProcessOwnership.Foreign)
                return "其他程序";
            return "确认中";
        }

        private static string GetListeningStateLabel(bool listening)
        {
            return listening ? "正常" : "未监听";
        }

        private static string GetHealthStatusLabel(McpServerStatus status)
        {
            if (status.HealthIdentifiesUPilot)
                return "UPilot 响应正常";
            if (status.HealthResponded)
                return "已响应，但身份不匹配";
            return "无响应";
        }

        private static string GetRuntimeModeLabel(UPilotMainState state)
        {
            if (state == UPilotMainState.Updating)
                return "正在更新";
            if (state == UPilotMainState.CheckingStatus)
                return "获取状态中";
            return UPilotServerRuntimeService.Instance.RuntimeModeLabel;
        }

        private void DrawAgentConfigurationList()
        {
            var toolbarRect = EditorGUILayout.GetControlRect(false, 30f);
            const float updateWidth = 96f;
            const float menuWidth = 24f;
            DrawBandBackground(toolbarRect);
            DrawBandAccent(toolbarRect);
            var menuRect = new Rect(toolbarRect.xMax - 6f - menuWidth, toolbarRect.y + 3f, menuWidth, 24f);
            var updateRect = new Rect(menuRect.x - updateWidth, toolbarRect.y + 3f, updateWidth, 24f);
            var titleRect = new Rect(toolbarRect.x + 10f, toolbarRect.y + 3f, Mathf.Max(70f, updateRect.x - toolbarRect.x - 16f), 24f);

            EditorGUI.LabelField(titleRect, "Agent", _sectionTitleStyle);

            var issueCount = CountAgentIntegrationIssues();
            var updateLabel = GetAgentUpdateButtonLabel(issueCount);
            if (GUI.Button(updateRect, updateLabel))
                UpdateAllAgentIntegrations();
            if (GUI.Button(menuRect, "▾"))
                ShowAgentBulkUpdateMenu();

            DrawAgentIntegrationAdvice();

            foreach (var mcpStatus in _agentConfigs)
            {
                DrawAgentConfigurationRow(
                    mcpStatus,
                    FindRuleStatus(mcpStatus.ClientName),
                    FindSkillStatus(mcpStatus.ClientName));
            }
        }

        internal static string GetAgentUpdateButtonLabel(int issueCount)
        {
            return issueCount > 0 ? $"更新 {issueCount} 项" : "更新全部";
        }

        private void ShowAgentBulkUpdateMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("检查配置"), false, CheckAgentIntegrations);
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("强制重新配置全部…"), false, ForceUpdateAllAgentIntegrations);
            menu.ShowAsContext();
        }

        private void CheckAgentIntegrations()
        {
            RefreshAgentConfigs(force: true);
            var checkedIssueCount = CountAgentIntegrationIssues();
            if (checkedIssueCount == 0)
            {
                ShowNotice("检查完成，已配置的 Agent 均为最新");
                return;
            }

            var confirmationCount = CountCustomizedRuleConfigs();
            ShowNotice(
                confirmationCount == checkedIssueCount
                    ? $"检查完成，有 {checkedIssueCount} 项内容需要确认"
                    : $"检查完成，有 {checkedIssueCount} 项配置需要更新",
                MessageType.Warning);
        }

        private void ForceUpdateAllAgentIntegrations()
        {
            UpdateAllAgentIntegrations(forceAll: true);
        }

        private void DrawAgentIntegrationAdvice()
        {
            var messageType = BuildAgentIntegrationAdvice(out var message);
            if (messageType == MessageType.Info)
            {
                EditorGUILayout.LabelField("已配置的 Agent 均为最新", _messageStyle);
                return;
            }

            EditorGUILayout.HelpBox(message, messageType);
        }

        private MessageType BuildAgentIntegrationAdvice(out string message)
        {
            AgentMcpConfigStatus? firstMcpIssue = null;
            AgentRuleConfigStatus? firstRuleIssue = null;
            AgentSkillConfigStatus? firstSkillIssue = null;
            var issueCount = 0;
            var hasErrors = false;

            foreach (var status in _agentConfigs)
            {
                if (!NeedsMcpUpdate(status))
                    continue;

                issueCount++;
                firstMcpIssue ??= status;
                if (!string.IsNullOrEmpty(status.ErrorMessage))
                    hasErrors = true;
            }

            foreach (var status in _ruleConfigs)
            {
                if (!NeedsRuleUpdate(status))
                    continue;

                issueCount++;
                firstRuleIssue ??= status;
                if (status.State == AgentRuleConfigState.Error)
                    hasErrors = true;
            }

            var seenSkillPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var status in _skillConfigs)
            {
                if (!NeedsSkillUpdate(status) || !seenSkillPaths.Add(GetSkillIssueKey(status)))
                    continue;

                issueCount++;
                firstSkillIssue ??= status;
                if (status.State == AgentSkillConfigState.Error)
                    hasErrors = true;
            }

            if (issueCount == 0)
            {
                message = "已配置的 Agent 均为最新";
                return MessageType.Info;
            }

            if (hasErrors)
            {
                message = "部分 Agent 配置无法读取\n请在下方列表中查看异常项目。";
                return MessageType.Error;
            }

            if (issueCount == 1 && firstRuleIssue.HasValue)
            {
                var status = firstRuleIssue.Value;
                if (status.State == AgentRuleConfigState.Customized)
                {
                    message = $"{status.ClientName} 内容需要确认\n" +
                              $"检测到 {status.ClientName} 的 UPilot Agent 规则有本地修改。" +
                              "当前仍可正常使用，更新时可以选择保留或替换。";
                    return MessageType.Warning;
                }

                if (status.State == AgentRuleConfigState.Missing)
                {
                    message = $"{status.ClientName} 尚未同步 UPilot Agent 规则\n" +
                              "完成安装后即可使用最新的 Agent 配置。";
                    return MessageType.Warning;
                }

                message = $"{status.ClientName} 有新内容可用\n更新后可使用最新的 UPilot Agent 规则。";
                return MessageType.Warning;
            }

            if (issueCount == 1 && firstSkillIssue.HasValue)
            {
                var status = firstSkillIssue.Value;
                if (status.State == AgentSkillConfigState.Customized)
                {
                    message = $"{status.ClientName} Skill 内容需要确认\n" +
                              "检测到已安装的 UPilot Skill 有本地修改。当前仍可使用，更新时可以选择保留或替换。";
                    return MessageType.Warning;
                }

                if (status.State == AgentSkillConfigState.Missing)
                {
                    message = $"{status.ClientName} 尚未安装 UPilot Skill\n安装后即可使用 Skill 提供的工作流与工具说明。";
                    return MessageType.Warning;
                }

                message = $"{status.ClientName} Skill 有新版本\n更新后可使用最新的 UPilot 工作流与工具说明。";
                return MessageType.Warning;
            }

            if (issueCount == 1 && firstMcpIssue.HasValue)
            {
                var status = firstMcpIssue.Value;
                message = $"{status.ClientName} 连接地址需要更新\n当前连接可能无法使用，处理后会同步到最新地址。";
                return MessageType.Warning;
            }

            message = $"有 {issueCount} 项 Agent 配置需要处理\n请查看下方标记的项目，处理时会先确认可能覆盖的内容。";
            return MessageType.Warning;
        }

        private bool HasAgentIntegrationIssues()
        {
            return CountAgentIntegrationIssues() > 0;
        }

        private int CountAgentIntegrationIssues()
        {
            var issueCount = 0;
            foreach (var status in _agentConfigs)
            {
                if (NeedsMcpUpdate(status))
                    issueCount++;
            }

            foreach (var status in _ruleConfigs)
            {
                if (NeedsRuleUpdate(status))
                    issueCount++;
            }

            var seenSkillPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var status in _skillConfigs)
            {
                if (NeedsSkillUpdate(status) && seenSkillPaths.Add(GetSkillIssueKey(status)))
                    issueCount++;
            }

            return issueCount;
        }

        private int CountCustomizedRuleConfigs()
        {
            var count = 0;
            foreach (var status in _ruleConfigs)
            {
                if (status.HasLocalCustomization)
                    count++;
            }

            var seenSkillPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var status in _skillConfigs)
            {
                if (status.HasLocalCustomization && seenSkillPaths.Add(GetSkillIssueKey(status)))
                    count++;
            }

            return count;
        }

        private static bool NeedsMcpUpdate(AgentMcpConfigStatus status)
        {
            if (!string.IsNullOrEmpty(status.ErrorMessage))
                return true;
            if (!status.FileExists)
                return false;
            return !status.HasUPilotEntry || !status.UsesCurrentUrl;
        }

        private static bool NeedsRuleUpdate(AgentRuleConfigStatus status)
        {
            return status.State != AgentRuleConfigState.Current;
        }

        private static bool NeedsSkillUpdate(AgentSkillConfigStatus status)
        {
            return !status.IsSatisfied;
        }

        private static string GetSkillIssueKey(AgentSkillConfigStatus status)
        {
            return string.IsNullOrEmpty(status.ConfigPath)
                ? status.ClientName
                : status.ConfigPath;
        }

        private void DrawAgentConfigurationRow(
            AgentMcpConfigStatus mcpStatus,
            AgentRuleConfigStatus ruleStatus,
            AgentSkillConfigStatus skillStatus)
        {
            var expanded = string.Equals(_expandedAgentClient, mcpStatus.ClientName, StringComparison.Ordinal);
            var row = EditorGUILayout.GetControlRect(false, 30f);
            var foldoutRect = new Rect(row.x + 2f, row.y + 3f, Mathf.Max(100f, row.width - 110f), 24f);
            var nextExpanded = EditorGUI.Foldout(
                foldoutRect,
                expanded,
                mcpStatus.ClientName,
                true,
                EditorStyles.foldout);
            if (nextExpanded != expanded)
                _expandedAgentClient = nextExpanded ? mcpStatus.ClientName : "";

            var statusText = GetAgentOverallStateText(mcpStatus, ruleStatus, skillStatus);
            var statusReady = IsAgentIntegrationReady(mcpStatus, ruleStatus, skillStatus);
            var statusRect = new Rect(row.xMax - 96f, row.y, 96f, row.height);
            DrawStatusCell(
                statusRect,
                statusText,
                statusReady,
                HasAgentIntegrationError(mcpStatus, ruleStatus, skillStatus));
            DrawTableSeparator();

            if (!nextExpanded)
                return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                DrawAgentDetailRow(
                    "Agent 规则",
                    GetRuleStateText(ruleStatus),
                    GetRuleDetailState(ruleStatus),
                    ruleStatus.State == AgentRuleConfigState.Missing ? "同步规则" : "更新规则",
                    () => UpdateAgentRuleConfig(ruleStatus),
                    BuildRuleTooltip(ruleStatus));
                DrawAgentDetailRow(
                    "MCP 配置",
                    GetCompactMcpState(mcpStatus),
                    GetMcpDetailState(mcpStatus),
                    mcpStatus.HasUPilotEntry ? "更新配置" : "配置",
                    () => UpdateAgentMcpConfig(mcpStatus),
                    BuildMcpTooltip(mcpStatus, _mcpStatus));
                DrawAgentDetailRow(
                    "Skill 技能",
                    GetSkillStateText(skillStatus),
                    GetSkillDetailState(skillStatus),
                    skillStatus.IsApplicable
                        ? skillStatus.State == AgentSkillConfigState.Missing ? "安装 Skill" : "更新 Skill"
                        : "",
                    skillStatus.IsApplicable ? () => UpdateAgentSkillConfig(skillStatus) : (Action)null,
                    BuildSkillTooltip(skillStatus));
            }
        }

        internal static string[] GetAgentDetailLabels()
        {
            return new[] { "Agent 规则", "MCP 配置", "Skill 技能" };
        }

        private enum AgentDetailState
        {
            Ready,
            NeedsAttention,
            Error,
            Unavailable,
        }

        private static void DrawAgentDetailRow(
            string label,
            string value,
            AgentDetailState state,
            string actionLabel,
            Action action,
            string tooltip)
        {
            var row = EditorGUILayout.GetControlRect(false, 26f);
            const float labelWidth = 96f;
            const float dotWidth = 16f;
            const float actionWidth = 88f;
            const float gap = 6f;
            var labelRect = new Rect(row.x, row.y + 2f, labelWidth, 22f);
            var dotRect = new Rect(labelRect.xMax, row.y + 2f, dotWidth, 22f);
            var actionRect = new Rect(row.xMax - actionWidth, row.y + 2f, actionWidth, 22f);
            var valueRect = new Rect(
                dotRect.xMax,
                row.y + 2f,
                Mathf.Max(40f, actionRect.x - dotRect.xMax - gap),
                22f);

            var content = new GUIContent(label, tooltip);
            EditorGUI.LabelField(labelRect, content, EditorStyles.label);
            var previous = GUI.color;
            switch (state)
            {
                case AgentDetailState.Ready:
                    GUI.color = new Color(0.25f, 0.82f, 0.38f);
                    break;
                case AgentDetailState.Error:
                    GUI.color = new Color(0.95f, 0.30f, 0.25f);
                    break;
                case AgentDetailState.Unavailable:
                    GUI.color = EditorGUIUtility.isProSkin
                        ? new Color(0.55f, 0.55f, 0.55f)
                        : new Color(0.42f, 0.42f, 0.42f);
                    break;
                default:
                    GUI.color = new Color(1f, 0.65f, 0.15f);
                    break;
            }

            EditorGUI.LabelField(dotRect, new GUIContent("●", tooltip));
            GUI.color = previous;
            EditorGUI.LabelField(valueRect, new GUIContent(value, tooltip), EditorStyles.label);
            if (action == null || string.IsNullOrEmpty(actionLabel))
            {
                var centered = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter };
                EditorGUI.LabelField(actionRect, new GUIContent("—", tooltip), centered);
                return;
            }

            if (GUI.Button(actionRect, new GUIContent(actionLabel, tooltip)))
                action.Invoke();
        }

        internal static string GetAgentOverallStateText(
            AgentMcpConfigStatus mcpStatus,
            AgentRuleConfigStatus ruleStatus,
            AgentSkillConfigStatus skillStatus)
        {
            if (HasAgentIntegrationError(mcpStatus, ruleStatus, skillStatus))
                return "异常";
            if (!mcpStatus.FileExists && !mcpStatus.HasUPilotEntry)
                return "未配置";
            if (IsAgentIntegrationReady(mcpStatus, ruleStatus, skillStatus))
                return "已就绪";
            return "需更新";
        }

        private static bool IsAgentIntegrationReady(
            AgentMcpConfigStatus mcpStatus,
            AgentRuleConfigStatus ruleStatus,
            AgentSkillConfigStatus skillStatus)
        {
            return mcpStatus.IsConfigured && ruleStatus.IsCurrent && skillStatus.IsSatisfied;
        }

        private static bool HasAgentIntegrationError(
            AgentMcpConfigStatus mcpStatus,
            AgentRuleConfigStatus ruleStatus,
            AgentSkillConfigStatus skillStatus)
        {
            return !string.IsNullOrEmpty(mcpStatus.ErrorMessage) ||
                   ruleStatus.State == AgentRuleConfigState.Error ||
                   skillStatus.State == AgentSkillConfigState.Error;
        }

        private static void DrawStatusCell(Rect rect, string value, bool ready, bool error = false)
        {
            var dotRect = new Rect(rect.x, rect.y, 14f, rect.height);
            var labelRect = new Rect(dotRect.xMax, rect.y, Mathf.Max(0f, rect.width - dotRect.width), rect.height);
            var previous = GUI.color;
            GUI.color = error
                ? new Color(0.95f, 0.30f, 0.25f)
                : ready
                    ? new Color(0.25f, 0.82f, 0.38f)
                    : new Color(1f, 0.65f, 0.15f);
            EditorGUI.LabelField(dotRect, "●");
            GUI.color = previous;
            EditorGUI.LabelField(labelRect, value);
        }

        private static void DrawTableSeparator()
        {
            var rect = EditorGUILayout.GetControlRect(false, 1f);
            var color = EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 0.08f)
                : new Color(0f, 0f, 0f, 0.12f);
            EditorGUI.DrawRect(rect, color);
        }

        private static void DrawBandBackground(Rect rect)
        {
            EditorGUI.DrawRect(
                rect,
                EditorGUIUtility.isProSkin
                    ? new Color(1f, 1f, 1f, 0.035f)
                    : new Color(0f, 0f, 0f, 0.045f));
        }

        private static void DrawBandAccent(Rect rect)
        {
            EditorGUI.DrawRect(
                new Rect(rect.x, rect.y, 3f, rect.height),
                EditorGUIUtility.isProSkin
                    ? new Color(0.28f, 0.58f, 0.92f, 0.85f)
                    : new Color(0.16f, 0.42f, 0.76f, 0.9f));
        }

        private AgentRuleConfigStatus FindRuleStatus(string clientName)
        {
            foreach (var status in _ruleConfigs)
            {
                if (status.ClientName == clientName)
                    return status;
            }

            return new AgentRuleConfigStatus(clientName, "", AgentRuleConfigState.Missing);
        }

        private AgentSkillConfigStatus FindSkillStatus(string clientName)
        {
            foreach (var status in _skillConfigs)
            {
                if (status.ClientName == clientName)
                    return status;
            }

            return new AgentSkillConfigStatus(
                clientName,
                "",
                AgentSkillConfigState.Error,
                "Skill 状态尚未加载");
        }

        private static string GetCompactMcpState(AgentMcpConfigStatus status)
        {
            if (status.IsConfigured) return "已配置";
            if (status.HasUPilotEntry && !status.UsesCurrentUrl) return "需要更新";
            if (!string.IsNullOrEmpty(status.ErrorMessage)) return "异常";
            return "未配置";
        }

        private static string GetRuleStateText(AgentRuleConfigStatus status)
        {
            if (status.State == AgentRuleConfigState.Customized) return "需要确认";
            if (status.State == AgentRuleConfigState.UpdateAvailable) return "有新版本";
            return status.StateText;
        }

        private static string GetSkillStateText(AgentSkillConfigStatus status)
        {
            return status.StateText;
        }

        private static AgentDetailState GetMcpDetailState(AgentMcpConfigStatus status)
        {
            if (!string.IsNullOrEmpty(status.ErrorMessage)) return AgentDetailState.Error;
            return status.IsConfigured ? AgentDetailState.Ready : AgentDetailState.NeedsAttention;
        }

        private static AgentDetailState GetRuleDetailState(AgentRuleConfigStatus status)
        {
            if (status.State == AgentRuleConfigState.Error) return AgentDetailState.Error;
            return status.IsCurrent ? AgentDetailState.Ready : AgentDetailState.NeedsAttention;
        }

        private static AgentDetailState GetSkillDetailState(AgentSkillConfigStatus status)
        {
            if (status.State == AgentSkillConfigState.NotProvided) return AgentDetailState.Unavailable;
            if (status.State == AgentSkillConfigState.Error) return AgentDetailState.Error;
            return status.IsCurrent ? AgentDetailState.Ready : AgentDetailState.NeedsAttention;
        }

        internal static string BuildMcpTooltip(AgentMcpConfigStatus status)
        {
            return BuildMcpTooltip(status, default);
        }

        internal static string BuildMcpTooltip(
            AgentMcpConfigStatus status,
            McpServerStatus serverStatus)
        {
            var text = new System.Text.StringBuilder();
            text.Append("状态：").Append(GetCompactMcpState(status));
            AppendTooltipLine(text, "配置文件", status.ConfigPath);
            AppendTooltipLine(text, "当前 URL", status.ConfiguredUrl);
            AppendTooltipLine(text, "目标 URL", UPilotAgentSetup.McpUrl);
            if (serverStatus.ToolCountsKnown)
            {
                if (serverStatus.DetailedToolCountsKnown)
                {
                    AppendTooltipLine(text, "已注册 MCP 工具", serverStatus.RegisteredToolCount + " 个");
                    AppendTooltipLine(text, "当前可用", serverStatus.AvailableToolCount + " 个");
                    AppendTooltipLine(text, "当前可调用", serverStatus.CallableToolCount + " 个");
                }
                else
                {
                    AppendTooltipLine(text, "当前可用 MCP 工具", serverStatus.AvailableToolCount + " 个");
                    AppendTooltipLine(text, "工具计数说明", "当前 MCP Server 版本未区分注册数量和可调用数量");
                }

                if (serverStatus.ToolRegistryVersion > 0)
                    AppendTooltipLine(text, "工具注册表版本", serverStatus.ToolRegistryVersion.ToString());
                AppendTooltipLine(
                    text,
                    "主要工具分类",
                    FormatMcpCategorySummary(serverStatus.ToolCategorySummary, 8));
                AppendTooltipLine(
                    text,
                    "数量说明",
                    "可调用数量会受 Unity 连接、功能开关和写入授权影响");
            }
            else
            {
                AppendTooltipLine(text, "MCP 工具数量", "服务尚未提供；更新或重启 MCP Server 后刷新状态");
            }
            AppendTooltipLine(text, "错误", status.ErrorMessage);
            return text.ToString();
        }

        internal static string BuildRuleTooltip(AgentRuleConfigStatus status)
        {
            var text = new System.Text.StringBuilder();
            text.Append("状态：").Append(GetRuleStateText(status));
            var paths = status.ConfigPaths ?? Array.Empty<string>();
            for (var i = 0; i < paths.Length; i++)
                AppendTooltipLine(text, paths.Length == 1 ? "规则文件" : $"规则文件 {i + 1}", paths[i]);
            AppendTooltipLine(text, "模板源文件", status.SourcePath);
            AppendVersionTooltipLines(text, status.InstalledVersion, status.TargetVersion);
            AppendTooltipLine(text, "当前内容 SHA256", status.ContentHash);
            AppendTooltipLine(text, "目标内容 SHA256", status.ExpectedHash);
            AppendTooltipLine(text, "错误", status.ErrorMessage);
            return text.ToString();
        }

        internal static string BuildSkillTooltip(AgentSkillConfigStatus status)
        {
            var text = new System.Text.StringBuilder();
            text.Append("状态：").Append(GetSkillStateText(status));
            var roots = status.SkillRootPaths ?? Array.Empty<string>();
            if (roots.Length == 0)
            {
                AppendTooltipLine(text, "项目级 Skill 根目录", status.SkillRootPath);
            }
            else
            {
                for (var i = 0; i < roots.Length; i++)
                {
                    AppendTooltipLine(
                        text,
                        roots.Length == 1 ? "项目级 Skill 根目录" : $"Skill 发现目录 {i + 1}",
                        roots[i]);
                }
            }
            AppendTooltipLine(text, "项目级 Skill 数量", status.InstalledSkillCount + " 个");
            AppendTooltipLine(text, "已安装 Skill", FormatLimitedList(status.InstalledSkillNames, 8));
            AppendTooltipLine(text, "UPilot Skill 数量", status.UpilotSkillCount + " 个");
            AppendTooltipLine(text, "安装目录", status.ConfigPath);
            AppendTooltipLine(text, "Skill 源目录", status.SourcePath);
            if (status.IsApplicable)
            {
                AppendTooltipLine(
                    text,
                    "Skill 当前版本",
                    status.InstalledVersion > 0 ? status.InstalledVersion.ToString() : "未检测到");
                AppendTooltipLine(
                    text,
                    "Skill 目标版本",
                    status.TargetVersion > 0 ? status.TargetVersion.ToString() : "未提供");
            }
            AppendTooltipLine(text, "能力覆盖", FormatLimitedList(status.CapabilityLabels, 10));
            AppendTooltipLine(text, "Skill 引用的 MCP 工具", status.AssociatedToolCount + " 个");
            AppendTooltipLine(text, "主要关联工具", FormatLimitedList(status.PrimaryToolNames, 10));
            AppendTooltipLine(text, "记录 SHA256", status.RecordedHash);
            AppendTooltipLine(text, "当前 SHA256", status.ContentHash);
            AppendTooltipLine(text, "说明", status.ApplicabilityExplanation);
            AppendTooltipLine(text, "错误", status.ErrorMessage);
            return text.ToString();
        }

        private static string FormatMcpCategorySummary(string summary, int maxItems)
        {
            if (string.IsNullOrWhiteSpace(summary))
                return "";
            var values = summary.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var count = Math.Min(values.Length, Math.Max(1, maxItems));
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                var pair = values[i].Split(new[] { ':' }, 2);
                var name = pair.Length > 0 ? GetMcpCategoryLabel(pair[0]) : values[i];
                result[i] = pair.Length == 2 ? name + " " + pair[1] : name;
            }

            var text = string.Join("、", result);
            if (values.Length > count)
                text += $"，另 {values.Length - count} 类";
            return text;
        }

        private static string GetMcpCategoryLabel(string category)
        {
            switch ((category ?? "").Trim().ToLowerInvariant())
            {
                case "asset": return "资源";
                case "editor": return "编辑器";
                case "flow": return "Flow";
                case "console": return "控制台";
                case "operation": return "长任务";
                case "scene": return "场景";
                case "test": return "测试";
                case "screenshot": return "截图";
                case "compile": return "编译";
                case "script": return "脚本";
                case "prefab": return "Prefab";
                case "gameobject": return "GameObject";
                case "component": return "组件";
                case "reflection": return "反射";
                case "package": return "包管理";
                case "material": return "材质";
                case "build": return "构建";
                default: return category;
            }
        }

        private static string FormatLimitedList(string[] values, int maxItems)
        {
            if (values == null || values.Length == 0)
                return "";
            var count = Math.Min(values.Length, Math.Max(1, maxItems));
            var displayed = new string[count];
            Array.Copy(values, displayed, count);
            var text = string.Join("、", displayed);
            if (values.Length > count)
                text += $"，另 {values.Length - count} 项";
            return text;
        }

        private static void AppendVersionTooltipLines(
            System.Text.StringBuilder text,
            int installedVersion,
            int targetVersion)
        {
            AppendTooltipLine(text, "当前版本", installedVersion > 0 ? installedVersion.ToString() : "未检测到");
            AppendTooltipLine(text, "目标版本", targetVersion > 0 ? targetVersion.ToString() : "未提供");
        }

        private static void AppendTooltipLine(System.Text.StringBuilder text, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            text.Append('\n').Append(label).Append("：").Append(value);
        }

        private void UpdateAgentMcpConfig(AgentMcpConfigStatus status)
        {
            try
            {
                if (status.HasUPilotEntry)
                {
                    var confirmed = EditorUtility.DisplayDialog(
                        $"更新 {status.ClientName} 连接配置？",
                        "将连接地址更新为当前 UPilot 使用的地址，不影响其他服务。",
                        "更新",
                        "取消");
                    if (!confirmed)
                        return;
                }

                var result = UPilotAgentSetup.WriteAgentMcpConfig(status.ClientName, promptBeforeOverwrite: false);
                Debug.Log($"[UPilot] {status.ClientName} MCP config:\n{result}");
                RefreshAgentConfigs(force: true);
                RefreshSnapshot();
                ShowNotice(status.HasUPilotEntry ? "Agent 配置已更新" : "Agent 配置已写入");
            }
            catch (Exception ex)
            {
                ReportMainWindowException(status.ClientName + " MCP 配置更新失败", ex);
                ShowExceptionNotice(status.ClientName + " MCP 配置更新失败", ex);
            }
        }

        private void UpdateAgentRuleConfig(AgentRuleConfigStatus status)
        {
            try
            {
                if (status.State == AgentRuleConfigState.Customized)
                {
                    var choice = EditorUtility.DisplayDialogComplex(
                        $"如何处理 {status.ClientName} Agent 规则？",
                        $"当前内容经过本地修改。更新为 UPilot 最新版本会替换这些修改。",
                        "更新为最新版本",
                        "取消",
                        "保留当前内容");
                    if (choice != 0)
                        return;
                }
                else if (status.State != AgentRuleConfigState.Missing)
                {
                    var confirmed = EditorUtility.DisplayDialog(
                        $"更新 {status.ClientName} Agent 规则？",
                        $"将更新为 UPilot 提供的最新内容。",
                        "更新",
                        "取消");
                    if (!confirmed)
                        return;
                }

                var result = UPilotAgentSetup.UpdateAgentRules(status.ClientName);
                Debug.Log($"[UPilot] {status.ClientName} rules:\n{result}");
                RefreshAgentConfigs(force: true);
                ShowNotice("Agent 规则已更新");
            }
            catch (Exception ex)
            {
                ReportMainWindowException(status.ClientName + " 规则更新失败", ex);
                ShowExceptionNotice(status.ClientName + " 规则更新失败", ex);
            }
        }

        private void UpdateAgentSkillConfig(AgentSkillConfigStatus status)
        {
            if (!status.IsApplicable)
                return;

            try
            {
                var force = status.State == AgentSkillConfigState.Customized ||
                            status.State == AgentSkillConfigState.Current;
                if (status.State == AgentSkillConfigState.Customized)
                {
                    var choice = EditorUtility.DisplayDialogComplex(
                        $"如何处理 {status.ClientName} Skill？",
                        "当前 Skill 有本地修改。更新为 UPilot 最新版本会替换这些修改。",
                        "更新为最新版本",
                        "取消",
                        "保留当前内容");
                    if (choice != 0)
                        return;
                }
                else if (status.State != AgentSkillConfigState.Missing)
                {
                    var confirmed = EditorUtility.DisplayDialog(
                        $"更新 {status.ClientName} Skill？",
                        "将更新为 UPilot 提供的最新 Skill 内容。",
                        "更新",
                        "取消");
                    if (!confirmed)
                        return;
                }

                var result = UPilotAgentSetup.UpdateAgentSkill(status.ClientName, force);
                Debug.Log($"[UPilot] {status.ClientName} Skill:\n{result}");
                RefreshAgentConfigs(force: true);
                ShowNotice("Skill 已更新");
            }
            catch (Exception ex)
            {
                ReportMainWindowException(status.ClientName + " Skill 更新失败", ex);
                ShowExceptionNotice(status.ClientName + " Skill 更新失败", ex);
            }
        }

        private void UpdateAllAgentIntegrations()
        {
            UpdateAllAgentIntegrations(forceAll: false);
        }

        private void UpdateAllAgentIntegrations(bool forceAll)
        {
            try
            {
                var hasCustomizedContent = false;
                foreach (var status in _ruleConfigs)
                {
                    if (status.HasLocalCustomization)
                    {
                        hasCustomizedContent = true;
                        break;
                    }
                }

                if (!hasCustomizedContent)
                {
                    foreach (var status in _skillConfigs)
                    {
                        if (!status.HasLocalCustomization)
                            continue;
                        hasCustomizedContent = true;
                        break;
                    }
                }

                var overwriteCustomizedSkill = forceAll;
                if (forceAll)
                {
                    var confirmed = EditorUtility.DisplayDialog(
                        "强制重新配置所有 Agent？",
                        "将重新写入已配置 Agent 的 MCP 地址，并重新生成 UPilot Skill 和 Agent 规则。\n\n" +
                        "UPilot 管理范围以外的用户配置不会被修改；各 Agent Skill 中的本地修改会被替换。\n\n" +
                        "完成后，已打开的 Agent 客户端可能需要刷新工具列表。",
                        "重新配置全部",
                        "取消");
                    if (!confirmed)
                        return;
                }
                else if (hasCustomizedContent)
                {
                    var choice = EditorUtility.DisplayDialogComplex(
                        "如何处理本地修改？",
                        "检测到 UPilot Agent 规则或 Skill 有本地修改。你可以保留这些修改并处理其他配置，也可以替换为最新版本。",
                        "更新为最新版本",
                        "取消",
                        "保留本地修改");
                    if (choice == 1)
                        return;
                    overwriteCustomizedSkill = choice == 0;
                }
                else if (!EditorUtility.DisplayDialog(
                             "处理 Agent 配置？",
                             "将处理下方标记的连接和内容更新。",
                             "继续",
                             "取消"))
                {
                    return;
                }

                var result = "";
                foreach (var status in _agentConfigs)
                {
                    if (!status.HasUPilotEntry)
                        continue;
                    result += UPilotAgentSetup.WriteAgentMcpConfig(status.ClientName, promptBeforeOverwrite: false) + "\n";
                }
                result += UPilotAgentSetup.UpdateAllAgentRules() + "\n";
                result += UPilotAgentSetup.UpdateAllAgentSkills(overwriteCustomizedSkill);
                Debug.Log("[UPilot] Updated all Agent integrations:\n" + result.TrimEnd());
                RefreshAgentConfigs(force: true);
                RefreshSnapshot();
                ShowNotice(
                    forceAll
                        ? "已重新配置 MCP、Skill 和 Agent 规则；请按需刷新 Agent 工具列表"
                        : hasCustomizedContent && !overwriteCustomizedSkill
                        ? "其他配置已处理，本地修改已保留"
                        : "Agent 配置已更新");
            }
            catch (Exception ex)
            {
                ReportMainWindowException("批量更新 Agent 配置失败", ex);
                ShowExceptionNotice("批量更新 Agent 配置失败", ex);
            }
        }

        private static void DrawColoredButton(string label, Color color, float height, Action action)
        {
            var previous = GUI.backgroundColor;
            try
            {
                GUI.backgroundColor = color;
                if (GUILayout.Button(label, GUILayout.Height(height)))
                    action?.Invoke();
            }
            finally
            {
                GUI.backgroundColor = previous;
            }
        }

        private void ConfigureAndStart()
        {
            try
            {
                EnterSetupView();
            }
            catch (Exception ex)
            {
                ReportMainWindowException("进入 UPilot 设置流程失败", ex);
                ShowExceptionNotice("进入 UPilot 设置流程失败", ex);
            }
        }

        private void StartUPilot()
        {
            try
            {
                if (UPilotUpdateService.Instance.IsServiceStartBlocked)
                {
                    ShowNotice(UPilotUpdateService.ServiceStartBlockedMessage, MessageType.Warning);
                    return;
                }

                UPilotQuickStart.Start();
                _stateChangedAt = EditorApplication.timeSinceStartup;
                ShowNotice("UPilot 正在启动…");
            }
            catch (Exception ex)
            {
                ReportMainWindowException("启动 UPilot 失败", ex);
                ShowExceptionNotice("启动 UPilot 失败", ex);
            }
        }

        private async void RepairUPilot()
        {
            if (_repairInProgress)
                return;

            _repairInProgress = true;
            Repaint();
            try
            {
                if (UPilotUpdateService.Instance.IsServiceStartBlocked)
                {
                    ShowNotice(UPilotUpdateService.ServiceStartBlockedMessage, MessageType.Warning);
                    return;
                }

                var message = await UPilotQuickStart.AutoRepairAsync(_agentConfigs);
                if (this == null)
                    return;
                RefreshAgentConfigs(force: true);
                RefreshSnapshot();
                _stateChangedAt = EditorApplication.timeSinceStartup;
                ShowNotice(message);
            }
            catch (Exception ex)
            {
                ReportMainWindowException("自动修复 UPilot 失败", ex);
                ShowExceptionNotice("自动修复 UPilot 失败", ex);
            }
            finally
            {
                _repairInProgress = false;
                if (this != null)
                    Repaint();
            }
        }

        private void DrawAdvancedEntry()
        {
            EditorGUILayout.Space(12);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("高级设置", GUILayout.Width(104), GUILayout.Height(24)))
                    UPilotStatusWindow.Open();
                GUILayout.FlexibleSpace();
            }
        }

        private void ShowMoreMenu(UPilotMainSnapshot snapshot)
        {
            var menu = new GenericMenu();
            var transitioning = IsServiceTransitioning(snapshot.State);
            if (UPilotSetupState.IsCompleted && !transitioning)
                menu.AddItem(new GUIContent("重新启动"), false, () =>
                {
                    RequestRestart();
                });
            else
                menu.AddDisabledItem(new GUIContent("重新启动"));

            menu.AddSeparator("");
            menu.AddItem(new GUIContent("更新中心…"), false, () =>
            {
                UPilotUpdateService.Instance.CheckForUpdates(ShowNotice);
            });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("高级设置"), false, UPilotStatusWindow.Open);
            menu.ShowAsContext();
        }

        private void DrawNotice(UPilotMainState state)
        {
            if (string.IsNullOrEmpty(_notice)) return;
            if (EditorApplication.timeSinceStartup > _noticeUntil)
            {
                _notice = "";
                return;
            }

            if (state == UPilotMainState.Updating && _noticeType != MessageType.Error)
                return;

            EditorGUILayout.HelpBox(_notice, _noticeType);
        }

        private void ShowNotice(string message)
        {
            ShowNotice(message, MessageType.Info);
        }

        private void ShowNotice(string message, MessageType type)
        {
            ShowNoticeForDuration(message, type, 3.5d);
        }

        private void ShowNoticeForDuration(
            string message,
            MessageType type,
            double durationSeconds)
        {
            _notice = message;
            _noticeType = type;
            _noticeUntil = EditorApplication.timeSinceStartup + durationSeconds;
        }

        private void ShowExceptionNotice(string context, Exception ex)
        {
            try
            {
                ShowNoticeForDuration(context + "：" + ex.Message, MessageType.Error, 8d);
            }
            catch (Exception noticeEx)
            {
                Debug.LogError("[UPilot] 主界面错误提示失败：" + noticeEx.Message + "\n" + noticeEx);
            }
        }

        private static void ReportMainWindowException(string context, Exception ex)
        {
            Debug.LogError("[UPilot] " + context + "：" + ex.Message + "\n" + ex);
        }

        private static void DrawExceptionFallback(string context, Exception ex)
        {
            try
            {
                EditorGUILayout.HelpBox(context + "：" + ex.Message, MessageType.Error);
            }
            catch (Exception fallbackEx)
            {
                Debug.LogError("[UPilot] 主界面错误提示绘制失败：" + fallbackEx.Message + "\n" + fallbackEx);
            }
        }

        private static Color GetStateColor(UPilotMainState state)
        {
            if (state == UPilotMainState.Ready) return Color.green;
            if (state == UPilotMainState.CheckingStatus ||
                state == UPilotMainState.Starting ||
                state == UPilotMainState.Restarting ||
                state == UPilotMainState.Stopping ||
                state == UPilotMainState.Updating)
                return new Color(1f, 0.65f, 0.1f);
            if (state == UPilotMainState.NeedsRepair) return new Color(1f, 0.35f, 0.2f);
            return Color.gray;
        }

        private static string GetStateLabel(UPilotMainState state)
        {
            if (state == UPilotMainState.Ready) return "已就绪";
            if (state == UPilotMainState.CheckingStatus) return "获取状态中";
            if (state == UPilotMainState.Starting) return "启动中";
            if (state == UPilotMainState.Restarting) return "重启中";
            if (state == UPilotMainState.Stopping) return "停止中";
            if (state == UPilotMainState.Updating) return "更新中";
            if (state == UPilotMainState.NeedsRepair) return "需修复";
            if (state == UPilotMainState.SetupRequired) return "待配置";
            return "已停止";
        }
    }
}
