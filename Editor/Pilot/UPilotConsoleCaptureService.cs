// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable]
    public sealed class ConsoleCaptureStartMessage
    {
        public ConsoleCaptureStartPayload payload;
    }

    [Serializable]
    public sealed class ConsoleCaptureStartPayload
    {
        public string title;
        public string path;
        public bool includeStackTrace = true;
        public bool excludeUPilot = true;
        public bool clearUnityConsole;
        public int flushIntervalMs = 1000;
        public long maxFileBytes = 50L * 1024L * 1024L;
        public bool allowOutsideProject;
    }

    [Serializable]
    public sealed class ConsoleCaptureSessionMessage
    {
        public ConsoleCaptureSessionPayload payload;
    }

    [Serializable]
    public sealed class ConsoleCaptureSessionPayload
    {
        public string sessionId;
    }

    [Serializable]
    public sealed class ConsoleCaptureReadMessage
    {
        public ConsoleCaptureReadPayload payload;
    }

    [Serializable]
    public sealed class ConsoleCaptureReadPayload
    {
        public string sessionId;
        public long afterSequence = -1;
        public int count = 200;
        public string logType;
        public bool includeStackTrace = true;
        public string[] contains;
        public bool containsAll;
        public bool newestFirst;
    }

    [Serializable]
    public sealed class ConsoleCaptureListMessage
    {
        public ConsoleCaptureListPayload payload;
    }

    [Serializable]
    public sealed class ConsoleCaptureListPayload
    {
        public int count = 20;
        public bool includeActive = true;
    }

    [Serializable]
    public sealed class ConsoleCaptureCleanupMessage
    {
        public ConsoleCaptureCleanupPayload payload;
    }

    [Serializable]
    public sealed class ConsoleCaptureCleanupPayload
    {
        public int olderThanDays = 14;
        public int keepLatest = 20;
        public bool dryRun = true;
        public string confirmToken;
    }

    [Serializable]
    public sealed class ConsoleCaptureRecord
    {
        public long sequence;
        public long timestampUtcMs;
        public string logType;
        public string message;
        public string stackTrace;
        public bool isPlaying;
        public int threadId;
    }

    [Serializable]
    public sealed class ConsoleCaptureManifest
    {
        public bool ok = true;
        public string sessionId;
        public string title;
        public string directory;
        public string jsonlPath;
        public string manifestPath;
        public string summaryPath;
        public bool active;
        public bool includeStackTrace;
        public bool excludeUPilot;
        public int flushIntervalMs;
        public long maxFileBytes;
        public long startedAtUtcMs;
        public long finishedAtUtcMs;
        public double durationSec;
        public long nextSequence;
        public long totalCount;
        public long logCount;
        public long warningCount;
        public long errorCount;
        public long exceptionCount;
        public long assertCount;
        public long droppedCount;
        public long fileBytes;
        public int segmentCount = 1;
        public string sha256;
        public string lastError;
    }

    [Serializable]
    public sealed class ConsoleCaptureResult
    {
        public bool ok;
        public string action;
        public string error;
        public ConsoleCaptureManifest session;
    }

    [Serializable]
    public sealed class ConsoleCaptureReadResult
    {
        public bool ok;
        public string action;
        public string error;
        public string sessionId;
        public List<ConsoleCaptureRecord> logs = new();
        public long afterSequence;
        public long nextSequence;
        public int matchedCount;
        public bool truncated;
    }

    [Serializable]
    public sealed class ConsoleCaptureListResult
    {
        public bool ok;
        public string action;
        public string error;
        public List<ConsoleCaptureManifest> sessions = new();
    }

    [Serializable]
    public sealed class ConsoleCaptureCleanupResult
    {
        public bool ok;
        public string action;
        public string error;
        public bool dryRun;
        public string confirmToken;
        public List<string> directories = new();
        public long totalBytes;
        public int deletedCount;
    }

    [Serializable]
    public sealed class ConsoleCaptureSessionIndexEntry
    {
        public string sessionId;
        public string directory;
        public long startedAtUtcMs;
    }

    [Serializable]
    public sealed class ConsoleCaptureSessionIndex
    {
        public List<ConsoleCaptureSessionIndexEntry> sessions = new();
    }

    /// <summary>
    /// Console 持久化采集服务。Unity 侧持续写 JSONL，MCP 只负责控制和读取会话。
    /// </summary>
    public sealed class UPilotConsoleCaptureService
    {
        private const string CaptureRootRelative = "Log/UPilotConsole";
        private const string ActiveDirectorySessionKey = "UPilot.ConsoleCapture.ActiveDirectory";
        private const string CleanupTokenSessionKey = "UPilot.ConsoleCapture.CleanupToken";
        private const string CleanupTargetsSessionKey = "UPilot.ConsoleCapture.CleanupTargets";
        private const string SessionIndexFileName = "session-index.json";
        private const int MaxPendingRecords = 10000;
        private const int MaxIndexedCustomSessions = 1000;

        private sealed class ActiveCapture
        {
            public ConsoleCaptureManifest Manifest;
            public readonly Queue<ConsoleCaptureRecord> Pending = new();
            public double LastFlushTime;
        }

        private static readonly object CaptureLock = new();
        private static ActiveCapture s_active;
        private static bool s_logSubscribed;
        private static bool s_updateSubscribed;
        private static volatile bool s_isPlaying;
        private static readonly UTF8Encoding Utf8NoBom = new(false);

        private readonly UPilotBridge _bridge;

        public UPilotConsoleCaptureService(UPilotBridge bridge)
        {
            _bridge = bridge;
            EnsureSubscriptions();
            TryRecoverActiveSession();
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("console.capture.start", HandleStartAsync);
            _bridge.Router.Register("console.capture.status", HandleStatusAsync);
            _bridge.Router.Register("console.capture.read", HandleReadAsync);
            _bridge.Router.Register("console.capture.stop", HandleStopAsync);
            _bridge.Router.Register("console.capture.list", HandleListAsync);
            _bridge.Router.Register("console.capture.cleanup", HandleCleanupAsync);
        }

        private static void EnsureSubscriptions()
        {
            s_isPlaying = Application.isPlaying;
            if (!s_logSubscribed)
            {
                s_logSubscribed = true;
                Application.logMessageReceivedThreaded += OnLogMessageReceived;
            }

            if (!s_updateSubscribed)
            {
                s_updateSubscribed = true;
                EditorApplication.update -= FlushOnEditorUpdate;
                EditorApplication.update += FlushOnEditorUpdate;
            }
        }

        private static void OnLogMessageReceived(string condition, string stackTrace, LogType logType)
        {
            lock (CaptureLock)
            {
                if (s_active == null || !s_active.Manifest.active)
                    return;

                if (s_active.Manifest.excludeUPilot && IsUPilotLog(condition, stackTrace))
                    return;

                var manifest = s_active.Manifest;
                var record = new ConsoleCaptureRecord
                {
                    sequence = manifest.nextSequence++,
                    timestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    logType = LogTypeToString(logType),
                    message = condition ?? string.Empty,
                    stackTrace = manifest.includeStackTrace ? stackTrace ?? string.Empty : string.Empty,
                    isPlaying = s_isPlaying,
                    threadId = Thread.CurrentThread.ManagedThreadId,
                };

                manifest.totalCount++;
                IncrementTypeCount(manifest, record.logType);
                if (s_active.Pending.Count >= MaxPendingRecords)
                {
                    manifest.droppedCount++;
                    return;
                }

                s_active.Pending.Enqueue(record);
            }
        }

        private static void FlushOnEditorUpdate()
        {
            s_isPlaying = Application.isPlaying;
            ActiveCapture active;
            lock (CaptureLock)
            {
                active = s_active;
                if (active == null || !active.Manifest.active || active.Pending.Count == 0)
                    return;

                double intervalSec = Math.Max(0.1d, active.Manifest.flushIntervalMs / 1000d);
                if (active.Pending.Count < 100 && EditorApplication.timeSinceStartup - active.LastFlushTime < intervalSec)
                    return;
            }

            FlushActiveCapture(false);
        }

        private async Task HandleStartAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<ConsoleCaptureStartMessage>(json);
            var payload = message?.payload ?? new ConsoleCaptureStartPayload();
            var tcs = new TaskCompletionSource<ConsoleCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try { tcs.TrySetResult(StartCapture(payload)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            await SendResultOrError(id, "console.capture.start", tcs.Task, token);
        }

        private async Task HandleStatusAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<ConsoleCaptureSessionMessage>(json);
            var payload = message?.payload ?? new ConsoleCaptureSessionPayload();
            var tcs = new TaskCompletionSource<ConsoleCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try { tcs.TrySetResult(GetStatus(payload.sessionId)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            await SendResultOrError(id, "console.capture.status", tcs.Task, token);
        }

        private async Task HandleReadAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<ConsoleCaptureReadMessage>(json);
            var payload = message?.payload ?? new ConsoleCaptureReadPayload();
            var tcs = new TaskCompletionSource<ConsoleCaptureReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try { tcs.TrySetResult(ReadCapture(payload)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            await SendResultOrError(id, "console.capture.read", tcs.Task, token);
        }

        private async Task HandleStopAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<ConsoleCaptureSessionMessage>(json);
            var payload = message?.payload ?? new ConsoleCaptureSessionPayload();
            var tcs = new TaskCompletionSource<ConsoleCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try { tcs.TrySetResult(StopCapture(payload.sessionId)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            await SendResultOrError(id, "console.capture.stop", tcs.Task, token);
        }

        private async Task HandleListAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<ConsoleCaptureListMessage>(json);
            var payload = message?.payload ?? new ConsoleCaptureListPayload();
            var tcs = new TaskCompletionSource<ConsoleCaptureListResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try { tcs.TrySetResult(ListCaptures(payload)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            await SendResultOrError(id, "console.capture.list", tcs.Task, token);
        }

        private async Task HandleCleanupAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<ConsoleCaptureCleanupMessage>(json);
            var payload = message?.payload ?? new ConsoleCaptureCleanupPayload();
            var tcs = new TaskCompletionSource<ConsoleCaptureCleanupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try { tcs.TrySetResult(CleanupCaptures(payload)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            await SendResultOrError(id, "console.capture.cleanup", tcs.Task, token);
        }

        private async Task SendResultOrError<T>(string id, string command, Task<T> task, CancellationToken token)
        {
            try { await _bridge.SendResultAsync(id, command, await task, token); }
            catch (Exception ex) { await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"Console Capture 操作失败：{ex.Message}", token, command); }
        }

        private static ConsoleCaptureResult StartCapture(ConsoleCaptureStartPayload payload)
        {
            TryRecoverActiveSession();
            lock (CaptureLock)
            {
                if (s_active != null && s_active.Manifest.active)
                    return Result(false, "StartCapture", "已有日志采集会话正在运行", CloneManifest(s_active.Manifest));
            }

            string projectRoot = GetProjectRoot();
            string title = SanitizeName(string.IsNullOrWhiteSpace(payload.title) ? "UnityConsole" : payload.title.Trim());
            string sessionId = "console_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string directory = string.IsNullOrWhiteSpace(payload.path)
                ? Path.Combine(projectRoot, CaptureRootRelative, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + "_" + title)
                : ResolveDirectory(payload.path, projectRoot);
            directory = Path.GetFullPath(directory);
            if (!payload.allowOutsideProject && !IsPathInside(directory, projectRoot))
                return Result(false, "StartCapture", "日志目录必须位于当前 Unity 工程内", null);

            Directory.CreateDirectory(directory);
            var manifest = new ConsoleCaptureManifest
            {
                sessionId = sessionId,
                title = title,
                directory = directory,
                jsonlPath = Path.Combine(directory, "console.jsonl"),
                manifestPath = Path.Combine(directory, "session.json"),
                summaryPath = Path.Combine(directory, "summary.json"),
                active = true,
                includeStackTrace = payload.includeStackTrace,
                excludeUPilot = payload.excludeUPilot,
                flushIntervalMs = Math.Max(100, Math.Min(payload.flushIntervalMs, 60000)),
                maxFileBytes = Math.Max(1024L * 1024L, payload.maxFileBytes),
                startedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            lock (CaptureLock)
            {
                s_active = new ActiveCapture { Manifest = manifest, LastFlushTime = EditorApplication.timeSinceStartup };
            }
            SessionState.SetString(ActiveDirectorySessionKey, directory);
            WriteManifest(manifest);
            RegisterCustomSession(manifest);
            if (payload.clearUnityConsole)
                ClearUnityConsole();
            return Result(true, "StartCapture", string.Empty, CloneManifest(manifest));
        }

        private static ConsoleCaptureResult GetStatus(string sessionId)
        {
            TryRecoverActiveSession();
            lock (CaptureLock)
            {
                if (s_active != null && (string.IsNullOrEmpty(sessionId) || s_active.Manifest.sessionId == sessionId))
                    return Result(true, "GetCaptureStatus", string.Empty, CloneManifest(s_active.Manifest));
            }

            var manifest = LoadManifestBySessionId(sessionId);
            return manifest != null
                ? Result(true, "GetCaptureStatus", string.Empty, manifest)
                : Result(false, "GetCaptureStatus", "未找到日志采集会话: " + (sessionId ?? string.Empty), null);
        }

        private static ConsoleCaptureReadResult ReadCapture(ConsoleCaptureReadPayload payload)
        {
            TryRecoverActiveSession();
            FlushActiveCapture(true);
            var manifest = ResolveManifest(payload.sessionId);
            if (manifest == null)
                return new ConsoleCaptureReadResult { ok = false, action = "ReadCapture", error = "未找到日志采集会话", sessionId = payload.sessionId ?? string.Empty };

            int count = Math.Max(1, Math.Min(payload.count, 5000));
            var matches = new List<ConsoleCaptureRecord>();
            foreach (string file in GetSegmentFiles(manifest.directory))
            {
                foreach (string line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    ConsoleCaptureRecord record;
                    try { record = JsonUtility.FromJson<ConsoleCaptureRecord>(line); }
                    catch { continue; }
                    if (record == null || record.sequence <= payload.afterSequence || !Matches(record, payload)) continue;
                    if (!payload.includeStackTrace) record.stackTrace = string.Empty;
                    matches.Add(record);
                }
            }

            int totalMatches = matches.Count;
            if (payload.newestFirst) matches.Reverse();
            if (matches.Count > count) matches = matches.GetRange(0, count);
            long nextSequence = matches.Count > 0 ? matches.Max(item => item.sequence) : payload.afterSequence;
            return new ConsoleCaptureReadResult
            {
                ok = true,
                action = "ReadCapture",
                sessionId = manifest.sessionId,
                logs = matches,
                afterSequence = payload.afterSequence,
                nextSequence = nextSequence,
                matchedCount = totalMatches,
                truncated = totalMatches > matches.Count,
            };
        }

        private static ConsoleCaptureResult StopCapture(string sessionId)
        {
            TryRecoverActiveSession();
            ActiveCapture active;
            lock (CaptureLock)
            {
                active = s_active;
                if (active != null && !string.IsNullOrEmpty(sessionId) && active.Manifest.sessionId != sessionId)
                    return Result(false, "StopCapture", "活跃会话与 sessionId 不匹配", CloneManifest(active.Manifest));
                if (active != null)
                    active.Manifest.active = false;
            }
            if (active == null)
            {
                var existing = LoadManifestBySessionId(sessionId);
                return existing != null
                    ? Result(true, "StopCapture", string.Empty, existing)
                    : Result(false, "StopCapture", "当前没有活跃日志采集会话", null);
            }

            FlushActiveCapture(true);
            lock (CaptureLock)
            {
                active.Manifest.finishedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                active.Manifest.durationSec = Math.Max(0d, (active.Manifest.finishedAtUtcMs - active.Manifest.startedAtUtcMs) / 1000d);
                active.Manifest.fileBytes = GetDirectoryLogBytes(active.Manifest.directory);
                active.Manifest.sha256 = ComputeCombinedSha256(active.Manifest.directory);
                WriteManifest(active.Manifest);
                File.WriteAllText(active.Manifest.summaryPath, JsonUtility.ToJson(active.Manifest, true), Utf8NoBom);
                var result = CloneManifest(active.Manifest);
                s_active = null;
                SessionState.EraseString(ActiveDirectorySessionKey);
                return Result(true, "StopCapture", string.Empty, result);
            }
        }

        private static ConsoleCaptureListResult ListCaptures(ConsoleCaptureListPayload payload)
        {
            TryRecoverActiveSession();
            int count = Math.Max(1, Math.Min(payload.count, 200));
            var manifests = LoadDefaultRootManifests();
            lock (CaptureLock)
            {
                if (s_active != null && manifests.All(item => item.sessionId != s_active.Manifest.sessionId))
                    manifests.Add(CloneManifest(s_active.Manifest));
            }
            var sessions = manifests
                .Where(item => payload.includeActive || !item.active)
                .OrderByDescending(item => item.startedAtUtcMs)
                .Take(count)
                .ToList();
            return new ConsoleCaptureListResult { ok = true, action = "ListCaptures", sessions = sessions };
        }

        private static ConsoleCaptureCleanupResult CleanupCaptures(ConsoleCaptureCleanupPayload payload)
        {
            int keepLatest = Math.Max(0, payload.keepLatest);
            DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(0, payload.olderThanDays));
            string activeId;
            lock (CaptureLock) { activeId = s_active?.Manifest.sessionId; }
            var manifests = LoadDefaultRootManifests().OrderByDescending(item => item.startedAtUtcMs).ToList();
            var targets = manifests.Skip(keepLatest)
                .Where(item => item.sessionId != activeId && item.startedAtUtcMs < cutoff.ToUnixTimeMilliseconds())
                .Select(item => item.directory)
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string token = ComputeTextSha256(string.Join("\n", targets.OrderBy(item => item, StringComparer.OrdinalIgnoreCase)));
            long totalBytes = targets.Sum(GetDirectoryBytes);

            if (payload.dryRun)
            {
                SessionState.SetString(CleanupTokenSessionKey, token);
                SessionState.SetString(CleanupTargetsSessionKey, string.Join("\n", targets));
                return new ConsoleCaptureCleanupResult
                {
                    ok = true, action = "CleanupCaptures", dryRun = true, confirmToken = token,
                    directories = targets, totalBytes = totalBytes,
                };
            }

            string expectedToken = SessionState.GetString(CleanupTokenSessionKey, string.Empty);
            string expectedTargets = SessionState.GetString(CleanupTargetsSessionKey, string.Empty);
            if (string.IsNullOrEmpty(payload.confirmToken) || payload.confirmToken != expectedToken || expectedTargets != string.Join("\n", targets))
            {
                return new ConsoleCaptureCleanupResult
                {
                    ok = false, action = "CleanupCaptures", error = "confirmToken 无效或清理目标已变化，请重新 dryRun",
                    dryRun = false, confirmToken = token, directories = targets, totalBytes = totalBytes,
                };
            }

            int deleted = 0;
            string root = GetDefaultCaptureRoot();
            foreach (string directory in targets)
            {
                string full = Path.GetFullPath(directory);
                if (!IsPathInside(full, root)) continue;
                Directory.Delete(full, true);
                deleted++;
            }
            SessionState.EraseString(CleanupTokenSessionKey);
            SessionState.EraseString(CleanupTargetsSessionKey);
            return new ConsoleCaptureCleanupResult
            {
                ok = true, action = "CleanupCaptures", dryRun = false, confirmToken = token,
                directories = targets, totalBytes = totalBytes, deletedCount = deleted,
            };
        }

        private static void TryRecoverActiveSession()
        {
            lock (CaptureLock)
            {
                if (s_active != null) return;
                string directory = SessionState.GetString(ActiveDirectorySessionKey, string.Empty);
                if (string.IsNullOrEmpty(directory)) return;
                string manifestPath = Path.Combine(directory, "session.json");
                var manifest = LoadManifest(manifestPath);
                if (manifest == null || !manifest.active)
                {
                    SessionState.EraseString(ActiveDirectorySessionKey);
                    return;
                }
                s_active = new ActiveCapture { Manifest = manifest, LastFlushTime = EditorApplication.timeSinceStartup };
            }
        }

        private static void FlushActiveCapture(bool force)
        {
            ActiveCapture active;
            List<ConsoleCaptureRecord> records;
            lock (CaptureLock)
            {
                active = s_active;
                if (active == null || active.Pending.Count == 0) return;
                if (!force)
                {
                    double intervalSec = Math.Max(0.1d, active.Manifest.flushIntervalMs / 1000d);
                    if (active.Pending.Count < 100 && EditorApplication.timeSinceStartup - active.LastFlushTime < intervalSec) return;
                }
                records = active.Pending.ToList();
                active.Pending.Clear();
            }

            try
            {
                var builder = new StringBuilder(records.Count * 256);
                foreach (ConsoleCaptureRecord record in records)
                    builder.AppendLine(JsonUtility.ToJson(record));
                byte[] bytes = Utf8NoBom.GetBytes(builder.ToString());
                string path = GetCurrentSegmentPath(active.Manifest);
                long currentBytes = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (currentBytes > 0 && currentBytes + bytes.Length > active.Manifest.maxFileBytes)
                {
                    active.Manifest.segmentCount++;
                    path = GetCurrentSegmentPath(active.Manifest);
                }
                File.AppendAllText(path, builder.ToString(), Utf8NoBom);
                active.Manifest.jsonlPath = path;
                active.Manifest.fileBytes = GetDirectoryLogBytes(active.Manifest.directory);
                active.Manifest.lastError = string.Empty;
                active.LastFlushTime = EditorApplication.timeSinceStartup;
                WriteManifest(active.Manifest);
            }
            catch (Exception ex)
            {
                lock (CaptureLock)
                {
                    active.Manifest.lastError = ex.Message;
                    active.Manifest.droppedCount += records.Count;
                    WriteManifest(active.Manifest);
                }
            }
        }

        private static bool Matches(ConsoleCaptureRecord record, ConsoleCaptureReadPayload payload)
        {
            if (!string.IsNullOrEmpty(payload.logType) && !string.Equals(record.logType, payload.logType, StringComparison.OrdinalIgnoreCase))
                return false;
            string[] contains = payload.contains ?? Array.Empty<string>();
            if (contains.Length == 0) return true;
            string text = (record.message ?? string.Empty) + "\n" + (record.stackTrace ?? string.Empty);
            bool any = false;
            foreach (string value in contains)
            {
                if (string.IsNullOrEmpty(value)) continue;
                bool hit = text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
                if (payload.containsAll && !hit) return false;
                if (hit) any = true;
            }
            return payload.containsAll || any;
        }

        private static ConsoleCaptureManifest ResolveManifest(string sessionId)
        {
            lock (CaptureLock)
            {
                if (s_active != null && (string.IsNullOrEmpty(sessionId) || s_active.Manifest.sessionId == sessionId))
                    return CloneManifest(s_active.Manifest);
            }
            return LoadManifestBySessionId(sessionId);
        }

        private static ConsoleCaptureManifest LoadManifestBySessionId(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;
            var manifest = LoadDefaultRootManifests().FirstOrDefault(item => item.sessionId == sessionId);
            if (manifest != null) return manifest;

            var index = LoadCustomSessionIndex();
            var entry = index.sessions.FirstOrDefault(item => item.sessionId == sessionId);
            if (entry == null || string.IsNullOrEmpty(entry.directory)) return null;
            manifest = LoadManifest(Path.Combine(entry.directory, "session.json"));
            return manifest != null && manifest.sessionId == sessionId ? manifest : null;
        }

        private static void RegisterCustomSession(ConsoleCaptureManifest manifest)
        {
            string defaultRoot = GetDefaultCaptureRoot();
            if (IsPathInside(manifest.directory, defaultRoot)) return;

            var index = LoadCustomSessionIndex();
            index.sessions.RemoveAll(item => item == null
                || item.sessionId == manifest.sessionId
                || string.IsNullOrEmpty(item.directory)
                || !File.Exists(Path.Combine(item.directory, "session.json")));
            index.sessions.Add(new ConsoleCaptureSessionIndexEntry
            {
                sessionId = manifest.sessionId,
                directory = manifest.directory,
                startedAtUtcMs = manifest.startedAtUtcMs,
            });
            index.sessions = index.sessions
                .OrderByDescending(item => item.startedAtUtcMs)
                .Take(MaxIndexedCustomSessions)
                .ToList();

            Directory.CreateDirectory(defaultRoot);
            File.WriteAllText(GetCustomSessionIndexPath(), JsonUtility.ToJson(index, true), Utf8NoBom);
        }

        private static ConsoleCaptureSessionIndex LoadCustomSessionIndex()
        {
            try
            {
                string path = GetCustomSessionIndexPath();
                if (!File.Exists(path)) return new ConsoleCaptureSessionIndex();
                return JsonUtility.FromJson<ConsoleCaptureSessionIndex>(File.ReadAllText(path, Encoding.UTF8))
                    ?? new ConsoleCaptureSessionIndex();
            }
            catch
            {
                return new ConsoleCaptureSessionIndex();
            }
        }

        private static List<ConsoleCaptureManifest> LoadDefaultRootManifests()
        {
            var result = new List<ConsoleCaptureManifest>();
            string root = GetDefaultCaptureRoot();
            if (!Directory.Exists(root)) return result;
            foreach (string file in Directory.GetFiles(root, "session.json", SearchOption.AllDirectories))
            {
                var manifest = LoadManifest(file);
                if (manifest != null) result.Add(manifest);
            }
            return result;
        }

        private static ConsoleCaptureManifest LoadManifest(string path)
        {
            try
            {
                return File.Exists(path) ? JsonUtility.FromJson<ConsoleCaptureManifest>(File.ReadAllText(path, Encoding.UTF8)) : null;
            }
            catch { return null; }
        }

        private static void WriteManifest(ConsoleCaptureManifest manifest)
        {
            Directory.CreateDirectory(manifest.directory);
            File.WriteAllText(manifest.manifestPath, JsonUtility.ToJson(manifest, true), Utf8NoBom);
        }

        private static ConsoleCaptureResult Result(bool ok, string action, string error, ConsoleCaptureManifest manifest)
        {
            return new ConsoleCaptureResult { ok = ok, action = action, error = error ?? string.Empty, session = manifest };
        }

        private static ConsoleCaptureManifest CloneManifest(ConsoleCaptureManifest manifest)
        {
            return manifest == null ? null : JsonUtility.FromJson<ConsoleCaptureManifest>(JsonUtility.ToJson(manifest));
        }

        private static string GetCurrentSegmentPath(ConsoleCaptureManifest manifest)
        {
            return manifest.segmentCount <= 1
                ? Path.Combine(manifest.directory, "console.jsonl")
                : Path.Combine(manifest.directory, $"console.{manifest.segmentCount - 1:000}.jsonl");
        }

        private static IEnumerable<string> GetSegmentFiles(string directory)
        {
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, "console*.jsonl", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => string.Equals(Path.GetFileName(path), "console.jsonl", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                : Array.Empty<string>();
        }

        private static long GetDirectoryLogBytes(string directory)
        {
            return GetSegmentFiles(directory).Sum(path => new FileInfo(path).Length);
        }

        private static long GetDirectoryBytes(string directory)
        {
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
                : 0;
        }

        private static string ComputeCombinedSha256(string directory)
        {
            using var sha = SHA256.Create();
            foreach (string file in GetSegmentFiles(directory))
            {
                byte[] bytes = File.ReadAllBytes(file);
                sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return ToHex(sha.Hash);
        }

        private static string ComputeTextSha256(string value)
        {
            using var sha = SHA256.Create();
            return ToHex(sha.ComputeHash(Utf8NoBom.GetBytes(value ?? string.Empty)));
        }

        private static string ToHex(byte[] bytes)
        {
            if (bytes == null) return string.Empty;
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) builder.Append(value.ToString("x2"));
            return builder.ToString();
        }

        private static string GetProjectRoot() => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static string GetDefaultCaptureRoot() => Path.Combine(GetProjectRoot(), CaptureRootRelative);
        private static string GetCustomSessionIndexPath() => Path.Combine(GetDefaultCaptureRoot(), SessionIndexFileName);

        private static string ResolveDirectory(string path, string projectRoot)
        {
            return Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path);
        }

        private static bool IsPathInside(string path, string root)
        {
            string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static string SanitizeName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(value) ? "UnityConsole" : value;
        }

        private static void IncrementTypeCount(ConsoleCaptureManifest manifest, string logType)
        {
            switch (logType)
            {
                case "Warning": manifest.warningCount++; break;
                case "Error": manifest.errorCount++; break;
                case "Exception": manifest.exceptionCount++; break;
                case "Assert": manifest.assertCount++; break;
                default: manifest.logCount++; break;
            }
        }

        private static string LogTypeToString(LogType type)
        {
            return type switch
            {
                LogType.Error => "Error",
                LogType.Assert => "Assert",
                LogType.Warning => "Warning",
                LogType.Exception => "Exception",
                _ => "Log",
            };
        }

        private static bool IsUPilotLog(string message, string stackTrace)
        {
            string text = (message ?? string.Empty) + "\n" + (stackTrace ?? string.Empty);
            return text.IndexOf("UPilot", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("CodingRiver.UPilot", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("[COMMAND ]", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ClearUnityConsole()
        {
            Type type = typeof(Editor).Assembly.GetType("UnityEditor.LogEntries")
                ?? typeof(Editor).Assembly.GetType("UnityEditorInternal.LogEntries");
            type?.GetMethod("Clear", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.Invoke(null, null);
        }
    }
}
