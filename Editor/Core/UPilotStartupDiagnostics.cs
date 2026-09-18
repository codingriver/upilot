// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable]
    internal sealed class UPilotStartupFocusSnapshot
    {
        public int targetProcessId;
        public int foregroundProcessId;
        public string state;
        public int mainThreadId;
        public long updateCount;
    }

    [Serializable]
    internal sealed class UPilotStartupMilestone
    {
        public string name;
        public long observedAtUtcMs;
        public long elapsedFromProcessStartMs;
        public string source;
        public int serverProcessId;
        public string bridgeSessionId;
        public UPilotStartupFocusSnapshot focus;
    }

    [Serializable]
    internal sealed class UPilotStartupBlockingReason
    {
        public string code;
        public string detail;
        public long observedAtUtcMs;
    }

    [Serializable]
    internal sealed class UPilotBackgroundExecutionEvidence
    {
        public string status = "not_started";
        public long observedAtUtcMs;
        public long elapsedFromReadyMs;
        public int sampleCount;
        public UPilotStartupFocusSnapshot focus;
        public string statement;
    }

    [Serializable]
    internal sealed class UPilotStartupRecord
    {
        public int schemaVersion = 2;
        public string projectPath;
        public int processId;
        public long processCreatedAt;
        public long processStartUtcMs;
        public long observedAtUtcMs;
        public long diagnosticsStartedAtUtcMs;
        public long healthObservationStartedAtUtcMs;
        public int healthProbeCount;
        public int observedServerProcessId;
        public string source = "unity.startup_diagnostics";
        public List<UPilotStartupMilestone> milestones = new();
        public List<UPilotStartupBlockingReason> blockingReasons = new();
        public string healthObservationStatus = "not_started";
        public string serverStartCycleId = "";
        public string serverStartRetryStatus = "not_started";
        public int serverStartAttemptCount;
        public long lastServerStartAttemptAtUtcMs;
        public long nextServerStartAttemptAtUtcMs;
        public string lastServerStartReason = "";
        public UPilotBackgroundExecutionEvidence backgroundExecution = new();
        public bool failureSummaryLogged;
        public bool recoverySummaryLogged;
    }

    internal static class UPilotStartupDiagnostics
    {
        private static readonly object Sync = new();
        private static readonly UTF8Encoding Utf8NoBom = new(false);
        private static readonly int[] HealthProbeOffsetsMs = { 0, 5000, 15000, 30000 };
        private static readonly HashSet<string> ExpectedMilestones = new(StringComparer.Ordinal)
        {
            "bootstrap_entered",
            "first_editor_update",
            "server_healthy",
            "bridge_authenticated",
            "editor_ready",
        };

        private const long StartupObservationWindowMs = 5 * 60 * 1000;
        private const long BackgroundSampleIntervalMs = 10 * 1000;
        private const long BackgroundSampleWindowMs = 5 * 60 * 1000;

        private static UPilotStartupRecord s_record;
        private static string s_path;
        private static int s_mainThreadId;
        private static long s_updateCount;
        private static long s_observationDeadlineUtcMs;
        private static long s_healthObservationStartedUtcMs;
        private static int s_healthProbeIndex;
        private static bool s_healthProbeRunning;
        private static bool s_supplementalHealthPending;
        private static bool s_supplementalHealthUsed;
        private static UPilotMcpServerManager s_serverManager;
        private static long s_readyAtUtcMs;
        private static long s_nextBackgroundSampleUtcMs;
        private static int s_backgroundSampleCount;
        private static bool s_updateHooked;
        private static bool s_writeFailureReported;

        internal static string RecordPath => Path.Combine(
            Directory.GetParent(Application.dataPath)?.FullName ?? ".",
            "Library", "UPilot", "startup.json");

        internal static void EnterBootstrap()
        {
            if (IsAssetImportWorkerProcess()) return;
            lock (Sync)
            {
                EnsureRecordLocked();
                s_mainThreadId = Thread.CurrentThread.ManagedThreadId;
                var now = UtcNowMs();
                if (s_record.diagnosticsStartedAtUtcMs <= 0)
                    s_record.diagnosticsStartedAtUtcMs = now;
                s_observationDeadlineUtcMs = s_record.diagnosticsStartedAtUtcMs + StartupObservationWindowMs;
                RecordMilestoneLocked("bootstrap_entered", "UPilotBootstrap.static_constructor", 0, "", now);
                EnsureUpdateHookLocked();
            }
        }

        internal static void BeginServerObservation(UPilotMcpServerManager manager, bool restartProbeWindow = false)
        {
            if (manager == null || IsAssetImportWorkerProcess()) return;
            lock (Sync)
            {
                EnsureRecordLocked();
                s_serverManager = manager;
                if (restartProbeWindow || s_record.healthObservationStartedAtUtcMs <= 0)
                {
                    s_record.healthObservationStartedAtUtcMs = UtcNowMs();
                    s_record.healthProbeCount = 0;
                }
                s_healthObservationStartedUtcMs = s_record.healthObservationStartedAtUtcMs;
                s_healthProbeIndex = Math.Max(0, Math.Min(s_record.healthProbeCount, HealthProbeOffsetsMs.Length));
                s_record.healthObservationStatus = "observing";
                PersistLocked();
                EnsureUpdateHookLocked();
            }
        }

        internal static void RecordServerProcessStarted(int processId)
        {
            if (processId <= 0 || IsAssetImportWorkerProcess()) return;
            lock (Sync)
            {
                EnsureRecordLocked();
                if (s_record.observedServerProcessId == processId) return;
                s_record.observedServerProcessId = processId;
                s_record.observedAtUtcMs = UtcNowMs();
                PersistLocked();
            }
        }

        internal static void RecordBlockingReason(string code, string detail)
        {
            if (IsAssetImportWorkerProcess()) return;
            code = string.IsNullOrWhiteSpace(code) ? "startup_blocked" : code.Trim();
            detail ??= "";
            lock (Sync)
            {
                EnsureRecordLocked();
                if (s_record.blockingReasons.Exists(item => string.Equals(item.code, code, StringComparison.Ordinal)))
                    return;
                if (s_record.blockingReasons.Count < 8)
                    s_record.blockingReasons.Add(new UPilotStartupBlockingReason
                {
                    code = code,
                    detail = detail,
                    observedAtUtcMs = UtcNowMs(),
                });
                if (!s_record.failureSummaryLogged)
                {
                    s_record.failureSummaryLogged = true;
                    Logger.AppendLine($"[WARN ] [STARTUP ] startup_incomplete reason={code} detail={Sanitize(detail)}");
                }
                PersistLocked();
            }
        }

        internal static bool TryBeginServerStartAttempt(
            long now,
            int maxAttempts,
            int[] retryDelaysMs,
            out int attemptNumber)
        {
            attemptNumber = 0;
            if (IsAssetImportWorkerProcess()) return false;
            lock (Sync)
            {
                EnsureRecordLocked();
                if (string.Equals(s_record.serverStartRetryStatus, "blocked", StringComparison.Ordinal))
                    return false;
                if (s_healthProbeRunning)
                    return false;
                if (s_record.serverStartAttemptCount >= Math.Max(1, maxAttempts))
                {
                    if (!string.Equals(s_record.serverStartRetryStatus, "exhausted", StringComparison.Ordinal))
                    {
                        s_record.serverStartRetryStatus = "exhausted";
                        s_record.nextServerStartAttemptAtUtcMs = 0;
                        PersistLocked();
                    }
                    return false;
                }
                if (s_record.nextServerStartAttemptAtUtcMs > 0 && now < s_record.nextServerStartAttemptAtUtcMs)
                    return false;

                attemptNumber = ++s_record.serverStartAttemptCount;
                s_record.lastServerStartAttemptAtUtcMs = now;
                s_record.lastServerStartReason = "automatic_start";
                s_record.serverStartRetryStatus = "observing";
                var delayIndex = attemptNumber - 1;
                s_record.nextServerStartAttemptAtUtcMs = attemptNumber < maxAttempts &&
                    retryDelaysMs != null && delayIndex >= 0 && delayIndex < retryDelaysMs.Length
                    ? now + Math.Max(0, retryDelaysMs[delayIndex])
                    : 0;
                PersistLocked();
                return true;
            }
        }

        internal static void RecordServerStartAttemptReason(string reason)
        {
            if (IsAssetImportWorkerProcess()) return;
            lock (Sync)
            {
                EnsureRecordLocked();
                s_record.lastServerStartReason = string.IsNullOrWhiteSpace(reason) ? "automatic_start" : reason.Trim();
                PersistLocked();
            }
        }

        internal static void MarkServerStartRetryBlocked(string reason)
        {
            if (IsAssetImportWorkerProcess()) return;
            lock (Sync)
            {
                EnsureRecordLocked();
                s_record.serverStartRetryStatus = "blocked";
                s_record.lastServerStartReason = string.IsNullOrWhiteSpace(reason) ? "startup_blocked" : reason.Trim();
                s_record.nextServerStartAttemptAtUtcMs = 0;
                PersistLocked();
            }
        }

        internal static void BeginServerStartCycle(string cycleId)
        {
            if (string.IsNullOrWhiteSpace(cycleId) || IsAssetImportWorkerProcess()) return;
            lock (Sync)
            {
                EnsureRecordLocked();
                if (string.Equals(s_record.serverStartCycleId, cycleId, StringComparison.Ordinal)) return;
                s_record.serverStartCycleId = cycleId;
                s_record.serverStartRetryStatus = "not_started";
                s_record.serverStartAttemptCount = 0;
                s_record.lastServerStartAttemptAtUtcMs = 0;
                s_record.nextServerStartAttemptAtUtcMs = 0;
                s_record.lastServerStartReason = "";
                s_record.healthObservationStatus = "not_started";
                s_record.healthObservationStartedAtUtcMs = 0;
                s_record.healthProbeCount = 0;
                s_healthObservationStartedUtcMs = 0;
                s_healthProbeIndex = 0;
                s_healthProbeRunning = false;
                PersistLocked();
            }
        }

        internal static bool IsServerStartRetryFinished
        {
            get
            {
                if (IsAssetImportWorkerProcess()) return true;
                lock (Sync)
                {
                    EnsureRecordLocked();
                    return string.Equals(s_record.serverStartRetryStatus, "succeeded", StringComparison.Ordinal) ||
                           string.Equals(s_record.serverStartRetryStatus, "blocked", StringComparison.Ordinal) ||
                           string.Equals(s_record.serverStartRetryStatus, "exhausted", StringComparison.Ordinal);
                }
            }
        }

        internal static void ObserveBridgeAuthenticated(string sessionId)
        {
            if (IsAssetImportWorkerProcess()) return;
            lock (Sync)
            {
                EnsureRecordLocked();
                RecordMilestoneLocked(
                    "bridge_authenticated",
                    "bridge.session_hello.accepted",
                    0,
                    sessionId ?? "",
                    UtcNowMs());
                if (!string.Equals(s_record.healthObservationStatus, "healthy", StringComparison.Ordinal) &&
                    !s_supplementalHealthUsed)
                    s_supplementalHealthPending = true;
            }
        }

        internal static void ObserveEditorState(bool ready, bool authoritative, bool isStale, string sessionId)
        {
            if (IsAssetImportWorkerProcess()) return;
            lock (Sync)
            {
                EnsureRecordLocked();
                if (s_supplementalHealthPending) EnsureUpdateHookLocked();
                if (!ready || !authoritative || isStale || HasMilestoneLocked("editor_ready"))
                    return;

                var now = UtcNowMs();
                if (!RecordMilestoneLocked("editor_ready", "bridge.editor_execution_state", 0, sessionId ?? "", now))
                    return;
                s_readyAtUtcMs = now;
                s_nextBackgroundSampleUtcMs = now + BackgroundSampleIntervalMs;
                s_backgroundSampleCount = 0;
                s_record.backgroundExecution = new UPilotBackgroundExecutionEvidence
                {
                    status = "pending",
                    statement = "Waiting for a sampled main-thread update while the Unity process is not foreground.",
                };
                if (s_record.failureSummaryLogged && !s_record.recoverySummaryLogged)
                {
                    s_record.recoverySummaryLogged = true;
                    Logger.AppendLine("[INFO ] [STARTUP ] startup_recovered stage=editor_ready");
                }
                PersistLocked();
                EnsureUpdateHookLocked();
            }
        }

        private static void OnEditorUpdate()
        {
            if (IsAssetImportWorkerProcess()) return;
            UPilotMcpServerManager manager = null;
            var launchHealthProbe = false;
            var finalHealthProbe = false;
            lock (Sync)
            {
                EnsureRecordLocked();
                s_updateCount++;
                var now = UtcNowMs();
                RecordMilestoneLocked("first_editor_update", "EditorApplication.update", 0, "", now);

                if (!string.Equals(s_record.healthObservationStatus, "healthy", StringComparison.Ordinal) &&
                    !s_healthProbeRunning && s_serverManager != null)
                {
                    if (s_healthProbeIndex < HealthProbeOffsetsMs.Length &&
                        now >= s_healthObservationStartedUtcMs + HealthProbeOffsetsMs[s_healthProbeIndex])
                    {
                        s_healthProbeIndex++;
                        s_record.healthProbeCount = s_healthProbeIndex;
                        s_healthProbeRunning = true;
                        manager = s_serverManager;
                        launchHealthProbe = true;
                        finalHealthProbe = s_healthProbeIndex >= HealthProbeOffsetsMs.Length;
                    }
                    else if (s_supplementalHealthPending && !s_supplementalHealthUsed)
                    {
                        s_supplementalHealthPending = false;
                        s_supplementalHealthUsed = true;
                        s_healthProbeRunning = true;
                        manager = s_serverManager;
                        launchHealthProbe = true;
                        finalHealthProbe = true;
                    }
                }

                ObserveBackgroundExecutionLocked(now);
                if (s_healthObservationStartedUtcMs > 0 &&
                    now >= s_healthObservationStartedUtcMs + HealthProbeOffsetsMs[HealthProbeOffsetsMs.Length - 1] + 5000 &&
                    string.Equals(s_record.healthObservationStatus, "observing", StringComparison.Ordinal))
                {
                    s_record.healthObservationStatus = "incomplete";
                    PersistLocked();
                }
                RemoveUpdateHookIfFinishedLocked(now);
            }

            if (launchHealthProbe)
                _ = ProbeServerHealthAsync(manager, finalHealthProbe);
        }

        private static async Task ProbeServerHealthAsync(UPilotMcpServerManager manager, bool finalProbe)
        {
            McpServerStatus status = default;
            Exception failure = null;
            try
            {
                status = await manager.GetFreshStatusAsync();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            lock (Sync)
            {
                s_healthProbeRunning = false;
                if (failure != null)
                {
                    RecordBlockingReason("server_health_probe_exception", failure.Message);
                }
                else if (UPilotMcpServerManager.IsVerifiedStartupHealth(status))
                {
                    s_record.healthObservationStatus = "healthy";
                    s_record.observedServerProcessId = status.HealthServerProcessId;
                    MarkServerStartRetrySucceededLocked();
                    RecordMilestoneLocked(
                        "server_healthy",
                        "mcp_server.http_health",
                        status.HealthServerProcessId,
                        "",
                        UtcNowMs());
                    PersistLocked();
                }
                else if (finalProbe)
                {
                    s_record.healthObservationStatus = "incomplete";
                    PersistLocked();
                }
            }
        }

        private static void ObserveBackgroundExecutionLocked(long now)
        {
            if (s_readyAtUtcMs <= 0 || s_record.backgroundExecution == null ||
                !string.Equals(s_record.backgroundExecution.status, "pending", StringComparison.Ordinal))
                return;
            if (now - s_readyAtUtcMs >= BackgroundSampleWindowMs)
            {
                s_record.backgroundExecution.status = "not_observed";
                s_record.backgroundExecution.sampleCount = s_backgroundSampleCount;
                s_record.backgroundExecution.statement =
                    "No sampled unfocused main-thread execution was observed before the bounded window elapsed; this is not evidence that background execution is unsupported.";
                PersistLocked();
                return;
            }
            if (now < s_nextBackgroundSampleUtcMs) return;

            s_nextBackgroundSampleUtcMs = now + BackgroundSampleIntervalMs;
            s_backgroundSampleCount++;
            var focus = CaptureFocusSnapshot(Process.GetCurrentProcess().Id, s_updateCount, s_mainThreadId);
            if (!string.Equals(focus.state, "unfocused", StringComparison.Ordinal)) return;

            s_record.backgroundExecution.status = "observed";
            s_record.backgroundExecution.observedAtUtcMs = now;
            s_record.backgroundExecution.elapsedFromReadyMs = Math.Max(0, now - s_readyAtUtcMs);
            s_record.backgroundExecution.sampleCount = s_backgroundSampleCount;
            s_record.backgroundExecution.focus = focus;
            s_record.backgroundExecution.statement =
                "At this sample, Unity executed the Editor update on the main thread while its process did not own the foreground window.";
            Logger.AppendLine(
                $"[INFO ] [STARTUP ] unfocused_execution_observed targetPid={focus.targetProcessId} " +
                $"foregroundPid={focus.foregroundProcessId} updateCount={focus.updateCount}");
            PersistLocked();
        }

        private static bool RecordMilestoneLocked(
            string name,
            string source,
            int serverProcessId,
            string sessionId,
            long now)
        {
            if (!ExpectedMilestones.Contains(name) || HasMilestoneLocked(name)) return false;
            var focus = CaptureFocusSnapshot(s_record.processId, s_updateCount, s_mainThreadId);
            var milestone = new UPilotStartupMilestone
            {
                name = name,
                observedAtUtcMs = now,
                elapsedFromProcessStartMs = Math.Max(0, now - s_record.processStartUtcMs),
                source = source ?? "",
                serverProcessId = serverProcessId,
                bridgeSessionId = sessionId ?? "",
                focus = focus,
            };
            s_record.milestones.Add(milestone);
            Logger.AppendLine(
                $"[INFO ] [STARTUP ] milestone={name} elapsedMs={milestone.elapsedFromProcessStartMs} " +
                $"targetPid={focus.targetProcessId} foregroundPid={focus.foregroundProcessId} " +
                $"focus={focus.state} mainThreadId={focus.mainThreadId} updateCount={focus.updateCount} " +
                $"serverPid={serverProcessId} sessionId={Sanitize(sessionId)} source={source}");
            PersistLocked();
            return true;
        }

        private static bool HasMilestoneLocked(string name) =>
            s_record?.milestones?.Exists(item => string.Equals(item.name, name, StringComparison.Ordinal)) == true;

        private static void MarkServerStartRetrySucceededLocked()
        {
            if (s_record == null) return;
            s_record.serverStartRetryStatus = "succeeded";
            s_record.nextServerStartAttemptAtUtcMs = 0;
        }

        private static void EnsureRecordLocked()
        {
            if (s_record != null) return;
            s_path = RecordPath;
            using var process = Process.GetCurrentProcess();
            var projectPath = Path.GetFullPath(Directory.GetParent(Application.dataPath)?.FullName ?? ".")
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var processCreatedAt = 0L;
            var processStartUtcMs = 0L;
            try
            {
                var startUtc = process.StartTime.ToUniversalTime();
                processCreatedAt = startUtc.ToFileTimeUtc();
                processStartUtcMs = new DateTimeOffset(startUtc).ToUnixTimeMilliseconds();
            }
            catch (Exception ex)
            {
                ReportWriteFailureOnce("读取 Unity 进程创建时间失败", ex);
            }

            var restoredExistingRecord = TryLoadRecord(
                s_path, projectPath, process.Id, processCreatedAt, out var restored, out _);
            if (restoredExistingRecord)
                s_record = restored;
            else
                s_record = CreateRecord(projectPath, process.Id, processCreatedAt, processStartUtcMs);
            s_updateCount = LatestUpdateCount(s_record);
            s_healthObservationStartedUtcMs = s_record.healthObservationStartedAtUtcMs;
            s_healthProbeIndex = Math.Max(0, Math.Min(s_record.healthProbeCount, HealthProbeOffsetsMs.Length));
            var ready = s_record.milestones.Find(item => string.Equals(item.name, "editor_ready", StringComparison.Ordinal));
            if (ready != null)
            {
                s_readyAtUtcMs = ready.observedAtUtcMs;
                if (string.Equals(s_record.backgroundExecution?.status, "pending", StringComparison.Ordinal))
                    s_nextBackgroundSampleUtcMs = UtcNowMs() + BackgroundSampleIntervalMs;
            }
            if (restoredExistingRecord)
                PersistLocked();
        }

        private static UPilotStartupRecord CreateRecord(
            string projectPath,
            int processId,
            long processCreatedAt,
            long processStartUtcMs) => new()
        {
            projectPath = projectPath,
            processId = processId,
            processCreatedAt = processCreatedAt,
            processStartUtcMs = processStartUtcMs,
            observedAtUtcMs = UtcNowMs(),
        };

        private static long LatestUpdateCount(UPilotStartupRecord record)
        {
            long latest = 0;
            if (record?.milestones == null) return latest;
            foreach (var milestone in record.milestones)
                latest = Math.Max(latest, milestone?.focus?.updateCount ?? 0);
            return latest;
        }

        private static void PersistLocked()
        {
            if (s_record == null || string.IsNullOrEmpty(s_path)) return;
            s_record.observedAtUtcMs = UtcNowMs();
            if (!TryWriteRecord(s_record, s_path, out var error) && !s_writeFailureReported)
            {
                s_writeFailureReported = true;
                Logger.LogWarning("STARTUP", $"启动诊断写入失败（后续同类异常不再刷屏）: {error}");
            }
        }

        private static bool TryWriteRecord(UPilotStartupRecord record, string path, out string error)
        {
            var temporary = string.Empty;
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, JsonUtility.ToJson(record, true), Utf8NoBom);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrEmpty(temporary) && File.Exists(temporary)) File.Delete(temporary);
                }
                catch { }
            }
        }

        private static bool TryLoadRecord(
            string path,
            string projectPath,
            int processId,
            long processCreatedAt,
            out UPilotStartupRecord record,
            out string reason)
        {
            record = null;
            reason = "missing";
            try
            {
                if (!File.Exists(path)) return false;
                var loaded = JsonUtility.FromJson<UPilotStartupRecord>(File.ReadAllText(path, Encoding.UTF8));
                if (loaded == null)
                {
                    reason = "corrupt";
                    return false;
                }
                if (!SamePath(loaded.projectPath, projectPath) || loaded.processId != processId ||
                    loaded.processCreatedAt <= 0 || loaded.processCreatedAt != processCreatedAt)
                {
                    reason = "identity_mismatch";
                    return false;
                }
                loaded.milestones ??= new List<UPilotStartupMilestone>();
                loaded.blockingReasons ??= new List<UPilotStartupBlockingReason>();
                loaded.backgroundExecution ??= new UPilotBackgroundExecutionEvidence();
                loaded.schemaVersion = Math.Max(2, loaded.schemaVersion);
                loaded.serverStartRetryStatus = string.IsNullOrWhiteSpace(loaded.serverStartRetryStatus)
                    ? "not_started"
                    : loaded.serverStartRetryStatus;
                loaded.lastServerStartReason ??= "";
                loaded.serverStartCycleId ??= "";
                if (loaded.milestones.Exists(item =>
                        string.Equals(item?.name, "server_healthy", StringComparison.Ordinal)))
                {
                    loaded.serverStartRetryStatus = "succeeded";
                    loaded.nextServerStartAttemptAtUtcMs = 0;
                }
                record = loaded;
                reason = "current";
                return true;
            }
            catch (Exception ex)
            {
                reason = "corrupt: " + ex.Message;
                return false;
            }
        }

        private static bool SamePath(string left, string right)
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

        private static UPilotStartupFocusSnapshot CaptureFocusSnapshot(int targetProcessId, long updateCount, int mainThreadId) =>
            CaptureFocusSnapshot(targetProcessId, updateCount, mainThreadId, GetForegroundWindow, GetWindowProcessId);

        internal static UPilotStartupFocusSnapshot CaptureFocusSnapshot(
            int targetProcessId,
            long updateCount,
            int mainThreadId,
            Func<IntPtr> foregroundWindow,
            Func<IntPtr, int> windowProcessId)
        {
            var snapshot = new UPilotStartupFocusSnapshot
            {
                targetProcessId = targetProcessId,
                foregroundProcessId = 0,
                state = "unknown",
                mainThreadId = mainThreadId,
                updateCount = updateCount,
            };
            if (Application.platform != RuntimePlatform.WindowsEditor) return snapshot;
            try
            {
                var foreground = foregroundWindow();
                if (foreground == IntPtr.Zero) return snapshot;
                snapshot.foregroundProcessId = windowProcessId(foreground);
                if (snapshot.foregroundProcessId <= 0) return snapshot;
                snapshot.state = snapshot.foregroundProcessId == targetProcessId ? "focused" : "unfocused";
            }
            catch
            {
                snapshot.state = "unknown";
            }
            return snapshot;
        }

        private static int GetWindowProcessId(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out var processId);
            return unchecked((int)processId);
        }

        private static void EnsureUpdateHookLocked()
        {
            if (s_updateHooked) return;
            EditorApplication.update += OnEditorUpdate;
            s_updateHooked = true;
        }

        private static void RemoveUpdateHookIfFinishedLocked(long now)
        {
            var healthFinished = string.Equals(s_record.healthObservationStatus, "healthy", StringComparison.Ordinal) ||
                                 string.Equals(s_record.healthObservationStatus, "incomplete", StringComparison.Ordinal) ||
                                 string.Equals(s_record.serverStartRetryStatus, "blocked", StringComparison.Ordinal) ||
                                 string.Equals(s_record.serverStartRetryStatus, "exhausted", StringComparison.Ordinal);
            var backgroundFinished = s_readyAtUtcMs <= 0
                ? now >= s_observationDeadlineUtcMs
                : !string.Equals(s_record.backgroundExecution?.status, "pending", StringComparison.Ordinal);
            if (!healthFinished || !backgroundFinished || s_healthProbeRunning) return;
            EditorApplication.update -= OnEditorUpdate;
            s_updateHooked = false;
        }

        private static void ReportWriteFailureOnce(string context, Exception ex)
        {
            if (s_writeFailureReported) return;
            s_writeFailureReported = true;
            Logger.LogWarning("STARTUP", $"{context}: {ex.Message}");
        }

        private static string Sanitize(string value) =>
            (value ?? "").Replace('\r', ' ').Replace('\n', ' ');

        private static bool IsAssetImportWorkerProcess()
        {
            try
            {
                return AssetDatabase.IsAssetImportWorkerProcess();
            }
            catch
            {
                return false;
            }
        }

        private static long UtcNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        internal static UPilotStartupRecord CreateRecordForTests(
            string projectPath,
            int processId,
            long processCreatedAt,
            long processStartUtcMs) =>
            CreateRecord(projectPath, processId, processCreatedAt, processStartUtcMs);

        internal static bool AddMilestoneForTests(
            UPilotStartupRecord record,
            string name,
            long observedAtUtcMs,
            UPilotStartupFocusSnapshot focus)
        {
            if (record == null || !ExpectedMilestones.Contains(name) ||
                record.milestones.Exists(item => string.Equals(item.name, name, StringComparison.Ordinal)))
                return false;
            record.milestones.Add(new UPilotStartupMilestone
            {
                name = name,
                observedAtUtcMs = observedAtUtcMs,
                elapsedFromProcessStartMs = Math.Max(0, observedAtUtcMs - record.processStartUtcMs),
                source = "test",
                focus = focus,
            });
            return true;
        }

        internal static bool TryWriteRecordForTests(UPilotStartupRecord record, string path, out string error) =>
            TryWriteRecord(record, path, out error);

        internal static bool TryLoadRecordForTests(
            string path,
            string projectPath,
            int processId,
            long processCreatedAt,
            out UPilotStartupRecord record,
            out string reason) =>
            TryLoadRecord(path, projectPath, processId, processCreatedAt, out record, out reason);

        internal static bool ShouldTakeBackgroundSampleForTests(long now, long nextSampleAt) =>
            now >= nextSampleAt;

        internal static bool TryBeginServerStartAttemptForTests(
            UPilotStartupRecord record,
            long now,
            int maxAttempts,
            int[] retryDelaysMs,
            out int attemptNumber)
        {
            attemptNumber = 0;
            if (record == null || record.serverStartAttemptCount >= Math.Max(1, maxAttempts))
            {
                if (record != null)
                {
                    record.serverStartRetryStatus = "exhausted";
                    record.nextServerStartAttemptAtUtcMs = 0;
                }
                return false;
            }
            if (record.nextServerStartAttemptAtUtcMs > 0 && now < record.nextServerStartAttemptAtUtcMs)
                return false;

            attemptNumber = ++record.serverStartAttemptCount;
            record.lastServerStartAttemptAtUtcMs = now;
            record.lastServerStartReason = "automatic_start";
            record.serverStartRetryStatus = "observing";
            var delayIndex = attemptNumber - 1;
            record.nextServerStartAttemptAtUtcMs = attemptNumber < maxAttempts &&
                retryDelaysMs != null && delayIndex >= 0 && delayIndex < retryDelaysMs.Length
                ? now + Math.Max(0, retryDelaysMs[delayIndex])
                : 0;
            return true;
        }

        internal static void BeginServerStartCycleForTests(UPilotStartupRecord record, string cycleId)
        {
            if (record == null || string.IsNullOrWhiteSpace(cycleId) ||
                string.Equals(record.serverStartCycleId, cycleId, StringComparison.Ordinal))
                return;
            record.serverStartCycleId = cycleId;
            record.serverStartRetryStatus = "not_started";
            record.serverStartAttemptCount = 0;
            record.lastServerStartAttemptAtUtcMs = 0;
            record.nextServerStartAttemptAtUtcMs = 0;
            record.lastServerStartReason = "";
            record.healthObservationStatus = "not_started";
            record.healthObservationStartedAtUtcMs = 0;
            record.healthProbeCount = 0;
        }
    }
}
