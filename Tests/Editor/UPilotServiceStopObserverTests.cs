// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotServiceStopObserverTests
    {
        private long _now;
        private int _probes, _ends;
        private bool _current;
        private ServiceStopObservationRecord _record;
        private ServiceStopObserver _observer;
        [SetUp] public void SetUp() { _now = 1000; _probes = _ends = 0; _current = true; _record = ServiceStopObservationRecord.Start(_now, 30, 100); }
        [TearDown] public void TearDown() => _observer?.Dispose();
        private void Start(Action probe = null)
        {
            _observer = new ServiceStopObserver(_record, () => _now, () => _current, probe ?? (() => ++_probes),
                (terminal, reason) => { _record.End(_now, terminal, reason); ++_ends; });
            _observer.Start();
        }
        [Test] public void FirstProbeIsImmediateAndSubsequentProbesUseFrozenInterval()
        {
            Start(); _observer.Start(); Assert.That(_probes, Is.EqualTo(1));
            _now += 99; _observer.Tick(); Assert.That(_probes, Is.EqualTo(1));
            ++_now; _observer.Tick(); Assert.That(_probes, Is.EqualTo(2));
        }
        [Test] public void DeadlineWinsOverNextCheckAndLateSuccessCannotOverwriteTimeout()
        {
            Start(); _record.nextCheckAt = 40000; _now = 31000; _observer.Tick();
            Assert.That(_record.status, Is.EqualTo("timed_out")); Assert.That(_observer.Subscribed, Is.False);
            _record.End(_now, "succeeded", "late"); _observer.Tick();
            Assert.That(_ends, Is.EqualTo(1)); Assert.That(_probes, Is.EqualTo(1)); Assert.That(_record.status, Is.EqualTo("timed_out"));
        }
        [Test] public void NewLifetimeOrNewStartStopsBeforeNextProbe()
        {
            Start(); _current = false; ++_now; _observer.Tick();
            Assert.That(_record.status, Is.EqualTo("stopped")); Assert.That(_observer.Subscribed, Is.False); Assert.That(_probes, Is.EqualTo(1));
        }
        [Test] public void ExceptionReleasesSubscriptionAndEndsOnce()
        {
            Start(() => throw new InvalidOperationException("probe failed")); _observer.Tick();
            Assert.That(_record.status, Is.EqualTo("failed")); Assert.That(_record.reason, Is.EqualTo("probe failed"));
            Assert.That(_ends, Is.EqualTo(1)); Assert.That(_observer.Subscribed, Is.False);
        }
        [Test] public void EarlySuccessAndElapsedTimeDoNotWaitThirtySeconds()
        {
            Start(() => { _record.processesExited = _record.portsAvailable = true; _record.End(_now, "succeeded", "verified"); });
            Assert.That(_record.status, Is.EqualTo("succeeded")); Assert.That(_record.endedAt - _record.startedAt, Is.Zero);
            Assert.That(_record.EndMessage, Is.EqualTo("MCP 服务停止成功")); Assert.That(_observer.Subscribed, Is.False);
        }
        [Test] public void ProcessExitWithoutPortsCannotBeReportedAsSuccessful()
        {
            Start(() => { _record.processesExited = true; }); _now = 31000; _observer.Tick();
            Assert.That(_record.status, Is.EqualTo("timed_out")); Assert.That(_record.portsAvailable, Is.False);
            Assert.That(_record.EndMessage, Does.Contain("端口仍不可用"));
        }
        [Test] public void ReloadRestoresOriginalIdFrozenParametersAndDeadline()
        {
            _record.dispatched = true; _record.Checked(1000);
            var restored = UnityEngine.JsonUtility.FromJson<ServiceStopObservationRecord>(UnityEngine.JsonUtility.ToJson(_record));
            Assert.That(restored.id, Is.EqualTo(_record.id)); Assert.That(restored.deadline, Is.EqualTo(31000));
            Assert.That(restored.startedAt, Is.EqualTo(1000)); Assert.That(restored.intervalMs, Is.EqualTo(100));
            _record = restored; _now = 30000; Start(); _now = 31000; _observer.Tick();
            Assert.That(_record.status, Is.EqualTo("timed_out")); Assert.That(_observer.Subscribed, Is.False);
        }
        [Test] public void ParentDeadlineIsSharedAndLateProbeCannotTurnExpiryIntoSuccess()
        {
            _record = ServiceStopObservationRecord.Start(1000, 30, 100, 5000);
            Assert.That(_record.deadline, Is.EqualTo(5000));
            Start(() => { _now = 5001; _record.End(_now, "succeeded", "too late"); });
            Assert.That(_record.status, Is.EqualTo("timed_out")); Assert.That(_observer.Subscribed, Is.False);
        }
        [UnityTest] public IEnumerator WaitingForExitAllowsUnrelatedEditorUpdate()
        {
            Start(); bool pumped = false; EditorApplication.CallbackFunction witness = () => pumped = true;
            EditorApplication.update += witness;
            try { for (int i = 0; i < 20 && !pumped; ++i) yield return null; Assert.That(pumped, Is.True); Assert.That(_record.Active, Is.True); }
            finally { EditorApplication.update -= witness; }
        }
        [TestCase("succeeded")] [TestCase("timed_out")] [TestCase("failed")]
        public void ReloadRetainsTerminalStopIntentUntilExplicitStart(string terminal)
        {
            const string key = "UPilot.ServiceStopObservation.v1";
            const string launchKey = "UPilot.ServerLaunchIdentity.v1";
            var previous = SessionState.GetString(key, "");
            var previousLaunch = SessionState.GetString(launchKey, "");
            var restartRecord = typeof(UPilotServerRestartDiagnostics).GetField("s_record", BindingFlags.Static | BindingFlags.NonPublic);
            var previousRestart = restartRecord.GetValue(null);
            restartRecord.SetValue(null, new UPilotServerRestartRecord { status = "succeeded" });
            SessionState.EraseString(launchKey);
            var manager = (UPilotMcpServerManager)FormatterServices.GetUninitializedObject(typeof(UPilotMcpServerManager));
            try
            {
                _record.project = UPilotProjectConfig.ProjectRoot;
                _record.lifetime = UPilotServiceLifetime.Id;
                _record.unityPid = System.Diagnostics.Process.GetCurrentProcess().Id;
                _record.originalStartGeneration = 17; _record.originalRestartGeneration = 23;
                _record.End(2000, terminal, "original");
                SessionState.SetString(key, UnityEngine.JsonUtility.ToJson(_record));
                manager.EnsureInitialServiceRecovery();
                Assert.That(manager.StopObservation.id, Is.EqualTo(_record.id));
                Assert.That(manager.StopObservation.originalStartGeneration, Is.EqualTo(17));
                Assert.That(manager.StopObservation.originalRestartGeneration, Is.EqualTo(23));
                Assert.That(manager.StopBlocksAutomaticRecovery, Is.True);
                Assert.That(manager.HasActiveStopObservation, Is.False);
                Assert.That(manager.CanAutomaticallyStartServer, Is.False);
                Assert.That(manager.CanAutomaticallyRepairDeployment, Is.False);
                Assert.That(manager.PeriodicHealthChecksAllowed, Is.False);
                manager.ResetStopObservation("new_service_start_or_restart");
                Assert.That(manager.StopObservation, Is.Null);
                Assert.That(manager.StopBlocksAutomaticRecovery, Is.False);
                Assert.That(SessionState.GetString(key, ""), Is.Empty);
            }
            finally
            {
                SessionState.SetString(key, previous);
                SessionState.SetString(launchKey, previousLaunch);
                restartRecord.SetValue(null, previousRestart);
            }
        }
        [Test] public void SavedStopScopeRejectsOtherProjectLifetimeAndEditor()
        {
            _record.project = "D:/upilot/Tests~/UPilotTest"; _record.lifetime = "life"; _record.unityPid = 123;
            Assert.That(_record.MatchesScope("D:\\upilot\\Tests~\\UPilotTest", "life", 123), Is.True);
            Assert.That(_record.MatchesScope("D:/other/UPilotTest", "life", 123), Is.False);
            Assert.That(_record.MatchesScope(_record.project, "new life", 123), Is.False);
            Assert.That(_record.MatchesScope(_record.project, "life", 124), Is.False);
        }
        [Test] public void CompletedUpdateStopCannotBeAutomaticallyReplayedByAnotherWindow()
        {
            _record.ownerOperationId = "update-1";
            Assert.That(_record.PreventsAutomaticUpdateRetry("update-1"), Is.False);
            _record.End(2000, "timed_out", "timeout");
            Assert.That(_record.PreventsAutomaticUpdateRetry("update-1"), Is.True);
            Assert.That(_record.PreventsAutomaticUpdateRetry("update-2"), Is.False);
            Assert.That(_record.PreventsAutomaticUpdateRetry(""), Is.False);
        }
        [Test] public void StopLogsShowDefaultEffectiveBudgetElapsedAndNoOp()
        {
            Assert.That(_record.StartMessage, Does.Contain("默认最多等待30秒"));
            Assert.That(_record.StartMessage, Does.Contain("有效预算"));
            _record.processesExited = _record.portsAvailable = true;
            _record.End(2250, "succeeded", "already_stopped");
            Assert.That(_record.TerminalMessage, Does.Contain("无需再次停止"));
            Assert.That(_record.TerminalMessage, Does.Contain((1.25d).ToString("F2") + "秒"));
            Assert.That(_record.endedAt - _record.startedAt, Is.EqualTo(1250));
        }
        [Test] public void DuplicateProductionStopSharesOriginalTaskAndNeverPreparesAnotherTarget()
        {
            var manager = (UPilotMcpServerManager)FormatterServices.GetUninitializedObject(typeof(UPilotMcpServerManager));
            Set(manager, "_initialServiceRecoveryCompleted", true);
            var completion = new TaskCompletionSource<UPilotMcpServerManager.ServerStopResult>();
            Set(manager, "_stopObservation", _record); Set(manager, "_stopCompletion", completion);
            var first = manager.StopServerAndObserveExitAsync(UPilotServerStopOrigin.User, "first");
            var duplicate = manager.StopServerAndObserveExitAsync(UPilotServerStopOrigin.PackageUpdate, "duplicate", 600000);
            Assert.That(first, Is.SameAs(completion.Task)); Assert.That(duplicate, Is.SameAs(first));
            Assert.That(_record.deadline, Is.EqualTo(31000));
            Assert.That(Get(manager, "_stopPreparation"), Is.Null);
            Assert.That(_record.dispatched, Is.False);
        }
        [Test] public void ExpiredParentDeadlineLogsStartAndOneTimedOutEndWithoutPreparingOrStopping()
        {
            const string key = "UPilot.ServiceStopObservation.v1";
            var previous = SessionState.GetString(key, "");
            var manager = (UPilotMcpServerManager)FormatterServices.GetUninitializedObject(typeof(UPilotMcpServerManager));
            Set(manager, "_initialServiceRecoveryCompleted", true);
            Set(manager, "_statusLock", new object()); Set(manager, "_stoppingProcesses", new List<System.Diagnostics.Process>());
            try
            {
                LogAssert.Expect(UnityEngine.LogType.Log, new System.Text.RegularExpressions.Regex("开始停止 MCP 服务.*默认最多等待30秒"));
                LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("MCP 服务停止观察结束.*总耗时.*状态=timed_out"));
                var work = manager.StopServerAndObserveExitAsync(UPilotServerStopOrigin.User,
                    "expired-parent", parentDeadlineUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1);
                Assert.That(work.IsCompleted, Is.True); Assert.That(work.Result.StopConfirmed, Is.False);
                Assert.That(manager.StopObservation.requestId, Is.EqualTo("expired-parent"));
                Assert.That(manager.StopObservation.status, Is.EqualTo("timed_out"));
                Assert.That(manager.StopObservation.dispatched, Is.False);
                Assert.That(Get(manager, "_stopPreparation"), Is.Null); Assert.That(Get(manager, "_stopObserver"), Is.Null);
                Assert.That(manager.StopObservation.endedAt, Is.GreaterThanOrEqualTo(manager.StopObservation.startedAt));
                manager.ResetStopObservation("late reset");
                LogAssert.NoUnexpectedReceived();
            }
            finally { SessionState.SetString(key, previous); }
        }
        [Test] public void LateTimedOutStopEvidenceNeverAuthorizesUpdateContinuation()
        {
            var manager = (UPilotMcpServerManager)FormatterServices.GetUninitializedObject(typeof(UPilotMcpServerManager));
            var result = new UPilotMcpServerManager.ServerStopResult(true, true, 0, 0, 0, observationSucceeded: false);
            Assert.That(result.StopConfirmed, Is.True, "Physical exit evidence is retained separately.");
            Assert.That(manager.IsStopResultCurrent(result), Is.False, "Expiry must prevent the update handoff even if ports later became free.");
            Assert.That(UPilotMainWindow.IsUpdateStopConfirmed(default, result, manager.IsStopResultCurrent(result)), Is.False);
        }
        [Test] public void ProductionNoTargetStopEndsLifecycleCompletesOnceAndReleasesObservation()
        {
            const string stopKey = "UPilot.ServiceStopObservation.v1", launchKey = "UPilot.ServerLaunchIdentity.v1";
            var savedStop = SessionState.GetString(stopKey, "");
            var savedLaunch = SessionState.GetString(launchKey, "");
            var manager = (UPilotMcpServerManager)FormatterServices.GetUninitializedObject(typeof(UPilotMcpServerManager));
            Set(manager, "_statusLock", new object());
            Set(manager, "_stoppingProcesses", new List<System.Diagnostics.Process>());
            Set(manager, "_restartOperationId", "");
            var completion = new TaskCompletionSource<UPilotMcpServerManager.ServerStopResult>();
            _record = ServiceStopObservationRecord.Start(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 30, 100);
            _record.httpPort = FreePort(); _record.wsPort = FreePort();
            Set(manager, "_stopObservation", _record); Set(manager, "_stopCompletion", completion);
            var preparedType = typeof(UPilotMcpServerManager).GetNestedType("PreparedProcessStop", BindingFlags.NonPublic);
            var prepared = Activator.CreateInstance(preparedType, true);
            preparedType.GetField("HttpPort", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(prepared, _record.httpPort);
            preparedType.GetField("WsPort", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(prepared, _record.wsPort);
            preparedType.GetField("VerifiedPorts", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(prepared, new Dictionary<int, List<int>>());
            var preparation = typeof(Task).GetMethod("FromResult").MakeGenericMethod(preparedType).Invoke(null, new[] { prepared });
            Set(manager, "_stopPreparation", preparation);
            var poll = typeof(UPilotMcpServerManager).GetMethod("PollStopObservation", BindingFlags.Instance | BindingFlags.NonPublic);
            try
            {
                poll.Invoke(manager, null);
                Assert.That(_record.status, Is.EqualTo("succeeded")); Assert.That(_record.dispatched, Is.True);
                Assert.That(_record.reason, Is.EqualTo("already_stopped"));
                Assert.That(Get(manager, "_lifecyclePhase"), Is.EqualTo(UPilotServerLifecyclePhase.None));
                Assert.That(Get(manager, "_stopPreparation"), Is.Null);
                Assert.That(manager.HasActiveStopObservation, Is.False);
                Assert.That(completion.Task.IsCompleted, Is.True);
                Assert.That(manager.IsStopResultCurrent(completion.Task.Result), Is.True);
                var generation = Get(manager, "_statusGeneration");
                poll.Invoke(manager, null);
                Assert.That(Get(manager, "_statusGeneration"), Is.EqualTo(generation));
                Assert.That(Get(manager, "_stopObserver"), Is.Null);
            }
            finally
            {
                ((System.Threading.CancellationTokenSource)Get(manager, "_lifecycleQueryCancellation"))?.Dispose();
                SessionState.SetString(stopKey, savedStop); SessionState.SetString(launchKey, savedLaunch);
            }
        }
        [TestCase(false)] [TestCase(true)]
        public void StopPreparationFailureRetainsExceptionClassificationAndRemainsVisible(bool transient)
        {
            const string key = "UPilot.ServiceStopObservation.v1";
            var previous = SessionState.GetString(key, "");
            var manager = (UPilotMcpServerManager)FormatterServices.GetUninitializedObject(typeof(UPilotMcpServerManager));
            Set(manager, "_statusLock", new object());
            Set(manager, "_stoppingProcesses", new List<System.Diagnostics.Process>());
            Set(manager, "_restartOperationId", "");
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _record = ServiceStopObservationRecord.Start(now, 30, 100);
            _record.project = UPilotProjectConfig.ProjectRoot;
            _record.lifetime = UPilotServiceLifetime.Id;
            _record.unityPid = System.Diagnostics.Process.GetCurrentProcess().Id;
            Set(manager, "_stopObservation", _record);
            Set(manager, "_stopCompletion", new TaskCompletionSource<UPilotMcpServerManager.ServerStopResult>());
            Exception failure;
            try
            {
                if (transient) throw new TimeoutException("stop fixture request timeout");
                throw new InvalidOperationException("stop fixture identity rejected");
            }
            catch (Exception ex) { failure = ex; }
            var preparedType = typeof(UPilotMcpServerManager).GetNestedType("PreparedProcessStop", BindingFlags.NonPublic);
            var fromException = Array.Find(typeof(Task).GetMethods(BindingFlags.Public | BindingFlags.Static),
                method => method.Name == "FromException" && method.IsGenericMethodDefinition);
            Set(manager, "_stopPreparation", fromException.MakeGenericMethod(preparedType).Invoke(null, new object[] { failure }));
            try
            {
                LogAssert.Expect(UnityEngine.LogType.Log,
                    $"[UPilotMcpServerManager] 生命周期：正在关闭服务器；PID=42；operationId={_record.id}。");
                var generation = manager.SetLifecyclePhase(UPilotServerLifecyclePhase.Stopping, 42, _record.id, _record.deadline);
                Set(manager, "_stopLifecycleGeneration", generation);
                LogAssert.Expect(UnityEngine.LogType.Log,
                    $"[UPilotMcpServerManager] 生命周期：生命周期操作结束；PID=42；operationId={_record.id}。");
                LogAssert.Expect(UnityEngine.LogType.Error,
                    new System.Text.RegularExpressions.Regex("MCP 服务停止观察结束.*状态=failed.*原因=" + failure.Message));
                typeof(UPilotMcpServerManager).GetMethod("PollStopObservation", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(manager, null);
                Assert.That(_record.failureException, Is.SameAs(failure));
                Assert.That(_record.failureException.StackTrace, Does.Contain(nameof(StopPreparationFailureRetainsExceptionClassificationAndRemainsVisible)));
                Assert.That(_record.status, Is.EqualTo("failed"));
                Assert.That(_record.dispatched, Is.False);
                var status = manager.GetStatus();
                Assert.That(status.ErrorMessage, Is.EqualTo(failure.Message));
                Assert.That(status.FailureKind, Is.EqualTo(transient ? McpStatusFailureKind.Transient : McpStatusFailureKind.Confirmed));
                Assert.That(UPilotHealthAssessment.Classify(default, status), Is.EqualTo(transient
                    ? UPilotHealthDisposition.Unconfirmed : UPilotHealthDisposition.ConfirmedFailure));
                for (var i = 0; i < 2; ++i)
                {
                    var view = UPilotQuickStart.EvaluateStopFailure(manager.StopObservation, false, false);
                    Assert.That(view.HasValue, Is.True);
                    Assert.That(view.Value.State, Is.EqualTo(UPilotMainState.NeedsRepair));
                    Assert.That(view.Value.Message, Does.Contain(failure.Message));
                }
                Assert.That(Get(manager, "_stopPreparation"), Is.Null);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                ((System.Threading.CancellationTokenSource)Get(manager, "_lifecycleQueryCancellation"))?.Dispose();
                SessionState.SetString(key, previous);
                Logger.CloseDiagnostics(_record.id);
            }
        }
        [Test]
        public void CancelledStopDoesNotLeaveAnErrorInTheHealthCache()
        {
            const string key = "UPilot.ServiceStopObservation.v1";
            var previous = SessionState.GetString(key, "");
            var manager = (UPilotMcpServerManager)FormatterServices.GetUninitializedObject(typeof(UPilotMcpServerManager));
            Set(manager, "_statusLock", new object());
            Set(manager, "_stoppingProcesses", new List<System.Diagnostics.Process>());
            Set(manager, "_stopObservation", _record);
            try
            {
                LogAssert.Expect(UnityEngine.LogType.Log,
                    $"[UPilotMcpServerManager] 生命周期：正在关闭服务器；PID=42；operationId={_record.id}。");
                var generation = manager.SetLifecyclePhase(UPilotServerLifecyclePhase.Stopping, 42, _record.id, _record.deadline);
                Set(manager, "_stopLifecycleGeneration", generation);
                LogAssert.Expect(UnityEngine.LogType.Log,
                    $"[UPilotMcpServerManager] 生命周期：生命周期操作结束；PID=42；operationId={_record.id}。");
                LogAssert.Expect(UnityEngine.LogType.Log, new System.Text.RegularExpressions.Regex(
                    System.Text.RegularExpressions.Regex.Escape("[UPilot] MCP 服务停止观察结束；stopId=" + _record.id + "；总耗时") +
                    @"\d+(?:[.,]\d+)?秒；状态=stopped；进程退出=False；端口释放=False；原因=new_service_start_or_restart。"));
                typeof(UPilotMcpServerManager).GetMethod("FinishStopObservation", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(manager, new object[] { "stopped", "new_service_start_or_restart" });
                Assert.That(_record.status, Is.EqualTo("stopped"));
                Assert.That(manager.GetStatus().ErrorMessage, Is.Null.Or.Empty);
                Assert.That(manager.GetStatus().FailureKind, Is.EqualTo(McpStatusFailureKind.None));
                Assert.That(UPilotQuickStart.EvaluateStopFailure(_record, false, false), Is.Null);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                ((System.Threading.CancellationTokenSource)Get(manager, "_lifecycleQueryCancellation"))?.Dispose();
                SessionState.SetString(key, previous);
            }
        }
        [TestCase("observing")] [TestCase("stopped")] [TestCase("succeeded")]
        public void StopViewDoesNotInventFailureForNonFailureOutcomes(string status)
        {
            _record.project = UPilotProjectConfig.ProjectRoot;
            _record.lifetime = UPilotServiceLifetime.Id;
            _record.unityPid = System.Diagnostics.Process.GetCurrentProcess().Id;
            _record.status = status;
            Assert.That(UPilotQuickStart.EvaluateStopFailure(_record, false, false), Is.Null);
        }
        [Test]
        public void StopTimeoutRemainsVisibleOnlyForTheCurrentProjectAndServiceLifetime()
        {
            _record.project = UPilotProjectConfig.ProjectRoot;
            _record.lifetime = UPilotServiceLifetime.Id;
            _record.unityPid = System.Diagnostics.Process.GetCurrentProcess().Id;
            _record.End(31000, "timed_out", "stop_observation_timeout");
            Assert.That(UPilotQuickStart.EvaluateStopFailure(_record, false, false)?.State, Is.EqualTo(UPilotMainState.NeedsRepair));
            _record.project = System.IO.Path.Combine(_record.project, "other-project");
            Assert.That(UPilotQuickStart.EvaluateStopFailure(_record, false, false), Is.Null);
            _record.project = UPilotProjectConfig.ProjectRoot;
            _record.lifetime = "other-service-lifetime";
            Assert.That(UPilotQuickStart.EvaluateStopFailure(_record, false, false), Is.Null);
            _record.lifetime = UPilotServiceLifetime.Id;
            ++_record.unityPid;
            Assert.That(UPilotQuickStart.EvaluateStopFailure(_record, false, false), Is.Null);
        }
        private static object Get(UPilotMcpServerManager manager, string field) => typeof(UPilotMcpServerManager).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
        private static void Set(UPilotMcpServerManager manager, string field, object value) => typeof(UPilotMcpServerManager).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, value);
        private static int FreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start(); var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
        }
    }
}
