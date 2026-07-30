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
        private bool _stylesInitialized;

        [MenuItem("UPilot/UPilot", false, 200)]
        public static void Open()
        {
            var window = GetWindow<UPilotMainWindow>("UPilot");
            window.minSize = new Vector2(360, 380);
            window.Show();
        }

        private void OnEnable()
        {
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
            DrawHeader(displaySnapshot);
            _mainScroll = EditorGUILayout.BeginScrollView(
                _mainScroll,
                false,
                true,
                GUIStyle.none,
                GUI.skin.verticalScrollbar,
                GUI.skin.scrollView);
            try
            {
                DrawNotice();
                EditorGUILayout.Space(4);
                DrawMainCard(displaySnapshot);
                EditorGUILayout.Space(6);
                DrawAdvancedEntry();
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
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(6, 6, 1, 1),
            };
            _titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 13,
                wordWrap = false,
            };
            _messageStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false,
                normal =
                {
                    textColor = EditorGUIUtility.isProSkin
                        ? new Color(0.76f, 0.76f, 0.76f)
                        : new Color(0.25f, 0.25f, 0.25f),
                },
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

        private void DrawHeader(UPilotMainSnapshot snapshot)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("UPilot", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();

                var previous = GUI.color;
                GUI.color = GetStateColor(snapshot.State);
                GUILayout.Label("●", GUILayout.Width(18));
                GUI.color = previous;
                GUILayout.Label(GetStateLabel(snapshot.State), EditorStyles.miniLabel, GUILayout.Width(62));

                if (GUILayout.Button("⋮", EditorStyles.miniButton, GUILayout.Width(24)))
                    ShowMoreMenu(snapshot);
            }
        }

        private void DrawMainCard(UPilotMainSnapshot snapshot)
        {
            using (new EditorGUILayout.VerticalScope(_cardStyle))
            {
                DrawCompactStatusSummary(snapshot);
                EditorGUILayout.Space(5);

                if (snapshot.State == UPilotMainState.SetupRequired)
                    DrawSetupControls();
                else
                    DrawOperationsDashboard(snapshot);
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
            DrawServiceControls(snapshot);
            EditorGUILayout.Space(6);
            DrawVersionSection();
            EditorGUILayout.Space(6);
            DrawMcpEndpoint();
            EditorGUILayout.Space(6);
            using (new EditorGUI.DisabledScope(IsServiceTransitioning(snapshot.State)))
                DrawAgentConfigurationList();
        }

        private void DrawCompactStatusSummary(UPilotMainSnapshot snapshot)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var previous = GUI.color;
                GUI.color = GetStateColor(snapshot.State);
                GUILayout.Label("●", GUILayout.Width(14));
                GUI.color = previous;

                EditorGUILayout.LabelField(snapshot.Title, _titleStyle, GUILayout.Width(58));
                EditorGUILayout.LabelField(snapshot.Message, _messageStyle, GUILayout.MinWidth(0));
            }
        }

        private void DrawServiceControls(UPilotMainSnapshot snapshot)
        {
            if (snapshot.State == UPilotMainState.Stopped)
            {
                DrawPrimaryButton("启动 UPilot", StartUPilot);
                return;
            }

            if (snapshot.State == UPilotMainState.Starting ||
                snapshot.State == UPilotMainState.Restarting ||
                snapshot.State == UPilotMainState.Stopping)
            {
                var label = snapshot.State == UPilotMainState.Restarting
                    ? "正在重启…"
                    : snapshot.State == UPilotMainState.Stopping
                        ? "正在停止…"
                        : "正在启动…";
                using (new EditorGUI.DisabledScope(true))
                    GUILayout.Button(label, GUILayout.Height(24));
                return;
            }

            var primaryLabel = snapshot.State == UPilotMainState.Ready ? "重启 UPilot" : "自动修复";
            if (GUILayout.Button(primaryLabel, GUILayout.Height(24)))
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

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("MCP 地址", EditorStyles.miniBoldLabel, GUILayout.Width(58));
                EditorGUILayout.SelectableLabel(mcpUrl, EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button("复制", EditorStyles.miniButton, GUILayout.Width(44)))
                {
                    EditorGUIUtility.systemCopyBuffer = mcpUrl;
                    ShowNotice("已复制 MCP 地址");
                }
            }
        }

        private void DrawVersionSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var serverVersion = string.IsNullOrEmpty(_mcpStatus.ServerVersion) ? "未启动/旧版" : _mcpStatus.ServerVersion;
                var runtimeMode = string.IsNullOrEmpty(_mcpStatus.RuntimeMode) ? UPilotServerRuntimeService.Instance.RuntimeModeLabel : _mcpStatus.RuntimeMode;
                var channel = string.IsNullOrEmpty(_mcpStatus.BuildChannel) ? "release" : _mcpStatus.BuildChannel;
                var compatibility = channel.IndexOf("main", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "main protocol/registry"
                    : "release 清单兼容";

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("版本与运行时", EditorStyles.miniBoldLabel, GUILayout.Width(72));
                    DrawInlineInfo("UPM", UPilotServerRuntimeService.UpmVersion, 70);
                    DrawInlineInfo("Server", serverVersion, 92);
                    DrawInlineInfo("模式", runtimeMode, 78);
                    GUILayout.FlexibleSpace();
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawInlineInfo("兼容", compatibility, 178);
                    DrawInlineInfo(
                        "授权",
                        UPilotProjectConfig.Current.safety?.writeAccessApproved == true ? "非 safe 已允许" : "safe 模式",
                        126);
                    GUILayout.FlexibleSpace();
                    if (UPilotProjectConfig.Current.safety?.writeAccessApproved == true)
                    {
                        if (GUILayout.Button("撤销", EditorStyles.miniButton, GUILayout.Width(52)))
                        {
                            if (EditorUtility.DisplayDialog(
                                "撤销写入授权？",
                                "撤销后，MCP 将回到 safe 模式并拒绝修改项目的工具。此操作会写入 .upilot/config.json。",
                                "撤销",
                                "取消"))
                            {
                                UPilotProjectConfig.RevokeProjectWriteAccess();
                                ShowNotice("已撤销写入授权");
                            }
                        }
                    }
                }
            }
        }

        private static void DrawInlineInfo(string label, string value, float width)
        {
            EditorGUILayout.LabelField(label + " " + value, EditorStyles.miniLabel, GUILayout.Width(width));
        }

        private void DrawAgentConfigurationList()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Agent 配置", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("检查", EditorStyles.miniButton, GUILayout.Width(48)))
                {
                    RefreshAgentConfigs(force: true);
                    ShowNotice("Agent 配置状态已刷新");
                }

                var updateLabel = HasAgentIntegrationIssues() ? "更新建议项" : "更新全部";
                if (GUILayout.Button(updateLabel, EditorStyles.miniButton, GUILayout.Width(86)))
                    UpdateAllAgentIntegrations();
            }

            DrawAgentIntegrationAdvice();

            foreach (var mcpStatus in _agentConfigs)
                DrawAgentConfigurationRow(mcpStatus, FindRuleStatus(mcpStatus.ClientName));
        }

        private void DrawAgentIntegrationAdvice()
        {
            var messageType = BuildAgentIntegrationAdvice(out var message);
            EditorGUILayout.HelpBox(message, messageType);
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
            foreach (var status in _agentConfigs)
            {
                if (NeedsMcpUpdate(status))
                    return true;
            }

            foreach (var status in _ruleConfigs)
            {
                if (NeedsRuleUpdate(status))
                    return true;
            }

            return false;
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
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var singleLine = position.width >= 430f;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(mcpStatus.ClientName, EditorStyles.boldLabel, GUILayout.Width(84));
                    EditorGUILayout.LabelField(
                        "MCP " + GetCompactMcpState(mcpStatus),
                        EditorStyles.miniLabel,
                        GUILayout.Width(78));
                    EditorGUILayout.LabelField(
                        GetRuleLabel(mcpStatus.ClientName) + " " + ruleStatus.StateText,
                        EditorStyles.miniLabel,
                        GUILayout.Width(88));
                    GUILayout.FlexibleSpace();

                    if (singleLine)
                        DrawAgentConfigurationButtons(mcpStatus, ruleStatus);
                }

                if (!singleLine)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        DrawAgentConfigurationButtons(mcpStatus, ruleStatus);
                    }
                }
            }
        }

        private void DrawAgentConfigurationButtons(
            AgentMcpConfigStatus mcpStatus,
            AgentRuleConfigStatus ruleStatus)
        {
            var configLabel = mcpStatus.HasUPilotEntry ? "更新配置" : "配置";
            if (GUILayout.Button(configLabel, EditorStyles.miniButton, GUILayout.Width(70)))
                UpdateAgentMcpConfig(mcpStatus);

            var ruleLabel = mcpStatus.ClientName == "Codex" ? "更新 Skill" : "更新规则";
            if (GUILayout.Button(ruleLabel, EditorStyles.miniButton, GUILayout.Width(76)))
                UpdateAgentRuleConfig(ruleStatus);
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
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("高级设置…", EditorStyles.miniButton, GUILayout.Width(96)))
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
            menu.AddItem(new GUIContent("检查更新"), false, () =>
            {
                UPilotUpdateService.Instance.CheckForUpdates(ShowNotice);
            });
            menu.AddItem(new GUIContent("更新 MCP Server exe"), false, () =>
            {
                UPilotUpdateService.Instance.UpdateServerExeAndRestart(ShowNotice);
            });
            menu.AddItem(new GUIContent("更新 UPM 包"), false, () =>
            {
                UPilotUpdateService.Instance.UpdateUpmFromManifest(ShowNotice);
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
