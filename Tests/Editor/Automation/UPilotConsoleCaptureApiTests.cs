using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests.Automation
{
    public class UPilotConsoleCaptureApiTests
    {
        private string _ownerToken;
        private string _relativeDirectory;
        private string _sessionId;

        [SetUp]
        public void SetUp()
        {
            _sessionId = null;
            _ownerToken = null;
            _relativeDirectory = null;
            Assert.That(UPilotConsoleCaptureApi.Capabilities().available, Is.True);
            if (UPilotConsoleCaptureApi.GetBoundary().ok)
                Assert.Ignore("Console Capture API tests require no unrelated active capture.");
            _ownerToken = Guid.NewGuid().ToString("N");
            _relativeDirectory = NewTestDirectory();
        }

        [TearDown]
        public void TearDown() => CleanupOwnCapture();

        private static string NewTestDirectory() =>
            Path.Combine("Log", "UPilotConsole", "AutomationCaptureApiTests", Guid.NewGuid().ToString("N"));

        private static string GetOwnedDirectory(string relativeDirectory)
        {
            if (string.IsNullOrEmpty(relativeDirectory)) return null;
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string root = Path.Combine(project, "Log", "UPilotConsole", "AutomationCaptureApiTests");
            string full = Path.GetFullPath(Path.Combine(project, relativeDirectory));
            if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(full), "N", out _))
                throw new InvalidOperationException("Capture test cleanup requires its exact GUID child directory.");
            for (var current = new DirectoryInfo(full); current != null; current = current.Parent)
            {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Capture test cleanup refuses reparse points.");
                if (string.Equals(current.FullName, project, StringComparison.OrdinalIgnoreCase)) break;
            }
            return full;
        }

        private void CleanupOwnCapture()
        {
            string full = GetOwnedDirectory(_relativeDirectory);
            if (!string.IsNullOrEmpty(_sessionId))
            {
                var stopped = UPilotConsoleCaptureApi.Stop(_sessionId, _ownerToken);
                if (!stopped.ok || stopped.session == null || stopped.session.active
                    || stopped.session.sessionId != _sessionId)
                    throw new InvalidOperationException("Capture test cleanup could not confirm its own stopped session: " + stopped.error);
            }
            if (full != null && Directory.Exists(full)) Directory.Delete(full, true);
            _sessionId = null;
            _relativeDirectory = null;
        }

        [Test]
        public void UninitializedCleanupNeverResolvesToProjectRoot()
        {
            _relativeDirectory = null;
            Assert.That(GetOwnedDirectory(null), Is.Null);
            Assert.That(GetOwnedDirectory(string.Empty), Is.Null);
            Assert.DoesNotThrow(CleanupOwnCapture);
            Assert.That(Directory.Exists(Application.dataPath), Is.True);
        }

        [TestCase(".")]
        [TestCase("..")]
        [TestCase("Log/UPilotConsole/AutomationCaptureApiTests")]
        [TestCase("Log/UPilotConsole/AutomationCaptureApiTests/not-owned")]
        public void UnsafeCleanupDirectoryIsRejected(string path)
        {
            Assert.Throws<InvalidOperationException>(() => GetOwnedDirectory(path));
        }

        [Test]
        public void RepeatedCleanupPreservesCustomSessionIndex()
        {
            string index = Path.GetFullPath(Path.Combine(Application.dataPath, "../Log/UPilotConsole/session-index.json"));
            byte[] before = File.Exists(index) ? File.ReadAllBytes(index) : null;
            for (int iteration = 0; iteration < 2; iteration++)
            {
                _relativeDirectory = NewTestDirectory();
                var started = Start();
                Assert.That(started.ok, Is.True, started.error);
                string directory = GetOwnedDirectory(_relativeDirectory);
                Assert.That(_sessionId, Is.EqualTo(started.session.sessionId));
                CleanupOwnCapture();
                Assert.That(Directory.Exists(directory), Is.False);
                Assert.DoesNotThrow(CleanupOwnCapture);
            }
            Assert.That(File.Exists(index) ? File.ReadAllBytes(index) : null, Is.EqualTo(before));
        }

        [Test]
        public void FailedStopPreservesOwnDirectoryForVerifiedCleanup()
        {
            var started = Start();
            Assert.That(started.ok, Is.True, started.error);
            string owner = _ownerToken;
            try
            {
                _ownerToken = "wrong-token";
                Assert.Throws<InvalidOperationException>(CleanupOwnCapture);
                Assert.That(Directory.Exists(GetOwnedDirectory(_relativeDirectory)), Is.True);
                Assert.That(UPilotConsoleCaptureApi.Status(_sessionId).session.active, Is.True);
            }
            finally { _ownerToken = owner; }
        }

        [Test]
        public void SuccessfulStartIsOwnedBeforeCallerAssertions()
        {
            var started = Start();
            Assert.That(started.ok, Is.True, started.error);
            string directory = GetOwnedDirectory(_relativeDirectory);
            Assert.Throws<AssertionException>(() => Assert.Fail("Simulated failure after Start."));
            Assert.That(_sessionId, Is.EqualTo(started.session.sessionId));
            CleanupOwnCapture();
            Assert.That(Directory.Exists(directory), Is.False);
            Assert.That(UPilotConsoleCaptureApi.GetBoundary().ok, Is.False);
        }

        [Test]
        public void CapabilitiesAndMainThreadGateDoNotExposeForceStop()
        {
            ConsoleCaptureApiCapabilities capabilities = UPilotConsoleCaptureApi.Capabilities();
            Assert.That(capabilities.forceStopExposed, Is.False);
            Assert.That(capabilities.operations, Does.Not.Contain("ForceStop"));

            ConsoleCaptureResult result = Task.Run(() => UPilotConsoleCaptureApi.Status("missing")).GetAwaiter().GetResult();
            Assert.That(result.ok, Is.False);
            Assert.That(result.error, Does.Contain("CONSOLE_CAPTURE_MAIN_THREAD_REQUIRED"));
        }

        [Test]
        public void StartRequiresAnOwnerToken()
        {
            ConsoleCaptureResult result = UPilotConsoleCaptureApi.Start(new ConsoleCaptureStartPayload
            {
                title = "UnownedCapture",
                path = _relativeDirectory,
            });
            Assert.That(result.ok, Is.False);
            Assert.That(result.error, Does.Contain("CONSOLE_CAPTURE_OWNER_TOKEN_REQUIRED"));
            Assert.That(UPilotConsoleCaptureApi.GetBoundary().ok, Is.False);
        }

        [UnityTest]
        public IEnumerator VerifyStoppedRequiresRealEmptyArtifactAndDetectsTampering()
        {
            ConsoleCaptureResult started = Start();
            Assert.That(started.ok, Is.True, started.error);
            _sessionId = started.session.sessionId;
            var stopped = UPilotConsoleCaptureApi.Stop(_sessionId, _ownerToken);
            Assert.That(stopped.ok, Is.True, stopped.error);
            var verify = UPilotConsoleCaptureApi.VerifyStoppedAsync(_sessionId);
            while (!verify.IsCompleted) yield return null;
            var result = verify.GetAwaiter().GetResult();
            Assert.That(result.ok && result.stopped && result.artifactsVerified, Is.True, result.errorMessage);
            Assert.That(result.fileBytes, Is.GreaterThanOrEqualTo(0));
            Assert.That(File.Exists(stopped.session.jsonlPath), Is.True);

            File.AppendAllText(stopped.session.jsonlPath, "tampered");
            verify = UPilotConsoleCaptureApi.VerifyStoppedAsync(_sessionId);
            while (!verify.IsCompleted) yield return null;
            result = verify.GetAwaiter().GetResult();
            Assert.That(result.ok && result.stopped && !result.artifactsVerified, Is.True);
        }

        [Test]
        public void VerifyActiveSessionNeverStopsItOrClaimsArtifacts()
        {
            var started = Start();
            Assert.That(started.ok, Is.True, started.error);
            var task = UPilotConsoleCaptureApi.VerifyStoppedAsync(_sessionId);
            Assert.That(task.IsCompleted, Is.True);
            var result = task.GetAwaiter().GetResult();
            Assert.That(result.ok, Is.True);
            Assert.That(result.stopState, Is.EqualTo("active"));
            Assert.That(result.stopped || result.artifactsVerified, Is.False);
            Assert.That(UPilotConsoleCaptureApi.GetBoundary(_sessionId).ok, Is.True);
        }

        [UnityTest]
        public IEnumerator VerifyStoppedRejectsEachMissingArtifactWithoutRepairingIt()
        {
            Assert.That(Start().ok, Is.True);
            var stopped = UPilotConsoleCaptureApi.Stop(_sessionId, _ownerToken);
            Assert.That(stopped.ok, Is.True, stopped.error);
            foreach (string path in new[] { stopped.session.summaryPath, stopped.session.jsonlPath, stopped.session.manifestPath })
            {
                byte[] bytes = File.ReadAllBytes(path);
                try
                {
                    File.Delete(path);
                    var task = UPilotConsoleCaptureApi.VerifyStoppedAsync(_sessionId);
                    while (!task.IsCompleted) yield return null;
                    var result = task.GetAwaiter().GetResult();
                    Assert.That(result.artifactsVerified, Is.False, path);
                    Assert.That(result.errorCode, Is.Not.Null.And.Not.Empty, path);
                    Assert.That(File.Exists(path), Is.False, "Observation must not repair missing evidence.");
                }
                finally { File.WriteAllBytes(path, bytes); }
            }
            var restored = UPilotConsoleCaptureApi.VerifyStoppedAsync(_sessionId);
            while (!restored.IsCompleted) yield return null;
            Assert.That(restored.GetAwaiter().GetResult().artifactsVerified, Is.True);
        }

        [UnityTest]
        public IEnumerator VerifyStoppedAcceptsRealRotatedSegmentsAndRejectsMissingOrExtraSegments()
        {
            Assert.That(Start(maxFileBytes: 1024 * 1024).ok, Is.True);
            // Drive the real writer directly, without adding megabytes to the Editor Console.
            var receive = typeof(UPilotConsoleCaptureService).GetMethod("OnLogMessageReceived", BindingFlags.Static | BindingFlags.NonPublic);
            var flush = typeof(UPilotConsoleCaptureService).GetMethod("FlushActiveCapture", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(receive, Is.Not.Null);
            Assert.That(flush, Is.Not.Null);
            for (int i = 0; i < 2; i++)
            {
                receive.Invoke(null, new object[] { new string((char)('a' + i), 600 * 1024), string.Empty, LogType.Log });
                flush.Invoke(null, new object[] { true });
            }
            var stopped = UPilotConsoleCaptureApi.Stop(_sessionId, _ownerToken);
            Assert.That(stopped.ok, Is.True, stopped.error);
            Assert.That(stopped.session.segmentCount, Is.EqualTo(2));
            Assert.That(Path.GetFileName(stopped.session.jsonlPath), Is.EqualTo("console.001.jsonl"));
            var task = UPilotConsoleCaptureApi.VerifyStoppedAsync(_sessionId);
            while (!task.IsCompleted) yield return null;
            var result = task.GetAwaiter().GetResult();
            Assert.That(result.ok && result.stopped && result.artifactsVerified, Is.True, result.errorMessage);
            Assert.That(result.fileBytes, Is.EqualTo(stopped.session.fileBytes));
            Assert.That(result.sha256, Is.EqualTo(stopped.session.sha256));

            string first = Path.Combine(stopped.session.directory, "console.jsonl");
            byte[] firstBytes = File.ReadAllBytes(first);
            try
            {
                File.Delete(first);
                task = UPilotConsoleCaptureApi.VerifyStoppedAsync(_sessionId);
                while (!task.IsCompleted) yield return null;
                Assert.That(task.GetAwaiter().GetResult().artifactsVerified, Is.False);
            }
            finally { File.WriteAllBytes(first, firstBytes); }
            string extra = Path.Combine(stopped.session.directory, "console.999.jsonl");
            try
            {
                File.WriteAllText(extra, "unexpected segment");
                task = UPilotConsoleCaptureApi.VerifyStoppedAsync(_sessionId);
                while (!task.IsCompleted) yield return null;
                Assert.That(task.GetAwaiter().GetResult().artifactsVerified, Is.False);
            }
            finally { File.Delete(extra); }
        }

        [UnityTest]
        public IEnumerator VerifyStoppedRejectsJsonlPathOutsideRegisteredDirectory()
        {
            Assert.That(Start().ok, Is.True);
            var stopped = UPilotConsoleCaptureApi.Stop(_sessionId, _ownerToken);
            Assert.That(stopped.ok, Is.True, stopped.error);
            var manifest = stopped.session;
            byte[] original = File.ReadAllBytes(manifest.manifestPath);
            string other = Path.Combine(GetOwnedDirectory(_relativeDirectory), "other");
            Directory.CreateDirectory(other);
            string displaced = Path.Combine(other, "console.jsonl");
            File.Copy(manifest.jsonlPath, displaced);
            try
            {
                manifest.jsonlPath = displaced;
                File.WriteAllText(manifest.manifestPath, JsonUtility.ToJson(manifest));
                var task = UPilotConsoleCaptureApi.VerifyStoppedAsync(_sessionId);
                while (!task.IsCompleted) yield return null;
                var result = task.GetAwaiter().GetResult();
                Assert.That(result.ok && result.stopped, Is.True);
                Assert.That(result.artifactsVerified, Is.False);
                Assert.That(result.errorMessage, Does.Contain("CONSOLE_CAPTURE_PATH_MISMATCH"));
            }
            finally { File.WriteAllBytes(manifest.manifestPath, original); }
        }

        [UnityTest]
        public IEnumerator VerifyStoppedRejectsActualDirectoryJunction()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                Assert.Ignore("This regression exercises a real Windows directory junction.");
            string owned = GetOwnedDirectory(_relativeDirectory);
            string target = Path.Combine(owned, "target");
            string link = Path.Combine(owned, "junction");
            Directory.CreateDirectory(target);
            string command = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '"
                + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null";
            var info = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -EncodedCommand "
                    + Convert.ToBase64String(Encoding.Unicode.GetBytes(command)),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            try
            {
                using (var process = System.Diagnostics.Process.Start(info))
                {
                    try
                    {
                        double deadline = UnityEditor.EditorApplication.timeSinceStartup + 15d;
                        while (!process.HasExited && UnityEditor.EditorApplication.timeSinceStartup < deadline)
                            yield return null;
                        Assert.That(process.HasExited, Is.True, "Owned junction creation exceeded its fixed deadline.");
                        Assert.That(process.ExitCode, Is.Zero);
                    }
                    finally
                    {
                        if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
                    }
                }
                Assert.That((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, Is.True);
                var started = Start(Path.Combine(_relativeDirectory, "junction"));
                Assert.That(started.ok, Is.True, started.error);
                Assert.That(UPilotConsoleCaptureApi.Stop(_sessionId, _ownerToken).ok, Is.True);
                var task = UPilotConsoleCaptureApi.VerifyStoppedAsync(_sessionId);
                while (!task.IsCompleted) yield return null;
                var result = task.GetAwaiter().GetResult();
                Assert.That(result.ok && result.stopped, Is.True);
                Assert.That(result.artifactsVerified, Is.False);
                Assert.That(result.errorMessage, Does.Contain("reparse point"));
            }
            finally
            {
                // Stop only our session before unlinking; never recursively traverse a junction.
                if (!string.IsNullOrEmpty(_sessionId))
                    Assert.That(UPilotConsoleCaptureApi.Stop(_sessionId, _ownerToken).ok, Is.True);
                Assert.That(Path.GetDirectoryName(Path.GetFullPath(link)), Is.EqualTo(GetOwnedDirectory(_relativeDirectory)));
                if (Directory.Exists(link)) Directory.Delete(link, false);
            }
        }

        [UnityTest]
        public IEnumerator VerifyStoppedCancellationDoesNotChangeSessionOrPreventLaterVerification()
        {
            Assert.That(Start().ok, Is.True);
            Assert.That(UPilotConsoleCaptureApi.Stop(_sessionId, _ownerToken).ok, Is.True);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var task = UPilotConsoleCaptureApi.VerifyStoppedAsync(_sessionId, cancellation.Token);
            while (!task.IsCompleted) yield return null;
            Assert.That(task.IsCanceled, Is.True);
            Assert.That(UPilotConsoleCaptureApi.Status(_sessionId).session.active, Is.False);
            task = UPilotConsoleCaptureApi.VerifyStoppedAsync(_sessionId);
            while (!task.IsCompleted) yield return null;
            Assert.That(task.GetAwaiter().GetResult().artifactsVerified, Is.True);
        }

        [UnityTest]
        public IEnumerator VerifyStoppedAcceptsRegisteredOutsideProjectDirectory()
        {
            // Retain this stopped session's real files: deleting them would leave a dangling custom index entry.
            string external = Path.Combine(Path.GetTempPath(), "UPilotCaptureVerificationEvidence", Guid.NewGuid().ToString("N"));
            var started = Start(external, allowOutsideProject: true);
            Assert.That(started.ok, Is.True, started.error);
            Assert.That(UPilotConsoleCaptureApi.Stop(_sessionId, _ownerToken).ok, Is.True);
            var task = UPilotConsoleCaptureApi.VerifyStoppedAsync(_sessionId);
            while (!task.IsCompleted) yield return null;
            var result = task.GetAwaiter().GetResult();
            Assert.That(result.sessionId, Is.EqualTo(_sessionId));
            Assert.That(result.ok && result.stopped && result.artifactsVerified, Is.True, result.errorMessage);
            Assert.That(File.Exists(Path.Combine(external, "console.jsonl")), Is.True);
        }

        [Test]
        public void VerificationKeepsFilesLockedThroughRegistrationAndReleasesThemOnFailure()
        {
            Assert.That(Start().ok, Is.True);
            var stopped = UPilotConsoleCaptureApi.Stop(_sessionId, _ownerToken);
            Assert.That(stopped.ok, Is.True, stopped.error);
            var manifest = stopped.session;
            bool registered = false;
            var files = AutomationRunCapture.VerifyStoppedFiles(manifest, JsonUtility.ToJson(manifest),
                AutomationReportWriter.GetArtifactMetadata, artifacts =>
                {
                    registered = true;
                    foreach (var artifact in artifacts)
                        Assert.Throws<IOException>(() =>
                        {
                            using var writer = new FileStream(artifact.path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                        }, artifact.path);
                }, CancellationToken.None);
            Assert.That(registered, Is.True);
            Assert.That(files.Length, Is.EqualTo(3));
            var error = Assert.Throws<InvalidOperationException>(() => AutomationRunCapture.VerifyStoppedFiles(
                manifest, JsonUtility.ToJson(manifest), AutomationReportWriter.GetArtifactMetadata,
                _ => throw new InvalidOperationException("EXPECTED_REGISTRATION_FAILURE"), CancellationToken.None));
            Assert.That(error.Message, Is.EqualTo("EXPECTED_REGISTRATION_FAILURE"));
            foreach (var file in files)
                using (new FileStream(file.path, FileMode.Open, FileAccess.Write, FileShare.None)) { }
        }

        [Test]
        public void VerificationDetectsMutationBeforeHandleAcquisitionAndReleasesOnMidReadCancellation()
        {
            Assert.That(Start().ok, Is.True);
            var stopped = UPilotConsoleCaptureApi.Stop(_sessionId, _ownerToken);
            Assert.That(stopped.ok, Is.True, stopped.error);
            var manifest = stopped.session;
            byte[] original = File.ReadAllBytes(manifest.jsonlPath);
            bool changed = false;
            try
            {
                var error = Assert.Throws<InvalidDataException>(() => AutomationRunCapture.VerifyStoppedFiles(
                    manifest, JsonUtility.ToJson(manifest), (kind, path) =>
                    {
                        var metadata = AutomationReportWriter.GetArtifactMetadata(kind, path);
                        if (kind == "consoleCapture.segment" && !changed)
                        {
                            changed = true;
                            File.AppendAllText(path, "mutation-before-hold");
                        }
                        return metadata;
                    }, _ => Assert.Fail("Changed evidence must not be registered."), CancellationToken.None));
                Assert.That(changed, Is.True);
                Assert.That(error.Message, Does.Contain("STEP_CAPTURE_FILES_CHANGED"));
            }
            finally { File.WriteAllBytes(manifest.jsonlPath, original); }
            using var cancellation = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() => AutomationRunCapture.VerifyStoppedFiles(
                manifest, JsonUtility.ToJson(manifest), (kind, path) =>
                {
                    var metadata = AutomationReportWriter.GetArtifactMetadata(kind, path);
                    cancellation.Cancel();
                    return metadata;
                }, _ => Assert.Fail("Cancelled evidence must not be registered."), cancellation.Token));
            using (new FileStream(manifest.manifestPath, FileMode.Open, FileAccess.Write, FileShare.None)) { }
        }

        [Test]
        public void VerifyStoppedNeverAdoptsAnUnknownSession()
        {
            var result = UPilotConsoleCaptureApi.VerifyStoppedAsync("missing-session").GetAwaiter().GetResult();
            Assert.That(result.stopped || result.artifactsVerified, Is.False);
        }

        [Test]
        public void BridgeMessageLimitUsesFinalUtf8Bytes()
        {
            var method = typeof(UPilotBridge).GetMethod("ResponseBytes", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            int limit = 4 * 1024 * 1024;
            foreach (int delta in new[] { -1, 0, 1 })
            {
                string message = new string('x', limit + delta);
                Assert.That((int)method.Invoke(null, new object[] { message }), Is.EqualTo(limit + delta));
            }
            Assert.That((int)method.Invoke(null, new object[] { "中文" }),
                Is.EqualTo(Encoding.UTF8.GetByteCount("中文")));
        }

        [Test]
        public void LifecycleCancellationNeverHidesNetworkFailureOrOrdinaryInvalidation()
        {
            var method = typeof(UPilotMcpServerManager).GetMethod("IsExpectedStatusInterruption",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            bool Expected(Exception ex, CancellationToken token, string reason) =>
                (bool)method.Invoke(null, new object[] { ex, token, reason });
            Assert.That(Expected(new OperationCanceledException(), cancelled.Token, "domain_reload"), Is.True);
            Assert.That(Expected(new OperationCanceledException(), cancelled.Token, "editor_exit"), Is.True);
            Assert.That(Expected(new OperationCanceledException(), cancelled.Token, "explicit_stop"), Is.True);
            Assert.That(Expected(new OperationCanceledException(), cancelled.Token, "lifecycle_stop"), Is.True);
            Assert.That(Expected(new OperationCanceledException(), cancelled.Token, "timeout"), Is.False);
            Assert.That(Expected(new OperationCanceledException(), cancelled.Token, "unknown"), Is.False);
            Assert.That(Expected(new IOException("network"), cancelled.Token, "lifecycle_stop"), Is.False);
            Assert.That(Expected(new OperationCanceledException(), CancellationToken.None, "lifecycle_stop"), Is.False);
            Assert.That(Expected(new OperationCanceledException(), cancelled.Token, ""), Is.False);
            Assert.That(Expected(new IOException("network"), cancelled.Token, "domain_reload"), Is.False);
            Assert.That(Expected(new OperationCanceledException(), CancellationToken.None, "domain_reload"), Is.False);
        }

        [Test]
        public void WrongOwnerCannotStopOrAdoptCapture()
        {
            ConsoleCaptureResult started = Start();
            Assert.That(started.ok, Is.True, started.error);
            _sessionId = started.session.sessionId;

            ConsoleCaptureResult rejected = UPilotConsoleCaptureApi.Stop(_sessionId, "wrong-token");
            Assert.That(rejected.ok, Is.False);
            Assert.That(rejected.error, Does.Contain("所有权"));
            Assert.That(UPilotConsoleCaptureApi.Status(_sessionId).session.active, Is.True);

            ConsoleCaptureResult stopped = UPilotConsoleCaptureApi.Stop(_sessionId, _ownerToken);
            Assert.That(stopped.ok, Is.True, stopped.error);
            Assert.That(stopped.session.active, Is.False);
        }

        [UnityTest]
        public IEnumerator BoundaryAndAsyncReadUseExactSessionIdentity()
        {
            ConsoleCaptureResult started = Start();
            Assert.That(started.ok, Is.True, started.error);
            _sessionId = started.session.sessionId;
            string longPayload = new string('x', 13_000);
            Debug.Log("UPILOT_AUTOMATION_CAPTURE_API_RECORD_" + _sessionId + longPayload);

            double flushDeadline = UnityEditor.EditorApplication.timeSinceStartup + 0.5d;
            while (UnityEditor.EditorApplication.timeSinceStartup < flushDeadline)
                yield return null;

            ConsoleCaptureBoundaryResult boundary = UPilotConsoleCaptureApi.GetBoundary(_sessionId);
            Assert.That(boundary.ok, Is.True, boundary.errorMessage);
            Assert.That(boundary.nextSequence, Is.GreaterThan(0));
            ConsoleCaptureBoundaryResult mismatch = UPilotConsoleCaptureApi.GetBoundary("foreign-session");
            Assert.That(mismatch.ok, Is.False);
            Assert.That(mismatch.errorCode, Is.EqualTo("CONSOLE_CAPTURE_IDENTITY_MISMATCH"));

            Task<ConsoleCaptureReadResult> readTask = UPilotConsoleCaptureApi.ReadAsync(new ConsoleCaptureReadPayload
            {
                sessionId = _sessionId,
                fromSequence = 0,
                toSequence = boundary.nextSequence - 1,
                count = 100,
                contains = new[] { "UPILOT_AUTOMATION_CAPTURE_API_RECORD" },
            });
            while (!readTask.IsCompleted) yield return null;
            ConsoleCaptureReadResult read = readTask.GetAwaiter().GetResult();

            Assert.That(read.ok, Is.True, read.error);
            Assert.That(read.scanComplete, Is.True);
            ConsoleCaptureRecord captured = read.logs.FirstOrDefault(item => item.message.Contains("UPILOT_AUTOMATION_CAPTURE_API_RECORD"));
            Assert.That(captured, Is.Not.Null);
            Assert.That(captured.message.Length, Is.GreaterThan(12_000));
            Assert.That(read.diskSnapshotNextSequence, Is.EqualTo(boundary.nextSequence));
        }

        private ConsoleCaptureResult Start(string path = null, bool allowOutsideProject = false, long maxFileBytes = 50L * 1024L * 1024L)
        {
            var result = UPilotConsoleCaptureApi.Start(new ConsoleCaptureStartPayload
            {
                title = "AutomationCaptureApiTest",
                path = path ?? _relativeDirectory,
                allowOutsideProject = allowOutsideProject,
                maxFileBytes = maxFileBytes,
                ownerId = "UPilot.Editor.Tests",
                ownerToken = _ownerToken,
                requestKey = Guid.NewGuid().ToString("N"),
                excludeUPilot = false,
                includeStackTrace = true,
                flushIntervalMs = 100,
            });
            if (result.ok && result.session != null) _sessionId = result.session.sessionId;
            return result;
        }
    }
}
