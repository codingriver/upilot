// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

namespace CodingRiver.UPilot
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
        public string AuthenticationErrorCode;
        public string AuthenticationError;
        public int ServerIdentityContractVersion;
        public long AuthenticationFailureAtUtcMs;
        public long LastConnectedAtUtcMs;
        public long LastAuthenticatedAtUtcMs;
        public long LastDisconnectedAtUtcMs;
        public string LastCloseCode;
        public string LastCloseReason;
        public long LastOversizeAtUtcMs;
        public string LastOversizeSource;
        public int LastOversizeActualBytes;
        public int LastOversizeLimitBytes;
        public int OversizeCount;
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

    public sealed partial class UPilotBridge
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
                _projectPathHashSuffix = ComputeProjectPathHash(root);

                return _projectPathHashSuffix;
            }
        }

        internal static string ComputeProjectPathHash(string root)
        {
            var normalized = root.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
            using var sha = SHA256.Create();
            var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
            var sb = new StringBuilder(16);
            for (var i = 0; i < 8; i++) sb.Append(digest[i].ToString("x2"));
            return sb.ToString();
        }

        private static string WsHostPrefsKey => $"upilot.WsHost.{ProjectPathHashSuffix}";
        private static string WsPortPrefsKey => $"upilot.WsPort.{ProjectPathHashSuffix}";
        private static string HttpPortPrefsKey => $"upilot.HttpPort.{ProjectPathHashSuffix}";
        internal const int   DefaultHeartbeatIntervalSeconds = 3;
        private const int    MainThreadContextFreshnessMs = 5000;
        private int          _heartbeatIntervalMs = DefaultHeartbeatIntervalSeconds * 1000;
        internal const int   IdentityContractVersion = 1;
        private const int    MaxLogEntries    = 1000;
        private const int    ExecutionStatePersistenceAttempts = 3;
        private const int    ExecutionStatePersistenceRetryDelayMs = 5;

        private static readonly Lazy<UPilotBridge> Lazy = new(() => new UPilotBridge());
        public static UPilotBridge Instance => Lazy.Value;

        private readonly UPilotCompileService  _compileService    = new();
        private readonly UPilotPlayInputService _playInputService  = new();
        private readonly UPilotCommandRouter   _router            = new();

        public UPilotCompileService CompileService => _compileService;
        internal UPilotExecutionService ExecutionService => _executionService;
        internal UPilotReflectionService ReflectionService => _reflectionService;
        private readonly ConcurrentQueue<Action>   _mainThreadQueue   = new();
        private readonly ConcurrentDictionary<Action, string> _trackedQueueActions = new();
        private readonly object _queueObservationLock = new();
        private Action _executingQueueAction;
        private readonly SemaphoreSlim             _sendLock          = new(1, 1);
        private readonly SemaphoreSlim             _heartbeatIntervalChanged = new(0, 1);
        private readonly SemaphoreSlim _reconnectIntervalChanged = new(0, 1);
        private int _reconnectDelayMs = 1000;
        private readonly object                    _executionStatePublicationLock = new();
        private static readonly object             ExecutionStatePersistenceLock = new();
        private readonly object                    _logLock           = new();
        private readonly List<BridgeLogEntry>      _logBuffer         = new(MaxLogEntries);
        private readonly int                       _mainThreadId;

        // Module services (initialized in constructor after Router is available)
        private UPilotConsoleService        _consoleService;
        private UPilotConsoleCaptureService _consoleCaptureService;
        private UPilotGameObjectService _gameObjectService;
        private UPilotSceneService      _sceneService;
        private UPilotComponentService  _componentService;
        private UPilotSnapshotService   _snapshotService;
        private UPilotAssetService      _assetService;
        private UPilotPrefabService     _prefabService;
        private UPilotMaterialService   _materialService;
        private UPilotMenuService       _menuService;
        private UPilotPackageService    _packageService;
        private UPilotTestService       _testService;
        // P2 services
        private UPilotScriptService     _scriptService;
        private UPilotExecutionService  _executionService;
        private UPilotReflectionService _reflectionService;
        private ReflectionEvalService       _reflectionEvalService;
        private UPilotBatchService      _batchService;
        private UPilotSelectionService  _selectionService;
        private UPilotResourceService   _resourceService;
        private UPilotBuildService      _buildService;
        private UPilotDragDropService   _dragDropService;
        private UPilotKeyboardService   _keyboardService;
        // UIToolkit MCP disabled — UPilotUIToolkitService not registered (restore with RegisterCommands).
        // private UPilotUIToolkitService  _uiToolkitService;
        private UPilotEditorService     _editorService;
        private UPilotWindowService     _windowService;
        private UPilotEditorDelayService _editorDelayService;
        private UPilotOperationService  _operationService;
        private UPilotRuntimeDiagnosticsService _runtimeDiagnosticsService;
        private object _flowService;

        private readonly ConcurrentQueue<Action> _controlQueue = new ConcurrentQueue<Action>();
        private volatile bool _resettingWork;
        private bool _resetExecuting;
        private volatile bool _admissionClosed;
        private string _resetErrors = "";
        internal void EnqueueControl(Action action)
        {
            var origin = RequestOrigin.Value;
            _controlQueue.Enqueue(() => {
                if (origin != null && !IsOriginCurrent(origin, !origin.MaintenanceControl)) return;
                var previous = RequestOrigin.Value;
                try { RequestOrigin.Value = origin; action(); }
                finally { RequestOrigin.Value = previous; }
            });
        }

        private string GetAdmissionErrorCode(string commandName)
        {
            if (commandName == "service.restart" && RequestOrigin.Value?.MaintenanceControl == true) return null;
            if (_resetExecuting || _resettingWork) return "UPILOT_RESET_IN_PROGRESS";
            // Authentication closes admission until the main-thread handshake is applied.
            // Normal Domain Reload/reconnect is waiting, not a hard stop.
            if (!IsStateSynchronized) return "UPILOT_RECONNECT_PENDING";
            if ((_cachedIsCompiling || _compileService.VerificationPending) && !IsCompileObservation(commandName))
                return "EDITOR_BUSY";
            return null;
        }

        internal string ResetActiveWork(string preservedRecoveryOwnerId = "")
        {
            if (ActiveWorkResetOverride != null) return ActiveWorkResetOverride(preservedRecoveryOwnerId);
            if (_resetExecuting) return _resetErrors;
            CloseOrdinaryAdmission("service_reset");
            _resetExecuting = true;
            _resettingWork = true;
            try
            {
                LifecycleManager.CancelExistingServiceRecoveryForReset(preservedRecoveryOwnerId);
                UPilotServiceLifetime.Advance(); // Fence callbacks before touching any owned resource.
                var errors = new System.Collections.Generic.List<string>();
                void Reset(Action action)
                {
                    try { action(); }
                    catch (Exception ex)
                    {
                        errors.Add(ex.Message);
                        Logger.ReportFailure("SYSTEM", "UPilot activity reset failed", ex,
                            scope: "bridge-reset", fingerprint: action.Method.Name);
                    }
                }
                lock (_queueObservationLock)
                {
                    while (_mainThreadQueue.TryDequeue(out _)) { }
                    _trackedQueueActions.Clear();
                }
                Reset(() => UPilotUpmRequestObservation.ResetAll("service_reset"));
                Reset(() => LifecycleManager.ResetStopObservation("service_reset"));
                Reset(() => UPilotOperationTracker.Instance.AbortActive());
                Reset(() => Automation.UPilotAutomationStepService.ResetActive());
                Reset(() => _testService?.ResetActive());
                Reset(() => _snapshotService?.ResetActive());
                Reset(() => _batchService?.ResetActive());
                Reset(() => _buildService?.ResetActive());
                Reset(() => _editorDelayService?.ResetActive());
                Reset(() => _flowService?.GetType().GetMethod("ResetActive")?.Invoke(_flowService, null));
                Reset(UPilotRuntimeDiagnosticsService.ResetActive);
                Reset(() => _executionService?.HardCloseModules());
                Reset(() => _executionService?.Sessions.CloseAll());
                Reset(UPilotConsoleCaptureService.ResetActive);
                _resetErrors = string.Join("; ", errors);
                return _resetErrors;
            }
            finally { _resetExecuting = false; }
        }

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
        private string _authenticationErrorCode = "";
        private string _authenticationError = "";
        private int _serverIdentityContractVersion;
        private long _authenticationFailureAtUtcMs;
        private long _lastConnectedAtUtcMs;
        private long _lastAuthenticatedAtUtcMs;
        private long _lastDisconnectedAtUtcMs;
        private string _lastCloseCode = "";
        private string _lastCloseReason = "";
        private long _lastOversizeAtUtcMs;
        private string _lastOversizeSource = "";
        private int _lastOversizeActualBytes;
        private int _oversizeCount;
        private long                 _lastHeartbeatSentAt;
        private string               _processRole = "unknown";
        private string               _activeSceneName = string.Empty;
        private long                 _lastMainThreadPumpAt;
        private string               _lastDequeuedCommandId = string.Empty;
        private string               _cachedPlayModeState = "edit";
        private bool                 _cachedIsPlaying;
        private bool                 _cachedIsPaused;
        private bool                 _cachedIsCompiling;
        private bool                 _cachedIsUpdating;
        private MaintenanceInitializationPayload _maintenanceInitialization;
        private long                 _pipelineCompileStartUtcMs;
        private string               _pipelineCompileStatusRequestId = string.Empty;
        private bool                 _pipelineCompileFromMcp;
        private string               _producerEpoch = string.Empty;
        private long                 _domainGeneration;
        private long                 _stateSequence;
        private string               _lastStateTransition = "startup";
        private bool                 _preReloadPublishFailed;
        private string               _wsHost = DefaultWsHost;
        private int                  _wsPort = DefaultWsPort;
        private int                  _httpPort = DefaultHttpPort;
        private bool                 _debugWireLogsEnabled;
        private bool                 _verboseLogsEnabled;
        private bool                 _autoRestartOnCriticalStuck;

        private UPilotBridge()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            RestoreExecutionStateIdentity();
            _compileService.LocalStateChanged += ObserveLocalCompileState;
            _compileService.ConfigureEvidenceIdentity(Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath,
                Process.GetCurrentProcess().Id, GetCurrentProcessCreatedAt(), _producerEpoch);
            LoadDefaultEndpoints();
            LoadHeartbeatIntervalPreference();
            ApplyTimingPreferenceChange("");
            UPilotTimingSettings.Changed += ApplyTimingPreferenceChange;
            _debugWireLogsEnabled = EditorPrefs.GetBool(UPilotPreferences.DebugWireLogsKey, false);
            _verboseLogsEnabled = EditorPrefs.GetBool(UPilotPreferences.VerboseLogsKey, false);
            Logger.SetRuntimeLogOptions(_debugWireLogsEnabled, _verboseLogsEnabled);
            _autoRestartOnCriticalStuck = EditorPrefs.GetBool(UPilotPreferences.AutoRestartOnStuckKey, false);
            RegisterLegacyCommands();
            RegisterModuleServices();
        }

        private void LoadDefaultEndpoints()
        {
            _wsHost = DefaultWsHost;
            _wsPort = DefaultWsPort;
            _httpPort = DefaultHttpPort;
        }

        /// <summary>The command router. Modules register handlers via Router.Register().</summary>
        public UPilotCommandRouter Router => _router;

        /// <summary>The main-thread work queue. Enqueue actions that must run on Unity's main thread.</summary>
        public ConcurrentQueue<Action> MainThreadQueue => _mainThreadQueue;

        // ── Public status API (called by status window on main thread) ──────────

        public string WsHost => _wsHost;
        public int HeartbeatIntervalSeconds
        {
            get => HeartbeatIntervalMs / 1000;
            set
            {
                var seconds = Math.Max(1, Math.Min(600, value));
                var milliseconds = seconds * 1000;
                if (Interlocked.Exchange(ref _heartbeatIntervalMs, milliseconds) == milliseconds) return;
                EditorPrefs.SetInt(UPilotPreferences.BridgeHeartbeatIntervalSecondsKey, seconds);
                SignalHeartbeatIntervalChanged();
            }
        }

        internal int HeartbeatIntervalMs => Volatile.Read(ref _heartbeatIntervalMs);
        internal int HeartbeatTimeoutMs => HeartbeatIntervalMs * 3;

        private void LoadHeartbeatIntervalPreference()
        {
            var seconds = EditorPrefs.GetInt(UPilotPreferences.BridgeHeartbeatIntervalSecondsKey,
                DefaultHeartbeatIntervalSeconds);
            _heartbeatIntervalMs = Math.Max(1, Math.Min(600, seconds)) * 1000;
        }

        internal void ResetHeartbeatIntervalToDefault()
        {
            var milliseconds = DefaultHeartbeatIntervalSeconds * 1000;
            if (Interlocked.Exchange(ref _heartbeatIntervalMs, milliseconds) != milliseconds)
                SignalHeartbeatIntervalChanged();
        }

        private int _watchdogIntervalMs;
        internal int WatchdogIntervalMs => Volatile.Read(ref _watchdogIntervalMs);
        internal int ReconnectDelayMs => Volatile.Read(ref _reconnectDelayMs);
        private void ApplyTimingPreferenceChange(string name)
        {
            if (name.Length == 0 || name == nameof(UPilotTimingSettings.BridgeReconnectDelaySeconds))
            {
                var value = UPilotTimingSettings.BridgeReconnectDelaySeconds * 1000;
                if (Interlocked.Exchange(ref _reconnectDelayMs, value) != value)
                {
                    try { _reconnectIntervalChanged.Release(); } catch (SemaphoreFullException) { }
                }
            }
            if (name.Length == 0 || name == nameof(UPilotTimingSettings.WatchdogIntervalSeconds))
                Interlocked.Exchange(ref _watchdogIntervalMs, UPilotTimingSettings.WatchdogIntervalSeconds * 1000);
        }

        private void SignalHeartbeatIntervalChanged()
        {
            try { _heartbeatIntervalChanged.Release(); }
            catch (SemaphoreFullException) { /* Coalesce changes until the existing loop consumes the signal. */ }
        }

        public int WsPort => _wsPort;
        public int HttpPort
        {
            get => _httpPort;
            set
            {
                if (_httpPort == value) return;
                SetProjectEndpoints(_wsHost, _wsPort, value);
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
                EditorPrefs.SetBool(UPilotPreferences.DebugWireLogsKey, value);
                Logger.SetRuntimeLogOptions(_debugWireLogsEnabled, _verboseLogsEnabled);
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
                EditorPrefs.SetBool(UPilotPreferences.VerboseLogsKey, value);
                Logger.SetRuntimeLogOptions(_debugWireLogsEnabled, _verboseLogsEnabled);
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
                EditorPrefs.SetBool(UPilotPreferences.AutoRestartOnStuckKey, value);
                Logger.Log("SYSTEM", value ? "临界超时自动重启已开启" : "临界超时自动重启已关闭");
            }
        }

        /// <summary>
        /// 更新 WebSocket 地址并写入项目级 UPilot 配置。
        /// </summary>
        public void SetWsEndpoint(string host, int port)
        {
            SetProjectEndpoints(host, port, _httpPort);
        }

        public void ApplyProjectEndpoints(string wsHost, int wsPort, int httpPort)
        {
            _wsHost = string.IsNullOrWhiteSpace(wsHost) ? DefaultWsHost : wsHost.Trim();
            _wsPort = wsPort > 0 ? wsPort : DefaultWsPort;
            _httpPort = httpPort > 0 ? httpPort : DefaultHttpPort;
            Logger.Log("NETWORK", $"应用项目配置端点: ws={_wsHost}:{_wsPort} http={_httpPort}");
        }

        public void SetProjectEndpoints(string host, int wsPort, int httpPort)
        {
            UPilotProjectConfigData config;
            try
            {
                config = System.IO.File.Exists(UPilotProjectConfig.ConfigPath)
                    ? UPilotPortRegistry.ReadConfig(UPilotProjectConfig.ProjectRoot)
                    : JsonUtility.FromJson<UPilotProjectConfigData>(JsonUtility.ToJson(UPilotProjectConfig.Current));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UPilotPorts] 读取工程端口配置失败：{ex}");
                throw;
            }
            config.mcp ??= new UPilotMcpConfig();
            config.mcp.wsHost = string.IsNullOrWhiteSpace(host) ? DefaultWsHost : host.Trim();
            config.mcp.wsPort = wsPort;
            config.mcp.httpPort = httpPort;
            UPilotProjectConfig.Save(config, updateEndpoints: true);
            ApplyProjectEndpoints(config.mcp.wsHost, wsPort, httpPort);
        }

        public string GetServerUrl() => $"ws://{_wsHost}:{_wsPort}";

        private bool CurrentIsCompiling()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                return _compileService.IsCompiling;
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
            PlayModeState       = CachedPlayModeChangedPayload().state,
            McpLabel            = _mcpLabelFromServer ?? "",
            McpServerHost       = string.IsNullOrEmpty(_mcpHostFromServer) ? _wsHost : _mcpHostFromServer,
            McpServerPort       = _mcpPortFromServer > 0 ? _mcpPortFromServer : _wsPort,
            McpWorkspaceAbsolutePath = _mcpWorkspacePathFromServer ?? "",
            AuthenticationErrorCode = _authenticationErrorCode,
            AuthenticationError = _authenticationError,
            ServerIdentityContractVersion = _serverIdentityContractVersion,
            AuthenticationFailureAtUtcMs = _authenticationFailureAtUtcMs,
            LastConnectedAtUtcMs = _lastConnectedAtUtcMs,
            LastAuthenticatedAtUtcMs = _lastAuthenticatedAtUtcMs,
            LastDisconnectedAtUtcMs = _lastDisconnectedAtUtcMs,
            LastCloseCode = _lastCloseCode,
            LastCloseReason = _lastCloseReason,
            LastOversizeAtUtcMs = _lastOversizeAtUtcMs,
            LastOversizeSource = _lastOversizeSource,
            LastOversizeActualBytes = _lastOversizeActualBytes,
            LastOversizeLimitBytes = MaxBridgeResponseBytes,
            OversizeCount = _oversizeCount,
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
            if (UPilotServiceMaintenance.IsActive && !UPilotServiceMaintenance.IsExecuting)
                return;
            UPilotOperationTracker.Instance.RecordSystemEvent(
                "sys.bridge.restart", "Bridge重启", "手动触发重启");
            UPilotMcpServerManager.Instance.RestartPreparedServer(() => EnsureStarted());
        }

        // ── Lifecycle ───────────────────────────────────────────────────────────

        public void EnsureStarted()
        {
            if (UPilotUpdateService.Instance.IsServiceStartBlocked)
            {
                Logger.LogWarning("SYSTEM", UPilotUpdateService.ServiceStartBlockedMessage);
                return;
            }

            if (_started)
            {
                if (IsConnectLoopAlive()) return;

                Logger.LogWarning("SYSTEM", "Bridge marked started but connect loop is not alive; restarting loop.");
                UPilotOperationTracker.Instance.RecordSystemEvent(
                    "sys.bridge.loop.recover", "连接循环自愈",
                    $"endpoint={GetServerUrl()} wsState={_ws?.State}");
                StartConnectLoop();
                return;
            }
            var processRole = DetermineProcessRole();
            _processRole = processRole;
            if (!IsMainEditorProcess(processRole))
            {
                Logger.LogWarning("SYSTEM", $"Bridge startup skipped: auxiliary Unity process role '{processRole}'.");
                return;
            }
            _connectFailureStreak = 0;
            LifecycleManager.ResetReadyRecoveryBudget();
            CloseOrdinaryAdmission("bridge_starting");
            LifecycleManager.ReleaseBridgeOnlyShutdown();
            _boundServerLifetime = SessionState.GetString(ServerLifetimeSessionKey, "");
            lock (ConnectionStateLock)
            {
                _started = true;
                _lastHeartbeatSentAt = 0;
                _sessionId = Guid.NewGuid().ToString("N");
                _cts = new CancellationTokenSource();
            }
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.pauseStateChanged += OnPauseStateChanged;
            EditorApplication.update += ProcessMainThreadQueue;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            UPilotOperationTracker.Instance.RecordSystemEvent(
                "sys.bridge.start", "Bridge启动",
                $"sessionId={_sessionId} endpoint={GetServerUrl()}");
            StartConnectLoop();
        }

        public void Stop()
        {
            CloseOrdinaryAdmission("bridge_stopping");
            LifecycleManager.MarkBridgeClosing();
            LifecycleManager.ResetReadyRecoveryBudget();
            bool wasAuthenticated, wasWsOpen;
            string stoppedSessionId;
            long stoppedEpoch;
            ClientWebSocket wsToClose;
            CancellationTokenSource cancellation;
            lock (ConnectionStateLock)
            {
                wasAuthenticated = _isAuthenticated;
                wasWsOpen = _ws?.State == WebSocketState.Open;
                stoppedEpoch = ++_helloConnectionEpoch;
                stoppedSessionId = _sessionId;
                _started = false;
                _isAuthenticated = false;
                _admissionClosed = true;
                _pendingAuthenticationHello = null;
                wsToClose = _ws;
                _ws = null;
                cancellation = _cts;
                _cts = null;
                _lastHeartbeatSentAt = 0;
                _connectFailureStreak = 0;
                _connectLoopTask = null;
                ClearMcpServerDisplayState();
            }
            DiscardPendingHello("bridge_closing");
            UPilotUnityConfiguration.ResetStartupDelay();
            var unityPid = Process.GetCurrentProcess().Id;
            Logger.Lifecycle("bridge_stop_started", $"unityPid={unityPid} sessionId={stoppedSessionId} connectionEpoch={stoppedEpoch} {LifecycleManager.CaptureServiceShutdown().Detail}");
            try
            {
                AbortAndDisposeWebSocket(wsToClose);
                cancellation?.Cancel();
            }
            finally
            {
                UPilotUpmRequestObservation.ResetOwner(this, "bridge_stopped");
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
                EditorApplication.pauseStateChanged -= OnPauseStateChanged;
                EditorApplication.update -= ProcessMainThreadQueue;
                CompilationPipeline.compilationStarted -= OnCompilationStarted;
                CompilationPipeline.compilationFinished -= OnCompilationFinished;
                AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
                AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
                Logger.Lifecycle("bridge_stop_finished", $"unityPid={unityPid} sessionId={stoppedSessionId} socketAbortRequested=true started={_started}");
                UPilotOperationTracker.Instance.RecordSystemEvent(
                    "sys.bridge.stop", "Bridge停止",
                    $"原因=手动停止 连接状态={(wasWsOpen ? "已连接" : "未连接")} 认证状态={(wasAuthenticated ? "已认证" : "未认证")} socket=abort",
                    "stopped");
            }
        }

        private static void AbortAndDisposeWebSocket(ClientWebSocket ws)
        {
            if (ws == null)
                return;

            try { ws.Abort(); } catch { }
            _ = Task.Run(() =>
            {
                try { ws.Dispose(); } catch { }
            });
        }

        private double _lastWatchdogCheck;
        private uint _connectFailureStreak;

        private bool IsConnectLoopAlive()
        {
            return _connectLoopTask != null && !_connectLoopTask.IsCompleted && _cts != null && !_cts.IsCancellationRequested;
        }

        private void StartConnectLoop()
        {
            CancellationToken token;
            lock (ConnectionStateLock)
            {
                if (!_started) return;
                if (_cts == null || _cts.IsCancellationRequested) _cts = new CancellationTokenSource();
                token = _cts.Token;
            }
            Debug.Log($"[UPilotBridge] 启动 Bridge 连接循环；端点={GetServerUrl()}。");
            var task = ConnectLoopAsync(token);
            lock (ConnectionStateLock)
            {
                if (_started && _cts != null && _cts.Token == token) _connectLoopTask = task;
            }
            _ = WatchConnectLoopAsync(task);
        }

        private async Task WatchConnectLoopAsync(Task loopTask)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
                if (_started && _connectLoopTask == loopTask && !(_cts?.IsCancellationRequested ?? true))
                {
                    Logger.LogError("NETWORK", "WS connect loop exited unexpectedly while Bridge is still started.");
                    UPilotOperationTracker.Instance.RecordSystemEvent(
                        "sys.bridge.loop.dead", "连接循环异常退出",
                        $"endpoint={GetServerUrl()} wsState={_ws?.State}",
                        "error");
                    EnqueueUntracked(() =>
                    {
                        if (_started && _connectLoopTask == loopTask)
                            StartConnectLoop();
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("NETWORK", $"WS connect loop crashed: {ex}");
                UPilotOperationTracker.Instance.RecordSystemEvent(
                    "sys.bridge.loop.crash", "连接循环崩溃",
                    ex.ToString(),
                    "error");
                EnqueueUntracked(() =>
                {
                    if (_started && _connectLoopTask == loopTask)
                        StartConnectLoop();
                });
            }
        }

        private void ProcessMainThreadQueue()
        {
            while (_controlQueue.TryDequeue(out var control))
            {
                try { control(); } catch (Exception ex) { Logger.ReportFailure("SYSTEM", "Control failed", ex, fingerprint: "control"); }
            }
            ApplyPendingOwnedHello();
            RefreshMainThreadObservation();
            PublishReadyIfInitialized();

            if (UPilotPlayModeTransitions.ReconcileEditorUpdate(_cachedIsPlaying,
                    EditorApplication.isPlayingOrWillChangePlaymode,
                    EditorApplication.isCompiling || _cachedIsUpdating, _sessionId))
            {
                UPilotMcpServerManager.Instance.InvalidateStatusCache();
                if (_cts != null && !_cts.IsCancellationRequested)
                    _ = PublishExecutionStateAsync("play_mode_recovered", _cts.Token);
            }

            UPilotUnityConfiguration.Update(_started && _isAuthenticated && _ws != null &&
                _ws.State == WebSocketState.Open && !_admissionClosed && !_resettingWork &&
                !_cachedIsCompiling && !_cachedIsUpdating, _ws);

            while (true)
            {
                Action action;
                lock (_queueObservationLock)
                {
                    if (!_mainThreadQueue.TryDequeue(out action)) break;
                    Volatile.Write(ref _executingQueueAction, action);
                }
                try { action(); }
                catch (OperationCanceledException ex) when (ex.CancellationToken.IsCancellationRequested) { }
                catch (Exception ex) { Logger.ReportFailure("SYSTEM", "Bridge main thread error", ex); }
                finally
                {
                    lock (_queueObservationLock)
                    {
                        Volatile.Write(ref _executingQueueAction, null);
                        _trackedQueueActions.TryRemove(action, out _);
                    }
                }
            }

            if (UPilotStartupDiagnostics.NeedsEditorStateObservation)
            {
                var startupContext = BuildEditorContextPayload("startup-diagnostics");
                UPilotStartupDiagnostics.ObserveEditorState(
                    startupContext.ready, startupContext.authoritative, startupContext.isStale, _sessionId);
            }

            CheckWatchdog(UPilotOperationTracker.Instance, EditorApplication.timeSinceStartup);
        }

        internal void CheckWatchdog(UPilotOperationTracker tracker, double now)
        {
            if (tracker.HasActiveCommands &&
                now - _lastWatchdogCheck > WatchdogIntervalMs / 1000d)
            {
                _lastWatchdogCheck = now;
                tracker.RunWatchdog();

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
            if (_resettingWork) throw new InvalidOperationException("UPILOT_RESET_IN_PROGRESS");
            var lifetime = UPilotServiceLifetime.Id;
            var origin = RequestOrigin.Value;
            var ctx = UPilotOperationTracker.Instance.GetContext(commandId);
            ctx?.SetPhase("queued");
            ctx?.Step("排队等待主线程");

            Action tracked = () =>
            {
                if (lifetime != UPilotServiceLifetime.Id ||
                    (origin != null && !IsOriginCurrent(origin, !origin.MaintenanceControl))) return;
                _lastDequeuedCommandId = commandId ?? string.Empty;
                ctx?.RecordMainThreadAction();
                ctx?.SetPhase("executing");
                ctx?.Step("主线程执行中");
                var previousOrigin = RequestOrigin.Value;
                try
                {
                    RequestOrigin.Value = origin;
                    var admissionError = origin == null ? null : GetAdmissionErrorCode(ctx?.CommandName);
                    if (admissionError != null)
                    {
                        ctx?.Fail(admissionError, "Editor admission changed before queued execution.", emitDiagnostic: false);
                        _ = SendErrorAsync(commandId, admissionError,
                            "Editor admission changed before queued execution.", origin.Token, ctx?.CommandName,
                            new ErrorDetailPayload { stage = "queued_execution", outcome = "not_executed",
                                commandSubmitted = false, sideEffectsMayHaveOccurred = false });
                        return;
                    }
                    action();
                    ctx?.Step("主线程执行完毕");
                }
                catch (Exception ex)
                {
                    ctx?.Fail("MAIN_THREAD_ERROR", ex.Message, emitDiagnostic: false);
                    throw;
                }
                finally { RequestOrigin.Value = previousOrigin; }
            };
            lock (_queueObservationLock)
            {
                _trackedQueueActions[tracked] = commandId ?? string.Empty;
                _mainThreadQueue.Enqueue(tracked);
            }
        }

        private void EnqueueUntracked(Action action)
        {
            lock (_queueObservationLock) _mainThreadQueue.Enqueue(action);
        }

        internal BridgeQueueSnapshot ObserveQueue(string excludeId)
        {
            lock (_queueObservationLock)
            {
                var queued = _mainThreadQueue.ToArray();
                var executing = Volatile.Read(ref _executingQueueAction);
                var active = UPilotOperationTracker.Instance.GetActiveCommandsSnapshot(excludeId);
                var result = new BridgeQueueSnapshot
                {
                    serviceLifecycleId = UPilotServiceLifetime.Id,
                    observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    activeCount = active.Count,
                    executingCount = executing == null ? 0 : 1,
                    complete = true,
                };
                foreach (var pair in active.Take(200))
                    result.activeCommands.Add(new BridgeQueueCommand { commandId = pair.Key, commandName = pair.Value });
                foreach (var action in queued)
                {
                    if (_trackedQueueActions.TryGetValue(action, out var id) && !string.IsNullOrEmpty(id))
                    {
                        if (string.Equals(id, excludeId, StringComparison.Ordinal)) continue;
                        result.queuedCount++;
                        if (result.queuedCommands.Count < 200)
                            result.queuedCommands.Add(new BridgeQueueCommand { commandId = id });
                    }
                    else result.untrackedCount++;
                }
                if (executing != null && _trackedQueueActions.TryGetValue(executing, out var executingId)
                    && !string.IsNullOrEmpty(executingId))
                {
                    result.executingCommandId = executingId;
                    if (string.Equals(executingId, excludeId, StringComparison.Ordinal)) result.executingCount = 0;
                }
                else if (executing != null) result.untrackedCount++;
                result.truncated = result.activeCount > result.activeCommands.Count
                    || result.queuedCount > result.queuedCommands.Count;
                result.complete = !result.truncated && result.untrackedCount == 0;
                return result;
            }
        }
        private const int ConnectTimeoutMs = 3000;  // 连接超时 3s
        private const int SocketTaskDrainTimeoutMs = 250;

        private async Task ConnectLoopAsync(CancellationToken token)
        {
            var tracker = UPilotOperationTracker.Instance;
            while (!token.IsCancellationRequested)
            {
                var cycleSocket = new ClientWebSocket();
                long cycleEpoch = 0;
                string cycleSessionId = null;
                bool wasAuthenticated = false, published = false;
                Task recvTask = Task.CompletedTask;
                Task hbTask   = Task.CompletedTask;
                using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(token);

                try
                {
                    var cycleShutdown = LifecycleManager.CaptureServiceShutdown();
                    published = TryPublishConnectionCycle(cycleSocket, token, out cycleEpoch, out cycleSessionId, out wasAuthenticated);
                    if (!published) break;
                    var serverUrl = GetServerUrl();
                    if (_verboseLogsEnabled)
                        Logger.Log("NETWORK", $"WS connecting to {serverUrl} (attempt={_connectFailureStreak + 1})");

                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    connectCts.CancelAfter(ConnectTimeoutMs);
                    try
                    {
                        await cycleSocket.ConnectAsync(new Uri(serverUrl), connectCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        throw new Exception($"连接超时 ({ConnectTimeoutMs / 1000}s)");
                    }

                    uint previousFailures = 0;
                    if (!TryUpdateConnectionCycle(cycleSocket, cycleEpoch, token, () =>
                    {
                        _lastConnectedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        previousFailures = _connectFailureStreak;
                        _connectFailureStreak = 0;
                    })) break;
                    if (previousFailures > 0)
                    {
                        Logger.Log("NETWORK", $"WS 重连成功（曾失败 {previousFailures} 次）");
                    }
                    else if (_verboseLogsEnabled)
                    {
                        Logger.Log("NETWORK", "WS connected");
                    }

                    tracker.RecordSystemEvent("sys.ws.connected", "WS连接成功",
                        $"endpoint={serverUrl} sessionId={cycleSessionId}");

                    await SendHelloAsync(cycleCts.Token, cycleSocket, cycleEpoch, cycleSessionId);
                    if (!IsConnectionCycleCurrent(cycleSocket, cycleEpoch, token)) break;

                    recvTask = ReceiveConnectionLoopAsync(cycleCts.Token, cycleSocket, cycleEpoch, cycleShutdown);
                    hbTask   = HeartbeatLoopAsync(cycleCts.Token, cycleSocket, cycleEpoch, cycleSessionId);

                    var completedTask = await Task.WhenAny(recvTask, hbTask);
                    var disconnectReason = AnalyzeDisconnectReason(recvTask, hbTask, completedTask, cycleCts.Token);
                    bool lostAuthentication = false;
                    if (!TryUpdateConnectionCycle(cycleSocket, cycleEpoch, token, () =>
                    {
                        lostAuthentication = wasAuthenticated || _isAuthenticated;
                        _lastDisconnectedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        _isAuthenticated = false;
                    })) break;
                    if (!token.IsCancellationRequested)
                        Logger.ReportWarning("NETWORK", $"连接断开 endpoint={serverUrl} reason={disconnectReason}\n{completedTask.Exception}",
                            scope: "bridge-connection", fingerprint: "disconnect|" + disconnectReason);

                    if (lostAuthentication)
                        tracker.RecordSystemEvent("sys.auth.lost", "认证丢失",
                            $"原因=连接断开 详情={disconnectReason}", "disconnected");

                    tracker.RecordSystemEvent("sys.ws.disconnected", "WS连接断开",
                        $"原因={disconnectReason} endpoint={serverUrl} wsState={cycleSocket.State}", "disconnected");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!TryUpdateConnectionCycle(cycleSocket, cycleEpoch, token, () => _connectFailureStreak++)) break;
                    if (ex is InitializationBudgetExpiredException expired)
                        ReportInitializationBudgetExpired(expired, GetServerUrl(), cycleSessionId, cycleEpoch);
                    else
                        Logger.ReportWarning("NETWORK", $"WS 连接失败: {ex}",
                            scope: "bridge-connection", fingerprint: ex.GetType().FullName + "|" + ex.Message);

                    if (wasAuthenticated)
                        tracker.RecordSystemEvent("sys.auth.lost", "连接失败导致认证丢失",
                            $"原因={ex.Message}", "disconnected");
                }
                finally
                {
                    var retiredCurrent = published && RetireConnectionCycle(cycleSocket, cycleEpoch);
                    if (!published) AbortAndDisposeWebSocket(cycleSocket);
                    cycleCts.Cancel();
                    if (retiredCurrent) UPilotQuickStart.NotifyDeploymentChanged();

                    var drainTask = Task.WhenAll(recvTask, hbTask);
                    if (await Task.WhenAny(drainTask, Task.Delay(SocketTaskDrainTimeoutMs)).ConfigureAwait(false) == drainTask)
                    {
                        try { await drainTask.ConfigureAwait(false); } catch { }
                    }
                }

                if (!token.IsCancellationRequested)
                {
                    try
                    {
                        var waitStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        while (!token.IsCancellationRequested)
                        {
                            var remaining = ReconnectDelayMs - (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - waitStartedAt);
                            if (remaining <= 0 || !await _reconnectIntervalChanged.WaitAsync((int)remaining, token).ConfigureAwait(false)) break;
                        }
                    }
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

        internal sealed class InitializationBudgetExpiredException : TimeoutException
        {
            internal readonly int RemainingBudgetMs;
            internal readonly long OriginalDeadlineTick, ReadyRecoveryDeadlineUtcMs;
            internal InitializationBudgetExpiredException(int remainingBudgetMs, long originalDeadlineTick, long readyRecoveryDeadlineUtcMs)
                : base("Bridge initialization budget expired.")
            {
                RemainingBudgetMs = remainingBudgetMs;
                OriginalDeadlineTick = originalDeadlineTick;
                ReadyRecoveryDeadlineUtcMs = readyRecoveryDeadlineUtcMs;
            }
        }

        internal static void ReportInitializationBudgetExpired(InitializationBudgetExpiredException failure,
            string endpoint, string sessionId, long epoch, string diagnosticScope = "bridge-connection")
        {
            Logger.ReportFailure("NETWORK", $"Bridge initialization failed: endpoint={endpoint} sessionId={sessionId} " +
                $"connectionEpoch={epoch} remainingBudgetMs={failure.RemainingBudgetMs} " +
                $"originalDeadlineTick={failure.OriginalDeadlineTick} readyRecoveryDeadlineUtcMs={failure.ReadyRecoveryDeadlineUtcMs} (0=unavailable); " +
                "Inspect the original service recovery failure; the original budget has not been renewed.",
                failure, scope: diagnosticScope, fingerprint: "initialization_budget_expired|" + endpoint + "|" + failure.ReadyRecoveryDeadlineUtcMs);
        }

        private async Task SendHelloAsync(CancellationToken token, ClientWebSocket socket, long epoch, string sessionId)
        {
            // Use project root (parent of Assets folder) as projectPath
            var projectRoot = Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath;
            var readyBudgetMs = LifecycleManager.GetReadyBudgetRemainingMs();
            if (readyBudgetMs <= 0)
                throw new InitializationBudgetExpiredException(readyBudgetMs, _readyDeadlineTick,
                    LifecycleManager.GetReadyRecoveryDeadlineUtcMs());
            var helloId = "h-" + Guid.NewGuid().ToString("N");
            if (!TryUpdateConnectionCycle(socket, epoch, token, () => {
                _readyHelloId = helloId;
                _readyProjectPath = projectRoot;
                _readyDeadlineTick = Stopwatch.GetTimestamp() + (long)(readyBudgetMs * (double)Stopwatch.Frequency / 1000);
            }, sessionId)) return;
            var msg = new HelloMessage
            {
                id = helloId,
                type = "hello",
                name = "session.hello",
                payload = new HelloPayload
                {
                    identityContractVersion = IdentityContractVersion,
                    bridgeReadyContractVersion = BridgeReadyContractVersion,
                    readyBudgetMs = readyBudgetMs,
                    heartbeatIntervalMs = HeartbeatIntervalMs,
                    unityVersion = Application.unityVersion,
                    projectPath = projectRoot,
                    platform = Application.platform == RuntimePlatform.OSXEditor ? "macos" :
                        Application.platform == RuntimePlatform.LinuxEditor ? "linux" : "windows",
                    processId = Process.GetCurrentProcess().Id,
                    processCreatedAt = GetCurrentProcessCreatedAt(),
                    processRole = _processRole,
                },
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sessionId = sessionId,
            };
            await SendConnectionJsonAsync(JsonUtility.ToJson(msg), token, socket, epoch);
        }

        internal static string DetermineProcessRole()
        {
            var isAssetImportWorker = false;
            try
            {
                isAssetImportWorker = AssetDatabase.IsAssetImportWorkerProcess();
            }
            catch { /* older Unity versions may not expose worker state here */ }

            var args = Environment.GetCommandLineArgs() ?? Array.Empty<string>();
            return DetermineProcessRoleFromArguments(Application.isBatchMode, isAssetImportWorker, args);
        }

        internal static bool IsMainEditorProcess()
        {
            return IsMainEditorProcess(DetermineProcessRole());
        }

        internal static bool IsMainEditorProcess(string processRole)
        {
            return string.Equals(processRole, "mainEditor", StringComparison.Ordinal);
        }

        internal static string DetermineProcessRoleFromArguments(
            bool isBatchMode,
            bool isAssetImportWorker,
            string[] args)
        {
            if (isBatchMode) return "batchMode";
            if (isAssetImportWorker) return "assetImportWorker";
            args ??= Array.Empty<string>();

            for (var index = 0; index < args.Length; index++)
            {
                var value = (args[index] ?? "").Trim();
                if (value.IndexOf("assetimportworker", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "assetImportWorker";
            }

            // Explicit role switches take precedence over the generic -ump worker marker.
            for (var index = 0; index < args.Length; index++)
            {
                var value = (args[index] ?? "").Trim();
                if ((string.Equals(value, "-ump-process-role", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(value, "-editor-mode", StringComparison.OrdinalIgnoreCase)) &&
                    index + 1 < args.Length &&
                    string.Equals(args[index + 1], "profiler", StringComparison.OrdinalIgnoreCase))
                    return "profiler";
                if (string.Equals(value, "-ump-process-role", StringComparison.OrdinalIgnoreCase) &&
                    index + 1 < args.Length)
                {
                    var declaredRole = (args[index + 1] ?? "").Trim();
                    if (!string.IsNullOrEmpty(declaredRole) &&
                        !string.Equals(declaredRole, "editor", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(declaredRole, "main", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(declaredRole, "main-editor", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(declaredRole, "mainEditor", StringComparison.OrdinalIgnoreCase))
                        return "worker";
                }
            }

            for (var index = 0; index < args.Length; index++)
            {
                var value = (args[index] ?? "").Trim();
                if (string.Equals(value, "-adb2", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "-ump", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("-worker", StringComparison.OrdinalIgnoreCase))
                    return "worker";
            }
            return "mainEditor";
        }

        internal static long GetCurrentProcessCreatedAt()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return process.StartTime.ToUniversalTime().ToFileTimeUtc();
            }
            catch
            {
                return 0;
            }
        }

        private async Task HeartbeatLoopAsync(CancellationToken token, ClientWebSocket socket, long epoch, string sessionId)
        {
            int consecutiveFailures = 0;
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                if (!IsConnectionCycleCurrent(socket, epoch, token)) return;
                if (!IsStateSynchronized)
                {
                    if (_readyDeadlineTick > 0 && Stopwatch.GetTimestamp() >= _readyDeadlineTick)
                    {
                        var failure = new TimeoutException("Bridge initialization did not complete within its original budget.");
                        FailBootstrap(CaptureOrigin(token), "bootstrap_timeout", failure);
                        throw failure;
                    }
                    await Task.Delay(100, token).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    var heartbeatOrigin = CaptureOrigin(token);
                    if (heartbeatOrigin.Socket != socket || heartbeatOrigin.Epoch != epoch ||
                        heartbeatOrigin.SessionId != sessionId || !IsOriginCurrent(heartbeatOrigin)) return;
                    if (!TryBuildAndPersistExecutionState("heartbeat", out var heartbeatPayload))
                    {
                        consecutiveFailures++;
                        await _heartbeatIntervalChanged.WaitAsync(HeartbeatIntervalMs, token).ConfigureAwait(false);
                        continue;
                    }
                    var hb = new HeartbeatMessage
                    {
                        id = $"hb-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                        type = "heartbeat",
                        name = "session.heartbeat",
                        payload = heartbeatPayload,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        sessionId = sessionId,
                    };
                    if (!await SendOriginJsonAsync(JsonUtility.ToJson(hb), token, heartbeatOrigin, true)) return;
                    if (!TryUpdateConnectionCycle(socket, epoch, token,
                        () => _lastHeartbeatSentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) return;
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= 3)
                    {
                        throw new Exception($"Heartbeat failed {consecutiveFailures} times consecutively, forcing reconnect", ex);
                    }
                }
                await _heartbeatIntervalChanged.WaitAsync(HeartbeatIntervalMs, token).ConfigureAwait(false);
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

        private Task ReceiveLoopAsync(CancellationToken token) =>
            ReceiveConnectionLoopAsync(token, _ws, Interlocked.Read(ref _helloConnectionEpoch), LifecycleManager.CaptureServiceShutdown());

        private async Task ReceiveConnectionLoopAsync(CancellationToken token, ClientWebSocket receivingSocket,
            long receivingEpoch, UPilotMcpServerManager.ServiceShutdownSnapshot receivingShutdown)
        {
            var buffer = new byte[65536];
            var sb = new StringBuilder();

            while (!token.IsCancellationRequested && receivingSocket?.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    // ConfigureAwait(false)：确保 ReceiveAsync 回调在线程池上立即执行，
                    // 不等 Unity 主线程 SynchronizationContext 的下一个 frame
                    result = await receivingSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        var closeStatus = result.CloseStatus?.ToString() ?? "Unknown";
                        var closeDesc = result.CloseStatusDescription ?? "";
                        if (!TryUpdateConnectionCycle(receivingSocket, receivingEpoch, token, () =>
                        {
                            _lastCloseCode = result.CloseStatus.HasValue ? ((int)result.CloseStatus.Value).ToString() : "unknown";
                            _lastCloseReason = closeDesc.Length > 256 ? closeDesc.Substring(0, 256) : closeDesc;
                            _lastDisconnectedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        })) return;
                        UPilotOperationTracker.Instance.RecordSystemEvent(
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

                // Keep receiving while a command waits on Unity's main thread or
                // performs a long background operation. Blocking this loop also
                // blocks ClientWebSocket from answering server ping frames.
                var commandTask = HandleReceivedMessageAsync(json, envelope, token, receivingSocket, receivingEpoch, receivingShutdown);
                _ = commandTask.ContinueWith(
                    completed => { var ignored = completed.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }
        }

        private Task HandleMessageJsonAsync(string json, BridgeEnvelope envelope, CancellationToken token) =>
            HandleReceivedMessageAsync(json, envelope, token, _ws, Interlocked.Read(ref _helloConnectionEpoch), LifecycleManager.CaptureServiceShutdown());

        private async Task HandleReceivedMessageAsync(string json, BridgeEnvelope envelope, CancellationToken token,
            ClientWebSocket receivingSocket, long receivingEpoch, UPilotMcpServerManager.ServiceShutdownSnapshot receivingShutdown)
        {
            // envelope 已在 ReceiveLoopAsync 中解析并写日志，此处直接使用
            if (envelope == null) return;

            // Handle hello result → authenticate
            if (envelope.type == "result" && envelope.name == "session.hello")
            {
                try { ReceiveHelloResult(json, envelope, token, receivingSocket, receivingEpoch, receivingShutdown); }
                catch (Exception ex)
                {
                    Logger.ReportFailure("BRIDGE", $"hello_receive_failed sessionId={envelope.sessionId} connectionEpoch={receivingEpoch}", ex,
                        scope: "hello|" + envelope.sessionId);
                    try { receivingSocket?.Abort(); } catch { }
                }
                return;
            }

            if (!IsConnectionCycleCurrent(receivingSocket, receivingEpoch, token) || envelope.sessionId != _sessionId) return;
            if (TryHandleReadinessControl(json, envelope, token, receivingSocket, receivingEpoch)) return;

            // Absorb heartbeats from server
            if (envelope.type == "heartbeat" ||
                string.Equals(envelope.name, "session.heartbeat", StringComparison.OrdinalIgnoreCase)) return;

            // Only process commands with a bounded request identity.
            if (envelope.type != "command" || string.IsNullOrEmpty(envelope.id) || envelope.id.Length > 256) return;

            var origin = CaptureOrigin(token);
            origin.MaintenanceControl = IsMaintenanceControl(json, envelope);
            RequestOrigin.Value = origin;
            CommandOrigins[envelope.id] = origin;

            // Guard: require authentication
            if (!_isAuthenticated)
            {
                var resetting = _resetExecuting || _resettingWork;
                var waitingForHello = !resetting && IsInfrastructureReadWaitingForHello(
                    json, envelope, token, receivingSocket, receivingEpoch);
                resetting = _resetExecuting || _resettingWork;
                // Hello can finish on the main thread while the read is classified.
                if (!_isAuthenticated || resetting)
                {
                    waitingForHello &= !resetting;
                    var code = resetting ? "UPILOT_RESET_IN_PROGRESS" :
                        waitingForHello ? "UPILOT_RECONNECT_PENDING" : "INTERNAL_ERROR";
                    var message = resetting ? "UPilot hard stop is in progress." :
                        waitingForHello ? "UPilot 等待当前连接的 hello 在主线程完成应用。" : "会话未认证";
                    await SendErrorAsync(envelope.id, code, message, token, envelope.name,
                        waitingForHello || resetting ? new ErrorDetailPayload
                        {
                            stage = resetting ? "service_reset" : "hello_apply",
                            outcome = "not_executed", commandSubmitted = false, sideEffectsMayHaveOccurred = false,
                            nextAction = waitingForHello
                                ? "Observe the original operation identity; only this read-only query may retry within its original deadline."
                                : "Observe the original service maintenance identity; do not replay the command.",
                        } : null);
                    return;
                }
            }

            var id = envelope.id;
            if (!string.Equals(envelope.name, "upilot_flow.results", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log("COMMAND", $"Received {envelope.name} id={id}");
            }

            try
            {
                var admissionError = GetAdmissionErrorCode(envelope.name);
                if (admissionError != null)
                {
                    var message = admissionError == "UPILOT_RESET_IN_PROGRESS"
                        ? "UPilot hard stop is in progress."
                        : (_cachedIsCompiling ? "Unity 编译中，等待 Bridge 重连完成。" : "UPilot 等待 Bridge 重连完成。");
                    await SendErrorAsync(id, admissionError, message, token, envelope.name);
                    return;
                }
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
                if (CommandResultAlreadyReported(id))
                {
                    Logger.ReportFailure("COMMAND", $"Post-result exception cmd={envelope.name} id={id}", ex, id);
                    return;
                }

                try
                {
                    await SendErrorAsync(id, "INTERNAL_ERROR", $"命令处理异常：{ex.Message}", token, envelope.name, errorDetail: null, exception: ex);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception sendEx)
                {
                    Logger.ReportFailure("COMMAND", $"Failed to report command exception: {envelope.name} id={id}", sendEx, id);
                }
            }
        }

        private static bool CommandResultAlreadyReported(string commandId)
        {
            var entries = UPilotOperationTracker.Instance.GetEntriesCopy();
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
            new Automation.UPilotAutomationStepService(this).RegisterCommands();
            new UPilotAgentIntegrationService(this).RegisterCommands();
            UPilotServiceMaintenance.Register(this);
            UPilotQueueCleanupLog.Register(this);

            _consoleService = new UPilotConsoleService(this);
            _consoleService.RegisterCommands();

            _consoleCaptureService = new UPilotConsoleCaptureService(this);
            _consoleCaptureService.RegisterCommands();

            _gameObjectService = new UPilotGameObjectService(this);
            _gameObjectService.RegisterCommands();

            _sceneService = new UPilotSceneService(this);
            _sceneService.RegisterCommands();

            _componentService = new UPilotComponentService(this);
            _componentService.RegisterCommands();

            _snapshotService = new UPilotSnapshotService(this);
            _snapshotService.RegisterCommands();

            _assetService = new UPilotAssetService(this);
            _assetService.RegisterCommands();

            _prefabService = new UPilotPrefabService(this);
            _prefabService.RegisterCommands();

            _materialService = new UPilotMaterialService(this);
            _materialService.RegisterCommands();

            _menuService = new UPilotMenuService(this);
            _menuService.RegisterCommands();

            _packageService = new UPilotPackageService(this);
            _packageService.RegisterCommands();

            _testService = UPilotTestService.AttachBridge(this);
            _testService.RegisterCommands();

            _dragDropService = new UPilotDragDropService(this);
            _dragDropService.RegisterCommands();

            // _uiToolkitService = new UPilotUIToolkitService(this);
            // _uiToolkitService.RegisterCommands();

            _keyboardService = new UPilotKeyboardService(this);
            _keyboardService.RegisterCommands();

            // P2 services
            _scriptService = new UPilotScriptService(this);
            _scriptService.RegisterCommands();
            _executionService = new UPilotExecutionService(this);
            _executionService.RegisterCommands();
            _reflectionService = new UPilotReflectionService(this, _executionService);
            _reflectionService.RegisterCommands();
            _reflectionEvalService = new ReflectionEvalService(this);
            _reflectionEvalService.RegisterCommands();
            _batchService = new UPilotBatchService(this);
            _batchService.RegisterCommands();
            _selectionService = new UPilotSelectionService(this);
            _selectionService.RegisterCommands();
            _resourceService = new UPilotResourceService(this);
            _resourceService.RegisterCommands();
            _buildService = new UPilotBuildService(this);
            _buildService.RegisterCommands();

            _editorService = new UPilotEditorService(this);
            _editorService.RegisterCommands();

            _windowService = new UPilotWindowService(this);
            _windowService.RegisterCommands();

            _editorDelayService = new UPilotEditorDelayService(this);
            _editorDelayService.RegisterCommands();

            _operationService = new UPilotOperationService(this);
            _operationService.RegisterCommands();

            _runtimeDiagnosticsService = new UPilotRuntimeDiagnosticsService(this);
            _runtimeDiagnosticsService.RegisterCommands();

            RegisterOptionalUPilotFlowService();
        }

        private void RegisterOptionalUPilotFlowService()
        {
            if (!UPilotFeatureManager.IsFlowEnabled)
            {
                _flowService = null;
                Logger.Log("UPilot.Flow", $"UPilot Flow disabled: {UPilotFeatureManager.FlowUnavailableReason}");
                return;
            }

            const string typeName = "CodingRiver.UPilot.UPilotFlowService, UPilot.FlowBridge";

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
                    _flowService = service;
                    Logger.Log("UPilot.Flow", "UPilot Flow MCP bridge registered.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.ReportWarning("FLOW", $"UPilot Flow MCP bridge is unavailable: {ex}");
            }

            _flowService = null;
            Logger.Log("UPilot.Flow", "UPilot Flow is disabled; optional MCP commands were not registered.");
        }

        // ──────────────────────────────── Command handlers ────────────────────────────────

        private async Task HandleCompileRequestAsync(string id, string json, CancellationToken token)
        {
            var opCtx = UPilotOperationTracker.Instance.GetContext(id);
            var command = JsonUtility.FromJson<CompileRequestMessage>(json);
            var requestId = command?.payload?.requestId ?? string.Empty;
            var diagnosticScope = "compile|" + (string.IsNullOrEmpty(command?.payload?.writeBatchId)
                ? (string.IsNullOrEmpty(requestId) ? id : requestId) : command.payload.writeBatchId);
            Logger.Log("COMPILE", $"收到 compile.request: requestId={requestId} id={id}");

            // Fast-path: already compiling
            if (IsPlayingOrWillChangePlaymode())
            {
                _compileService.ClearStaleCompileBusy(
                    "compile.request received while Unity is in PlayMode.",
                    ignoreEditorCompiling: true);
                await SendErrorCoreAsync(id, "EDITOR_IN_PLAY_MODE", "Unity 正在 PlayMode 或切换 PlayMode，不能触发脚本编译", token, "compile.request", null, null, diagnosticScope);
                return;
            }

            if (CurrentIsCompiling())
            {
                await SendErrorCoreAsync(id, "EDITOR_BUSY", "编译进行中，请稍后重试", token, "compile.request", null, null, diagnosticScope);
                return;
            }
            opCtx?.Step("参数校验通过，准备触发编译");

            var startTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            string rejectedMode = null;
            string startFailureCode = null;
            EnqueueTracked(id, () =>
            {
                if (!_compileService.TryBeginCompile(
                    requestId,
                    command?.payload?.writeBatchId,
                    command?.payload?.writeBatchCreatedAt ?? 0,
                    () => PublishExecutionStateBounded("compile_queued")))
                {
                    startFailureCode = _compileService.LastStartFailureCode;
                    rejectedMode = UPilotPlayModeCompilePolicy.CurrentMode;
                    startTcs.TrySetResult(false);
                    return;
                }
                startTcs.TrySetResult(true);
            });

            var compileStarted = await startTcs.Task;
            if (!compileStarted)
            {
                var persistenceFailed = string.Equals(
                    startFailureCode,
                    "StatePersistenceFailed",
                    StringComparison.Ordinal);
                var playModeRejected = startFailureCode == "EditorInPlayMode";
                await SendErrorCoreAsync(
                    id,
                    persistenceFailed ? "STATE_PERSISTENCE_FAILED" : playModeRejected ? "EDITOR_IN_PLAY_MODE" : "EDITOR_BUSY",
                    persistenceFailed ? "编译前状态持久化失败，未触发编译" : playModeRejected ? $"Unity 正在播放、暂停或切换模式，未触发编译；observedMode={rejectedMode}" : "编译进行中，请稍后重试",
                    token,
                    "compile.request", null, null, diagnosticScope);
                return;
            }
            Logger.CloseDiagnostics(diagnosticScope);
            Logger.Log("COMPILE", $"编译已触发，等待完成: requestId={requestId} timeout=120s");

            // compile.status / compile.started / compile.finished / compile.errors are pushed from
            // OnCompilationStarted / SendCompileFinishedMcpPushAsync for every compilation cycle.

            opCtx?.Step("等待编译完成", "超时120s");
            var compileTask = _compileService.WaitForCompileAsync();
            var waitDeadline = OperationContext.CreateWaitDeadline(120000);
            opCtx?.SetWaitingUntil(waitDeadline);
            var timeoutTask = Task.Delay(OperationContext.RemainingWaitMilliseconds(waitDeadline), token);
            var winner = await Task.WhenAny(compileTask, timeoutTask);
            opCtx?.SetPhase("executing");
            token.ThrowIfCancellationRequested();
            if (winner == timeoutTask)
            {

                await ObserveCompileOnMainThreadAsync(() => {
                    _compileService.ClearStaleCompileBusy("compile.request timed out before Unity reported compilation finished.");
                    return true;
                }, token).ConfigureAwait(false);
                await SendErrorAsync(id, "COMMAND_TIMEOUT", "编译超时", token, "compile.request");
                return;
            }

            opCtx?.Step("编译完成，发送结果");
            Logger.Log("COMPILE", $"编译完成，发送结果: requestId={requestId} errors={_compileService.LastErrorCount}");
            var accepted = new CompileAcceptedPayload
            {
                accepted = true,
                compileRequestId = requestId,
                compileOperationId = _compileService.CompileOperationId,
                writeBatchId = _compileService.WriteBatchId,
            };
            await SendResultAsync(id, "compile.request", accepted, token);
        }

        private async Task HandleCompileErrorsGetAsync(string id, string json, CancellationToken token)
        {
            var includeWarnings = JsonUtility.FromJson<CompileErrorsGetMessage>(json)?.payload?.includeWarnings ?? false;
            var transitioned = await ObserveCompileOnMainThreadAsync(_compileService.CompleteVerification, token).ConfigureAwait(false);
            var payload = _compileService.BuildLastCompileErrorsPayload(includeWarnings);
            if (transitioned)
            {
                await PublishExecutionStateAsync(
                    _compileService.HasCompileErrors ? "compile_failed" : "compile_completed",
                    token).ConfigureAwait(false);
            }
            Logger.Log("COMPILE", $"compile.errors.get: errors={payload.total} requestId={payload.requestId}");
            await SendResultAsync(id, "compile.errors.get", payload, token);
        }

        /// <summary>
        /// Wait until EditorApplication reports no script compilation (any source). Polls on main thread via EditorApplication.update.
        /// </summary>
        private async Task HandleCompileWaitAsync(string id, string json, CancellationToken token)
        {
            var opCtx = UPilotOperationTracker.Instance.GetContext(id);
            var command = JsonUtility.FromJson<CompileWaitMessage>(json);
            var timeoutMs = command?.payload?.timeoutMs ?? 300000;
            if (timeoutMs < 1000) timeoutMs = 1000;
            if (timeoutMs > 600000) timeoutMs = 600000;
            opCtx?.Step("等待编译空闲", $"timeout={timeoutMs}ms");
            Logger.Log("COMPILE", $"compile.wait 开始: timeout={timeoutMs}ms isCompiling={CurrentIsCompiling()} id={id}");

            if (IsPlayingOrWillChangePlaymode())
            {
                await SendResultAsync(id, "compile.wait", new GenericOkPayload
                {
                    ok = false,
                    state = "compile_blocked",
                    status = "blocked",
                    blocked = true,
                    blockedReason = "PlayMode",
                    nextAction = "Exit PlayMode after user confirmation, then retry the compile wait.",
                    playModeState = CachedPlayModeChangedPayload().state,
                    compilePhase = _compileService.Phase,
                }, token);
                return;
            }

            var verificationCompleted = false;

            bool CompileWaitPending()
            {
                if (CurrentIsCompiling())
                    return true;

                if (string.Equals(_compileService.Phase, "verifying", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_compileService.Phase, "compiler_finished", StringComparison.OrdinalIgnoreCase))
                {
                    verificationCompleted = _compileService.CompleteVerification();
                    return !verificationCompleted && _compileService.VerificationPending;
                }

                return string.Equals(_compileService.Phase, "queued", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(_compileService.Phase, "compiler_finished", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(_compileService.Phase, "domain_reload", StringComparison.OrdinalIgnoreCase);
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var waitDeadline = OperationContext.CreateWaitDeadline(timeoutMs);
            var delayTask = Task.Delay(OperationContext.RemainingWaitMilliseconds(waitDeadline), token);
            var stop = false;

            void PollIdle()
            {
                if (stop) return;
                if (!CompileWaitPending())
                {
                    EditorApplication.update -= PollIdle;
                    tcs.TrySetResult(true);
                }
            }

            EnqueueTracked(id, () =>
            {
                if (!CompileWaitPending())
                {
                    tcs.TrySetResult(true);
                    return;
                }

                opCtx?.SetWaitingUntil(waitDeadline);
                EditorApplication.update += PollIdle;
                PollIdle();
            });

            var winner = await Task.WhenAny(tcs.Task, delayTask);
            opCtx?.SetPhase("executing");
            stop = true;
            _controlQueue.Enqueue(() => { EditorApplication.update -= PollIdle; });
            token.ThrowIfCancellationRequested();

            if (winner == delayTask)
            {

                await SendErrorAsync(id, "COMMAND_TIMEOUT", $"等待编译结束超时（{timeoutMs}ms）", token, "compile.wait");
                return;
            }

            if (verificationCompleted)
            {
                await PublishExecutionStateAsync(
                    _compileService.HasCompileErrors ? "compile_failed" : "compile_completed",
                    token).ConfigureAwait(false);
            }

            Logger.Log("COMPILE", $"compile.wait 完成，编译空闲 id={id}");
            if (string.Equals(_compileService.Phase, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                await SendResultAsync(id, "compile.wait", new GenericOkPayload {
                    ok = false, state = "compile_unknown", status = "unknown", blocked = true,
                    blockedReason = "CompileEvidenceUnknown", compilePhase = "unknown",
                    nextAction = "No verified compile result is available. Observe a newly authorized compile; do not infer success.",
                    playModeState = CachedPlayModeChangedPayload().state,
                }, token);
                return;
            }
            if (string.Equals(_compileService.Phase, "failed", StringComparison.OrdinalIgnoreCase))
            {
                await SendResultAsync(id, "compile.wait", new GenericOkPayload
                {
                    ok = false,
                    state = "compile_failed",
                    status = "failed",
                    blocked = true,
                    blockedReason = "CompileErrors",
                    nextAction = "Read compile.errors.get and fix the reported compiler errors.",
                    playModeState = CachedPlayModeChangedPayload().state,
                    compilePhase = _compileService.Phase,
                }, token);
                return;
            }
            await SendResultAsync(id, "compile.wait",
                new GenericOkPayload
                {
                    ok = true,
                    state = "compile_idle",
                    status = "ready",
                    playModeState = CachedPlayModeChangedPayload().state,
                    compilePhase = _compileService.Phase,
                }, token);
        }

        private async Task HandlePlayModeSetAsync(string id, string json, CancellationToken token)
        {
            var command = JsonUtility.FromJson<PlayModeSetMessage>(json);
            var action = command?.payload?.action ?? "stop";

            if (action != "play" && action != "stop" && action != "pause" && action != "resume")
            {
                await SendErrorAsync(id, "INVALID_PAYLOAD", $"非法 PlayMode 动作：{action}", token, "playmode.set",
                    new ErrorDetailPayload
                    {
                        commandId = id,
                        commandName = "playmode.set",
                        commandSubmitted = false,
                        stateObserved = false,
                        changed = false,
                        writeCount = 0,
                        stage = "preflight",
                        blockedReason = "INVALID_PAYLOAD",
                        nextAction = "Use play, stop, pause, or resume.",
                        sideEffectsMayHaveOccurred = false,
                    });
                return;
            }

            if (token.IsCancellationRequested)
            {
                await SendErrorAsync(id, "EXECUTION_CANCELLED", "PlayMode request was cancelled before it reached the main thread.",
                    CancellationToken.None, "playmode.set", new ErrorDetailPayload
                    {
                        commandId = id,
                        commandName = "playmode.set",
                        commandSubmitted = false,
                        stateObserved = false,
                        changed = false,
                        writeCount = 0,
                        stage = "cancelled",
                        nextAction = "Inspect the current PlayMode state; do not submit the cancelled request again.",
                        sideEffectsMayHaveOccurred = false,
                    });
                return;
            }

            var resultTcs = new TaskCompletionSource<GenericOkPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            EnqueueTracked(id, () =>
            {
                if (token.IsCancellationRequested)
                {
                    resultTcs.TrySetCanceled(token);
                    return;
                }
                if (action == "play" && !EditorApplication.isPlaying)
                    UPilotPlayModeTransitions.RegisterIntent("upilot", action == "play" ? "play" : "edit",
                        command?.payload?.requestId, id, command?.payload?.operationId, command?.payload?.toolName);
                else if (action == "stop" && EditorApplication.isPlaying)
                    UPilotPlayModeTransitions.RegisterIntent("upilot", "edit",
                        command?.payload?.requestId, id, command?.payload?.operationId, command?.payload?.toolName);
                var payload = _playInputService.SetPlayMode(action);
                resultTcs.TrySetResult(payload);
            });

            GenericOkPayload result;
            try { result = await resultTcs.Task; }
            catch (OperationCanceledException)
            {
                await SendErrorAsync(id, "EXECUTION_CANCELLED", "PlayMode request was cancelled before the state setter ran.",
                    CancellationToken.None, "playmode.set", new ErrorDetailPayload
                    {
                        commandId = id,
                        commandName = "playmode.set",
                        commandSubmitted = false,
                        stateObserved = false,
                        changed = false,
                        writeCount = 0,
                        stage = "cancelled",
                        nextAction = "Inspect the current PlayMode state; do not submit the cancelled request again.",
                        sideEffectsMayHaveOccurred = false,
                    });
                return;
            }
            if (!result.ok)
            {
                await SendErrorAsync(id, result.blockedReason ?? "PLAYMODE_SET_REJECTED",
                    result.nextAction ?? "PlayMode request was rejected.", token, "playmode.set",
                    new ErrorDetailPayload
                    {
                        commandId = id,
                        commandName = "playmode.set",
                        commandSubmitted = false,
                        stateObserved = false,
                        changed = false,
                        writeCount = 0,
                        stage = "preflight",
                        blockedReason = result.blockedReason ?? "PLAYMODE_SET_REJECTED",
                        nextAction = result.nextAction ?? "PlayMode request was rejected.",
                        sideEffectsMayHaveOccurred = false,
                    });
                return;
            }
            result.commandId = id;
            result.stateObserved = true;
            result.commandSubmitted = true;
            var context = BuildEditorContextPayload("playmode.set");
            result.authoritative = context.authoritative;
            result.isStale = context.isStale;
            result.confirmed = result.authoritative && !result.isStale;
            var responseToken = token.IsCancellationRequested ? CancellationToken.None : token;
            await SendResultAsync(id, "playmode.set", result, responseToken);
            await SendPlayModeChangedEventAsync(new PlayModeChangedPayload { state = result.state }, responseToken);
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

            var mouseResult = await UPilotModalObserver.RunAsync(this, id,
                () => _playInputService.HandleMouseEvent(mousePayload),
                escapeGenericMenu: mousePayload.escapeGenericMenu,
                target: () => UPilotWindowInputRegistry.Resolve(mousePayload.windowInstanceId, mousePayload.targetWindow));
            // The observer returns object (receipt or pending modal). JsonUtility would
            // serialize ResultMessage<object>.payload as {}, so retain the concrete wire type.
            if (mouseResult is GenericOkPayload receipt)
                await SendResultAsync(id, "mouse.event", receipt, token);
            else if (mouseResult is ModalObservationPayload modal)
                await SendResultAsync(id, "mouse.event", modal, token);
            else
                throw new InvalidOperationException("Unexpected mouse observation result type.");
        }

        private async Task HandleEditorStateAsync(string id, string json, CancellationToken token)
        {
            await PublishExecutionStateAsync("editor_state_requested", token).ConfigureAwait(false);
            var payload = BuildEditorStatePayload("editor.state");
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

            UPilotOperationTracker.Instance.IngestAgentError(
                p.source, p.errorType, p.message, p.relatedCommandId, p.context);
            Logger.LogWarning("AGENT", $"[{p.errorType}] {p.message}");

            await SendResultAsync(id, "agent.reportError", new GenericOkPayload { ok = true }, token);
        }

        // ──────────────────────────────── Force restart ──────────────────────────────

        private async Task HandleForceRestartAsync(string id, string json, CancellationToken token)
        {
            Logger.LogWarning("SYSTEM", "收到 editor.forceRestart 命令，即将强制重启编辑器");
            await SendResultAsync(id, "editor.forceRestart", new GenericOkPayload { ok = true }, token);

            EnqueueUntracked(() =>
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
            UPilotOperationTracker.Instance.RecordSystemEvent(
                "sys.force.restart", "强制重启Unity",
                $"project={Application.dataPath} pid={System.Diagnostics.Process.GetCurrentProcess().Id}",
                "critical");

            try
            {
                var projectPath = Path.GetDirectoryName(Application.dataPath);
                var unityExePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(unityExePath))
                {
                    Debug.LogError("[UPilot] 无法获取 Unity 编辑器路径，放弃重启");
                    return;
                }

                var pid = System.Diagnostics.Process.GetCurrentProcess().Id;

#if UNITY_EDITOR_WIN
                var restartScript = Path.Combine(Path.GetTempPath(), $"upilot_restart_{pid}.bat");
                var scriptContent =
                    $"@echo off\r\n" +
                    $"echo [UPilot] Waiting for Unity (PID {pid}) to exit...\r\n" +
                    $"taskkill /F /PID {pid} >nul 2>&1\r\n" +
                    $"timeout /t 3 /nobreak >nul\r\n" +
                    $"echo [UPilot] Restarting Unity project: {projectPath}\r\n" +
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
                    $"echo '[UPilot] Waiting for Unity (PID {pid}) to exit...'\n" +
                    $"kill -9 {pid} 2>/dev/null\n" +
                    $"sleep 3\n" +
                    $"echo '[UPilot] Restarting Unity project: {projectPath}'\n" +
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
                Debug.LogWarning($"[UPilot] 重启脚本已启动，Unity 即将关闭");
            }
            catch (Exception ex)
            {
                Logger.ReportFailure("SYSTEM", "强制重启失败", ex);
            }
        }

        // ──────────────────────────────── Send helpers ────────────────────────────────

        private const int MaxBridgeResponseBytes = 4 * 1024 * 1024;

        private void RecordOversize(string source, int actualBytes)
        {
            _lastOversizeAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _lastOversizeSource = source?.Length > 128 ? source.Substring(0, 128) : source ?? "";
            _lastOversizeActualBytes = actualBytes;
            System.Threading.Interlocked.Increment(ref _oversizeCount);
        }
        private static int ResponseBytes(string value) => Encoding.UTF8.GetByteCount(value);

        private async Task SendBoundedErrorAsync(string id, string commandName, string code,
            string message, long actualBytes, CancellationToken token, bool resultWasProduced = false)
        {
            var error = new ErrorMessage
            {
                id = id,
                name = "command.error",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sessionId = OriginForSend(id, token, true)?.SessionId ?? string.Empty,
                payload = new ErrorPayload
                {
                    code = code,
                    message = message.Length > 512 ? message.Substring(0, 512) : message,
                    detail = new ErrorDetailPayload
                    {
                        commandId = id,
                        commandName = commandName,
                        actualBytes = actualBytes,
                        limitBytes = MaxBridgeResponseBytes,
                        outcome = "unknown",
                        // A produced result cannot prove absence of business side effects.
                        commandSubmitted = resultWasProduced,
                        sideEffectsMayHaveOccurred = resultWasProduced,
                        stage = resultWasProduced ? "response_serialization" : "",
                        replayAttempted = false,
                        responseTruncated = true,
                        nextAction = "Observe the original command or operation identity; do not replay its start.",
                    },
                },
            };
            var json = JsonUtility.ToJson(error);
            if (ResponseBytes(json) > 8192)
                throw new InvalidDataException("Bridge error identity exceeds diagnostic frame budget.");
            if (await SendOriginJsonAsync(json, token, OriginForSend(id, token, true), false))
                UPilotOperationTracker.Instance.GetContext(id)?.MarkReported(true);
            CommandOrigins.TryRemove(id, out _);
        }

        public async Task SendResultAsync<TPayload>(string id, string name, TPayload payload, CancellationToken token)
        {
            var origin = OriginForSend(id, token, true);
            var timing = UPilotOperationTracker.Instance.GetTimingSnapshot(id);
            var result = new ResultMessage<TPayload>
            {
                id = id,
                name = name,
                payload = payload,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sessionId = origin?.SessionId ?? string.Empty,
                timing = timing,
                context = BuildEditorContextPayload("bridge-response"),
            };
            var serializeWatch = Stopwatch.StartNew();
            var json = JsonUtility.ToJson(result);
            serializeWatch.Stop();
            timing.serializationMs = serializeWatch.ElapsedMilliseconds;
            if (timing.serializationMs > 0)
                json = JsonUtility.ToJson(result);
            var size = ResponseBytes(json);
            if (size > MaxBridgeResponseBytes)
            {
                RecordOversize("tool result", size);
                UPilotOperationTracker.Instance.GetContext(id)?.Fail("BRIDGE_RESPONSE_TOO_LARGE", "Result exceeds Bridge message limit.");
                await SendBoundedErrorAsync(id, name, "BRIDGE_RESPONSE_TOO_LARGE",
                    "Result exceeds Bridge message limit; outcome unknown.", size, token, resultWasProduced: true);
                return;
            }
            if (await SendOriginJsonAsync(json, token, origin, origin?.MaintenanceControl != true))
                UPilotOperationTracker.Instance.GetContext(id)?.MarkReported(false);
            CommandOrigins.TryRemove(id, out _);
        }

        public async Task SendEventAsync<TPayload>(string id, string name, TPayload payload, CancellationToken token)
        {
            var origin = OriginForSend(id, token, false);
            var evt = new EventMessage<TPayload>
            {
                id = id,
                name = name,
                payload = payload,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sessionId = origin?.SessionId ?? string.Empty,
            };
            var json = JsonUtility.ToJson(evt);
            var size = ResponseBytes(json);
            if (size > MaxBridgeResponseBytes)
            {
                RecordOversize(name, size);
                var diagnostic = new EventMessage<BridgeOversizeEventPayload>
                {
                    id = id, name = "bridge.payload_oversize", sessionId = origin?.SessionId ?? string.Empty,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    payload = new BridgeOversizeEventPayload { sourceEvent = name, actualBytes = size,
                        limitBytes = MaxBridgeResponseBytes },
                };
                json = JsonUtility.ToJson(diagnostic);
            }
            await SendOriginJsonAsync(json, token, origin, true).ConfigureAwait(false);
        }

        public async Task SendErrorAsync(string id, string code, string message, CancellationToken token,
            string commandName = "")
        {
            await SendErrorAsync(id, code, message, token, commandName, null);
        }

        public Task SendErrorAsync(string id, string code, string message, CancellationToken token,
            string commandName, ErrorDetailPayload errorDetail) =>
            SendErrorAsync(id, code, message, token, commandName, errorDetail, null);

        public Task SendErrorAsync(string id, string code, string message, CancellationToken token,
            string commandName, ErrorDetailPayload errorDetail, Exception exception)
            => SendErrorCoreAsync(id, code, message, token, commandName, errorDetail, exception, null);

        private async Task SendErrorCoreAsync(string id, string code, string message, CancellationToken token,
            string commandName, ErrorDetailPayload errorDetail, Exception exception, string diagnosticScope)
        {
            ReportCommandError(id, commandName, code, message, exception, diagnosticScope);
            var origin = OriginForSend(id, token, true);
            if (errorDetail != null)
            {
                errorDetail.commandId = id;
                errorDetail.commandName = commandName;
            }
            var timing = UPilotOperationTracker.Instance.GetTimingSnapshot(id);
            var err = new ErrorMessage
            {
                id = id,
                name = "command.error",
                payload = new ErrorPayload
                {
                    code = code,
                    message = message,
                    detail = errorDetail ?? new ErrorDetailPayload { commandId = id, commandName = commandName },
                },
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sessionId = origin?.SessionId ?? string.Empty,
                timing = timing,
                context = IsStateSynchronized ? BuildEditorContextPayload("bridge-error") : null,
            };
            var serializeWatch = Stopwatch.StartNew();
            var json = JsonUtility.ToJson(err);
            serializeWatch.Stop();
            timing.serializationMs = serializeWatch.ElapsedMilliseconds;
            if (timing.serializationMs > 0)
                json = JsonUtility.ToJson(err);
            var size = ResponseBytes(json);
            if (size > MaxBridgeResponseBytes)
            {
                UPilotOperationTracker.Instance.GetContext(id)?.Fail(code, message.Length > 512 ? message.Substring(0, 512) : message);
                await SendBoundedErrorAsync(id, commandName, code, message, size, token);
                return;
            }
            if (await SendOriginJsonAsync(json, token, OriginForSend(id, token, true), false))
                UPilotOperationTracker.Instance.GetContext(id)?.MarkReported(true);
            CommandOrigins.TryRemove(id, out _);
        }

        internal static void ReportCommandError(string id, string commandName, string code, string message,
            Exception exception = null, string diagnosticScope = null)
        {
            // Cancellation is a normal terminal result. Cleanup faults have their own error code.
            if (code == "EXECUTION_CANCELLED" || code == "OPERATION_CANCELLED") return;
            string context = $"[{code}] {message} cmd={commandName} id={id}";
            bool expected = code == "INVALID_PARAMS" || code == "INVALID_PAYLOAD" || code == "EDITOR_BUSY" ||
                code == "EDITOR_IN_PLAY_MODE" || code == "COMMAND_NOT_FOUND" || code == "TEST_SELECTORS_INVALID" || code == "UPILOT_RECONNECT_PENDING";
            if (expected)
                Logger.ReportWarning("COMMAND", context + (exception == null ? "" : "\n" + exception),
                    scope: diagnosticScope ?? id,
                    fingerprint: commandName + "|" + code + "|" + message);
            else
                Logger.ReportFailure("COMMAND", context, exception, id, terminal: true);
        }

        internal EditorContextPayload GetEditorExecutionContext(string source) =>
            BuildEditorContextPayload(source);

        private EditorContextPayload BuildEditorContextPayload(string source) =>
            BuildEditorContextFromCompile(source, _compileService.BuildLastCompileErrorsPayload());

        private EditorContextPayload BuildEditorContextFromCompile(string source, CompileErrorsPayload compile)
        {
            var updatedAt = _lastMainThreadPumpAt;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var connected = _started && _isAuthenticated && _ws != null && _ws.State == WebSocketState.Open;
            var authoritative = updatedAt > 0 && now - updatedAt <= MainThreadContextFreshnessMs;
            var playModeState = _cachedPlayModeState ?? "unknown";
            var editorIsCompiling = _cachedIsCompiling;
            var compilePhase = compile.phase ?? "idle";
            var compilePhaseActive =
                string.Equals(compilePhase, "queued", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(compilePhase, "compiling", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(compilePhase, "compiler_finished", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(compilePhase, "domain_reload", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(compilePhase, "verifying", StringComparison.OrdinalIgnoreCase);
            var isCompiling = editorIsCompiling || compilePhaseActive;
            var blockedReason = string.Empty;
            var nextAction = string.Empty;
            if (!connected || !authoritative)
            {
                blockedReason = connected ? "EditorContextStale" : "UnityDisconnected";
                nextAction = "Wait for a live authoritative Editor response before mutating the Editor.";
            }
            else if (string.Equals(playModeState, "play", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(playModeState, "pause", StringComparison.OrdinalIgnoreCase))
            {
                blockedReason = "PlayMode";
                nextAction = "Exit PlayMode after user confirmation, then retry the operation.";
            }
            else if (!string.Equals(playModeState, "edit", StringComparison.OrdinalIgnoreCase))
            {
                blockedReason = "EditorModeUnknown";
                nextAction = "Wait for an authoritative Editor state that confirms EditMode.";
            }
            else if (isCompiling || compilePhaseActive)
            {
                blockedReason = "CompilationInProgress";
                nextAction = "Continue with unity_compile_wait until compilation reaches a terminal state.";
            }
            else if (string.Equals(compilePhase, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                blockedReason = "CompileEvidenceUnknown";
                nextAction = "Compiler evidence is unavailable for this Editor process. Inspect diagnostics; a new explicitly authorized compilation can establish fresh evidence.";
            }
            else if (string.Equals(compilePhase, "failed", StringComparison.OrdinalIgnoreCase) ||
                     (compile.terminal && (compile.total > 0)))
            {
                blockedReason = "CompileErrors";
                nextAction = "Read unity_compile_errors and fix the reported compiler errors.";
            }
            return new EditorContextPayload
            {
                playModeTransition = UPilotPlayModeTransitions.Current,
                heartbeatIntervalMs = HeartbeatIntervalMs,
                stateContractVersion = 2,
                projectId = ProjectPathHashSuffix,
                producerEpoch = _producerEpoch,
                domainGeneration = _domainGeneration,
                sequence = _stateSequence,
                snapshotId = BuildSnapshotId(),
                transition = _lastStateTransition,
                observedAt = now,
                connected = connected,
                authoritative = authoritative,
                isStale = !authoritative,
                ready = connected && authoritative && string.IsNullOrEmpty(blockedReason),
                blocked = !string.IsNullOrEmpty(blockedReason),
                blockedReason = blockedReason,
                nextAction = nextAction,
                source = source ?? "bridge",
                sessionId = _sessionId ?? string.Empty,
                updatedAt = updatedAt,
                contextUpdatedAt = updatedAt,
                playModeState = playModeState,
                isPlaying = _cachedIsPlaying,
                isPaused = _cachedIsPaused,
                isCompiling = isCompiling,
                isUpdating = _cachedIsUpdating,
                maintenanceInitialization = _maintenanceInitialization,
                compileStatus = compile.status,
                compileEvidence = compile.compileEvidence,
                compilePhase = compilePhase,
                compileRequestId = compile.requestId,
                compileOperationId = compile.compileOperationId,
                originatingCommandRequestId = compile.requestId,
                writeBatchId = compile.writeBatchId,
                writeBatchCreatedAt = compile.writeBatchCreatedAt,
                compileOrigin = compile.compileOrigin,
                terminal = compile.terminal,
                verificationPending = compile.verificationPending,
                errorsVerified = compile.errorsVerified,
                lastCompileRequestedAt = compile.lastCompileRequestedAt,
                lastCompileStartedAt = compile.lastCompileStartedAt,
                lastCompilerFinishedAt = compile.lastCompilerFinishedAt,
                lastCompileVerifiedAt = compile.lastCompileVerifiedAt,
                lastTerminalCompileAt = compile.lastTerminalCompileAt,
                reloadId = compile.reloadId,
                domainReloadObserved = compile.domainReloadObserved,
                compileStartedAt = compile.startedAt,
                compileFinishedAt = compile.finishedAt,
                lastProgressAt = compile.lastProgressAt,
                activeScene = _activeSceneName ?? string.Empty,
                lastMainThreadPumpAt = updatedAt,
                mainThreadQueueDepth = _mainThreadQueue.Count,
                lastDequeuedCommandId = _lastDequeuedCommandId ?? string.Empty,
                processId = Process.GetCurrentProcess().Id,
                processCreatedAt = GetCurrentProcessCreatedAt(),
            };
        }

        private static string ExecutionStatePath => Path.Combine(
            Directory.GetParent(Application.dataPath)?.FullName ?? ".",
            "Logs", "UPilot", "EditorExecutionState.json");

        private string BuildSnapshotId() =>
            $"{_producerEpoch}:{_domainGeneration}:{_stateSequence}";

        private void RestoreExecutionStateIdentity()
        {
            var processId = Process.GetCurrentProcess().Id;
            try
            {
                if (File.Exists(ExecutionStatePath))
                {
                    var restored = JsonUtility.FromJson<EditorExecutionStatePayload>(
                        File.ReadAllText(ExecutionStatePath));
                    if (restored != null && restored.processId == processId &&
                        restored.processCreatedAt == GetCurrentProcessCreatedAt() &&
                        !string.IsNullOrEmpty(restored.producerEpoch))
                    {
                        _producerEpoch = restored.producerEpoch;
                        _domainGeneration = Math.Max(0, restored.domainGeneration);
                        _stateSequence = Math.Max(0, restored.sequence);
                        _lastStateTransition = string.IsNullOrEmpty(restored.transition)
                            ? "restored"
                            : restored.transition;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.ReportFailure("SYSTEM", "恢复 EditorExecutionState identity 失败", ex, fingerprint: "execution-state-restore");
            }

            if (string.IsNullOrEmpty(_producerEpoch))
            {
                _producerEpoch = Guid.NewGuid().ToString("N");
                _domainGeneration = 0;
                _stateSequence = 0;
                _lastStateTransition = "process_started";
            }
        }

        private EditorExecutionStatePayload BuildExecutionStatePayload(string transition)
        {
            _lastStateTransition = transition ?? "snapshot";
            _stateSequence++;
            var compile = _compileService.BuildLastCompileErrorsPayload();
            var context = BuildEditorContextFromCompile("editor.execution_state", compile);
            return new EditorExecutionStatePayload
            {
                stateContractVersion = context.stateContractVersion,
                heartbeatIntervalMs = context.heartbeatIntervalMs,
                projectId = context.projectId,
                producerEpoch = context.producerEpoch,
                domainGeneration = context.domainGeneration,
                sequence = context.sequence,
                snapshotId = context.snapshotId,
                transition = context.transition,
                observedAt = context.observedAt,
                connected = context.connected,
                authoritative = context.authoritative,
                isStale = context.isStale,
                ready = context.ready,
                blocked = context.blocked,
                blockedReason = context.blockedReason,
                nextAction = context.nextAction,
                source = context.source,
                sessionId = context.sessionId,
                updatedAt = context.updatedAt,
                contextUpdatedAt = context.contextUpdatedAt,
                playModeState = context.playModeState,
                playModeTransition = context.playModeTransition,
                isPlaying = context.isPlaying,
                isPaused = context.isPaused,
                isCompiling = context.isCompiling,
                isUpdating = context.isUpdating,
                maintenanceInitialization = context.maintenanceInitialization,
                compileStatus = context.compileStatus,
                compileEvidence = context.compileEvidence,
                compilePhase = context.compilePhase,
                compileRequestId = context.compileRequestId,
                compileOperationId = context.compileOperationId,
                originatingCommandRequestId = context.originatingCommandRequestId,
                writeBatchId = context.writeBatchId,
                writeBatchCreatedAt = context.writeBatchCreatedAt,
                compileOrigin = context.compileOrigin,
                terminal = context.terminal,
                verificationPending = context.verificationPending,
                errorsVerified = context.errorsVerified,
                lastCompileRequestedAt = context.lastCompileRequestedAt,
                lastCompileStartedAt = context.lastCompileStartedAt,
                lastCompilerFinishedAt = context.lastCompilerFinishedAt,
                lastCompileVerifiedAt = context.lastCompileVerifiedAt,
                lastTerminalCompileAt = context.lastTerminalCompileAt,
                reloadId = context.reloadId,
                domainReloadObserved = context.domainReloadObserved,
                pendingWriteBatchId = context.pendingWriteBatchId,
                compileDeferredReason = context.compileDeferredReason,
                compileStartedAt = context.compileStartedAt,
                compileFinishedAt = context.compileFinishedAt,
                lastProgressAt = context.lastProgressAt,
                activeScene = context.activeScene,
                lastMainThreadPumpAt = context.lastMainThreadPumpAt,
                mainThreadQueueDepth = context.mainThreadQueueDepth,
                lastDequeuedCommandId = context.lastDequeuedCommandId,
                processId = context.processId,
                processCreatedAt = context.processCreatedAt,
                errorCount = compile.total,
                warningCount = compile.warningCount,
                preReloadPublishFailed = _preReloadPublishFailed,
            };
        }

        /// <summary>
        /// Creates and persists one snapshot as a single ordered operation.  Snapshot
        /// identity and file replacement must share a lock: otherwise two async
        /// publishers can write the shared state file in sequence order A/B but leave
        /// its contents at A.
        /// </summary>
        private bool TryBuildAndPersistExecutionState(
            string transition,
            out EditorExecutionStatePayload payload)
        {
            lock (_executionStatePublicationLock)
            {
                payload = BuildExecutionStatePayload(transition);
                return PersistExecutionState(payload);
            }
        }

        private static bool PersistExecutionState(EditorExecutionStatePayload payload) =>
            PersistExecutionState(payload, ExecutionStatePath);

        private static bool PersistExecutionState(EditorExecutionStatePayload payload, string executionStatePath)
        {
            lock (ExecutionStatePersistenceLock)
            {
                var temporary = string.Empty;
                try
                {
                    var directory = Path.GetDirectoryName(executionStatePath);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                    temporary = $"{executionStatePath}.{payload.processId}.{payload.sequence}.{Guid.NewGuid():N}.tmp";
                    File.WriteAllText(temporary, JsonUtility.ToJson(payload, true));
                    for (var attempt = 1; attempt <= ExecutionStatePersistenceAttempts; attempt++)
                    {
                        try
                        {
                            if (File.Exists(executionStatePath))
                                File.Replace(temporary, executionStatePath, null);
                            else
                                File.Move(temporary, executionStatePath);
                            return true;
                        }
                        catch (IOException) when (attempt < ExecutionStatePersistenceAttempts)
                        {
                            Thread.Sleep(ExecutionStatePersistenceRetryDelayMs * attempt);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.ReportFailure("SYSTEM",
                        $"持久化 EditorExecutionState 失败: path={executionStatePath}; " +
                        $"sequence={payload.sequence}; transition={payload.transition}; " +
                        $"attempts={ExecutionStatePersistenceAttempts}", ex, fingerprint: "execution-state-persist|" + executionStatePath);
                    return false;
                }
                finally
                {
                    if (!string.IsNullOrEmpty(temporary))
                    {
                        try
                        {
                            if (File.Exists(temporary)) File.Delete(temporary);
                        }
                        catch { }
                    }
                }

                Logger.ReportFailure("SYSTEM",
                    $"持久化 EditorExecutionState 失败: path={executionStatePath}; " +
                    $"sequence={payload.sequence}; transition={payload.transition}; " +
                    $"attempts={ExecutionStatePersistenceAttempts}", fingerprint: "execution-state-persist|" + executionStatePath);
                return false;
            }
        }

        internal async Task<bool> PublishExecutionStateAsync(string transition, CancellationToken token)
        {
            if (RequestOrigin.Value != null && !IsOriginCurrent(RequestOrigin.Value)) return false;
            RequestOrigin.Value ??= CaptureOrigin(token);
            if (!TryBuildAndPersistExecutionState(transition, out var payload)) return false;
            if (!IsStateSynchronized) return true;
            await SendEventAsync(
                $"evt-editor-execution-state-{payload.snapshotId}",
                "editor.execution_state", payload, token).ConfigureAwait(false);
            return true;
        }

        private bool PublishExecutionStateBounded(string transition, int timeoutMs = 100)
        {
            if (!TryBuildAndPersistExecutionState(transition, out var payload)) return false;
            if (!IsStateSynchronized) return true;
            try
            {
                using var timeout = new CancellationTokenSource(Math.Max(1, timeoutMs));
                SendEventAsync(
                    $"evt-editor-execution-state-{payload.snapshotId}",
                    "editor.execution_state", payload, timeout.Token)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception ex)
            {
                Logger.ReportFailure("NETWORK", $"Publish {transition} failed", ex, fingerprint: "execution-state-publish|" + transition);
                return false;
            }
        }

        private EditorStatePayload BuildEditorStatePayload(string source)
        {
            var context = BuildEditorContextPayload(source);
            return new EditorStatePayload
            {
                connected = context.connected,
                authoritative = context.authoritative,
                isStale = context.isStale,
                ready = context.ready,
                blocked = context.blocked,
                blockedReason = context.blockedReason,
                nextAction = context.nextAction,
                source = context.source,
                sessionId = context.sessionId,
                updatedAt = context.updatedAt,
                isCompiling = context.isCompiling,
                isUpdating = context.isUpdating,
                maintenanceInitialization = context.maintenanceInitialization,
                compileStatus = context.compileStatus,
                compileEvidence = context.compileEvidence,
                compilePhase = context.compilePhase,
                playModeState = context.playModeState,
                activeScene = context.activeScene,
            };
        }

        private async Task SendEditorStateEventAsync(CancellationToken token)
        {
            RequestOrigin.Value ??= CaptureOrigin(token);
            await PublishExecutionStateAsync(
                string.Equals(_compileService.Phase, "verifying", StringComparison.OrdinalIgnoreCase)
                    ? "domain_reload_recovered"
                    : "connected_snapshot",
                token).ConfigureAwait(false);
            var payload = BuildEditorStatePayload("editor.state.event");
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
                if (await ObserveCompileOnMainThreadAsync(_compileService.CompleteVerification, token).ConfigureAwait(false))
                {
                    await PublishExecutionStateAsync(
                        _compileService.HasCompileErrors ? "compile_failed" : "compile_completed",
                        token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Push editor.state only (no compile.errors) — used around compile start/end to avoid duplicate error snapshots.</summary>
        private async Task SendEditorStateOnlyAsync(CancellationToken token)
        {
            if (_ws?.State != WebSocketState.Open || !_isAuthenticated) return;
            var payload = BuildEditorStatePayload("editor.state.event");
            await SendEventAsync(
                $"evt-editor-state-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                "editor.state", payload, token);
        }

        private PlayModeChangedPayload CachedPlayModeChangedPayload()
        {
            return new PlayModeChangedPayload { state = _cachedPlayModeState };
        }

        private async Task SendPlayModeChangedEventAsync(
            PlayModeChangedPayload payload,
            CancellationToken token)
        {
            await SendEventAsync(
                $"evt-playmode-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                "playmode.changed",
                payload ?? CachedPlayModeChangedPayload(),
                token);
        }

        private void OnPauseStateChanged(PauseState change)
        {
            // Read actual Editor facts; the notification/request is not a state source.
            _lastMainThreadPumpAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _cachedIsPlaying = EditorApplication.isPlaying;
            _cachedIsPaused = EditorApplication.isPaused;
            _cachedPlayModeState = _cachedIsPaused ? "pause" : (_cachedIsPlaying ? "play" : "edit");
            if (_cts == null || _cts.IsCancellationRequested) return;
            _ = PublishExecutionStateAsync("pause_state_changed", _cts.Token);
        }

        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            UPilotPlayModeTransitions.Observe(change, _sessionId);
            _lastMainThreadPumpAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // Persist and bounded-publish the start before project callbacks can block
            // the main thread or a domain unload destroys the outgoing async send.
            if (change == PlayModeStateChange.ExitingEditMode || change == PlayModeStateChange.ExitingPlayMode)
                PublishExecutionStateBounded("play_mode_changed");
            Logger.Log("SYSTEM", $"PlayMode 状态变更: {change}");
            if (change == PlayModeStateChange.EnteredPlayMode || change == PlayModeStateChange.EnteredEditMode)
                UPilotMcpServerManager.Instance.InvalidateStatusCache();
            _executionService?.OnPlayModeStateChanged(change);
            _lastMainThreadPumpAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _cachedIsPlaying = EditorApplication.isPlaying;
            _cachedIsPaused = EditorApplication.isPaused;
            _cachedPlayModeState = _cachedIsPaused ? "pause" : (_cachedIsPlaying ? "play" : "edit");
            if (change == PlayModeStateChange.ExitingEditMode ||
                change == PlayModeStateChange.EnteredPlayMode ||
                change == PlayModeStateChange.ExitingPlayMode)
            {
                _compileService.ClearStaleCompileBusy(
                    $"PlayMode state changed: {change}",
                    ignoreEditorCompiling: true);
            }

            UPilotOperationTracker.Instance.RecordSystemEvent(
                "sys.playmode.changed", "PlayMode状态变更",
                $"state={change}");
            if (_cts == null || _cts.IsCancellationRequested) return;
            var payload = _playInputService.CurrentPlayModeChangedPayload();
            payload.transition = UPilotPlayModeTransitions.Current;
            _cachedPlayModeState = payload.state;
            _ = PublishExecutionStateAsync("play_mode_changed", _cts.Token);
            _ = SendPlayModeChangedEventAsync(payload, _cts.Token);
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
            Debug.Log("[UPilotBridge] Domain Reload 即将卸载脚本域，Bridge 当前连接将断开。");
            var focus = GetFocusStateString();
            var isCompiling = CurrentIsCompiling();
            var reloadOrigin = CaptureOrigin(CancellationToken.None);
            CloseOrdinaryAdmission("domain_reload");
            reloadOrigin = CaptureOrigin(CancellationToken.None);
            _domainGeneration++;
            _preReloadPublishFailed = !PublishExecutionStateBounded("domain_reload_starting");
            UPilotOperationTracker.Instance.RecordSystemEvent(
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
                        playModeState = CachedPlayModeChangedPayload().state,
                        compilePhase = _compileService?.Phase ?? "domain_reload",
                        compileRequestId = _compileService?.LastRequestId ?? string.Empty,
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
                    using var timeout = new CancellationTokenSource(100);
                    SendOriginJsonAsync(json, timeout.Token, reloadOrigin, false).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Logger.ReportFailure("NETWORK", "Send domain_reload.starting failed", ex);
                }
            }
        }

        private void OnAfterAssemblyReload()
        {
            var focus = GetFocusStateString();
            UPilotOperationTracker.Instance.RecordSystemEvent(
                "sys.domain.reload.done", "Domain Reload完成",
                $"Bridge={(_started ? "运行中" : "已停止")} 焦点={focus}");

            // Restore compile errors from disk after domain reload
            _compileService?.TryRestoreFromDisk();
            _compileService?.MarkVerifyingAfterReload();

            if (_started && !Application.isBatchMode && (_ws == null || _ws.State != WebSocketState.Open))
            {
                Logger.LogWarning("SYSTEM", "Bridge appears running but connection loop is dead after Domain Reload; restarting...");
                Restart();
            }
        }

        private void OnCompilationStarted(object context)
        {
            if (UPilotDiagnosticModuleService.IsCompilationContext(context)) return;
            _pipelineCompileStartUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _pipelineCompileFromMcp = _compileService.IsRequestCompileActive && !string.IsNullOrEmpty(_compileService.LastRequestId);
            _pipelineCompileStatusRequestId = _pipelineCompileFromMcp
                ? _compileService.LastRequestId
                : $"editor-{_pipelineCompileStartUtcMs}";
            var focus = GetFocusStateString();
            UPilotOperationTracker.Instance.RecordSystemEvent(
                "sys.compile.start", "Unity编译开始",
                $"ws={(_ws?.State == WebSocketState.Open ? "连接中" : "未连接")} 焦点={focus}");
            var tok = _cts?.Token ?? CancellationToken.None;
            PublishExecutionStateBounded("compile_started");
            _ = SendCompileStartedMcpPushAsync(tok);
        }

        private async Task SendCompileStartedMcpPushAsync(CancellationToken token)
        {
            RequestOrigin.Value ??= CaptureOrigin(token);
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

        private void OnCompilationFinished(object context)
        {
            if (UPilotDiagnosticModuleService.IsCompilationContext(context)) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var duration = now - _pipelineCompileStartUtcMs;
            if (duration < 0) duration = 0;
            var focus = GetFocusStateString();
            UPilotOperationTracker.Instance.RecordSystemEvent(
                "sys.compile.done", "Unity编译完成",
                $"耗时={duration}ms errors={_compileService.LastErrorCount} ws={(_ws?.State == WebSocketState.Open ? "连接中" : "未连接")} 焦点={focus}");
            var tok = _cts?.Token ?? CancellationToken.None;
            _ = PublishExecutionStateAsync("compiler_finished", tok);
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

            EnqueueUntracked(() =>
            {
                _ = SendCompileFinishedMcpPushAsync(tok, duration);
            });

            // Local compilation must never start a Bridge explicitly stopped by the user.
        }

        private async Task SendCompileFinishedMcpPushAsync(CancellationToken token, long pipelineDurationMs)
        {
            RequestOrigin.Value ??= CaptureOrigin(token);
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
                Logger.ReportFailure("COMPILE", $"Send {eventName} failed", ex, fingerprint: eventName);
            }
        }

        private async Task SendCompileFinishedSnapshotAsync(CancellationToken token)
        {
            RequestOrigin.Value ??= CaptureOrigin(token);
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
                if (errorsPayload.total > 0)
                {
                    if (await ObserveCompileOnMainThreadAsync(_compileService.CompleteVerification, token).ConfigureAwait(false))
                        await PublishExecutionStateAsync("compile_failed", token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Logger.ReportFailure("COMPILE", "Send compile.errors failed", ex);
            }
        }

        private Task SendConnectionJsonAsync(string json, CancellationToken token, ClientWebSocket socket, long epoch) =>
            SendFrameAsync(json, token, socket, epoch, null, false);

        private async Task<bool> SendFrameAsync(string json, CancellationToken token, ClientWebSocket socket, long epoch,
            ConnectionOrigin origin, bool requireReady)
        {
            lock (ConnectionStateLock)
            {
                if (token.IsCancellationRequested || epoch != _helloConnectionEpoch || !ReferenceEquals(socket, _ws) ||
                    (origin != null && !IsOriginCurrentLocked(origin, requireReady))) return false;
            }
            if (socket == null || socket.State != WebSocketState.Open) return false;
            var bytes = Encoding.UTF8.GetBytes(json);
            if (bytes.Length > MaxBridgeResponseBytes)
                throw new InvalidDataException("Bridge message exceeds the 4 MiB send limit.");
            LogOutboundCommand(json);
            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                lock (ConnectionStateLock)
                {
                    if (token.IsCancellationRequested || epoch != _helloConnectionEpoch || !ReferenceEquals(socket, _ws) ||
                        (origin != null && !IsOriginCurrentLocked(origin, requireReady))) return false;
                }
                if (socket.State != WebSocketState.Open) return false;
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private void LogInboundCommand(BridgeEnvelope envelope, string json)
        {
            if (!_debugWireLogsEnabled || envelope == null) return;
            var type = string.IsNullOrEmpty(envelope.type) ? "(unknown)" : envelope.type;
            if (type == "heartbeat" ||
                string.Equals(envelope.name, "session.heartbeat", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(envelope.name, "upilot_flow.results", StringComparison.OrdinalIgnoreCase)) return;

            var payload = UPilotWireJson.StripEnvelopeForDisplay(json);
            Logger.LogNetwork("NETWORK",
                $"sessionId={envelope.sessionId} | name={envelope.name} | type={envelope.type} | id={envelope.id} | {payload}",
                isSend: false);
        }

        private void LogOutboundCommand(string json)
        {
            if (!_debugWireLogsEnabled || string.IsNullOrEmpty(json)) return;
            var envelope = JsonUtility.FromJson<BridgeEnvelope>(json);
            if (envelope == null) return;
            var type = string.IsNullOrEmpty(envelope.type) ? "(unknown)" : envelope.type;
            if (type == "heartbeat") return;
            if (string.Equals(envelope.name, "upilot_flow.results", StringComparison.OrdinalIgnoreCase)) return;

            var payload = UPilotWireJson.StripEnvelopeForDisplay(json);
            Logger.LogNetwork("NETWORK",
                $"sessionId={envelope.sessionId} | name={envelope.name} | type={envelope.type} | id={envelope.id} | {payload}",
                isSend: true);
        }
    }
}
