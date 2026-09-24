// -----------------------------------------------------------------------
// upilot Editor — MCP Server Process Manager
// Manages the external UPilot MCP server process independently of Unity.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CodingRiver.UPilot
{
    public enum McpProcessOwnership
    {
        Unknown,
        CurrentUPilot,
        Foreign,
    }

    public struct McpServerStatus
    {
        public bool IsRunning;
        public bool HttpPortListening;
        public bool WsPortListening;
        public int? ProcessId;
        public string ProcessCommandLine;
        public string ErrorMessage;
        public int WsClientCount;
        public int HttpClientCount;
        public string ServerVersion;
        public string ProtocolVersion;
        public string BuildCommit;
        public string BuildChannel;
        public string RuntimeMode;
        public bool ToolCountsKnown;
        public bool DetailedToolCountsKnown;
        public int RegisteredToolCount;
        public int AvailableToolCount;
        public int CallableToolCount;
        public int ToolRegistryVersion;
        public string ToolCategorySummary;
        public McpProcessOwnership ProcessOwnership;
        public string ProcessOwnershipEvidence;
        public bool HealthResponded;
        public bool HealthEndpointResponded;
        public bool HealthIdentifiesUPilot;
        public int HealthServerProcessId;
        public string HealthProjectPath;
        public bool DiagnosisPending;
        public int ConsecutiveIdentityMisses;
        public long IdentityPendingSinceUtcMs;
        public bool StatusQueryCompleted;
        public string StatusFailureStage;
        public int StatusGeneration;
        public string StatusCancellationReason;
        public long LastSuccessfulStatusAtUtcMs;
        internal UPilotServerHealth Health;
    }

    internal enum UPilotServerStopOrigin
    {
        User, PackageRegistration, PackageUpdate, ManagedServerUpdate, UpdateWindow, EditorShutdown
    }

    public sealed class UPilotMcpServerManager
    {
        public static UPilotMcpServerManager Instance { get; } = new();

        private static string DefaultPythonEntry => ResolveDefaultPythonEntry();
        private const string DefaultLogLevel = "INFO";
        private const string PackageName = "io.github.codingriver.upilot";
        private static readonly string CurrentProjectLogPath =
            Path.Combine(UPilotProjectConfig.ProjectRoot, "log", "mcp-server.log");
        private string _pythonEntryPath = DefaultPythonEntry;
        private string _logLevel = DefaultLogLevel;
        private bool _autoStart = true;
        private bool _pythonEntryManaged = true;
        private string EntryManagedKey => UPilotPreferences.McpPythonEntryKey + ".Managed";

        /// <summary>HTTP port is stored and managed by UPilotBridge (single source of truth).</summary>
        public int HttpPort => UPilotBridge.Instance?.HttpPort ?? 8011;
        /// <summary>WS port is stored and managed by UPilotBridge (single source of truth).</summary>
        public int WsPort => UPilotBridge.Instance?.WsPort ?? 8765;
        public string PythonEntryPath => _pythonEntryPath;

        public bool IsPythonEntryValid(out string absolutePath)
        {
            absolutePath = ToAbsoluteProjectPath(_pythonEntryPath).Replace('\\', '/');
            return File.Exists(absolutePath);
        }

        public void ResetPythonEntryPathToDefaultAbsolute()
        {
            string defaultPath = ResolveDefaultPythonEntry();
            _pythonEntryPath = ToAbsoluteProjectPath(defaultPath).Replace('\\', '/');
            _pythonEntryManaged = true;
            SavePrefs();
        }

        public void SetPythonEntryPath(string path)
        {
            _pythonEntryPath = path;
            _pythonEntryManaged = UPilotDeploymentDiagnostics.SamePath(ToAbsoluteProjectPath(path), DefaultPythonEntry);
            SavePrefs();
        }

        /// <summary>
        /// Validates the current persisted path on window open.
        /// If the path is invalid, attempts auto-discovery.
        /// Only updates when auto-discovery succeeds; leaves user-defined paths untouched on failure.
        /// </summary>
        public void ValidateAndAutoFixPath()
        {
            if (_pythonEntryManaged)
                ResetPythonEntryPathToDefaultAbsolute();
            // Custom entries, including missing ones, require an explicit user edit.
        }

        internal void PreparePythonEntryForRepair()
        {
            ValidateAndAutoFixPath();
            if (!IsPythonEntryValid(out var actual))
                throw new FileNotFoundException("配套 Server 入口不存在：" + actual);
            if (!UPilotDeploymentDiagnostics.SamePath(actual, UPilotDeploymentDiagnostics.PythonEntry))
                throw new InvalidOperationException("自定义 Server 入口与当前包不一致，请在高级设置明确修改或重置入口。\n" +
                    "配置入口：" + actual + "\n当前包入口：" + UPilotDeploymentDiagnostics.PythonEntry);
        }
        public string LogLevel { get => _logLevel; set { if (_logLevel != value) { _logLevel = value; SavePrefs(); } } }
        public bool AutoStartEnabled { get => _autoStart; set { if (_autoStart != value) { _autoStart = value; SavePrefs(); } } }

        internal void ResetPreferencesToDefaultsInMemory()
        {
            _pythonEntryPath = DefaultPythonEntry;
            _pythonEntryManaged = true;
            _logLevel = DefaultLogLevel;
            _autoStart = true;
        }

        // ── Cached status (background refresh) ────────────────────────────────

        private const int RefreshIntervalMs = 2000;
        private const int IdentityGraceMs = 8000;
        private const int IdentityFailureThreshold = 3;
        private const long RestartVerificationTimeoutMs = 20000;
        private const long RestartProbeIntervalMs = 250;
        private readonly object _statusLock = new();
        private McpServerStatus _cachedStatus;
        private Task<McpServerStatus> _statusRefreshTask;
        private CancellationTokenSource _statusRefreshCancellation;
        private string _statusInterruptReason = "";
        private int _statusInterruptedGeneration = -1;
        private int _statusGeneration;
        private int _consecutiveIdentityMisses;
        private long _identityPendingSinceMs;
        private long _lastRefreshMs;
        private bool _restartPending;
        private bool _startInProgress;
        private int _startAttemptGeneration;
        private int? _trackedProcessId;
        private Process _trackedProcess;
        private long _trackedProcessCreatedAtTicks;
        private readonly List<Process> _stoppingProcesses = new();
        private EditorApplication.CallbackFunction _restartWaitCallback;
        private EditorApplication.CallbackFunction _restartObserveCallback;
        private Action _afterRestartStarted;
        private string _restartOperationId = "";
        private long _restartGeneration;
        private string _restartOldBridgeSessionId = "";
        private string _restartExpectedProjectPath = "";
        private long _restartVerificationDeadlineUtcMs;
        private long _restartMaintenanceDeadlineUtcMs;
        internal bool IsServiceTransitionActive => _restartPending || _startInProgress || IsRestartObservationActive();
        private long _restartNextProbeAtUtcMs;
        private bool _restartHealthProbeRunning;
        private string _activeStatusRefreshStage = "port_probe";
        private bool _recoveryObservationRunning;
        private long _lastRecoveryObservedStatusAtUtcMs;

        private static readonly System.Net.Http.HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(2)
        };

        private readonly struct ServerStatsProbe
        {
            public ServerStatsProbe(
                int wsCount,
                int httpCount,
                string version,
                string protocol,
                string commit,
                string channel,
                bool toolCountsKnown,
                bool detailedToolCountsKnown,
                int registeredToolCount,
                int availableToolCount,
                int callableToolCount,
                int toolRegistryVersion,
                string toolCategorySummary,
                bool responded,
                bool identifiesUPilot,
                bool healthEndpointResponded,
                int healthServerProcessId,
                string healthProjectPath,
                UPilotServerHealth health,
                string failure)
            {
                WsCount = wsCount;
                HttpCount = httpCount;
                Version = version;
                Protocol = protocol;
                Commit = commit;
                Channel = channel;
                ToolCountsKnown = toolCountsKnown;
                DetailedToolCountsKnown = detailedToolCountsKnown;
                RegisteredToolCount = registeredToolCount;
                AvailableToolCount = availableToolCount;
                CallableToolCount = callableToolCount;
                ToolRegistryVersion = toolRegistryVersion;
                ToolCategorySummary = toolCategorySummary;
                Responded = responded;
                IdentifiesUPilot = identifiesUPilot;
                HealthEndpointResponded = healthEndpointResponded;
                HealthServerProcessId = healthServerProcessId;
                HealthProjectPath = healthProjectPath;
                Health = health;
                Failure = failure;
            }

            public int WsCount { get; }
            public int HttpCount { get; }
            public string Version { get; }
            public string Protocol { get; }
            public string Commit { get; }
            public string Channel { get; }
            public bool ToolCountsKnown { get; }
            public bool DetailedToolCountsKnown { get; }
            public int RegisteredToolCount { get; }
            public int AvailableToolCount { get; }
            public int CallableToolCount { get; }
            public int ToolRegistryVersion { get; }
            public string ToolCategorySummary { get; }
            public bool Responded { get; }
            public bool IdentifiesUPilot { get; }
            public bool HealthEndpointResponded { get; }
            public int HealthServerProcessId { get; }
            public string HealthProjectPath { get; }
            public UPilotServerHealth Health { get; }
            public string Failure { get; }
        }

        private readonly struct McpProcessProbe
        {
            public McpProcessProbe(
                McpProcessOwnership ownership,
                int? processId,
                string commandLine,
                string evidence)
            {
                Ownership = ownership;
                ProcessId = processId;
                CommandLine = commandLine;
                Evidence = evidence;
            }

            public McpProcessOwnership Ownership { get; }
            public int? ProcessId { get; }
            public string CommandLine { get; }
            public string Evidence { get; }
        }

        private static string ResolveDefaultPythonEntry()
        {
            try
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UPilotBridge).Assembly);
                if (!string.IsNullOrWhiteSpace(package?.resolvedPath))
                    return Path.Combine(package.resolvedPath, "upilotserver~", "run_upilot_mcp.py").Replace('\\', '/');
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UPilotMcpServerManager] ResolveDefaultPythonEntry exception: {ex}");
            }
            // Missing package identity is not permission to select an arbitrary cached checkout.
            return Path.Combine(UPilotProjectConfig.ProjectRoot, "Packages", PackageName,
                "upilotserver~", "run_upilot_mcp.py").Replace('\\', '/');
        }

        private static bool TryResolveManifestFileDependencyRoot(
            string dependencyValue,
            string manifestPath,
            out string packageRoot,
            out bool isArchive)
        {
            packageRoot = null;
            isArchive = false;

            if (string.IsNullOrEmpty(dependencyValue) ||
                !dependencyValue.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                return false;

            string filePath = dependencyValue.Substring(5);

            // Normalize URI-style file paths: file:///path → /path
            while (filePath.Length >= 2 && filePath[0] == '/' && filePath[1] == '/')
                filePath = filePath.Substring(1);

            // Windows: /D:/path → D:/path
            if (filePath.Length > 2 && filePath[0] == '/' && filePath[2] == ':' && char.IsLetter(filePath[1]))
                filePath = filePath.Substring(1);

            if (filePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ||
                filePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                isArchive = true;
                return false;
            }

            if (Path.IsPathRooted(filePath))
            {
                packageRoot = Path.GetFullPath(filePath);
                return true;
            }

            string manifestDirectory = Path.GetDirectoryName(manifestPath);
            if (string.IsNullOrEmpty(manifestDirectory))
                return false;

            packageRoot = Path.GetFullPath(Path.Combine(manifestDirectory, filePath));
            return true;
        }

        private static string ToAbsoluteProjectPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                path = "./upilotserver~/run_upilot_mcp.py";
            }

            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            return Path.GetFullPath(Path.Combine(projectRoot, path));
        }

        private UPilotMcpServerManager()
        {
            LoadPrefs();
            AssemblyReloadEvents.beforeAssemblyReload += () => InvalidateStatusCache("domain_reload");
            EditorApplication.quitting += () =>
            {
                CancelPendingRestart(true, UPilotServerStopOrigin.EditorShutdown, Guid.NewGuid().ToString("N"),
                    _restartOperationId, _restartGeneration);
                InvalidateStatusCache("editor_exit");
            };
            try
            {
                if (!AssetDatabase.IsAssetImportWorkerProcess())
                    EditorApplication.delayCall += ResumePersistedRestartObservation;
            }
            catch
            {
                // Fail closed: an import worker must never take ownership of restart recovery.
            }
        }

        private void LoadPrefs()
        {

            _pythonEntryPath = EditorPrefs.GetString(UPilotPreferences.McpPythonEntryKey, DefaultPythonEntry);
            var absoluteEntry = ToAbsoluteProjectPath(_pythonEntryPath).Replace('\\', '/');
            var legacyCache = Path.Combine(UPilotProjectConfig.ProjectRoot, "Library", "PackageCache", PackageName + "@").Replace('\\', '/');
            _pythonEntryManaged = EditorPrefs.GetBool(EntryManagedKey,
                UPilotDeploymentDiagnostics.SamePath(absoluteEntry, DefaultPythonEntry) ||
                (absoluteEntry.StartsWith(legacyCache, StringComparison.OrdinalIgnoreCase) &&
                 absoluteEntry.EndsWith("/upilotserver~/run_upilot_mcp.py", StringComparison.OrdinalIgnoreCase)));
            if (_pythonEntryManaged) _pythonEntryPath = DefaultPythonEntry;
            _logLevel = EditorPrefs.GetString(UPilotPreferences.McpLogLevelKey, DefaultLogLevel);
            _autoStart = EditorPrefs.GetBool(UPilotPreferences.McpAutoStartKey, true);
            var bridge = UPilotBridge.Instance;
            Debug.Log($"[UPilotMcpServerManager] LoadPrefs loaded: HttpPort={bridge?.HttpPort ?? 8011}, WsPort={bridge?.WsPort ?? 8765}, PythonEntryPath={_pythonEntryPath}, LogLevel={_logLevel}, AutoStart={_autoStart}");

            // If the persisted path points to a file that no longer exists
            // (e.g. old root-level upilot/ was removed), or if the path
            // mistakenly uses the UPM package name as the directory name
            // (e.g. "Packages/io.github.codingriver.upilot/..."), re-discover.
            if (_pythonEntryManaged && !string.IsNullOrEmpty(_pythonEntryPath))
            {
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                string fullPath = Path.IsPathRooted(_pythonEntryPath)
                    ? _pythonEntryPath
                    : Path.GetFullPath(Path.Combine(projectRoot, _pythonEntryPath));
                bool needsRediscover = !File.Exists(fullPath);
                Debug.Log($"[UPilotMcpServerManager] LoadPrefs validation: fullPath={fullPath}, exists={File.Exists(fullPath)}, needsRediscover={needsRediscover}");
                if (needsRediscover)
                {
                    string discovered = DefaultPythonEntry;
                    Debug.Log($"[UPilotMcpServerManager] LoadPrefs rediscovering: {discovered}");
                    _pythonEntryPath = discovered;
                    SavePrefs();
                }
            }
        }
        
        private void SavePrefs()
        {
            EditorPrefs.SetString(UPilotPreferences.McpPythonEntryKey, _pythonEntryPath ?? DefaultPythonEntry);
            EditorPrefs.SetString(UPilotPreferences.McpLogLevelKey, _logLevel ?? DefaultLogLevel);
            EditorPrefs.SetBool(UPilotPreferences.McpAutoStartKey, _autoStart);
            EditorPrefs.SetBool(EntryManagedKey, _pythonEntryManaged);
        }

        // ── Status ──────────────────────────────────────────────────────────

        public McpServerStatus GetStatus()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _lastRefreshMs > RefreshIntervalMs)
                RequestBackgroundStatusRefresh();
            McpServerStatus status;
            lock (_statusLock) status = _cachedStatus;
            ObservePossibleRecovery(status);
            return status;
        }

        public void InvalidateStatusCache(string lifecycleReason = "")
        {
            lock (_statusLock)
            {
                int interrupted = Interlocked.Increment(ref _statusGeneration) - 1;
                _statusInterruptReason = lifecycleReason;
                _statusInterruptedGeneration = interrupted;
                if (!string.IsNullOrEmpty(lifecycleReason)) _statusRefreshCancellation?.Cancel();
                // A cache refresh must not erase an error or restart its deadline.
                _lastRefreshMs = 0;
            }
        }

        public async Task<McpServerStatus> GetFreshStatusAsync(long deadlineUtcMs = 0)
        {
            RequestBackgroundStatusRefresh();
            Task<McpServerStatus> task;
            lock (_statusLock) task = _statusRefreshTask;
            if (deadlineUtcMs > 0)
            {
                var remaining = deadlineUtcMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (remaining <= 0 || await Task.WhenAny(task, Task.Delay((int)Math.Min(remaining, 30000))) != task)
                    throw new TimeoutException("Maintenance status probe exceeded its remaining budget.");
            }
            var result = await task;
            ObservePossibleRecovery(result);
            return result;
        }

        private void ObservePossibleRecovery(McpServerStatus status)
        {
            var record = UPilotServerRestartDiagnostics.Current;
            if (_recoveryObservationRunning || record == null || record.status != "failed" ||
                status.LastSuccessfulStatusAtUtcMs <= record.endedAtUtcMs ||
                status.LastSuccessfulStatusAtUtcMs <= _lastRecoveryObservedStatusAtUtcMs ||
                record.recoveredAtUtcMs > 0 || record.newProcessId <= 0 ||
                string.IsNullOrEmpty(record.newBridgeSessionId) ||
                (record.errorCode != "restart_verification_timeout" && record.errorCode != "SERVICE_RESTART_TIMEOUT") ||
                !IsVerifiedRestartHealth(status, record.newProcessId, record.projectPath)) return;
            var bridge = UPilotBridge.Instance.GetStatus();
            if (!bridge.IsAuthenticated || !bridge.IsWsOpen || bridge.SessionId != record.newBridgeSessionId) return;
            _lastRecoveryObservedStatusAtUtcMs = status.LastSuccessfulStatusAtUtcMs;
            _recoveryObservationRunning = true;
            _ = VerifyRecoveryAsync(record.operationId, status, bridge);
        }

        private async Task VerifyRecoveryAsync(string operationId, McpServerStatus status, BridgeStatus bridge)
        {
            try
            {
                var issues = UPilotDeploymentDiagnostics.Observe(bridge, status);
                if (issues.Length != 0) return;
                await VerifyReadOnlyRoundTripAsync(status, bridge.SessionId);
                var currentBridge = UPilotBridge.Instance.GetStatus();
                if (!currentBridge.IsAuthenticated || !currentBridge.IsWsOpen || currentBridge.SessionId != bridge.SessionId) return;
                UPilotServerRestartDiagnostics.ObserveRecovery(operationId, status.ProcessId ?? 0, bridge.SessionId,
                    status.HealthProjectPath, status.HealthEndpointResponded, true, true);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("SERVER", "Read-only recovery observation failed: " + ex.Message);
            }
            finally { _recoveryObservationRunning = false; }
        }

        private void RequestBackgroundStatusRefresh()
        {
            var httpPort = HttpPort;
            var wsPort = WsPort;
            lock (_statusLock)
            {
                if (_statusRefreshTask != null && !_statusRefreshTask.IsCompleted) return;
                var generation = Volatile.Read(ref _statusGeneration);
                var cancellation = new CancellationTokenSource();
                _activeStatusRefreshStage = "port_probe";
                _statusRefreshCancellation = cancellation;
                _statusRefreshTask = RunBoundedStatusRefreshAsync(httpPort, wsPort, generation, cancellation);
            }
        }

        private async Task<McpServerStatus> RunBoundedStatusRefreshAsync(int httpPort, int wsPort, int generation,
            CancellationTokenSource cancellation)
        {
            var work = Task.Run(() => RefreshStatusAsync(httpPort, wsPort, generation, cancellation.Token));
            _ = work.ContinueWith(_ =>
            {
                lock (_statusLock)
                {
                    if (ReferenceEquals(_statusRefreshCancellation, cancellation))
                        _statusRefreshCancellation = null;
                }
                cancellation.Dispose();
            }, TaskScheduler.Default);
            if (await Task.WhenAny(work, Task.Delay(30000)).ConfigureAwait(false) == work)
                return await work.ConfigureAwait(false);
            lock (_statusLock)
            {
                if (ReferenceEquals(_statusRefreshCancellation, cancellation))
                    cancellation.Cancel();
                if (generation == Volatile.Read(ref _statusGeneration))
                {
                    Interlocked.Increment(ref _statusGeneration);
                    _cachedStatus.ErrorMessage = "状态获取超时（30 秒）：进程识别、HTTP 查询或状态汇总未完成。";
                    _cachedStatus.StatusFailureStage = _activeStatusRefreshStage;
                    _cachedStatus.StatusGeneration = generation;
                    _cachedStatus.StatusCancellationReason = "timeout";
                    _cachedStatus.StatusQueryCompleted = true;
                    _lastRefreshMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
                return _cachedStatus;
            }
        }

        private void TrackStatusRefreshStage(int generation, string stage)
        {
            lock (_statusLock)
                if (generation == Volatile.Read(ref _statusGeneration))
                    _activeStatusRefreshStage = stage;
        }

        private async Task<McpServerStatus> RefreshStatusAsync(int httpPort, int wsPort, int generation,
            CancellationToken token)
        {
            var status = new McpServerStatus();
            status.StatusGeneration = generation;
            var stage = "port_probe";
            try
            {
                var httpTask = IsPortListeningAsync("127.0.0.1", httpPort);
                var wsTask = IsPortListeningAsync("127.0.0.1", wsPort);
                token.ThrowIfCancellationRequested();
                status.HttpPortListening = await httpTask;
                status.WsPortListening = await wsTask;
                token.ThrowIfCancellationRequested();
                status.IsRunning = status.HttpPortListening || status.WsPortListening;

                if (status.IsRunning)
                {
                    stage = "process_identity";
                    TrackStatusRefreshStage(generation, stage);
                    var process = ProbeMcpProcessOwnership(httpPort, wsPort);
                    status.ProcessOwnership = process.Ownership;
                    status.ProcessId = process.ProcessId;
                    status.ProcessCommandLine = process.CommandLine;
                    status.ProcessOwnershipEvidence = process.Evidence;

                    stage = "health_query";
                    TrackStatusRefreshStage(generation, stage);
                    var stats = await FetchServerStatsAsync(httpPort, token);
                    status.WsClientCount = stats.WsCount;
                    status.HttpClientCount = stats.HttpCount;
                    status.ServerVersion = stats.Version;
                    status.ProtocolVersion = stats.Protocol;
                    status.BuildCommit = stats.Commit;
                    status.BuildChannel = stats.Channel;
                    status.ToolCountsKnown = stats.ToolCountsKnown;
                    status.DetailedToolCountsKnown = stats.DetailedToolCountsKnown;
                    status.RegisteredToolCount = stats.RegisteredToolCount;
                    status.AvailableToolCount = stats.AvailableToolCount;
                    status.CallableToolCount = stats.CallableToolCount;
                    status.ToolRegistryVersion = stats.ToolRegistryVersion;
                    status.ToolCategorySummary = stats.ToolCategorySummary;
                    status.HealthResponded = stats.Responded;
                    status.HealthEndpointResponded = stats.HealthEndpointResponded;
                    status.HealthIdentifiesUPilot = stats.IdentifiesUPilot;
                    status.HealthServerProcessId = stats.HealthServerProcessId;
                    status.HealthProjectPath = stats.HealthProjectPath;
                    status.Health = stats.Health;
                    status.StatusFailureStage = stats.HealthEndpointResponded ? "" : "health_query";
                    if (!stats.HealthEndpointResponded)
                        status.ErrorMessage = "状态获取失败：" + stats.Failure;
                }
            }
            catch (Exception ex)
            {
                string reason;
                lock (_statusLock) reason = _statusInterruptedGeneration == generation ? _statusInterruptReason : "";
                bool expected = IsExpectedStatusInterruption(ex, token, reason);
                status.ErrorMessage = ex.GetType().Name + ": " + ex.Message;
                status.StatusFailureStage = stage;
                status.StatusCancellationReason = expected ? reason : ex is OperationCanceledException ? "timeout" : "";
                if (expected)
                    Debug.LogWarning($"[UPilotMcpServerManager] Expected {reason} status interruption at {stage} (generation {generation}).");
                else
                    Debug.LogError($"[UPilotMcpServerManager] Status refresh failed at {stage} (generation {generation}): {ex}");
            }

            var refreshedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (_statusLock)
            {
                if (generation != Volatile.Read(ref _statusGeneration))
                    return _cachedStatus;

                status.StatusQueryCompleted = true;
                status.LastSuccessfulStatusAtUtcMs = status.HealthEndpointResponded
                    ? refreshedAt : _cachedStatus.LastSuccessfulStatusAtUtcMs;
                if (!string.IsNullOrEmpty(status.ErrorMessage))
                    status.Health ??= _cachedStatus.Health;
                UpdateDiagnosisTracking(ref status, refreshedAt);
                _cachedStatus = status;
                _lastRefreshMs = refreshedAt;
            }

            return status;
        }

        internal static bool IsExpectedStatusInterruption(Exception ex, CancellationToken token, string reason) =>
            ex is OperationCanceledException && token.IsCancellationRequested
            && (reason == "domain_reload" || reason == "editor_exit" || reason == "explicit_stop");

        private void UpdateDiagnosisTracking(ref McpServerStatus status, long nowMs)
        {
            var settled = !status.IsRunning ||
                          (status.HttpPortListening &&
                           status.WsPortListening &&
                           status.ProcessOwnership == McpProcessOwnership.CurrentUPilot);
            if (settled)
            {
                _consecutiveIdentityMisses = 0;
                _identityPendingSinceMs = 0;
                status.DiagnosisPending = false;
                return;
            }

            if (_identityPendingSinceMs <= 0)
                _identityPendingSinceMs = nowMs;
            _consecutiveIdentityMisses++;

            status.ConsecutiveIdentityMisses = _consecutiveIdentityMisses;
            status.IdentityPendingSinceUtcMs = _identityPendingSinceMs;
            status.DiagnosisPending =
                _consecutiveIdentityMisses < IdentityFailureThreshold ||
                nowMs - _identityPendingSinceMs < IdentityGraceMs;
        }

        private async Task<ServerStatsProbe> FetchServerStatsAsync(int httpPort, CancellationToken token = default)
        {
            int wsCount = 0;
            int httpCount = 0;
            string version = "";
            string protocol = "";
            string commit = "";
            string channel = "";
            bool toolCountsKnown = false;
            bool detailedToolCountsKnown = false;
            int registeredToolCount = 0;
            int availableToolCount = 0;
            int callableToolCount = 0;
            int toolRegistryVersion = 0;
            string toolCategorySummary = "";
            bool responded = false;
            bool identifiesUPilot = false;
            bool healthEndpointResponded = false;
            int healthServerProcessId = 0;
            string healthProjectPath = "";
            UPilotServerHealth health = null;
            string failure = "";
            try
            {
                var url = $"http://127.0.0.1:{httpPort}/stats";
                using var response = await _httpClient.GetAsync(url, token);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    responded = true;
                    identifiesUPilot |= IsUPilotServerPayload(json);
                    wsCount = ParseIntFromJson(json, "ws_connections");
                    httpCount = ParseIntFromJson(json, "http_sessions");
                    version = ParseStringFromJson(json, "server_version");
                    protocol = ParseStringFromJson(json, "protocol_version");
                    commit = ParseStringFromJson(json, "build_commit");
                    channel = ParseStringFromJson(json, "build_channel");
                    toolCountsKnown = HasJsonProperty(json, "tool_count") ||
                                      HasJsonProperty(json, "available_tool_count");
                    detailedToolCountsKnown = HasJsonProperty(json, "registered_tool_count") &&
                                              HasJsonProperty(json, "available_tool_count") &&
                                              HasJsonProperty(json, "callable_tool_count");
                    availableToolCount = HasJsonProperty(json, "available_tool_count")
                        ? ParseIntFromJson(json, "available_tool_count")
                        : ParseIntFromJson(json, "tool_count");
                    registeredToolCount = detailedToolCountsKnown
                        ? ParseIntFromJson(json, "registered_tool_count")
                        : availableToolCount;
                    callableToolCount = detailedToolCountsKnown
                        ? ParseIntFromJson(json, "callable_tool_count")
                        : availableToolCount;
                    toolRegistryVersion = ParseIntFromJson(json, "registry_version");
                    toolCategorySummary = ParseStringFromJson(json, "tool_category_summary");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = "/stats: " + ex.GetType().Name + ": " + ex.Message;
            }

            try
            {
                var url = $"http://127.0.0.1:{httpPort}/health";
                using var response = await _httpClient.GetAsync(url, token);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    health = JsonUtility.FromJson<UPilotServerHealth>(json);
                    if (health == null) throw new InvalidDataException("/health 未返回 JSON 对象");
                    responded = true;
                    healthEndpointResponded = true;
                    identifiesUPilot |= IsUPilotServerPayload(json);
                    healthServerProcessId = ParseIntFromJson(json, "server_pid");
                    healthProjectPath = ParseStringFromJson(json, "project_path");
                    version = health.server_version;
                    protocol = health.protocol_version;
                    commit = health.build_commit;
                    channel = health.build_channel;

                    var healthToolCountsKnown = HasJsonProperty(json, "tool_count") ||
                                                HasJsonProperty(json, "available_tool_count");
                    var healthDetailedToolCountsKnown = HasJsonProperty(json, "registered_tool_count") &&
                                                        HasJsonProperty(json, "available_tool_count") &&
                                                        HasJsonProperty(json, "callable_tool_count");
                    if (healthDetailedToolCountsKnown)
                    {
                        toolCountsKnown = true;
                        detailedToolCountsKnown = true;
                        registeredToolCount = ParseIntFromJson(json, "registered_tool_count");
                        availableToolCount = ParseIntFromJson(json, "available_tool_count");
                        callableToolCount = ParseIntFromJson(json, "callable_tool_count");
                    }
                    else if (!toolCountsKnown && healthToolCountsKnown)
                    {
                        toolCountsKnown = true;
                        availableToolCount = HasJsonProperty(json, "available_tool_count")
                            ? ParseIntFromJson(json, "available_tool_count")
                            : ParseIntFromJson(json, "tool_count");
                        registeredToolCount = availableToolCount;
                        callableToolCount = availableToolCount;
                    }

                    if (toolRegistryVersion <= 0)
                        toolRegistryVersion = ParseIntFromJson(json, "registry_version");
                    if (string.IsNullOrEmpty(toolCategorySummary))
                        toolCategorySummary = ParseStringFromJson(json, "tool_category_summary");
                }
                else failure = "/health HTTP " + (int)response.StatusCode;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = (failure.Length > 0 ? failure + "; " : "") + "/health: " + ex.GetType().Name + ": " + ex.Message;
            }

            return new ServerStatsProbe(
                wsCount,
                httpCount,
                version,
                protocol,
                commit,
                channel,
                toolCountsKnown,
                detailedToolCountsKnown,
                registeredToolCount,
                availableToolCount,
                callableToolCount,
                toolRegistryVersion,
                toolCategorySummary,
                responded,
                identifiesUPilot,
                healthEndpointResponded,
                healthServerProcessId,
                healthProjectPath,
                health,
                failure);
        }

        internal static bool IsVerifiedStartupHealth(McpServerStatus status)
        {
            if (!status.HealthEndpointResponded || !status.HealthIdentifiesUPilot ||
                status.ProcessOwnership != McpProcessOwnership.CurrentUPilot ||
                !status.ProcessId.HasValue || status.ProcessId.Value <= 0 ||
                status.HealthServerProcessId != status.ProcessId.Value)
                return false;

            return !string.Equals(
                status.ProcessOwnershipEvidence,
                "UPilot 健康检查响应",
                StringComparison.Ordinal);
        }

        private static bool HasJsonProperty(string json, string key)
        {
            return !string.IsNullOrEmpty(json) &&
                   json.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsUPilotServerPayload(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return false;

            return json.IndexOf("\"server_version\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   json.IndexOf("\"protocol_version\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   json.IndexOf("\"build_channel\"", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public async Task<string> WaitForServerVersionAsync(string expectedVersion, int timeoutMs = 10000)
        {
            var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + timeoutMs;
            string latestVersion = "";
            while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < deadline)
            {
                var stats = await FetchServerStatsAsync(HttpPort);
                latestVersion = stats.Version;
                if (!string.IsNullOrWhiteSpace(latestVersion) &&
                    (string.IsNullOrWhiteSpace(expectedVersion) ||
                     string.Equals(latestVersion, expectedVersion, StringComparison.Ordinal)))
                {
                    InvalidateStatusCache();
                    return latestVersion;
                }

                await Task.Delay(250);
            }

            InvalidateStatusCache();
            return latestVersion;
        }

        private static int ParseIntFromJson(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(json)) return 0;
            var pattern = $"\"{key}\"\\s*:\\s*(\\d+)";
            var match = System.Text.RegularExpressions.Regex.Match(json, pattern);
            return match.Success && int.TryParse(match.Groups[1].Value, out var val) ? val : 0;
        }

        private static string ParseStringFromJson(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(json)) return "";
            var pattern = $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"";
            var match = Regex.Match(json, pattern);
            return match.Success ? Regex.Unescape(match.Groups[1].Value) : "";
        }

        // ── Start ───────────────────────────────────────────────────────────

        public void StartServer()
        {
            if (UPilotServiceMaintenance.IsActive && _restartMaintenanceDeadlineUtcMs <= 0 &&
                !UPilotServiceMaintenance.IsExecuting) return;
            var processRole = UPilotBridge.DetermineProcessRole();
            if (!UPilotBridge.IsMainEditorProcess(processRole))
            {
                var reason = $"MCP Server start rejected for auxiliary Unity process role '{processRole}'.";
                Debug.LogWarning("[UPilotMcpServerManager] " + reason);
                UPilotStartupDiagnostics.RecordBlockingReason("server_auxiliary_process_role", reason);
                RecordRestartStartFailure("server_auxiliary_process_role", reason);
                return;
            }

            if (UPilotUpdateService.Instance.IsServiceStartBlocked)
            {
                Debug.LogWarning("[UPilotMcpServerManager] " + UPilotUpdateService.ServiceStartBlockedMessage);
                UPilotStartupDiagnostics.RecordBlockingReason(
                    "server_update_blocked",
                    UPilotUpdateService.ServiceStartBlockedMessage);
                RecordRestartStartFailure(
                    "server_update_blocked",
                    UPilotUpdateService.ServiceStartBlockedMessage);
                return;
            }

            if (_restartPending)
            {
                Debug.Log("[UPilotMcpServerManager] MCP server restart is already pending; start request merged.");
                return;
            }

            // Startup must not reuse the stop-grade global process scan: an unrelated process with
            // unreadable identity evidence must not block this project from starting. Port conflicts
            // are handled below by the health-based attach probe and the availability recheck.
            if (_startInProgress || IsTrackedProcessAlive())
            {
                Debug.Log("[UPilotMcpServerManager] MCP server start is already in progress or the project service is running; start request merged.");
                return;
            }

            if (!UPilotPortAllocator.IsPortAvailable(HttpPort) ||
                !UPilotPortAllocator.IsPortAvailable(WsPort))
            {
                _startInProgress = true;
                var generation = Interlocked.Increment(ref _startAttemptGeneration);
                _ = AttachOrStartAfterIdentityProbeAsync(generation);
                return;
            }

            StartNewServer();
        }

        private async Task AttachOrStartAfterIdentityProbeAsync(int generation)
        {
            McpServerStatus status = default;
            try
            {
                status = await GetFreshStatusAsync();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UPilotMcpServerManager] Existing server identity probe failed: " + ex.Message);
            }

            if (generation != Volatile.Read(ref _startAttemptGeneration) || !_startInProgress)
                return;

            try
            {
                if (IsVerifiedExistingProjectService(status, UPilotProjectConfig.ProjectRoot) &&
                    TryTrackExistingProcess(status.HealthServerProcessId))
                {
                    Debug.Log(
                        $"[UPilotMcpServerManager] Reattached to existing MCP server PID={status.HealthServerProcessId} " +
                        $"for project {status.HealthProjectPath}; start request merged.");
                    InvalidateStatusCache();
                    return;
                }

                StartNewServer();
            }
            finally
            {
                if (generation == Volatile.Read(ref _startAttemptGeneration))
                    _startInProgress = false;
            }
        }

        private void StartNewServer()
        {
            if (!UPilotPortRegistration.TrySyncCurrent(requireAvailable: true))
            {
                UPilotStartupDiagnostics.RecordBlockingReason(
                    "server_port_registration_failed",
                    "Current project ports could not be registered as available.");
                RecordRestartStartFailure(
                    "server_port_registration_failed",
                    "Current project ports could not be registered as available.");
                return;
            }

            _startInProgress = true;

            // Auto-sync Bridge WS endpoint to match MCP server port before starting.
            // Bridge and MCP manager store their ports in separate EditorPrefs keys,
            // so they can drift apart after manual edits or version upgrades.
            var bridge = UPilotBridge.Instance;
            if (bridge != null && (bridge.WsPort != WsPort || bridge.WsHost != "127.0.0.1"))
            {
                int oldPort = bridge.WsPort;
                string oldHost = bridge.WsHost;
                bridge.SetWsEndpoint("127.0.0.1", WsPort);
                Debug.LogWarning($"[UPilotMcpServerManager] Auto-synced Bridge endpoint from ws://{oldHost}:{oldPort} to ws://127.0.0.1:{WsPort} to match MCP server.");
            }

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;

            try
            {
                var process = UPilotServerRuntimeService.Instance.GetConfiguredMode() == UPilotServerRuntimeMode.StandaloneExe
                    ? StartViaStandaloneExe(projectRoot)
                    : StartViaDirectPython(projectRoot);
                TrackStartedProcess(process);
                if (process == null)
                    RecordRestartStartFailure(
                        "server_start_failed",
                        "The MCP Server start path returned without a replacement process.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UPilotMcpServerManager] Failed to start server: {ex.Message}");
                UPilotStartupDiagnostics.RecordBlockingReason("server_start_exception", ex.Message);
                RecordRestartStartFailure("server_start_exception", ex.Message);
            }
            finally
            {
                _startInProgress = false;
                InvalidateStatusCache();
            }
        }

        internal static bool IsVerifiedExistingProjectService(
            McpServerStatus status,
            string expectedProjectPath)
        {
            return status.IsRunning &&
                   status.HttpPortListening &&
                   status.WsPortListening &&
                   status.HealthEndpointResponded &&
                   status.HealthIdentifiesUPilot &&
                   status.HealthServerProcessId > 0 &&
                   status.ProcessId == status.HealthServerProcessId &&
                   status.ProcessOwnership != McpProcessOwnership.Foreign &&
                   SameProjectPath(status.HealthProjectPath, expectedProjectPath);
        }

        private bool TryTrackExistingProcess(int processId)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    process.Dispose();
                    return false;
                }

                TrackStartedProcess(process);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[UPilotMcpServerManager] Existing MCP server PID={processId} exited or could not be attached: {ex.Message}");
                return false;
            }
        }

        private Process StartViaDirectPython(string projectRoot)
        {
            string entryFullPath = Path.IsPathRooted(_pythonEntryPath)
                ? _pythonEntryPath
                : Path.GetFullPath(Path.Combine(projectRoot, _pythonEntryPath));

            if (!File.Exists(entryFullPath))
            {
                Debug.LogError($"[UPilotMcpServerManager] Python entry not found: {entryFullPath}");
                UPilotStartupDiagnostics.RecordBlockingReason("server_entry_missing", entryFullPath);
                return null;
            }

            string pythonExe = UPilotProjectConfig.Current.runtime?.pythonPath ?? "";
            if (string.IsNullOrWhiteSpace(pythonExe) || !File.Exists(pythonExe))
                pythonExe = FindPythonExecutable();
            if (string.IsNullOrEmpty(pythonExe))
            {
                Debug.LogError("[UPilotMcpServerManager] No Python interpreter found. Please install Python and ensure 'python', 'py', or 'python3' is available in PATH.");
                UPilotStartupDiagnostics.RecordBlockingReason(
                    "server_runtime_missing",
                    "No configured or discoverable Python interpreter.");
                return null;
            }

            string logDir = Path.Combine(projectRoot, "log");
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);
            string logFile = Path.Combine(logDir, "mcp-server.log");

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{entryFullPath}\" --transport http --http-port {HttpPort} --port {WsPort} --log-file \"{logFile}\" --log-level {_logLevel}",
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            psi.EnvironmentVariables["UPILOT_BUILD_CHANNEL"] = UPilotDeploymentDiagnostics.Channel;
            psi.EnvironmentVariables["UPILOT_INSTALL_SOURCE"] = UPilotDeploymentDiagnostics.InstallSource;
            if (_restartMaintenanceDeadlineUtcMs > 0) UPilotServiceMaintenance.RequireContinuation();
            var proc = Process.Start(psi);
            UPilotStartupDiagnostics.RecordServerProcessStarted(proc?.Id ?? 0);
            Debug.Log($"[UPilotMcpServerManager] Started python process PID={proc?.Id} via {pythonExe} for {entryFullPath} (HTTP={HttpPort}, WS={WsPort})");
            return proc;
        }

        private Process StartViaStandaloneExe(string projectRoot)
        {
            if (!UPilotServerRuntimeService.Instance.IsStandaloneExeConfigured(out var exePath))
            {
                Debug.LogError("[UPilotMcpServerManager] Standalone MCP server exe is not configured. Run UPilot first setup or select a local exe.");
                UPilotStartupDiagnostics.RecordBlockingReason(
                    "server_runtime_missing",
                    "Standalone MCP server executable is not configured.");
                return null;
            }

            string logDir = Path.Combine(projectRoot, "log");
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);
            string logFile = Path.Combine(logDir, "mcp-server.log");

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--transport http --http-port {HttpPort} --port {WsPort} --log-file \"{logFile}\" --log-level {_logLevel}",
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            psi.EnvironmentVariables["UPILOT_INSTALL_SOURCE"] = "ManagedExe";
            if (_restartMaintenanceDeadlineUtcMs > 0) UPilotServiceMaintenance.RequireContinuation();
            var proc = Process.Start(psi);
            UPilotStartupDiagnostics.RecordServerProcessStarted(proc?.Id ?? 0);
            Debug.Log($"[UPilotMcpServerManager] Started standalone server PID={proc?.Id} via {exePath} (HTTP={HttpPort}, WS={WsPort})");
            return proc;
        }

        private static string FindPythonExecutable()
        {
            string[] candidates = new[] { "python", "py", "python3" };
            foreach (var name in candidates)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "where",
                        Arguments = name,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var proc = Process.Start(psi);
                    if (proc == null) continue;
                    proc.WaitForExit(2000);
                    if (proc.ExitCode != 0) continue;
                    string output = proc.StandardOutput.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(output) && File.Exists(output))
                        return output;
                }
                catch { }
            }
            return null;
        }

        // ── Stop ────────────────────────────────────────────────────────────

        public void StopServer() => StopServer(UPilotServerStopOrigin.User);

        internal void StopServer(UPilotServerStopOrigin origin, string requestId = "")
        {
            if (UPilotServiceMaintenance.IsActive && !UPilotServiceMaintenance.IsExecuting) return;
            var operationId = _restartOperationId;
            var generation = _restartGeneration;
            // Capture verified process handles before ending this restart. A delayed stop must
            // never rediscover and kill a replacement started by another generation.
            using var prepared = PrepareCurrentProjectStop();
            if (generation != _restartGeneration || operationId != _restartOperationId) return;
            if (string.IsNullOrEmpty(requestId)) requestId = Guid.NewGuid().ToString("N");
            var target = prepared.Processes.FirstOrDefault();
            CancelPendingRestart(recordCancellation: true, origin, requestId, operationId, generation,
                target.pid, target.createdAtTicks);
            InvalidateStatusCache(origin == UPilotServerStopOrigin.User ? "explicit_stop" : "lifecycle_stop");
            StopPreparedProcesses(prepared, 0);
        }

        private sealed class PreparedProcessStop : IDisposable
        {
            internal readonly List<(Process process, int pid, long createdAtTicks, string commandLine)> Processes = new();
            internal readonly HashSet<int> Transferred = new();
            internal void Add(Process process, string commandLine) =>
                Processes.Add((process, process.Id, process.StartTime.ToUniversalTime().Ticks, commandLine));
            public void Dispose()
            {
                foreach (var entry in Processes)
                    if (!Transferred.Contains(entry.pid)) entry.process.Dispose();
                Processes.Clear();
            }
        }

        private PreparedProcessStop PrepareCurrentProjectStop(int expectedProcessId = 0)
        {
            var prepared = new PreparedProcessStop();
            try
            {
                var processes = FindCurrentProjectMcpProcesses(out var portsByPid, out var portQuerySucceeded, stopTargetsOnly: true);
                if (!portQuerySucceeded)
                    throw new InvalidOperationException("无法安全停止 Server：监听端口归属查询失败。");
                var owners = portsByPid.Where(entry => entry.Value.Contains(HttpPort) || entry.Value.Contains(WsPort))
                    .Select(entry => entry.Key).Distinct().ToArray();
                if (owners.Length > 1 || owners.Any(pid => !processes.Any(process => process.pid == pid)) ||
                    (owners.Length == 0 && (!UPilotPortAllocator.IsPortAvailable(HttpPort) ||
                                            !UPilotPortAllocator.IsPortAvailable(WsPort))))
                    throw new InvalidOperationException("无法安全停止 Server：端口进程不属于当前项目或身份未知。");
                if (expectedProcessId > 0 && (processes.Count != 1 || processes[0].pid != expectedProcessId))
                    throw new ServiceMaintenanceException("SERVICE_RESTART_IDENTITY_CHANGED", "Expected Server process changed before stop.");
                foreach (var entry in processes)
                {
                    var process = Process.GetProcessById(entry.pid);
                    try
                    {
                        // Retain a handle to the verified process so a reused PID cannot be terminated.
                        var handle = process.Handle;
                        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != entry.createdAtTicks)
                            throw new InvalidOperationException("Server process identity changed while preparing stop.");
                        prepared.Add(process, entry.cmdLine);
                    }
                    catch { process.Dispose(); throw; }
                }
                return prepared;
            }
            catch { prepared.Dispose(); throw; }
        }

        private void StopCurrentProjectProcesses(int expectedProcessId = 0)
        {
            InvalidateStatusCache("explicit_stop");
            using var prepared = PrepareCurrentProjectStop(expectedProcessId);
            StopPreparedProcesses(prepared, expectedProcessId);
        }

        private void StopPreparedProcesses(PreparedProcessStop prepared, int expectedProcessId, Action onStopAttempt = null)
        {
            Interlocked.Increment(ref _startAttemptGeneration);
            _startInProgress = false;
            if (expectedProcessId > 0 && (prepared.Processes.Count != 1 || prepared.Processes[0].pid != expectedProcessId))
                throw new ServiceMaintenanceException("SERVICE_RESTART_IDENTITY_CHANGED", "Expected Server process changed before stop.");
            // Check every handle before causing any termination. No PID-only lookup is used here.
            foreach (var entry in prepared.Processes)
            {
                if (entry.process.HasExited || entry.process.Id != entry.pid ||
                    entry.process.StartTime.ToUniversalTime().Ticks != entry.createdAtTicks ||
                    !IsCurrentProjectMcpCommandLine(entry.commandLine, HttpPort, WsPort))
                    throw new InvalidOperationException("Server process identity changed before stop.");
            }
            var currentPorts = SafeGetListeningPortsByPid(out var portQuerySucceeded);
            if (!portQuerySucceeded)
                throw new InvalidOperationException("Server port ownership became unknown before stop.");
            var owners = currentPorts.Where(item => item.Value.Contains(HttpPort) || item.Value.Contains(WsPort))
                .Select(item => item.Key).Distinct().ToArray();
            if (owners.Any(pid => !prepared.Processes.Any(entry => entry.pid == pid)))
                throw new InvalidOperationException("Server ports changed owner before stop.");
            foreach (var entry in prepared.Processes)
            {
                if (expectedProcessId > 0) UPilotServiceMaintenance.RequireContinuation();
                onStopAttempt?.Invoke();
                if (_restartOperationId.Length > 0 && UPilotServerRestartDiagnostics.IsActive(_restartOperationId))
                    UPilotServerRestartDiagnostics.RecordStopProgress(_restartOperationId, serverAttempted: true);
                try
                {
                    entry.process.Kill();
                    _stoppingProcesses.Add(entry.process);
                    prepared.Transferred.Add(entry.pid);
                    UnityEngine.Debug.Log($"[UPilotMcpServerManager] Killed MCP server process PID={entry.pid}");
                }
                catch (InvalidOperationException)
                {
                    // Exited after the preflight; the handle identifies the old process, not a replacement.
                    if (!entry.process.HasExited) throw;
                }
            }
            _trackedProcessId = null;
            DisposeTrackedProcess();
            InvalidateStatusCache();
        }

        public bool StopServerAndWaitForExit(int timeoutMs = 3000) =>
            StopServerAndWaitForExit(UPilotServerStopOrigin.User, "", timeoutMs);

        internal bool StopServerAndWaitForExit(UPilotServerStopOrigin origin, string requestId = "", int timeoutMs = 3000)
        {
            StopServer(origin, requestId);
            var stoppedGeneration = _restartGeneration;
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (stoppedGeneration != _restartGeneration) return false;
                if (StoppedProcessesExited() && UPilotPortAllocator.IsPortAvailable(HttpPort) &&
                    UPilotPortAllocator.IsPortAvailable(WsPort))
                {
                    InvalidateStatusCache();
                    return true;
                }

                System.Threading.Thread.Sleep(50);
            }

            InvalidateStatusCache();
            return stoppedGeneration == _restartGeneration && StoppedProcessesExited() && UPilotPortAllocator.IsPortAvailable(HttpPort) &&
                   UPilotPortAllocator.IsPortAvailable(WsPort);
        }

        internal string[] GetUpdateOccupancyResourcePaths()
        {
            var paths = new List<string>();
            try
            {
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                var pythonEntry = Path.IsPathRooted(_pythonEntryPath)
                    ? _pythonEntryPath
                    : Path.GetFullPath(Path.Combine(projectRoot, _pythonEntryPath));
                if (!string.IsNullOrWhiteSpace(pythonEntry))
                    paths.Add(pythonEntry);
            }
            catch { }

            try
            {
                if (UPilotServerRuntimeService.Instance.IsStandaloneExeConfigured(out var exePath) &&
                    !string.IsNullOrWhiteSpace(exePath))
                    paths.Add(exePath);
            }
            catch { }

            var failedPath = UPilotServerRuntimeService.Instance.LastManagedInstallFailurePath;
            if (!string.IsNullOrWhiteSpace(failedPath))
                paths.Add(failedPath);
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private bool GetHealthForRecovery(int oldPid, string expectedProjectPath, long deadlineUtcMs)
        {
            var remaining = deadlineUtcMs > 0
                ? Math.Min(1500, deadlineUtcMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) : 1500;
            if (remaining <= 0) return false;
            // HTTP runs off the Editor synchronization context; no callback is allowed to restart the Server.
            var json = Task.Run(async () =>
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(remaining) };
                using var response = await client.GetAsync($"http://127.0.0.1:{HttpPort}/health").ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }).GetAwaiter().GetResult();
            return ParseIntFromJson(json, "server_pid") == oldPid &&
                   SameProjectPath(ParseStringFromJson(json, "project_path"), expectedProjectPath) &&
                   IsUPilotServerPayload(json);
        }

        public void RestartServer(Action afterStart = null)
        {
            _ = UPilotQuickStart.AutoRepairAsync(null, afterStart);
        }

        internal void RestartPreparedServer(Action afterStart = null, long maintenanceDeadlineUtcMs = 0, int expectedProcessId = 0)
        {
            if (UPilotServiceMaintenance.IsActive && !UPilotServiceMaintenance.IsExecuting) return;
            if (maintenanceDeadlineUtcMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= maintenanceDeadlineUtcMs)
                throw new ServiceMaintenanceException("SERVICE_RESTART_TIMEOUT", "Maintenance expired before stopping services.");
            var processRole = UPilotBridge.DetermineProcessRole();
            if (!UPilotBridge.IsMainEditorProcess(processRole))
            {
                var reason = $"MCP Server restart rejected for auxiliary Unity process role '{processRole}'.";
                Debug.LogWarning("[UPilotMcpServerManager] " + reason);
                UPilotStartupDiagnostics.RecordBlockingReason("server_auxiliary_process_role", reason);
                return;
            }

            if (UPilotUpdateService.Instance.IsServiceStartBlocked)
            {
                Debug.LogWarning("[UPilotMcpServerManager] " + UPilotUpdateService.ServiceStartBlockedMessage);
                return;
            }

            if (_restartPending || IsRestartObservationActive())
            {
                if (afterStart != null)
                    _afterRestartStarted += afterStart;
                Debug.Log("[UPilotMcpServerManager] MCP server restart is already active; restart request merged without replaying it.");
                return;
            }

            CancelPendingRestart(recordCancellation: false);
            var bridge = UPilotBridge.Instance;
            var bridgeStatus = bridge?.GetStatus() ?? default;
            var oldBridgeSessionId = bridgeStatus.SessionId ?? "";
            var bridgeWasStarted = bridgeStatus.IsStarted;
            var oldProcessId = expectedProcessId > 0 ? expectedProcessId :
                (IsTrackedProcessAlive() && _trackedProcessId.HasValue ? _trackedProcessId.Value : 0);
            if (expectedProcessId > 0 && oldProcessId != expectedProcessId)
                throw new ServiceMaintenanceException("SERVICE_RESTART_IDENTITY_CHANGED", "Server changed before restart.");
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            UPilotServerRestartRecord restart;
            try
            {
                restart = UPilotServerRestartDiagnostics.Begin(projectRoot, oldProcessId, oldBridgeSessionId, maintenanceDeadlineUtcMs);
            }
            catch (Exception ex)
            {
                Debug.LogError("[UPilotMcpServerManager] MCP restart was not started: " + ex.Message);
                return;
            }
            _restartGeneration = Math.Max(_restartGeneration + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            restart.restartGeneration = _restartGeneration;
            UPilotServerRestartDiagnostics.RecordGeneration(restart.operationId, _restartGeneration);
            _restartOperationId = restart.operationId;
            _restartOldBridgeSessionId = oldBridgeSessionId;
            _restartExpectedProjectPath = restart.projectPath;
            _restartVerificationDeadlineUtcMs = 0;
            _restartMaintenanceDeadlineUtcMs = maintenanceDeadlineUtcMs;
            _restartNextProbeAtUtcMs = 0;
            _restartHealthProbeRunning = false;

            PreparedProcessStop prepared = null;
            try
            {
                RunVerifiedStopSequence(() =>
                {
                    UPilotServerRestartDiagnostics.RecordPhase(_restartOperationId, "identity_probe");
                    prepared = PrepareCurrentProjectStop(expectedProcessId);
                    UPilotServerRestartDiagnostics.MarkGate(_restartOperationId, "old_identity", "passed",
                        prepared.Processes.Count == 0 ? "no_old_process" : "identity_verified",
                        prepared.Processes.Count == 0 ? "未发现旧进程，无需停止" : "已核实进程归属");
                    if (!bridgeWasStarted)
                        UPilotServerRestartDiagnostics.MarkGate(_restartOperationId, "bridge_stop", "passed",
                            "no_old_bridge", "旧 Bridge 未启动，无需停止");
                    if (oldProcessId == 0 && prepared.Processes.Count == 1)
                    {
                        oldProcessId = prepared.Processes[0].pid;
                        UPilotServerRestartDiagnostics.RecordOldProcessId(_restartOperationId, oldProcessId);
                    }
                    if (maintenanceDeadlineUtcMs > 0) UPilotServiceMaintenance.RequireContinuation();
                    UPilotServerRestartDiagnostics.RecordPhase(_restartOperationId, "stopping_old_services");
                }, bridgeWasStarted, () =>
                {
                    UPilotServerRestartDiagnostics.RecordStopProgress(_restartOperationId, bridgeAttempted: true);
                    bridge.Stop();
                    UPilotServerRestartDiagnostics.RecordStopProgress(_restartOperationId,
                        bridgeConfirmed: !bridge.GetStatus().IsStarted);
                }, onAttempt => StopPreparedProcesses(prepared, expectedProcessId, () =>
                {
                    onAttempt();
                    UPilotServerRestartDiagnostics.RecordStopProgress(_restartOperationId, serverAttempted: true);
                }), () =>
                {
                    if (oldProcessId <= 0) return false;
                    var ownership = ProbeMcpProcessOwnership(HttpPort, WsPort);
                    var health = GetHealthForRecovery(oldProcessId, _restartExpectedProjectPath, maintenanceDeadlineUtcMs);
                    using var original = Process.GetProcessById(oldProcessId);
                    return ownership.Ownership == McpProcessOwnership.CurrentUPilot &&
                        ownership.ProcessId == oldProcessId && health &&
                        prepared.Processes.Any(entry => entry.pid == oldProcessId &&
                            !original.HasExited && original.StartTime.ToUniversalTime().Ticks == entry.createdAtTicks);
                }, () => bridge.EnsureStarted(), (attempted, result, error) =>
                    UPilotServerRestartDiagnostics.RecordBridgeRestore(_restartOperationId, attempted, result, error));
                UPilotServerRestartDiagnostics.RecordStopProgress(_restartOperationId, exitConfirmed: StoppedProcessesExited());
                _afterRestartStarted += () =>
                {
                    if (maintenanceDeadlineUtcMs > 0) UPilotServiceMaintenance.RequireContinuation();
                    bridge.EnsureStarted();
                };
                if (afterStart != null) _afterRestartStarted += afterStart;
            }
            catch (Exception ex)
            {
                RecordRestartStartFailure((ex as ServiceMaintenanceException)?.Code ??
                    (ex is TimeoutException ? "process_identity_timeout" : "stop_failed"), ex.Message);
                _afterRestartStarted = null;
                FinishRestartObservation();
                return;
            }
            finally { prepared?.Dispose(); }
            InvalidateStatusCache();

            _restartPending = true;
            var waitingOperationId = _restartOperationId;
            var waitingGeneration = _restartGeneration;
            var deadline = EditorApplication.timeSinceStartup + 4d;
            _restartWaitCallback = () =>
            {
                if (waitingGeneration != _restartGeneration || waitingOperationId != _restartOperationId ||
                    !UPilotServerRestartDiagnostics.IsActive(waitingOperationId)) return;
                if (_restartMaintenanceDeadlineUtcMs > 0 &&
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= _restartMaintenanceDeadlineUtcMs)
                {
                    EndMaintenanceObservation(_restartOperationId);
                    return;
                }
                if (!_restartPending)
                {
                    CancelPendingRestart(recordCancellation: false);
                    return;
                }

                var portsAvailable = StoppedProcessesExited() && UPilotPortAllocator.IsPortAvailable(HttpPort) &&
                                     UPilotPortAllocator.IsPortAvailable(WsPort);
                if (portsAvailable)
                {
                    if (_restartMaintenanceDeadlineUtcMs > 0)
                    {
                        try { UPilotServiceMaintenance.RequireContinuation(); }
                        catch (ServiceMaintenanceException ex)
                        {
                            RecordRestartStartFailure(ex.Code, ex.Message);
                            CancelPendingRestart(recordCancellation: false);
                            return;
                        }
                    }
                    var callback = _restartWaitCallback;
                    if (callback != null)
                        EditorApplication.update -= callback;
                    _restartWaitCallback = null;
                    _restartPending = false;
                    InvalidateStatusCache();
                    UPilotServerRestartDiagnostics.RecordStopProgress(_restartOperationId, exitConfirmed: true);
                    UPilotServerRestartDiagnostics.RecordPortsReleased(waitingOperationId);
                    if (waitingGeneration != _restartGeneration || waitingOperationId != _restartOperationId ||
                        !UPilotServerRestartDiagnostics.IsActive(waitingOperationId)) return;
                    StartServer();
                    if (IsRestartObservationActive())
                        InvokeAfterRestartStarted();
                    BeginRestartObservation();
                    return;
                }

                if (_restartMaintenanceDeadlineUtcMs > 0 || EditorApplication.timeSinceStartup < deadline)
                    return;

                var timedOutCallback = _restartWaitCallback;
                if (timedOutCallback != null)
                    EditorApplication.update -= timedOutCallback;
                _restartWaitCallback = null;
                _restartPending = false;
                _afterRestartStarted = null;
                InvalidateStatusCache();
                var message =
                    $"MCP restart timed out waiting for ports HTTP={HttpPort}, WS={WsPort} to be released.";
                UPilotServerRestartDiagnostics.RecordFailure(
                    _restartOperationId,
                    "port_release_timeout",
                    message,
                    UPilotServerRestartDiagnostics.ReadServerLogTail(CurrentProjectLogPath),
                    "log/mcp-server.log",
                    "Inspect the process owning the configured ports before requesting one new restart.");
                FinishRestartObservation();
                Debug.LogError("[UPilotMcpServerManager] " + message);
            };
            EditorApplication.update += _restartWaitCallback;
        }

        // This seam exercises the exact preflight/stop/recovery ordering without killing test processes.
        internal static void RunVerifiedStopSequence(Action prepare, bool bridgeWasStarted,
            Action stopBridge, Action<Action> stopServer, Func<bool> originalServerSafe, Action restoreBridge,
            Action<bool, string, string> onBridgeRestore = null)
        {
            var bridgeAttempted = false;
            var serverAttempted = false;
            try
            {
                prepare();
                if (bridgeWasStarted)
                {
                    bridgeAttempted = true;
                    stopBridge();
                }
                stopServer(() => serverAttempted = true);
            }
            catch
            {
                if (bridgeAttempted && !serverAttempted)
                {
                    var restoreAttempted = false;
                    var restoreResult = "original_server_unverified";
                    var restoreError = "";
                    try
                    {
                        if (originalServerSafe())
                        {
                            restoreAttempted = true;
                            restoreBridge();
                            restoreResult = "requested_unverified";
                        }
                    }
                    catch (Exception recoveryError)
                    {
                        restoreResult = "failed";
                        restoreError = recoveryError.Message;
                        UnityEngine.Debug.LogWarning("[UPilotMcpServerManager] Bridge recovery unconfirmed: " + recoveryError.Message);
                    }
                    try { onBridgeRestore?.Invoke(restoreAttempted, restoreResult, restoreError); }
                    catch (Exception diagnosticError)
                    {
                        UnityEngine.Debug.LogWarning("[UPilotMcpServerManager] Bridge recovery diagnostic failed: " + diagnosticError.Message);
                    }
                }
                throw;
            }
        }

        private void CancelPendingRestart(bool recordCancellation,
            UPilotServerStopOrigin origin = UPilotServerStopOrigin.User, string requestId = "",
            string expectedOperationId = null, long expectedGeneration = 0,
            int targetProcessId = 0, long targetCreatedAtTicks = 0)
        {
            var operationId = _restartOperationId;
            if (expectedOperationId != null &&
                (operationId != expectedOperationId || _restartGeneration != expectedGeneration)) return;
            _restartPending = false;
            _afterRestartStarted = null;
            if (_restartWaitCallback != null)
            {
                EditorApplication.update -= _restartWaitCallback;
                _restartWaitCallback = null;
            }
            if (_restartObserveCallback != null)
            {
                EditorApplication.update -= _restartObserveCallback;
                _restartObserveCallback = null;
            }
            _restartHealthProbeRunning = false;
            if (recordCancellation && UPilotServerRestartDiagnostics.IsActive(operationId))
            {
                UPilotServerRestartDiagnostics.RecordStop(operationId, _restartGeneration, origin,
                    requestId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), targetProcessId, targetCreatedAtTicks);
            }
            ClearRestartObservationState();
        }

        private void InvokeAfterRestartStarted()
        {
            var callback = _afterRestartStarted;
            _afterRestartStarted = null;
            if (callback == null)
                return;

            try
            {
                callback.Invoke();
            }
            catch (Exception ex)
            {
                if (_restartMaintenanceDeadlineUtcMs > 0)
                    RecordRestartStartFailure((ex as ServiceMaintenanceException)?.Code ?? "SERVICE_RESTART_FAILED", ex.Message);
                Debug.LogError("[UPilotMcpServerManager] Restart callback failed: " + ex);
            }
        }

        private void TrackStartedProcess(Process process)
        {
            DisposeTrackedProcess();
            _trackedProcess = process;
            _trackedProcessId = process?.Id;
            _trackedProcessCreatedAtTicks = process?.StartTime.ToUniversalTime().Ticks ?? 0;
            if (process != null && UPilotServerRestartDiagnostics.IsActive(_restartOperationId))
                UPilotServerRestartDiagnostics.RecordProcessStarted(_restartOperationId, process.Id, _trackedProcessCreatedAtTicks);
        }

        private void RecordRestartStartFailure(string code, string message)
        {
            if (!UPilotServerRestartDiagnostics.IsActive(_restartOperationId)) return;
            UPilotServerRestartDiagnostics.RecordFailure(
                _restartOperationId,
                code,
                message,
                UPilotServerRestartDiagnostics.ReadServerLogTail(CurrentProjectLogPath),
                "log/mcp-server.log");
        }

        private int ResolveCurrentProjectProcessId()
        {
            if (IsTrackedProcessAlive() && _trackedProcessId.HasValue)
                return _trackedProcessId.Value;
            var processes = FindCurrentProjectMcpProcesses();
            return processes.Count == 1 ? processes[0].pid : 0;
        }

        private bool IsRestartObservationActive() =>
            !string.IsNullOrEmpty(_restartOperationId) &&
            UPilotServerRestartDiagnostics.IsActive(_restartOperationId);

        private void BeginRestartObservation()
        {
            if (!IsRestartObservationActive())
            {
                FinishRestartObservation();
                return;
            }
            if (!_trackedProcessId.HasValue || _trackedProcessId.Value <= 0)
            {
                RecordRestartStartFailure(
                    "server_process_identity_missing",
                    "The replacement MCP Server process identity was not established.");
                FinishRestartObservation();
                return;
            }

            var record = UPilotServerRestartDiagnostics.Current;
            _restartMaintenanceDeadlineUtcMs = record?.maintenanceDeadlineUtcMs ?? 0;
            _restartVerificationDeadlineUtcMs = _restartMaintenanceDeadlineUtcMs > 0
                ? _restartMaintenanceDeadlineUtcMs
                : (record?.newProcessStartedAtUtcMs > 0
                    ? record.newProcessStartedAtUtcMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) + RestartVerificationTimeoutMs;
            _restartNextProbeAtUtcMs = 0;
            if (_restartObserveCallback != null)
                EditorApplication.update -= _restartObserveCallback;
            _restartObserveCallback = ObserveRestart;
            EditorApplication.update += _restartObserveCallback;
        }

        private void ResumePersistedRestartObservation()
        {
            if (!UPilotServerRestartDiagnostics.TryGetActive(out var record)) return;
            _restartOperationId = record.operationId ?? "";
            _restartGeneration = Math.Max(_restartGeneration, record.restartGeneration);
            _restartOldBridgeSessionId = record.oldBridgeSessionId ?? "";
            _restartExpectedProjectPath = record.projectPath ?? "";
            if (record.newProcessId <= 0)
            {
                UPilotServerRestartDiagnostics.RecordFailure(
                    _restartOperationId,
                    "restart_recovery_required",
                    "A persisted restart intent has no established replacement process identity; start is not replayed.",
                    UPilotServerRestartDiagnostics.ReadServerLogTail(CurrentProjectLogPath),
                    "log/mcp-server.log",
                    "Inspect the prior restart evidence and request one explicit restart if still needed.");
                ClearRestartObservationState();
                return;
            }

            try
            {
                _trackedProcess = Process.GetProcessById(record.newProcessId);
                if (record.newProcessCreatedAtTicks <= 0 ||
                    _trackedProcess.StartTime.ToUniversalTime().Ticks != record.newProcessCreatedAtTicks ||
                    !IsCurrentProjectMcpCommandLine(GetProcessCommandLineForDiagnostics(record.newProcessId), HttpPort, WsPort))
                    throw new InvalidOperationException("持久化 Server 的创建时间或项目归属无法验证。");
                _trackedProcessId = record.newProcessId;
                _trackedProcessCreatedAtTicks = record.newProcessCreatedAtTicks;
            }
            catch (Exception ex)
            {
                UPilotServerRestartDiagnostics.RecordFailure(
                    _restartOperationId,
                    "server_process_missing_after_reload",
                    "The persisted replacement MCP Server process could not be reattached: " + ex.Message,
                    UPilotServerRestartDiagnostics.ReadServerLogTail(CurrentProjectLogPath),
                    "log/mcp-server.log");
                ClearRestartObservationState();
                return;
            }
            BeginRestartObservation();
        }

        private void ObserveRestart()
        {
            var operationId = _restartOperationId;
            if (!UPilotServerRestartDiagnostics.IsActive(operationId))
            {
                FinishRestartObservation();
                return;
            }

            var processId = _trackedProcessId ?? 0;
            try
            {
                if (_trackedProcess == null && processId > 0)
                    _trackedProcess = Process.GetProcessById(processId);
                if (_trackedProcess == null || _trackedProcess.HasExited)
                {
                    var exitCode = _trackedProcess?.ExitCode ?? -1;
                    UPilotServerRestartDiagnostics.RecordProcessExited(
                        operationId,
                        processId,
                        exitCode,
                        UPilotServerRestartDiagnostics.ReadServerLogTail(CurrentProjectLogPath),
                        "log/mcp-server.log");
                    FinishRestartObservation();
                    return;
                }
            }
            catch (Exception ex)
            {
                UPilotServerRestartDiagnostics.RecordFailure(
                    operationId,
                    "server_process_observation_failed",
                    ex.Message,
                    UPilotServerRestartDiagnostics.ReadServerLogTail(CurrentProjectLogPath),
                    "log/mcp-server.log");
                FinishRestartObservation();
                return;
            }

            var bridgeStatus = UPilotBridge.Instance?.GetStatus() ?? default;
            if (!string.IsNullOrWhiteSpace(bridgeStatus.AuthenticationError) &&
                bridgeStatus.AuthenticationFailureAtUtcMs >=
                (UPilotServerRestartDiagnostics.Current?.newProcessStartedAtUtcMs ?? long.MaxValue))
            {
                RecordRestartStartFailure("authentication_failed", bridgeStatus.AuthenticationError);
                FinishRestartObservation();
                return;
            }
            if (bridgeStatus.IsWsOpen && bridgeStatus.IsAuthenticated &&
                IsNewBridgeSession(_restartOldBridgeSessionId, bridgeStatus.SessionId))
            {
                UPilotServerRestartDiagnostics.RecordBridgeVerified(operationId, bridgeStatus.SessionId);
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now >= _restartVerificationDeadlineUtcMs)
            {
                UPilotServerRestartDiagnostics.RecordBridgeSnapshot(operationId, bridgeStatus);
                var pending = UPilotServerRestartDiagnostics.Current;
                var waitSeconds = pending == null ? 0 : Math.Max(0, (now - pending.requestedAtUtcMs) / 1000);
                var nextGate = pending == null ? "unknown" : UPilotServerRestartDiagnostics.FirstUnpassedGate(pending);
                var passed = pending == null ? "无" : UPilotRestartDiagnosticView.LastPassed(pending);
                if (nextGate == "bridge_session")
                    UPilotServerRestartDiagnostics.MarkGate(operationId, nextGate, "failed",
                        bridgeStatus.IsWsOpen && bridgeStatus.IsAuthenticated
                            ? "bridge_session_mismatch" : "bridge_unavailable",
                        "未建立经过认证且属于本次重启的新 Bridge 会话");
                else if (nextGate == "readonly")
                    UPilotServerRestartDiagnostics.MarkGate(operationId, nextGate, "failed",
                        "readonly_timeout", "实际只读往返未在验证期限内通过");
                UPilotServerRestartDiagnostics.RecordFailure(
                    operationId,
                    _restartMaintenanceDeadlineUtcMs > 0 ? "SERVICE_RESTART_TIMEOUT" : "restart_verification_timeout",
                    $"重启验证等待 {waitSeconds} 秒；status probe {pending?.statusProbeCount ?? 0} 次；" +
                    $"最后阶段 {pending?.statusProbeFailureStage ?? "未知"}；最后通过 {passed}；首个未通过验证门 {UPilotRestartDiagnosticView.Label(nextGate)}。" +
                    (string.IsNullOrEmpty(pending?.statusProbeError) ? "" : " 最后错误：" + pending.statusProbeError),
                    UPilotServerRestartDiagnostics.ReadServerLogTail(CurrentProjectLogPath),
                    "log/mcp-server.log",
                    "Inspect health, exact projectPath, and Bridge session diagnostics before requesting one new restart.");
                FinishRestartObservation();
                return;
            }

            if (!_restartHealthProbeRunning && now >= _restartNextProbeAtUtcMs)
            {
                _restartHealthProbeRunning = true;
                _restartNextProbeAtUtcMs = now + RestartProbeIntervalMs;
                _ = ProbeRestartHealthAsync(operationId, processId);
            }
        }

        private async Task ProbeRestartHealthAsync(string operationId, int processId)
        {
            UPilotServerRestartDiagnostics.BeginStatusProbe(operationId);
            var probeEnded = false;
            var readOnlyStarted = false;
            try
            {
                var status = await GetFreshStatusAsync(_restartMaintenanceDeadlineUtcMs);
                if (UPilotServerRestartDiagnostics.IsActive(operationId))
                {
                    var outcome = ClassifyRestartProbe(status);
                    if (outcome == "success" && status.HealthServerProcessId != processId)
                        outcome = "process_identity_mismatch";
                    else if (outcome == "success" && !SameProjectPath(status.HealthProjectPath, _restartExpectedProjectPath))
                        outcome = "project_identity_mismatch";
                    var probeError = outcome == "project_identity_mismatch"
                        ? "期望项目 " + _restartExpectedProjectPath + "；实际项目 " + status.HealthProjectPath
                        : outcome == "process_identity_mismatch"
                            ? "期望 PID " + processId + "；health PID " + status.HealthServerProcessId
                            : status.ErrorMessage;
                    UPilotServerRestartDiagnostics.EndStatusProbe(operationId, outcome,
                        status.StatusFailureStage, status.StatusCancellationReason, probeError);
                    probeEnded = true;
                    if (outcome == "lifecycle_canceled")
                    {
                        UPilotServerRestartDiagnostics.RecordCanceled(operationId,
                            status.StatusCancellationReason ?? "Editor 生命周期操作取消了状态探测");
                        return;
                    }
                    UPilotServerRestartDiagnostics.RecordBridgeSnapshot(operationId, UPilotBridge.Instance.GetStatus());
                    if (status.HealthEndpointResponded)
                    {
                        UPilotServerRestartDiagnostics.RecordServerBridgeDiagnostics(operationId, status.Health);
                        UPilotServerRestartDiagnostics.RecordDeploymentIdentity(operationId, status.Health);
                    }
                }
                if (!string.Equals(operationId, _restartOperationId, StringComparison.Ordinal) ||
                    !UPilotServerRestartDiagnostics.IsActive(operationId))
                    return;
                if (IsVerifiedRestartHealth(status, processId, _restartExpectedProjectPath))
                {
                    UPilotServerRestartDiagnostics.RecordHealthVerified(operationId, processId, status.HealthProjectPath);
                    var bridge = UPilotBridge.Instance.GetStatus();
                    var issues = UPilotDeploymentDiagnostics.Observe(bridge, status);
                    if (issues.Any(issue => issue.Code != "authentication" && issue.Code != "timeout"))
                    {
                        RecordRestartStartFailure("deployment_mismatch", string.Join("\n", issues.Select(issue => issue.Message)));
                        return;
                    }
                    if (issues.Any(issue => issue.Code == "timeout") ||
                        !bridge.IsWsOpen || !bridge.IsAuthenticated ||
                        !IsNewBridgeSession(_restartOldBridgeSessionId, bridge.SessionId))
                        return;
                    UPilotServerRestartDiagnostics.RecordDeploymentVerified(operationId);
                    UPilotServerRestartDiagnostics.MarkGate(operationId, "readonly", "running");
                    readOnlyStarted = true;
                    await VerifyReadOnlyRoundTripAsync(status, bridge.SessionId, _restartMaintenanceDeadlineUtcMs);
                    if (!string.Equals(operationId, _restartOperationId, StringComparison.Ordinal) ||
                        !UPilotServerRestartDiagnostics.IsActive(operationId)) return;
                    var verifiedBridge = UPilotBridge.Instance.GetStatus();
                    if (!verifiedBridge.IsWsOpen || !verifiedBridge.IsAuthenticated ||
                        verifiedBridge.SessionId != bridge.SessionId) return;
                    UPilotServerRestartDiagnostics.RecordReadOnlyVerified(operationId);
                }
            }
            catch (Exception ex)
            {
                if (string.Equals(operationId, _restartOperationId, StringComparison.Ordinal))
                {
                    if (!probeEnded && UPilotServerRestartDiagnostics.IsActive(operationId))
                        UPilotServerRestartDiagnostics.EndStatusProbe(operationId,
                            ex is TimeoutException || ex is TaskCanceledException ? "request_timeout" : "unknown",
                            "status_collection", "", ex.Message);
                    else if (readOnlyStarted && UPilotServerRestartDiagnostics.IsActive(operationId))
                        UPilotServerRestartDiagnostics.MarkGate(operationId, "readonly", "failed",
                            ex is TimeoutException || ex is TaskCanceledException ? "readonly_timeout" : "readonly_mismatch", ex.Message);
                    // Transient probe timeouts may recover within the original maintenance budget.
                    if (_restartMaintenanceDeadlineUtcMs <= 0 || ex is InvalidOperationException)
                        RecordRestartStartFailure(readOnlyStarted
                            ? (ex is TimeoutException || ex is TaskCanceledException ? "readonly_timeout" : "readonly_mismatch")
                            : "restart_validation_failed", ex.Message);
                }
            }
            finally
            {
                if (string.Equals(operationId, _restartOperationId, StringComparison.Ordinal))
                {
                    _restartHealthProbeRunning = false;
                    if (!UPilotServerRestartDiagnostics.IsActive(operationId))
                        FinishRestartObservation();
                }
            }
        }

        internal static string ClassifyRestartProbe(McpServerStatus status)
        {
            if (status.StatusCancellationReason == "domain_reload" ||
                status.StatusCancellationReason == "lifecycle_stop" ||
                status.StatusCancellationReason == "editor_exit" ||
                status.StatusCancellationReason == "explicit_stop") return "lifecycle_canceled";
            if (status.StatusCancellationReason == "timeout") return "request_timeout";
            if (!string.IsNullOrEmpty(status.ErrorMessage))
            {
                if (status.StatusFailureStage == "health_query")
                {
                    var error = status.ErrorMessage;
                    if (error.Contains("/health HTTP ")) return "http_status_failure";
                    if (error.Contains("HttpRequestException") || error.Contains("SocketException")) return "connect_failure";
                    if (error.Contains("TaskCanceledException") || error.Contains("A task was canceled")) return "request_timeout";
                    return "malformed_response";
                }
                if (status.StatusFailureStage == "port_probe") return "connect_failure";
                if (status.StatusFailureStage == "process_identity") return "process_identity_timeout";
                return "unknown";
            }
            return status.HealthEndpointResponded
                ? (status.HealthIdentifiesUPilot ? "success" : "malformed_response")
                : "connect_failure";
        }

        internal async Task VerifyReadOnlyRoundTripAsync(McpServerStatus status, string bridgeSessionId, long deadlineUtcMs = 0)
        {
            var nonce = Guid.NewGuid().ToString("N");
            var remaining = deadlineUtcMs <= 0 ? 7000 : Math.Min(7000, deadlineUtcMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (remaining <= 0) throw new TimeoutException("Maintenance deadline elapsed.");
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(remaining) };
            using var response = await client.GetAsync(
                $"http://127.0.0.1:{HttpPort}/health?probe=bridge&session={Uri.EscapeDataString(bridgeSessionId)}&nonce={nonce}");
            response.EnsureSuccessStatusCode();
            var health = JsonUtility.FromJson<UPilotServerHealth>(await response.Content.ReadAsStringAsync());
            if (health == null || !health.bridge_probe_ok || health.bridge_probe_nonce != nonce ||
                health.bridge_session_id != bridgeSessionId || health.server_pid != status.ProcessId ||
                health.server_instance_id != status.Health?.server_instance_id ||
                !UPilotDeploymentDiagnostics.SamePath(health.configured_project_path, UPilotProjectConfig.ProjectRoot))
                throw new InvalidOperationException("实际只读调用未通过：" + (health?.bridge_probe_error ?? "实例、项目或会话证据缺失"));
        }

        internal static bool IsVerifiedRestartHealth(
            McpServerStatus status,
            int expectedProcessId,
            string expectedProjectPath)
        {
            return expectedProcessId > 0 &&
                   IsVerifiedStartupHealth(status) &&
                   status.ProcessId == expectedProcessId &&
                   status.HealthServerProcessId == expectedProcessId &&
                   SameProjectPath(status.HealthProjectPath, expectedProjectPath);
        }

        internal static bool IsNewBridgeSession(string oldSessionId, string newSessionId) =>
            !string.IsNullOrWhiteSpace(newSessionId) &&
            !string.Equals(oldSessionId ?? "", newSessionId, StringComparison.Ordinal);

        private static bool SameProjectPath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void FinishRestartObservation()
        {
            if (_restartObserveCallback != null)
            {
                EditorApplication.update -= _restartObserveCallback;
                _restartObserveCallback = null;
            }
            ClearRestartObservationState();
        }

        internal void EndMaintenanceObservation(string restartId)
        {
            if (string.IsNullOrEmpty(restartId) || restartId != _restartOperationId ||
                _restartMaintenanceDeadlineUtcMs <= 0) return;
            if (_restartWaitCallback != null) EditorApplication.update -= _restartWaitCallback;
            _restartWaitCallback = null;
            _restartPending = false;
            _afterRestartStarted = null;
            UPilotServerRestartDiagnostics.RecordFailure(restartId, "SERVICE_RESTART_TIMEOUT",
                "Maintenance observation ended; no further stop/start was scheduled.", "", "",
                "Inspect current service identity and original maintenance diagnostics.");
            FinishRestartObservation();
        }

        private void ClearRestartObservationState()
        {
            _restartOperationId = "";
            _restartOldBridgeSessionId = "";
            _restartExpectedProjectPath = "";
            _restartVerificationDeadlineUtcMs = 0;
            _restartMaintenanceDeadlineUtcMs = 0;
            _restartNextProbeAtUtcMs = 0;
            _restartHealthProbeRunning = false;
        }

        private void DisposeTrackedProcess()
        {
            try { _trackedProcess?.Dispose(); }
            catch { }
            _trackedProcess = null;
        }

        private bool StoppedProcessesExited()
        {
            for (var i = _stoppingProcesses.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (!_stoppingProcesses[i].HasExited) return false;
                }
                catch { return false; }
                _stoppingProcesses[i].Dispose();
                _stoppingProcesses.RemoveAt(i);
            }
            return true;
        }

        // ── Port & Process Helpers ─────────────────────────────────────────

        private static async Task<bool> IsPortListeningAsync(string host, int port, int timeoutMs = 300)
        {
            try
            {
                // Run synchronous Connect on thread pool to avoid any potential
                // mono-runtime quirks with TcpClient.ConnectAsync on the main thread.
                using var client = new TcpClient();
                var connectTask = Task.Run(() =>
                {
                    try { client.Connect(host, port); return true; }
                    catch { return false; }
                });
                var timeoutTask = Task.Delay(timeoutMs);
                if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
                    return false;
                return connectTask.Result;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPortListening(string host, int port)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect(host, port);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private McpProcessProbe ProbeMcpProcessOwnership(int httpPort, int wsPort)
        {
            var portsByPid = SafeGetListeningPortsByPid(out var ownerQuerySucceeded);
            if (!ownerQuerySucceeded)
            {
                return new McpProcessProbe(
                    McpProcessOwnership.Unknown,
                    null,
                    null,
                    "端口归属查询暂不可用");
            }

            var candidatePids = new HashSet<int>();
            var hasHttpOwner = false;
            var hasWsOwner = false;
            foreach (var entry in portsByPid)
            {
                if (entry.Value.Contains(httpPort))
                {
                    hasHttpOwner = true;
                    candidatePids.Add(entry.Key);
                }
                if (entry.Value.Contains(wsPort))
                {
                    hasWsOwner = true;
                    candidatePids.Add(entry.Key);
                }
            }

            if (candidatePids.Count == 0)
            {
                return new McpProcessProbe(
                    McpProcessOwnership.Unknown,
                    null,
                    null,
                    "监听端口尚未映射到进程");
            }
            if (candidatePids.Count > 1)
                return new McpProcessProbe(McpProcessOwnership.Foreign, null, "",
                    "HTTP 与 Bridge 端口属于不同进程：" + string.Join(", ", candidatePids));

            int? firstPid = null;
            string firstCommandLine = null;
            var allCommandLinesReadable = true;
            foreach (var pid in candidatePids)
            {
                var commandLine = GetProcessCommandLineForDiagnostics(pid);
                if (!firstPid.HasValue)
                {
                    firstPid = pid;
                    firstCommandLine = commandLine;
                }

                if (!IsUsableCommandLine(commandLine))
                {
                    allCommandLinesReadable = false;
                    continue;
                }

                if (IsCurrentProjectMcpCommandLine(commandLine, httpPort, wsPort))
                {
                    return new McpProcessProbe(
                        McpProcessOwnership.CurrentUPilot,
                        pid,
                        commandLine,
                        "监听端口与 UPilot 启动参数匹配");
                }
            }

            if (hasHttpOwner && hasWsOwner && allCommandLinesReadable)
            {
                return new McpProcessProbe(
                    McpProcessOwnership.Foreign,
                    firstPid,
                    firstCommandLine,
                    "监听端口属于非当前 UPilot 进程");
            }

            return new McpProcessProbe(
                McpProcessOwnership.Unknown,
                firstPid,
                firstCommandLine,
                allCommandLinesReadable ? "端口进程证据尚不完整" : "进程命令行暂不可读");
        }

        private (int? pid, string cmdLine) FindMcpProcessByPorts()
        {
            var processes = FindCurrentProjectMcpProcesses();
            if (processes.Count > 0)
                return (processes[0].pid, processes[0].cmdLine);
            return (null, null);
        }

        private List<(int pid, string cmdLine, long createdAtTicks)> FindCurrentProjectMcpProcesses() =>
            FindCurrentProjectMcpProcesses(out _, out _);

        private List<(int pid, string cmdLine, long createdAtTicks)> FindCurrentProjectMcpProcesses(
            out Dictionary<int, List<int>> portsByPid, out bool portQuerySucceeded, bool stopTargetsOnly = false)
        {
            var remainingMaintenanceMs = _restartMaintenanceDeadlineUtcMs > 0
                ? _restartMaintenanceDeadlineUtcMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                : 8000;
            if (remainingMaintenanceMs <= 0) throw new TimeoutException("Maintenance deadline elapsed before process identity query.");
            var probeBudgetMs = (int)Math.Min(8000, remainingMaintenanceMs);
            const int maxCandidates = 128;
            var timer = Stopwatch.StartNew();
            portsByPid = new Dictionary<int, List<int>>();
            portQuerySucceeded = false;
            var candidatePids = new HashSet<int>();
            long portQueryMs = 0;
            long candidateMs = 0;
            int verified = 0;
            int? queryExitCode = null;
            long queryMs = 0;
            string queryFailure = "";
            try
            {
                portsByPid = SafeGetListeningPortsByPid(out portQuerySucceeded);
                portQueryMs = timer.ElapsedMilliseconds;
                if (_trackedProcessId.HasValue) candidatePids.Add(_trackedProcessId.Value);
                foreach (var entry in portsByPid)
                    if (entry.Value.Contains(HttpPort) || entry.Value.Contains(WsPort)) candidatePids.Add(entry.Key);
                if (!stopTargetsOnly)
                {
                    AddProcessIdsByName(candidatePids, "python");
                    AddProcessIdsByName(candidatePids, "python3");
                    AddProcessIdsByName(candidatePids, "py");
                    foreach (var process in Process.GetProcesses())
                    {
                        try
                        {
                            if (process.ProcessName.IndexOf("upilot", StringComparison.OrdinalIgnoreCase) >= 0)
                                candidatePids.Add(process.Id);
                        }
                        catch { }
                        finally { process.Dispose(); }
                    }
                }
                candidateMs = timer.ElapsedMilliseconds - portQueryMs;
                if (!portQuerySucceeded) throw new InvalidOperationException("监听端口归属查询失败。");
                if (candidatePids.Count > maxCandidates)
                    throw new InvalidOperationException("进程归属候选数超过安全上限。");
                if (timer.ElapsedMilliseconds >= probeBudgetMs)
                    throw new TimeoutException("进程归属识别超过 8 秒，未授权停止未核实的进程。");
                var result = new List<(int pid, string cmdLine, long createdAtTicks)>();
                if (candidatePids.Count == 0) return result;
                var before = new Dictionary<int, long>();
                foreach (var pid in candidatePids)
                {
                    if (timer.ElapsedMilliseconds >= probeBudgetMs)
                        throw new TimeoutException("进程归属识别超过 8 秒，未授权停止未核实的进程。");
                    try { using var process = Process.GetProcessById(pid); before[pid] = process.StartTime.ToUniversalTime().Ticks; }
                    catch (ArgumentException) { /* An already exited candidate cannot be stopped. */ }
                }
                if (before.Count == 0) return result;
                var selected = ResolveVerifiedCandidates(before, ids =>
                {
                    var queryStart = timer.ElapsedMilliseconds;
                    try { return QueryCandidateCommandLines(ids, timer, probeBudgetMs, out queryExitCode); }
                    finally { queryMs = timer.ElapsedMilliseconds - queryStart; }
                }, pid =>
                {
                    using var process = Process.GetProcessById(pid);
                    if (process.HasExited) throw new InvalidOperationException("候选进程已退出。");
                    return process.StartTime.ToUniversalTime().Ticks;
                }, HttpPort, WsPort, () => timer.ElapsedMilliseconds >= probeBudgetMs, out verified);
                result.AddRange(selected);
                return result;
            }
            catch (Exception ex) { queryFailure = ex is TimeoutException ? "timeout" : ex.GetType().Name; throw; }
            finally
            {
                if (UPilotServerRestartDiagnostics.IsActive(_restartOperationId))
                    UPilotServerRestartDiagnostics.RecordIdentityProbe(_restartOperationId, timer.ElapsedMilliseconds,
                        candidateMs, portQueryMs, queryMs, candidatePids.Count, verified, queryExitCode, queryFailure);
            }
        }

        // The query and process clock are injectable only inside this bounded internal seam.
        // No test path invokes Kill, netstat, wmic, or Bridge.Stop.
        internal static List<(int pid, string cmdLine, long createdAtTicks)> ResolveVerifiedCandidates(
            Dictionary<int, long> before, Func<IEnumerable<int>, Dictionary<int, string>> query,
            Func<int, long> createdAtTicks, int httpPort, int wsPort,
            Func<bool> budgetExpired, out int verified)
        {
            verified = 0;
            var commands = query(before.Keys);
            var selected = new List<(int pid, string cmdLine, long createdAtTicks)>();
            foreach (var entry in before)
            {
                if (budgetExpired()) throw new TimeoutException("进程归属识别超过 8 秒，未授权停止未核实的进程。");
                if (!commands.TryGetValue(entry.Key, out var cmdLine) || !IsUsableCommandLine(cmdLine))
                    throw new InvalidOperationException("候选进程命令行缺失或不可读，停止操作未获授权。");
                long after;
                try { after = createdAtTicks(entry.Key); }
                catch (ArgumentException) { throw new InvalidOperationException("候选进程身份在核验期间发生变化。"); }
                if (after != entry.Value)
                    throw new InvalidOperationException("候选进程身份在核验期间发生变化。");
                verified++;
                if (IsCurrentProjectMcpCommandLine(cmdLine, httpPort, wsPort))
                    selected.Add((entry.Key, cmdLine, entry.Value));
            }
            return selected;
        }

        private const int MaxCandidateQueryOutputChars = 1024 * 1024;

        internal static Dictionary<int, string> ParseCandidateCommandLines(string output, ICollection<int> requested)
        {
            if (output == null || output.Length > MaxCandidateQueryOutputChars)
                throw new InvalidOperationException("进程命令行批量查询输出缺失或超过安全上限。");
            var found = new Dictionary<int, string>();
            string commandLine = null;
            int? pid = null;
            void Finish()
            {
                if (pid.HasValue)
                {
                    if (!requested.Contains(pid.Value) || commandLine == null || found.ContainsKey(pid.Value))
                        throw new FormatException("进程命令行批量查询返回无效记录。");
                    found.Add(pid.Value, commandLine);
                }
                else if (commandLine != null) throw new FormatException("进程命令行记录缺少 PID。");
                pid = null;
                commandLine = null;
            }
            foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (string.IsNullOrWhiteSpace(line)) { Finish(); continue; }
                var index = line.IndexOf('=');
                if (index <= 0) throw new FormatException("进程命令行批量查询格式无效。");
                var key = line.Substring(0, index).Trim().TrimStart('\ufeff');
                var value = line.Substring(index + 1).Trim();
                if (key.Equals("ProcessId", StringComparison.OrdinalIgnoreCase))
                {
                    if (pid.HasValue || !int.TryParse(value, out var parsed) || parsed <= 0)
                        throw new FormatException("进程 PID 字段无效。");
                    pid = parsed;
                }
                else if (key.Equals("CommandLine", StringComparison.OrdinalIgnoreCase))
                {
                    if (commandLine != null) throw new FormatException("进程命令行字段重复。");
                    commandLine = value;
                }
                else throw new FormatException("进程命令行批量查询存在未知字段。");
            }
            Finish();
            return found;
        }

        private static Dictionary<int, string> QueryCandidateCommandLines(IEnumerable<int> pids,
            Stopwatch timer, int budgetMs, out int? exitCode)
        {
            exitCode = null;
#if UNITY_EDITOR_WIN
            var ids = pids.OrderBy(id => id).ToArray();
            var filter = string.Join(" or ", ids.Select(id => "ProcessId=" + id));
            var psi = new ProcessStartInfo
            {
                FileName = "wmic",
                Arguments = "process where \"(" + filter + ")\" get ProcessId,CommandLine /format:list",
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动进程命令行查询。");
            try
            {
                var output = new StringBuilder();
                var buffer = new char[2048];
                while (true)
                {
                    var remaining = budgetMs - (int)timer.ElapsedMilliseconds;
                    if (remaining <= 0) throw new TimeoutException("进程归属识别超过 8 秒，未授权停止未核实的进程。");
                    var read = process.StandardOutput.ReadAsync(buffer, 0, buffer.Length);
                    if (!read.Wait(remaining)) throw new TimeoutException("进程命令行批量查询超时。");
                    if (read.Result == 0) break;
                    output.Append(buffer, 0, read.Result);
                    if (output.Length > MaxCandidateQueryOutputChars) throw new InvalidOperationException("进程命令行批量查询输出超过安全上限。");
                }
                var waitMs = budgetMs - (int)timer.ElapsedMilliseconds;
                if (waitMs <= 0 || !process.WaitForExit(waitMs)) throw new TimeoutException("进程命令行批量查询超时。");
                exitCode = process.ExitCode;
                if (exitCode != 0) throw new InvalidOperationException("进程命令行批量查询失败，退出码=" + exitCode);
                return ParseCandidateCommandLines(output.ToString(), ids);
            }
            catch
            {
                if (!process.HasExited) { try { process.Kill(); } catch { } }
                throw;
            }
#else
            throw new PlatformNotSupportedException("当前平台未实现进程归属批量查询。");
#endif
        }

        private static void AddProcessIdsByName(HashSet<int> target, string processName)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    target.Add(process.Id);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private bool IsTrackedProcessAlive()
        {
            if (!_trackedProcessId.HasValue)
                return false;

            try
            {
                using var process = Process.GetProcessById(_trackedProcessId.Value);
                if (!process.HasExited && _trackedProcessCreatedAtTicks > 0 &&
                    process.StartTime.ToUniversalTime().Ticks == _trackedProcessCreatedAtTicks)
                    return true;
            }
            catch
            {
            }

            _trackedProcessId = null;
            DisposeTrackedProcess();
            return false;
        }

        private static bool IsCurrentProjectMcpCommandLine(string cmdLine, int httpPort, int wsPort)
        {
            return IsUPilotMcpLike(cmdLine) &&
                   HasCommandLinePort(cmdLine, "--http-port", httpPort) &&
                   HasCommandLinePort(cmdLine, "--port", wsPort) &&
                   HasCurrentProjectLogPath(cmdLine);
        }

        private static bool HasCurrentProjectLogPath(string cmdLine)
        {
            var match = Regex.Match(cmdLine,
                "(?:^|\\s)--log-file(?:\\s+|=)(?:\"([^\"]+)\"|(\\S+))",
                RegexOptions.IgnoreCase);
            if (!match.Success) return false;
            var path = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (!Path.IsPathRooted(path)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(path), Path.GetFullPath(CurrentProjectLogPath),
                    Path.DirectorySeparatorChar == '\\'
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool HasCommandLinePort(string cmdLine, string argument, int port)
        {
            if (string.IsNullOrWhiteSpace(cmdLine))
                return false;

            var pattern = $@"(?:^|\s){Regex.Escape(argument)}(?:\s+|=){port}(?=\s|$)";
            return Regex.IsMatch(cmdLine, pattern, RegexOptions.IgnoreCase);
        }

        private static bool IsUPilotMcpLike(string cmdLine)
        {
            if (string.IsNullOrWhiteSpace(cmdLine)) return false;
            return cmdLine.IndexOf("run_upilot_mcp.py", StringComparison.OrdinalIgnoreCase) >= 0
                || cmdLine.IndexOf("upilot-mcp", StringComparison.OrdinalIgnoreCase) >= 0
                || cmdLine.IndexOf("upilot_mcp", StringComparison.OrdinalIgnoreCase) >= 0
                || cmdLine.IndexOf("upilot-mcp-server", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsUsableCommandLine(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                return false;

            return commandLine != "(读取命令行失败)" &&
                   commandLine != "(空)" &&
                   commandLine != "(当前平台未实现命令行读取)";
        }

        private static Dictionary<int, List<int>> SafeGetListeningPortsByPid(out bool success)
        {
            var result = new Dictionary<int, List<int>>();
            success = false;

#if UNITY_EDITOR_WIN
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = "-ano -p tcp",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = Process.Start(psi);
                if (proc == null) return result;

                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(2000) || !stdout.Wait(100))
                {
                    try { proc.Kill(); } catch { }
                    return result;
                }
                string output = stdout.Result;

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

                    var state = parts[3];
                    if (!state.Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!int.TryParse(parts[4], out var pid) || pid <= 0)
                        continue;

                    int port = SafeParsePortFromEndpoint(parts[1]);
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

        internal static string GetProcessCommandLineForDiagnostics(int pid)
        {
#if UNITY_EDITOR_WIN
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "wmic",
                    Arguments = $"process where ProcessId={pid} get CommandLine /value",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = Process.Start(psi);
                if (proc == null) return "(读取命令行失败)";
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(1000) || !stdout.Wait(100))
                {
                    try { proc.Kill(); } catch { }
                    return "(读取命令行失败)";
                }
                string output = stdout.Result;

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
    }
}
