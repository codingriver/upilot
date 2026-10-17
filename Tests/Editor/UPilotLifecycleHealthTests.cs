using System;
using System.Collections;
using UnityEngine.TestTools;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;

namespace CodingRiver.UPilot.Tests
{
    public class UPilotLifecycleHealthTests
    {
        private static McpServerStatus Waiting(string phase, long duration)
        {
            var status = new McpServerStatus
            {
                IsRunning = true, HttpPortListening = true, WsPortListening = true,
                ProcessOwnership = McpProcessOwnership.CurrentUPilot, ProcessId = 42,
                HealthEndpointResponded = true, StatusQueryCompleted = true,
                Health = new UPilotServerHealth { server_pid = 42, server_instance_id = "server-a",
                    configured_project_path = UPilotProjectConfig.ProjectRoot, bridge_session_id = "session-a" },
            };
            UPilotMcpServerManager.ApplyEditorObservation(ref status, new UPilotEditorObservation
            {
                status = "waiting_lifecycle", lifecycle_phase = phase, lifecycle_id = "lifecycle-a",
                lifecycle_started_at_ms = 100000, waiting_duration_ms = duration,
                session_id = "session-a", producer_epoch = "epoch-a", domain_generation = 1,
                observed_at_ms = 100000 + duration, heartbeat_age_ms = duration, pump_age_ms = duration,
            });
            return status;
        }

