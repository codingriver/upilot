// -----------------------------------------------------------------------
// UPilot Editor - manual MonoHook point management window.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public sealed partial class UPilotMonoHookWindow : EditorWindow
    {
        private const string ScrollXSessionKey = "UPilot.MonoHook.Tracing.Window.ScrollX";
        private const string ScrollYSessionKey = "UPilot.MonoHook.Tracing.Window.ScrollY";
        private const string ConfigurationScrollXSessionKey = "UPilot.MonoHook.Tracing.Window.ConfigurationScrollX";
        private const string ConfigurationScrollYSessionKey = "UPilot.MonoHook.Tracing.Window.ConfigurationScrollY";
        private const string SelectedTabSessionKey = "UPilot.MonoHook.Tracing.Window.SelectedTab";
        private const float FooterHeight = 20f;
        private static readonly string[] TabLabels = { "追踪点位", "配置与日志" };

        private enum WindowTab
        {
            TracePoints,
            Configuration,
        }

        private UPilotMonoHookController _controller;
        private Vector2 _scroll;
        private Vector2 _configurationScroll;
        private WindowTab _selectedTab;
        private string _status = "未应用";
        private bool _showEvents = true;
        private bool _showFilterProfiles;
        private bool _showPointAdvancedSettings;
        private string _eventFilter = string.Empty;

        [MenuItem("UPilot/Advanced/追踪器", false, 215)]
        public static void ShowWindow()
        {
            var window = GetWindow<UPilotMonoHookWindow>("UPilot 追踪器");
            window.minSize = new Vector2(520f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            _controller = new UPilotMonoHookController();
            _scroll = new Vector2(
                SessionState.GetFloat(ScrollXSessionKey, 0f),
                SessionState.GetFloat(ScrollYSessionKey, 0f));
            _configurationScroll = new Vector2(
                SessionState.GetFloat(ConfigurationScrollXSessionKey, 0f),
                SessionState.GetFloat(ConfigurationScrollYSessionKey, 0f));
            _selectedTab = (WindowTab)Mathf.Clamp(
                SessionState.GetInt(SelectedTabSessionKey, (int)WindowTab.TracePoints),
                (int)WindowTab.TracePoints,
                (int)WindowTab.Configuration);
        }

        private void OnDisable()
        {
            SessionState.SetFloat(ScrollXSessionKey, _scroll.x);
            SessionState.SetFloat(ScrollYSessionKey, _scroll.y);
            SessionState.SetFloat(ConfigurationScrollXSessionKey, _configurationScroll.x);
            SessionState.SetFloat(ConfigurationScrollYSessionKey, _configurationScroll.y);
            SessionState.SetInt(SelectedTabSessionKey, (int)_selectedTab);
        }

        private void OnGUI()
        {
            if (_controller == null)
                _controller = new UPilotMonoHookController();

            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();

            float viewHeight = GetUsableViewHeight();
            GUILayout.BeginArea(new Rect(
                0f,
                0f,
                position.width,
                Mathf.Max(0f, viewHeight - FooterHeight)));
            DrawTabToolbar();
            switch (_selectedTab)
            {
                case WindowTab.Configuration:
                    DrawConfigurationTab(settings);
                    break;
                default:
                    DrawTracePointsTab(settings);
                    break;
            }
            GUILayout.EndArea();

            DrawCommonFooter(viewHeight);
        }

        private void DrawTabToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            int selectedTab = GUILayout.Toolbar(
                (int)_selectedTab,
                TabLabels,
                EditorStyles.toolbarButton,
                GUILayout.ExpandWidth(true));
            EditorGUILayout.EndHorizontal();

            if (selectedTab == (int)_selectedTab) return;
            _selectedTab = (WindowTab)selectedTab;
            SessionState.SetInt(SelectedTabSessionKey, selectedTab);
            GUI.FocusControl(null);
        }

        private void DrawTracePointsTab(UPilotMonoHookSettings settings)
        {
            EditorGUILayout.LabelField("追踪点位", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "手动选择需要追踪的点位。修改配置后点击“应用”才会安装或移除对应追踪。内置点位默认仅透传原始调用并记录日志，不改变参数、返回值或调用结果。",
                MessageType.Info);

            EditorGUILayout.BeginVertical("helpBox");
            EditorGUILayout.BeginHorizontal();
            bool master = EditorGUILayout.ToggleLeft("启用追踪器", settings.masterEnabled, GUILayout.Width(105f));
            if (master != settings.masterEnabled)
            {
                settings.masterEnabled = master;
                MarkConfigurationChanged();
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label(new GUIContent(_status, _status), EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("应用配置", GUILayout.Width(130f), GUILayout.Height(30f)))
                ApplyConfiguration();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("全部启用", GUILayout.Width(85f), GUILayout.Height(23f)))
            {
                foreach (var definition in UPilotMonoHookCatalog.All)
                    settings.SetEnabled(definition.Id, true);
                MarkConfigurationChanged();
            }
            if (GUILayout.Button("全部禁用", GUILayout.Width(85f), GUILayout.Height(23f)))
            {
                foreach (var definition in UPilotMonoHookCatalog.All)
                    settings.SetEnabled(definition.Id, false);
                MarkConfigurationChanged();
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("更多 ▾", GUILayout.Width(75f), GUILayout.Height(23f)))
                ShowTracePointActionsMenu(settings);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            _scroll.x = 0f;
            _scroll = EditorGUILayout.BeginScrollView(_scroll, false, true);
            foreach (var group in UPilotMonoHookCatalog.All.GroupBy(definition => definition.CategoryId))
            {
                var first = group.First();
                DrawCategory(settings, first.CategoryId, first.CategoryDisplayName, group);
            }
            EditorGUILayout.EndScrollView();
            _scroll.x = 0f;
        }

        private void DrawConfigurationTab(UPilotMonoHookSettings settings)
        {
            _configurationScroll = EditorGUILayout.BeginScrollView(_configurationScroll);
            EditorGUILayout.LabelField("配置与日志", EditorStyles.boldLabel);
            DrawRuntimeOptions(settings);
            DrawCaptureOptions(settings);
            DrawFilterProfiles(settings);
            DrawPointAdvancedSettings(settings);
            DrawEventLog(settings);
            EditorGUILayout.EndScrollView();
        }

        private float GetUsableViewHeight()
        {
            return Mathf.Max(0f, position.height - EditorGUIUtility.singleLineHeight - 4f);
        }

        private void DrawCommonFooter(float viewHeight)
        {
            string settingsPath = UPilotMonoHookSettings.GetAssetPath();
            float settingsPathWidth = Mathf.Clamp(position.width * 0.42f, 220f, 360f);
            GUILayout.BeginArea(
                new Rect(0f, Mathf.Max(0f, viewHeight - FooterHeight), position.width, FooterHeight),
                EditorStyles.toolbar);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(_status, _status), EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
            GUILayout.Label(
                new GUIContent("配置文件：" + settingsPath, settingsPath),
                EditorStyles.miniLabel,
                GUILayout.Width(settingsPathWidth));
            EditorGUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private static void DrawRuntimeOptions(UPilotMonoHookSettings settings)
        {
            EditorGUILayout.BeginVertical("helpBox");
            EditorGUILayout.LabelField("运行保护", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            settings.autoInjectEnabled = EditorGUILayout.ToggleLeft(
                new GUIContent("自动注入追踪点位（默认关闭）", "统一控制所有自动注入时机；关闭后仍可手动点击“应用”。"),
                settings.autoInjectEnabled);
            EditorGUI.BeginDisabledGroup(!settings.autoInjectEnabled);
            settings.autoApplyOnEditorLoad = EditorGUILayout.ToggleLeft(
                "Domain Reload 后自动注入已保存点位",
                settings.autoApplyOnEditorLoad);
            settings.autoApplyOnPlayMode = EditorGUILayout.ToggleLeft(
                "进入 PlayMode 自动注入已启用点位",
                settings.autoApplyOnPlayMode);
            EditorGUI.EndDisabledGroup();
            settings.suppressUnchangedValues = EditorGUILayout.ToggleLeft(
                "忽略值未发生变化的事件",
                settings.suppressUnchangedValues);
            settings.maxEventsPerSecond = Mathf.Max(1, EditorGUILayout.IntField(
                new GUIContent("每秒最大事件数", "超过上限的事件会被丢弃并计入丢弃数量。"),
                settings.maxEventsPerSecond));
            settings.enablePerObjectRateLimit = EditorGUILayout.ToggleLeft(
                new GUIContent("启用单对象限流", "按点位和对象分别限制事件数量，默认关闭。"),
                settings.enablePerObjectRateLimit);
            if (settings.enablePerObjectRateLimit)
                settings.maxEventsPerObjectPerSecond = Mathf.Clamp(EditorGUILayout.IntField(
                    new GUIContent("单对象每秒最大事件数", "只影响单个对象，不改变全局事件上限。"),
                    settings.maxEventsPerObjectPerSecond), 1, 10000);
            settings.suppressDuplicateEvents = EditorGUILayout.ToggleLeft(
                new GUIContent("抑制重复事件", "相同点位、对象、方法、阶段和值在短窗口内只保留一条，默认关闭。"),
                settings.suppressDuplicateEvents);
            if (settings.suppressDuplicateEvents)
                settings.duplicateEventWindowMilliseconds = Mathf.Clamp(EditorGUILayout.IntField(
                    new GUIContent("重复事件窗口（毫秒）", "仅用于重复事件抑制，不影响值变化抑制。"),
                    settings.duplicateEventWindowMilliseconds), 1, 60000);
            if (EditorGUI.EndChangeCheck())
                settings.SaveSettings();
            EditorGUILayout.EndVertical();
        }

        private static void DrawCaptureOptions(UPilotMonoHookSettings settings)
        {
            EditorGUILayout.BeginVertical("helpBox");
            EditorGUILayout.LabelField("追踪采集", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            var mode = settings.stackTraceCaptureMode;
            if (EditorGUILayout.ToggleLeft("不采集堆栈", mode == UPilotStackTraceCaptureMode.Disabled))
                mode = UPilotStackTraceCaptureMode.Disabled;
            if (EditorGUILayout.ToggleLeft("仅指定点位", mode == UPilotStackTraceCaptureMode.SelectedPoints))
                mode = UPilotStackTraceCaptureMode.SelectedPoints;
            if (EditorGUILayout.ToggleLeft("所有已启用点位", mode == UPilotStackTraceCaptureMode.AllEnabledPoints))
                mode = UPilotStackTraceCaptureMode.AllEnabledPoints;
            settings.stackTraceCaptureMode = mode;

            if (mode == UPilotStackTraceCaptureMode.SelectedPoints)
            {
                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField("采集堆栈的点位", EditorStyles.miniBoldLabel);
                foreach (var group in UPilotMonoHookCatalog.All.GroupBy(definition => definition.CategoryId))
                {
                    EditorGUILayout.LabelField(group.First().CategoryDisplayName, EditorStyles.miniLabel);
                    foreach (var definition in group)
                    {
                        bool selected = settings.ShouldCaptureStackTrace(definition.Id);
                        bool next = EditorGUILayout.ToggleLeft(definition.DisplayName, selected);
                        if (next != selected)
                            settings.SetCaptureStackTrace(definition.Id, next);
                    }
                }
            }

            using (new EditorGUI.DisabledScope(mode == UPilotStackTraceCaptureMode.Disabled))
            {
                settings.stackTraceMaxFrames = Mathf.Max(1, EditorGUILayout.IntField(
                    new GUIContent("堆栈最大帧数", "限制每条追踪事件记录的调用帧数量。"),
                    settings.stackTraceMaxFrames));
                settings.stackTraceSampleEveryN = Mathf.Max(1, EditorGUILayout.IntField(
                    new GUIContent("堆栈采样间隔", "1 表示每条事件采集；N 表示每 N 条采集一次。"),
                    settings.stackTraceSampleEveryN));
            }
            EditorGUILayout.HelpBox("有效过滤器通过后才会采集堆栈、写入事件缓存并输出 Console。默认不采集堆栈。", MessageType.None);
            if (EditorGUI.EndChangeCheck())
                settings.SaveSettings();
            EditorGUILayout.EndVertical();
        }

        private void DrawPointAdvancedSettings(UPilotMonoHookSettings settings)
        {
            EditorGUILayout.BeginVertical("helpBox");
            _showPointAdvancedSettings = EditorGUILayout.Foldout(
                _showPointAdvancedSettings,
                "点位高级配置",
                true);
            if (!_showPointAdvancedSettings)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.HelpBox(
                "仅显示点位自身支持的高级能力。默认使用推荐安全重载并透传原始调用。修改后需点击追踪点位页的“应用配置”更新物理 Hook。",
                MessageType.None);
            foreach (var group in UPilotMonoHookCatalog.All.GroupBy(definition => definition.CategoryId))
            {
                bool hasAdvanced = group.Any(definition =>
                    _controller.Runtime.TryGetValue(definition.Id, out var state) &&
                    (state.SupportsHookAllSafeOverloads || state.SupportsInterception));
                if (!hasAdvanced) continue;

                EditorGUILayout.LabelField(group.First().CategoryDisplayName, EditorStyles.boldLabel);
                foreach (var definition in group)
                {
                    if (!_controller.Runtime.TryGetValue(definition.Id, out var state) ||
                        (!state.SupportsHookAllSafeOverloads && !state.SupportsInterception))
                        continue;

                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUILayout.LabelField(definition.DisplayName, EditorStyles.miniBoldLabel);
                    EditorGUI.BeginChangeCheck();
                    if (state.SupportsHookAllSafeOverloads)
                    {
                        bool hookAll = settings.ShouldHookAllSafeOverloads(definition.Id);
                        hookAll = EditorGUILayout.ToggleLeft(
                            new GUIContent(
                                "安装全部安全重载",
                                "关闭时安装推荐安全重载；开启后安装所有通过安全检查的公开非泛型重载，包装调用可能产生重复事件。"),
                            hookAll);
                        settings.SetHookAllSafeOverloads(definition.Id, hookAll);
                    }
                    if (state.SupportsInterception)
                    {
                        bool intercept = settings.GetExecutionMode(definition.Id) == UPilotMonoHookExecutionMode.Intercept;
                        intercept = EditorGUILayout.ToggleLeft(
                            new GUIContent("拦截原始调用", "默认关闭。关闭时透传原始调用并仅记录追踪日志。"),
                            intercept);
                        settings.SetExecutionMode(
                            definition.Id,
                            intercept ? UPilotMonoHookExecutionMode.Intercept : UPilotMonoHookExecutionMode.PassThrough);
                    }
                    if (EditorGUI.EndChangeCheck())
                    {
                        settings.SaveSettings();
                        _controller.RefreshRuntime();
                        MarkConfigurationChanged();
                    }
                    EditorGUILayout.EndVertical();
                }
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawEventLog(UPilotMonoHookSettings settings)
        {
            EditorGUILayout.BeginVertical("helpBox");
            EditorGUILayout.BeginHorizontal();
            _showEvents = EditorGUILayout.Foldout(
                _showEvents,
                $"事件日志（缓存 {UPilotMonoHookTelemetry.Count}，丢弃 {UPilotMonoHookTelemetry.DroppedCount}）",
                true);
            EditorGUI.BeginChangeCheck();
            bool logEventsToConsole = EditorGUILayout.ToggleLeft(
                new GUIContent("输出到 Console", "追踪事件通过过滤和事件限流后，同步输出格式化日志到 Unity Console。"),
                settings.logEventsToConsole,
                GUILayout.Width(115f));
            if (EditorGUI.EndChangeCheck())
            {
                settings.logEventsToConsole = logEventsToConsole;
                settings.SaveSettings();
            }
            EditorGUILayout.EndHorizontal();
            if (!_showEvents)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            if (settings.logEventsToConsole)
            {
                EditorGUI.BeginChangeCheck();
                settings.maxConsoleLogsPerSecond = Mathf.Clamp(EditorGUILayout.IntField(
                    new GUIContent("每秒最大 Console 日志数", "Console 输出独立限流；超过上限只丢弃 Console 日志，不影响事件缓存和导出。"),
                    settings.maxConsoleLogsPerSecond), 1, 200);
                if (EditorGUI.EndChangeCheck())
                    settings.SaveSettings();
                EditorGUILayout.LabelField(
                    $"Console 限流丢弃：{UPilotMonoHookTelemetry.ConsoleDroppedCount}",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.BeginHorizontal();
            _eventFilter = EditorGUILayout.TextField("筛选", _eventFilter);
            if (GUILayout.Button("清空", GUILayout.Width(48f)))
                UPilotMonoHookTelemetry.Clear();
            if (GUILayout.Button("导出", GUILayout.Width(48f)))
            {
                string path = EditorUtility.SaveFilePanel(
                    "导出追踪事件",
                    string.Empty,
                    "UPilotTrace.jsonl",
                    "jsonl");
                if (!string.IsNullOrEmpty(path))
                {
                    int count = UPilotMonoHookTelemetry.ExportJsonLines(path);
                    _status = $"已导出 {count} 条事件：{path}";
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(
                $"低噪声统计：重复丢弃 {UPilotMonoHookTelemetry.DuplicateDroppedCount}；单对象限流丢弃 {UPilotMonoHookTelemetry.PerObjectDroppedCount}；追踪失败 {UPilotMonoHookTelemetry.TraceFailureCount}",
                EditorStyles.miniLabel);

            var events = UPilotMonoHookTelemetry.Snapshot(100);
            foreach (var hookEvent in events)
            {
                string line = $"#{hookEvent.sequence} F{hookEvent.frame} {hookEvent.kind} {hookEvent.hierarchyPath}";
                if (!string.IsNullOrEmpty(hookEvent.methodSignature))
                    line += "  " + hookEvent.methodSignature;
                if (!string.IsNullOrEmpty(hookEvent.beforeValue) || !string.IsNullOrEmpty(hookEvent.afterValue))
                    line += $"  {hookEvent.beforeValue} -> {hookEvent.afterValue}";
                if (!string.IsNullOrEmpty(_eventFilter) &&
                    line.IndexOf(_eventFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                string display = string.IsNullOrEmpty(hookEvent.stackTrace)
                    ? line
                    : line + "\n" + hookEvent.stackTrace;
                float height = string.IsNullOrEmpty(hookEvent.stackTrace)
                    ? EditorGUIUtility.singleLineHeight
                    : EditorGUIUtility.singleLineHeight * 3f;
                EditorGUILayout.SelectableLabel(display, EditorStyles.miniLabel, GUILayout.Height(height));
            }
            EditorGUILayout.EndVertical();
        }

        private void ApplyConfiguration()
        {
            var report = _controller.Apply();
            int actualFailed = Mathf.Max(0, report.Failed.Count - report.Unsupported.Count);
            _status = $"应用完成：启用 {report.Enabled.Count}（部分 {report.Partial.Count}），禁用 {report.Disabled.Count}，不支持 {report.Unsupported.Count}，失败 {actualFailed}";
        }

        private void MarkConfigurationChanged()
        {
            _status = "配置已修改，尚未应用";
        }

        private void ShowTracePointActionsMenu(UPilotMonoHookSettings settings)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("卸载全部"), false, () =>
            {
                _controller.UninstallAll();
                _status = "已卸载全部追踪点位";
                Repaint();
            });
            menu.AddItem(new GUIContent("导出诊断"), false, () =>
            {
                string path = EditorUtility.SaveFilePanel(
                    "导出 UPilot 追踪器诊断",
                    string.Empty,
                    "UPilotTraceDiagnostics.jsonl",
                    "jsonl");
                if (string.IsNullOrEmpty(path)) return;
                int count = _controller.ExportDiagnosticsJsonLines(path);
                _status = $"已导出 {count} 个点位诊断：{path}";
                Repaint();
            });
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("恢复默认配置"), false, () =>
            {
                if (!EditorUtility.DisplayDialog(
                        "恢复 UPilot 追踪器默认配置",
                        "将重置所有追踪点位、全局过滤器、堆栈和日志配置。是否继续？",
                        "恢复默认",
                        "取消"))
                    return;
                settings.ResetToDefaults();
                _controller = new UPilotMonoHookController();
                _status = "已恢复默认配置，尚未应用";
                Repaint();
            });
            menu.ShowAsContext();
        }

        private void DrawCategory(
            UPilotMonoHookSettings settings,
            string categoryId,
            string title,
            IEnumerable<UPilotMonoHookPointDefinition> definitions)
        {
            var items = definitions.ToArray();
            int enabledCount = items.Count(definition => settings.IsConfiguredEnabled(definition.Id));
            EditorGUILayout.BeginVertical("helpBox");
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"已启用 {enabledCount}/{items.Length}", EditorStyles.miniLabel, GUILayout.Width(75f));
            if (GUILayout.Button("全开", GUILayout.Width(45f)))
            {
                settings.SetCategoryEnabled(categoryId, true);
                MarkConfigurationChanged();
            }
            if (GUILayout.Button("全关", GUILayout.Width(45f)))
            {
                settings.SetCategoryEnabled(categoryId, false);
                MarkConfigurationChanged();
            }
            EditorGUILayout.EndHorizontal();

            foreach (var definition in items)
                DrawPointSummaryRow(settings, definition);
            EditorGUILayout.EndVertical();
        }

        private void DrawPointSummaryRow(
            UPilotMonoHookSettings settings,
            UPilotMonoHookPointDefinition definition)
        {
            _controller.Runtime.TryGetValue(definition.Id, out var state);
            bool enabled = settings.IsConfiguredEnabled(definition.Id);
            EditorGUILayout.BeginHorizontal();
            bool next = EditorGUILayout.ToggleLeft(definition.DisplayName, enabled, GUILayout.ExpandWidth(true));
            if (next != enabled)
            {
                settings.SetEnabled(definition.Id, next);
                MarkConfigurationChanged();
            }

            if (definition.HighFrequency)
                GUILayout.Label("高频", EditorStyles.miniLabel, GUILayout.Width(30f));

            if (state != null)
            {
                var previousColor = GUI.color;
                GUI.color = GetStatusColor(state.InstallState);
                if (GUILayout.Button(
                        new GUIContent(GetCompactStatusText(state), BuildStatusTooltip(state)),
                        EditorStyles.miniButton,
                        GUILayout.Width(82f)))
                    ShowInstallationDetailsDialog(definition, state);
                GUI.color = previousColor;
            }
            EditorGUILayout.EndHorizontal();
        }

        internal static string GetCompactStatusText(UPilotMonoHookPointRuntimeState state)
        {
            if (state == null) return "未知";
            switch (state.InstallState)
            {
                case UPilotMonoHookInstallState.Installed:
                    return "已安装";
                case UPilotMonoHookInstallState.PartiallyInstalled:
                    if (state.Coverage == null) return "部分安装";
                    int installed = state.Coverage.InstalledTypeCount > 0
                        ? state.Coverage.InstalledTypeCount
                        : state.Coverage.InstalledCount;
                    return $"部分 {installed}/{state.Coverage.CandidateCount}";
                case UPilotMonoHookInstallState.Unsupported:
                    return "不支持";
                case UPilotMonoHookInstallState.Failed:
                    return "失败";
                default:
                    return state.ConfiguredEnabled ? "未应用" : "未安装";
            }
        }

        internal static void ShowInstallationDetailsDialog(
            UPilotMonoHookPointDefinition definition,
            UPilotMonoHookPointRuntimeState state)
        {
            string title = $"UPilot 追踪器 - {definition?.DisplayName ?? "未知点位"} 安装详情";
            UPilotScrollableDialog.ShowDialog(title, BuildInstallationDetailsText(definition, state));
        }

        internal static string BuildInstallationDetailsText(
            UPilotMonoHookPointDefinition definition,
            UPilotMonoHookPointRuntimeState state)
        {
            var builder = new StringBuilder();
            builder.AppendLine("点位：" + (definition?.DisplayName ?? "未知点位"));
            builder.AppendLine("点位 ID：" + (definition?.Id ?? state?.PointId ?? string.Empty));
            builder.AppendLine("安装状态：" + (state?.InstallState.ToString() ?? UPilotMonoHookInstallState.NotInstalled.ToString()));
            builder.AppendLine("执行策略：" + (state?.AppliedExecutionMode.ToString() ?? UPilotMonoHookExecutionMode.PassThrough.ToString()));
            if (!string.IsNullOrWhiteSpace(state?.Message))
                builder.AppendLine("状态说明：" + state.Message);

            var coverage = state?.Coverage;
            builder.AppendLine();
            if (coverage == null)
            {
                builder.Append("当前没有安装快照。");
                return builder.ToString();
            }

            string coverageSummary = coverage.Entries.Count > 0
                ? $"候选 {coverage.CandidateCount} · 已安装类型 {coverage.InstalledTypeCount} · " +
                  $"目标方法 {coverage.InstalledMethodCount} · trampoline {coverage.TrampolineCount} · " +
                  $"跳过 {coverage.SkippedCount} · 失败 {coverage.FailedCount}"
                : $"候选方法 {coverage.CandidateCount} · 已安装方法 {coverage.InstalledCount} · " +
                  $"跳过 {coverage.SkippedCount} · 失败 {coverage.FailedCount}";
            builder.AppendLine("安装统计：" + coverageSummary);
            builder.AppendLine();
            builder.AppendLine("安装明细：");

            var entries = coverage.Entries ?? Array.Empty<UPilotMonoHookInstallEntry>();
            if (entries.Count == 0)
            {
                if (coverage.Samples.Count > 0)
                    builder.Append(string.Join("\n", coverage.Samples));
                else
                    builder.Append("该点位没有类型级安装详情。");
                return builder.ToString();
            }

            var validEntries = entries.Where(item => item != null).ToList();
            bool showMethodSignature = validEntries
                .Select(item => item.MethodSignature)
                .Where(signature => !string.IsNullOrWhiteSpace(signature))
                .Distinct(StringComparer.Ordinal)
                .Skip(1)
                .Any();

            var groups = validEntries
                .GroupBy(item => item.Status ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => GetInstallEntryStatusOrder(group.Key))
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToList();

            for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var group = groups[groupIndex];
                var lines = group
                    .Select(entry => BuildInstallEntryDisplayLine(entry, showMethodSignature))
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(line => line, StringComparer.Ordinal)
                    .ToList();

                if (groupIndex > 0)
                    builder.AppendLine();
                builder.AppendLine($"{GetInstallEntryStatusTitle(group.Key)}（{lines.Count}）");
                foreach (string line in lines)
                    builder.AppendLine(line);
            }
            return builder.ToString().TrimEnd();
        }

        private static string BuildInstallEntryDisplayLine(
            UPilotMonoHookInstallEntry entry,
            bool showMethodSignature)
        {
            string line = FormatInstallEntryTypeName(entry.TargetTypeName);
            if (!string.IsNullOrEmpty(entry.DeclaringTypeName) &&
                !string.Equals(entry.DeclaringTypeName, entry.TargetTypeName, StringComparison.Ordinal))
                line += "（继承自 " + FormatInstallEntryTypeName(entry.DeclaringTypeName) + "）";
            if (showMethodSignature && !string.IsNullOrEmpty(entry.MethodSignature))
                line += " · " + entry.MethodSignature;
            if (!string.IsNullOrEmpty(entry.Reason))
                line += " — " + entry.Reason;
            return line;
        }

        private static string FormatInstallEntryTypeName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return string.Empty;

            Type resolvedType = ResolveInstallEntryType(fullName);
            return resolvedType == null
                ? fullName.Replace('+', '.')
                : FormatInstallEntryTypeName(resolvedType);
        }

        private static Type ResolveInstallEntryType(string fullName)
        {
            try
            {
                var resolved = Type.GetType(fullName, false);
                if (resolved != null) return resolved;
            }
            catch
            {
                // Fall through to the loaded-assembly lookup.
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var resolved = assembly.GetType(fullName, false);
                    if (resolved != null) return resolved;
                }
                catch
                {
                    // A display-only lookup must not block the details dialog.
                }
            }
            return null;
        }

        private static string FormatInstallEntryTypeName(Type type)
        {
            if (type == null) return string.Empty;
            if (type.IsArray)
                return FormatInstallEntryTypeName(type.GetElementType()) + "[]";
            if (type.IsByRef)
                return FormatInstallEntryTypeName(type.GetElementType()) + "&";
            if (type.IsPointer)
                return FormatInstallEntryTypeName(type.GetElementType()) + "*";
            if (type.IsGenericParameter)
                return type.Name;

            string typeName = type.IsGenericType
                ? type.GetGenericTypeDefinition().FullName
                : type.FullName;
            typeName = RemoveGenericArity(typeName ?? type.Name).Replace('+', '.');
            if (!type.IsGenericType) return typeName;

            string genericArguments = string.Join(", ", type.GetGenericArguments()
                .Select(FormatInstallEntryTypeName));
            return typeName + "<" + genericArguments + ">";
        }

        private static string RemoveGenericArity(string typeName)
        {
            if (string.IsNullOrEmpty(typeName) || typeName.IndexOf('`') < 0)
                return typeName ?? string.Empty;

            var builder = new StringBuilder(typeName.Length);
            for (int index = 0; index < typeName.Length; index++)
            {
                if (typeName[index] != '`')
                {
                    builder.Append(typeName[index]);
                    continue;
                }

                while (index + 1 < typeName.Length && char.IsDigit(typeName[index + 1]))
                    index++;
            }
            return builder.ToString();
        }

        private static int GetInstallEntryStatusOrder(string status)
        {
            if (string.Equals(status, "Installed", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(status, "Skipped", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)) return 2;
            return 3;
        }

        private static string GetInstallEntryStatusTitle(string status)
        {
            if (string.Equals(status, "Installed", StringComparison.OrdinalIgnoreCase)) return "已安装";
            if (string.Equals(status, "Skipped", StringComparison.OrdinalIgnoreCase)) return "跳过";
            if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)) return "失败";
            return string.IsNullOrWhiteSpace(status) ? "其他" : status;
        }

        private static Color GetStatusColor(UPilotMonoHookInstallState state)
        {
            switch (state)
            {
                case UPilotMonoHookInstallState.Installed:
                    return new Color(0.45f, 0.85f, 0.45f);
                case UPilotMonoHookInstallState.PartiallyInstalled:
                    return new Color(0.85f, 0.8f, 0.35f);
                case UPilotMonoHookInstallState.Failed:
                    return new Color(1f, 0.45f, 0.45f);
                case UPilotMonoHookInstallState.Unsupported:
                    return new Color(1f, 0.75f, 0.35f);
                default:
                    return new Color(0.65f, 0.65f, 0.65f);
            }
        }

        private static string BuildStatusTooltip(UPilotMonoHookPointRuntimeState state)
        {
            if (state == null) return string.Empty;
            string tooltip = state.Coverage == null || state.Coverage.Samples.Count == 0
                ? state.Message ?? string.Empty
                : (state.Message ?? string.Empty) + "\n" + string.Join("\n", state.Coverage.Samples);
            return string.IsNullOrEmpty(tooltip)
                ? "点击查看安装详情"
                : tooltip + "\n点击查看安装详情";
        }
    }
}
