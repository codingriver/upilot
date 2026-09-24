using System;
using System.IO;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests
{
    public class UPilotStartupDiagnosticsTests
    {
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
    }
}
