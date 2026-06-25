using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static UnityEditor.TypeCache;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using InputSystemKey = UnityEngine.InputSystem.Key;
using UnityEngine.UIElements;

namespace UnityUIFlow
{
    /// <summary>
    /// Describes an execution-time reporter sink.
    /// </summary>
    public interface IExecutionReporter
    {
        void RecordAction(string stepId, string actionName, string message);
    }

    /// <summary>
    /// Attribute used to declare a YAML action name.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class ActionNameAttribute : Attribute
    {
        public ActionNameAttribute(string actionName)
        {
            ActionName = actionName;
        }

        /// <summary>
        /// YAML action name.
        /// </summary>
        public string ActionName { get; }
    }

    /// <summary>
    /// Action execution context.
    /// </summary>
    public sealed class ActionContext
    {
        public VisualElement Root;
        public ElementFinder Finder;
        public TestOptions Options;
        public IExecutionReporter Reporter;
        public object Simulator;
        public UnityUIFlowSimulationSession SimulationSession;
        public string CurrentStepId;
        public string CurrentCaseName;
        public int CurrentStepIndex;
        public Dictionary<string, object> SharedBag = new Dictionary<string, object>(StringComparer.Ordinal);
        public CancellationToken CancellationToken;
        public ScreenshotManager ScreenshotManager;
        public RuntimeController RuntimeController;
        public readonly List<string> CurrentAttachments = new List<string>();

        public void AddAttachment(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (CurrentAttachments.Count >= 10)
            {
                Codingriver.Logger.LogWarning($"[UnityUIFlow] {ErrorCodes.AttachmentLimitExceeded}: step {CurrentStepId} already has 10 attachments.");
                return;
            }

            CurrentAttachments.Add(path);
        }

        /// <summary>
        /// Writes a verbose log entry when EnableVerboseLog is true.
        /// </summary>
        public void Log(string message)
        {
            if (Options?.EnableVerboseLog == true)
            {
                Codingriver.Logger.Log($"[UnityUIFlow][{CurrentCaseName}][{CurrentStepId}] {message}");
            }
        }

        /// <summary>
        /// Returns a short display string for a visual element.
        /// </summary>
        public static string ElementInfo(VisualElement element)
        {
            if (element == null)
            {
                return "(null)";
            }

            string name = string.IsNullOrEmpty(element.name) ? string.Empty : $"#{element.name}";
            return $"{element.GetType().Name}{name}";
        }
    }

