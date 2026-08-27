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
using System.Text.RegularExpressions;
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
        public long fromSequence = -1;
        public long toSequence = -1;
        public int count = 200;
        public string logType;
        public bool includeStackTrace = true;
        public string[] contains;
        public bool containsAll;
        public string regex;
        public bool newestFirst;
        public string continuationToken;
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
        public int returnedCount;
        public long totalMatchCount;
        public bool truncated;
        public string continuationToken;
        public long effectiveFromSequence;
        public long effectiveToSequence;
        public long scannedFromSequence = -1;
        public long scannedToSequence = -1;
        public long scannedCount;
        public bool scanComplete;
        public double elapsedMs;
        public string indexPath;
        public bool indexUsed;
        public bool indexCreated;
    }

    [Serializable]
    public sealed class ConsoleCaptureReadCursor
    {
        public int version = 1;
        public string sessionId;
        public string queryHash;
        public long rangeFromSequence;
        public long snapshotToSequence;
        public long nextBoundarySequence;
        public long totalMatchCount;
        public long consumedMatchCount;
        public bool newestFirst;
        public string logType;
        public bool includeStackTrace;
        public string[] contains;
        public bool containsAll;
        public string regex;
    }

    [Serializable]
    public sealed class ConsoleCaptureSparseIndexEntry
    {
        public long sequence;
        public long byteOffset;
        public string logType;
    }

    [Serializable]
    public sealed class ConsoleCaptureSparseIndexSegment
    {
        public string fileName;
        public long indexedBytes;
        public long indexedRecordCount;
        public long firstSequence = -1;
        public long lastSequence = -1;
        public List<ConsoleCaptureSparseIndexEntry> entries = new();
    }

    [Serializable]
    public sealed class ConsoleCaptureSparseIndex
    {
        public int version = 1;
        public int stride = 128;
        public long updatedAtUtcMs;
        public List<ConsoleCaptureSparseIndexSegment> segments = new();
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
        private const string SparseIndexFileName = "console.index.json";
        private const int SparseIndexStride = 128;
        private const int MaxPendingRecords = 10000;
        private const int MaxIndexedCustomSessions = 1000;

        private sealed class ActiveCapture
        {
            public ConsoleCaptureManifest Manifest;
            public readonly Queue<ConsoleCaptureRecord> Pending = new();
            public double LastFlushTime;
        }

        private sealed class ConsoleCaptureReadPreparation
        {
            public ConsoleCaptureManifest Manifest;
            public string Error;
        }

        private sealed class ConsoleCaptureLiteralScanResult
        {
            public readonly List<ConsoleCaptureRecord> Matches = new();
            public long TotalMatches;
            public long ScannedCount;
            public long ScannedFrom = -1;
            public long ScannedTo = -1;
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
            var preparationTcs = new TaskCompletionSource<ConsoleCaptureReadPreparation>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try { preparationTcs.TrySetResult(PrepareReadCapture(payload)); }
                catch (Exception ex) { preparationTcs.TrySetException(ex); }
            });

            try
            {
                var preparation = await preparationTcs.Task.ConfigureAwait(false);
                if (!string.IsNullOrEmpty(preparation.Error))
                {
                    await _bridge.SendResultAsync(
                        id,
                        "console.capture.read",
                        new ConsoleCaptureReadResult
                        {
                            ok = false,
                            action = "ReadCapture",
                            error = preparation.Error,
                            sessionId = payload.sessionId ?? string.Empty,
                        },
                        token);
                    return;
                }

                // File IO and JSON parsing must not run inside EditorApplication.update.
                var result = await Task.Run(
                    () => ReadCaptureFiles(preparation.Manifest, payload, token),
                    token).ConfigureAwait(false);
                await _bridge.SendResultAsync(id, "console.capture.read", result, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(
                    id,
                    "INTERNAL_ERROR",
                    $"Console Capture 操作失败：{ex.Message}",
                    token,
                    "console.capture.read");
            }
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
            SessionState.SetString(ProjectSessionKey(ActiveDirectorySessionKey), directory);
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

        private static ConsoleCaptureReadPreparation PrepareReadCapture(ConsoleCaptureReadPayload payload)
        {
            TryRecoverActiveSession();
            FlushActiveCapture(true);
            var manifest = ResolveManifest(payload.sessionId);
            if (manifest == null)
                return new ConsoleCaptureReadPreparation { Error = "未找到日志采集会话" };

            return new ConsoleCaptureReadPreparation { Manifest = manifest };
        }

        private static ConsoleCaptureReadResult ReadCaptureFiles(
            ConsoleCaptureManifest manifest,
            ConsoleCaptureReadPayload payload,
            CancellationToken token)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int count = Math.Max(1, Math.Min(payload.count, 5000));
            var matches = new List<ConsoleCaptureRecord>(count);
            var newestMatches = payload.newestFirst
                ? new Queue<ConsoleCaptureRecord>(count)
                : null;
            var cursor = DecodeReadCursor(payload.continuationToken);
            if (cursor != null && cursor.version == 1)
            {
                payload.logType = cursor.logType;
                payload.includeStackTrace = cursor.includeStackTrace;
                payload.contains = cursor.contains;
                payload.containsAll = cursor.containsAll;
                payload.regex = cursor.regex;
                payload.newestFirst = cursor.newestFirst;
            }
            string queryHash = ComputeReadQueryHash(payload);
            long requestedFrom = payload.fromSequence >= 0
                ? payload.fromSequence
                : Math.Max(0, payload.afterSequence + 1);
            long requestedTo = payload.toSequence >= 0
                ? payload.toSequence
                : Math.Max(-1, manifest.nextSequence - 1);
            if (cursor != null && (cursor.sessionId != manifest.sessionId
                || cursor.queryHash != queryHash
                || cursor.newestFirst != payload.newestFirst))
            {
                return new ConsoleCaptureReadResult
                {
                    ok = false,
                    action = "ReadCapture",
                    error = "continuationToken 与当前会话、筛选条件或排序方式不匹配",
                    sessionId = manifest.sessionId,
                };
            }

            long rangeFrom = cursor?.rangeFromSequence ?? requestedFrom;
            long snapshotTo = cursor?.snapshotToSequence ?? requestedTo;
            if (snapshotTo < rangeFrom)
                snapshotTo = rangeFrom - 1;
            long pageBoundary = cursor?.nextBoundarySequence
                ?? (payload.newestFirst ? snapshotTo : rangeFrom);
            long scanFrom = payload.newestFirst ? rangeFrom : Math.Max(rangeFrom, pageBoundary);
            long scanTo = payload.newestFirst ? Math.Min(snapshotTo, pageBoundary) : snapshotTo;
            long totalMatches = cursor?.totalMatchCount ?? 0;
            long scannedCount = 0;
            long scannedFrom = -1;
            long scannedTo = -1;
            bool countAllMatches = cursor == null;

            string indexPath = Path.Combine(manifest.directory, SparseIndexFileName);
            bool indexCreated;
            ConsoleCaptureSparseIndex sparseIndex = LoadOrBuildSparseIndex(manifest.directory, token, out indexCreated);
            Regex compiledRegex = CompileReadRegex(payload.regex);
            string[] rawLineNeedles = GetRawLinePrefilterNeedles(payload);

            if (rawLineNeedles != null && !payload.newestFirst)
            {
                ConsoleCaptureLiteralScanResult fastScan = ScanLiteralRegexFiles(
                    manifest.directory,
                    sparseIndex,
                    scanFrom,
                    scanTo,
                    count,
                    countAllMatches,
                    cursor != null,
                    payload,
                    compiledRegex,
                    rawLineNeedles,
                    token);
                matches = fastScan.Matches;
                scannedCount = fastScan.ScannedCount;
                scannedFrom = fastScan.ScannedFrom;
                scannedTo = fastScan.ScannedTo;
                if (countAllMatches) totalMatches = fastScan.TotalMatches;
            }
            else foreach (string file in GetSegmentFiles(manifest.directory))
            {
                var segmentIndex = sparseIndex?.segments?.FirstOrDefault(item =>
                    string.Equals(item.fileName, Path.GetFileName(file), StringComparison.OrdinalIgnoreCase));
                if (segmentIndex != null && (segmentIndex.lastSequence < scanFrom || segmentIndex.firstSequence > scanTo))
                    continue;

                long offset = FindReadOffset(segmentIndex, scanFrom);
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
                if (offset > 0) stream.Seek(offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8, true, 64 * 1024, false);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (!TryExtractSequence(line, out long sequence)) continue;
                    if (sequence < scanFrom) continue;
                    if (sequence > scanTo) break;
                    scannedCount++;
                    if (scannedFrom < 0) scannedFrom = sequence;
                    scannedTo = sequence;
                    if (rawLineNeedles != null && !RawLineContainsAny(line, rawLineNeedles)) continue;
                    ConsoleCaptureRecord record;
                    try { record = JsonUtility.FromJson<ConsoleCaptureRecord>(line); }
                    catch { continue; }
                    if (record == null) continue;
                    if (!Matches(record, payload, compiledRegex)) continue;
                    if (countAllMatches)
                        totalMatches = totalMatches == long.MaxValue ? long.MaxValue : totalMatches + 1;
                    if (!payload.includeStackTrace) record.stackTrace = string.Empty;
                    if (payload.newestFirst)
                    {
                        if (newestMatches.Count >= count) newestMatches.Dequeue();
                        newestMatches.Enqueue(record);
                    }
                    else if (matches.Count < count)
                    {
                        matches.Add(record);
                    }
                    if (cursor != null && !payload.newestFirst && matches.Count >= count)
                        break;
                }
                if (cursor != null && !payload.newestFirst && matches.Count >= count)
                    break;
            }

            if (payload.newestFirst)
            {
                matches = newestMatches.ToList();
                matches.Reverse();
            }

            long consumedMatches = (cursor?.consumedMatchCount ?? 0) + matches.Count;
            bool hasMore = consumedMatches < totalMatches;
            long nextBoundary = payload.newestFirst
                ? (matches.Count > 0 ? matches.Min(item => item.sequence) - 1 : rangeFrom - 1)
                : (matches.Count > 0 ? matches.Max(item => item.sequence) + 1 : snapshotTo + 1);
            string continuationToken = hasMore
                ? EncodeReadCursor(new ConsoleCaptureReadCursor
                {
                    sessionId = manifest.sessionId,
                    queryHash = queryHash,
                    rangeFromSequence = rangeFrom,
                    snapshotToSequence = snapshotTo,
                    nextBoundarySequence = nextBoundary,
                    totalMatchCount = totalMatches,
                    consumedMatchCount = consumedMatches,
                    newestFirst = payload.newestFirst,
                    logType = payload.logType,
                    includeStackTrace = payload.includeStackTrace,
                    contains = payload.contains,
                    containsAll = payload.containsAll,
                    regex = payload.regex,
                })
                : string.Empty;
            long nextSequence = matches.Count > 0 ? matches.Max(item => item.sequence) : payload.afterSequence;
            stopwatch.Stop();
            return new ConsoleCaptureReadResult
            {
                ok = true,
                action = "ReadCapture",
                sessionId = manifest.sessionId,
                logs = matches,
                afterSequence = payload.afterSequence,
                nextSequence = nextSequence,
                matchedCount = totalMatches > int.MaxValue ? int.MaxValue : (int)totalMatches,
                returnedCount = matches.Count,
                totalMatchCount = totalMatches,
                truncated = hasMore,
                continuationToken = continuationToken,
                effectiveFromSequence = rangeFrom,
                effectiveToSequence = snapshotTo,
                scannedFromSequence = scannedFrom,
                scannedToSequence = scannedTo,
                scannedCount = scannedCount,
                scanComplete = true,
                elapsedMs = stopwatch.Elapsed.TotalMilliseconds,
                indexPath = indexPath,
                indexUsed = sparseIndex != null,
                indexCreated = indexCreated,
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
                if (existing == null)
                    return Result(false, "StopCapture", "当前没有活跃日志采集会话", null);

                // A service restart or domain reload can lose the SessionState
                // pointer while the persisted manifest still says active.  A
                // successful stop must finalize that historical session too.
                if (existing.active)
                {
                    existing.active = false;
                    existing.finishedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    existing.durationSec = Math.Max(
                        0d,
                        (existing.finishedAtUtcMs - existing.startedAtUtcMs) / 1000d);
                    existing.fileBytes = GetDirectoryLogBytes(existing.directory);
                    existing.sha256 = ComputeCombinedSha256(existing.directory);
                    WriteManifest(existing);
                    File.WriteAllText(
                        existing.summaryPath,
                        JsonUtility.ToJson(existing, true),
                        Utf8NoBom);
                }

                return Result(true, "StopCapture", string.Empty, CloneManifest(existing));
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
                SessionState.EraseString(ProjectSessionKey(ActiveDirectorySessionKey));
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
                SessionState.SetString(ProjectSessionKey(CleanupTokenSessionKey), token);
                SessionState.SetString(ProjectSessionKey(CleanupTargetsSessionKey), string.Join("\n", targets));
                return new ConsoleCaptureCleanupResult
                {
                    ok = true, action = "CleanupCaptures", dryRun = true, confirmToken = token,
                    directories = targets, totalBytes = totalBytes,
                };
            }

            string expectedToken = SessionState.GetString(ProjectSessionKey(CleanupTokenSessionKey), string.Empty);
            string expectedTargets = SessionState.GetString(ProjectSessionKey(CleanupTargetsSessionKey), string.Empty);
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
            SessionState.EraseString(ProjectSessionKey(CleanupTokenSessionKey));
            SessionState.EraseString(ProjectSessionKey(CleanupTargetsSessionKey));
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
                string directory = SessionState.GetString(ProjectSessionKey(ActiveDirectorySessionKey), string.Empty);
                if (string.IsNullOrEmpty(directory)) return;
                string manifestPath = Path.Combine(directory, "session.json");
                var manifest = LoadManifest(manifestPath);
                if (manifest == null || !manifest.active)
                {
                    SessionState.EraseString(ProjectSessionKey(ActiveDirectorySessionKey));
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
                    currentBytes = File.Exists(path) ? new FileInfo(path).Length : 0;
                }
                File.AppendAllText(path, builder.ToString(), Utf8NoBom);
                UpdateSparseIndexAfterAppend(path, records, currentBytes, bytes.Length);
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

        private static void UpdateSparseIndexAfterAppend(
            string file,
            List<ConsoleCaptureRecord> records,
            long startingOffset,
            long appendedBytes)
        {
            if (records == null || records.Count == 0) return;
            string directory = Path.GetDirectoryName(file);
            string indexPath = Path.Combine(directory, SparseIndexFileName);
            ConsoleCaptureSparseIndex index = null;
            try
            {
                if (File.Exists(indexPath))
                    index = JsonUtility.FromJson<ConsoleCaptureSparseIndex>(File.ReadAllText(indexPath, Encoding.UTF8));
            }
            catch { index = null; }
            index ??= new ConsoleCaptureSparseIndex { stride = SparseIndexStride };
            index.segments ??= new List<ConsoleCaptureSparseIndexSegment>();
            string fileName = Path.GetFileName(file);
            var segment = index.segments.FirstOrDefault(item =>
                string.Equals(item.fileName, fileName, StringComparison.OrdinalIgnoreCase));
            if (segment == null && startingOffset > 0 || segment != null && segment.indexedBytes != startingOffset)
            {
                LoadOrBuildSparseIndex(directory, CancellationToken.None, out _);
                return;
            }
            if (segment == null)
            {
                segment = new ConsoleCaptureSparseIndexSegment { fileName = fileName };
                index.segments.RemoveAll(item =>
                    string.Equals(item.fileName, fileName, StringComparison.OrdinalIgnoreCase));
                index.segments.Add(segment);
            }

            long offset = startingOffset;
            long recordIndex = segment.indexedRecordCount;
            int newlineBytes = Utf8NoBom.GetByteCount(Environment.NewLine);
            foreach (ConsoleCaptureRecord record in records)
            {
                if (segment.firstSequence < 0) segment.firstSequence = record.sequence;
                segment.lastSequence = record.sequence;
                if (recordIndex % SparseIndexStride == 0)
                {
                    segment.entries.Add(new ConsoleCaptureSparseIndexEntry
                    {
                        sequence = record.sequence,
                        byteOffset = offset,
                        logType = record.logType,
                    });
                }
                offset += Utf8NoBom.GetByteCount(JsonUtility.ToJson(record)) + newlineBytes;
                recordIndex++;
            }
            segment.indexedBytes = startingOffset + appendedBytes;
            segment.indexedRecordCount = recordIndex;
            index.updatedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            File.WriteAllText(indexPath, JsonUtility.ToJson(index), Utf8NoBom);
        }

        private static Regex CompileReadRegex(string pattern)
        {
            return string.IsNullOrWhiteSpace(pattern)
                ? null
                : new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }

        private static bool Matches(ConsoleCaptureRecord record, ConsoleCaptureReadPayload payload, Regex regex)
        {
            if (!string.IsNullOrEmpty(payload.logType) && !string.Equals(record.logType, payload.logType, StringComparison.OrdinalIgnoreCase))
                return false;
            string text = (record.message ?? string.Empty) + "\n" + (record.stackTrace ?? string.Empty);
            if (regex != null && !regex.IsMatch(text)) return false;
            string[] contains = payload.contains ?? Array.Empty<string>();
            if (contains.Length == 0) return true;
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

        private static ConsoleCaptureSparseIndex LoadOrBuildSparseIndex(
            string directory,
            CancellationToken token,
            out bool changed)
        {
            changed = false;
            string path = Path.Combine(directory, SparseIndexFileName);
            ConsoleCaptureSparseIndex index = null;
            try
            {
                if (File.Exists(path))
                    index = JsonUtility.FromJson<ConsoleCaptureSparseIndex>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch { index = null; }
            if (index == null || index.version != 1 || index.stride != SparseIndexStride)
            {
                index = new ConsoleCaptureSparseIndex { stride = SparseIndexStride };
                changed = true;
            }

            var files = GetSegmentFiles(directory).ToList();
            index.segments ??= new List<ConsoleCaptureSparseIndexSegment>();
            index.segments.RemoveAll(item => item == null || files.All(file =>
                !string.Equals(Path.GetFileName(file), item.fileName, StringComparison.OrdinalIgnoreCase)));
            foreach (string file in files)
            {
                token.ThrowIfCancellationRequested();
                string fileName = Path.GetFileName(file);
                long fileBytes = new FileInfo(file).Length;
                var segment = index.segments.FirstOrDefault(item =>
                    string.Equals(item.fileName, fileName, StringComparison.OrdinalIgnoreCase));
                if (segment == null || segment.indexedBytes > fileBytes)
                {
                    segment = new ConsoleCaptureSparseIndexSegment { fileName = fileName };
                    index.segments.RemoveAll(item => string.Equals(item.fileName, fileName, StringComparison.OrdinalIgnoreCase));
                    index.segments.Add(segment);
                    changed = true;
                }
                if (segment.indexedBytes == fileBytes) continue;

                long offset = segment.indexedBytes;
                long recordIndex = segment.indexedRecordCount;
                byte[] bytes = File.ReadAllBytes(file);
                int position = offset > int.MaxValue ? bytes.Length : Math.Max(0, (int)offset);
                while (position < bytes.Length)
                {
                    token.ThrowIfCancellationRequested();
                    int lineStart = position;
                    while (position < bytes.Length && bytes[position] != (byte)'\n') position++;
                    int lineEnd = position;
                    if (lineEnd > lineStart && bytes[lineEnd - 1] == (byte)'\r') lineEnd--;
                    position++;
                    if (!TryExtractSequence(bytes, lineStart, lineEnd, out long sequence)) continue;
                    if (segment.firstSequence < 0) segment.firstSequence = sequence;
                    segment.lastSequence = sequence;
                    if (recordIndex % SparseIndexStride == 0)
                    {
                        segment.entries.Add(new ConsoleCaptureSparseIndexEntry
                        {
                            sequence = sequence,
                            byteOffset = lineStart,
                        });
                    }
                    recordIndex++;
                }
                segment.indexedBytes = fileBytes;
                segment.indexedRecordCount = recordIndex;
                changed = true;
            }

            if (changed)
            {
                index.updatedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                File.WriteAllText(path, JsonUtility.ToJson(index), Utf8NoBom);
            }
            return index;
        }

        private static bool TryExtractSequence(string line, out long sequence)
        {
            sequence = 0;
            if (string.IsNullOrEmpty(line)) return false;
            int keyIndex = line.IndexOf("\"sequence\"", StringComparison.Ordinal);
            if (keyIndex < 0) return false;
            int colonIndex = line.IndexOf(':', keyIndex + 10);
            if (colonIndex < 0) return false;
            int index = colonIndex + 1;
            while (index < line.Length && char.IsWhiteSpace(line[index])) index++;
            bool negative = index < line.Length && line[index] == '-';
            if (negative) index++;
            long value = 0;
            int digitCount = 0;
            while (index < line.Length)
            {
                char current = line[index];
                if (current < '0' || current > '9') break;
                int digit = current - '0';
                if (value > (long.MaxValue - digit) / 10) return false;
                value = value * 10 + digit;
                digitCount++;
                index++;
            }
            if (digitCount == 0) return false;
            sequence = negative ? -value : value;
            return true;
        }

        private static bool TryExtractSequence(byte[] bytes, int start, int end, out long sequence)
        {
            sequence = 0;
            if (bytes == null || start < 0 || end <= start || end > bytes.Length) return false;
            byte[] key = { (byte)'"', (byte)'s', (byte)'e', (byte)'q', (byte)'u', (byte)'e', (byte)'n', (byte)'c', (byte)'e', (byte)'"' };
            int probe = start;
            if (end - probe >= 3 && bytes[probe] == 0xEF && bytes[probe + 1] == 0xBB && bytes[probe + 2] == 0xBF) probe += 3;
            while (probe < end && (bytes[probe] == (byte)' ' || bytes[probe] == (byte)'\t')) probe++;
            if (probe < end && bytes[probe] == (byte)'{') probe++;
            int keyIndex = BytesEqualAt(bytes, probe, end, key, false)
                ? probe
                : IndexOfBytes(bytes, start, end, key, false);
            if (keyIndex < 0) return false;
            int index = keyIndex + key.Length;
            while (index < end && bytes[index] != (byte)':') index++;
            if (index >= end) return false;
            index++;
            while (index < end && (bytes[index] == (byte)' ' || bytes[index] == (byte)'\t')) index++;
            bool negative = index < end && bytes[index] == (byte)'-';
            if (negative) index++;
            long value = 0;
            int digitCount = 0;
            while (index < end && bytes[index] >= (byte)'0' && bytes[index] <= (byte)'9')
            {
                int digit = bytes[index] - (byte)'0';
                if (value > (long.MaxValue - digit) / 10) return false;
                value = value * 10 + digit;
                digitCount++;
                index++;
            }
            if (digitCount == 0) return false;
            sequence = negative ? -value : value;
            return true;
        }

        private static ConsoleCaptureLiteralScanResult ScanLiteralRegexFiles(
            string directory,
            ConsoleCaptureSparseIndex sparseIndex,
            long scanFrom,
            long scanTo,
            int count,
            bool countAllMatches,
            bool stopWhenPageFull,
            ConsoleCaptureReadPayload payload,
            Regex regex,
            string[] needles,
            CancellationToken token)
        {
            var result = new ConsoleCaptureLiteralScanResult();
            byte[][] needleBytes = needles.Select(value => Encoding.ASCII.GetBytes(value)).ToArray();
            foreach (string file in GetSegmentFiles(directory))
            {
                var segment = sparseIndex?.segments?.FirstOrDefault(item =>
                    string.Equals(item.fileName, Path.GetFileName(file), StringComparison.OrdinalIgnoreCase));
                if (segment != null && (segment.lastSequence < scanFrom || segment.firstSequence > scanTo)) continue;
                long requestedOffset = FindReadOffset(segment, scanFrom);
                byte[] bytes = File.ReadAllBytes(file);
                int searchStart = requestedOffset > int.MaxValue ? bytes.Length : Math.Max(0, (int)requestedOffset);
                var lineStarts = new HashSet<int>();
                foreach (byte[] needle in needleBytes)
                {
                    int match = searchStart;
                    while ((match = IndexOfBytesBoyerMoore(bytes, match, bytes.Length, needle, true)) >= 0)
                    {
                        int lineStart = match;
                        while (lineStart > searchStart && bytes[lineStart - 1] != (byte)'\n') lineStart--;
                        lineStarts.Add(lineStart);
                        match += Math.Max(1, needle.Length);
                    }
                }

                bool pageFull = false;
                foreach (int lineStart in lineStarts.OrderBy(value => value))
                {
                    token.ThrowIfCancellationRequested();
                    int lineEnd = lineStart;
                    while (lineEnd < bytes.Length && bytes[lineEnd] != (byte)'\n') lineEnd++;
                    if (lineEnd > lineStart && bytes[lineEnd - 1] == (byte)'\r') lineEnd--;
                    if (!TryExtractSequence(bytes, lineStart, lineEnd, out long sequence) || sequence < scanFrom) continue;
                    if (sequence > scanTo) break;
                    string line = Utf8NoBom.GetString(bytes, lineStart, lineEnd - lineStart);
                    ConsoleCaptureRecord record;
                    try { record = JsonUtility.FromJson<ConsoleCaptureRecord>(line); }
                    catch { continue; }
                    if (record == null || !Matches(record, payload, regex)) continue;
                    if (countAllMatches) result.TotalMatches++;
                    if (!payload.includeStackTrace) record.stackTrace = string.Empty;
                    if (result.Matches.Count < count) result.Matches.Add(record);
                    if (stopWhenPageFull && result.Matches.Count >= count)
                    {
                        pageFull = true;
                        break;
                    }
                }
                long segmentFrom = segment == null ? scanFrom : Math.Max(scanFrom, segment.firstSequence);
                long segmentTo = segment == null ? scanTo : Math.Min(scanTo, segment.lastSequence);
                if (segmentTo >= segmentFrom)
                {
                    if (result.ScannedFrom < 0) result.ScannedFrom = segmentFrom;
                    result.ScannedTo = segmentTo;
                    result.ScannedCount += segmentTo - segmentFrom + 1;
                }
                if (pageFull) return result;
            }
            return result;
        }

        private static int IndexOfBytesBoyerMoore(
            byte[] haystack,
            int start,
            int end,
            byte[] needle,
            bool ignoreAsciiCase)
        {
            if (needle == null || needle.Length == 0) return start;
            var shifts = new int[256];
            for (int index = 0; index < shifts.Length; index++) shifts[index] = needle.Length;
            for (int index = 0; index < needle.Length - 1; index++)
                shifts[FoldAscii(needle[index], ignoreAsciiCase)] = needle.Length - 1 - index;
            int cursor = Math.Max(0, start) + needle.Length - 1;
            while (cursor < end)
            {
                int needleIndex = needle.Length - 1;
                int haystackIndex = cursor;
                while (needleIndex >= 0 && FoldAscii(haystack[haystackIndex], ignoreAsciiCase) == FoldAscii(needle[needleIndex], ignoreAsciiCase))
                {
                    haystackIndex--;
                    needleIndex--;
                }
                if (needleIndex < 0) return haystackIndex + 1;
                cursor += Math.Max(1, shifts[FoldAscii(haystack[cursor], ignoreAsciiCase)]);
            }
            return -1;
        }

        private static int IndexOfBytes(byte[] haystack, int start, int end, byte[] needle, bool ignoreAsciiCase)
        {
            if (needle == null || needle.Length == 0) return start;
            int last = end - needle.Length;
            for (int index = start; index <= last; index++)
            {
                if (BytesEqualAt(haystack, index, end, needle, ignoreAsciiCase)) return index;
            }
            return -1;
        }

        private static bool BytesEqualAt(byte[] haystack, int index, int end, byte[] needle, bool ignoreAsciiCase)
        {
            if (needle == null || index < 0 || index + needle.Length > end) return false;
            for (int offset = 0; offset < needle.Length; offset++)
            {
                if (FoldAscii(haystack[index + offset], ignoreAsciiCase) != FoldAscii(needle[offset], ignoreAsciiCase))
                    return false;
            }
            return true;
        }

        private static byte FoldAscii(byte value, bool ignoreAsciiCase)
        {
            return ignoreAsciiCase && value >= (byte)'A' && value <= (byte)'Z'
                ? (byte)(value + 32)
                : value;
        }

        private static string[] GetRawLinePrefilterNeedles(ConsoleCaptureReadPayload payload)
        {
            if (payload == null || string.IsNullOrWhiteSpace(payload.regex)
                || (payload.contains?.Length ?? 0) > 0)
                return null;
            string[] values = payload.regex.Split('|');
            if (values.Length == 0) return null;
            foreach (string value in values)
            {
                if (string.IsNullOrEmpty(value)) return null;
                foreach (char current in value)
                {
                    if (!(current >= 'a' && current <= 'z')
                        && !(current >= 'A' && current <= 'Z')
                        && !(current >= '0' && current <= '9')
                        && current != '_' && current != '-' && current != '.' && current != ':' && current != '/')
                        return null;
                }
            }
            return values;
        }

        private static bool RawLineContainsAny(string line, string[] needles)
        {
            foreach (string needle in needles)
            {
                if (line.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static long FindReadOffset(ConsoleCaptureSparseIndexSegment segment, long fromSequence)
        {
            if (segment?.entries == null || segment.entries.Count == 0) return 0;
            return segment.entries
                .Where(item => item.sequence <= fromSequence)
                .OrderByDescending(item => item.sequence)
                .Select(item => item.byteOffset)
                .FirstOrDefault();
        }

        private static string ComputeReadQueryHash(ConsoleCaptureReadPayload payload)
        {
            string canonical = string.Join("\n", new[]
            {
                payload.logType ?? string.Empty,
                payload.containsAll.ToString(),
                payload.regex ?? string.Empty,
                payload.newestFirst.ToString(),
                string.Join("\u001f", payload.contains ?? Array.Empty<string>()),
            });
            return ComputeTextSha256(canonical);
        }

        private static string EncodeReadCursor(ConsoleCaptureReadCursor cursor)
        {
            return Convert.ToBase64String(Utf8NoBom.GetBytes(JsonUtility.ToJson(cursor)));
        }

        private static ConsoleCaptureReadCursor DecodeReadCursor(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            try
            {
                var cursor = JsonUtility.FromJson<ConsoleCaptureReadCursor>(
                    Utf8NoBom.GetString(Convert.FromBase64String(token)));
                return cursor != null && cursor.version == 1 ? cursor : null;
            }
            catch
            {
                return new ConsoleCaptureReadCursor { version = -1 };
            }
        }

        private static int DetectNewlineBytes(string file)
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int previous = -1;
            int current;
            while ((current = stream.ReadByte()) >= 0)
            {
                if (current == '\n') return previous == '\r' ? 2 : 1;
                previous = current;
            }
            return Utf8NoBom.GetByteCount(Environment.NewLine);
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

        private static string ProjectSessionKey(string key)
        {
            return UPilotPreferences.ProjectKey(key);
        }

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
