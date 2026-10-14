using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

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

        [Test]
        public void InitializationProjectionDoesNotLoadJournalOrRecover()
        {
            WithIsolatedRecovery((type, flags) =>
            {
                type.GetField("_journal", flags).SetValue(null, null);
                type.GetField("_recoveryChecked", flags).SetValue(null, false);
                type.GetField("_recoveryCheckedAt", flags).SetValue(null, 0L);
                var value = UPilotServiceMaintenance.Initialization;
                Assert.That(value.recoveryChecked, Is.False);
                Assert.That(value.checkedAt, Is.Zero);
                Assert.That(type.GetField("_journal", flags).GetValue(null), Is.Null);
                Assert.That(File.Exists(_path), Is.False);
            });
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        // These synchronous tests swap only the in-memory journal, and restore every field in finally.
        // They never dispatch service maintenance or touch the project's maintenance journal.
        private void WithIsolatedRecovery(Action<Type, BindingFlags> test)
        {
            Assert.That(UPilotServiceMaintenance.IsActive, Is.False, "Do not isolate an active maintenance request.");
            var type = typeof(UPilotServiceMaintenance);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var names = new[] { "_journal", "_recoveryChecked", "_storageError", "_recoveryCheckedAt", "_shuttingDown" };
            var previous = new object[names.Length];
            for (var i = 0; i < names.Length; i++) previous[i] = type.GetField(names[i], flags).GetValue(null);
            try
            {
                type.GetField("_journal", flags).SetValue(null, _journal);
                type.GetField("_recoveryChecked", flags).SetValue(null, false);
                type.GetField("_storageError", flags).SetValue(null, "");
                type.GetField("_shuttingDown", flags).SetValue(null, false);
                type.GetMethod("RefreshUpdateSubscription", flags).Invoke(null, null);
                test(type, flags);
            }
            finally
            {
                for (var i = 0; i < names.Length; i++) type.GetField(names[i], flags).SetValue(null, previous[i]);
                type.GetMethod("RefreshUpdateSubscription", flags).Invoke(null, null);
            }
        }

        private static int MaintenanceUpdates => EditorApplication.update?.GetInvocationList().Count(callback =>
            callback.Method.DeclaringType == typeof(UPilotServiceMaintenance) && callback.Method.Name == "Update") ?? 0;

        [Test]
        public void UpdateInitializesRecoveryWithoutInspectorDelayCall()
        {
            WithIsolatedRecovery((type, flags) =>
            {
                type.GetMethod("Update", flags).Invoke(null, null);
                Assert.That(type.GetField("_recoveryChecked", flags).GetValue(null), Is.True);
                Assert.That(UPilotServiceMaintenance.StorageError, Is.Empty);
                Assert.That(_journal.Current, Is.Null);
                Assert.That(File.Exists(_path), Is.False, "No request was accepted or dispatched.");
                Assert.That(MaintenanceUpdates, Is.Zero);
            });
        }

        [Test]
        public void LateRecoveryCallbackDoesNotFailNewlyAcceptedRequest()
        {
            WithIsolatedRecovery((type, flags) =>
            {
                type.GetMethod("Update", flags).Invoke(null, null);
                var record = _journal.Accept(Request(), 120, 456, null);
                var original = File.ReadAllBytes(_path);
                type.GetMethod("Recover", flags).Invoke(null, null);
                Assert.That(record.status, Is.EqualTo("accepted"));
                Assert.That(record.stopAttempted || record.startAttempted, Is.False);
                Assert.That(File.ReadAllBytes(_path), Is.EqualTo(original));
            });
        }

        [Test]
        public void UpdateRecoveryRetainsCorruptJournalProtection()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(_path, "{");
            _journal = new ServiceMaintenanceJournal(_path, () => _now);
            WithIsolatedRecovery((type, flags) =>
            {
                type.GetMethod("Update", flags).Invoke(null, null);
                type.GetMethod("Update", flags).Invoke(null, null);
                Assert.That(type.GetField("_recoveryChecked", flags).GetValue(null), Is.True);
                Assert.That(UPilotServiceMaintenance.StorageError, Is.Not.Empty);
                var initialization = UPilotServiceMaintenance.Initialization;
                Assert.That(initialization.recoveryChecked, Is.True);
                Assert.That(initialization.checkedAt, Is.GreaterThan(0));
                Assert.That(initialization.storageError, Is.Not.Empty);
                Assert.That(initialization.source, Is.EqualTo("unity-maintenance-initialization"));
                Assert.That(File.ReadAllText(_path), Is.EqualTo("{"));
                Assert.That(MaintenanceUpdates, Is.Zero);
                Assert.That(Assert.Throws<ServiceMaintenanceException>(() =>
                    _journal.Accept(Request(), 120, 456, null)).Code, Is.EqualTo("SERVICE_RESTART_RECOVERY_REQUIRED"));
            });
        }

        [TestCase("succeeded")]
        [TestCase("failed")]
        [TestCase("timed_out")]
        public void RecoveredMaintenanceObservesOnlyUntilItsOriginalRecordIsTerminal(string terminal)
        {
            _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var request = Request();
            request.expectedProjectPath = UPilotProjectConfig.ProjectRoot;
            var record = _journal.Accept(request, 120, System.Diagnostics.Process.GetCurrentProcess().Id, null);
            record.status = "running";
            record.serverRestartId = "isolated-existing-restart";
            long deadline = record.deadlineAtUtcMs;
            WithIsolatedRecovery((type, flags) =>
            {
                type.GetMethod("Update", flags).Invoke(null, null); // Recovery only; no maintenance dispatch.
                type.GetMethod("Recover", flags).Invoke(null, null);
                type.GetMethod("RefreshUpdateSubscription", flags).Invoke(null, null);
                Assert.That(MaintenanceUpdates, Is.EqualTo(1));
                Assert.That(record.deadlineAtUtcMs, Is.EqualTo(deadline));
                Assert.That(record.stopAttempted || record.startAttempted, Is.False);
                record.Finish(terminal, "fixture", "isolated terminal", _now);
                type.GetMethod("Update", flags).Invoke(null, null);
                Assert.That(MaintenanceUpdates, Is.Zero);
                Assert.That(record.status, Is.EqualTo(terminal));
                Assert.That(record.stopAttempted || record.startAttempted, Is.False);
            });
        }

        [Test]
        public void DomainShutdownCannotReattachAnActiveMaintenanceObserver()
        {
            _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var request = Request();
            request.expectedProjectPath = UPilotProjectConfig.ProjectRoot;
            var record = _journal.Accept(request, 120, System.Diagnostics.Process.GetCurrentProcess().Id, null);
            record.status = "running";
            record.serverRestartId = "isolated-existing-restart";
            WithIsolatedRecovery((type, flags) =>
            {
                type.GetMethod("Update", flags).Invoke(null, null);
                Assert.That(MaintenanceUpdates, Is.EqualTo(1));
                type.GetMethod("StopObservation", flags).Invoke(null, null);
                type.GetMethod("RefreshUpdateSubscription", flags).Invoke(null, null);
                Assert.That(MaintenanceUpdates, Is.Zero);
                Assert.That(record.Active, Is.True, "Shutdown preserves the original record for legitimate recovery.");
                Assert.That(record.stopAttempted || record.startAttempted, Is.False);
            });
        }

        [Test]
        public void InitializationReportsIsolatedJournalReadFailureWithoutWriting()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(_path, "isolated read failure fixture");
            var original = File.ReadAllBytes(_path);
            using (var held = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                _journal = new ServiceMaintenanceJournal(_path, () => _now);
                Assert.That(_journal.LoadError, Is.Not.Empty, "The isolated file lock must cause a real read failure.");
                WithIsolatedRecovery((type, flags) =>
                {
                    type.GetField("_recoveryCheckedAt", flags).SetValue(null, 0L);
                    var before = UPilotServiceMaintenance.Initialization;
                    Assert.That(before.recoveryChecked, Is.False);
                    Assert.That(before.checkedAt, Is.Zero);
                    Assert.That(before.storageError, Is.Not.Empty);
                    type.GetMethod("Update", flags).Invoke(null, null);
                    var after = UPilotServiceMaintenance.Initialization;
                    Assert.That(after.recoveryChecked, Is.True);
                    Assert.That(after.checkedAt, Is.GreaterThan(0));
                    Assert.That(after.storageError, Is.EqualTo(before.storageError));
                    Assert.That(_journal.Current, Is.Null, "No maintenance request may be created.");
                });
            }
            Assert.That(File.ReadAllBytes(_path), Is.EqualTo(original));
        }

        [Test]
        public void InitializationBoundsErrorWithoutChangingStoredDiagnostic()
        {
            WithIsolatedRecovery((type, flags) =>
            {
                var message = new string('x', 700);
                type.GetField("_storageError", flags).SetValue(null, message);
                var value = UPilotServiceMaintenance.Initialization;
                Assert.That(value.storageError, Is.EqualTo(message.Substring(0, 512)));
                Assert.That(type.GetField("_storageError", flags).GetValue(null), Is.EqualTo(message));
                Assert.That(File.Exists(_path), Is.False);
            });
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
        public void ConfiguredTimeoutIsPreserved(int timeout)
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
            var joined = _journal.Accept(Request(), 120, 456, null);
            Assert.That(joined, Is.SameAs(record));
            Assert.That(joined.deadlineAtUtcMs, Is.EqualTo(record.deadlineAtUtcMs));
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
            Assert.That(record.affectedComponents, Is.EqualTo(new[] { "server", "bridge" }));
            record.Finish("succeeded", "", "", record.deadlineAtUtcMs);
            Assert.That(record.status, Is.EqualTo("timed_out"));
        }

        [Test]
        public void CorruptJournalRefusesAmbiguousHistoryButArchiveFailureDoesNotBlockReset()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(_path, "{");
            var corrupt = new ServiceMaintenanceJournal(_path, () => _now);
            Assert.That(Assert.Throws<ServiceMaintenanceException>(() =>
                corrupt.Accept(Request(), 120, 456, null)).Code, Is.EqualTo("SERVICE_RESTART_RECOVERY_REQUIRED"));
            var blockedPath = Path.Combine(_path, "cannot-be-created.json");
            var blocked = new ServiceMaintenanceJournal(blockedPath, () => _now);
            var accepted = blocked.Accept(Request(), 120, 456, null);
            Assert.That(blocked.Current, Is.SameAs(accepted));
            Assert.That(accepted.queueResetError, Is.Not.Empty);
            var calls = 0;
            Assert.That(blocked.Dispatch(() => { }, _ => calls++), Is.True);
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void HumanConfirmedHardResetCanReplaceCorruptJournalWithoutDispatchingRealServices()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(_path, "{");
            var journal = new ServiceMaintenanceJournal(_path, () => _now);
            var record = journal.Accept(Request(), 120, 456, null, humanConfirmed: true);
            Assert.That(record.humanConfirmed, Is.True);
            Assert.That(record.queueResetError, Does.Contain("Previous journal"));
            var calls = 0;
            Assert.That(journal.Dispatch(() => { }, _ => calls++), Is.True);
            Assert.That(journal.Dispatch(() => { }, _ => calls++), Is.False);
            Assert.That(calls, Is.EqualTo(1));
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
        public void DispatchPersistenceFailureDoesNotBlockSingleHardReset()
        {
            var record = _journal.Accept(Request(), 120, 456, null);
            File.Delete(_path);
            Directory.CreateDirectory(_path);
            var starts = 0;
            Assert.That(_journal.Dispatch(() => { }, _ => starts++), Is.True);
            Assert.That(starts, Is.EqualTo(1));
            Assert.That(record.queueResetError, Is.Not.Empty);
            Assert.That(_journal.Dispatch(() => { }, _ => starts++), Is.False);
            Assert.That(starts, Is.EqualTo(1));
        }
        [TestCase(1000)]
        [TestCase(21000)]
        [TestCase(29999)]
        public void MaintenanceDoesNotRenewConfiguredDeadline(long elapsed)
        {
            var r = _journal.Accept(Request("bridge"), 30, 456, null);
            r.phase = "health_query";
            r.healthProbeError = "/health HTTP 503";
            var accepted = _now;
            _now += elapsed;
            Assert.That(_journal.Expire(), Is.False);
            _journal.Save();
            _journal = new ServiceMaintenanceJournal(_path, () => _now);
            r = _journal.Current;
            Assert.That(r.deadlineAtUtcMs, Is.EqualTo(accepted + 30000));
            _now = accepted + 30000;
            Assert.That(_journal.Expire(), Is.True);
            Assert.That(_journal.Expire(), Is.False, "Finalization is once-only.");
            Assert.That(r.error, Does.Contain("health_query"));
            Assert.That(r.error, Does.Contain("/health HTTP 503"));
            r.Finish("succeeded", "", "", _now + 1);
            Assert.That(r.status, Is.EqualTo("timed_out"));
        }

        [Test]
        public void LegacyInFlightJournalAndConfigAreNotRewrittenOnRead()
        {
            var r = _journal.Accept(Request(), 120, 456, null);
            r.restartTimeoutSeconds = 120;
            r.deadlineAtUtcMs = r.acceptedAtUtcMs + 120000; // Persisted pre-upgrade journal.
            _journal.Save();
            var bytes = File.ReadAllBytes(_path);
            _journal = new ServiceMaintenanceJournal(_path, () => _now);
            Assert.That(_journal.Current.deadlineAtUtcMs, Is.EqualTo(r.deadlineAtUtcMs));
            Assert.That(File.ReadAllBytes(_path), Is.EqualTo(bytes));
            _now = r.deadlineAtUtcMs;
            Assert.That(_journal.Expire(), Is.True);
            Assert.That(_journal.Current.error, Does.Not.Contain("10 分钟"));
        }

        [Test]
        public void BridgeHealthMissingIsNotAnIdentityConflict()
        {
            Assert.That(UPilotServiceMaintenance.BridgeIdentityChanged(default, 42, _root), Is.False);
            var status = new McpServerStatus { HealthEndpointResponded = true, HealthServerProcessId = 42, HealthProjectPath = _root };
            Assert.That(UPilotServiceMaintenance.BridgeIdentityChanged(status, 42, _root), Is.False);
            status.HealthServerProcessId = 43;
            Assert.That(UPilotServiceMaintenance.BridgeIdentityChanged(status, 42, _root), Is.True);
            status.HealthServerProcessId = 42;
            status.HealthProjectPath = _root + "-foreign";
            Assert.That(UPilotServiceMaintenance.BridgeIdentityChanged(status, 42, _root), Is.True);
        }

        [Test]
        public void RestartWorkerRecordPreservesLauncherAndOriginalDeadline()
        {
            var record = UPilotServerRestartDiagnostics.CreateRecordForTests(_root, 41, "old");
            record.newProcessId = 42;
            record.newProcessCreatedAtTicks = 100;
            record.newProcessStartedAtUtcMs = 1000;
            record.deadlineAtUtcMs = 5000;
            Assert.That(UPilotServerRestartDiagnostics.ApplyWorkerProcess(record, 42, 43, 102), Is.True);
            Assert.That(record.launcherProcessId, Is.EqualTo(42));
            Assert.That(record.launcherProcessCreatedAtTicks, Is.EqualTo(100));
            Assert.That(record.newProcessId, Is.EqualTo(43));
            Assert.That(record.newProcessCreatedAtTicks, Is.EqualTo(102));
            Assert.That(record.newProcessStartedAtUtcMs, Is.EqualTo(1000));
            Assert.That(record.deadlineAtUtcMs, Is.EqualTo(5000));
            Assert.That(record.healthVerified, Is.False, "Adoption is not health/Bridge/readonly acceptance.");
            Assert.That(UPilotServerRestartDiagnostics.ApplyWorkerProcess(record, 42, 44, 103), Is.False);
            Assert.That(record.newProcessId, Is.EqualTo(43));
            Assert.That(UPilotRestartDiagnosticView.Identity(record), Does.Contain("启动器 PID | 42 | 42"));
        }

        [TestCase("failed", 42, 43, 102)]
        [TestCase("running", 99, 43, 102)]
        [TestCase("running", 42, 42, 102)]
        [TestCase("running", 42, 0, 102)]
        [TestCase("running", 42, 43, 99)]
        public void RestartWorkerRecordRejectsInvalidOrLateAdoption(string state, int launcher, int worker, long createdAt)
        {
            var record = UPilotServerRestartDiagnostics.CreateRecordForTests(_root, 41, "old");
            record.status = state;
            record.newProcessId = 42;
            record.newProcessCreatedAtTicks = 100;
            Assert.That(UPilotServerRestartDiagnostics.ApplyWorkerProcess(record, launcher, worker, createdAt), Is.False);
            Assert.That(record.newProcessId, Is.EqualTo(42));
            Assert.That(record.launcherProcessId, Is.Zero);
        }

        [Test]
        public void RestartParentQueryReadsRealOsEvidenceWithoutStartingAProcess()
        {
#if UNITY_EDITOR_WIN
            Assert.That(UPilotMcpServerManager.ReadParentProcessId(System.Diagnostics.Process.GetCurrentProcess().Id), Is.GreaterThan(0));
#endif
            Assert.That(UPilotMcpServerManager.ReadParentProcessId(0), Is.Zero);
            Assert.That(UPilotMcpServerManager.ReadParentProcessId(-1), Is.Zero);
        }

    }
}
