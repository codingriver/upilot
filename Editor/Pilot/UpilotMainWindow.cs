// -----------------------------------------------------------------------
// UPilot Editor - simple user-facing entry window.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
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

        private string _notice = "";
        private MessageType _noticeType = MessageType.Info;
        private double _noticeUntil;
        private bool _restartRequested;

        private GUIStyle _cardStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _messageStyle;
        private bool _stylesInitialized;

        [MenuItem("UPilot/UPilot", false, 200)]
        public static void Open()
        {
            var window = GetWindow<UPilotMainWindow>("UPilot");
            window.minSize = new Vector2(360, 460);
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
            DrawNotice();
            EditorGUILayout.Space(8);
            DrawMainCard(displaySnapshot);
            EditorGUILayout.Space(8);
            DrawAdvancedEntry();
        }

        private void InitializeStyles()
        {
            if (_stylesInitialized) return;
            _stylesInitialized = true;

            _cardStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(16, 16, 14, 14),
                margin = new RectOffset(8, 8, 2, 2),
            };
            _titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 16,
                wordWrap = true,
            };
            _messageStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
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
                EditorGUILayout.LabelField(snapshot.Title, _titleStyle, GUILayout.Height(28));
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField(snapshot.Message, _messageStyle, GUILayout.MinHeight(34));
                EditorGUILayout.Space(8);

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
            EditorGUILayout.Space(8);
            DrawPrimaryButton("配置并启动", ConfigureAndStart);
        }

        private void DrawOperationsDashboard(UPilotMainSnapshot snapshot)
        {
            DrawServiceControls(snapshot);
            EditorGUILayout.Space(10);
            DrawMcpEndpoint();
            EditorGUILayout.Space(10);
            using (new EditorGUI.DisabledScope(IsServiceTransitioning(snapshot.State)))
                DrawAgentConfigurationList();
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
                    GUILayout.Button(label, GUILayout.Height(30));
                return;
            }

            var primaryLabel = snapshot.State == UPilotMainState.Ready ? "重启 UPilot" : "自动修复";
            if (GUILayout.Button(primaryLabel, GUILayout.Height(30)))
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

            EditorGUILayout.LabelField("MCP 地址", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.SelectableLabel(mcpUrl, EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button("复制", EditorStyles.miniButton, GUILayout.Width(44)))
                {
                    EditorGUIUtility.systemCopyBuffer = mcpUrl;
                    ShowNotice("已复制 MCP 地址");
                }
            }
        }

        private void DrawAgentConfigurationList()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Agent 配置", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("更新全部", EditorStyles.miniButton, GUILayout.Width(76)))
                    UpdateAllAgentIntegrations();
            }

            foreach (var mcpStatus in _agentConfigs)
                DrawAgentConfigurationRow(mcpStatus, FindRuleStatus(mcpStatus.ClientName));
        }

        private void DrawAgentConfigurationRow(
            AgentMcpConfigStatus mcpStatus,
            AgentRuleConfigStatus ruleStatus)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(mcpStatus.ClientName, EditorStyles.boldLabel, GUILayout.Width(84));
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField(
                        "MCP " + GetCompactMcpState(mcpStatus),
                        EditorStyles.miniLabel,
                        GUILayout.Width(86));
                    EditorGUILayout.LabelField(
                        GetRuleLabel(mcpStatus.ClientName) + " " + ruleStatus.StateText,
                        EditorStyles.miniLabel,
                        GUILayout.Width(92));
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    var configLabel = mcpStatus.HasUPilotEntry ? "更新配置" : "配置";
                    if (GUILayout.Button(configLabel, EditorStyles.miniButton, GUILayout.Width(76)))
                        UpdateAgentMcpConfig(mcpStatus);

                    var ruleLabel = mcpStatus.ClientName == "Codex" ? "更新 Skill" : "更新规则";
                    if (GUILayout.Button(ruleLabel, EditorStyles.miniButton, GUILayout.Width(82)))
                        UpdateAgentRuleConfig(ruleStatus);
                }
            }
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
            if (GUILayout.Button(label, GUILayout.Height(30)))
                action();
        }

        private void ConfigureAndStart()
        {
            var result = UPilotQuickStart.ConfigureAndStart(_useCodex, _useClaudeCode, _useCursor);
            Debug.Log("[UPilot] Quick setup:\n" + result);
            RefreshAgentConfigs(force: true);
            _stateChangedAt = EditorApplication.timeSinceStartup;
            ShowNotice("配置完成，UPilot 正在启动…");
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
