// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
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
