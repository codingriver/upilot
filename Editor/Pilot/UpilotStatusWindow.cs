// -----------------------------------------------------------------------
// upilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace codingriver.upilot
{
    public class UpilotStatusWindow : EditorWindow
    {
        // ── Volatile state written by background threads ───────────────────────
        private volatile string _diagResult  = "";
        private volatile bool   _diagRunning = false;
        private long            _diagResultAtMs;

        // ── Layout ────────────────────────────────────────────────────────────
        private Vector2 _logScroll;
        private Vector2 _diagScroll;
        private Vector2 _opLogScroll;
        private Vector2 _mainScroll;
        private bool    _autoScroll = true;
        private bool    _opAutoScroll = true;
        private double  _lastRepaint;

        // ── GUI event-stable snapshots (Layout -> Repaint) ───────────────────
        private BridgeStatus         _guiStatusSnapshot;
        private McpServerStatus      _guiMcpStatusSnapshot;
        private AgentMcpConfigStatus[] _guiAgentConfigSnapshot = Array.Empty<AgentMcpConfigStatus>();
        private List<BridgeLogEntry> _guiLogsSnapshot = new();
        private string               _guiDiagResultSnapshot = "";
        private bool                 _guiDiagRunningSnapshot;
        private long                 _guiDiagResultAtMsSnapshot;

        // ── Log filter ────────────────────────────────────────────────────────
        private bool _showInfo    = true;
        private bool _showWarn    = true;
        private bool _showError   = true;
        private bool _showCompile = true;
        private bool _showNetwork = true;

        // ── UI state ──────────────────────────────────────────────────────────
        private bool _skipKillConfirm;
        private string _toastMessage = "";
        private MessageType _toastType = MessageType.Info;
        private double _toastExpireAt;

        // ── Operation log tab ──────────────────────────────────────────────
        private bool _opFilterAll   = true;
        private bool _opFilterFail  = false;
        private bool _opFilterStuck = false;
        private List<OperationLogEntry> _guiOpLogsSnapshot = new();

        private string _wsHostInput = "127.0.0.1";
        private int _wsPortInput = 8765;
        private int _httpPortInput = 8011;
        private int _activeTab;
        private bool _showEndpointSettings;
        private bool _showConnectionDetails;
        private bool _showDangerousDiagnostics;
        private double _lastAgentConfigRefresh;

        // ── Styles (lazy-init on main thread) ─────────────────────────────────
        private GUIStyle _styleInfo;
        private GUIStyle _styleWarn;
        private GUIStyle _styleError;
        private GUIStyle _styleBox;
        private Texture2D _texCardError;
        private Texture2D _texCardStuck;
        private Texture2D _texCardActive;
        /// <summary>连接状态面板：与 Inspector 一致，略收紧内边距；行间间距用标准 vertical spacing。</summary>
        private GUIStyle _styleBoxConnection;
        private GUIStyle _styleLogCard;
        private bool     _stylesInit;

        [MenuItem("upilot/Advanced Settings", false, 210)]
        public static void Open()
        {
            var win = GetWindow<UpilotStatusWindow>("upilot Advanced");
            win.minSize = new Vector2(400, 540);
            win.Show();
        }

        private void OnEnable()
        {
            var bridge = UpilotBridge.Instance;
            _wsHostInput = bridge.WsHost;
            _wsPortInput = bridge.WsPort;
            _httpPortInput = bridge.HttpPort;
            EditorApplication.update += OnEditorUpdate;

            // Validate MCP server path once when the window is opened.
            UpilotMcpServerManager.Instance.ValidateAndAutoFixPath();
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            PersistEndpointInputsIfIdle();
            UpilotWindowDiagnostics.OnWindowClosed();
        }

        /// <summary>
        /// 将 IP/端口写入 <see cref="UpilotBridge"/>（内部使用 EditorPrefs），Bridge 未运行时关闭窗口也会保存。
        /// </summary>
        private void PersistEndpointInputsIfIdle()
        {
            var bridge = UpilotBridge.Instance;
            if (bridge.GetStatus().IsStarted)
                return;

            var host = string.IsNullOrWhiteSpace(_wsHostInput) ? "127.0.0.1" : _wsHostInput.Trim();
            var wsPort = _wsPortInput <= 0 ? 8765 : _wsPortInput;
            var httpPort = _httpPortInput <= 0 ? 8011 : _httpPortInput;
            if (host == bridge.WsHost && wsPort == bridge.WsPort && httpPort == bridge.HttpPort)
                return;

            bridge.SetWsEndpoint(host, wsPort);
            bridge.HttpPort = httpPort;
        }

        private void OnEditorUpdate()
        {
            if (EditorApplication.timeSinceStartup - _lastRepaint > 0.3)
            {
                _lastRepaint = EditorApplication.timeSinceStartup;
                Repaint();
            }
        }

        private void InitStyles()
        {
            if (_stylesInit) return;
            _stylesInit = true;

            _styleBox = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(10, 10, 8, 8),
                margin  = new RectOffset(4, 4, 3, 3),
            };
            _styleBoxConnection = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(10, 10, 7, 7),
                margin  = new RectOffset(4, 4, 3, 3),
            };
            _styleInfo = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                wordWrap = true,
                normal   = { textColor = EditorGUIUtility.isProSkin ? new Color(0.75f, 0.75f, 0.75f) : Color.black },
            };
            _styleWarn  = new GUIStyle(_styleInfo) { normal = { textColor = new Color(1f, 0.78f, 0.15f) } };
            _styleError = new GUIStyle(_styleInfo) { normal = { textColor = new Color(1f, 0.35f, 0.35f) } };

            _styleLogCard = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 8, 8),
                margin  = new RectOffset(6, 6, 0, 12),
            };

            _texCardError = MakeSolidTex(new Color(0.4f, 0.1f, 0.1f, 0.3f));
            _texCardStuck = MakeSolidTex(new Color(0.5f, 0.35f, 0.05f, 0.3f));
            _texCardActive = MakeSolidTex(new Color(0.1f, 0.3f, 0.5f, 0.2f));
        }

        private static Texture2D MakeSolidTex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        // ─────────────────────────────────── GUI ──────────────────────────────
        // IMPORTANT: snapshot all background-thread-written state here once,
        // so the Layout pass and Repaint pass see identical control counts.

        private void OnGUI()
        {
            InitStyles();

            var bridge = UpilotBridge.Instance;

            if (Event.current.type == EventType.Layout || _guiLogsSnapshot.Count == 0)
            {
                _guiStatusSnapshot       = bridge.GetStatus();
                _guiMcpStatusSnapshot    = UpilotMcpServerManager.Instance.GetStatus();
                _guiLogsSnapshot         = bridge.GetLogsCopy();
                _guiDiagResultSnapshot   = _diagResult;
                _guiDiagRunningSnapshot  = _diagRunning;
                _guiDiagResultAtMsSnapshot = _diagResultAtMs;
                _guiOpLogsSnapshot       = UpilotOperationTracker.Instance.GetEntriesCopy();

                if (_guiAgentConfigSnapshot.Length == 0 ||
                    EditorApplication.timeSinceStartup - _lastAgentConfigRefresh > 2d)
                {
                    RefreshAgentConfigSnapshot();
                }
            }

            UpilotWindowDiagnostics.RecordWindow(position.width, position.height, 0);
            UpilotLogsTabDiagnostics.ClearNotOnLogsTab();

            _mainScroll.x = 0f;
            var mainContentWidth = Mathf.Max(280f, position.width - 20f);
            _mainScroll = EditorGUILayout.BeginScrollView(
                _mainScroll,
                false,
                true,
                GUIStyle.none,
                GUI.skin.verticalScrollbar,
                GUI.skin.scrollView);
            try
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(mainContentWidth)))
                {
                    DrawTopStatusOverview(bridge, _guiStatusSnapshot, _guiMcpStatusSnapshot);
                    DrawToastIfAny();

                    EditorGUILayout.Space(4);
                    DrawRuntimeTab(
                        bridge,
                        _guiStatusSnapshot,
                        _guiMcpStatusSnapshot,
                        _guiDiagResultSnapshot,
                        _guiDiagRunningSnapshot,
                        _guiDiagResultAtMsSnapshot);
                }
            }
            finally
            {
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawRuntimeTab(
            UpilotBridge bridge,
            BridgeStatus status,
            McpServerStatus mcpStatus,
            string diagResult,
            bool diagRunning,
            long diagResultAtMs)
        {
            string[] tabs = new[] { "服务", "Agent", "日志与诊断" };
            _activeTab = GUILayout.Toolbar(_activeTab, tabs);

            EditorGUILayout.Space(4);

            if (_activeTab == 0)
            {
                DrawEnableSection(bridge, status);
                EditorGUILayout.Space(6);
                DrawSharedEndpointSection(bridge, status);
                EditorGUILayout.Space(6);
                DrawMcpServerSection(status, mcpStatus);
            }
            else if (_activeTab == 1)
            {
                DrawAgentSetupSection(_guiAgentConfigSnapshot);
            }
            else
            {
                DrawConnectionSection(status);
                EditorGUILayout.Space(4);
                DrawDiagnosticsSection(bridge, diagResult, diagRunning, diagResultAtMs);
                EditorGUILayout.Space(4);
                DrawLogFileSection();
            }
        }

        private void DrawLogsTab(UpilotBridge bridge, List<BridgeLogEntry> logs)
        {
            var tabMaxW = Mathf.Max(100f, position.width);
            using (new EditorGUILayout.VerticalScope(_styleBox, GUILayout.MaxWidth(tabMaxW)))
            {
                DrawLogToolbar(logs, bridge);

                float logHeight = Mathf.Clamp(position.height * 0.68f, 220f, 640f);
                _logScroll.x = 0f;

                var vBarW = GUI.skin.verticalScrollbar.fixedWidth > 0f
                    ? GUI.skin.verticalScrollbar.fixedWidth
                    : 15f;
                // 滚动区域内容宽度不得超过视口，否则会出现横向滚动条
                var scrollViewportW = Mathf.Max(80f, position.width - 28f - vBarW);
                var labelMaxW = Mathf.Max(60f, scrollViewportW - 28f);

                _logScroll = EditorGUILayout.BeginScrollView(
                    _logScroll,
                    false,
                    true,
                    GUIStyle.none,
                    GUI.skin.verticalScrollbar,
                    GUI.skin.scrollView,
                    GUILayout.Height(logHeight),
                    GUILayout.MaxWidth(tabMaxW));

                using (new EditorGUILayout.VerticalScope(GUILayout.MaxWidth(scrollViewportW)))
                {
                    foreach (var entry in logs)
                    {
                        if (!ShouldShowLogEntry(entry)) continue;

                        var fullText = BuildLogEntryDisplayText(entry);
                        var labelStyle = GetLogLabelStyle(entry);
                        var blockH = labelStyle.CalcHeight(new GUIContent(fullText), labelMaxW);
                        blockH = Mathf.Max(blockH, EditorGUIUtility.singleLineHeight * 2f);

                        using (new EditorGUILayout.VerticalScope(_styleLogCard))
                        {
                            EditorGUILayout.LabelField(
                                fullText,
                                labelStyle,
                                GUILayout.Width(labelMaxW),
                                GUILayout.MaxWidth(labelMaxW),
                                GUILayout.Height(blockH),
                                GUILayout.ExpandWidth(false));
                        }
                    }
                }

                EditorGUILayout.EndScrollView();

                if (_autoScroll && logs.Count > 0)
                    _logScroll = new Vector2(0, float.MaxValue);

                UpilotLogsTabDiagnostics.RecordLogsTab(position.width, scrollViewportW, labelMaxW, _logScroll);
                UpilotWindowDiagnostics.RecordSection("logScroll", labelMaxW, scrollViewportW);
            }
        }

        // ── Operation Log Tab ──────────────────────────────────────────────

        private void DrawOperationLogTab(List<OperationLogEntry> entries)
        {
            var tabMaxW = Mathf.Max(100f, position.width);
            using (new EditorGUILayout.VerticalScope(_styleBox, GUILayout.MaxWidth(tabMaxW)))
            {
                DrawOperationLogToolbar(entries);

                float logHeight = Mathf.Clamp(position.height * 0.72f, 240f, 800f);

                var vBarW = GUI.skin.verticalScrollbar.fixedWidth > 0f
                    ? GUI.skin.verticalScrollbar.fixedWidth
                    : 15f;
                var scrollViewportW = Mathf.Max(80f, position.width - 28f - vBarW);
                var labelMaxW = Mathf.Max(60f, scrollViewportW - 28f);

                _opLogScroll = EditorGUILayout.BeginScrollView(
                    _opLogScroll,
                    false,
                    true,
                    GUIStyle.none,
                    GUI.skin.verticalScrollbar,
                    GUI.skin.scrollView,
                    GUILayout.Height(logHeight),
                    GUILayout.MaxWidth(tabMaxW));

                using (new EditorGUILayout.VerticalScope(GUILayout.MaxWidth(scrollViewportW)))
                {
                    foreach (var entry in entries)
                    {
                        if (!ShouldShowOpEntry(entry)) continue;
                        DrawOperationLogCard(entry, labelMaxW);
                    }
                }

                EditorGUILayout.EndScrollView();

                if (_opAutoScroll && entries.Count > 0)
                    _opLogScroll = new Vector2(0, float.MaxValue);
            }
        }

        private void DrawOperationLogToolbar(List<OperationLogEntry> entries)
        {
            var tracker = UpilotOperationTracker.Instance;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("操作日志", EditorStyles.boldLabel, GUILayout.Width(52));
                GUILayout.FlexibleSpace();

                var statsStyle = EditorStyles.miniLabel;
                var active = tracker.ActiveCount;
                var total  = tracker.TotalCount;
                var failed = tracker.FailedCount;
                var stuck  = tracker.StuckCount;

                var statsColor = (stuck > 0 || failed > 0) ? new Color(1f, 0.5f, 0.3f) : Color.white;
                var prevColor = GUI.color;
                GUI.color = statsColor;
                EditorGUILayout.LabelField(
                    $"活跃:{active}  总计:{total}  失败:{failed}  卡住:{stuck}",
                    statsStyle, GUILayout.Width(200));
                GUI.color = prevColor;
            }

            EditorGUILayout.Space(2);
            using (new EditorGUILayout.HorizontalScope())
            {
                _opFilterAll   = GUILayout.Toggle(_opFilterAll,   "全部",       EditorStyles.miniButtonLeft, GUILayout.Width(40));
                _opFilterFail  = GUILayout.Toggle(_opFilterFail,  "仅失败",     EditorStyles.miniButtonMid,  GUILayout.Width(44));
                _opFilterStuck = GUILayout.Toggle(_opFilterStuck, "仅卡住",     EditorStyles.miniButtonRight, GUILayout.Width(44));

                if (_opFilterFail || _opFilterStuck) _opFilterAll = false;
                if (!_opFilterFail && !_opFilterStuck) _opFilterAll = true;

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("打开日志文件", EditorStyles.miniButton, GUILayout.Width(88)))
                {
                    tracker.RevealLogFile();
                    ShowToast("已定位到 " + Logger.LogFilePath);
                }


                _opAutoScroll = GUILayout.Toggle(_opAutoScroll, "自动滚动", EditorStyles.miniButton, GUILayout.Width(60));

                if (GUILayout.Button("清除", EditorStyles.miniButton, GUILayout.Width(36)))
                {
                    tracker.ClearEntries();
                    ShowToast("操作日志已清除");
                }
            }

            EditorGUILayout.Space(2);
            using (new EditorGUILayout.HorizontalScope())
            {
                var bridge = UpilotBridge.Instance;
                var autoRestart = bridge.AutoRestartOnCriticalStuck;
                var newAutoRestart = GUILayout.Toggle(autoRestart, "临界超时自动重启", EditorStyles.miniButton, GUILayout.Width(110));
                if (newAutoRestart != autoRestart)
                    bridge.AutoRestartOnCriticalStuck = newAutoRestart;

                GUILayout.FlexibleSpace();

                var prevColor = GUI.color;
                GUI.color = new Color(1f, 0.6f, 0.6f);
                if (GUILayout.Button("强制重启 Unity", EditorStyles.miniButton, GUILayout.Width(100)))
                {
                    if (EditorUtility.DisplayDialog("确认操作", "确定要强制关闭并重启当前 Unity 项目吗？\n未保存的更改将丢失！", "确定", "取消"))
                    {
                        UpilotBridge.ForceRestartUnityEditor();
                    }
                }
                GUI.color = prevColor;
            }
        }

        private bool ShouldShowOpEntry(OperationLogEntry entry)
        {
            if (_opFilterAll) return true;
            if (_opFilterFail && (entry.Phase == "failed" || entry.Phase == "agent_error" || entry.Phase == "critical")) return true;
            if (_opFilterStuck && entry.IsStuck) return true;
            if (_opFilterFail && entry.Phase == "disconnected") return true;
            return false;
        }

        private void DrawOperationLogCard(OperationLogEntry entry, float maxW)
        {
            var isError  = entry.Phase == "failed" || entry.Phase == "agent_error" || entry.Phase == "critical";
            var isStuck  = entry.IsStuck;
            var isSystem = entry.Phase == "system" || entry.Phase == "stopped" || entry.Phase == "disconnected";
            var isActive = !entry.CompletedAt.HasValue && entry.Phase != "agent_error" && !isSystem;

            var cardStyle = new GUIStyle(_styleLogCard);
            if (isError)
                cardStyle.normal.background = _texCardError;
            else if (isStuck)
                cardStyle.normal.background = _texCardStuck;
            else if (isActive)
                cardStyle.normal.background = _texCardActive;

            using (new EditorGUILayout.VerticalScope(cardStyle))
            {
                // Header line: time + description + elapsed/status
                using (new EditorGUILayout.HorizontalScope())
                {
                    var timeStr = entry.ReceivedAt.ToString("HH:mm:ss.fff");
                    string statusIcon;
                    if (isError) statusIcon = "✗";
                    else if (isStuck) statusIcon = "⚠";
                    else if (isActive) statusIcon = "⏳";
                    else if (entry.Phase == "disconnected") statusIcon = "⚡";
                    else if (isSystem) statusIcon = "●";
                    else statusIcon = "✓";

                    var headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 11 };
                    if (isError) headerStyle.normal.textColor = new Color(1f, 0.35f, 0.35f);
                    else if (isStuck) headerStyle.normal.textColor = new Color(1f, 0.78f, 0.15f);
                    else if (entry.Phase == "disconnected") headerStyle.normal.textColor = new Color(1f, 0.6f, 0.3f);
                    else if (isSystem) headerStyle.normal.textColor = new Color(0.5f, 0.8f, 1f);

                    EditorGUILayout.LabelField(
                        $"{statusIcon} {timeStr}  {entry.Description}",
                        headerStyle, GUILayout.MaxWidth(maxW * 0.65f));

                    GUILayout.FlexibleSpace();

                    if (entry.CompletedAt.HasValue)
                    {
                        EditorGUILayout.LabelField($"[{entry.ElapsedMs}ms]",
                            EditorStyles.miniLabel, GUILayout.Width(80));
                    }
                    else if (isActive)
                    {
                        var elapsed = (long)(DateTime.Now - entry.ReceivedAt).TotalMilliseconds;
                        EditorGUILayout.LabelField($"⏳ {elapsed / 1000.0:F1}s",
                            EditorStyles.miniLabel, GUILayout.Width(80));
                    }
                }

                // Command name + id
                EditorGUILayout.LabelField(
                    $"  cmd: {entry.CommandName}  id: {entry.CommandId}",
                    _styleInfo, GUILayout.MaxWidth(maxW));

                // Step records
                List<OperationStepRecord> steps;
                lock (entry.Steps) steps = new List<OperationStepRecord>(entry.Steps);

                foreach (var step in steps)
                {
                    var stepTime = step.Time.ToString("HH:mm:ss.fff");
                    var stepText = step.Detail != null
                        ? $"  ├ {stepTime}  {step.Step} | {step.Detail}"
                        : $"  ├ {stepTime}  {step.Step}";

                    GUIStyle stepStyle;
                    if (step.Step.StartsWith("⚠") || step.Step.StartsWith("失败") || step.Step.Contains("Agent上报"))
                        stepStyle = step.Step.StartsWith("失败") ? _styleError : _styleWarn;
                    else
                        stepStyle = _styleInfo;

                    EditorGUILayout.LabelField(stepText, stepStyle, GUILayout.MaxWidth(maxW));
                }

                // Progress bar (if applicable)
                if (entry.Progress >= 0 && isActive)
                {
                    var rect = GUILayoutUtility.GetRect(0, 14, GUILayout.MaxWidth(maxW - 20));
                    EditorGUI.ProgressBar(rect, entry.Progress / 100f, $"{entry.Progress}%");
                }

                // Error info
                if (isError && !string.IsNullOrEmpty(entry.ErrorMessage))
                {
                    EditorGUILayout.LabelField(
                        $"  └ {entry.ErrorCode}: {entry.ErrorMessage}",
                        _styleError, GUILayout.MaxWidth(maxW));
                }
            }
        }

        private GUIStyle GetLogLabelStyle(BridgeLogEntry entry)
        {
            if (entry.Level == "warn") return _styleWarn;
            if (entry.Level == "error") return _styleError;
            return _styleInfo;
        }

        private static string BuildLogEntryDisplayText(BridgeLogEntry entry)
        {
            if (!entry.IsWireStructured)
                return $"[{entry.Time:yyyy-MM-dd HH:mm:ss.fff}] {entry.Message}";

            var tag = entry.WireDirection == "TX" ? "[send]" : "[recv]";
            var ts = entry.WireEnvelopeUnixMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(entry.WireEnvelopeUnixMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff")
                : entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var line1 =
                $"{tag}  {ts}  |  sessionId={entry.WireSessionId}  |  name={entry.WireName}  |  type={entry.WireType}  |  id={entry.WireId}";

            if (!entry.WireIsRaw)
                return line1 + Environment.NewLine + entry.WireDetail;

            var body = UpilotWireJson.StripEnvelopeForDisplay(entry.WireDetail);
            return line1 + Environment.NewLine + body;
        }

        private void DrawTopStatusOverview(UpilotBridge bridge, BridgeStatus status, McpServerStatus mcpStatus)
        {
            var mcpMgr = UpilotMcpServerManager.Instance;
            var mcpHealthy = mcpStatus.IsRunning && mcpStatus.HttpPortListening && mcpStatus.WsPortListening;
            var isReady = mcpHealthy && status.IsWsOpen && status.IsAuthenticated;
            var isActive = mcpStatus.IsRunning || status.IsStarted;

            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("upilot", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();

                    var dotColor = isReady ? Color.green : isActive ? new Color(1f, 0.6f, 0f) : Color.gray;
                    var prev = GUI.color;
                    GUI.color = dotColor;
                    GUILayout.Label("●", GUILayout.Width(18));
                    GUI.color = prev;
                    GUILayout.Label(isReady ? "已就绪" : isActive ? "正在连接" : "已停止", GUILayout.Width(64));
                }

                EditorGUILayout.LabelField("Agent 客户端  ↔  MCP 服务  ↔  Unity 编辑器", EditorStyles.centeredGreyMiniLabel);
                EditorGUILayout.Space(4);

                if (!isActive)
                {
                    if (GUILayout.Button("启动 upilot", GUILayout.Height(28)))
                    {
                        bridge.EnsureStarted();
                        mcpMgr.StartServer();
                        ShowToast("upilot 启动中…");
                    }
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (!isReady && GUILayout.Button("重新连接", GUILayout.Height(26)))
                        {
                            bridge.Restart();
                            if (!mcpStatus.IsRunning)
                                mcpMgr.StartServer();
                            ShowToast("正在重新连接…");
                        }

                        if (GUILayout.Button("检查状态", GUILayout.Height(26)))
                        {
                            ShowToast(isReady ? "upilot 已就绪" : "服务正在启动或等待连接");
                        }

                        using (new EditorGUI.DisabledScope(status.IsCompiling && mcpStatus.IsRunning))
                        {
                            if (GUILayout.Button("停止", GUILayout.Width(72), GUILayout.Height(26)))
                            {
                                bridge.Stop();
                                if (mcpStatus.IsRunning)
                                    mcpMgr.StopServer();
                                ShowToast("upilot 已停止", MessageType.Warning);
                            }
                        }
                    }
                }

                EditorGUILayout.Space(4);
                DrawResponsiveStatusGrid(status, mcpStatus);
            }
        }

        private void DrawResponsiveStatusGrid(BridgeStatus status, McpServerStatus mcpStatus)
        {
            var (heartbeatText, heartbeatOk) = GetHeartbeatText(status);
            var mcpHealthy = mcpStatus.IsRunning && mcpStatus.HttpPortListening && mcpStatus.WsPortListening;
            DrawStatusGridRow(
                ("Unity 连接", status.IsWsOpen ? "已连接" : "未连接", status.IsWsOpen),
                ("MCP 服务", mcpHealthy ? "运行中" : mcpStatus.IsRunning ? "监听异常" : "已停止", mcpHealthy));
            DrawStatusGridRow(
                ("认证", status.IsAuthenticated ? "已完成" : "未完成", status.IsAuthenticated),
                ("编译", status.IsCompiling ? "编译中" : "空闲", !status.IsCompiling));
            DrawStatusGridRow(
                ("心跳", heartbeatText, heartbeatOk),
                ("Console 错误", status.LastErrorCount.ToString(), status.LastErrorCount == 0));
        }

        private void DrawStatusGridRow(
            (string label, string value, bool ok) left,
            (string label, string value, bool ok) right)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawStatusCell(left.label, left.value, left.ok);
                DrawStatusCell(right.label, right.value, right.ok);
            }
        }

        private void DrawStatusCell(string label, string value, bool ok)
        {
            var prev = GUI.color;
            GUI.color = ok
                ? new Color(0.2f, 0.75f, 0.3f, 0.14f)
                : new Color(0.95f, 0.45f, 0.2f, 0.16f);
            using (new EditorGUILayout.VerticalScope(GUI.skin.box, GUILayout.MinWidth(120)))
            {
                GUI.color = prev;
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(label, EditorStyles.miniBoldLabel);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(value, EditorStyles.miniLabel);
                }
            }
            GUI.color = prev;
        }

        private void DrawQuickStartSection(
            BridgeStatus status,
            McpServerStatus mcpStatus,
            AgentMcpConfigStatus[] configStatuses)
        {
            var configuredCount = CountConfiguredAgents(configStatuses);
            var mcpHealthy = mcpStatus.IsRunning && mcpStatus.HttpPortListening && mcpStatus.WsPortListening;
            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                EditorGUILayout.LabelField("快速开始", EditorStyles.boldLabel);
                DrawSetupStep("1", "首次设置", UpilotSetupState.IsCompleted, UpilotSetupState.IsCompleted ? "已完成" : "需要设置端口和 Agent");
                DrawSetupStep("2", "MCP 服务", mcpHealthy, mcpHealthy ? "运行中" : mcpStatus.IsRunning ? "监听异常" : "尚未启动");
                DrawSetupStep("3", "Unity 桥接器", status.IsAuthenticated, status.IsAuthenticated ? "已连接并认证" : status.IsStarted ? "等待连接" : "尚未启动");
                DrawSetupStep("4", "Agent 配置", configuredCount > 0, configuredCount > 0 ? $"{configuredCount}/3 已配置" : "尚未写入客户端配置");
                DrawSetupStep("5", "Agent 连接", mcpStatus.HttpClientCount > 0, mcpStatus.HttpClientCount > 0 ? $"已连接 {mcpStatus.HttpClientCount} 个" : "等待 Agent 客户端连接");

                EditorGUILayout.Space(4);
                if (!UpilotSetupState.IsCompleted)
                {
                    if (GUILayout.Button("打开首次设置向导", GUILayout.Height(24)))
                        UpilotFirstSetupWindow.Open();
                }
                else
                {
                    DrawAgentQuickActions();
                }
            }
        }

        private void DrawSetupStep(string number, string label, bool complete, string detail)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(number + ".", EditorStyles.miniBoldLabel, GUILayout.Width(18));
                var prev = GUI.color;
                GUI.color = complete ? Color.green : new Color(1f, 0.65f, 0.2f);
                GUILayout.Label(complete ? "✓" : "•", EditorStyles.boldLabel, GUILayout.Width(18));
                GUI.color = prev;
                GUILayout.Label(label, GUILayout.Width(82));
                GUILayout.Label(detail, EditorStyles.miniLabel);
            }
        }

        private void DrawAgentQuickActions()
        {
            EditorGUILayout.LabelField("配置 Agent", EditorStyles.miniBoldLabel);
            if (position.width < 500f)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawAgentConfigButton("Codex", () => UpilotAgentSetup.WriteCodexMcpConfig(promptBeforeOverwrite: true));
                    DrawAgentConfigButton("Claude", () => UpilotAgentSetup.WriteClaudeCodeMcpConfig(promptBeforeOverwrite: true));
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawAgentConfigButton("Cursor", () => UpilotAgentSetup.WriteCursorMcpConfig(promptBeforeOverwrite: true));
                    if (GUILayout.Button("写入规则", GUILayout.Height(22)))
                    {
                        Debug.Log("[upilot] Agent rules:\n" + UpilotAgentSetup.WriteAgentRules(overwriteExisting: false));
                        ShowToast("Agent 规则已检查/写入");
                    }
                }
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawAgentConfigButton("Codex", () => UpilotAgentSetup.WriteCodexMcpConfig(promptBeforeOverwrite: true));
                    DrawAgentConfigButton("Claude", () => UpilotAgentSetup.WriteClaudeCodeMcpConfig(promptBeforeOverwrite: true));
                    DrawAgentConfigButton("Cursor", () => UpilotAgentSetup.WriteCursorMcpConfig(promptBeforeOverwrite: true));
                    if (GUILayout.Button("写入规则", GUILayout.Height(22)))
                    {
                        Debug.Log("[upilot] Agent rules:\n" + UpilotAgentSetup.WriteAgentRules(overwriteExisting: false));
                        ShowToast("Agent 规则已检查/写入");
                    }
                }
            }
        }

        private void DrawAgentConfigButton(string label, Func<string> configure)
        {
            if (!GUILayout.Button(label, GUILayout.Height(22)))
                return;

            HandleAgentConfigResult(label, configure());
        }

        private static int CountConfiguredAgents(AgentMcpConfigStatus[] statuses)
        {
            var count = 0;
            if (statuses == null) return count;
            foreach (var status in statuses)
            {
                if (status.IsConfigured)
                    count++;
            }
            return count;
        }

        private void RefreshAgentConfigSnapshot()
        {
            _guiAgentConfigSnapshot = UpilotAgentSetup.GetMcpConfigStatuses();
            _lastAgentConfigRefresh = EditorApplication.timeSinceStartup;
        }

        private void DrawIssueGuidanceSection(
            UpilotBridge bridge,
            BridgeStatus status,
            McpServerStatus mcpStatus)
        {
            var manager = UpilotMcpServerManager.Instance;
            var pythonEntryValid = manager.IsPythonEntryValid(out var pythonEntryPath);
            var partialListeners = mcpStatus.IsRunning &&
                                   (!mcpStatus.HttpPortListening || !mcpStatus.WsPortListening);
            var suspectedPortConflict = mcpStatus.IsRunning &&
                                        !mcpStatus.ProcessId.HasValue &&
                                        (mcpStatus.HttpPortListening || mcpStatus.WsPortListening);
            var authenticationFailed = status.IsWsOpen && !status.IsAuthenticated;
            var bridgeDisconnected = status.IsStarted &&
                                     mcpStatus.WsPortListening &&
                                     !status.IsWsOpen;

            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                EditorGUILayout.LabelField("运行检查", EditorStyles.boldLabel);

                var issueCount = 0;
                if (!pythonEntryValid)
                {
                    issueCount++;
                    DrawGuidanceItem(
                        "Python 入口无效",
                        string.IsNullOrEmpty(pythonEntryPath) ? "未找到 MCP 服务启动脚本。" : pythonEntryPath,
                        "自动修复入口",
                        RepairPythonEntry);
                }

                if (suspectedPortConflict)
                {
                    issueCount++;
                    DrawGuidanceItem(
                        "端口可能被其他进程占用",
                        $"HTTP {manager.HttpPort} 或 WS {manager.WsPort} 已监听，但没有识别到 upilot MCP 进程。",
                        "切换空闲端口并重启",
                        () => RepairPortsAndRestart(bridge, mcpStatus));
                }
                else if (partialListeners)
                {
                    issueCount++;
                    DrawGuidanceItem(
                        "MCP 服务监听不完整",
                        $"HTTP：{(mcpStatus.HttpPortListening ? "正常" : "未监听")}；WS：{(mcpStatus.WsPortListening ? "正常" : "未监听")}。",
                        "重启 MCP 服务",
                        () =>
                        {
                            manager.RestartServer();
                            ShowToast("MCP 服务正在重启…");
                        });
                }

                if (authenticationFailed)
                {
                    issueCount++;
                    DrawGuidanceItem(
                        "Unity 认证未完成",
                        "网络连接已建立，但 MCP 服务与 Unity 尚未完成会话认证。",
                        "重新连接 Unity",
                        () =>
                        {
                            bridge.Restart();
                            ShowToast("Unity 桥接器正在重新连接…");
                        });
                }
                else if (bridgeDisconnected)
                {
                    issueCount++;
                    DrawGuidanceItem(
                        "Unity 桥接器未连接",
                        "MCP 服务正在监听，但 Unity WebSocket 尚未连接。",
                        "重新连接 Unity",
                        () =>
                        {
                            bridge.Restart();
                            ShowToast("Unity 桥接器正在重新连接…");
                        });
                }

                if (issueCount == 0)
                {
                    EditorGUILayout.LabelField(
                        mcpStatus.IsRunning || status.IsStarted
                            ? "✓ 未发现需要处理的连接异常"
                            : "服务当前已停止，可使用上方按钮启动。",
                        EditorStyles.miniLabel);
                }
            }
        }

        private void DrawGuidanceItem(string title, string detail, string actionLabel, Action action)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(detail, _styleInfo);
                if (GUILayout.Button(actionLabel, GUILayout.Height(22)))
                    action();
            }
        }

        private void RepairPythonEntry()
        {
            var manager = UpilotMcpServerManager.Instance;
            manager.ValidateAndAutoFixPath();
            if (!manager.IsPythonEntryValid(out _))
            {
                manager.ResetPythonEntryPathToDefaultAbsolute();
                manager.ValidateAndAutoFixPath();
            }

            ShowToast(
                manager.IsPythonEntryValid(out var path)
                    ? "Python 入口已修复：" + path
                    : "未能自动找到 Python 入口，请在设置页手动选择。",
                manager.IsPythonEntryValid(out _) ? MessageType.Info : MessageType.Error,
                4d);
        }

        private void RepairPortsAndRestart(UpilotBridge bridge, McpServerStatus mcpStatus)
        {
            var manager = UpilotMcpServerManager.Instance;
            if (mcpStatus.ProcessId.HasValue)
                manager.StopServer();
            bridge.Stop();

            var pair = UpilotPortAllocator.FindAvailablePair(_wsPortInput, _httpPortInput);
            _wsHostInput = UpilotBridge.DefaultWsHost;
            _wsPortInput = pair.wsPort;
            _httpPortInput = pair.httpPort;
            bridge.SetWsEndpoint(_wsHostInput, _wsPortInput);
            bridge.HttpPort = _httpPortInput;

            manager.InvalidateStatusCache();
            bridge.EnsureStarted();
            EditorApplication.delayCall += manager.StartServer;
            RefreshAgentConfigSnapshot();
            ShowToast($"已切换到 WS {_wsPortInput} / HTTP {_httpPortInput}，服务正在重启…", MessageType.Info, 4d);
        }

        private void DrawOverviewRuntimeSection(BridgeStatus status)
        {
            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                EditorGUILayout.LabelField("运行状态", EditorStyles.boldLabel);
                var playMode = string.IsNullOrEmpty(status.PlayModeState) || status.PlayModeState == "edit"
                    ? "编辑模式"
                    : status.PlayModeState;
                DrawRow("Unity 模式", playMode, true);
                var workspace = NormalizeMcpWorkspacePathForUi(status.McpWorkspaceAbsolutePath);
                DrawRow("工作区", string.IsNullOrEmpty(workspace) ? "—" : workspace, status.IsAuthenticated);

                EditorGUILayout.Space(2);
                if (GUILayout.Button("查看连接详情", GUILayout.Height(22)))
                {
                    _activeTab = 2;
                    _showConnectionDetails = true;
                }
            }
        }

        private void DrawToastIfAny()
        {
            if (string.IsNullOrEmpty(_toastMessage)) return;
            if (EditorApplication.timeSinceStartup > _toastExpireAt)
            {
                _toastMessage = string.Empty;
                return;
            }

            EditorGUILayout.HelpBox(_toastMessage, _toastType);
        }

        private void ShowToast(string message, MessageType type = MessageType.Info, double seconds = 2.6)
        {
            _toastMessage = message;
            _toastType = type;
            _toastExpireAt = EditorApplication.timeSinceStartup + seconds;
        }

        // ── Enable / Disable ──────────────────────────────────────────────────

        private void DrawEnableSection(UpilotBridge bridge, BridgeStatus status)
        {
            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                EditorGUILayout.LabelField("Unity 桥接器", EditorStyles.boldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    bool enabled    = UpilotBootstrap.IsEnabled;
                    bool newEnabled = EditorGUILayout.Toggle("自动启动", enabled);
                    if (newEnabled != enabled)
                        UpilotBootstrap.IsEnabled = newEnabled;
                }

                EditorGUILayout.LabelField(
                    status.IsAuthenticated
                        ? "已连接并完成认证"
                        : status.IsStarted ? "已启动，正在等待 MCP 服务连接" : "当前已停止",
                    EditorStyles.miniLabel);
                EditorGUILayout.Space(4);

                if (!status.IsStarted)
                {
                    if (GUILayout.Button("启动 Unity 桥接器", GUILayout.Height(26)))
                    {
                        bridge.EnsureStarted();
                        ShowToast("Unity 桥接器启动中…");
                    }
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(
                                status.IsAuthenticated ? "重新连接" : "立即重连",
                                GUILayout.Height(26)))
                        {
                            bridge.Restart();
                            ShowToast("Unity 桥接器正在重新连接…");
                        }
                        if (GUILayout.Button("停止", EditorStyles.miniButton, GUILayout.Width(72), GUILayout.Height(26)))
                        {
                            bridge.Stop();
                            ShowToast("Unity 桥接器已停止", MessageType.Warning);
                        }
                    }
                }

                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("日志选项", EditorStyles.miniBoldLabel);
                var debugWire = bridge.DebugWireLogsEnabled;
                var newDebugWire = EditorGUILayout.ToggleLeft("调试通信日志（收发命令）", debugWire);
                if (newDebugWire != debugWire)
                    bridge.DebugWireLogsEnabled = newDebugWire;

                EditorGUILayout.Space(2);
                var verboseLogs = bridge.VerboseLogsEnabled;
                var newVerboseLogs = EditorGUILayout.ToggleLeft("输出详细日志（心跳、连接、请求状态）", verboseLogs);
                if (newVerboseLogs != verboseLogs)
                    bridge.VerboseLogsEnabled = newVerboseLogs;

                EditorGUILayout.Space(2);
                var logToConsole = Logger.LogToUnityConsole;
                var newLogToConsole = EditorGUILayout.ToggleLeft(
                    new GUIContent("日志同步到 Unity Console", "关闭后仍会写入 upilot 日志文件。"),
                    logToConsole);
                if (newLogToConsole != logToConsole)
                {
                    Logger.SetLogToUnityConsole(newLogToConsole);
                    ShowToast(newLogToConsole ? "已开启 Unity Console 日志输出" : "已关闭 Unity Console 日志输出");
                }
            }
        }

        private void DrawSharedEndpointSection(UpilotBridge bridge, BridgeStatus status)
        {
            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _showEndpointSettings = EditorGUILayout.Foldout(
                        _showEndpointSettings,
                        "连接地址",
                        true,
                        EditorStyles.foldoutHeader);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField(
                        $"{_wsHostInput} · WS {_wsPortInput} · HTTP {_httpPortInput}",
                        EditorStyles.miniLabel,
                        GUILayout.MaxWidth(220));
                }

                if (!_showEndpointSettings)
                    return;

                EditorGUILayout.HelpBox("设置仅保存在本机当前项目中。运行时需要先停止桥接器才能修改。", MessageType.None);

                _wsHostInput = EditorGUILayout.TextField("主机地址", _wsHostInput);
                _wsPortInput = EditorGUILayout.IntField("Unity WS 端口", _wsPortInput);
                _httpPortInput = EditorGUILayout.IntField("MCP HTTP 端口", _httpPortInput);

                using (new EditorGUI.DisabledScope(status.IsStarted))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("恢复默认", GUILayout.Height(22)))
                        {
                            _wsHostInput = UpilotBridge.DefaultWsHost;
                            _wsPortInput = UpilotBridge.DefaultWsPort;
                            _httpPortInput = UpilotBridge.DefaultHttpPort;
                        }

                        if (GUILayout.Button("应用", GUILayout.Height(22)))
                        {
                            if (_wsPortInput <= 0) _wsPortInput = UpilotBridge.DefaultWsPort;
                            if (_httpPortInput <= 0) _httpPortInput = UpilotBridge.DefaultHttpPort;
                            if (string.IsNullOrWhiteSpace(_wsHostInput)) _wsHostInput = UpilotBridge.DefaultWsHost;
                            bridge.SetWsEndpoint(_wsHostInput, _wsPortInput);
                            bridge.HttpPort = _httpPortInput;
                            ShowToast($"已应用 ws://{_wsHostInput}:{_wsPortInput}  http://{_wsHostInput}:{_httpPortInput}/mcp");
                        }
                    }
                }

                if (status.IsStarted)
                    EditorGUILayout.LabelField("桥接器运行中，连接地址暂不可修改。", EditorStyles.miniLabel);
            }
        }

        // ── Connection status ─────────────────────────────────────────────────

        private void DrawConnectionSection(BridgeStatus status)
        {
            InitStyles();
            // var prevVs = EditorGUIUtility.standardVerticalSpacing;
            // EditorGUIUtility.standardVerticalSpacing = 2f;
            try
            {
                using (new EditorGUILayout.VerticalScope(_styleBoxConnection))
                {
                    _showConnectionDetails = EditorGUILayout.Foldout(
                        _showConnectionDetails,
                        "连接详情",
                        true,
                        EditorStyles.foldoutHeader);
                    if (!_showConnectionDetails)
                    {
                        EditorGUILayout.LabelField(
                            status.IsAuthenticated ? "Unity 与 MCP 已建立认证连接" : "尚未建立完整连接",
                            EditorStyles.miniLabel);
                        return;
                    }

                    {
                        var pathUi = NormalizeMcpWorkspacePathForUi(status.McpWorkspaceAbsolutePath);
                        if (status.IsAuthenticated && !string.IsNullOrEmpty(pathUi))
                            DrawMcpWorkspaceRow("MCP 工作区", pathUi, true);
                        else
                            DrawRow("MCP 工作区", "—", false);
                    }
                    DrawRow("Session ID", string.IsNullOrEmpty(status.SessionId) ? "—" : status.SessionId, true);
                    {
                        string mcpVal;
                        bool mcpOk;
                        if (status.IsAuthenticated)
                        {
                            var ep = $"ws://{status.McpServerHost}:{status.McpServerPort}";
                            mcpVal = string.IsNullOrEmpty(status.McpLabel) ? ep : $"{status.McpLabel}  ·  {ep}";
                            mcpOk = true;
                        }
                        else
                        {
                            mcpVal = "—";
                            mcpOk = false;
                        }
                        DrawRow("MCP 服务", mcpVal, mcpOk);
                    }

                    // Show Bridge actual WS endpoint for diagnostics
                    var bridge = UpilotBridge.Instance;
                    DrawRow("Unity WS 地址", $"ws://{bridge.WsHost}:{bridge.WsPort}", !status.IsStarted || status.IsWsOpen);
                }
            }
            finally
            {
                // EditorGUIUtility.standardVerticalSpacing = prevVs;
            }
        }

        // ── MCP Server Management ─────────────────────────────────────────────

        private void DrawMcpServerSection(BridgeStatus status, McpServerStatus mcpStatus)
        {
            var mgr = UpilotMcpServerManager.Instance;

            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                EditorGUILayout.LabelField("MCP 服务", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "MCP 服务作为独立进程运行，不受 Unity 编译和 Domain Reload 影响。",
                    MessageType.None);

                using (new EditorGUILayout.HorizontalScope())
                {
                    bool autoStart = mgr.AutoStartEnabled;
                    bool newAutoStart = EditorGUILayout.Toggle("自动启动", autoStart);
                    if (newAutoStart != autoStart)
                    {
                        mgr.AutoStartEnabled = newAutoStart;
                        ShowToast(newAutoStart ? "已开启 MCP 服务自动启动" : "已关闭 MCP 服务自动启动");
                    }
                    EditorGUILayout.LabelField("Unity 启动时自动启动 MCP 服务", EditorStyles.miniLabel);
                }

                bool isRunning = mcpStatus.IsRunning;
                bool isCompiling = status.IsCompiling;

                // ── Configuration ──
                if (isRunning)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("日志级别", EditorStyles.miniBoldLabel, GUILayout.Width(62));
                        EditorGUILayout.LabelField(mgr.LogLevel, EditorStyles.miniLabel);
                    }
                    EditorGUILayout.LabelField("Python 入口", EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField(mgr.PythonEntryPath, _styleInfo);
                    EditorGUILayout.LabelField("停止 MCP 服务后可修改以上配置。", EditorStyles.miniLabel);
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        string[] levels = new[] { "DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL" };
                        int lvlIdx = Array.IndexOf(levels, mgr.LogLevel);
                        if (lvlIdx < 0) lvlIdx = 1;
                        EditorGUILayout.LabelField("日志级别", GUILayout.Width(62));
                        lvlIdx = EditorGUILayout.Popup(lvlIdx, levels, GUILayout.Width(110));
                        mgr.LogLevel = levels[lvlIdx];
                    }

                    EditorGUILayout.LabelField("Python 入口", EditorStyles.miniBoldLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        var newPath = EditorGUILayout.TextField(mgr.PythonEntryPath);
                        if (EditorGUI.EndChangeCheck())
                            mgr.SetPythonEntryPath(newPath);
                        if (GUILayout.Button("重置", GUILayout.Width(52)))
                        {
                            mgr.ResetPythonEntryPathToDefaultAbsolute();
                            ShowToast("Python 入口已重置为默认绝对路径");
                        }
                    }
                }

                EditorGUILayout.Space(4);
                if (!isRunning)
                {
                    if (GUILayout.Button("启动 MCP 服务", GUILayout.Height(26)))
                    {
                        mgr.StartServer();
                        ShowToast("MCP 服务启动中…");
                    }
                }
                else
                {
                    var listenersHealthy = mcpStatus.HttpPortListening && mcpStatus.WsPortListening;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (!mcpStatus.ProcessId.HasValue)
                        {
                            if (GUILayout.Button("查看端口异常", GUILayout.Height(26)))
                                _activeTab = 0;
                        }
                        else if (!listenersHealthy)
                        {
                            if (GUILayout.Button("重启 MCP 服务", GUILayout.Height(26)))
                            {
                                mgr.RestartServer();
                                ShowToast("MCP 服务正在重启…");
                            }
                        }
                        else
                        {
                            if (GUILayout.Button("检查状态", GUILayout.Height(26)))
                                ShowToast($"MCP 服务正常，PID={mcpStatus.ProcessId}");
                        }

                        using (new EditorGUI.DisabledScope(isCompiling))
                        {
                            if (GUILayout.Button("停止", EditorStyles.miniButton, GUILayout.Width(72), GUILayout.Height(26)))
                            {
                                mgr.StopServer();
                                ShowToast("MCP 服务已停止", MessageType.Warning);
                            }
                        }
                    }
                }

                if (isCompiling && isRunning)
                {
                    EditorGUILayout.LabelField("Unity 编译中，停止操作暂不可用。", EditorStyles.miniLabel);
                }

                // ── Status ──
                EditorGUILayout.Space(2);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var dotColor = mcpStatus.IsRunning
                        ? (mcpStatus.HttpPortListening && mcpStatus.WsPortListening ? Color.green : new Color(1f, 0.6f, 0f))
                        : Color.gray;
                    var prev = GUI.color;
                    GUI.color = dotColor;
                    GUILayout.Label("●", GUILayout.Width(18));
                    GUI.color = prev;

                    var st = mcpStatus.IsRunning
                        ? $"运行中 · PID {mcpStatus.ProcessId}"
                        : "已停止";
                    EditorGUILayout.LabelField(st, EditorStyles.miniLabel);
                }

                if (mcpStatus.IsRunning)
                {
                    EditorGUILayout.LabelField(
                        $"HTTP {mgr.HttpPort}：{(mcpStatus.HttpPortListening ? "正常" : "未监听")}    ·    WS {mgr.WsPort}：{(mcpStatus.WsPortListening ? "正常" : "未监听")}",
                        EditorStyles.miniLabel);
                    EditorGUILayout.LabelField(
                        $"Unity 客户端：{mcpStatus.WsClientCount}    ·    Agent 客户端：{mcpStatus.HttpClientCount}",
                        EditorStyles.miniLabel);
                }

                if (isRunning && !string.IsNullOrEmpty(mcpStatus.ProcessCommandLine))
                {
                    var cmd = mcpStatus.ProcessCommandLine;
                    if (cmd.Length > 240) cmd = cmd.Substring(0, 240) + "…";
                    EditorGUILayout.LabelField(
                        new GUIContent("进程命令", mcpStatus.ProcessCommandLine),
                        EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField(cmd, _styleInfo);
                }
            }

        }

        private void DrawAgentSetupSection(AgentMcpConfigStatus[] statuses)
        {
            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                EditorGUILayout.LabelField("Agent 配置", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "检测当前项目中的 Codex、Claude Code 和 Cursor MCP 配置。端口变化后会提示更新。",
                    MessageType.None);

                EditorGUILayout.LabelField("MCP 地址：" + UpilotAgentSetup.McpUrl, EditorStyles.miniLabel);
                EditorGUILayout.Space(3);

                if (statuses != null)
                {
                    foreach (var configStatus in statuses)
                        DrawAgentConfigStatusRow(configStatus);
                }

                EditorGUILayout.Space(4);
                if (GUILayout.Button("检查并写入 Agent 识别规则", GUILayout.Height(22)))
                {
                    Debug.Log("[upilot] Agent rules:\n" + UpilotAgentSetup.WriteAgentRules(overwriteExisting: false));
                    ShowToast("Agent 识别规则已检查/写入");
                }
            }
        }

        private void DrawAgentConfigStatusRow(AgentMcpConfigStatus status)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                var prev = GUI.color;
                GUI.color = status.IsConfigured ? Color.green : new Color(1f, 0.65f, 0.2f);
                GUILayout.Label(status.IsConfigured ? "✓" : "!", EditorStyles.boldLabel, GUILayout.Width(18));
                GUI.color = prev;

                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField(status.ClientName, EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField(
                        new GUIContent(status.StateText, status.ConfigPath +
                            (string.IsNullOrEmpty(status.ErrorMessage) ? "" : "\n" + status.ErrorMessage)),
                        EditorStyles.miniLabel);
                }

                if (GUILayout.Button(status.IsConfigured ? "更新" : "写入", GUILayout.Width(54), GUILayout.Height(22)))
                    ConfigureAgentClient(status.ClientName);
            }
        }

        private void ConfigureAgentClient(string clientName)
        {
            if (clientName == "Codex")
                HandleAgentConfigResult("Codex", UpilotAgentSetup.WriteCodexMcpConfig(promptBeforeOverwrite: true));
            else if (clientName == "Claude Code")
                HandleAgentConfigResult("Claude Code", UpilotAgentSetup.WriteClaudeCodeMcpConfig(promptBeforeOverwrite: true));
            else if (clientName == "Cursor")
                HandleAgentConfigResult("Cursor", UpilotAgentSetup.WriteCursorMcpConfig(promptBeforeOverwrite: true));
        }

        private void HandleAgentConfigResult(string clientName, string result)
        {
            Debug.Log($"[upilot] {clientName} MCP config:\n" + result);
            RefreshAgentConfigSnapshot();
            ShowToast($"{clientName} MCP 配置已处理");
        }

        // ── Diagnostics ───────────────────────────────────────────────────────

        private void DrawDiagnosticsSection(UpilotBridge bridge, string diagResult, bool diagRunning, long diagResultAtMs)
        {
            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                EditorGUILayout.LabelField("诊断 / 通信测试", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(diagRunning))
                    {
                        if (GUILayout.Button("测试服务器", GUILayout.Height(22)))
                        {
                            var snap = bridge.GetStatus();
                            RunDiag(() => Task.FromResult(BuildServerTestResult(snap, bridge.WsHost, bridge.WsPort)));
                            ShowToast("已执行服务器测试");
                        }
                    }

                    using (new EditorGUI.DisabledScope(diagRunning))
                    {
                        if (GUILayout.Button("检查服务器", GUILayout.Height(22)))
                        {
                            RunDiag(CheckPythonMcpProcess);
                            ShowToast("已执行服务器检查");
                        }
                    }
                }

                if (!string.IsNullOrEmpty(diagResult))
                {
                    EditorGUILayout.Space(2);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("诊断日志", EditorStyles.miniBoldLabel, GUILayout.Width(48));
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("复制", EditorStyles.miniButton, GUILayout.Width(36)))
                        {
                            GUIUtility.systemCopyBuffer = diagResult;
                            ShowToast("已复制诊断结果");
                        }
                    }

                    _diagScroll = EditorGUILayout.BeginScrollView(_diagScroll, GUILayout.Height(92));
                    DrawDiagLines(diagResult, diagResultAtMs);
                    EditorGUILayout.EndScrollView();
                }

                if (diagRunning)
                    EditorGUILayout.LabelField("正在测试…", EditorStyles.centeredGreyMiniLabel);

                EditorGUILayout.Space(6);
                _showDangerousDiagnostics = EditorGUILayout.Foldout(
                    _showDangerousDiagnostics,
                    "高级诊断与危险操作",
                    true,
                    EditorStyles.foldoutHeader);
                if (_showDangerousDiagnostics)
                {
                    EditorGUILayout.HelpBox(
                        "仅在普通停止操作无效、残留进程持续占用端口时使用。该操作会结束所有疑似 upilot MCP Python 进程。",
                        MessageType.Warning);
                    using (new EditorGUI.DisabledScope(diagRunning))
                    {
                        var prev = GUI.backgroundColor;
                        GUI.backgroundColor = new Color(1f, 0.45f, 0.45f);
                        if (GUILayout.Button("结束所有疑似 MCP 服务进程", GUILayout.Height(24)))
                        {
                            bool confirmed = _skipKillConfirm || EditorUtility.DisplayDialog(
                                "确认结束 MCP 服务进程？",
                                "此操作会结束所有疑似 upilot MCP Python 进程。仅在普通停止操作无效时继续。",
                                "确认结束",
                                "取消");
                            if (confirmed)
                            {
                                RunDiag(KillPythonMcpProcesses);
                                ShowToast("已执行进程清理", MessageType.Warning);
                            }
                        }
                        GUI.backgroundColor = prev;
                    }
                }
            }
        }

        private void DrawLogFileSection()
        {
            using (new EditorGUILayout.VerticalScope(_styleBox))
            {
                EditorGUILayout.LabelField("日志文件", EditorStyles.boldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("打开日志文件", GUILayout.Height(22)))
                    {
                        Logger.RevealLogFile();
                        ShowToast("已定位到 " + Logger.LogFilePath);
                    }
                }
            }
        }

        private void DrawLogToolbar(List<BridgeLogEntry> logs, UpilotBridge bridge)
        {
            var toolbarMaxW = Mathf.Max(50f, position.width - 16f);
            float row1Min = 52 + 88 + 70 + 60 + 24;
            float row2Min = 40 * 3 + 44 * 2 + 52 + 36 + 24;
            float toolbarDesired = Mathf.Max(row1Min, row2Min);
            UpilotWindowDiagnostics.RecordSection("logToolbar", toolbarDesired, toolbarMaxW);

            using (new EditorGUILayout.VerticalScope(GUILayout.MaxWidth(toolbarMaxW)))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("通信日志", EditorStyles.boldLabel, GUILayout.Width(52));
                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button("打开日志文件", EditorStyles.miniButton, GUILayout.Width(88)))
                    {
                        Logger.RevealLogFile();
                        ShowToast("已定位到 " + Logger.LogFilePath);
                    }

                    var debugWire = bridge.DebugWireLogsEnabled;
                    var newDebugWire = GUILayout.Toggle(debugWire, "调试通信", EditorStyles.miniButton, GUILayout.Width(70));
                    if (newDebugWire != debugWire)
                    {
                        bridge.DebugWireLogsEnabled = newDebugWire;
                        ShowToast(newDebugWire ? "已开启调试通信日志（含实时 RAW）" : "已关闭调试通信日志");
                    }

                    _autoScroll = GUILayout.Toggle(_autoScroll, "自动滚动", EditorStyles.miniButton, GUILayout.Width(60));
                }

                EditorGUILayout.Space(2);
                using (new EditorGUILayout.HorizontalScope())
                {
                    _showInfo    = GUILayout.Toggle(_showInfo,    "Info",    EditorStyles.miniButtonLeft, GUILayout.Width(40));
                    _showWarn    = GUILayout.Toggle(_showWarn,    "Warn",    EditorStyles.miniButtonMid,  GUILayout.Width(40));
                    _showError   = GUILayout.Toggle(_showError,   "Error",   EditorStyles.miniButtonMid,  GUILayout.Width(44));
                    _showCompile = GUILayout.Toggle(_showCompile, "编译",    EditorStyles.miniButtonMid,  GUILayout.Width(44));
                    _showNetwork = GUILayout.Toggle(_showNetwork, "网络",    EditorStyles.miniButtonRight, GUILayout.Width(44));

                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button("复制全部", EditorStyles.miniButton, GUILayout.Width(52)))
                    {
                        CopyFilteredLogs(logs);
                        ShowToast("已复制过滤后的日志");
                    }

                    if (GUILayout.Button("清除", EditorStyles.miniButton, GUILayout.Width(36)))
                    {
                        bridge.ClearLogs();
                        ShowToast("日志已清除");
                    }
                }
            }
        }

        private bool ShouldShowLogEntry(BridgeLogEntry entry)
        {
            if (entry.Level == "info"  && !_showInfo)  return false;
            if (entry.Level == "warn"  && !_showWarn)  return false;
            if (entry.Level == "error" && !_showError) return false;

            var msg = entry.Message ?? string.Empty;
            if (!_showCompile && msg.IndexOf("编译", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (!_showNetwork)
            {
                var isNetwork = msg.IndexOf("WS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                msg.IndexOf("session.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                msg.IndexOf("connect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                msg.IndexOf("heartbeat", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isNetwork) return false;
            }

            return true;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static (string text, bool ok) GetHeartbeatText(BridgeStatus status)
        {
            if (!status.IsStarted)
                return ("未启动", false);
            if (!status.IsWsOpen)
                return ("未连接", false);
            if (status.LastHeartbeatSentAt <= 0)
                return ("等待首个心跳", true);

            var elapsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - status.LastHeartbeatSentAt;
            string text;
            if (elapsedMs < 1000)
                text = "< 1 s 前";
            else
                text = $"{elapsedMs / 1000.0:F1} s 前";
            bool ok = elapsedMs < 5000;
            return (text, ok);
        }

        private void DrawRow(string label, string value, bool ok)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(96), GUILayout.ExpandHeight(false));
                var prev = GUI.color;
                GUI.color = ok ? Color.white : new Color(1f, 0.6f, 0.4f);
                float maxW = Mathf.Max(60f, position.width - 96f - 40f);
                // var style = new GUIStyle(EditorStyles.label) { wordWrap = true };
                var style =EditorStyles.label;
                EditorGUILayout.LabelField(value ?? "", style, GUILayout.MaxWidth(maxW));
                GUI.color = prev;
            }
        }

        /// <summary>Trim、统一 Windows 下路径分隔符，便于单行/多行 Label 展示。</summary>
        private static string NormalizeMcpWorkspacePathForUi(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";
            var s = raw.Trim();
            if (Application.platform == RuntimePlatform.WindowsEditor)
                s = s.Replace('/', '\\');
            return s;
        }

        /// <summary>MCP 工作区路径：单行 Horizontal，右侧 Label 换行；不再套 VerticalScope，避免额外行距。</summary>
        private static void DrawMcpWorkspaceRow(string label, string value, bool ok)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(96));
                var prev = GUI.color;
                GUI.color = ok ? Color.white : new Color(1f, 0.6f, 0.4f);
                var style = new GUIStyle(EditorStyles.label)
                {
                    wordWrap = true,
                    margin  = new RectOffset(0, 0, 0, 0),
                };
                EditorGUILayout.LabelField(value ?? "", style, GUILayout.ExpandWidth(true));
                GUI.color = prev;
            }
        }

        private void CopyFilteredLogs(List<BridgeLogEntry> logs)
        {
            var sb = new StringBuilder();
            var first = true;
            foreach (var e in logs)
            {
                if (!ShouldShowLogEntry(e)) continue;
                if (!first) sb.AppendLine();
                first = false;
                sb.AppendLine(BuildLogEntryDisplayText(e));
            }
            GUIUtility.systemCopyBuffer = sb.ToString();
        }

        private void DrawDiagLines(string diagResult, long diagResultAtMs)
        {
            var lines = diagResult.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var ts = diagResultAtMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(diagResultAtMs).LocalDateTime
                : DateTime.Now;
            foreach (var raw in lines)
            {
                var line = raw ?? string.Empty;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var baseStyle = line.StartsWith("✗") || line.StartsWith("异常") || line.StartsWith("检查失败")
                    ? _styleError
                    : line.StartsWith("✓")
                        ? _styleInfo
                        : _styleWarn;
                var style = new GUIStyle(baseStyle) { wordWrap = true };

                EditorGUILayout.LabelField($"[{ts:HH:mm:ss.fff}] {line}", style);
            }
        }

        private void RunDiag(Func<Task<string>> taskFactory)
        {
            _diagRunning = true;
            _diagResult  = "";
            _diagResultAtMs = 0;
            Task.Run(async () =>
            {
                try
                {
                    _diagResult = await taskFactory();
                    _diagResultAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
                catch (Exception ex)
                {
                    _diagResult = $"异常: {ex.Message}";
                    _diagResultAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
                finally { _diagRunning = false; }
            });
        }

        private static string BuildServerTestResult(BridgeStatus status, string wsHost, int wsPort)
        {
            if (!status.IsStarted) return "✗ Unity 桥接器未启动";
            if (!status.IsWsOpen)
            {
                var mgr = UpilotMcpServerManager.Instance;
                var sb = new StringBuilder();
                sb.AppendLine($"✗ WS 未连接  Unity 桥接器尝试: ws://{wsHost}:{wsPort}");
                sb.AppendLine($"  MCP 服务 HTTP 端口: {mgr.HttpPort}  WS 端口: {mgr.WsPort}");
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    client.Connect("127.0.0.1", mgr.WsPort);
                    sb.AppendLine($"  MCP WS 端口 {mgr.WsPort} 可连通 → 服务正在监听，但 Unity 桥接器未完成连接");
                }
                catch
                {
                    sb.AppendLine($"  MCP WS 端口 {mgr.WsPort} 无法连通 → 服务未监听该端口，请检查 MCP 服务是否已启动");
                }
                return sb.ToString().TrimEnd();
            }
            if (!status.IsAuthenticated) return "✗ 尚未认证";

            var hbText = "未发送";
            if (status.LastHeartbeatSentAt > 0)
            {
                var elapsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - status.LastHeartbeatSentAt;
                hbText = elapsedMs < 1000 ? "<1s" : $"{elapsedMs / 1000.0:F1}s";
            }

            return "✓ 服务器测试通过\n" +
                   $"  WS      : ws://{wsHost}:{wsPort}\n" +
                   $"  认证状态: 已认证\n" +
                   $"  Session : {status.SessionId}\n" +
                   $"  心跳延迟: {hbText}\n" +
                   $"  编译中  : {(status.IsCompiling ? "是" : "否")}\n" +
                   $"  编译错误: {status.LastErrorCount}";
        }

        private static Task<string> CheckPythonMcpProcess()
        {
            try
            {
                var p1 = System.Diagnostics.Process.GetProcessesByName("python");
                var p2 = System.Diagnostics.Process.GetProcessesByName("python3");
                var all = new List<System.Diagnostics.Process>(p1.Length + p2.Length);
                all.AddRange(p1);
                all.AddRange(p2);

                if (all.Count == 0)
                    return Task.FromResult("✗ 未检测到 python / python3 进程\n  请先启动 MCP 服务");

                var portsByPid = SafeGetListeningPortsByPid(out var portsFetched);

                int upilotLikeCount = 0;
                var summary = new StringBuilder();
                var detail = new StringBuilder();

                summary.AppendLine($"检测到 {all.Count} 个 Python 进程（简要）:");
                detail.AppendLine("[upilot] Python 进程诊断（详细）");

                foreach (var p in all)
                {
                    string exePath = SafeGetExePath(p);
                    string cmdLine = SafeGetCommandLine(p.Id);
                    int parentPid = SafeGetParentProcessId(p.Id);
                    string parentName = SafeGetProcessNameById(parentPid);

                    bool isUpilotLike = IsUpilotMcpLike(cmdLine);

                    string listenPortText = "unkown";
                    if (portsFetched && portsByPid.TryGetValue(p.Id, out var listenPorts) && listenPorts.Count > 0)
                        listenPortText = string.Join(",", listenPorts);

                    if (isUpilotLike) upilotLikeCount++;

                    var tag = isUpilotLike ? "✓" : "?";
                    summary.AppendLine($"[{tag}] PID={p.Id}  PPID={parentPid}({parentName})  PORT={listenPortText}");

                    detail.AppendLine($"[{tag}] PID={p.Id}  PPID={parentPid}({parentName})");
                    detail.AppendLine($"    PORT: {listenPortText}");
                    detail.AppendLine($"    EXE: {exePath}");
                    detail.AppendLine($"    CMD: {cmdLine}");
                }

                if (!portsFetched)
                    summary.AppendLine("! 监听端口读取失败，已显示为 unkown");

                if (upilotLikeCount == 0)
                {
                    summary.AppendLine("✗ 未发现明显的 upilot MCP 进程特征");
                    summary.AppendLine("  请确认命令行包含 run_upilot_mcp.py、upilot-mcp 或 upilot_mcp");
                }
                else
                {
                    summary.AppendLine($"✓ 疑似 upilot MCP 进程数量: {upilotLikeCount}");
                }

                summary.AppendLine("(详细命令行已输出到 Console 的 Warning 日志)");
                Debug.LogWarning(detail.ToString().TrimEnd());
                return Task.FromResult(summary.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return Task.FromResult($"检查失败: {ex.Message}");
            }
        }

        private static Task<string> KillPythonMcpProcesses()
        {
            try
            {
                var p1 = System.Diagnostics.Process.GetProcessesByName("python");
                var p2 = System.Diagnostics.Process.GetProcessesByName("python3");
                var all = new List<System.Diagnostics.Process>(p1.Length + p2.Length);
                all.AddRange(p1);
                all.AddRange(p2);

                var targets = new List<System.Diagnostics.Process>();
                foreach (var p in all)
                {
                    var cmd = SafeGetCommandLine(p.Id);
                    if (IsUpilotMcpLike(cmd))
                        targets.Add(p);
                }

                if (targets.Count == 0)
                    return Task.FromResult("✓ 未发现需要终止的服务器进程");

                int killed = 0;
                var sb = new StringBuilder();
                sb.AppendLine($"准备终止 {targets.Count} 个疑似服务器进程：");

                foreach (var p in targets)
                {
                    try
                    {
                        int pid = p.Id;
                        var cmd = SafeGetCommandLine(pid);
                        p.Kill();
                        killed++;
                        sb.AppendLine($"✓ 已终止 PID={pid}");
                        Debug.LogWarning($"[upilot] 已终止服务器进程 PID={pid}\nCMD: {cmd}");
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"✗ 终止失败 PID={p.Id}: {ex.Message}");
                    }
                }

                sb.AppendLine($"完成：成功 {killed}/{targets.Count}");
                return Task.FromResult(sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return Task.FromResult($"终止进程失败: {ex.Message}");
            }
        }

        private static bool IsUpilotMcpLike(string cmdLine)
        {
            return ContainsIgnoreCase(cmdLine, "run_upilot_mcp.py") ||
                   ContainsIgnoreCase(cmdLine, "upilot-mcp") ||
                   ContainsIgnoreCase(cmdLine, "upilot_mcp");
        }

        private static string SafeGetExePath(System.Diagnostics.Process process)
        {
            try
            {
                return process.MainModule?.FileName ?? "(未知)";
            }
            catch
            {
                return "(无权限或不可读取)";
            }
        }

        private static int SafeGetParentProcessId(int pid)
        {
#if UNITY_EDITOR_WIN
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "wmic",
                    Arguments = $"process where ProcessId={pid} get ParentProcessId /value",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return -1;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(1000);

                var marker = "ParentProcessId=";
                var idx = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return -1;
                var value = output.Substring(idx + marker.Length).Trim();
                return int.TryParse(value, out var ppid) ? ppid : -1;
            }
            catch
            {
                return -1;
            }
#else
            return -1;
#endif
        }

        private static string SafeGetProcessNameById(int pid)
        {
            if (pid <= 0) return "unknown";
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(pid);
                return proc.ProcessName;
            }
            catch
            {
                return "unknown";
            }
        }

        private static string SafeGetCommandLine(int pid)
        {
#if UNITY_EDITOR_WIN
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "wmic",
                    Arguments = $"process where ProcessId={pid} get CommandLine /value",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return "(读取命令行失败)";
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(1000);

                if (string.IsNullOrWhiteSpace(output)) return "(空)";
                var marker = "CommandLine=";
                var idx = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return output.Trim();

                var cmd = output.Substring(idx + marker.Length).Trim();
                return string.IsNullOrWhiteSpace(cmd) ? "(空)" : cmd;
            }
            catch
            {
                return "(读取命令行失败)";
            }
