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
            service.RunnerAdapterResolverForTests = _ => throw new MissingMemberException("P0_EXPECTED_CANCEL_BINDING_FAILURE");

            var invocation = Assert.Throws<TargetInvocationException>(() =>
                typeof(UPilotTestService).GetMethod("RequestCancel", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(service, new object[] { false }));
            Assert.That(invocation.InnerException, Is.TypeOf<InvalidOperationException>());
            var status = service.GetStatusSnapshot();
            Assert.That(status.status, Is.EqualTo("running"));
            Assert.That(status.isRunning, Is.True);
            Assert.That(status.cancelRequested, Is.False);
            Assert.That(status.cancelAttemptCount, Is.Zero);
            Assert.That(status.runnerState, Is.EqualTo("unknown"));
            Assert.That(status.recoveryDiagnostic, Does.Contain("no cancel API was invoked"));
            Assert.That(status.recoveryDiagnostic, Does.Contain("P0_EXPECTED_CANCEL_BINDING_FAILURE"));
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
            Assert.That(adapter.Cancel.Name, Is.EqualTo("CancelTestRun"));
            var active = UPilotTestService.Instance.GetStatusSnapshot().runGuid;
            Assert.That(adapter.Probe(active, out runner, out diagnostic), Is.EqualTo("active"), diagnostic);
            Assert.That(runner, Is.Not.Null);
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
            UPilotTestRunnerAdapter.Get(_apiType).Unregister.MakeGenericMethod(_callbacksType)
                .Invoke(null, new[] { _callback });
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