        [TestCase("User")]
        [TestCase("EditorShutdown")]
        public void CancelingRestartEndsLifecycleWithoutReportingAnError(string origin)
        {
            var type = typeof(UPilotMcpServerManager);
            var manager = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            string operationId = "cancel-restart-" + Guid.NewGuid().ToString("N");
            type.GetField("_statusLock", fields).SetValue(manager, new object());
            type.GetField("_restartOperationId", fields).SetValue(manager, operationId);
            type.GetField("_lifecycleOperationId", fields).SetValue(manager, operationId);
            type.GetField("_lifecyclePhase", fields).SetValue(manager, UPilotServerLifecyclePhase.Verifying);
            type.GetField("_lifecycleGeneration", fields).SetValue(manager, 1L);
            type.GetField("_lifecycleStartedAtUtcMs", fields).SetValue(manager, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            try
            {
                LogAssert.Expect(UnityEngine.LogType.Log,
                    new System.Text.RegularExpressions.Regex("event=server_lifecycle_phase[\\s\\S]*phase=None[\\s\\S]*operationId=" + operationId));
                type.GetMethod("CancelPendingRestart", fields).Invoke(manager, new object[]
                    { false, Enum.Parse(typeof(UPilotServerStopOrigin), origin), "", null, 0L, 0, 0L });
                Assert.That(type.GetField("_lifecyclePhase", fields).GetValue(manager), Is.EqualTo(UPilotServerLifecyclePhase.None));
                Assert.That(type.GetField("_restartOperationId", fields).GetValue(manager), Is.Empty);
                Assert.That(((McpServerStatus)type.GetField("_cachedStatus", fields).GetValue(manager)).ErrorMessage, Is.EqualTo("restart_canceled"));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                (type.GetField("_lifecycleQueryCancellation", fields).GetValue(manager) as CancellationTokenSource)?.Dispose();
                Logger.CloseDiagnostics(operationId);
            }
        }

        [TestCase("enteringPlay", 120000)]
        [TestCase("enteringPlay", 180000)]
        [TestCase("enteringPlay", 600000)]
        [TestCase("exitingPlay", 600000)]
        [TestCase("compiling", 600000)]
        [TestCase("domain_reload", 600000)]
        public void LongLifecycleWaitNeverBecomesRepairEvenWithDisconnectedBridge(string phase, long elapsed)
        {
            var status = Waiting(phase, elapsed);
            status.ErrorMessage = "fixture request timeout";
            status.FailureKind = McpStatusFailureKind.Transient;
            status.HealthEndpointResponded = false;
            var bridge = new BridgeStatus { IsStarted = true };
            Assert.That(UPilotHealthAssessment.Classify(bridge, status), Is.EqualTo(UPilotHealthDisposition.WaitingLifecycle));
            var view = UPilotQuickStart.EvaluateServiceState(bridge, status);
            Assert.That(view.State, Is.EqualTo(UPilotMainState.CheckingStatus));
            Assert.That(view.Title, Does.Contain("Unity 正在"));
            Assert.That(view.Message, Does.Contain("未执行自动重启"));
            Assert.That(UPilotHealthAssessment.ObservationStatus(bridge, status), Is.EqualTo("waiting_lifecycle"));
            Assert.That(UPilotHealthAssessment.CanVerifyRecovery(bridge, status, status.EditorObservationObservedAtUtcMs), Is.False);
            Assert.That(UPilotMcpServerManager.ShouldRequestFullStatusRefresh(status), Is.False);
        }

        [TestCase("auth")]
        [TestCase("foreign")]
        [TestCase("unknown-error")]
        [TestCase("process-exit")]
        public void ConfirmedFailuresCannotBeHiddenByLifecycle(string error)
        {
            var status = Waiting("enteringPlay", 600000);
            var bridge = new BridgeStatus { IsStarted = true };
            if (error == "auth") bridge.AuthenticationError = "token rejected";
            if (error == "foreign") status.ProcessOwnership = McpProcessOwnership.Foreign;
            if (error == "unknown-error") status.ErrorMessage = "unclassified failure";
            if (error == "process-exit") status.EditorObservationReason = "unity_process_exited";
            Assert.That(UPilotHealthAssessment.Classify(bridge, status), Is.EqualTo(UPilotHealthDisposition.ConfirmedFailure));
            Assert.That(UPilotQuickStart.EvaluateServiceState(bridge, status).State, Is.EqualTo(UPilotMainState.NeedsRepair));
        }

        [Test]
        public void ConfirmedHttpFailureEscapesLifecycleLightweightCache()
        {
            var status = Waiting("enteringPlay", 180000);
            status.HealthEndpointResponded = false;
            status.FailureKind = McpStatusFailureKind.Confirmed;
            status.ErrorMessage = "UnauthorizedAccessException: /health HTTP 401";
            Assert.That(UPilotMcpServerManager.ShouldRequestFullStatusRefresh(status), Is.True);
            Assert.That(UPilotHealthAssessment.Classify(default, status), Is.EqualTo(UPilotHealthDisposition.ConfirmedFailure));
            status.FailureKind = McpStatusFailureKind.Transient;
            status.ErrorMessage = "health timeout";
            Assert.That(UPilotMcpServerManager.ShouldRequestFullStatusRefresh(status), Is.False);
        }

        [Test]
        public void ForeignEndpointAndMissingLifecycleEvidenceAreNotTrustedWaits()
        {
            var status = Waiting("compiling", 180000);
            status.Health.configured_project_path = System.IO.Path.Combine(UPilotProjectConfig.ProjectRoot, "other-project");
            Assert.That(UPilotHealthAssessment.HasLifecycleEvidence(status), Is.False);
            status.Health.configured_project_path = UPilotProjectConfig.ProjectRoot;
            status.Health.server_pid = 43;
            Assert.That(UPilotHealthAssessment.HasLifecycleEvidence(status), Is.False);
            status.Health.server_pid = 42;
            status.EditorObservationLifecycleId = "";
            Assert.That(UPilotHealthAssessment.Classify(default, status), Is.EqualTo(UPilotHealthDisposition.Unconfirmed));
        }

        [Test]
        public void RecoveryRequiresFreshResponsiveSameIdentityAndHandshake()
        {
            var status = Waiting("exitingPlay", 180000);
            var bridge = new BridgeStatus { IsStarted = true, IsWsOpen = true, IsAuthenticated = true, SessionId = "session-a" };
            Assert.That(UPilotHealthAssessment.CanVerifyRecovery(bridge, status, status.EditorObservationObservedAtUtcMs), Is.False);
            status.EditorObservationStatus = "responsive";
            status.EditorObservationHeartbeatAgeMs = 1000;
            status.EditorObservationPumpAgeMs = 100;
            Assert.That(UPilotHealthAssessment.CanVerifyRecovery(bridge, status, status.EditorObservationObservedAtUtcMs), Is.True);
            status.EditorObservationSessionId = "old-session";
            Assert.That(UPilotHealthAssessment.CanVerifyRecovery(bridge, status, status.EditorObservationObservedAtUtcMs), Is.False);
            status.EditorObservationSessionId = "session-a";
            status.EditorObservationStatus = "unknown";
            Assert.That(UPilotHealthAssessment.CanVerifyRecovery(bridge, status, status.EditorObservationObservedAtUtcMs), Is.False);
            Assert.That(UPilotMcpServerManager.ShouldRequestFullStatusRefresh(status), Is.True);
        }

        [Test]
        public void TimeoutIsUnconfirmedButMalformedIdentityIsConfirmed()
        {
            Assert.That(UPilotMcpServerManager.ClassifyStatusException(new TimeoutException()), Is.EqualTo(McpStatusFailureKind.Transient));
            Assert.That(UPilotMcpServerManager.ClassifyStatusException(new TaskCanceledException()), Is.EqualTo(McpStatusFailureKind.Transient));
            Assert.That(UPilotMcpServerManager.ClassifyStatusException(new System.IO.InvalidDataException()), Is.EqualTo(McpStatusFailureKind.Confirmed));
            var status = Waiting("compiling", 1000);
            status.EditorObservationStatus = "unknown";
            status.FailureKind = McpStatusFailureKind.Transient;
            status.ErrorMessage = "HTTP timeout";
            Assert.That(UPilotQuickStart.EvaluateServiceState(default, status).State, Is.EqualTo(UPilotMainState.CheckingStatus));
        }

        [Test]
        public void TransitionHistoryFromOldProcessOrReusedPidCannotAuthorizeWait()
        {
            var record = UPilotPlayModeTransitions.Build("edit", "enteringPlay", "session-a", 100000, null);
            Assert.That(UPilotPlayModeTransitions.IsCurrentProcessRecord(record), Is.True);
            record.processCreatedAt--;
            Assert.That(UPilotPlayModeTransitions.IsCurrentProcessRecord(record), Is.False);
            record.processCreatedAt++;
            record.projectPath = System.IO.Path.Combine(UPilotProjectConfig.ProjectRoot, "foreign");
            Assert.That(UPilotPlayModeTransitions.IsCurrentProcessRecord(record), Is.False);
        }

        [Test]
        public void MissingHeartbeatAndPumpEvidenceRemainUnknown()
        {
            var status = Waiting("compiling", 180000);
            UPilotMcpServerManager.ApplyEditorObservation(ref status, new UPilotEditorObservation
                { status = "responsive", session_id = "session-a", observed_at_ms = 280000 });
            Assert.That(status.EditorObservationPumpAgeMs, Is.EqualTo(-1));
            Assert.That(status.EditorObservationHeartbeatAgeMs, Is.EqualTo(-1));
            var bridge = new BridgeStatus { IsWsOpen = true, IsAuthenticated = true, SessionId = "session-a" };
            Assert.That(UPilotHealthAssessment.CanVerifyRecovery(bridge, status, 280000), Is.False);
        }

        [Test]
        public void CachedObservationAgesWithoutInventingFreshEvidence()
        {
            var status = Waiting("enteringPlay", 120000);
            UPilotMcpServerManager.AgeEditorObservation(ref status, 280000);
            Assert.That(status.EditorObservationPumpAgeMs, Is.EqualTo(180000));
            Assert.That(status.EditorObservationHeartbeatAgeMs, Is.EqualTo(180000));
            Assert.That(status.EditorObservationWaitingDurationMs, Is.EqualTo(180000));
            Assert.That(status.EditorObservationStatus, Is.EqualTo("waiting_lifecycle"));
            Assert.That(status.HealthEndpointResponded, Is.True, "Aging must not alter the recorded HTTP result");
        }

        [Test]
        public void LongObservationDoesNotConfirmFailureAndTrustedLifecycleRemovesTimeoutIssue()
        {
            WithIsolatedHealthUi(() =>
            {
                var status = Waiting("compiling", 600000);
                var bridge = new BridgeStatus { IsStarted = true };
                var fields = BindingFlags.Static | BindingFlags.NonPublic;
                typeof(UPilotDeploymentDiagnostics).GetField("_checkingSince", fields).SetValue(null,
                    System.Diagnostics.Stopwatch.GetTimestamp() - 600 * System.Diagnostics.Stopwatch.Frequency);
                var issues = DiagnosticIssues();
                issues["timeout"] = new UPilotDeploymentIssue { Code = "timeout", Confirmed = false };
                UPilotDeploymentDiagnostics.Observe(bridge, status);
                Assert.That(issues.ContainsKey("timeout"), Is.False);
                status.EditorObservationStatus = "unknown";
                status.HealthEndpointResponded = false;
                status.FailureKind = McpStatusFailureKind.Transient;
                status.ErrorMessage = "HTTP timeout";
                typeof(UPilotDeploymentDiagnostics).GetField("_checkingSince", fields).SetValue(null,
                    System.Diagnostics.Stopwatch.GetTimestamp() - 600 * System.Diagnostics.Stopwatch.Frequency);
                UPilotDeploymentDiagnostics.Observe(bridge, status);
                Assert.That(issues["timeout"].Confirmed, Is.False);
                Assert.That(UPilotQuickStart.EvaluateServiceState(bridge, status).State, Is.EqualTo(UPilotMainState.CheckingStatus));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OnlyVerifiedRecoveryClearsTransientUiWithoutRewritingRestartHistory(bool valid)
        {
            WithIsolatedHealthUi(() =>
            {
                var status = Waiting("enteringPlay", 180000);
                status.EditorObservationStatus = "responsive";
                status.EditorObservationLifecyclePhase = "verifying_recovery";
                status.EditorObservationObservedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                status.EditorObservationHeartbeatAgeMs = 100;
                status.EditorObservationPumpAgeMs = 100;
                PopulateDeploymentIdentity(status.Health);
                status.Identity = status.Health;
                status.IdentityAvailable = status.IdentityIdentifiesUPilot = true;
                status.IdentityServerProcessId = status.Health.server_pid;
                status.IdentityProjectPath = status.Health.configured_project_path;
                var bridge = new BridgeStatus { IsStarted = true, IsWsOpen = true, IsAuthenticated = true, SessionId = "session-a" };
                var prefix = typeof(UPilotQuickStart);
                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                var pendingKey = (string)prefix.GetProperty("HealthRecoveryKey", flags).GetValue(null);
                var failureKey = (string)prefix.GetProperty("FailureKey", flags).GetValue(null);
                var autoKey = (string)prefix.GetProperty("AutoAttemptKey", flags).GetValue(null);
                var generation = (int)prefix.GetField("_repairGeneration", flags).GetValue(null);
                const string token = "legacy-observation-token";
                SessionState.SetString(pendingKey, token);
                EditorPrefs.SetString(failureKey, "old health timeout");
                EditorPrefs.SetBool(autoKey, true);
                DiagnosticIssues()["timeout"] = new UPilotDeploymentIssue { Code = "timeout", Confirmed = false };
                var record = UPilotServerRestartDiagnostics.Current;
                var historicalStatus = record?.status;
                var historicalError = record?.error;
                var latest = status;
                if (!valid)
                {
                    latest.Identity = UnityEngine.JsonUtility.FromJson<UPilotServerHealth>(UnityEngine.JsonUtility.ToJson(status.Identity));
                    latest.Identity.bridge_session_id = "old-session";
                }
                Assert.That(UPilotQuickStart.CompleteObservedRecovery(status, bridge.SessionId, generation, token, bridge, latest), Is.EqualTo(valid));
                Assert.That(SessionState.GetString(pendingKey, ""), Is.EqualTo(valid ? "" : token));
                Assert.That(EditorPrefs.HasKey(failureKey), Is.EqualTo(!valid));
                Assert.That(EditorPrefs.GetBool(autoKey), Is.True, "Observations never rearm automatic repair.");
                if (valid) Assert.That(DiagnosticIssues().Values.Any(issue => !issue.Confirmed), Is.False);
                Assert.That(record?.status, Is.EqualTo(historicalStatus));
                Assert.That(record?.error, Is.EqualTo(historicalError));
            });
        }

        private static McpServerStatus Responsive(long now)
        {
            var status = Waiting("compiling", 1000);
            status.LastSuccessfulStatusAtUtcMs = now;
            status.EditorObservationObservedAtUtcMs = now;
            status.EditorObservationStatus = "responsive";
            status.EditorObservationHeartbeatAgeMs = 100;
            status.EditorObservationPumpAgeMs = 100;
            return status;
        }

        [Test]
        public void FailedExistingRecoveryNeedsVerifiedIdentityAndKeepsStartupReplayBlocked()
        {
            WithIsolatedHealthUi(() =>
            {
                var manager = FailedRecoveryManager(out var record);
                var status = Responsive(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                PopulateDeploymentIdentity(status.Health);
                var bridge = new BridgeStatus { IsStarted = true, IsWsOpen = true, IsAuthenticated = true, SessionId = "session-a" };
                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                var autoKey = (string)typeof(UPilotQuickStart).GetProperty("AutoAttemptKey", flags).GetValue(null);
                var generation = (int)typeof(UPilotQuickStart).GetField("_repairGeneration", flags).GetValue(null);
                EditorPrefs.SetBool(autoKey, true);
                Assert.That(UPilotQuickStart.EvaluateExistingServiceRecoveryFailure(manager, bridge, status)?.State,
                    Is.EqualTo(UPilotMainState.NeedsRepair));
                Assert.That(UPilotQuickStart.CanObserveFailedExistingServiceRecovery(manager), Is.True);

                Assert.That(UPilotQuickStart.CompleteObservedRecovery(status, bridge.SessionId, generation, "",
                    bridge, status, manager, record.id), Is.True);
                Assert.That(UPilotQuickStart.EvaluateExistingServiceRecoveryFailure(manager, bridge, status), Is.Null);
                Assert.That(record.status, Is.EqualTo("failed"));
                Assert.That(record.error, Is.EqualTo("historical cleanup failure"));
                Assert.That(manager.CanAutomaticallyStartServer, Is.False);
                Assert.That(manager.CanAutomaticallyRepairDeployment, Is.False);
                Assert.That(EditorPrefs.GetBool(autoKey, false), Is.True,
                    "Read-only verification must not reopen automatic repair permission.");
            });
        }

        [TestCase("recovery")]
        [TestCase("pid")]
        [TestCase("instance")]
        [TestCase("session")]
        [TestCase("generation")]
        [TestCase("observation")]
        public void ChangedIdentityOrObservationRejectsFailedRecoveryVerification(string changed)
        {
            WithIsolatedHealthUi(() =>
            {
                var manager = FailedRecoveryManager(out var record);
                var expected = Responsive(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                PopulateDeploymentIdentity(expected.Health);
                var latest = Responsive(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                PopulateDeploymentIdentity(latest.Health);
                var bridge = new BridgeStatus { IsStarted = true, IsWsOpen = true, IsAuthenticated = true, SessionId = "session-a" };
                var generation = (int)typeof(UPilotQuickStart).GetField("_repairGeneration",
                    BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                var originalRecoveryId = record.id;
                ChangeRecoveryIdentity(changed, record, ref bridge, ref latest);
                if (changed == "generation") ++generation;
                if (changed == "observation") latest.EditorObservationObservedAtUtcMs = 1;
                Assert.That(UPilotQuickStart.CompleteObservedRecovery(expected, "session-a", generation, "",
                    bridge, latest, manager, originalRecoveryId), Is.False, changed);
                Assert.That(UPilotQuickStart.EvaluateExistingServiceRecoveryFailure(manager, bridge, latest)?.State,
                    Is.EqualTo(UPilotMainState.NeedsRepair));
                Assert.That(record.status, Is.EqualTo("failed"));
            });
        }

        [TestCase("recovery")]
        [TestCase("pid")]
        [TestCase("instance")]
        [TestCase("session")]
        [TestCase("project")]
        public void VerifiedFailureMarkerDoesNotApplyToAnotherCurrentIdentity(string changed)
        {
            WithIsolatedHealthUi(() =>
            {
                var manager = FailedRecoveryManager(out var record);
                var status = Responsive(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                PopulateDeploymentIdentity(status.Health);
                var bridge = new BridgeStatus { IsStarted = true, IsWsOpen = true, IsAuthenticated = true, SessionId = "session-a" };
                var generation = (int)typeof(UPilotQuickStart).GetField("_repairGeneration",
                    BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                Assert.That(UPilotQuickStart.CompleteObservedRecovery(status, "session-a", generation, "",
                    bridge, status, manager, record.id), Is.True);
                ChangeRecoveryIdentity(changed, record, ref bridge, ref status);
                Assert.That(UPilotQuickStart.EvaluateExistingServiceRecoveryFailure(manager, bridge, status)?.State,
                    Is.EqualTo(UPilotMainState.NeedsRepair), changed);
                Assert.That(record.status, Is.EqualTo("failed"));
            });
        }

        private static UPilotMcpServerManager FailedRecoveryManager(out UPilotMcpServerManager.ExistingServiceRecoveryRecord record)
        {
            var type = typeof(UPilotMcpServerManager);
            var manager = (UPilotMcpServerManager)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            type.GetField("_statusLock", flags).SetValue(manager, new object());
            type.GetField("_initialServiceRecoveryCompleted", flags).SetValue(manager, true);
            record = new UPilotMcpServerManager.ExistingServiceRecoveryRecord {
                id = "ui-recovery-" + Guid.NewGuid().ToString("N"), status = "failed", error = "historical cleanup failure",
            };
            type.GetField("_existingServiceRecovery", flags).SetValue(manager, record);
            manager.StopDiscoveryOverride = (_, _, _) => throw new InvalidOperationException("UI observation cannot stop a service.");
            manager.StopTerminationOverride = _ => throw new InvalidOperationException("UI observation cannot terminate a service.");
            manager.ExistingServiceReplacementStartOverride = _ => throw new InvalidOperationException("UI observation cannot start a service.");
            return manager;
        }

        private static void ChangeRecoveryIdentity(string changed, UPilotMcpServerManager.ExistingServiceRecoveryRecord record,
            ref BridgeStatus bridge, ref McpServerStatus status)
        {
            if (changed == "recovery") record.id += "-next";
            if (changed == "pid") { status.ProcessId = 43; status.Health.server_pid = 43; }
            if (changed == "instance") status.Health.server_instance_id = "server-next";
            if (changed == "session") { bridge.SessionId = "session-next"; status.Health.bridge_session_id = "session-next"; }
            if (changed == "project") status.Health.configured_project_path = System.IO.Path.Combine(UPilotProjectConfig.ProjectRoot, "other");
        }

        [Test]
        public void EntryWarningKeepsHealthyCommunicationVisibleAndRepairRequired()
        {
            WithIsolatedHealthUi(() =>
            {
                var setupKey = UPilotPreferences.SetupCompletedKey;
                var hadSetup = EditorPrefs.HasKey(setupKey);
                var setup = EditorPrefs.GetBool(setupKey);
                try
                {
                    EditorPrefs.SetBool(setupKey, true);
                    var status = Responsive(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    PopulateDeploymentIdentity(status.Health);
                    status.Health.server_entry_path = System.IO.Path.Combine(UPilotProjectConfig.ProjectRoot, "other-server.exe");
                    var bridge = new BridgeStatus { IsStarted = true, IsWsOpen = true, IsAuthenticated = true, SessionId = "session-a" };
                    var view = UPilotQuickStart.Evaluate(bridge, status, Array.Empty<AgentMcpConfigStatus>());
                    Assert.That(view.State, Is.EqualTo(UPilotMainState.NeedsRepair));
                    Assert.That(view.Message, Does.Contain("Server：正常｜Bridge：已连接（心跳新鲜）｜Unity：响应正常"));
                    Assert.That(view.Message, Does.Contain("启动入口不一致（期望来源："));
                    Assert.That(view.Message, Does.Contain("期望入口："));
                    Assert.That(view.Message, Does.Contain("实际入口：" + status.Health.server_entry_path));
                    Assert.That(view.Message, Does.Not.Contain("当前包配套入口"));
                }
                finally
                {
                    if (hadSetup) EditorPrefs.SetBool(setupKey, setup); else EditorPrefs.DeleteKey(setupKey);
                }
            });
        }

        [TestCase("health-failed", "Server：待确认")]
        [TestCase("health-expired", "Server：待确认")]
        [TestCase("query-pending", "Server：待确认")]
        [TestCase("timeout-with-stopped-cache", "Server：待确认")]
        [TestCase("timeout-with-port-cache", "Server：待确认")]
        [TestCase("server-stopped", "Server：未运行")]
        [TestCase("port-closed", "Server：监听异常")]
        [TestCase("server-error", "Server：状态异常")]
        [TestCase("foreign", "Server：身份不符")]
        [TestCase("wrong-pid", "Server：待确认")]
        [TestCase("wrong-project", "Server：待确认")]
        [TestCase("ws-closed", "Bridge：未连接")]
        [TestCase("bridge-stopped", "Bridge：未启动")]
        [TestCase("auth-failed", "Bridge：认证失败")]
        [TestCase("auth-pending", "Bridge：等待认证")]
        [TestCase("wrong-session", "Bridge：已连接（服务身份待确认）")]
        [TestCase("heartbeat-expired", "Bridge：已连接（心跳待确认）")]
        [TestCase("pump-expired", "Unity：待确认")]
        [TestCase("observation-expired", "Unity：待确认")]
        [TestCase("observation-missing", "Unity：待确认")]
        [TestCase("observation-old-session", "Unity：待确认")]
        [TestCase("waiting-editor", "Unity：UPilot 观测待更新")]
        [TestCase("waiting-lifecycle", "Unity：正在编译")]
        [TestCase("unity-exited", "Unity：进程已退出")]
        public void RepairWarningDoesNotHideUnhealthyOrUnknownCommunication(string condition, string expected)
        {
            const long now = 1000000;
            var status = Responsive(now);
            var bridge = new BridgeStatus { IsStarted = true, IsWsOpen = true, IsAuthenticated = true, SessionId = "session-a" };
            switch (condition)
            {
                case "health-failed": status.HealthEndpointResponded = false; break;
                case "health-expired": status.LastSuccessfulStatusAtUtcMs -= UPilotMcpServerManager.Instance.HealthCheckIntervalMs + 6001; break;
                case "query-pending": status.DiagnosisPending = true; break;
                case "timeout-with-stopped-cache":
                    status.IsRunning = false;
                    status.FailureKind = McpStatusFailureKind.Transient;
                    status.ErrorMessage = "fixture status timeout";
                    break;
                case "timeout-with-port-cache":
                    status.HttpPortListening = false;
                    status.FailureKind = McpStatusFailureKind.Transient;
                    status.ErrorMessage = "fixture status timeout";
                    break;
                case "server-stopped": status.IsRunning = false; break;
                case "port-closed": status.HttpPortListening = false; break;
                case "server-error": status.ErrorMessage = "fixture failure"; break;
                case "foreign": status.ProcessOwnership = McpProcessOwnership.Foreign; break;
                case "wrong-pid": status.Health.server_pid++; break;
                case "wrong-project": status.Health.configured_project_path += "/other-project"; break;
                case "ws-closed": bridge.IsWsOpen = false; break;
                case "bridge-stopped": bridge.IsStarted = false; break;
                case "auth-failed": bridge.AuthenticationErrorCode = "TOKEN_REJECTED"; break;
                case "auth-pending": bridge.IsAuthenticated = false; break;
                case "wrong-session": bridge.SessionId = "old-session"; break;
                case "heartbeat-expired": status.EditorObservationHeartbeatAgeMs = UPilotBridge.Instance.HeartbeatTimeoutMs + 1; break;
                case "pump-expired": status.EditorObservationPumpAgeMs = 5001; break;
                case "observation-expired": status.EditorObservationObservedAtUtcMs -= UPilotMcpServerManager.Instance.HealthCheckIntervalMs + 6001; break;
                case "observation-missing": status.EditorObservationAvailable = false; break;
                case "observation-old-session": status.EditorObservationSessionId = "old-session"; break;
                case "waiting-editor": status.EditorObservationStatus = "waiting_editor"; break;
                case "waiting-lifecycle": status.EditorObservationStatus = "waiting_lifecycle"; break;
                case "unity-exited": status.EditorObservationReason = "unity_process_exited"; break;
            }
            var message = UPilotQuickStart.BuildRepairMessage(bridge, status, "入口差异详情", now);
            Assert.That(message, Does.Contain(expected));
            Assert.That(message, Does.Not.Contain("Unity：响应正常"));
            Assert.That(message, Does.EndWith("\n\n入口差异详情"));
        }

        [Test]
        public void HealthyCachedObservationRemainsValidBetweenConfiguredHealthChecks()
        {
            const long now = 1000000;
            var status = Responsive(now - UPilotMcpServerManager.Instance.HealthCheckIntervalMs + 1);
            var bridge = new BridgeStatus { IsStarted = true, IsWsOpen = true, IsAuthenticated = true, SessionId = "session-a" };
            var message = UPilotQuickStart.BuildRepairMessage(bridge, status, "fixture", now);
            Assert.That(message, Does.Contain("Server：正常").And.Contain("Bridge：已连接（心跳新鲜）").And.Contain("Unity：响应正常"));
            Assert.That(UPilotHealthAssessment.CanVerifyRecovery(bridge, status, now), Is.True);
            status.EditorObservationHeartbeatAgeMs = UPilotBridge.Instance.HeartbeatTimeoutMs + 1;
            Assert.That(UPilotHealthAssessment.CanVerifyRecovery(bridge, status, now), Is.False);
        }

        [TestCase(false, TestName = "ConfiguredHeartbeatTimeoutKeepsPumpFreshnessIndependent_Fresh")]
        [TestCase(true, TestName = "ConfiguredHeartbeatTimeoutKeepsPumpFreshnessIndependent_Cached")]
        public void ConfiguredHeartbeatTimeoutKeepsPumpFreshnessIndependent(bool cached)
        {
            const long now = 1000000;
            var heartbeatTimeoutMs = UPilotBridge.Instance.HeartbeatTimeoutMs;
            var cacheAge = cached ? UPilotMcpServerManager.Instance.HealthCheckIntervalMs : 0;
            var status = Responsive(now - cacheAge);
            var bridge = new BridgeStatus { IsStarted = true, IsWsOpen = true, IsAuthenticated = true, SessionId = "session-a" };
            // Read the active budget without changing settings or waking the running heartbeat loop.
            status.EditorObservationHeartbeatAgeMs = heartbeatTimeoutMs;
            Assert.That(UPilotHealthAssessment.CanVerifyRecovery(bridge, status, now), Is.True);
            var message = UPilotQuickStart.BuildRepairMessage(bridge, status, "fixture", now);
            Assert.That(message, Does.Contain("Bridge：已连接（心跳新鲜）").And.Contain("Unity：响应正常"));

            status.EditorObservationPumpAgeMs = 5001;
            Assert.That(UPilotHealthAssessment.CanVerifyRecovery(bridge, status, now), Is.False);
            message = UPilotQuickStart.BuildRepairMessage(bridge, status, "fixture", now);
            Assert.That(message, Does.Contain("Bridge：已连接（心跳新鲜）").And.Contain("Unity：待确认"));

            status.EditorObservationPumpAgeMs = 100;
            status.EditorObservationHeartbeatAgeMs = heartbeatTimeoutMs + 1;
            Assert.That(UPilotHealthAssessment.CanVerifyRecovery(bridge, status, now), Is.False);
            message = UPilotQuickStart.BuildRepairMessage(bridge, status, "fixture", now);
            Assert.That(message, Does.Contain("Bridge：已连接（心跳待确认）").And.Contain("Unity：待确认"));
        }

        [TestCase("enteringPlay", true, "play")]
        [TestCase("exitingPlay", false, "edit")]
        public void StableEditorUpdatesRecoverTransitionWithoutChangingRequestProvenance(string transition, bool playing, string terminal)
        {
            var intent = new PlayModeTransitionRecord { origin = "upilot", requestId = "request-a",
                commandId = "command-a", operationId = "operation-a", toolName = "playmode.set", runGuid = "run-a" };
            var start = UPilotPlayModeTransitions.Build(playing ? "edit" : "play", transition, "old-session", 100000, intent);
            var original = UnityEngine.JsonUtility.ToJson(start);
            string candidate = null;
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                playing, playing, false, "new-session", 700000), Is.Null);
            Assert.Throws<UPilotCompilePolicyException>(() => UPilotPlayModeCompilePolicy.RequireEditMode("compile",
                UPilotPlayModeCompilePolicy.ClassifyMode(playing, false, playing, start.toState)));
            var end = UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                playing, playing, false, "new-session", 700001);
            Assert.That(end, Is.Not.Null);
            Assert.That(end.toState, Is.EqualTo(terminal));
            Assert.That(end.fromState, Is.EqualTo(transition));
            Assert.That(end.transitionId, Is.Not.EqualTo(start.transitionId));
            Assert.That(end.at, Is.EqualTo(700001));
            Assert.That(end.unitySessionId, Is.EqualTo("new-session"));
            Assert.That(end.origin, Is.EqualTo(start.origin));
            Assert.That(end.requestId, Is.EqualTo(start.requestId));
            Assert.That(end.commandId, Is.EqualTo(start.commandId));
            Assert.That(end.operationId, Is.EqualTo(start.operationId));
            Assert.That(end.toolName, Is.EqualTo(start.toolName));
            Assert.That(end.runGuid, Is.EqualTo(start.runGuid));
            Assert.That(UnityEngine.JsonUtility.ToJson(start), Is.EqualTo(original));
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(end, ref candidate,
                playing, playing, false, "new-session", 700002), Is.Null);
            if (!playing) Assert.DoesNotThrow(() => UPilotPlayModeCompilePolicy.RequireEditMode("compile",
                UPilotPlayModeCompilePolicy.ClassifyMode(false, false, false, end.toState)));
        }

        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        public void InterruptedExitRecoveryRequiresTwoMoreStableEditorUpdates(bool playing, bool willPlay, bool busy)
        {
            var start = UPilotPlayModeTransitions.Build("play", "exitingPlay", "session-a", 100000, null);
            string candidate = null;
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                false, false, false, "session-a", 100001), Is.Null);
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                playing, willPlay, busy, "session-a", 700002), Is.Null);
            Assert.That(candidate, Is.Null);
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                false, false, false, "session-a", 700003), Is.Null);
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                false, false, false, "session-a", 700004)?.toState, Is.EqualTo("edit"));
        }

