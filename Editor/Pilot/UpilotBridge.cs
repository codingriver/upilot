// -----------------------------------------------------------------------
// Upilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace codingriver.upilot
{
    // ── Status snapshot returned to the status window ──────────────────────────
    public struct BridgeStatus
    {
        public bool IsStarted;
        public bool IsWsOpen;
        public bool IsAuthenticated;
        public string SessionId;
        public long LastHeartbeatSentAt;   // unix ms, 0 = never
        public bool IsCompiling;
        public int  LastErrorCount;
        public string PlayModeState;
        /// <summary>服务端 session.hello 返回的可选 MCP 显示名（与 Cursor mcpServers 中 --label 一致）。</summary>
        public string McpLabel;
        /// <summary>服务端 ack 中的监听地址（与 MCP 进程实际一致）。</summary>
        public string McpServerHost;
        public int McpServerPort;
        /// <summary>MCP 服务端进程工作区绝对路径（与 Cursor 工程目录一致，由服务端 ack 提供）。</summary>
        public string McpWorkspaceAbsolutePath;
    }

    // ── Log entry ───────────────────────────────────────────────────────────────
    public readonly struct BridgeLogEntry
    {
        public readonly DateTime Time;
        public readonly string   Level; // "info" | "warn" | "error"
        public readonly string   Message;
        public readonly bool     IsWireStructured;
        public readonly string   WireDirection;
        public readonly bool     WireIsRaw;
        public readonly long     WireEnvelopeUnixMs;
        public readonly string   WireSessionId;
        public readonly string   WireName;
        public readonly string   WireType;
        public readonly string   WireId;
        public readonly string   WireDetail;

        public BridgeLogEntry(string level, string message)
        {
            Time               = DateTime.Now;
            Level              = level ?? "info";
            Message            = message ?? string.Empty;
            IsWireStructured   = false;
            WireDirection      = "";
            WireIsRaw          = false;
            WireEnvelopeUnixMs = 0;
            WireSessionId      = "";
            WireName           = "";
            WireType           = "";
            WireId             = "";
            WireDetail         = "";
        }

        public static BridgeLogEntry Wire(
            string level,
            string direction,
            bool isRaw,
            BridgeEnvelope env,
            string detail,
            string messageForFilter)
        {
            var sid = env?.sessionId ?? "";
            var nm  = env?.name ?? "";
            var tp  = env?.type ?? "";
            var id  = env?.id ?? "";
            var ts  = env != null ? env.timestamp : 0L;
            return new BridgeLogEntry(
                level ?? "info",
                messageForFilter ?? "",
                true,
                direction ?? "",
                isRaw,
                ts,
                sid,
                nm,
                tp,
                id,
                detail ?? "");
        }

        private BridgeLogEntry(
            string level,
            string message,
            bool isWireStructured,
            string wireDirection,
            bool wireIsRaw,
            long wireEnvelopeUnixMs,
            string wireSessionId,
            string wireName,
            string wireType,
            string wireId,
            string wireDetail)
        {
            Time               = DateTime.Now;
            Level              = level ?? "info";
            Message            = message ?? string.Empty;
            IsWireStructured   = isWireStructured;
            WireDirection      = wireDirection ?? "";
            WireIsRaw          = wireIsRaw;
            WireEnvelopeUnixMs = wireEnvelopeUnixMs;
            WireSessionId      = wireSessionId ?? "";
            WireName           = wireName ?? "";
            WireType           = wireType ?? "";
            WireId             = wireId ?? "";
            WireDetail         = wireDetail ?? "";
        }
    }

    public sealed class UpilotBridge
    {
        public const string DefaultWsHost   = "127.0.0.1";
        public const int    DefaultWsPort   = 8765;
        public const int    DefaultHttpPort = 8011;
        private static string _projectPathHashSuffix;

        /// <summary>当前工程路径规范化后的 SHA256 前 8 字节（16 hex），用于 EditorPrefs 键后缀。</summary>
        private static string ProjectPathHashSuffix
        {
            get
            {
                if (_projectPathHashSuffix != null)
                    return _projectPathHashSuffix;

                var root = Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath;
                var normalized = root.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
                using (var sha = SHA256.Create())
                {
                    var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                    var sb = new StringBuilder(16);
                    for (var i = 0; i < 8; i++)
                        sb.Append(digest[i].ToString("x2"));
                    _projectPathHashSuffix = sb.ToString();
                }

                return _projectPathHashSuffix;
            }
        }

        private static string WsHostPrefsKey => $"upilot.WsHost.{ProjectPathHashSuffix}";
        private static string WsPortPrefsKey => $"upilot.WsPort.{ProjectPathHashSuffix}";
        private static string HttpPortPrefsKey => $"upilot.HttpPort.{ProjectPathHashSuffix}";
        private const int    HeartbeatIntervalMs = 2000;
        private const int    MaxLogEntries    = 1000;
        private const string DebugLogPrefsKey = "upilot.DebugWireLogs";
        private const string VerboseLogPrefsKey = "upilot.VerboseLogs";
        private const string AutoRestartPrefsKey = "upilot.AutoRestartOnStuck";

        private static readonly Lazy<UpilotBridge> Lazy = new(() => new UpilotBridge());
        public static UpilotBridge Instance => Lazy.Value;

        private readonly UpilotCompileService  _compileService    = new();
        private readonly UpilotPlayInputService _playInputService  = new();
        private readonly UpilotCommandRouter   _router            = new();

        public UpilotCompileService CompileService => _compileService;
        private readonly ConcurrentQueue<Action>   _mainThreadQueue   = new();
        private readonly SemaphoreSlim             _sendLock          = new(1, 1);
        private readonly object                    _logLock           = new();
        private readonly List<BridgeLogEntry>      _logBuffer         = new(MaxLogEntries);
        private readonly int                       _mainThreadId;

        // Module services (initialized in constructor after Router is available)
        private UpilotConsoleService    _consoleService;
        private UpilotGameObjectService _gameObjectService;
        private UpilotSceneService      _sceneService;
        private UpilotComponentService  _componentService;
        private UpilotScreenshotService _screenshotService;
        private UpilotAssetService      _assetService;
        private UpilotPrefabService     _prefabService;
        private UpilotMaterialService   _materialService;
        private UpilotMenuService       _menuService;
        private UpilotPackageService    _packageService;
        private UpilotTestService       _testService;
        // P2 services
        private UpilotScriptService     _scriptService;
        private UpilotCSharpService     _csharpService;
        private UpilotReflectionService _reflectionService;
        private ReflectionEvalService       _reflectionEvalService;
        private UpilotBatchService      _batchService;
        private UpilotSelectionService  _selectionService;
        private UpilotResourceService   _resourceService;
        private UpilotBuildService      _buildService;
        private UpilotDragDropService   _dragDropService;
        private UpilotKeyboardService   _keyboardService;
        // UIToolkit MCP disabled — UpilotUIToolkitService not registered (restore with RegisterCommands).
        // private UpilotUIToolkitService  _uiToolkitService;
        private UpilotEditorService     _editorService;
        private UpilotWindowService     _windowService;
        private UpilotEditorDelayService _editorDelayService;
        private object _uiFlowService;

        private ClientWebSocket      _ws;
        private CancellationTokenSource _cts;
        private Task                 _connectLoopTask;
        private string               _sessionId;
        private string               _mcpLabelFromServer = "";
        private string               _mcpHostFromServer = "";
        private int                  _mcpPortFromServer;
        private string               _mcpWorkspacePathFromServer = "";
        private bool                 _started;
        public bool                  IsStarted => _started;
        private bool                 _isAuthenticated;
        private long                 _lastHeartbeatSentAt;
        private string               _activeSceneName = string.Empty;
        private long                 _pipelineCompileStartUtcMs;
        private string               _pipelineCompileStatusRequestId = string.Empty;
        private bool                 _pipelineCompileFromMcp;
        private string               _wsHost = DefaultWsHost;
        private int                  _wsPort = DefaultWsPort;
        private int                  _httpPort = DefaultHttpPort;
        private bool                 _debugWireLogsEnabled;
        private bool                 _verboseLogsEnabled;
        private bool                 _autoRestartOnCriticalStuck;

        private UpilotBridge()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            LoadEndpointFromEditorPrefs();
            _debugWireLogsEnabled = EditorPrefs.GetBool(DebugLogPrefsKey, false);
            _verboseLogsEnabled = EditorPrefs.GetBool(VerboseLogPrefsKey, false);
            _autoRestartOnCriticalStuck = EditorPrefs.GetBool(AutoRestartPrefsKey, false);
            RegisterLegacyCommands();
            RegisterModuleServices();
        }

        private void LoadEndpointFromEditorPrefs()
        {
            var hKey = WsHostPrefsKey;
            var pKey = WsPortPrefsKey;
            var httpKey = HttpPortPrefsKey;

            if (EditorPrefs.HasKey(hKey))
            {
                _wsHost = EditorPrefs.GetString(hKey, DefaultWsHost);
                _wsPort = EditorPrefs.GetInt(pKey, DefaultWsPort);
                _httpPort = EditorPrefs.GetInt(httpKey, DefaultHttpPort);
                return;
            }

            _wsHost = DefaultWsHost;
            _wsPort = DefaultWsPort;
            _httpPort = DefaultHttpPort;
        }

        /// <summary>The command router. Modules register handlers via Router.Register().</summary>
        public UpilotCommandRouter Router => _router;

        /// <summary>The main-thread work queue. Enqueue actions that must run on Unity's main thread.</summary>
        public ConcurrentQueue<Action> MainThreadQueue => _mainThreadQueue;

        // ── Public status API (called by status window on main thread) ──────────

        public string WsHost => _wsHost;
        public int WsPort => _wsPort;
        public int HttpPort
        {
            get => _httpPort;
            set
            {
                if (_httpPort == value) return;
                _httpPort = value > 0 ? value : DefaultHttpPort;
                EditorPrefs.SetInt(HttpPortPrefsKey, _httpPort);
                Logger.Log("NETWORK", $"设置HTTP端口: {_httpPort}");
            }
        }

        /// <summary>当前工程在 EditorPrefs 中使用的路径哈希后缀（16 位 hex），供界面提示。</summary>
        public static string WsEndpointEditorPrefsKeySuffix => ProjectPathHashSuffix;
        public bool DebugWireLogsEnabled
        {
            get => _debugWireLogsEnabled;
            set
            {
                if (_debugWireLogsEnabled == value) return;
                _debugWireLogsEnabled = value;
                EditorPrefs.SetBool(DebugLogPrefsKey, value);
                Logger.Log("SYSTEM", value ? "调试日志已开启（通信命令收发可见）" : "调试日志已关闭");
            }
        }

        public bool VerboseLogsEnabled
        {
            get => _verboseLogsEnabled;
            set
            {
                if (_verboseLogsEnabled == value) return;
                _verboseLogsEnabled = value;
                EditorPrefs.SetBool(VerboseLogPrefsKey, value);
                Logger.Log("SYSTEM", value ? "详细日志已开启（心跳、连接、请求状态）" : "详细日志已关闭");
            }
        }

        public bool AutoRestartOnCriticalStuck
        {
            get => _autoRestartOnCriticalStuck;
            set
            {
                if (_autoRestartOnCriticalStuck == value) return;
                _autoRestartOnCriticalStuck = value;
                EditorPrefs.SetBool(AutoRestartPrefsKey, value);
                Logger.Log("SYSTEM", value ? "临界超时自动重启已开启" : "临界超时自动重启已关闭");
            }
        }

        /// <summary>
        /// 更新 WebSocket 地址并写入 <see cref="EditorPrefs"/>。
        /// 键为 <c>upilot.WsHost.{项目路径哈希}</c> / <c>upilot.WsPort.{项目路径哈希}</c>，按工程区分。
        /// </summary>
        public void SetWsEndpoint(string host, int port)
        {
            Logger.Log("NETWORK", $"设置WS端点: host={host} port={port}");
            _wsHost = string.IsNullOrWhiteSpace(host) ? DefaultWsHost : host.Trim();
            _wsPort = port > 0 ? port : DefaultWsPort;
            EditorPrefs.SetString(WsHostPrefsKey, _wsHost);
            EditorPrefs.SetInt(WsPortPrefsKey, _wsPort);
        }

        public string GetServerUrl() => $"ws://{_wsHost}:{_wsPort}";

        private bool CurrentIsCompiling()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                return _compileService.IsCompiling;

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                _compileService.ClearStaleCompileBusy(
                    "Unity is in PlayMode or changing PlayMode; script compilation is not available.",
                    ignoreEditorCompiling: true);
                return false;
            }

            return _compileService.IsCompiling || EditorApplication.isCompiling;
        }

        private bool IsPlayingOrWillChangePlaymode()
        {
            return Thread.CurrentThread.ManagedThreadId == _mainThreadId &&
                   EditorApplication.isPlayingOrWillChangePlaymode;
        }

        public BridgeStatus GetStatus() => new BridgeStatus
        {
            IsStarted           = _started,
            IsWsOpen            = _ws?.State == WebSocketState.Open,
            IsAuthenticated     = _isAuthenticated,
            SessionId           = _sessionId ?? "",
            LastHeartbeatSentAt = _lastHeartbeatSentAt,
            IsCompiling         = CurrentIsCompiling(),
            LastErrorCount      = _compileService.LastErrorCount,
            PlayModeState       = _playInputService.CurrentPlayModeChangedPayload().state,
            McpLabel            = _mcpLabelFromServer ?? "",
            McpServerHost       = string.IsNullOrEmpty(_mcpHostFromServer) ? _wsHost : _mcpHostFromServer,
            McpServerPort       = _mcpPortFromServer > 0 ? _mcpPortFromServer : _wsPort,
            McpWorkspaceAbsolutePath = _mcpWorkspacePathFromServer ?? "",
        };

        public List<BridgeLogEntry> GetLogsCopy()
        {
            lock (_logLock) { return new List<BridgeLogEntry>(_logBuffer); }
        }

        public void ClearLogs()
        {
            Logger.Log("SYSTEM", "清除日志");
            lock (_logLock)
            {
                _logBuffer.Clear();
            }
        }

        public void Restart()
        {
            UpilotOperationTracker.Instance.RecordSystemEvent(
                "sys.bridge.restart", "Bridge重启", "手动触发重启");
            Stop();
            EnsureStarted();
        }

        // ── Lifecycle ───────────────────────────────────────────────────────────

        public void EnsureStarted()
        {
            if (_started)
            {
                if (IsConnectLoopAlive()) return;

                Logger.LogWarning("SYSTEM", "Bridge marked started but connect loop is not alive; restarting loop.");
                UpilotOperationTracker.Instance.RecordSystemEvent(
                    "sys.bridge.loop.recover", "连接循环自愈",
                    $"endpoint={GetServerUrl()} wsState={_ws?.State}");
                StartConnectLoop();
                return;
            }
            if (Application.isBatchMode)
            {
                Logger.LogWarning("SYSTEM", "Bridge startup skipped: batch mode is temporarily disabled.");
                return;
            }
            _connectFailureStreak = 0;
            _started = true;
            _lastHeartbeatSentAt = 0;
            _sessionId = Guid.NewGuid().ToString("N");
            _cts = new CancellationTokenSource();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += ProcessMainThreadQueue;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            UpilotOperationTracker.Instance.RecordSystemEvent(
                "sys.bridge.start", "Bridge启动",
                $"sessionId={_sessionId} endpoint={GetServerUrl()}");
            StartConnectLoop();
        }

        public void Stop()
        {
            var wasAuthenticated = _isAuthenticated;
            var wasWsOpen = _ws?.State == WebSocketState.Open;
            try
            {
                _cts?.Cancel();
                _ws?.Dispose();
            }
            finally
            {
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
                EditorApplication.update -= ProcessMainThreadQueue;
                CompilationPipeline.compilationStarted -= OnCompilationStarted;
                CompilationPipeline.compilationFinished -= OnCompilationFinished;
                AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
                AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
                _started = false;
                _isAuthenticated = false;
                _lastHeartbeatSentAt = 0;
                _connectFailureStreak = 0;
                _connectLoopTask = null;
                ClearMcpServerDisplayState();
                UpilotOperationTracker.Instance.RecordSystemEvent(
                    "sys.bridge.stop", "Bridge停止",
                    $"原因=手动停止 连接状态={(wasWsOpen ? "已连接" : "未连接")} 认证状态={(wasAuthenticated ? "已认证" : "未认证")}",
                    "stopped");
            }
        }

        private double _lastWatchdogCheck;
        private uint _connectFailureStreak;

        private bool IsConnectLoopAlive()
        {
            return _connectLoopTask != null && !_connectLoopTask.IsCompleted && _cts != null && !_cts.IsCancellationRequested;
        }

        private void StartConnectLoop()
        {
            if (_cts == null || _cts.IsCancellationRequested)
                _cts = new CancellationTokenSource();

            _connectLoopTask = ConnectLoopAsync(_cts.Token);
            _ = WatchConnectLoopAsync(_connectLoopTask);
        }

        private async Task WatchConnectLoopAsync(Task loopTask)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
                if (_started && _connectLoopTask == loopTask && !(_cts?.IsCancellationRequested ?? true))
                {
                    Logger.LogError("NETWORK", "WS connect loop exited unexpectedly while Bridge is still started.");
                    UpilotOperationTracker.Instance.RecordSystemEvent(
                        "sys.bridge.loop.dead", "连接循环异常退出",
                        $"endpoint={GetServerUrl()} wsState={_ws?.State}",
                        "error");
                    _mainThreadQueue.Enqueue(() =>
                    {
                        if (_started && _connectLoopTask == loopTask)
                            StartConnectLoop();
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("NETWORK", $"WS connect loop crashed: {ex}");
                UpilotOperationTracker.Instance.RecordSystemEvent(
                    "sys.bridge.loop.crash", "连接循环崩溃",
                    ex.ToString(),
                    "error");
                _mainThreadQueue.Enqueue(() =>
                {
                    if (_started && _connectLoopTask == loopTask)
                        StartConnectLoop();
                });
            }
        }

        private void ProcessMainThreadQueue()
        {
            _activeSceneName = SceneManager.GetActiveScene().name;

            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Debug.LogError($"[UpilotBridge] main thread error: {ex}"); }
            }

            if (EditorApplication.timeSinceStartup - _lastWatchdogCheck > 2.0)
            {
                _lastWatchdogCheck = EditorApplication.timeSinceStartup;
                var tracker = UpilotOperationTracker.Instance;
                var stuckCommands = tracker.RunWatchdog();
                foreach (var cmd in stuckCommands)
                    Logger.LogWarning("WATCHDOG", $"操作卡住: {cmd}");

                // Critical stuck detection: auto-restart if enabled
                if (_autoRestartOnCriticalStuck)
                {
                    var critical = tracker.GetCriticallyStuckCommandIds();
                    if (critical.Count > 0)
                    {
                        Logger.LogError("WATCHDOG", $"{critical.Count} 个操作超过临界超时，准备强制重启 Unity");
                        _autoRestartOnCriticalStuck = false;
                        ForceRestartUnityEditor();
                    }
                }
            }
        }

        /// <summary>
        /// 替代 <c>MainThreadQueue.Enqueue</c>，自动追踪"排队等待主线程→主线程执行中→主线程执行完毕"。
        /// </summary>
        public void EnqueueTracked(string commandId, Action action)
        {
            var ctx = UpilotOperationTracker.Instance.GetContext(commandId);
            ctx?.Step("排队等待主线程");

            _mainThreadQueue.Enqueue(() =>
            {
                ctx?.Step("主线程执行中");
                try
                {
                    action();
                    ctx?.Step("主线程执行完毕");
                }
                catch (Exception ex)
                {
                    ctx?.Fail("MAIN_THREAD_ERROR", ex.Message);
                    throw;
                }
            });
        }
        private const int ConnectTimeoutMs = 3000;  // 连接超时 3s
        private const int ReconnectDelayMs  = 1000;  // 失败后等待 1s 再重连

        private async Task ConnectLoopAsync(CancellationToken token)
        {
            var tracker = UpilotOperationTracker.Instance;
            while (!token.IsCancellationRequested)
            {
                var wasAuthenticated = _isAuthenticated;
                _isAuthenticated = false;
                ClearMcpServerDisplayState();

                Task recvTask = Task.CompletedTask;
                Task hbTask   = Task.CompletedTask;
                using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(token);

                try
                {
                    _ws = new ClientWebSocket();
                    var serverUrl = GetServerUrl();
                    if (_verboseLogsEnabled)
                        Logger.Log("NETWORK", $"WS connecting to {serverUrl} (attempt={_connectFailureStreak + 1})");

                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    connectCts.CancelAfter(ConnectTimeoutMs);
                    try
                    {
                        await _ws.ConnectAsync(new Uri(serverUrl), connectCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        throw new Exception($"连接超时 ({ConnectTimeoutMs / 1000}s)");
                    }

                    if (_connectFailureStreak > 0)
                    {
                        Logger.Log("NETWORK", $"WS 重连成功（曾失败 {_connectFailureStreak} 次）");
                        _connectFailureStreak = 0;
                    }
                    else if (_verboseLogsEnabled)
                    {
                        Logger.Log("NETWORK", "WS connected");
                    }

                    tracker.RecordSystemEvent("sys.ws.connected", "WS连接成功",
                        $"endpoint={serverUrl} sessionId={_sessionId}");

                    await SendHelloAsync(cycleCts.Token);

                    recvTask = ReceiveLoopAsync(cycleCts.Token);
                    hbTask   = HeartbeatLoopAsync(cycleCts.Token);

                    var completedTask = await Task.WhenAny(recvTask, hbTask);
                    var disconnectReason = AnalyzeDisconnectReason(recvTask, hbTask, completedTask, cycleCts.Token);

                    if (wasAuthenticated || _isAuthenticated)
                        tracker.RecordSystemEvent("sys.auth.lost", "认证丢失",
                            $"原因=连接断开 详情={disconnectReason}", "disconnected");

                    tracker.RecordSystemEvent("sys.ws.disconnected", "WS连接断开",
                        $"原因={disconnectReason} endpoint={serverUrl} wsState={_ws?.State}", "disconnected");

                    _isAuthenticated = false;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _connectFailureStreak++;
                    if (_verboseLogsEnabled)
                        Logger.LogWarning("NETWORK", $"WS 连接失败（第 {_connectFailureStreak} 次）: {ex.Message}");

                    if (wasAuthenticated)
                        tracker.RecordSystemEvent("sys.auth.lost", "连接失败导致认证丢失",
                            $"原因={ex.Message}", "disconnected");
                }
                finally
                {
                    cycleCts.Cancel();
                    try { await Task.WhenAll(recvTask, hbTask); } catch { }

                    var wsToClose = _ws;
                    _ws = null;
                    if (wsToClose != null)
                    {
                        try { wsToClose.Abort(); } catch { }
                        _ = Task.Run(() => { try { wsToClose.Dispose(); } catch { } });
                    }
                }

                if (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(ReconnectDelayMs, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        private string AnalyzeDisconnectReason(Task recvTask, Task hbTask, Task completedTask, CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return "Bridge主动停止(CancellationToken)";

            if (completedTask == recvTask)
            {
                if (recvTask.IsFaulted)
                {
                    var ex = recvTask.Exception?.InnerException ?? recvTask.Exception;
                    if (ex is WebSocketException wsEx)
                        return $"WebSocket接收异常: {wsEx.WebSocketErrorCode} — {wsEx.Message}";
                    return $"接收循环异常: {ex?.Message}";
                }
                if (recvTask.IsCanceled)
                    return "接收循环被取消";

                // recvTask completed normally → server closed or WS state changed
                var wsState = _ws?.State;
                if (wsState == WebSocketState.CloseReceived || wsState == WebSocketState.Closed)
                    return $"服务端主动关闭连接(wsState={wsState})";
                return $"接收循环正常退出(wsState={wsState}，可能服务端断开)";
            }

            if (completedTask == hbTask)
            {
                if (hbTask.IsFaulted)
                {
                    var ex = hbTask.Exception?.InnerException ?? hbTask.Exception;
                    if (ex is WebSocketException wsEx)
                        return $"心跳发送失败: {wsEx.WebSocketErrorCode} — {wsEx.Message}";
                    return $"心跳循环异常: {ex?.Message}";
                }
                if (hbTask.IsCanceled)
                    return "心跳循环被取消";
                return $"心跳循环退出(wsState={_ws?.State}，可能WS已关闭)";
            }

            return "未知断开原因";
        }

        private async Task SendHelloAsync(CancellationToken token)
        {
            // Use project root (parent of Assets folder) as projectPath
            var projectRoot = Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath;
            var msg = new HelloMessage
            {
                id = $"h-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                type = "hello",
                name = "session.hello",
                payload = new HelloPayload
                {
                    unityVersion = Application.unityVersion,
                    projectPath = projectRoot,
                    platform = Application.platform == RuntimePlatform.OSXEditor ? "macos" : "windows",
                },
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sessionId = _sessionId,
            };
            await SendJsonAsync(JsonUtility.ToJson(msg), token);
        }

        private async Task HeartbeatLoopAsync(CancellationToken token)
        {
            int consecutiveFailures = 0;
            int heartbeatCount = 0;
            while (!token.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                try
                {
                    var hb = new HeartbeatMessage
                    {
                        id = $"hb-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                        type = "heartbeat",
                        name = "session.heartbeat",
                        payload = new HeartbeatPayload(),
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        sessionId = _sessionId,
                    };
                    await SendJsonAsync(JsonUtility.ToJson(hb), token);
                    _lastHeartbeatSentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    consecutiveFailures = 0;
                    heartbeatCount++;
                    if (_verboseLogsEnabled && (heartbeatCount == 1 || heartbeatCount % 5 == 0))
                    {
                        Logger.Log("NETWORK", $"Heartbeat OK (count={heartbeatCount})");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    if (_verboseLogsEnabled)
                        Logger.LogWarning("NETWORK", $"Heartbeat send failed ({consecutiveFailures}): {ex.Message}");
                    if (consecutiveFailures >= 3)
                    {
                        throw new Exception($"Heartbeat failed {consecutiveFailures} times consecutively, forcing reconnect");
                    }
                }
                await Task.Delay(HeartbeatIntervalMs, token).ConfigureAwait(false);
            }
        }

        private void ClearMcpServerDisplayState()
        {
            _mcpLabelFromServer = "";
            _mcpHostFromServer = "";
            _mcpPortFromServer = 0;
            _mcpWorkspacePathFromServer = "";
        }

        private string FormatMcpServerHintForLog()
        {
            var host = string.IsNullOrEmpty(_mcpHostFromServer) ? _wsHost : _mcpHostFromServer;
            var port = _mcpPortFromServer > 0 ? _mcpPortFromServer : _wsPort;
            var endpoint = $"{host}:{port}";
            var head = !string.IsNullOrEmpty(_mcpLabelFromServer)
                ? $"MCP「{_mcpLabelFromServer}」· {endpoint}"
                : $"MCP {endpoint}";
            if (!string.IsNullOrEmpty(_mcpWorkspacePathFromServer))
                return $"{head}  |  工作区 {_mcpWorkspacePathFromServer}";
            return head;
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[65536];
            var sb = new StringBuilder();

            while (!token.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    // ConfigureAwait(false)：确保 ReceiveAsync 回调在线程池上立即执行，
                    // 不等 Unity 主线程 SynchronizationContext 的下一个 frame
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        var closeStatus = result.CloseStatus?.ToString() ?? "Unknown";
                        var closeDesc = result.CloseStatusDescription ?? "";
                        UpilotOperationTracker.Instance.RecordSystemEvent(
                            "sys.ws.close.received", "收到服务端关闭帧",
                            $"CloseStatus={closeStatus} Description={closeDesc}",
                            "disconnected");
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                var json = sb.ToString();

                // ── 消息组装完成后立即同步写日志，不等后续 await 调度 ──────────────
                var envelope = JsonUtility.FromJson<BridgeEnvelope>(json);
                if (envelope != null) LogInboundCommand(envelope, json);

                // ConfigureAwait(false)：同样避免命令处理 continuation 被推到主线程队列
                await HandleMessageJsonAsync(json, envelope, token).ConfigureAwait(false);
            }
        }

        private async Task HandleMessageJsonAsync(string json, BridgeEnvelope envelope, CancellationToken token)
        {
            // envelope 已在 ReceiveLoopAsync 中解析并写日志，此处直接使用
            if (envelope == null) return;

            // Handle hello result → authenticate
            if (envelope.type == "result" && envelope.name == "session.hello")
            {
                var ack = JsonUtility.FromJson<HelloAckMessage>(json);
                if (ack?.payload != null)
                {
                    if (!ack.payload.accepted)
                    {
                        UpilotOperationTracker.Instance.RecordSystemEvent(
                            "sys.auth.rejected", "认证被拒绝",
                            $"sessionId={_sessionId}", "error");
                        try { _ws?.Abort(); } catch { /* ignored */ }
                        return;
                    }
                    _mcpLabelFromServer = ack.payload.mcpLabel ?? "";
                    _mcpHostFromServer = ack.payload.mcpHost ?? "";
                    _mcpPortFromServer = ack.payload.mcpPort;
                    _mcpWorkspacePathFromServer = ack.payload.mcpWorkingDirectory ?? "";
                }
                else
                {
                    ClearMcpServerDisplayState();
                }

                _isAuthenticated = true;
                UpilotOperationTracker.Instance.RecordSystemEvent(
                    "sys.auth.success", "认证成功",
                    $"sessionId={_sessionId} {FormatMcpServerHintForLog()}");
                _ = SendEditorStateEventAsync(token);
                return;
            }

            // Absorb heartbeats from server
            if (envelope.type == "heartbeat" ||
                string.Equals(envelope.name, "session.heartbeat", StringComparison.OrdinalIgnoreCase)) return;

            // Only process commands
            if (envelope.type != "command") return;

            // Guard: require authentication
            if (!_isAuthenticated)
            {
                await SendErrorAsync(envelope.id, "INTERNAL_ERROR", "会话未认证", token, envelope.name);
                return;
            }

            var id = envelope.id;
            if (!string.Equals(envelope.name, "uiflow.results", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log("COMMAND", $"Received {envelope.name} id={id}");
            }

            try
            {
                if (!await _router.TryHandleAsync(envelope.name, id, json, token))
                {
                    Logger.LogWarning("COMMAND", $"Unknown command: {envelope.name}");
                    await SendErrorAsync(id, "COMMAND_NOT_FOUND", $"未注册命令：{envelope.name}", token, envelope.name);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError("COMMAND", $"Unhandled command exception: {envelope.name} id={id} | {ex.Message}");
                if (CommandResultAlreadyReported(id))
                    return;

                try
                {
                    await SendErrorAsync(id, "INTERNAL_ERROR", $"命令处理异常：{ex.Message}", token, envelope.name);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception sendEx)
                {
                    Logger.LogWarning("COMMAND", $"Failed to report command exception: {envelope.name} id={id} | {sendEx.Message}");
                }
            }
        }

        private static bool CommandResultAlreadyReported(string commandId)
        {
            var entries = UpilotOperationTracker.Instance.GetEntriesCopy();
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].CommandId == commandId)
                    return entries[i].ResultReported;
            }
            return false;
        }

        // ──────────────────────────────── Legacy command registration ────────────────────────────────

        private void RegisterLegacyCommands()
        {
            _router.Register("compile.request",   HandleCompileRequestAsync);
            _router.Register("compile.wait",      HandleCompileWaitAsync);
            _router.Register("compile.errors.get", HandleCompileErrorsGetAsync);
            _router.Register("playmode.set",       HandlePlayModeSetAsync);
            _router.Register("mouse.event",        HandleMouseEventAsync);
            _router.Register("editor.state",       HandleEditorStateAsync);
            _router.Register("agent.reportError",  HandleAgentReportErrorAsync);
            _router.Register("editor.forceRestart", HandleForceRestartAsync);
        }

        private void RegisterModuleServices()
        {
            _consoleService = new UpilotConsoleService(this);
            _consoleService.RegisterCommands();

            _gameObjectService = new UpilotGameObjectService(this);
            _gameObjectService.RegisterCommands();

            _sceneService = new UpilotSceneService(this);
            _sceneService.RegisterCommands();

            _componentService = new UpilotComponentService(this);
            _componentService.RegisterCommands();

            _screenshotService = new UpilotScreenshotService(this);
            _screenshotService.RegisterCommands();

            _assetService = new UpilotAssetService(this);
            _assetService.RegisterCommands();

            _prefabService = new UpilotPrefabService(this);
            _prefabService.RegisterCommands();

            _materialService = new UpilotMaterialService(this);
            _materialService.RegisterCommands();

            _menuService = new UpilotMenuService(this);
            _menuService.RegisterCommands();

            _packageService = new UpilotPackageService(this);
            _packageService.RegisterCommands();

            _testService = new UpilotTestService(this);
            _testService.RegisterCommands();

            _dragDropService = new UpilotDragDropService(this);
            _dragDropService.RegisterCommands();

            // _uiToolkitService = new UpilotUIToolkitService(this);
            // _uiToolkitService.RegisterCommands();

            _keyboardService = new UpilotKeyboardService(this);
            _keyboardService.RegisterCommands();

            // P2 services
            _scriptService = new UpilotScriptService(this);
            _scriptService.RegisterCommands();
            _csharpService = new UpilotCSharpService(this);
            _csharpService.RegisterCommands();
            _reflectionService = new UpilotReflectionService(this);
            _reflectionService.RegisterCommands();
            _reflectionEvalService = new ReflectionEvalService(this);
            _reflectionEvalService.RegisterCommands();
            _batchService = new UpilotBatchService(this);
            _batchService.RegisterCommands();
            _selectionService = new UpilotSelectionService(this);
            _selectionService.RegisterCommands();
            _resourceService = new UpilotResourceService(this);
            _resourceService.RegisterCommands();
            _buildService = new UpilotBuildService(this);
            _buildService.RegisterCommands();

            _editorService = new UpilotEditorService(this);
            _editorService.RegisterCommands();

            _windowService = new UpilotWindowService(this);
            _windowService.RegisterCommands();

            _editorDelayService = new UpilotEditorDelayService(this);
            _editorDelayService.RegisterCommands();

            RegisterOptionalUIFlowService();
        }

        private void RegisterOptionalUIFlowService()
        {
            const string typeName = "codingriver.upilot.UpilotUIFlowService, Upilot.UIFlowBridge";

            try
            {
                var serviceType = Type.GetType(typeName, throwOnError: false);
                if (serviceType != null)
                {
                    var service = Activator.CreateInstance(serviceType, this);
                    var registerMethod = serviceType.GetMethod("RegisterCommands");
                    if (registerMethod == null)
                    {
                        throw new MissingMethodException(serviceType.FullName, "RegisterCommands");
                    }

                    registerMethod.Invoke(service, null);
                    _uiFlowService = service;
                    Logger.Log("UIFlow", "UIFlow MCP bridge registered.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("UIFlow", $"UIFlow MCP bridge is unavailable: {ex.Message}");
            }

            var unavailableService = new UpilotUIFlowUnavailableService(this);
            unavailableService.RegisterCommands();
            _uiFlowService = unavailableService;
            Logger.Log("UIFlow", "UIFlow MCP bridge disabled; registered unavailable command handlers.");
        }

        // ──────────────────────────────── Command handlers ────────────────────────────────

        private async Task HandleCompileRequestAsync(string id, string json, CancellationToken token)
        {
            var opCtx = UpilotOperationTracker.Instance.GetContext(id);
            var command = JsonUtility.FromJson<CompileRequestMessage>(json);
            var requestId = command?.payload?.requestId ?? string.Empty;
            Logger.Log("COMPILE", $"收到 compile.request: requestId={requestId} id={id}");

            // Fast-path: already compiling
            if (IsPlayingOrWillChangePlaymode())
            {
                _compileService.ClearStaleCompileBusy(
                    "compile.request received while Unity is in PlayMode.",
                    ignoreEditorCompiling: true);
                await SendErrorAsync(id, "EDITOR_IN_PLAY_MODE", "Unity 正在 PlayMode 或切换 PlayMode，不能触发脚本编译", token, "compile.request");
                return;
            }

            if (CurrentIsCompiling())
            {
                await SendErrorAsync(id, "EDITOR_BUSY", "编译进行中，请稍后重试", token, "compile.request");
                return;
            }
            opCtx?.Step("参数校验通过，准备触发编译");

            var startTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EnqueueTracked(id, () =>
            {
                if (!_compileService.TryBeginCompile(requestId))
                {
                    startTcs.TrySetResult(false);
                    return;
                }
                startTcs.TrySetResult(true);
            });

            var compileStarted = await startTcs.Task;
            if (!compileStarted)
            {
                await SendErrorAsync(id, "EDITOR_BUSY", "编译进行中，请稍后重试", token, "compile.request");
                return;
            }
            Logger.Log("COMPILE", $"编译已触发，等待完成: requestId={requestId} timeout=120s");

            // compile.status / compile.started / compile.finished / compile.errors are pushed from
            // OnCompilationStarted / SendCompileFinishedMcpPushAsync for every compilation cycle.

            opCtx?.Step("等待编译完成", "超时120s");
            var compileTask = _compileService.WaitForCompileAsync();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(120), token);
            var winner = await Task.WhenAny(compileTask, timeoutTask);
            if (winner == timeoutTask)
            {
                Logger.LogWarning("COMPILE", $"compile.request 超时 (120s): requestId={requestId}");
                _compileService.ClearStaleCompileBusy("compile.request timed out before Unity reported compilation finished.");
                await SendErrorAsync(id, "COMMAND_TIMEOUT", "编译超时", token, "compile.request");
                return;
            }

            opCtx?.Step("编译完成，发送结果");
            Logger.Log("COMPILE", $"编译完成，发送结果: requestId={requestId} errors={_compileService.LastErrorCount}");
            var accepted = new CompileAcceptedPayload { accepted = true, compileRequestId = requestId };
            await SendResultAsync(id, "compile.request", accepted, token);
        }

        private async Task HandleCompileErrorsGetAsync(string id, string json, CancellationToken token)
        {
            var payload = _compileService.BuildLastCompileErrorsPayload();
            Logger.Log("COMPILE", $"compile.errors.get: errors={payload.total} requestId={payload.requestId}");
            await SendResultAsync(id, "compile.errors.get", payload, token);
        }

        /// <summary>
        /// Wait until EditorApplication reports no script compilation (any source). Polls on main thread via EditorApplication.update.
        /// </summary>
        private async Task HandleCompileWaitAsync(string id, string json, CancellationToken token)
        {
            var opCtx = UpilotOperationTracker.Instance.GetContext(id);
            var command = JsonUtility.FromJson<CompileWaitMessage>(json);
            var timeoutMs = command?.payload?.timeoutMs ?? 300000;
            if (timeoutMs < 1000) timeoutMs = 1000;
            if (timeoutMs > 600000) timeoutMs = 600000;
            opCtx?.Step("等待编译空闲", $"timeout={timeoutMs}ms");
            Logger.Log("COMPILE", $"compile.wait 开始: timeout={timeoutMs}ms isCompiling={CurrentIsCompiling()} id={id}");

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stop = false;

            void PollIdle()
            {
                if (stop) return;
                if (!CurrentIsCompiling())
                {
                    EditorApplication.update -= PollIdle;
                    tcs.TrySetResult(true);
                }
            }

            EnqueueTracked(id, () =>
            {
                if (!CurrentIsCompiling())
                {
                    tcs.TrySetResult(true);
                    return;
                }

                EditorApplication.update += PollIdle;
                PollIdle();
            });

            var delayTask = Task.Delay(TimeSpan.FromMilliseconds(timeoutMs), token);
            var winner = await Task.WhenAny(tcs.Task, delayTask);
            stop = true;
            _mainThreadQueue.Enqueue(() => { EditorApplication.update -= PollIdle; });

            if (winner == delayTask)
            {
                Logger.LogWarning("COMPILE", $"compile.wait 超时 ({timeoutMs}ms) id={id}");
                await SendErrorAsync(id, "COMMAND_TIMEOUT", $"等待编译结束超时（{timeoutMs}ms）", token, "compile.wait");
                return;
            }

            Logger.Log("COMPILE", $"compile.wait 完成，编译空闲 id={id}");
            await SendResultAsync(id, "compile.wait",
                new GenericOkPayload { ok = true, state = "compile_idle", status = "ready" }, token);
        }

        private async Task HandlePlayModeSetAsync(string id, string json, CancellationToken token)
        {
            var command = JsonUtility.FromJson<PlayModeSetMessage>(json);
            var action = command?.payload?.action ?? "stop";

            if (action != "play" && action != "stop")
            {
                await SendErrorAsync(id, "INVALID_PAYLOAD", $"非法 PlayMode 动作：{action}", token, "playmode.set");
                return;
            }

            var resultTcs = new TaskCompletionSource<GenericOkPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            EnqueueTracked(id, () =>
            {
                var payload = _playInputService.SetPlayMode(action);
                resultTcs.TrySetResult(payload);
            });

            var result = await resultTcs.Task;
            await SendResultAsync(id, "playmode.set", result, token);
            await SendPlayModeChangedEventAsync(token);
        }

        private async Task HandleMouseEventAsync(string id, string json, CancellationToken token)
        {
            var command = JsonUtility.FromJson<MouseEventMessage>(json);
            var mousePayload = command?.payload ?? new MouseEventPayload();

            var validButtons = new[] { "left", "middle", "right" };
            if (Array.IndexOf(validButtons, mousePayload.button) < 0)
            {
                await SendErrorAsync(id, "INVALID_PAYLOAD", $"非法鼠标按键：{mousePayload.button}", token, "mouse.event");
                return;
            }

            var resultTcs = new TaskCompletionSource<GenericOkPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            EnqueueTracked(id, () =>
            {
                var result = _playInputService.HandleMouseEvent(mousePayload);
                resultTcs.TrySetResult(result);
            });

            var mouseResult = await resultTcs.Task;
            await SendResultAsync(id, "mouse.event", mouseResult, token);
        }

        private async Task HandleEditorStateAsync(string id, string json, CancellationToken token)
        {
            var payload = new EditorStatePayload
            {
                connected = true,
                isCompiling = CurrentIsCompiling(),
                playModeState = _playInputService.CurrentPlayModeChangedPayload().state,
                activeScene = _activeSceneName,
            };
            await SendResultAsync(id, "editor.state", payload, token);
        }

        // ──────────────────────────────── Agent error ingestion ────────────────────────

        [Serializable] public class AgentReportErrorMessage { public AgentReportErrorPayload payload; }
        [Serializable]
        public class AgentReportErrorPayload
        {
            public string source = "agent";
            public string errorType = "";
            public string message = "";
            public string relatedCommandId = "";
            public string context = "";
        }

        private async Task HandleAgentReportErrorAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AgentReportErrorMessage>(json);
            var p   = msg?.payload ?? new AgentReportErrorPayload();

            UpilotOperationTracker.Instance.IngestAgentError(
                p.source, p.errorType, p.message, p.relatedCommandId, p.context);
            Logger.LogWarning("AGENT", $"[{p.errorType}] {p.message}");

            await SendResultAsync(id, "agent.reportError", new GenericOkPayload { ok = true }, token);
        }

        // ──────────────────────────────── Force restart ──────────────────────────────

        private async Task HandleForceRestartAsync(string id, string json, CancellationToken token)
        {
            Logger.LogWarning("SYSTEM", "收到 editor.forceRestart 命令，即将强制重启编辑器");
            await SendResultAsync(id, "editor.forceRestart", new GenericOkPayload { ok = true }, token);

            _mainThreadQueue.Enqueue(() =>
            {
                ForceRestartUnityEditor();
            });
        }

        /// <summary>
        /// 强制重启 Unity 编辑器：先杀掉当前进程再重新打开项目。
        /// 创建一个外部脚本来完成杀进程和重启。
        /// </summary>
        public static void ForceRestartUnityEditor()
        {
            UpilotOperationTracker.Instance.RecordSystemEvent(
                "sys.force.restart", "强制重启Unity",
                $"project={Application.dataPath} pid={System.Diagnostics.Process.GetCurrentProcess().Id}",
                "critical");

            try
            {
                var projectPath = Path.GetDirectoryName(Application.dataPath);
                var unityExePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(unityExePath))
                {
                    Debug.LogError("[Upilot] 无法获取 Unity 编辑器路径，放弃重启");
                    return;
                }

                var pid = System.Diagnostics.Process.GetCurrentProcess().Id;

#if UNITY_EDITOR_WIN
                var restartScript = Path.Combine(Path.GetTempPath(), $"upilot_restart_{pid}.bat");
                var scriptContent =
                    $"@echo off\r\n" +
                    $"echo [Upilot] Waiting for Unity (PID {pid}) to exit...\r\n" +
                    $"taskkill /F /PID {pid} >nul 2>&1\r\n" +
                    $"timeout /t 3 /nobreak >nul\r\n" +
                    $"echo [Upilot] Restarting Unity project: {projectPath}\r\n" +
                    $"\"{unityExePath}\" -projectPath \"{projectPath}\"\r\n" +
                    $"del \"%~f0\"\r\n";
                File.WriteAllText(restartScript, scriptContent, Encoding.Default);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{restartScript}\"",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                };
                System.Diagnostics.Process.Start(psi);
#else
                var restartScript = Path.Combine(Path.GetTempPath(), $"upilot_restart_{pid}.sh");
                var scriptContent =
                    $"#!/bin/bash\n" +
                    $"echo '[Upilot] Waiting for Unity (PID {pid}) to exit...'\n" +
                    $"kill -9 {pid} 2>/dev/null\n" +
                    $"sleep 3\n" +
                    $"echo '[Upilot] Restarting Unity project: {projectPath}'\n" +
                    $"\"{unityExePath}\" -projectPath \"{projectPath}\" &\n" +
                    $"rm -f \"$0\"\n";
                File.WriteAllText(restartScript, scriptContent, Encoding.UTF8);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = restartScript,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                System.Diagnostics.Process.Start(psi);
#endif
                Debug.LogWarning($"[Upilot] 重启脚本已启动，Unity 即将关闭");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Upilot] 强制重启失败: {ex.Message}");
            }
        }

        // ──────────────────────────────── Send helpers ────────────────────────────────

        public async Task SendResultAsync<TPayload>(string id, string name, TPayload payload, CancellationToken token)
        {
            var result = new ResultMessage<TPayload>
            {
                id = id,
                name = name,
                payload = payload,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sessionId = _sessionId,
            };
            await SendJsonAsync(JsonUtility.ToJson(result), token);
            UpilotOperationTracker.Instance.GetContext(id)?.MarkReported(false);
        }

        public async Task SendEventAsync<TPayload>(string id, string name, TPayload payload, CancellationToken token)
        {
            var evt = new EventMessage<TPayload>
            {
                id = id,
                name = name,
                payload = payload,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sessionId = _sessionId,
            };
            await SendJsonAsync(JsonUtility.ToJson(evt), token);
        }

        public async Task SendErrorAsync(string id, string code, string message, CancellationToken token,
            string commandName = "")
        {
            Logger.LogWarning("NETWORK", $"TX error [{code}] {message}  cmd={commandName} id={id}");
            var err = new ErrorMessage
            {
                id = id,
                name = "command.error",
                payload = new ErrorPayload
                {
                    code = code,
                    message = message,
                    detail = new ErrorDetailPayload { commandId = id, commandName = commandName },
                },
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sessionId = _sessionId,
            };
            await SendJsonAsync(JsonUtility.ToJson(err), token);
            UpilotOperationTracker.Instance.GetContext(id)?.MarkReported(true);
        }

        private async Task SendEditorStateEventAsync(CancellationToken token)
        {
            var payload = new EditorStatePayload
            {
                connected = true,
                isCompiling = CurrentIsCompiling(),
                playModeState = _playInputService.CurrentPlayModeChangedPayload().state,
                activeScene = _activeSceneName,
            };
            await SendEventAsync(
                $"evt-editor-state-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                "editor.state", payload, token);

            // After reconnect, proactively send current compile errors snapshot.
            // Even if _lastErrors is empty (cleared by domain reload), this tells
            // the Python side there are no errors, overriding any stale cache.
            if (!CurrentIsCompiling())
            {
                var errorsPayload = _compileService.BuildLastCompileErrorsPayload();
                await SendEventAsync(
                    $"evt-compile-errors-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    "compile.errors", errorsPayload, token);
            }
        }

        /// <summary>Push editor.state only (no compile.errors) — used around compile start/end to avoid duplicate error snapshots.</summary>
        private async Task SendEditorStateOnlyAsync(CancellationToken token)
        {
            if (_ws?.State != WebSocketState.Open || !_isAuthenticated) return;
            var payload = new EditorStatePayload
            {
                connected = true,
                isCompiling = CurrentIsCompiling(),
                playModeState = _playInputService.CurrentPlayModeChangedPayload().state,
                activeScene = _activeSceneName,
            };
            await SendEventAsync(
                $"evt-editor-state-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                "editor.state", payload, token);
        }

        private async Task SendPlayModeChangedEventAsync(CancellationToken token)
        {
            await SendEventAsync(
                $"evt-playmode-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                "playmode.changed",
                _playInputService.CurrentPlayModeChangedPayload(),
                token);
        }

        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            Logger.Log("SYSTEM", $"PlayMode 状态变更: {change}");
            if (change == PlayModeStateChange.ExitingEditMode ||
                change == PlayModeStateChange.EnteredPlayMode ||
                change == PlayModeStateChange.ExitingPlayMode)
            {
                _compileService.ClearStaleCompileBusy(
                    $"PlayMode state changed: {change}",
                    ignoreEditorCompiling: true);
            }

            UpilotOperationTracker.Instance.RecordSystemEvent(
                "sys.playmode.changed", "PlayMode状态变更",
                $"state={change}");
            if (_cts == null || _cts.IsCancellationRequested) return;
            _ = SendPlayModeChangedEventAsync(_cts.Token);
        }

        private static string GetFocusStateString()
        {
            try
            {
                var fw = EditorWindow.focusedWindow;
                if (fw != null)
                    return $"focused({fw.GetType().Name})";
                return "unfocused";
            }
            catch
            {
                return "unknown";
            }
        }

        private void OnBeforeAssemblyReload()
        {
            var focus = GetFocusStateString();
            var isCompiling = CurrentIsCompiling();
            UpilotOperationTracker.Instance.RecordSystemEvent(
                "sys.domain.reload.start", "Domain Reload开始",
                $"ws={(_ws?.State == WebSocketState.Open ? "连接中" : "未连接")} 认证={(_isAuthenticated ? "是" : "否")} 编译={(isCompiling ? "是" : "否")}");
            if (_ws?.State == WebSocketState.Open && _isAuthenticated)
            {
                try
                {
                    var payload = new DomainReloadPayload
                    {
                        phase = "starting",
                        isCompiling = isCompiling,
                        playModeState = _playInputService.CurrentPlayModeChangedPayload().state,
                    };
                    // Synchronous send — we must complete before the domain unloads
                    var msg = new EventMessage<DomainReloadPayload>
                    {
                        id = $"evt-domain-reload-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                        name = "domain_reload.starting",
                        payload = payload,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        sessionId = _sessionId,
                    };
                    var json = JsonUtility.ToJson(msg);
                    Logger.LogNetwork("NETWORK",
                        $"sessionId={_sessionId} | name=domain_reload.starting | type=event | id={msg.id} | (sync)", isSend: true);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
                       .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("NETWORK", $"Send domain_reload.starting failed: {ex.Message}");
                }
            }
        }

        private void OnAfterAssemblyReload()
        {
            var focus = GetFocusStateString();
            UpilotOperationTracker.Instance.RecordSystemEvent(
                "sys.domain.reload.done", "Domain Reload完成",
                $"Bridge={(_started ? "运行中" : "已停止")} 焦点={focus}");

            // Restore compile errors from disk after domain reload
            _compileService?.TryRestoreFromDisk();

            if (!_started && !Application.isBatchMode)
            {
                Logger.Log("SYSTEM", "Bridge auto-recovered after Domain Reload");
                EnsureStarted();
            }
            else if (_started && !Application.isBatchMode && (_ws == null || _ws.State != WebSocketState.Open))
            {
                Logger.LogWarning("SYSTEM", "Bridge appears running but connection loop is dead after Domain Reload; restarting...");
                Restart();
            }
        }

        private void OnCompilationStarted(object _)
        {
            _pipelineCompileStartUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _pipelineCompileFromMcp = _compileService.IsRequestCompileActive && !string.IsNullOrEmpty(_compileService.LastRequestId);
            _pipelineCompileStatusRequestId = _pipelineCompileFromMcp
                ? _compileService.LastRequestId
                : $"editor-{_pipelineCompileStartUtcMs}";
            var focus = GetFocusStateString();
            UpilotOperationTracker.Instance.RecordSystemEvent(
                "sys.compile.start", "Unity编译开始",
                $"ws={(_ws?.State == WebSocketState.Open ? "连接中" : "未连接")} 焦点={focus}");
            var tok = _cts?.Token ?? CancellationToken.None;
            _ = SendCompileStartedMcpPushAsync(tok);
        }

        private async Task SendCompileStartedMcpPushAsync(CancellationToken token)
        {
            await SendCompilePipelineEventAsync(
                "compile.pipeline.started",
                new CompilePipelinePayload
                {
                    phase = "started",
                    source = "pipeline",
                    startedAt = _pipelineCompileStartUtcMs,
                    durationMs = 0,
                },
                token);

            if (_ws?.State != WebSocketState.Open || !_isAuthenticated) return;

            CompileStatusPayload startedStatus;
            if (_pipelineCompileFromMcp)
                startedStatus = _compileService.BuildStartedStatusPayload(_compileService.LastRequestId);
            else
                startedStatus = new CompileStatusPayload
                {
                    requestId = _pipelineCompileStatusRequestId,
                    status = "started",
                    errorCount = 0,
                    warningCount = 0,
                    startedAt = _pipelineCompileStartUtcMs,
                    finishedAt = 0,
                };

            await SendEventAsync(
                $"evt-compile-status-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                "compile.status",
                startedStatus,
                token);

            var lifeStarted = new CompileLifecyclePayload
            {
                phase = "started",
                requestId = _pipelineCompileStatusRequestId,
                source = _pipelineCompileFromMcp ? "mcp" : "editor",
                startedAt = startedStatus.startedAt,
                finishedAt = 0,
                errorCount = 0,
                warningCount = 0,
                durationMs = 0,
            };
            await SendEventAsync(
                $"evt-compile-started-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                "compile.started",
                lifeStarted,
                token);
            await SendEditorStateOnlyAsync(token);
        }

        private void OnCompilationFinished(object _)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var duration = now - _pipelineCompileStartUtcMs;
            if (duration < 0) duration = 0;
            var focus = GetFocusStateString();
            UpilotOperationTracker.Instance.RecordSystemEvent(
                "sys.compile.done", "Unity编译完成",
                $"耗时={duration}ms errors={_compileService.LastErrorCount} ws={(_ws?.State == WebSocketState.Open ? "连接中" : "未连接")} 焦点={focus}");
            var tok = _cts?.Token ?? CancellationToken.None;
            _ = SendCompilePipelineEventAsync(
                "compile.pipeline.finished",
                new CompilePipelinePayload
                {
                    phase = "finished",
                    source = "pipeline",
                    startedAt = _pipelineCompileStartUtcMs,
                    durationMs = duration,
                },
                tok);

            _mainThreadQueue.Enqueue(() =>
            {
                _ = SendCompileFinishedMcpPushAsync(tok, duration);
            });

            if (!_started && !Application.isBatchMode)
            {
                Logger.Log("SYSTEM", "Bridge auto-recovered after compilation");
                EnsureStarted();
            }
        }

        private async Task SendCompileFinishedMcpPushAsync(CancellationToken token, long pipelineDurationMs)
        {
            if (_ws?.State != WebSocketState.Open || !_isAuthenticated) return;

            var finishedPayload = _compileService.BuildFinishedStatusPayload(_pipelineCompileStatusRequestId);
            await SendEventAsync(
                $"evt-compile-status-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                "compile.status",
                finishedPayload,
                token);

            var dur = finishedPayload.finishedAt > 0 && finishedPayload.startedAt > 0
                ? finishedPayload.finishedAt - finishedPayload.startedAt
                : pipelineDurationMs;
            var lifeFinished = new CompileLifecyclePayload
            {
                phase = "finished",
                requestId = _pipelineCompileStatusRequestId,
                source = _pipelineCompileFromMcp ? "mcp" : "editor",
                startedAt = finishedPayload.startedAt,
                finishedAt = finishedPayload.finishedAt,
                errorCount = finishedPayload.errorCount,
                warningCount = finishedPayload.warningCount,
                durationMs = dur,
            };
            await SendEventAsync(
                $"evt-compile-finished-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                "compile.finished",
                lifeFinished,
                token);

            await SendCompileFinishedSnapshotAsync(token);
            await SendEditorStateOnlyAsync(token);
        }

        private async Task SendCompilePipelineEventAsync(string eventName, CompilePipelinePayload payload, CancellationToken token)
        {
            if (_ws?.State != WebSocketState.Open || !_isAuthenticated) return;
            try
            {
                await SendEventAsync(
                    $"evt-{eventName}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    eventName,
                    payload,
                    token);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("COMPILE", $"Send {eventName} failed: {ex.Message}");
            }
        }

        private async Task SendCompileFinishedSnapshotAsync(CancellationToken token)
        {
            try
            {
                if (CurrentIsCompiling())
                {
                    Logger.Log("COMPILE", "Compile snapshot skipped: still compiling");
                    return;
                }

                var errorsPayload = _compileService.BuildLastCompileErrorsPayload();
                await SendEventAsync(
                    $"evt-compile-errors-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    "compile.errors", errorsPayload, token);
                Logger.Log("COMPILE", $"Compile errors sent: {errorsPayload.total}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("COMPILE", $"Send compile.errors failed: {ex.Message}");
            }
        }

        /// <summary>Thread-safe WS send guarded by semaphore.</summary>
        private async Task SendJsonAsync(string json, CancellationToken token)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;

            LogOutboundCommand(json);

            var bytes = Encoding.UTF8.GetBytes(json);
            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private void LogInboundCommand(BridgeEnvelope envelope, string json)
        {
            if (envelope == null) return;
            var type = string.IsNullOrEmpty(envelope.type) ? "(unknown)" : envelope.type;
            if (type == "heartbeat" ||
                string.Equals(envelope.name, "session.heartbeat", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(envelope.name, "uiflow.results", StringComparison.OrdinalIgnoreCase)) return;

            var payload = Logger.TruncatePayload(UpilotWireJson.StripEnvelopeForDisplay(json));
            Logger.LogNetwork("NETWORK",
                $"sessionId={envelope.sessionId} | name={envelope.name} | type={envelope.type} | id={envelope.id} | {payload}",
                isSend: false);
        }

        private void LogOutboundCommand(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            var envelope = JsonUtility.FromJson<BridgeEnvelope>(json);
            if (envelope == null) return;
            var type = string.IsNullOrEmpty(envelope.type) ? "(unknown)" : envelope.type;
            if (type == "heartbeat") return;
            if (string.Equals(envelope.name, "uiflow.results", StringComparison.OrdinalIgnoreCase)) return;

            var payload = Logger.TruncatePayload(UpilotWireJson.StripEnvelopeForDisplay(json));
            Logger.LogNetwork("NETWORK",
                $"sessionId={envelope.sessionId} | name={envelope.name} | type={envelope.type} | id={envelope.id} | {payload}",
                isSend: true);
        }
    }
}
