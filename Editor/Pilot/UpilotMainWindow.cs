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

        private GUIStyle _cardStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _messageStyle;
        private bool _stylesInitialized;

        [MenuItem("UPilot/UPilot", false, 200)]
        public static void Open()
        {
            var window = GetWindow<UPilotMainWindow>("UPilot");
            window.minSize = new Vector2(360, 260);
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
                _lastState = next.State;
                _stateChangedAt = EditorApplication.timeSinceStartup;
            }
            _snapshot = next;
        }

        private UPilotMainSnapshot GetDisplaySnapshot()
        {
            if (_snapshot.State != UPilotMainState.Starting ||
                EditorApplication.timeSinceStartup - _stateChangedAt < 8d)
                return _snapshot;

            return new UPilotMainSnapshot(
                UPilotMainState.NeedsRepair,
                "连接没有完成",
                "可以自动检查并恢复服务连接。",
                _snapshot.BridgeActive,
                _snapshot.McpActive);
        }

        private void RefreshAgentConfigs(bool force)
        {
            if (!force && EditorApplication.timeSinceStartup - _lastAgentRefresh < 2d)
                return;

            _lastAgentRefresh = EditorApplication.timeSinceStartup;
            _agentConfigs = UPilotAgentSetup.GetMcpConfigStatuses();
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
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField(snapshot.Title, _titleStyle, GUILayout.Height(28));
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField(snapshot.Message, _messageStyle, GUILayout.MinHeight(34));
                EditorGUILayout.Space(10);

                if (snapshot.State == UPilotMainState.SetupRequired)
                    DrawSetupControls();
                else if (snapshot.State == UPilotMainState.Stopped)
                    DrawPrimaryButton("启动 UPilot", StartUPilot);
                else if (snapshot.State == UPilotMainState.Starting)
                    DrawPrimaryButton("重新连接", RepairUPilot);
                else if (snapshot.State == UPilotMainState.NeedsRepair)
                    DrawPrimaryButton("自动修复", RepairUPilot);
                else
                    EditorGUILayout.LabelField("无需其他操作", EditorStyles.centeredGreyMiniLabel);

                GUILayout.FlexibleSpace();
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
            if (UPilotSetupState.IsCompleted)
                menu.AddItem(new GUIContent("重新启动"), false, () =>
                {
                    UPilotQuickStart.Restart();
                    ShowNotice("UPilot 正在重新启动…");
                });
            else
                menu.AddDisabledItem(new GUIContent("重新启动"));

            if (snapshot.AnyServiceActive)
                menu.AddItem(new GUIContent("停止服务"), false, () =>
                {
                    UPilotQuickStart.Stop();
                    ShowNotice("UPilot 已停止", MessageType.Warning);
                });
            else
                menu.AddDisabledItem(new GUIContent("停止服务"));

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
            if (state == UPilotMainState.Starting) return new Color(1f, 0.65f, 0.1f);
            if (state == UPilotMainState.NeedsRepair) return new Color(1f, 0.35f, 0.2f);
            return Color.gray;
        }

        private static string GetStateLabel(UPilotMainState state)
        {
            if (state == UPilotMainState.Ready) return "已就绪";
            if (state == UPilotMainState.Starting) return "启动中";
            if (state == UPilotMainState.NeedsRepair) return "需修复";
            if (state == UPilotMainState.SetupRequired) return "待配置";
            return "已停止";
        }
    }
}
