// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotExistingServiceRecoveryTests
    {
        private const string LaunchKey = "UPilot.ServerLaunchIdentity.v1";
        private const string StopKey = "UPilot.ServiceStopObservation.v1";
        private const string RecoveryKey = "UPilot.ServerRecovery.Existing.v1";
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags StaticFields = BindingFlags.Static | BindingFlags.NonPublic;
        private readonly Dictionary<string, string> _session = new Dictionary<string, string>();
        private readonly Dictionary<string, object> _deployment = new Dictionary<string, object>();
        private Dictionary<string, UPilotDeploymentIssue> _issues;
        private string _missing;
        private object _startupRecord, _startupPath;
        private UPilotMcpServerManager _manager;
        private UPilotMcpServerManager.ExistingServiceRecoveryRecord _record;
        private Task _work;
        private int _stopDispatches, _terminations;

        [SetUp]
        public void SaveState()
        {
            _missing = "missing-" + Guid.NewGuid().ToString("N");
            _session.Clear();
            foreach (var key in new[] { LaunchKey, StopKey, RecoveryKey })
                _session.Add(key, SessionState.GetString(key, _missing));
            _deployment.Clear();
            foreach (var name in new[] { "_checkingSince", "_transientStatus", "_lastDetails" })
                _deployment.Add(name, DeploymentField(name).GetValue(null));
            _issues = new Dictionary<string, UPilotDeploymentIssue>(DeploymentIssues);
            _startupRecord = StartupField("s_record").GetValue(null);
            _startupPath = StartupField("s_path").GetValue(null);
            _work = null;
            _stopDispatches = _terminations = 0;
            _manager = (UPilotMcpServerManager)FormatterServices.GetUninitializedObject(typeof(UPilotMcpServerManager));
            Set("_statusLock", new object());
            Set("_stoppingProcesses", new List<System.Diagnostics.Process>());
            Set("_initialServiceRecoveryCompleted", true);
            Set("_restartOperationId", "");
            Set("_startAttemptGeneration", 13);
            Set("_restartGeneration", 17L);
            Set("_healthCheckIntervalSeconds", 600);
            Set("_pythonEntryPath", UPilotDeploymentDiagnostics.PythonEntry);
            Set("_autoStart", true);
            // These guards make an accidental stop regression fail without touching the service.
            _manager.StopDiscoveryOverride = (scope, pids, deadline) => {
                Interlocked.Increment(ref _stopDispatches);
                throw new InvalidOperationException("A read-only recovery test must not dispatch a stop.");
            };
            _manager.StopTerminationOverride = target => {
                Interlocked.Increment(ref _terminations);
                throw new InvalidOperationException("A read-only recovery test must not terminate a process.");
            };
        }

        [TearDown]
        public async Task RestoreState()
        {
            Call("BeforeExistingServiceRecoveryReload");
            try
            {
                try
                {
                    Get<CancellationTokenSource>("_existingServiceRecoveryCancellation")?.Cancel();
                    if (_work != null) await _work;
                }
                finally
                {
                    try
                    {
                        var loop = Get<Task>("_editorHealthObservationLoopTask");
                        var probe = Get<Task>("_editorHealthObservationTask");
                        _manager.PausePeriodicHealthObservation();
                        if (loop != null) await loop;
                        if (probe != null) await probe;
                    }
                    finally
                    {
                        Call("ReleaseRecoveredServiceAttachment", false);
                        var cancellation = Get<CancellationTokenSource>("_lifecycleQueryCancellation");
                        cancellation?.Cancel();
                        cancellation?.Dispose();
                    }
                }
            }
            finally
            {
                DeploymentIssues.Clear();
                foreach (var item in _issues) DeploymentIssues.Add(item.Key, item.Value);
                foreach (var item in _deployment) DeploymentField(item.Key).SetValue(null, item.Value);
                StartupField("s_record").SetValue(null, _startupRecord);
                StartupField("s_path").SetValue(null, _startupPath);
                foreach (var item in _session)
                {
                    if (item.Value == _missing) SessionState.EraseString(item.Key);
                    else SessionState.SetString(item.Key, item.Value);
                }
            }
        }

        [Test, Explicit("Requires the canonical acceptance project's healthy Server and current authenticated Bridge; read-only.")]
        public async Task HealthyExistingServiceReusesCurrentBridgeWithoutHistoricalLaunchIdentity()
        {
            Assert.That(UPilotDeploymentDiagnostics.SamePath(UPilotProjectConfig.ProjectRoot,
                Path.Combine(UPilotDeploymentDiagnostics.PackageRoot, "Tests~", "UPilotTest")), Is.True);
            Assert.That(UPilotQuickStart.IsExplicitlyStopped, Is.False);
            var before = UPilotBridge.Instance.GetStatus();
            Assert.That(before.IsWsOpen && before.IsAuthenticated, Is.True);
            Assert.That(before.SessionId, Is.Not.Null.And.Not.Empty);
            // A new Editor starts with a new deployment diagnostic inventory.
            DeploymentIssues.Clear();
            await RunRecoveryAsync(false, false);

            Assert.That(_record.status, Is.EqualTo("reused"), _record.error);
            Assert.That(_manager.HasVerifiedRecoveredAttachment, Is.True);
            var status = _manager.GetStatus();
            Assert.That(status.StatusQueryCompleted && status.HealthEndpointResponded, Is.True);
            Assert.That(status.ProcessOwnership, Is.EqualTo(McpProcessOwnership.CurrentUPilot));
            Assert.That(status.ProcessId, Is.GreaterThan(0));
            Assert.That(status.ProcessId, Is.EqualTo(status.HealthServerProcessId));
            Assert.That(status.Health.bridge_session_id, Is.EqualTo(before.SessionId));
            Assert.That(Get<string>("_recoveredServiceInstanceId"), Is.EqualTo(status.Health.server_instance_id));
            Assert.That(status.LifecyclePhase, Is.EqualTo(UPilotServerLifecyclePhase.None));
            Assert.That(status.LastSuccessfulStatusAtUtcMs, Is.GreaterThan(0));

            // A later full refresh must preserve the verified recovered identity too.
            for (var refresh = 0; refresh < 2; refresh++)
            {
                var refreshed = await _manager.GetFreshStatusAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10000);
                Assert.That(refreshed.StatusQueryCompleted && refreshed.HealthEndpointResponded, Is.True);
                Assert.That(refreshed.ProcessOwnership, Is.EqualTo(McpProcessOwnership.CurrentUPilot),
                    "A healthy recovered Server must remain identified after a full status refresh.");
                Assert.That(refreshed.ProcessId, Is.EqualTo(status.ProcessId));
                Assert.That(refreshed.Health.server_instance_id, Is.EqualTo(status.Health.server_instance_id));
                Assert.That(UPilotHealthAssessment.HasServiceIdentity(refreshed), Is.True);
                Assert.That(UPilotQuickStart.BuildRepairMessage(before, refreshed, ""),
                    Does.StartWith("Server：正常｜Bridge：已连接"));
            }
            // Current health must still match the exact recovered PID, instance and project.
            foreach (var mismatch in new[] { "pid", "instance", "project" })
            {
                var candidate = status;
                candidate.ProcessOwnership = McpProcessOwnership.Unknown;
                candidate.ProcessId = null;
                candidate.ProcessOwnershipEvidence = null;
                candidate.Health = new UPilotServerHealth { server_instance_id = status.Health.server_instance_id };
                if (mismatch == "pid") candidate.HealthServerProcessId = 0;
                if (mismatch == "instance") candidate.Health.server_instance_id = "different-instance";
                if (mismatch == "project") candidate.HealthProjectPath = Path.Combine(UPilotProjectConfig.ProjectRoot, "other-project");
                var arguments = new object[] { candidate };
                Assert.That((bool)Call("TryApplyRecoveredProcessOwnership", arguments), Is.False, mismatch);
                var rejected = (McpServerStatus)arguments[0];
                Assert.That(rejected.ProcessOwnership, Is.EqualTo(McpProcessOwnership.Unknown), mismatch);
                Assert.That(rejected.ProcessId, Is.Null, mismatch);
            }
            AssertRecoveryDidNotStopOrLaunch();
        }

        [Test]
        public async Task ExpiredReuseBudgetFailsWithoutCleanupOrReplacement()
        {
            Assert.That(UPilotQuickStart.IsExplicitlyStopped, Is.False);
            // Isolate only this expected failure's startup diagnostics; preserve startup.json.
            StartupField("s_record").SetValue(null, new UPilotStartupRecord { failureSummaryLogged = true });
            StartupField("s_path").SetValue(null, null);
            LogAssert.Expect(LogType.Error, new Regex("遗留服务恢复结束；recoveryId="));
            await RunRecoveryAsync(false, true);

            Assert.That(_record.status, Is.EqualTo("failed"));
            Assert.That(_record.error, Is.Not.Null.And.Not.Empty);
            Assert.That(_record.reuseDeadline, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            Assert.That(_record.validationAttemptCount, Is.Zero);
            Assert.That(_record.validationStage, Is.EqualTo("discovery"));
            Assert.That(_record.lastBlockReason, Is.Not.Empty);
            Assert.That(_record.healthElapsedMs + _record.readOnlyElapsedMs, Is.Zero);
            Assert.That(_manager.GetStatus().LifecyclePhase, Is.EqualTo(UPilotServerLifecyclePhase.None));
            var startup = (UPilotStartupRecord)StartupField("s_record").GetValue(null);
            Assert.That(startup.blockingReasons.Exists(item => item.code == "existing_service_recovery_failed"), Is.True);
            AssertRecoveryDidNotStopOrLaunch();
        }

        [Test]
        public async Task DisabledAutoStartEndsAutomaticRecoveryWithoutCleanupOrReplacement()
        {
            Set("_autoStart", false);
            await RunRecoveryAsync(true, false);

            Assert.That(_record.status, Is.EqualTo("stopped"));
            Assert.That(_record.error, Is.Empty);
            Assert.That(_manager.GetStatus().LifecyclePhase, Is.EqualTo(UPilotServerLifecyclePhase.None));
            AssertRecoveryDidNotStopOrLaunch();
        }

        [TestCase("verifying")]
        [TestCase("cleaning")]
        public void SupersedingResetRevokesRecoveryBeforeItsStopWaiterCanResume(string phase)
        {
            using var cancellation = new CancellationTokenSource();
            var record = new UPilotMcpServerManager.ExistingServiceRecoveryRecord {
                id = "superseded-" + Guid.NewGuid().ToString("N"), status = phase,
                deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30000,
                spec = new ServerLaunchSpec { httpPort = _manager.HttpPort, wsPort = _manager.WsPort },
                projectPath = UPilotProjectConfig.ProjectRoot,
            };
            Set("_existingServiceRecovery", record);
            Set("_existingServiceRecoveryCancellation", cancellation);
            Set("_startInProgress", true);
            ExpectInterruptedRecoveryError(record);
            _manager.CancelExistingServiceRecoveryForReset("");
            _manager.CancelExistingServiceRecoveryForReset("");
            LogAssert.NoUnexpectedReceived();

            Assert.That(record.status, Is.EqualTo("stopped"));
            Assert.That(cancellation.IsCancellationRequested, Is.True);
            Assert.That(Get<CancellationTokenSource>("_existingServiceRecoveryCancellation"), Is.Null);
            Assert.That((bool)Call("OwnsExistingServiceRecovery", record, cancellation), Is.False,
                "The old stop continuation must lose ownership before ResetStopObservation completes it.");

            var nextLaunch = new ServerLaunchSpec { httpPort = 9991, wsPort = 9992 };
            Set("_pendingLaunchSpec", nextLaunch);
            Set("_pendingLaunchDeadline", 123456L);
            Set("_startInProgress", true);
            Call("FinishExistingServiceRecoveryWork", record, cancellation);
            Assert.That(Get<ServerLaunchSpec>("_pendingLaunchSpec"), Is.SameAs(nextLaunch));
            Assert.That(Get<long>("_pendingLaunchDeadline"), Is.EqualTo(123456));
            Assert.That(Get<bool>("_startInProgress"), Is.True, "The superseded finally must not clear the next owner.");
            Assert.That(record.replacementStarted, Is.False);
            Assert.That(_stopDispatches + _terminations, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InterruptedRecoveryIsVisibleButNormalUserStopRemainsCancellation(bool userStop)
        {
            using var cancellation = new CancellationTokenSource();
            var record = new UPilotMcpServerManager.ExistingServiceRecoveryRecord {
                id = "interrupted-" + Guid.NewGuid().ToString("N"), status = "verifying", error = "",
                deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30000,
                spec = new ServerLaunchSpec { httpPort = _manager.HttpPort, wsPort = _manager.WsPort },
                projectPath = UPilotProjectConfig.ProjectRoot,
            };
            Set("_existingServiceRecovery", record);
            Set("_existingServiceRecoveryCancellation", cancellation);
            Set("_startInProgress", true);
            if (userStop) Call("CancelExistingServiceRecoveryForStop", "user-stop");
            else
            {
                ExpectInterruptedRecoveryError(record);
                _manager.CancelExistingServiceRecoveryForReset("");
            }
            LogAssert.NoUnexpectedReceived();

            Assert.That(record.status, Is.EqualTo("stopped"));
            Assert.That(cancellation.IsCancellationRequested, Is.True);
            Assert.That(_manager.CanAutomaticallyStartServer, Is.False);
            Assert.That(_manager.CanAutomaticallyRepairDeployment, Is.False);
            Assert.That(_stopDispatches + _terminations, Is.Zero);
            if (userStop)
            {
                Assert.That(_manager.ExistingServiceRecoveryFailure, Is.Empty);
                Assert.That(_manager.ExistingServiceRecoveryFailureId, Is.Empty);
                Assert.That(record.error, Is.Empty, "A normal user stop must not become a recovery failure.");
                Assert.That(UPilotQuickStart.EvaluateExistingServiceRecoveryFailure(_manager, default, default), Is.Null);
            }
            else
            {
                Assert.That(_manager.ExistingServiceRecoveryFailure, Is.Not.Empty,
                    "A reset-interrupted startup must expose a terminal reason instead of remaining in an unknown UI state.");
                Assert.That(_manager.ExistingServiceRecoveryFailureId, Is.EqualTo(record.id));
                Assert.That(record.error, Is.Not.Empty);
                Assert.That(UPilotQuickStart.EvaluateExistingServiceRecoveryFailure(_manager, default, default)?.State,
                    Is.EqualTo(UPilotMainState.NeedsRepair));
                Assert.That(UPilotQuickStart.CanObserveFailedExistingServiceRecovery(_manager), Is.True);
            }
        }

        [TestCase("verifying", true)]
        [TestCase("cleaning", true)]
        [TestCase("replacement_starting", false)]
        [TestCase("replacement_started", false)]
        [TestCase("failed", false)]
        [TestCase("succeeded", false)]
        public void ResetPreservesAuthorizedOwnerAndReplacementOrTerminalRecords(string phase, bool preserveOwner)
        {
            using var cancellation = new CancellationTokenSource();
            var record = new UPilotMcpServerManager.ExistingServiceRecoveryRecord {
                id = "preserved-" + Guid.NewGuid().ToString("N"), status = phase, error = "historical evidence",
                replacementStarted = phase.StartsWith("replacement", StringComparison.Ordinal), replacementPid = 42,
            };
            var pending = new ServerLaunchSpec();
            Set("_existingServiceRecovery", record);
            Set("_existingServiceRecoveryCancellation", cancellation);
            Set("_pendingLaunchSpec", pending);
            Set("_startInProgress", true);
            _manager.CancelExistingServiceRecoveryForReset(preserveOwner ? record.id : "");
            LogAssert.NoUnexpectedReceived();
            Assert.That(record.status, Is.EqualTo(phase));
            Assert.That(record.error, Is.EqualTo("historical evidence"));
            Assert.That(record.replacementPid, Is.EqualTo(42));
            Assert.That(cancellation.IsCancellationRequested, Is.False);
            Assert.That(Get<CancellationTokenSource>("_existingServiceRecoveryCancellation"), Is.SameAs(cancellation));
            Assert.That(Get<ServerLaunchSpec>("_pendingLaunchSpec"), Is.SameAs(pending));
            Assert.That(Get<bool>("_startInProgress"), Is.True);
            Set("_existingServiceRecoveryCancellation", null);
        }

        [TestCase("current", true)]
        [TestCase("unknown", false)]
        [TestCase("project", false)]
        [TestCase("lifetime", false)]
        [TestCase("unity_pid", false)]
        [TestCase("stop_id", false)]
        [TestCase("request_id", false)]
        [TestCase("shutdown_owner", false)]
        [TestCase("stop_lifetime", false)]
        [TestCase("standalone_stop", false)]
        [TestCase("expired", false)]
        [TestCase("deadline", false)]
        [TestCase("replacement_started", false)]
        public void RestoredCleanupReceiptRequiresExplicitOriginalIdentity(string changed, bool expected)
        {
            var record = CreateCleanupReceiptRecord(out var stop);
            // This is the production SessionState representation, with no old-domain manager fields.
            record = JsonUtility.FromJson<UPilotMcpServerManager.ExistingServiceRecoveryRecord>(JsonUtility.ToJson(record));
            if (changed == "unknown") { record.resetPerformed = false; record.resetError = ""; }
            if (changed == "project") record.projectPath = Path.Combine(record.projectPath, "another-project");
            if (changed == "lifetime") record.resetLifetime += "-old";
            if (changed == "unity_pid") ++record.resetUnityPid;
            if (changed == "stop_id") stop.id += "-old";
            if (changed == "request_id") stop.requestId += "-old";
            if (changed == "shutdown_owner") stop.shutdownOwnerId += "-old";
            if (changed == "stop_lifetime") stop.lifetime += "-old";
            if (changed == "standalone_stop") stop.preserveShutdown = false;
            if (changed == "expired") record.deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1;
            if (changed == "deadline") stop.deadline = record.deadline + 1;
            if (changed == "replacement_started") record.replacementStarted = true;
            Assert.That(UPilotMcpServerManager.CanRestoreExistingServiceResetReceipt(record, stop,
                UPilotProjectConfig.ProjectRoot, UPilotServiceLifetime.Id, System.Diagnostics.Process.GetCurrentProcess().Id,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out var reason), Is.EqualTo(expected), reason);
            if (expected) Assert.That(record.resetError, Is.EqualTo("original_partial_cleanup_failure"));
        }

        [Test]
        public void ResumeCleaningRestoresReceiptBeforeSingleReplacementWithoutRepeatingReset()
        {
            var record = CreateCleanupReceiptRecord(out var stop);
            stop.status = "succeeded"; stop.processesExited = stop.portsAvailable = true;
            var stopDeadline = stop.deadline;
            Set("_existingServiceRecovery", record);
            Set("_stopObservation", stop);
            _manager.BeginServiceShutdown(record.id, "stop_restored", stop.deadline);
            var resetAttempts = 0;
            var replacements = 0;
            _manager.ServiceWorkResetOverride = _ => { ++resetAttempts; return "unexpected_reset"; };
            _manager.ExistingServiceReplacementStartOverride = current =>
            {
                ++replacements;
                var receipt = _manager.ResetActiveWorkForServiceOwner(current.id, "replacement_test_observer");
                Assert.That(receipt.Performed, Is.True);
                Assert.That(receipt.Error, Is.EqualTo("original_partial_cleanup_failure"));
                current.replacementPid = 42;
                current.status = "replacement_started";
                Set("_launchState", new ServerLaunchState { launcherPid = 42, deadlineUtcMs = current.deadline });
                Call("AllowServiceReplacement", current.id);
            };
            var callback = ScheduleRecoveryOnUpdate(ref record);
            Assert.That(RecoveryCallback(false), Is.Null, "Restoring must not depend on delayCall.");
            callback.DynamicInvoke();
            Assert.That(RecoveryCallback(true), Is.Null, "The first update must unsubscribe before recovery starts.");
            callback.DynamicInvoke(); // A captured late callback must not replay completed work.
            Assert.That(replacements, Is.EqualTo(1));
            Assert.That(resetAttempts + _stopDispatches + _terminations, Is.Zero);
            Assert.That(_manager.CaptureServiceShutdown().DeadlineUtcMs, Is.EqualTo(record.deadline));
            Assert.That(stop.deadline, Is.EqualTo(stopDeadline));
            Assert.That(_manager.IsOwnedResetVerificationPending(_manager.CaptureServiceShutdown()), Is.True);
            Assert.That(record.resetError, Is.EqualTo("original_partial_cleanup_failure"));
            Assert.That(Get<CancellationTokenSource>("_existingServiceRecoveryCancellation"), Is.Null);
        }

        [Test]
        public void ResumeCleaningWithUnknownLegacyReceiptFailsWithoutRepeatingResetOrLaunch()
        {
            StartupField("s_record").SetValue(null, new UPilotStartupRecord { failureSummaryLogged = true });
            StartupField("s_path").SetValue(null, null);
            var record = CreateCleanupReceiptRecord(out var stop);
            record.resetPerformed = false;
            record.resetError = "";
            stop.status = "succeeded"; stop.processesExited = stop.portsAvailable = true;
            Set("_existingServiceRecovery", record);
            Set("_stopObservation", stop);
            var resetAttempts = 0;
            var replacements = 0;
            _manager.ServiceWorkResetOverride = _ => { ++resetAttempts; return ""; };
            _manager.ExistingServiceReplacementStartOverride = _ => ++replacements;
            LogAssert.Expect(LogType.Error, new Regex("遗留服务恢复结束；recoveryId=" + Regex.Escape(record.id)));
            var callback = ScheduleRecoveryOnUpdate(ref record);
            callback.DynamicInvoke();
            callback.DynamicInvoke();
            Assert.That(record.status, Is.EqualTo("failed"));
            Assert.That(record.error, Does.Contain("reset_receipt_unknown"));
            Assert.That(resetAttempts + replacements + _stopDispatches + _terminations, Is.Zero);
            Assert.That(record.replacementStarted, Is.False);
            Assert.That(Get<CancellationTokenSource>("_existingServiceRecoveryCancellation"), Is.Null);
            LogAssert.NoUnexpectedReceived();
        }

        [TestCase("current", true)]
        [TestCase("unknown", false)]
        [TestCase("project", false)]
        [TestCase("lifetime", false)]
        [TestCase("unity_pid", false)]
        [TestCase("operation", false)]
        [TestCase("starting", false)]
        [TestCase("not_dispatched", false)]
        [TestCase("blocked", false)]
        [TestCase("expired", false)]
        [TestCase("deadline", false)]
        [TestCase("missing_spec", false)]
        [TestCase("changed_spec", false)]
        [TestCase("invalid_spec", false)]
        [TestCase("launcher_pid", false)]
        [TestCase("launcher_created", false)]
        [TestCase("missing_created", false)]
        [TestCase("terminal", false)]
        public void DispatchedRecoveryReceiptRequiresFrozenLaunchAndOriginalIdentity(string changed, bool expected)
        {
            var record = CreateDispatchedReceiptRecord(out var state);
            record = JsonUtility.FromJson<UPilotMcpServerManager.ExistingServiceRecoveryRecord>(JsonUtility.ToJson(record));
            if (changed == "unknown") { record.resetPerformed = false; record.resetError = ""; }
            if (changed == "project") record.projectPath = Path.Combine(record.projectPath, "another-project");
            if (changed == "lifetime") record.resetLifetime += "-old";
            if (changed == "unity_pid") ++record.resetUnityPid;
            if (changed == "operation") record.id = "";
            if (changed == "starting") record.status = "replacement_starting";
            if (changed == "not_dispatched") record.replacementStarted = false;
            if (changed == "blocked") record.replacementRestoreBlocked = true;
            if (changed == "expired") record.deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1;
            if (changed == "deadline") ++state.deadlineUtcMs;
            if (changed == "missing_spec") record.replacementSpec = null;
            if (changed == "changed_spec") ++record.replacementSpec.httpPort;
            if (changed == "invalid_spec") { record.replacementSpec.environmentKeys = null; state.spec.environmentKeys = null; }
            if (changed == "launcher_pid") ++record.replacementPid;
            if (changed == "launcher_created") ++record.replacementLauncherCreatedAt;
            if (changed == "missing_created") record.replacementLauncherCreatedAt = 0;
            if (changed == "terminal") state.terminal = true;
            Assert.That(UPilotMcpServerManager.CanRestoreExistingServiceReplacementResetReceipt(record, state,
                UPilotProjectConfig.ProjectRoot, UPilotServiceLifetime.Id, System.Diagnostics.Process.GetCurrentProcess().Id,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out var reason), Is.EqualTo(expected), reason);
            if (expected) Assert.That(record.resetError, Is.EqualTo("original_partial_cleanup_failure"));
        }

        [Test]
        public void ReplacementDispatchPersistsActualFrozenLaunchWithoutResetOrLaunchReplay()
        {
            var record = CreateDispatchedReceiptRecord(out var state);
            record.status = "replacement_starting";
            record.replacementPid = 0; record.replacementSpec = null; record.replacementLauncherCreatedAt = 0;
            Set("_existingServiceRecovery", record);
            Set("_launchState", state);
            Call("RecordExistingServiceReplacement", state.launcherPid);
            var persisted = JsonUtility.FromJson<UPilotMcpServerManager.ExistingServiceRecoveryRecord>(SessionState.GetString(RecoveryKey, ""));
            Assert.That(persisted.status, Is.EqualTo("replacement_started"));
            Assert.That(persisted.replacementPid, Is.EqualTo(state.launcherPid));
            Assert.That(persisted.replacementLauncherCreatedAt, Is.EqualTo(state.launcherCreatedAt));
            Assert.That(JsonUtility.ToJson(persisted.replacementSpec), Is.EqualTo(JsonUtility.ToJson(state.spec)));
            Assert.That(persisted.deadline, Is.EqualTo(record.deadline));
            Assert.That(persisted.resetError, Is.EqualTo("original_partial_cleanup_failure"));
            Assert.That(ReferenceEquals(record.replacementSpec, state.spec), Is.False);
            Assert.That(_stopDispatches + _terminations, Is.Zero);
        }

        [TestCase("replacement_starting", false)]
        [TestCase("replacement_started", false)]
        [TestCase("replacement_started", true)]
        public void UncertainDispatchedRecoveryKeepsAdmissionClosedWithoutResetOrLaunch(string status, bool missingSpec)
        {
            StartupField("s_record").SetValue(null, new UPilotStartupRecord { failureSummaryLogged = true });
            StartupField("s_path").SetValue(null, null);
            var restartField = typeof(UPilotServerRestartDiagnostics).GetField("s_record", StaticFields);
            var previousRestart = restartField.GetValue(null);
            var maintenanceField = typeof(UPilotServiceMaintenance).GetField("_journal", StaticFields);
            var previousMaintenance = maintenanceField.GetValue(null);
            try
            {
                restartField.SetValue(null, new UPilotServerRestartRecord { status = "succeeded" });
                maintenanceField.SetValue(null, FormatterServices.GetUninitializedObject(typeof(ServiceMaintenanceJournal)));
                var record = CreateDispatchedReceiptRecord(out _);
                record.status = status;
                if (missingSpec) record.spec = null;
                SessionState.SetString(RecoveryKey, JsonUtility.ToJson(record));
                SessionState.SetString(LaunchKey, "");
                var resets = 0;
                _manager.ServiceWorkResetOverride = _ => { ++resets; return "unexpected"; };
                LogAssert.Expect(LogType.Log, new Regex(@"^\[UPilot\]\[Lifecycle\] utc=\S+ pid=\d+ thread=\d+ eventId=[0-9a-f]{32} event=shutdown_protection_started\n" +
                    @"shutdownGeneration=1 ownerId=" + Regex.Escape(record.id) + @" phase=recovery_reload_receipt_verifying stopping=True replacementEligible=False " +
                    @"deadlineUtcMs=" + record.deadline + @" remainingBudgetMs=\d+ shutdownElapsedMs=\d+$"));
                LogAssert.Expect(LogType.Error, new Regex("遗留服务恢复结束；recoveryId=" + Regex.Escape(record.id)));
                Call("RestoreExistingServiceRecovery");
                var restored = Get<UPilotMcpServerManager.ExistingServiceRecoveryRecord>("_existingServiceRecovery");
                var shutdown = _manager.CaptureServiceShutdown();
                Assert.That(restored.status, Is.EqualTo("failed"));
                Assert.That(restored.replacementRestoreBlocked, Is.True);
                Assert.That(shutdown.OwnerId, Is.EqualTo(record.id));
                Assert.That(shutdown.DeadlineUtcMs, Is.EqualTo(record.deadline));
                Assert.That(shutdown.Stopping, Is.True);
                Assert.That(_manager.ExistingServiceReplacementNeedsReceiptRestore, Is.True);
                Assert.That(_manager.CanApplyHello(shutdown, out var reason), Is.False);
                Assert.That(reason, Is.EqualTo("shutdown_in_progress"));
                Assert.That(resets + _stopDispatches + _terminations, Is.Zero);
                Assert.That(Get<ServerLaunchState>("_launchState"), Is.Null);
                Assert.That(restored.resetError, Is.EqualTo("original_partial_cleanup_failure"));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                restartField.SetValue(null, previousRestart);
                maintenanceField.SetValue(null, previousMaintenance);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HistoricalBlockedRecoveryDoesNotDisplaceCurrentRestartOrMaintenance(bool maintenance)
        {
            var restartField = typeof(UPilotServerRestartDiagnostics).GetField("s_record", StaticFields);
            var maintenanceField = typeof(UPilotServiceMaintenance).GetField("_journal", StaticFields);
            var previousRestart = restartField.GetValue(null);
            var previousMaintenance = maintenanceField.GetValue(null);
            try
            {
                restartField.SetValue(null, new UPilotServerRestartRecord { status = maintenance ? "succeeded" : "running", operationId = "current-restart" });
                var journal = (ServiceMaintenanceJournal)FormatterServices.GetUninitializedObject(typeof(ServiceMaintenanceJournal));
                if (maintenance)
                    typeof(ServiceMaintenanceJournal).GetField("<Current>k__BackingField", InstanceFields).SetValue(journal,
                        new ServiceMaintenanceRecord { status = "running", maintenanceId = "current-maintenance" });
                maintenanceField.SetValue(null, journal);
                var record = CreateDispatchedReceiptRecord(out _);
                record.status = "failed"; record.error = "original_recovery_failure"; record.replacementRestoreBlocked = true;
                SessionState.SetString(RecoveryKey, JsonUtility.ToJson(record));
                Call("RestoreExistingServiceRecovery");
                var shutdown = _manager.CaptureServiceShutdown();
                Assert.That(shutdown.Stopping || shutdown.ReplacementEligible, Is.False);
                Assert.That(string.IsNullOrEmpty(shutdown.OwnerId), Is.True);
                var restored = Get<UPilotMcpServerManager.ExistingServiceRecoveryRecord>("_existingServiceRecovery");
                Assert.That(restored.status, Is.EqualTo("failed"));
                Assert.That(restored.error, Is.EqualTo("original_recovery_failure"));
                Assert.That(restored.resetError, Is.EqualTo("original_partial_cleanup_failure"));
                Assert.That(restored.replacementRestoreBlocked, Is.True);
                Assert.That(_stopDispatches + _terminations, Is.Zero);
            }
            finally
            {
                restartField.SetValue(null, previousRestart);
                maintenanceField.SetValue(null, previousMaintenance);
            }
        }

        private UPilotMcpServerManager.ExistingServiceRecoveryRecord CreateDispatchedReceiptRecord(out ServerLaunchState state)
        {
            var record = CreateCleanupReceiptRecord(out _);
            var executable = Path.Combine(record.projectPath, "upilot-test-receipt.exe");
            var spec = new ServerLaunchSpec { projectPath = record.projectPath, executablePath = executable,
                expectedEntry = executable, serviceExecutablePaths = new[] { executable },
                environmentKeys = Array.Empty<string>(), environmentValues = Array.Empty<string>(),
                httpPort = 18011, wsPort = 18765 };
            record.status = "replacement_started"; record.replacementStarted = true;
            record.replacementPid = 42; record.replacementLauncherCreatedAt = 987654321;
            record.replacementSpec = spec.Snapshot();
            state = new ServerLaunchState { spec = spec, launcherPid = record.replacementPid,
                launcherCreatedAt = record.replacementLauncherCreatedAt, deadlineUtcMs = record.deadline };
            return record;
        }

        [TestCase("stop")]
        [TestCase("reset")]
        [TestCase("reload")]
        public void CancelPendingRecoveryUnsubscribesUpdateAndIgnoresCapturedCallback(string cancellation)
        {
            var record = CreateCleanupReceiptRecord(out _);
            var callback = ScheduleRecoveryOnUpdate(ref record);
            var saved = JsonUtility.ToJson(record);
            if (cancellation == "stop") Call("CancelExistingServiceRecoveryForStop", "user-stop");
            else if (cancellation == "reset")
            {
                ExpectInterruptedRecoveryError(record);
                _manager.CancelExistingServiceRecoveryForReset("other-owner");
            }
            else Call("BeforeExistingServiceRecoveryReload");
            var afterCancellation = JsonUtility.ToJson(record);

            Assert.That(RecoveryCallback(true), Is.Null);
            callback.DynamicInvoke();
            Assert.That(JsonUtility.ToJson(record), Is.EqualTo(afterCancellation));
            if (cancellation == "reload") Assert.That(afterCancellation, Is.EqualTo(saved));
            Assert.That(Get<CancellationTokenSource>("_existingServiceRecoveryCancellation"), Is.Null);
            Assert.That(_stopDispatches + _terminations, Is.Zero);
            Assert.That(record.replacementStarted, Is.False);
            LogAssert.NoUnexpectedReceived();
        }

        private Delegate ScheduleRecoveryOnUpdate(ref UPilotMcpServerManager.ExistingServiceRecoveryRecord record)
        {
            SessionState.SetString(RecoveryKey, JsonUtility.ToJson(record));
            Call("RestoreExistingServiceRecovery");
            Call("RestoreExistingServiceRecovery"); // Repeated restoration must register only one update.
            record = Get<UPilotMcpServerManager.ExistingServiceRecoveryRecord>("_existingServiceRecovery");
            var callback = RecoveryCallback(true);
            Assert.That(callback, Is.Not.Null);
            Assert.That(EditorApplication.update.GetInvocationList().Count(d =>
                ReferenceEquals(d.Target, _manager) && d.Method.Name == "ResumeExistingServiceRecovery"), Is.EqualTo(1));
            return callback;
        }

        private Delegate RecoveryCallback(bool update) =>
            ((update ? EditorApplication.update : EditorApplication.delayCall)?.GetInvocationList() ?? Array.Empty<Delegate>())
                .FirstOrDefault(d => ReferenceEquals(d.Target, _manager) && d.Method.Name == "ResumeExistingServiceRecovery");

        private UPilotMcpServerManager.ExistingServiceRecoveryRecord CreateCleanupReceiptRecord(out ServiceStopObservationRecord stop)
        {
            var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 120000;
            var id = "restored-recovery-" + Guid.NewGuid().ToString("N");
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            stop = new ServiceStopObservationRecord {
                id = "stop-" + Guid.NewGuid().ToString("N"), requestId = id, shutdownOwnerId = id,
                project = UPilotProjectConfig.ProjectRoot, lifetime = UPilotServiceLifetime.Id,
                unityPid = pid, deadline = deadline - 90000, preserveShutdown = true,
            };
            return new UPilotMcpServerManager.ExistingServiceRecoveryRecord {
                id = id, projectPath = UPilotProjectConfig.ProjectRoot, status = "cleaning",
                spec = new ServerLaunchSpec { httpPort = _manager.HttpPort, wsPort = _manager.WsPort },
                deadline = deadline, stopObservationId = stop.id,
                resetPerformed = true, resetLifetime = UPilotServiceLifetime.Id, resetUnityPid = pid,
                resetError = "original_partial_cleanup_failure",
            };
        }

        private async Task RunRecoveryAsync(bool automatic, bool expired)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var scope = _manager.GetStopScope();
            var spec = (ServerLaunchSpec)Call("ExistingServiceComparisonSpec", scope);
            _record = new UPilotMcpServerManager.ExistingServiceRecoveryRecord {
                id = "readonly-recovery-test-" + Guid.NewGuid().ToString("N"),
                projectPath = scope.ProjectPath, pythonEntry = scope.PythonEntry, serverExe = scope.ServerExe,
                spec = spec, status = "verifying", automatic = automatic, revalidateOnly = true,
                deadline = now + 30000,
                reuseDeadline = expired ? now - 1 : now + UPilotMcpServerManager.ExistingServiceReuseBudgetMs,
            };
            var reuseDeadline = _record.reuseDeadline;
            Set("_existingServiceRecovery", _record);
            Set("_existingServiceRecoveryCancellation", new CancellationTokenSource());
            Set("_startInProgress", true);
            _work = (Task)Call("RecoverExistingServiceAsync", _record, scope, 13, 17L,
                Get<CancellationTokenSource>("_existingServiceRecoveryCancellation"), true);
            await _work;
            Assert.That(_record.reuseDeadline, Is.EqualTo(reuseDeadline), "Observation must not renew the original reuse budget.");
            Assert.That(_record.deadline, Is.EqualTo(now + 30000), "Observation must not renew the parent recovery deadline.");
        }

        private void AssertRecoveryDidNotStopOrLaunch()
        {
            Assert.That(_work.IsCompleted, Is.True);
            Assert.That(_record.revalidateOnly, Is.True);
            Assert.That(_record.replacementStarted, Is.False);
            Assert.That(_record.replacementPid, Is.Zero);
            Assert.That(_record.stopObservationId, Is.Empty);
            Assert.That(_stopDispatches, Is.Zero);
            Assert.That(_terminations, Is.Zero);
            Assert.That(_manager.StopObservation, Is.Null);
            Assert.That(Get<Task>("_stopPreparation"), Is.Null);
            Assert.That(_manager.LaunchIdentity, Is.Null);
            Assert.That(Get<System.Diagnostics.Process>("_launcherProcess"), Is.Null);
            Assert.That(Get<System.Diagnostics.Process>("_trackedProcess"), Is.Null);
            Assert.That(Get<CancellationTokenSource>("_existingServiceRecoveryCancellation"), Is.Null);
            Assert.That(Get<bool>("_startInProgress"), Is.False);
            Assert.That(Get<ServerLaunchSpec>("_pendingLaunchSpec"), Is.Null);
            Assert.That(Get<long>("_pendingLaunchDeadline"), Is.Zero);
            Assert.That(Get<int>("_startAttemptGeneration"), Is.EqualTo(13));
            Assert.That(Get<long>("_restartGeneration"), Is.EqualTo(17));
            Assert.That(SessionState.GetString(LaunchKey, _missing), Is.EqualTo(_session[LaunchKey]));
        }

        private static void ExpectInterruptedRecoveryError(UPilotMcpServerManager.ExistingServiceRecoveryRecord record)
        {
            var lifecycleDetail = $"recoveryId={record.id} stopId={record.stopObservationId} " +
                $"reason=service_reset deadlineUtcMs={record.deadline}";
            LogAssert.Expect(LogType.Log, new Regex(@"^\[UPilot\]\[Lifecycle\][^\r\n]* event=recovery_superseded\n" +
                Regex.Escape(lifecycleDetail) + "$"));
            var context = $"服务恢复验证被生命周期重置中断；recoveryId={record.id}；project={record.projectPath}；" +
                $"HTTP={record.spec.httpPort}；WS={record.spec.wsPort}；deadlineUtcMs={record.deadline}；reason=service_reset。";
            LogAssert.Expect(LogType.Error, new Regex(Regex.Escape(context)));
        }

        private static FieldInfo DeploymentField(string name) => typeof(UPilotDeploymentDiagnostics).GetField(name, StaticFields);
        private static FieldInfo StartupField(string name) => typeof(UPilotStartupDiagnostics).GetField(name, StaticFields);
        private static Dictionary<string, UPilotDeploymentIssue> DeploymentIssues =>
            (Dictionary<string, UPilotDeploymentIssue>)DeploymentField("Issues").GetValue(null);
        private void Set(string name, object value) => typeof(UPilotMcpServerManager).GetField(name, InstanceFields).SetValue(_manager, value);
        private T Get<T>(string name) => (T)typeof(UPilotMcpServerManager).GetField(name, InstanceFields).GetValue(_manager);
        private object Call(string name, params object[] arguments) =>
            typeof(UPilotMcpServerManager).GetMethod(name, InstanceFields).Invoke(_manager, arguments);
    }
}
