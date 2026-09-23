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
        public long LastSuccessfulStatusAtUtcMs;
        internal UPilotServerHealth Health;
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
        private string _restartOldBridgeSessionId = "";
        private string _restartExpectedProjectPath = "";
        private long _restartVerificationDeadlineUtcMs;
        private long _restartMaintenanceDeadlineUtcMs;
        internal bool IsServiceTransitionActive => _restartPending || _startInProgress || IsRestartObservationActive();
        private long _restartNextProbeAtUtcMs;
        private bool _restartHealthProbeRunning;

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
            lock (_statusLock) { return _cachedStatus; }
        }

        public void InvalidateStatusCache()
        {
            Interlocked.Increment(ref _statusGeneration);
            // A cache refresh must not erase an error or restart its deadline.
            _lastRefreshMs = 0;
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
            return await task;
        }

        private void RequestBackgroundStatusRefresh()
        {
            var httpPort = HttpPort;
            var wsPort = WsPort;
            lock (_statusLock)
            {
                if (_statusRefreshTask != null && !_statusRefreshTask.IsCompleted) return;
                var generation = Volatile.Read(ref _statusGeneration);
                _statusRefreshTask = RunBoundedStatusRefreshAsync(httpPort, wsPort, generation);
            }
        }

        private async Task<McpServerStatus> RunBoundedStatusRefreshAsync(int httpPort, int wsPort, int generation)
        {
            var work = Task.Run(() => RefreshStatusAsync(httpPort, wsPort, generation));
            if (await Task.WhenAny(work, Task.Delay(30000)).ConfigureAwait(false) == work)
                return await work.ConfigureAwait(false);
            lock (_statusLock)
            {
                if (generation == Volatile.Read(ref _statusGeneration))
                {
                    Interlocked.Increment(ref _statusGeneration);
                    _cachedStatus.ErrorMessage = "状态获取超时（30 秒）：进程识别、HTTP 查询或状态汇总未完成。";
                    _cachedStatus.StatusFailureStage = "status_collection";
                    _cachedStatus.StatusQueryCompleted = true;
                    _lastRefreshMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
                return _cachedStatus;
            }
        }

        private async Task<McpServerStatus> RefreshStatusAsync(int httpPort, int wsPort, int generation)
        {
            var status = new McpServerStatus();
            try
            {
                var httpTask = IsPortListeningAsync("127.0.0.1", httpPort);
                var wsTask = IsPortListeningAsync("127.0.0.1", wsPort);
                status.HttpPortListening = await httpTask;
                status.WsPortListening = await wsTask;
                status.IsRunning = status.HttpPortListening || status.WsPortListening;

                if (status.IsRunning)
                {
                    var process = ProbeMcpProcessOwnership(httpPort, wsPort);
                    status.ProcessOwnership = process.Ownership;
                    status.ProcessId = process.ProcessId;
                    status.ProcessCommandLine = process.CommandLine;
                    status.ProcessOwnershipEvidence = process.Evidence;

                    var stats = await FetchServerStatsAsync(httpPort);
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
                    status.StatusFailureStage = stats.Failure;
                    if (!stats.HealthEndpointResponded)
                        status.ErrorMessage = "状态获取失败：" + stats.Failure;
                }
            }
            catch (Exception ex)
            {
                status.ErrorMessage = ex.Message;
                Debug.LogError($"[UPilotMcpServerManager] Status refresh failed: {ex.Message}");
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

        private async Task<ServerStatsProbe> FetchServerStatsAsync(int httpPort)
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
                using var response = await _httpClient.GetAsync(url);
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
            catch
            {
            }

            try
            {
                var url = $"http://127.0.0.1:{httpPort}/health";
                using var response = await _httpClient.GetAsync(url);
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
            catch (Exception ex)
            {
                failure = "/health: " + ex.Message;
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

            if (_startInProgress || IsTrackedProcessAlive() || FindCurrentProjectMcpProcesses().Count > 0)
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

        public void StopServer()
        {
            if (UPilotServiceMaintenance.IsActive && !UPilotServiceMaintenance.IsExecuting) return;
            CancelPendingRestart(recordCancellation: true);
            StopCurrentProjectProcesses();
        }

        private void StopCurrentProjectProcesses(int expectedProcessId = 0)
        {
            Interlocked.Increment(ref _startAttemptGeneration);
            _startInProgress = false;
            var ownership = ProbeMcpProcessOwnership(HttpPort, WsPort);
            var portsBusy = !UPilotPortAllocator.IsPortAvailable(HttpPort) ||
                            !UPilotPortAllocator.IsPortAvailable(WsPort);
            if (portsBusy && ownership.Ownership != McpProcessOwnership.CurrentUPilot)
                throw new InvalidOperationException("无法安全停止 Server：端口进程不属于当前项目或身份未知；" +
                    $"PID={ownership.ProcessId}；{ownership.Evidence}");
            var processes = FindCurrentProjectMcpProcesses();
            if (expectedProcessId > 0 && (processes.Count != 1 || processes[0].pid != expectedProcessId))
                throw new ServiceMaintenanceException("SERVICE_RESTART_IDENTITY_CHANGED", "Expected Server process changed before stop.");
            if (processes.Count == 0)
            {
                _trackedProcessId = null;
                DisposeTrackedProcess();
                Debug.LogWarning("[UPilotMcpServerManager] No MCP server process found for the current project ports.");
                return;
            }

            foreach (var process in processes)
            {
                try
                {
                    var proc = Process.GetProcessById(process.pid);
                    var handle = proc.Handle;
                    if (proc.StartTime.ToUniversalTime().Ticks != process.createdAtTicks ||
                        !IsCurrentProjectMcpCommandLine(GetProcessCommandLineForDiagnostics(process.pid), HttpPort, WsPort))
                    {
                        proc.Dispose();
                        throw new InvalidOperationException($"Server PID={process.pid} 的创建时间或项目归属已改变。");
                    }
                    if (expectedProcessId > 0) UPilotServiceMaintenance.RequireContinuation();
                    proc.Kill();
                    _stoppingProcesses.Add(proc);
                    Debug.Log($"[UPilotMcpServerManager] Killed MCP server process PID={process.pid}");
                }
                catch (ArgumentException)
                {
                    // The process exited between discovery and termination.
                }
                catch (Exception ex)
                {
                    if (ex is ServiceMaintenanceException) throw;
                    throw new InvalidOperationException($"停止 Server PID={process.pid} 失败：{ex.Message}", ex);
                }
            }

            _trackedProcessId = null;
            DisposeTrackedProcess();
            InvalidateStatusCache();
        }

        public bool StopServerAndWaitForExit(int timeoutMs = 3000)
        {
            StopServer();
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (StoppedProcessesExited() && UPilotPortAllocator.IsPortAvailable(HttpPort) &&
                    UPilotPortAllocator.IsPortAvailable(WsPort))
                {
                    InvalidateStatusCache();
                    return true;
                }

                System.Threading.Thread.Sleep(50);
            }

            InvalidateStatusCache();
            return StoppedProcessesExited() && UPilotPortAllocator.IsPortAvailable(HttpPort) &&
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
            var oldProcessId = ResolveCurrentProjectProcessId();
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
            _restartOperationId = restart.operationId;
            _restartOldBridgeSessionId = oldBridgeSessionId;
            _restartExpectedProjectPath = restart.projectPath;
            _restartVerificationDeadlineUtcMs = 0;
            _restartMaintenanceDeadlineUtcMs = maintenanceDeadlineUtcMs;
            _restartNextProbeAtUtcMs = 0;
            _restartHealthProbeRunning = false;

            if (bridgeWasStarted)
            {
                if (maintenanceDeadlineUtcMs > 0) UPilotServiceMaintenance.RequireContinuation();
                bridge.Stop();
            }
            _afterRestartStarted += () =>
            {
                if (maintenanceDeadlineUtcMs > 0) UPilotServiceMaintenance.RequireContinuation();
                bridge.EnsureStarted();
            };
            if (afterStart != null)
                _afterRestartStarted += afterStart;
            try { StopCurrentProjectProcesses(expectedProcessId); }
            catch (Exception ex)
            {
                RecordRestartStartFailure((ex as ServiceMaintenanceException)?.Code ?? "stop_failed", ex.Message);
                _afterRestartStarted = null;
                FinishRestartObservation();
                return;
            }
            InvalidateStatusCache();

            _restartPending = true;
            var deadline = EditorApplication.timeSinceStartup + 4d;
            _restartWaitCallback = () =>
            {
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
                    UPilotServerRestartDiagnostics.RecordPortsReleased(_restartOperationId);
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

        private void CancelPendingRestart(bool recordCancellation)
        {
            var operationId = _restartOperationId;
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
                UPilotServerRestartDiagnostics.RecordCanceled(
                    operationId,
                    "MCP Server restart was interrupted by an explicit stop request.");
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
                UPilotServerRestartDiagnostics.RecordFailure(
                    operationId,
                    _restartMaintenanceDeadlineUtcMs > 0 ? "SERVICE_RESTART_TIMEOUT" : "restart_verification_timeout",
                    "The replacement MCP Server did not satisfy process, project, health, and Bridge verification before its deadline.",
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
            try
            {
                var status = await GetFreshStatusAsync(_restartMaintenanceDeadlineUtcMs);
                if (!string.Equals(operationId, _restartOperationId, StringComparison.Ordinal) ||
                    !UPilotServerRestartDiagnostics.IsActive(operationId))
                    return;
                if (IsVerifiedRestartHealth(status, processId, _restartExpectedProjectPath))
                {
                    var bridge = UPilotBridge.Instance.GetStatus();
                    var issues = UPilotDeploymentDiagnostics.Observe(bridge, status);
                    if (issues.Any(issue => issue.Code != "authentication" && issue.Code != "timeout"))
                    {
                        RecordRestartStartFailure("deployment_mismatch", string.Join("\n", issues.Select(issue => issue.Message)));
                        return;
                    }
                    if (!bridge.IsAuthenticated || !IsNewBridgeSession(_restartOldBridgeSessionId, bridge.SessionId))
                        return;
                    await VerifyReadOnlyRoundTripAsync(status, bridge.SessionId, _restartMaintenanceDeadlineUtcMs);
                    if (!string.Equals(operationId, _restartOperationId, StringComparison.Ordinal) ||
                        !UPilotServerRestartDiagnostics.IsActive(operationId)) return;
                    UPilotServerRestartDiagnostics.RecordHealthVerified(
                        operationId,
                        processId,
                        status.HealthProjectPath);
                    UPilotServerRestartDiagnostics.RecordDeploymentVerified(operationId);
                }
            }
            catch (Exception ex)
            {
                if (string.Equals(operationId, _restartOperationId, StringComparison.Ordinal))
                {
                    // Network/probe timeouts may recover within the one maintenance budget.
                    if (_restartMaintenanceDeadlineUtcMs <= 0 || ex is InvalidOperationException)
                        RecordRestartStartFailure("restart_validation_failed", ex.Message);
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

        private List<(int pid, string cmdLine, long createdAtTicks)> FindCurrentProjectMcpProcesses()
        {
            var deadline = Stopwatch.StartNew();
            var result = new List<(int pid, string cmdLine, long createdAtTicks)>();
            var candidatePids = new HashSet<int>();

            if (_trackedProcessId.HasValue)
                candidatePids.Add(_trackedProcessId.Value);

            var portsByPid = SafeGetListeningPortsByPid(out _);
            foreach (var entry in portsByPid)
            {
                if (entry.Value.Contains(HttpPort) || entry.Value.Contains(WsPort))
                    candidatePids.Add(entry.Key);
            }

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
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            foreach (var pid in candidatePids)
            {
                if (deadline.ElapsedMilliseconds >= 8000)
                    throw new TimeoutException("进程归属识别超过 8 秒，未授权停止未核实的进程。");
                try
                {
                    using var process = Process.GetProcessById(pid);
                    var createdAt = process.StartTime.ToUniversalTime().Ticks;
                    string cmdLine = GetProcessCommandLineForDiagnostics(pid);
                    if (!process.HasExited && IsCurrentProjectMcpCommandLine(cmdLine, HttpPort, WsPort))
                        result.Add((pid, cmdLine, createdAt));
                }
                catch (ArgumentException) { }
            }

            return result;
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
