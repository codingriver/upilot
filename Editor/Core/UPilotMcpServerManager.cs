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
using System.Runtime.InteropServices;
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

    public enum McpStatusFailureKind { None, Transient, Confirmed }

    public struct McpServerStatus
    {
        public bool IsRunning;
        public bool HttpPortListening;
        public bool WsPortListening;
        public int? ProcessId;
        public string ProcessCommandLine;
        public string ErrorMessage;
        public McpStatusFailureKind FailureKind;
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
        public bool EditorObservationAvailable;
        public string EditorObservationStatus;
        public string EditorObservationReason;
        public string EditorObservationLifecyclePhase;
        public string EditorObservationLifecycleId;
        public long EditorObservationLifecycleStartedAtUtcMs;
        public long EditorObservationObservedAtUtcMs;
        public long EditorObservationWaitingSinceUtcMs;
        public long EditorObservationWaitingDurationMs;
        public long EditorObservationPumpAgeMs;
        public long EditorObservationHeartbeatAgeMs;
        public int EditorObservationCount;
        public string EditorObservationSessionId;
        public string EditorObservationProducerEpoch;
        public int EditorObservationDomainGeneration;
        public int EditorObservationQueueDepth;
        public string EditorObservationLastCommandId;
        internal UPilotEditorStallSummary RecentEditorStall;
        internal UPilotServerHealth Health;
        internal UPilotServerHealth Identity;
        public bool IdentityAvailable;
        public bool IdentityIdentifiesUPilot;
        public int IdentityServerProcessId;
        public string IdentityProjectPath;
        public long HealthObservedAtUtcMs;
        public long HealthElapsedMs;
        public string HealthError;
        internal UPilotServerLifecyclePhase LifecyclePhase;
    }

    internal enum UPilotServerStopOrigin
    {
        User, PackageRegistration, PackageUpdate, ManagedServerUpdate, UpdateWindow, EditorShutdown
    }

    public sealed partial class UPilotMcpServerManager
    {
        public static UPilotMcpServerManager Instance { get; } = new();

        private static string DefaultPythonEntry => ResolveDefaultPythonEntry();
        private const string DefaultLogLevel = "INFO";
        private const string PackageName = "io.github.codingriver.upilot";
        private static readonly string CurrentProjectRoot = UPilotProjectConfig.ProjectRoot;
        private static readonly string CurrentProjectLogPath =
            UPilotOutputPaths.Resolve(UPilotOutputPaths.ServerLog, CurrentProjectRoot);
        internal static string ServerLogPath => UPilotDailyLogFile.ResolveExisting(CurrentProjectLogPath);
        private string _pythonEntryPath = DefaultPythonEntry;
        private string _logLevel = DefaultLogLevel;
        private bool _autoStart = true;
        internal const int DefaultHealthCheckIntervalSeconds = 20;
        private int _healthCheckIntervalSeconds = DefaultHealthCheckIntervalSeconds;
        private bool _pythonEntryManaged = true;
        private string EntryManagedKey => UPilotPreferences.McpPythonEntryKey + ".Managed";

        // Internal dependency for isolated process acceptance; the real manager uses Bridge ports.
        internal ServerStopScope EndpointOverride;
        /// <summary>HTTP port is stored and managed by UPilotBridge (single source of truth).</summary>
        public int HttpPort => EndpointOverride?.HttpPort ?? UPilotBridge.Instance?.HttpPort ?? 8011;
        /// <summary>WS port is stored and managed by UPilotBridge (single source of truth).</summary>
        public int WsPort => EndpointOverride?.WsPort ?? UPilotBridge.Instance?.WsPort ?? 8765;
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

        /// <summary>服务就绪后的周期健康检查间隔，按本机当前项目保存。</summary>
        public int HealthCheckIntervalSeconds
        {
            get => _healthCheckIntervalSeconds;
            set
            {
                value = Math.Max(1, Math.Min(600, value));
                lock (_statusLock)
                {
                    if (_healthCheckIntervalSeconds == value) return;
                    _healthCheckIntervalSeconds = value;
                    UpdateEditorHealthObservationScheduleLocked(_cachedStatus,
                        _lastRefreshMs > 0 ? _lastRefreshMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                }
                SignalPeriodicHealthObservation();
                SavePrefs();
            }
        }

        internal int HealthCheckIntervalMs => HealthCheckIntervalSeconds * 1000;
        private ServerAttachmentVerification _attachmentVerification;
        private ServerAttachmentFailure _attachmentFailure;
        internal bool HasVerifiedAttachment => _attachmentVerification?.Matches(_launchState) == true || HasVerifiedRecoveredAttachment;
        internal ServerAttachmentFailure AttachmentFailure =>
            _attachmentFailure?.Matches(_launchState, Volatile.Read(ref _startAttemptGeneration)) == true &&
            !HasVerifiedAttachment ? _attachmentFailure : null;
        internal bool PeriodicHealthChecksAllowed =>
            !StopBlocksAutomaticRecovery && OrdinaryStatusAllowed &&
            (HasVerifiedRecoveredAttachment || (_launchState?.verified == true &&
            (_attachmentVerification?.Matches(_launchState) == true || (_launchState.readyVerified && !_launchState.terminal)) && !_launchNeedsRevalidation)) &&
            !IsServiceTransitionActive && !UPilotServiceMaintenance.IsActive && !UPilotQuickStart.IsExplicitlyStopped;

        internal void ResetPreferencesToDefaultsInMemory()
        {
            _pythonEntryPath = DefaultPythonEntry;
            _pythonEntryManaged = true;
            _logLevel = DefaultLogLevel;
            _autoStart = true;
            lock (_statusLock)
            {
                _healthCheckIntervalSeconds = DefaultHealthCheckIntervalSeconds;
                UpdateEditorHealthObservationScheduleLocked(_cachedStatus,
                    _lastRefreshMs > 0 ? _lastRefreshMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
            SignalPeriodicHealthObservation();
        }

        // ── Cached status (background refresh) ────────────────────────────────

        private const int IdentityGraceMs = 8000;
        private const int IdentityFailureThreshold = 3;
        private CancellationTokenSource _restartProbeCancellation;
        private string _restartLastLoggedError;
        private long _restartLastLoggedAt;
        internal const long RestartProbeIntervalMs = 2000;
        private readonly object _statusLock = new();
        private McpServerStatus _cachedStatus;
        private Task<McpServerStatus> _statusRefreshTask;
        private StatusRefreshRequest _activeStatusRefresh;

        // Cancellation provenance belongs to the work, not to the latest cache invalidation.
        internal sealed class StatusRefreshRequest : IDisposable
        {
            private readonly object _gate = new();
            private readonly CancellationTokenSource _cancellation = new();
            private string _reason = "";
            private bool _disposed;
            internal readonly int Generation;
            internal readonly CancellationToken Token;
            internal string Reason { get { lock (_gate) return _reason; } }

            internal StatusRefreshRequest(int generation)
            {
                Generation = generation;
                Token = _cancellation.Token;
            }

            internal void Cancel(string reason)
            {
                lock (_gate)
                {
                    if (_disposed || _reason.Length > 0 || string.IsNullOrEmpty(reason)) return;
                    _reason = reason; // First cause wins, including timeout before a later stop.
                    _cancellation.Cancel();
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _cancellation.Dispose();
                }
            }
        }
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
        private List<ServerStopTarget> _stoppingTargets = new();
        private List<ServerStopTarget> StoppingTargets => _stoppingTargets ??= new List<ServerStopTarget>();
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
        private bool _editorHealthObservationEnabled;
        private Task _editorHealthObservationTask;
        private CancellationTokenSource _editorHealthObservationCancellation;
        private Task _editorHealthObservationLoopTask;
        private CancellationTokenSource _editorHealthObservationLoopCancellation;
        private long _nextEditorHealthObservationAtUtcMs;
        private SemaphoreSlim _editorHealthObservationSignal;
        private bool _periodicHealthNeedsVerification;

        private static readonly System.Net.Http.HttpClient _httpClient = CreateLoopbackStatusClient();

        private static System.Net.Http.HttpClient CreateLoopbackStatusClient()
        {
            return new System.Net.Http.HttpClient(CreateLoopbackStatusHandler())
            {
                Timeout = TimeSpan.FromSeconds(2)
            };
        }

        internal static System.Net.Http.HttpClientHandler CreateLoopbackStatusHandler()
        {
            return new System.Net.Http.HttpClientHandler { UseProxy = false };
        }

        internal static System.Net.Http.HttpRequestMessage CreateLoopbackStatusRequest(string url)
        {
            var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            request.Headers.ConnectionClose = true;
            return request;
        }

        private readonly struct McpProcessProbe
        {
            public McpProcessProbe(
                McpProcessOwnership ownership,
                int? processId,
                string commandLine,
                string evidence,
                bool ownsBothPorts = false)
            {
                OwnsBothPorts = ownsBothPorts;
                Ownership = ownership;
                ProcessId = processId;
                CommandLine = commandLine;
                Evidence = evidence;
            }

            public bool OwnsBothPorts { get; }
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
                Logger.ReportFailure("SERVER", "ResolveDefaultPythonEntry exception", ex);
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
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                BeforeStopDomainReload();
                BeforeLaunchDomainReload();
                InvalidateStatusCache("domain_reload");
            };
            EditorApplication.quitting += () =>
            {
                ResetStopObservation("editor_quitting");
                CancelPendingRestart(true, UPilotServerStopOrigin.EditorShutdown, Guid.NewGuid().ToString("N"),
                    _restartOperationId, _restartGeneration);
                StopLaunchObservation();
                InvalidateStatusCache("editor_exit");
            };
            try
            {
                if (!AssetDatabase.IsAssetImportWorkerProcess())
                {
                    EditorApplication.delayCall += EnsureInitialServiceRecovery;
                    // delayCall can wait for Inspector updates while the Editor is unattended.
                    EditorApplication.update += EnsureInitialServiceRecovery;
                }
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
            _healthCheckIntervalSeconds = Math.Max(1, Math.Min(600,
                EditorPrefs.GetInt(UPilotPreferences.McpHealthCheckIntervalSecondsKey, DefaultHealthCheckIntervalSeconds)));
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
            EditorPrefs.SetInt(UPilotPreferences.McpHealthCheckIntervalSecondsKey, _healthCheckIntervalSeconds);
            EditorPrefs.SetBool(EntryManagedKey, _pythonEntryManaged);
        }

        // ── Status ──────────────────────────────────────────────────────────

        public McpServerStatus GetStatus()
        {
            lock (_statusLock)
            {
                var status = _cachedStatus;
                status.LifecyclePhase = _lifecyclePhase;
                return status;
            }
        }

        public void InvalidateStatusCache(string lifecycleReason = "")
        {
            lock (_statusLock)
            {
                Interlocked.Increment(ref _statusGeneration);
                if (!string.IsNullOrEmpty(lifecycleReason))
                {
                    _activeStatusRefresh?.Cancel(lifecycleReason);
                    _lifecycleQueryCancellation?.Cancel();
                    _lifecycleQueryCancellation?.Dispose();
                    _lifecycleQueryCancellation = new CancellationTokenSource();
                    ++_lifecycleGeneration;
                    _restartProbeCancellation?.Cancel();
                    _periodicHealthNeedsVerification = true;
                    PausePeriodicHealthObservation();
                }
                // A cache refresh must not erase an error or restart its deadline.
                _lastRefreshMs = 0;
            }
            NotifyStatusChanged();
        }

        internal static async Task AwaitStoppedIdentityRecoveryAsync(Task recovery, long deadlineUtcMs = 0)
        {
            if (!recovery.IsCompleted && deadlineUtcMs > 0)
            {
                var remaining = deadlineUtcMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (remaining <= 0 || await Task.WhenAny(recovery, Task.Delay((int)Math.Min(remaining, 30000))) != recovery)
                    throw new TimeoutException("Stopped-service identity recovery exceeded the original status deadline.");
            }
            await recovery;
        }

        public async Task<McpServerStatus> GetFreshStatusAsync(long deadlineUtcMs = 0)
        {
            await AwaitStoppedIdentityRecoveryAsync(StoppedIdentityRecovery, deadlineUtcMs);
            if (TryGetDeferredLifecycleStatus(out var deferred))
            {
                NotifyStatusChanged();
                return deferred;
            }
            RequestBackgroundStatusRefresh(supersedeEditorObservation: true);
            Task<McpServerStatus> task;
            lock (_statusLock) task = _statusRefreshTask;
            if (deadlineUtcMs > 0)
            {
                var remaining = deadlineUtcMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (remaining <= 0 || await Task.WhenAny(task, Task.Delay((int)Math.Min(remaining, 30000))) != task)
                    throw new TimeoutException("Maintenance status probe exceeded its remaining budget.");
            }
            var result = await task;
            NotifyStatusChanged();
            return result;
        }

        // A new repair observes current ports/OS ownership without requiring the failed Bridge to be Ready.
        // This does not update ordinary status, consume an old lifecycle or renew its original budget.
        internal async Task<McpServerStatus> GetRepairPreflightStatusAsync(long deadlineUtcMs = 0,
            CancellationToken token = default, Func<int, bool> portAvailable = null,
            Func<long, CancellationToken, Task<McpServerStatus>> ownershipProbe = null)
        {
            var deadline = ProcessQueryDeadline(deadlineUtcMs, 8000, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            token.ThrowIfCancellationRequested();
            await AwaitStoppedIdentityRecoveryAsync(StoppedIdentityRecovery, deadline);
            token.ThrowIfCancellationRequested();
            var startGeneration = Volatile.Read(ref _startAttemptGeneration);
            var restartGeneration = _restartGeneration;
            var lifecycleGeneration = _lifecycleGeneration;
            var httpPort = HttpPort;
            var wsPort = WsPort;
            void CheckCurrent()
            {
                token.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadline)
                    throw new TimeoutException("Repair preflight exceeded its original deadline.");
                if (startGeneration != Volatile.Read(ref _startAttemptGeneration) || restartGeneration != _restartGeneration ||
                    lifecycleGeneration != _lifecycleGeneration || httpPort != HttpPort || wsPort != WsPort)
                    throw new LifecycleProbeDeferredException();
            }
            CheckCurrent();
            if (_lifecyclePhase != UPilotServerLifecyclePhase.None || HasActiveStopObservation ||
                _restartPending || IsRestartObservationActive())
                throw new InvalidOperationException("现有服务生命周期仍在执行；保留原操作及期限，未重复派发修复。");
            portAvailable ??= UPilotPortAllocator.IsPortAvailable;
            var httpAvailable = portAvailable(httpPort);
            var wsAvailable = portAvailable(wsPort);
            CheckCurrent();
            if (httpAvailable && wsAvailable)
                return new McpServerStatus { StatusQueryCompleted = true };
            // Use current configured endpoints. A terminal launch's old runtime spec is not a new launch request.
            var status = await (ownershipProbe != null ? ownershipProbe(deadline, token) :
                GetRestartStatusAsync(deadline, token));
            CheckCurrent();
            if (status.ProcessOwnership != McpProcessOwnership.CurrentUPilot || status.ProcessId.GetValueOrDefault() <= 0)
                throw new InvalidOperationException("无法安全确定当前项目 Server，未停止任何进程。\n" +
                    (status.ErrorMessage ?? status.ProcessOwnershipEvidence));
            status.IsRunning = true;
            status.StatusQueryCompleted = true;
            return status;
        }

        // Called by committed deployment notifications on the Editor thread, never by a window.
        internal void ObserveDeploymentChange()
        {
            if (PeriodicHealthChecksAllowed && !_periodicHealthNeedsVerification)
                ResumePeriodicHealthObservation();
            else
                PausePeriodicHealthObservation();
            ObservePossibleRecovery(GetStatus());
        }

        private void NotifyStatusChanged()
        {
            SignalPeriodicHealthObservation();
            // Isolated diagnostic/test managers must not drive the real project's repair workflow.
            if (ReferenceEquals(this, Instance)) UPilotQuickStart.NotifyDeploymentChanged();
        }

        internal void ResumePeriodicHealthObservation(System.Net.Http.HttpClient client = null)
        {
            if (!PeriodicHealthChecksAllowed || _periodicHealthNeedsVerification) return;
            lock (_statusLock)
            {
                _editorHealthObservationEnabled = true;
                if (_nextEditorHealthObservationAtUtcMs <= 0)
                    UpdateEditorHealthObservationScheduleLocked(_cachedStatus,
                        _lastRefreshMs > 0 ? _lastRefreshMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (_editorHealthObservationLoopTask != null && !_editorHealthObservationLoopTask.IsCompleted &&
                    _editorHealthObservationLoopCancellation?.IsCancellationRequested != true) return;
                var cancellation = new CancellationTokenSource();
                var signal = new SemaphoreSlim(0, 1);
                _editorHealthObservationLoopCancellation = cancellation;
                _editorHealthObservationSignal = signal;
                _editorHealthObservationLoopTask = Task.Run(() =>
                    RunEditorHealthObservationLoopAsync(cancellation, signal, client));
            }
        }

        internal void PausePeriodicHealthObservation()
        {
            lock (_statusLock)
            {
                _editorHealthObservationEnabled = false;
                _nextEditorHealthObservationAtUtcMs = 0;
                _editorHealthObservationCancellation?.Cancel();
                _editorHealthObservationLoopCancellation?.Cancel();
            }
        }

        private void SignalPeriodicHealthObservation()
        {
            lock (_statusLock)
            {
                var signal = _editorHealthObservationSignal;
                if (signal != null && signal.CurrentCount == 0) signal.Release();
            }
        }

        private async Task RunEditorHealthObservationLoopAsync(CancellationTokenSource cancellation,
            SemaphoreSlim signal, System.Net.Http.HttpClient client)
        {
            var token = cancellation.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    McpServerStatus status;
                    Task pending;
                    long due;
                    lock (_statusLock)
                    {
                        if (!_editorHealthObservationEnabled || !PeriodicHealthChecksAllowed ||
                            _periodicHealthNeedsVerification) break;
                        status = _cachedStatus;
                        pending = _statusRefreshTask != null && !_statusRefreshTask.IsCompleted
                            ? _statusRefreshTask : _editorHealthObservationTask;
                        if (_nextEditorHealthObservationAtUtcMs <= 0)
                            UpdateEditorHealthObservationScheduleLocked(status,
                                _lastRefreshMs > 0 ? _lastRefreshMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        due = _nextEditorHealthObservationAtUtcMs;
                    }

                    if (pending != null && !pending.IsCompleted)
                    {
                        // A single request owns this interval; configuration signals do not start another.
                        var stopped = new TaskCompletionSource<bool>();
                        using (token.Register(() => stopped.TrySetResult(true)))
                            await Task.WhenAny(pending, stopped.Task).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        continue;
                    }

                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (now < due)
                    {
                        await signal.WaitAsync((int)Math.Min(int.MaxValue, due - now), token).ConfigureAwait(false);
                        continue;
                    }
                    lock (_statusLock)
                    {
                        // Admit the periodic request atomically with lifecycle pause/cancellation.
                        token.ThrowIfCancellationRequested();
                        if (!_editorHealthObservationEnabled || !PeriodicHealthChecksAllowed ||
                            _periodicHealthNeedsVerification) break;
                        status = _cachedStatus;
                        RequestEditorHealthObservationIfNeeded(status, now, client);
                        // A skipped/instantaneous probe still observes the configured minimum interval.
                        if (_nextEditorHealthObservationAtUtcMs <= now &&
                            (_statusRefreshTask == null || _statusRefreshTask.IsCompleted) &&
                            (_editorHealthObservationTask == null || _editorHealthObservationTask.IsCompleted))
                            UpdateEditorHealthObservationScheduleLocked(_cachedStatus,
                                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Service lifecycle transitions end this observer; windows have no ownership.
            }
            finally
            {
                lock (_statusLock)
                {
                    if (ReferenceEquals(_editorHealthObservationLoopCancellation, cancellation))
                    {
                        _editorHealthObservationLoopCancellation = null;
                        _editorHealthObservationLoopTask = null;
                        _editorHealthObservationSignal = null;
                        _editorHealthObservationEnabled = false;
                        _nextEditorHealthObservationAtUtcMs = 0;
                    }
                    signal.Dispose();
                }
                cancellation.Dispose();
            }
        }

        private void RequestEditorHealthObservationIfNeeded(McpServerStatus status, long nowUtcMs,
            System.Net.Http.HttpClient client = null)
        {
            if (!PeriodicHealthChecksAllowed ||
                IsServiceTransitionActive || UPilotServiceMaintenance.IsActive || UPilotQuickStart.IsExplicitlyStopped)
                return;

            lock (_statusLock)
            {
                if (!_editorHealthObservationEnabled ||
                    (_editorHealthObservationTask != null && !_editorHealthObservationTask.IsCompleted) ||
                    (_statusRefreshTask != null && !_statusRefreshTask.IsCompleted))
                    return;

                if (_nextEditorHealthObservationAtUtcMs <= 0)
                    _nextEditorHealthObservationAtUtcMs =
                        (_lastRefreshMs > 0 ? _lastRefreshMs : nowUtcMs) + HealthCheckIntervalMs;
                if (nowUtcMs < _nextEditorHealthObservationAtUtcMs)
                    return;

                var generation = Volatile.Read(ref _statusGeneration);
                var cancellation = new CancellationTokenSource();
                _editorHealthObservationCancellation = cancellation;
                _editorHealthObservationTask = RefreshEditorHealthObservationAsync(
                    HttpPort, generation, status, cancellation, client);
            }
        }

        private async Task RefreshEditorHealthObservationAsync(
            int httpPort,
            int generation,
            McpServerStatus expected,
            CancellationTokenSource cancellation,
            System.Net.Http.HttpClient client = null)
        {
            var address = $"http://127.0.0.1:{httpPort}/health";
            var started = Stopwatch.GetTimestamp();
            var observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int httpStatus = 0;
            try
            {
                using var response = await SendLifecycleStatusAsync(client ?? _httpClient,
                    address, 0, cancellation.Token);
                httpStatus = (int)response.StatusCode;
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                var health = JsonUtility.FromJson<UPilotServerHealth>(json);
                if (health == null || health.server_pid <= 0 || string.IsNullOrWhiteSpace(health.server_instance_id))
                    throw new FormatException("Health response lacks diagnostic identity.");
                if (expected.Identity != null && (health.server_pid != expected.Identity.server_pid ||
                    health.server_instance_id != expected.Identity.server_instance_id ||
                    !UPilotDeploymentDiagnostics.SamePath(health.configured_project_path, expected.Identity.configured_project_path)))
                    throw new InvalidOperationException("Health diagnostic identity differs from the current hello ACK.");
                lock (_statusLock)
                {
                    if (!_editorHealthObservationEnabled || cancellation.IsCancellationRequested ||
                        generation != Volatile.Read(ref _statusGeneration)) return;
                    _cachedStatus.Health = health;
                    _cachedStatus.HealthEndpointResponded = true;
                    _cachedStatus.HealthError = "";
                    ApplyEditorObservation(ref _cachedStatus, health.editor_observation);
                }
                Logger.CloseDiagnostics("service-health|" + address);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                lock (_statusLock)
                {
                    if (generation == Volatile.Read(ref _statusGeneration))
                    {
                        _cachedStatus.HealthEndpointResponded = false;
                        _cachedStatus.HealthError = ex.GetType().FullName + ": " + ex.Message;
                    }
                }
                Logger.ReportServiceObservation("SERVER",
                    $"Health observation address={address} observedAtUtcMs={observedAt} elapsedMs={Logger.LifecycleElapsedMs(started)} httpStatus={httpStatus}",
                    ex, scope: "service-health|" + address, fingerprint: "health|" + httpStatus + "|" + ex.GetType().FullName);
            }
            finally
            {
                lock (_statusLock)
                {
                    if (ReferenceEquals(_editorHealthObservationCancellation, cancellation))
                    {
                        _editorHealthObservationCancellation = null;
                        _editorHealthObservationTask = null;
                        _cachedStatus.HealthObservedAtUtcMs = observedAt;
                        _cachedStatus.HealthElapsedMs = (long)((Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency);
                        if (!cancellation.IsCancellationRequested)
                            UpdateEditorHealthObservationScheduleLocked(_cachedStatus, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    }
                }
                cancellation.Dispose();
                NotifyStatusChanged();
            }
        }

        internal static bool MatchesEditorObservationIdentity(
            McpServerStatus expected,
            UPilotServerHealth health,
            UPilotEditorObservation observation)
        {
            if (health == null || observation == null ||
                expected.IdentityServerProcessId <= 0 || health.server_pid != expected.IdentityServerProcessId)
                return false;
            if (!string.IsNullOrEmpty(expected.Identity?.server_instance_id) &&
                health.server_instance_id != expected.Identity.server_instance_id)
                return false;
            if (!string.IsNullOrEmpty(expected.Identity?.configured_project_path) &&
                !UPilotDeploymentDiagnostics.SamePath(expected.Identity.configured_project_path, health.configured_project_path))
                return false;
            return observation.session_id == expected.EditorObservationSessionId &&
                   observation.producer_epoch == expected.EditorObservationProducerEpoch &&
                   observation.domain_generation == expected.EditorObservationDomainGeneration;
        }

        internal static void ApplyEditorObservation(ref McpServerStatus status, UPilotEditorObservation observation)
        {
            var recognized = observation != null &&
                             (observation.status == "responsive" || observation.status == "waiting_editor" || observation.status == "waiting_lifecycle" ||
                              observation.status == "unknown");
            status.EditorObservationAvailable = recognized;
            status.EditorObservationStatus = recognized ? observation.status : "";
            status.EditorObservationReason = recognized ? observation.reason ?? "" : "";
            status.EditorObservationLifecyclePhase = recognized ? observation.lifecycle_phase ?? "" : "";
            status.EditorObservationLifecycleId = recognized ? observation.lifecycle_id ?? "" : "";
            status.EditorObservationLifecycleStartedAtUtcMs = recognized ? observation.lifecycle_started_at_ms : 0;
            status.EditorObservationObservedAtUtcMs = recognized ? observation.observed_at_ms : 0;
            status.EditorObservationWaitingSinceUtcMs = recognized ? observation.waiting_since_ms : 0;
            status.EditorObservationWaitingDurationMs = recognized ? observation.waiting_duration_ms : 0;
            status.EditorObservationPumpAgeMs = recognized && (observation.pump_age_ms > 0 || observation.last_main_thread_pump_at_ms > 0)
                ? observation.pump_age_ms : -1;
            status.EditorObservationHeartbeatAgeMs = recognized && (observation.heartbeat_age_ms > 0 || observation.heartbeat_at_ms > 0)
                ? observation.heartbeat_age_ms : -1;
            status.EditorObservationCount = recognized ? observation.observation_count : 0;
            status.EditorObservationSessionId = recognized ? observation.session_id ?? "" : "";
            status.EditorObservationProducerEpoch = recognized ? observation.producer_epoch ?? "" : "";
            status.EditorObservationDomainGeneration = recognized ? observation.domain_generation : 0;
            status.EditorObservationQueueDepth = recognized ? observation.main_thread_queue_depth : 0;
            status.EditorObservationLastCommandId = recognized ? observation.last_dequeued_command_id ?? "" : "";
            status.RecentEditorStall = recognized ? observation.recent_stall : null;
        }

        internal static void AgeEditorObservation(ref McpServerStatus status, long now)
        {
            if (!status.EditorObservationAvailable) return;
            var age = Math.Max(0, now - status.EditorObservationObservedAtUtcMs);
            if (status.EditorObservationPumpAgeMs >= 0) status.EditorObservationPumpAgeMs += age;
            if (status.EditorObservationHeartbeatAgeMs >= 0) status.EditorObservationHeartbeatAgeMs += age;
            if (status.EditorObservationStatus == "waiting_lifecycle")
                status.EditorObservationWaitingDurationMs = Math.Max(0, now - status.EditorObservationLifecycleStartedAtUtcMs);
            status.EditorObservationObservedAtUtcMs = now;
        }

        internal static bool ShouldRequestFullStatusRefresh(McpServerStatus status, int currentGeneration) =>
            // An explicit invalidation must escape the waiting_editor lightweight-only path.
            status.StatusGeneration != currentGeneration || ShouldRequestFullStatusRefresh(status);

        internal static bool ShouldRequestFullStatusRefresh(McpServerStatus status) =>
            status.FailureKind == McpStatusFailureKind.Confirmed ||
            !status.EditorObservationAvailable || (status.EditorObservationStatus != "waiting_editor" &&
                status.EditorObservationStatus != "waiting_lifecycle");

        private void UpdateEditorHealthObservationScheduleLocked(McpServerStatus status, long completedAtUtcMs)
        {
            _nextEditorHealthObservationAtUtcMs =
                _editorHealthObservationEnabled
                    ? completedAtUtcMs + HealthCheckIntervalMs
                    : 0;
        }

        private bool CacheVerifiedStartupStatus(McpServerStatus status, long lifecycleGeneration = -1,
            string bridgeSessionId = null, Func<bool> commitOwnerState = null)
        {
            lock (_statusLock)
            {
                // Gate the whole owner completion before its flags, lifecycle, cache or observer can become terminal.
                if (lifecycleGeneration >= 0 && (lifecycleGeneration != _lifecycleGeneration ||
                    _lifecyclePhase != UPilotServerLifecyclePhase.Verifying)) return false;
                var synchronized = StateSynchronizedOverride != null ? StateSynchronizedOverride() :
                    lifecycleGeneration < 0 ? UPilotBridge.Instance.IsStateSynchronized :
                    UPilotBridge.Instance.TryGetBootstrapReceipt(bridgeSessionId, status.Identity?.server_instance_id, out _);
                if (!synchronized || (commitOwnerState != null && !commitOwnerState())) return false;
                if (lifecycleGeneration >= 0 && !EndLifecyclePhase(lifecycleGeneration)) return false;
                _attachmentFailure = null;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                status.LifecyclePhase = _lifecyclePhase;
                status.StatusGeneration = _statusGeneration;
                status.StatusQueryCompleted = true;
                status.LastSuccessfulStatusAtUtcMs = now;
                ApplyEditorObservation(ref status, status.Health?.editor_observation);
                _cachedStatus = status;
                _lastRefreshMs = now;
                _periodicHealthNeedsVerification = false;
                UpdateEditorHealthObservationScheduleLocked(status, now);
            }
            ResumePeriodicHealthObservation();
            NotifyStatusChanged();
            return true;
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
            var startedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try
            {
                var issues = UPilotDeploymentDiagnostics.Observe(bridge, status, _launchState?.spec);
                if (issues.Any(issue => issue.Blocking)) return;
                if (!UPilotBridge.Instance.TryGetBootstrapReceipt(bridge.SessionId, status.Identity?.server_instance_id, out _)) return;
                var currentBridge = UPilotBridge.Instance.GetStatus();
                if (!currentBridge.IsAuthenticated || !currentBridge.IsWsOpen || currentBridge.SessionId != bridge.SessionId) return;
                UPilotServerRestartDiagnostics.ObserveRecovery(operationId, status.ProcessId ?? 0, bridge.SessionId,
                    status.IdentityProjectPath, status.IdentityAvailable, true, true);
            }
            catch (Exception ex)
            {
                LogRestartProbeException(operationId, "post_restart_readonly_observation", startedAtUtcMs, ex);
                Logger.LogWarning("SERVER", "Read-only recovery observation failed: " + ex.Message);
            }
            finally { _recoveryObservationRunning = false; }
        }

        private void RequestBackgroundStatusRefresh(bool supersedeEditorObservation = false)
        {
            var httpPort = HttpPort;
            var wsPort = WsPort;
            lock (_statusLock)
            {
                if (!OrdinaryStatusAllowed || (_statusRefreshTask != null && !_statusRefreshTask.IsCompleted)) return;
                if (_editorHealthObservationTask != null && !_editorHealthObservationTask.IsCompleted)
                {
                    if (!supersedeEditorObservation) return;
                    _editorHealthObservationCancellation?.Cancel();
                    _nextEditorHealthObservationAtUtcMs = 0;
                }
                var generation = Volatile.Read(ref _statusGeneration);
                var request = new StatusRefreshRequest(generation);
                _activeStatusRefreshStage = "port_probe";
                _activeStatusRefresh = request;
                _statusRefreshTask = RunBoundedStatusRefreshAsync(httpPort, wsPort, request);
            }
        }

        private void ReleaseStatusRefresh(StatusRefreshRequest request)
        {
            lock (_statusLock)
            {
                if (ReferenceEquals(_activeStatusRefresh, request))
                    _activeStatusRefresh = null;
            }
            request.Dispose();
        }

        private async Task<McpServerStatus> RunBoundedStatusRefreshAsync(int httpPort, int wsPort,
            StatusRefreshRequest request)
        {
            var generation = request.Generation;
            var work = Task.Run(() => RefreshStatusAsync(httpPort, wsPort, request));
            _ = work.ContinueWith(_ => ReleaseStatusRefresh(request), TaskScheduler.Default);
            if (await Task.WhenAny(work, Task.Delay(30000)).ConfigureAwait(false) == work)
                return await work.ConfigureAwait(false);
            lock (_statusLock)
            {
                request.Cancel("timeout");
                if (generation == Volatile.Read(ref _statusGeneration) && OrdinaryStatusAllowed)
                {
                    Interlocked.Increment(ref _statusGeneration);
                    _cachedStatus.ErrorMessage = "状态获取超时（30 秒）：进程识别、HTTP 查询或状态汇总未完成。";
                    _cachedStatus.StatusFailureStage = _activeStatusRefreshStage;
                    _cachedStatus.StatusGeneration = generation;
                    _cachedStatus.StatusCancellationReason = "timeout";
                    _cachedStatus.FailureKind = McpStatusFailureKind.Transient;
                    _cachedStatus.StatusQueryCompleted = true;
                    _lastRefreshMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    UpdateEditorHealthObservationScheduleLocked(_cachedStatus, _lastRefreshMs);
                }
                NotifyStatusChanged();
                return _cachedStatus;
            }
        }

        private void TrackStatusRefreshStage(int generation, string stage)
        {
            lock (_statusLock)
                if (generation == Volatile.Read(ref _statusGeneration))
                    _activeStatusRefreshStage = stage;
        }

        private async Task<McpServerStatus> RefreshStatusAsync(int httpPort, int wsPort,
            StatusRefreshRequest request)
        {
            var generation = request.Generation;
            var token = request.Token;
            var status = new McpServerStatus();
            status.StatusGeneration = generation;
            var stage = "port_probe";
            try
            {
                token.ThrowIfCancellationRequested();
                lock (_statusLock)
                    if (!OrdinaryStatusAllowed || generation != Volatile.Read(ref _statusGeneration)) return _cachedStatus;
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
                    if (!HasVerifiedRecoveredAttachment)
                    {
                        var process = ProbeMcpProcessOwnership(httpPort, wsPort, token: token);
                        status.ProcessOwnership = process.Ownership;
                        status.ProcessId = process.ProcessId;
                        status.ProcessCommandLine = process.CommandLine;
                        status.ProcessOwnershipEvidence = process.Evidence;
                    }

                    ApplyCurrentServerIdentity(ref status);
                    TryApplyRecoveredProcessOwnership(ref status);
                }
            }
            catch (ThreadAbortException)
            {
                // 线程中止继续向上传播，不写入状态失败或输出故障日志。
                throw;
            }
            catch (LifecycleProbeDeferredException) { return _cachedStatus; }
            catch (Exception ex)
            {
                var reason = request.Reason;
                bool expected = IsExpectedStatusInterruption(ex, token, reason);
                status.ErrorMessage = ex.GetType().Name + ": " + ex.Message;
                status.FailureKind = ClassifyStatusException(ex);
                status.StatusFailureStage = stage;
                status.StatusCancellationReason = ex is OperationCanceledException
                    ? (string.IsNullOrEmpty(reason) ? "unknown" : reason) : "";
                if (reason == "lifecycle_phase") { }
                else if (expected)
                    Debug.LogWarning($"[UPilotMcpServerManager] Expected {reason} status interruption at {stage} (generation {generation}).");
                else if (status.FailureKind == McpStatusFailureKind.Transient)
                    Logger.Log("SERVER", $"Status not yet confirmed at {stage}: {ex.GetType().Name}");
                else
                    Logger.ReportFailure("SERVER", $"Status refresh failed at {stage} generation={generation}", ex, fingerprint: "status-refresh|" + stage);
            }

            var refreshedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (_statusLock)
            {
                if (generation != Volatile.Read(ref _statusGeneration) || !OrdinaryStatusAllowed)
                    return _cachedStatus;

                status.LifecyclePhase = _lifecyclePhase;
                status.StatusQueryCompleted = true;
                status.LastSuccessfulStatusAtUtcMs = status.IdentityAvailable
                    ? refreshedAt : _cachedStatus.LastSuccessfulStatusAtUtcMs;
                status.Health = _cachedStatus.Health;
                status.HealthEndpointResponded = _cachedStatus.HealthEndpointResponded;
                status.HealthObservedAtUtcMs = _cachedStatus.HealthObservedAtUtcMs;
                status.HealthElapsedMs = _cachedStatus.HealthElapsedMs;
                status.HealthError = _cachedStatus.HealthError;
                ApplyEditorObservation(ref status, status.Health?.editor_observation);
                UpdateDiagnosisTracking(ref status, refreshedAt);
                _cachedStatus = status;
                _lastRefreshMs = refreshedAt;
            }

            NotifyStatusChanged();
            return status;
        }

        internal static McpStatusFailureKind ClassifyStatusException(Exception ex) =>
            ex is OperationCanceledException || ex is TimeoutException || ex is System.Net.Http.HttpRequestException ||
            ex is SocketException ? McpStatusFailureKind.Transient : McpStatusFailureKind.Confirmed;

        internal static bool IsExpectedStatusInterruption(Exception ex, CancellationToken token, string reason) =>
            ex is OperationCanceledException && token.IsCancellationRequested
            && (reason == "domain_reload" || reason == "editor_exit" || reason == "explicit_stop"
                || reason == "lifecycle_stop");

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

        // The request budget covers headers AND buffered body, independently of a handler's
        // cancellation implementation. Late completion is disposed, never applied to a record.
        internal static async Task<System.Net.Http.HttpResponseMessage> SendBoundedStatusAsync(
            System.Net.Http.HttpClient client, string url, long deadlineUtcMs, CancellationToken token,
            int requestTimeoutMs = 2000)
        {
            var remaining = deadlineUtcMs > 0 ? deadlineUtcMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : requestTimeoutMs;
            if (remaining <= 0) throw new TimeoutException("/health operation deadline elapsed.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            var budget = (int)Math.Min(requestTimeoutMs, remaining);
            timeout.CancelAfter(budget);
            using var request = CreateLoopbackStatusRequest(url);
            var send = client.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseContentRead, timeout.Token);
            var delay = Task.Delay(budget, token);
            if (await Task.WhenAny(send, delay) != send)
            {
                timeout.Cancel();
                _ = send.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); else { var ignored = t.Exception; } }, TaskScheduler.Default);
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("/health request headers/body timed out.");
            }
            System.Net.Http.HttpResponseMessage response;
            try { response = await send; }
            catch (OperationCanceledException error) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
            {
                // CancelAfter can win the race with Task.Delay. Keep budget expiry
                // deterministic without turning caller cancellation into a timeout.
                throw new TimeoutException("/health operation deadline elapsed.", error);
            }
            if (timeout.IsCancellationRequested || (deadlineUtcMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadlineUtcMs))
            {
                response.Dispose();
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("/health operation deadline elapsed.");
            }
            return response;
        }

        internal static string DescribeProbeException(Exception error)
        {
            var parts = new List<string>();
            for (var e = error; e != null && parts.Count < 5; e = e.InnerException)
            {
                var codes = " HResult=0x" + e.HResult.ToString("X8");
                if (e is System.Net.Sockets.SocketException socket)
                    codes += " SocketError=" + socket.SocketErrorCode + " NativeError=" + socket.NativeErrorCode;
                parts.Add(e.GetType().Name + codes + ": " + e.Message);
            }
            // Do not include response payloads or credentials in diagnostic evidence.
            var text = string.Join(" -> ", parts);
            text = System.Text.RegularExpressions.Regex.Replace(text,
                @"(?i)(authorization|bearer|token|secret|password|nonce)([=:\s]+)[^\s,;]+", "$1=<redacted>");
            return TruncateDiagnostic(text, 1024);
        }

        internal static Task<McpServerStatus> GetRestartHealthAsync(System.Net.Http.HttpClient client, int httpPort,
            long deadlineUtcMs, CancellationToken token = default, UPilotMcpServerManager lifecycle = null, int requestTimeoutMs = 2000)
        {
            token.ThrowIfCancellationRequested();
            if (deadlineUtcMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadlineUtcMs)
                throw new TimeoutException("Original service verification deadline elapsed.");
            var status = new McpServerStatus { StatusQueryCompleted = true };
            ApplyCurrentServerIdentity(ref status);
            return Task.FromResult(status);
        }

        private static void ApplyCurrentServerIdentity(ref McpServerStatus status)
        {
            status.IdentityAvailable = UPilotBridge.Instance.TryGetServerIdentity(out var identity);
            status.Identity = identity;
            status.IdentityIdentifiesUPilot = status.IdentityAvailable;
            status.IdentityServerProcessId = identity?.server_pid ?? 0;
            status.IdentityProjectPath = identity?.configured_project_path;
            status.ServerVersion = identity?.server_version;
            status.ProtocolVersion = identity?.protocol_version;
            status.BuildCommit = identity?.build_commit;
            status.BuildChannel = identity?.build_channel;
        }

        internal async Task<McpServerStatus> GetRestartStatusAsync(long deadlineUtcMs, CancellationToken token = default, ServerLaunchSpec spec = null, bool useRemainingRoundBudget = false)
        {
            if (!await PrepareLifecycleProbeAsync(token))
            {
                TryGetDeferredLifecycleStatus(out var local);
                return local;
            }
            var generation = _lifecycleGeneration;
            var httpPort = spec?.httpPort ?? HttpPort;
            var wsPort = spec?.wsPort ?? WsPort;
            using var roundClient = useRemainingRoundBudget ? CreateLoopbackStatusClient() : null;
            if (roundClient != null) roundClient.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            var roundBudget = useRemainingRoundBudget ? (int)Math.Max(1, deadlineUtcMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) : 2000;
            var status = await GetRestartHealthAsync(roundClient ?? _httpClient, httpPort, deadlineUtcMs, token, this, roundBudget);
            token.ThrowIfCancellationRequested();
            if (generation != _lifecycleGeneration) throw new LifecycleProbeDeferredException();
            if (TryApplyRecoveredProcessOwnership(ref status))
            {
                status.HttpPortListening = await IsPortListeningAsync("127.0.0.1", httpPort);
                status.WsPortListening = await IsPortListeningAsync("127.0.0.1", wsPort);
                token.ThrowIfCancellationRequested();
                if (generation != _lifecycleGeneration) throw new LifecycleProbeDeferredException();
                return status;
            }
            var remaining = deadlineUtcMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (remaining <= 0) throw new TimeoutException("Restart identity deadline elapsed.");
            string expectedLogPath = null;
            if (spec != null && (!SameProjectPath(spec.projectPath, CurrentProjectRoot) ||
                !spec.TryGetLogFilePath(out expectedLogPath)))
                throw new InvalidOperationException("冻结启动描述的项目或日志路径无效，未核实进程归属。");
            // One ownership round shares its fixed deadline with the worker and its helper process.
            var identityDeadline = ProcessQueryDeadline(deadlineUtcMs, 8000, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var processTask = Task.Run(() => ProbeMcpProcessOwnership(httpPort, wsPort,
                identityDeadline, expectedLogPath, token));
            if (await Task.WhenAny(processTask, Task.Delay((int)Math.Max(1, identityDeadline - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), token)) != processTask)
            {
                _ = processTask.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                token.ThrowIfCancellationRequested();
                if (generation != _lifecycleGeneration) throw new LifecycleProbeDeferredException();
                status.StatusFailureStage = "process_identity";
                status.ErrorMessage = "Process ownership probe timed out; ownership unconfirmed.";
                return status;
            }
            var process = await processTask;
            token.ThrowIfCancellationRequested();
            if (generation != _lifecycleGeneration) throw new LifecycleProbeDeferredException();
            status.ProcessId = process.ProcessId;
            status.ProcessOwnership = process.Ownership;
            status.ProcessOwnershipEvidence = process.Evidence;
            status.ProcessCommandLine = process.CommandLine;
            status.IsRunning = true;
            status.HttpPortListening = true;
            // Port ownership is OS evidence, not evidence of an authenticated Bridge.
            status.WsPortListening = process.OwnsBothPorts;
            return status;
        }

        internal static bool IsVerifiedStartupHealth(McpServerStatus status)
        {
            if (!status.IdentityAvailable || !status.IdentityIdentifiesUPilot ||
                status.ProcessOwnership != McpProcessOwnership.CurrentUPilot ||
                !status.ProcessId.HasValue || status.ProcessId.Value <= 0 ||
                status.IdentityServerProcessId != status.ProcessId.Value)
                return false;

            return !string.Equals(
                status.ProcessOwnershipEvidence,
                "UPilot 健康检查响应",
                StringComparison.Ordinal);
        }

        internal static string ResolveHealthProjectPath(string json)
        {
            var configured = ParseStringFromJson(json, "configured_project_path");
            return string.IsNullOrWhiteSpace(configured)
                ? ParseStringFromJson(json, "project_path")
                : configured;
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
                if (!await PrepareLifecycleProbeAsync()) { await Task.Delay(250); continue; }
                latestVersion = UPilotBridge.Instance.TryGetServerIdentity(out var identity) ? identity.server_version : "";
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
            Debug.Log($"[UPilotMcpServerManager] 请求启动 MCP Server；HTTP={HttpPort}；WS={WsPort}。");
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

            EnsureInitialServiceRecovery();
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
            if (_startInProgress)
            {
                Debug.Log("[UPilotMcpServerManager] MCP server start is already in progress; start request merged.");
                return;
            }
            if (HasPendingLaunchVerification)
            {
                Debug.Log("[UPilotMcpServerManager] MCP server launch verification is already in progress; start request merged with the original observation.");
                return;
            }
            _attachmentFailure = null;
            var trackedProcessAlive = IsTrackedProcessAlive();
            if (trackedProcessAlive || !UPilotPortAllocator.IsPortAvailable(HttpPort) || !UPilotPortAllocator.IsPortAvailable(WsPort))
            {
                if (TryBeginExistingServiceRecovery(null)) return;
            }

            try { _pendingLaunchSpec ??= PrepareLaunchSpec(LaunchDeadline()); }
            catch (Exception ex)
            {
                UPilotStartupDiagnostics.RecordBlockingReason("launch_spec_invalid", ex.Message);
                RecordRestartStartFailure("launch_spec_invalid", ex.Message);
                _pendingLaunchSpec = null;
                _pendingLaunchDeadline = 0;
                return;
            }
            if (trackedProcessAlive || !UPilotPortAllocator.IsPortAvailable(HttpPort) ||
                !UPilotPortAllocator.IsPortAvailable(WsPort))
            {
                if (TryBeginExistingServiceRecovery(_pendingLaunchSpec)) return;
                var attachSpec = _pendingLaunchSpec;
                if (trackedProcessAlive && TryGetVerifiedOwnershipLogPath(_launchState, CurrentProjectRoot, HttpPort, WsPort, out _))
                    attachSpec = _launchState.spec;
                _startInProgress = true;
                var generation = Interlocked.Increment(ref _startAttemptGeneration);
                _ = AttachOrStartAfterIdentityProbeAsync(generation, attachSpec.Snapshot());
                return;
            }

            StartNewServer();
        }

        private async Task StartAfterStoppedIdentityRecoveryAsync(Task recovery, int startGeneration, long restartGeneration)
        {
            try
            {
                await AwaitStoppedIdentityRecoveryAsync(recovery);
                if (startGeneration != _startAttemptGeneration || restartGeneration != _restartGeneration || !_startInProgress) return;
                _startInProgress = false;
                StartServer();
            }
            catch (Exception ex)
            {
                if (startGeneration != _startAttemptGeneration || restartGeneration != _restartGeneration) return;
                _startInProgress = false;
                Logger.ReportFailure("SERVER", "启动前的原停止身份恢复失败；未启动新服务。", ex,
                    "server-start-recovery-" + startGeneration, terminal: true);
            }
        }

        internal sealed class ServerAttachmentVerification
        {
            private readonly ServerLaunchState _state;
            private readonly ServerLaunchSpec _spec;
            private readonly int _launcherPid, _servicePid;
            private readonly long _launcherCreatedAt, _serviceCreatedAt;
            private readonly string _instanceId;

            internal ServerAttachmentVerification(ServerLaunchState state)
            {
                _state = state; _spec = state?.spec;
                _launcherPid = state?.launcherPid ?? 0; _servicePid = state?.servicePid ?? 0;
                _launcherCreatedAt = state?.launcherCreatedAt ?? 0; _serviceCreatedAt = state?.serviceCreatedAt ?? 0;
                _instanceId = state?.instanceId;
            }

            internal bool Matches(ServerLaunchState state) =>
                state != null && ReferenceEquals(state, _state) && ReferenceEquals(state.spec, _spec) && state.verified &&
                _launcherPid > 0 && _servicePid > 0 && _launcherCreatedAt > 0 && _serviceCreatedAt > 0 &&
                !string.IsNullOrEmpty(_instanceId) && state.launcherPid == _launcherPid && state.servicePid == _servicePid &&
                state.launcherCreatedAt == _launcherCreatedAt && state.serviceCreatedAt == _serviceCreatedAt &&
                state.instanceId == _instanceId;
        }

        internal sealed class ServerAttachmentFailure
        {
            private readonly ServerAttachmentVerification _identity;
            private readonly int _generation;
            internal readonly string OperationId, Reason;
            internal readonly bool TimedOut;

            internal ServerAttachmentFailure(ServerLaunchState state, int generation, string operationId, Exception exception)
            {
                _identity = new ServerAttachmentVerification(state);
                _generation = generation;
                OperationId = operationId;
                Reason = exception.Message;
                TimedOut = exception is TimeoutException;
            }

            internal bool Matches(ServerLaunchState state, int generation) =>
                generation == _generation && _identity.Matches(state);
        }

        internal bool CompleteExplicitAttachmentFailure(ServerLaunchState state, int generation, long lifecycleGeneration,
            string operationId, Exception exception)
        {
            if (generation != Volatile.Read(ref _startAttemptGeneration) || lifecycleGeneration != _lifecycleGeneration ||
                _lifecyclePhase == UPilotServerLifecyclePhase.None || !ReferenceEquals(state, _launchState)) return false;
            _launchNeedsRevalidation = true;
            _attachmentFailure = new ServerAttachmentFailure(state, generation, operationId, exception);
            EndLifecyclePhase(lifecycleGeneration, exception.Message, exception is TimeoutException,
                emitDiagnostic: false, failureException: exception);
            Logger.ReportFailure("SERVER", $"显式附着失败；operationId={operationId}；PID={state.servicePid}。", exception,
                operationId, terminal: true);
            return true;
        }

        private async Task VerifyExplicitAttachmentAsync(ServerLaunchState state, int generation, long deadline)
        {
            var observationId = Guid.NewGuid().ToString("N");
            var identity = new ServerAttachmentVerification(state);
            var lifecycleGeneration = SetLifecyclePhase(UPilotServerLifecyclePhase.Verifying, state.servicePid, observationId, deadline);
            Exception lastException = null;
            var lastReason = "等待 Bridge 握手和实际只读往返";
            bool OwnsObservation() => generation == Volatile.Read(ref _startAttemptGeneration) &&
                lifecycleGeneration == _lifecycleGeneration && ReferenceEquals(state, _launchState);
            try
            {
                while (OwnsObservation() && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < deadline)
                {
                    try
                    {
                        var status = await GetRestartStatusAsync(deadline, default, state.spec);
                        if (!OwnsObservation()) return;
                        if (!identity.Matches(state))
                            throw new InvalidOperationException("显式附着期间冻结服务身份发生变化。");
                        if (!TryResolveStartedService(state, status, out lastReason, stopping: true))
                        {
                            if (ClassifyLaunchIdentityProbe(status, false, lastReason) == "process_identity_mismatch" ||
                                lastReason == "process_exited")
                                throw new InvalidOperationException("显式附着身份核验失败：" + lastReason);
                        }
                        else
                        {
                            var bridge = UPilotBridge.Instance.GetStatus();
                            if (bridge.IsWsOpen && bridge.IsAuthenticated &&
                                !UPilotDeploymentDiagnostics.Observe(bridge, status, state.spec).Any(issue => issue.Blocking))
                            {
                                await VerifyReadOnlyRoundTripAsync(status, bridge.SessionId, deadline);
                                var after = UPilotBridge.Instance.GetStatus();
                                if (!OwnsObservation()) return;
                                if (!identity.Matches(state))
                                    throw new InvalidOperationException("实际只读往返后冻结服务身份发生变化。");
                                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadline)
                                    throw new TimeoutException("显式附着的实际只读往返超过本次原定期限。");
                                if (after.IsWsOpen && after.IsAuthenticated && after.SessionId == bridge.SessionId)
                                {
                                    if (CacheVerifiedStartupStatus(status, lifecycleGeneration, bridge.SessionId, () =>
                                    {
                                        _attachmentVerification = identity;
                                        _launchNeedsRevalidation = false;
                                        state.readyVerified = true;
                                        PersistLaunchState();
                                        return true;
                                    })) return;
                                }
                                lastReason = "实际只读往返后 Bridge 会话发生变化";
                            }
                        }
                    }
                    catch (LifecycleProbeDeferredException) { if (!OwnsObservation()) return; }
                    catch (Exception ex)
                    {
                        if (!OwnsObservation()) return;
                        if (ClassifyStatusException(ex) == McpStatusFailureKind.Confirmed) throw;
                        lastException = ex; lastReason = ex.Message;
                    }
                    var remaining = deadline - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (remaining > 0) await Task.Delay((int)Math.Min(RestartProbeIntervalMs, remaining));
                }
                if (OwnsObservation()) throw new TimeoutException("显式附着验证未在本次原定期限内完成：" + lastReason, lastException);
            }
            catch (Exception ex)
            {
                if (OwnsObservation())
                    CompleteExplicitAttachmentFailure(state, generation, lifecycleGeneration, observationId, ex);
            }
        }

        private async Task AttachOrStartAfterIdentityProbeAsync(int generation, ServerLaunchSpec spec)
        {
            var deadline = LaunchDeadline();
            try
            {
                McpServerStatus status = default;
                var identityError = "service_ownership_unverified";
                // 只重试观察；端口仍不可绑定时绝不尝试启动第二个服务。
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (generation != Volatile.Read(ref _startAttemptGeneration) || !_startInProgress) return;
                    try { status = await GetRestartStatusAsync(deadline, default, spec); }
                    catch (ThreadAbortException) { throw; }
                    catch (Exception ex)
                    {
                        status = new McpServerStatus { StatusFailureStage = "health_query",
                            ErrorMessage = DescribeProbeException(ex), FailureKind = ClassifyStatusException(ex) };
                    }
                    if (generation != Volatile.Read(ref _startAttemptGeneration) || !_startInProgress) return;
                    identityError = "service_ownership_unverified";
                    if (IsVerifiedExistingProjectService(status, spec.projectPath) &&
                        (_launchState != null
                            ? TryResolveStartedService(_launchState, status, out identityError, stopping: true)
                            : TryTrackExistingProcess(status, spec, deadline, out identityError)))
                    {
                        Debug.Log($"[UPilotMcpServerManager] 已认领当前工程的 MCP 服务 PID={status.IdentityServerProcessId}；开始本次独立只读附着验证。");
                        StopLaunchObservation();
                        _attachmentVerification = null;
                        _launchNeedsRevalidation = true;
                        ResetStopObservation("new_service_start_or_restart");
                        await VerifyExplicitAttachmentAsync(_launchState, generation, deadline);
                        return;
                    }
                    if (status.IdentityAvailable && !SameProjectPath(status.IdentityProjectPath, spec.projectPath))
                        identityError = "project_identity_mismatch";
                    else if (status.IdentityAvailable && status.IdentityServerProcessId > 0 &&
                             status.ProcessId.GetValueOrDefault() > 0 && status.ProcessId != status.IdentityServerProcessId)
                        identityError = "service_health_pid_mismatch";
                    if (UPilotPortAllocator.IsPortAvailable(spec.httpPort) && UPilotPortAllocator.IsPortAvailable(spec.wsPort))
                    {
                        StartNewServer();
                        return;
                    }
                    var retryDelayMs = attempt == 0 ? (int)RestartProbeIntervalMs : 3000;
                    var remaining = deadline - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (attempt == 2 || remaining <= retryDelayMs ||
                        !ShouldRetryExistingServiceProbe(status, identityError)) break;
                    Debug.Log($"[UPilotMcpServerManager] HTTP={spec.httpPort} / WS={spec.wsPort} 仍不可绑定；等待 {retryDelayMs / 1000} 秒后复查服务身份与端口（第 {attempt + 1}/2 次等待）。");
                    await Task.Delay(retryDelayMs);
                }
                var stage = string.IsNullOrEmpty(status.StatusFailureStage) ? "attach_identity" : status.StatusFailureStage;
                var message = $"已有监听服务身份未确认；阶段={stage}，分类={status.FailureKind}，归属={status.ProcessOwnership}，" +
                    $"身份依据={identityError}；{status.ErrorMessage}。HTTP={spec.httpPort} / WS={spec.wsPort} 仍不可绑定，本次不启动新服务。";
                UPilotStartupDiagnostics.RecordBlockingReason("server_existing_identity_unverified", message);
                if (IsRestartObservationActive())
                    RecordRestartStartFailure("server_existing_identity_unverified", message);
                else
                    Logger.ReportFailure("SERVER", message, scope: "server-start-" + generation, terminal: true);
            }
            finally
            {
                if (generation == Volatile.Read(ref _startAttemptGeneration))
                {
                    _startInProgress = false;
                    _pendingLaunchSpec = null;
                    _pendingLaunchDeadline = 0;
                }
            }
        }

        internal static bool ShouldRetryExistingServiceProbe(McpServerStatus status, string identityError)
        {
            if (status.ProcessOwnership == McpProcessOwnership.Foreign || status.FailureKind == McpStatusFailureKind.Confirmed ||
                (identityError != "service_ownership_unverified" &&
                 !(identityError ?? "").StartsWith("process_evidence_unavailable", StringComparison.Ordinal))) return false;
            var outcome = ClassifyLaunchIdentityProbe(status, false, identityError);
            return status.FailureKind == McpStatusFailureKind.Transient ||
                   outcome == "process_identity_pending" || outcome == "process_identity_timeout";
        }

        private void StartNewServer()
        {
            if (_pendingLaunchSpec != null && (_pendingLaunchSpec.httpPort != HttpPort || _pendingLaunchSpec.wsPort != WsPort))
            {
                RecordRestartStartFailure("launch_configuration_changed", "冻结启动描述与当前端口不符，未启动服务");
                _pendingLaunchSpec = null;
                _pendingLaunchDeadline = 0;
                return;
            }
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
                var spec = _pendingLaunchSpec ?? PrepareLaunchSpec(LaunchDeadline());
                if (spec.httpPort != HttpPort || spec.wsPort != WsPort)
                    throw new InvalidOperationException("启动描述冻结后端口配置已变更，停止本次启动");
                _pendingLaunchSpec = null;
                LaunchServer(spec, LaunchDeadline());
                ResetStopObservation("new_service_start_or_restart");
            }
            catch (Exception ex)
            {
                EndLifecyclePhase(_lifecycleGeneration, "server_start_exception: " + ex.Message, emitDiagnostic: false);
                Logger.ReportFailure("SERVER", "Failed to start server", ex);
                UPilotStartupDiagnostics.RecordBlockingReason("server_start_exception", ex.Message);
                RecordRestartStartFailure("server_start_exception", ex.Message);
            }
            finally
            {
                _startInProgress = false;
                _pendingLaunchSpec = null;
                _pendingLaunchDeadline = 0;
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
                   status.IdentityAvailable &&
                   status.IdentityIdentifiesUPilot &&
                   status.IdentityServerProcessId > 0 &&
                   status.ProcessId == status.IdentityServerProcessId &&
                   status.ProcessOwnership != McpProcessOwnership.Foreign &&
                   SameProjectPath(status.IdentityProjectPath, expectedProjectPath);
        }

        // ── Stop ────────────────────────────────────────────────────────────

        public void StopServer() => StopServer(UPilotServerStopOrigin.User);

        internal void StopServer(UPilotServerStopOrigin origin, string requestId = "") => _ = StopServerAndObserveExitAsync(origin, requestId);

        internal sealed class PreparedProcessStop : IDisposable
        {
            internal readonly List<ServerStopTarget> Processes = new();
            internal readonly HashSet<int> Transferred = new();
            internal Dictionary<int, List<int>> VerifiedPorts;
            internal ServerStopScope Scope;
            internal int[] UnmatchedPortOwners = Array.Empty<int>();
            internal int HttpPort, WsPort;
            public void Dispose()
            {
                foreach (var entry in Processes)
                    if (!Transferred.Contains(entry.ProcessId)) entry.Dispose();
                Processes.Clear();
            }
        }

        private PreparedProcessStop PrepareCurrentProjectStop(int expectedProcessId = 0, long deadlineUtcMs = 0)
        {
            var deadline = deadlineUtcMs > 0 ? deadlineUtcMs : _restartVerificationDeadlineUtcMs > 0 ? _restartVerificationDeadlineUtcMs :
                StopNow + UPilotTimingSettings.ServiceStopObservationTimeoutSeconds * 1000L;
            // The accepted RPC endpoint identity is separate from residual stop targets.
            // It may already have exited while matching listeners still require cleanup.
            return PrepareStopScope(GetStopScope(), new[] { expectedProcessId, _trackedProcessId ?? 0 }, deadline);
        }

        internal static void ValidateStopPortOwnership(Dictionary<int, List<int>> portsByPid,
            bool portQuerySucceeded, IEnumerable<int> verifiedProcessIds, int httpPort, int wsPort)
        {
            if (!portQuerySucceeded || portsByPid == null || verifiedProcessIds == null)
                throw new InvalidOperationException("无法安全停止 Server：监听端口归属查询失败。");
            var owners = portsByPid.Where(entry => entry.Value.Contains(httpPort) || entry.Value.Contains(wsPort))
                .Select(entry => entry.Key).Distinct().ToArray();
            if (owners.Any(pid => !verifiedProcessIds.Contains(pid)))
                throw new InvalidOperationException("无法安全停止 Server：端口进程不属于当前项目或身份未知。");
            // A reserved/permission-denied port can be unbindable without a listener.
            // Bindability gates startup/restart, not an identity-verified stop (or an empty no-op).
            // Retained OS handles identify the termination targets; old launch history is not a stop gate.
        }

        private void StopCurrentProjectProcesses(int expectedProcessId = 0)
        {
            InvalidateStatusCache("explicit_stop");
            using var prepared = PrepareCurrentProjectStop(expectedProcessId);
            StopPreparedProcesses(prepared, expectedProcessId);
        }

        private void StopPreparedProcesses(PreparedProcessStop prepared, int expectedProcessId, Action onStopAttempt = null, Dictionary<int, List<int>> verifiedPorts = null, string stopObservationId = "", long stopDeadline = 0, bool initialDispatch = true, string maintenanceId = "")
        {
            if (initialDispatch || prepared.Processes.Any(x => !StoppingTargets.Any(t => t.ProcessId == x.ProcessId && !t.HasExited)))
                Logger.Lifecycle("server_termination_targets_verified", $"stopId={stopObservationId} maintenanceId={maintenanceId} {CaptureServiceShutdown().Detail} targetPids=[{string.Join(",", prepared.Processes.Select(entry => entry.ProcessId))}] targetCount={prepared.Processes.Count} httpPort={prepared.HttpPort} wsPort={prepared.WsPort}");
            if (initialDispatch) { Interlocked.Increment(ref _startAttemptGeneration); _startInProgress = false; }
            _stoppingHttpPort = prepared.HttpPort;
            _stoppingWsPort = prepared.WsPort;
            var stopOperationId = !string.IsNullOrEmpty(stopObservationId) ? stopObservationId :
                string.IsNullOrEmpty(_restartOperationId) ? Guid.NewGuid().ToString("N") : _restartOperationId;
            long stopLifecycleGeneration = 0;
            try
            {
                foreach (var entry in prepared.Processes)
                {
                    if (entry.HasExited || StoppingTargets.Any(t => t.ProcessId == entry.ProcessId && !t.HasExited)) continue;
                    if (stopDeadline > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= stopDeadline)
                        throw new TimeoutException("Stop deadline elapsed before dispatch.");
                    if (!string.IsNullOrEmpty(maintenanceId)) UPilotServiceMaintenance.RequireContinuation(maintenanceId);
                    onStopAttempt?.Invoke();
                    if (stopLifecycleGeneration == 0)
                        stopLifecycleGeneration = SetLifecyclePhase(UPilotServerLifecyclePhase.Stopping, entry.ProcessId, stopOperationId, stopDeadline);
                    if (_restartOperationId.Length > 0 && UPilotServerRestartDiagnostics.IsActive(_restartOperationId))
                        UPilotServerRestartDiagnostics.RecordStopProgress(_restartOperationId, serverAttempted: true);
                    try
                    {
                        var dispatchStarted = Stopwatch.GetTimestamp();
                        Logger.Lifecycle("server_termination_dispatching", $"stopId={stopObservationId} maintenanceId={maintenanceId} {CaptureServiceShutdown().Detail} targetPid={entry.ProcessId} method=TerminateProcess remainingMs={(stopDeadline > 0 ? Math.Max(0, stopDeadline - StopNow).ToString() : "unknown")}");
                        if (StopTerminationOverride != null) StopTerminationOverride(entry);
                        else entry.Terminate();
                        StoppingTargets.Add(entry);
                        prepared.Transferred.Add(entry.ProcessId);
                        Logger.Lifecycle("server_termination_dispatched", $"stopId={stopObservationId} {CaptureServiceShutdown().Detail} targetPid={entry.ProcessId} dispatchReturned=true processExited={entry.HasExited} elapsedMs={Logger.LifecycleElapsedMs(dispatchStarted)}");
                    }
                    catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5 &&
                        _stopObservation?.Active == true && _stopObservation.id == stopObservationId)
                    {
                        // A process that is terminating can reject TerminateProcess before its
                        // handle is signaled. Keep that original handle, never dispatch again,
                        // and let the same observer prove exit/ports or fail at its deadline.
                        StoppingTargets.Add(entry);
                        prepared.Transferred.Add(entry.ProcessId);
                        var diagnostic = $"Win32={ex.NativeErrorCode}；PID={entry.ProcessId}；入口={entry.EntryPath}；stopId={stopObservationId}\n{ex}";
                        _stopObservation.pendingTerminationDiagnostic += diagnostic + "\n";
                        _stopObservation.failureException = _stopObservation.failureException == null ? ex :
                            new AggregateException(_stopObservation.failureException, ex);
                        Logger.ReportWarning("SERVER", "Server 终止调用被 OS 拒绝，继续按原期限观察同一句柄；" + diagnostic,
                            stopObservationId, fingerprint: "termination-pending|" + entry.ProcessId);
                    }
                    catch (InvalidOperationException)
                    {
                        // Exited after the preflight; the handle identifies the old process, not a replacement.
                        if (!entry.HasExited) throw;
                    }
                }
                if (stopLifecycleGeneration == 0 && initialDispatch)
                    stopLifecycleGeneration = SetLifecyclePhase(UPilotServerLifecyclePhase.Stopping, 0, stopOperationId);
                if (initialDispatch)
                {
                    ReleaseLaunchAfterStop();
                    _trackedProcessId = null;
                    DisposeTrackedProcess();
                    InvalidateStatusCache();
                }
            }
            catch
            {
                if (stopLifecycleGeneration != 0) EndLifecyclePhase(stopLifecycleGeneration, "stop_failed");
                throw;
            }
        }

        internal readonly struct ServerStopResult
        {
            internal readonly bool StopConfirmed;
            internal readonly bool PortsAvailable;
            internal readonly bool ObservationSucceeded;
            internal readonly int StatusGeneration;
            internal readonly int StartGeneration;
            internal readonly long RestartGeneration;

            internal ServerStopResult(bool stopConfirmed, bool portsAvailable, int statusGeneration,
                int startGeneration, long restartGeneration, bool observationSucceeded = true)
            {
                StopConfirmed = stopConfirmed;
                PortsAvailable = portsAvailable;
                ObservationSucceeded = observationSucceeded;
                StatusGeneration = statusGeneration;
                StartGeneration = startGeneration;
                RestartGeneration = restartGeneration;
            }
        }

        internal bool IsStopResultCurrent(ServerStopResult result) =>
            result.StopConfirmed && result.ObservationSucceeded && result.RestartGeneration == _restartGeneration &&
            result.StartGeneration == _startAttemptGeneration && !_startInProgress && !_restartPending &&
            !_trackedProcessId.HasValue;

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

        internal void RestartPreparedServer(Action afterStart = null, long maintenanceDeadlineUtcMs = 0, int expectedProcessId = 0, long acceptedAtUtcMs = 0, string resetOwnerId = "")
        {
            if (UPilotServiceMaintenance.IsActive && !UPilotServiceMaintenance.IsExecuting) return;
            if (maintenanceDeadlineUtcMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= maintenanceDeadlineUtcMs)
                throw new ServiceMaintenanceException("SERVICE_RESTART_TIMEOUT", "Maintenance expired before stopping services.");
            var maintenanceId = maintenanceDeadlineUtcMs > 0 ? UPilotServiceMaintenance.Current?.maintenanceId ?? "" : "";
            if (maintenanceDeadlineUtcMs > 0) UPilotServiceMaintenance.RequireContinuation(maintenanceId);
            var processRole = UPilotBridge.DetermineProcessRole();
            if (!UPilotBridge.IsMainEditorProcess(processRole))
            {
                var reason = $"MCP Server restart rejected for auxiliary Unity process role '{processRole}'.";
                Debug.LogWarning("[UPilotMcpServerManager] " + reason);
                UPilotStartupDiagnostics.RecordBlockingReason("server_auxiliary_process_role", reason);
                return;
            }

            EnsureInitialServiceRecovery();
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

            ResetStopObservation("new_service_start_or_restart");

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
                restart = UPilotServerRestartDiagnostics.Begin(projectRoot, oldProcessId, oldBridgeSessionId, maintenanceDeadlineUtcMs, acceptedAtUtcMs);
            }
            catch (Exception ex)
            {
                Logger.ReportFailure("SERVER", "MCP restart was not started", ex);
                return;
            }
            _restartGeneration = Math.Max(_restartGeneration + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            restart.restartGeneration = _restartGeneration;
            UPilotServerRestartDiagnostics.RecordGeneration(restart.operationId, _restartGeneration);
            _restartOperationId = restart.operationId;
            _restartOldBridgeSessionId = oldBridgeSessionId;
            _restartExpectedProjectPath = restart.projectPath;
            _restartVerificationDeadlineUtcMs = UPilotServerRestartDiagnostics.Deadline(restart);
            _restartMaintenanceDeadlineUtcMs = maintenanceDeadlineUtcMs;
            _restartNextProbeAtUtcMs = 0;
            _restartHealthProbeRunning = false;
            _restartShutdownOwnerId = string.IsNullOrEmpty(resetOwnerId) ? _restartOperationId : resetOwnerId;
            BeginServiceShutdown(_restartShutdownOwnerId, "restart_preparing", _restartVerificationDeadlineUtcMs);

            PreparedProcessStop prepared = null;
            Task<ServerStopResult> stopping = null;
            var stopDeadline = Math.Min(StopNow + UPilotTimingSettings.ServiceStopObservationTimeoutSeconds * 1000L,
                _restartVerificationDeadlineUtcMs);
            try
            {
                RunVerifiedStopSequence(() =>
                {
                    UPilotServerRestartDiagnostics.RecordPhase(_restartOperationId, "identity_probe");
                    RequireRestartBudget();
                    _pendingLaunchSpec = PrepareLaunchSpec(_restartVerificationDeadlineUtcMs);
                    UPilotServerRestartDiagnostics.RecordLaunchSpec(_restartOperationId, _pendingLaunchSpec);
                    RequireRestartBudget();
                    prepared = PrepareCurrentProjectStop(expectedProcessId, stopDeadline);
                    var receipt = ResetActiveWorkForServiceOwner(_restartShutdownOwnerId, "server_restart");
                    UPilotServerRestartDiagnostics.RecordResetReceipt(_restartOperationId, _restartShutdownOwnerId, receipt);
                    UPilotServerRestartDiagnostics.MarkGate(_restartOperationId, "old_identity", "passed",
                        prepared.Processes.Count == 0 ? "no_old_process" : "identity_verified",
                        prepared.Processes.Count == 0 ? "未发现旧进程，无需停止" : "已核实进程归属");
                    if (!bridgeWasStarted)
                        UPilotServerRestartDiagnostics.MarkGate(_restartOperationId, "bridge_stop", "passed",
                            "no_old_bridge", "旧 Bridge 未启动，无需停止");
                    if (oldProcessId == 0 && prepared.Processes.Count == 1)
                    {
                        oldProcessId = prepared.Processes[0].ProcessId;
                        UPilotServerRestartDiagnostics.RecordOldProcessId(_restartOperationId, oldProcessId);
                    }
                    RequireRestartBudget();
                    if (maintenanceDeadlineUtcMs > 0) UPilotServiceMaintenance.RequireContinuation(maintenanceId);
                    UPilotServerRestartDiagnostics.RecordPhase(_restartOperationId, "stopping_old_services");
                }, bridgeWasStarted, () =>
                {
                    UPilotServerRestartDiagnostics.RecordStopProgress(_restartOperationId, bridgeAttempted: true);
                    RequireRestartBudget();
                    Logger.Lifecycle("bridge_stop_started", $"restartId={_restartOperationId} {CaptureServiceShutdown().Detail} oldSessionId={oldBridgeSessionId}");
                    bridge.Stop();
                    Logger.Lifecycle("bridge_stop_finished", $"restartId={_restartOperationId} {CaptureServiceShutdown().Detail} bridgeStarted={bridge.GetStatus().IsStarted}");
                    UPilotServerRestartDiagnostics.RecordStopProgress(_restartOperationId,
                        bridgeConfirmed: !bridge.GetStatus().IsStarted);
                }, onAttempt =>
                {
                    RequireRestartBudget();
                    onAttempt();
                    UPilotServerRestartDiagnostics.RecordStopProgress(_restartOperationId, serverAttempted: true);
                    stopping = StopServerAndObserveExitAsync(UPilotServerStopOrigin.User, _restartOperationId,
                        parentDeadlineUtcMs: stopDeadline, scope: prepared.Scope, prepared: prepared, preserveRestart: true,
                        maintenanceId: maintenanceId);
                    prepared = null; // Ownership moved to the one stop observer, including residual sweeps.
                }, () =>
                {
                    if (oldProcessId <= 0) return false;
                    RequireRestartBudget();
                    return prepared != null && SameProjectPath(prepared.Scope.ProjectPath, _restartExpectedProjectPath) &&
                        prepared.Processes.Any(entry => entry.ProcessId == oldProcessId && !entry.HasExited);
                }, () => { RequireRestartBudget(); bridge.EnsureStarted(); }, (attempted, result, error) =>
                    UPilotServerRestartDiagnostics.RecordBridgeRestore(_restartOperationId, attempted, result, error));
                _afterRestartStarted += () =>
                {
                    RequireRestartBudget();
                    if (maintenanceDeadlineUtcMs > 0) UPilotServiceMaintenance.RequireContinuation(maintenanceId);
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
            _restartWaitCallback = () =>
            {
                if (waitingGeneration != _restartGeneration || waitingOperationId != _restartOperationId ||
                    !UPilotServerRestartDiagnostics.IsActive(waitingOperationId)) return;
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= _restartVerificationDeadlineUtcMs)
                {
                    var pending = UPilotServerRestartDiagnostics.Current;
                    RecordRestartStartFailure(_restartMaintenanceDeadlineUtcMs > 0 ? "SERVICE_RESTART_TIMEOUT" : "restart_verification_timeout",
                        UPilotServerRestartDiagnostics.TimeoutMessage(pending, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                    CancelPendingRestart(recordCancellation: false);
                    return;
                }
                if (!_restartPending)
                {
                    CancelPendingRestart(recordCancellation: false);
                    return;
                }

                if (stopping?.IsCompleted != true) return;
                var stopped = stopping.GetAwaiter().GetResult();
                var portsAvailable = stopped.ObservationSucceeded && stopped.StopConfirmed && stopped.PortsAvailable;
                if (portsAvailable)
                {
                    if (_restartMaintenanceDeadlineUtcMs > 0)
                    {
                        try { UPilotServiceMaintenance.RequireContinuation(maintenanceId); }
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
                    if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= _restartVerificationDeadlineUtcMs)
                    {
                        RecordRestartStartFailure(_restartMaintenanceDeadlineUtcMs > 0 ? "SERVICE_RESTART_TIMEOUT" : "restart_verification_timeout",
                            UPilotServerRestartDiagnostics.TimeoutMessage(restart, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                        FinishRestartObservation();
                        return;
                    }
                    EndLifecyclePhase(_lifecycleGeneration);
                    Logger.Lifecycle("replacement_start_requested", $"restartId={waitingOperationId} restartGeneration={waitingGeneration} {CaptureServiceShutdown().Detail} processExited={stopped.StopConfirmed} portsReleased={stopped.PortsAvailable} observationSucceeded={stopped.ObservationSucceeded} remainingMs={Math.Max(0, _restartVerificationDeadlineUtcMs - StopNow)}");
                    StartServer();
                    if (IsRestartObservationActive())
                        InvokeAfterRestartStarted();
                    BeginRestartObservation();
                    return;
                }

                var timedOutCallback = _restartWaitCallback;
                if (timedOutCallback != null)
                    EditorApplication.update -= timedOutCallback;
                _restartWaitCallback = null;
                _restartPending = false;
                _afterRestartStarted = null;
                InvalidateStatusCache();
                var message = _stopObservation?.TerminalMessage ??
                    $"MCP restart failed waiting for ports HTTP={HttpPort}, WS={WsPort} to be released.";
                UPilotServerRestartDiagnostics.RecordFailure(
                    _restartOperationId,
                    "port_release_timeout",
                    message,
                    UPilotServerRestartDiagnostics.ReadServerLogTail(ServerLogPath),
                    ServerLogPath,
                    "Inspect the process owning the configured ports before requesting one new restart.");
                FinishRestartObservation();
                Logger.ReportFailure("SERVER", message);
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
                        Logger.ReportFailure("SERVER", "Bridge recovery unconfirmed", recoveryError);
                    }
                    try { onBridgeRestore?.Invoke(restoreAttempted, restoreResult, restoreError); }
                    catch (Exception diagnosticError)
                    {
                        Logger.ReportFailure("SERVER", "Bridge recovery diagnostic failed", diagnosticError);
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
            FinishServiceShutdown(_restartShutdownOwnerId, "restart_canceled");
            _restartShutdownOwnerId = "";
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
            if (_lifecycleOperationId == operationId) EndLifecyclePhase(_lifecycleGeneration, "restart_canceled", emitDiagnostic: false);
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
                Logger.ReportFailure("SERVER", "Restart callback failed", ex);
            }
        }

        private void RecordRestartStartFailure(string code, string message)
        {
            if (!UPilotServerRestartDiagnostics.IsActive(_restartOperationId)) return;
            if ((code ?? "").IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 && _lifecycleOperationId == _restartOperationId)
                EndLifecyclePhase(_lifecycleGeneration, code + ": " + message, true);
            UPilotServerRestartDiagnostics.RecordFailure(
                _restartOperationId,
                code,
                message,
                UPilotServerRestartDiagnostics.ReadServerLogTail(ServerLogPath),
                ServerLogPath);
        }

        private int ResolveCurrentProjectProcessId()
        {
            if (IsTrackedProcessAlive() && _trackedProcessId.HasValue)
                return _trackedProcessId.Value;
            var processes = FindCurrentProjectMcpProcesses();
            return processes.Count == 1 ? processes[0].pid : 0;
        }

        private void RequireRestartBudget()
        {
            if (!IsRestartObservationActive() || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= _restartVerificationDeadlineUtcMs)
                throw new TimeoutException("重启总期限已到；不再执行停止或启动。");
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
            _restartVerificationDeadlineUtcMs = UPilotServerRestartDiagnostics.Deadline(record);
            _restartProbeCancellation?.Cancel();
            _restartProbeCancellation = new CancellationTokenSource();
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
                    UPilotServerRestartDiagnostics.ReadServerLogTail(ServerLogPath),
                    ServerLogPath,
                    "Inspect the prior restart evidence and request one explicit restart if still needed.");
                ClearRestartObservationState();
                return;
            }

            if (_launchState == null || _launcherProcess == null || _trackedProcess == null ||
                _launchState.terminal || record.launchSpec == null ||
                _launchState.launcherPid != record.launcherProcessId ||
                _launchState.launcherCreatedAt != record.launcherProcessCreatedAtTicks ||
                _launchState.deadlineUtcMs != UPilotServerRestartDiagnostics.Deadline(record) ||
                _restoredRestartResetReceiptId != record.operationId)
            {
                RecordRestartStartFailure("restart_recovery_required",
                    "Reload 后冻结启动身份缺失或无法核实；仅观察历史记录，不重放启动。");
                ClearRestartObservationState();
                return;
            }
            _launchReloadRevalidation = false;
            SetLifecyclePhase(_lifecyclePhase, record.newProcessId, record.operationId, _launchState.deadlineUtcMs);
            BeginRestartObservation();
        }

        private void ObserveRestart()
        {
            // Reload/exit cancels observation, never starts another probe in the old domain.
            if (_restartProbeCancellation?.IsCancellationRequested == true) return;
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
                        UPilotServerRestartDiagnostics.ReadServerLogTail(ServerLogPath),
                        ServerLogPath);
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
                    UPilotServerRestartDiagnostics.ReadServerLogTail(ServerLogPath),
                    ServerLogPath);
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
                    UPilotServerRestartDiagnostics.TimeoutMessage(pending, now),
                    UPilotServerRestartDiagnostics.ReadServerLogTail(ServerLogPath),
                    ServerLogPath,
                    "Inspect health, exact projectPath, and Bridge session diagnostics before requesting one new restart.");
                FinishRestartObservation();
                return;
            }

            if (!_restartHealthProbeRunning && now >= _restartNextProbeAtUtcMs)
            {
                _restartHealthProbeRunning = true;
                _ = ProbeRestartHealthAsync(operationId, processId);
            }
        }

        private async Task ProbeRestartHealthAsync(string operationId, int processId)
        {
            var probeStartedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var probeEnded = false;
            var readOnlyStarted = false;
            var cancellation = _restartProbeCancellation.Token;
            try
            {
                if (!await PrepareLifecycleProbeAsync(cancellation)) return;
                var lifecycleGeneration = _lifecycleGeneration;
                UPilotServerRestartDiagnostics.BeginStatusProbe(operationId);
                var status = await GetRestartStatusAsync(_restartVerificationDeadlineUtcMs, cancellation, _launchState?.spec);
                if (lifecycleGeneration != _lifecycleGeneration) return;
                if (cancellation.IsCancellationRequested || operationId != _restartOperationId || !UPilotServerRestartDiagnostics.IsActive(operationId)) return;
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= _restartVerificationDeadlineUtcMs) { ObserveRestart(); return; }
                if (UPilotServerRestartDiagnostics.IsActive(operationId))
                {
                    if (status.IdentityAvailable)
                    {
                        // Preserve actual health identity even when a later gate rejects it.
                        UPilotServerRestartDiagnostics.RecordServerBridgeDiagnostics(operationId, status.Identity);
                        UPilotServerRestartDiagnostics.RecordDeploymentIdentity(operationId, status.Identity);
                    }
                    var identityResolved = TryResolveStartedService(_launchState, status, out var identityError);
                    if (identityResolved) processId = _trackedProcessId ?? processId;
                    var outcome = ClassifyLaunchIdentityProbe(status, identityResolved, identityError);
                    if (status.IdentityAvailable && !string.IsNullOrWhiteSpace(status.IdentityProjectPath) &&
                        !SameProjectPath(status.IdentityProjectPath, _restartExpectedProjectPath))
                        outcome = "project_identity_mismatch";
                    var probeError = outcome == "project_identity_mismatch"
                        ? "期望项目 " + _restartExpectedProjectPath + "；实际项目 " + status.IdentityProjectPath
                        : outcome == "process_identity_mismatch"
                            ? "启动 PID " + (_launchState?.launcherPid ?? processId) + "；health PID " + status.IdentityServerProcessId + "；身份依据：" + identityError
                            : status.ErrorMessage;
                    UPilotServerRestartDiagnostics.EndStatusProbe(operationId, outcome,
                        status.StatusFailureStage, status.StatusCancellationReason, probeError);
                    probeEnded = true;
                    if (outcome != "success")
                        LogRestartProbeOutcome(operationId, "status_collection", probeStartedAtUtcMs,
                            outcome, probeError, status);
                    if (outcome == "process_identity_mismatch" || outcome == "project_identity_mismatch")
                    {
                        RecordRestartStartFailure(outcome, probeError);
                        return;
                    }
                    if (outcome == "lifecycle_canceled")
                    {
                        UPilotServerRestartDiagnostics.RecordCanceled(operationId,
                            status.StatusCancellationReason ?? "Editor 生命周期操作取消了状态探测");
                        return;
                    }
                    UPilotServerRestartDiagnostics.RecordBridgeSnapshot(operationId, UPilotBridge.Instance.GetStatus());
                }
                if (!string.Equals(operationId, _restartOperationId, StringComparison.Ordinal) ||
                    !UPilotServerRestartDiagnostics.IsActive(operationId))
                    return;
                if (_launchState?.verified == true && IsVerifiedRestartHealth(status, processId, _restartExpectedProjectPath))
                {
                    UPilotServerRestartDiagnostics.RecordHealthVerified(operationId, processId, status.IdentityProjectPath);
                    var bridge = UPilotBridge.Instance.GetStatus();
                    var issues = UPilotDeploymentDiagnostics.Observe(bridge, status, _launchState?.spec);
                    if (issues.Any(issue => issue.Blocking && issue.Confirmed && issue.Code != "authentication" && issue.Code != "timeout"))
                    {
                        RecordRestartStartFailure("deployment_mismatch", string.Join("\n", issues.Select(issue => issue.Message)));
                        return;
                    }
                    if (issues.Any(issue => issue.Blocking) ||
                        !bridge.IsWsOpen || !bridge.IsAuthenticated ||
                        !IsNewBridgeSession(_restartOldBridgeSessionId, bridge.SessionId))
                        return;
                    UPilotServerRestartDiagnostics.RecordDeploymentVerified(operationId);
                    UPilotServerRestartDiagnostics.MarkGate(operationId, "readonly", "running");
                    readOnlyStarted = true;
                    await VerifyReadOnlyRoundTripAsync(status, bridge.SessionId, _restartVerificationDeadlineUtcMs, cancellation);
                    if (cancellation.IsCancellationRequested) return;
                    if (!string.Equals(operationId, _restartOperationId, StringComparison.Ordinal) ||
                        !UPilotServerRestartDiagnostics.IsActive(operationId)) return;
                    var verifiedBridge = UPilotBridge.Instance.GetStatus();
                    if (!verifiedBridge.IsWsOpen || !verifiedBridge.IsAuthenticated ||
                        verifiedBridge.SessionId != bridge.SessionId) return;
                    if (_launchState != null)
                        CacheVerifiedStartupStatus(status, lifecycleGeneration, bridge.SessionId, () =>
                        {
                            UPilotServerRestartDiagnostics.RecordReadOnlyVerified(operationId);
                            if (UPilotServerRestartDiagnostics.Current?.status != "succeeded") return false;
                            _launchState.readyVerified = true;
                            _launchNeedsRevalidation = false;
                            PersistLaunchState();
                            return true;
                        });
                }
            }
            catch (LifecycleProbeDeferredException) { }
            catch (Exception ex)
            {
                if (cancellation.IsCancellationRequested || operationId != _restartOperationId) return;
                LogRestartProbeException(operationId,
                    readOnlyStarted ? "readonly_roundtrip" : "status_collection", probeStartedAtUtcMs, ex);
                if (string.Equals(operationId, _restartOperationId, StringComparison.Ordinal))
                {
                    if (!probeEnded && UPilotServerRestartDiagnostics.IsActive(operationId))
                        UPilotServerRestartDiagnostics.EndStatusProbe(operationId,
                            ex is TimeoutException || ex is TaskCanceledException ? "request_timeout" : "unknown",
                            "health_query", "", DescribeProbeException(ex));
                    else if (readOnlyStarted && UPilotServerRestartDiagnostics.IsActive(operationId))
                        UPilotServerRestartDiagnostics.MarkGate(operationId, "readonly", "running",
                            "readonly_retry", DescribeProbeException(ex));
                    // Transient probe timeouts may recover within the original maintenance budget.
                    if (ex is InvalidOperationException)
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
                    _restartNextProbeAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + RestartProbeIntervalMs;
                    if (!UPilotServerRestartDiagnostics.IsActive(operationId))
                        FinishRestartObservation();
                }
            }
        }

        private void LogRestartProbeOutcome(
            string operationId,
            string phase,
            long startedAtUtcMs,
            string outcome,
            string error,
            McpServerStatus status)
        {
            try
            {
                var key = phase + ":" + outcome + ":" + error;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (_restartLastLoggedError == key && now - _restartLastLoggedAt < 30000) return;
                _restartLastLoggedError = key;
                _restartLastLoggedAt = now;
                var elapsed = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedAtUtcMs);
                Logger.LogWarning("SERVER",
                    $"重启/查询诊断 source=Unity客户端 PID={_lifecycleProcessId} operation={operationId} phase={phase} elapsedMs={elapsed} " +
                    $"outcome={outcome} error={error} " +
                    BuildEditorObservationDiagnostic(status));
            }
            catch
            {
                // Diagnostic logging must never affect restart control flow or its result.
            }
        }

        private void LogRestartProbeException(
            string operationId,
            string phase,
            long startedAtUtcMs,
            Exception exception)
        {
            McpServerStatus cached;
            lock (_statusLock) cached = _cachedStatus;
            LogRestartProbeOutcome(operationId, phase, startedAtUtcMs, exception.GetType().Name,
                DescribeProbeException(exception), cached);
        }

        private static string BuildEditorObservationDiagnostic(McpServerStatus status)
        {
            return "editorObservation=" +
                   $"available:{status.EditorObservationAvailable}," +
                   $"status:{status.EditorObservationStatus ?? ""}," +
                   $"reason:{status.EditorObservationReason ?? ""}," +
                   $"observedAtMs:{status.EditorObservationObservedAtUtcMs}," +
                   $"session:{status.EditorObservationSessionId ?? ""}," +
                   $"epoch:{status.EditorObservationProducerEpoch ?? ""}," +
                   $"domain:{status.EditorObservationDomainGeneration}," +
                   $"pumpAgeMs:{status.EditorObservationPumpAgeMs}," +
                   $"heartbeatAgeMs:{status.EditorObservationHeartbeatAgeMs}," +
                   $"queueDepth:{status.EditorObservationQueueDepth}," +
                   $"lastCommand:{status.EditorObservationLastCommandId ?? ""}";
        }

        private static string TruncateDiagnostic(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "…";
        }

        internal static string ClassifyLaunchIdentityProbe(McpServerStatus status, bool resolved, string identityError)
        {
            if (status.ProcessOwnership == McpProcessOwnership.Foreign) return "process_identity_mismatch";
            var outcome = ClassifyRestartProbe(status);
            if (outcome != "success" || resolved) return outcome;
            // health 已出现但双端口/OS 身份尚未齐全，不将合法子进程与启动 PID 的差异当冲突。
            if (identityError == "service_ownership_unverified" ||
                (identityError ?? "").StartsWith("process_evidence_unavailable", StringComparison.Ordinal))
                return "process_identity_pending";
            return identityError == "project_identity_mismatch" ? "project_identity_mismatch" : "process_identity_mismatch";
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
                    if (error.Contains("TaskCanceledException") || error.Contains("TimeoutException") || error.Contains("A task was canceled")) return "request_timeout";
                    return "malformed_response";
                }
                if (status.StatusFailureStage == "port_probe") return "connect_failure";
                if (status.StatusFailureStage == "process_identity") return "process_identity_timeout";
                return "unknown";
            }
            return status.IdentityAvailable
                ? (status.IdentityIdentifiesUPilot ? "success" : "malformed_response")
                : "connect_failure";
        }

        internal static System.Net.Http.HttpClient CreateReadOnlyProbeClient()
        {
            var client = CreateLoopbackStatusClient();
            client.Timeout = TimeSpan.FromMilliseconds(7000);
            return client;
        }

        internal Task VerifyReadOnlyRoundTripAsync(McpServerStatus status, string bridgeSessionId, long deadlineUtcMs = 0, CancellationToken token = default, bool useRemainingRoundBudget = false)
        {
            token.ThrowIfCancellationRequested();
            if (deadlineUtcMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadlineUtcMs)
                throw new TimeoutException("Maintenance deadline elapsed.");
            if (status.ProcessId.GetValueOrDefault() <= 0 || status.Identity?.server_pid != status.ProcessId ||
                string.IsNullOrEmpty(status.Identity?.server_instance_id) ||
                !UPilotDeploymentDiagnostics.SamePath(status.IdentityProjectPath, UPilotProjectConfig.ProjectRoot))
                throw new InvalidOperationException("实际只读调用未通过：服务进程、实例或项目身份缺失。");
            // Bootstrap already performed the bounded read-only challenge for this exact connection.
            // Its receipt is revoked by disconnect, epoch/session changes, shutdown and lifetime changes.
            if (!UPilotBridge.Instance.TryGetBootstrapReceipt(bridgeSessionId, status.Identity.server_instance_id, out var receipt))
                throw new LifecycleProbeDeferredException();
            if (receipt.ServerProcessId != status.ProcessId ||
                !UPilotDeploymentDiagnostics.SamePath(receipt.ProjectPath, status.IdentityProjectPath))
                throw new InvalidOperationException("实际只读调用未通过：Bootstrap 项目身份不一致。");
            token.ThrowIfCancellationRequested();
            if (deadlineUtcMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadlineUtcMs)
                throw new TimeoutException("Maintenance deadline elapsed.");
            return Task.CompletedTask;
        }

        internal static int ReadParentProcessId(int processId)
        {
#if UNITY_EDITOR_WIN
            if (processId <= 0) return 0;
            var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
            if (snapshot == new IntPtr(-1) || snapshot == IntPtr.Zero) return 0;
            try
            {
                var entry = new RestartProcessEntry { size = (uint)Marshal.SizeOf(typeof(RestartProcessEntry)) };
                if (!Process32First(snapshot, ref entry)) return 0;
                do
                {
                    if (entry.processId == (uint)processId) return (int)entry.parentProcessId;
                } while (Process32Next(snapshot, ref entry));
                return 0;
            }
            finally { CloseHandle(snapshot); }
#else
            return 0;
#endif
        }

