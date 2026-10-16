using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public static partial class Logger
    {
        private const int LifecycleDetailLimit = 16 * 1024;
        private const int LifecycleStackLimit = 16 * 1024;
        private const string LifecycleSecretNames =
            @"access[_-]?token|refresh[_-]?token|auth[_-]?token|owner[_-]?token|confirm[_-]?token|token|password|passwd|client[_-]?secret|secret|api[_-]?key|(?:bridge[_-]?probe[_-]?|bridge[_-]?|probe[_-]?|request[_-]?|launch[_-]?|session[_-]?|auth[_-]?)?nonce";
        private static readonly Regex LifecycleUrlCredentials = new(
            @"(\b(?:https?|wss?)://)[^\s""'<>/@]+@",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        private static readonly Regex LifecycleUrlQueries = new(
            @"(\b(?:https?|wss?)://[^\s""'<>?#]+)\?[^\s""'<>#]*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        private static readonly Regex LifecycleCommandSecrets = new(
            @"((?<!\S)(?:--?|/)(?:" + LifecycleSecretNames + @")(?:\s*[:=]\s*|\s+))(""(?:\\.|[^""\\\r\n])*""|'(?:\\.|[^'\\\r\n])*'|[^\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        private static readonly Regex LifecycleHeaders = new(
            @"(\b(?:authorization|cookie|set-cookie)\b[""']?\s*[:=]\s*)(""(?:\\.|[^""\\\r\n])*""|'(?:\\.|[^'\\\r\n])*'|[^\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        private static readonly Regex LifecycleSecrets = new(
            @"(\b(?:" + LifecycleSecretNames + @")\b[""']?\s*[:=]\s*)(""(?:\\.|[^""\\\r\n])*""|'(?:\\.|[^'\\\r\n])*'|[^\s,;}&\]]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        private static readonly Regex LifecycleBearer = new(
            @"\bBearer\s+[A-Za-z0-9._~+/=-]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

        /// <summary>有限生命周期事件始终以普通 Log 双写，不改变用户日志级别或 Console 设置。</summary>
        internal static void Lifecycle(string eventName, string detail, string requestStack = null,
            bool captureExecutionStack = false)
        {
            if (IsAssetImportWorker) return;
            try
            {
                var message = new StringBuilder("[UPilot][Lifecycle] utc=")
                    .Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                    .Append(" pid=").Append(CurrentProcessId)
                    .Append(" thread=").Append(Thread.CurrentThread.ManagedThreadId)
                    .Append(" eventId=").Append(Guid.NewGuid().ToString("N"))
                    .Append(" event=").Append(SafeLifecycleText(eventName, 128).Replace('\r', ' ').Replace('\n', ' '))
                    .Append('\n').Append(SafeLifecycleText(detail, LifecycleDetailLimit));
                if (!string.IsNullOrEmpty(requestStack))
                    message.Append("\nrequestStack:\n").Append(SafeLifecycleText(requestStack, LifecycleStackLimit));
                if (captureExecutionStack)
                    message.Append("\nexecutionStack:\n").Append(CaptureLifecycleStack());
                EmitLifecycleEntry(message.ToString(), line => AppendLine(line), line => UnityEngine.Debug.Log(line));
            }
            catch (Exception ex) { ReportLifecycleOutputFailure("prepare", ex); }
        }

        /// <summary>真实生命周期故障使用既有 Error 聚合，原异常正文和堆栈先脱敏再输出。</summary>
        internal static void LifecycleFailure(string eventName, string detail, Exception exception,
            string scope = "", bool terminal = false)
        {
            if (IsAssetImportWorker) return;
            try
            {
                var safeEvent = SafeLifecycleText(eventName, 128).Replace('\r', ' ').Replace('\n', ' ');
                var exceptionType = exception?.GetType().FullName ?? "unknown";
                var exceptionMessage = SafeLifecycleText(exception?.Message ?? detail, LifecycleDetailLimit);
                var root = exception?.GetBaseException();
                var rootType = root?.GetType().FullName ?? "unknown";
                var rootMessage = SafeLifecycleText(root?.Message ?? detail, LifecycleDetailLimit);
                var message = new StringBuilder("[UPilot][Lifecycle] utc=")
                    .Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                    .Append(" pid=").Append(CurrentProcessId)
                    .Append(" thread=").Append(Thread.CurrentThread.ManagedThreadId)
                    .Append(" event=").Append(safeEvent)
                    .Append('\n').Append(SafeLifecycleText(detail, LifecycleDetailLimit));
                if (exception != null)
                {
                    // Reserve independent stack budgets: a large Message must not consume its stack evidence.
                    message.Append("\nexception:\n").Append(SafeLifecycleText(exception.ToString(), LifecycleStackLimit / 2));
                    if (!string.IsNullOrEmpty(exception.StackTrace))
                        message.Append("\noriginalExceptionStack:\n").Append(SafeLifecycleText(exception.StackTrace, LifecycleStackLimit));
                    if (!ReferenceEquals(root, exception) && root != null)
                    {
                        message.Append("\nrootException:\n").Append(rootType).Append(": ")
                            .Append(SafeLifecycleText(root.Message, LifecycleStackLimit / 4));
                        if (!string.IsNullOrEmpty(root.StackTrace))
                            message.Append("\nrootExceptionStack:\n").Append(SafeLifecycleText(root.StackTrace, LifecycleStackLimit));
                    }
                }
                // The raw exception must not reach ReportFailure: it appends Exception.ToString() again.
                ReportFailure("LIFECYCLE", message.ToString(), scope: scope,
                    terminal: terminal, fingerprint: safeEvent + "|" + exceptionType + "|" + exceptionMessage +
                        "|" + rootType + "|" + rootMessage);
            }
            catch (Exception ex) { ReportLifecycleOutputFailure("failure_prepare", ex); }
        }

        /// <summary>显式捕获调用来源，不依赖 Unity 普通 Log 的自动堆栈开关。</summary>
        internal static string CaptureLifecycleStack()
        {
            try { return SafeLifecycleText(new StackTrace(1, true).ToString(), LifecycleStackLimit); }
            catch (Exception ex)
            {
                ReportLifecycleOutputFailure("stack", ex);
                return "[stack unavailable]";
            }
        }

        /// <summary>从 Stopwatch.GetTimestamp() 起点计算耗时，不受墙上时钟调整影响。</summary>
        internal static long LifecycleElapsedMs(long stopwatchStart)
        {
            if (stopwatchStart <= 0) return 0;
            var ticks = Stopwatch.GetTimestamp() - stopwatchStart;
            return ticks <= 0 ? 0 : (long)(ticks * (1000d / Stopwatch.Frequency));
        }

        // 同一正文送至两个独立输出端；某一端失败不能阻断生命周期操作或另一端。
        internal static void EmitLifecycleEntry(string message, Action<string> append, Action<string> console)
        {
            try { append(message); }
            catch (Exception ex) { ReportLifecycleOutputFailure("file", ex); }
            finally
            {
                try { console(message); }
                catch (Exception ex) { ReportLifecycleOutputFailure("console", ex); }
            }
        }

        private static string SafeLifecycleText(string value, int limit)
        {
            try
            {
                // Query names/values may be percent-encoded or contain nested URLs: retain the location only.
                var safe = LifecycleUrlCredentials.Replace(value ?? "", "$1[REDACTED]@");
                safe = LifecycleUrlQueries.Replace(safe, "$1?[REDACTED]");
                safe = LifecycleHeaders.Replace(safe, "$1[REDACTED]");
                safe = LifecycleSecrets.Replace(safe, "$1[REDACTED]");
                safe = LifecycleCommandSecrets.Replace(safe, "$1[REDACTED]");
                safe = LifecycleBearer.Replace(safe, "Bearer [REDACTED]");
                return TruncateLogEntry(safe, limit);
            }
            catch { return "[REDACTED: text sanitization unavailable]"; }
        }

        private static void ReportLifecycleOutputFailure(string sink, Exception exception)
        {
            // 输出故障只尝试 Console，禁止递归写回刚失败的文件或影响原操作。
            try
            {
                UnityEngine.Debug.LogError("[UPilot][Lifecycle] 日志输出失败 sink=" + sink + "\n" +
                    SafeLifecycleText(exception.ToString(), LifecycleStackLimit));
            }
            catch { /* 输出端本身不可用时保留原生命周期流程。 */ }
        }
    }
}
