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
                throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"type_text target type is not writable: {element.GetType().Name}");
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
                        throw new UPilotFlowException(
                            ErrorCodes.ActionExecutionFailed,
                            "动作 type_text 在高保真模式下禁止回退到直接写值实现");
                    }

                    if (!ActionHelpers.TryAssignFieldValue(element, expected))
                    {
                        throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"type_text target type is not writable: {element.GetType().Name}");
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
                throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"type_text_fast target type is not writable: {element.GetType().Name}");
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
                throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, "press_key parameter 'key' is invalid.");
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
                throw new UPilotFlowException(
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
                throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, "press_key_combination parameter 'keys' must contain at least two keys separated by '+'.");
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
                    throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"press_key_combination contains invalid key: {part}");
                }
            }

            if (keyCodes.Count != 1)
            {
                throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, "press_key_combination must contain exactly one non-modifier key.");
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

    public sealed class FocusAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "focus");
            if (!element.focusable)
            {
                throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, $"focus failed: {ActionContext.ElementInfo(element)} is not focusable.");
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
                throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"set_value target type is not writable: {element.GetType().Name}");
            }

            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }
}
