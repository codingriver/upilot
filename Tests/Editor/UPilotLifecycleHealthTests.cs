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
                UPilotQuickStart.TrackLifecycleRecovery(status);
                status.EditorObservationStatus = "responsive";
                status.EditorObservationLifecyclePhase = "verifying_recovery";
                status.EditorObservationObservedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                status.EditorObservationHeartbeatAgeMs = 100;
                status.EditorObservationPumpAgeMs = 100;
                PopulateDeploymentIdentity(status.Health);
                var bridge = new BridgeStatus { IsStarted = true, IsWsOpen = true, IsAuthenticated = true, SessionId = "session-a" };
                var prefix = typeof(UPilotQuickStart);
                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                var pendingKey = (string)prefix.GetProperty("HealthRecoveryKey", flags).GetValue(null);
                var failureKey = (string)prefix.GetProperty("FailureKey", flags).GetValue(null);
                var autoKey = (string)prefix.GetProperty("AutoAttemptKey", flags).GetValue(null);
                var generation = (int)prefix.GetField("_repairGeneration", flags).GetValue(null);
                var token = SessionState.GetString(pendingKey, "");
                EditorPrefs.SetString(failureKey, "old health timeout");
                EditorPrefs.SetBool(autoKey, true);
                DiagnosticIssues()["timeout"] = new UPilotDeploymentIssue { Code = "timeout", Confirmed = false };
                var record = UPilotServerRestartDiagnostics.Current;
                var historicalStatus = record?.status;
                var historicalError = record?.error;
                var latest = status;
                if (!valid) latest.EditorObservationSessionId = "old-session";
                Assert.That(UPilotQuickStart.CompleteObservedRecovery(status, bridge.SessionId, generation, token, bridge, latest), Is.EqualTo(valid));
                Assert.That(SessionState.GetString(pendingKey, ""), Is.EqualTo(valid ? "" : token));
                Assert.That(EditorPrefs.HasKey(failureKey), Is.EqualTo(!valid));
                Assert.That(EditorPrefs.HasKey(autoKey), Is.EqualTo(!valid));
                if (valid) Assert.That(DiagnosticIssues().Values.Any(issue => !issue.Confirmed), Is.False);
                Assert.That(record?.status, Is.EqualTo(historicalStatus));
                Assert.That(record?.error, Is.EqualTo(historicalError));
            });
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
            var sessionKeys = new[] { "HealthRecoveryKey", "VerifiedHealthKey", "VerifiedRestartHistoryKey" }
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
