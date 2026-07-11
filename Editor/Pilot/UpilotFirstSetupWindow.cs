// -----------------------------------------------------------------------
// upilot Editor — first-run setup wizard.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using UnityEditor;
using UnityEngine;

namespace codingriver.upilot
{
    public sealed class UpilotFirstSetupWindow : EditorWindow
    {
        private int _step;
        private string _host = UpilotBridge.DefaultWsHost;
        private int _wsPort = UpilotBridge.DefaultWsPort;
        private int _httpPort = UpilotBridge.DefaultHttpPort;
        private bool _writeAgentRules = true;
        private bool _writeCodexConfig;
        private bool _writeClaudeConfig;
        private bool _writeCursorConfig;
        private bool _startAfterSetup = true;
        private string _portMessage = "";
        private MessageType _portMessageType = MessageType.None;
        private Vector2 _scroll;

        public static void Open()
        {
            var win = GetWindow<UpilotFirstSetupWindow>(true, "upilot first setup");
            win.minSize = new Vector2(520, 430);
            win.InitializeFromPrefs();
            win.ShowUtility();
        }

        private void OnEnable()
        {
            InitializeFromPrefs();
        }

        private void InitializeFromPrefs()
        {
            var bridge = UpilotBridge.Instance;
            _host = string.IsNullOrWhiteSpace(bridge.WsHost) ? UpilotBridge.DefaultWsHost : bridge.WsHost;
            _wsPort = bridge.WsPort > 0 ? bridge.WsPort : UpilotBridge.DefaultWsPort;
            _httpPort = bridge.HttpPort > 0 ? bridge.HttpPort : UpilotBridge.DefaultHttpPort;
            if (string.IsNullOrEmpty(_portMessage))
                DetectAndRecommendPorts();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("upilot 首次设置", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            if (_step == 0)
                DrawPortStep();
            else
                DrawConfigStep();

            EditorGUILayout.EndScrollView();
        }

        private void DrawPortStep()
        {
            EditorGUILayout.HelpBox(
                "第一步：为当前 Unity 项目设置独立端口。同一台电脑打开多个 Unity 项目时，每个项目应使用不同的 WS 和 HTTP 端口。",
                MessageType.Info);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _host = EditorGUILayout.TextField("Host", _host);
                _wsPort = EditorGUILayout.IntField("Unity Bridge WS 端口", _wsPort);
                _httpPort = EditorGUILayout.IntField("MCP HTTP 端口", _httpPort);

                if (!string.IsNullOrEmpty(_portMessage))
                    EditorGUILayout.HelpBox(_portMessage, _portMessageType);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("检测端口"))
                        DetectPortsOnly();

                    if (GUILayout.Button("使用推荐空闲端口"))
                        DetectAndRecommendPorts();
                }
            }

            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!PortsAreValid(showMessage: false)))
                {
                    if (GUILayout.Button("下一步", GUILayout.Width(120), GUILayout.Height(26)))
                    {
                        SavePorts();
                        _step = 1;
                    }
                }
            }
        }

        private void DrawConfigStep()
        {
            var mcpUrl = UpilotAgentSetup.GetMcpUrl(_httpPort);
            EditorGUILayout.HelpBox(
                "第二步：写入 Agent 识别规则和项目级 MCP 配置。Agent 规则会追加或更新 upilot 标记块；MCP 配置只更新 upilot 项，尽量保留其它 server。",
                MessageType.Info);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _writeAgentRules = EditorGUILayout.ToggleLeft(
                    "写入 Agent 识别规则（AGENTS.md、CLAUDE.md、Cursor rule、.agents skill）",
                    _writeAgentRules);
                EditorGUILayout.LabelField("规则写入策略：已有文件中追加/更新 <!-- upilot:start --> 标记块，保留用户其它内容。", EditorStyles.miniLabel);
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("MCP URL: " + mcpUrl, EditorStyles.miniLabel);
                _writeCodexConfig = EditorGUILayout.ToggleLeft("写入 Codex 项目配置  .codex/config.toml", _writeCodexConfig);
                _writeClaudeConfig = EditorGUILayout.ToggleLeft("写入 Claude Code 项目配置  .mcp.json", _writeClaudeConfig);
                _writeCursorConfig = EditorGUILayout.ToggleLeft("写入 Cursor 项目配置  .cursor/mcp.json", _writeCursorConfig);
                EditorGUILayout.LabelField("MCP 写入策略：存在配置时只更新 upilot 映射；其它 MCP server 保留。", EditorStyles.miniLabel);
            }

            _startAfterSetup = EditorGUILayout.ToggleLeft("完成后启动 Bridge 和 MCP Server", _startAfterSetup);

            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("上一步", GUILayout.Width(90), GUILayout.Height(26)))
                    _step = 0;

                GUILayout.FlexibleSpace();

                if (GUILayout.Button(_startAfterSetup ? "写入配置并启动" : "保存设置", GUILayout.Width(150), GUILayout.Height(26)))
                    CompleteSetup();
            }
        }

        private void DetectPortsOnly()
        {
            PortsAreValid(showMessage: true);
        }

        private void DetectAndRecommendPorts()
        {
            if (_wsPort != _httpPort &&
                UpilotPortAllocator.IsPortAvailable(_wsPort) &&
                UpilotPortAllocator.IsPortAvailable(_httpPort))
            {
                _portMessage = $"当前端口可用：WS {_wsPort}, HTTP {_httpPort}";
                _portMessageType = MessageType.Info;
                return;
            }

            var pair = UpilotPortAllocator.FindAvailablePair(_wsPort, _httpPort);
            _wsPort = pair.wsPort;
            _httpPort = pair.httpPort;
            _portMessage = $"检测到端口不可用，已推荐空闲端口：WS {_wsPort}, HTTP {_httpPort}";
            _portMessageType = MessageType.Warning;
        }

        private bool PortsAreValid(bool showMessage)
        {
            if (_wsPort <= 0 || _httpPort <= 0 || _wsPort > 65535 || _httpPort > 65535)
            {
                if (showMessage)
                {
                    _portMessage = "端口必须在 1 到 65535 之间。";
                    _portMessageType = MessageType.Error;
                }
                return false;
            }

            if (_wsPort == _httpPort)
            {
                if (showMessage)
                {
                    _portMessage = "WS 端口和 HTTP 端口不能相同。";
                    _portMessageType = MessageType.Error;
                }
                return false;
            }

            var wsOk = UpilotPortAllocator.IsPortAvailable(_wsPort);
            var httpOk = UpilotPortAllocator.IsPortAvailable(_httpPort);
            if (showMessage)
            {
                _portMessage = $"WS {_wsPort}: {(wsOk ? "可用" : "不可用")}；HTTP {_httpPort}: {(httpOk ? "可用" : "不可用")}";
                _portMessageType = wsOk && httpOk ? MessageType.Info : MessageType.Error;
            }
            return wsOk && httpOk;
        }

        private void SavePorts()
        {
            if (string.IsNullOrWhiteSpace(_host))
                _host = UpilotBridge.DefaultWsHost;

            var bridge = UpilotBridge.Instance;
            bridge.SetWsEndpoint(_host, _wsPort);
            bridge.HttpPort = _httpPort;
        }

        private void CompleteSetup()
        {
            SavePorts();

            if (_writeAgentRules)
                Debug.Log("[upilot] First setup agent rules:\n" + UpilotAgentSetup.WriteAgentRules(overwriteExisting: false));
            if (_writeCodexConfig)
                Debug.Log("[upilot] First setup Codex MCP config:\n" + UpilotAgentSetup.WriteCodexMcpConfig(promptBeforeOverwrite: true));
            if (_writeClaudeConfig)
                Debug.Log("[upilot] First setup Claude MCP config:\n" + UpilotAgentSetup.WriteClaudeCodeMcpConfig(promptBeforeOverwrite: true));
            if (_writeCursorConfig)
                Debug.Log("[upilot] First setup Cursor MCP config:\n" + UpilotAgentSetup.WriteCursorMcpConfig(promptBeforeOverwrite: true));

            UpilotAgentSetup.MarkAgentRulesHandledForCurrentProject();
            UpilotSetupState.MarkCompleted();

            if (_startAfterSetup)
            {
                UpilotBridge.Instance.EnsureStarted();
                UpilotMcpServerManager.Instance.ValidateAndAutoFixPath();
                UpilotMcpServerManager.Instance.StartServer();
            }

            Close();
            UpilotStatusWindow.Open();
        }
    }
}
