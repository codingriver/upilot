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
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo RestartRecord = typeof(UPilotServerRestartDiagnostics)
            .GetField("s_record", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo ExplicitlyStopped = typeof(UPilotQuickStart)
            .GetField("_explicitlyStopped", BindingFlags.NonPublic | BindingFlags.Static);
        private string _savedLaunch;
        private object _savedRestart;
        private object _savedExplicitlyStopped;
        private readonly List<UPilotMcpServerManager> _managers = new List<UPilotMcpServerManager>();

        [SetUp]
        public void SaveState()
        {
            _savedLaunch = SessionState.GetString(LaunchKey, "");
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
                var cancellation = Field<CancellationTokenSource>(manager, "_lifecycleQueryCancellation");
                cancellation?.Cancel();
                cancellation?.Dispose();
            }
            _managers.Clear();
            RestartRecord.SetValue(null, _savedRestart);
            ExplicitlyStopped.SetValue(null, _savedExplicitlyStopped);
            if (string.IsNullOrEmpty(_savedLaunch)) SessionState.EraseString(LaunchKey);
            else SessionState.SetString(LaunchKey, _savedLaunch);
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
