// -----------------------------------------------------------------------
// UPilot Editor - simple user-facing entry window.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
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
        private double _lastUpdateStopAttempt;
        private Vector2 _mainScroll;

        private GUIStyle _cardStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _messageStyle;
        private GUIStyle _sectionTitleStyle;
        private GUIStyle _tableHeaderStyle;
        private GUIStyle _infoLabelStyle;
        private GUIStyle _infoValueStyle;
        private bool _stylesInitialized;

        private const double UpdateStopRetryCooldownSeconds = 2d;

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
                DrawNotice();
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
            _tableHeaderStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                fixedHeight = 22,
                normal =
                {
                    textColor = EditorGUIUtility.isProSkin
                        ? new Color(0.58f, 0.58f, 0.58f)
                        : new Color(0.38f, 0.38f, 0.38f),
                },
            };
            _infoLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fixedHeight = 15,
                normal =
                {
                    textColor = EditorGUIUtility.isProSkin
                        ? new Color(0.58f, 0.58f, 0.58f)
                        : new Color(0.38f, 0.38f, 0.38f),
                },
            };
            _infoValueStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                fixedHeight = 18,
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
            }
            _snapshot = next;
        }

        private UPilotMainSnapshot GetDisplaySnapshot()
        {
            var updateStatus = UPilotUpdateService.Instance.GetOperationStatus();
            if (updateStatus.IsRunning)
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

                var targetText = BuildUpdateTargetText(status);
                if (!string.IsNullOrWhiteSpace(targetText))
                    EditorGUILayout.LabelField(targetText, EditorStyles.miniLabel);

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
            DrawVersionSection(snapshot);
            EditorGUILayout.Space(8);
            DrawSectionTitleBar("MCP 连接");
            EditorGUILayout.Space(4);
            DrawMcpEndpoint();
            EditorGUILayout.Space(10);
            using (new EditorGUI.DisabledScope(IsServiceTransitioning(snapshot.State)))
                DrawAgentConfigurationList();
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

            if (snapshot.State != UPilotMainState.SetupRequired)
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

        private void DrawVersionSection(UPilotMainSnapshot snapshot)
        {
            var serverVersion = string.IsNullOrEmpty(_mcpStatus.ServerVersion) ? "未启动/旧版" : _mcpStatus.ServerVersion;
            var runtimeMode = GetRuntimeModeLabel(snapshot.State);
            var channel = string.IsNullOrEmpty(_mcpStatus.BuildChannel) ? "release" : _mcpStatus.BuildChannel;
            var compatibility = UPilotServerRuntimeService.IsSourceChannel(channel)
                ? "source / 本机 Python"
                : "release 清单兼容";
            var writeApproved = UPilotProjectConfig.Current.safety?.writeAccessApproved == true;

            DrawSectionTitleBar("运行信息");
            EditorGUILayout.Space(4);
            if (position.width >= 620f)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawInfoColumn("UPM", UPilotServerRuntimeService.UpmVersion, 82);
                    DrawInfoColumn("服务", serverVersion, 100);
                    DrawInfoColumn("运行方式", runtimeMode, 130);
                    DrawInfoColumn(new GUIContent("通道", compatibility), channel, 92);
                    DrawInfoColumn("授权", writeApproved ? "已允许" : "Safe", 76);
                    GUILayout.FlexibleSpace();
                }
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawInfoColumn("UPM", UPilotServerRuntimeService.UpmVersion, 86);
                    DrawInfoColumn("服务", serverVersion, 112);
                    DrawInfoColumn("运行方式", runtimeMode, 145);
                    GUILayout.FlexibleSpace();
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawInfoColumn(new GUIContent("通道", compatibility), channel, 112);
                    DrawInfoColumn("授权", writeApproved ? "已允许" : "Safe", 90);
                    GUILayout.FlexibleSpace();
                }
            }

            if (writeApproved)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    DrawRevokeWriteAccessButton(writeApproved);
                }
            }
        }

        private static string GetRuntimeModeLabel(UPilotMainState state)
        {
            if (state == UPilotMainState.Updating)
                return "正在更新";
            if (state == UPilotMainState.CheckingStatus)
                return "获取状态中";
            return UPilotServerRuntimeService.Instance.RuntimeModeLabel;
        }

        private void DrawInfoColumn(string label, string value, float width)
        {
            DrawInfoColumn(new GUIContent(label), value, width);
        }

        private void DrawInfoColumn(GUIContent label, string value, float width)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(width)))
            {
                EditorGUILayout.LabelField(label, _infoLabelStyle, GUILayout.Width(width));
                EditorGUILayout.LabelField(value, _infoValueStyle, GUILayout.Width(width));
            }
        }

        private void DrawRevokeWriteAccessButton(bool writeApproved)
        {
            if (!writeApproved)
                return;

            if (GUILayout.Button("撤销授权", GUILayout.Width(76), GUILayout.Height(22)) &&
                EditorUtility.DisplayDialog(
                    "撤销写入授权？",
                    "撤销后，MCP 将回到 safe 模式并拒绝修改项目的工具。此操作会写入 .upilot/config.json。",
                    "撤销",
                    "取消"))
            {
                UPilotProjectConfig.RevokeProjectWriteAccess();
                ShowNotice("已撤销写入授权");
            }
        }

        private void DrawAgentConfigurationList()
        {
            var toolbarRect = EditorGUILayout.GetControlRect(false, 30f);
            const float checkWidth = 82f;
            const float updateWidth = 96f;
            const float gap = 6f;
            DrawBandBackground(toolbarRect);
            DrawBandAccent(toolbarRect);
            var updateRect = new Rect(toolbarRect.xMax - 6f - updateWidth, toolbarRect.y + 3f, updateWidth, 24f);
            var checkRect = new Rect(updateRect.x - gap - checkWidth, toolbarRect.y + 3f, checkWidth, 24f);
            var titleRect = new Rect(toolbarRect.x + 10f, toolbarRect.y + 3f, Mathf.Max(70f, checkRect.x - toolbarRect.x - 16f), 24f);

            EditorGUI.LabelField(titleRect, "Agent 配置", _sectionTitleStyle);
            if (GUI.Button(checkRect, "检查配置"))
            {
                RefreshAgentConfigs(force: true);
                var checkedIssueCount = CountAgentIntegrationIssues();
                if (checkedIssueCount == 0)
                {
                    ShowNotice("检查完成，所有 Agent 配置已是最新");
                }
                else
                {
                    var confirmationCount = CountCustomizedRuleConfigs();
                    ShowNotice(
                        confirmationCount == checkedIssueCount
                            ? $"检查完成，有 {checkedIssueCount} 项内容需要确认"
                            : $"检查完成，有 {checkedIssueCount} 项配置需要处理",
                        MessageType.Warning);
                }
            }

            var issueCount = CountAgentIntegrationIssues();
            var updateLabel = issueCount > 0 ? $"处理 {issueCount} 项" : "更新全部";
            if (GUI.Button(updateRect, updateLabel))
                UpdateAllAgentIntegrations();

            DrawAgentIntegrationAdvice();
            DrawAgentTableHeader();

            foreach (var mcpStatus in _agentConfigs)
                DrawAgentConfigurationRow(mcpStatus, FindRuleStatus(mcpStatus.ClientName));
        }

        private void DrawAgentIntegrationAdvice()
        {
            var messageType = BuildAgentIntegrationAdvice(out var message);
            if (messageType == MessageType.Info)
            {
                EditorGUILayout.LabelField("所有 Agent 配置已是最新", _messageStyle);
                return;
            }

            EditorGUILayout.HelpBox(message, messageType);
        }

        private void DrawAgentTableHeader()
        {
            EditorGUILayout.Space(2);
            var row = EditorGUILayout.GetControlRect(false, 22f);
            GetAgentColumnRects(row, 72f, out var agentRect, out var mcpRect, out var ruleRect, out var actionRect);
            EditorGUI.LabelField(agentRect, "Agent", _tableHeaderStyle);
            EditorGUI.LabelField(mcpRect, "MCP 状态", _tableHeaderStyle);
            EditorGUI.LabelField(ruleRect, "Skill / 规则", _tableHeaderStyle);
            EditorGUI.LabelField(actionRect, "操作", _tableHeaderStyle);
            DrawTableSeparator();
        }

        private MessageType BuildAgentIntegrationAdvice(out string message)
        {
            AgentMcpConfigStatus? firstMcpIssue = null;
            AgentRuleConfigStatus? firstRuleIssue = null;
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

            if (issueCount == 0)
            {
                message = "所有 Agent 配置已是最新";
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
                              $"检测到 {status.ClientName} 的 UPilot {GetRuleLabel(status.ClientName)} 有本地修改。" +
                              "当前仍可正常使用，更新时可以选择保留或替换。";
                    return MessageType.Warning;
                }

                if (status.State == AgentRuleConfigState.Missing)
                {
                    message = $"{status.ClientName} 尚未安装 UPilot {GetRuleLabel(status.ClientName)}\n" +
                              "完成安装后即可使用最新的 Agent 配置。";
                    return MessageType.Warning;
                }

                message = $"{status.ClientName} 有新内容可用\n更新后可使用最新的 UPilot {GetRuleLabel(status.ClientName)}。";
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

        private void DrawAgentConfigurationRow(
            AgentMcpConfigStatus mcpStatus,
            AgentRuleConfigStatus ruleStatus)
        {
            var expandedActions = position.width >= 760f;
            var actionWidth = expandedActions ? 166f : 72f;
            var row = EditorGUILayout.GetControlRect(false, 32f);
            GetAgentColumnRects(row, actionWidth, out var agentRect, out var mcpRect, out var ruleRect, out var actionRect);

            EditorGUI.LabelField(agentRect, mcpStatus.ClientName, EditorStyles.boldLabel);
            DrawStatusCell(mcpRect, GetCompactMcpState(mcpStatus), mcpStatus.IsConfigured);
            DrawStatusCell(ruleRect, GetRuleStateText(ruleStatus), ruleStatus.State == AgentRuleConfigState.Current);

            if (expandedActions)
            {
                var configRect = new Rect(actionRect.x, actionRect.y + 4f, 76f, 24f);
                var ruleButtonRect = new Rect(configRect.xMax + 6f, configRect.y, 84f, 24f);
                var configLabel = mcpStatus.HasUPilotEntry ? "更新配置" : "配置";
                if (GUI.Button(configRect, configLabel))
                    UpdateAgentMcpConfig(mcpStatus);

                var ruleLabel = mcpStatus.ClientName == "Codex" ? "更新 Skill" : "更新规则";
                if (GUI.Button(ruleButtonRect, ruleLabel))
                    UpdateAgentRuleConfig(ruleStatus);
            }
            else
            {
                var buttonRect = new Rect(actionRect.x, actionRect.y + 4f, actionRect.width, 24f);
                if (GUI.Button(buttonRect, "管理 ▾"))
                    ShowAgentConfigurationMenu(mcpStatus, ruleStatus);
            }

            DrawTableSeparator();
        }

        private static void GetAgentColumnRects(
            Rect row,
            float actionWidth,
            out Rect agentRect,
            out Rect mcpRect,
            out Rect ruleRect,
            out Rect actionRect)
        {
            const float gap = 6f;
            var contentWidth = Mathf.Max(240f, row.width - actionWidth - gap * 3f);
            var agentWidth = Mathf.Max(84f, contentWidth * 0.27f);
            var mcpWidth = Mathf.Max(88f, contentWidth * 0.27f);
            var ruleWidth = Mathf.Max(100f, contentWidth - agentWidth - mcpWidth);

            agentRect = new Rect(row.x, row.y, agentWidth, row.height);
            mcpRect = new Rect(agentRect.xMax + gap, row.y, mcpWidth, row.height);
            ruleRect = new Rect(mcpRect.xMax + gap, row.y, ruleWidth, row.height);
            actionRect = new Rect(row.xMax - actionWidth, row.y, actionWidth, row.height);
        }

        private static void DrawStatusCell(Rect rect, string value, bool ready)
        {
            var dotRect = new Rect(rect.x, rect.y, 14f, rect.height);
            var labelRect = new Rect(dotRect.xMax, rect.y, Mathf.Max(0f, rect.width - dotRect.width), rect.height);
            var previous = GUI.color;
            GUI.color = ready ? new Color(0.25f, 0.82f, 0.38f) : new Color(1f, 0.65f, 0.15f);
            EditorGUI.LabelField(dotRect, "●");
            GUI.color = previous;
            EditorGUI.LabelField(labelRect, value);
        }

        private void ShowAgentConfigurationMenu(
            AgentMcpConfigStatus mcpStatus,
            AgentRuleConfigStatus ruleStatus)
        {
            var menu = new GenericMenu();
            var configLabel = mcpStatus.HasUPilotEntry ? "更新 MCP 配置" : "写入 MCP 配置";
            menu.AddItem(new GUIContent(configLabel), false, () => UpdateAgentMcpConfig(mcpStatus));

            var ruleLabel = mcpStatus.ClientName == "Codex" ? "更新 Skill" : "更新规则";
            menu.AddItem(new GUIContent(ruleLabel), false, () => UpdateAgentRuleConfig(ruleStatus));
            menu.ShowAsContext();
        }

        private static void DrawTableSeparator()
        {
            var rect = EditorGUILayout.GetControlRect(false, 1f);
            var color = EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 0.08f)
                : new Color(0f, 0f, 0f, 0.12f);
            EditorGUI.DrawRect(rect, color);
        }

        private void DrawSectionTitleBar(string title)
        {
            var rect = EditorGUILayout.GetControlRect(false, 26f);
            DrawBandBackground(rect);
            DrawBandAccent(rect);
            var labelRect = new Rect(rect.x + 10f, rect.y + 2f, rect.width - 16f, 22f);
            EditorGUI.LabelField(labelRect, title, _sectionTitleStyle);
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

        private static string GetRuleLabel(string clientName)
        {
            return clientName == "Codex" ? "Skill" : "规则";
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
                var force = status.State == AgentRuleConfigState.Customized ||
                            status.State == AgentRuleConfigState.Current;
                if (status.State == AgentRuleConfigState.Customized)
                {
                    var choice = EditorUtility.DisplayDialogComplex(
                        $"如何处理 {status.ClientName} {GetRuleLabel(status.ClientName)}？",
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
                        $"更新 {status.ClientName} {GetRuleLabel(status.ClientName)}？",
                        $"将更新为 UPilot 提供的最新内容。",
                        "更新",
                        "取消");
                    if (!confirmed)
                        return;
                }

                var result = UPilotAgentSetup.UpdateAgentRules(status.ClientName, force);
                Debug.Log($"[UPilot] {status.ClientName} rules:\n{result}");
                RefreshAgentConfigs(force: true);
                ShowNotice(status.ClientName == "Codex" ? "Skill 已更新" : "规则已更新");
            }
            catch (Exception ex)
            {
                ReportMainWindowException(status.ClientName + " 规则更新失败", ex);
                ShowExceptionNotice(status.ClientName + " 规则更新失败", ex);
            }
        }

        private void UpdateAllAgentIntegrations()
        {
            try
            {
                var hasCustomizedRules = false;
                foreach (var status in _ruleConfigs)
                {
                    if (status.HasLocalCustomization)
                    {
                        hasCustomizedRules = true;
                        break;
                    }
                }

                var overwriteCustomizedRules = true;
                if (hasCustomizedRules)
                {
                    var choice = EditorUtility.DisplayDialogComplex(
                        "如何处理本地修改？",
                        "检测到 Codex 的 UPilot Skill 有本地修改。你可以保留这些修改并处理其他配置，也可以替换为最新版本。",
                        "更新为最新版本",
                        "取消",
                        "保留本地修改");
                    if (choice == 1)
                        return;
                    overwriteCustomizedRules = choice == 0;
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
                result += UPilotAgentSetup.UpdateAllAgentRules(overwriteCustomizedRules);
                Debug.Log("[UPilot] Updated all Agent integrations:\n" + result.TrimEnd());
                RefreshAgentConfigs(force: true);
                RefreshSnapshot();
                ShowNotice(
                    hasCustomizedRules && !overwriteCustomizedRules
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

        private void RepairUPilot()
        {
            try
            {
                if (UPilotUpdateService.Instance.IsServiceStartBlocked)
                {
                    ShowNotice(UPilotUpdateService.ServiceStartBlockedMessage, MessageType.Warning);
                    return;
                }

                var message = UPilotQuickStart.AutoRepair(_bridgeStatus, _mcpStatus, _agentConfigs);
                RefreshAgentConfigs(force: true);
                _stateChangedAt = EditorApplication.timeSinceStartup;
                ShowNotice(message);
            }
            catch (Exception ex)
            {
                ReportMainWindowException("自动修复 UPilot 失败", ex);
                ShowExceptionNotice("自动修复 UPilot 失败", ex);
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

        private void DrawNotice()
        {
            if (string.IsNullOrEmpty(_notice)) return;
            if (EditorApplication.timeSinceStartup > _noticeUntil)
            {
                _notice = "";
                return;
            }
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
