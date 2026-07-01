// -----------------------------------------------------------------------
// Upilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace codingriver.upilot
{
    // ── M07 Console DTOs ──────────────────────────────────────────────────────

    [Serializable]
    public class ConsoleLogsGetMessage
    {
        public string id;
        public string type;
        public string name;
        public ConsoleLogsGetPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class ConsoleLogsGetPayload
    {
        public string logType;
        public int count = 100;
    }

    [Serializable]
    public class ConsoleLogsMarkPayload
    {
    }

    [Serializable]
    public class ConsoleLogsTailMessage
    {
        public string id;
        public string type;
        public string name;
        public ConsoleLogsTailPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class ConsoleLogsTailPayload
    {
        public int cursor = -1;
        public int count = 200;
        public string logType;
        public bool includeStackTrace;
        public bool excludeUpilot = true;
        public string[] contains;
        public bool containsAll;
        public string regex;
        public bool newestFirst;
        public int maxMessageLength = 0;
    }

    [Serializable]
    public class ConsoleLogsSearchMessage
    {
        public string id;
        public string type;
        public string name;
        public ConsoleLogsSearchPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class ConsoleLogsSearchPayload
    {
        public int count = 200;
        public string logType;
        public bool includeStackTrace;
        public bool excludeUpilot = true;
        public string[] contains;
        public bool containsAll;
        public string regex;
        public bool newestFirst = true;
        public int maxMessageLength = 0;
    }

    [Serializable]
    public class ConsoleLogEntry
    {
        public int index;
        public string logType;
        public string message;
        public string stackTrace;
        public long timestamp;
        public int count;
    }

    [Serializable]
    public class ConsoleLogsResultPayload
    {
        public List<ConsoleLogEntry> logs = new();
        public int total;
    }

    [Serializable]
    public class ConsoleLogsMarkResultPayload
    {
        public int cursor;
        public int totalCount;
    }

    [Serializable]
    public class ConsoleLogsTailResultPayload
    {
        public List<ConsoleLogEntry> logs = new();
        public int cursor;
        public int nextCursor;
        public int totalCount;
        public bool truncated;
    }

    [Serializable]
    public class ConsoleLogsSearchResultPayload
    {
        public List<ConsoleLogEntry> logs = new();
        public int totalCount;
        public int matchedCount;
        public bool truncated;
    }

    // ── M07 Console Service ───────────────────────────────────────────────────

    public sealed class UpilotConsoleService
    {
        private const int RingBufferCapacity = 500;

        private static readonly object _ringLock = new();
        private static readonly List<ConsoleLogEntry> _ringBuffer = new(RingBufferCapacity);
        private static bool _subscribed;

        private readonly UpilotBridge _bridge;

        public UpilotConsoleService(UpilotBridge bridge)
        {
            _bridge = bridge;
            EnsureLogSubscription();
        }

        private static void EnsureLogSubscription()
        {
            if (_subscribed) return;
            _subscribed = true;
            UnityEngine.Application.logMessageReceived += OnLogMessageReceived;
        }

        private static void OnLogMessageReceived(string condition, string stackTrace, UnityEngine.LogType logType)
        {
            var entry = new ConsoleLogEntry
            {
                logType = LogTypeToString(logType),
                message = condition ?? "",
                stackTrace = stackTrace ?? "",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                count = 1,
            };
            lock (_ringLock)
            {
                if (_ringBuffer.Count >= RingBufferCapacity)
                    _ringBuffer.RemoveAt(0);
                _ringBuffer.Add(entry);
            }
        }

        private static string LogTypeToString(UnityEngine.LogType t) => t switch
        {
            UnityEngine.LogType.Error => "Error",
            UnityEngine.LogType.Assert => "Assert",
            UnityEngine.LogType.Warning => "Warning",
            UnityEngine.LogType.Log => "Log",
            UnityEngine.LogType.Exception => "Exception",
            _ => "Log",
        };

        public void RegisterCommands()
        {
            _bridge.Router.Register("console.logs.get", HandleConsoleLogsGetAsync);
            _bridge.Router.Register("console.logs.mark", HandleConsoleLogsMarkAsync);
            _bridge.Router.Register("console.logs.tail", HandleConsoleLogsTailAsync);
            _bridge.Router.Register("console.logs.search", HandleConsoleLogsSearchAsync);
            _bridge.Router.Register("console.clear", HandleConsoleClearAsync);
        }

        private async Task HandleConsoleLogsGetAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ConsoleLogsGetMessage>(json);
            var logType = msg?.payload?.logType ?? "";
            var count = msg?.payload?.count ?? 100;
            if (count <= 0) count = 1;
            if (count > 1000) count = 1000;

            var tcs = new TaskCompletionSource<ConsoleLogsResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var result = GetConsoleLogs(logType, count);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "console.logs.get", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"读取控制台日志失败：{ex.Message}", token, "console.logs.get");
            }
        }

        private async Task HandleConsoleClearAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    ClearConsole();
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                await tcs.Task;
                await _bridge.SendResultAsync(id, "console.clear", new GenericOkPayload { ok = true }, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"清空控制台失败：{ex.Message}", token, "console.clear");
            }
        }

        private async Task HandleConsoleLogsMarkAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<ConsoleLogsMarkResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var total = GetLogEntryCount();
                    tcs.TrySetResult(new ConsoleLogsMarkResultPayload { cursor = total, totalCount = total });
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "console.logs.mark", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"标记控制台日志游标失败：{ex.Message}", token, "console.logs.mark");
            }
        }

        private async Task HandleConsoleLogsTailAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ConsoleLogsTailMessage>(json);
            var payload = msg?.payload ?? new ConsoleLogsTailPayload();
            NormalizeTailPayload(payload);

            var tcs = new TaskCompletionSource<ConsoleLogsTailResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    tcs.TrySetResult(TailConsoleLogs(payload));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "console.logs.tail", result, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"读取控制台增量日志失败：{ex.Message}", token, "console.logs.tail");
            }
        }

        private async Task HandleConsoleLogsSearchAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ConsoleLogsSearchMessage>(json);
            var payload = msg?.payload ?? new ConsoleLogsSearchPayload();
            NormalizeSearchPayload(payload);

            var tcs = new TaskCompletionSource<ConsoleLogsSearchResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    tcs.TrySetResult(SearchConsoleLogs(payload));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "console.logs.search", result, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"搜索控制台日志失败：{ex.Message}", token, "console.logs.search");
            }
        }

        // ── public API — ring buffer primary, reflection fallback ────────────

        private static ConsoleLogsResultPayload GetConsoleLogs(string logType, int count)
        {
            // Primary: read from Application.logMessageReceived ring buffer
            var result = GetLogsFromRingBuffer(logType, count);
            if (result.logs.Count > 0)
                return result;

            // Fallback: reflection on public LogEntries API
            return GetLogsViaReflection(logType, count);
        }

        private static ConsoleLogsResultPayload GetLogsFromRingBuffer(string logType, int count)
        {
            var result = new ConsoleLogsResultPayload();
            lock (_ringLock)
            {
                for (int i = _ringBuffer.Count - 1; i >= 0 && result.logs.Count < count; i--)
                {
                    var entry = _ringBuffer[i];
                    if (!string.IsNullOrEmpty(logType) &&
                        !string.Equals(entry.logType, logType, StringComparison.OrdinalIgnoreCase))
                        continue;
                    result.logs.Add(entry);
                }
            }
            result.total = result.logs.Count;
            return result;
        }

        private static ConsoleLogsResultPayload GetLogsViaReflection(string logType, int count)
        {
            var result = new ConsoleLogsResultPayload();

            var logEntriesType = GetLogEntriesType();
            if (logEntriesType == null) return result;

            var startMethod = logEntriesType.GetMethod("StartGettingEntries",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var endMethod = logEntriesType.GetMethod("EndGettingEntries",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var getEntryMethod = logEntriesType.GetMethod("GetEntryInternal",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var getCount = logEntriesType.GetMethod("GetCount",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            if (getCount == null) return result;

            int totalCount = (int)getCount.Invoke(null, null);
            if (totalCount == 0) return result;

            startMethod?.Invoke(null, null);

            try
            {
                var logEntryType = GetLogEntryType();

                if (logEntryType == null || getEntryMethod == null)
                    return FallbackGetLogs(logEntriesType, logType, count, totalCount);

                var entry = Activator.CreateInstance(logEntryType);
                var messageField = logEntryType.GetField("message") ?? logEntryType.GetField("condition");
                var modeField = logEntryType.GetField("mode");

                for (int i = totalCount - 1; i >= 0 && result.logs.Count < count; i--)
                {
                    bool ok = (bool)getEntryMethod.Invoke(null, new object[] { i, entry });
                    if (!ok) continue;

                    int mode = modeField != null ? (int)modeField.GetValue(entry) : 0;
                    string entryType = ModeToLogType(mode);

                    if (!string.IsNullOrEmpty(logType) &&
                        !string.Equals(entryType, logType, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string msg = messageField?.GetValue(entry)?.ToString() ?? "";
                    string stackTrace = "";
                    int nlIndex = msg.IndexOf('\n');
                    if (nlIndex >= 0)
                    {
                        stackTrace = msg.Substring(nlIndex + 1);
                        msg = msg.Substring(0, nlIndex);
                    }

                    result.logs.Add(new ConsoleLogEntry
                    {
                        index = i,
                        logType = entryType,
                        message = msg,
                        stackTrace = stackTrace,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        count = 1
                    });
                }

                result.total = result.logs.Count;
            }
            finally
            {
                endMethod?.Invoke(null, null);
            }

            return result;
        }

        private static ConsoleLogsResultPayload FallbackGetLogs(System.Type logEntriesType, string logType, int count, int totalCount)
        {
            var result = new ConsoleLogsResultPayload();

            var getEntryAtIndex = logEntriesType.GetMethod("GetEntryStringAtIndex",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (getEntryAtIndex == null) return result;

            for (int i = totalCount - 1; i >= 0 && result.logs.Count < count; i--)
            {
                string msg = getEntryAtIndex.Invoke(null, new object[] { i })?.ToString() ?? "";
                result.logs.Add(new ConsoleLogEntry
                {
                    index = i,
                    logType = "Log",
                    message = msg,
                    stackTrace = "",
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    count = 1
                });
            }
            result.total = result.logs.Count;
            return result;
        }

        private static void NormalizeTailPayload(ConsoleLogsTailPayload payload)
        {
            if (payload.count <= 0) payload.count = 1;
            if (payload.count > 5000) payload.count = 5000;
            if (payload.contains == null) payload.contains = Array.Empty<string>();
            if (payload.maxMessageLength < 0) payload.maxMessageLength = 0;
        }

        private static void NormalizeSearchPayload(ConsoleLogsSearchPayload payload)
        {
            if (payload.count <= 0) payload.count = 1;
            if (payload.count > 5000) payload.count = 5000;
            if (payload.contains == null) payload.contains = Array.Empty<string>();
            if (payload.maxMessageLength < 0) payload.maxMessageLength = 0;
        }

        private static ConsoleLogsTailResultPayload TailConsoleLogs(ConsoleLogsTailPayload payload)
        {
            int total = GetLogEntryCount();
            int cursor = payload.cursor < 0 ? total : payload.cursor;
            if (cursor < 0) cursor = 0;
            if (cursor > total) cursor = total;

            var result = new ConsoleLogsTailResultPayload
            {
                cursor = cursor,
                nextCursor = total,
                totalCount = total,
            };

            WithLogEntries((logEntriesType, getEntryMethod, logEntryType, messageField, modeField) =>
            {
                if (payload.newestFirst)
                {
                    for (int i = total - 1; i >= cursor && result.logs.Count < payload.count; i--)
                        TryAddLogEntry(result.logs, i, payload, getEntryMethod, logEntryType, messageField, modeField);
                }
                else
                {
                    for (int i = cursor; i < total && result.logs.Count < payload.count; i++)
                        TryAddLogEntry(result.logs, i, payload, getEntryMethod, logEntryType, messageField, modeField);
                }
            });

            result.truncated = CountPotentialRange(cursor, total) > result.logs.Count && result.logs.Count >= payload.count;
            return result;
        }

        private static ConsoleLogsSearchResultPayload SearchConsoleLogs(ConsoleLogsSearchPayload payload)
        {
            int total = GetLogEntryCount();
            var result = new ConsoleLogsSearchResultPayload { totalCount = total };

            WithLogEntries((logEntriesType, getEntryMethod, logEntryType, messageField, modeField) =>
            {
                if (payload.newestFirst)
                {
                    for (int i = total - 1; i >= 0; i--)
                    {
                        if (TryAddLogEntry(result.logs, i, payload, getEntryMethod, logEntryType, messageField, modeField))
                            result.matchedCount++;
                        if (result.logs.Count >= payload.count) break;
                    }
                }
                else
                {
                    for (int i = 0; i < total; i++)
                    {
                        if (TryAddLogEntry(result.logs, i, payload, getEntryMethod, logEntryType, messageField, modeField))
                            result.matchedCount++;
                        if (result.logs.Count >= payload.count) break;
                    }
                }
            });

            result.truncated = result.logs.Count >= payload.count;
            return result;
        }

        private static int CountPotentialRange(int cursor, int total)
        {
            return total > cursor ? total - cursor : 0;
        }

        private static bool TryAddLogEntry(
            List<ConsoleLogEntry> logs,
            int index,
            ConsoleLogsTailPayload payload,
            MethodInfo getEntryMethod,
            Type logEntryType,
            FieldInfo messageField,
            FieldInfo modeField)
        {
            return TryAddLogEntryCore(
                logs, index, payload.logType, payload.includeStackTrace, payload.excludeUpilot,
                payload.contains, payload.containsAll, payload.regex, payload.maxMessageLength,
                getEntryMethod, logEntryType, messageField, modeField);
        }

        private static bool TryAddLogEntry(
            List<ConsoleLogEntry> logs,
            int index,
            ConsoleLogsSearchPayload payload,
            MethodInfo getEntryMethod,
            Type logEntryType,
            FieldInfo messageField,
            FieldInfo modeField)
        {
            return TryAddLogEntryCore(
                logs, index, payload.logType, payload.includeStackTrace, payload.excludeUpilot,
                payload.contains, payload.containsAll, payload.regex, payload.maxMessageLength,
                getEntryMethod, logEntryType, messageField, modeField);
        }

        private static bool TryAddLogEntryCore(
            List<ConsoleLogEntry> logs,
            int index,
            string logType,
            bool includeStackTrace,
            bool excludeUpilot,
            string[] contains,
            bool containsAll,
            string regex,
            int maxMessageLength,
            MethodInfo getEntryMethod,
            Type logEntryType,
            FieldInfo messageField,
            FieldInfo modeField)
        {
            var entry = Activator.CreateInstance(logEntryType);
            bool ok = (bool)getEntryMethod.Invoke(null, new object[] { index, entry });
            if (!ok) return false;

            int mode = modeField != null ? (int)modeField.GetValue(entry) : 0;
            string entryType = ModeToLogType(mode);
            if (!string.IsNullOrEmpty(logType) &&
                !string.Equals(entryType, logType, StringComparison.OrdinalIgnoreCase))
                return false;

            string msg = messageField?.GetValue(entry)?.ToString() ?? "";
            string stackTrace = "";
            int nlIndex = msg.IndexOf('\n');
            if (nlIndex >= 0)
            {
                stackTrace = msg.Substring(nlIndex + 1);
                msg = msg.Substring(0, nlIndex);
            }

            if (excludeUpilot && IsUpilotLog(msg, stackTrace))
                return false;

            if (!MatchesText(msg, stackTrace, contains, containsAll, regex))
                return false;

            if (maxMessageLength > 0 && msg.Length > maxMessageLength)
                msg = msg.Substring(0, maxMessageLength);

            logs.Add(new ConsoleLogEntry
            {
                index = index,
                logType = entryType,
                message = msg,
                stackTrace = includeStackTrace ? stackTrace : "",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                count = 1
            });
            return true;
        }

        private static bool MatchesText(string message, string stackTrace, string[] contains, bool containsAll, string regex)
        {
            string haystack = message + "\n" + stackTrace;
            bool hasContains = contains != null && contains.Length > 0;

            if (hasContains)
            {
                bool any = false;
                foreach (var raw in contains)
                {
                    if (string.IsNullOrEmpty(raw)) continue;
                    bool hit = haystack.IndexOf(raw, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (containsAll && !hit) return false;
                    if (hit) any = true;
                }
                if (!containsAll && !any) return false;
            }

            if (!string.IsNullOrEmpty(regex))
            {
                try
                {
                    if (!Regex.IsMatch(haystack, regex, RegexOptions.IgnoreCase))
                        return false;
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsUpilotLog(string message, string stackTrace)
        {
            return ContainsUpilot(message) || ContainsUpilot(stackTrace);
        }

        private static bool ContainsUpilot(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return value.IndexOf("Upilot", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("codingriver.upilot", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("[COMMAND ]", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("upilot", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int GetLogEntryCount()
        {
            var logEntriesType = GetLogEntriesType();
            if (logEntriesType == null) return 0;
            var getCount = logEntriesType.GetMethod("GetCount",
                BindingFlags.Public | BindingFlags.Static);
            return getCount != null ? (int)getCount.Invoke(null, null) : 0;
        }

        private static void WithLogEntries(Action<Type, MethodInfo, Type, FieldInfo, FieldInfo> action)
        {
            var logEntriesType = GetLogEntriesType();
            var logEntryType = GetLogEntryType();
            if (logEntriesType == null || logEntryType == null) return;

            var startMethod = logEntriesType.GetMethod("StartGettingEntries",
                BindingFlags.Public | BindingFlags.Static);
            var endMethod = logEntriesType.GetMethod("EndGettingEntries",
                BindingFlags.Public | BindingFlags.Static);
            var getEntryMethod = logEntriesType.GetMethod("GetEntryInternal",
                BindingFlags.Public | BindingFlags.Static);
            if (getEntryMethod == null) return;

            var messageField = logEntryType.GetField("message") ?? logEntryType.GetField("condition");
            var modeField = logEntryType.GetField("mode");

            startMethod?.Invoke(null, null);
            try
            {
                action(logEntriesType, getEntryMethod, logEntryType, messageField, modeField);
            }
            finally
            {
                endMethod?.Invoke(null, null);
            }
        }

        private static Type GetLogEntriesType()
        {
            return typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries")
                ?? typeof(UnityEditor.Editor).Assembly.GetType("UnityEditorInternal.LogEntries");
        }

        private static Type GetLogEntryType()
        {
            return typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntry")
                ?? typeof(UnityEditor.Editor).Assembly.GetType("UnityEditorInternal.LogEntry");
        }

        private static string ModeToLogType(int mode)
        {
            if ((mode & (1 << 0)) != 0) return "Error";
            if ((mode & (1 << 1)) != 0) return "Assert";
            if ((mode & (1 << 3)) != 0) return "Exception";
            if ((mode & (1 << 8)) != 0) return "Error";
            if ((mode & (1 << 11)) != 0) return "Error";
            if ((mode & (1 << 9)) != 0) return "Warning";
            if ((mode & (1 << 12)) != 0) return "Warning";
            return "Log";
        }

        private static void ClearConsole()
        {
            var logEntriesType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries");
            if (logEntriesType == null)
                logEntriesType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditorInternal.LogEntries");
            if (logEntriesType == null) return;

            var clearMethod = logEntriesType.GetMethod("Clear",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            clearMethod?.Invoke(null, null);
        }
    }
}
