// Explicit, network-dependent comparison of the UPilot production transfer paths.
// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotDownloadBenchmarkTests
    {
        private const string AssetName = "upilot-mcp-server-0.3.37-win-x64.exe";
        private const string AssetUrl = "https://github.com/codingriver/upilot/releases/download/v0.3.37/" + AssetName;
        private const string ExpectedSha256 = "17b59e654286af39a7b026693aa8b4c52df328630abeada94830bb3e27da8ba0";
        private const long ExpectedBytes = 26942250;

        [Serializable]
        private sealed class BenchmarkReport
        {
            public string runId;
            public string startedUtc;
            public string finishedUtc;
            public string downloader = "UPilot";
            public string assetUrl = AssetUrl;
            public string assetRelativePath;
            public string outcome = "not_started";
            public bool success;
            public string error = "";
            public long elapsedMilliseconds;
            public long expectedBytes = ExpectedBytes;
            public long receivedBytes;
            public long fileBytes;
            public double verifiedMiBPerSecond;
            public string expectedSha256 = ExpectedSha256;
            public string actualSha256 = "";
            public int configuredSegmentCount = UPilotServerRuntimeService.ParallelDownloadSegments;
            public int maxConcurrentSegmentRequests = UPilotDownloadHelper.DefaultMaxConcurrentSegmentRequests;
            public int segmentCount;
            public int completedSegments;
            public int dedicatedThreadId;
            public int observedOwnedThreadCount;
            public int threadConstraintViolationCount;
            public int peakConcurrentSegmentRequests;
            public long sessionDurationMilliseconds;
            public long transferDurationMilliseconds;
            public long verificationDurationMilliseconds;
            public double transferBytesPerSecond;
            public string phase = "";
            public bool cacheHit; // Per-run target only; never consult or mutate the managed cache.
        }

        private static string ReportsRoot => Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "Logs", "UPilotDownloadBenchmark"));

        [Test]
        public void BenchmarkRejectsTargetsOutsideProjectLogsWithoutDownloading()
        {
            var download = CreateDownload();
            var outside = Path.Combine(Application.dataPath, Guid.NewGuid().ToString("N") + ".exe");
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await UPilotServerRuntimeService.Instance.BenchmarkDownloadAsync(download, outside, CancellationToken.None));
            Assert.That(File.Exists(outside), Is.False);
        }

        [Test]
        [Explicit("Transfers the pinned 26.9 MB GitHub release asset; select this single test manually.")]
        [Category("NetworkBenchmark")]
        public async Task DownloadPinnedReleaseThroughUPilotAndWriteReport()
        {
            var report = new BenchmarkReport
            {
                runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"),
                startedUtc = DateTime.UtcNow.ToString("o"),
            };
            var runDirectory = Path.Combine(ReportsRoot, report.runId);
            var target = Path.Combine(runDirectory, AssetName);
            report.assetRelativePath = "Logs/UPilotDownloadBenchmark/" + report.runId + "/" + AssetName;
            var stopwatch = new Stopwatch();
            Exception failure = null;
            try
            {
                if (Directory.Exists(runDirectory))
                    throw new IOException("A benchmark run directory with the same ID already exists.");
                Directory.CreateDirectory(runDirectory);
                if (Application.platform != RuntimePlatform.WindowsEditor ||
                    RuntimeInformation.ProcessArchitecture != Architecture.X64)
                {
                    report.outcome = "not_run_unsupported_platform";
                }
                else if (UPilotServerRuntimeService.Instance.DownloadState.IsRunning)
                {
                    report.outcome = "not_run_download_busy";
                }
                else
                {
                    using (var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8)))
                    {
                        stopwatch.Start();
                        // Exactly one invocation; no retry after failure, cancellation, or observation timeout.
                        await UPilotServerRuntimeService.Instance.BenchmarkDownloadAsync(
                            CreateDownload(), target, deadline.Token);
                    }
                    report.outcome = "success";
                    report.success = true;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                report.outcome = ex is OperationCanceledException ? "cancelled" : "failed";
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                report.error = (ex.GetType().Name + ": " + ex.Message)
                    .Replace(projectRoot, "[project]");
            }
            finally
            {
                stopwatch.Stop();
                report.elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                report.finishedUtc = DateTime.UtcNow.ToString("o");
                var state = UPilotServerRuntimeService.Instance.DownloadState;
                // A busy preflight belongs to another operation; do not attribute its progress here.
                if (report.outcome != "not_run_download_busy" && report.outcome != "not_run_unsupported_platform")
                {
                    report.receivedBytes = state.BytesReceived;
                    report.segmentCount = state.SegmentCount;
                    report.completedSegments = state.CompletedSegments;
                    report.phase = state.Phase;
                    report.configuredSegmentCount = state.ConfiguredSegmentCount;
                    report.maxConcurrentSegmentRequests = state.MaxConcurrentSegmentRequests;
                    report.peakConcurrentSegmentRequests = state.PeakConcurrentSegmentRequests;
                    report.dedicatedThreadId = state.DedicatedThreadId;
                    report.observedOwnedThreadCount = state.ObservedOwnedThreadCount;
                    report.threadConstraintViolationCount = state.ThreadConstraintViolationCount;
                    report.sessionDurationMilliseconds = state.SessionDurationMilliseconds;
                    report.transferDurationMilliseconds = state.TransferDurationMilliseconds;
                    report.verificationDurationMilliseconds = state.VerificationDurationMilliseconds;
                    report.transferBytesPerSecond = state.ThroughputBytesPerSecond;
                    report.actualSha256 = state.ActualSha256;
                    if (!string.IsNullOrEmpty(state.Outcome)) report.outcome = state.Outcome;
                    if (string.IsNullOrEmpty(report.error) && !string.IsNullOrEmpty(state.ErrorMessage))
                        report.error = state.ErrorMessage;
                }
                if (File.Exists(target))
                    report.fileBytes = new FileInfo(target).Length;
                if (report.success && report.elapsedMilliseconds > 0)
                    report.verifiedMiBPerSecond = report.fileBytes / 1048576.0 / (report.elapsedMilliseconds / 1000.0);
                // Reports and downloaded files remain under the unique project Logs directory.
                Directory.CreateDirectory(runDirectory);
                var reportPath = Path.Combine(runDirectory, "report.json");
                using (var file = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
                    writer.Write(JsonUtility.ToJson(report, true));
                Debug.Log("[UPilotDownloadBenchmark] " + report.outcome + " — " +
                          "Logs/UPilotDownloadBenchmark/" + report.runId + "/report.json");
            }

            if (report.outcome.StartsWith("not_run_", StringComparison.Ordinal))
                Assert.Ignore("Benchmark not run: " + report.outcome);
            if (failure != null)
                Assert.Fail("UPilot benchmark failed; inspect Logs/UPilotDownloadBenchmark/" + report.runId + "/report.json: " + report.error);
            Assert.That(report.actualSha256, Is.EqualTo(ExpectedSha256).IgnoreCase);
            Assert.That(report.fileBytes, Is.EqualTo(ExpectedBytes));
            Assert.That(report.observedOwnedThreadCount, Is.EqualTo(1));
            Assert.That(report.threadConstraintViolationCount, Is.Zero);
            Assert.That(report.configuredSegmentCount, Is.EqualTo(12));
            Assert.That(report.maxConcurrentSegmentRequests, Is.EqualTo(5));
            Assert.That(report.segmentCount, Is.EqualTo(12));
            Assert.That(report.completedSegments, Is.EqualTo(12));
            Assert.That(report.peakConcurrentSegmentRequests, Is.EqualTo(5));
        }

        private static UPilotServerDownloadInfo CreateDownload() => new UPilotServerDownloadInfo
        {
            Url = AssetUrl,
            FileName = AssetName,
            SizeBytes = ExpectedBytes,
            Sha256 = ExpectedSha256,
            Platform = "windows",
            Architecture = "x64",
        };
    }
}