// -----------------------------------------------------------------------
// UPilot Editor - target filter profile UI.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public sealed partial class UPilotMonoHookWindow
    {
        private int _selectedFilterProfile;
        private string _filterTestResult = string.Empty;
        private bool _showPointFilterOverrides;
        private readonly Dictionary<string, bool> _expandedFilterRules = new Dictionary<string, bool>(StringComparer.Ordinal);

        private void DrawFilterProfiles(UPilotMonoHookSettings settings)
        {
            EditorGUILayout.BeginVertical("helpBox");
            _showFilterProfiles = EditorGUILayout.Foldout(
                _showFilterProfiles,
                "目标过滤器（支持包含/排除、类型、名称、Hierarchy、身份、状态、事件和低噪声条件）",
                true);
            if (!_showFilterProfiles)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            settings.EnsureDefaults();
            var profiles = settings.filterProfiles ?? new List<UPilotTraceFilterProfile>();
            var profileIds = new List<string> { UPilotTraceFilterProfileIds.None };
            var profileLabels = new List<string> { "不过滤（全部目标）" };
            profileIds.AddRange(profiles.Where(profile => profile != null).Select(profile => profile.Id));
            profileLabels.AddRange(profiles.Where(profile => profile != null).Select(profile => profile.Name));
            int globalIndex = profileIds.IndexOf(settings.globalFilterProfileId);
            if (globalIndex < 0) globalIndex = 0;
            int nextGlobalIndex = EditorGUILayout.Popup("全局过滤器", globalIndex, profileLabels.ToArray());
            if (nextGlobalIndex != globalIndex)
            {
                settings.globalFilterProfileId = profileIds[nextGlobalIndex];
                settings.SaveSettings();
            }

            EditorGUI.BeginChangeCheck();
            settings.pointFilterOverridesEnabled = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "启用点位独立过滤器",
                    "关闭时所有点位使用全局过滤器；开启后，仅配置了覆盖项的点位使用独立过滤器，其余点位仍继承全局。"),
                settings.pointFilterOverridesEnabled);
            if (EditorGUI.EndChangeCheck())
                settings.SaveSettings();

            if (settings.pointFilterOverridesEnabled)
            {
                _showPointFilterOverrides = EditorGUILayout.Foldout(
                    _showPointFilterOverrides,
                    "点位过滤器覆盖",
                    true);
                if (_showPointFilterOverrides)
                {
                    var overrideIds = new List<string> { string.Empty };
                    var overrideLabels = new List<string> { "继承全局" };
                    overrideIds.AddRange(profileIds);
                    overrideLabels.AddRange(profileLabels);
                    foreach (var group in UPilotMonoHookCatalog.All.GroupBy(definition => definition.CategoryId))
                    {
                        EditorGUILayout.LabelField(group.First().CategoryDisplayName, EditorStyles.miniBoldLabel);
                        foreach (var definition in group)
                        {
                            string configuredId = settings.GetConfiguredFilterProfileId(definition.Id);
                            int configuredIndex = overrideIds.IndexOf(configuredId);
                            if (configuredIndex < 0) configuredIndex = 0;
                            int nextIndex = EditorGUILayout.Popup(
                                definition.DisplayName,
                                configuredIndex,
                                overrideLabels.ToArray());
                            if (nextIndex != configuredIndex)
                            {
                                settings.SetFilterProfileId(definition.Id, overrideIds[nextIndex]);
                                settings.SaveSettings();
                            }
                        }
                    }
                }
            }

            EditorGUILayout.HelpBox(
                "规则内条件按 AND 组合；多个包含规则按 OR 组合；排除规则优先。点位覆盖为空时继承全局过滤器；有效过滤器会在堆栈采集、事件缓存和 Console 输出之前执行。",
                MessageType.None);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("新建过滤器", GUILayout.Width(95f)))
            {
                var profile = new UPilotTraceFilterProfile { Name = "自定义过滤器" };
                profile.Rules.Add(new UPilotTraceFilterRule { Name = "包含规则" });
                profiles.Add(profile);
                _selectedFilterProfile = profiles.Count - 1;
                settings.SaveSettings();
            }
            if (GUILayout.Button("复制", GUILayout.Width(55f)) && profiles.Count > 0 && _selectedFilterProfile < profiles.Count)
            {
                profiles.Add(profiles[_selectedFilterProfile].Clone());
                _selectedFilterProfile = profiles.Count - 1;
                settings.SaveSettings();
            }
            if (GUILayout.Button("导出", GUILayout.Width(55f)))
            {
                string path = EditorUtility.SaveFilePanel("导出追踪器过滤器", string.Empty, "UPilotTraceFilters.json", "json");
                if (!string.IsNullOrEmpty(path))
                {
                    int count = UPilotTraceFilterPresetService.Export(path, profiles);
                    _filterTestResult = $"已导出 {count} 个过滤器：{path}";
                }
            }
            if (GUILayout.Button("导入", GUILayout.Width(55f)))
            {
                string path = EditorUtility.OpenFilePanel("导入追踪器过滤器", string.Empty, "json");
                if (!string.IsNullOrEmpty(path))
                {
                    int count = UPilotTraceFilterPresetService.Import(path, settings, false);
                    _filterTestResult = $"已导入 {count} 个过滤器";
                }
            }
            if (GUILayout.Button("清零统计", GUILayout.Width(65f)))
            {
                UPilotTraceFilterEngine.ClearStatistics();
                _filterTestResult = "已清零过滤统计";
            }
            if (Selection.activeGameObject != null && GUILayout.Button("按当前选中创建", GUILayout.Width(105f)))
            {
                var profile = new UPilotTraceFilterProfile
                {
                    Name = "选中对象：" + Selection.activeGameObject.name,
                };
                profile.Rules.Add(new UPilotTraceFilterRule
                {
                    Name = "当前选中对象及子树",
                    Effect = UPilotTraceFilterRuleEffect.Include,
                    SelectionScope = UPilotTraceSelectionScope.SelectedSubtree,
                });
                profiles.Add(profile);
                _selectedFilterProfile = profiles.Count - 1;
                settings.SaveSettings();
            }
            EditorGUILayout.EndHorizontal();

            var editableProfiles = profiles.Where(profile => profile != null).ToList();
            if (editableProfiles.Count == 0)
            {
                EditorGUILayout.EndVertical();
                return;
            }
            _selectedFilterProfile = Mathf.Clamp(_selectedFilterProfile, 0, editableProfiles.Count - 1);
            int nextProfile = EditorGUILayout.Popup("编辑过滤器", _selectedFilterProfile, editableProfiles.Select(profile => profile.Name).ToArray());
            if (nextProfile != _selectedFilterProfile) _selectedFilterProfile = nextProfile;
            var selected = editableProfiles[_selectedFilterProfile];

            EditorGUI.BeginChangeCheck();
            selected.Name = EditorGUILayout.TextField("名称", selected.Name);
            selected.Enabled = EditorGUILayout.ToggleLeft("启用该过滤器", selected.Enabled);
            if (selected.BuiltIn)
                EditorGUILayout.LabelField("内置预设（可复制后自定义）", EditorStyles.miniLabel);
            if (EditorGUI.EndChangeCheck() && !selected.BuiltIn)
                settings.SaveSettings();

            for (int i = 0; i < selected.Rules.Count; i++)
            {
                var rule = selected.Rules[i];
                if (rule == null) continue;
                DrawFilterRule(settings, selected, rule, i);
            }
            if (GUILayout.Button("添加规则"))
            {
                selected.Rules.Add(new UPilotTraceFilterRule { Name = "新规则" });
                settings.SaveSettings();
            }

            var statistics = UPilotTraceFilterEngine.SnapshotStatistics()
                .Where(item => string.Equals(item.profileId, selected.Id, StringComparison.Ordinal))
                .ToArray();
            if (statistics.Length > 0)
            {
                EditorGUILayout.LabelField(
                    "统计：" + string.Join("；", statistics.Select(item =>
                        $"{item.pointId} 命中 {item.accepted} / 排除 {item.rejected}")),
                    EditorStyles.miniLabel);
            }
            if (GUILayout.Button("测试当前选中对象"))
            {
                var selectedObject = Selection.activeObject;
                var pointId = UPilotMonoHookPointId.GameObjectDestroy;
                UPilotTraceFilterEngine.Evaluate(
                    pointId,
                    selectedObject,
                    selectedObject != null ? selectedObject.name : string.Empty,
                    string.Empty,
                    string.Empty,
                    selectedObject != null ? selectedObject.GetType().FullName : string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    false,
                    out var decision);
                _filterTestResult = selectedObject == null
                    ? "当前没有选中对象"
                    : (decision.Accepted ? "匹配：" : "不匹配：") + decision.Reason;
            }
            if (!string.IsNullOrEmpty(_filterTestResult))
                EditorGUILayout.HelpBox(_filterTestResult, MessageType.None);
            EditorGUILayout.EndVertical();
        }

        private void DrawFilterRule(
            UPilotMonoHookSettings settings,
            UPilotTraceFilterProfile profile,
            UPilotTraceFilterRule rule,
            int index)
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();
            rule.Enabled = EditorGUILayout.Toggle(rule.Enabled, GUILayout.Width(18f));
            rule.Name = EditorGUILayout.TextField(rule.Name, GUILayout.MinWidth(120f));
            rule.Effect = (UPilotTraceFilterRuleEffect)EditorGUILayout.EnumPopup(rule.Effect, GUILayout.Width(65f));
            if (!profile.BuiltIn && GUILayout.Button("删除", GUILayout.Width(45f)))
            {
                profile.Rules.RemoveAt(index);
                settings.SaveSettings();
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                return;
            }
            bool expanded = !_expandedFilterRules.TryGetValue(rule.Id, out var storedExpanded) || storedExpanded;
            if (GUILayout.Button(expanded ? "收起" : "展开", GUILayout.Width(45f)))
            {
                expanded = !expanded;
                _expandedFilterRules[rule.Id] = expanded;
            }
            EditorGUILayout.EndHorizontal();

            if (!expanded)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUI.BeginChangeCheck();
            rule.ObjectScope = (UPilotTraceObjectScope)EditorGUILayout.EnumPopup("对象来源", rule.ObjectScope);
            rule.AssemblyPatterns = EditorGUILayout.TextField("程序集模式", rule.AssemblyPatterns);
            rule.NamespacePatterns = EditorGUILayout.TextField("命名空间模式", rule.NamespacePatterns);
            rule.TypePatterns = EditorGUILayout.TextField("类型模式", rule.TypePatterns);
            rule.TargetTypeName = EditorGUILayout.TextField("目标类型", rule.TargetTypeName);
            if (!string.IsNullOrWhiteSpace(rule.TargetTypeName))
                rule.TargetTypeMatchMode = (UPilotTraceTypeMatchMode)EditorGUILayout.EnumPopup("类型匹配", rule.TargetTypeMatchMode);
            rule.RequiredComponentTypeName = EditorGUILayout.TextField("包含组件", rule.RequiredComponentTypeName);
            if (!string.IsNullOrWhiteSpace(rule.RequiredComponentTypeName))
                rule.RequiredComponentIncludeDerived = EditorGUILayout.ToggleLeft("包含派生组件", rule.RequiredComponentIncludeDerived);
            rule.NameMatchMode = (UPilotTraceStringMatchMode)EditorGUILayout.EnumPopup("名称匹配", rule.NameMatchMode);
            if (rule.NameMatchMode != UPilotTraceStringMatchMode.Any)
                rule.NamePattern = EditorGUILayout.TextField("名称条件", rule.NamePattern);
            rule.HierarchyMatchMode = (UPilotTraceHierarchyMatchMode)EditorGUILayout.EnumPopup("Hierarchy 匹配", rule.HierarchyMatchMode);
            if (rule.HierarchyMatchMode != UPilotTraceHierarchyMatchMode.Any)
                rule.HierarchyPattern = EditorGUILayout.TextField("Hierarchy 条件", rule.HierarchyPattern);
            rule.HierarchyRelation = (UPilotTraceHierarchyRelation)EditorGUILayout.EnumPopup("层级关系", rule.HierarchyRelation);
            rule.ParentNameMatchMode = (UPilotTraceStringMatchMode)EditorGUILayout.EnumPopup("父节点名称", rule.ParentNameMatchMode);
            if (rule.ParentNameMatchMode != UPilotTraceStringMatchMode.Any)
                rule.ParentNamePattern = EditorGUILayout.TextField("父节点条件", rule.ParentNamePattern);
            rule.AncestorNameMatchMode = (UPilotTraceStringMatchMode)EditorGUILayout.EnumPopup("祖先节点名称", rule.AncestorNameMatchMode);
            if (rule.AncestorNameMatchMode != UPilotTraceStringMatchMode.Any)
                rule.AncestorNamePattern = EditorGUILayout.TextField("祖先节点条件", rule.AncestorNamePattern);
            rule.MaxHierarchyDepth = EditorGUILayout.IntField("最大层级深度（-1 不限）", rule.MaxHierarchyDepth);
            rule.SceneMatchMode = (UPilotTraceStringMatchMode)EditorGUILayout.EnumPopup("Scene 匹配", rule.SceneMatchMode);
            if (rule.SceneMatchMode != UPilotTraceStringMatchMode.Any)
                rule.ScenePattern = EditorGUILayout.TextField("Scene 条件", rule.ScenePattern);
            rule.AssetPathMatchMode = (UPilotTraceStringMatchMode)EditorGUILayout.EnumPopup("资源路径匹配", rule.AssetPathMatchMode);
            if (rule.AssetPathMatchMode != UPilotTraceStringMatchMode.Any)
                rule.AssetPathPattern = EditorGUILayout.TextField("资源路径条件", rule.AssetPathPattern);
            rule.PrefabAssetPathPattern = EditorGUILayout.TextField(
                new GUIContent("Prefab 来源路径", "Prefab 实例会解析到对应 Prefab 资源路径，支持 * 和 ? 通配符。"),
                rule.PrefabAssetPathPattern);
            rule.LayerMask = EditorGUILayout.MaskField("Layer", rule.LayerMask, InternalEditorUtility.layers);
            rule.TagMatchMode = (UPilotTraceTagMatchMode)EditorGUILayout.EnumPopup("Tag 匹配", rule.TagMatchMode);
            rule.Tags = EditorGUILayout.TextField("Tag（逗号分隔）", rule.Tags);
            rule.ActiveState = (UPilotTraceActiveState)EditorGUILayout.EnumPopup("激活状态", rule.ActiveState);
            rule.ComponentEnabledState = (UPilotTraceEnabledState)EditorGUILayout.EnumPopup("组件 enabled", rule.ComponentEnabledState);
            if (!string.IsNullOrWhiteSpace(rule.RequiredComponentTypeName))
                rule.RequiredComponentEnabledState = (UPilotTraceEnabledState)EditorGUILayout.EnumPopup(
                    "指定组件 enabled", rule.RequiredComponentEnabledState);
            rule.PrefabState = (UPilotTracePrefabState)EditorGUILayout.EnumPopup("Prefab 状态", rule.PrefabState);
            rule.SelectionScope = (UPilotTraceSelectionScope)EditorGUILayout.EnumPopup("选中范围", rule.SelectionScope);
            rule.PointPatterns = EditorGUILayout.TextField("点位模式", rule.PointPatterns);
            rule.MethodPatterns = EditorGUILayout.TextField("方法模式", rule.MethodPatterns);
            rule.PhasePatterns = EditorGUILayout.TextField("阶段模式", rule.PhasePatterns);
            rule.EventSourcePatterns = EditorGUILayout.TextField(
                new GUIContent("事件来源模式", "内置事件来源为 EditMode 或 PlayMode；自定义事件可提供自己的来源字符串。"),
                rule.EventSourcePatterns);
            rule.PlayMode = (UPilotTracePlayMode)EditorGUILayout.EnumPopup("运行模式", rule.PlayMode);
            rule.ValueCondition = (UPilotTraceValueCondition)EditorGUILayout.EnumPopup("值条件", rule.ValueCondition);
            if (rule.ValueCondition == UPilotTraceValueCondition.BeforeEquals ||
                rule.ValueCondition == UPilotTraceValueCondition.AfterEquals ||
                rule.ValueCondition == UPilotTraceValueCondition.AfterContains)
                rule.ValuePattern = EditorGUILayout.TextField("值条件内容", rule.ValuePattern);
            if (rule.ValueCondition == UPilotTraceValueCondition.NumericDeltaAtLeast)
                rule.NumericDeltaThreshold = EditorGUILayout.FloatField("最小数值变化", rule.NumericDeltaThreshold);
            rule.CustomFilterId = EditorGUILayout.TextField("自定义过滤器 ID", rule.CustomFilterId);
            if (!string.IsNullOrWhiteSpace(rule.CustomFilterId))
                rule.CustomFilterArgument = EditorGUILayout.TextField("自定义参数", rule.CustomFilterArgument);
            rule.IgnoreCase = EditorGUILayout.ToggleLeft("字符串忽略大小写", rule.IgnoreCase);
            rule.TargetInstanceId = EditorGUILayout.IntField(
                new GUIContent("目标 InstanceID", "仅当前 Unity 会话稳定，保存到项目配置前请优先使用名称、路径或 GlobalObjectId。"),
                rule.TargetInstanceId);
            rule.TargetGlobalObjectId = EditorGUILayout.TextField(
                new GUIContent("目标 GlobalObjectId", "适合持久化定位场景对象或资源。"),
                rule.TargetGlobalObjectId);
            if (EditorGUI.EndChangeCheck()) settings.SaveSettings();
            EditorGUILayout.EndVertical();
        }
    }
}
