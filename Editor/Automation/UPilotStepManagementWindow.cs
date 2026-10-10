using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    /// <summary>Current-run memory monitor. Sampling never advances execution.</summary>
    public sealed class UPilotStepManagementWindow : EditorWindow
    {
        internal const double RefreshIntervalSeconds = 1;
        private const float StartTimeColumnWidth = 110, DurationColumnWidth = 100;
        internal Func<UPilotAutomationStepService.CurrentSnapshot> ReadCurrent = UPilotAutomationStepService.GetCurrentSnapshot;
        internal UPilotAutomationStepService.CurrentSnapshot Latest { get; private set; }
        internal Vector2 Scroll;
        private UPilotAutomationStepService.CurrentSnapshot _gui;
        private string _observedKey = "", _notice = "", _guiNotice = "";
        private bool _guiInitialized;
        private double _nextRefreshAt;
        private long _guiNow;

        [MenuItem("UPilot/Step管理", false, 401)]
        public static void Open()
        {
            var window = GetWindow<UPilotStepManagementWindow>("Step管理");
            window.minSize = new Vector2(820, 360);
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Step管理");
            minSize = new Vector2(820, 360);
            EditorApplication.update -= SampleOnUpdate;
            EditorApplication.update += SampleOnUpdate;
            Refresh(EditorApplication.timeSinceStartup);
        }
        private void OnDisable()
        {
            EditorApplication.update -= SampleOnUpdate;
            Latest = null;
            _gui = null;
            _observedKey = _notice = _guiNotice = "";
            _guiInitialized = false;
            Scroll = Vector2.zero;
            // Never stop a task or detach the executor's framework resource release.
        }
        private void SampleOnUpdate() => RefreshIfDue(EditorApplication.timeSinceStartup);
        internal void RefreshIfDue(double now)
        {
            if (now >= _nextRefreshAt) Refresh(now);
        }
        internal void Refresh(double now)
        {
            _nextRefreshAt = now + RefreshIntervalSeconds; // No catch-up burst after a blocked main thread.
            try { AcceptSnapshot(ReadCurrent()); }
            catch (Exception ex)
            {
                AcceptSnapshot(new UPilotAutomationStepService.CurrentSnapshot { error = ex.Message });
            }
            Repaint();
        }
        private static string Key(AutomationStepRun run) => run == null ? ""
            : run.serviceLifecycleId + ":" + run.operationId + ":" + run.runId;
        internal void AcceptSnapshot(UPilotAutomationStepService.CurrentSnapshot snapshot)
        {
            Latest = snapshot;
            if (snapshot?.initialized != true) return;
            var run = snapshot.run;
            string key = Key(run);
            if (snapshot.busy && run != null)
            {
                if (key != _observedKey)
                {
                    Scroll = Vector2.zero;
                    _notice = "";
                    _observedKey = key;
                }
            }
            else if (run?.terminal == true && key == _observedKey && key != "")
            {
                _notice = "刚观察到的任务已结束：" + run.status;
                if (run.immediateStopRequestedAtUtcMs > 0) _notice += "（立即停止，未恢复业务环境）";
                if (!string.IsNullOrEmpty(run.resourceRecoveryError)) _notice += "\n资源释放未验证：" + run.resourceRecoveryError;
                _observedKey = "";
                Scroll = Vector2.zero;
            }
        }
        internal static bool CanStop(UPilotAutomationStepService.CurrentSnapshot snapshot) =>
            snapshot?.initialized == true && snapshot.busy && snapshot.run != null && !snapshot.run.terminal;
        internal static string StepState(AutomationStepRecord record)
        {
            if (record.cleanupState == "SkippedByImmediateStop")
                return record.startedAtUtcMs > 0 ? "已停止（清理未执行）" : "未执行";
            if (record.stage == "Pending") return "未开始";
            if (record.stage == "Executing") return "执行中／等待 Poll";
            if (record.stage == "Cleaning") return "业务清理中";
            if (record.stage == "ResourceCleaning") return "框架资源收尾中";
            if (record.stage != "Completed") return record.stage;
            return record.outcome switch
            {
                AutomationStepStatus.Succeeded => "已成功",
                AutomationStepStatus.SucceededWithWarnings => "已成功（有警告）",
                AutomationStepStatus.Skipped => "已跳过",
                AutomationStepStatus.Failed => "已失败",
                AutomationStepStatus.Canceled => "已停止",
                AutomationStepStatus.TimedOut => "已超时",
                _ => "结果未知",
            };
        }

        // Completed durations use recorded timestamps; active durations use the same GUI observation time.
        internal static void GetStepTiming(AutomationStepRecord record, out GUIContent start, out GUIContent duration, long observedAtUtcMs)
        {
            long startMs = record?.startedAtUtcMs ?? 0;
            long endMs = record?.finishedAtUtcMs ?? 0;
            bool startKnown = TryTimestamp(startMs, out var startAt);
            // Never present a skipped/unstarted item's terminal marker as an execution end.
            bool endKnown = TryTimestamp(endMs, out var endAt) && startMs > 0;
            bool running = startKnown && endMs == 0
                && (record.stage == "Executing" || record.stage == "Cleaning" || record.stage == "ResourceCleaning")
                && TryTimestamp(observedAtUtcMs, out _) && observedAtUtcMs >= startMs;
            long durationEnd = running ? observedAtUtcMs : endMs;
            string elapsed = "未知";
            if (startKnown && (endKnown || running) && durationEnd >= startMs)
            {
                var span = TimeSpan.FromMilliseconds(durationEnd - startMs);
                elapsed = span.TotalSeconds < 60
                    ? span.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s"
                    : span.TotalHours < 1
                        ? span.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture)
                        : ((long)span.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":"
                            + span.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
            }
            string tooltip = "开始：" + (startKnown ? FullTimestamp(startAt) : "未知")
                + "\n结束：" + (endKnown ? FullTimestamp(endAt) : "未知")
                + (running ? "\n耗时（进行中）：" : "\n耗时：") + elapsed
                + "\n时间为本地时间；结束为完成／被停止的记录时间。"
                + "\n耗时包含执行、等待 Poll、业务清理及该 Step 的资源收尾，不包含任务级报告收尾。"
                + "\n执行／清理中按当前采样时间计算耗时，结束时间仍未确定；已结束项不以当前时间补算。";
            start = new GUIContent(startKnown ? startAt.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) : "未知", tooltip);
            duration = new GUIContent(elapsed, tooltip);
        }
        private static bool TryTimestamp(long milliseconds, out DateTimeOffset timestamp)
        {
            timestamp = default;
            if (milliseconds <= 0) return false;
            try { timestamp = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); return true; }
            catch (ArgumentOutOfRangeException) { return false; }
        }
        private static string FullTimestamp(DateTimeOffset timestamp) =>
            timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);

        private void OnGUI()
        {
            if (!_guiInitialized || Event.current.type == EventType.Layout)
            {
                _gui = Latest;
                _guiNotice = _notice;
                _guiNow = AutomationStepJson.Now;
                _guiInitialized = true;
            }
            var snapshot = _gui;
            var run = snapshot?.initialized == true && snapshot.busy ? snapshot.run : null;
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Step管理", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("刷新", EditorStyles.toolbarButton)) Refresh(EditorApplication.timeSinceStartup);
            using (new EditorGUI.DisabledScope(!CanStop(snapshot)))
                if (GUILayout.Button("立即停止整个任务", EditorStyles.toolbarButton)) ConfirmStop(run);
            EditorGUILayout.EndHorizontal();

            if (snapshot == null || !snapshot.initialized)
            {
                bool initializing = snapshot?.error == "STEP_SERVICE_INITIALIZING";
                EditorGUILayout.HelpBox(initializing ? "Step 服务初始化中，暂不能确定执行器状态。"
                    : "执行器状态未知：" + (snapshot?.error ?? "尚未读取"), MessageType.Warning);
            }
            else if (run == null)
            {
                EditorGUILayout.HelpBox(snapshot.busy ? "执行器正在结束上一轮调用，暂不接受新任务。"
                    : "当前无正在执行的 Step 任务。", MessageType.Info);
                if (!string.IsNullOrEmpty(_guiNotice)) EditorGUILayout.HelpBox(_guiNotice, MessageType.Info);
            }
            else
            {
                string state = run.immediateStopRequestedAtUtcMs > 0
                    ? "测试已立即停止，正在释放框架资源；不执行 Finally，暂不接受新任务。"
                    : run.stage == "Finalizing" ? "测试步骤已处理，证据／报告收尾中，执行器仍被占用。"
                    : "Step 执行器正在执行测试任务。";
                EditorGUILayout.HelpBox(state, MessageType.Info);
                EditorGUILayout.LabelField("提交策略", "执行器占用时拒绝新任务，不进入排队");
                DrawIdentity("Run ID", run.runId);
                DrawIdentity("Operation ID", run.operationId);
                int total = run.plan?.steps?.Length ?? 0;
                int processed = Math.Min(run.cursor, total);
                string current = run.terminal || processed >= total ? "无业务 Step 正在执行" : $"{processed + 1} / {total}";
                EditorGUILayout.LabelField("当前 Step", current);
                long end = run.terminal ? run.finishedAtUtcMs : _guiNow;
                var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, end - run.startedAtUtcMs));
                EditorGUILayout.LabelField($"已处理：{processed} / {total}（非通过率）", $"已运行：{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}");
                Rect progress = GUILayoutUtility.GetRect(0, 18, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(progress, total == 0 ? 0 : (float)processed / total, $"已处理 {processed} / {total}");
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("序号", GUILayout.Width(38));
            GUILayout.Label("Step / Instance", GUILayout.ExpandWidth(true));
            GUILayout.Label("阶段", GUILayout.Width(70));
            GUILayout.Label("状态", GUILayout.Width(160));
            GUILayout.Label("开始时间", GUILayout.Width(StartTimeColumnWidth));
            GUILayout.Label("耗时", GUILayout.Width(DurationColumnWidth));
            EditorGUILayout.EndHorizontal();
            Scroll = EditorGUILayout.BeginScrollView(Scroll, GUILayout.ExpandHeight(true));
            if (run != null)
            {
                for (int i = 0; i < run.steps.Count; i++)
                {
                    var record = run.steps[i];
                    var item = run.plan.steps[i];
                    Color before = GUI.backgroundColor;
                    if (!run.terminal && i == run.cursor) GUI.backgroundColor = new Color(0.45f, 0.75f, 1f);
                    EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                    GUI.backgroundColor = before;
                    GUILayout.Label((i + 1).ToString(), GUILayout.Width(38));
                    EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));
                    GUILayout.Label(item.stepId, EditorStyles.boldLabel);
                    GUILayout.Label(record.instanceId, EditorStyles.miniLabel);
                    EditorGUILayout.EndVertical();
                    GUILayout.Label(item.phase, GUILayout.Width(70));
                    GUILayout.Label(StepState(record), GUILayout.Width(160));
                    GetStepTiming(record, out var start, out var duration, _guiNow);
                    GUILayout.Label(start, GUILayout.Width(StartTimeColumnWidth));
                    GUILayout.Label(duration, GUILayout.Width(DurationColumnWidth));
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.HelpBox("每 1 秒刷新 · 忙时拒绝新任务、不排队\n立即停止不执行后续业务清理或 Finally，不能回滚副作用或强行中断同步代码。", MessageType.None);
        }
        private static void DrawIdentity(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(100));
            EditorGUILayout.SelectableLabel(value ?? "", EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();
        }
        private void ConfirmStop(AutomationStepRun run)
        {
            if (run == null) return;
            if (!EditorUtility.DisplayDialog("立即停止整个 Step 任务",
                "立即停止当前整个测试任务。停止后不再调用 Step 的 Cancel、Cleanup、Poll，也不再执行后续 Step 或 Finally。\n\n"
                + "已经发生的业务副作用不会回滚，PlayMode、场景和业务状态可能保留。框架将另行释放自己拥有的 Capture／Snapshot 资源。\n\n"
                + "不能强行中断已经进入的同步 Unity 代码或保证终止外部异步工作。",
                "立即停止", "返回")) return;
            try
            {
                // Use the confirmed identity, not whichever run appeared while the dialog was open.
                var result = UPilotAutomationStepService.StopCurrentImmediately(run.runId, run.operationId, run.serviceLifecycleId);
                if (!result.changed) _notice = "任务已结束、正在结束或身份已变化；未停止其他任务。";
            }
            catch (Exception ex) { _notice = "停止结果未确认，请刷新查看原任务：" + ex.Message; }
            Refresh(EditorApplication.timeSinceStartup);
        }
    }
}
