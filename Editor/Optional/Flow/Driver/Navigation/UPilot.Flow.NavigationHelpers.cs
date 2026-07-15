using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace CodingRiver.UPilot.Flow
{
    public static partial class AdvancedActionHelpers
    {
        private static bool TryActivateTab(VisualElement tab, ActionContext context)
        {
            if (tab == null)
            {
                return false;
            }

            if (TryInvokeParameterlessMethod(tab, "Activate")
                || TryInvokeParameterlessMethod(tab, "Click")
                || TryInvokeParameterlessMethod(tab, "Select"))
            {
                return true;
            }

            ActionHelpers.DispatchClick(tab, 1, MouseButton.LeftMouse, EventModifiers.None, context);
            return true;
        }

        private static bool TryInvokeParameterlessMethod(object target, string methodName)
        {
            MethodInfo method = target?.GetType().GetMethod(methodName, PublicInstance, null, Type.EmptyTypes, null);
            if (method == null)
            {
                return false;
            }

            method.Invoke(target, null);
            return true;
        }

        public static List<string> ReadBreadcrumbsOrThrow(VisualElement element, string actionName)
        {
            List<Button> buttons = element.Query<Button>().ToList();
            if (buttons.Count == 0)
            {
                throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} target does not contain breadcrumb buttons: {element.GetType().Name}");
            }

            return buttons.Select(button => button.text).ToList();
        }

        public static void PageScrollerOrThrow(VisualElement element, Dictionary<string, string> parameters, string actionName)
        {
            if (!(element is Scroller scroller))
            {
                throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} target is not a Scroller: {element.GetType().Name}");
            }

            int pages = 1;
            if (parameters.TryGetValue("pages", out string pagesLiteral) && !string.IsNullOrWhiteSpace(pagesLiteral))
            {
                if (!TryParseInt(pagesLiteral, out pages) || pages <= 0)
                {
                    throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action {actionName} pages is invalid: {pagesLiteral}");
                }
            }

            string directionLiteral = ActionHelpers.Require(parameters, actionName, "direction").Trim().ToLowerInvariant();
            float sign;
            switch (directionLiteral)
            {
                case "up":
                case "left":
                case "decrease":
                case "previous":
                    sign = -1f;
                    break;
                case "down":
                case "right":
                case "increase":
                case "next":
                    sign = 1f;
                    break;
                default:
                    throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action {actionName} direction is invalid: {directionLiteral}");
            }

            float pageSize = ResolveScrollerPageSize(scroller, parameters);
            float next = Mathf.Clamp(scroller.value + (pageSize * pages * sign), scroller.lowValue, scroller.highValue);
            scroller.value = next;
        }

        private static string GetTabLabel(VisualElement element)
        {
            string text = ActionHelpers.GetText(element);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            PropertyInfo labelProperty = element?.GetType().GetProperty("label", PublicInstance);
            if (labelProperty?.CanRead == true)
            {
                return labelProperty.GetValue(element)?.ToString() ?? string.Empty;
            }

            return string.Empty;
        }

        public static void ToggleFoldoutOrThrow(VisualElement element, Dictionary<string, string> parameters, string actionName)
        {
            if (!(element is Foldout foldout))
            {
                throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} target is not a Foldout: {element.GetType().Name}");
            }

            if (parameters.TryGetValue("expand", out string expandLiteral) && !string.IsNullOrWhiteSpace(expandLiteral))
            {
                if (!TryParseBool(expandLiteral, out bool expand))
                {
                    throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action {actionName} expand is invalid: {expandLiteral}");
                }

                foldout.value = expand;
                return;
            }

            foldout.value = !foldout.value;
        }

        public static void SelectTabOrThrow(VisualElement element, Dictionary<string, string> parameters, string actionName, ActionContext context)
        {
            parameters.TryGetValue("label", out string labelLiteral);
            parameters.TryGetValue("index", out string indexLiteral);

            if (string.IsNullOrWhiteSpace(labelLiteral) && string.IsNullOrWhiteSpace(indexLiteral))
            {
                throw new UPilotFlowException(ErrorCodes.ActionParameterMissing, $"Action {actionName} requires label or index.");
            }

            if (!string.IsNullOrWhiteSpace(indexLiteral) && TryParseInt(indexLiteral, out int index))
            {
                if (TrySelectTab(element, index, context))
                {
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(labelLiteral) && TrySelectTab(element, labelLiteral, context))
            {
                return;
            }

            throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} target is not a supported TabView: {element.GetType().Name}");
        }

        private static float ResolveScrollerPageSize(Scroller scroller, Dictionary<string, string> parameters)
        {
            if (parameters.TryGetValue("page_size", out string literal) && !string.IsNullOrWhiteSpace(literal) && TryParseFloat(literal, out float explicitPageSize) && explicitPageSize > 0f)
            {
                return explicitPageSize;
            }

            PropertyInfo pageSizeProperty = scroller.GetType().GetProperty("pageSize", PublicInstance);
            if (pageSizeProperty?.CanRead == true && pageSizeProperty.GetValue(scroller) is float reflectedPageSize && reflectedPageSize > 0f)
            {
                return reflectedPageSize;
            }

            return Mathf.Max(1f, (scroller.highValue - scroller.lowValue) * 0.1f);
        }

        public static (Vector2 from, Vector2 to) ResolveScrollerThumbDrag(Scroller scroller, Dictionary<string, string> parameters, string actionName)
        {
            string directionLiteral = parameters.TryGetValue("direction", out string dir) && !string.IsNullOrWhiteSpace(dir)
                ? dir.Trim().ToLowerInvariant()
                : null;

            float ratio = 0.5f;
            if (parameters.TryGetValue("ratio", out string ratioLiteral) && !string.IsNullOrWhiteSpace(ratioLiteral))
            {
                if (!TryParseFloat(ratioLiteral, out ratio) || ratio < 0f || ratio > 1f)
                {
                    throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action {actionName} ratio is invalid: {ratioLiteral}");
                }
            }
            else if (!string.IsNullOrWhiteSpace(directionLiteral))
            {
                switch (directionLiteral)
                {
                    case "up":
                    case "left":
                    case "decrease":
                    case "previous":
                        ratio = 0f;
                        break;
                    case "down":
                    case "right":
                    case "increase":
                    case "next":
                        ratio = 1f;
                        break;
                    default:
                        throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action {actionName} direction is invalid: {directionLiteral}");
                }
            }

            VisualElement thumb = scroller.Q<VisualElement>(className: "unity-scroller__thumb")
                ?? scroller.Q<VisualElement>(className: "unity-base-slider__dragger")
                ?? scroller.Q<VisualElement>(className: "unity-base-slider__thumb");
            VisualElement track = scroller.Q<VisualElement>(className: "unity-scroller__track")
                ?? scroller.Q<VisualElement>(className: "unity-base-slider__tracker")
                ?? scroller.Q<VisualElement>(className: "unity-base-slider__track");
            if (thumb == null || track == null)
            {
                throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} could not locate scroller thumb or track.");
            }

            Vector2 fromPos = thumb.worldBound.center;
            Rect trackRect = track.worldBound;
            Vector2 toPos = scroller.direction == SliderDirection.Horizontal
                ? new Vector2(Mathf.Lerp(trackRect.xMin, trackRect.xMax, ratio), fromPos.y)
                : new Vector2(fromPos.x, Mathf.Lerp(trackRect.yMin, trackRect.yMax, ratio));

            return (fromPos, toPos);
        }

        public static void SetSliderOrThrow(VisualElement element, Dictionary<string, string> parameters, string actionName)
        {
            if (element is Slider slider)
            {
                string valueLiteral = ActionHelpers.Require(parameters, actionName, "value");
                if (!TryParseFloat(valueLiteral, out float value))
                {
                    throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action {actionName} value is invalid: {valueLiteral}");
                }

                slider.value = Mathf.Clamp(value, slider.lowValue, slider.highValue);
                return;
            }

            if (element is SliderInt sliderInt)
            {
                string valueLiteral = ActionHelpers.Require(parameters, actionName, "value");
                if (!TryParseInt(valueLiteral, out int value))
                {
                    throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action {actionName} value is invalid: {valueLiteral}");
                }

                sliderInt.value = Mathf.Clamp(value, sliderInt.lowValue, sliderInt.highValue);
                return;
            }

            if (element is MinMaxSlider minMaxSlider)
            {
                float minValue;
                float maxValue;

                if (parameters.TryGetValue("value", out string valueLiteral) && !string.IsNullOrWhiteSpace(valueLiteral))
                {
                    if (!TryParseFloatPair(valueLiteral, out minValue, out maxValue))
                    {
                        throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action {actionName} value is invalid: {valueLiteral}");
                    }
                }
                else
                {
                    string minLiteral = ActionHelpers.Require(parameters, actionName, "min_value");
                    string maxLiteral = ActionHelpers.Require(parameters, actionName, "max_value");
                    if (!TryParseFloat(minLiteral, out minValue) || !TryParseFloat(maxLiteral, out maxValue))
                    {
                        throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action {actionName} min/max values are invalid.");
                    }
                }

                minValue = Mathf.Clamp(minValue, minMaxSlider.lowLimit, minMaxSlider.highLimit);
                maxValue = Mathf.Clamp(maxValue, minMaxSlider.lowLimit, minMaxSlider.highLimit);
                if (maxValue < minValue)
                {
                    maxValue = minValue;
                }

                minMaxSlider.value = new Vector2(minValue, maxValue);
                return;
            }

            throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} target is not a slider control: {element.GetType().Name}");
        }

        private static bool TrySelectTab(VisualElement element, int index, ActionContext context)
        {
            List<VisualElement> tabs = CollectTabCandidates(element);
            if (index < 0 || index >= tabs.Count)
            {
                return false;
            }

            if (TrySetIntProperty(element, "selectedTabIndex", index) || TrySetIntProperty(element, "selectedIndex", index))
            {
                return true;
            }

            if (TrySetObjectProperty(element, "activeTab", tabs[index]))
            {
                return true;
            }

            return TryActivateTab(tabs[index], context);
        }

        private static bool TrySelectTab(VisualElement element, string label, ActionContext context)
        {
            foreach (VisualElement candidate in CollectTabCandidates(element))
            {
                if (!string.Equals(GetTabLabel(candidate), label, StringComparison.Ordinal))
                {
                    continue;
                }

                if (TrySetObjectProperty(element, "activeTab", candidate))
                {
                    return true;
                }

                return TryActivateTab(candidate, context);
            }

            return false;
        }

        public static void CloseTabOrThrow(VisualElement element, Dictionary<string, string> parameters, string actionName, ActionContext context)
        {
            parameters.TryGetValue("label", out string labelLiteral);
            parameters.TryGetValue("index", out string indexLiteral);

            if (string.IsNullOrWhiteSpace(labelLiteral) && string.IsNullOrWhiteSpace(indexLiteral))
            {
                throw new UPilotFlowException(ErrorCodes.ActionParameterMissing, $"Action {actionName} requires label or index.");
            }

            if (!string.IsNullOrWhiteSpace(indexLiteral) && TryParseInt(indexLiteral, out int index))
            {
                if (TryCloseTab(element, index, context))
                {
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(labelLiteral) && TryCloseTab(element, labelLiteral, context))
            {
                return;
            }

            throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} could not close the specified tab on {element.GetType().Name}");
        }

        private static IEnumerable<VisualElement> EnumerateDescendants(VisualElement root)
        {
            if (root == null)
            {
                yield break;
            }

            foreach (VisualElement child in root.Children())
            {
                yield return child;
                foreach (VisualElement nested in EnumerateDescendants(child))
                {
                    yield return nested;
                }
            }
        }

        private static List<VisualElement> CollectTabCandidates(VisualElement root)
        {
            var results = new List<VisualElement>();
            foreach (VisualElement element in EnumerateDescendants(root))
            {
                string typeName = element.GetType().Name;
                if (!typeName.Contains("Tab", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(GetTabLabel(element)))
                {
                    continue;
                }

                results.Add(element);
            }

            return results;
        }

        public static void NavigateBreadcrumbOrThrow(VisualElement element, Dictionary<string, string> parameters, string actionName, ActionContext context)
        {
            parameters.TryGetValue("label", out string labelLiteral);
            parameters.TryGetValue("index", out string indexLiteral);

            List<Button> buttons = element.Query<Button>().ToList();
            if (buttons.Count == 0)
            {
                throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} target does not contain breadcrumb buttons: {element.GetType().Name}");
            }

            Button target = null;
            if (!string.IsNullOrWhiteSpace(labelLiteral))
            {
                target = buttons.Find(button => string.Equals(button.text, labelLiteral, StringComparison.Ordinal));
                if (target == null)
                {
                    throw new UPilotFlowException(ErrorCodes.ActionOptionNotFound, $"Action {actionName} breadcrumb label was not found: {labelLiteral}");
                }
            }
            else
            {
                string indexValue = ActionHelpers.Require(parameters, actionName, "index");
                if (!TryParseInt(indexValue, out int index) || index < 0 || index >= buttons.Count)
                {
                    throw new UPilotFlowException(ErrorCodes.ActionIndexOutOfRange, $"Action {actionName} breadcrumb index is out of range: {indexValue}");
                }

                target = buttons[index];
            }

            try
            {
                if (context?.SimulationSession?.PointerDriver != null)
                {
                    context.SimulationSession.PointerDriver.Click(target, 1, MouseButton.LeftMouse, EventModifiers.None, context);
                }
                else
                {
                    ActionHelpers.DispatchClick(target, 1, MouseButton.LeftMouse, EventModifiers.None, context);
                }
            }
            catch (UPilotFlowException)
            {
                // Fallback for elements inside containers (e.g. Toolbar) where panel hit-testing may miss the target.
                context?.Log($"navigate_breadcrumb: panel click failed, using reflection fallback on {ActionContext.ElementInfo(target)}");
                bool triggered = false;

                // 1) Try Button.SendClickEvent() if available (Unity 2023+)
                try
                {
                    var sendClick = target.GetType().GetMethod("SendClickEvent", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (sendClick != null)
                    {
                        sendClick.Invoke(target, null);
                        triggered = true;
                    }
                }
                catch { }

                // 2) Try invoking Button.clicked directly via reflection
                if (!triggered)
                {
                    try
                    {
                        var clickedField = typeof(Button).GetField("clicked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                        if (clickedField != null)
                        {
                            var del = clickedField.GetValue(target) as System.Delegate;
                            if (del != null)
                            {
                                del.DynamicInvoke();
                                triggered = true;
                            }
                        }
                    }
                    catch { }
                }

                // 3) Try invoking Clickable.clicked via reflection (com.unity.ui 1.x only)
                object clickable = null;
                try
                {
                    var clickableProp = target.GetType().GetProperty("clickable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    clickable = clickableProp?.GetValue(target);
                }
                catch { }

                if (!triggered && clickable != null)
                {
                    try
                    {
                        var clickedField = clickable.GetType().GetField("clicked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                        if (clickedField != null)
                        {
                            var del = clickedField.GetValue(clickable) as System.Delegate;
                            if (del != null)
                            {
                                del.DynamicInvoke();
                                triggered = true;
                            }
                        }
                    }
                    catch { }
                }

                // 4) Try to simulate Clickable using its SimulateSingleClick public method if available (com.unity.ui 1.x only)
                if (!triggered && clickable != null)
                {
                    try
                    {
                        var sim = clickable.GetType().GetMethod("SimulateSingleClick", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (sim != null)
                        {
                            sim.Invoke(clickable, new object[] { null, Vector2.zero });
                            triggered = true;
                        }
                    }
                    catch { }
                }

                // 5) Dispatch ClickEvent directly to the button
                if (!triggered)
                {
                    using (ClickEvent clickEvt = ClickEvent.GetPooled())
                    {
                        clickEvt.target = target;
                        target.SendEvent(clickEvt);
                    }
                }
            }
        }

        private static bool TryCloseTab(VisualElement element, int index, ActionContext context)
        {
            List<VisualElement> tabs = CollectTabCandidates(element);
            if (index < 0 || index >= tabs.Count)
            {
                return false;
            }

            return TryCloseTab(tabs[index], context);
        }

        private static bool TryCloseTab(VisualElement element, string label, ActionContext context)
        {
            foreach (VisualElement candidate in CollectTabCandidates(element))
            {
                if (!string.Equals(GetTabLabel(candidate), label, StringComparison.Ordinal))
                {
                    continue;
                }

                return TryCloseTab(candidate, context);
            }

            return false;
        }

        private static bool TryCloseTab(VisualElement tab, ActionContext context)
        {
            if (tab == null)
            {
                return false;
            }

            // 1) Try tab.Close()
            if (TryInvokeParameterlessMethod(tab, "Close"))
            {
                return true;
            }

            // 2) Try parent TabView.CloseTab(tab) or CloseTab(index)
            VisualElement parent = tab.parent;
            while (parent != null)
            {
                if (parent.GetType().Name == "TabView")
                {
                    foreach (MethodInfo m in parent.GetType().GetMethods(PublicInstance).Where(m => m.Name == "CloseTab"))
                    {
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType.IsInstanceOfType(tab))
                        {
                            m.Invoke(parent, new[] { tab });
                            return true;
                        }
                        if (ps.Length == 1 && ps[0].ParameterType == typeof(int))
                        {
                            var tabs = CollectTabCandidates(parent);
                            int idx = tabs.IndexOf(tab);
                            if (idx >= 0)
                            {
                                m.Invoke(parent, new object[] { idx });
                                return true;
                            }
                        }
                    }

                    // 2.5) Unity 6000+: TabView has private OnTabClosed(Tab) instead of public CloseTab
                    MethodInfo onTabClosed = parent.GetType().GetMethod("OnTabClosed", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { tab.GetType() }, null);
                    if (onTabClosed != null)
                    {
                        onTabClosed.Invoke(parent, new[] { tab });
                        return true;
                    }

                    // 3) Fallback: directly remove the tab from the TabView
                    MethodInfo removeMethod = parent.GetType().GetMethod("Remove", PublicInstance, null, new[] { typeof(VisualElement) }, null)
                        ?? parent.GetType().GetMethods(PublicInstance).FirstOrDefault(m => m.Name == "Remove" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(tab));
                    if (removeMethod != null)
                    {
                        removeMethod.Invoke(parent, new[] { tab });
                        return true;
                    }

                    break;
                }
                parent = parent.parent;
            }

            // 4) Fallback: click the close button inside the tab if available
            VisualElement closeButton = tab.Q<VisualElement>(className: "unity-tab__close-button")
                ?? tab.Query<VisualElement>().ToList().FirstOrDefault(child => child.name.Contains("close", StringComparison.OrdinalIgnoreCase));
            if (closeButton != null)
            {
                ActionHelpers.DispatchClick(closeButton, 1, MouseButton.LeftMouse, EventModifiers.None, context);
                return true;
            }

            return false;
        }

        public static void SetSplitViewSizeOrThrow(VisualElement element, Dictionary<string, string> parameters, string actionName)
        {
            if (!(element is TwoPaneSplitView splitView))
            {
                throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action {actionName} target is not a TwoPaneSplitView: {element.GetType().Name}");
            }

            string sizeLiteral = ActionHelpers.Require(parameters, actionName, "size");
            if (!TryParseFloat(sizeLiteral, out float size) || size <= 0f)
            {
                throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action {actionName} size is invalid: {sizeLiteral}");
            }

            int paneIndex = 0;
            if (parameters.TryGetValue("pane", out string paneLiteral) && !string.IsNullOrWhiteSpace(paneLiteral))
            {
                if (!TryParseInt(paneLiteral, out paneIndex) || paneIndex < 0 || paneIndex > 1)
                {
                    throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action {actionName} pane is invalid: {paneLiteral}");
                }
            }

            TrySetFloatProperty(splitView, "fixedPaneInitialDimension", size);
            TrySetIntProperty(splitView, "fixedPaneIndex", paneIndex);

            if (splitView.childCount > paneIndex)
            {
                VisualElement pane = splitView[paneIndex];
                if (splitView.orientation == TwoPaneSplitViewOrientation.Horizontal)
                {
                    pane.style.width = size;
                    pane.style.minWidth = size;
                }
                else
                {
                    pane.style.height = size;
                    pane.style.minHeight = size;
                }
            }
        }
    }
}
