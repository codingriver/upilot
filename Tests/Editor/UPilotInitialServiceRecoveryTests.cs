// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotInitialServiceRecoveryTests
    {
        private const string LaunchKey = "UPilot.ServerLaunchIdentity.v1";
        private const string StopKey = "UPilot.ServiceStopObservation.v1";
        private const string ExistingRecoveryKey = "UPilot.ServerRecovery.Existing.v1";
        private const string EditorRecoveryKey = "UPilot.ServerRecovery.EditorSession.v1";
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo RestartRecord = typeof(UPilotServerRestartDiagnostics)
            .GetField("s_record", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo ExplicitlyStopped = typeof(UPilotQuickStart)
            .GetField("_explicitlyStopped", BindingFlags.NonPublic | BindingFlags.Static);
        private string _savedLaunch;
        private string _savedStop;
        private string _savedExistingRecovery;
        private bool _savedEditorRecovery;
        private object _savedRestart;
        private object _savedExplicitlyStopped;
        private readonly List<UPilotMcpServerManager> _managers = new List<UPilotMcpServerManager>();

        [SetUp]
        public void SaveState()
        {
            _savedLaunch = SessionState.GetString(LaunchKey, "");
            _savedStop = SessionState.GetString(StopKey, "");
            _savedExistingRecovery = SessionState.GetString(ExistingRecoveryKey, "");
            _savedEditorRecovery = SessionState.GetBool(EditorRecoveryKey, false);
            SessionState.EraseString(StopKey);
            SessionState.EraseString(ExistingRecoveryKey);
            // These managers emulate a Reload in the existing Editor, not a new Unity process.
            SessionState.SetBool(EditorRecoveryKey, true);
            _savedRestart = RestartRecord.GetValue(null);
            _savedExplicitlyStopped = ExplicitlyStopped.GetValue(null);
            // Synchronous isolated tests must never resume the project's actual restart record.
            RestartRecord.SetValue(null, new UPilotServerRestartRecord { status = "succeeded" });
            ExplicitlyStopped.SetValue(null, false);
        }

        [TearDown]
        public void RestoreState()
        {
            foreach (var manager in _managers)
            {
                EditorApplication.delayCall -= manager.EnsureInitialServiceRecovery;
                EditorApplication.update -= manager.EnsureInitialServiceRecovery;
                Call(manager, "StopLaunchObservation");
                Call(manager, "BeforeExistingServiceRecoveryReload");
                Field<System.Diagnostics.Process>(manager, "_launcherProcess")?.Dispose();
                Call(manager, "DisposeTrackedProcess");
                var cancellation = Field<CancellationTokenSource>(manager, "_lifecycleQueryCancellation");
                cancellation?.Cancel();
                cancellation?.Dispose();
                Field<CancellationTokenSource>(manager, "_stoppedIdentityRecoveryCancellation")?.Cancel();
            }
            _managers.Clear();
            RestartRecord.SetValue(null, _savedRestart);
            ExplicitlyStopped.SetValue(null, _savedExplicitlyStopped);
            if (string.IsNullOrEmpty(_savedLaunch)) SessionState.EraseString(LaunchKey);
            else SessionState.SetString(LaunchKey, _savedLaunch);
            if (string.IsNullOrEmpty(_savedStop)) SessionState.EraseString(StopKey);
            else SessionState.SetString(StopKey, _savedStop);
            if (string.IsNullOrEmpty(_savedExistingRecovery)) SessionState.EraseString(ExistingRecoveryKey);
            else SessionState.SetString(ExistingRecoveryKey, _savedExistingRecovery);
            SessionState.SetBool(EditorRecoveryKey, _savedEditorRecovery);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BootstrapWaitsForInitialRecoveryThenAdmitsOnlyColdStart(bool useUpdate)
        {
            SessionState.EraseString(LaunchKey);
            var manager = Manager();
            EditorApplication.delayCall += manager.EnsureInitialServiceRecovery;
            EditorApplication.update += manager.EnsureInitialServiceRecovery;
            var firstCallback = RecoveryCallback(manager, useUpdate);
            var otherCallback = RecoveryCallback(manager, !useUpdate);
            Assert.That(manager.InitialServiceRecoveryCompleted, Is.False);
            Assert.That(manager.CanAutomaticallyStartServer, Is.False);
            Assert.That(manager.CanAutomaticallyRepairDeployment, Is.False);

            // Invoke only this isolated manager's callback; never pump real Editor work.
            firstCallback.DynamicInvoke();

            Assert.That(manager.InitialServiceRecoveryCompleted, Is.True);
            Assert.That(manager.CanAutomaticallyStartServer, Is.True);
            Assert.That(manager.CanAutomaticallyRepairDeployment, Is.True);
            Assert.That(HasRecoveryCallback(manager), Is.False);
            Assert.That(manager.LaunchIdentity, Is.Null);
            Assert.That(Field<int>(manager, "_startAttemptGeneration"), Is.Zero);
            Assert.That(Field<long>(manager, "_pendingLaunchDeadline"), Is.Zero);

            // The other event may already have captured its delegate before unsubscription.
            var successor = SavedState("succeeded");
            Set(manager, "_launchState", successor);
            Call(manager, "PersistLaunchState");
            var persistedSuccessor = SessionState.GetString(LaunchKey, "");
            otherCallback.DynamicInvoke();
            Assert.That(Field<ServerLaunchState>(manager, "_launchState"), Is.SameAs(successor));
            Assert.That(SessionState.GetString(LaunchKey, ""), Is.EqualTo(persistedSuccessor));
            Assert.That(HasRecoveryCallback(manager), Is.False);
        }

        [Test]
        public void AnotherReloadBeforeInitialRecoveryDoesNotEraseTheOriginalIdentity()
        {
            var original = JsonUtility.ToJson(SavedState("observing"));
            SessionState.SetString(LaunchKey, original);
            var manager = Manager();
            EditorApplication.delayCall += manager.EnsureInitialServiceRecovery;
            EditorApplication.update += manager.EnsureInitialServiceRecovery;
            Assert.That(HasRecoveryCallback(manager), Is.True);

            Call(manager, "BeforeLaunchDomainReload");

            Assert.That(SessionState.GetString(LaunchKey, ""), Is.EqualTo(original));
            Assert.That(HasRecoveryCallback(manager), Is.False);
            Assert.That(manager.InitialServiceRecoveryCompleted, Is.False);
            Assert.That(manager.CanAutomaticallyStartServer, Is.False);
        }

        [TestCase(false, "observing")]
        [TestCase(true, "observing")]
        [TestCase(false, "unverified")]
        [TestCase(true, "unverified")]
        public void ExplicitEntryRestoresBeforeMergeAndOldCallbackCannotOverwriteSuccessor(bool restart, string previousStatus)
        {
            var original = SavedState(previousStatus);
            SessionState.SetString(LaunchKey, JsonUtility.ToJson(original));
            var manager = Manager();
            EditorApplication.CallbackFunction delayedRestore = manager.EnsureInitialServiceRecovery;
            EditorApplication.delayCall += delayedRestore;
            EditorApplication.update += delayedRestore;
            // Admission merges after recovery, so neither public entry can launch/stop real processes.
            Set(manager, "_restartPending", true);

            if (restart) manager.RestartPreparedServer();
            else manager.StartServer();

            var restored = manager.LaunchIdentity;
            Assert.That(manager.InitialServiceRecoveryCompleted, Is.True);
            Assert.That(manager.CanAutomaticallyStartServer, Is.False);
            Assert.That(manager.CanAutomaticallyRepairDeployment, Is.False);
            Assert.That(HasRecoveryCallback(manager), Is.False);
            Assert.That(restored.reloadRevalidation.observationId, Is.EqualTo(original.reloadRevalidation.observationId));
            Assert.That(restored.reloadRevalidation.deadlineUtcMs, Is.EqualTo(original.reloadRevalidation.deadlineUtcMs));
            Assert.That(restored.reloadRevalidation.attemptsUsed, Is.EqualTo(3));
            Assert.That(restored.reloadRevalidation.maxAttempts, Is.EqualTo(4));
            Assert.That(restored.reloadRevalidation.status, Is.EqualTo("unverified"));
            Assert.That(Field<int>(manager, "_startAttemptGeneration"), Is.Zero);
            Assert.That(Field<long>(manager, "_pendingLaunchDeadline"), Is.Zero);

            // Simulate an explicit successor having committed its new identity after admission.
            var successor = SavedState("succeeded");
            Set(manager, "_launchState", successor);
            Call(manager, "PersistLaunchState");
            var persistedSuccessor = SessionState.GetString(LaunchKey, "");
            delayedRestore();

            Assert.That(Field<ServerLaunchState>(manager, "_launchState"), Is.SameAs(successor));
            Assert.That(SessionState.GetString(LaunchKey, ""), Is.EqualTo(persistedSuccessor));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DuplicateStartPreservesDispatchedLaunchUntilReadyVerification(bool identityVerified)
        {
            var manager = Manager();
            Set(manager, "_initialServiceRecoveryCompleted", true);
            Set(manager, "_stoppingProcesses", new List<System.Diagnostics.Process>());
            Set(manager, "_startAttemptGeneration", 7);
            Set(manager, "_restartGeneration", 9L);
            Set(manager, "_restartOperationId", "");
            var entry = Path.Combine(UPilotProjectConfig.ProjectRoot, "PendingLaunchFixture.exe");
            manager.EndpointOverride = new UPilotMcpServerManager.ServerStopScope(
                UPilotProjectConfig.ProjectRoot, entry, entry, 18011, 18765);
            int mutations = 0;
            manager.StopDiscoveryOverride = (_, _, _) => { ++mutations; throw new InvalidOperationException("Duplicate Start must not stop a service."); };
            manager.StopTerminationOverride = _ => { ++mutations; throw new InvalidOperationException("Duplicate Start must not terminate a process."); };
            manager.ExistingServiceShutdownOverride = _ => { ++mutations; throw new InvalidOperationException("Duplicate Start must not reset service work."); };
            manager.ExistingServiceReplacementStartOverride = _ => { ++mutations; throw new InvalidOperationException("Duplicate Start must not dispatch a replacement."); };
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60000;
            var state = new ServerLaunchState {
                spec = new ServerLaunchSpec { projectPath = UPilotProjectConfig.ProjectRoot,
                    executablePath = entry, expectedEntry = entry, httpPort = 18011, wsPort = 18765 },
                launcherPid = current.Id, launcherCreatedAt = current.StartTime.ToUniversalTime().Ticks,
                deadlineUtcMs = deadline, verified = identityVerified, readyVerified = false,
            };
            Set(manager, "_launchState", state);
            Set(manager, "_trackedProcessId", (int?)current.Id);
            Set(manager, "_trackedProcessCreatedAtTicks", state.launcherCreatedAt);
            manager.SetLifecyclePhase(identityVerified ? UPilotServerLifecyclePhase.Verifying : UPilotServerLifecyclePhase.Starting,
                current.Id, "pending-launch-fixture", deadline);
            Call(manager, "PersistLaunchState");
            Call(manager, "BeginLaunchObservation");
            var observer = Field<CancellationTokenSource>(manager, "_launchObservationCancellation");
            var originalToken = observer.Token;
            var persisted = SessionState.GetString(LaunchKey, "");
            bool identityPreserved = false, observerPreserved = false, originalCanceled = false;
            bool recoveryCreated = false, persistencePreserved = false;
            int generation = -1;
            long lifecycleDeadline = 0;
            try
            {
                // Invoke the public entry while the first launch awaits identity/round-trip verification.
                // This synchronous test never pumps Editor callbacks; cancellation below precedes any
                // recovery continuation, and every stop/reset/replacement route is guarded above.
                manager.StartServer();
                identityPreserved = ReferenceEquals(Field<ServerLaunchState>(manager, "_launchState"), state);
                observerPreserved = ReferenceEquals(Field<CancellationTokenSource>(manager, "_launchObservationCancellation"), observer);
                originalCanceled = originalToken.IsCancellationRequested;
                recoveryCreated = Field<object>(manager, "_existingServiceRecovery") != null;
                persistencePreserved = SessionState.GetString(LaunchKey, "") == persisted;
                generation = Field<int>(manager, "_startAttemptGeneration");
                lifecycleDeadline = Field<long>(manager, "_lifecycleDeadlineUtcMs");
            }
            finally
            {
                Call(manager, "BeforeExistingServiceRecoveryReload");
                Call(manager, "StopLaunchObservation");
            }
            Assert.That(mutations, Is.Zero);
            Assert.That(identityPreserved, Is.True, "A repeated Start must retain the already-dispatched launch identity.");
            Assert.That(observerPreserved && !originalCanceled, Is.True, "The original verification observer must remain responsible.");
            Assert.That(recoveryCreated, Is.False, "The original launch must not be replaced with legacy-service recovery.");
            Assert.That(persistencePreserved, Is.True);
            Assert.That(generation, Is.EqualTo(7));
            Assert.That(lifecycleDeadline, Is.EqualTo(deadline));
            Assert.That(state.deadlineUtcMs, Is.EqualTo(deadline));
            Assert.That(state.readyVerified, Is.False, "Merging a Start is not readiness evidence.");
        }

        [TestCase("observing", false)]
        [TestCase("unverified", false)]
        [TestCase("succeeded", true)]
        public void AutomaticDeploymentRepairRequiresOriginalReloadObservationToSucceed(string status, bool allowed)
        {
            var manager = Manager();
            Set(manager, "_initialServiceRecoveryCompleted", true);
            Set(manager, "_initialServiceRecoveryHadPersistedState", true);
            var state = SavedState(status);
            state.terminal = false;
            Set(manager, "_launchState", state);
            // Even a ready-looking cache must not let an observing/failed record start a new repair.
            Assert.That(manager.PeriodicHealthChecksAllowed, Is.True);
            Assert.That(manager.CanAutomaticallyRepairDeployment, Is.EqualTo(allowed));
            Assert.That(manager.CanAutomaticallyStartServer, Is.False);
        }

        [TestCase("failed")]
        [TestCase("timed_out")]
        [TestCase("succeeded")]
        [TestCase("stopped")]
        public void TerminalStopPreservesOutcomeWithoutRestoringHistoricalLaunchGate(string terminal)
        {
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            var original = SavedState("observing");
            var executable = current.MainModule.FileName;
            var logPath = Path.Combine(UPilotProjectConfig.ProjectRoot, "log", "mcp-server.log");
            original.spec.executablePath = original.spec.expectedEntry = executable;
            original.spec.serviceExecutablePaths = new[] { executable };
            original.spec.arguments = $"--http-port {original.spec.httpPort} --port {original.spec.wsPort} --log-file \"{logPath}\"";
            original.launcherPid = original.servicePid = current.Id;
            original.launcherCreatedAt = original.serviceCreatedAt = current.StartTime.ToUniversalTime().Ticks;
            original.instanceId = "frozen-initial-recovery-fixture";
            var savedLaunch = JsonUtility.ToJson(original);
            var stop = SavedStop(terminal, original.spec.httpPort, original.spec.wsPort);
            var savedStop = JsonUtility.ToJson(stop);
            SessionState.SetString(LaunchKey, savedLaunch);
            SessionState.SetString(StopKey, savedStop);
            var manager = Manager();

            manager.EnsureInitialServiceRecovery();
            manager.EnsureInitialServiceRecovery();

            Assert.That(manager.InitialServiceRecoveryCompleted, Is.True);
            Assert.That(manager.StopBlocksAutomaticRecovery, Is.True);
            Assert.That(manager.CanAutomaticallyStartServer || manager.CanAutomaticallyRepairDeployment || manager.PeriodicHealthChecksAllowed, Is.False);
            Assert.That(manager.HasActiveStopObservation, Is.False);
            Assert.That(manager.GetStatus().LifecyclePhase, Is.EqualTo(UPilotServerLifecyclePhase.None));
            Assert.That(Field<CancellationTokenSource>(manager, "_launchObservationCancellation"), Is.Null);
            Assert.That(Field<bool>(manager, "_launchReloadRevalidation"), Is.False);
            Assert.That(Field<int>(manager, "_startAttemptGeneration"), Is.Zero);
            Assert.That(Field<long>(manager, "_pendingLaunchDeadline"), Is.Zero);
            Assert.That(SessionState.GetString(LaunchKey, ""), Is.EqualTo(savedLaunch));
            Assert.That(SessionState.GetString(StopKey, ""), Is.EqualTo(savedStop));
            Assert.That(manager.LaunchIdentity, Is.Null);
            Assert.That(Field<System.Diagnostics.Process>(manager, "_trackedProcess"), Is.Null);
            Assert.That(Field<System.Diagnostics.Process>(manager, "_launcherProcess"), Is.Null);
            Assert.That(manager.StoppedIdentityRecovery.IsCompleted, Is.True,
                "A new explicit start discovers current entry/ports; it must not wait for historical creation/parent evidence.");
        }

        [TestCase("unverified")]
        [TestCase("project")]
        [TestCase("ports")]
        [TestCase("missing_instance")]
        public void FailedStopDoesNotRestoreUntrustedFrozenIdentity(string invalid)
        {
            var original = SavedState("observing");
            original.launcherPid = original.servicePid = 42;
            original.instanceId = "original-instance";
            original.spec.arguments = $"--log-file \"{Path.Combine(UPilotProjectConfig.ProjectRoot, "log", "mcp-server.log")}\"";
            var stop = SavedStop("failed", original.spec.httpPort, original.spec.wsPort);
            if (invalid == "unverified") original.verified = false;
            if (invalid == "project") original.spec.projectPath += "-other-project";
            if (invalid == "ports") original.spec.httpPort++;
            if (invalid == "missing_instance") original.instanceId = "";
            var savedLaunch = JsonUtility.ToJson(original);
            SessionState.SetString(LaunchKey, savedLaunch);
            SessionState.SetString(StopKey, JsonUtility.ToJson(stop));
            var manager = Manager();

            manager.EnsureInitialServiceRecovery();

            Assert.That(manager.LaunchIdentity, Is.Null);
            Assert.That(Field<System.Diagnostics.Process>(manager, "_trackedProcess"), Is.Null);
            Assert.That(manager.StopBlocksAutomaticRecovery, Is.True);
            Assert.That(manager.CanAutomaticallyStartServer || manager.CanAutomaticallyRepairDeployment, Is.False);
            Assert.That(SessionState.GetString(LaunchKey, ""), Is.EqualTo(savedLaunch));
            Call(manager, "BeforeLaunchDomainReload");
            Assert.That(SessionState.GetString(LaunchKey, ""), Is.EqualTo(savedLaunch),
                "An unresolved stop must preserve the original frozen bytes for later verification.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HistoricalCandidateCannotPersistAChangedServiceIdentity(bool changedPid)
        {
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            var state = SavedState("succeeded");
            var executable = current.MainModule.FileName;
            state.spec.executablePath = state.spec.expectedEntry = executable;
            state.spec.serviceExecutablePaths = new[] { executable };
            state.spec.arguments = $"--http-port {state.spec.httpPort} --port {state.spec.wsPort} --log-file \"{Path.Combine(UPilotProjectConfig.ProjectRoot, "log", "mcp-server.log")}\"";
            state.launcherPid = state.servicePid = current.Id;
            state.launcherCreatedAt = state.serviceCreatedAt = current.StartTime.ToUniversalTime().Ticks;
            if (changedPid) state.servicePid++;
            else state.serviceCreatedAt++;
            state.verified = state.readyVerified = false;
            var status = new McpServerStatus { ProcessId = current.Id, ProcessOwnership = McpProcessOwnership.CurrentUPilot,
                HealthEndpointResponded = true, HealthIdentifiesUPilot = true, HealthServerProcessId = current.Id,
                HttpPortListening = true, WsPortListening = true, HealthProjectPath = UPilotProjectConfig.ProjectRoot,
                ProcessCommandLine = $"\"{executable}\" {state.spec.arguments}",
                Health = new UPilotServerHealth { server_pid = current.Id, project_path = UPilotProjectConfig.ProjectRoot,
                    configured_project_path = UPilotProjectConfig.ProjectRoot, server_entry_path = executable,
                    server_instance_id = "live-fixture-instance" } };
            var manager = Manager();
            Set(manager, "_launcherProcess", System.Diagnostics.Process.GetProcessById(current.Id));
            Set(manager, "_launchState", state);
            var original = JsonUtility.ToJson(state);
            SessionState.SetString(LaunchKey, original);
            var args = new object[] { state, status, null, true };

            var accepted = (bool)typeof(UPilotMcpServerManager).GetMethod("TryResolveStartedService", PrivateInstance)
                .Invoke(manager, args);

            Assert.That(accepted, Is.False);
            Assert.That(args[2], Is.EqualTo("service_identity_changed_or_expired"));
            Assert.That(state.verified, Is.False);
            Assert.That(Field<System.Diagnostics.Process>(manager, "_trackedProcess"), Is.Null);
            Assert.That(SessionState.GetString(LaunchKey, ""), Is.EqualTo(original));
        }

        [TestCase("valid", true)]
        [TestCase("failed", false)]
        [TestCase("missing_verification", false)]
        [TestCase("project", false)]
        [TestCase("ports", false)]
        [TestCase("missing_created_at", false)]
        [TestCase("entry", false)]
        public void HistoricalStopRecoveryOnlyCreatesAnUnverifiedExactCandidate(string scenario, bool accepted)
        {
            var state = SavedState("succeeded");
            state.spec.arguments = $"--log-file \"{Path.Combine(UPilotProjectConfig.ProjectRoot, "log", "mcp-server.log")}\"";
            var record = new UPilotServerRestartRecord { status = "succeeded", projectPath = UPilotProjectConfig.ProjectRoot,
                healthProjectPath = UPilotProjectConfig.ProjectRoot, launchSpec = state.spec, actualEntry = state.spec.expectedEntry,
                launcherProcessId = 42, launcherProcessCreatedAtTicks = 100, newProcessId = 43,
                newProcessCreatedAtTicks = 200, healthProcessId = 43, deadlineAtUtcMs = 1234,
                healthVerified = true, projectIdentityVerified = true, bridgeVerified = true,
                deploymentVerified = true, readOnlyVerified = true };
            var stop = SavedStop("failed", state.spec.httpPort, state.spec.wsPort);
            if (scenario == "failed") record.status = "failed";
            if (scenario == "missing_verification") record.readOnlyVerified = false;
            if (scenario == "project") record.projectPath += "-other";
            if (scenario == "ports") record.launchSpec.httpPort++;
            if (scenario == "missing_created_at") record.newProcessCreatedAtTicks = 0;
            if (scenario == "entry") record.actualEntry += "-other";
            var original = JsonUtility.ToJson(record);

            Assert.That(UPilotMcpServerManager.TryCreateStoppedIdentityRecoveryCandidate(record, stop, out var candidate), Is.EqualTo(accepted));

            Assert.That(JsonUtility.ToJson(record), Is.EqualTo(original));
            if (!accepted) { Assert.That(candidate, Is.Null); return; }
            Assert.That(candidate.verified || candidate.readyVerified || candidate.terminal, Is.False);
            Assert.That(candidate.instanceId, Is.Empty, "A missing historical instance must come only from live health.");
            Assert.That(candidate.deadlineUtcMs, Is.EqualTo(1234));
            Assert.That(candidate.launcherPid, Is.EqualTo(42));
            Assert.That(candidate.servicePid, Is.EqualTo(43));
            Assert.That(candidate.serviceCreatedAt, Is.EqualTo(200));
            Assert.That(candidate.reloadRevalidation, Is.Null);
            Assert.That(JsonUtility.ToJson(candidate.spec), Is.EqualTo(JsonUtility.ToJson(record.launchSpec)));
        }

        private static ServiceStopObservationRecord SavedStop(string terminal, int httpPort, int wsPort)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var stop = ServiceStopObservationRecord.Start(now, 30, 100);
            stop.project = UPilotProjectConfig.ProjectRoot;
            stop.lifetime = UPilotServiceLifetime.Id;
            stop.unityPid = System.Diagnostics.Process.GetCurrentProcess().Id;
            stop.httpPort = httpPort;
            stop.wsPort = wsPort;
            stop.End(now, terminal, "original-stop-result");
            return stop;
        }

        private static ServerLaunchState SavedState(string status)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var executable = Path.Combine(UPilotProjectConfig.ProjectRoot, "InitialRecoveryFixture.exe");
            return new ServerLaunchState
            {
                spec = new ServerLaunchSpec
                {
                    executablePath = executable, expectedEntry = executable,
                    projectPath = UPilotProjectConfig.ProjectRoot, serviceExecutablePaths = new[] { executable },
                    environmentKeys = Array.Empty<string>(), environmentValues = Array.Empty<string>(),
                    httpPort = 18011, wsPort = 18765,
                },
                // Negative PIDs exercise recovery failure without opening any real process handles.
                launcherPid = -1, servicePid = -2, launcherCreatedAt = 1, serviceCreatedAt = 2,
                verified = true, readyVerified = true, deadlineUtcMs = now - 200000,
                terminal = status == "unverified",
                reloadRevalidation = new ReloadRevalidationObservation
                {
                    observationId = "initial-recovery-" + Guid.NewGuid().ToString("N"), status = status,
                    intervalSeconds = 2, roundTimeoutSeconds = 5, totalTimeoutSeconds = 120,
                    maxAttempts = 4, attemptsUsed = 3, startedAtUtcMs = now - 1000,
                    deadlineUtcMs = now + 60000, endedAtUtcMs = status == "observing" ? 0 : now - 10,
                    endReason = status == "unverified" ? "previous_failure" : "",
                },
            };
        }

        private UPilotMcpServerManager Manager()
        {
            var manager = (UPilotMcpServerManager)FormatterServices.GetUninitializedObject(typeof(UPilotMcpServerManager));
            Set(manager, "_statusLock", new object());
            _managers.Add(manager);
            return manager;
        }
        private static bool HasRecoveryCallback(UPilotMcpServerManager manager) =>
            RecoveryCallback(manager, false) != null || RecoveryCallback(manager, true) != null;
        private static Delegate RecoveryCallback(UPilotMcpServerManager manager, bool useUpdate) =>
            ((useUpdate ? EditorApplication.update : EditorApplication.delayCall)?.GetInvocationList() ?? Array.Empty<Delegate>())
                .FirstOrDefault(callback => ReferenceEquals(callback.Target, manager) && callback.Method.Name == "EnsureInitialServiceRecovery");
        private static T Field<T>(UPilotMcpServerManager manager, string name) =>
            (T)typeof(UPilotMcpServerManager).GetField(name, PrivateInstance).GetValue(manager);
        private static void Set(UPilotMcpServerManager manager, string name, object value) =>
            typeof(UPilotMcpServerManager).GetField(name, PrivateInstance).SetValue(manager, value);
        private static void Call(UPilotMcpServerManager manager, string name) =>
            typeof(UPilotMcpServerManager).GetMethod(name, PrivateInstance).Invoke(manager, null);
    }
}
