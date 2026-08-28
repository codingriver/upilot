// -----------------------------------------------------------------------
// UPilot Editor - manual MonoHook point management window.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public sealed partial class UPilotMonoHookWindow : EditorWindow
    {
        private const string ScrollXSessionKey = "UPilot.MonoHook.Tracing.Window.ScrollX";
        private const string ScrollYSessionKey = "UPilot.MonoHook.Tracing.Window.ScrollY";

        private UPilotMonoHookController _controller;
        private Vector2 _scroll;
        private string _status = "未应用";
        private bool _showEvents = true;
        private bool _showFilterProfiles = true;
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
        }

        private void OnDisable()
        {
            SessionState.SetFloat(ScrollXSessionKey, _scroll.x);
            SessionState.SetFloat(ScrollYSessionKey, _scroll.y);
        }

        private void OnGUI()
        {
            if (_controller == null)
                _controller = new UPilotMonoHookController();

            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();

            EditorGUILayout.LabelField("追踪点位设置", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "手动选择需要追踪的点位。修改配置后点击“应用”才会安装或移除对应追踪。堆栈采集按点位独立开启，默认全部关闭。",
                MessageType.Info);

            DrawRuntimeOptions(settings);
            DrawFilterProfiles(settings);

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            bool master = EditorGUILayout.ToggleLeft("总开关", settings.masterEnabled, GUILayout.Width(80f));
            if (master != settings.masterEnabled)
            {
                settings.masterEnabled = master;
                GUI.changed = true;
            }

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("应用", EditorStyles.toolbarButton, GUILayout.Width(55f)))
            {
                var report = _controller.Apply();
                int actualFailed = Mathf.Max(0, report.Failed.Count - report.Unsupported.Count);
                _status = $"应用完成：启用 {report.Enabled.Count}（部分 {report.Partial.Count}），禁用 {report.Disabled.Count}，不支持 {report.Unsupported.Count}，失败 {actualFailed}";
            }
            if (GUILayout.Button("卸载全部", EditorStyles.toolbarButton, GUILayout.Width(70f)))
            {
                _controller.UninstallAll();
                _status = "已卸载全部追踪点位";
            }
            if (GUILayout.Button("导出诊断", EditorStyles.toolbarButton, GUILayout.Width(70f)))
            {
                string path = EditorUtility.SaveFilePanel(
                    "导出 UPilot 追踪器诊断",
                    string.Empty,
                    "UPilotTraceDiagnostics.jsonl",
                    "jsonl");
                if (!string.IsNullOrEmpty(path))
                {
                    int count = _controller.ExportDiagnosticsJsonLines(path);
                    _status = $"已导出 {count} 个点位诊断：{path}";
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("全部启用"))
            {
                foreach (var definition in UPilotMonoHookCatalog.All)
                    settings.SetEnabled(definition.Id, true);
            }
            if (GUILayout.Button("全部禁用"))
            {
                foreach (var definition in UPilotMonoHookCatalog.All)
                    settings.SetEnabled(definition.Id, false);
            }
            if (GUILayout.Button("恢复默认"))
            {
                settings.ResetToDefaults();
                _controller = new UPilotMonoHookController();
                _status = "已恢复默认配置";
            }
            EditorGUILayout.EndHorizontal();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var group in UPilotMonoHookCatalog.All.GroupBy(definition => definition.CategoryId))
            {
                var first = group.First();
                DrawCategory(settings, first.CategoryId, first.CategoryDisplayName, group);
            }
            DrawEventLog(settings);
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(_status, EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"配置文件：{UPilotMonoHookSettings.GetAssetPath()}", EditorStyles.miniLabel);
        }

        private static void DrawRuntimeOptions(UPilotMonoHookSettings settings)
        {
            EditorGUILayout.BeginVertical("helpBox");
            EditorGUILayout.LabelField("运行保护", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            settings.autoApplyOnEditorLoad = EditorGUILayout.ToggleLeft(
                "Domain Reload 后自动应用已保存配置（默认关闭）",
                settings.autoApplyOnEditorLoad);
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
            settings.stackTraceMaxFrames = Mathf.Max(1, EditorGUILayout.IntField(
                new GUIContent("堆栈最大帧数", "仅对勾选了“堆栈”的点位生效。"),
                settings.stackTraceMaxFrames));
            settings.stackTraceSampleEveryN = Mathf.Max(1, EditorGUILayout.IntField(
                new GUIContent("堆栈采样间隔", "1 表示每条事件采集；N 表示每 N 条采集一次。"),
                settings.stackTraceSampleEveryN));
            if (EditorGUI.EndChangeCheck())
                settings.SaveSettings();
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
                $"低噪声统计：重复丢弃 {UPilotMonoHookTelemetry.DuplicateDroppedCount}；单对象限流丢弃 {UPilotMonoHookTelemetry.PerObjectDroppedCount}",
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

        private void DrawCategory(
            UPilotMonoHookSettings settings,
            string categoryId,
            string title,
            IEnumerable<UPilotMonoHookPointDefinition> definitions)
        {
            EditorGUILayout.BeginVertical("helpBox");
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("全开", GUILayout.Width(45f)))
                settings.SetCategoryEnabled(categoryId, true);
            if (GUILayout.Button("全关", GUILayout.Width(45f)))
                settings.SetCategoryEnabled(categoryId, false);
            EditorGUILayout.EndHorizontal();

            foreach (var definition in definitions)
            {
                bool enabled = settings.IsConfiguredEnabled(definition.Id);
                EditorGUILayout.BeginHorizontal();
                bool next = EditorGUILayout.ToggleLeft(
                    definition.DisplayName + (definition.HighFrequency ? "（高频）" : ""),
                    enabled,
                    GUILayout.ExpandWidth(true));
                if (next != enabled)
                    settings.SetEnabled(definition.Id, next);

                string configuredFilter = settings.GetConfiguredFilterProfileId(definition.Id);
                var filterIds = new List<string> { string.Empty, UPilotTraceFilterProfileIds.None };
                var filterLabels = new List<string> { "继承全局", "不过滤" };
                foreach (var profile in settings.filterProfiles ?? new List<UPilotTraceFilterProfile>())
                {
                    if (profile == null) continue;
                    filterIds.Add(profile.Id);
                    filterLabels.Add(profile.Name);
                }
                int filterIndex = filterIds.IndexOf(configuredFilter);
                if (filterIndex < 0) filterIndex = 0;
                int nextFilterIndex = EditorGUILayout.Popup(filterIndex, filterLabels.ToArray(), GUILayout.Width(125f));
                if (nextFilterIndex != filterIndex)
                {
                    settings.SetFilterProfileId(definition.Id, filterIds[nextFilterIndex]);
                    settings.SaveSettings();
                }

                if (_controller.Runtime.TryGetValue(definition.Id, out var overloadState) &&
                    overloadState.SupportsHookAllSafeOverloads)
                {
                    bool hookAllSafeOverloads = settings.ShouldHookAllSafeOverloads(definition.Id);
                    bool nextHookAllSafeOverloads = EditorGUILayout.ToggleLeft(
                        new GUIContent(
                            "全部安全重载",
                            "默认安装当前 Unity 版本下的推荐安全重载集合；开启后安装所有通过安全检查的公开非泛型重载，包装调用可能产生重复事件。"),
                        hookAllSafeOverloads,
                        GUILayout.Width(105f));
                    if (nextHookAllSafeOverloads != hookAllSafeOverloads)
                    {
                        settings.SetHookAllSafeOverloads(definition.Id, nextHookAllSafeOverloads);
                        settings.SaveSettings();
                        _controller.RefreshRuntime();
                    }
                }

                bool captureStack = settings.ShouldCaptureStackTrace(definition.Id);
                bool nextCaptureStack = EditorGUILayout.ToggleLeft(
                    new GUIContent("堆栈", "仅为该点位采集调用堆栈；默认关闭。"),
                    captureStack,
                    GUILayout.Width(52f));
                if (nextCaptureStack != captureStack)
                    settings.SetCaptureStackTrace(definition.Id, nextCaptureStack);

                if (_controller.Runtime.TryGetValue(definition.Id, out var state))
                {
                    string effectiveFilterId = settings.GetEffectiveFilterProfileId(definition.Id);
                    var effectiveFilter = settings.FindFilterProfile(effectiveFilterId);
                    var filterStats = UPilotTraceFilterEngine.GetStatistics(definition.Id, effectiveFilterId);
                    string statusText = state.Message ?? string.Empty;
                    if (effectiveFilter != null)
                    {
                        statusText += " · 过滤：" + effectiveFilter.Name;
                        if (filterStats != null)
                            statusText += $" ({filterStats.accepted}/{filterStats.evaluated})";
                    }
                    var previousColor = GUI.color;
                    GUI.color = GetStatusColor(state.InstallState);
                    var statusStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleRight,
                        clipping = TextClipping.Clip,
                    };
                    GUILayout.Label(new GUIContent(statusText, BuildStatusTooltip(state)), statusStyle, GUILayout.Width(170f));
                    GUI.color = previousColor;
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
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
            if (state.Coverage == null || state.Coverage.Samples.Count == 0)
                return state.Message ?? string.Empty;
            return (state.Message ?? string.Empty) + "\n" + string.Join("\n", state.Coverage.Samples);
        }
    }
}