#if UNITY_EDITOR_WIN
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RestartProcessEntry
        {
            public uint size, usage, processId;
            public UIntPtr defaultHeapId;
            public uint moduleId, threads, parentProcessId;
            public int priority;
            public uint flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string executable;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32First(IntPtr snapshot, ref RestartProcessEntry entry);
        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32Next(IntPtr snapshot, ref RestartProcessEntry entry);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
#endif

        internal static bool IsVerifiedRestartHealth(
            McpServerStatus status,
            int expectedProcessId,
            string expectedProjectPath)
        {
            return expectedProcessId > 0 &&
                   IsVerifiedStartupHealth(status) &&
                   status.ProcessId == expectedProcessId &&
                   status.IdentityServerProcessId == expectedProcessId &&
                   SameProjectPath(status.IdentityProjectPath, expectedProjectPath);
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
            var terminal = UPilotServerRestartDiagnostics.Current;
            FinishServiceShutdown(_restartShutdownOwnerId, "restart_" + (terminal?.status ?? "unknown"));
            _restartShutdownOwnerId = "";
            if (_lifecycleOperationId == _restartOperationId)
                EndLifecyclePhase(_lifecycleGeneration, terminal?.status == "succeeded" ? "" : terminal?.errorCode ?? "restart_ended",
                    terminal?.errorCode == "SERVICE_RESTART_TIMEOUT" || terminal?.errorCode == "restart_verification_timeout" || terminal?.errorCode == "port_release_timeout");
            if (_launchState != null && !_launchState.verified &&
                UPilotServerRestartDiagnostics.Current?.operationId == _restartOperationId &&
                UPilotServerRestartDiagnostics.Current?.status != "running")
            {
                _launchState.terminal = true;
                _launchState.error = UPilotServerRestartDiagnostics.Current?.errorCode ?? "restart_ended";
                PersistLaunchState();
                StopLaunchObservation();
            }
            if (_restartObserveCallback != null)
            {
                EditorApplication.update -= _restartObserveCallback;
                _restartObserveCallback = null;
            }
            ClearRestartObservationState();
            NotifyStatusChanged();
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
                UPilotServerRestartDiagnostics.TimeoutMessage(UPilotServerRestartDiagnostics.Current, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), "", "",
                "Inspect current service identity and original maintenance diagnostics.");
            FinishRestartObservation();
        }

        private void ClearRestartObservationState()
        {
            _restartProbeCancellation?.Cancel();
            _restartProbeCancellation?.Dispose();
            _restartProbeCancellation = null;
            _restartLastLoggedError = null;
            _restartLastLoggedAt = 0;
            _pendingLaunchSpec = null;
            _pendingLaunchDeadline = 0;
            _restartOperationId = "";
            _restartOldBridgeSessionId = "";
            _restartExpectedProjectPath = "";
            _restoredRestartResetReceiptId = "";
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
            var allExited = true;
            for (var i = StoppingTargets.Count - 1; i >= 0; i--)
            {
                if (!StoppingTargets[i].HasExited) { allExited = false; continue; }
                StoppingTargets[i].Dispose(); StoppingTargets.RemoveAt(i);
            }
            for (var i = _stoppingProcesses.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (!_stoppingProcesses[i].HasExited) { allExited = false; continue; }
                }
                catch { allExited = false; continue; }
                _stoppingProcesses[i].Dispose();
                _stoppingProcesses.RemoveAt(i);
            }
            return allExited;
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

        internal static bool TryGetVerifiedOwnershipLogPath(ServerLaunchState state, string projectPath,
            int httpPort, int wsPort, out string logPath)
        {
            logPath = null;
            return state?.verified == true && state.launcherPid > 0 && state.launcherCreatedAt > 0 &&
                state.servicePid > 0 && state.serviceCreatedAt > 0 && !string.IsNullOrEmpty(state.instanceId) &&
                state.spec != null && state.spec.Validate().Length == 0 &&
                SameProjectPath(state.spec.projectPath, projectPath) &&
                state.spec.httpPort == httpPort && state.spec.wsPort == wsPort &&
                state.spec.TryGetLogFilePath(out logPath);
        }

        private McpProcessProbe ProbeMcpProcessOwnership(int httpPort, int wsPort, long deadlineUtcMs = 0,
            string expectedLogPath = null, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            deadlineUtcMs = ProcessQueryDeadline(deadlineUtcMs, 8000, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var launch = _launchState;
            var hasVerifiedLaunch = TryGetVerifiedOwnershipLogPath(launch, CurrentProjectRoot, httpPort, wsPort, out var frozenLogPath);
            expectedLogPath ??= hasVerifiedLaunch ? frozenLogPath : CurrentProjectLogPath;
            var constrainService = hasVerifiedLaunch && UPilotDeploymentDiagnostics.SamePath(expectedLogPath, frozenLogPath);
            var expectedPid = constrainService ? launch.servicePid : 0;
            var expectedCreatedAt = constrainService ? launch.serviceCreatedAt : 0;
            var portsByPid = SafeGetListeningPortsByPid(out var ownerQuerySucceeded, deadlineUtcMs, token);
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
            ProcessCommandLineReadResult unreadableCommandLine = null;
            foreach (var pid in candidatePids)
            {
                var commandLineRead = ProbeProcessCommandLine(pid, deadlineUtcMs, token: token, helperTimeoutMs: 5000);
                var commandLine = commandLineRead.LegacyValue;
                if (!firstPid.HasValue)
                {
                    firstPid = pid;
                    firstCommandLine = commandLine;
                }

                if (!IsUsableCommandLine(commandLine))
                {
                    allCommandLinesReadable = false;
                    unreadableCommandLine = commandLineRead;
                    continue;
                }

                if (IsCurrentProjectMcpCommandLine(commandLine, httpPort, wsPort, expectedLogPath))
                {
                    if (constrainService)
                    {
                        try
                        {
                            using var candidate = Process.GetProcessById(pid);
                            if (pid != expectedPid || candidate.HasExited ||
                                candidate.StartTime.ToUniversalTime().Ticks != expectedCreatedAt)
                                return new McpProcessProbe(McpProcessOwnership.Foreign, pid, commandLine,
                                    "监听进程与已核实的冻结服务身份不符");
                        }
                        catch (ThreadAbortException) { throw; }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            allCommandLinesReadable = false;
                            Logger.LifecycleFailure("process_identity_recheck_failed",
                                $"targetPid={pid} expectedPid={expectedPid} expectedCreatedAtTicks={expectedCreatedAt} httpPort={httpPort} wsPort={wsPort}",
                                ex, scope: "process-identity|" + pid);
                            continue;
                        }
                    }
                    return new McpProcessProbe(
                        McpProcessOwnership.CurrentUPilot,
                        pid,
                        commandLine,
                        "监听端口与 UPilot 启动参数匹配",
                        hasHttpOwner && hasWsOwner);
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
                allCommandLinesReadable ? "端口进程证据尚不完整" : "进程命令行暂不可读" +
                    (unreadableCommandLine != null ? "；" + unreadableCommandLine.SafeDetail : "；已核实进程的活体身份复查失败"));
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
            Func<bool> budgetExpired, out int verified, Func<string, bool> belongs = null)
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
                if (belongs != null ? belongs(cmdLine) : IsCurrentProjectMcpCommandLine(cmdLine, httpPort, wsPort))
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

        internal static bool IsCurrentProjectMcpCommandLine(string cmdLine, int httpPort, int wsPort, string expectedLogPath = null)
        {
            return IsUPilotMcpLike(cmdLine) &&
                   HasCommandLinePort(cmdLine, "--http-port", httpPort) &&
                   HasCommandLinePort(cmdLine, "--port", wsPort) &&
                   HasCurrentProjectLogPath(cmdLine, expectedLogPath);
        }

        private static bool HasCurrentProjectLogPath(string cmdLine, string expectedLogPath)
        {
            var match = Regex.Match(cmdLine,
                "(?:^|\\s)--log-file(?:\\s+|=)(?:\"([^\"]+)\"|(\\S+))",
                RegexOptions.IgnoreCase);
            if (!match.Success) return false;
            var path = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (!Path.IsPathRooted(path)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedLogPath ?? CurrentProjectLogPath),
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

        internal static int ProcessProbeWaitMs(long deadlineUtcMs, int ordinaryWaitMs) =>
            deadlineUtcMs > 0
                ? (int)Math.Max(0, Math.Min(int.MaxValue, deadlineUtcMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                : ordinaryWaitMs;

        private static Dictionary<int, List<int>> SafeGetListeningPortsByPid(out bool success, long deadlineUtcMs = 0, CancellationToken token = default)
        {
            var result = new Dictionary<int, List<int>>();
            success = false;
            token.ThrowIfCancellationRequested();
            if (deadlineUtcMs > 0 && ProcessProbeWaitMs(deadlineUtcMs, 0) == 0) return result;

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
                try
                {
                    if (!WaitForProcessQuery(() => proc.WaitForExit(0), ProcessQueryDeadline(deadlineUtcMs, 2000,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), token) ||
                        !WaitForProcessQuery(() => stdout.IsCompleted, ProcessQueryDeadline(deadlineUtcMs, 100,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), token))
                        return result;
                }
                finally
                {
                    try { if (!proc.HasExited) proc.Kill(); }
                    catch (ThreadAbortException) { throw; }
                    catch (Exception cleanup)
                    {
                        Logger.LifecycleFailure("process_port_helper_cleanup_failed", "provider=netstat",
                            cleanup, scope: "process-port-ownership");
                    }
                    _ = stdout.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    _ = stderr.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                }
                token.ThrowIfCancellationRequested();
                string output = stdout.GetAwaiter().GetResult();

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
            catch (ThreadAbortException) { throw; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
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

        internal static string GetProcessCommandLineForDiagnostics(int pid, long deadlineUtcMs = 0)
            => ProbeProcessCommandLine(pid, deadlineUtcMs).LegacyValue;
    }
}