#else
            return "(当前平台未实现命令行读取)";
#endif
        }

        private static Dictionary<int, List<int>> SafeGetListeningPortsByPid(out bool success)
        {
            var result = new Dictionary<int, List<int>>();
            success = false;

#if UNITY_EDITOR_WIN
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = "-ano -p tcp",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return result;

                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(2000);

                if (string.IsNullOrWhiteSpace(output))
                    return result;

                var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var raw in lines)
                {
                    var line = (raw ?? string.Empty).Trim();
                    if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 5)
                        continue;

                    var localAddress = parts[1];
                    var state = parts[3];
                    if (!state.Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!int.TryParse(parts[4], out var pid) || pid <= 0)
                        continue;

                    int port = SafeParsePortFromEndpoint(localAddress);
                    if (port <= 0)
                        continue;

                    if (!result.TryGetValue(pid, out var list))
                    {
                        list = new List<int>();
                        result[pid] = list;
                    }

                    if (!list.Contains(port))
                        list.Add(port);
                }

                success = true;
            }
            catch
            {
                success = false;
            }
#endif

            return result;
        }

        private static int SafeParsePortFromEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                return -1;

            int idx = endpoint.LastIndexOf(':');
            if (idx < 0 || idx >= endpoint.Length - 1)
                return -1;

            var portText = endpoint.Substring(idx + 1).Trim();
            return int.TryParse(portText, out var port) ? port : -1;
        }

        private static bool ContainsIgnoreCase(string text, string token)
        {
            return !string.IsNullOrEmpty(text) &&
                   text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
