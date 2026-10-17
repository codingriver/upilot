// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Manager = CodingRiver.UPilot.UPilotMcpServerManager;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotVerifiedStartupWaitTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private Manager _manager;
        private ServerLaunchState _launch;
        private BridgeStatus _bridge;
        private CancellationTokenSource _caller;
        private Task<McpServerStatus> _wait;
        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        [SetUp]
        public void SetUp()
        {
            // Never construct a Manager, register Editor callbacks, or launch a real process.
            _manager = (Manager)FormatterServices.GetUninitializedObject(typeof(Manager));
            Set("_statusLock", new object());
            Set("_startAttemptGeneration", 7);
            Set("_restartGeneration", 11L);
            Set("_statusGeneration", 13);
            Set("_initialServiceRecoveryCompleted", true);
            _caller = new CancellationTokenSource();
            var root = UPilotProjectConfig.ProjectRoot;
            var executable = Path.Combine(root, "upilot-mcp-server.exe");
            _manager.EndpointOverride = new Manager.ServerStopScope(root, "", executable, 18011, 18765);
            _launch = new ServerLaunchState {
                spec = new ServerLaunchSpec { projectPath = root, executablePath = executable,
                    expectedEntry = executable, httpPort = 18011, wsPort = 18765 },
                launcherPid = 41, launcherCreatedAt = 100, servicePid = 42, serviceCreatedAt = 102,
                instanceId = "startup-instance", verified = true, deadlineUtcMs = Now + 30000,
            };
            Set("_launchState", _launch);
            _bridge = new BridgeStatus { IsStarted = true, IsWsOpen = true,
                IsAuthenticated = true, SessionId = "startup-session" };
            _manager.StartupBridgeStatusOverride = () => _bridge;
            // Model the completed bootstrap gate independently of the identity evidence under test.
            // Never consult the live Editor Bridge from this isolated observer fixture.
            _manager.StateSynchronizedOverride = () => true;
        }

        [TearDown]
        public async Task TearDown()
        {
            _caller.Cancel();
            if (_wait != null)
            {
                Assert.That(await Task.WhenAny(_wait, Task.Delay(3000)), Is.SameAs(_wait),
                    "The observer must finish when its caller cancels.");
                try { await _wait; } catch (Exception) { }
            }
            _caller.Dispose();
            Assert.That(Get<Task>("_editorHealthObservationLoopTask"), Is.Null);
            Assert.That(Get<object>("_activeStatusRefresh"), Is.Null);
            Assert.That(Get<object>("_launcherProcess"), Is.Null);
            Assert.That(Get<object>("_trackedProcess"), Is.Null);
            Assert.That(Get<long>("_pendingLaunchDeadline"), Is.Zero);
        }

        [TestCase("0.3.51")]
        [TestCase("0.3.52")]
        public async Task MatchingVersionWaitsForCompleteVerificationAndCommittedCache(string installedVersion)
        {
            var deadline = _launch.deadlineUtcMs;
            Set("_cachedStatus", HealthyStatus());
            _wait = Wait();
            Assert.That(_wait.IsCompleted, Is.False, "A matching version is not readiness evidence.");
            _launch.readyVerified = true;
            await Task.Delay(150);
            Assert.That(_wait.IsCompleted, Is.False, "The ordinary cache has not committed verified startup evidence.");

            var verified = HealthyStatus();
            verified.ServerVersion = verified.Health.server_version = installedVersion;
            Publish(verified);
            var result = await Complete(_wait);
            Assert.That(result.ServerVersion, Is.EqualTo(installedVersion));
            Assert.That(result.StatusQueryCompleted, Is.True);
            Assert.That(result.ProcessId, Is.EqualTo(42));
            Assert.That(result.LastSuccessfulStatusAtUtcMs, Is.GreaterThan(0));
            Assert.That(_launch.deadlineUtcMs, Is.EqualTo(deadline));
            Assert.That(Get<int>("_startAttemptGeneration"), Is.EqualTo(7));
            Assert.That(Get<long>("_restartGeneration"), Is.EqualTo(11));
            Assert.That(Get<ServerLaunchState>("_launchState"), Is.SameAs(_launch));
        }

        [TestCase("pid")]
        [TestCase("identity_pid")]
        [TestCase("instance")]
        [TestCase("project")]
        [TestCase("http")]
        [TestCase("ws")]
        [TestCase("ownership")]
        [TestCase("generation")]
        [TestCase("revalidation")]
        [TestCase("bridge_auth")]
        [TestCase("bridge_session")]
        [TestCase("old_version")]
        [TestCase("failed_cache")]
        public async Task IncompleteOrMismatchedEvidenceNeverCompletesSuccessfully(string mismatch)
        {
            _launch.readyVerified = true;
            var status = HealthyStatus();
            switch (mismatch)
            {
                case "pid": status.ProcessId = 43; break;
                case "identity_pid": status.Identity.server_pid = 43; break;
                case "instance": status.Identity.server_instance_id = "other"; break;
                case "project": status.Identity.configured_project_path += "-other"; break;
                case "http": status.HttpPortListening = false; break;
                case "ws": status.WsPortListening = false; break;
                case "ownership": status.ProcessOwnership = McpProcessOwnership.Unknown; break;
                case "bridge_auth": _bridge.IsAuthenticated = false; break;
                case "bridge_session": _bridge.SessionId = "other"; break;
                case "old_version": status.ServerVersion = "0.3.50"; break;
                case "failed_cache": status.FailureKind = McpStatusFailureKind.Confirmed; status.ErrorMessage = "fixture_failure"; break;
            }
            Publish(status);
            if (mismatch == "generation") Set("_statusGeneration", 14);
            if (mismatch == "revalidation") Set("_launchNeedsRevalidation", true);
            _wait = Wait();
            await Task.Delay(150);
            Assert.That(_wait.Status, Is.Not.EqualTo(TaskStatus.RanToCompletion));
            _caller.Cancel();
            Assert.That(await Failure(_wait), mismatch == "old_version"
                ? Is.InstanceOf<InvalidOperationException>() : Is.InstanceOf<OperationCanceledException>());
        }

        [TestCase("missing_health")]
        [TestCase("conflicting_health")]
        [TestCase("ordinary_version")]
        public async Task AuxiliaryObservationsDoNotBlockVerifiedOrdinaryStartup(string observation)
        {
            _launch.readyVerified = true;
            var status = HealthyStatus();
            if (observation == "missing_health")
            {
                status.Health = null;
                status.HealthEndpointResponded = false;
                status.HealthIdentifiesUPilot = false;
            }
            else if (observation == "conflicting_health")
            {
                status.Health.server_pid = status.HealthServerProcessId = 99;
                status.Health.server_instance_id = "foreign-health-instance";
                status.Health.configured_project_path = status.HealthProjectPath = "foreign-health-project";
                status.Health.bridge_session_id = "foreign-health-session";
            }
            else
                status.ServerVersion = status.Identity.server_version = "0.1.0";
            Publish(status);
            _wait = Wait(expectedVersion: "");
            Assert.That((await Complete(_wait)).ProcessId, Is.EqualTo(_launch.servicePid));
            Assert.That(_launch.readyVerified, Is.True);
        }

        [TestCase("launch_replaced")]
        [TestCase("start_generation")]
        [TestCase("restart_generation")]
        [TestCase("stopping")]
        public async Task SupersedingOrStoppingOriginalOperationCancelsItsObserver(string change)
        {
            _wait = Wait();
            Assert.That(_wait.IsCompleted, Is.False);
            switch (change)
            {
                case "launch_replaced": Set("_launchState", _launch.Snapshot()); break;
                case "start_generation": Set("_startAttemptGeneration", 8); break;
                case "restart_generation": Set("_restartGeneration", 12L); break;
                case "stopping": Set("_lifecyclePhase", UPilotServerLifecyclePhase.Stopping); break;
            }
            Assert.That(await Failure(_wait), Is.InstanceOf<OperationCanceledException>());
            Assert.That(_launch.readyVerified, Is.False);
        }

        [Test]
        public async Task TerminalLaunchFailurePreservesTheOriginalError()
        {
            _wait = Wait();
            _launch.terminal = true;
            _launch.error = "fixture_readonly_probe_failed";
            var failure = await Failure(_wait);
            Assert.That(failure, Is.InstanceOf<InvalidOperationException>());
            Assert.That(failure.Message, Does.Contain(_launch.error));
        }

        [TestCase("replaced")]
        [TestCase("failed")]
        [TestCase("stopped")]
        [TestCase("user_stop")]
        public async Task RecoveryObserverDoesNotFollowAnotherRecoveryOrHideItsFailure(string change)
        {
            Set("_launchState", null);
            var recovery = new Manager.ExistingServiceRecoveryRecord {
                id = "original-recovery", status = "verifying", projectPath = _launch.spec.projectPath,
                spec = _launch.spec, deadline = _launch.deadlineUtcMs, reuseDeadline = _launch.deadlineUtcMs,
            };
            Set("_existingServiceRecovery", recovery);
            _wait = Wait();
            Assert.That(_wait.IsCompleted, Is.False);
            if (change == "replaced")
                Set("_existingServiceRecovery", new Manager.ExistingServiceRecoveryRecord {
                    id = "successor-recovery", status = "verifying", deadline = recovery.deadline,
                });
            else
            {
                recovery.status = change == "user_stop" ? "stopped" : change;
                recovery.error = change == "user_stop" ? "" : "fixture_recovery_" + change;
                // The recovery's own stop already advanced the generation before ending.
                Set("_startAttemptGeneration", 9);
                if (change == "user_stop") Set("_lifecyclePhase", UPilotServerLifecyclePhase.Stopping);
            }
            var failure = await Failure(_wait);
            var canceled = change == "replaced" || change == "user_stop";
            Assert.That(failure, canceled
                ? Is.InstanceOf<OperationCanceledException>() : Is.InstanceOf<InvalidOperationException>());
            if (!canceled) Assert.That(failure.Message, Does.Contain(recovery.error));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ObserverUsesEarlierOriginalOrCallerDeadlineWithoutExtendingIt(bool callerEarlier)
        {
            var originalDeadline = _launch.deadlineUtcMs = Now + (callerEarlier ? 5000 : 100);
            var callerDeadline = Now + (callerEarlier ? 100 : 5000);
            _wait = Wait(callerDeadline);
            Assert.That(await Failure(_wait), Is.InstanceOf<TimeoutException>());
            Assert.That(_launch.deadlineUtcMs, Is.EqualTo(originalDeadline));
            Assert.That(_launch.terminal, Is.False, "A waiting caller must not terminate its owned startup.");
        }

        [Test]
        public async Task CallerCancellationDoesNotCancelOrRestartTheOriginalLaunch()
        {
            var deadline = _launch.deadlineUtcMs;
            _wait = Wait();
            _caller.Cancel();
            Assert.That(await Failure(_wait), Is.InstanceOf<OperationCanceledException>());
            Assert.That(Get<ServerLaunchState>("_launchState"), Is.SameAs(_launch));
            Assert.That(_launch.deadlineUtcMs, Is.EqualTo(deadline));
            Assert.That(_launch.terminal, Is.False);
            Assert.That(Get<int>("_startAttemptGeneration"), Is.EqualTo(7));
        }

        [Test]
        public async Task MissingStartupFailsWithoutStartingOrProbingAService()
        {
            Set("_launchState", null);
            _wait = Wait();
            Assert.That(await Failure(_wait), Is.InstanceOf<InvalidOperationException>());
            Assert.That(Get<ServerLaunchState>("_launchState"), Is.Null);
            Assert.That(Get<Manager.ExistingServiceRecoveryRecord>("_existingServiceRecovery"), Is.Null);
            Assert.That(Get<int>("_startAttemptGeneration"), Is.EqualTo(7));
            Assert.That(Get<long>("_restartGeneration"), Is.EqualTo(11));
        }

        [TestCase("owned")]
        [TestCase("foreign_stop")]
        [TestCase("foreign_receipt")]
        public async Task RecoveryWaitFollowsOnlyItsOwnCleanupAndReplacement(string ownership)
        {
            Set("_launchState", null);
            var recovery = new Manager.ExistingServiceRecoveryRecord {
                id = "cleanup-recovery", status = "verifying", projectPath = _launch.spec.projectPath,
                spec = _launch.spec, deadline = _launch.deadlineUtcMs, reuseDeadline = _launch.deadlineUtcMs,
            };
            Set("_existingServiceRecovery", recovery);
            _wait = Wait();

            recovery.status = "cleaning";
            recovery.stopObservationId = "cleanup-stop";
            Set("_stopObservation", new ServiceStopObservationRecord {
                id = recovery.stopObservationId, requestId = ownership == "foreign_stop" ? "another-owner" : recovery.id,
                shutdownOwnerId = recovery.id, preserveShutdown = true,
                originalStartGeneration = 7, originalRestartGeneration = 11,
                project = recovery.projectPath, httpPort = recovery.spec.httpPort, wsPort = recovery.spec.wsPort,
            });
            Set("_lifecyclePhase", UPilotServerLifecyclePhase.Stopping);
            Set("_stopRestartGeneration", 11L);
            Set("_stopStartGeneration", 8);
            Set("_startAttemptGeneration", 8);
            if (ownership == "foreign_stop")
            {
                Assert.That(await Failure(_wait), Is.InstanceOf<OperationCanceledException>());
                return;
            }
            await Task.Delay(150);
            Assert.That(_wait.IsCompleted, Is.False, "Its own cleanup must not cancel the original observer.");
            // Discovery admission and termination dispatch each advance the owned stop generation.
            Set("_stopStartGeneration", 9);
            Set("_startAttemptGeneration", 9);
            await Task.Delay(150);
            Assert.That(_wait.IsCompleted, Is.False);

            recovery.verifiedStopResult = new Manager.ServerStopResult(true, true, 13,
                ownership == "foreign_receipt" ? 8 : 9, 11);
            recovery.status = "replacement_started";
            recovery.replacementPid = _launch.launcherPid;
            recovery.replacementLauncherCreatedAt = _launch.launcherCreatedAt;
            recovery.replacementSpec = _launch.spec.Snapshot();
            Set("_stopObservation", null);
            Set("_lifecyclePhase", UPilotServerLifecyclePhase.None);
            Set("_launchState", _launch);
            _launch.readyVerified = true;
            Publish(HealthyStatus());

            if (ownership == "foreign_receipt")
                Assert.That(await Failure(_wait), Is.InstanceOf<OperationCanceledException>());
            else
                Assert.That((await Complete(_wait)).ProcessId, Is.EqualTo(_launch.servicePid));
            Assert.That(Get<Manager.ExistingServiceRecoveryRecord>("_existingServiceRecovery"), Is.SameAs(recovery));
            Assert.That(_launch.deadlineUtcMs, Is.EqualTo(recovery.deadline));
            Assert.That(Get<int>("_startAttemptGeneration"), Is.EqualTo(9));
        }

        private Task<McpServerStatus> Wait(long deadline = 0, string expectedVersion = "0.3.51") =>
            _manager.WaitForVerifiedStartupAsync(expectedVersion, deadline, _caller.Token);

        private McpServerStatus HealthyStatus() => new McpServerStatus {
            IsRunning = true, HttpPortListening = true, WsPortListening = true,
            ProcessId = 42, ProcessOwnership = McpProcessOwnership.CurrentUPilot,
            ProcessOwnershipEvidence = "fixture OS identity", HealthEndpointResponded = true,
            HealthIdentifiesUPilot = true, HealthServerProcessId = 42, ServerVersion = "0.3.51",
            HealthProjectPath = _launch.spec.projectPath, StatusGeneration = 13,
            IdentityAvailable = true, IdentityIdentifiesUPilot = true, IdentityServerProcessId = 42,
            IdentityProjectPath = _launch.spec.projectPath,
            Identity = new UPilotServerHealth { server_pid = 42, server_instance_id = "startup-instance",
                configured_project_path = _launch.spec.projectPath, project_path = _launch.spec.projectPath,
                server_version = "0.3.51", bridge_session_id = "startup-session" },
            Health = new UPilotServerHealth { server_pid = 42, server_instance_id = "startup-instance",
                configured_project_path = _launch.spec.projectPath, project_path = _launch.spec.projectPath,
                server_version = "0.3.51", bridge_session_id = "startup-session" },
        };

        private void Publish(McpServerStatus status)
        {
            // The outer startup owns completion while its verified result is committed.
            // Keep periodic observation paused without registering callbacks or performing I/O.
            Set("_startInProgress", true);
            try
            {
                var committed = (bool)typeof(Manager).GetMethod("CacheVerifiedStartupStatus", PrivateInstance)
                    .Invoke(_manager, new object[] { status, -1L, _bridge.SessionId, null });
                Assert.That(committed, Is.True, "The synchronized fixture must publish the cache used by the original observer.");
            }
            finally { Set("_startInProgress", false); }
        }

        private async Task<McpServerStatus> Complete(Task<McpServerStatus> task)
        {
            Assert.That(await Task.WhenAny(task, Task.Delay(3000)), Is.SameAs(task));
            return await task;
        }

        private async Task<Exception> Failure(Task<McpServerStatus> task)
        {
            try { await Complete(task); }
            catch (Exception exception) { return exception; }
            Assert.Fail("The startup observation unexpectedly succeeded.");
            return null;
        }

        private void Set(string name, object value) =>
            typeof(Manager).GetField(name, PrivateInstance).SetValue(_manager, value);
        private T Get<T>(string name) =>
            (T)typeof(Manager).GetField(name, PrivateInstance).GetValue(_manager);
    }
}