        [TestCase("session")]
        [TestCase("transition")]
        public void ExitRecoveryCannotReuseObservationFromAnotherSessionOrTransition(string change)
        {
            var start = UPilotPlayModeTransitions.Build("play", "exitingPlay", "session-a", 100000, null);
            string candidate = null;
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                false, false, false, "session-a", 100001), Is.Null);
            var session = change == "session" ? "session-b" : "session-a";
            if (change == "transition") start = UPilotPlayModeTransitions.Build("play", "exitingPlay", session, 100001, null);
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                false, false, false, session, 100002), Is.Null);
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                false, false, false, session, 100003)?.toState, Is.EqualTo("edit"));
        }

        [TestCase("pid")]
        [TestCase("creation")]
        [TestCase("project")]
        [TestCase("id")]
        [TestCase("session")]
        [TestCase("terminal")]
        [TestCase("timestamp")]
        public void ExitRecoveryRejectsUnknownIdentityAndUnstableEvidence(string invalid)
        {
            var start = UPilotPlayModeTransitions.Build("play", "exitingPlay", "session-a", 100000, null);
            string candidate = null, session = "session-a";
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                false, false, false, session, 100001), Is.Null);
            if (invalid == "pid") start.processId++;
            if (invalid == "creation") start.processCreatedAt++;
            if (invalid == "project") start.projectPath += "/other-project";
            if (invalid == "id") start.transitionId = "";
            if (invalid == "session") session = "";
            if (invalid == "terminal") start.toState = "edit";
            if (invalid == "timestamp") start.at = 700000;
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                false, false, false, session, 700000), Is.Null);
            Assert.That(candidate, Is.Null);
        }

        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, true)]
        public void InterruptedEntryRecoveryRequiresTwoMoreStableEditorUpdates(bool playing, bool willPlay, bool busy)
        {
            var start = UPilotPlayModeTransitions.Build("edit", "enteringPlay", "session-a", 100000, null);
            string candidate = null;
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                true, true, false, "session-a", 100001), Is.Null);
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                playing, willPlay, busy, "session-a", 100002), Is.Null);
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                true, true, false, "session-a", 100003), Is.Null);
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                true, true, false, "session-a", 100004), Is.Not.Null);
        }

        [Test]
        public void EntryRecoveryRequiresTwoStableUpdatesInTheSameBridgeSession()
        {
            var start = UPilotPlayModeTransitions.Build("edit", "enteringPlay", "session-a", 100000, null);
            string candidate = null;
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                true, true, false, "session-a", 100001), Is.Null);
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                true, true, false, "session-b", 100002), Is.Null);
            var end = UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                true, true, false, "session-b", 100003);
            Assert.That(end, Is.Not.Null);
            Assert.That(end.unitySessionId, Is.EqualTo("session-b"));
        }
        [Test]
        public void ReplacedEntryAndLongWaitDoNotReuseStableObservation()
        {
            var start = UPilotPlayModeTransitions.Build("edit", "enteringPlay", "session-a", 100000, null);
            var replacement = UPilotPlayModeTransitions.Build("edit", "enteringPlay", "session-a", 100001, null);
            string candidate = null;
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                true, true, false, "session-a", 100002), Is.Null);
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(replacement, ref candidate,
                true, true, false, "session-a", 100003), Is.Null);
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(replacement, ref candidate,
                false, true, false, "session-a", 700001), Is.Null);
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(replacement, ref candidate,
                false, true, false, "session-a", 700002), Is.Null);
        }

        [TestCase("pid")]
        [TestCase("creation")]
        [TestCase("project")]
        [TestCase("id")]
        [TestCase("session")]
        [TestCase("exiting")]
        [TestCase("timestamp")]
        public void EntryRecoveryRejectsUnknownIdentityAndNonEntryStates(string invalid)
        {
            var start = UPilotPlayModeTransitions.Build("edit", "enteringPlay", "session-a", 100000, null);
            var session = "session-a";
            if (invalid == "pid") start.processId++;
            if (invalid == "creation") start.processCreatedAt++;
            if (invalid == "project") start.projectPath += "/other-project";
            if (invalid == "id") start.transitionId = "";
            if (invalid == "session") session = "";
            if (invalid == "exiting") start.toState = "exitingPlay";
            if (invalid == "timestamp") start.at = 700000;
            string candidate = start.transitionId;
            Assert.That(UPilotPlayModeTransitions.RecoverOnEditorUpdate(start, ref candidate,
                true, true, false, session, 700000), Is.Null);
            Assert.That(candidate, Is.Null);
        }

        [TestCase("play")]
        [TestCase("edit")]
        public void CompletedLocalLifecycleCannotKeepOldEnteringNotice(string terminalMode)
        {
            var field = typeof(UPilotPlayModeTransitions).GetField("ledger", BindingFlags.Static | BindingFlags.NonPublic);
            var saved = field.GetValue(null);
            var start = UPilotPlayModeTransitions.Build("edit", "enteringPlay", "session-a", 100000, null);
            var end = UPilotPlayModeTransitions.Build("enteringPlay", terminalMode, "session-a", 100001, start);
            var isolated = Activator.CreateInstance(field.FieldType, true);
            field.FieldType.GetField("records").SetValue(isolated, new List<PlayModeTransitionRecord> { start, end });
            try
            {
                field.SetValue(null, isolated);
                var status = Waiting("enteringPlay", 600000);
                status.EditorObservationLifecycleId = start.transitionId;
                var bridge = new BridgeStatus { IsStarted = true };
                Assert.That(UPilotPlayModeTransitions.HasEndedLifecycle(start.transitionId, start.at), Is.True);
                Assert.That(UPilotHealthAssessment.Classify(bridge, status), Is.EqualTo(UPilotHealthDisposition.Unconfirmed));
                Assert.That(UPilotHealthAssessment.ObservationStatus(bridge, status), Is.EqualTo("unknown"));
                var view = UPilotQuickStart.EvaluateServiceState(bridge, status);
                Assert.That(view.State, Is.EqualTo(UPilotMainState.CheckingStatus));
                Assert.That(view.Title, Does.Not.Contain("正在进入"));
                Assert.That(status.EditorObservationStatus, Is.EqualTo("waiting_lifecycle"));
            }
            finally { field.SetValue(null, saved); }
        }

        [Test]
        public void ExpiredUpilotObservationDoesNotClaimUnityHasStalled()
        {
            var status = Waiting("enteringPlay", 600000);
            status.EditorObservationStatus = "waiting_editor";
            var bridge = new BridgeStatus { IsStarted = true, IsWsOpen = true, IsAuthenticated = true, SessionId = "session-a" };
            var view = UPilotQuickStart.EvaluateServiceState(bridge, status);
            Assert.That(view.State, Is.EqualTo(UPilotMainState.CheckingStatus));
            Assert.That(view.Title, Does.Contain("UPilot"));
            Assert.That(view.Message, Does.Contain("不能据此认定 Unity 或游戏卡死"));
            Assert.That(UPilotHealthAssessment.ObservationStatus(bridge, status), Is.EqualTo("waiting_editor"));
        }

        private static void PopulateDeploymentIdentity(UPilotServerHealth health)
        {
            health.server_version = UPilotDeploymentDiagnostics.Version;
            health.build_channel = UPilotDeploymentDiagnostics.Channel;
            health.protocol_version = "1";
            health.identity_contract_version = UPilotBridge.IdentityContractVersion;
            health.server_entry_path = UPilotDeploymentDiagnostics.ExpectedEntry;
            health.server_module_root = System.IO.Path.Combine(UPilotDeploymentDiagnostics.PackageRoot, "upilotserver~", "src", "upilot_mcp");
            health.server_started_at_ms = 100000;
        }

        private static Dictionary<string, UPilotDeploymentIssue> DiagnosticIssues() =>
            (Dictionary<string, UPilotDeploymentIssue>)typeof(UPilotDeploymentDiagnostics)
                .GetField("Issues", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

        private static void WithIsolatedHealthUi(Action test)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var sessionKeys = new[] { "HealthRecoveryKey", "VerifiedHealthKey", "VerifiedRestartHistoryKey", "VerifiedExistingRecoveryKey" }
                .Select(name => (string)typeof(UPilotQuickStart).GetProperty(name, flags).GetValue(null)).ToArray();
            var sessions = sessionKeys.Select(key => SessionState.GetString(key, "")).ToArray();
            var failureKey = (string)typeof(UPilotQuickStart).GetProperty("FailureKey", flags).GetValue(null);
            var autoKey = (string)typeof(UPilotQuickStart).GetProperty("AutoAttemptKey", flags).GetValue(null);
            var hadFailure = EditorPrefs.HasKey(failureKey); var failure = EditorPrefs.GetString(failureKey, "");
            var hadAuto = EditorPrefs.HasKey(autoKey); var auto = EditorPrefs.GetBool(autoKey);
            var issues = DiagnosticIssues(); var previous = issues.ToArray();
            var since = typeof(UPilotDeploymentDiagnostics).GetField("_checkingSince", flags);
            var oldSince = since.GetValue(null);
            var transient = typeof(UPilotDeploymentDiagnostics).GetField("_transientStatus", flags);
            var oldTransient = transient.GetValue(null);
            var details = typeof(UPilotDeploymentDiagnostics).GetField("_lastDetails", flags);
            var oldDetails = details.GetValue(null);
            try { issues.Clear(); foreach (var key in sessionKeys) SessionState.EraseString(key); test(); }
            finally
            {
                issues.Clear(); foreach (var pair in previous) issues.Add(pair.Key, pair.Value);
                since.SetValue(null, oldSince);
                transient.SetValue(null, oldTransient);
                details.SetValue(null, oldDetails);
                for (var i = 0; i < sessionKeys.Length; i++) SessionState.SetString(sessionKeys[i], sessions[i]);
                if (hadFailure) EditorPrefs.SetString(failureKey, failure); else EditorPrefs.DeleteKey(failureKey);
                if (hadAuto) EditorPrefs.SetBool(autoKey, auto); else EditorPrefs.DeleteKey(autoKey);
            }
        }

        private sealed class DelayedHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                await Task.Delay(2200, token).ConfigureAwait(false);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
        }

        [UnityTest]
        public IEnumerator ReadOnlyProbeAllowsMoreThanTwoSecondsButKeepsOriginalDeadline()
        {
            using var configured = UPilotMcpServerManager.CreateReadOnlyProbeClient();
            Assert.That(configured.Timeout.TotalMilliseconds, Is.EqualTo(7000));
            using var client = new HttpClient(new DelayedHandler()) { Timeout = configured.Timeout };
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var successful = UPilotMcpServerManager.SendBoundedStatusAsync(client,
                "http://127.0.0.1:1/health?probe=bridge", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 7000, default, 7000);
            // Yield the Editor thread: NUnit's synchronous async assertion must not
            // block a continuation captured on Unity's synchronization context.
            while (!successful.IsCompleted && elapsed.ElapsedMilliseconds < 9000) yield return null;
            Assert.That(successful.IsCompleted, Is.True, "The bounded probe did not complete.");
            using var response = successful.GetAwaiter().GetResult();
            Assert.That(response.IsSuccessStatusCode, Is.True);
            Assert.That(elapsed.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(2000));
            Assert.That(elapsed.ElapsedMilliseconds, Is.LessThan(7000));

            elapsed.Restart();
            var expired = UPilotMcpServerManager.SendBoundedStatusAsync(client,
                "http://127.0.0.1:1/health?probe=bridge", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 50, default, 7000);
            while (!expired.IsCompleted && elapsed.ElapsedMilliseconds < 3000) yield return null;
            Assert.That(expired.IsCompleted, Is.True, "The original maintenance deadline was not enforced.");
            Assert.That(expired.IsFaulted, Is.True);
            Assert.That(expired.Exception.InnerException, Is.TypeOf<TimeoutException>());
            Assert.That(elapsed.ElapsedMilliseconds, Is.LessThan(2000), "The 7-second probe budget must not renew the deadline.");
        }

        [UnityTest]
        public IEnumerator ReadOnlyProbePreservesCallerCancellation()
        {
            using var client = new HttpClient(new DelayedHandler()) { Timeout = TimeSpan.FromSeconds(7) };
            using var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(50);
            var probe = UPilotMcpServerManager.SendBoundedStatusAsync(client,
                "http://127.0.0.1:1/health?probe=bridge", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 7000,
                cancellation.Token, 7000);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (!probe.IsCompleted && elapsed.ElapsedMilliseconds < 3000) yield return null;
            Assert.That(probe.IsCompleted, Is.True);
            Assert.That(probe.IsCanceled, Is.True, "Caller cancellation must not be reported as a health timeout.");
        }
    }
}
