// -----------------------------------------------------------------------
// UPilot Editor - UPilot Tracer target filtering and preset services.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public sealed class UPilotTraceFilterDescriptor
    {
        public string Id { get; }
        public string DisplayName { get; }
        public int Order { get; }
        public Type ProviderType { get; }
        public IUPilotTraceFilterProvider Provider { get; }
        public string Error { get; }
        public bool IsValid => Provider != null && string.IsNullOrEmpty(Error);

        internal UPilotTraceFilterDescriptor(
            string id,
            string displayName,
            int order,
            Type providerType,
            IUPilotTraceFilterProvider provider,
            string error)
        {
            Id = id ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            Order = order;
            ProviderType = providerType;
            Provider = provider;
            Error = error ?? string.Empty;
        }
    }

    public sealed class UPilotTraceFilterRegistry
    {
        private static readonly Lazy<UPilotTraceFilterRegistry> LazyInstance =
            new Lazy<UPilotTraceFilterRegistry>(() => new UPilotTraceFilterRegistry());
        private readonly List<UPilotTraceFilterDescriptor> _descriptors = new List<UPilotTraceFilterDescriptor>();
        private readonly Dictionary<string, UPilotTraceFilterDescriptor> _byId =
            new Dictionary<string, UPilotTraceFilterDescriptor>(StringComparer.Ordinal);

        public static UPilotTraceFilterRegistry Instance => LazyInstance.Value;
        public IReadOnlyList<UPilotTraceFilterDescriptor> Descriptors => _descriptors;

        private UPilotTraceFilterRegistry() => Refresh();

        public UPilotTraceFilterDescriptor Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            _byId.TryGetValue(id, out var descriptor);
            return descriptor;
        }

        public void Refresh()
        {
            _descriptors.Clear();
            _byId.Clear();
            var discovered = new List<UPilotTraceFilterDescriptor>();
            foreach (var type in TypeCache.GetTypesWithAttribute<UPilotTraceFilterAttribute>())
            {
                var attribute = type.GetCustomAttribute<UPilotTraceFilterAttribute>(false);
                if (attribute == null) continue;
                string error = string.Empty;
                IUPilotTraceFilterProvider provider = null;
                if (string.IsNullOrWhiteSpace(attribute.Id)) error = "过滤器 ID 不能为空";
                else if (string.IsNullOrWhiteSpace(attribute.DisplayName)) error = "过滤器显示名称不能为空";
                else if (type.IsAbstract || !typeof(IUPilotTraceFilterProvider).IsAssignableFrom(type))
                    error = "过滤器必须是实现 IUPilotTraceFilterProvider 的非抽象类";
                else if (type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                             null, Type.EmptyTypes, null) == null)
                    error = "过滤器必须提供无参构造方法";
                if (string.IsNullOrEmpty(error))
                {
                    try { provider = Activator.CreateInstance(type, true) as IUPilotTraceFilterProvider; }
                    catch (Exception ex) { error = ex.GetBaseException().Message; }
                }
                discovered.Add(new UPilotTraceFilterDescriptor(
                    attribute.Id,
                    attribute.DisplayName,
                    attribute.Order,
                    type,
                    provider,
                    error));
            }

            foreach (var group in discovered.GroupBy(item => item.Id, StringComparer.Ordinal))
            {
                var entries = group.ToList();
                var descriptor = entries.Count == 1
                    ? entries[0]
                    : new UPilotTraceFilterDescriptor(
                        group.Key,
                        entries[0].DisplayName,
                        entries[0].Order,
                        entries[0].ProviderType,
                        null,
                        "过滤器 ID 重复：" + string.Join(", ", entries.Select(item => item.ProviderType?.FullName)));
                _descriptors.Add(descriptor);
                _byId[descriptor.Id] = descriptor;
            }
            _descriptors.Sort((left, right) =>
            {
                int order = left.Order.CompareTo(right.Order);
                return order != 0 ? order : string.Compare(left.DisplayName, right.DisplayName, StringComparison.Ordinal);
            });
        }
    }

    internal sealed class UPilotTraceFilterDecision
    {
        public bool Accepted;
        public string ProfileId = string.Empty;
        public string ProfileName = string.Empty;
        public string Reason = string.Empty;
    }

    internal static class UPilotTraceFilterEngine
    {
        private static readonly Dictionary<string, Type> TypeCacheByName =
            new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly Dictionary<string, UPilotTraceFilterStatistics> Statistics =
            new Dictionary<string, UPilotTraceFilterStatistics>(StringComparer.Ordinal);
        private static readonly Regex NumberRegex = new Regex(
            @"[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static bool Evaluate(UPilotMonoHookEvent hookEvent, bool track, out UPilotTraceFilterDecision decision)
        {
            if (hookEvent == null) throw new ArgumentNullException(nameof(hookEvent));
            return Evaluate(
                hookEvent.pointId ?? hookEvent.kind,
                hookEvent.target,
                hookEvent.objectName,
                hookEvent.hierarchyPath,
                hookEvent.scenePath,
                hookEvent.componentType,
                hookEvent.methodSignature,
                hookEvent.beforeValue,
                hookEvent.afterValue,
                hookEvent.phase,
                EditorApplication.isPlaying,
                track,
                out decision,
                hookEvent.targetType,
                hookEvent.targetGlobalObjectId,
                hookEvent.eventSource,
                hookEvent.instanceId);
        }

        internal static bool Evaluate(
            string pointId,
            UnityEngine.Object target,
            string objectName,
            string hierarchyPath,
            string scenePath,
            string componentType,
            string methodSignature,
            string beforeValue,
            string afterValue,
            bool track,
            out UPilotTraceFilterDecision decision)
        {
            return Evaluate(
                pointId,
                target,
                objectName,
                hierarchyPath,
                scenePath,
                componentType,
                methodSignature,
                beforeValue,
                afterValue,
                null,
                EditorApplication.isPlaying,
                track,
                out decision);
        }

        internal static bool Evaluate(
            string pointId,
            UnityEngine.Object target,
            string objectName,
            string hierarchyPath,
            string scenePath,
            string componentType,
            string methodSignature,
            string beforeValue,
            string afterValue,
            string phase,
            bool isPlaying,
            bool track,
            out UPilotTraceFilterDecision decision)
        {
            return Evaluate(
                pointId,
                target,
                objectName,
                hierarchyPath,
                scenePath,
                componentType,
                methodSignature,
                beforeValue,
                afterValue,
                phase,
                isPlaying,
                track,
                out decision,
                null,
                null,
                null,
                0);
        }

        internal static bool Evaluate(
            string pointId,
            UnityEngine.Object target,
            string objectName,
            string hierarchyPath,
            string scenePath,
            string componentType,
            string methodSignature,
            string beforeValue,
            string afterValue,
            string phase,
            bool isPlaying,
            bool track,
            out UPilotTraceFilterDecision decision,
            string targetType,
            string targetGlobalObjectId,
            string eventSource,
            int targetInstanceId)
        {
            var settings = UPilotMonoHookSettings.instance;
            settings.EnsureDefaults();
            var profile = settings.ResolveFilterProfile(pointId ?? string.Empty);
            decision = new UPilotTraceFilterDecision
            {
                Accepted = true,
                ProfileId = profile?.Id ?? UPilotTraceFilterProfileIds.None,
                ProfileName = profile?.Name ?? "不过滤",
                Reason = profile == null ? "未配置过滤器" : "过滤器未启用",
            };
            if (profile == null || !profile.Enabled)
            {
                if (track) Track(pointId, decision);
                return true;
            }

            var context = BuildContext(
                pointId,
                target,
                objectName,
                hierarchyPath,
                scenePath,
                componentType,
                methodSignature,
                beforeValue,
                afterValue,
                phase,
                isPlaying,
                targetType,
                targetGlobalObjectId,
                eventSource,
                targetInstanceId);
            var rules = (profile.Rules ?? new List<UPilotTraceFilterRule>())
                .Where(rule => rule != null && rule.Enabled)
                .ToArray();
            var includes = rules.Where(rule => rule.Effect == UPilotTraceFilterRuleEffect.Include).ToArray();
            var excludes = rules.Where(rule => rule.Effect == UPilotTraceFilterRuleEffect.Exclude).ToArray();

            foreach (var rule in excludes)
            {
                if (!RuleMatches(rule, context, out _)) continue;
                decision.Accepted = false;
                decision.Reason = "命中排除规则：" + rule.Name;
                if (track) Track(pointId, decision);
                return false;
            }

            if (includes.Length > 0)
            {
                foreach (var rule in includes)
                {
                    if (!RuleMatches(rule, context, out _)) continue;
                    decision.Accepted = true;
                    decision.Reason = "命中包含规则：" + rule.Name;
                    if (track) Track(pointId, decision);
                    return true;
                }
                decision.Accepted = false;
                decision.Reason = "未命中任何包含规则";
                if (track) Track(pointId, decision);
                return false;
            }

            decision.Accepted = true;
            decision.Reason = rules.Length == 0 ? "过滤器没有启用的规则" : "未命中排除规则";
            if (track) Track(pointId, decision);
            return true;
        }

        internal static bool IncludesLifecycleType(
            Type type,
            string pointId,
            UPilotMonoHookSettings settings,
            out string reason)
        {
            settings ??= UPilotMonoHookSettings.instance;
            var profile = settings.ResolveFilterProfile(pointId);
            if (profile == null || !profile.Enabled)
            {
                reason = string.Empty;
                return true;
            }
            var rules = (profile.Rules ?? new List<UPilotTraceFilterRule>())
                .Where(rule => rule != null && rule.Enabled)
                .ToArray();
            foreach (var rule in rules.Where(item => item.Effect == UPilotTraceFilterRuleEffect.Exclude))
            {
                if (!HasOnlyTypeCriteria(rule) || !MatchesTypeCriteria(rule, type)) continue;
                reason = "命中类型排除规则：" + rule.Name;
                return false;
            }

            var includes = rules.Where(item => item.Effect == UPilotTraceFilterRuleEffect.Include).ToArray();
            if (includes.Length == 0 || includes.Any(rule => !HasAnyTypeCriteria(rule)))
            {
                reason = string.Empty;
                return true;
            }
            if (includes.Any(rule => MatchesTypeCriteria(rule, type)))
            {
                reason = string.Empty;
                return true;
            }
            reason = "未命中任何类型包含规则：" + (type?.FullName ?? "(null)");
            return false;
        }

        internal static string GetProfileSignature(string pointId, UPilotMonoHookSettings settings)
        {
            settings ??= UPilotMonoHookSettings.instance;
            var profile = settings.ResolveFilterProfile(pointId);
            return profile == null ? UPilotTraceFilterProfileIds.None : JsonUtility.ToJson(profile);
        }

        internal static IReadOnlyList<UPilotTraceFilterStatistics> SnapshotStatistics()
        {
            return Statistics.Values.Select(item => new UPilotTraceFilterStatistics
            {
                pointId = item.pointId,
                profileId = item.profileId,
                evaluated = item.evaluated,
                accepted = item.accepted,
                rejected = item.rejected,
                lastDecision = item.lastDecision,
                lastReason = item.lastReason,
            }).OrderBy(item => item.pointId).ThenBy(item => item.profileId).ToArray();
        }

        internal static UPilotTraceFilterStatistics GetStatistics(string pointId, string profileId)
        {
            Statistics.TryGetValue(BuildStatisticsKey(pointId, profileId), out var result);
            return result;
        }

        internal static void ClearStatistics() => Statistics.Clear();

        private static void Track(string pointId, UPilotTraceFilterDecision decision)
        {
            string key = BuildStatisticsKey(pointId, decision.ProfileId);
            if (!Statistics.TryGetValue(key, out var statistics))
            {
                statistics = new UPilotTraceFilterStatistics
                {
                    pointId = pointId ?? string.Empty,
                    profileId = decision.ProfileId,
                };
                Statistics[key] = statistics;
            }
            statistics.evaluated++;
            if (decision.Accepted) statistics.accepted++;
            else statistics.rejected++;
            statistics.lastDecision = decision.Accepted ? "accepted" : "rejected";
            statistics.lastReason = decision.Reason;
        }

        private static string BuildStatisticsKey(string pointId, string profileId) =>
            (pointId ?? string.Empty) + "\u001f" + (profileId ?? string.Empty);

        private static UPilotTraceFilterContext BuildContext(
            string pointId,
            UnityEngine.Object target,
            string objectName,
            string hierarchyPath,
            string scenePath,
            string componentType,
            string methodSignature,
            string beforeValue,
            string afterValue,
            string phase,
            bool isPlaying,
            string targetType = null,
            string targetGlobalObjectId = null,
            string eventSource = null,
            int targetInstanceId = 0)
        {
            var gameObject = target as GameObject;
            var component = target as Component;
            if (gameObject == null && component != null) gameObject = component.gameObject;
            if (string.IsNullOrEmpty(objectName))
                objectName = gameObject != null ? gameObject.name : component != null ? component.name : target != null ? target.name : string.Empty;
            if (string.IsNullOrEmpty(hierarchyPath) && gameObject != null)
                hierarchyPath = BuildHierarchyPath(gameObject.transform);
            if (string.IsNullOrEmpty(scenePath) && gameObject != null && gameObject.scene.IsValid())
                scenePath = string.IsNullOrEmpty(gameObject.scene.path) ? gameObject.scene.name : gameObject.scene.path;
            if (string.IsNullOrEmpty(componentType) && target != null)
                componentType = target.GetType().FullName;
            if (string.IsNullOrEmpty(targetType) && target != null)
                targetType = target.GetType().FullName;
            if (targetInstanceId == 0 && target != null)
                targetInstanceId = UPilotMonoHookInstallationService.GetObjectId(target);
            if (string.IsNullOrEmpty(targetGlobalObjectId) && target != null)
            {
                try { targetGlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(target).ToString(); }
                catch { targetGlobalObjectId = string.Empty; }
            }
            return new UPilotTraceFilterContext(
                pointId,
                target,
                gameObject,
                component,
                objectName,
                hierarchyPath,
                scenePath,
                componentType,
                methodSignature,
                beforeValue,
                afterValue,
                phase,
                isPlaying,
                targetType,
                targetGlobalObjectId,
                eventSource,
                targetInstanceId);
        }

        private static bool RuleMatches(
            UPilotTraceFilterRule rule,
            UPilotTraceFilterContext context,
            out string reason)
        {
            if (!MatchesObjectScope(rule.ObjectScope, context)) { reason = "对象来源不匹配"; return false; }
            if (!MatchesPatternList(context.PointId, rule.PointPatterns)) { reason = "点位不匹配"; return false; }
            if (!MatchesPatternList(context.MethodSignature, rule.MethodPatterns)) { reason = "方法不匹配"; return false; }
            if (!MatchesPatternList(context.Phase, rule.PhasePatterns)) { reason = "阶段不匹配"; return false; }
            if (!MatchesPatternList(context.EventSource, rule.EventSourcePatterns)) { reason = "事件来源不匹配"; return false; }
            if (rule.PlayMode != UPilotTracePlayMode.Any &&
                (rule.PlayMode == UPilotTracePlayMode.PlayMode) != context.IsPlaying)
            { reason = "运行模式不匹配"; return false; }
            Type targetType = context.Target?.GetType() ?? ResolveType(context.ComponentType);
            if (!MatchesPatternList(targetType?.Assembly.GetName().Name, rule.AssemblyPatterns)) { reason = "程序集不匹配"; return false; }
            if (!MatchesPatternList(targetType?.Namespace, rule.NamespacePatterns)) { reason = "命名空间不匹配"; return false; }
            if (!MatchesPatternList(targetType?.FullName, rule.TypePatterns)) { reason = "类型模式不匹配"; return false; }
            if (!MatchesTargetType(rule, targetType)) { reason = "目标类型不匹配"; return false; }
            if (!MatchesRequiredComponent(rule, context.GameObject)) { reason = "必需组件不匹配"; return false; }
            if (rule.TargetInstanceId != 0 && context.TargetInstanceId != rule.TargetInstanceId)
            { reason = "对象 InstanceID 不匹配"; return false; }
            if (!string.IsNullOrWhiteSpace(rule.TargetGlobalObjectId) &&
                !string.Equals(context.TargetGlobalObjectId, rule.TargetGlobalObjectId.Trim(), StringComparison.Ordinal))
            { reason = "对象 GlobalObjectId 不匹配"; return false; }
            if (!MatchesString(context.ObjectName, rule.NamePattern, rule.NameMatchMode, rule.IgnoreCase)) { reason = "名称不匹配"; return false; }
            if (!MatchesHierarchy(context.HierarchyPath, rule.HierarchyPattern, rule.HierarchyMatchMode, rule.IgnoreCase)) { reason = "Hierarchy 不匹配"; return false; }
            if (!MatchesHierarchyRelation(context.GameObject, rule)) { reason = "父级或层级不匹配"; return false; }
            if (!MatchesString(context.ScenePath, rule.ScenePattern, rule.SceneMatchMode, rule.IgnoreCase)) { reason = "场景不匹配"; return false; }
            string assetPath = context.Target == null ? string.Empty : AssetDatabase.GetAssetPath(context.Target);
            if (!MatchesString(assetPath, rule.AssetPathPattern, rule.AssetPathMatchMode, rule.IgnoreCase)) { reason = "资源路径不匹配"; return false; }
            if (!MatchesPrefabAssetPath(rule, context)) { reason = "Prefab 来源不匹配"; return false; }
            if (rule.LayerMask != -1 && (context.GameObject == null || (rule.LayerMask & (1 << context.GameObject.layer)) == 0)) { reason = "Layer 不匹配"; return false; }
            if (!MatchesTags(context.GameObject, rule.Tags, rule.TagMatchMode, rule.IgnoreCase)) { reason = "Tag 不匹配"; return false; }
            if (!MatchesActive(rule.ActiveState, context.GameObject)) { reason = "激活状态不匹配"; return false; }
            if (!MatchesEnabled(rule.ComponentEnabledState, context.Component)) { reason = "组件启用状态不匹配"; return false; }
            if (!MatchesPrefab(rule.PrefabState, context)) { reason = "Prefab 状态不匹配"; return false; }
            if (!MatchesSelection(rule.SelectionScope, context.GameObject)) { reason = "选中范围不匹配"; return false; }
            if (!MatchesValue(rule, context)) { reason = "值条件不匹配"; return false; }
            if (!MatchesCustom(rule, context, out reason)) return false;
            reason = "匹配";
            return true;
        }

        private static bool MatchesObjectScope(UPilotTraceObjectScope scope, UPilotTraceFilterContext context)
        {
            if (scope == UPilotTraceObjectScope.Any) return true;
            var target = context.Target;
            var gameObject = context.GameObject;
            bool persistent = target != null && EditorUtility.IsPersistent(target);
            bool prefabStage = gameObject != null && PrefabStageUtility.GetPrefabStage(gameObject) != null;
            bool dontSave = target != null && (target.hideFlags & (HideFlags.DontSave | HideFlags.HideAndDontSave)) != 0;
            bool editorTemporary = !persistent && (dontSave || gameObject == null || !gameObject.scene.IsValid());
            switch (scope)
            {
                case UPilotTraceObjectScope.SceneObject:
                    return gameObject != null && gameObject.scene.IsValid() && !persistent && !prefabStage && !editorTemporary;
                case UPilotTraceObjectScope.Asset:
                    return persistent;
                case UPilotTraceObjectScope.EditorTemporary:
                    return editorTemporary;
                case UPilotTraceObjectScope.PrefabStage:
                    return prefabStage;
                default:
                    return true;
            }
        }

        private static bool MatchesTargetType(UPilotTraceFilterRule rule, Type targetType)
        {
            if (rule.TargetTypeMatchMode == UPilotTraceTypeMatchMode.Any || string.IsNullOrWhiteSpace(rule.TargetTypeName)) return true;
            Type expected = ResolveType(rule.TargetTypeName);
            if (expected == null || targetType == null) return false;
            return rule.TargetTypeMatchMode == UPilotTraceTypeMatchMode.Exact
                ? targetType == expected
                : expected.IsAssignableFrom(targetType);
        }

        private static bool MatchesRequiredComponent(UPilotTraceFilterRule rule, GameObject gameObject)
        {
            if (string.IsNullOrWhiteSpace(rule.RequiredComponentTypeName)) return true;
            if (gameObject == null) return false;
            Type expected = ResolveType(rule.RequiredComponentTypeName);
            if (expected == null || !typeof(Component).IsAssignableFrom(expected)) return false;
            Component component = rule.RequiredComponentIncludeDerived
                ? gameObject.GetComponent(expected)
                : gameObject.GetComponents<Component>().FirstOrDefault(item => item != null && item.GetType() == expected);
            if (component == null) return false;
            return MatchesEnabled(rule.RequiredComponentEnabledState, component);
        }

        private static bool MatchesTags(
            GameObject gameObject,
            string tags,
            UPilotTraceTagMatchMode mode,
            bool ignoreCase)
        {
            var patterns = ParseList(tags);
            if (patterns.Length == 0) return true;
            if (gameObject == null) return false;
            string tag = gameObject.tag ?? string.Empty;
            return patterns.Any(pattern => MatchesString(tag, pattern, mode == UPilotTraceTagMatchMode.Equals
                ? UPilotTraceStringMatchMode.Equals
                : mode == UPilotTraceTagMatchMode.Wildcard
                    ? UPilotTraceStringMatchMode.Wildcard
                    : UPilotTraceStringMatchMode.Regex, ignoreCase));
        }

        private static bool MatchesPrefabAssetPath(UPilotTraceFilterRule rule, UPilotTraceFilterContext context)
        {
            if (string.IsNullOrWhiteSpace(rule.PrefabAssetPathPattern)) return true;
            UnityEngine.Object source = context.Target;
            if (source != null && PrefabUtility.IsPartOfPrefabInstance(source))
                source = PrefabUtility.GetCorrespondingObjectFromSource(source);
            string path = source == null ? string.Empty : AssetDatabase.GetAssetPath(source);
            return MatchesString(path, rule.PrefabAssetPathPattern, UPilotTraceStringMatchMode.Wildcard, rule.IgnoreCase);
        }

        private static bool MatchesActive(UPilotTraceActiveState state, GameObject gameObject)
        {
            if (state == UPilotTraceActiveState.Any) return true;
            if (gameObject == null) return false;
            return state == UPilotTraceActiveState.Active ? gameObject.activeInHierarchy : !gameObject.activeInHierarchy;
        }

        private static bool MatchesEnabled(UPilotTraceEnabledState state, Component component)
        {
            if (state == UPilotTraceEnabledState.Any) return true;
            if (component == null) return false;
            bool? enabled = GetComponentEnabled(component);
            return enabled.HasValue && (state == UPilotTraceEnabledState.Enabled ? enabled.Value : !enabled.Value);
        }

        private static bool? GetComponentEnabled(Component component)
        {
            if (component is Behaviour behaviour) return behaviour.enabled;
            var property = component.GetType().GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
            return property != null && property.PropertyType == typeof(bool) ? (bool?)property.GetValue(component) : null;
        }

        private static bool MatchesPrefab(UPilotTracePrefabState state, UPilotTraceFilterContext context)
        {
            if (state == UPilotTracePrefabState.Any) return true;
            var target = context.Target != null ? context.Target : context.GameObject;
            if (target == null) return false;
            bool asset = PrefabUtility.IsPartOfPrefabAsset(target);
            bool instance = PrefabUtility.IsPartOfPrefabInstance(target);
            switch (state)
            {
                case UPilotTracePrefabState.PrefabAsset: return asset;
                case UPilotTracePrefabState.PrefabInstance: return instance;
                case UPilotTracePrefabState.NonPrefab: return !asset && !instance;
                default: return true;
            }
        }

        private static bool MatchesSelection(UPilotTraceSelectionScope scope, GameObject gameObject)
        {
            if (scope == UPilotTraceSelectionScope.Any) return true;
            var selected = Selection.activeGameObject;
            if (selected == null || gameObject == null) return false;
            if (scope == UPilotTraceSelectionScope.SelectedObject) return selected == gameObject;
            return gameObject == selected || gameObject.transform.IsChildOf(selected.transform);
        }

        private static bool MatchesValue(UPilotTraceFilterRule rule, UPilotTraceFilterContext context)
        {
            switch (rule.ValueCondition)
            {
                case UPilotTraceValueCondition.Any:
                    return true;
                case UPilotTraceValueCondition.Changed:
                    return !string.Equals(context.BeforeValue, context.AfterValue, StringComparison.Ordinal);
                case UPilotTraceValueCondition.BeforeEquals:
                    return string.Equals(context.BeforeValue, rule.ValuePattern, StringComparison.Ordinal);
                case UPilotTraceValueCondition.AfterEquals:
                    return string.Equals(context.AfterValue, rule.ValuePattern, StringComparison.Ordinal);
                case UPilotTraceValueCondition.AfterContains:
                    return (context.AfterValue ?? string.Empty).IndexOf(rule.ValuePattern ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
                case UPilotTraceValueCondition.NumericDeltaAtLeast:
                    return CalculateNumericDelta(context.BeforeValue, context.AfterValue) >= Math.Max(0f, rule.NumericDeltaThreshold);
                default:
                    return true;
            }
        }

        private static bool MatchesCustom(
            UPilotTraceFilterRule rule,
            UPilotTraceFilterContext context,
            out string reason)
        {
            if (string.IsNullOrWhiteSpace(rule.CustomFilterId))
            {
                reason = "匹配";
                return true;
            }
            var descriptor = UPilotTraceFilterRegistry.Instance.Find(rule.CustomFilterId);
            if (descriptor == null || !descriptor.IsValid)
            {
                reason = "自定义过滤器不可用：" + rule.CustomFilterId;
                return false;
            }
            try
            {
                return descriptor.Provider.Matches(context, rule.CustomFilterArgument ?? string.Empty, out reason);
            }
            catch (Exception ex)
            {
                reason = "自定义过滤器异常：" + ex.GetBaseException().Message;
                return false;
            }
        }

        private static double CalculateNumericDelta(string before, string after)
        {
            var left = ParseNumbers(before);
            var right = ParseNumbers(after);
            if (left.Count == 0 || left.Count != right.Count) return double.NegativeInfinity;
            double sum = 0d;
            for (int i = 0; i < left.Count; i++)
            {
                double delta = right[i] - left[i];
                sum += delta * delta;
            }
            return Math.Sqrt(sum);
        }

        private static List<double> ParseNumbers(string value)
        {
            var result = new List<double>();
            foreach (Match match in NumberRegex.Matches(value ?? string.Empty))
                if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) result.Add(parsed);
            return result;
        }

        private static bool MatchesHierarchy(
            string value,
            string pattern,
            UPilotTraceHierarchyMatchMode mode,
            bool ignoreCase)
        {
            if (mode == UPilotTraceHierarchyMatchMode.Any || string.IsNullOrEmpty(pattern)) return true;
            if (mode == UPilotTraceHierarchyMatchMode.UnderRoot)
            {
                var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                string root = pattern.TrimEnd('/');
                return string.Equals(value, root, comparison) || (value ?? string.Empty).StartsWith(root + "/", comparison);
            }
            UPilotTraceStringMatchMode stringMode;
            switch (mode)
            {
                case UPilotTraceHierarchyMatchMode.Equals: stringMode = UPilotTraceStringMatchMode.Equals; break;
                case UPilotTraceHierarchyMatchMode.Contains: stringMode = UPilotTraceStringMatchMode.Contains; break;
                case UPilotTraceHierarchyMatchMode.StartsWith: stringMode = UPilotTraceStringMatchMode.StartsWith; break;
                case UPilotTraceHierarchyMatchMode.Wildcard: stringMode = UPilotTraceStringMatchMode.Wildcard; break;
                case UPilotTraceHierarchyMatchMode.Regex: stringMode = UPilotTraceStringMatchMode.Regex; break;
                default: return true;
            }
            return MatchesString(value, pattern, stringMode, ignoreCase);
        }

        private static bool MatchesString(
            string value,
            string pattern,
            UPilotTraceStringMatchMode mode,
            bool ignoreCase)
        {
            if (mode == UPilotTraceStringMatchMode.Any || string.IsNullOrEmpty(pattern)) return true;
            value ??= string.Empty;
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            switch (mode)
            {
                case UPilotTraceStringMatchMode.Equals: return string.Equals(value, pattern, comparison);
                case UPilotTraceStringMatchMode.Contains: return value.IndexOf(pattern, comparison) >= 0;
                case UPilotTraceStringMatchMode.StartsWith: return value.StartsWith(pattern, comparison);
                case UPilotTraceStringMatchMode.Wildcard: return WildcardMatches(value, pattern, ignoreCase);
                case UPilotTraceStringMatchMode.Regex:
                    try { return Regex.IsMatch(value, pattern, ignoreCase ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant); }
                    catch { return false; }
                default: return true;
            }
        }

        private static bool MatchesPatternList(string value, string patterns)
        {
            var parsed = ParseList(patterns);
            return parsed.Length == 0 || parsed.Any(pattern => WildcardMatches(value ?? string.Empty, pattern, true));
        }

        private static bool WildcardMatches(string value, string pattern, bool ignoreCase)
        {
            string expression = "^" + Regex.Escape(pattern ?? string.Empty).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(value ?? string.Empty, expression,
                ignoreCase ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant);
        }

        private static string[] ParseList(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
            return value.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim()).Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static Type ResolveType(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (TypeCacheByName.TryGetValue(name, out var cached)) return cached;
            Type resolved = Type.GetType(name, false);
            if (resolved == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    resolved = assembly.GetType(name, false);
                    if (resolved != null) break;
                }
            }
            TypeCacheByName[name] = resolved;
            return resolved;
        }

        private static string BuildHierarchyPath(Transform transform)
        {
            if (transform == null) return string.Empty;
            var names = new Stack<string>();
            while (transform != null)
            {
                names.Push(transform.name);
                transform = transform.parent;
            }
            return string.Join("/", names);
        }

        private static bool HasAnyTypeCriteria(UPilotTraceFilterRule rule) =>
            !string.IsNullOrWhiteSpace(rule.AssemblyPatterns) ||
            !string.IsNullOrWhiteSpace(rule.NamespacePatterns) ||
            !string.IsNullOrWhiteSpace(rule.TypePatterns) ||
            rule.TargetTypeMatchMode != UPilotTraceTypeMatchMode.Any && !string.IsNullOrWhiteSpace(rule.TargetTypeName);

        private static bool HasOnlyTypeCriteria(UPilotTraceFilterRule rule)
        {
            if (!HasAnyTypeCriteria(rule)) return false;
            return rule.ObjectScope == UPilotTraceObjectScope.Any &&
                   string.IsNullOrWhiteSpace(rule.RequiredComponentTypeName) &&
                   rule.RequiredComponentEnabledState == UPilotTraceEnabledState.Any &&
                   rule.NameMatchMode == UPilotTraceStringMatchMode.Any &&
                   rule.HierarchyMatchMode == UPilotTraceHierarchyMatchMode.Any &&
                   rule.HierarchyRelation == UPilotTraceHierarchyRelation.Any &&
                   rule.SceneMatchMode == UPilotTraceStringMatchMode.Any &&
                   rule.AssetPathMatchMode == UPilotTraceStringMatchMode.Any &&
                   rule.LayerMask == -1 && string.IsNullOrWhiteSpace(rule.Tags) &&
                   rule.TagMatchMode == UPilotTraceTagMatchMode.Equals &&
                   rule.ActiveState == UPilotTraceActiveState.Any &&
                   rule.ComponentEnabledState == UPilotTraceEnabledState.Any &&
                   rule.PrefabState == UPilotTracePrefabState.Any &&
                   string.IsNullOrWhiteSpace(rule.PrefabAssetPathPattern) &&
                   rule.TargetInstanceId == 0 &&
                   string.IsNullOrWhiteSpace(rule.TargetGlobalObjectId) &&
                   string.IsNullOrWhiteSpace(rule.EventSourcePatterns) &&
                   rule.SelectionScope == UPilotTraceSelectionScope.Any &&
                   string.IsNullOrWhiteSpace(rule.PointPatterns) &&
                   string.IsNullOrWhiteSpace(rule.MethodPatterns) &&
                   string.IsNullOrWhiteSpace(rule.PhasePatterns) &&
                   rule.PlayMode == UPilotTracePlayMode.Any &&
                   rule.ParentNameMatchMode == UPilotTraceStringMatchMode.Any &&
                   string.IsNullOrWhiteSpace(rule.ParentNamePattern) &&
                   rule.AncestorNameMatchMode == UPilotTraceStringMatchMode.Any &&
                   string.IsNullOrWhiteSpace(rule.AncestorNamePattern) &&
                   rule.MaxHierarchyDepth < 0 &&
                   rule.ValueCondition == UPilotTraceValueCondition.Any &&
                   string.IsNullOrWhiteSpace(rule.CustomFilterId);
        }

        private static bool MatchesHierarchyRelation(GameObject gameObject, UPilotTraceFilterRule rule)
        {
            if (gameObject == null)
                return rule.HierarchyRelation == UPilotTraceHierarchyRelation.Any &&
                       rule.ParentNameMatchMode == UPilotTraceStringMatchMode.Any &&
                       rule.AncestorNameMatchMode == UPilotTraceStringMatchMode.Any &&
                       rule.MaxHierarchyDepth < 0;

            var transform = gameObject.transform;
            if (rule.HierarchyRelation == UPilotTraceHierarchyRelation.RootObject && transform.parent != null)
                return false;
            if (rule.HierarchyRelation == UPilotTraceHierarchyRelation.DirectChild && transform.parent == null)
                return false;
            if (rule.MaxHierarchyDepth >= 0)
            {
                int depth = 0;
                for (var cursor = transform.parent; cursor != null; cursor = cursor.parent) depth++;
                if (depth > rule.MaxHierarchyDepth) return false;
            }

            if (rule.ParentNameMatchMode != UPilotTraceStringMatchMode.Any &&
                !MatchesString(transform.parent?.name, rule.ParentNamePattern, rule.ParentNameMatchMode, rule.IgnoreCase))
                return false;

            if (rule.AncestorNameMatchMode != UPilotTraceStringMatchMode.Any)
            {
                bool found = false;
                for (var cursor = transform.parent; cursor != null; cursor = cursor.parent)
                {
                    if (!MatchesString(cursor.name, rule.AncestorNamePattern, rule.AncestorNameMatchMode, rule.IgnoreCase)) continue;
                    found = true;
                    break;
                }
                if (!found) return false;
            }
            return true;
        }

        private static bool MatchesTypeCriteria(UPilotTraceFilterRule rule, Type type)
        {
            if (type == null) return false;
            if (!MatchesPatternList(type.Assembly.GetName().Name, rule.AssemblyPatterns)) return false;
            if (!MatchesPatternList(type.Namespace, rule.NamespacePatterns)) return false;
            if (!MatchesPatternList(type.FullName, rule.TypePatterns)) return false;
            return MatchesTargetType(rule, type);
        }
    }

    internal static class UPilotTraceFilterPresetService
    {
        internal static int Export(string path, IEnumerable<UPilotTraceFilterProfile> profiles)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("导出路径不能为空", nameof(path));
            var file = new UPilotTraceFilterPresetFile();
            foreach (var profile in profiles ?? Enumerable.Empty<UPilotTraceFilterProfile>())
            {
                if (profile == null) continue;
                file.profiles.Add(JsonUtility.FromJson<UPilotTraceFilterProfile>(JsonUtility.ToJson(profile)));
            }
            File.WriteAllText(path, JsonUtility.ToJson(file, true));
            return file.profiles.Count;
        }

        internal static int Import(string path, UPilotMonoHookSettings settings, bool replace)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("过滤器预设文件不存在", path);
            settings ??= UPilotMonoHookSettings.instance;
            var file = JsonUtility.FromJson<UPilotTraceFilterPresetFile>(File.ReadAllText(path));
            var loaded = file?.profiles?.Where(profile => profile != null).ToList() ?? new List<UPilotTraceFilterProfile>();
            int importedCount = loaded.Count;
            if (replace) settings.filterProfiles = loaded;
            else
            {
                settings.EnsureDefaults();
                foreach (var profile in loaded)
                {
                    if (settings.FindFilterProfile(profile.Id) != null) profile.Id = Guid.NewGuid().ToString("N");
                    profile.BuiltIn = false;
                    settings.filterProfiles.Add(profile);
                }
            }
            settings.EnsureDefaults();
            settings.SaveSettings();
            return importedCount;
        }
    }
}
