// Deterministic tests for the package download's dedicated-thread session.
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotDownloadHelperTests
    {
        private string _directory;
        private string Target => Path.Combine(_directory, "download.bin");
        private const string Url = "https://download.test/file";

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "UPilotDownloadHelperTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        [Test]
        public void DefaultOptionsUse512KiBSegmentsAndEightConcurrentRequests()
        {
            var options = new UPilotDownloadOptions();
            Assert.That(options.MaxSegmentSizeBytes, Is.EqualTo(512 * 1024));
            Assert.That(options.MaxConcurrentSegmentRequests, Is.EqualTo(8));
        }

        [TestCase(1L, 524288L, 1)]
        [TestCase(524288L, 524288L, 1)]
        [TestCase(524289L, 524288L, 2)]
        [TestCase(1048576L, 524288L, 2)]
        [TestCase(26942250L, 524288L, 52)]
        [TestCase(long.MaxValue, long.MaxValue, 1)]
        [TestCase(long.MaxValue, long.MaxValue - 1, 2)]
        [TestCase(2147483647L, 1L, int.MaxValue)]
        public void SegmentCountRoundsUpWithoutOverflow(long bytes, long limit, int expected)
        {
            Assert.That(UPilotDownloadHelper.CalculateSegmentCount(bytes, limit), Is.EqualTo(expected));
        }

        [TestCase(0L, 1L)]
        [TestCase(-1L, 1L)]
        [TestCase(1L, 0L)]
        [TestCase(1L, -1L)]
        public void SegmentCountRejectsNonpositiveSizes(long bytes, long limit)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => UPilotDownloadHelper.CalculateSegmentCount(bytes, limit));
        }

        [Test]
        public void UnrepresentableSegmentCountFailsBeforeAnyNetworkRequest()
        {
            var issued = 0;
            using var handler = new Handler((request, token) =>
            {
                Interlocked.Increment(ref issued);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            });
            using var http = new HttpClient(handler);
            Assert.Throws<OverflowException>(() => UPilotDownloadHelper.DownloadAsync(http, Url, Target,
                2147483648L, new string('0', 64), null, CancellationToken.None,
                new UPilotDownloadOptions { MaxSegmentSizeBytes = 1 }));
            Assert.That(issued, Is.Zero);
            Assert.That(File.Exists(Target), Is.False);
        }

        [Test, Timeout(15000)]
        public async Task SixteenSegmentsUseAtMostEightRequestsOnOneThread()
        {
            var bytes = Enumerable.Range(0, 64).Select(index => (byte)index).ToArray();
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var reachedEight = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var issued = 0;
            var active = 0;
            using var handler = new Handler(async (request, token) =>
            {
                var range = request.Headers.Range?.Ranges.Single();
                if (range?.From == 0 && range.To == 0) return Segment(bytes, 0, 0);
                Interlocked.Increment(ref issued);
                if (Interlocked.Increment(ref active) == 8) reachedEight.TrySetResult(true);
                try
                {
                    await release.Task;
                    return Segment(bytes, range.From.Value, range.To.Value);
                }
                finally { Interlocked.Decrement(ref active); }
            });
            using var http = new HttpClient(handler);
            var options = new UPilotDownloadOptions { MaxSegmentSizeBytes = 4, BufferSize = 3 };
            DownloadProgress last = null;
            var reportedEight = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var maxReportedActive = 0;
            var download = UPilotDownloadHelper.DownloadAsync(http, Url, Target, bytes.Length, Hash(bytes),
                p =>
                {
                    last = p;
                    maxReportedActive = Math.Max(maxReportedActive, p.ActiveSegmentRequests);
                    if (p.ActiveSegmentRequests == 8)
                        reportedEight.TrySetResult(true);
                }, CancellationToken.None, options);
            await Bounded(reachedEight.Task);
            await Bounded(reportedEight.Task);
            Assert.That(issued, Is.EqualTo(8), "Eight queued slices must not start a network request yet.");
            Assert.That(download.IsCompleted, Is.False);
            release.TrySetResult(true);
            var result = await Bounded(download);
            await AssertExited(result.DedicatedThread);
            Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(mainThread));
            Assert.That(issued, Is.EqualTo(16));
            Assert.That(last.RangeProbeOutcome, Is.EqualTo("supported"));
            Assert.That(last.RangeProbeDetail, Does.Contain("HTTP 206"));
            LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex("Range 探测成功.*分片下载"));
            UPilotServerRuntimeService.LogRangeProbeOutcome(last);
            Assert.That(result.MaxSegmentSizeBytes, Is.EqualTo(4));
            Assert.That(last.MaxSegmentSizeBytes, Is.EqualTo(4));
            Assert.That(result.ConfiguredSegmentCount, Is.EqualTo(16));
            Assert.That(result.MaxConcurrentSegmentRequests, Is.EqualTo(8));
            Assert.That(result.SegmentCount, Is.EqualTo(16));
            Assert.That(result.CompletedSegments, Is.EqualTo(16));
            Assert.That(maxReportedActive, Is.EqualTo(8));
            Assert.That(last.ActiveSegmentRequests, Is.Zero);
            Assert.That(result.ActiveSegmentRequests, Is.Zero);
            Assert.That(result.PeakConcurrentSegmentRequests, Is.EqualTo(8));
            Assert.That(result.ObservedOwnedThreadCount, Is.EqualTo(1));
            Assert.That(result.ThreadConstraintViolationCount, Is.Zero);
            Assert.That(File.ReadAllBytes(Target), Is.EqualTo(bytes));
            AssertNoParts();
        }

        [Test, Timeout(15000)]
        public async Task CancelWhileEightSlicesWaitForSlotsSettlesAndCleansUp()
        {
            var bytes = Enumerable.Range(0, 64).Select(index => (byte)index).ToArray();
            var reachedEight = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var issued = 0;
            DownloadProgress last = null;
            using var cts = new CancellationTokenSource();
            using var handler = new Handler(async (request, token) =>
            {
                var range = request.Headers.Range?.Ranges.Single();
                if (range?.From == 0 && range.To == 0) return Segment(bytes, 0, 0);
                if (Interlocked.Increment(ref issued) == 8) reachedEight.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                return Segment(bytes, range.From.Value, range.To.Value);
            });
            using var http = new HttpClient(handler);
            var download = UPilotDownloadHelper.DownloadAsync(http, Url, Target, bytes.Length, Hash(bytes),
                p => last = p, cts.Token, new UPilotDownloadOptions { MaxSegmentSizeBytes = 4 });
            await Bounded(reachedEight.Task);
            Assert.That(issued, Is.EqualTo(8));
            cts.Cancel();
            try { await Bounded(download); Assert.Fail("Cancellation should propagate."); }
            catch (OperationCanceledException) { }
            Assert.That(issued, Is.EqualTo(8));
            Assert.That(last.Outcome, Is.EqualTo("cancelled"));
            Assert.That(last.ActiveSegmentRequests, Is.Zero);
            Assert.That(last.PeakConcurrentSegmentRequests, Is.EqualTo(8));
            Assert.That(last.ThreadConstraintViolationCount, Is.Zero);
            await AssertExited(last.DedicatedThread);
            AssertNoParts();
        }

        [TestCase(12, 3)]
        [TestCase(11, 3)]
        [TestCase(10, 4)]
        [Timeout(15000)]
        public async Task AutomaticRangesCoverFileExactlyWithinConfiguredLimit(int length, int limit)
        {
            var bytes = Enumerable.Range(0, length).Select(index => (byte)index).ToArray();
            var ranges = new List<(long Start, long End)>();
            using var handler = new Handler((request, token) =>
            {
                var range = request.Headers.Range.Ranges.Single();
                if (range.From != 0 || range.To != 0)
                    ranges.Add((range.From.Value, range.To.Value));
                return Task.FromResult(Segment(bytes, range.From.Value, range.To.Value));
            });
            using var http = new HttpClient(handler);
            var result = await Bounded(UPilotDownloadHelper.DownloadAsync(http, Url, Target, bytes.Length,
                Hash(bytes), null, CancellationToken.None, new UPilotDownloadOptions { MaxSegmentSizeBytes = limit }));
            await AssertExited(result.DedicatedThread);
            var count = UPilotDownloadHelper.CalculateSegmentCount(bytes.Length, limit);
            Assert.That(result.ConfiguredSegmentCount, Is.EqualTo(count));
            Assert.That(result.SegmentCount, Is.EqualTo(count));
            Assert.That(result.CompletedSegments, Is.EqualTo(count));
            Assert.That(ranges.Count, Is.EqualTo(count));
            long nextStart = 0;
            foreach (var range in ranges.OrderBy(range => range.Start))
            {
                Assert.That(range.Start, Is.EqualTo(nextStart));
                Assert.That(range.End - range.Start + 1, Is.InRange(1L, (long)limit));
                nextStart = range.End + 1;
            }
            Assert.That(nextStart, Is.EqualTo(bytes.Length));
            Assert.That(File.ReadAllBytes(Target), Is.EqualTo(bytes));
            AssertNoParts();
        }

        [TestCase(7)]
        [TestCase(8)]
        [Timeout(15000)]
        public async Task FileWithinSegmentLimitUsesSingleStreamWithoutRangeProbe(int length)
        {
            var bytes = Enumerable.Range(0, length).Select(index => (byte)index).ToArray();
            var issued = 0;
            var rangeRequests = 0;
            DownloadProgress last = null;
            using var handler = new Handler((request, token) =>
            {
                Interlocked.Increment(ref issued);
                if (request.Headers.Range != null) Interlocked.Increment(ref rangeRequests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            });
            using var http = new HttpClient(handler);
            var result = await Bounded(UPilotDownloadHelper.DownloadAsync(http, Url, Target, bytes.Length,
                Hash(bytes), progress => last = progress, CancellationToken.None,
                new UPilotDownloadOptions { MaxSegmentSizeBytes = 8 }));
            await AssertExited(result.DedicatedThread);
            Assert.That(issued, Is.EqualTo(1));
            Assert.That(rangeRequests, Is.Zero);
            Assert.That(last.RangeProbeOutcome, Is.Empty);
            Assert.That(result.ConfiguredSegmentCount, Is.EqualTo(1));
            Assert.That(result.SegmentCount, Is.EqualTo(1));
            Assert.That(result.CompletedSegments, Is.EqualTo(1));
            Assert.That(File.ReadAllBytes(Target), Is.EqualTo(bytes));
        }

        [Test, Timeout(15000)]
        public async Task OptionsAreFrozenForCurrentDownloadAndReReadForNextDownload()
        {
            var bytes = Enumerable.Range(0, 16).Select(index => (byte)index).ToArray();
            var probeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new Handler(async (request, token) =>
            {
                var range = request.Headers.Range.Ranges.Single();
                if (range.From == 0 && range.To == 0)
                {
                    probeStarted.TrySetResult(true);
                    await releaseProbe.Task;
                }
                return Segment(bytes, range.From.Value, range.To.Value);
            });
            using var http = new HttpClient(handler);
            var options = new UPilotDownloadOptions
            {
                MaxSegmentSizeBytes = 4,
                MaxConcurrentSegmentRequests = 2,
                BufferSize = 3,
                SegmentRetryCount = 0,
            };
            var firstDownload = UPilotDownloadHelper.DownloadAsync(http, Url, Target, bytes.Length,
                Hash(bytes), null, CancellationToken.None, options);
            await Bounded(probeStarted.Task);
            options.MaxSegmentSizeBytes = 8;
            options.MaxConcurrentSegmentRequests = 1;
            options.BufferSize = 7;
            releaseProbe.TrySetResult(true);
            var first = await Bounded(firstDownload);
            await AssertExited(first.DedicatedThread);
            Assert.That(first.MaxSegmentSizeBytes, Is.EqualTo(4));
            Assert.That(first.MaxConcurrentSegmentRequests, Is.EqualTo(2));
            Assert.That(first.ConfiguredSegmentCount, Is.EqualTo(4));
            Assert.That(first.SegmentCount, Is.EqualTo(4));
            Assert.That(File.ReadAllBytes(Target), Is.EqualTo(bytes));

            var secondPath = Path.Combine(_directory, "second.bin");
            var second = await Bounded(UPilotDownloadHelper.DownloadAsync(http, Url, secondPath, bytes.Length,
                Hash(bytes), null, CancellationToken.None, options));
            await AssertExited(second.DedicatedThread);
            Assert.That(second.MaxSegmentSizeBytes, Is.EqualTo(8));
            Assert.That(second.MaxConcurrentSegmentRequests, Is.EqualTo(1));
            Assert.That(second.ConfiguredSegmentCount, Is.EqualTo(2));
            Assert.That(second.SegmentCount, Is.EqualTo(2));
            Assert.That(File.ReadAllBytes(secondPath), Is.EqualTo(bytes));
            AssertNoParts();
        }

        [Test, Timeout(15000)]
        public async Task FourRequestsOverlapAndCompleteOnOneDedicatedThread()
        {
            var bytes = Encoding.UTF8.GetBytes("123456789AB");
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ranges = new List<string>();
            using var handler = new Handler(async (request, token) =>
            {
                var range = request.Headers.Range?.Ranges.Single();
                if (range?.From == 0 && range.To == 0)
                    return Segment(bytes, 0, 0);
                lock (ranges)
                {
                    ranges.Add(range.From + "-" + range.To);
                    if (ranges.Count == 4) reached.TrySetResult(true);
                }
                await gate.Task;
                return Segment(bytes, range.From.Value, range.To.Value);
            });
            using var http = new HttpClient(handler);
            DownloadProgress last = null;
            var download = UPilotDownloadHelper.DownloadAsync(http, Url, Target, bytes.Length, Hash(bytes),
                p => last = p, CancellationToken.None, SegmentedOptions());
            await Bounded(reached.Task);
            Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(mainThread));
            Assert.That(download.IsCompleted, Is.False, "All requests must be outstanding before releasing the barrier.");
            gate.TrySetResult(true); // Completion originates on the Unity test thread.
            var result = await Bounded(download);
            await AssertExited(result.DedicatedThread);
            Assert.That(result.Success, Is.True);
            Assert.That(File.ReadAllBytes(Target), Is.EqualTo(bytes));
            Assert.That(ranges.Distinct().Count(), Is.EqualTo(4));
            Assert.That(result.DedicatedThreadId, Is.Not.EqualTo(mainThread));
            Assert.That(result.ObservedOwnedThreadCount, Is.EqualTo(1));
            Assert.That(result.ThreadConstraintViolationCount, Is.Zero);
            Assert.That(result.ConfiguredSegmentCount, Is.EqualTo(4));
            Assert.That(result.SegmentCount, Is.EqualTo(4));
            Assert.That(result.CompletedSegments, Is.EqualTo(4));
            Assert.That(result.PeakConcurrentSegmentRequests, Is.EqualTo(4));
            Assert.That(result.ActualSha256, Is.EqualTo(Hash(bytes)));
            Assert.That(last.Outcome, Is.EqualTo("success"));
            AssertNoParts();
        }

        [Test, Timeout(15000)]
        public async Task SingleStreamFallbackAndSynchronouslyCompletedAwaitsStayOnDedicatedThread()
        {
            var bytes = Encoding.UTF8.GetBytes("single stream");
            DownloadProgress last = null;
            var maxActive = 0;
            using var handler = new Handler((request, token) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            using var http = new HttpClient(handler);
            var result = await Bounded(UPilotDownloadHelper.DownloadAsync(http, Url, Target, bytes.Length,
                Hash(bytes), progress =>
                {
                    last = progress;
                    maxActive = Math.Max(maxActive, progress.ActiveSegmentRequests);
                }, CancellationToken.None, SegmentedOptions()));
            await AssertExited(result.DedicatedThread);
            Assert.That(last.RangeProbeOutcome, Is.EqualTo("unsupported"));
            Assert.That(last.RangeProbeDetail, Does.Contain("HTTP 200"));
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                "Range 探测未通过.*已降级"));
            UPilotServerRuntimeService.LogRangeProbeOutcome(last);
            Assert.That(result.ConfiguredSegmentCount, Is.EqualTo(5));
            Assert.That(result.SegmentCount, Is.EqualTo(1));
            Assert.That(result.CompletedSegments, Is.EqualTo(1));
            Assert.That(maxActive, Is.EqualTo(1));
            Assert.That(last.ActiveSegmentRequests, Is.Zero);
            Assert.That(result.ActiveSegmentRequests, Is.Zero);
            Assert.That(result.ObservedOwnedThreadCount, Is.EqualTo(1));
            Assert.That(result.DedicatedThreadId, Is.Not.EqualTo(Thread.CurrentThread.ManagedThreadId));
            Assert.That(result.PeakConcurrentSegmentRequests, Is.EqualTo(1));
            Assert.That(File.ReadAllBytes(Target), Is.EqualTo(bytes));
        }

        [Test, Timeout(15000)]
        public async Task TransientFailureRetriesAndNonDivisibleSegmentsMergeExactly()
        {
            var bytes = Encoding.UTF8.GetBytes("123456789AB");
            var failures = 0;
            using var handler = new Handler((request, token) =>
            {
                var range = request.Headers.Range?.Ranges.Single();
                if (range == null) throw new AssertionException("Unexpected fallback to single-stream.");
                if (range.From == 0 && range.To == 2 && Interlocked.Increment(ref failures) == 1)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                return Task.FromResult(Segment(bytes, range.From.Value, range.To.Value));
            });
            using var http = new HttpClient(handler);
            var result = await Bounded(UPilotDownloadHelper.DownloadAsync(http, Url, Target, bytes.Length,
                Hash(bytes), null, CancellationToken.None, SegmentedOptions()));
            await AssertExited(result.DedicatedThread);
            Assert.That(failures, Is.EqualTo(2));
            Assert.That(result.BytesReceived, Is.EqualTo(bytes.Length));
            Assert.That(result.SegmentCount, Is.EqualTo(4));
            Assert.That(File.ReadAllBytes(Target), Is.EqualTo(bytes));
            AssertNoParts();
        }

        [TestCase("wrong-range")]
        [TestCase("truncated")]
        [TestCase("wrong-sha")]
        [Timeout(15000)]
        public async Task InvalidResponsesFailAndReleaseAllSegments(string failure)
        {
            var bytes = Encoding.UTF8.GetBytes("bad response");
            DownloadProgress last = null;
            using var handler = new Handler((request, token) =>
            {
                var range = request.Headers.Range?.Ranges.Single();
                if (range?.From == 0 && range.To == 0) return Task.FromResult(Segment(bytes, 0, 0));
                var response = Segment(bytes, range.From.Value, range.To.Value);
                if (range.From == 0 && failure == "wrong-range")
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(1, range.To.Value, bytes.Length);
                if (range.From == 0 && failure == "truncated")
                    response.Content = new ByteArrayContent(Array.Empty<byte>());
                return Task.FromResult(response);
            });
            using var http = new HttpClient(handler);
            var hash = failure == "wrong-sha" ? new string('0', 64) : Hash(bytes);
            try
            {
                await Bounded(UPilotDownloadHelper.DownloadAsync(http, Url, Target, bytes.Length, hash,
                    p => last = p, CancellationToken.None, SegmentedOptions()));
                Assert.Fail("Corrupt download must fail.");
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is InvalidOperationException)
            {
                Assert.That(last, Is.Not.Null);
                Assert.That(last.Outcome, Is.EqualTo("failed"));
                Assert.That(last.IsComplete, Is.False);
                Assert.That(last.ActiveSegmentRequests, Is.Zero);
                Assert.That(last.ThreadConstraintViolationCount, Is.Zero);
                await AssertExited(last.DedicatedThread);
                AssertNoParts();
                if (File.Exists(Target))
                    using (new FileStream(Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            }
        }

        [Test, Timeout(15000)]
        public async Task CancellationWaitsForChildrenCleansFilesAndExitsThread()
        {
            var bytes = Encoding.UTF8.GetBytes("cancel range");
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            DownloadProgress last = null;
            using var cts = new CancellationTokenSource();
            using var handler = new Handler(async (request, token) =>
            {
                var range = request.Headers.Range?.Ranges.Single();
                if (range?.From == 0 && range.To == 0) return Segment(bytes, 0, 0);
                if (Interlocked.Increment(ref requests) == 4) reached.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                return Segment(bytes, range.From.Value, range.To.Value);
            });
            using var http = new HttpClient(handler);
            var download = UPilotDownloadHelper.DownloadAsync(http, Url, Target, bytes.Length, Hash(bytes),
                p => last = p, cts.Token, SegmentedOptions());
            await Bounded(reached.Task);
            cts.Cancel();
            try { await Bounded(download); Assert.Fail("Cancellation should propagate."); }
            catch (OperationCanceledException) { }
            Assert.That(last, Is.Not.Null);
            Assert.That(last.Outcome, Is.EqualTo("cancelled"));
            Assert.That(last.IsCancelled, Is.True);
            Assert.That(last.ActiveSegmentRequests, Is.Zero);
            Assert.That(last.ThreadConstraintViolationCount, Is.Zero);
            await AssertExited(last.DedicatedThread);
            AssertNoParts();
            if (File.Exists(Target))
                using (new FileStream(Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }

        [Test, Timeout(15000)]
        public async Task CancellationDuringAsyncBodyReadDisposesStreamAndExitsThread()
        {
            var bytes = Encoding.UTF8.GetBytes("body read is pending");
            var body = new BlockingReadStream();
            DownloadProgress last = null;
            using var cts = new CancellationTokenSource();
            using var handler = new Handler((request, token) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(body),
                };
                response.Content.Headers.ContentLength = bytes.Length;
                return Task.FromResult(response);
            });
            using var http = new HttpClient(handler);
            var download = UPilotDownloadHelper.DownloadAsync(http, Url, Target, bytes.Length, Hash(bytes),
                progress => last = progress, cts.Token);
            await Bounded(body.ReadStarted);
            cts.Cancel();
            try { await Bounded(download); Assert.Fail("Cancelled body read must propagate."); }
            catch (OperationCanceledException) { }
            Assert.That(last, Is.Not.Null);
            Assert.That(last.Outcome, Is.EqualTo("cancelled"));
            Assert.That(last.ActiveSegmentRequests, Is.Zero);
            Assert.That(last.ObservedOwnedThreadCount, Is.EqualTo(1));
            Assert.That(last.ThreadConstraintViolationCount, Is.Zero);
            await AssertExited(last.DedicatedThread);
            Assert.That(body.Disposed, Is.True, "The response and its stream must be released.");
            AssertNoParts();
            if (File.Exists(Target))
                using (new FileStream(Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }

        [Test, Timeout(15000)]
        public async Task ConsecutiveSessionsCreateDistinctThreadsThatBothExit()
        {
            var bytes = Encoding.ASCII.GetBytes("two sessions");
            using var handler = new Handler((request, token) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            using var http = new HttpClient(handler);
            var first = await Bounded(UPilotDownloadHelper.DownloadAsync(http, Url, Target, bytes.Length,
                Hash(bytes), null, CancellationToken.None));
            await AssertExited(first.DedicatedThread);
            var second = await Bounded(UPilotDownloadHelper.DownloadAsync(http, Url, Target, bytes.Length,
                Hash(bytes), null, CancellationToken.None));
            await AssertExited(second.DedicatedThread);
            Assert.That(second.DedicatedThread, Is.Not.SameAs(first.DedicatedThread));
            Assert.That(second.Success, Is.True);
        }

        [Test]
        public void CleanupRemovesAllNumericPartsAndPreservesNeighborFiles()
        {
            var parts = new[] { ".part0", ".part12", ".part999", ".part0007", ".part999999999999999999999" };
            var neighbors = new[] { "", ".part", ".part1.backup", ".part-1", ".part\u0661", ".part\uff11", ".part1a", ".other.part1" };
            foreach (var suffix in parts) File.WriteAllText(Target + suffix, "old segment");
            foreach (var suffix in neighbors) File.WriteAllText(Target + suffix, "preserve");
            var otherFile = Path.Combine(_directory, "other.bin.part1");
            File.WriteAllText(otherFile, "preserve");
            var segmentDirectory = Target + ".part77";
            Directory.CreateDirectory(segmentDirectory);
            var nestedPart = Path.Combine(segmentDirectory, Path.GetFileName(Target) + ".part1");
            File.WriteAllText(nestedPart, "preserve");

            UPilotDownloadHelper.CleanupSegmentFiles(Target);

            foreach (var suffix in parts) Assert.That(File.Exists(Target + suffix), Is.False, suffix);
            foreach (var suffix in neighbors) Assert.That(File.ReadAllText(Target + suffix), Is.EqualTo("preserve"), suffix);
            Assert.That(File.ReadAllText(otherFile), Is.EqualTo("preserve"));
            Assert.That(File.ReadAllText(nestedPart), Is.EqualTo("preserve"));
        }

        private void AssertNoParts()
        {
            Assert.That(Directory.GetFiles(_directory, "*.part*"), Is.Empty);
        }

        private static UPilotDownloadOptions SegmentedOptions() => new UPilotDownloadOptions
        {
            MaxSegmentSizeBytes = 3,
            MaxConcurrentSegmentRequests = 4,
            SegmentRetryCount = 2,
            RetryDelayMilliseconds = 1,
            BufferSize = 3,
        };

        private static HttpResponseMessage Segment(byte[] bytes, long start, long end)
        {
            var fragment = bytes.Skip((int)start).Take((int)(end - start + 1)).ToArray();
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(fragment),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, bytes.Length);
            return response;
        }

        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static async Task<T> Bounded<T>(Task<T> task)
        {
            var completed = await Task.WhenAny(task, Task.Delay(10000));
            if (completed != task) throw new TimeoutException("Download did not finish in ten seconds.");
            return await task;
        }

        private static async Task Bounded(Task task)
        {
            var completed = await Task.WhenAny(task, Task.Delay(10000));
            if (completed != task) throw new TimeoutException("Download did not reach barrier in ten seconds.");
            await task;
        }

        private static async Task AssertExited(Thread thread)
        {
            Assert.That(thread, Is.Not.Null);
            var limit = DateTime.UtcNow.AddSeconds(2);
            while (thread.IsAlive && DateTime.UtcNow < limit)
                await Task.Delay(10);
            Assert.That(thread.IsAlive, Is.False, "Completion precedes the final return from the thread entry.");
        }

        private sealed class BlockingReadStream : Stream
        {
            private readonly TaskCompletionSource<bool> _readStarted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task ReadStarted => _readStarted.Task;
            public bool Disposed { get; private set; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                _readStarted.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
            public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
            {
                _send = send;
            }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return _send(request, cancellationToken);
            }
        }
    }
}
