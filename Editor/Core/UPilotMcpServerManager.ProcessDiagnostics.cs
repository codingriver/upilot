// SPDX-License-Identifier: MIT
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace CodingRiver.UPilot
{
    public sealed partial class UPilotMcpServerManager
    {
        internal enum ProcessCommandLineOutcome
        {
            Succeeded, DeadlineElapsed, HelperStartFailed, HelperTimedOut, OutputTimedOut,
            HelperExitFailed, EmptyValue, InvalidOutput, QueryFailed, UnsupportedPlatform, HelperCleanupFailed,
        }

        internal sealed class ProcessCommandLineQueryObservation
        {
            internal bool Exited, StdoutComplete;
            internal int? ExitCode, StderrLength, HelperProcessId;
            internal string Stdout = "", FailureStage = "";
            internal Exception Exception, CleanupException;
        }

        internal sealed class ProcessCommandLineReadResult
        {
            internal ProcessCommandLineOutcome Outcome;
            internal int ProcessId;
            internal long ElapsedMs, DeadlineUtcMs, RemainingBudgetMs;
            internal int? ExitCode, OsError, StdoutLength, StderrLength, HelperProcessId, HelperCleanupOsError;
            internal string CommandLine = "", Stage = "";
            internal Exception Exception, CleanupException;
            internal bool Succeeded => Outcome == ProcessCommandLineOutcome.Succeeded;
            internal string LegacyValue => Succeeded ? CommandLine : Outcome == ProcessCommandLineOutcome.EmptyValue
                ? "(空)" : Outcome == ProcessCommandLineOutcome.UnsupportedPlatform
                ? "(当前平台未实现命令行读取)" : "(读取命令行失败)";
            internal string SafeDetail => $"provider=wmic outcome={Outcome} stage={Stage} targetPid={ProcessId} elapsedMs={ElapsedMs} " +
                $"deadlineUtcMs={(DeadlineUtcMs > 0 ? DeadlineUtcMs.ToString() : "unknown")} remainingBudgetMs={(DeadlineUtcMs > 0 ? RemainingBudgetMs.ToString() : "unknown")} " +
                $"exitCode={ExitCode?.ToString() ?? "unknown"} osError={OsError?.ToString() ?? "unknown"} " +
                $"stdoutChars={StdoutLength?.ToString() ?? "unknown"} stderrChars={StderrLength?.ToString() ?? "unknown"} " +
                $"helperPid={HelperProcessId?.ToString() ?? "unknown"} helperCleanupOsError={HelperCleanupOsError?.ToString() ?? "unknown"}";
        }

        // The seam supplies query observations only; tests never launch WMIC or inspect another process.
        internal static ProcessCommandLineReadResult ProbeProcessCommandLine(int pid, long deadlineUtcMs = 0,
            Func<int, long, ProcessCommandLineQueryObservation> queryOverride = null,
            Func<long> utcClockOverride = null, string diagnosticScope = "", CancellationToken token = default,
            int helperTimeoutMs = 1000)
        {
            var started = Stopwatch.GetTimestamp();
            var clock = utcClockOverride ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var result = new ProcessCommandLineReadResult { ProcessId = pid, DeadlineUtcMs = deadlineUtcMs, Stage = "before_query" };
            var interrupted = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (deadlineUtcMs > 0 && clock() >= deadlineUtcMs)
                {
                    result.Outcome = ProcessCommandLineOutcome.DeadlineElapsed;
                    return result;
                }
#if !UNITY_EDITOR_WIN
                if (queryOverride == null)
                {
                    result.Outcome = ProcessCommandLineOutcome.UnsupportedPlatform;
                    return result;
                }
#endif
                var observed = queryOverride != null ? queryOverride(pid, deadlineUtcMs) :
                    ExecuteProcessCommandLineQuery(pid, deadlineUtcMs, token, helperTimeoutMs);
                if (observed == null) throw new InvalidOperationException("Process command-line query returned no observation.");
                result.Stage = observed.FailureStage ?? "";
                result.ExitCode = observed.ExitCode;
                result.StdoutLength = observed.StdoutComplete ? (int?)observed.Stdout.Length : null;
                result.StderrLength = observed.StderrLength;
                result.HelperProcessId = observed.HelperProcessId;
                result.CleanupException = observed.CleanupException;
                result.HelperCleanupOsError = (observed.CleanupException as Win32Exception)?.NativeErrorCode;
                result.Exception = observed.Exception;
                result.OsError = (observed.Exception as Win32Exception)?.NativeErrorCode;
                if (observed.Exception is ThreadAbortException ||
                    (observed.Exception is OperationCanceledException && token.IsCancellationRequested))
                    ExceptionDispatchInfo.Capture(observed.Exception).Throw();
                token.ThrowIfCancellationRequested();
                if (observed.Exception != null)
                    result.Outcome = result.Stage == "start" ? ProcessCommandLineOutcome.HelperStartFailed : ProcessCommandLineOutcome.QueryFailed;
                else if (deadlineUtcMs > 0 && clock() >= deadlineUtcMs)
                    result.Outcome = ProcessCommandLineOutcome.DeadlineElapsed;
                else if (!observed.Exited)
                    result.Outcome = ProcessCommandLineOutcome.HelperTimedOut;
                else if (observed.ExitCode != 0)
                    result.Outcome = ProcessCommandLineOutcome.HelperExitFailed;
                else if (!observed.StdoutComplete)
                    result.Outcome = ProcessCommandLineOutcome.OutputTimedOut;
                else
                {
                    result.Stage = "parse_output";
                    var output = observed.Stdout.Trim().TrimStart('\uFEFF').TrimStart();
                    const string marker = "CommandLine=";
                    if (output.Length == 0) result.Outcome = ProcessCommandLineOutcome.EmptyValue;
                    else if (output.Length > 64 * 1024 || !output.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                        result.Outcome = ProcessCommandLineOutcome.InvalidOutput;
                    else
                    {
                        result.CommandLine = output.Substring(marker.Length).Trim();
                        result.Outcome = result.CommandLine.Length == 0 ? ProcessCommandLineOutcome.EmptyValue : ProcessCommandLineOutcome.Succeeded;
                    }
                }
                if (result.Succeeded && result.CleanupException != null)
                    result.Outcome = ProcessCommandLineOutcome.HelperCleanupFailed;
                return result;
            }
            catch (ThreadAbortException) { interrupted = true; throw; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { interrupted = true; throw; }
            catch (Exception ex)
            {
                result.Outcome = ProcessCommandLineOutcome.QueryFailed;
                result.Exception = ex;
                result.OsError = (ex as Win32Exception)?.NativeErrorCode;
                return result;
            }
            finally
            {
                result.ElapsedMs = Logger.LifecycleElapsedMs(started);
                result.RemainingBudgetMs = deadlineUtcMs > 0 ? Math.Max(0, deadlineUtcMs - clock()) : 0;
                var scope = string.IsNullOrEmpty(diagnosticScope) ? "process-command-line|" + pid : diagnosticScope;
                if (!interrupted && !result.Succeeded)
                {
                    var failure = result.Exception != null && result.CleanupException != null
                        ? new AggregateException(result.Exception, result.CleanupException)
                        : result.Exception ?? result.CleanupException ?? new InvalidOperationException(
                        "Process command-line query failed: " + result.Outcome + " stage=" + result.Stage);
                    Logger.LifecycleFailure("process_command_line_failed", result.SafeDetail, failure,
                        scope: scope);
                }
                else if (interrupted && result.CleanupException != null)
                    Logger.LifecycleFailure("process_command_line_helper_cleanup_failed", result.SafeDetail,
                        result.CleanupException, scope: scope);
                else if (!interrupted) Logger.CloseDiagnostics(scope);
            }
        }

        internal static int ProcessCommandLineWaitMs(long deadlineUtcMs, int maximumMs, long nowUtcMs) =>
            deadlineUtcMs > 0 ? (int)Math.Max(0, Math.Min(maximumMs, deadlineUtcMs - nowUtcMs)) : maximumMs;

        private static ProcessCommandLineQueryObservation ExecuteProcessCommandLineQuery(int pid, long deadlineUtcMs,
            CancellationToken token, int helperTimeoutMs)
        {
            var observation = new ProcessCommandLineQueryObservation { FailureStage = "start" };
            Process process = null;
            var interrupted = false;
            try
            {
                token.ThrowIfCancellationRequested();
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = "wmic", Arguments = $"process where ProcessId={pid} get CommandLine /value",
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                });
                if (process == null) throw new InvalidOperationException("WMIC did not return a helper process.");
                observation.HelperProcessId = process.Id;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                _ = stdout.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                _ = stderr.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                observation.FailureStage = "process_exit";
                var waitDeadline = ProcessQueryDeadline(deadlineUtcMs, helperTimeoutMs,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                observation.Exited = WaitForProcessQuery(() => process.WaitForExit(0), waitDeadline, token);
                if (!observation.Exited) return observation;
                observation.ExitCode = process.ExitCode;
                observation.FailureStage = "stdout_read";
                var outputDeadline = ProcessQueryDeadline(deadlineUtcMs, 100,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                observation.StdoutComplete = WaitForProcessQuery(() => stdout.IsCompleted, outputDeadline, token);
                if (observation.StdoutComplete) observation.Stdout = stdout.GetAwaiter().GetResult();
                if (stderr.Status == TaskStatus.RanToCompletion) observation.StderrLength = stderr.Result.Length;
                return observation;
            }
            catch (ThreadAbortException) { interrupted = true; throw; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { interrupted = true; throw; }
            catch (Exception ex) { observation.Exception = ex; return observation; }
            finally
            {
                // Only this query helper is terminated; the target PID is read-only evidence.
                if (process != null)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(); // Process owns the helper handle; never kill the inspected target PID.
                            var cleanupWait = ProcessCommandLineWaitMs(deadlineUtcMs, 250,
                                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                            if (cleanupWait > 0 && !process.WaitForExit(cleanupWait))
                                throw new TimeoutException("The process command-line helper did not exit after termination.");
                        }
                    }
                    catch (Exception ex) { observation.CleanupException = ex; }
                    try { process.Dispose(); }
                    catch (Exception ex) { observation.CleanupException = observation.CleanupException == null ? ex :
                        new AggregateException(observation.CleanupException, ex); }
                    if (interrupted && observation.CleanupException != null)
                        Logger.LifecycleFailure("process_command_line_helper_cleanup_failed",
                            $"targetPid={pid} helperPid={observation.HelperProcessId}", observation.CleanupException,
                            scope: "process-command-line|" + pid);
                }
            }
        }

        internal static long ProcessQueryDeadline(long parentDeadlineUtcMs, int maximumMs, long nowUtcMs) =>
            parentDeadlineUtcMs > 0 ? Math.Min(parentDeadlineUtcMs, nowUtcMs + maximumMs) : nowUtcMs + maximumMs;

        private static bool WaitForProcessQuery(Func<bool> completed, long deadlineUtcMs, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (completed()) return true;
                var remaining = ProcessCommandLineWaitMs(deadlineUtcMs, 100,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (remaining <= 0) return false;
                if (token.WaitHandle.WaitOne(remaining)) token.ThrowIfCancellationRequested();
            }
        }
    }
}
