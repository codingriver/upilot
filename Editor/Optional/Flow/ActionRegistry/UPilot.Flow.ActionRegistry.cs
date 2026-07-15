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

namespace CodingRiver.UPilot.Flow
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
        public UPilotFlowSimulationSession SimulationSession;
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
                Codingriver.Logger.LogWarning($"[UPilot Flow] {ErrorCodes.AttachmentLimitExceeded}: step {CurrentStepId} already has 10 attachments.");
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
                Codingriver.Logger.Log($"[UPilot Flow][{CurrentCaseName}][{CurrentStepId}] {message}");
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
        private readonly Dictionary<string, ActionDescriptor> _descriptors = new Dictionary<string, ActionDescriptor>(StringComparer.Ordinal);

        public IReadOnlyCollection<ActionDescriptor> Descriptors => _descriptors.Values;

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
                throw new UPilotFlowException(ErrorCodes.ActionNameConflict, "Action name cannot be empty.");
            }

            if (!typeof(IAction).IsAssignableFrom(actionType))
            {
                throw new UPilotFlowException(ErrorCodes.ActionNameConflict, $"Action {actionName} does not implement IAction.");
            }

            if (_actions.ContainsKey(actionName))
            {
                throw new UPilotFlowException(ErrorCodes.ActionNameConflict, $"Duplicate action name: {actionName}");
            }

            _actions[actionName] = actionType;
            _descriptors[actionName] = ActionDescriptorFactory.Create(actionName, actionType);
        }

        /// <summary>
        /// Resolves an action instance by name.
        /// </summary>
        public IAction Resolve(string actionName)
        {
            if (!_actions.TryGetValue(actionName, out Type actionType))
            {
                Codingriver.Logger.LogError($"[UPilot Flow] 未找到动作 \"{actionName}\"，已注册动作数={_actions.Count}。可用动作：{string.Join(", ", _actions.Keys.Take(20))}");
                throw new UPilotFlowException(ErrorCodes.ActionNotFound, $"Action not found: {actionName}");
            }

            try
            {
                return (IAction)Activator.CreateInstance(actionType);
            }
            catch (Exception ex)
            {
                Codingriver.Logger.LogError($"[UPilot Flow] 构造动作 \"{actionName}\" 失败: {ex.Message}");
                throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, $"Failed to construct action {actionName}: {ex.Message}", ex);
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
            HashSet<string> allowedAssemblies = UPilotFlowConfigResolver.GetCustomActionAssemblyWhitelist();
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
                throw new UPilotFlowException(ErrorCodes.ActionParameterMissing, $"Action {actionName} is missing parameter {key}.");
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

            throw new UPilotFlowException(
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
                    throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action {actionName} parameter '{key}' is invalid: {literal}");
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
                        throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action {actionName} parameter '{key}' is invalid: {part}");
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

            throw new UPilotFlowException(ErrorCodes.ActionParameterMissing, $"Action {actionName} is missing parameter item.");
        }

        public static void DispatchClick(VisualElement element, int clickCount, MouseButton button, EventModifiers modifiers, ActionContext context = null)
        {
            var dispatchRoot = element?.panel?.visualTree ?? element;
            if (dispatchRoot == null)
            {
                throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, "Click target is unavailable.");
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
                        throw new UPilotFlowException(
                            ErrorCodes.ActionExecutionFailed,
                            $"click failed: {ActionContext.ElementInfo(element)} did not receive pointer or mouse down.");
                    }
                    context?.Log($"click: allowing non-interactive element {ActionContext.ElementInfo(element)} (clickable=null) without event receipt");
                }

                if (!pointerUpReceived && !mouseUpReceived)
                {
                    throw new UPilotFlowException(
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
                throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, "Click target is unavailable.");
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
                throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, "Keyboard event target is unavailable.");
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
                throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, "Command event target is unavailable.");
            }

            if (string.IsNullOrWhiteSpace(commandName))
            {
                throw new UPilotFlowException(ErrorCodes.ActionParameterMissing, "Command name cannot be empty.");
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
                    throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Unsupported command event type: {eventType}");
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
                throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, "Mouse event target is unavailable.");
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
                    throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Unsupported mouse event type: {eventType}");
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
                throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, "Wheel event target is unavailable.");
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

        public static IEnumerable<string> MenuItemNameCandidates(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName))
                yield break;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in BuildMenuItemNameCandidates(itemName))
            {
                if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
                    yield return candidate;
            }
        }

        private static IEnumerable<string> BuildMenuItemNameCandidates(string itemName)
        {
            string trimmed = itemName.Trim();
            yield return trimmed;

            string slashNormalized = NormalizeMenuPath(trimmed, '/');
            yield return slashNormalized;

            string arrowNormalized = NormalizeMenuPath(trimmed, '>');
            if (!string.Equals(arrowNormalized, slashNormalized, StringComparison.Ordinal))
                yield return arrowNormalized;
        }

        private static string NormalizeMenuPath(string itemName, char separator)
        {
            if (string.IsNullOrWhiteSpace(itemName))
                return itemName;

            char[] separators = separator == '>'
                ? new[] { '>' }
                : new[] { '/', '\\' };
            return string.Join("/", itemName.Split(separators, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
        }

        public static bool TryFindDropdownMenuAction(VisualElement element, string itemName, out DropdownMenuAction action)
        {
            action = null;
            DropdownMenu menu = TryGetDropdownMenu(element);
            if (menu == null || string.IsNullOrWhiteSpace(itemName))
                return false;

            var actions = menu.MenuItems().OfType<DropdownMenuAction>().ToList();
            foreach (string candidate in MenuItemNameCandidates(itemName))
            {
                action = actions.FirstOrDefault(a => string.Equals(a.name, candidate, StringComparison.Ordinal));
                if (action != null)
                {
                    return true;
                }
            }

            string requestedLeaf = LastMenuPathSegment(itemName);
            var leafMatches = actions
                .Where(a => string.Equals(LastMenuPathSegment(a.name), requestedLeaf, StringComparison.Ordinal))
                .Take(2)
                .ToList();
            if (leafMatches.Count == 1)
            {
                action = leafMatches[0];
                return true;
            }

            return false;
        }

        private static string LastMenuPathSegment(string itemName)
        {
            string normalized = NormalizeMenuPath(itemName ?? string.Empty, '/');
            int slash = normalized.LastIndexOf('/');
            return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
        }

        public static bool TryExecuteDropdownMenuItem(VisualElement element, string itemName)
        {
            if (!TryFindDropdownMenuAction(element, itemName, out DropdownMenuAction action))
                return false;

            action.Execute();
            return true;
        }
    }
































































}
