using System;
using System.IO;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotServiceMaintenanceTests
    {
        private string _root, _path;
        private long _now;
        private ServiceMaintenanceJournal _journal;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "upilot-maintenance-" + Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_root, "record.json");
            _now = 1000000;
            _journal = new ServiceMaintenanceJournal(_path, () => _now);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private ServiceRestartRequest Request(string target = "server") => new()
        {
            maintenanceId = Guid.NewGuid().ToString("D"), target = target, reason = "test fixture",
            expectedProjectPath = _root, expectedServerProcessId = 123, expectedBridgeSessionId = "old",
            expectedMaintenanceId = "",
        };

        [TestCase("{}")]
        [TestCase("{\"aiServiceMaintenance\":{}}")]
        [TestCase("{\"safety\":{\"writeAccessApproved\":true,\"automationAuthorizationScopes\":[\"hangRestart\"]}}")]
        public void DefaultsAreIndependentAndDoNotAuthorize(string json)
        {
            var config = ServiceMaintenanceJournal.ParseSettings(json);
            Assert.That(config.restartTimeoutSeconds, Is.EqualTo(120));
            Assert.That(config.approved, Is.False);
            Assert.That(Assert.Throws<ServiceMaintenanceException>(() =>
                ServiceMaintenanceJournal.RequireAuthorization(config, _root)).Code, Is.EqualTo("SERVICE_MAINTENANCE_NOT_APPROVED"));
        }

        [TestCase("0")]
        [TestCase("29")]
        [TestCase("601")]
        [TestCase("120.5")]
        [TestCase("\"120\"")]
        [TestCase("true")]
        [TestCase("null")]
        public void InvalidExplicitTimeoutIsNotClamped(string value)
        {
            var error = Assert.Throws<ServiceMaintenanceException>(() =>
                ServiceMaintenanceJournal.ParseSettings("{\"aiServiceMaintenance\":{\"restartTimeoutSeconds\":" + value + "}}"));
            Assert.That(error.Code, Is.EqualTo("SERVICE_MAINTENANCE_CONFIG_INVALID"));
        }

        [TestCase(30)]
        [TestCase(120)]
        [TestCase(600)]
        public void ValidTimeoutIsPreserved(int timeout)
        {
            Assert.That(ServiceMaintenanceJournal.ParseSettings(
                "{\"aiServiceMaintenance\":{\"restartTimeoutSeconds\":" + timeout + "}}").restartTimeoutSeconds, Is.EqualTo(timeout));
        }

        [Test]
        public void AuthorizationRequiresExactProjectNotPackageVersion()
        {
            var config = new UPilotAiServiceMaintenanceConfig { approved = true, projectPath = _root };
            Assert.DoesNotThrow(() => ServiceMaintenanceJournal.RequireAuthorization(config, _root));
            Assert.Throws<ServiceMaintenanceException>(() => ServiceMaintenanceJournal.RequireAuthorization(config, _root + "-other"));
            config.approved = false;
            Assert.Throws<ServiceMaintenanceException>(() => ServiceMaintenanceJournal.RequireAuthorization(config, _root));
        }

        [Test]
        public void AcceptedRecordSurvivesDisconnectAndDuplicateDoesNotExtendDeadline()
        {
            var request = Request();
            var record = _journal.Accept(request, 120, 456, new[] { "fixture-in-flight" });
            Assert.That(record.status, Is.EqualTo("accepted"));
            Assert.That(record.affectedComponents, Is.EqualTo(new[] { "server", "bridge" }));
            Assert.That(record.affectedWorkComplete, Is.False);
            _now += 60000;
            _journal = new ServiceMaintenanceJournal(_path, () => _now);
            var duplicate = _journal.Accept(request, 600, 456, null);
            Assert.That(duplicate.deadlineAtUtcMs, Is.EqualTo(1120000));
            Assert.That(duplicate.restartTimeoutSeconds, Is.EqualTo(120));
            Assert.That(duplicate.stopAttempted, Is.False);
        }

        [Test]
        public void ConflictingAndOldRequestsCannotDispatchAgain()
        {
            var request = Request();
            var record = _journal.Accept(request, 120, 456, null);
            Assert.That(Assert.Throws<ServiceMaintenanceException>(() =>
                _journal.Accept(Request(), 120, 456, null)).Code, Is.EqualTo("SERVICE_RESTART_BUSY"));
            request.reason = "different";
            Assert.That(Assert.Throws<ServiceMaintenanceException>(() =>
                _journal.FindDuplicate(request)).Code, Is.EqualTo("SERVICE_RESTART_REQUEST_CONFLICT"));
            request.reason = record.reason;
            record.Finish("failed", "TEST", "fixture", _now);
            _journal.Save();
            var next = Request();
            next.expectedMaintenanceId = record.maintenanceId;
            var replacement = _journal.Accept(next, 120, 456, null);
            replacement.Finish("failed", "TEST", "fixture", _now);
            _journal.Save();
            Assert.That(Assert.Throws<ServiceMaintenanceException>(() =>
                _journal.Accept(request, 120, 456, null)).Code, Is.EqualTo("SERVICE_RESTART_IDENTITY_CHANGED"));
        }

        [Test]
        public void DeadlineCoversAllPhasesAndLateSuccessCannotOverwriteTimeout()
        {
            var record = _journal.Accept(Request(), 120, 456, null);
            _now += 5000;
            Assert.That(_journal.Expire(), Is.False, "Old 4-second port limit must not apply.");
            record.status = "running";
            record.phase = "verifying";
            record.stopAttempted = true;
            record.startAttempted = true;
            _now += 21000;
            Assert.That(_journal.Expire(), Is.False, "Old 20-second verification limit must not apply.");
            _journal.Save();
            _journal = new ServiceMaintenanceJournal(_path, () => _now);
            record = _journal.Current;
            Assert.That(record.deadlineAtUtcMs, Is.EqualTo(1120000));
            _now = record.deadlineAtUtcMs;
            Assert.That(_journal.Expire(), Is.True);
            Assert.That(record.status, Is.EqualTo("timed_out"));
            Assert.That(record.failurePhase, Is.EqualTo("verifying"));
            Assert.That(record.errorCode, Is.EqualTo("SERVICE_RESTART_TIMEOUT"));
            record.Finish("succeeded", "", "", _now + 1000);
            Assert.That(record.status, Is.EqualTo("timed_out"));
            Assert.That(record.stopAttempted && record.startAttempted, Is.True, "Timeout is not rollback.");
        }

        [Test]
        public void SuccessAtDeadlineIsTimeoutEvenWithoutObserverTick()
        {
            var record = _journal.Accept(Request("bridge"), 120, 456, null);
            Assert.That(record.affectedComponents, Is.EqualTo(new[] { "bridge" }));
            record.Finish("succeeded", "", "", record.deadlineAtUtcMs);
            Assert.That(record.status, Is.EqualTo("timed_out"));
        }

        [Test]
        public void CorruptJournalRefusesNewWorkAndPersistenceFailureDoesNotAccept()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(_path, "{");
            var corrupt = new ServiceMaintenanceJournal(_path, () => _now);
            Assert.That(Assert.Throws<ServiceMaintenanceException>(() =>
                corrupt.Accept(Request(), 120, 456, null)).Code, Is.EqualTo("SERVICE_RESTART_RECOVERY_REQUIRED"));
            var blockedPath = Path.Combine(_path, "cannot-be-created.json");
            var blocked = new ServiceMaintenanceJournal(blockedPath, () => _now);
            Assert.Throws<ServiceMaintenanceException>(() => blocked.Accept(Request(), 120, 456, null));
            Assert.That(blocked.Current, Is.Null);
        }

        [Test]
        public void DispatchRunsFixtureOnceWithoutReplayingInterruptedBusiness()
        {
            var request = Request();
            var record = _journal.Accept(request, 120, 456, new[] { "fixture-running" });
            var starts = 0;
            var businessCalls = 1;
            Assert.That(_journal.Dispatch(() => { }, r => { starts++; r.startAttempted = true; }), Is.True);
            _journal.Accept(request, 120, 456, null);
            Assert.That(_journal.Dispatch(() => { }, _ => starts++), Is.False);
            _now = record.deadlineAtUtcMs;
            _journal.Expire();
            Assert.That(_journal.Dispatch(() => { }, _ => starts++), Is.False);
            Assert.That(starts, Is.EqualTo(1));
            Assert.That(businessCalls, Is.EqualTo(1));
            Assert.That(record.affectedCommandIds, Is.EqualTo(new[] { "fixture-running" }));
        }

        [Test]
        public void RevocationAndTimeSpentInValidationCannotStartServices()
        {
            var record = _journal.Accept(Request(), 120, 456, null);
            var starts = 0;
            Assert.Throws<ServiceMaintenanceException>(() => _journal.Dispatch(
                () => ServiceMaintenanceJournal.RequireAuthorization(new UPilotAiServiceMaintenanceConfig(), _root),
                _ => starts++));
            Assert.That(starts, Is.Zero);
            Assert.That(record.stopAttempted, Is.False);
            Assert.That(_journal.Dispatch(() => _now = record.deadlineAtUtcMs, _ => starts++), Is.False);
            Assert.That(starts, Is.Zero);
            Assert.That(record.status, Is.EqualTo("timed_out"));
            Assert.That(record.stopAttempted, Is.False);
        }

        [Test]
        public void DispatchPersistenceFailurePreventsEffects()
        {
            var record = _journal.Accept(Request(), 120, 456, null);
            File.Delete(_path);
            Directory.CreateDirectory(_path);
            var starts = 0;
            Assert.Throws<ServiceMaintenanceException>(() => _journal.Dispatch(() => { }, _ => starts++));
            Assert.That(starts, Is.Zero);
        }
    }
}
