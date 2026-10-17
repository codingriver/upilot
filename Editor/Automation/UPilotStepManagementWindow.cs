using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    /// <summary>Current-run memory monitor. Sampling never advances execution.</summary>
    public sealed class UPilotStepManagementWindow : EditorWindow
    {
        internal static double RefreshIntervalSeconds => UPilotTimingSettings.StepWindowRefreshMs / 1000d;
        private const float IndexColumnWidth = 32, PhaseColumnWidth = 60, StateColumnWidth = 140;
        private const float StartTimeColumnWidth = 96, DurationColumnWidth = 84;
        internal Func<UPilotAutomationStepService.CurrentSnapshot> ReadCurrent = UPilotAutomationStepService.GetCurrentSnapshot;
        // Instance-local seams let isolated GUI tests use frozen snapshots without touching the executor.
        internal Action<string, string, Rect> ObserveGui;
        internal Func<bool> ConfirmImmediateStop = ConfirmImmediateStopDialog;
        internal Func<string, string, string, UPilotAutomationStepService.StopReply> StopImmediately =
            UPilotAutomationStepService.StopCurrentImmediately;
        private Vector2 _guiOrigin;
        internal UPilotAutomationStepService.CurrentSnapshot Latest { get; private set; }
        internal Vector2 Scroll;
        private UPilotAutomationStepService.CurrentSnapshot _gui;
        private string _observedKey = "", _notice = "", _guiNotice = "";
        private bool _guiInitialized;
        private double _lastRefreshAt;
        private long _guiNow;
        private bool _taskDetails, _stepDetails = true;
        internal string SelectedKey { get; private set; } = "";
        private string _selectionRunKey = "";
        internal static string Recorded(string value) => string.IsNullOrEmpty(value) ? "未记录" : value;
        internal static AutomationStepRun VisibleRun(UPilotAutomationStepService.CurrentSnapshot snapshot) =>
            snapshot?.initialized == true && string.IsNullOrEmpty(snapshot.error) ? snapshot.run : null;
        internal void SelectStep(AutomationStepRun run, string instanceId) => SelectedKey = Key(run) + ":" + instanceId;

        [MenuItem("UPilot/Step管理", false, 401)]
        public static void Open()
        {
            bool alreadyOpen = HasOpenInstances<UPilotStepManagementWindow>();
            var window = GetWindow<UPilotStepManagementWindow>("Step管理");
            if (!alreadyOpen)
                window.position = new Rect(window.position.position, new Vector2(680, 440));
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Step管理");
            minSize = new Vector2(640, 360);
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
            SelectedKey = _selectionRunKey = "";
            Scroll = Vector2.zero;
            // Never stop a task or detach the executor's framework resource release.
        }
        private void SampleOnUpdate() => RefreshIfDue(EditorApplication.timeSinceStartup);
        internal void RefreshIfDue(double now)
        {
            if (now >= _lastRefreshAt + RefreshIntervalSeconds) Refresh(now);
        }
        internal void Refresh(double now)
        {
            _lastRefreshAt = now; // No catch-up burst after a blocked main thread.
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
            string selectionRun = Key(VisibleRun(snapshot));
            if (selectionRun != _selectionRunKey)
            {
                SelectedKey = "";
                _selectionRunKey = selectionRun;
            }
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
            if (ObserveGui != null && Event.current.type == EventType.Repaint)
            {
                _guiOrigin = GUIUtility.GUIToScreenPoint(Vector2.zero);
                ObserveGui(null, null, default);
            }
            if (!_guiInitialized || Event.current.type == EventType.Layout)
            {
                _gui = Latest;
                _guiNotice = _notice;
                _guiNow = AutomationStepJson.Now;
                _guiInitialized = true;
            }
            var snapshot = _gui;
            var run = VisibleRun(snapshot);
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Step管理", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("刷新", EditorStyles.toolbarButton)) Refresh(EditorApplication.timeSinceStartup);
            Observe("刷新", "刷新");
            using (new EditorGUI.DisabledScope(!CanStop(snapshot)))
            {
                if (GUILayout.Button("立即停止整个任务", EditorStyles.toolbarButton)) ConfirmStop(run);
                Observe("立即停止整个任务", "立即停止整个任务");
            }
            EditorGUILayout.EndHorizontal();

            if (snapshot == null || !snapshot.initialized || !string.IsNullOrEmpty(snapshot.error))
            {
                bool initializing = snapshot?.error == "STEP_SERVICE_INITIALIZING";
                DrawNotice(initializing ? "Step 服务初始化中，暂不能确定执行器状态。"
                    : "执行器状态未知：" + (snapshot?.error ?? "尚未读取"), MessageType.Warning);
            }
            else if (run == null)
            {
                DrawNotice(snapshot.busy ? "执行器正在结束上一轮调用，暂不接受新任务。"
                    : "当前无正在执行的 Step 任务。", MessageType.Info);
                if (!string.IsNullOrEmpty(_guiNotice)) EditorGUILayout.HelpBox(_guiNotice, MessageType.Info);
            }
            else
            {
                string state = run.terminal && !snapshot.busy ? "最近结束任务：" + run.status
                    : run.immediateStopRequestedAtUtcMs > 0
                    ? "测试已立即停止，正在释放框架资源；不执行 Finally，暂不接受新任务。"
                    : run.stage == "Finalizing" ? "测试步骤已处理，证据／报告收尾中，执行器仍被占用。"
                    : "Step 执行器正在执行测试任务。";
                DrawNotice(state, MessageType.Info);
                EditorGUILayout.LabelField("提交策略", "执行器占用时拒绝新任务，不进入排队");
                DrawIdentity("Run ID", run.runId);
                DrawIdentity("Operation ID", run.operationId);
                int total = run.plan?.steps?.Length ?? 0;
                int processed = Math.Min(run.cursor, total);
                string current = run.terminal || processed >= total ? "无业务 Step 正在执行" : $"{processed + 1} / {total}";
                EditorGUILayout.LabelField("当前 Step", current);
                EditorGUILayout.LabelField($"已处理：{processed} / {total}（非通过率）", "已运行：" + RunDuration(run, _guiNow));
                Rect progress = GUILayoutUtility.GetRect(0, 18, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(progress, total == 0 ? 0 : (float)processed / total, $"已处理 {processed} / {total}");
                _taskDetails = EditorGUILayout.Foldout(_taskDetails, "任务详情", true);
                Observe("任务详情", "任务详情");
                if (_taskDetails) DrawTaskDetails(run);
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("序号", GUILayout.Width(IndexColumnWidth));
            GUILayout.Label("Step / Instance", GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
            GUILayout.Label("阶段", GUILayout.Width(PhaseColumnWidth));
            GUILayout.Label("状态", GUILayout.Width(StateColumnWidth));
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
                    GUILayout.Label((i + 1).ToString(), GUILayout.Width(IndexColumnWidth));
                    EditorGUILayout.BeginVertical(GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
                    DrawStepIdentity(item.stepId, EditorStyles.boldLabel);
                    if (GUILayout.Button(new GUIContent(record.instanceId, record.instanceId), EditorStyles.miniButton))
                        SelectStep(run, record.instanceId);
                    Observe("instance:" + record.instanceId, record.instanceId);
                    EditorGUILayout.EndVertical();
                    GUILayout.Label(new GUIContent(item.phase, item.phase), GUILayout.Width(PhaseColumnWidth));
                    string state = StepState(record);
                    GUILayout.Label(new GUIContent(state, state), GUILayout.Width(StateColumnWidth));
                    GetStepTiming(record, out var start, out var duration, _guiNow);
                    GUILayout.Label(start, GUILayout.Width(StartTimeColumnWidth));
                    GUILayout.Label(duration, GUILayout.Width(DurationColumnWidth));
                    EditorGUILayout.EndHorizontal();
                    if (SelectedKey == Key(run) + ":" + record.instanceId)
                    {
                        _stepDetails = EditorGUILayout.Foldout(_stepDetails, "选中 Step 详情", true);
                        Observe("选中 Step 详情", "选中 Step 详情");
                        if (_stepDetails) DrawStepDetails(item, record);
                    }
                }
            }
            EditorGUILayout.EndScrollView();
            string refreshNotice = $"每 {UPilotTimingSettings.StepWindowRefreshMs} 毫秒刷新 · 忙时拒绝新任务、不排队\n立即停止不执行后续业务清理或 Finally，不能回滚副作用或强行中断同步代码。";
            EditorGUILayout.HelpBox(refreshNotice, MessageType.None);
            Observe("刷新说明", refreshNotice);
        }
        private void DrawError(string label, AutomationStepError error) =>
            DrawValue(label, error == null ? "未记录" : error.code + "\n" + error.message + "\n" + error.diagnostic);
        private void DrawValue(string label, string value)
        {
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            string text = Recorded(value);
            EditorGUILayout.SelectableLabel(text, EditorStyles.textArea,
                GUILayout.MinHeight(EditorGUIUtility.singleLineHeight * 2));
            Observe(label, text);
        }
        private void DrawTaskDetails(AutomationStepRun run)
        {
            EditorGUILayout.LabelField("状态／阶段", run.status + " / " + run.stage);
            EditorGUILayout.LabelField("终态／等待清理", run.terminal + " / " + run.cleanupPending);
            DrawError("首错", run.error);
            foreach (var error in run.secondaryErrors) DrawError("次错", error);
            DrawIdentity("清理结果", Recorded(run.cleanupOutcome));
            DrawIdentity("报告结果", Recorded(run.reportOutcome));
            DrawValue("未确认资源", ResourceText(run.unresolvedResources));
            DrawValue("资源错误", run.resourceRecoveryError);
            foreach (var artifact in run.artifacts ?? Array.Empty<AutomationReportArtifact>()) DrawArtifactPath(artifact.path);
            foreach (var artifact in run.registeredArtifacts) DrawArtifactPath(artifact.path);
        }
        private void DrawArtifactPath(string path)
        {
            EditorGUILayout.BeginHorizontal();
            DrawIdentity("附件路径", path);
            if (GUILayout.Button("复制", GUILayout.Width(45))) EditorGUIUtility.systemCopyBuffer = path ?? "";
            Observe("复制:" + path, "复制");
            EditorGUILayout.EndHorizontal();
        }
        private static string TimestampText(long value) => TryTimestamp(value, out var at) ? FullTimestamp(at) : "未知";
        private void DrawStepDetails(AutomationStepItem item, AutomationStepRecord record)
        {
            DrawValue("原始参数", ArgumentsText(item.arguments));
            DrawError("步骤错误", record.error);
            DrawIdentity("Cleanup", Recorded(record.cleanupState));
            DrawValue("Cleanup 错误", record.cleanupRecoveryError);
            DrawIdentity("执行开始", TimestampText(record.startedAtUtcMs));
            DrawIdentity("清理开始", TimestampText(record.cleanupStartedAtUtcMs));
            DrawIdentity("完成时间", TimestampText(record.finishedAtUtcMs));
            DrawValue("运行计划时间值（秒）", "执行=" + item.timeoutSeconds.ToString(CultureInfo.InvariantCulture)
                + "；Cleanup=" + item.cleanupTimeoutSeconds.ToString(CultureInfo.InvariantCulture)
                + "；轮询=" + item.pollIntervalSeconds.ToString(CultureInfo.InvariantCulture) + "\n来源未记录");
        }
        internal static string ArgumentsText(string value) => value == null ? "未记录" : value.Length == 0 ? "\"\"（空字符串）" : value;
        internal static string ResourceText(string[] values) => values == null ? "未记录" : values.Length == 0 ? "0 项" : string.Join("\n", values);
        internal static string RunDuration(AutomationStepRun run, long observedAt)
        {
            long end = run.terminal ? run.finishedAtUtcMs : observedAt;
            if (!TryTimestamp(run.startedAtUtcMs, out _) || !TryTimestamp(end, out _) || end < run.startedAtUtcMs) return "未知";
            var elapsed = TimeSpan.FromMilliseconds(end - run.startedAtUtcMs);
            return ((long)elapsed.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":" + elapsed.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
        }
        private static void DrawStepIdentity(string value, GUIStyle style)
        {
            Rect rect = GUILayoutUtility.GetRect(0, EditorGUIUtility.singleLineHeight, GUILayout.ExpandWidth(true));
            string full = value ?? "";
            var content = new GUIContent(full, full);
            if (style.CalcSize(content).x > rect.width)
            {
                int low = 0, high = full.Length;
                while (low < high)
                {
                    int length = (low + high + 1) / 2;
                    content.text = full.Substring(0, length) + "…";
                    if (style.CalcSize(content).x <= rect.width) low = length;
                    else high = length - 1;
                }
                if (low > 0 && char.IsHighSurrogate(full[low - 1])) low--;
                content.text = full.Substring(0, low) + "…";
            }
            GUI.Label(rect, content, style);
        }
        private void DrawIdentity(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(100));
            EditorGUILayout.SelectableLabel(value ?? "", EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            Observe(label, value ?? "");
            EditorGUILayout.EndHorizontal();
        }
        private void Observe(string label, string value)
        {
            if (ObserveGui == null || Event.current.type != EventType.Repaint) return;
            Rect rect = GUILayoutUtility.GetLastRect();
            rect.position = GUIUtility.GUIToScreenPoint(rect.position) - _guiOrigin;
            ObserveGui(label, value, rect);
        }
        private void DrawNotice(string value, MessageType type)
        {
            EditorGUILayout.HelpBox(value, type);
            Observe("状态提示", value);
        }
        private static bool ConfirmImmediateStopDialog() => EditorUtility.DisplayDialog("立即停止整个 Step 任务",
                "立即停止当前整个测试任务。停止后不再调用 Step 的 Cancel、Cleanup、Poll，也不再执行后续 Step 或 Finally。\n\n"
                + "已经发生的业务副作用不会回滚，PlayMode、场景和业务状态可能保留。框架将另行释放自己拥有的 Capture／Snapshot 资源。\n\n"
                + "不能强行中断已经进入的同步 Unity 代码或保证终止外部异步工作。",
                "立即停止", "返回");
        private void ConfirmStop(AutomationStepRun run)
        {
            if (run == null) return;
            // Freeze strings before confirmation: even a mutable snapshot cannot redirect the action.
            string runId = run.runId, operationId = run.operationId, lifecycleId = run.serviceLifecycleId;
            if (!ConfirmImmediateStop()) return;
            try
            {
                // Use the confirmed identity, not whichever run appeared while the dialog was open.
                var result = StopImmediately(runId, operationId, lifecycleId);
                if (!result.changed) _notice = "任务已结束、正在结束或身份已变化；未停止其他任务。";
            }
            catch (Exception ex) { _notice = "停止结果未确认，请刷新查看原任务：" + ex.Message; }
            Refresh(EditorApplication.timeSinceStartup);
        }
    }
}
