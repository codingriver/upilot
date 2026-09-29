using System;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.TestTools;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests
{
    public class UPilotStartupDiagnosticsTests
    {
        private static UPilotMcpServerManager IsolatedStatusManager()
        {
            // Do not construct the singleton, register Editor callbacks, or touch real processes.
            var manager = (UPilotMcpServerManager)FormatterServices.GetUninitializedObject(typeof(UPilotMcpServerManager));
            SetManagerField(manager, "_statusLock", new object());
            return manager;
        }

        private static void SetManagerField(UPilotMcpServerManager manager, string name, object value) =>
            typeof(UPilotMcpServerManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(manager, value);

        [TestCase("lifecycle_stop")]
        [TestCase("explicit_stop")]
        [TestCase("domain_reload")]
        [TestCase("editor_exit")]
        public async Task StatusCancellationSurvivesInvalidationAndLateCompletion(string reason)
        {
            var manager = IsolatedStatusManager();
            using var old = new UPilotMcpServerManager.StatusRefreshRequest(0);
            using var current = new UPilotMcpServerManager.StatusRefreshRequest(3);
            SetManagerField(manager, "_activeStatusRefresh", old);
            var release = new TaskCompletionSource<bool>();
            async Task<McpServerStatus> CompleteLater()
            {
                await release.Task;
                return await (Task<McpServerStatus>)typeof(UPilotMcpServerManager)
                    .GetMethod("RefreshStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(manager, new object[] { 0, 0, old });
            }
            var work = CompleteLater();
            manager.InvalidateStatusCache(reason);
            manager.InvalidateStatusCache();
            manager.InvalidateStatusCache();
            Assert.That(old.Reason, Is.EqualTo(reason));
            Assert.That(old.Token.IsCancellationRequested, Is.True);
            SetManagerField(manager, "_activeStatusRefresh", current);
            SetManagerField(manager, "_cachedStatus", new McpServerStatus { IsRunning = true, StatusGeneration = 3 });
            LogAssert.Expect(LogType.Warning,
                $"[UPilotMcpServerManager] Expected {reason} status interruption at port_probe (generation 0).");
            release.SetResult(true);
            var observed = await work;
            Assert.That(observed.StatusGeneration, Is.EqualTo(3));
            Assert.That(observed.IsRunning, Is.True, "Old work must not publish into the new cache.");
            typeof(UPilotMcpServerManager).GetMethod("ReleaseStatusRefresh", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(manager, new object[] { old });
            manager.InvalidateStatusCache("explicit_stop");
            Assert.That(current.Reason, Is.EqualTo("explicit_stop"), "Old cleanup must not clear or dispose the newer request.");
        }

        [Test]
        public void StatusCancellationFirstCauseWinsAndOrdinaryInvalidationDoesNotCancel()
        {
            var manager = IsolatedStatusManager();
            using var request = new UPilotMcpServerManager.StatusRefreshRequest(0);
            SetManagerField(manager, "_activeStatusRefresh", request);
            manager.InvalidateStatusCache();
            Assert.That(request.Token.IsCancellationRequested, Is.False);
            request.Cancel("timeout");
            manager.InvalidateStatusCache("lifecycle_stop");
            Assert.That(request.Reason, Is.EqualTo("timeout"));
            Assert.That(UPilotMcpServerManager.IsExpectedStatusInterruption(
                new OperationCanceledException(), request.Token, request.Reason), Is.False);
            using var stopped = new UPilotMcpServerManager.StatusRefreshRequest(1);
            stopped.Cancel("lifecycle_stop");
            stopped.Cancel("timeout");
            Assert.That(stopped.Reason, Is.EqualTo("lifecycle_stop"));
            stopped.Dispose();
            Assert.DoesNotThrow(() => stopped.Cancel("editor_exit"));
        }

        [TestCase("timeout")]
        [TestCase("unknown")]
        public async Task StatusUnexpectedCancellationRemainsErrorAfterStop(string reason)
        {
            var manager = IsolatedStatusManager();
            using var request = new UPilotMcpServerManager.StatusRefreshRequest(0);
            SetManagerField(manager, "_activeStatusRefresh", request);
            request.Cancel(reason);
            manager.InvalidateStatusCache("lifecycle_stop");
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
                @"\[UPilotMcpServerManager\] Status refresh failed at port_probe \(generation 0\): System.OperationCanceledException"));
            await (Task<McpServerStatus>)typeof(UPilotMcpServerManager)
                .GetMethod("RefreshStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(manager, new object[] { 0, 0, request });
            Assert.That(request.Reason, Is.EqualTo(reason));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void UpdateStopUsesConfirmedExitInsteadOfStaleRunningCache(bool portsAvailable)
        {
            var result = new UPilotMcpServerManager.ServerStopResult(true, portsAvailable, 4, 2, 3);
            var bridge = new BridgeStatus();
            var stale = new McpServerStatus { IsRunning = true, StatusGeneration = 3 };
            Assert.That(UPilotMainWindow.IsUpdateStopConfirmed(bridge, result, true), Is.True);
            Assert.That(result.PortsAvailable, Is.EqualTo(portsAvailable));
            Assert.That(UPilotMainWindow.ShouldStopServicesForUpdate(bridge, stale, result, true), Is.False);
            stale.StatusGeneration = 4;
            Assert.That(UPilotMainWindow.ShouldStopServicesForUpdate(bridge, stale, result, true), Is.True,
                "A fresh running observation must not be hidden by prior stop evidence.");
        }

        [Test]
        public void UpdateStopInvalidationForcesFreshStatusEvenWhenOldEditorObservationWasWaiting()
        {
            var stale = new McpServerStatus
            {
                IsRunning = true,
                StatusGeneration = 3,
                EditorObservationAvailable = true,
                EditorObservationStatus = "waiting_editor",
            };
            Assert.That(UPilotMcpServerManager.ShouldRequestFullStatusRefresh(stale, 3), Is.False);
            Assert.That(UPilotMcpServerManager.ShouldRequestFullStatusRefresh(stale, 4), Is.True);
            stale.StatusGeneration = 4;
            Assert.That(UPilotMcpServerManager.ShouldRequestFullStatusRefresh(stale, 4), Is.False);
            var stopped = new UPilotMcpServerManager.ServerStopResult(true, true, 4, 0, 0);
            Assert.That(UPilotMainWindow.ShouldStopServicesForUpdate(default, stale, stopped, true), Is.True,
                "Fresh evidence of a subsequent start must not be suppressed.");
        }

        [Test]
        public void UpdateStopRejectsUnknownFailureChangedGenerationAndRunningBridge()
        {
            var manager = IsolatedStatusManager();
            var result = new UPilotMcpServerManager.ServerStopResult(true, true, 4, 0, 0);
            var bridge = new BridgeStatus();
            var stale = new McpServerStatus { IsRunning = true, StatusGeneration = 3 };
            Assert.That(manager.IsStopResultCurrent(result), Is.True);
            foreach (var field in new[] { "_startInProgress", "_restartPending" })
            {
                SetManagerField(manager, field, true);
                Assert.That(manager.IsStopResultCurrent(result), Is.False);
                SetManagerField(manager, field, false);
            }
            SetManagerField(manager, "_trackedProcessId", (int?)42);
            Assert.That(manager.IsStopResultCurrent(result), Is.False);
            SetManagerField(manager, "_trackedProcessId", null);
            Assert.That(UPilotMainWindow.IsUpdateStopConfirmed(bridge, default, true), Is.False);
            Assert.That(UPilotMainWindow.ShouldStopServicesForUpdate(bridge, stale, default, true), Is.True);
            SetManagerField(manager, "_restartGeneration", 1L);
            Assert.That(manager.IsStopResultCurrent(result), Is.False);
            SetManagerField(manager, "_restartGeneration", 0L);
            SetManagerField(manager, "_startAttemptGeneration", 1);
            Assert.That(manager.IsStopResultCurrent(result), Is.False);
            Assert.That(UPilotMainWindow.IsUpdateStopConfirmed(bridge, result, false), Is.False);
            Assert.That(UPilotMainWindow.ShouldStopServicesForUpdate(bridge, stale, result, false), Is.True);
            bridge.IsStarted = true;
            Assert.That(UPilotMainWindow.IsUpdateStopConfirmed(bridge, result, true), Is.False);
            Assert.That(UPilotMainWindow.ShouldStopServicesForUpdate(bridge, stale, result, true), Is.True);
        }

        [TestCase("mainEditor", true)]
        [TestCase("assetImportWorker", false)]
        [TestCase("batchMode", false)]
        [TestCase("worker", false)]
        [TestCase("unknown", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void UnifiedMainEditorProcessPredicateRejectsAuxiliaryRoles(string role, bool expected)
        {
            Assert.That(UPilotBridge.IsMainEditorProcess(role), Is.EqualTo(expected));
        }

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
        public void ExistingProjectServiceCanBeReattachedFromHealthAndPortIdentity()
        {
            var project = Path.Combine(Path.GetTempPath(), "upilot-existing-server");
            var status = new McpServerStatus
            {
                IsRunning = true,
                HttpPortListening = true,
                WsPortListening = true,
                HealthEndpointResponded = true,
                HealthIdentifiesUPilot = true,
                HealthServerProcessId = 77,
                HealthProjectPath = project,
                ProcessId = 77,
                ProcessOwnership = McpProcessOwnership.CurrentUPilot,
                ProcessOwnershipEvidence = "UPilot 健康检查响应",
            };

            Assert.That(
                UPilotMcpServerManager.IsVerifiedExistingProjectService(status, project),
                Is.True,
                "A Domain Reload may lose the tracked process and command-line evidence while health and port PID remain exact.");

            status.HealthProjectPath = project + "-other";
            Assert.That(UPilotMcpServerManager.IsVerifiedExistingProjectService(status, project), Is.False);
            status.HealthProjectPath = project;

            status.ProcessId = 78;
            Assert.That(UPilotMcpServerManager.IsVerifiedExistingProjectService(status, project), Is.False);
            status.ProcessId = 77;

            status.HealthEndpointResponded = false;
            Assert.That(UPilotMcpServerManager.IsVerifiedExistingProjectService(status, project), Is.False);
            status.HealthEndpointResponded = true;

            status.WsPortListening = false;
            Assert.That(UPilotMcpServerManager.IsVerifiedExistingProjectService(status, project), Is.False);
            status.WsPortListening = true;

            status.ProcessOwnership = McpProcessOwnership.Foreign;
            Assert.That(UPilotMcpServerManager.IsVerifiedExistingProjectService(status, project), Is.False);
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
        public void BatchProcessQueryParsesWindowsRecordsWithoutSplittingCommandLines()
        {
            var output = "\r\r\nCommandLine=python \"C:/中文 路径/run_upilot_mcp.py\" --log-file=\"D:/a=b/项目/mcp-server.log\" --port=8767\r\r\nProcessId=41\r\r\n\r\r\nCommandLine=\r\r\nProcessId=42\r\r\n";
            var commands = UPilotMcpServerManager.ParseCandidateCommandLines(output, new[] { 41, 42 });
            Assert.That(commands[41], Does.Contain("a=b/项目"));
            Assert.That(commands[41], Does.Contain("--port=8767"));
            Assert.That(commands[42], Is.Empty);
            Assert.Throws<FormatException>(() => UPilotMcpServerManager.ParseCandidateCommandLines(
                "CommandLine=x\nProcessId=41\n\nCommandLine=y\nProcessId=41", new[] { 41 }));
            Assert.Throws<FormatException>(() => UPilotMcpServerManager.ParseCandidateCommandLines(
                "CommandLine=x\nUnknown=z\nProcessId=41", new[] { 41 }));
            Assert.Throws<InvalidOperationException>(() => UPilotMcpServerManager.ParseCandidateCommandLines(
                new string('x', 1024 * 1024 + 1), new[] { 41 }));
        }

        [Test]
        public void CandidateSelectionQueriesOnceAndRejectsChangedIdentityOrMissingEvidence()
        {
            const int http = 8012, ws = 8766;
            var log = Path.Combine(UPilotProjectConfig.ProjectRoot, "log", "mcp-server.log");
            var same = "python \"C:/中文 目录/run_upilot_mcp.py\" --http-port=8012 --port=8766 --log-file \"" + log + "\"";
            var other = same.Replace("8012", "8013");
            var before = new System.Collections.Generic.Dictionary<int, long> { [41] = 100, [42] = 200, [43] = 300 };
            var commands = new System.Collections.Generic.Dictionary<int, string> { [41] = same, [42] = other, [43] = same };
            var count = 0;
            var selected = UPilotMcpServerManager.ResolveVerifiedCandidates(before,
                ids => { count++; Assert.That(ids, Is.EquivalentTo(new[] { 41, 42, 43 })); return commands; },
                pid => before[pid], http, ws, () => false, out var verified);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(verified, Is.EqualTo(3));
            Assert.That(selected.ConvertAll(x => x.pid), Is.EquivalentTo(new[] { 41, 43 }));
            Assert.Throws<InvalidOperationException>(() => UPilotMcpServerManager.ResolveVerifiedCandidates(before,
                ids => commands, pid => pid == 41 ? 999 : before[pid], http, ws, () => false, out _));
            Assert.Throws<InvalidOperationException>(() => UPilotMcpServerManager.ResolveVerifiedCandidates(before,
                ids => new System.Collections.Generic.Dictionary<int, string> { [41] = same },
                pid => before[pid], http, ws, () => false, out _));
            Assert.Throws<TimeoutException>(() => UPilotMcpServerManager.ResolveVerifiedCandidates(before,
                ids => commands, pid => before[pid], http, ws, () => true, out _));
            Assert.Throws<InvalidOperationException>(() => UPilotMcpServerManager.ResolveVerifiedCandidates(before,
                ids => throw new InvalidOperationException("WMIC failed"), pid => before[pid],
                http, ws, () => false, out _));
        }

        [Test]
        public void StopOwnershipIgnoresOtherPortsAndAcceptsOnlyVerifiedTargetOwners()
        {
            var ports = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<int>>
            {
                [10] = new() { 8765, 8011 },
            };
            Assert.DoesNotThrow(() => UPilotMcpServerManager.ValidateStopPortOwnership(
                ports, true, Array.Empty<int>(), 8017, 8770));
            ports[20] = new() { 8017, 8770 };
            Assert.DoesNotThrow(() => UPilotMcpServerManager.ValidateStopPortOwnership(
                ports, true, new[] { 20 }, 8017, 8770));
            Assert.Throws<InvalidOperationException>(() => UPilotMcpServerManager.ValidateStopPortOwnership(
                ports, true, new[] { 10 }, 8017, 8770));
            ports[20] = new() { 8017 };
            ports[30] = new() { 8770 };
            Assert.Throws<InvalidOperationException>(() => UPilotMcpServerManager.ValidateStopPortOwnership(
                ports, true, new[] { 20, 30 }, 8017, 8770));
        }

        [Test]
        public void StopOwnershipQueryFailureNeverMeansAnEmptyQueue()
        {
            var ports = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<int>>();
            Assert.Throws<InvalidOperationException>(() => UPilotMcpServerManager.ValidateStopPortOwnership(
                ports, false, Array.Empty<int>(), 8017, 8770));
            Assert.Throws<InvalidOperationException>(() => UPilotMcpServerManager.ValidateStopPortOwnership(
                null, true, Array.Empty<int>(), 8017, 8770));
            Assert.Throws<InvalidOperationException>(() => UPilotMcpServerManager.ValidateStopPortOwnership(
                ports, true, null, 8017, 8770));
        }

        [TestCase("TryStartBridge")]
        [TestCase("TryStartMcpServer")]
        public void ExplicitUserStopRemovesPendingBootstrapCallbackBeforeStarting(string methodName)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic |
                                                       System.Reflection.BindingFlags.Static;
            var stoppedField = typeof(UPilotQuickStart).GetField("_explicitlyStopped", flags);
            var recordField = typeof(UPilotStartupDiagnostics).GetField("s_record", flags);
            var pathField = typeof(UPilotStartupDiagnostics).GetField("s_path", flags);
            var method = typeof(UPilotBootstrap).GetMethod(methodName, flags);
            var callback = (UnityEditor.EditorApplication.CallbackFunction)Delegate.CreateDelegate(
                typeof(UnityEditor.EditorApplication.CallbackFunction), method);
            var stopped = stoppedField.GetValue(null);
            var previousRecord = recordField.GetValue(null);
            var previousPath = pathField.GetValue(null);
            var previousUpdates = UnityEditor.EditorApplication.update;
            var record = UPilotStartupDiagnostics.CreateRecordForTests("C:/isolated-bootstrap", 42, 123, 1000);
            record.serverStartRetryStatus = "observing";
            record.serverStartAttemptCount = 3;
            record.nextServerStartAttemptAtUtcMs = 1;
            try
            {
                // Synchronous isolation: no service stop, real settings changes or project journal writes.
                stoppedField.SetValue(null, true);
                recordField.SetValue(null, record);
                pathField.SetValue(null, "");
                UnityEditor.EditorApplication.update += callback;

                method.Invoke(null, null);

                Assert.That(UPilotQuickStart.IsExplicitlyStopped, Is.True);
                Assert.That(record.serverStartAttemptCount, Is.EqualTo(3));
                Assert.That(UnityEditor.EditorApplication.update?.GetInvocationList() ?? Array.Empty<Delegate>(),
                    Has.No.Member(callback));
                if (methodName == "TryStartMcpServer")
                {
                    Assert.That(record.serverStartRetryStatus, Is.EqualTo("blocked"));
                    Assert.That(record.lastServerStartReason, Is.EqualTo("user_stopped"));
                    Assert.That(record.nextServerStartAttemptAtUtcMs, Is.Zero);
                }
            }
            finally
            {
                UnityEditor.EditorApplication.update = previousUpdates;
                stoppedField.SetValue(null, stopped);
                recordField.SetValue(null, previousRecord);
                pathField.SetValue(null, previousPath);
            }
        }

        [Test]
        public void PreflightFailureDoesNotStopBridgeOrServer()
        {
            var bridgeStops = 0;
            var serverStops = 0;
            var restores = 0;
            Assert.Throws<TimeoutException>(() => UPilotMcpServerManager.RunVerifiedStopSequence(
                () => throw new TimeoutException("probe budget exhausted"), true,
                () => bridgeStops++, _ => serverStops++, () => true, () => restores++));
            Assert.That(bridgeStops, Is.Zero);
            Assert.That(serverStops, Is.Zero);
            Assert.That(restores, Is.Zero);
        }

        [Test]
        public void BridgeStopFailureRestoresOnlyVerifiedOriginalOnceAndRetainsFailure()
        {
            var calls = new System.Collections.Generic.List<string>();
            Assert.Throws<InvalidOperationException>(() => UPilotMcpServerManager.RunVerifiedStopSequence(
                () => calls.Add("prepare"), true, () => calls.Add("bridge"),
                onAttempt => { calls.Add("server_before_attempt"); throw new InvalidOperationException("stop failed"); },
                () => { calls.Add("verify"); return true; }, () => calls.Add("restore")));
            Assert.That(calls, Is.EqualTo(new[] { "prepare", "bridge", "server_before_attempt", "verify", "restore" }));
            calls.Clear();
            Assert.Throws<InvalidOperationException>(() => UPilotMcpServerManager.RunVerifiedStopSequence(
                () => calls.Add("prepare"), true, () => calls.Add("bridge"),
                _ => throw new InvalidOperationException("stop failed"),
                () => false, () => calls.Add("restore")));
            Assert.That(calls, Is.EqualTo(new[] { "prepare", "bridge" }));
        }

        [Test]
        public void UncertainServerStopAndExpiredMaintenanceNeverReplayOrRestore()
        {
            var calls = new System.Collections.Generic.List<string>();
            Assert.Throws<InvalidOperationException>(() => UPilotMcpServerManager.RunVerifiedStopSequence(
                () => calls.Add("prepare"), true, () => calls.Add("bridge"),
                onAttempt => { onAttempt(); calls.Add("kill_attempted"); throw new InvalidOperationException("exit unknown"); },
                () => { calls.Add("verify"); return true; }, () => calls.Add("restore")));
            Assert.That(calls, Is.EqualTo(new[] { "prepare", "bridge", "kill_attempted" }));
            calls.Clear();
            Assert.Throws<TimeoutException>(() => UPilotMcpServerManager.RunVerifiedStopSequence(
                () => throw new TimeoutException("maintenance expired"), true, () => calls.Add("bridge"),
                _ => calls.Add("server"), () => true, () => calls.Add("restore")));
            Assert.That(calls, Is.Empty);
        }

        [Test]
        public void RestartDiagnosticGateOrderAndSpecificFailureCategorySurviveTimeout()
        {
            var record = UPilotServerRestartDiagnostics.CreateRecordForTests("C:/project", 1, "old");
            Assert.That(UPilotServerRestartDiagnostics.GateKeys, Is.EqualTo(new[]
            {
                "old_identity", "bridge_stop", "old_exit", "ports", "new_process",
                "health", "identity", "bridge_session", "deployment", "readonly"
            }));
            record.status = "failed";
            record.errorCode = "SERVICE_RESTART_TIMEOUT";
            record.statusProbeCount = 3;
            record.gateDiagnostics = new[]
            {
                new UPilotRestartGate { key = "health", state = "passed", startedAtUtcMs = 1000, endedAtUtcMs = 2000 },
                new UPilotRestartGate { key = "readonly", state = "failed", evidenceCode = "readonly_timeout",
                    evidence = "Bridge did not return", startedAtUtcMs = 2000, endedAtUtcMs = 3200 }
            };
            Assert.That(UPilotRestartDiagnosticView.Gates(record), Does.Contain("1 秒"));
            Assert.That(UPilotRestartDiagnosticView.Gates(record), Does.Contain("readonly_timeout"));
            Assert.That(UPilotRestartDiagnosticView.FailureCategory(record), Is.EqualTo("readonly_timeout"));
            Assert.That(UPilotRestartDiagnosticView.NextAction(record), Does.Contain("只读往返"));
            Assert.That(UPilotRestartDiagnosticView.Summary(record), Does.Contain("readonly_timeout"));
        }

        [Test]
        public void RestartOversizeDiagnosticReportsActualLimitAndCloseCode()
        {
            var record = UPilotServerRestartDiagnostics.CreateRecordForTests("C:/project", 1, "old");
            record.status = "failed";
            record.lastBridgeCloseCode = "1009";
            record.lastBridgeOversizeActualBytes = 1300775;
            record.lastBridgeOversizeLimitBytes = 1048576;
            record.lastBridgeOversizeSource = "tool result";
            var summary = UPilotRestartDiagnosticView.Summary(record);
            Assert.That(summary, Does.Contain("1009"));
            Assert.That(summary, Does.Contain("1,300,775"));
            Assert.That(summary, Does.Contain("1,048,576"));
            Assert.That(summary, Does.Contain("tool result"));
            Assert.That(UPilotRestartDiagnosticView.NextAction(record), Does.Contain("缩小结果"));
            record.lastBridgeOversizeActualBytes = 0;
            record.lastBridgeOversizeLimitBytes = 0;
            Assert.That(UPilotRestartDiagnosticView.Summary(record), Does.Contain("实际 未知"));
        }

        [Test]
        public void RestartDiagnosticTimeIsSecondPrecisionAndCopyIncludesOffset()
        {
            var zone = TimeZoneInfo.CreateCustomTimeZone("test-8", TimeSpan.FromHours(8), "test", "test");
            var stamp = UPilotRestartDiagnosticView.Time(1780000000123L, false, zone);
            Assert.That(stamp, Does.Match(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$"));
            Assert.That(UPilotRestartDiagnosticView.Time(1780000000123L, true, zone), Is.EqualTo(stamp + " +08:00"));
            Assert.That(UPilotRestartDiagnosticView.Duration(1000, 1500), Is.EqualTo("<1 秒"));
            Assert.That(UPilotRestartDiagnosticView.Duration(1000, 3200), Is.EqualTo("2.2 秒"));
            Assert.That(UPilotRestartDiagnosticView.Duration(1000, 31000), Is.EqualTo("30 秒"));
        }

        [Test]
        public void RestartGateViewDoesNotInferLegacyCompletionFromPhase()
        {
            var record = UPilotServerRestartDiagnostics.CreateRecordForTests("C:/project", 1, "old");
            record.schemaVersion = 1;
            record.phase = "bridge_verified";
            Assert.That(UPilotRestartDiagnosticView.Gates(record), Does.Contain("? 未知 | 新 Bridge 会话验证"));
            Assert.That(UPilotRestartDiagnosticView.Summary(record), Does.Contain("旧版记录"));
            Assert.That(UPilotRestartDiagnosticView.FirstPending(record), Is.EqualTo("未知"));
            record.status = "succeeded";
            record.schemaVersion = 2;
            Assert.That(UPilotRestartDiagnosticView.FirstPending(record), Is.EqualTo("无"));
        }

        [Test]
        public void RestartHistoryRetainsTenTerminalRecordsAndDeduplicatesByOperation()
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-history-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "history.json");
            try
            {
                for (var i = 0; i < 12; i++)
                {
                    var record = UPilotServerRestartDiagnostics.CreateRecordForTests(directory, 1, "old");
                    record.operationId = "restart-" + i;
                    record.status = i % 2 == 0 ? "failed" : "succeeded";
                    record.requestedAtUtcMs = i + 1;
                    Assert.That(UPilotServerRestartDiagnostics.TryArchive(record, path, out var error), Is.True, error);
                }
                var history = UPilotServerRestartDiagnostics.ReadHistoryAt(path, out var warning);
                Assert.That(warning, Is.Empty);
                Assert.That(history.Length, Is.EqualTo(10));
                Assert.That(history[0].operationId, Is.EqualTo("restart-11"));
                history[0].recoveredAtUtcMs = 123;
                Assert.That(UPilotServerRestartDiagnostics.TryArchive(history[0], path, out _), Is.True);
                history = UPilotServerRestartDiagnostics.ReadHistoryAt(path, out _);
                Assert.That(history.Length, Is.EqualTo(10));
                Assert.That(history[0].recoveredAtUtcMs, Is.EqualTo(123));
                File.WriteAllText(path, "{broken");
                Assert.That(UPilotServerRestartDiagnostics.ReadHistoryAt(path, out warning), Is.Empty);
                Assert.That(warning, Is.Not.Empty);
                Assert.That(File.ReadAllText(path), Is.EqualTo("{broken"));
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [Test]
        public void RestartStatusProbeSeparatesLifecycleCancellationFromTimeout()
        {
            Assert.That(UPilotMcpServerManager.ClassifyRestartProbe(new McpServerStatus
            {
                StatusCancellationReason = "domain_reload", ErrorMessage = "A task was canceled"
            }), Is.EqualTo("lifecycle_canceled"));
            Assert.That(UPilotMcpServerManager.ClassifyRestartProbe(new McpServerStatus
            {
                StatusCancellationReason = "timeout", ErrorMessage = "A task was canceled"
            }), Is.EqualTo("request_timeout"));
            Assert.That(UPilotMcpServerManager.ClassifyRestartProbe(new McpServerStatus
            {
                StatusCancellationReason = "timeout",
                ErrorMessage = "A task was canceled",
                EditorObservationAvailable = true,
                EditorObservationStatus = "waiting_editor",
            }), Is.EqualTo("request_timeout"));
        }

        [Test]
        public void RestartRecordsPreserveUnobservedFieldsAcrossOldJsonAndFailedState()
        {
            var path = Path.Combine(Path.GetTempPath(), "upilot-restart-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{\"operationId\":\"old\",\"status\":\"failed\"}");
                Assert.That(UPilotServerRestartDiagnostics.TryLoadRecordForTests(path, out var old, out var error), Is.True, error);
                Assert.That(old.schemaVersion, Is.EqualTo(1));
                Assert.That(UPilotRestartDiagnosticView.FirstPending(old), Is.EqualTo("未知"));
                Assert.That(old.hasIdentityProbe, Is.False);
                Assert.That(old.bridgeStopAttempted, Is.False);
                Assert.That(old.hasBridgeStopConfirmation, Is.False);
                Assert.That(old.hasOldServerExitConfirmation, Is.False);
                old.hasIdentityProbe = true;
                old.candidateCount = 3;
                old.bridgeStopAttempted = true;
                old.hasBridgeStopConfirmation = true;
                old.bridgeStopConfirmed = false;
                old.hasOldServerExitConfirmation = true;
                old.oldServerExitConfirmed = false;
                Assert.That(UPilotServerRestartDiagnostics.TryWriteRecordForTests(old, path, out error), Is.True, error);
                Assert.That(UPilotServerRestartDiagnostics.TryLoadRecordForTests(path, out var restored, out error), Is.True, error);
                Assert.That(restored.hasIdentityProbe && restored.candidateCount == 3 && restored.bridgeStopAttempted, Is.True);
                Assert.That(restored.hasBridgeStopConfirmation && !restored.bridgeStopConfirmed, Is.True);
                Assert.That(restored.hasOldServerExitConfirmation && !restored.oldServerExitConfirmed, Is.True);
                Assert.That(restored.status, Is.EqualTo("failed"));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void RestartCompletesOnlyAfterProcessProjectBridgeAndDeploymentAreVerified()
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
            Assert.That(record.status, Is.EqualTo("running"));
            UPilotServerRestartDiagnostics.RecordDeploymentVerifiedForTests(record);
            Assert.That(record.status, Is.EqualTo("running"), "A Bridge session is not a real round trip.");
            UPilotServerRestartDiagnostics.RecordReadOnlyVerifiedForTests(record);

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
        public void RestartSchemaTwoRoundTripsAndUsesFixedSecondPrecision()
        {
            var zone = TimeZoneInfo.CreateCustomTimeZone("UTC plus eight", TimeSpan.FromHours(8), "UTC+08", "UTC+08");
            var at = new DateTimeOffset(2026, 9, 24, 7, 39, 58, TimeSpan.Zero).ToUnixTimeMilliseconds() + 730;
            Assert.That(UPilotRestartDiagnosticView.Time(at, false, zone), Is.EqualTo("2026-09-24 15:39:58"));
            Assert.That(UPilotRestartDiagnosticView.Time(at, true, zone), Is.EqualTo("2026-09-24 15:39:58 +08:00"));
            Assert.That(UPilotRestartDiagnosticView.Duration(1, 999), Is.EqualTo("<1 秒"));
            Assert.That(UPilotRestartDiagnosticView.Duration(1, 2201), Is.EqualTo("2.2 秒"));
            Assert.That(UPilotRestartDiagnosticView.Duration(1, 30001), Is.EqualTo("30 秒"));
            var record = UPilotServerRestartDiagnostics.CreateRecordForTests("C:/test", 1, "old");
            record.schemaVersion = 2; // Explicitly exercise the retained legacy schema, not the new-record default.
            record.statusProbeCount = 3;
            record.lastBridgeCloseCode = "1009";
            record.lastBridgeOversizeActualBytes = 1300775;
            record.lastBridgeOversizeLimitBytes = 1048576;
            record.gateDiagnostics = new[] { new UPilotRestartGate
                { key = "health", state = "failed", startedAtUtcMs = at, endedAtUtcMs = at + 30000 } };
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
            try
            {
                Assert.That(UPilotServerRestartDiagnostics.TryWriteRecordForTests(record, path, out var error), Is.True, error);
                Assert.That(UPilotServerRestartDiagnostics.TryLoadRecordForTests(path, out var loaded, out error), Is.True, error);
                Assert.That(loaded.schemaVersion, Is.EqualTo(2));
                Assert.That(loaded.statusProbeCount, Is.EqualTo(3));
                Assert.That(loaded.gateDiagnostics[0].startedAtUtcMs, Is.EqualTo(at));
                Assert.That(UPilotRestartDiagnosticView.Gates(loaded).Split('\n').Length, Is.GreaterThanOrEqualTo(11));
                Assert.That(UPilotRestartDiagnosticView.Full(loaded), Does.Contain("1,300,775").Or.Contain("1300775"));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void RestartRecoveryRequiresSameProcessSessionProjectAndSuccessfulReadOnlyProbe()
        {
            var record = UPilotServerRestartDiagnostics.CreateRecordForTests("C:/test", 1, "old");
            record.status = "failed";
            record.errorCode = "restart_verification_timeout";
            record.newProcessId = 42;
            record.newBridgeSessionId = "new";
            Assert.That(UPilotServerRestartDiagnostics.IsEligibleRecovery(record, 42, "new", "C:/test", true, true, true), Is.True);
            Assert.That(record.status, Is.EqualTo("failed"));
            Assert.That(UPilotServerRestartDiagnostics.IsEligibleRecovery(record, 43, "new", "C:/test", true, true, true), Is.False);
            Assert.That(UPilotServerRestartDiagnostics.IsEligibleRecovery(record, 42, "other", "C:/test", true, true, true), Is.False);
            Assert.That(UPilotServerRestartDiagnostics.IsEligibleRecovery(record, 42, "new", "C:/wrong", true, true, true), Is.False);
            Assert.That(UPilotServerRestartDiagnostics.IsEligibleRecovery(record, 42, "new", "C:/test", true, true, false), Is.False);
        }

        [Test]
        public void RestartFailureSummarySeparatesProbeCancellationAndIdentityTimeoutEvidence()
        {
            var record = UPilotServerRestartDiagnostics.CreateRecordForTests("C:/test", 1, "old");
            record.status = "canceled";
            record.statusProbeOutcome = "lifecycle_canceled";
            record.statusProbeCancellationReason = "domain_reload";
            Assert.That(UPilotRestartDiagnosticView.Summary(record), Does.Contain("lifecycle_canceled"));
            Assert.That(UPilotRestartDiagnosticView.NextAction(record), Does.Contain("生命周期"));
            record.status = "failed";
            record.errorCode = "process_identity_timeout";
            record.statusProbeOutcome = "";
            record.hasIdentityProbe = true;
            record.identityProbeElapsedMs = 8000;
            record.candidateCount = 6;
            record.portQueryMs = 2200;
            record.commandLineQueryMs = 5700;
            var full = UPilotRestartDiagnosticView.Full(record);
            Assert.That(full, Does.Contain("8 秒"));
            Assert.That(full, Does.Contain("候选=6"));
            Assert.That(full, Does.Contain("2.2 秒"));
            Assert.That(full, Does.Contain("5.7 秒"));
            Assert.That(UPilotRestartDiagnosticView.NextAction(record), Does.Contain("不要强制结束"));
            record.errorCode = "restart_verification_timeout";
            record.lastBridgeCloseCode = "1009";
            record.lastBridgeOversizeActualBytes = 1300775;
            record.lastBridgeOversizeLimitBytes = 1048576;
            Assert.That(UPilotRestartDiagnosticView.FailureCategory(record), Is.EqualTo("websocket_close_1009"));
            Assert.That(UPilotRestartDiagnosticView.NextAction(record), Does.Contain("缩小结果"));
            record.lastBridgeCloseCode = "";
            Assert.That(UPilotRestartDiagnosticView.FailureCategory(record), Is.EqualTo("payload_oversize"));
        }

        [Test]
        public void RestartHistoryIsBoundedDeduplicatedAndCorruptionPreserved()
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-history-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "server-restart-history.json");
            try
            {
                for (var i = 0; i < 12; i++)
                {
                    var record = UPilotServerRestartDiagnostics.CreateRecordForTests(directory, 1, "old");
                    record.operationId = "id-" + i;
                    record.requestedAtUtcMs = i + 1;
                    record.status = i % 2 == 0 ? "failed" : "succeeded";
                    Assert.That(UPilotServerRestartDiagnostics.TryArchive(record, path, out var error), Is.True, error);
                }
                var history = UPilotServerRestartDiagnostics.ReadHistoryAt(path, out var warning);
                Assert.That(warning, Is.Empty);
                Assert.That(history.Length, Is.EqualTo(10));
                Assert.That(history[0].operationId, Is.EqualTo("id-11"));
                history[0].recoveredAtUtcMs = 123;
                Assert.That(UPilotServerRestartDiagnostics.TryArchive(history[0], path, out var error2), Is.True, error2);
                Assert.That(UPilotServerRestartDiagnostics.ReadHistoryAt(path, out warning).Length, Is.EqualTo(10));
                File.WriteAllText(path, "{broken");
                Assert.That(UPilotServerRestartDiagnostics.ReadHistoryAt(path, out warning), Is.Empty);
                Assert.That(warning, Is.Not.Empty);
                Assert.That(UPilotServerRestartDiagnostics.TryArchive(history[0], path, out error2), Is.False);
                Assert.That(File.Exists(path + ".corrupt"), Is.True);
                Assert.That(File.ReadAllText(path), Is.EqualTo("{broken"));
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test]
        public void LoopbackStatusProbeBypassesProxyAndDoesNotReuseServerConnections()
        {
            using var handler = UPilotMcpServerManager.CreateLoopbackStatusHandler();
            Assert.That(handler.UseProxy, Is.False);

            using var request = UPilotMcpServerManager.CreateLoopbackStatusRequest(
                "http://127.0.0.1:8011/health");
            Assert.That(request.Method, Is.EqualTo(System.Net.Http.HttpMethod.Get));
            Assert.That(request.Headers.ConnectionClose, Is.True);
        }

        [Test]
        public void HealthProjectIdentityUsesConfiguredPathBeforeBridgeSessionIsAvailable()
        {
            var json = "{\"configured_project_path\":\"F:/xclient2\",\"project_path\":\"\"}";
            Assert.That(UPilotMcpServerManager.ResolveHealthProjectPath(json), Is.EqualTo("F:/xclient2"));

            var legacyJson = "{\"project_path\":\"F:/legacy-project\"}";
            Assert.That(UPilotMcpServerManager.ResolveHealthProjectPath(legacyJson), Is.EqualTo("F:/legacy-project"));
        }

        [Test]
        public void EditorHealthObservationUsesFastCadenceOnlyWhileWaiting()
        {
            Assert.That(UPilotMcpServerManager.GetEditorHealthObservationIntervalMs(default), Is.EqualTo(2000));
            Assert.That(UPilotMcpServerManager.GetEditorHealthObservationIntervalMs(new McpServerStatus
            {
                EditorObservationAvailable = true,
                EditorObservationStatus = "responsive",
            }), Is.EqualTo(2000));
            Assert.That(UPilotMcpServerManager.GetEditorHealthObservationIntervalMs(new McpServerStatus
            {
                EditorObservationAvailable = true,
                EditorObservationStatus = "unknown",
            }), Is.EqualTo(2000));
            Assert.That(UPilotMcpServerManager.GetEditorHealthObservationIntervalMs(new McpServerStatus
            {
                EditorObservationAvailable = true,
                EditorObservationStatus = "waiting_editor",
            }), Is.EqualTo(1000));
            Assert.That(UPilotMcpServerManager.ShouldRequestFullStatusRefresh(new McpServerStatus
            {
                EditorObservationAvailable = true,
                EditorObservationStatus = "waiting_editor",
            }), Is.False);
            Assert.That(UPilotMcpServerManager.ShouldRequestFullStatusRefresh(new McpServerStatus
            {
                EditorObservationAvailable = true,
                EditorObservationStatus = "responsive",
            }), Is.True);
        }

        [Test]
        public void EditorHealthObservationRequiresExactServerAndEditorIdentity()
        {
            var expected = new McpServerStatus
            {
                HealthServerProcessId = 42,
                Health = new UPilotServerHealth { server_instance_id = "server-a" },
                EditorObservationSessionId = "session-a",
                EditorObservationProducerEpoch = "epoch-a",
                EditorObservationDomainGeneration = 3,
            };
            var health = new UPilotServerHealth
            {
                server_pid = 42,
                server_instance_id = "server-a",
            };
            var observation = new UPilotEditorObservation
            {
                status = "waiting_editor",
                session_id = "session-a",
                producer_epoch = "epoch-a",
                domain_generation = 3,
            };

            Assert.That(UPilotMcpServerManager.MatchesEditorObservationIdentity(expected, health, observation), Is.True);
            health.server_pid = 43;
            Assert.That(UPilotMcpServerManager.MatchesEditorObservationIdentity(expected, health, observation), Is.False);
            health.server_pid = 42;
            observation.domain_generation = 4;
            Assert.That(UPilotMcpServerManager.MatchesEditorObservationIdentity(expected, health, observation), Is.False);
        }

        [Test]
        public void EditorHealthObservationMappingKeepsRecentStallAndClearsLegacyResponses()
        {
            var status = new McpServerStatus();
            var recent = new UPilotEditorStallSummary
            {
                outcome = "recovered",
                duration_ms = 8000,
            };
            UPilotMcpServerManager.ApplyEditorObservation(ref status, new UPilotEditorObservation
            {
                status = "waiting_editor",
                reason = "main_thread_pump_stale",
                observed_at_ms = 10000,
                pump_age_ms = 8000,
                heartbeat_age_ms = 500,
                waiting_since_ms = 9000,
                waiting_duration_ms = 1000,
                observation_count = 2,
                session_id = "session-a",
                producer_epoch = "epoch-a",
                domain_generation = 3,
                main_thread_queue_depth = 4,
                last_dequeued_command_id = "command-a",
                recent_stall = recent,
            });

            Assert.That(status.EditorObservationAvailable, Is.True);
            Assert.That(status.EditorObservationStatus, Is.EqualTo("waiting_editor"));
            Assert.That(status.EditorObservationPumpAgeMs, Is.EqualTo(8000));
            Assert.That(status.EditorObservationCount, Is.EqualTo(2));
            Assert.That(status.RecentEditorStall, Is.SameAs(recent));

            UPilotMcpServerManager.ApplyEditorObservation(ref status, null);
            Assert.That(status.EditorObservationAvailable, Is.False);
            Assert.That(status.EditorObservationStatus, Is.Empty);
            Assert.That(status.RecentEditorStall, Is.Null);
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
            Assert.That(UPilotMcpServerManager.ClassifyRestartProbe(new McpServerStatus
            {
                StatusFailureStage = "health_query", ErrorMessage = "状态获取失败：/health HTTP 503"
            }), Is.EqualTo("http_status_failure"));
            Assert.That(UPilotMcpServerManager.ClassifyRestartProbe(new McpServerStatus
            {
                StatusFailureStage = "health_query", ErrorMessage = "/health: HttpRequestException: refused"
            }), Is.EqualTo("connect_failure"));
            Assert.That(UPilotMcpServerManager.ClassifyRestartProbe(new McpServerStatus
            {
                StatusFailureStage = "health_query", ErrorMessage = "/health: JsonException: broken"
            }), Is.EqualTo("malformed_response"));
        }
        [TestCase(21000, false)]
        [TestCase(121000, false)]
        [TestCase(599000, false)]
        [TestCase(600000, true)]
        public void RestartDeadlineIsAcceptanceBasedAcrossSessionAndPhaseChanges(long elapsed, bool expired)
        {
            var r = new UPilotServerRestartRecord { requestedAtUtcMs = 1000, deadlineAtUtcMs = 601000 };
            r.newProcessStartedAtUtcMs = 150000;
            r.newBridgeSessionId = "replacement";
            r.phase = "bridge_verified";
            var reloaded = JsonUtility.FromJson<UPilotServerRestartRecord>(JsonUtility.ToJson(r));
            Assert.That(UPilotServerRestartDiagnostics.Deadline(reloaded), Is.EqualTo(601000));
            Assert.That(UPilotServerRestartDiagnostics.HasExpired(reloaded, 1000 + elapsed), Is.EqualTo(expired));
            Assert.That(UPilotQuickStart.EvaluateRestartRecord(reloaded, true, true).State, Is.EqualTo(UPilotMainState.Restarting),
                "UI must wait for authoritative terminal record, even if sockets are ready or the wall clock has expired.");
        }

        [Test]
        public void LegacyOrdinaryRecordKeepsOriginalVerificationBudget()
        {
            var legacy = new UPilotServerRestartRecord { requestedAtUtcMs = 1000, newProcessStartedAtUtcMs = 5000 };
            Assert.That(UPilotServerRestartDiagnostics.Deadline(legacy), Is.EqualTo(25000));
            legacy.maintenanceDeadlineUtcMs = 121000;
            Assert.That(UPilotServerRestartDiagnostics.Deadline(legacy), Is.EqualTo(121000));
        }

        [Test]
        public void LateSuccessAndLaterGateTimeoutCannotBecomeHealthTimeout()
        {
            var r = new UPilotServerRestartRecord { requestedAtUtcMs = 1000, deadlineAtUtcMs = 601000,
                healthVerified = true, projectIdentityVerified = true, bridgeVerified = true, deploymentVerified = true,
                readOnlyVerified = true, gateDiagnostics = UPilotServerRestartDiagnostics.GateKeys.Select(key =>
                    new UPilotRestartGate { key = key, state = key == "readonly" ? "running" : "passed" }).ToArray() };
            UPilotServerRestartDiagnostics.TryComplete(r, 601000);
            Assert.That(r.status, Is.EqualTo("failed"));
            Assert.That(r.error, Does.Not.Contain(UPilotServerRestartDiagnostics.HealthTimeoutMessage));
            Assert.That(r.error, Does.Contain("只读"));
            var original = r.error;
            UPilotServerRestartDiagnostics.TryComplete(r, 601001);
            Assert.That(r.error, Is.EqualTo(original));
        }

        [Test]
        public void HealthSuccessWithMissingOwnershipKeepsIdentityAsPendingGate()
        {
            var r = new UPilotServerRestartRecord { requestedAtUtcMs = 1000, deadlineAtUtcMs = 601000,
                gateDiagnostics = UPilotServerRestartDiagnostics.GateKeys.Select(key =>
                    new UPilotRestartGate { key = key, state = Array.IndexOf(UPilotServerRestartDiagnostics.GateKeys, key) < 6 ? "passed" : "pending" }).ToArray() };
            UPilotServerRestartDiagnostics.ApplyProbeOutcome(r, "process_identity_timeout", "process_identity", "", "ownership unknown");
            Assert.That(UPilotServerRestartDiagnostics.FirstUnpassedGate(r), Is.EqualTo("identity"));
            UPilotServerRestartDiagnostics.ApplyProbeOutcome(r, "request_timeout", "health_query", "", "transient");
            Assert.That(UPilotServerRestartDiagnostics.FirstUnpassedGate(r), Is.EqualTo("identity"));
            Assert.That(UPilotServerRestartDiagnostics.TimeoutMessage(r, 601000), Does.Not.Contain("/health 仍未验证通过"));
            Assert.That(UPilotMcpServerManager.ClassifyRestartProbe(new McpServerStatus {
                StatusFailureStage = "health_query", ErrorMessage = "/health: TimeoutException: body timed out" }), Is.EqualTo("request_timeout"));
        }

        [Test]
        public async Task LifecycleCancellationEndsHungHealthRequestWithoutApplyingLateResponse()
        {
            var body = new StalledHealthBody();
            using var cancel = new System.Threading.CancellationTokenSource();
            var handler = new RestartHealthHandler { Response = _ =>
                new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = body } };
            using var client = new System.Net.Http.HttpClient(handler);
            var request = UPilotMcpServerManager.SendBoundedStatusAsync(client, "http://127.0.0.1:1/health",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600000, cancel.Token);
            cancel.Cancel();
            try { using var response = await request; Assert.Fail("Canceled observation must not accept a response."); }
            catch (OperationCanceledException) { }
            finally { body.Release.TrySetResult(true); }
            Assert.That(handler.Calls, Is.EqualTo(1));
        }

        private sealed class RestartHealthHandler : System.Net.Http.HttpMessageHandler
        {
            internal int Calls;
            internal Func<int, System.Net.Http.HttpResponseMessage> Response;
            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,
                System.Threading.CancellationToken token)
            {
                Assert.That(request.RequestUri.AbsolutePath, Is.EqualTo("/health"), "/stats is optional and must never delay restart.");
                return Task.FromResult(Response(++Calls));
            }
        }

        private sealed class StalledHealthBody : System.Net.Http.HttpContent
        {
            internal readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>();
            protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext context) => Release.Task;
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
        }

        [Test]
        public async Task HealthRetriesTransport503AndInvalidJsonWithoutRequestingStats()
        {
            var handler = new RestartHealthHandler { Response = n =>
            {
                if (n == 1) throw new System.Net.Http.HttpRequestException("fixture refused", new IOException("socket fixture"));
                if (n == 2) throw new TaskCanceledException("fixture timeout");
                return new System.Net.Http.HttpResponseMessage(n == 3 ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK)
                { Content = new System.Net.Http.StringContent(n == 4 ? "not JSON" :
                    "{\"server_version\":\"fixture\",\"server_pid\":42,\"configured_project_path\":\"fixture\"}") };
            } };
            using var client = new System.Net.Http.HttpClient(handler);
            var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600000;
            for (var i = 0; i < 4; i++)
            {
                var failed = await UPilotMcpServerManager.GetRestartHealthAsync(client, 1, deadline);
                Assert.That(failed.HealthEndpointResponded, Is.False);
                Assert.That(failed.ErrorMessage, Is.Not.Empty);
            }
            var recovered = await UPilotMcpServerManager.GetRestartHealthAsync(client, 1, deadline);
            Assert.That(recovered.HealthEndpointResponded, Is.True);
            Assert.That(recovered.HealthServerProcessId, Is.EqualTo(42));
            Assert.That(handler.Calls, Is.EqualTo(5));
            Assert.That(UPilotMcpServerManager.RestartProbeIntervalMs, Is.EqualTo(2000));
        }

        [Test]
        public async Task HungResponseBodyIsBoundedAndLateResponseIsNotApplied()
        {
            var body = new StalledHealthBody();
            using var client = new System.Net.Http.HttpClient(new RestartHealthHandler { Response = _ =>
                new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = body } });
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                try
                {
                    using var result = await UPilotMcpServerManager.SendBoundedStatusAsync(client, "http://127.0.0.1:1/health",
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60, default, 2000);
                    Assert.Fail("Hung body must not pass the health gate.");
                }
                catch (TimeoutException) { }
                catch (OperationCanceledException) { }
                Assert.That(watch.ElapsedMilliseconds, Is.LessThan(1500));
            }
            finally { body.Release.TrySetResult(true); }
        }

        [Test]
        public void ProbeErrorsKeepBoundedRedactedInnerExceptionEvidence()
        {
            var text = UPilotMcpServerManager.DescribeProbeException(new IOException("token=secret fixture",
                new InvalidOperationException("inner " + new string('x', 3000))));
            Assert.That(text, Does.Contain("IOException"));
            Assert.That(text, Does.Contain("InvalidOperationException"));
            Assert.That(text, Does.Not.Contain("token=secret"));
            Assert.That(text.Length, Is.LessThanOrEqualTo(1025));
        }

    }
}
