// -----------------------------------------------------------------------
// upilot Editor — first-run setup wizard.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public sealed class UPilotFirstSetupWindow : EditorWindow
    {
        private int _step;
        private string _host = UPilotBridge.DefaultWsHost;
        private int _wsPort = UPilotBridge.DefaultWsPort;
        private int _httpPort = UPilotBridge.DefaultHttpPort;
        private bool _writeAgentRules = true;
        private bool _writeCodexConfig;
        private bool _writeClaudeConfig;
        private bool _writeCursorConfig;
        private bool _startAfterSetup = true;
        private bool _approveProjectWrites;
        private string _portMessage = "";
        private MessageType _portMessageType = MessageType.None;
        private UPilotPythonProbeResult _pythonProbe;
        private Vector2 _scroll;

        public static void Open()
        {
            var win = GetWindow<UPilotFirstSetupWindow>(true, "UPilot First Setup");
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
            var bridge = UPilotBridge.Instance;
            _host = string.IsNullOrWhiteSpace(bridge.WsHost) ? UPilotBridge.DefaultWsHost : bridge.WsHost;
            _wsPort = bridge.WsPort > 0 ? bridge.WsPort : UPilotBridge.DefaultWsPort;
            _httpPort = bridge.HttpPort > 0 ? bridge.HttpPort : UPilotBridge.DefaultHttpPort;
            if (string.IsNullOrEmpty(_portMessage))
                DetectAndRecommendPorts();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("UPilot 首次设置", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            if (_step == 0)
                DrawPortStep();
            else if (_step == 1)
                DrawRuntimeStep();
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
                        RefreshPythonProbe();
                    }
                }
            }
        }

        private void DrawRuntimeStep()
        {
            EditorGUILayout.HelpBox(
                "第二步：检查 MCP Server 运行环境。Python 3.11+ 可用时优先使用 Python；不可用时下载完整独立 exe。",
                MessageType.Info);

            _pythonProbe ??= UPilotServerRuntimeService.Instance.ProbePython();

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Python 环境", EditorStyles.boldLabel);
                var envState = UPilotServerRuntimeService.Instance.PythonEnvironmentState;
                var messageType = _pythonProbe.IsUsable ? MessageType.Info : MessageType.Warning;
                EditorGUILayout.HelpBox(_pythonProbe.Message, messageType);
                if (!string.IsNullOrEmpty(_pythonProbe.PythonPath))
                    EditorGUILayout.SelectableLabel(_pythonProbe.PythonPath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (!string.IsNullOrEmpty(_pythonProbe.VersionText))
                    EditorGUILayout.LabelField(_pythonProbe.VersionText, EditorStyles.miniLabel);
                if (UPilotServerRuntimeService.Instance.IsPythonRuntimeConfigured(out var configuredPython))
                    EditorGUILayout.HelpBox("已配置 Python 运行时：" + configuredPython, MessageType.Info);
                if (envState.IsRunning || envState.IsComplete || !string.IsNullOrEmpty(envState.ErrorMessage))
                {
                    var phase = string.IsNullOrEmpty(envState.Phase) ? "准备中" : envState.Phase;
                    EditorGUILayout.LabelField("自动配置", phase, EditorStyles.miniLabel);
                    if (!string.IsNullOrEmpty(envState.VenvPath))
                        EditorGUILayout.SelectableLabel(envState.VenvPath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    if (!string.IsNullOrEmpty(envState.ErrorMessage))
                        EditorGUILayout.HelpBox(envState.ErrorMessage, MessageType.Error);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("重新检测", GUILayout.Width(90)))
                        RefreshPythonProbe();
                    using (new EditorGUI.DisabledScope(!_pythonProbe.IsUsable))
                    {
                        if (GUILayout.Button("使用 Python 环境"))
                        {
                            UPilotServerRuntimeService.Instance.SetPythonRuntime(_pythonProbe.PythonPath);
                            _step = 2;
                        }
                    }
                    using (new EditorGUI.DisabledScope(!_pythonProbe.IsUsable || envState.IsRunning))
                    {
                        if (GUILayout.Button("自动配置环境"))
                            UPilotServerRuntimeService.Instance.StartAutoConfigurePythonEnvironment();
                    }
                    using (new EditorGUI.DisabledScope(!envState.IsRunning))
                    {
                        if (GUILayout.Button("取消", GUILayout.Width(60)))
                            UPilotServerRuntimeService.Instance.CancelPythonEnvironmentSetup();
                    }
                }
            }

            DrawStandaloneDownloadSection();

            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("上一步", GUILayout.Width(90), GUILayout.Height(26)))
                    _step = 0;
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!RuntimeReady()))
                {
                    if (GUILayout.Button("下一步", GUILayout.Width(120), GUILayout.Height(26)))
                        _step = 2;
                }
            }
        }

        private void DrawStandaloneDownloadSection()
        {
            var runtime = UPilotServerRuntimeService.Instance;
            var state = runtime.DownloadState;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("独立 MCP Server exe", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("缓存目录", runtime.RuntimeCacheRoot, EditorStyles.miniLabel);

                if (runtime.IsStandaloneExeConfigured(out var exePath))
                    EditorGUILayout.HelpBox("已配置：" + exePath, MessageType.Info);

                if (state.IsRunning || state.IsComplete || !string.IsNullOrEmpty(state.ErrorMessage))
                {
                    var progressLabel = string.IsNullOrEmpty(state.Phase) ? "准备中" : state.Phase;
                    EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(false, 18), state.Progress, progressLabel);
                    var sizeText = state.TotalBytes > 0
                        ? $"{FormatBytes(state.BytesReceived)} / {FormatBytes(state.TotalBytes)}"
                        : FormatBytes(state.BytesReceived);
                    EditorGUILayout.LabelField(sizeText, EditorStyles.miniLabel);
                    if (!string.IsNullOrEmpty(state.Version))
                        EditorGUILayout.LabelField("版本: " + state.Version, EditorStyles.miniLabel);
                    if (!string.IsNullOrEmpty(state.DownloadUrl))
                        EditorGUILayout.SelectableLabel("来源: " + state.DownloadUrl, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    if (!string.IsNullOrEmpty(state.Sha256))
                        EditorGUILayout.LabelField("SHA256: " + state.Sha256, EditorStyles.miniLabel);
                    if (!string.IsNullOrEmpty(state.ErrorMessage))
                        EditorGUILayout.HelpBox(state.ErrorMessage, MessageType.Error);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(state.IsRunning))
                    {
                        if (GUILayout.Button(state.IsComplete ? "重新下载" : "下载完整 Server exe"))
                            runtime.StartDownloadLatestServerExe();
                    }
                    using (new EditorGUI.DisabledScope(!state.IsRunning))
                    {
                        if (GUILayout.Button("取消", GUILayout.Width(70)))
                            runtime.CancelDownload();
                    }
                    if (GUILayout.Button("选择本地 exe", GUILayout.Width(100)))
                    {
                        var selected = EditorUtility.OpenFilePanel("选择 UPilot MCP Server exe", "", "exe");
                        if (!string.IsNullOrEmpty(selected) && File.Exists(selected))
                            runtime.SetStandaloneExeRuntime(selected);
                    }
                }
            }
        }

        private void DrawConfigStep()
        {
            var mcpUrl = UPilotAgentSetup.GetMcpUrl(_httpPort);
            EditorGUILayout.HelpBox(
                "第三步：写入 Agent 识别规则和项目级 MCP 配置。Agent 规则会追加或更新 UPilot 标记块；MCP 配置只更新名为 upilot 的服务项，尽量保留其它 server。",
                MessageType.Info);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _writeAgentRules = EditorGUILayout.ToggleLeft(
                    "写入 Agent 识别规则（AGENTS.md、CLAUDE.md、Cursor rule、.agents skill）",
                    _writeAgentRules);
                EditorGUILayout.LabelField("规则写入策略：已有文件中追加/更新 UPilot 管理标记块，保留用户其它内容。", EditorStyles.miniLabel);
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("MCP URL: " + mcpUrl, EditorStyles.miniLabel);
                _writeCodexConfig = EditorGUILayout.ToggleLeft("写入 Codex 项目配置  .codex/config.toml", _writeCodexConfig);
                _writeClaudeConfig = EditorGUILayout.ToggleLeft("写入 Claude Code 项目配置  .mcp.json", _writeClaudeConfig);
                _writeCursorConfig = EditorGUILayout.ToggleLeft("写入 Cursor 项目配置  .cursor/mcp.json", _writeCursorConfig);
                EditorGUILayout.LabelField("MCP 写入策略：存在配置时只更新名为 upilot 的服务映射；其它 MCP server 保留。", EditorStyles.miniLabel);
            }

            _startAfterSetup = EditorGUILayout.ToggleLeft("完成后启动 Bridge 和 MCP Server", _startAfterSetup);
            _approveProjectWrites = EditorGUILayout.ToggleLeft("允许 Agent 通过 MCP 修改当前 Unity 项目（非 safe 模式）", _approveProjectWrites);

            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("上一步", GUILayout.Width(90), GUILayout.Height(26)))
                    _step = 1;

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
                UPilotPortAllocator.IsPortAvailable(_wsPort) &&
                UPilotPortAllocator.IsPortAvailable(_httpPort))
            {
                _portMessage = $"当前端口可用：WS {_wsPort}, HTTP {_httpPort}";
                _portMessageType = MessageType.Info;
                return;
            }

            var pair = UPilotPortAllocator.FindAvailablePair(_wsPort, _httpPort);
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

            var wsOk = UPilotPortAllocator.IsPortAvailable(_wsPort);
            var httpOk = UPilotPortAllocator.IsPortAvailable(_httpPort);
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
                _host = UPilotBridge.DefaultWsHost;

            var bridge = UPilotBridge.Instance;
            bridge.SetWsEndpoint(_host, _wsPort);
            bridge.HttpPort = _httpPort;
        }

        private void CompleteSetup()
        {
            SavePorts();

            if (_approveProjectWrites && !ConfirmProjectWriteAccess())
                return;

            if (_writeAgentRules)
                Debug.Log("[UPilot] First setup agent rules:\n" + UPilotAgentSetup.WriteAgentRules(overwriteExisting: false));
            if (_writeCodexConfig)
                Debug.Log("[UPilot] First setup Codex MCP config:\n" + UPilotAgentSetup.WriteCodexMcpConfig(promptBeforeOverwrite: true));
            if (_writeClaudeConfig)
                Debug.Log("[UPilot] First setup Claude MCP config:\n" + UPilotAgentSetup.WriteClaudeCodeMcpConfig(promptBeforeOverwrite: true));
            if (_writeCursorConfig)
                Debug.Log("[UPilot] First setup Cursor MCP config:\n" + UPilotAgentSetup.WriteCursorMcpConfig(promptBeforeOverwrite: true));

            UPilotAgentSetup.MarkAgentRulesHandledForCurrentProject();
            UPilotSetupState.MarkCompleted();

            if (_startAfterSetup)
            {
                UPilotBridge.Instance.EnsureStarted();
                UPilotMcpServerManager.Instance.ValidateAndAutoFixPath();
                UPilotMcpServerManager.Instance.StartServer();
            }

            Close();
            UPilotMainWindow.Open();
        }

        private void RefreshPythonProbe()
        {
            _pythonProbe = UPilotServerRuntimeService.Instance.ProbePython();
        }

        private bool RuntimeReady()
        {
            var runtime = UPilotServerRuntimeService.Instance;
            if (runtime.GetConfiguredMode() == UPilotServerRuntimeMode.StandaloneExe)
                return runtime.IsStandaloneExeConfigured(out _);
            return runtime.IsPythonRuntimeConfigured(out _) || _pythonProbe != null && _pythonProbe.IsUsable;
        }

        private static bool ConfirmProjectWriteAccess()
        {
            var confirmed = EditorUtility.DisplayDialog(
                "允许 UPilot 修改项目？",
                "启用后，Agent 可以通过 MCP 修改 Assets、Packages、场景、Prefab、脚本和组件，并可能触发编译或保存。请确认你信任当前 Agent 工作流。",
                "允许修改",
                "保持 safe 模式");
            if (!confirmed)
                return false;

            UPilotProjectConfig.ApproveProjectWriteAccess();
            return true;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("F1") + " MB";
        }
    }
}
