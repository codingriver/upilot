// -----------------------------------------------------------------------
// UPilot Editor - simple user-facing entry window.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public sealed class UPilotMainWindow : EditorWindow
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

        private bool _useCodex = true;
        private bool _useClaudeCode;
        private bool _useCursor;
        private bool _selectionInitialized;
        private bool _agentAdviceNoticeShown;

        private string _notice = "";
        private MessageType _noticeType = MessageType.Info;
        private double _noticeUntil;
        private bool _restartRequested;
        private Vector2 _mainScroll;

        private GUIStyle _cardStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _messageStyle;
        private GUIStyle _sectionTitleStyle;
        private GUIStyle _tableHeaderStyle;
        private GUIStyle _infoLabelStyle;
        private GUIStyle _infoValueStyle;
        private bool _stylesInitialized;

        [MenuItem("UPilot/UPilot", false, 200)]
        public static void Open()
        {
            var window = GetWindow<UPilotMainWindow>("UPilot");
            window.minSize = new Vector2(440, 400);
            window.Show();
        }

        private void OnEnable()
        {
            minSize = new Vector2(440, 400);
            RefreshAgentConfigs(force: true);
            RefreshSnapshot();
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            if (EditorApplication.timeSinceStartup - _lastRepaint < 0.4d)
                return;

            _lastRepaint = EditorApplication.timeSinceStartup;
            Repaint();
        }

        private void OnGUI()
        {
            InitializeStyles();
            if (Event.current.type == EventType.Layout)
            {
                RefreshAgentConfigs(force: false);
                RefreshSnapshot();
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
            return _snapshot;
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

            if (_selectionInitialized)
                return;

            _selectionInitialized = true;
            _useCodex = ShouldSelectClient("Codex");
            _useClaudeCode = ShouldSelectClient("Claude Code");
            _useCursor = ShouldSelectClient("Cursor");
            if (!_useCodex && !_useClaudeCode && !_useCursor)
                _useCodex = true;
        }

        private bool ShouldSelectClient(string clientName)
        {
            foreach (var config in _agentConfigs)
            {
                if (config.ClientName == clientName &&
                    (config.IsConfigured || config.HasUPilotEntry))
                    return true;
            }
            return false;
        }

        private void DrawMainCard(UPilotMainSnapshot snapshot)
        {
            using (new EditorGUILayout.VerticalScope(_cardStyle))
            {
                DrawStatusActionBar(snapshot);

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

        private void DrawSetupControls()
        {
            EditorGUILayout.LabelField("你使用哪个 Agent？", EditorStyles.centeredGreyMiniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _useCodex = GUILayout.Toggle(_useCodex, "Codex", EditorStyles.miniButtonLeft);
                _useClaudeCode = GUILayout.Toggle(_useClaudeCode, "Claude", EditorStyles.miniButtonMid);
                _useCursor = GUILayout.Toggle(_useCursor, "Cursor", EditorStyles.miniButtonRight);
            }
            EditorGUILayout.Space(5);
            DrawPrimaryButton("配置并启动", ConfigureAndStart);
        }

        private void DrawOperationsDashboard(UPilotMainSnapshot snapshot)
        {
            DrawVersionSection();
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
            var actionRect = new Rect(menuRect.x - buttonGap - 82f, rect.y + 6f, 82f, 24f);
            var titleRect = new Rect(rect.x + horizontalPadding, rect.y + 6f, 58f, 24f);
            var dotRect = new Rect(titleRect.xMax, titleRect.y, 14f, titleRect.height);
            var stateRect = new Rect(dotRect.xMax, titleRect.y, 62f, titleRect.height);

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

            if (snapshot.State == UPilotMainState.Starting ||
                snapshot.State == UPilotMainState.Restarting ||
                snapshot.State == UPilotMainState.Stopping)
            {
                var label = snapshot.State == UPilotMainState.Restarting
                    ? "重启中…"
                    : snapshot.State == UPilotMainState.Stopping
                        ? "停止中…"
                        : "启动中…";
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
            _restartRequested = true;
            UPilotQuickStart.Restart();
            _stateChangedAt = EditorApplication.timeSinceStartup;
            RefreshSnapshot();
        }

        private static bool IsServiceTransitioning(UPilotMainState state)
        {
            return state == UPilotMainState.Starting ||
                   state == UPilotMainState.Restarting ||
                   state == UPilotMainState.Stopping;
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

        private void DrawVersionSection()
        {
            var serverVersion = string.IsNullOrEmpty(_mcpStatus.ServerVersion) ? "未启动/旧版" : _mcpStatus.ServerVersion;
            var runtimeMode = string.IsNullOrEmpty(_mcpStatus.RuntimeMode)
                ? UPilotServerRuntimeService.Instance.RuntimeModeLabel
                : _mcpStatus.RuntimeMode;
            var channel = string.IsNullOrEmpty(_mcpStatus.BuildChannel) ? "release" : _mcpStatus.BuildChannel;
            var compatibility = channel.IndexOf("main", StringComparison.OrdinalIgnoreCase) >= 0
                ? "main protocol/registry"
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
                var issueCount = CountAgentIntegrationIssues();
                if (issueCount == 0)
                {
                    ShowNotice("检查完成，所有 Agent 配置已是最新");
                }
                else
                {
                    ShowNotice($"检查完成，有{issueCount}项配置需要更新", MessageType.Warning);
                }
            }

            var updateLabel = HasAgentIntegrationIssues() ? "更新建议项" : "更新全部";
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
            var mcpIssues = 0;
            var ruleIssues = 0;
            var hasErrors = false;
            var hasCustomizedRules = false;
            var details = new StringBuilder();

            foreach (var status in _agentConfigs)
            {
                if (!NeedsMcpUpdate(status))
                    continue;

                mcpIssues++;
                if (!string.IsNullOrEmpty(status.ErrorMessage))
                    hasErrors = true;
                AppendAdviceDetail(details, $"{status.ClientName} MCP：{status.StateText}");
            }

            foreach (var status in _ruleConfigs)
            {
                if (!NeedsRuleUpdate(status))
                    continue;

                ruleIssues++;
                if (status.State == AgentRuleConfigState.Error)
                    hasErrors = true;
                if (status.HasLocalCustomization)
                    hasCustomizedRules = true;
                AppendAdviceDetail(details, $"{status.ClientName} {GetRuleLabel(status.ClientName)}：{status.StateText}");
            }

            if (mcpIssues == 0 && ruleIssues == 0)
            {
                message = "打开 UPilot 时已完成 Agent/MCP/Skill 一致性检查：当前配置已同步。";
                return MessageType.Info;
            }

            var summary = $"打开 UPilot 时已完成一致性检查：{mcpIssues} 个 MCP 配置、{ruleIssues} 个 Skill/规则需要处理。";
            var action = hasCustomizedRules
                ? "建议先确认本地修改来源，再点击“更新建议项”；UPilot 会在覆盖管理内容前二次确认。"
                : "建议点击“更新建议项”同步 MCP 地址、Agent 规则和 Codex Skill。";
            message = summary + "\n" + action + "\n" + details.ToString().TrimEnd();
            return hasErrors ? MessageType.Error : MessageType.Warning;
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

        private static void AppendAdviceDetail(StringBuilder builder, string detail)
        {
            if (builder.Length > 0)
                builder.AppendLine();
            builder.Append("- ").Append(detail);
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
            DrawStatusCell(ruleRect, ruleStatus.StateText, ruleStatus.State == AgentRuleConfigState.Current);

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
            if (status.HasUPilotEntry && !status.UsesCurrentUrl) return "需更新";
            if (!string.IsNullOrEmpty(status.ErrorMessage)) return "异常";
            return "未配置";
        }

        private static string GetRuleLabel(string clientName)
        {
            return clientName == "Codex" ? "Skill" : "规则";
        }

        private void UpdateAgentMcpConfig(AgentMcpConfigStatus status)
        {
            if (status.HasUPilotEntry)
            {
                var confirmed = EditorUtility.DisplayDialog(
                    "强制更新 Agent 配置？",
                    $"将更新 {status.ClientName} 的 UPilot MCP 配置项，不影响其他 MCP 服务。",
                    "强制更新",
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

        private void UpdateAgentRuleConfig(AgentRuleConfigStatus status)
        {
            var force = status.State == AgentRuleConfigState.Customized ||
                        status.State == AgentRuleConfigState.Current;
            if (status.State != AgentRuleConfigState.Missing)
            {
                var message = status.State == AgentRuleConfigState.Customized
                    ? $"{status.ClientName} 的 UPilot Skill/规则包含本地修改。强制更新会覆盖 UPilot 管理的内容，是否继续？"
                    : $"是否更新 {status.ClientName} 的 UPilot Skill/规则？";
                var confirmed = EditorUtility.DisplayDialog(
                    force ? "强制更新 Skill/规则？" : "更新 Skill/规则？",
                    message,
                    force ? "强制更新" : "更新",
                    "取消");
                if (!confirmed)
                    return;
            }

            var result = UPilotAgentSetup.UpdateAgentRules(status.ClientName, force);
            Debug.Log($"[UPilot] {status.ClientName} rules:\n{result}");
            RefreshAgentConfigs(force: true);
            ShowNotice(status.ClientName == "Codex" ? "Skill 已更新" : "规则已更新");
        }

        private void UpdateAllAgentIntegrations()
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

            var message = hasCustomizedRules
                ? "检测到本地修改。将更新已有的 UPilot MCP 连接条目，重新同步全部 UPilot Skill/AGENT规则，并覆盖 UPilot 管理的内容。"
                : "将更新已有的 UPilot MCP 连接条目，重新同步全部 UPilot Skill/AGENT规则。";
            var confirmed = EditorUtility.DisplayDialog(
                "更新全部 UPilot 配置？",
                message,
                "确认更新",
                "取消");
            if (!confirmed)
                return;

            var result = "";
            foreach (var status in _agentConfigs)
            {
                if (!status.HasUPilotEntry)
                    continue;
                result += UPilotAgentSetup.WriteAgentMcpConfig(status.ClientName, promptBeforeOverwrite: false) + "\n";
            }
            result += UPilotAgentSetup.UpdateAllAgentRules(forceCodexSkillOverwrite: true);
            Debug.Log("[UPilot] Updated all Agent integrations:\n" + result.TrimEnd());
            RefreshAgentConfigs(force: true);
            RefreshSnapshot();
            ShowNotice("全部已配置项已更新");
        }

        private static void DrawPrimaryButton(string label, Action action)
        {
            if (GUILayout.Button(label, GUILayout.Height(24)))
                action();
        }

        private void ConfigureAndStart()
        {
            UPilotFirstSetupWindow.Open();
        }

        private void StartUPilot()
        {
            UPilotQuickStart.Start();
            _stateChangedAt = EditorApplication.timeSinceStartup;
            ShowNotice("UPilot 正在启动…");
        }

        private void RepairUPilot()
        {
            var message = UPilotQuickStart.AutoRepair(_bridgeStatus, _mcpStatus, _agentConfigs);
            RefreshAgentConfigs(force: true);
            _stateChangedAt = EditorApplication.timeSinceStartup;
            ShowNotice(message);
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

        private void ShowNotice(string message, MessageType type = MessageType.Info)
        {
            _notice = message;
            _noticeType = type;
            _noticeUntil = EditorApplication.timeSinceStartup + 3.5d;
        }

        private static Color GetStateColor(UPilotMainState state)
        {
            if (state == UPilotMainState.Ready) return Color.green;
            if (state == UPilotMainState.Starting ||
                state == UPilotMainState.Restarting ||
                state == UPilotMainState.Stopping)
                return new Color(1f, 0.65f, 0.1f);
            if (state == UPilotMainState.NeedsRepair) return new Color(1f, 0.35f, 0.2f);
            return Color.gray;
        }

        private static string GetStateLabel(UPilotMainState state)
        {
            if (state == UPilotMainState.Ready) return "已就绪";
            if (state == UPilotMainState.Starting) return "启动中";
            if (state == UPilotMainState.Restarting) return "重启中";
            if (state == UPilotMainState.Stopping) return "停止中";
            if (state == UPilotMainState.NeedsRepair) return "需修复";
            if (state == UPilotMainState.SetupRequired) return "待配置";
            return "已停止";
        }
    }
}
