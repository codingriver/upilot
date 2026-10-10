using System;
using System.Globalization;
using System.Reflection;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Tests.Automation
{
    public sealed class UPilotStepManagementWindowTests
    {
        private UPilotStepManagementWindow _window;
        [SetUp] public void SetUp() => _window = ScriptableObject.CreateInstance<UPilotStepManagementWindow>();
        [TearDown] public void TearDown() { if (_window != null) UnityEngine.Object.DestroyImmediate(_window); }
        private static UPilotAutomationStepService.CurrentSnapshot Running(string id) => new()
        {
            initialized = true, busy = true,
            run = new AutomationStepRun { runId = id, operationId = "operation", serviceLifecycleId = "lifetime" }
        };
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
