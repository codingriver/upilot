using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace codingriver.upilot.UIFlow
{
    public static class EditorAsyncUtility
    {
        public static Task NextFrameAsync(CancellationToken cancellationToken)
        {
            return DelayAsync(16, cancellationToken);
        }

        public static Task DelayAsync(int delayMs, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            double startedAt = EditorApplication.timeSinceStartup;

            void Tick()
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    EditorApplication.update -= Tick;
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }

                double elapsedMs = (EditorApplication.timeSinceStartup - startedAt) * 1000d;
                if (elapsedMs < delayMs)
                {
                    return;
                }

                EditorApplication.update -= Tick;
                tcs.TrySetResult(true);
            }

            EditorApplication.update += Tick;
            return tcs.Task;
        }
    }

    /// <summary>
    /// Determines whether a visual element should be treated as visible.
    /// </summary>
    public static class ElementVisibilityEvaluator
    {
        /// <summary>
        /// Evaluates basic visibility constraints for an element.
        /// </summary>
        public static bool IsVisible(VisualElement element)
        {
            if (element == null || element.panel == null)
            {
                return false;
            }

            return element.resolvedStyle.display != DisplayStyle.None
                && element.resolvedStyle.visibility != Visibility.Hidden
                && element.resolvedStyle.opacity > 0f;
        }
    }

    public static class VisualElementQueryAdapter
    {
        public static List<VisualElement> QueryAll(VisualElement root)
        {
            var results = new List<VisualElement>();
            if (root == null)
            {
                return results;
            }

            Traverse(root, results);
            return results;
        }

        private static void Traverse(VisualElement current, List<VisualElement> results)
        {
            results.Add(current);
            for (int index = 0; index < current.childCount; index++)
            {
                Traverse(current[index], results);
            }
        }
    }

    /// <summary>
    /// Discovers visual elements in floating panels created by dropdowns, menus, and popups.
    /// </summary>
    public static class FloatingPanelLocator
    {
        private static readonly MethodInfo GetPanelsMethod;
        private static readonly PropertyInfo VisualTreeProperty;

        static FloatingPanelLocator()
        {
            Type uieUtility = Type.GetType("UnityEngine.UIElements.UIElementsUtility, UnityEngine.UIElementsModule");
            GetPanelsMethod = uieUtility?.GetMethod("GetPanels", BindingFlags.Static | BindingFlags.Public);

            Type panelType = Type.GetType("UnityEngine.UIElements.Panel, UnityEngine.UIElementsModule");
            VisualTreeProperty = panelType?.GetProperty("visualTree", BindingFlags.Public | BindingFlags.Instance);
        }

        public static bool IsAvailable => GetPanelsMethod != null && VisualTreeProperty != null;

        public static IEnumerable<VisualElement> GetFloatingPanelRoots(VisualElement excludeRoot = null)
        {
            if (!IsAvailable)
            {
                yield break;
            }

            object panelsDict = GetPanelsMethod.Invoke(null, null);
            if (panelsDict == null)
            {
                yield break;
            }

            var valuesProperty = panelsDict.GetType().GetProperty("Values");
            System.Collections.IEnumerable values = valuesProperty?.GetValue(panelsDict) as System.Collections.IEnumerable;
            if (values == null)
            {
                yield break;
            }

            foreach (object entry in values)
            {
                if (entry == null)
                {
                    continue;
                }

                VisualElement root = VisualTreeProperty.GetValue(entry) as VisualElement;
                if (root != null && root != excludeRoot)
                {
                    yield return root;
                }
            }
        }
    }

    /// <summary>
    /// Finds and waits for UI Toolkit elements.
    /// </summary>
    public sealed class ElementFinder
    {
        public bool EnableVerboseLog { get; set; }

        private static string RootInfo(VisualElement root)
        {
            if (root == null) return "(null)";
            string name = string.IsNullOrEmpty(root.name) ? "" : $"#{root.name}";
            return $"{root.GetType().Name}{name}";
        }

        /// <summary>
        /// Finds the first matching element synchronously.
        /// </summary>
        public FindResult Find(SelectorExpression selector, VisualElement root, bool requireVisible = true)
        {
            if (selector == null)
            {
                throw new UIFlowException(ErrorCodes.SelectorEmpty, "选择器不能为空");
            }

            if (root == null)
            {
                throw new UIFlowException(ErrorCodes.RootElementMissing, $"根节点不存在，无法查找 {selector.Raw}");
            }

            if (EnableVerboseLog)
                Codingriver.Logger.Log($"[UIFlow][Locators] 查找元素 选择器={selector.Raw} 范围={RootInfo(root)} 要求可见={requireVisible}");

            VisualElement element = TryFastPath(selector, root, requireVisible);
            if (element == null)
            {
                foreach (VisualElement candidate in VisualElementQueryAdapter.QueryAll(root))
                {
                    if (!Matches(selector, candidate))
                    {
                        continue;
                    }

                    if (requireVisible && !ElementVisibilityEvaluator.IsVisible(candidate))
                    {
                        continue;
                    }

                    element = candidate;
                    break;
                }
            }

            if (element != null)
            {
                if (EnableVerboseLog)
                    Codingriver.Logger.Log($"[UIFlow][Locators] 找到元素 {ActionContext.ElementInfo(element)} 选择器={selector.Raw}");
                return new FindResult
                {
                    Element = element,
                    FoundAtMs = 0,
                };
            }

            // Fallback: search in floating panels (dropdowns, menus, popups)
            foreach (VisualElement floatingRoot in FloatingPanelLocator.GetFloatingPanelRoots(root))
            {
                element = TryFastPath(selector, floatingRoot, requireVisible);
                if (element == null)
                {
                    foreach (VisualElement candidate in VisualElementQueryAdapter.QueryAll(floatingRoot))
                    {
                        if (!Matches(selector, candidate))
                        {
                            continue;
                        }

                        if (requireVisible && !ElementVisibilityEvaluator.IsVisible(candidate))
                        {
                            continue;
                        }

                        element = candidate;
                        break;
                    }
                }

                if (element != null)
                {
                    if (EnableVerboseLog)
                        Codingriver.Logger.Log($"[UIFlow][Locators] 找到元素(浮动面板) {ActionContext.ElementInfo(element)} 选择器={selector.Raw}");
                    return new FindResult
                    {
                        Element = element,
                        FoundAtMs = 0,
                    };
                }
            }

            if (EnableVerboseLog)
                Codingriver.Logger.Log($"[UIFlow][Locators] 未找到元素 选择器={selector.Raw} 范围={RootInfo(root)}");
            return new FindResult();
        }

        /// <summary>
        /// Waits for the first matching element to appear.
        /// </summary>
        public async Task<FindResult> WaitForElementAsync(SelectorExpression selector, VisualElement root, WaitOptions options, CancellationToken cancellationToken)
        {
            if (root == null)
            {
                throw new UIFlowException(ErrorCodes.RootElementMissing, $"根节点不存在，无法查找 {selector?.Raw}");
            }

            if (selector == null)
            {
                throw new UIFlowException(ErrorCodes.SelectorEmpty, "选择器不能为空");
            }

            if (options == null || options.TimeoutMs < 100 || options.TimeoutMs > 600000)
            {
                throw new UIFlowException(ErrorCodes.TestOptionsInvalid, $"等待超时值非法：{options?.TimeoutMs}");
            }

            if (options.PollIntervalMs < 16 || options.PollIntervalMs > 1000)
            {
                throw new UIFlowException(ErrorCodes.TestOptionsInvalid, "轮询间隔不能小于 16ms");
            }

            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (root.panel == null)
                {
                    throw new UIFlowException(ErrorCodes.ElementDisposedDuringQuery, $"元素树已释放：{selector.Raw}");
                }

                FindResult result = Find(selector, root, options.RequireVisible);
                if (result.Element != null)
                {
                    result.FoundAtMs = UIFlowUtility.DurationMs(startedAt, DateTimeOffset.UtcNow);
                    return result;
                }

                int elapsedMs = UIFlowUtility.DurationMs(startedAt, DateTimeOffset.UtcNow);
                if (elapsedMs >= options.TimeoutMs)
                {
                    throw new UIFlowException(ErrorCodes.ElementWaitTimeout, $"等待元素超时：{selector.Raw}，超时 {options.TimeoutMs}ms");
                }

                await EditorAsyncUtility.DelayAsync(options.PollIntervalMs, cancellationToken);
            }
        }

        /// <summary>
        /// Checks whether the selector currently matches an element.
        /// </summary>
        public bool Exists(SelectorExpression selector, VisualElement root, bool requireVisible = true)
        {
            bool exists = Find(selector, root, requireVisible).Element != null;
            if (EnableVerboseLog)
                Codingriver.Logger.Log($"[UIFlow][Locators] 检查存在 选择器={selector?.Raw} => {exists}");
            return exists;
        }

        private static VisualElement TryFastPath(SelectorExpression selector, VisualElement root, bool requireVisible)
        {
            if (selector.Segments.Count != 1)
            {
                return null;
            }

            SelectorSegment segment = selector.Segments[0];
            VisualElement candidate = null;
            switch (segment.TokenType)
            {
                case SelectorTokenType.Id:
                    if (root.name == segment.TokenValue)
                    {
                        candidate = root;
                    }
                    else
                    {
                        candidate = root.Q(segment.TokenValue);
                    }

                    break;
                case SelectorTokenType.Class:
                    if (root.ClassListContains(segment.TokenValue))
                    {
                        candidate = root;
                    }
                    else
                    {
                        candidate = root.Query<VisualElement>(className: segment.TokenValue).First();
                    }

                    break;
                case SelectorTokenType.Type:
                    foreach (VisualElement element in VisualElementQueryAdapter.QueryAll(root))
                    {
                        if (MatchesType(element, segment.TokenValue))
                        {
                            candidate = element;
                            break;
                        }
                    }

                    break;
            }

            if (candidate != null && (!requireVisible || ElementVisibilityEvaluator.IsVisible(candidate)))
            {
                return candidate;
            }

            return null;
        }

        private static bool Matches(SelectorExpression expression, VisualElement candidate)
        {
            List<SelectorGroup> groups = SelectorGroup.Create(expression);
            if (groups.Count == 0 || !MatchesGroup(candidate, groups[groups.Count - 1]))
            {
                return false;
            }

            VisualElement cursor = candidate;
            for (int groupIndex = groups.Count - 2; groupIndex >= 0; groupIndex--)
            {
                SelectorCombinator relation = groups[groupIndex + 1].CombinatorFromPrevious;
                if (relation == SelectorCombinator.Child)
                {
                    cursor = cursor.parent;
                    if (cursor == null || !MatchesGroup(cursor, groups[groupIndex]))
                    {
                        return false;
                    }

                    continue;
                }

                VisualElement ancestor = cursor.parent;
                VisualElement matchedAncestor = null;
                while (ancestor != null)
                {
                    if (MatchesGroup(ancestor, groups[groupIndex]))
                    {
                        matchedAncestor = ancestor;
                        break;
                    }

                    ancestor = ancestor.parent;
                }

                if (matchedAncestor == null)
                {
                    return false;
                }

                cursor = matchedAncestor;
            }

            return true;
        }

        private static bool MatchesGroup(VisualElement element, SelectorGroup group)
        {
            foreach (SelectorSegment token in group.Tokens)
            {
                switch (token.TokenType)
                {
                    case SelectorTokenType.Id:
                        if (!string.Equals(element.name, token.TokenValue, StringComparison.Ordinal))
                        {
                            return false;
                        }

                        break;
                    case SelectorTokenType.Class:
                        if (!element.ClassListContains(token.TokenValue))
                        {
                            return false;
                        }

                        break;
                    case SelectorTokenType.Type:
                        if (!MatchesType(element, token.TokenValue))
                        {
                            return false;
                        }

                        break;
                    case SelectorTokenType.Attribute:
                        if (!MatchesAttribute(element, token.TokenValue))
                        {
                            return false;
                        }

                        break;
                    case SelectorTokenType.Pseudo:
                        if (!MatchesPseudo(element, token.TokenValue))
                        {
                            return false;
                        }

                        break;
                }
            }

            return true;
        }

        private static bool MatchesType(VisualElement element, string typeName)
        {
            Type type = element.GetType();
            while (type != null)
            {
                if (string.Equals(type.Name, typeName, StringComparison.Ordinal))
                {
                    return true;
                }

                type = type.BaseType;
            }

            return false;
        }

        private static bool MatchesAttribute(VisualElement element, string tokenValue)
        {
            string[] parts = tokenValue.Split(new[] { '=' }, 2);
            if (parts.Length != 2)
            {
                return false;
            }

            string attribute = parts[0];
            string expected = parts[1];
            switch (attribute)
            {
                case "name":
                    return string.Equals(element.name, expected, StringComparison.Ordinal);
                case "tooltip":
                    return string.Equals(element.tooltip, expected, StringComparison.Ordinal);
                default:
                    if (attribute.StartsWith("data-", StringComparison.Ordinal))
                    {
                        if (element.userData is IDictionary<string, object> objectMap && objectMap.TryGetValue(attribute, out object objectValue))
                        {
                            return string.Equals(objectValue?.ToString(), expected, StringComparison.Ordinal);
                        }

                        if (element.userData is IDictionary<string, string> stringMap && stringMap.TryGetValue(attribute, out string stringValue))
                        {
                            return string.Equals(stringValue, expected, StringComparison.Ordinal);
                        }
                    }

                    return false;
            }
        }

        private static bool MatchesPseudo(VisualElement element, string tokenValue)
        {
            if (string.Equals(tokenValue, "first-child", StringComparison.Ordinal))
            {
                return element.parent != null && element.parent.IndexOf(element) == 0;
            }

            return false;
        }

        private sealed class SelectorGroup
        {
            public SelectorCombinator CombinatorFromPrevious = SelectorCombinator.Self;
            public List<SelectorSegment> Tokens = new List<SelectorSegment>();

            public static List<SelectorGroup> Create(SelectorExpression expression)
            {
                var groups = new List<SelectorGroup>();
                SelectorGroup current = null;
                foreach (SelectorSegment segment in expression.Segments)
                {
                    if (current == null || segment.Combinator != SelectorCombinator.Self)
                    {
                        current = new SelectorGroup
                        {
                            CombinatorFromPrevious = groups.Count == 0 ? SelectorCombinator.Self : segment.Combinator,
                        };
                        groups.Add(current);
                    }

                    current.Tokens.Add(segment);
                }

                return groups;
            }
        }
    }
}
