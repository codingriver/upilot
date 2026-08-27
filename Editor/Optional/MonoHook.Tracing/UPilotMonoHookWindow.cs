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
    public sealed class UPilotMonoHookWindow : EditorWindow
    {
        private const string ScrollXSessionKey = "UPilot.MonoHook.Tracing.Window.ScrollX";
        private const string ScrollYSessionKey = "UPilot.MonoHook.Tracing.Window.ScrollY";

        private UPilotMonoHookController _controller;
        private Vector2 _scroll;
        private string _status = "未应用";
        private bool _showEvents = true;
        private bool _showLifecycleScope;
        private string _eventFilter = string.Empty;

        [MenuItem("UPilot/Advanced/MonoHook", false, 215)]
        public static void ShowWindow()
        {
            var window = GetWindow<UPilotMonoHookWindow>("UPilot MonoHook");
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

            EditorGUILayout.LabelField("MonoHook 打点设置", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "手动选择需要安装的打点点位。修改配置后点击“应用”才会安装或卸载变化的 Hook。堆栈采集按点位独立开启，默认全部关闭。",
                MessageType.Info);

            DrawRuntimeOptions(settings);
            DrawLifecycleScope(settings);

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
                _status = "已卸载全部 Hook";
            }
            if (GUILayout.Button("导出诊断", EditorStyles.toolbarButton, GUILayout.Width(70f)))
            {
                string path = EditorUtility.SaveFilePanel(
                    "导出 MonoHook 覆盖诊断",
                    string.Empty,
                    "UPilotMonoHookDiagnostics.jsonl",
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

        private void DrawLifecycleScope(UPilotMonoHookSettings settings)
        {
            EditorGUILayout.BeginVertical("helpBox");
            _showLifecycleScope = EditorGUILayout.Foldout(
                _showLifecycleScope,
                "生命周期目标范围（空值表示不过滤）",
                true);
            if (_showLifecycleScope)
            {
                EditorGUILayout.HelpBox(
                    "支持逗号、分号或换行分隔，支持 * 和 ? 通配符；排除规则优先于包含规则。修改后需要重新应用生命周期 Hook。",
                    MessageType.None);
                EditorGUI.BeginChangeCheck();
                settings.lifecycleAssemblyIncludes = EditorGUILayout.TextField("程序集包含", settings.lifecycleAssemblyIncludes);
                settings.lifecycleAssemblyExcludes = EditorGUILayout.TextField("程序集排除", settings.lifecycleAssemblyExcludes);
                settings.lifecycleNamespaceIncludes = EditorGUILayout.TextField("命名空间包含", settings.lifecycleNamespaceIncludes);
                settings.lifecycleNamespaceExcludes = EditorGUILayout.TextField("命名空间排除", settings.lifecycleNamespaceExcludes);
                settings.lifecycleTypeIncludes = EditorGUILayout.TextField("类型包含", settings.lifecycleTypeIncludes);
                settings.lifecycleTypeExcludes = EditorGUILayout.TextField("类型排除", settings.lifecycleTypeExcludes);
                if (EditorGUI.EndChangeCheck())
                    settings.SaveSettings();
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
                new GUIContent("输出到 Console", "Hook 事件通过过滤和事件限流后，同步输出格式化日志到 Unity Console。"),
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
                    "导出 MonoHook 事件",
                    string.Empty,
                    "UPilotMonoHook.jsonl",
                    "jsonl");
                if (!string.IsNullOrEmpty(path))
                {
                    int count = UPilotMonoHookTelemetry.ExportJsonLines(path);
                    _status = $"已导出 {count} 条事件：{path}";
                }
            }
            EditorGUILayout.EndHorizontal();

            var events = UPilotMonoHookTelemetry.Snapshot(100);
            foreach (var hookEvent in events)
            {
                string line = $"#{hookEvent.sequence} F{hookEvent.frame} {hookEvent.kind} {hookEvent.hierarchyPath}";
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

                bool captureStack = settings.ShouldCaptureStackTrace(definition.Id);
                bool nextCaptureStack = EditorGUILayout.ToggleLeft(
                    new GUIContent("堆栈", "仅为该点位采集调用堆栈；默认关闭。"),
                    captureStack,
                    GUILayout.Width(52f));
                if (nextCaptureStack != captureStack)
                    settings.SetCaptureStackTrace(definition.Id, nextCaptureStack);

                if (_controller.Runtime.TryGetValue(definition.Id, out var state))
                {
                    var previousColor = GUI.color;
                    GUI.color = GetStatusColor(state.InstallState);
                    var statusStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleRight,
                        clipping = TextClipping.Clip,
                    };
                    GUILayout.Label(new GUIContent(state.Message, BuildStatusTooltip(state)), statusStyle, GUILayout.Width(170f));
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
