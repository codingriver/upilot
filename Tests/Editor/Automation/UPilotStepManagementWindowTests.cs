using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests.Automation
{
    public sealed class UPilotStepManagementWindowTests
    {
        private UPilotStepManagementWindow _window;
        private bool _hadRefreshPreference;
        private int _refreshPreference;
        [SetUp] public void SetUp()
        {
            string key = UPilotTimingSettings.Key(nameof(UPilotTimingSettings.StepWindowRefreshMs));
            _hadRefreshPreference = EditorPrefs.HasKey(key); _refreshPreference = EditorPrefs.GetInt(key);
            EditorPrefs.DeleteKey(key);
            _guiOpened = _guiShown = _readFails = false;
            _unexpectedStopCalls = 0;
            _window = ScriptableObject.CreateInstance<UPilotStepManagementWindow>();
        }
        [TearDown] public void TearDown()
        {
            try
            {
                if (_window != null)
                {
                    _window.ObserveGui = null;
                    _window.ConfirmImmediateStop = null;
                    _window.StopImmediately = null;
                    _window.ReadCurrent = null;
                    try { if (_guiShown) _window.Close(); }
                    finally { if (_window != null) UnityEngine.Object.DestroyImmediate(_window); }
                }
                if (_guiOpened)
                {
                    Assert.That(_unexpectedStopCalls, Is.Zero, "Read-only interactions must never dispatch a real or unexpected stop.");
                    var current = UPilotAutomationStepService.GetCurrentSnapshot();
                    Assert.That(current.initialized, Is.EqualTo(_serviceBeforeGui.initialized));
                    Assert.That(current.error, Is.EqualTo(_serviceBeforeGui.error));
                    Assert.That(current.busy, Is.EqualTo(_serviceBeforeGui.busy));
                    Assert.That(current.run, Is.SameAs(_serviceBeforeGui.run), "Read-only window interactions must not replace the service run.");
                    Assert.That(JsonUtility.ToJson(current.run), Is.EqualTo(_serviceRunJson));
                }
            }
            finally
            {
                if (_guiOpened) EditorGUIUtility.systemCopyBuffer = _clipboard;
                string key = UPilotTimingSettings.Key(nameof(UPilotTimingSettings.StepWindowRefreshMs));
                if (_hadRefreshPreference) EditorPrefs.SetInt(key, _refreshPreference); else EditorPrefs.DeleteKey(key);
            }
        }
        private static UPilotAutomationStepService.CurrentSnapshot Running(string id) => new()
        {
            initialized = true, busy = true,
            run = new AutomationStepRun { runId = id, operationId = "operation", serviceLifecycleId = "lifetime" }
        };
        [Test] public void DetailsRetainTerminalButClearUnknownAndSelectionOnIdentityChange()
        {
            var one = Running("one");
            _window.AcceptSnapshot(one); _window.SelectStep(one.run, "instance");
            Assert.That(_window.SelectedKey, Does.Contain("lifetime:operation:one:instance"));
            one.busy = false; one.run.terminal = true;
            _window.AcceptSnapshot(one);
            Assert.That(UPilotStepManagementWindow.VisibleRun(_window.Latest), Is.SameAs(one.run));
            Assert.That(UPilotStepManagementWindow.Recorded(one.run.cleanupOutcome), Is.EqualTo("未记录"));
            Assert.That(UPilotStepManagementWindow.Recorded(one.run.reportOutcome), Is.EqualTo("未记录"));
            _window.AcceptSnapshot(Running("two")); Assert.That(_window.SelectedKey, Is.Empty);
            var failed = new UPilotAutomationStepService.CurrentSnapshot { error = "read failed", run = one.run };
            _window.AcceptSnapshot(failed);
            Assert.That(UPilotStepManagementWindow.VisibleRun(_window.Latest), Is.Null);
        }
        [Test] public void KnownEmptyAndMissingDetailValuesRemainDistinct()
        {
            Assert.That(UPilotStepManagementWindow.ArgumentsText(""), Is.EqualTo("\"\"（空字符串）"));
            Assert.That(UPilotStepManagementWindow.ArgumentsText(null), Is.EqualTo("未记录"));
            Assert.That(UPilotStepManagementWindow.ResourceText(Array.Empty<string>()), Is.EqualTo("0 项"));
            Assert.That(UPilotStepManagementWindow.ResourceText(null), Is.EqualTo("未记录"));
            Assert.That(UPilotStepManagementWindow.RunDuration(new AutomationStepRun { terminal = true }, Timestamp), Is.EqualTo("未知"));
            Assert.That(UPilotStepManagementWindow.RunDuration(new AutomationStepRun { terminal = true, startedAtUtcMs = Timestamp }, Timestamp), Is.EqualTo("未知"));
        }
        [Test] public void CoreMenuIsBelowTracerAndDoesNotRequireOptionalAssembly()
        {
            var menu = typeof(UPilotStepManagementWindow).GetMethod("Open").GetCustomAttribute<MenuItem>();
            Assert.That(menu.menuItem, Is.EqualTo("UPilot/Step管理")); Assert.That(menu.priority, Is.EqualTo(401));
            Assert.That(typeof(UPilotStepManagementWindow).Assembly.GetName().Name, Is.EqualTo("UPilot.Editor"));
            Assert.That(_window.titleContent.text, Is.EqualTo("Step管理"));
        }
        [Test] public void OneSecondSamplingAndManualRefreshNeverCatchUpInBursts()
        {
            int reads = 0;
            _window.ReadCurrent = () => { reads++; return new UPilotAutomationStepService.CurrentSnapshot { initialized = true }; };
            _window.Refresh(10); Assert.That(reads, Is.EqualTo(1));
            _window.RefreshIfDue(10.999); Assert.That(reads, Is.EqualTo(1));
            _window.RefreshIfDue(11); Assert.That(reads, Is.EqualTo(2));
            _window.RefreshIfDue(30); _window.RefreshIfDue(30); Assert.That(reads, Is.EqualTo(3));
            _window.Refresh(30.1); Assert.That(reads, Is.EqualTo(4));
            _window.RefreshIfDue(31); Assert.That(reads, Is.EqualTo(4));
        }
        [Test] public void ChangedRefreshPeriodAdjustsNextReadWithoutCatchUpOrTaskProgression()
        {
            int reads = 0;
            _window.ReadCurrent = () => { reads++; return Running("original"); };
            _window.Refresh(10);
            UPilotTimingSettings.StepWindowRefreshMs = 3000;
            _window.RefreshIfDue(11); Assert.That(reads, Is.EqualTo(1));
            UPilotTimingSettings.StepWindowRefreshMs = 500;
            _window.RefreshIfDue(11); Assert.That(reads, Is.EqualTo(2));
            _window.RefreshIfDue(11); Assert.That(reads, Is.EqualTo(2));
            Assert.That(_window.Latest.run.runId, Is.EqualTo("original")); Assert.That(_window.Latest.run.terminal, Is.False);
        }
        [Test] public void OpeningReadsImmediatelyAndClosingUnsubscribesWithoutStoppingTask()
        {
            int reads = 0;
            _window.ReadCurrent = () => { reads++; return Running("original"); };
            typeof(UPilotStepManagementWindow).GetMethod("OnEnable", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_window, null);
            Assert.That(reads, Is.EqualTo(1)); Assert.That(_window.Latest.run.terminal, Is.False);
            typeof(UPilotStepManagementWindow).GetMethod("OnDisable", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_window, null);
            Assert.That(_window.Latest, Is.Null);
            var update = EditorApplication.update;
            Assert.That(Array.Find(update?.GetInvocationList() ?? Array.Empty<Delegate>(), d => ReferenceEquals(d.Target, _window)), Is.Null);
        }
        [Test] public void SameRunKeepsScrollAndNewRunOrCompletionResetsIt()
        {
            _window.AcceptSnapshot(Running("one")); _window.Scroll = new Vector2(0, 700);
            _window.AcceptSnapshot(Running("one")); Assert.That(_window.Scroll.y, Is.EqualTo(700));
            _window.AcceptSnapshot(Running("two")); Assert.That(_window.Scroll, Is.EqualTo(Vector2.zero));
            _window.Scroll = new Vector2(0, 400); var ended = Running("two"); ended.busy = false; ended.run.terminal = true;
            _window.AcceptSnapshot(ended); Assert.That(_window.Scroll, Is.EqualTo(Vector2.zero));
            Assert.That(UPilotStepManagementWindow.CanStop(ended), Is.False);
        }
        [Test] public void UnknownOrReadErrorIsNotIdleAndTerminalResourceReleaseCannotBeStoppedAgain()
        {
            _window.ReadCurrent = () => throw new InvalidOperationException("read failed"); _window.Refresh(0);
            Assert.That(_window.Latest.initialized, Is.False); Assert.That(_window.Latest.error, Does.Contain("read failed"));
            Assert.That(UPilotStepManagementWindow.CanStop(_window.Latest), Is.False);
            var snapshot = Running("release"); snapshot.run.terminal = true; snapshot.run.stage = "ResourceReleasing";
            _window.AcceptSnapshot(snapshot);
            Assert.That(_window.Latest.busy, Is.True); Assert.That(UPilotStepManagementWindow.CanStop(snapshot), Is.False);
        }
        [TestCase("Pending", "NotStarted", "未开始")]
        [TestCase("Executing", "NotStarted", "执行中／等待 Poll")]
        [TestCase("Cleaning", "Pending", "业务清理中")]
        [TestCase("ResourceCleaning", "Verified", "框架资源收尾中")]
        public void RowsReflectActualStage(string stage, string cleanup, string label)
        { Assert.That(UPilotStepManagementWindow.StepState(new AutomationStepRecord { stage = stage, cleanupState = cleanup }), Is.EqualTo(label)); }

        private readonly Dictionary<string, (string text, Rect rect)> _drawn = new();
        private int _repaints, _reads, _unexpectedStopCalls;
        private bool _guiOpened, _guiShown, _readFails;
        private string _clipboard, _serviceRunJson;
        private UPilotAutomationStepService.CurrentSnapshot _sample, _serviceBeforeGui;

        private void OpenGui()
        {
            _guiOpened = true;
            _serviceBeforeGui = UPilotAutomationStepService.GetCurrentSnapshot();
            _serviceRunJson = JsonUtility.ToJson(_serviceBeforeGui.run);
            _clipboard = EditorGUIUtility.systemCopyBuffer;
            UPilotTimingSettings.StepWindowRefreshMs = 5000;
            _sample = Running("gui-one");
            _sample.run.plan = new AutomationStepPlan { steps = new[] { new AutomationStepItem
            {
                instanceId = "gui-instance", stepId = "upilot.wait_seconds", arguments = "{\"中文\":\"原始参数\"}",
                timeoutSeconds = 60, cleanupTimeoutSeconds = 10, pollIntervalSeconds = 0.25,
            } } };
            _sample.run.steps.Add(new AutomationStepRecord
            {
                instanceId = "gui-instance", stage = "Completed", outcome = AutomationStepStatus.Failed,
                error = new AutomationStepError { code = "STEP_ERROR", message = "step failure" },
                cleanupState = "Unknown",
            });
            _sample.run.error = new AutomationStepError { code = "FIRST_ERROR", message = "task failure" };
            _sample.run.secondaryErrors.Add(new AutomationStepError { code = "SECOND_ERROR", message = "secondary failure" });
            _sample.run.registeredArtifacts.Add(new AutomationReportArtifact { path = "Logs/证据/attachment.json" });
            _window.ReadCurrent = () =>
            {
                _reads++;
                if (_readFails) throw new InvalidOperationException("isolated read failure");
                return _sample;
            };
            _window.ConfirmImmediateStop = () => throw new AssertionException("Unexpected stop confirmation");
            _window.StopImmediately = (_, _, _) =>
            {
                _unexpectedStopCalls++;
                return new UPilotAutomationStepService.StopReply { status = "not_found" };
            };
            _window.ObserveGui = (label, text, rect) =>
            {
                if (label == null) { _drawn.Clear(); _repaints++; }
                else _drawn[label] = (text, rect);
            };
            _window.Refresh(EditorApplication.timeSinceStartup);
            _window.ShowUtility();
            _guiShown = true;
            _window.position = new Rect(40, 40, 1000, 1000);
        }
        private IEnumerator RepaintGui()
        {
            int previous = _repaints;
            double deadline = EditorApplication.timeSinceStartup + 10;
            do
            {
                _window.Repaint();
                yield return null;
            } while (_repaints <= previous && EditorApplication.timeSinceStartup < deadline);
            Assert.That(_repaints, Is.GreaterThan(previous), "A real owner-window Repaint must complete.");
        }
        private IEnumerator ClickGui(string label)
        {
            yield return RepaintGui();
            Assert.That(_drawn.ContainsKey(label), Is.True, "Missing rendered control: " + label);
            Rect rect = _drawn[label].rect;
            Assert.That(rect.width, Is.GreaterThan(0));
            Assert.That(rect.height, Is.GreaterThan(0));
            Assert.That(new Rect(Vector2.zero, _window.position.size).Contains(rect.center), Is.True,
                "The actual client-space control must be visible: " + label + " " + rect);
            _window.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = rect.center });
            _window.SendEvent(new Event { type = EventType.MouseUp, button = 0, clickCount = 1, mousePosition = rect.center });
            yield return RepaintGui();
        }
        [UnityTest] public IEnumerator RealGuiTaskDetailsAndCopyPreserveMissingOutcomes()
        {
            OpenGui();
            _sample.busy = false; _sample.run.terminal = true; _sample.run.status = "Succeeded";
            _window.Refresh(EditorApplication.timeSinceStartup);
            yield return ClickGui("任务详情");
            Assert.That(_drawn["首错"].text, Does.Contain("FIRST_ERROR"));
            Assert.That(_drawn["次错"].text, Does.Contain("SECOND_ERROR"));
            Assert.That(_drawn["清理结果"].text, Is.EqualTo("未记录"));
            Assert.That(_drawn["报告结果"].text, Is.EqualTo("未记录"));
            Assert.That(_drawn["附件路径"].text, Is.EqualTo("Logs/证据/attachment.json"));
            yield return ClickGui("复制:Logs/证据/attachment.json");
            Assert.That(EditorGUIUtility.systemCopyBuffer, Is.EqualTo("Logs/证据/attachment.json"));
            yield return ClickGui("任务详情");
            Assert.That(_drawn.ContainsKey("首错"), Is.False);
            LogAssert.NoUnexpectedReceived();
        }
        [UnityTest] public IEnumerator RealGuiSelectionRendersStepErrorArgumentsAndFrozenTiming()
        {
            OpenGui();
            yield return ClickGui("instance:gui-instance");
            Assert.That(_window.SelectedKey, Is.EqualTo("lifetime:operation:gui-one:gui-instance"));
            Assert.That(_drawn["步骤错误"].text, Does.Contain("STEP_ERROR").And.Not.Contain("FIRST_ERROR"));
            Assert.That(_drawn["原始参数"].text, Is.EqualTo(_sample.run.plan.steps[0].arguments));
            Assert.That(_drawn["Cleanup"].text, Is.EqualTo("Unknown"));
            Assert.That(_drawn["执行开始"].text, Is.EqualTo("未知"));
            Assert.That(_drawn["清理开始"].text, Is.EqualTo("未知"));
            Assert.That(_drawn["完成时间"].text, Is.EqualTo("未知"));
            Assert.That(_drawn["运行计划时间值（秒）"].text, Is.EqualTo("执行=60；Cleanup=10；轮询=0.25\n来源未记录"));
            yield return ClickGui("选中 Step 详情");
            Assert.That(_drawn.ContainsKey("原始参数"), Is.False);
            LogAssert.NoUnexpectedReceived();
        }
        [UnityTest] public IEnumerator RealGuiRefreshTerminalSwitchAndReadFailureNeverShowStaleRun()
        {
            OpenGui();
            yield return RepaintGui();
            int previousReads = _reads;
            yield return ClickGui("刷新");
            Assert.That(_reads, Is.EqualTo(previousReads + 1));
            Assert.That(_drawn["刷新说明"].text, Does.StartWith("每 5000 毫秒刷新"));
            yield return ClickGui("instance:gui-instance");
            _sample.busy = false; _sample.run.terminal = true; _sample.run.status = "Succeeded";
            yield return ClickGui("刷新");
            Assert.That(_drawn["状态提示"].text, Is.EqualTo("最近结束任务：Succeeded"));
            Assert.That(_drawn["Run ID"].text, Is.EqualTo("gui-one"));
            _sample = Running("gui-two");
            yield return ClickGui("刷新");
            Assert.That(_window.SelectedKey, Is.Empty);
            Assert.That(_drawn["Run ID"].text, Is.EqualTo("gui-two"));
            _readFails = true;
            yield return ClickGui("刷新");
            Assert.That(_drawn["状态提示"].text, Does.Contain("执行器状态未知：isolated read failure"));
            Assert.That(_drawn.ContainsKey("Run ID"), Is.False);
            Assert.That(_drawn.ContainsKey("原始参数"), Is.False);
            _readFails = false; _sample = new UPilotAutomationStepService.CurrentSnapshot { error = "STEP_SERVICE_INITIALIZING" };
            yield return ClickGui("刷新");
            Assert.That(_drawn["状态提示"].text, Does.Contain("Step 服务初始化中"));
            Assert.That(_drawn.ContainsKey("Run ID"), Is.False);
            LogAssert.NoUnexpectedReceived();
        }
        [UnityTest] public IEnumerator RealGuiStopConfirmationCannotRedirectToReplacementTask()
        {
            OpenGui();
            int confirmations = 0, stops = 0;
            string receivedRun = null, receivedOperation = null, receivedLifetime = null;
            bool replacementRejected = false;
            _window.ConfirmImmediateStop = () =>
            {
                confirmations++;
                // Simulate an update of even the original snapshot while a native dialog is open.
                _sample.run.runId = "gui-two"; _sample.run.operationId = "operation-two";
                _sample.run.serviceLifecycleId = "lifetime-two";
                _window.Refresh(EditorApplication.timeSinceStartup);
                return true;
            };
            _window.StopImmediately = (run, operation, lifetime) =>
            {
                stops++;
                receivedRun = run; receivedOperation = operation; receivedLifetime = lifetime;
                replacementRejected = run != _sample.run.runId || operation != _sample.run.operationId
                    || lifetime != _sample.run.serviceLifecycleId;
                return new UPilotAutomationStepService.StopReply { status = replacementRejected ? "not_found" : "stopped",
                    changed = !replacementRejected };
            };
            yield return ClickGui("立即停止整个任务");
            Assert.That(confirmations, Is.EqualTo(1)); Assert.That(stops, Is.EqualTo(1));
            // Assert outside the production exception boundary: a caught assertion must never pass this test.
            Assert.That(receivedRun, Is.EqualTo("gui-one"));
            Assert.That(receivedOperation, Is.EqualTo("operation"));
            Assert.That(receivedLifetime, Is.EqualTo("lifetime"));
            Assert.That(replacementRejected, Is.True);
            Assert.That(_sample.run.runId, Is.EqualTo("gui-two")); Assert.That(_sample.run.terminal, Is.False);
            Assert.That(_drawn["Run ID"].text, Is.EqualTo("gui-two"));
            _window.ConfirmImmediateStop = () => { confirmations++; return false; };
            yield return ClickGui("立即停止整个任务");
            Assert.That(confirmations, Is.EqualTo(2)); Assert.That(stops, Is.EqualTo(1));
            Assert.That(_sample.run.terminal, Is.False);
            LogAssert.NoUnexpectedReceived();
        }

        private const long Timestamp = 1791624407000;
        [TestCase(0L, 0L)]
        [TestCase(0L, Timestamp)]
        [TestCase(-1L, Timestamp)]
        [TestCase(long.MaxValue, Timestamp)]
        [TestCase(Timestamp, 0L)]
        [TestCase(Timestamp, -1L)]
        [TestCase(Timestamp, long.MaxValue)]
        [TestCase(Timestamp, Timestamp - 1)]
        public void MissingInvalidOrReversedTimesNeverProduceDuration(long started, long finished)
        {
            var record = new AutomationStepRecord { startedAtUtcMs = started, finishedAtUtcMs = finished };
            UPilotStepManagementWindow.GetStepTiming(record, out var start, out var duration, Timestamp + 5000);
            Assert.That(duration.text, Is.EqualTo("未知"));
            if (started <= 0 || started == long.MaxValue) Assert.That(start.text, Is.EqualTo("未知"));
            Assert.That(record.startedAtUtcMs, Is.EqualTo(started));
            Assert.That(record.finishedAtUtcMs, Is.EqualTo(finished));
        }
        [TestCase(0L, "0.000 s")]
        [TestCase(3250L, "3.250 s")]
        [TestCase(60000L, "01:00.000")]
        [TestCase(83456L, "01:23.456")]
        [TestCase(3601234L, "01:00:01.234")]
        [TestCase(90000000L, "25:00:00.000")]
        public void RecordedDurationRetainsMillisecondsAndDoesNotWrapAtOneDay(long elapsed, string expected)
        {
            UPilotStepManagementWindow.GetStepTiming(new AutomationStepRecord
                { startedAtUtcMs = Timestamp, finishedAtUtcMs = Timestamp + elapsed }, out _, out var duration, Timestamp + 5000);
            Assert.That(duration.text, Is.EqualTo(expected));
        }
        [TestCase("Executing")]
        [TestCase("Cleaning")]
        [TestCase("ResourceCleaning")]
        public void ActiveDurationAdvancesWithObservationWithoutInventingEnd(string stage)
        {
            var record = new AutomationStepRecord { stage = stage, startedAtUtcMs = Timestamp };
            UPilotStepManagementWindow.GetStepTiming(record, out var start, out var duration, Timestamp + 5000);
            Assert.That(start.text, Is.Not.EqualTo("未知"));
            Assert.That(duration.text, Is.EqualTo("5.000 s"));
            Assert.That(duration.tooltip, Does.Contain("结束：未知"));
            Assert.That(duration.tooltip, Does.Contain("耗时（进行中）"));
            UPilotStepManagementWindow.GetStepTiming(record, out _, out var next, Timestamp + 6000);
            Assert.That(next.text, Is.EqualTo("6.000 s"));
            Assert.That(duration.text, Is.EqualTo("5.000 s"), "A previous GUI snapshot must stay consistent.");
            Assert.That(record.startedAtUtcMs, Is.EqualTo(Timestamp));
            Assert.That(record.finishedAtUtcMs, Is.Zero);
        }
        [Test] public void CompletedMissingEndRemainsUnknown()
        {
            UPilotStepManagementWindow.GetStepTiming(new AutomationStepRecord
                { stage = "Completed", startedAtUtcMs = Timestamp }, out _, out var duration, Timestamp + 5000);
            Assert.That(duration.text, Is.EqualTo("未知"));
            Assert.That(duration.tooltip, Does.Not.Contain("进行中"));
        }
        [TestCase(0L)]
        [TestCase(-1L)]
        [TestCase(long.MaxValue)]
        public void ActiveStepWithoutValidStartRemainsUnknown(long started)
        {
            UPilotStepManagementWindow.GetStepTiming(new AutomationStepRecord
                { stage = "Executing", startedAtUtcMs = started }, out var start, out var duration, Timestamp + 5000);
            Assert.That(start.text, Is.EqualTo("未知")); Assert.That(duration.text, Is.EqualTo("未知"));
        }
        [TestCase(0L)]
        [TestCase(-1L)]
        [TestCase(long.MaxValue)]
        [TestCase(Timestamp - 1)]
        public void ActiveStepWithInvalidOrEarlierObservationRemainsUnknown(long now)
        {
            UPilotStepManagementWindow.GetStepTiming(new AutomationStepRecord
                { stage = "Executing", startedAtUtcMs = Timestamp }, out _, out var duration, now);
            Assert.That(duration.text, Is.EqualTo("未知"));
        }
        [TestCase(-1L)]
        [TestCase(long.MaxValue)]
        [TestCase(Timestamp - 1)]
        public void ActiveStepWithMalformedEndDoesNotHideItUsingCurrentTime(long end)
        {
            UPilotStepManagementWindow.GetStepTiming(new AutomationStepRecord
                { stage = "Executing", startedAtUtcMs = Timestamp, finishedAtUtcMs = end }, out _, out var duration, Timestamp + 5000);
            Assert.That(duration.text, Is.EqualTo("未知"));
        }
        [Test] public void RecordedEndFreezesDurationDespiteLaterObservations()
        {
            var record = new AutomationStepRecord { stage = "Completed", startedAtUtcMs = Timestamp, finishedAtUtcMs = Timestamp + 1234 };
            UPilotStepManagementWindow.GetStepTiming(record, out _, out var first, Timestamp + 5000);
            UPilotStepManagementWindow.GetStepTiming(record, out _, out var later, Timestamp + 15000);
            Assert.That(first.text, Is.EqualTo("1.234 s")); Assert.That(later.text, Is.EqualTo(first.text));
            Assert.That(later.tooltip, Does.Not.Contain("进行中"));
        }
        [Test] public void ImmediateStopPreservesRecordedDurationButUnstartedItemsHaveNoExecutionEnd()
        {
            var record = new AutomationStepRecord { stage = "Completed", cleanupState = "SkippedByImmediateStop",
                outcome = AutomationStepStatus.Canceled, startedAtUtcMs = Timestamp, finishedAtUtcMs = Timestamp + 1234 };
            UPilotStepManagementWindow.GetStepTiming(record, out _, out var stopped, Timestamp + 5000);
            Assert.That(stopped.text, Is.EqualTo("1.234 s"));
            record.startedAtUtcMs = 0; record.outcome = AutomationStepStatus.Skipped;
            UPilotStepManagementWindow.GetStepTiming(record, out var start, out var skipped, Timestamp + 5000);
            Assert.That(start.text, Is.EqualTo("未知")); Assert.That(skipped.text, Is.EqualTo("未知"));
            Assert.That(skipped.tooltip, Does.Contain("结束：未知"));
            Assert.That(record.finishedAtUtcMs, Is.EqualTo(Timestamp + 1234));
        }
        [Test] public void LocalTimeTooltipIncludesFullDateOffsetAndEndAcrossMidnight()
        {
            var began = new DateTimeOffset(2026, 10, 10, 23, 59, 59, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 10)));
            var ended = began.AddSeconds(2);
            UPilotStepManagementWindow.GetStepTiming(new AutomationStepRecord
                { startedAtUtcMs = began.ToUnixTimeMilliseconds(), finishedAtUtcMs = ended.ToUnixTimeMilliseconds() },
                out var start, out var duration, Timestamp + 5000);
            Assert.That(start.text, Is.EqualTo(began.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)));
            Assert.That(start.tooltip, Does.Contain("开始：" + began.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)));
            Assert.That(start.tooltip, Does.Contain("结束：" + ended.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)));
            Assert.That(duration.tooltip, Is.EqualTo(start.tooltip)); Assert.That(duration.text, Is.EqualTo("2.000 s"));
        }
        [Test] public void TimingUsesInvariantFormattingAndNullRecordIsUnknown()
        {
            var before = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                UPilotStepManagementWindow.GetStepTiming(new AutomationStepRecord
                    { startedAtUtcMs = Timestamp, finishedAtUtcMs = Timestamp + 3250 }, out _, out var duration, Timestamp + 5000);
                Assert.That(duration.text, Is.EqualTo("3.250 s"));
                UPilotStepManagementWindow.GetStepTiming(null, out var start, out duration, Timestamp + 5000);
                Assert.That(start.text, Is.EqualTo("未知")); Assert.That(duration.text, Is.EqualTo("未知"));
            }
            finally { CultureInfo.CurrentCulture = before; }
        }
    }
}
