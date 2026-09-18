using System;
using System.IO;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests
{
    public class UPilotStartupDiagnosticsTests
    {
        [Test]
        public void MilestonesAreDeduplicatedAndLimitedToTheFiveStartupStages()
        {
            var record = UPilotStartupDiagnostics.CreateRecordForTests("C:/Project", 42, 123, 1000);
            var focus = new UPilotStartupFocusSnapshot
            {
                targetProcessId = 42,
                foregroundProcessId = 7,
                state = "unfocused",
                mainThreadId = 1,
                updateCount = 1,
            };

            Assert.That(UPilotStartupDiagnostics.AddMilestoneForTests(record, "bootstrap_entered", 1010, focus), Is.True);
            Assert.That(UPilotStartupDiagnostics.AddMilestoneForTests(record, "bootstrap_entered", 1020, focus), Is.False);
            Assert.That(UPilotStartupDiagnostics.AddMilestoneForTests(record, "first_editor_update", 1030, focus), Is.True);
            Assert.That(UPilotStartupDiagnostics.AddMilestoneForTests(record, "server_healthy", 1040, focus), Is.True);
            Assert.That(UPilotStartupDiagnostics.AddMilestoneForTests(record, "bridge_authenticated", 1050, focus), Is.True);
            Assert.That(UPilotStartupDiagnostics.AddMilestoneForTests(record, "editor_ready", 1060, focus), Is.True);
            Assert.That(UPilotStartupDiagnostics.AddMilestoneForTests(record, "unexpected", 1070, focus), Is.False);
            Assert.That(record.milestones.Count, Is.EqualTo(5));
            Assert.That(record.milestones[1].name, Is.EqualTo("first_editor_update"));
            Assert.That(record.milestones[1].elapsedFromProcessStartMs, Is.EqualTo(30));
        }

        [Test]
        public void MatchingProcessIdentityRestoresAcrossDomainReloadAndMismatchDoesNot()
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-startup-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "startup.json");
            Directory.CreateDirectory(directory);
            try
            {
                var record = UPilotStartupDiagnostics.CreateRecordForTests(directory, 42, 123, 1000);
                record.diagnosticsStartedAtUtcMs = 1005;
                record.healthObservationStartedAtUtcMs = 1006;
                record.healthProbeCount = 2;
                record.serverStartRetryStatus = "observing";
                record.serverStartAttemptCount = 2;
                record.lastServerStartAttemptAtUtcMs = 1007;
                record.nextServerStartAttemptAtUtcMs = 9007;
                record.backgroundExecution.status = "pending";
                UPilotStartupDiagnostics.AddMilestoneForTests(
                    record,
                    "bootstrap_entered",
                    1010,
                    new UPilotStartupFocusSnapshot { state = "unknown" });
                Assert.That(UPilotStartupDiagnostics.TryWriteRecordForTests(record, path, out var writeError), Is.True, writeError);

                Assert.That(UPilotStartupDiagnostics.TryLoadRecordForTests(
                    path, directory, 42, 123, out var restored, out var reason), Is.True, reason);
                Assert.That(restored.milestones.Count, Is.EqualTo(1));
                Assert.That(restored.diagnosticsStartedAtUtcMs, Is.EqualTo(1005));
                Assert.That(restored.healthObservationStartedAtUtcMs, Is.EqualTo(1006));
                Assert.That(restored.healthProbeCount, Is.EqualTo(2));
                Assert.That(restored.serverStartRetryStatus, Is.EqualTo("observing"));
                Assert.That(restored.serverStartAttemptCount, Is.EqualTo(2));
                Assert.That(restored.nextServerStartAttemptAtUtcMs, Is.EqualTo(9007));
                Assert.That(restored.backgroundExecution.status, Is.EqualTo("pending"));

                Assert.That(UPilotStartupDiagnostics.TryLoadRecordForTests(
                    path, directory, 42, 124, out _, out reason), Is.False);
                Assert.That(reason, Is.EqualTo("identity_mismatch"));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void LegacyHealthyRecordMigratesRetryStateWithoutInventingAttempts()
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-startup-legacy-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "startup.json");
            Directory.CreateDirectory(directory);
            try
            {
                var record = UPilotStartupDiagnostics.CreateRecordForTests(directory, 42, 123, 1000);
                record.schemaVersion = 1;
                UPilotStartupDiagnostics.AddMilestoneForTests(
                    record,
                    "server_healthy",
                    1010,
                    new UPilotStartupFocusSnapshot { state = "unknown" });
                Assert.That(UPilotStartupDiagnostics.TryWriteRecordForTests(record, path, out var error), Is.True, error);

                Assert.That(UPilotStartupDiagnostics.TryLoadRecordForTests(
                    path, directory, 42, 123, out var restored, out error), Is.True, error);
                Assert.That(restored.schemaVersion, Is.EqualTo(2));
                Assert.That(restored.serverStartRetryStatus, Is.EqualTo("succeeded"));
                Assert.That(restored.serverStartAttemptCount, Is.Zero);
                Assert.That(restored.nextServerStartAttemptAtUtcMs, Is.Zero);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void FocusSnapshotRemainsUnknownWhenForegroundWindowCannotBeResolved()
        {
            var snapshot = UPilotStartupDiagnostics.CaptureFocusSnapshot(
                42,
                9,
                1,
                () => IntPtr.Zero,
                _ => throw new AssertionException("PID lookup must not run without a foreground window."));

            Assert.That(snapshot.state, Is.EqualTo("unknown"));
            Assert.That(snapshot.foregroundProcessId, Is.Zero);
            Assert.That(snapshot.updateCount, Is.EqualTo(9));
        }

        [Test]
        public void BackgroundSamplingHonorsTheTenSecondBoundary()
        {
            Assert.That(UPilotStartupDiagnostics.ShouldTakeBackgroundSampleForTests(19999, 20000), Is.False);
            Assert.That(UPilotStartupDiagnostics.ShouldTakeBackgroundSampleForTests(20000, 20000), Is.True);
        }

        [Test]
        public void ServerStartRetryScheduleIsBoundedAndDoesNotConsumeEarlyPolls()
        {
            var record = UPilotStartupDiagnostics.CreateRecordForTests("project", 42, 123, 1000);
            var delays = new[] { 2000, 8000, 20000 };

            Assert.That(UPilotStartupDiagnostics.TryBeginServerStartAttemptForTests(
                record, 1000, 4, delays, out var attempt), Is.True);
            Assert.That(attempt, Is.EqualTo(1));
            Assert.That(record.nextServerStartAttemptAtUtcMs, Is.EqualTo(3000));

            Assert.That(UPilotStartupDiagnostics.TryBeginServerStartAttemptForTests(
                record, 2999, 4, delays, out _), Is.False);
            Assert.That(record.serverStartAttemptCount, Is.EqualTo(1));
            Assert.That(UPilotStartupDiagnostics.TryBeginServerStartAttemptForTests(
                record, 3000, 4, delays, out attempt), Is.True);
            Assert.That(attempt, Is.EqualTo(2));
            Assert.That(record.nextServerStartAttemptAtUtcMs, Is.EqualTo(11000));

            Assert.That(UPilotStartupDiagnostics.TryBeginServerStartAttemptForTests(
                record, 11000, 4, delays, out attempt), Is.True);
            Assert.That(attempt, Is.EqualTo(3));
            Assert.That(record.nextServerStartAttemptAtUtcMs, Is.EqualTo(31000));
            Assert.That(UPilotStartupDiagnostics.TryBeginServerStartAttemptForTests(
                record, 31000, 4, delays, out attempt), Is.True);
            Assert.That(attempt, Is.EqualTo(4));
            Assert.That(record.nextServerStartAttemptAtUtcMs, Is.Zero);

            Assert.That(UPilotStartupDiagnostics.TryBeginServerStartAttemptForTests(
                record, 31001, 4, delays, out _), Is.False);
            Assert.That(record.serverStartRetryStatus, Is.EqualTo("exhausted"));
            Assert.That(record.serverStartAttemptCount, Is.EqualTo(4));
        }

        [Test]
        public void NewDomainCycleResetsOnlyRetryStateAndSameCycleIsIdempotent()
        {
            var record = UPilotStartupDiagnostics.CreateRecordForTests("project", 42, 123, 1000);
            record.serverStartCycleId = "old";
            record.serverStartRetryStatus = "exhausted";
            record.serverStartAttemptCount = 4;
            record.lastServerStartAttemptAtUtcMs = 2000;
            record.nextServerStartAttemptAtUtcMs = 3000;
            record.lastServerStartReason = "server_entry_missing";
            record.healthObservationStatus = "healthy";
            record.healthObservationStartedAtUtcMs = 1500;
            record.healthProbeCount = 3;

            UPilotStartupDiagnostics.BeginServerStartCycleForTests(record, "new");
            Assert.That(record.serverStartCycleId, Is.EqualTo("new"));
            Assert.That(record.serverStartRetryStatus, Is.EqualTo("not_started"));
            Assert.That(record.serverStartAttemptCount, Is.Zero);
            Assert.That(record.lastServerStartAttemptAtUtcMs, Is.Zero);
            Assert.That(record.nextServerStartAttemptAtUtcMs, Is.Zero);
            Assert.That(record.healthObservationStatus, Is.EqualTo("not_started"));
            Assert.That(record.healthObservationStartedAtUtcMs, Is.Zero);
            Assert.That(record.healthProbeCount, Is.Zero);

            record.serverStartAttemptCount = 1;
            UPilotStartupDiagnostics.BeginServerStartCycleForTests(record, "new");
            Assert.That(record.serverStartAttemptCount, Is.EqualTo(1));
        }

        [Test]
        public void PersistenceFailureIsReturnedWithoutThrowing()
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-startup-write-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var record = UPilotStartupDiagnostics.CreateRecordForTests(directory, 42, 123, 1000);
                Assert.That(UPilotStartupDiagnostics.TryWriteRecordForTests(record, directory, out var error), Is.False);
                Assert.That(error, Is.Not.Empty);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void VerifiedStartupHealthRequiresTheHealthPidAndIndependentOwnershipEvidence()
        {
            var status = new McpServerStatus
            {
                HealthEndpointResponded = true,
                HealthIdentifiesUPilot = true,
                HealthServerProcessId = 77,
                ProcessId = 77,
                ProcessOwnership = McpProcessOwnership.CurrentUPilot,
                ProcessOwnershipEvidence = "已跟踪的 UPilot 进程",
            };
            Assert.That(UPilotMcpServerManager.IsVerifiedStartupHealth(status), Is.True);

            status.HealthServerProcessId = 78;
            Assert.That(UPilotMcpServerManager.IsVerifiedStartupHealth(status), Is.False);
            status.HealthServerProcessId = 77;
            status.ProcessOwnershipEvidence = "UPilot 健康检查响应";
            Assert.That(UPilotMcpServerManager.IsVerifiedStartupHealth(status), Is.False);
        }

        [Test]
        public void HistoricalHealthMilestoneDoesNotFinishANewDomainCycle()
        {
            var record = UPilotStartupDiagnostics.CreateRecordForTests("project", 42, 123, 1000);
            UPilotStartupDiagnostics.AddMilestoneForTests(
                record,
                "server_healthy",
                1010,
                new UPilotStartupFocusSnapshot { state = "unknown" });
            record.serverStartCycleId = "old";
            record.serverStartRetryStatus = "succeeded";

            UPilotStartupDiagnostics.BeginServerStartCycleForTests(record, "new");

            Assert.That(record.serverStartRetryStatus, Is.EqualTo("not_started"));
            Assert.That(UPilotStartupDiagnostics.TryBeginServerStartAttemptForTests(
                record, 2000, 4, new[] { 2000, 8000, 20000 }, out var attempt), Is.True);
            Assert.That(attempt, Is.EqualTo(1));
        }

        [Test]
        public void RestartCompletesOnlyAfterExactProcessProjectAndNewBridgeSessionAreVerified()
        {
            var project = Path.Combine(Path.GetTempPath(), "upilot-restart-project");
            var record = UPilotServerRestartDiagnostics.CreateRecordForTests(project, 41, "old-session");
            UPilotServerRestartDiagnostics.RecordProcessStartedForTests(record, 42);

            UPilotServerRestartDiagnostics.RecordHealthVerifiedForTests(record, 42, project + "-other");
            UPilotServerRestartDiagnostics.RecordBridgeVerifiedForTests(record, "old-session");
            Assert.That(record.status, Is.EqualTo("running"));
            Assert.That(record.projectIdentityVerified, Is.False);
            Assert.That(record.bridgeVerified, Is.False);

            UPilotServerRestartDiagnostics.RecordHealthVerifiedForTests(record, 42, project);
            Assert.That(record.status, Is.EqualTo("running"));
            UPilotServerRestartDiagnostics.RecordBridgeVerifiedForTests(record, "new-session");

            Assert.That(record.status, Is.EqualTo("succeeded"));
            Assert.That(record.phase, Is.EqualTo("completed"));
            Assert.That(record.newProcessId, Is.EqualTo(42));
            Assert.That(record.newBridgeSessionId, Is.EqualTo("new-session"));
        }

        [Test]
        public void RestartRecordPersistsIdentityAndBoundsFailureDiagnostics()
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-restart-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "server-restart.json");
            Directory.CreateDirectory(directory);
            try
            {
                var record = UPilotServerRestartDiagnostics.CreateRecordForTests(directory, 41, "old-session");
                record.newProcessId = 42;
                record.status = "failed";
                record.exitObserved = true;
                record.exitCode = 17;
                record.stderrTail = UPilotServerRestartDiagnostics.BoundForTests(new string('x', 5000));

                Assert.That(UPilotServerRestartDiagnostics.TryWriteRecordForTests(record, path, out var error), Is.True, error);
                Assert.That(UPilotServerRestartDiagnostics.TryLoadRecordForTests(path, out var restored, out error), Is.True, error);
                Assert.That(restored.operationId, Is.EqualTo("restart-test"));
                Assert.That(restored.oldProcessId, Is.EqualTo(41));
                Assert.That(restored.newProcessId, Is.EqualTo(42));
                Assert.That(restored.exitObserved, Is.True);
                Assert.That(restored.exitCode, Is.EqualTo(17));
                Assert.That(restored.stderrTail.Length, Is.EqualTo(UPilotServerRestartDiagnostics.DiagnosticTailLimit));
                Assert.That(
                    UPilotServerRestartDiagnostics.TryWriteRecordForTests(record, directory, out error),
                    Is.False);
                Assert.That(error, Is.Not.Empty);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void RestartHealthRejectsPidAndProjectMismatch()
        {
            var project = Path.Combine(Path.GetTempPath(), "upilot-restart-health");
            var status = new McpServerStatus
            {
                HealthEndpointResponded = true,
                HealthIdentifiesUPilot = true,
                HealthServerProcessId = 42,
                HealthProjectPath = project,
                ProcessId = 42,
                ProcessOwnership = McpProcessOwnership.CurrentUPilot,
                ProcessOwnershipEvidence = "已跟踪的 UPilot 进程",
            };

            Assert.That(UPilotMcpServerManager.IsVerifiedRestartHealth(status, 42, project), Is.True);
            Assert.That(UPilotMcpServerManager.IsVerifiedRestartHealth(status, 43, project), Is.False);
            Assert.That(UPilotMcpServerManager.IsVerifiedRestartHealth(status, 42, project + "-other"), Is.False);
            Assert.That(UPilotMcpServerManager.IsNewBridgeSession("old", "old"), Is.False);
            Assert.That(UPilotMcpServerManager.IsNewBridgeSession("old", "new"), Is.True);
        }
    }
}
