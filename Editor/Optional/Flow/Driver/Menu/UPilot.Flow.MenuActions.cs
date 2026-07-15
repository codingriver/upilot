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
                throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, $"Context menu item was not available: {itemName}");
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
                throw new UPilotFlowException(ErrorCodes.OfficialUiTestFrameworkUnavailable, "open_popup_menu requires official PopupMenuSimulator support.");
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
                throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, $"Popup menu item was not available: {itemName}");
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
                throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, $"Menu item was not available or not enabled: {itemName}");
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
                throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, $"Menu item was not available or not disabled: {itemName}");
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
                        throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, $"Menu item was not available: {itemName}");
                    }
                    break;
                case "assert_enabled":
                case "enabled":
                case "assert":
                    if (!TryAssertMenuItem(menuKind, itemName, expectDisabled: false, context, element, usedFallback))
                    {
                        throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, $"Menu item was not available or not enabled: {itemName}");
                    }
                    break;
                case "assert_disabled":
                case "disabled":
                    if (!TryAssertMenuItem(menuKind, itemName, expectDisabled: true, context, element, usedFallback))
                    {
                        throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, $"Menu item was not available or not disabled: {itemName}");
                    }
                    break;
                default:
                    throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"Action menu_item parameter 'mode' is invalid: {mode}");
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

            throw new UPilotFlowException(ErrorCodes.OfficialUiTestFrameworkUnavailable, $"menu_item could not open a {menuKind} menu.");
        }

        private static bool TrySelect(string menuKind, string itemName, ActionContext context, VisualElement element, bool usedFallback)
        {
            if (!usedFallback)
            {
                bool selected;
                if (menuKind == "context")
                {
                    selected = context?.SimulationSession?.TrySelectContextMenuItem(itemName, context) == true;
                }
                else if (menuKind == "popup")
                {
                    selected = context?.SimulationSession?.TrySelectPopupMenuItem(itemName, context) == true;
                }
                else
                {
                    selected = (context?.SimulationSession?.TrySelectPopupMenuItem(itemName, context) == true)
                    || (context?.SimulationSession?.TrySelectContextMenuItem(itemName, context) == true);
                }

                if (selected)
                    return true;

                if (element != null && ActionHelpers.TryGetDropdownMenu(element) != null)
                {
                    context?.Log($"menu_item: official menu selection failed; using DropdownMenu fallback for {element.GetType().Name}");
                    return ActionHelpers.TryExecuteDropdownMenuItem(element, itemName);
                }

                return false;
            }

            return ActionHelpers.TryExecuteDropdownMenuItem(element, itemName);
        }

        private static bool TryAssertMenuItem(string menuKind, string itemName, bool expectDisabled, ActionContext context, VisualElement element, bool usedFallback)
        {
            if (!usedFallback)
            {
                if (context?.SimulationSession?.TryAssertMenuItem(itemName, expectDisabled, context) == true)
                    return true;

                if (element == null || ActionHelpers.TryGetDropdownMenu(element) == null)
                    return false;

                context?.Log($"menu_item: official menu assertion failed; using DropdownMenu fallback for {element.GetType().Name}");
            }

            if (!ActionHelpers.TryFindDropdownMenuAction(element, itemName, out DropdownMenuAction action))
                return false;

            DropdownMenuAction.Status status = ActionHelpers.GetDropdownMenuActionStatus(action);
            bool isDisabled = status == DropdownMenuAction.Status.Disabled;
            return isDisabled == expectDisabled;
        }
    }
}
