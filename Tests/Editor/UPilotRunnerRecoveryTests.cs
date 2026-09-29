using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public class UPilotRunnerRecoveryTests
    {
        private sealed class MissingApi { }
        private sealed class ProbeHolder
        {
            public bool Active;
            public object GetRunner(string runGuid) => Active ? this : null;
            public IEnumerable TestRuns => Array.Empty<object>();
        }
        private sealed class ProbeWithoutCancelApi
        {
            public static ProbeHolder m_testJobDataHolder = new ProbeHolder();
        }
        private class InheritedSingletonBase
        {
            public static object instance { get; } = new object();
        }
        private sealed class InheritedSingletonHolder : InheritedSingletonBase { }
        private sealed class ThrowingHolder
        {
            public object GetRunner(string runGuid) { return null; }
            public IEnumerable TestRuns => Array.Empty<object>();
        }

        private sealed class ThrowingHolderApi
        {
            public static ThrowingHolder m_testJobDataHolder =>
                throw new InvalidOperationException("P0_EXPECTED_HOLDER_BINDING_FAILURE");
            public static bool CancelTestRun(string runGuid) { return false; }
            public static void UnregisterTestCallback<T>(T callback) { }
        }

        public sealed class ReleaseApiStub : ScriptableObject { }

        [Test]
        public void DirtyScenePreflightBlocksNamedUntitledAndMultipleScenesBeforeRunnerStart()
        {
            bool runnerStarted = false;
            var scenes = new[]
            {
                new SceneInfoPayload { scenePath = "Assets/Clean.unity", sceneName = "Clean", isDirty = false },
                new SceneInfoPayload { scenePath = "Assets/Named.unity", sceneName = "Named", isDirty = true, isActive = true },
                new SceneInfoPayload { scenePath = "", sceneName = "Untitled", isDirty = true },
            };

            var error = Assert.Throws<TestRunPreflightException>(() =>
            {
                UPilotTestService.EnsureCleanScenesBeforeRunner(scenes, "ignore");
                runnerStarted = true;
            });

            Assert.That(runnerStarted, Is.False);
            Assert.That(error.DirtyScenePolicy, Is.EqualTo("ignore"));
            Assert.That(error.DirtyScenes, Has.Length.EqualTo(2));
            Assert.That(error.DirtyScenes[0].scenePath, Is.EqualTo("Assets/Named.unity"));
            Assert.That(error.DirtyScenes[0].isActive, Is.True);
            Assert.That(error.DirtyScenes[1].scenePath, Is.Empty);
        }

        [Test]
        public void CleanSavedScenesPassRunnerPreflight()
        {
            Assert.DoesNotThrow(() => UPilotTestService.EnsureCleanScenesBeforeRunner(
                new[]
                {
                    new SceneInfoPayload
                    {
                        scenePath = "Assets/Saved.unity", sceneName = "Saved", isDirty = false, isActive = true,
                    },
                },
                "block"));
        }

        [Test]
        public void MissingBindingCannotReportInactive()
        {
            var adapter = UPilotTestRunnerAdapter.Get(typeof(MissingApi));
            var state = adapter.Probe(Guid.NewGuid().ToString(), out var runner, out var diagnostic);
            Assert.That(state, Is.EqualTo("unknown"));
            Assert.That(runner, Is.Null);
            Assert.That(diagnostic, Does.Contain("TEST_RUNNER_BINDING_UNAVAILABLE"));
            Assert.That(diagnostic, Does.Contain(typeof(MissingApi).Assembly.FullName));
            Assert.That(diagnostic, Does.Contain("candidates="));
        }

        [Test]
        public void StaticSingletonPropertyCanBeResolvedFromBaseType()
        {
            var property = UPilotTestRunnerAdapter.FindStaticProperty(typeof(InheritedSingletonHolder), "instance");
            Assert.That(property, Is.Not.Null);
            Assert.That(property.DeclaringType, Is.EqualTo(typeof(InheritedSingletonBase)));
            Assert.That(property.GetValue(null), Is.SameAs(InheritedSingletonBase.instance));
        }

        [Test]
        public void MissingBindingDiagnosticIsCachedAcrossProbes()
        {
            var adapter = UPilotTestRunnerAdapter.Get(typeof(MissingApi));
            adapter.Probe("first", out _, out var first);
            adapter.Probe("second", out _, out var second);
            Assert.That(second, Is.SameAs(first));
        }

        [Test]
        public void RepeatedUnchangedCleanupProbeBacksOffWithoutPersistingAgain()
        {
            var snapshot = new TestRunResultPayload
            {
                runGuid = "unchanged-cleanup",
                cleanupAttemptDeadlineAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30000,
                status = "cleanup",
                phase = "cleanup",
                isRunning = true,
                cleanupPending = true,
                runnerState = "active",
            };
            var service = DetachedService(snapshot);
            SetField(service, "_activeRunGuid", snapshot.runGuid);
            SetField(service, "_cleanupProbeDelayMs", 100L);
            SetField(service, "_nextCleanupProbeAt", 0L);
            service.RunnerAdapterResolverForTests = _ => UPilotTestRunnerAdapter.Get(typeof(MissingApi));
            int saves = 0;
            service.SnapshotSaverForTests = (_, __, ___) => saves++;
            var cleanup = typeof(UPilotTestService).GetMethod("CleanupActiveRun", BindingFlags.NonPublic | BindingFlags.Instance);

            cleanup.Invoke(service, null);
            Assert.That(saves, Is.EqualTo(1));
            Assert.That(snapshot.runnerState, Is.EqualTo("unknown"));
            Assert.That(snapshot.unresolvedResources, Does.Contain("test-runner-job"));
            Assert.That((long)GetField(service, "_cleanupProbeDelayMs"), Is.EqualTo(100));

            SetField(service, "_nextCleanupProbeAt", 0L);
            cleanup.Invoke(service, null);
            Assert.That(saves, Is.EqualTo(1));
            Assert.That((long)GetField(service, "_cleanupProbeDelayMs"), Is.EqualTo(200));
            for (int i = 0; i < 8; i++)
            {
                SetField(service, "_nextCleanupProbeAt", 0L);
                cleanup.Invoke(service, null);
            }
            Assert.That((long)GetField(service, "_cleanupProbeDelayMs"), Is.EqualTo(2000));
            Assert.That(saves, Is.EqualTo(1));
            Assert.That(snapshot.cleanupErrors.Count(error => error.StartsWith("runner-observation:")), Is.EqualTo(1));
            Assert.That(snapshot.cleanupResourcesReleased, Is.False);
        }

        [Test]
        public void HolderGetterFailureReportsUnknownWithIdentity()
        {
            var adapter = UPilotTestRunnerAdapter.Get(typeof(ThrowingHolderApi));
            var state = adapter.Probe(Guid.NewGuid().ToString(), out var runner, out var diagnostic);
            Assert.That(state, Is.EqualTo("unknown"));
            Assert.That(runner, Is.Null);
            Assert.That(diagnostic, Does.Contain(typeof(ThrowingHolderApi).Assembly.FullName));
            Assert.That(diagnostic, Does.Contain("P0_EXPECTED_HOLDER_BINDING_FAILURE"));
        }

        [Test]
        public void CancelBindingFailureKeepsRunNonTerminalAndUncalled()
        {
            var service = DetachedService(new TestRunResultPayload
            {
                status = "running", phase = "running", isRunning = true, runnerState = "active",
            });
            SetField(service, "_activeRunGuid", "binding-failure-run");
            ProbeWithoutCancelApi.m_testJobDataHolder.Active = true;
            service.SnapshotSaverForTests = (_, __, ___) => { };
            service.RunnerAdapterResolverForTests = _ => UPilotTestRunnerAdapter.Get(typeof(ProbeWithoutCancelApi));

            var invocation = Assert.Throws<TargetInvocationException>(() =>
                typeof(UPilotTestService).GetMethod("RequestCancel", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(service, new object[] { false }));
            Assert.That(invocation.InnerException, Is.TypeOf<InvalidOperationException>());
            var status = service.GetStatusSnapshot();
            Assert.That(status.status, Is.EqualTo("cleanup"));
            Assert.That(status.phase, Is.EqualTo("recovery_required"));
            Assert.That(status.cleanupErrors.Single(), Does.StartWith("cancel-binding:"));
            Assert.That(status.isRunning, Is.True);
            Assert.That(status.cancelRequested, Is.False);
            Assert.That(status.cancelAttemptCount, Is.Zero);
            Assert.That(status.runnerState, Is.EqualTo("active"));
            Assert.That(status.recoveryDiagnostic, Does.Contain("no cancel API was invoked"));
            Assert.That(status.recoveryDiagnostic, Does.Contain("TEST_CANCEL_BINDING_UNAVAILABLE"));
        }

        [Test]
        public void CallbackReleaseFailureRetainsCallbackAndApiAsUnresolved()
        {
            var service = DetachedService(new TestRunResultPayload
            {
                status = "cleanup", phase = "cleanup", cleanupPending = true, runnerState = "inactive",
            });
            var apiType = FindInstalledApiType();
            var callbackType = apiType.Assembly.GetType("UnityEditor.TestTools.TestRunner.Api.ICallbacks", true);
            var callback = typeof(UPilotTestService).GetMethod("CreateCallbackProxy", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(service, new object[] { callbackType });
            var api = ScriptableObject.CreateInstance<ReleaseApiStub>();
            SetField(service, "_activeCallback", callback);
            SetField(service, "_activeApi", api);
            SetField(service, "_activeRunGuid", "release-failure-run");
            service.RunnerAdapterResolverForTests = _ => UPilotTestRunnerAdapter.Get(apiType);
            service.CallbackUnregisterInvokerForTests = (_, __, ___) =>
                throw new InvalidOperationException("P0_EXPECTED_CALLBACK_RELEASE_FAILURE");
            int destroyCalls = 0;
            service.ApiDestroyerForTests = _ => destroyCalls++;

            Assert.That(service.ReleaseOwnedRunnerResources(), Is.False);
            Assert.That(GetField(service, "_activeCallback"), Is.SameAs(callback));
            Assert.That(GetField(service, "_activeApi"), Is.SameAs(api));
            Assert.That(destroyCalls, Is.Zero);
            var status = service.GetStatusSnapshot();
            Assert.That(status.cleanupPending, Is.True);
            Assert.That(status.unresolvedResources, Does.Contain("test-callback"));
            Assert.That(status.unresolvedResources, Does.Contain("test-runner-api"));
            Assert.That(status.cleanupErrors.Single(error => error.StartsWith("callback-unregister:")),
                Does.Contain("P0_EXPECTED_CALLBACK_RELEASE_FAILURE"));
            UnityEngine.Object.DestroyImmediate(api);
        }

        [Test]
        public void ApiReleaseFailureRetainsApiAsUnresolved()
        {
            var service = DetachedService(new TestRunResultPayload
            {
                status = "cleanup", phase = "cleanup", cleanupPending = true, runnerState = "inactive",
            });
            var api = ScriptableObject.CreateInstance<ReleaseApiStub>();
            SetField(service, "_activeApi", api);
            SetField(service, "_activeRunGuid", "api-release-failure-run");
            service.ApiDestroyerForTests = _ =>
                throw new InvalidOperationException("P0_EXPECTED_API_RELEASE_FAILURE");

            Assert.That(service.ReleaseOwnedRunnerResources(), Is.False);
            Assert.That(GetField(service, "_activeApi"), Is.SameAs(api));
            var status = service.GetStatusSnapshot();
            Assert.That(status.cleanupPending, Is.True);
            Assert.That(status.unresolvedResources, Does.Contain("test-runner-api"));
            Assert.That(status.cleanupErrors.Single(error => error.StartsWith("api-release:")),
                Does.Contain("P0_EXPECTED_API_RELEASE_FAILURE"));
            UnityEngine.Object.DestroyImmediate(api);
        }

        [Test]
        public void InstalledRunnerProbeUsesExactGuidAndProperty()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi"))
                .First(candidate => candidate != null);
            var adapter = UPilotTestRunnerAdapter.Get(type);
            var state = adapter.Probe(Guid.NewGuid().ToString(), out var runner, out var diagnostic);
            Assert.That(state, Is.EqualTo("inactive"), diagnostic);
            Assert.That(runner, Is.Null);
            var active = UPilotTestService.Instance.GetStatusSnapshot().runGuid;
            Assert.That(adapter.Probe(active, out runner, out diagnostic), Is.EqualTo("active"), diagnostic);
            Assert.That(runner, Is.Not.Null);
        }

        [Test]
        public void InstalledCancelBindingMatchesPublicCapability()
        {
            var type = FindInstalledApiType();
            var method = type.GetMethod("CancelTestRun", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string) }, null);
            if (method == null)
                Assert.That(Assert.Throws<InvalidOperationException>(() => { var unused = UPilotTestRunnerAdapter.Get(type).Cancel; }).Message,
                    Does.Contain("TEST_CANCEL_BINDING_UNAVAILABLE"));
            else
                Assert.That(UPilotTestRunnerAdapter.Get(type).Cancel, Is.EqualTo(method));
        }

        [TestCase("completed")]
        [TestCase("failed")]
        public void InactiveRunnerWithoutCancelCleansUpAndPreservesResult(string outcome)
        {
            var snapshot = new TestRunResultPayload
            {
                runGuid = "inactive-no-cancel", status = "cleanup", phase = "cleanup",
                outcomeStatus = outcome, resultAuthoritative = true,
            };
            var service = DetachedService(snapshot);
            SetField(service, "_activeRunGuid", snapshot.runGuid);
            ProbeWithoutCancelApi.m_testJobDataHolder.Active = false;
            service.RunnerAdapterResolverForTests = _ => UPilotTestRunnerAdapter.Get(typeof(ProbeWithoutCancelApi));
            service.SnapshotSaverForTests = (_, __, ___) => { };
            try
            {
                service.ForceCleanupActiveRun(snapshot.runGuid);
                typeof(UPilotTestService).GetMethod("CleanupActiveRun", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(service, null);
                Assert.That(service.IsRunning, Is.False);
                Assert.That(service.GetStatusSnapshot().outcomeStatus, Is.EqualTo(outcome));
                Assert.That(service.GetStatusSnapshot().status, Is.EqualTo(outcome));
                Assert.That(snapshot.cancelAttemptCount, Is.Zero);
            }
            finally { DetachCleanup(service); }
        }

        [Test]
        public void DestroyedNativeApiUnregistersOnlyItsOriginalCallback()
        {
            var apiType = FindInstalledApiType();
            var callbackType = apiType.Assembly.GetType("UnityEditor.TestTools.TestRunner.Api.ICallbacks", true);
            var service = DetachedService(new TestRunResultPayload { status = "cleanup" });
            var create = typeof(UPilotTestService).GetMethod("CreateCallbackProxy", BindingFlags.NonPublic | BindingFlags.Instance);
            var callback = create.Invoke(service, new object[] { callbackType });
            var api = ScriptableObject.CreateInstance(apiType);
            var register = typeof(UPilotTestService).GetMethod("RegisterCallbacks", BindingFlags.NonPublic | BindingFlags.Static);
            var holderType = apiType.Assembly.GetType("UnityEditor.TestTools.TestRunner.Api.CallbacksHolder", true);
            var holder = holderType.BaseType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            var getAll = holderType.GetMethod("GetAll");
            var before = ((IEnumerable)getAll.Invoke(holder, null)).Cast<object>().ToArray();
            try
            {
                register.Invoke(null, new object[] { apiType, api, callbackType, callback });
                SetField(service, "_activeApi", api);
                SetField(service, "_activeCallback", callback);
                UnityEngine.Object.DestroyImmediate(api);
                Assert.That(api == null, Is.True);
                Assert.That(ReferenceEquals(api, null), Is.False);
                Assert.That(service.GetStatusSnapshot().unresolvedResources, Does.Contain("test-runner-api"));
                Assert.That(UPilotTestRunnerAdapter.Get(apiType).Unregister.IsStatic, Is.False);
                Assert.That(service.ReleaseOwnedRunnerResources(), Is.True);
                Assert.That(GetField(service, "_activeApi"), Is.Null);
                CollectionAssert.AreEqual(before, ((IEnumerable)getAll.Invoke(holder, null)).Cast<object>().ToArray());
            }
            finally
            {
                var unregister = UPilotTestRunnerAdapter.Get(apiType).Unregister.MakeGenericMethod(callbackType);
                unregister.Invoke(unregister.IsStatic ? null : api, new[] { callback });
                if (api != null) UnityEngine.Object.DestroyImmediate(api);
            }
        }

        private static void DetachCleanup(UPilotTestService service)
        {
            foreach (string name in new[] { "CleanupActiveRunFromUpdate", "ForceStopTick" })
                EditorApplication.update -= (EditorApplication.CallbackFunction)Delegate.CreateDelegate(
                    typeof(EditorApplication.CallbackFunction), service,
                    typeof(UPilotTestService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance));
            EditorApplication.delayCall -= (EditorApplication.CallbackFunction)Delegate.CreateDelegate(
                typeof(EditorApplication.CallbackFunction), service,
                typeof(UPilotTestService).GetMethod("CleanupActiveRun", BindingFlags.NonPublic | BindingFlags.Instance));
        }

        [Test]
        public void BridgeAttachmentKeepsCallbackOwner()
        {
            var owner = UPilotTestService.Instance;
            Assert.That(owner, Is.Not.Null);
            // Do not replace the actual bridge while its acceptance test is running.
            var bridge = typeof(UPilotTestService).GetField("_bridge", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(owner);
            Assert.That(UPilotTestService.AttachBridge((UPilotBridge)bridge), Is.SameAs(owner));
        }

        [Test]
        public void RecoveryDeadlineDoesNotInventAbortedOrForgetIdentity()
        {
            var service = (UPilotTestService)FormatterServices.GetUninitializedObject(typeof(UPilotTestService));
            var snapshot = new TestRunResultPayload { runGuid = "", resultAuthoritative = false, status = "running" };
            typeof(UPilotTestService).GetField("_lastResults", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(service, snapshot);
            typeof(UPilotTestService).GetMethod("MarkRecoveredRunOrphaned", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
            Assert.That(snapshot.status, Is.EqualTo("running"));
            Assert.That(snapshot.phase, Is.EqualTo("recovery_required"));
            Assert.That(snapshot.outcomeStatus, Is.EqualTo("unknown"));
            Assert.That(snapshot.resultAuthoritative, Is.False);
            Assert.That(snapshot.endedAt, Is.Zero);
        }

        [Test]
        public void MissingHistoricalAuthorityNeverDefaultsToSuccess()
        {
            Assert.That(new TestRunResultPayload().resultAuthoritative, Is.False);
            var old = JsonUtility.FromJson<TestRunResultPayload>("{\"runGuid\":\"old\",\"status\":\"completed\",\"passed\":1}");
            Assert.That(old.resultAuthoritative, Is.False);
            Assert.That(old.cleanupSucceeded, Is.False);
        }

        [Test]
        public void PersistedLeafStreamRetainsSequenceAndCursorGapAfterRecoveryRead()
        {
            string directory = Path.Combine(Path.GetTempPath(), "UPilotTestRuns", Guid.NewGuid().ToString("N"));
            const string runGuid = "11111111-1111-1111-1111-111111111111";
            try
            {
                var snapshot = new TestRunResultPayload
                {
                    runGuid = runGuid,
                    resultStreamVersion = 7,
                    nextEventSequence = 10001,
                    earliestEventSequence = 2,
                    eventsTruncated = true,
                    events = new List<TestRunEventPayload>
                    {
                        new TestRunEventPayload { sequence = 2, kind = "leaf_completed", leafKey = "pass" },
                        new TestRunEventPayload { sequence = 3, kind = "leaf_completed", leafKey = "fail" },
                    },
                };
                UPilotTestRunStore.Save(directory, snapshot, active: true, clearActive: false);

                var recovered = UPilotTestRunStore.Read(Path.Combine(directory, runGuid + ".json"));

                Assert.That(recovered.resultStreamVersion, Is.EqualTo(7));
                Assert.That(recovered.nextEventSequence, Is.EqualTo(10001));
                Assert.That(recovered.eventsTruncated, Is.True);
                Assert.That(UPilotTestService.GetEarliestEventSequence(recovered), Is.EqualTo(2));
                Assert.That(UPilotTestService.CreateIncrementalResult(recovered, 1, 1).events.Single().sequence, Is.EqualTo(2));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [TestCase("OnRunStarted")]
        [TestCase("OnTestStarted")]
        [TestCase("OnTestFinished")]
        public void LateProgressCallbacksCannotOverwriteSavedOutcome(string method)
        {
            var service = (UPilotTestService)FormatterServices.GetUninitializedObject(typeof(UPilotTestService));
            var snapshot = new TestRunResultPayload
            {
                status = "cleanup", phase = "cleanup", outcomeStatus = "failed", failed = 1,
                resultAuthoritative = true, lastProgressAt = 123,
            };
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(UPilotTestService).GetField("_lastResults", flags).SetValue(service, snapshot);
            typeof(UPilotTestService).GetField("_isRunning", flags).SetValue(service, true);
            string before = JsonUtility.ToJson(snapshot);
            var callback = typeof(UPilotTestService).GetMethod(method, flags);
            Assert.That(callback, Is.Not.Null, method);
            callback.Invoke(service, callback.GetParameters().Select(_ => (object)null).ToArray());
            Assert.That(JsonUtility.ToJson(snapshot), Is.EqualTo(before));
        }

        private interface CallbackShape { void RunStarted(object test); }

        [TestCase(true)]
        [TestCase(false)]
        public void CallbackFromAnotherRunOrDomainIsIgnored(bool wrongDomain)
        {
            var owner = UPilotTestService.Instance;
            string before = JsonUtility.ToJson(owner.GetStatusSnapshot());
            var active = owner.GetStatusSnapshot().runGuid;
            var domain = (string)typeof(UPilotTestService).GetField("CallbackDomain",
                BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var proxy = new UPilotTestService.TestCallbackProxy();
            proxy.Initialize(owner, wrongDomain ? active : Guid.NewGuid().ToString(),
                wrongDomain ? "expired-domain" : domain);
            typeof(UPilotTestService.TestCallbackProxy).GetMethod("Invoke",
                BindingFlags.NonPublic | BindingFlags.Instance).Invoke(proxy,
                new object[] { typeof(CallbackShape).GetMethod("RunStarted"), new object[] { null } });
            Assert.That(JsonUtility.ToJson(owner.GetStatusSnapshot()), Is.EqualTo(before));
        }

        [Test]
        public void ReloadProbeRefusesUnrelatedRunBeforeArming()
        {
            Assert.Throws<InvalidOperationException>(() =>
                UPilotRunnerReloadProbe.ArmCancellationAtReload(Guid.NewGuid().ToString()));
        }

        [Test]
        public void ResultReloadProbeRefusesUnrelatedRunBeforeRegistering()
        {
            Assert.Throws<InvalidOperationException>(() =>
                UPilotResultReloadProbe.Arm(Guid.NewGuid().ToString()));
        }

        [Test]
        public void CleanupReleaseFailureStopsUntilExplicitRetryAndArchivesOnlyResolvedStage()
        {
            var snapshot = new TestRunResultPayload
            {
                runGuid = "bounded-release", status = "cleanup", phase = "cleanup", outcomeStatus = "failed",
                resultAuthoritative = true, cleanupAttemptDeadlineAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30000,
            };
            var service = DetachedService(snapshot);
            var api = ScriptableObject.CreateInstance<ReleaseApiStub>();
            SetField(service, "_activeApi", api);
            SetField(service, "_activeRunGuid", snapshot.runGuid);
            ProbeWithoutCancelApi.m_testJobDataHolder.Active = false;
            service.RunnerAdapterResolverForTests = _ => UPilotTestRunnerAdapter.Get(typeof(ProbeWithoutCancelApi));
            int attempts = 0, saves = 0;
            service.SnapshotSaverForTests = (_, __, ___) => saves++;
            service.ApiDestroyerForTests = _ => { attempts++; throw new IOException("injected release failure"); };
            try
            {
                InvokeCleanup(service);
                Assert.That(service.GetStatusSnapshot().phase, Is.EqualTo("recovery_required"));
                Assert.That(service.IsRunning, Is.True);
                int saved = saves;
                for (int i = 0; i < 10; i++) { service.GetStatusSnapshot(); InvokeCleanup(service); }
                Assert.That(attempts, Is.EqualTo(1));
                Assert.That(saves, Is.EqualTo(saved));
                Assert.That(service.GetStatusSnapshot().cleanupErrors, Has.Count.EqualTo(1));
                service.ApiDestroyerForTests = owned => { attempts++; UnityEngine.Object.DestroyImmediate(owned); };
                service.ForceCleanupActiveRun(snapshot.runGuid);
                InvokeCleanup(service);
                var completed = service.GetStatusSnapshot();
                Assert.That(service.IsRunning, Is.False);
                Assert.That(attempts, Is.EqualTo(2));
                Assert.That(completed.status, Is.EqualTo("failed"));
                Assert.That(completed.cleanupSucceeded, Is.True);
                Assert.That(completed.cleanupErrors, Is.Empty);
                Assert.That(completed.cleanupErrorHistory.Single(), Does.Contain("api-release:"));
            }
            finally { DetachCleanup(service); if (api != null) UnityEngine.Object.DestroyImmediate(api); }
        }

        [Test]
        public void CleanupDeadlineExpiresWithoutReleaseAndQueriesCannotRenewIt()
        {
            var snapshot = new TestRunResultPayload { runGuid = "expired", status = "cleanup", phase = "cleanup",
                cleanupAttemptDeadlineAt = 1, resultAuthoritative = true, outcomeStatus = "completed" };
            var service = DetachedService(snapshot);
            SetField(service, "_activeRunGuid", snapshot.runGuid);
            service.SnapshotSaverForTests = (_, __, ___) => { };
            InvokeCleanup(service);
            Assert.That(service.GetStatusSnapshot().phase, Is.EqualTo("recovery_required"));
            for (int i = 0; i < 5; i++) { service.GetStatusSnapshot(); InvokeCleanup(service); }
            Assert.That(snapshot.cleanupAttemptDeadlineAt, Is.EqualTo(1));
            Assert.That(snapshot.cleanupResourcesReleased, Is.False);
            Assert.That(service.IsRunning, Is.True);
        }

        [Test]
        public void DuplicateCleanupDoesNotExtendAttemptAndWrongIdentityHasNoEffects()
        {
            var snapshot = new TestRunResultPayload { runGuid = "repeat", status = "cleanup", phase = "cleanup",
                outcomeStatus = "completed", resultAuthoritative = true };
            var service = DetachedService(snapshot);
            SetField(service, "_activeRunGuid", snapshot.runGuid);
            ProbeWithoutCancelApi.m_testJobDataHolder.Active = false;
            service.RunnerAdapterResolverForTests = _ => UPilotTestRunnerAdapter.Get(typeof(ProbeWithoutCancelApi));
            service.SnapshotSaverForTests = (_, __, ___) => { };
            try
            {
                Assert.Throws<InvalidOperationException>(() => service.ForceCleanupActiveRun("wrong"));
                Assert.That(snapshot.cleanupAttemptDeadlineAt, Is.Zero);
                service.ForceCleanupActiveRun(snapshot.runGuid);
                long deadline = snapshot.cleanupAttemptDeadlineAt;
                service.ForceCleanupActiveRun(snapshot.runGuid);
                Assert.That(snapshot.cleanupAttemptDeadlineAt, Is.EqualTo(deadline));
                InvokeCleanup(service);
                service.ForceCleanupActiveRun(snapshot.runGuid);
                Assert.That(service.GetStatusSnapshot().cleanupAttemptDeadlineAt, Is.EqualTo(deadline));
                Assert.That(service.GetStatusSnapshot().cancelAttemptCount, Is.Zero);
            }
            finally { DetachCleanup(service); }
        }

        [Test]
        public void TerminalCommitFailureRetainsOwnershipAndRetriesOnlyPersistence()
        {
            var snapshot = new TestRunResultPayload { runGuid = "commit-retry", status = "cleanup", phase = "cleanup",
                resultAuthoritative = true, outcomeStatus = "failed",
                cleanupAttemptDeadlineAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30000 };
            var service = DetachedService(snapshot);
            SetField(service, "_activeRunGuid", snapshot.runGuid);
            var api = ScriptableObject.CreateInstance<ReleaseApiStub>();
            SetField(service, "_activeApi", api);
            ProbeWithoutCancelApi.m_testJobDataHolder.Active = false;
            service.RunnerAdapterResolverForTests = _ => UPilotTestRunnerAdapter.Get(typeof(ProbeWithoutCancelApi));
            int releases = 0;
            service.ApiDestroyerForTests = owned => { releases++; UnityEngine.Object.DestroyImmediate(owned); };
            service.SnapshotSaverForTests = (_, __, clear) => { if (clear) throw new IOException("pointer failure"); };
            try
            {
                InvokeCleanup(service);
                Assert.That(service.IsRunning, Is.True);
                Assert.That(service.GetStatusSnapshot().cleanupSucceeded, Is.False);
                Assert.That(snapshot.cleanupResourcesReleased, Is.True);
                Assert.That(snapshot.cleanupErrors, Has.Count.EqualTo(1));
                service.SnapshotSaverForTests = (_, __, ___) => { };
                typeof(UPilotTestService).GetMethod("PersistSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(service, new object[] { false });
                Assert.That(snapshot.cleanupErrors.Single(), Does.StartWith("persistence-commit:"));
                Assert.That(snapshot.cleanupErrorHistory, Is.Empty, "Nonterminal writes cannot resolve a commit failure.");
                SetField(service, "_nextCleanupProbeAt", 0L);
                InvokeCleanup(service);
                Assert.That(releases, Is.EqualTo(1));
                Assert.That(service.IsRunning, Is.False);
                Assert.That(service.GetStatusSnapshot().cleanupErrorHistory.Single(), Does.Contain("pointer failure"));
                Assert.That(service.GetStatusSnapshot().cleanupErrors, Is.Empty);
            }
            finally { DetachCleanup(service); if (api != null) UnityEngine.Object.DestroyImmediate(api); }
        }

        [TestCase("cleanup", "cleanup", false)]
        [TestCase("running", "recovery_required", false)]
        [TestCase("cancel_requested", "cancel_requested", true)]
        public void CleanupRecoveryNeverCreatesReplacementApi(string status, string phase, bool cancelled)
        {
            var snapshot = new TestRunResultPayload { runGuid = "no-replacement", status = status,
                phase = phase, cancelRequested = cancelled };
            var service = DetachedService(snapshot);
            SetField(service, "_activeRunGuid", snapshot.runGuid);
            try
            {
                typeof(UPilotTestService).GetMethod("ReattachPersistedRun", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(service, null);
                Assert.That(GetField(service, "_activeApi"), Is.Null);
                Assert.That(GetField(service, "_activeCallback"), Is.Null);
            }
            finally { DetachCleanup(service); }
        }

        [Test]
        public void RunFinishedPersistsOwnReleaseBeforeRunnerStopsWithoutReleasingSlot()
        {
            var snapshot = new TestRunResultPayload { runGuid = "finished-before-runner-exit", testMode = "EditMode" };
            var service = DetachedService(snapshot);
            SetField(service, "_activeRunGuid", snapshot.runGuid);
            var api = ScriptableObject.CreateInstance<ReleaseApiStub>();
            SetField(service, "_activeApi", api);
            int releases = 0;
            var writes = new System.Collections.Generic.List<TestRunResultPayload>();
            service.SnapshotSaverForTests = (value, _, clear) =>
            {
                if (clear) Assert.That(ProbeWithoutCancelApi.m_testJobDataHolder.Active, Is.False);
                writes.Add(value.ShallowCopyForPersistence());
            };
            service.ApiDestroyerForTests = value =>
            {
                Assert.That(writes.Any(saved => saved.resultAuthoritative && saved.cleanupAttemptDeadlineAt > 0), Is.True);
                Assert.That(ProbeWithoutCancelApi.m_testJobDataHolder.Active, Is.True);
                releases++;
                UnityEngine.Object.DestroyImmediate(value);
            };
            service.RunnerAdapterResolverForTests = _ => UPilotTestRunnerAdapter.Get(typeof(ProbeWithoutCancelApi));
            ProbeWithoutCancelApi.m_testJobDataHolder.Active = true;
            try
            {
                InvokeRunFinished(service);
                Assert.That(releases, Is.EqualTo(1));
                Assert.That(writes.Last().cleanupResourcesReleased, Is.True);
                Assert.That(service.IsRunning, Is.True);
                Assert.That(snapshot.cleanupPending, Is.True);
                Assert.That(snapshot.cleanupSucceeded, Is.False);
                Assert.That(snapshot.endedAt, Is.Zero);
                long deadline = snapshot.cleanupAttemptDeadlineAt;
                InvokeCleanup(service);
                Assert.That(service.IsRunning, Is.True);
                Assert.That(snapshot.runnerState, Is.EqualTo("active"));
                // Emulate loss of managed references across Reload, retaining durable evidence.
                DetachCleanup(service);
                var restored = DetachedService(writes.Last().ShallowCopyForPersistence());
                SetField(restored, "_activeRunGuid", snapshot.runGuid);
                SetField(restored, "_cleanupOwnershipUnknown", true);
                restored.SnapshotSaverForTests = (_, __, ___) => { };
                restored.RunnerAdapterResolverForTests = service.RunnerAdapterResolverForTests;
                restored.ApiDestroyerForTests = _ => Assert.Fail("Released API must not be recreated/released after Reload.");
                ProbeWithoutCancelApi.m_testJobDataHolder.Active = false;
                try
                {
                    InvokeCleanup(restored);
                    Assert.That(restored.IsRunning, Is.False);
                    var result = restored.GetStatusSnapshot();
                    Assert.That(result.status, Is.EqualTo("no_tests"));
                    Assert.That(result.cleanupSucceeded, Is.True);
                    Assert.That(result.cleanupAttemptDeadlineAt, Is.EqualTo(deadline));
                    Assert.That(result.cancelAttemptCount, Is.Zero);
                    Assert.That(result.unresolvedResources, Is.Empty);
                }
                finally { DetachCleanup(restored); }
            }
            finally { DetachCleanup(service); if (api != null) UnityEngine.Object.DestroyImmediate(api); }
        }

        [TestCase("intent")]
        [TestCase("release-evidence")]
        [TestCase("destroy")]
        [TestCase("expired")]
        public void RunFinishedReleaseFailureCannotCommitOrRenewBudget(string failure)
        {
            long deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (failure == "expired" ? -1000 : 30000);
            var snapshot = new TestRunResultPayload { runGuid = "early-release-failure", cleanupAttemptDeadlineAt = deadline };
            var service = DetachedService(snapshot);
            SetField(service, "_activeRunGuid", snapshot.runGuid);
            var api = ScriptableObject.CreateInstance<ReleaseApiStub>();
            SetField(service, "_activeApi", api);
            int releases = 0;
            service.SnapshotSaverForTests = (value, _, clear) =>
            {
                Assert.That(clear, Is.False);
                if (failure == "intent" || (failure == "release-evidence" && value.cleanupResourcesReleased))
                    throw new System.IO.IOException("injected snapshot failure");
            };
            service.ApiDestroyerForTests = value =>
            {
                releases++;
                if (failure == "destroy") throw new InvalidOperationException("injected release failure");
                UnityEngine.Object.DestroyImmediate(value);
            };
            try
            {
                InvokeRunFinished(service);
                Assert.That(service.IsRunning, Is.True);
                Assert.That(snapshot.cleanupSucceeded, Is.False);
                Assert.That(snapshot.cleanupPending, Is.True);
                Assert.That(snapshot.cleanupAttemptDeadlineAt, Is.EqualTo(deadline));
                Assert.That(releases, Is.EqualTo(failure == "destroy" || failure == "release-evidence" ? 1 : 0));
                // The duplicate result cannot retry an uncertain release or renew the window.
                InvokeRunFinished(service);
                Assert.That(releases, Is.EqualTo(failure == "destroy" || failure == "release-evidence" ? 1 : 0));
                if (failure == "destroy")
                {
                    Assert.That(snapshot.phase, Is.EqualTo("recovery_required"));
                    Assert.That(GetField(service, "_cleanupScheduled"), Is.False);
                    Assert.That(snapshot.cleanupResourcesReleased, Is.False);
                }
            }
            finally { DetachCleanup(service); if (api != null) UnityEngine.Object.DestroyImmediate(api); }
        }

        [Test]
        public void RunFinishedUnregistersOwnRealCallbackWithoutTouchingOtherCallbacks()
        {
            var apiType = FindInstalledApiType();
            var callbacksType = apiType.Assembly.GetType("UnityEditor.TestTools.TestRunner.Api.ICallbacks", true);
            var snapshot = new TestRunResultPayload { runGuid = "self-unregister" };
            var service = DetachedService(snapshot);
            SetField(service, "_activeRunGuid", snapshot.runGuid);
            service.SnapshotSaverForTests = (_, __, ___) => { };
            var api = ScriptableObject.CreateInstance(apiType);
            // The production proxy intentionally rejects detached service instances. Use
            // our own callback with real UTF registration, without replacing the singleton
            // or bypassing the outer acceptance Runner's callback identity protection.
            var create = typeof(DispatchProxy).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method => method.Name == "Create" && method.IsGenericMethodDefinition);
            var callback = create.MakeGenericMethod(callbacksType, typeof(CleanupCallbackProxy)).Invoke(null, null);
            ((CleanupCallbackProxy)callback).Finished = () => InvokeRunFinished(service);
            var holderType = apiType.Assembly.GetType("UnityEditor.TestTools.TestRunner.Api.CallbacksHolder", true);
            var holder = holderType.BaseType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            var getAll = holderType.GetMethod("GetAll");
            var before = ((IEnumerable)getAll.Invoke(holder, null)).Cast<object>().ToArray();
            try
            {
                typeof(UPilotTestService).GetMethod("RegisterCallbacks", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { apiType, api, callbacksType, callback });
                SetField(service, "_activeApi", api);
                SetField(service, "_activeCallback", callback);
                // Invoke only our callback from UTF's snapshot, never the outer acceptance callbacks.
                var dispatch = ((IEnumerable)getAll.Invoke(holder, null)).Cast<object>().Single(item => ReferenceEquals(item, callback));
                callbacksType.GetMethod("RunFinished").Invoke(dispatch, new object[] { null });
                Assert.That(snapshot.resultAuthoritative, Is.True);
                Assert.That(snapshot.cleanupResourcesReleased, Is.True);
                Assert.That(api == null, Is.True);
                Assert.That(service.IsRunning, Is.True);
                CollectionAssert.AreEqual(before, ((IEnumerable)getAll.Invoke(holder, null)).Cast<object>().ToArray());
            }
            finally
            {
                DetachCleanup(service);
                var unregister = UPilotTestRunnerAdapter.Get(apiType).Unregister.MakeGenericMethod(callbacksType);
                unregister.Invoke(unregister.IsStatic ? null : api, new[] { callback });
                if (api != null) UnityEngine.Object.DestroyImmediate(api);
            }
        }

        public class CleanupCallbackProxy : DispatchProxy
        {
            public Action Finished;
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == "RunFinished") Finished();
                return null;
            }
        }

        private static void InvokeRunFinished(UPilotTestService service) =>
            typeof(UPilotTestService).GetMethod("OnRunFinished", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(service, new object[] { null });

        [Test]
        public void LateResultPreservesRecoveryBarrierAndDeadline()
        {
            var snapshot = new TestRunResultPayload { runGuid = "late-result", status = "cleanup",
                phase = "recovery_required", cleanupAttemptDeadlineAt = 123,
                cleanupErrors = new System.Collections.Generic.List<string> { "api-release: original failure" } };
            var service = DetachedService(snapshot);
            service.SnapshotSaverForTests = (_, __, ___) => { };
            try
            {
                typeof(UPilotTestService).GetMethod("OnRunFinished", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(service, new object[] { null });
                Assert.That(snapshot.resultAuthoritative, Is.True);
                Assert.That(snapshot.outcomeStatus, Is.EqualTo("no_tests"));
                Assert.That(snapshot.phase, Is.EqualTo("recovery_required"));
                Assert.That(snapshot.cleanupAttemptDeadlineAt, Is.EqualTo(123));
                Assert.That(snapshot.cleanupErrors.Single(), Does.StartWith("api-release:"));
                Assert.That(GetField(service, "_cleanupScheduled"), Is.False);
                Assert.That(service.IsRunning, Is.True);
            }
            finally { DetachCleanup(service); }
        }

        [Test]
        public void UncertainCancellationDuplicateOnlyObservesOriginalRun()
        {
            var snapshot = new TestRunResultPayload { runGuid = "uncertain-cancel", status = "cancel_requested",
                cancelRequested = true, cancelAccepted = false, cancelAttemptCount = 1,
                cleanupAttemptDeadlineAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30000 };
            var service = DetachedService(snapshot);
            SetField(service, "_activeRunGuid", snapshot.runGuid);
            service.SnapshotSaverForTests = (_, __, ___) => { };
            ProbeWithoutCancelApi.m_testJobDataHolder.Active = true;
            service.RunnerAdapterResolverForTests = _ => UPilotTestRunnerAdapter.Get(typeof(ProbeWithoutCancelApi));
            try
            {
                service.ForceCleanupActiveRun(snapshot.runGuid);
                Assert.That(snapshot.cancelAttemptCount, Is.EqualTo(1));
                Assert.That(GetField(service, "_forceStopRequested"), Is.False);
                Assert.That(snapshot.runnerState, Is.EqualTo("active"));
            }
            finally { DetachCleanup(service); }
        }

        [Test]
        public void RunnerObservationRecoveryArchivesOnlyItsOwnDeduplicatedError()
        {
            var snapshot = new TestRunResultPayload { runGuid = "observation-history" };
            snapshot.cleanupErrors.Add("api-release: unresolved");
            var service = DetachedService(snapshot);
            SetField(service, "_activeRunGuid", snapshot.runGuid);
            service.RunnerAdapterResolverForTests = _ => UPilotTestRunnerAdapter.Get(typeof(MissingApi));
            var probe = typeof(UPilotTestService).GetMethod("ProbeFrameworkRun", BindingFlags.NonPublic | BindingFlags.Instance);
            probe.Invoke(service, new object[] { null });
            probe.Invoke(service, new object[] { null });
            Assert.That(snapshot.cleanupErrors.Count(error => error.StartsWith("runner-observation:")), Is.EqualTo(1));
            ProbeWithoutCancelApi.m_testJobDataHolder.Active = false;
            service.RunnerAdapterResolverForTests = _ => UPilotTestRunnerAdapter.Get(typeof(ProbeWithoutCancelApi));
            probe.Invoke(service, new object[] { null });
            Assert.That(snapshot.runnerState, Is.EqualTo("inactive"));
            Assert.That(snapshot.cleanupErrors, Is.EqualTo(new[] { "api-release: unresolved" }));
            Assert.That(snapshot.cleanupErrorHistory.Single(), Does.StartWith("runner-observation:"));
            Assert.That(snapshot.recoveryDiagnostic, Is.EqualTo(typeof(ProbeWithoutCancelApi).Assembly.FullName));
        }

        [Test]
        public void CleanupStageResolutionAndCandidateListsAreIsolated()
        {
            var snapshot = new TestRunResultPayload();
            snapshot.cleanupErrors.AddRange(new[] { "callback-unregister: fixed", "api-release: still pending" });
            snapshot.unresolvedResources.Add("test-runner-api");
            var candidate = snapshot.ShallowCopyForPersistence();
            typeof(UPilotTestService).GetMethod("ResolveCleanupErrors", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { candidate, "callback-unregister" });
            candidate.unresolvedResources.Clear();
            Assert.That(snapshot.cleanupErrors, Has.Count.EqualTo(2));
            Assert.That(snapshot.cleanupErrorHistory, Is.Empty);
            Assert.That(snapshot.unresolvedResources, Has.Count.EqualTo(1));
            Assert.That(candidate.cleanupErrors.Single(), Does.StartWith("api-release:"));
            Assert.That(candidate.cleanupErrorHistory.Single(), Does.StartWith("callback-unregister:"));
        }

        [Test]
        public void MissingCleanupDeadlineDoesNotGainBudgetOrReleaseResources()
        {
            var snapshot = new TestRunResultPayload { runGuid = "legacy-cleanup", status = "cleanup" };
            var service = DetachedService(snapshot);
            service.SnapshotSaverForTests = (_, __, ___) => { };
            try
            {
                InvokeCleanup(service);
                Assert.That(snapshot.phase, Is.EqualTo("recovery_required"));
                Assert.That(snapshot.cleanupAttemptDeadlineAt, Is.Zero);
                Assert.That(snapshot.cleanupResourcesReleased, Is.False);
            }
            finally { DetachCleanup(service); }
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void TestRunStoreCannotReplaceAnotherRunsActivePointer(bool active, bool clear)
        {
            string directory = Path.Combine(Path.GetTempPath(), "upilot-pointer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string pointer = Path.Combine(directory, "active-run.txt");
            File.WriteAllText(pointer, "other-run");
            try
            {
                var candidate = new TestRunResultPayload { runGuid = "original-run" };
                Assert.Throws<IOException>(() => UPilotTestRunStore.Save(directory, candidate, active, clear));
                Assert.That(File.ReadAllText(pointer), Is.EqualTo("other-run"));
                Assert.That(File.Exists(Path.Combine(directory, "original-run.json")), Is.False);
            }
            finally { Directory.Delete(directory, true); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PostReloadDispositionPreservesEvidenceAndRequiresFinalCommit(bool failCommit)
        {
            string directory = Path.Combine(Path.GetTempPath(), "upilot-disposition-" + Guid.NewGuid().ToString("N"));
            var snapshot = new TestRunResultPayload { runGuid = "disposition-original", phase = "recovery_required",
                callbackDomain = "old-domain", resultAuthoritative = true, outcomeStatus = "failed", failed = 1,
                cleanupPending = true, cleanupResourcesReleased = false };
            snapshot.unresolvedResources.Add("original callback release unverified");
            var service = DetachedService(snapshot);
            SetField(service, "_activeRunGuid", snapshot.runGuid);
            SetField(service, "_cleanupOwnershipUnknown", true);
            service.DispositionDirectoryForTests = directory;
            ProbeWithoutCancelApi.m_testJobDataHolder.Active = false;
            service.RunnerAdapterResolverForTests = _ => UPilotTestRunnerAdapter.Get(typeof(ProbeWithoutCancelApi));
            int commits = 0;
            service.SnapshotSaverForTests = (value, active, clear) =>
            {
                Assert.That(GetField(service, "_isRunning"), Is.True, "Must retain slot until commit returns");
                if (clear) { commits++; if (failCommit) throw new IOException("injected pointer failure"); }
            };
            try
            {
                var proof = service.PreviewAbandonReloadedRun(snapshot.runGuid);
                var request = new UPilotTestService.TestAbandonRequest { runGuid = snapshot.runGuid,
                    requestId = "dispose-fixture", expectedStateHash = proof.stateHash, reason = "fixture only" };
                var result = service.AbandonReloadedRun(request);
                Assert.That(result.cleanupSucceeded, Is.False);
                Assert.That(result.cleanupResourcesReleased, Is.False);
                Assert.That(result.unresolvedResources, Has.Count.EqualTo(1));
                Assert.That(result.failed, Is.EqualTo(1));
                Assert.That(result.outcomeStatus, Is.EqualTo("failed"));
                Assert.That(File.Exists(result.disposition.backupPath), Is.True);
                Assert.That(result.terminal, Is.EqualTo(!failCommit));
                Assert.That((bool)GetField(service, "_isRunning"), Is.EqualTo(failCommit));
                Assert.That((bool)GetField(service, "_cleanupOwnershipUnknown"), Is.EqualTo(failCommit),
                    "Only a fully committed disposition may release the previous ownership barrier");
                service.AbandonReloadedRun(request);
                Assert.That(commits, Is.EqualTo(1), "Duplicate request must not repeat commit actions");
                if (failCommit)
                {
                    // Explicit cleanup renews only the commit attempt, never cancel or unregister.
                    result.phase = "recovery_required";
                    result.cleanupAttemptDeadlineAt = 1;
                    failCommit = false;
                    service.ForceCleanupActiveRun(snapshot.runGuid);
                    result = (TestRunResultPayload)GetField(service, "_lastResults");
                    Assert.That(result.terminal, Is.True);
                    Assert.That(result.status, Is.EqualTo("Released"));
                    Assert.That(result.cleanupSucceeded, Is.False);
                }
            }
            finally { DetachCleanup(service); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [TestCase(true, "old-domain", "original")]
        [TestCase(false, "", "original")]
        [TestCase(false, "old-domain", "wrong")]
        public void PostReloadDispositionRejectsUnsafeIdentity(bool active, string domain, string requested)
        {
            var snapshot = new TestRunResultPayload { runGuid = "original", phase = "recovery_required", callbackDomain = domain };
            var service = DetachedService(snapshot);
            SetField(service, "_activeRunGuid", "original"); SetField(service, "_cleanupOwnershipUnknown", true);
            ProbeWithoutCancelApi.m_testJobDataHolder.Active = active;
            service.RunnerAdapterResolverForTests = _ => UPilotTestRunnerAdapter.Get(typeof(ProbeWithoutCancelApi));
            try { Assert.Throws<InvalidOperationException>(() => service.PreviewAbandonReloadedRun(requested)); }
            finally { DetachCleanup(service); ProbeWithoutCancelApi.m_testJobDataHolder.Active = false; }
        }

        [TestCase("{}")]
        [TestCase("{\"disposition\":null}")]
        [TestCase("{\"disposition\":{}}")]
        public void EmptySerializedDispositionIsNotAnAdministrativeIntent(string json)
        {
            var value = JsonUtility.FromJson<TestRunResultPayload>(json);
            Assert.That(value.disposition, Is.Null);
            Assert.That(JsonUtility.FromJson<TestRunResultPayload>(JsonUtility.ToJson(value)).disposition, Is.Null);
            Assert.That(UPilotTestService.CreateStatusSummary(value).disposition, Is.Null);
            Assert.That(UPilotTestService.CreateIncrementalResult(value, 0, 1).disposition, Is.Null);
        }

        [Test]
        public void PartialSerializedDispositionRemainsProtectedForEveryEvidenceField()
        {
            foreach (var field in typeof(TestRunDisposition).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var disposition = new TestRunDisposition();
                field.SetValue(disposition, field.FieldType == typeof(long) ? (object)(-1L) : " ");
                var value = JsonUtility.FromJson<TestRunResultPayload>(JsonUtility.ToJson(
                    new TestRunResultPayload { disposition = disposition }));
                Assert.That(value.disposition, Is.Not.Null, field.Name);
                Assert.That(field.GetValue(value.disposition), Is.EqualTo(field.GetValue(disposition)), field.Name);
            }
        }

        [Test]
        public void LegacyStoreReadDoesNotEnterDispositionCommit()
        {
            string directory = Path.Combine(Path.GetTempPath(), "upilot-disposition-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "original.json");
                File.WriteAllText(path, JsonUtility.ToJson(new TestRunResultPayload {
                    runGuid = "original", phase = "recovery_required", cleanupPending = true }));
                File.WriteAllText(Path.Combine(directory, "active-run.txt"), "original");
                var value = UPilotTestRunStore.Read(path);
                Assert.That(value.disposition, Is.Null);
                Assert.That(value.phase, Is.EqualTo("recovery_required"));
                value.disposition = new TestRunDisposition { requestId = "partial-intent" };
                File.WriteAllText(path, JsonUtility.ToJson(value));
                value = UPilotTestRunStore.Read(path);
                Assert.That(value.disposition.requestId, Is.EqualTo("partial-intent"));
                Assert.That(value.cleanupStatus, Is.EqualTo("committing"));
                Assert.That(value.terminal, Is.False);
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void InvokeCleanup(UPilotTestService service) =>
            typeof(UPilotTestService).GetMethod("CleanupActiveRun", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(service, null);

        private static UPilotTestService DetachedService(TestRunResultPayload snapshot)
        {
            var service = (UPilotTestService)FormatterServices.GetUninitializedObject(typeof(UPilotTestService));
            SetField(service, "_lastResults", snapshot);
            SetField(service, "_isRunning", true);
            return service;
        }

        private static Type FindInstalledApiType()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi"))
                .First(candidate => candidate != null);
        }

        private static void SetField(object target, string name, object value)
        {
            target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
        }

        private static object GetField(object target, string name)
        {
            return target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
        }
    }

    public static class UPilotRunnerReloadProbe
    {
        private static string _armedRun;
        private static string _directory;

        [Serializable]
        private sealed class ReloadWitness
        {
            public string runGuid;
            public string projectPath;
            public string callbackDomain;
            public long observedAt;
            public int cancelRequests;
            public TestRunResultPayload before;
            public TestRunResultPayload after;
            public string error;
        }

        public static string ArmCancellationAtReload(string runGuid)
        {
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/');
            if (!string.Equals(project, "D:/upilot/Tests~/UPilotTest", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(project, "D:/upilot/Tests~/UPilotTest2022", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Reload probe requires an exact authorized repository project.");
            var status = UPilotTestService.Instance.GetStatusSnapshot();
            if (!Guid.TryParse(runGuid, out _) || status?.runGuid != runGuid || !status.isRunning
                || status.cancelRequested || status.resultAuthoritative || _armedRun != null)
                throw new InvalidOperationException("Reload probe requires the exact uncancelled active run.");
            if (!(status.selectedLeafIdentities ?? Array.Empty<string>()).Any(
                identity => identity.Contains("UPilotRunnerReloadAcceptance.CancellationAtReloadBoundary")))
                throw new InvalidOperationException("Reload probe only cancels its dedicated Reload fixture.");
            string directory = Path.Combine(project, "Log", "P0P1", "CancelReload", runGuid);
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(Path.Combine(directory, "armed-once.json"), FileMode.CreateNew))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(JsonUtility.ToJson(new ReloadWitness
                {
                    runGuid = runGuid, projectPath = project, callbackDomain = status.callbackDomain,
                    observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }));
                writer.Flush();
                stream.Flush(true);
            }
            _armedRun = runGuid;
            _directory = directory;
            AssemblyReloadEvents.beforeAssemblyReload += CancelAtReload;
            EditorUtility.RequestScriptReload();
            return directory;
        }

        private static void CancelAtReload()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= CancelAtReload;
            var witness = new ReloadWitness
            {
                runGuid = _armedRun,
                projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            try
            {
                var service = UPilotTestService.Instance;
                var before = service.GetStatusSnapshot();
                if (before?.runGuid != _armedRun || !before.isRunning)
                    throw new InvalidOperationException("Armed run no longer active; no cancellation was sent.");
                witness.callbackDomain = before.callbackDomain;
                witness.before = JsonUtility.FromJson<TestRunResultPayload>(JsonUtility.ToJson(before));
                service.CancelActiveRun(_armedRun);
                witness.cancelRequests++;
                service.CancelActiveRun(_armedRun);
                witness.cancelRequests++;
                witness.after = service.GetStatusSnapshot();
            }
            catch (Exception ex) { witness.error = ex.ToString(); }
            finally
            {
                File.WriteAllText(Path.Combine(_directory, "cancel-at-reload.json"), JsonUtility.ToJson(witness, true));
                _armedRun = null;
            }
        }
    }

    public class UPilotRunnerReloadAcceptance
    {
        [UnityTest, Explicit("Targeted cancellation at the actual Reload boundary only."), Timeout(60000)]
        public IEnumerator CancellationAtReloadBoundary()
        {
            yield return null;
            yield return null;
            string runGuid = UPilotTestService.Instance.GetStatusSnapshot().runGuid;
            UPilotRunnerReloadProbe.ArmCancellationAtReload(runGuid);
            // UTF prepares its persisted continuation and releases its own reload lock.
            yield return new WaitForDomainReload();
            double deadline = EditorApplication.timeSinceStartup + 30;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                TestContext.Progress.WriteLine("P0 cancellation recovery still observing original run.");
                yield return null;
            }
            Assert.Fail("P0 cancellation at Reload did not stop the original fixture.");
        }

        [Test, Explicit("Targeted authoritative result followed immediately by Reload.")]
        public void PassImmediatelyBeforeReload()
        {
            UPilotResultReloadProbe.Arm(UPilotTestService.Instance.GetStatusSnapshot().runGuid);
            Assert.That(6 * 7, Is.EqualTo(42));
        }

        [Test, Explicit("Expected failure detail must survive immediate post-result Reload.")]
        public void FailImmediatelyBeforeReload()
        {
            UPilotResultReloadProbe.Arm(UPilotTestService.Instance.GetStatusSnapshot().runGuid);
            Assert.Fail("P0_EXPECTED_IMMEDIATE_RESULT_RELOAD_FAILURE");
        }
    }

    public static class UPilotResultReloadProbe
    {
        private static object _callback;
        private static Type _callbacksType;
        private static Type _apiType;
        private static string _directory;
        private static double _deadline;
        private static ResultWitness _witness;

        [Serializable]
        private sealed class ResultWitness
        {
            public string runGuid;
            public string projectPath;
            public string testName;
            public string domain;
            public long resultObservedAt;
            public long reloadRequestedAt;
            public long beforeReloadAt;
            public int resultCallbacks;
            public int duplicateCallbacks;
            public int foreignCallbacks;
            public bool callbacksPreservedResult;
            public bool observerReleased;
            public TestRunResultPayload persistedResult;
            public TestRunResultPayload beforeReload;
            public string error = "";
        }

        public static void Arm(string runGuid)
        {
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/');
            if (!string.Equals(project, "D:/upilot/Tests~/UPilotTest", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(project, "D:/upilot/Tests~/UPilotTest2022", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Result Reload probe requires an exact authorized project.");
            var status = UPilotTestService.Instance.GetStatusSnapshot();
            if (!Guid.TryParse(runGuid, out _) || status?.runGuid != runGuid || !status.isRunning
                || status.resultAuthoritative || _callback != null)
                throw new InvalidOperationException("Result Reload probe requires the exact active run.");
            string prefix = typeof(UPilotRunnerReloadAcceptance).FullName + ".";
            if (status.selectedLeafIdentities.Length != 1
                || (status.currentTest != prefix + "PassImmediatelyBeforeReload"
                    && status.currentTest != prefix + "FailImmediatelyBeforeReload"))
                throw new InvalidOperationException("Result Reload probe requires one exact dedicated test.");
            _apiType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi"))
                .First(type => type != null);
            _callbacksType = _apiType.Assembly.GetType("UnityEditor.TestTools.TestRunner.Api.ICallbacks", true);
            var register = _apiType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method => method.Name == "RegisterTestCallback" && method.IsGenericMethodDefinition);
            var create = typeof(DispatchProxy).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method => method.Name == "Create" && method.IsGenericMethodDefinition);
            _callback = create.MakeGenericMethod(_callbacksType, typeof(ResultCallback)).Invoke(null, null);
            _directory = Path.Combine(project, "Log", "P0P1", "ResultReload", runGuid);
            _witness = new ResultWitness
            {
                runGuid = runGuid, projectPath = project, testName = status.currentTest, domain = status.callbackDomain,
            };
            try
            {
                Directory.CreateDirectory(_directory);
                using (var stream = new FileStream(Path.Combine(_directory, "armed-once.json"), FileMode.CreateNew))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(JsonUtility.ToJson(_witness));
                    writer.Flush();
                    stream.Flush(true);
                }
                register.MakeGenericMethod(_callbacksType).Invoke(null, new[] { _callback, (object)(-100) });
                _deadline = EditorApplication.timeSinceStartup + 30;
                EditorApplication.update += CheckDeadline;
            }
            catch
            {
                ReleaseObserver();
                throw;
            }
        }

        public class ResultCallback : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name != "RunFinished") return null;
                var service = UPilotTestService.Instance;
                var status = service.GetStatusSnapshot();
                if (status?.runGuid != _witness?.runGuid || status.callbackDomain != _witness.domain) return null;
                try
                {
                    _witness.resultCallbacks++;
                    _witness.resultObservedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    string path = Path.Combine(_witness.projectPath, "Library", "UPilot", "TestRuns", _witness.runGuid + ".json");
                    _witness.persistedResult = UPilotTestRunStore.Read(path);
                    if (!_witness.persistedResult.resultAuthoritative || _witness.persistedResult.total != 1
                        || _witness.persistedResult.runGuid != _witness.runGuid || !string.IsNullOrEmpty(status.persistenceError))
                        throw new InvalidOperationException("RunFinished did not persist an authoritative matching result.");

                    string before = JsonUtility.ToJson(status);
                    var invoke = typeof(UPilotTestService.TestCallbackProxy).GetMethod("Invoke",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    var proxy = new UPilotTestService.TestCallbackProxy();
                    proxy.Initialize(service, _witness.runGuid, _witness.domain);
                    for (int i = 0; i < 2; i++)
                    {
                        invoke.Invoke(proxy, new object[] { method, args });
                        _witness.duplicateCallbacks++;
                    }
                    proxy.Initialize(service, Guid.NewGuid().ToString(), _witness.domain);
                    invoke.Invoke(proxy, new object[] { method, args });
                    _witness.foreignCallbacks++;
                    proxy.Initialize(service, _witness.runGuid, "expired-domain");
                    invoke.Invoke(proxy, new object[] { method, args });
                    _witness.foreignCallbacks++;
                    _witness.callbacksPreservedResult = before == JsonUtility.ToJson(service.GetStatusSnapshot());
                    if (!_witness.callbacksPreservedResult)
                        throw new InvalidOperationException("Duplicate or unrelated callback changed the saved result.");
                    ReleaseObserver();
                    AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
                    _witness.reloadRequestedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    SaveWitness("result-observed.json");
                    EditorUtility.RequestScriptReload();
                }
                catch (Exception ex)
                {
                    _witness.error = ex.ToString();
                    ReleaseObserver();
                    AssemblyReloadEvents.beforeAssemblyReload -= BeforeReload;
                    EditorApplication.update -= CheckDeadline;
                    SaveWitness("probe-error.json");
                }
                return null;
            }
        }

        private static void ReleaseObserver()
        {
            if (_callback == null) return;
            // This observer uses static RegisterTestCallback, not a service-owned instance.
            _apiType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method => method.Name == "UnregisterTestCallback" && method.IsGenericMethodDefinition)
                .MakeGenericMethod(_callbacksType).Invoke(null, new[] { _callback });
            _callback = null;
            _witness.observerReleased = true;
        }

        private static void BeforeReload()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeReload;
            EditorApplication.update -= CheckDeadline;
            _witness.beforeReloadAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string path = Path.Combine(_witness.projectPath, "Library", "UPilot", "TestRuns", _witness.runGuid + ".json");
            _witness.beforeReload = UPilotTestRunStore.Read(path);
            SaveWitness("before-reload.json");
        }

        private static void CheckDeadline()
        {
            if (EditorApplication.timeSinceStartup < _deadline) return;
            EditorApplication.update -= CheckDeadline;
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeReload;
            _witness.error = "Result/Reload boundary was not observed within 30 seconds.";
            ReleaseObserver();
            SaveWitness("probe-error.json");
        }

        private static void SaveWitness(string name) =>
            File.WriteAllText(Path.Combine(_directory, name), JsonUtility.ToJson(_witness, true));
    }
}
