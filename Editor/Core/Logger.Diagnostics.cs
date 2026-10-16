using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("UPilot.FlowBridge")]

namespace CodingRiver.UPilot
{
    public static partial class Logger
    {
        private static readonly DiagnosticBuffer Diagnostics = new(EmitDiagnostic);
        private static long _nextDiagnosticPoll;

        // Infrastructure failures use this boundary; normal event/report streams remain complete.
        internal static void ReportFailure(string category, string message, Exception exception = null,
            string scope = "", bool terminal = false, string fingerprint = null)
        {
            if (IsAssetImportWorker || !ShouldLog(LogLevel.Error)) return;
            string body = message + (exception == null ? "" : "\n" + exception);
            string key = category + "|" + (fingerprint ?? message) + "|" + exception?.GetType().FullName + "|" + exception?.Message;
            Diagnostics.Report(key, $"[ERROR] [{category,-8}] {body}", LogType.Error, scope, terminal);
        }

        internal static void ReportWarning(string category, string message, string scope = "", string fingerprint = null)
        {
            if (IsAssetImportWorker || !ShouldLog(LogLevel.Warning)) return;
            Diagnostics.Report(category + "|" + (fingerprint ?? message), $"[WARN ] [{category,-8}] {message}", LogType.Warning, scope);
        }

        internal static void CloseDiagnostics(string scope) => Diagnostics.Flush(scope);
        private static void FlushDiagnostics() => Diagnostics.Flush();
        private static void PollDiagnostics()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now < _nextDiagnosticPoll) return;
            _nextDiagnosticPoll = now + 1000;
            Diagnostics.Poll();
        }

        private static void EmitDiagnostic(string message, LogType type, bool file)
        {
            try { if (file) AppendLine(message); }
            catch (Exception ex) { UnityEngine.Debug.LogError("[UPilot] 日志写入失败\n" + ex); }
            finally { WriteToUnityConsole(message, type); }
        }

        internal static void ReportFileDiagnostic(string operation, string path, Exception exception)
        {
            // A broken file destination must never call its own writer again.
            Diagnostics.Report("file|" + operation + "|" + path + "|" + exception.GetType().FullName + "|" + exception.Message,
                $"[UPilot] 日志文件异常 operation={operation} path={path}\n{exception}", LogType.Error, scope: "file|" + path, file: false);
        }

        internal static void FileDiagnosticRecovered(string path) => Diagnostics.Flush("file|" + path);

        internal sealed class DiagnosticBuffer
        {
            internal const long WindowMs = 10000;
            internal const int Capacity = 256;
            private readonly object _gate = new();
            private readonly Dictionary<string, Entry> _entries = new();
            private readonly Action<string, LogType, bool> _emit;
            private readonly Func<long> _clock;
            private sealed class Entry
            {
                internal string Message, Scope;
                internal LogType Type;
                internal long First, Last, Emitted, Pending;
                internal bool File, Terminal;
            }

            internal DiagnosticBuffer(Action<string, LogType, bool> emit, Func<long> clock = null)
            { _emit = emit; _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
            internal int Count { get { lock (_gate) return _entries.Count; } }

            internal void Report(string key, string message, LogType type, string scope = "", bool terminal = false, bool file = true)
            {
                var output = new List<(string, LogType, bool)>();
                try
                {
                    lock (_gate)
                    {
                        long now = _clock();
                        key = (scope ?? "") + "\n" + key;
                        if (_entries.TryGetValue(key, out var entry))
                        {
                            entry.Last = now;
                            if (entry.Terminal && terminal) return;
                            if (!terminal && entry.Type == type)
                            {
                                entry.Pending++;
                                if (now - entry.Emitted >= WindowMs) Summary(entry, now, output);
                                return;
                            }
                            Summary(entry, now, output); // Escalation/terminal must remain immediately visible.
                        }
                        else
                        {
                            if (_entries.Count >= Capacity)
                            {
                                var oldest = _entries.OrderBy(x => x.Value.Last).First();
                                Summary(oldest.Value, now, output);
                                _entries.Remove(oldest.Key);
                            }
                            entry = new Entry { First = now, Scope = scope ?? "" };
                            _entries[key] = entry;
                        }
                        entry.Message = message; entry.Type = type; entry.Last = entry.Emitted = now;
                        entry.File = file; entry.Terminal = terminal;
                        output.Add((entry.Message, entry.Type, entry.File));
                        if (terminal) FlushEntries(scope, true, output);
                    }
                }
                finally { EmitAll(output); }
            }

            internal void Poll()
            {
                var output = new List<(string, LogType, bool)>();
                lock (_gate)
                {
                    long now = _clock();
                    foreach (var item in _entries.ToArray())
                    {
                        if (now - item.Value.Emitted >= WindowMs) Summary(item.Value, now, output);
                        if (now - item.Value.Last >= 60000) _entries.Remove(item.Key);
                    }
                }
                EmitAll(output);
            }

            internal void Flush(string scope = null, bool preserveTerminal = false)
            {
                var output = new List<(string, LogType, bool)>();
                lock (_gate) FlushEntries(scope, preserveTerminal, output);
                EmitAll(output);
            }

            private void FlushEntries(string scope, bool preserveTerminal, List<(string, LogType, bool)> output)
            {
                long now = _clock();
                foreach (var item in _entries.ToArray())
                {
                    if (scope != null && item.Value.Scope != scope) continue;
                    Summary(item.Value, now, output);
                    if (!preserveTerminal || !item.Value.Terminal) _entries.Remove(item.Key);
                }
            }

            private void Summary(Entry entry, long now, List<(string, LogType, bool)> output)
            {
                if (entry.Pending == 0) return;
                long count = entry.Pending; entry.Pending = 0; entry.Emitted = now;
                output.Add((entry.Message + $"\n[UPilot] 合并重复日志 count={count} firstAt={entry.First} lastAt={entry.Last}", entry.Type, entry.File));
            }
            private void EmitAll(List<(string, LogType, bool)> output)
            {
                // Never call a file/Console sink while holding the aggregation lock.
                // File-fault callbacks can reenter this buffer from a different writer lock.
                foreach (var item in output) Emit(item.Item1, item.Item2, item.Item3);
            }
            private void Emit(string message, LogType type, bool file)
            {
                try { _emit(message, type, file); }
                catch (Exception ex) { UnityEngine.Debug.LogError("[UPilot] 诊断输出失败\n" + ex); }
            }
        }
    }
}
