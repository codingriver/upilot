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
            Assert.That(UPilotConsoleCaptureApi.Capabilities().available, Is.True);
            if (UPilotConsoleCaptureApi.GetBoundary().ok)
                Assert.Ignore("Console Capture API tests require no unrelated active capture.");
            _ownerToken = Guid.NewGuid().ToString("N");
            _relativeDirectory = Path.Combine("Log", "AutomationCaptureApiTests", Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(_sessionId))
                UPilotConsoleCaptureApi.Stop(_sessionId, _ownerToken);
            string full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", _relativeDirectory ?? string.Empty));
            if (Directory.Exists(full)) Directory.Delete(full, true);
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
            _sessionId = null;
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
            _sessionId = null;
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

        private ConsoleCaptureResult Start() => UPilotConsoleCaptureApi.Start(new ConsoleCaptureStartPayload
        {
            title = "AutomationCaptureApiTest",
            path = _relativeDirectory,
            ownerId = "UPilot.Editor.Tests",
            ownerToken = _ownerToken,
            requestKey = Guid.NewGuid().ToString("N"),
            excludeUPilot = false,
            includeStackTrace = true,
            flushIntervalMs = 100,
        });
    }
}
