using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests.Automation
{
    public class UPilotConsoleCaptureApiV1Tests
    {
        private string _ownerToken;
        private string _relativeDirectory;
        private string _sessionId;

        [SetUp]
        public void SetUp()
        {
            Assert.That(UPilotConsoleCaptureApiV1.Capabilities().available, Is.True);
            if (UPilotConsoleCaptureApiV1.GetBoundary().ok)
                Assert.Ignore("Console Capture API tests require no unrelated active capture.");
            _ownerToken = Guid.NewGuid().ToString("N");
            _relativeDirectory = Path.Combine("Log", "AutomationCaptureApiTests", Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(_sessionId))
                UPilotConsoleCaptureApiV1.Stop(_sessionId, _ownerToken);
            string full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", _relativeDirectory ?? string.Empty));
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }

        [Test]
        public void CapabilitiesAndMainThreadGateDoNotExposeForceStop()
        {
            ConsoleCaptureApiCapabilitiesV1 capabilities = UPilotConsoleCaptureApiV1.Capabilities();
            Assert.That(capabilities.forceStopExposed, Is.False);
            Assert.That(capabilities.operations, Does.Not.Contain("ForceStop"));

            ConsoleCaptureResult result = Task.Run(() => UPilotConsoleCaptureApiV1.Status("missing")).GetAwaiter().GetResult();
            Assert.That(result.ok, Is.False);
            Assert.That(result.error, Does.Contain("CONSOLE_CAPTURE_MAIN_THREAD_REQUIRED"));
        }

        [Test]
        public void StartRequiresAnOwnerToken()
        {
            ConsoleCaptureResult result = UPilotConsoleCaptureApiV1.Start(new ConsoleCaptureStartPayload
            {
                title = "UnownedCapture",
                path = _relativeDirectory,
            });
            Assert.That(result.ok, Is.False);
            Assert.That(result.error, Does.Contain("CONSOLE_CAPTURE_OWNER_TOKEN_REQUIRED"));
            Assert.That(UPilotConsoleCaptureApiV1.GetBoundary().ok, Is.False);
        }

        [Test]
        public void WrongOwnerCannotStopOrAdoptCapture()
        {
            ConsoleCaptureResult started = Start();
            Assert.That(started.ok, Is.True, started.error);
            _sessionId = started.session.sessionId;

            ConsoleCaptureResult rejected = UPilotConsoleCaptureApiV1.Stop(_sessionId, "wrong-token");
            Assert.That(rejected.ok, Is.False);
            Assert.That(rejected.error, Does.Contain("所有权"));
            Assert.That(UPilotConsoleCaptureApiV1.Status(_sessionId).session.active, Is.True);

            ConsoleCaptureResult stopped = UPilotConsoleCaptureApiV1.Stop(_sessionId, _ownerToken);
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

            ConsoleCaptureBoundaryResultV1 boundary = UPilotConsoleCaptureApiV1.GetBoundary(_sessionId);
            Assert.That(boundary.ok, Is.True, boundary.errorMessage);
            Assert.That(boundary.nextSequence, Is.GreaterThan(0));
            ConsoleCaptureBoundaryResultV1 mismatch = UPilotConsoleCaptureApiV1.GetBoundary("foreign-session");
            Assert.That(mismatch.ok, Is.False);
            Assert.That(mismatch.errorCode, Is.EqualTo("CONSOLE_CAPTURE_IDENTITY_MISMATCH"));

            Task<ConsoleCaptureReadResult> readTask = UPilotConsoleCaptureApiV1.ReadAsync(new ConsoleCaptureReadPayload
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

        private ConsoleCaptureResult Start() => UPilotConsoleCaptureApiV1.Start(new ConsoleCaptureStartPayload
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