    /// <summary>
    /// Contract implemented by all actions.
    /// </summary>
    public interface IAction
    {
        /// <summary>
        /// Executes the action.
        /// </summary>
        Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters);
    }

    /// <summary>
    /// Resolves built-in and custom actions.
    /// </summary>
    public sealed class ActionRegistry
    {
        private readonly Dictionary<string, Type> _actions = new Dictionary<string, Type>(StringComparer.Ordinal);

        public ActionRegistry()
        {
            RegisterBuiltIns();
            RegisterCustomActions();
        }

        /// <summary>
        /// Returns true when the action exists.
        /// </summary>
        public bool HasAction(string actionName)
        {
            return !string.IsNullOrWhiteSpace(actionName) && _actions.ContainsKey(actionName);
        }

        /// <summary>
        /// Registers a specific action type.
        /// </summary>
        public void Register(string actionName, Type actionType)
        {
            if (string.IsNullOrWhiteSpace(actionName))
            {
                throw new UnityUIFlowException(ErrorCodes.ActionNameConflict, "Action name cannot be empty.");
            }

            if (!typeof(IAction).IsAssignableFrom(actionType))
            {
                throw new UnityUIFlowException(ErrorCodes.ActionNameConflict, $"Action {actionName} does not implement IAction.");
            }

            if (_actions.ContainsKey(actionName))
            {
                throw new UnityUIFlowException(ErrorCodes.ActionNameConflict, $"Duplicate action name: {actionName}");
            }

            _actions[actionName] = actionType;
        }

        /// <summary>
        /// Resolves an action instance by name.
        /// </summary>
        public IAction Resolve(string actionName)
        {
            if (!_actions.TryGetValue(actionName, out Type actionType))
            {
                Codingriver.Logger.LogError($"[UnityUIFlow] 未找到动作 \"{actionName}\"，已注册动作数={_actions.Count}。可用动作：{string.Join(", ", _actions.Keys.Take(20))}");
                throw new UnityUIFlowException(ErrorCodes.ActionNotFound, $"Action not found: {actionName}");
            }

            try
            {
                return (IAction)Activator.CreateInstance(actionType);
            }
            catch (Exception ex)
            {
                Codingriver.Logger.LogError($"[UnityUIFlow] 构造动作 \"{actionName}\" 失败: {ex.Message}");
                throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, $"Failed to construct action {actionName}: {ex.Message}", ex);
            }
        }

        private void RegisterBuiltIns()
        {
            Register("click", typeof(ClickAction));
            Register("double_click", typeof(DoubleClickAction));
            Register("type_text", typeof(TypeTextAction));
            Register("type_text_fast", typeof(TypeTextFastAction));
            Register("press_key", typeof(PressKeyAction));
            Register("press_key_combination", typeof(PressKeyCombinationAction));
            Register("execute_command", typeof(ExecuteCommandAction));
            Register("validate_command", typeof(ValidateCommandAction));
            Register("drag", typeof(DragAction));
            Register("scroll", typeof(ScrollAction));
            Register("hover", typeof(HoverAction));
            Register("open_context_menu", typeof(OpenContextMenuAction));
            Register("select_context_menu_item", typeof(SelectContextMenuItemAction));
            Register("open_popup_menu", typeof(OpenPopupMenuAction));
            Register("select_popup_menu_item", typeof(SelectPopupMenuItemAction));
            Register("assert_menu_item", typeof(AssertMenuItemAction));
            Register("assert_menu_item_disabled", typeof(AssertMenuItemDisabledAction));
            Register("focus", typeof(FocusAction));
            Register("set_value", typeof(SetValueAction));
            Register("set_bound_value", typeof(SetBoundValueAction));
            Register("select_option", typeof(SelectOptionAction));
            Register("toggle_mask_option", typeof(ToggleMaskOptionAction));
            Register("select_list_item", typeof(SelectListItemAction));
            Register("drag_reorder", typeof(DragReorderAction));
            Register("select_tree_item", typeof(SelectTreeItemAction));
            Register("toggle_foldout", typeof(ToggleFoldoutAction));
            Register("set_slider", typeof(SetSliderAction));
            Register("select_tab", typeof(SelectTabAction));
            Register("close_tab", typeof(CloseTabAction));
            Register("navigate_breadcrumb", typeof(NavigateBreadcrumbAction));
            Register("read_breadcrumbs", typeof(ReadBreadcrumbsAction));
            Register("set_split_view_size", typeof(SetSplitViewSizeAction));
            Register("page_scroller", typeof(PageScrollerAction));
            Register("drag_scroller", typeof(DragScrollerAction));
            Register("sort_column", typeof(SortColumnAction));
            Register("resize_column", typeof(ResizeColumnAction));
            Register("click_popup_item", typeof(ClickPopupItemAction));
            Register("menu_item", typeof(MenuItemAction));
            Register("wait", typeof(WaitAction));
            Register("wait_for_element", typeof(WaitForElementAction));
            Register("assert_visible", typeof(AssertVisibleAction));
            Register("assert_not_visible", typeof(AssertNotVisibleAction));
            Register("assert_text", typeof(AssertTextAction));
            Register("assert_text_contains", typeof(AssertTextContainsAction));
            Register("assert_value", typeof(AssertValueAction));
            Register("assert_bound_value", typeof(AssertBoundValueAction));
            Register("assert_enabled", typeof(AssertEnabledAction));
            Register("assert_disabled", typeof(AssertDisabledAction));
            Register("assert_property", typeof(AssertPropertyAction));
            Register("screenshot", typeof(ScreenshotAction));

            // IMGUI actions (Tier 1 + Tier 2 + Extended)
            Register("imgui_click", typeof(ImguiClickAction));
            Register("imgui_double_click", typeof(ImguiDoubleClickAction));
            Register("imgui_right_click", typeof(ImguiRightClickAction));
            Register("imgui_hover", typeof(ImguiHoverAction));
            Register("imgui_type", typeof(ImguiTypeAction));
            Register("imgui_focus", typeof(ImguiFocusAction));
            Register("imgui_scroll", typeof(ImguiScrollAction));
            Register("imgui_select_option", typeof(ImguiSelectOptionAction));
            Register("imgui_press_key", typeof(ImguiPressKeyAction));
            Register("imgui_press_key_combination", typeof(ImguiPressKeyCombinationAction));
            Register("imgui_read_value", typeof(ImguiReadValueAction));
            Register("imgui_assert_text", typeof(ImguiAssertTextAction));
            Register("imgui_assert_visible", typeof(ImguiAssertVisibleAction));
            Register("imgui_assert_value", typeof(ImguiAssertValueAction));
            Register("imgui_wait", typeof(ImguiWaitAction));
        }

        private void RegisterCustomActions()
        {
            HashSet<string> allowedAssemblies = UnityUIFlowConfigResolver.GetCustomActionAssemblyWhitelist();
            var discoveredTypes = new HashSet<Type>();
            foreach (Type type in GetTypesWithAttribute<ActionNameAttribute>())
            {
                if (type != null && IsAllowedCustomActionType(type, allowedAssemblies))
                {
                    discoveredTypes.Add(type);
                }
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == null || !allowedAssemblies.Contains(assembly.GetName().Name ?? string.Empty))
                {
                    continue;
                }

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    if (type != null && IsAllowedCustomActionType(type, allowedAssemblies) && type.GetCustomAttribute<ActionNameAttribute>() != null)
                    {
                        discoveredTypes.Add(type);
                    }
                }
            }

            foreach (Type type in discoveredTypes)
            {
                if (!typeof(IAction).IsAssignableFrom(type))
                {
                    continue;
                }

                var attribute = type.GetCustomAttribute<ActionNameAttribute>();
                if (attribute == null || string.IsNullOrWhiteSpace(attribute.ActionName) || _actions.ContainsKey(attribute.ActionName))
                {
                    continue;
                }

                _actions[attribute.ActionName] = type;
            }
        }

        private static bool IsAllowedCustomActionType(Type type, HashSet<string> allowedAssemblies)
        {
            string assemblyName = type?.Assembly?.GetName().Name;
            return !string.IsNullOrWhiteSpace(assemblyName) && allowedAssemblies.Contains(assemblyName);
        }
    }

    public static class ActionHelpers
    {
        public static string Require(Dictionary<string, string> parameters, string actionName, string key)
        {
            if (!parameters.TryGetValue(key, out string value))
            {
                throw new UnityUIFlowException(ErrorCodes.ActionParameterMissing, $"Action {actionName} is missing parameter {key}.");
            }

            return value;
        }

        public static async Task<VisualElement> RequireElementAsync(ActionContext context, Dictionary<string, string> parameters, string actionName)
        {
            string selector = Require(parameters, actionName, "selector");
            context.Log($"{actionName}: waiting for {selector}");
            var compiledSelector = new SelectorCompiler().Compile(selector);
            FindResult result = await context.Finder.WaitForElementAsync(
                compiledSelector,
                context.Root,
                new WaitOptions
                {
                    TimeoutMs = parameters.TryGetValue("timeout", out string timeoutLiteral)
                        ? DurationParser.ParseToMilliseconds(timeoutLiteral, actionName)
                        : context.Options.DefaultTimeoutMs,
                    PollIntervalMs = 16,
                    RequireVisible = true,
                },
                context.CancellationToken);

            context.Log($"{actionName}: found {ActionContext.ElementInfo(result.Element)}");
            return result.Element;
        }

        public static string GetText(VisualElement element)
        {
            switch (element)
            {
                case TextElement textElement:
                    return textElement.text;
                case HelpBox helpBox:
                    return helpBox.text;
                default:
                    PropertyInfo valueProperty = element.GetType().GetProperty("value");
                    if (valueProperty != null)
                    {
                        object value = valueProperty.GetValue(element);
                        return value?.ToString() ?? string.Empty;
                    }

                    return string.Empty;
            }
        }

        public static string GetValueText(VisualElement element)
        {
            if (AdvancedActionHelpers.TryReadValueAsString(element, out string valueText))
            {
                return valueText;
            }

            return GetText(element);
        }

        public static bool TryAssignFieldValue(VisualElement element, string value)
        {
            switch (element)
            {
                case Label _:
                    return false;
                case TextField textField:
                    textField.value = value;
                    return true;
                case IntegerField integerField when int.TryParse(value, out int intValue):
                    integerField.value = intValue;
                    return true;
                case FloatField floatField when float.TryParse(value, out float floatValue):
                    floatField.value = floatValue;
                    return true;
                case LongField longField when long.TryParse(value, out long longValue):
                    longField.value = longValue;
                    return true;
                case DoubleField doubleField when double.TryParse(value, out double doubleValue):
                    doubleField.value = doubleValue;
                    return true;
            }

            if (AdvancedActionHelpers.TryAssignFieldValue(element, value))
            {
                return true;
            }

            PropertyInfo property = element.GetType().GetProperty("value");
            if (property != null && property.CanWrite)
            {
                try
                {
                    if (!AdvancedActionHelpers.TryConvertStringValue(value, property.PropertyType, out object converted))
                    {
                        return false;
                    }

                    property.SetValue(element, converted);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        public static string ResolvePointerDriver(ActionContext context)
        {
            if (context?.SimulationSession == null)
            {
                return "UIToolkitFallbackOnly";
            }

            ResolveHostDriver(context);
            string driver = context.SimulationSession.PointerDriverName;
            context.SharedBag["inputDriver.pointer"] = driver;
            context.SharedBag["officialUiToolkit.describe"] = context.SimulationSession.OfficialUiToolkit.Describe();
            context.SharedBag["driver.binding.summary"] = context.SimulationSession.DescribeDrivers();
            return driver;
        }

        public static string ResolveHostDriver(ActionContext context)
        {
            string host = context?.SimulationSession?.HostDriverName ?? "RootOverrideOnly";
            if (context != null)
            {
                context.SharedBag["inputDriver.host"] = host;
            }

            return host;
        }

        public static string ResolveKeyboardDriver(ActionContext context)
        {
            string driver = context?.SimulationSession?.KeyboardDriverName ?? "UIToolkitFallbackOnly";
            if (context != null)
            {
                ResolveHostDriver(context);
                context.SharedBag["inputDriver.keyboard"] = driver;
                context.SharedBag["officialUiToolkit.describe"] = context.SimulationSession?.OfficialUiToolkit.Describe() ?? "unavailable";
                context.SharedBag["driver.binding.summary"] = context.SimulationSession?.DescribeDrivers() ?? $"host={ResolveHostDriver(context)}; pointer=UIToolkitFallbackOnly; keyboard={driver}; official=unavailable";
            }

            return driver;
        }

        public static void RequireOfficialPointerDriver(ActionContext context, string actionName)
        {
            if (context?.Options?.RequireOfficialPointerDriver != true)
            {
                return;
            }

            if (context?.SimulationSession?.HasExecutableOfficialPointerDriver == true)
            {
                return;
            }

            throw new UnityUIFlowException(
                ErrorCodes.OfficialUiTestFrameworkUnavailable,
                $"com.unity.test-framework UI 测试子系统不可用，动作 {actionName} 无法执行");
        }

        public static MouseButton ParseMouseButton(Dictionary<string, string> parameters, string actionName, string key = "button", MouseButton defaultValue = MouseButton.LeftMouse)
        {
            if (parameters == null || !parameters.TryGetValue(key, out string literal) || string.IsNullOrWhiteSpace(literal))
            {
                return defaultValue;
            }

            switch (literal.Trim().ToLowerInvariant())
            {
                case "left":
                case "leftmouse":
                case "0":
                    return MouseButton.LeftMouse;
                case "right":
                case "rightmouse":
                case "1":
                    return MouseButton.RightMouse;
                case "middle":
                case "middlemouse":
                case "2":
                    return MouseButton.MiddleMouse;
                default:
                    throw new UnityUIFlowException(ErrorCodes.ActionParameterInvalid, $"Action {actionName} parameter '{key}' is invalid: {literal}");
            }
        }

        public static EventModifiers ParseEventModifiers(Dictionary<string, string> parameters, string actionName, string key = "modifiers")
        {
            if (parameters == null || !parameters.TryGetValue(key, out string literal) || string.IsNullOrWhiteSpace(literal))
            {
                return EventModifiers.None;
            }

            EventModifiers modifiers = EventModifiers.None;
            string[] parts = literal.Split(new[] { ',', '+', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                switch (part.Trim().ToLowerInvariant())
                {
                    case "none":
                        modifiers = EventModifiers.None;
                        break;
                    case "shift":
                        modifiers |= EventModifiers.Shift;
                        break;
                    case "ctrl":
                    case "control":
                        modifiers |= EventModifiers.Control;
                        break;
                    case "alt":
                        modifiers |= EventModifiers.Alt;
                        break;
                    case "cmd":
                    case "command":
                    case "meta":
                        modifiers |= EventModifiers.Command;
                        break;
                    default:
                        throw new UnityUIFlowException(ErrorCodes.ActionParameterInvalid, $"Action {actionName} parameter '{key}' is invalid: {part}");
                }
            }

            return modifiers;
        }

        public static string RequireMenuItem(Dictionary<string, string> parameters, string actionName)
        {
            if (parameters.TryGetValue("item", out string itemName) && !string.IsNullOrWhiteSpace(itemName))
            {
                return itemName;
            }

            if (parameters.TryGetValue("value", out string value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            throw new UnityUIFlowException(ErrorCodes.ActionParameterMissing, $"Action {actionName} is missing parameter item.");
        }

        public static void DispatchClick(VisualElement element, int clickCount, MouseButton button, EventModifiers modifiers, ActionContext context = null)
        {
            var dispatchRoot = element?.panel?.visualTree ?? element;
            if (dispatchRoot == null)
            {
                throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, "Click target is unavailable.");
            }

            // worldPos is in panel/screen coordinates — required for correct hit-testing when the
            // panel dispatches the event. We intentionally dispatch through the panel visual tree so
            // UI Toolkit can perform normal picking/compatibility-event generation.
            Vector2 worldPos = element.worldBound.center;
            Vector2 localPos = element.WorldToLocal(worldPos);
            context?.Log($"click: focus {ActionContext.ElementInfo(element)} local={localPos} world={worldPos} button={button} modifiers={modifiers}");
            element.Focus();

            for (int index = 0; index < clickCount; index++)
            {
                int currentClickCount = index + 1;
                bool pointerDownReceived = false;
                bool pointerUpReceived = false;
                bool mouseDownReceived = false;
                bool mouseUpReceived = false;
                bool clickEventReceived = false;
                bool clickPropagationStopped = false;

                void OnPointerDown(PointerDownEvent evt) { pointerDownReceived = true; }
                void OnPointerUp(PointerUpEvent evt) { pointerUpReceived = true; }
                void OnMouseDown(MouseDownEvent evt) { mouseDownReceived = true; }
                void OnMouseUp(MouseUpEvent evt) { mouseUpReceived = true; }
                void OnClickEvt(ClickEvent evt) { clickEventReceived = true; clickPropagationStopped = evt.isPropagationStopped; }

                element.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
                element.RegisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
                element.RegisterCallback<MouseDownEvent>(OnMouseDown, TrickleDown.TrickleDown);
                element.RegisterCallback<MouseUpEvent>(OnMouseUp, TrickleDown.TrickleDown);
                element.RegisterCallback<ClickEvent>(OnClickEvt, TrickleDown.TrickleDown);

                var imgui = new UnityEngine.Event
                {
                    type = EventType.MouseDown,
                    mousePosition = worldPos,
                    button = (int)button,
                    clickCount = currentClickCount,
                    modifiers = modifiers,
                };
                using (PointerDownEvent pointerDown = PointerDownEvent.GetPooled(imgui))
                {
                    dispatchRoot.SendEvent(pointerDown);
                }

                imgui = new UnityEngine.Event
                {
                    type = EventType.MouseUp,
                    mousePosition = worldPos,
                    button = (int)button,
                    clickCount = currentClickCount,
                    modifiers = modifiers,
                };
                using (PointerUpEvent pointerUp = PointerUpEvent.GetPooled(imgui))
                {
                    dispatchRoot.SendEvent(pointerUp);
                }

                element.UnregisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
                element.UnregisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
                element.UnregisterCallback<MouseDownEvent>(OnMouseDown, TrickleDown.TrickleDown);
                element.UnregisterCallback<MouseUpEvent>(OnMouseUp, TrickleDown.TrickleDown);
                element.UnregisterCallback<ClickEvent>(OnClickEvt, TrickleDown.TrickleDown);

                if (context != null)
                {
                    string pointerStatus = pointerDownReceived && pointerUpReceived
                        ? "PointerDown+Up 均已接收"
                        : pointerDownReceived
                            ? "PointerDown 已接收，PointerUp 未接收"
                            : "PointerDown 未接收（元素未响应指针事件）";
                    string mouseStatus = mouseDownReceived && mouseUpReceived
                        ? "MouseDown+Up 均已接收"
                        : mouseDownReceived
                            ? "MouseDown 已接收，MouseUp 未接收"
                            : "MouseDown 未接收（未生成兼容鼠标事件）";
                    string clickStatus = clickEventReceived
                        ? clickPropagationStopped
                            ? "ClickEvent 已触发（传播已停止，处理器已响应）"
                            : "ClickEvent 已触发（传播继续，无处理器消费）"
                        : "ClickEvent 未触发（Clickable 未响应）";
                    context.Log($"click[{currentClickCount}/{clickCount}]: {pointerStatus}  |  {mouseStatus}  |  {clickStatus}");
                }

                if (!pointerDownReceived && !mouseDownReceived)
                {
                    // For plain containers without interactive handlers, missing pointer/mouse events is expected
                    // (e.g. Box with pickingMode=Ignore). Only throw for elements that should be interactive.
                    bool hasInteractiveCapability = false;
                    var clickableProp = element.GetType().GetProperty("clickable", BindingFlags.Public | BindingFlags.Instance);
                    if (clickableProp != null)
                    {
                        hasInteractiveCapability = clickableProp.GetValue(element) != null;
                    }
                    if (hasInteractiveCapability)
                    {
                        // In com.unity.ui 2.0.0, generated pointer events may miss the target inside some
                        // EditorWindow layouts. Fallback to direct callback invocation.
                        if (element is Button btn && TryInvokeButtonDirectly(btn, context))
                        {
                            continue;
                        }
                        throw new UnityUIFlowException(
                            ErrorCodes.ActionExecutionFailed,
                            $"click failed: {ActionContext.ElementInfo(element)} did not receive pointer or mouse down.");
                    }
                    context?.Log($"click: allowing non-interactive element {ActionContext.ElementInfo(element)} (clickable=null) without event receipt");
                }

                if (!pointerUpReceived && !mouseUpReceived)
                {
                    throw new UnityUIFlowException(
                        ErrorCodes.ActionExecutionFailed,
                        $"click failed: {ActionContext.ElementInfo(element)} did not receive pointer or mouse up.");
                }
            }
        }

        public static void DispatchClickAt(VisualElement element, Vector2 worldPos, int clickCount, MouseButton button, EventModifiers modifiers, ActionContext context = null)
        {
            var dispatchRoot = element?.panel?.visualTree ?? element;
            if (dispatchRoot == null)
            {
                throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, "Click target is unavailable.");
            }

            Vector2 localPos = element.WorldToLocal(worldPos);
            context?.Log($"click: focus {ActionContext.ElementInfo(element)} local={localPos} world={worldPos} button={button} modifiers={modifiers}");
            element.Focus();

            for (int index = 0; index < clickCount; index++)
            {
                int currentClickCount = index + 1;

                var imgui = new UnityEngine.Event
                {
                    type = EventType.MouseDown,
                    mousePosition = worldPos,
                    button = (int)button,
                    clickCount = currentClickCount,
                    modifiers = modifiers,
                };
                using (PointerDownEvent pointerDown = PointerDownEvent.GetPooled(imgui))
                {
                    dispatchRoot.SendEvent(pointerDown);
                }

                imgui = new UnityEngine.Event
                {
                    type = EventType.MouseUp,
                    mousePosition = worldPos,
                    button = (int)button,
                    clickCount = currentClickCount,
                    modifiers = modifiers,
                };
                using (PointerUpEvent pointerUp = PointerUpEvent.GetPooled(imgui))
                {
                    dispatchRoot.SendEvent(pointerUp);
                }
            }
        }

        private static bool TryInvokeButtonDirectly(Button button, ActionContext context)
        {
            if (button == null)
                return false;

            // 1) Try Button.clicked delegate directly
            try
            {
                var clickedField = typeof(Button).GetField("clicked", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (clickedField != null)
                {
                    var del = clickedField.GetValue(button) as System.Delegate;
                    if (del != null)
                    {
                        del.DynamicInvoke();
                        context?.Log($"click: invoked Button.clicked directly for {ActionContext.ElementInfo(button)}");
                        return true;
                    }
                }
            }
            catch { }

            // 2) Try Clickable.clicked delegate via reflection (com.unity.ui 1.x)
            try
            {
                var clickableProp = button.GetType().GetProperty("clickable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object clickable = clickableProp?.GetValue(button);
                if (clickable != null)
                {
                    var clickedField = clickable.GetType().GetField("clicked", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (clickedField != null)
                    {
                        var del = clickedField.GetValue(clickable) as System.Delegate;
                        if (del != null)
                        {
                            del.DynamicInvoke();
                            context?.Log($"click: invoked Clickable.clicked directly for {ActionContext.ElementInfo(button)}");
                            return true;
                        }
                    }
                }
            }
            catch { }

            // 3) Dispatch a synthetic ClickEvent
            try
            {
                button.SendEvent(ClickEvent.GetPooled());
                context?.Log($"click: dispatched synthetic ClickEvent to {ActionContext.ElementInfo(button)}");
                return true;
            }
            catch { }

            return false;
        }

        public static void DispatchKeyboardEvent(VisualElement target, EventType eventType, KeyCode keyCode, char character = '\0')
        {
            var dispatchRoot = target?.panel?.visualTree ?? target;
            if (dispatchRoot == null)
            {
                throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, "Keyboard event target is unavailable.");
            }

            var imguiEvent = new UnityEngine.Event
            {
                type = eventType,
                keyCode = keyCode,
                character = character == '\0' ? ToCharacter(keyCode) : character,
            };

            using (var keyboardEvent = eventType == EventType.KeyDown
                ? (EventBase)KeyDownEvent.GetPooled(imguiEvent)
                : KeyUpEvent.GetPooled(imguiEvent))
            {
                keyboardEvent.target = target;
                dispatchRoot.SendEvent(keyboardEvent);
            }
        }

        public static void DispatchCommandEvent(VisualElement target, EventType eventType, string commandName)
        {
            if (target == null)
            {
                throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, "Command event target is unavailable.");
            }

            if (string.IsNullOrWhiteSpace(commandName))
            {
                throw new UnityUIFlowException(ErrorCodes.ActionParameterMissing, "Command name cannot be empty.");
            }

            var imguiEvent = new UnityEngine.Event
            {
                type = eventType,
                commandName = commandName,
            };

            EventBase commandEvent;
            switch (eventType)
            {
                case EventType.ExecuteCommand:
                    commandEvent = ExecuteCommandEvent.GetPooled(imguiEvent);
                    break;
                case EventType.ValidateCommand:
                    commandEvent = ValidateCommandEvent.GetPooled(imguiEvent);
                    break;
                default:
                    throw new UnityUIFlowException(ErrorCodes.ActionParameterInvalid, $"Unsupported command event type: {eventType}");
            }

            using (commandEvent)
            {
                // Send directly on target; VisualElement.SendEvent will set evt.target to this
                // and still perform full propagation (capture/target/bubble) through the panel.
                target.SendEvent(commandEvent);
            }
        }

        public static bool TrySimulateKeyWithInputSystem(ActionContext context, KeyCode keyCode)
        {
            if (context?.SimulationSession == null || !TryMapToInputSystemKey(keyCode, out InputSystemKey inputSystemKey))
            {
                ResolveKeyboardDriver(context);
                return false;
            }

            context.SimulationSession.EnsureInputSystemReady();
            context.SimulationSession.PressKey(inputSystemKey);
            ResolveKeyboardDriver(context);
            return true;
        }

        public static bool TrySimulateTextWithInputSystem(ActionContext context, char character)
        {
            if (context?.SimulationSession == null)
            {
                ResolveKeyboardDriver(context);
                return false;
            }

            context.SimulationSession.EnsureInputSystemReady();
            context.SimulationSession.SendText(character.ToString());
            ResolveKeyboardDriver(context);
            return true;
        }

        public static void DispatchMouseEvent(VisualElement target, EventType eventType, Vector2 mousePosition, Vector2 delta, int button = 0, int clickCount = 1, EventModifiers modifiers = EventModifiers.None)
        {
            var dispatchRoot = target?.panel?.visualTree ?? target;
            if (dispatchRoot == null)
            {
                throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, "Mouse event target is unavailable.");
            }

            var imguiEvent = new UnityEngine.Event
            {
                type = eventType,
                mousePosition = mousePosition,
                delta = delta,
                button = button,
                clickCount = clickCount,
                modifiers = modifiers,
            };

            EventBase evt;
            switch (eventType)
            {
                case EventType.MouseDown:
                    evt = MouseDownEvent.GetPooled(imguiEvent);
                    break;
                case EventType.MouseUp:
                    evt = MouseUpEvent.GetPooled(imguiEvent);
                    break;
                case EventType.MouseMove:
                    evt = MouseMoveEvent.GetPooled(imguiEvent);
                    break;
                default:
                    throw new UnityUIFlowException(ErrorCodes.ActionParameterInvalid, $"Unsupported mouse event type: {eventType}");
            }

            using (evt)
            {
                evt.target = target;
                dispatchRoot.SendEvent(evt);
            }
        }

        public static void DispatchWheelEvent(VisualElement target, Vector2 mousePosition, Vector2 delta)
        {
            var dispatchRoot = target?.panel?.visualTree ?? target;
            if (dispatchRoot == null)
            {
                throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, "Wheel event target is unavailable.");
            }

            var imguiEvent = new UnityEngine.Event
            {
                type = EventType.ScrollWheel,
                mousePosition = mousePosition,
                delta = delta,
            };

            using (WheelEvent wheelEvent = WheelEvent.GetPooled(imguiEvent))
            {
                wheelEvent.target = target;
                dispatchRoot.SendEvent(wheelEvent);
            }
        }

        private static char ToCharacter(KeyCode keyCode)
        {
            if (keyCode >= KeyCode.A && keyCode <= KeyCode.Z)
            {
                return (char)('A' + (keyCode - KeyCode.A));
            }

            if (keyCode >= KeyCode.Alpha0 && keyCode <= KeyCode.Alpha9)
            {
                return (char)('0' + (keyCode - KeyCode.Alpha0));
            }

            return '\0';
        }

        private static bool TryMapToInputSystemKey(KeyCode keyCode, out InputSystemKey inputSystemKey)
        {
            inputSystemKey = default;

            if (keyCode >= KeyCode.A && keyCode <= KeyCode.Z)
            {
                inputSystemKey = (InputSystemKey)((int)InputSystemKey.A + (keyCode - KeyCode.A));
                return true;
            }

            if (keyCode >= KeyCode.Alpha0 && keyCode <= KeyCode.Alpha9)
            {
                inputSystemKey = (InputSystemKey)((int)InputSystemKey.Digit0 + (keyCode - KeyCode.Alpha0));
                return true;
            }

            switch (keyCode)
            {
                case KeyCode.Space:
                    inputSystemKey = InputSystemKey.Space;
                    return true;
                case KeyCode.Tab:
                    inputSystemKey = InputSystemKey.Tab;
                    return true;
                case KeyCode.Return:
                    inputSystemKey = InputSystemKey.Enter;
                    return true;
                case KeyCode.KeypadEnter:
                    inputSystemKey = InputSystemKey.NumpadEnter;
                    return true;
                case KeyCode.Backspace:
                    inputSystemKey = InputSystemKey.Backspace;
                    return true;
                case KeyCode.Escape:
                    inputSystemKey = InputSystemKey.Escape;
                    return true;
                case KeyCode.Delete:
                    inputSystemKey = InputSystemKey.Delete;
                    return true;
                case KeyCode.Insert:
                    inputSystemKey = InputSystemKey.Insert;
                    return true;
                case KeyCode.Home:
                    inputSystemKey = InputSystemKey.Home;
                    return true;
                case KeyCode.End:
                    inputSystemKey = InputSystemKey.End;
                    return true;
                case KeyCode.PageUp:
                    inputSystemKey = InputSystemKey.PageUp;
                    return true;
                case KeyCode.PageDown:
                    inputSystemKey = InputSystemKey.PageDown;
                    return true;
                case KeyCode.UpArrow:
                    inputSystemKey = InputSystemKey.UpArrow;
                    return true;
                case KeyCode.DownArrow:
                    inputSystemKey = InputSystemKey.DownArrow;
                    return true;
                case KeyCode.LeftArrow:
                    inputSystemKey = InputSystemKey.LeftArrow;
                    return true;
                case KeyCode.RightArrow:
                    inputSystemKey = InputSystemKey.RightArrow;
                    return true;
                case KeyCode.LeftShift:
                    inputSystemKey = InputSystemKey.LeftShift;
                    return true;
                case KeyCode.RightShift:
                    inputSystemKey = InputSystemKey.RightShift;
                    return true;
                case KeyCode.LeftControl:
                    inputSystemKey = InputSystemKey.LeftCtrl;
                    return true;
                case KeyCode.RightControl:
                    inputSystemKey = InputSystemKey.RightCtrl;
                    return true;
                case KeyCode.LeftAlt:
                    inputSystemKey = InputSystemKey.LeftAlt;
                    return true;
                case KeyCode.RightAlt:
                    inputSystemKey = InputSystemKey.RightAlt;
                    return true;
                default:
                    return false;
            }
        }

        public static bool TryMapCharacterToKeyCode(char character, out KeyCode keyCode)
        {
            keyCode = KeyCode.None;

            if (character >= 'a' && character <= 'z')
            {
                keyCode = (KeyCode)((int)KeyCode.A + (character - 'a'));
                return true;
            }

            if (character >= 'A' && character <= 'Z')
            {
                keyCode = (KeyCode)((int)KeyCode.A + (character - 'A'));
                return true;
            }

            if (character >= '0' && character <= '9')
            {
                keyCode = (KeyCode)((int)KeyCode.Alpha0 + (character - '0'));
                return true;
            }

            switch (character)
            {
                case ' ':
                    keyCode = KeyCode.Space;
                    return true;
                case '\t':
                    keyCode = KeyCode.Tab;
                    return true;
                case '\n':
                case '\r':
                    keyCode = KeyCode.Return;
                    return true;
                default:
                    return false;
            }
        }

        public static DropdownMenuAction.Status GetDropdownMenuActionStatus(DropdownMenuAction action)
        {
            if (action == null)
                return DropdownMenuAction.Status.None;
            var callback = typeof(DropdownMenuAction).GetField("actionStatusCallback", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(action)
                as System.Func<DropdownMenuAction, DropdownMenuAction.Status>;
            if (callback != null)
            {
                try { return callback(action); } catch { }
            }
            return action.status;
        }

        public static DropdownMenu TryGetDropdownMenu(VisualElement element)
        {
            if (element is UnityEditor.UIElements.ToolbarMenu toolbarMenu)
                return toolbarMenu.menu;
            return null;
        }

        public static bool TryFindDropdownMenuAction(VisualElement element, string itemName, out DropdownMenuAction action)
        {
            action = null;
            DropdownMenu menu = TryGetDropdownMenu(element);
            if (menu == null || string.IsNullOrWhiteSpace(itemName))
                return false;

            foreach (var item in menu.MenuItems())
            {
                if (item is DropdownMenuAction a && a.name == itemName)
                {
                    action = a;
                    return true;
                }
            }
            return false;
        }

        public static bool TryExecuteDropdownMenuItem(VisualElement element, string itemName)
        {
            if (!TryFindDropdownMenuAction(element, itemName, out DropdownMenuAction action))
                return false;

            action.Execute();
            return true;
        }
    }

    public sealed class ClickAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "click");
            MouseButton button = ActionHelpers.ParseMouseButton(parameters, "click");
            EventModifiers modifiers = ActionHelpers.ParseEventModifiers(parameters, "click");
            ActionHelpers.RequireOfficialPointerDriver(context, "click");
            context.Log($"click: dispatch to {ActionContext.ElementInfo(element)} at {element.worldBound.center} via {ActionHelpers.ResolvePointerDriver(context)} button={button} modifiers={modifiers}");
            if (context?.SimulationSession?.PointerDriver != null)
            {
                context.SimulationSession.PointerDriver.Click(element, 1, button, modifiers, context);
            }
            else
            {
                ActionHelpers.DispatchClick(element, 1, button, modifiers, context);
            }

            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);

            // Support clicking a menu item through generic click when menu_item is provided.
            if (parameters.TryGetValue("menu_item", out string menuItemName) && !string.IsNullOrWhiteSpace(menuItemName))
            {
                bool selected = false;

                // Try official PopupMenuSimulator first
                if (context?.SimulationSession != null)
                {
                    selected = context.SimulationSession.TrySelectPopupMenuItem(menuItemName, context)
                        || context.SimulationSession.TrySelectContextMenuItem(menuItemName, context);
                }

                // Fallback to DropdownMenu reflection
                if (!selected)
                {
                    selected = ActionHelpers.TryExecuteDropdownMenuItem(element, menuItemName);
                }

                if (selected)
                {
                    context.Log($"click: selected menu item '{menuItemName}'");
                    await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
                }
                else
                {
                    context.Log($"click: menu item '{menuItemName}' was not available");
                }
            }
        }
    }

    public sealed class DoubleClickAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "double_click");
            MouseButton button = ActionHelpers.ParseMouseButton(parameters, "double_click");
            EventModifiers modifiers = ActionHelpers.ParseEventModifiers(parameters, "double_click");
            ActionHelpers.RequireOfficialPointerDriver(context, "double_click");
            context.Log($"double_click: dispatch to {ActionContext.ElementInfo(element)} at {element.worldBound.center} via {ActionHelpers.ResolvePointerDriver(context)} button={button} modifiers={modifiers}");
            if (context?.SimulationSession?.PointerDriver != null)
            {
                context.SimulationSession.PointerDriver.Click(element, 2, button, modifiers, context);
            }
            else
            {
                ActionHelpers.DispatchClick(element, 2, button, modifiers, context);
            }

            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class TypeTextAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "type_text");
            string value = ActionHelpers.Require(parameters, "type_text", "value");
            context.Log($"type_text: writing {value.Length} chars to {ActionContext.ElementInfo(element)}");

            if (element is Label)
            {
                throw new UnityUIFlowException(ErrorCodes.ActionTargetTypeInvalid, $"type_text target type is not writable: {element.GetType().Name}");
            }

            element.Focus();
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);

            if (context?.SimulationSession?.TryTypeTextWithOfficialDriver(element, value, context) == true)
            {
                context.Log($"type_text: final value \"{value}\" via {ActionHelpers.ResolveKeyboardDriver(context)}");
                return;
            }

            string current = ActionHelpers.GetValueText(element);
            bool allowDirectWriteCompensation = context?.Options?.RequireInputSystemKeyboardDriver != true;
            context?.SimulationSession?.MarkKeyboardFallback();
            foreach (char ch in value)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                ActionHelpers.TrySimulateTextWithInputSystem(context, ch);

                if (ActionHelpers.TryMapCharacterToKeyCode(ch, out KeyCode keyCode))
                {
                    ActionHelpers.DispatchKeyboardEvent(element, EventType.KeyDown, keyCode, ch);
                    ActionHelpers.DispatchKeyboardEvent(element, EventType.KeyUp, keyCode, ch);
                }

                string expected = current + ch;
                string actual = ActionHelpers.GetValueText(element);
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    if (!allowDirectWriteCompensation)
                    {
                        throw new UnityUIFlowException(
                            ErrorCodes.ActionExecutionFailed,
                            "动作 type_text 在高保真模式下禁止回退到直接写值实现");
                    }

                    if (!ActionHelpers.TryAssignFieldValue(element, expected))
                    {
                        throw new UnityUIFlowException(ErrorCodes.ActionTargetTypeInvalid, $"type_text target type is not writable: {element.GetType().Name}");
                    }
                }

                current = ActionHelpers.GetValueText(element);
                await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
            }

            context.Log($"type_text: final value \"{value}\" via {ActionHelpers.ResolveKeyboardDriver(context)}");
        }
    }

    public sealed class TypeTextFastAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "type_text_fast");
            string value = ActionHelpers.Require(parameters, "type_text_fast", "value");
            context.Log($"type_text_fast: setting {ActionContext.ElementInfo(element)} to \"{value}\"");
            if (!ActionHelpers.TryAssignFieldValue(element, value))
            {
                throw new UnityUIFlowException(ErrorCodes.ActionTargetTypeInvalid, $"type_text_fast target type is not writable: {element.GetType().Name}");
            }
        }
    }

    public sealed class PressKeyAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            string key = ActionHelpers.Require(parameters, "press_key", "key");
            if (!Enum.TryParse(key, true, out KeyCode keyCode))
            {
                throw new UnityUIFlowException(ErrorCodes.ActionParameterInvalid, "press_key parameter 'key' is invalid.");
            }

            bool hasExplicitSelector = parameters.TryGetValue("selector", out string selector) && !string.IsNullOrWhiteSpace(selector);
            VisualElement target = hasExplicitSelector
                ? await ActionHelpers.RequireElementAsync(context, parameters, "press_key")
                : root.focusController?.focusedElement as VisualElement ?? root;

            target?.Focus();
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);

            bool canUseOfficialDriver = context?.SimulationSession != null
                && target != null
                && (hasExplicitSelector || !ReferenceEquals(target, root));

            if (canUseOfficialDriver && context.SimulationSession.TryPressKeyWithOfficialDriver(target, keyCode, context))
            {
                context.Log($"press_key: sending {keyCode} to {ActionContext.ElementInfo(target)} via {ActionHelpers.ResolveKeyboardDriver(context)}");
                // PanelSimulator.KeyPress does not trigger UIElements KeyDownEvent for regular keys.
                // Explicitly dispatch KeyDown/KeyUp so RegisterCallback<KeyDownEvent> handlers fire.
                ActionHelpers.DispatchKeyboardEvent(target, EventType.KeyDown, keyCode, '\0');
                ActionHelpers.DispatchKeyboardEvent(target, EventType.KeyUp, keyCode, '\0');
                return;
            }

            bool usedInputSystem = ActionHelpers.TrySimulateKeyWithInputSystem(context, keyCode);
            if (context?.Options?.RequireInputSystemKeyboardDriver == true && !usedInputSystem)
            {
                throw new UnityUIFlowException(
                    ErrorCodes.InputSystemTestFrameworkUnavailable,
                    $"缺少 InputSystem 测试输入能力，动作 press_key 无法执行");
            }

            if (!usedInputSystem)
            {
                context?.SimulationSession?.MarkKeyboardFallback();
            }

            context.Log($"press_key: sending {keyCode} to {ActionContext.ElementInfo(target)} via {(usedInputSystem ? ActionHelpers.ResolveKeyboardDriver(context) : "UIToolkitFallbackOnly")}");
            ActionHelpers.DispatchKeyboardEvent(target, EventType.KeyDown, keyCode);
            ActionHelpers.DispatchKeyboardEvent(target, EventType.KeyUp, keyCode);
        }
    }

    public sealed class PressKeyCombinationAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            string keys = ActionHelpers.Require(parameters, "press_key_combination", "keys");
            var parts = keys.Split('+').Select(p => p.Trim()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (parts.Count < 2)
            {
                throw new UnityUIFlowException(ErrorCodes.ActionParameterInvalid, "press_key_combination parameter 'keys' must contain at least two keys separated by '+'.");
            }

            var modifierMap = new Dictionary<string, EventModifiers>(StringComparer.OrdinalIgnoreCase)
            {
                ["Ctrl"] = EventModifiers.Control,
                ["Control"] = EventModifiers.Control,
                ["Shift"] = EventModifiers.Shift,
                ["Alt"] = EventModifiers.Alt,
                ["Cmd"] = EventModifiers.Command,
                ["Command"] = EventModifiers.Command,
            };

            var modifiers = EventModifiers.None;
            var keyCodes = new List<KeyCode>();
            foreach (string part in parts)
            {
                if (modifierMap.TryGetValue(part, out EventModifiers mod))
                {
                    modifiers |= mod;
                }
                else if (Enum.TryParse(part, true, out KeyCode kc))
                {
                    keyCodes.Add(kc);
                }
                else if (part.Length == 1 && ActionHelpers.TryMapCharacterToKeyCode(part[0], out KeyCode charKc))
                {
                    keyCodes.Add(charKc);
                }
                else
                {
                    throw new UnityUIFlowException(ErrorCodes.ActionParameterInvalid, $"press_key_combination contains invalid key: {part}");
                }
            }

            if (keyCodes.Count != 1)
            {
                throw new UnityUIFlowException(ErrorCodes.ActionParameterInvalid, "press_key_combination must contain exactly one non-modifier key.");
            }

            KeyCode mainKey = keyCodes[0];
            bool hasExplicitSelector = parameters.TryGetValue("selector", out string selector) && !string.IsNullOrWhiteSpace(selector);
            VisualElement target = hasExplicitSelector
                ? await ActionHelpers.RequireElementAsync(context, parameters, "press_key_combination")
                : root.focusController?.focusedElement as VisualElement ?? root;

            target?.Focus();
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);

            context.Log($"press_key_combination: sending {modifiers}+{mainKey} to {ActionContext.ElementInfo(target)}");

            // Dispatch key down for modifiers
            foreach (var modKc in GetModifierKeyCodes(modifiers))
            {
                ActionHelpers.DispatchKeyboardEvent(target, EventType.KeyDown, modKc);
            }

            // Dispatch main key with modifiers
            var imguiEvent = new UnityEngine.Event
            {
                type = EventType.KeyDown,
                keyCode = mainKey,
                modifiers = modifiers,
            };
            using (var evt = KeyDownEvent.GetPooled(imguiEvent))
            {
                evt.target = target;
                (target?.panel?.visualTree ?? target)?.SendEvent(evt);
            }

            // For known shortcut commands (e.g. Ctrl+C), dispatch Validate/ExecuteCommand events
            // so TextField command callbacks fire.
            string commandName = ResolveCommandName(modifiers, mainKey);
            if (!string.IsNullOrEmpty(commandName))
            {
                // For TextField, the inner TextElement is the actual command handler in Unity 6000.
                // Sending to the inner element lets capture-phase callbacks on the TextField fire first.
                VisualElement commandTarget = target;
                if (target is TextField tf)
                {
                    VisualElement innerText = tf.Q<TextElement>();
                    if (innerText != null)
                        commandTarget = innerText;
                }
                ActionHelpers.DispatchCommandEvent(commandTarget, EventType.ValidateCommand, commandName);
                ActionHelpers.DispatchCommandEvent(commandTarget, EventType.ExecuteCommand, commandName);
            }

            // Dispatch main key up
            imguiEvent = new UnityEngine.Event
            {
                type = EventType.KeyUp,
                keyCode = mainKey,
                modifiers = modifiers,
            };
            using (var evt = KeyUpEvent.GetPooled(imguiEvent))
            {
                evt.target = target;
                (target?.panel?.visualTree ?? target)?.SendEvent(evt);
            }

            // Dispatch key up for modifiers in reverse order
            foreach (var modKc in GetModifierKeyCodes(modifiers).Reverse())
            {
                ActionHelpers.DispatchKeyboardEvent(target, EventType.KeyUp, modKc);
            }
        }

        private static string ResolveCommandName(EventModifiers modifiers, KeyCode mainKey)
        {
            bool hasCtrl = (modifiers & EventModifiers.Control) != 0 || (modifiers & EventModifiers.Command) != 0;
            if (!hasCtrl) return null;

            switch (mainKey)
            {
                case KeyCode.C: return "Copy";
                case KeyCode.V: return "Paste";
                case KeyCode.X: return "Cut";
                case KeyCode.A: return "SelectAll";
                case KeyCode.Z: return "Undo";
                case KeyCode.Y: return "Redo";
                default: return null;
            }
        }

        private static IEnumerable<KeyCode> GetModifierKeyCodes(EventModifiers modifiers)
        {
            if ((modifiers & EventModifiers.Control) != 0) yield return KeyCode.LeftControl;
            if ((modifiers & EventModifiers.Shift) != 0) yield return KeyCode.LeftShift;
            if ((modifiers & EventModifiers.Alt) != 0) yield return KeyCode.LeftAlt;
            if ((modifiers & EventModifiers.Command) != 0) yield return KeyCode.LeftCommand;
        }
    }

    public sealed class ExecuteCommandAction : IAction
    {
        public Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            return CommandActionExecutor.ExecuteAsync(root, context, parameters, "execute_command", EventType.ExecuteCommand);
        }
    }

    public sealed class ValidateCommandAction : IAction
    {
        public Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            return CommandActionExecutor.ExecuteAsync(root, context, parameters, "validate_command", EventType.ValidateCommand);
        }
    }

    public static class CommandActionExecutor
    {
        public static async Task ExecuteAsync(
            VisualElement root,
            ActionContext context,
            Dictionary<string, string> parameters,
            string actionName,
            EventType eventType)
        {
            string commandName = ActionHelpers.Require(parameters, actionName, "command");
            VisualElement target = root.focusController?.focusedElement as VisualElement ?? root;

            if (parameters.TryGetValue("selector", out string selector) && !string.IsNullOrWhiteSpace(selector))
            {
                target = await ActionHelpers.RequireElementAsync(context, parameters, actionName);
            }

            target?.Focus();
            context.Log($"{actionName}: sending {commandName} to {ActionContext.ElementInfo(target)} via {ActionHelpers.ResolveHostDriver(context)}");
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);

            bool usedOfficialDriver = eventType == EventType.ExecuteCommand
                ? context?.SimulationSession?.TryExecuteCommandWithOfficialDriver(target, commandName, context) == true
                : context?.SimulationSession?.TryValidateCommandWithOfficialDriver(target, commandName, context) == true;

            // In com.unity.ui 2.0.0, PanelSimulator.ExecuteCommand may generate an event that does not reach
            // the focused element correctly. Always dispatch the compatibility event as a safeguard.
            if (!usedOfficialDriver || eventType == EventType.ExecuteCommand)
            {
                ActionHelpers.DispatchCommandEvent(target, eventType, commandName);
            }

            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class DragAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            string from = ActionHelpers.Require(parameters, "drag", "from");
            string to = ActionHelpers.Require(parameters, "drag", "to");
            MouseButton button = ActionHelpers.ParseMouseButton(parameters, "drag");
            EventModifiers modifiers = ActionHelpers.ParseEventModifiers(parameters, "drag");
            int delayMs = parameters.TryGetValue("duration", out string duration)
                ? DurationParser.ParseToMilliseconds(duration, "drag")
                : 100;

            context.Log($"drag: resolve from {from}");
            Vector2 fromPos = await ResolvePositionAsync(from, root, context);
            context.Log($"drag: resolve to {to}");
            Vector2 toPos = await ResolvePositionAsync(to, root, context);
            int frameCount = Math.Max(1, delayMs / 16);
            ActionHelpers.RequireOfficialPointerDriver(context, "drag");
            context.Log($"drag: {fromPos} -> {toPos} in {delayMs}ms across {frameCount} frames via {ActionHelpers.ResolvePointerDriver(context)} button={button} modifiers={modifiers}");

            VisualElement fromElement = TryResolveElement(from, root, context);
            VisualElement toElement = TryResolveElement(to, root, context);

            if (context?.SimulationSession?.PointerDriver != null)
            {
                await context.SimulationSession.PointerDriver.DragAsync(root, fromPos, toPos, delayMs, frameCount, button, modifiers, context);
            }
            else
            {
                ActionHelpers.DispatchMouseEvent(fromElement ?? root, EventType.MouseDown, fromPos, Vector2.zero, button: (int)button, modifiers: modifiers);

                await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);

                for (int i = 1; i <= frameCount; i++)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    Vector2 prev = Vector2.Lerp(fromPos, toPos, (float)(i - 1) / frameCount);
                    Vector2 pos = Vector2.Lerp(fromPos, toPos, (float)i / frameCount);
                    Vector2 delta = pos - prev;
                    ActionHelpers.DispatchMouseEvent(root, EventType.MouseMove, pos, delta, button: (int)button, modifiers: modifiers);

                    await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
                }

                ActionHelpers.DispatchMouseEvent(toElement ?? root, EventType.MouseUp, toPos, Vector2.zero, button: (int)button, modifiers: modifiers);
            }

            // Ensure compatibility MouseDown/MouseUp and PointerDown/PointerUp events are dispatched.
            // PanelSimulator.DragAndDrop dispatches Pointer events but does not reliably
            // generate legacy mouse events on the drag source when the drag ends on a different target.
            VisualElement pointerDownTarget = fromElement ?? root;
            VisualElement pointerUpTarget = fromElement ?? root;

            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);

            var downImg = new UnityEngine.Event
            {
                type = EventType.MouseDown,
                mousePosition = fromPos,
                delta = Vector2.zero,
                button = (int)button,
                clickCount = 1,
                modifiers = modifiers,
            };
            using (PointerDownEvent pde = PointerDownEvent.GetPooled(downImg))
            {
                pde.target = pointerDownTarget;
                pointerDownTarget.SendEvent(pde);
            }
            using (MouseDownEvent mde = MouseDownEvent.GetPooled(downImg))
            {
                mde.target = pointerDownTarget;
                pointerDownTarget.SendEvent(mde);
            }

            var upImg = new UnityEngine.Event
            {
                type = EventType.MouseUp,
                mousePosition = toPos,
                delta = Vector2.zero,
                button = (int)button,
                clickCount = 1,
                modifiers = modifiers,
            };

            // Dispatch to drop target first so drag-source up events (sent next) "win" any text/status races.
            if (toElement != null && !ReferenceEquals(toElement, fromElement))
            {
                using (PointerUpEvent pueDrop = PointerUpEvent.GetPooled(upImg))
                {
                    pueDrop.target = toElement;
                    toElement.SendEvent(pueDrop);
                }
                using (MouseUpEvent mueDrop = MouseUpEvent.GetPooled(upImg))
                {
                    mueDrop.target = toElement;
                    toElement.SendEvent(mueDrop);
                }
            }

            using (PointerUpEvent pue = PointerUpEvent.GetPooled(upImg))
            {
                pue.target = pointerUpTarget;
                pointerUpTarget.SendEvent(pue);
            }
            using (MouseUpEvent mue = MouseUpEvent.GetPooled(upImg))
            {
                mue.target = pointerUpTarget;
                pointerUpTarget.SendEvent(mue);
            }

            context.Log($"drag: completed {from} -> {to}");
            context.SharedBag["lastDrag"] = $"{from}->{to}";
        }

        private static async Task<Vector2> ResolvePositionAsync(string selectorOrCoord, VisualElement root, ActionContext context)
        {
            if (TryParseCoordinate(selectorOrCoord, out Vector2 coord))
            {
                return coord;
            }

            SelectorExpression compiled = new SelectorCompiler().Compile(selectorOrCoord);
            FindResult result = await context.Finder.WaitForElementAsync(
                compiled,
                root,
                new WaitOptions
                {
                    TimeoutMs = context.Options.DefaultTimeoutMs,
                    PollIntervalMs = 16,
                    RequireVisible = true,
                },
                context.CancellationToken);
            return result.Element.worldBound.center;
        }

        private static VisualElement TryResolveElement(string selectorOrCoord, VisualElement root, ActionContext context)
        {
            if (TryParseCoordinate(selectorOrCoord, out _))
            {
                return null;
            }

            try
            {
                SelectorExpression compiled = new SelectorCompiler().Compile(selectorOrCoord);
                FindResult result = context.Finder.Find(compiled, root, false);
                return result.Element;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryParseCoordinate(string value, out Vector2 result)
        {
            result = Vector2.zero;
            string[] parts = value.Split(',');
            if (parts.Length != 2)
            {
                return false;
            }

            if (int.TryParse(parts[0].Trim(), out int x) && int.TryParse(parts[1].Trim(), out int y) && x >= 0 && y >= 0)
            {
                result = new Vector2(x, y);
                return true;
            }

            return false;
        }
    }

    public sealed class ScrollAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "scroll");
            string delta = ActionHelpers.Require(parameters, "scroll", "delta");
            string[] parts = delta.Split(',');
            if (parts.Length != 2 || !float.TryParse(parts[0], out float dx) || !float.TryParse(parts[1], out float dy))
            {
                throw new UnityUIFlowException(ErrorCodes.ActionParameterInvalid, "scroll parameter 'delta' is invalid.");
            }

            ActionHelpers.RequireOfficialPointerDriver(context, "scroll");
            context.Log($"scroll: {ActionContext.ElementInfo(element)} delta=({dx},{dy}) via {ActionHelpers.ResolvePointerDriver(context)}");
            if (context?.SimulationSession?.PointerDriver != null)
            {
                context.SimulationSession.PointerDriver.Scroll(element, dx, dy, context);
            }
            else if (element is ScrollView scrollView)
            {
                float nextX = scrollView.scrollOffset.x + dx;
                float nextY = scrollView.scrollOffset.y + dy;
                if (Math.Abs(dx) > 0.01f && scrollView.horizontalScroller != null)
                {
                    scrollView.horizontalScroller.value += dx;
                    nextX = scrollView.horizontalScroller.value;
                }

                if (Math.Abs(dy) > 0.01f && scrollView.verticalScroller != null)
                {
                    scrollView.verticalScroller.value += dy;
                    nextY = scrollView.verticalScroller.value;
                }

                scrollView.scrollOffset = new Vector2(nextX, nextY);
                ActionHelpers.DispatchWheelEvent(scrollView, scrollView.worldBound.center, new Vector2(dx, dy));
                context.Log($"scroll: offset is now {scrollView.scrollOffset}");
            }
            else
            {
                ActionHelpers.DispatchWheelEvent(element, element.worldBound.center, new Vector2(dx, dy));
            }
        }
    }

    public sealed class HoverAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "hover");
            Vector2 center = element.worldBound.center;
            EventModifiers modifiers = ActionHelpers.ParseEventModifiers(parameters, "hover");
            ActionHelpers.RequireOfficialPointerDriver(context, "hover");
            context.Log($"hover: {ActionContext.ElementInfo(element)} at {center} via {ActionHelpers.ResolvePointerDriver(context)} modifiers={modifiers}");
            int delay = 0;
            if (parameters.TryGetValue("duration", out string durationLiteral))
            {
                delay = DurationParser.ParseToMilliseconds(durationLiteral, "hover");
            }

            if (context?.SimulationSession?.PointerDriver != null)
            {
                await context.SimulationSession.PointerDriver.HoverAsync(element, center, delay, modifiers, context);
            }
            else
            {
                element.Focus();
                ActionHelpers.DispatchMouseEvent(element, EventType.MouseMove, center, Vector2.zero, 0, 0, modifiers);

                if (delay > 0)
                {
                    context.Log($"hover: wait {delay}ms");
                    await EditorAsyncUtility.DelayAsync(delay, context.CancellationToken);
                }
            }
        }
    }

    public sealed class OpenContextMenuAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "open_context_menu");
            EventModifiers modifiers = ActionHelpers.ParseEventModifiers(parameters, "open_context_menu");
            bool opened = context?.SimulationSession?.TryOpenContextMenu(element, modifiers, context) == true;
            if (!opened && ActionHelpers.TryGetDropdownMenu(element) != null)
            {
                opened = true;
                context?.Log($"open_context_menu: using DropdownMenu fallback for {element.GetType().Name}");
            }
            if (!opened)
            {
                // Synthetic fallback: dispatch right-click mouse events to trigger ContextualMenuManipulator
                Vector2 worldPos = element.worldBound.center;
                ActionHelpers.DispatchMouseEvent(element, EventType.MouseDown, worldPos, Vector2.zero, button: 1, modifiers: modifiers);
                ActionHelpers.DispatchMouseEvent(element, EventType.MouseUp, worldPos, Vector2.zero, button: 1, modifiers: modifiers);
                opened = true;
                context?.Log($"open_context_menu: dispatched synthetic right-click to {ActionContext.ElementInfo(element)}");
            }

            context.Log($"open_context_menu: {ActionContext.ElementInfo(element)} modifiers={modifiers}");
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class SelectContextMenuItemAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            string itemName = ActionHelpers.RequireMenuItem(parameters, "select_context_menu_item");
            bool selected = context?.SimulationSession?.TrySelectContextMenuItem(itemName, context) == true;
            if (!selected && parameters.TryGetValue("selector", out string selector) && !string.IsNullOrWhiteSpace(selector))
            {
                VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "select_context_menu_item");
                selected = ActionHelpers.TryExecuteDropdownMenuItem(element, itemName);
            }
            if (!selected)
            {
                throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, $"Context menu item was not available: {itemName}");
            }

            context.Log($"select_context_menu_item: {itemName}");
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class OpenPopupMenuAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "open_popup_menu");
            EventModifiers modifiers = ActionHelpers.ParseEventModifiers(parameters, "open_popup_menu");
            bool opened = context?.SimulationSession?.TryOpenPopupMenu(element, modifiers, context) == true;
            if (!opened && ActionHelpers.TryGetDropdownMenu(element) != null)
            {
                opened = true;
                context?.Log($"open_popup_menu: using DropdownMenu fallback for {element.GetType().Name}");
            }
            if (!opened)
            {
                throw new UnityUIFlowException(ErrorCodes.OfficialUiTestFrameworkUnavailable, "open_popup_menu requires official PopupMenuSimulator support.");
            }

            context.Log($"open_popup_menu: {ActionContext.ElementInfo(element)} modifiers={modifiers}");
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class SelectPopupMenuItemAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            string itemName = ActionHelpers.RequireMenuItem(parameters, "select_popup_menu_item");
            bool selected = context?.SimulationSession?.TrySelectPopupMenuItem(itemName, context) == true;
            if (!selected && parameters.TryGetValue("selector", out string selector) && !string.IsNullOrWhiteSpace(selector))
            {
                VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "select_popup_menu_item");
                selected = ActionHelpers.TryExecuteDropdownMenuItem(element, itemName);
            }
            if (!selected)
            {
                throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, $"Popup menu item was not available: {itemName}");
            }

            context.Log($"select_popup_menu_item: {itemName}");
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class AssertMenuItemAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            string itemName = ActionHelpers.RequireMenuItem(parameters, "assert_menu_item");
            bool passed = context?.SimulationSession?.TryAssertMenuItem(itemName, expectDisabled: false, context) == true;
            if (!passed && parameters.TryGetValue("selector", out string selector) && !string.IsNullOrWhiteSpace(selector))
            {
                VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "assert_menu_item");
                if (ActionHelpers.TryFindDropdownMenuAction(element, itemName, out DropdownMenuAction action))
                {
                    passed = ActionHelpers.GetDropdownMenuActionStatus(action) != DropdownMenuAction.Status.Disabled;
                }
            }
            if (!passed)
            {
                throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, $"Menu item was not available or not enabled: {itemName}");
            }

            context.Log($"assert_menu_item: {itemName}");
        }
    }

    public sealed class AssertMenuItemDisabledAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            string itemName = ActionHelpers.RequireMenuItem(parameters, "assert_menu_item_disabled");
            bool passed = context?.SimulationSession?.TryAssertMenuItem(itemName, expectDisabled: true, context) == true;
            if (!passed && parameters.TryGetValue("selector", out string selector) && !string.IsNullOrWhiteSpace(selector))
            {
                VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "assert_menu_item_disabled");
                if (ActionHelpers.TryFindDropdownMenuAction(element, itemName, out DropdownMenuAction action))
                {
                    passed = ActionHelpers.GetDropdownMenuActionStatus(action) == DropdownMenuAction.Status.Disabled;
                }
            }
            if (!passed)
            {
                throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, $"Menu item was not available or not disabled: {itemName}");
            }

            context.Log($"assert_menu_item_disabled: {itemName}");
        }
    }

    public sealed class MenuItemAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            parameters.TryGetValue("mode", out string modeLiteral);
            parameters.TryGetValue("menu", out string menuLiteral);
            string mode = string.IsNullOrWhiteSpace(modeLiteral) ? (string.IsNullOrWhiteSpace(menuLiteral) ? "select" : menuLiteral) : modeLiteral;
            string itemName = ActionHelpers.RequireMenuItem(parameters, "menu_item");
            string menuKind = parameters.TryGetValue("kind", out string kindLiteral) && !string.IsNullOrWhiteSpace(kindLiteral)
                ? kindLiteral.Trim().ToLowerInvariant()
                : "auto";

            VisualElement element = null;
            bool usedFallback = false;
            if (parameters.TryGetValue("selector", out string selector) && !string.IsNullOrWhiteSpace(selector))
            {
                element = await ActionHelpers.RequireElementAsync(context, parameters, "menu_item");
                OpenMenuOrThrow(element, menuKind, context, out usedFallback);
                await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
            }

            switch (mode.Trim().ToLowerInvariant())
            {
                case "select":
                case "click":
                    if (!TrySelect(menuKind, itemName, context, element, usedFallback))
                    {
                        throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, $"Menu item was not available: {itemName}");
                    }
                    break;
                case "assert_enabled":
                case "enabled":
                case "assert":
                    if (!TryAssertMenuItem(menuKind, itemName, expectDisabled: false, context, element, usedFallback))
                    {
                        throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, $"Menu item was not available or not enabled: {itemName}");
                    }
                    break;
                case "assert_disabled":
                case "disabled":
                    if (!TryAssertMenuItem(menuKind, itemName, expectDisabled: true, context, element, usedFallback))
                    {
                        throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, $"Menu item was not available or not disabled: {itemName}");
                    }
                    break;
                default:
                    throw new UnityUIFlowException(ErrorCodes.ActionParameterInvalid, $"Action menu_item parameter 'mode' is invalid: {mode}");
            }

            context.Log($"menu_item: kind={menuKind} mode={mode} item={itemName}");
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }

        private static void OpenMenuOrThrow(VisualElement element, string menuKind, ActionContext context, out bool usedFallback)
        {
            usedFallback = false;
            if (menuKind == "context")
            {
                if (context?.SimulationSession?.TryOpenContextMenu(element, EventModifiers.None, context) == true)
                    return;
            }
            else if (menuKind == "popup")
            {
                if (context?.SimulationSession?.TryOpenPopupMenu(element, EventModifiers.None, context) == true)
                    return;
            }
            else
            {
                if (context?.SimulationSession?.TryOpenPopupMenu(element, EventModifiers.None, context) == true)
                    return;
                if (context?.SimulationSession?.TryOpenContextMenu(element, EventModifiers.None, context) == true)
                    return;
            }

            if (ActionHelpers.TryGetDropdownMenu(element) != null)
            {
                usedFallback = true;
                context?.Log($"menu_item: using DropdownMenu fallback for {element.GetType().Name}");
                return;
            }

            throw new UnityUIFlowException(ErrorCodes.OfficialUiTestFrameworkUnavailable, $"menu_item could not open a {menuKind} menu.");
        }

        private static bool TrySelect(string menuKind, string itemName, ActionContext context, VisualElement element, bool usedFallback)
        {
            if (!usedFallback)
            {
                if (menuKind == "context")
                    return context?.SimulationSession?.TrySelectContextMenuItem(itemName, context) == true;
                if (menuKind == "popup")
                    return context?.SimulationSession?.TrySelectPopupMenuItem(itemName, context) == true;
                return (context?.SimulationSession?.TrySelectPopupMenuItem(itemName, context) == true)
                    || (context?.SimulationSession?.TrySelectContextMenuItem(itemName, context) == true);
            }

            return ActionHelpers.TryExecuteDropdownMenuItem(element, itemName);
        }

        private static bool TryAssertMenuItem(string menuKind, string itemName, bool expectDisabled, ActionContext context, VisualElement element, bool usedFallback)
        {
            if (!usedFallback)
            {
                return context?.SimulationSession?.TryAssertMenuItem(itemName, expectDisabled, context) == true;
            }

            if (!ActionHelpers.TryFindDropdownMenuAction(element, itemName, out DropdownMenuAction action))
                return false;

            DropdownMenuAction.Status status = ActionHelpers.GetDropdownMenuActionStatus(action);
            bool isDisabled = status == DropdownMenuAction.Status.Disabled;
            return isDisabled == expectDisabled;
        }
    }

    public sealed class FocusAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "focus");
            if (!element.focusable)
            {
                throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, $"focus failed: {ActionContext.ElementInfo(element)} is not focusable.");
            }
            element.Focus();
            context.Log($"focus: {ActionContext.ElementInfo(element)}");
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class SetValueAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "set_value");
            string value = ActionHelpers.Require(parameters, "set_value", "value");
            context.Log($"set_value: setting {ActionContext.ElementInfo(element)} to \"{value}\"");
            if (!ActionHelpers.TryAssignFieldValue(element, value))
            {
                throw new UnityUIFlowException(ErrorCodes.ActionTargetTypeInvalid, $"set_value target type is not writable: {element.GetType().Name}");
            }

            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class WaitAction : IAction
    {
        public Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            string duration = ActionHelpers.Require(parameters, "wait", "duration");
            int ms = DurationParser.ParseToMilliseconds(duration, "wait");
            context.Log($"wait: {ms}ms");
            return EditorAsyncUtility.DelayAsync(ms, context.CancellationToken);
        }
    }

    public sealed class WaitForElementAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            string selector = ActionHelpers.Require(parameters, "wait_for_element", "selector");
            int timeout = parameters.TryGetValue("timeout", out string timeoutLiteral)
                ? DurationParser.ParseToMilliseconds(timeoutLiteral, "wait_for_element")
                : context.Options.DefaultTimeoutMs;

            context.Log($"wait_for_element: {selector}, timeout={timeout}ms");
            await context.Finder.WaitForElementAsync(
                new SelectorCompiler().Compile(selector),
                context.Root,
                new WaitOptions
                {
                    TimeoutMs = timeout,
                    PollIntervalMs = 16,
                    RequireVisible = true,
                },
                context.CancellationToken);
            context.Log($"wait_for_element: {selector} is visible");
        }
    }

    public sealed class AssertVisibleAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            string selector = parameters.TryGetValue("selector", out string s) ? s : string.Empty;
            context.Log($"assert_visible: {selector}");
            await new WaitForElementAction().ExecuteAsync(root, context, parameters);
            context.Log("assert_visible: passed");
        }
    }

    public sealed class AssertNotVisibleAction : IAction
    {
        public Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            string selector = ActionHelpers.Require(parameters, "assert_not_visible", "selector");
            int timeout = parameters.TryGetValue("timeout", out string timeoutLiteral)
                ? DurationParser.ParseToMilliseconds(timeoutLiteral, "assert_not_visible")
                : context.Options.DefaultTimeoutMs;

            context.Log($"assert_not_visible: {selector}, timeout={timeout}ms");
            return AssertAsync(selector, timeout);

            async Task AssertAsync(string currentSelector, int currentTimeout)
            {
                DateTimeOffset startedAt = DateTimeOffset.UtcNow;
                SelectorExpression compiled = new SelectorCompiler().Compile(currentSelector);
                while (true)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    if (!context.Finder.Exists(compiled, context.Root, true))
                    {
                        context.Log("assert_not_visible: passed");
                        return;
                    }

                    if (UnityUIFlowUtility.DurationMs(startedAt, DateTimeOffset.UtcNow) >= currentTimeout)
                    {
                        throw new UnityUIFlowException(ErrorCodes.ElementNotVisible, $"Element is still visible: {currentSelector}");
                    }

                    await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
                }
            }
        }
    }

    public sealed class AssertTextAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "assert_text");
            string expected = ActionHelpers.Require(parameters, "assert_text", "expected");
            string actual = ActionHelpers.GetText(element);
            context.Log($"assert_text: expected={expected}, actual={actual}");
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, $"assert_text failed: expected '{expected}', actual '{actual}'");
            }

            context.Log("assert_text: passed");
        }
    }

    public sealed class AssertTextContainsAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "assert_text_contains");
            string expected = ActionHelpers.Require(parameters, "assert_text_contains", "expected");
            string actual = ActionHelpers.GetText(element);
            context.Log($"assert_text_contains: expected token={expected}, actual={actual}");
            if (actual == null || actual.IndexOf(expected, StringComparison.Ordinal) < 0)
            {
                throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, $"assert_text_contains failed: missing '{expected}'");
            }

            context.Log("assert_text_contains: passed");
        }
    }

    public sealed class AssertValueAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "assert_value");
            string expected = ActionHelpers.Require(parameters, "assert_value", "expected");

            if (AdvancedActionHelpers.TryReadValue(element, out object actualValue, out Type valueType)
                && AdvancedActionHelpers.TryConvertStringValue(expected, valueType, out object expectedValue))
            {
                string actualText = ActionHelpers.GetValueText(element);
                context.Log($"assert_value: expected={expected}, actual={actualText}");
                if (!AdvancedActionHelpers.ValuesEqual(actualValue, expectedValue, valueType))
                {
                    throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, $"assert_value failed: expected '{expected}', actual '{actualText}'");
                }

                return;
            }

            string actual = ActionHelpers.GetValueText(element);
            context.Log($"assert_value: expected={expected}, actual={actual}");
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, $"assert_value failed: expected '{expected}', actual '{actual}'");
            }
        }
    }

    public sealed class AssertEnabledAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "assert_enabled");
            context.Log($"assert_enabled: {ActionContext.ElementInfo(element)} => {element.enabledInHierarchy}");
            if (!element.enabledInHierarchy)
            {
                throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, $"assert_enabled failed: {ActionContext.ElementInfo(element)} is disabled");
            }
        }
    }

    public sealed class AssertDisabledAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "assert_disabled");
            context.Log($"assert_disabled: {ActionContext.ElementInfo(element)} => {element.enabledInHierarchy}");
            if (element.enabledInHierarchy)
            {
                throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, $"assert_disabled failed: {ActionContext.ElementInfo(element)} is enabled");
            }
        }
    }

    public sealed class AssertPropertyAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "assert_property");
            string propertyName = ActionHelpers.Require(parameters, "assert_property", "property");
            string expected = ActionHelpers.Require(parameters, "assert_property", "expected");

            PropertyInfo property = element.GetType().GetProperty(propertyName);
            if (property == null)
            {
                throw new UnityUIFlowException(ErrorCodes.ActionParameterInvalid, $"assert_property property is invalid: {propertyName}");
            }

            object actual = property.GetValue(element);
            context.Log($"assert_property: {propertyName} expected={expected}, actual={actual}");
            if (!string.Equals(actual?.ToString(), expected, StringComparison.Ordinal))
            {
                throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed, $"assert_property failed: expected '{expected}', actual '{actual}'");
            }

            context.Log("assert_property: passed");
        }
    }

    public sealed class ScreenshotAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            if (context.ScreenshotManager == null)
            {
                throw new UnityUIFlowException(ErrorCodes.ScreenshotSaveFailed, "Screenshot manager is not initialized.");
            }

            if (!parameters.TryGetValue("tag", out string tag) || string.IsNullOrWhiteSpace(tag))
            {
                tag = context.CurrentStepId;
            }

            context.Log($"screenshot: tag={tag}, case={context.CurrentCaseName}, step={context.CurrentStepIndex}");
            string path = await context.ScreenshotManager.CaptureAsync(context.CurrentCaseName, context.CurrentStepIndex, tag, context.CancellationToken);
            if (path == null)
            {
                context.Log("screenshot: skipped (unfocused)");
                return;
            }
            context.Log($"screenshot: saved {path}");
            context.AddAttachment(path);
        }
    }
}

