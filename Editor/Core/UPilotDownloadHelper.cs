// -----------------------------------------------------------------------
// UPilot Editor - isolated background download session and verification.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodingRiver.UPilot
{
    internal sealed class DownloadProgress
    {
        public string Phase = "";
        public string Outcome = "running";
        public string ErrorMessage = "";
        public string RangeProbeOutcome = "";
        public string RangeProbeDetail = "";
        public long BytesReceived;
        public long TotalBytes;
        public int ConfiguredSegmentCount;
        public int MaxConcurrentSegmentRequests;
        public int SegmentCount;
        public int CompletedSegments;
        public int PeakConcurrentSegmentRequests;
        public int DedicatedThreadId;
        public int ObservedOwnedThreadCount;
        public int ThreadConstraintViolationCount;
        public long SessionDurationMilliseconds;
        public long TransferDurationMilliseconds;
        public long VerificationDurationMilliseconds;
        public double ThroughputBytesPerSecond;
        public string ActualSha256 = "";
        public bool IsComplete;
        public bool IsCancelled;
        internal Thread DedicatedThread;
    }

    internal sealed class DownloadResult
    {
        public bool Success;
        public string Outcome = "";
        public long BytesReceived;
        public long TotalBytes;
        public int ConfiguredSegmentCount;
        public int MaxConcurrentSegmentRequests;
        public int SegmentCount;
        public int CompletedSegments;
        public int PeakConcurrentSegmentRequests;
        public int DedicatedThreadId;
        public int ObservedOwnedThreadCount;
        public int ThreadConstraintViolationCount;
        public long SessionDurationMilliseconds;
        public long TransferDurationMilliseconds;
        public long VerificationDurationMilliseconds;
        public double ThroughputBytesPerSecond;
        public string ActualSha256 = "";
        internal Thread DedicatedThread;
    }

    internal sealed class UPilotDownloadOptions
    {
        public int ParallelThresholdBytes = UPilotDownloadHelper.DefaultParallelThresholdBytes;
        public int SegmentCount = UPilotDownloadHelper.DefaultSegmentCount;
        public int MaxConcurrentSegmentRequests = UPilotDownloadHelper.DefaultMaxConcurrentSegmentRequests;
        public int SegmentRetryCount = UPilotDownloadHelper.DefaultSegmentRetryCount;
        public int BufferSize = UPilotDownloadHelper.DefaultBufferSize;
        public int RetryDelayMilliseconds = 350;
    }

    internal static class UPilotDownloadHelper
    {
        internal const int DefaultParallelThresholdBytes = 8 * 1024 * 1024;
        internal const int DefaultSegmentCount = 12;
        internal const int DefaultMaxConcurrentSegmentRequests = 5;
        internal const int DefaultSegmentRetryCount = 2;
        internal const int DefaultBufferSize = 128 * 1024;

        private static int _sessionSequence;

        public static Task<DownloadResult> DownloadAsync(
            HttpClient http,
            string url,
            string targetPath,
            long expectedBytes,
            string expectedSha256,
            Action<DownloadProgress> reportProgress,
            CancellationToken cancellationToken)
        {
            return DownloadAsync(
                http,
                url,
                targetPath,
                expectedBytes,
                expectedSha256,
                reportProgress,
                cancellationToken,
                new UPilotDownloadOptions());
        }

        internal static Task<DownloadResult> DownloadAsync(
            HttpClient http,
            string url,
            string targetPath,
            long expectedBytes,
            string expectedSha256,
            Action<DownloadProgress> reportProgress,
            CancellationToken cancellationToken,
            UPilotDownloadOptions options)
        {
            if (http == null)
                throw new ArgumentNullException(nameof(http));
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("Download URL is required.", nameof(url));
            if (string.IsNullOrWhiteSpace(targetPath))
                throw new ArgumentException("Download target path is required.", nameof(targetPath));
            if (expectedBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(expectedBytes));
            if (!IsSha256(expectedSha256))
                throw new ArgumentException("Expected SHA-256 must contain 64 hexadecimal characters.", nameof(expectedSha256));
            ValidateOptions(options);

            var completion = new TaskCompletionSource<DownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sessionNumber = Interlocked.Increment(ref _sessionSequence);
            Thread thread = null;
            thread = new Thread(() => RunThread(
                completion,
                thread,
                http,
                url,
                targetPath,
                expectedBytes,
                expectedSha256,
                reportProgress,
                cancellationToken,
                options))
            {
                IsBackground = true,
                Name = "UPilot Download " + sessionNumber,
            };

            try
            {
                thread.Start();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }

            return completion.Task;
        }

        private static void RunThread(
            TaskCompletionSource<DownloadResult> completion,
            Thread thread,
            HttpClient http,
            string url,
            string targetPath,
            long expectedBytes,
            string expectedSha256,
            Action<DownloadProgress> reportProgress,
            CancellationToken cancellationToken,
            UPilotDownloadOptions options)
        {
            var previousContext = SynchronizationContext.Current;
            DownloadSynchronizationContext context = null;
            DownloadResult result = null;
            Exception failure = null;
            try
            {
                context = new DownloadSynchronizationContext(Thread.CurrentThread.ManagedThreadId);
                SynchronizationContext.SetSynchronizationContext(context);
                var session = new SessionState(thread, expectedBytes, options, reportProgress);
                var rootTask = RunSessionAndCompleteLoopAsync(
                    session,
                    context,
                    http,
                    url,
                    targetPath,
                    expectedSha256,
                    cancellationToken);
                context.RunOnCurrentThread();
                result = rootTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                context?.Dispose();
            }

            if (failure == null)
                completion.TrySetResult(result);
            else if (failure is OperationCanceledException)
                completion.TrySetCanceled();
            else
                completion.TrySetException(failure);
        }

        private static async Task<DownloadResult> RunSessionAndCompleteLoopAsync(
            SessionState session,
            DownloadSynchronizationContext context,
            HttpClient http,
            string url,
            string targetPath,
            string expectedSha256,
            CancellationToken cancellationToken)
        {
            try
            {
                return await RunSessionAsync(
                    session,
                    http,
                    url,
                    targetPath,
                    expectedSha256,
                    cancellationToken);
            }
            finally
            {
                context.Complete();
            }
        }

        private static async Task<DownloadResult> RunSessionAsync(
            SessionState session,
            HttpClient http,
            string url,
            string targetPath,
            string expectedSha256,
            CancellationToken cancellationToken)
        {
            Exception failure = null;
            session.CheckThread("session start");
            session.SessionStopwatch.Start();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                session.SetPhase("probing_ranges");
                session.TransferStopwatch.Start();
                var useSegments = session.ExpectedBytes >= session.Options.ParallelThresholdBytes &&
                                  await SupportsRangeDownloadAsync(session, http, url, cancellationToken);
                session.CheckThread("range probe continuation");
                if (session.RangeProbeOutcome.Length != 0) session.Report();

                if (useSegments)
                    await DownloadInSegmentsAsync(session, http, url, targetPath, cancellationToken);
                else
                    await DownloadSingleStreamAsync(session, http, url, targetPath, cancellationToken);
                session.CheckThread("transfer continuation");
                session.TransferStopwatch.Stop();

                cancellationToken.ThrowIfCancellationRequested();
                session.SetPhase("verifying");
                session.VerificationStopwatch.Start();
                VerifyDownloadedFile(session, targetPath, expectedSha256);
                session.VerificationStopwatch.Stop();
                session.CheckThread("verification complete");

                session.Outcome = "success";
                session.IsComplete = true;
                session.SetPhase("complete");
            }
            catch (Exception ex)
            {
                failure = ex;
                session.TransferStopwatch.Stop();
                session.VerificationStopwatch.Stop();
                session.IsCancelled = ex is OperationCanceledException || cancellationToken.IsCancellationRequested;
                session.Outcome = session.IsCancelled ? "cancelled" : "failed";
                session.ErrorMessage = ex.Message;
                session.Phase = session.IsCancelled ? "cancelled" : "failed";
            }

            try
            {
                session.CheckThread("cleanup start");
                CleanupSegmentFiles(targetPath, session.Options.SegmentCount);
                session.CheckThread("cleanup complete");
            }
            catch (Exception ex)
            {
                if (failure == null)
                {
                    failure = ex;
                    session.IsComplete = false;
                    session.Outcome = "failed";
                    session.Phase = "failed";
                    session.ErrorMessage = ex.Message;
                }
                else
                {
                    session.ErrorMessage = session.ErrorMessage + " | Segment cleanup failed: " + ex.Message;
                }
            }
            finally
            {
                session.SessionStopwatch.Stop();
                session.Report();
            }

            if (failure != null)
                ExceptionDispatchInfo.Capture(failure).Throw();
            return session.CreateResult();
        }

        private static async Task<bool> SupportsRangeDownloadAsync(SessionState session, HttpClient http, string url, CancellationToken token)
        {
            session.CheckThread("probe start");
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(0, 0);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                session.CheckThread("probe continuation");
                var range = response.Content.Headers.ContentRange;
                var supported = response.StatusCode == HttpStatusCode.PartialContent && range != null &&
                                string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) &&
                                range.From == 0 && range.To == 0 && range.Length == session.ExpectedBytes;
                session.RangeProbeOutcome = supported ? "supported" : "unsupported";
                session.RangeProbeDetail = supported
                    ? $"HTTP 206; Content-Range: bytes 0-0/{session.ExpectedBytes}"
                    : $"HTTP {(int)response.StatusCode}; " +
                      (range == null ? "Content-Range missing" : "Content-Range does not match expected bytes 0-0/" + session.ExpectedBytes);
                return supported;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                session.CheckThread("probe fallback");
                session.RangeProbeOutcome = "unsupported";
                session.RangeProbeDetail = "Range probe failed: " + ex.GetType().Name;
                return false;
            }
        }

        private static async Task DownloadSingleStreamAsync(SessionState session, HttpClient http, string url,
            string targetPath, CancellationToken token)
        {
            session.CheckThread("single start");
            session.SegmentCount = 1;
            session.SetPhase("downloading");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            session.BeginRequest();
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                session.CheckThread("single response");
                response.EnsureSuccessStatusCode();
                var length = response.Content.Headers.ContentLength;
                if (length.HasValue && length.Value != session.ExpectedBytes)
                    throw new InvalidDataException($"Download content length mismatch: {length.Value} != {session.ExpectedBytes}.");
                using var input = await response.Content.ReadAsStreamAsync();
                session.CheckThread("single content");
                using var output = OpenAsyncFile(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, session.Options.BufferSize);
                var copied = await CopyDownloadStreamAsync(session, input, output, token);
                await output.FlushAsync(token);
                session.CheckThread("single flushed");
                if (copied != session.ExpectedBytes)
                    throw new InvalidDataException($"Downloaded body was truncated: {copied} != {session.ExpectedBytes}.");
            }
            finally { session.EndRequest(); }
            session.CompletedSegments = 1;
            session.Report();
        }

        private static async Task DownloadInSegmentsAsync(SessionState session, HttpClient http, string url,
            string targetPath, CancellationToken token)
        {
            session.CheckThread("segments start");
            var count = (int)Math.Min(session.Options.SegmentCount, session.ExpectedBytes);
            session.SegmentCount = count;
            session.SetPhase("downloading");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var limiter = new SemaphoreSlim(Math.Min(count, session.Options.MaxConcurrentSegmentRequests));
            var tasks = new List<Task>(count);
            var baseSize = session.ExpectedBytes / count;
            var remainder = session.ExpectedBytes % count;
            long start = 0;
            for (var index = 0; index < count; index++)
            {
                var length = baseSize + (index < remainder ? 1 : 0);
                var end = start + length - 1;
                tasks.Add(DownloadSegmentWithRetryAsync(session, http, url, targetPath + ".part" + index,
                    start, end, linked, limiter));
                start = end + 1;
            }

            Exception error = null;
            try { await Task.WhenAll(tasks); }
            catch (Exception ex)
            {
                error = ex;
                linked.Cancel();
                try { await Task.WhenAll(tasks); }
                catch { /* All children reached a terminal state. Preserve the original error. */ }
            }
            session.CheckThread("segments settled");
            if (token.IsCancellationRequested)
                throw new OperationCanceledException(token);
            if (error != null)
            {
                var root = tasks.Where(t => t.IsFaulted && t.Exception != null)
                    .SelectMany(t => t.Exception.Flatten().InnerExceptions)
                    .FirstOrDefault(ex => !(ex is OperationCanceledException));
                ExceptionDispatchInfo.Capture(root ?? error).Throw();
            }

            session.SetPhase("merging");
            using (var output = OpenAsyncFile(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, session.Options.BufferSize))
            {
                for (var index = 0; index < count; index++)
                {
                    using var input = OpenAsyncFile(targetPath + ".part" + index, FileMode.Open,
                        FileAccess.Read, FileShare.Read, session.Options.BufferSize);
                    await input.CopyToAsync(output, session.Options.BufferSize, token);
                    session.CheckThread("merge continuation");
                }
                await output.FlushAsync(token);
                session.CheckThread("merge flush");
            }
        }

        private static async Task DownloadSegmentWithRetryAsync(SessionState session, HttpClient http,
            string url, string path, long start, long end, CancellationTokenSource linked, SemaphoreSlim limiter)
        {
            session.CheckThread("segment start");
            Exception lastFailure = null;
            for (var attempt = 0; attempt <= session.Options.SegmentRetryCount; attempt++)
            {
                linked.Token.ThrowIfCancellationRequested();
                long received = 0;
                try
                {
                    await limiter.WaitAsync(linked.Token);
                    session.CheckThread("segment slot acquired");
                    try
                    {
                        DeleteIfExists(path);
                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.Range = new RangeHeaderValue(start, end);
                        session.BeginRequest();
                        try
                        {
                            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                            session.CheckThread("segment response");
                            ValidateRangeResponse(response, start, end, session.ExpectedBytes);
                            using var input = await response.Content.ReadAsStreamAsync();
                            session.CheckThread("segment content");
                            using var output = OpenAsyncFile(path, FileMode.Create, FileAccess.Write, FileShare.None,
                                session.Options.BufferSize);
                            received = await CopyDownloadStreamAsync(session, input, output, linked.Token,
                                count => received += count);
                            await output.FlushAsync(linked.Token);
                            session.CheckThread("segment flush");
                        }
                        finally { session.EndRequest(); }
                        if (received != end - start + 1)
                            throw new InvalidDataException($"Range {start}-{end} was truncated: {received} bytes.");
                        session.CompletedSegments++;
                        session.Report();
                        return;
                    }
                    finally { limiter.Release(); }
                }
                catch (OperationCanceledException)
                {
                    session.RemoveReceivedBytes(received);
                    DeleteIfExists(path);
                    throw;
                }
                catch (Exception ex)
                {
                    session.RemoveReceivedBytes(received);
                    DeleteIfExists(path);
                    lastFailure = ex;
                    if (attempt < session.Options.SegmentRetryCount)
                    {
                        await Task.Delay(session.Options.RetryDelayMilliseconds * (attempt + 1), linked.Token);
                        session.CheckThread("retry continuation");
                    }
                }
            }
            linked.Cancel();
            throw new InvalidOperationException($"Range {start}-{end} failed after retries.", lastFailure);
        }

        private static async Task<long> CopyDownloadStreamAsync(SessionState session, Stream input,
            Stream output, CancellationToken token, Action<int> onWritten = null)
        {
            session.CheckThread("copy start");
            var buffer = new byte[session.Options.BufferSize];
            long copied = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, 0, buffer.Length, token);
                session.CheckThread("read continuation");
                if (read <= 0) break;
                await output.WriteAsync(buffer, 0, read, token);
                session.CheckThread("write continuation");
                copied += read;
                onWritten?.Invoke(read);
                session.AddReceivedBytes(read);
            }
            return copied;
        }

        private static void ValidateRangeResponse(HttpResponseMessage response, long start, long end, long total)
        {
            if (response.StatusCode != HttpStatusCode.PartialContent)
                throw new InvalidDataException($"Range {start}-{end} returned HTTP {(int)response.StatusCode}, not 206.");
            var range = response.Content.Headers.ContentRange;
            if (range == null || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
                range.From != start || range.To != end || range.Length != total)
                throw new InvalidDataException($"Invalid Content-Range for {start}-{end} of {total}.");
            var length = response.Content.Headers.ContentLength;
            if (length.HasValue && length.Value != end - start + 1)
                throw new InvalidDataException($"Range content length mismatch: {length.Value} != {end - start + 1}.");
        }

        private static void VerifyDownloadedFile(SessionState session, string targetPath, string expectedSha256)
        {
            session.CheckThread("verify start");
            var length = new FileInfo(targetPath).Length;
            if (length != session.ExpectedBytes)
                throw new InvalidDataException($"Downloaded file size mismatch: {length} != {session.ExpectedBytes}.");
            using var sha = SHA256.Create();
            using var stream = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                session.Options.BufferSize, FileOptions.SequentialScan);
            var hash = sha.ComputeHash(stream);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var value in hash) builder.Append(value.ToString("x2"));
            session.ActualSha256 = builder.ToString();
            if (!string.Equals(session.ActualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Downloaded SHA-256 mismatch. Expected {expectedSha256}, actual {session.ActualSha256}.");
        }

        private static FileStream OpenAsyncFile(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize)
        {
            return new FileStream(path, mode, access, share, bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        internal static void CleanupSegmentFiles(string targetPath, int count)
        {
            for (var index = 0; index < count; index++) DeleteIfExists(targetPath + ".part" + index);
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            foreach (var character in value)
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') ||
                      (character >= 'A' && character <= 'F'))) return false;
            return true;
        }

        private static void ValidateOptions(UPilotDownloadOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.ParallelThresholdBytes <= 0 || options.SegmentCount <= 0 ||
                options.MaxConcurrentSegmentRequests <= 0 ||
                options.SegmentRetryCount < 0 || options.BufferSize <= 0 || options.RetryDelayMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(options));
        }

        private sealed class SessionState
        {
            private readonly Action<DownloadProgress> _reportProgress;
            private readonly HashSet<int> _observedThreadIds = new();
            private int _activeRequests;
            private long _lastProgressReportMs;

            public SessionState(Thread dedicatedThread, long expectedBytes, UPilotDownloadOptions options,
                Action<DownloadProgress> reportProgress)
            {
                DedicatedThread = dedicatedThread;
                DedicatedThreadId = Thread.CurrentThread.ManagedThreadId;
                ExpectedBytes = expectedBytes;
                Options = options;
                ConfiguredSegmentCount = options.SegmentCount;
                MaxConcurrentSegmentRequests = options.MaxConcurrentSegmentRequests;
                TotalBytes = expectedBytes;
                _reportProgress = reportProgress;
                _observedThreadIds.Add(DedicatedThreadId);
            }

            public readonly Thread DedicatedThread;
            public readonly int DedicatedThreadId;
            public readonly long ExpectedBytes;
            public readonly int ConfiguredSegmentCount;
            public readonly int MaxConcurrentSegmentRequests;
            public readonly UPilotDownloadOptions Options;
            public readonly Stopwatch SessionStopwatch = new();
            public readonly Stopwatch TransferStopwatch = new();
            public readonly Stopwatch VerificationStopwatch = new();
            public string Phase = "starting";
            public string Outcome = "running";
            public string ErrorMessage = "";
            public string RangeProbeOutcome = "";
            public string RangeProbeDetail = "";
            public string ActualSha256 = "";
            public long BytesReceived;
            public long TotalBytes;
            public int SegmentCount;
            public int CompletedSegments;
            public int PeakConcurrentSegmentRequests;
            public int ThreadConstraintViolationCount;
            public bool IsComplete;
            public bool IsCancelled;

            public void CheckThread(string boundary)
            {
                var current = Thread.CurrentThread.ManagedThreadId;
                _observedThreadIds.Add(current);
                if (current == DedicatedThreadId) return;
                ThreadConstraintViolationCount++;
                throw new InvalidOperationException(
                    $"Download thread constraint violated at {boundary}: expected {DedicatedThreadId}, observed {current}.");
            }

            public void SetPhase(string phase)
            {
                CheckThread("phase");
                Phase = phase;
                Report();
            }

            public void BeginRequest()
            {
                CheckThread("request begin");
                _activeRequests++;
                PeakConcurrentSegmentRequests = Math.Max(PeakConcurrentSegmentRequests, _activeRequests);
                Report();
            }

            public void EndRequest()
            {
                CheckThread("request end");
                _activeRequests--;
                Report();
            }

            public void AddReceivedBytes(int count)
            {
                CheckThread("progress");
                BytesReceived += count;
                if (SessionStopwatch.ElapsedMilliseconds - _lastProgressReportMs >= 100)
                    Report();
            }

            public void RemoveReceivedBytes(long count)
            {
                CheckThread("retry rollback");
                BytesReceived = Math.Max(0, BytesReceived - count);
                Report();
            }

            private double Throughput => TransferStopwatch.Elapsed.TotalSeconds > 0
                ? BytesReceived / TransferStopwatch.Elapsed.TotalSeconds : 0;

            public DownloadResult CreateResult()
            {
                CheckThread("result");
                return new DownloadResult
                {
                    Success = IsComplete && Outcome == "success", Outcome = Outcome,
                    BytesReceived = BytesReceived, TotalBytes = TotalBytes,
                    ConfiguredSegmentCount = ConfiguredSegmentCount,
                    MaxConcurrentSegmentRequests = MaxConcurrentSegmentRequests, SegmentCount = SegmentCount,
                    CompletedSegments = CompletedSegments, PeakConcurrentSegmentRequests = PeakConcurrentSegmentRequests,
                    DedicatedThreadId = DedicatedThreadId, ObservedOwnedThreadCount = _observedThreadIds.Count,
                    ThreadConstraintViolationCount = ThreadConstraintViolationCount,
                    SessionDurationMilliseconds = SessionStopwatch.ElapsedMilliseconds,
                    TransferDurationMilliseconds = TransferStopwatch.ElapsedMilliseconds,
                    VerificationDurationMilliseconds = VerificationStopwatch.ElapsedMilliseconds,
                    ThroughputBytesPerSecond = Throughput, ActualSha256 = ActualSha256,
                    DedicatedThread = DedicatedThread,
                };
            }

            public void Report()
            {
                CheckThread("report");
                _lastProgressReportMs = SessionStopwatch.ElapsedMilliseconds;
                if (_reportProgress == null) return;
                _reportProgress(new DownloadProgress
                {
                    Phase = Phase, Outcome = Outcome, ErrorMessage = ErrorMessage,
                    RangeProbeOutcome = RangeProbeOutcome, RangeProbeDetail = RangeProbeDetail,
                    BytesReceived = BytesReceived, TotalBytes = TotalBytes,
                    ConfiguredSegmentCount = ConfiguredSegmentCount,
                    MaxConcurrentSegmentRequests = MaxConcurrentSegmentRequests, SegmentCount = SegmentCount,
                    CompletedSegments = CompletedSegments, PeakConcurrentSegmentRequests = PeakConcurrentSegmentRequests,
                    DedicatedThreadId = DedicatedThreadId, ObservedOwnedThreadCount = _observedThreadIds.Count,
                    ThreadConstraintViolationCount = ThreadConstraintViolationCount,
                    SessionDurationMilliseconds = SessionStopwatch.ElapsedMilliseconds,
                    TransferDurationMilliseconds = TransferStopwatch.ElapsedMilliseconds,
                    VerificationDurationMilliseconds = VerificationStopwatch.ElapsedMilliseconds,
                    ThroughputBytesPerSecond = Throughput, ActualSha256 = ActualSha256,
                    IsComplete = IsComplete, IsCancelled = IsCancelled, DedicatedThread = DedicatedThread,
                });
                CheckThread("progress callback return");
            }
        }

        private sealed class DownloadSynchronizationContext : SynchronizationContext, IDisposable
        {
            private readonly BlockingCollection<WorkItem> _queue = new();
            private readonly int _ownerThreadId;

            public DownloadSynchronizationContext(int ownerThreadId) { _ownerThreadId = ownerThreadId; }

            public override void Post(SendOrPostCallback callback, object state)
            {
                if (_queue.IsAddingCompleted)
                    throw new InvalidOperationException("Download event loop has completed.");
                _queue.Add(new WorkItem(callback, state, null));
            }

            public override void Send(SendOrPostCallback callback, object state)
            {
                if (Thread.CurrentThread.ManagedThreadId == _ownerThreadId)
                {
                    callback(state);
                    return;
                }
                using var completed = new ManualResetEventSlim(false);
                var item = new WorkItem(callback, state, completed);
                _queue.Add(item);
                completed.Wait();
                item.Failure?.Throw();
            }

            public void RunOnCurrentThread()
            {
                if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
                    throw new InvalidOperationException("Download event loop must run on its owner thread.");
                foreach (var item in _queue.GetConsumingEnumerable()) item.Invoke();
            }

            public void Complete() { if (!_queue.IsAddingCompleted) _queue.CompleteAdding(); }
            public void Dispose() { Complete(); _queue.Dispose(); }

            private sealed class WorkItem
            {
                private readonly SendOrPostCallback _callback;
                private readonly object _state;
                private readonly ManualResetEventSlim _completed;
                public ExceptionDispatchInfo Failure;

                public WorkItem(SendOrPostCallback callback, object state, ManualResetEventSlim completed)
                {
                    _callback = callback;
                    _state = state;
                    _completed = completed;
                }

                public void Invoke()
                {
                    try { _callback(_state); }
                    catch (Exception ex)
                    {
                        Failure = ExceptionDispatchInfo.Capture(ex);
                        if (_completed == null) throw;
                    }
                    finally { _completed?.Set(); }
                }
            }
        }
    }
}
