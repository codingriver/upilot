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
    public sealed class SetSliderAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "set_slider");
            context.Log($"set_slider: target {ActionContext.ElementInfo(element)}");
            AdvancedActionHelpers.SetSliderOrThrow(element, parameters, "set_slider");
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class SelectTabAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "select_tab");
            context.Log($"select_tab: target {ActionContext.ElementInfo(element)}");
            AdvancedActionHelpers.SelectTabOrThrow(element, parameters, "select_tab", context);
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class CloseTabAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "close_tab");
            context.Log($"close_tab: target {ActionContext.ElementInfo(element)}");
            AdvancedActionHelpers.CloseTabOrThrow(element, parameters, "close_tab", context);
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class NavigateBreadcrumbAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "navigate_breadcrumb");
            context.Log($"navigate_breadcrumb: target {ActionContext.ElementInfo(element)}");
            AdvancedActionHelpers.NavigateBreadcrumbOrThrow(element, parameters, "navigate_breadcrumb", context);
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class ReadBreadcrumbsAction : IAction
    {
        public Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            return ActionHelpers.RequireElementAsync(context, parameters, "read_breadcrumbs")
                .ContinueWith(task =>
                {
                    VisualElement element = task.Result;
                    List<string> crumbs = AdvancedActionHelpers.ReadBreadcrumbsOrThrow(element, "read_breadcrumbs");
                    string bagKey = parameters.TryGetValue("bag_key", out string key) && !string.IsNullOrWhiteSpace(key) ? key : "breadcrumbs";
                    context.SharedBag[bagKey] = crumbs;
                    context.Log($"read_breadcrumbs: {string.Join(" > ", crumbs)}");
                }, TaskScheduler.Default);
        }
    }

    public sealed class SetSplitViewSizeAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "set_split_view_size");
            context.Log($"set_split_view_size: target {ActionContext.ElementInfo(element)}");
            AdvancedActionHelpers.SetSplitViewSizeOrThrow(element, parameters, "set_split_view_size");
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class PageScrollerAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "page_scroller");
            context.Log($"page_scroller: target {ActionContext.ElementInfo(element)}");
            AdvancedActionHelpers.PageScrollerOrThrow(element, parameters, "page_scroller");
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }

    public sealed class DragScrollerAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "drag_scroller");
            if (!(element is Scroller scroller))
            {
                throw new UPilotFlowException(ErrorCodes.ActionTargetTypeInvalid, $"Action drag_scroller target is not a Scroller: {element.GetType().Name}");
            }

            (Vector2 fromPos, Vector2 toPos) = AdvancedActionHelpers.ResolveScrollerThumbDrag(scroller, parameters, "drag_scroller");
            MouseButton button = ActionHelpers.ParseMouseButton(parameters, "drag_scroller");
            EventModifiers modifiers = ActionHelpers.ParseEventModifiers(parameters, "drag_scroller");
            int delayMs = parameters.TryGetValue("duration", out string duration) && !string.IsNullOrWhiteSpace(duration)
                ? DurationParser.ParseToMilliseconds(duration, "drag_scroller")
                : 100;
            int frameCount = Math.Max(1, delayMs / 16);

            context.Log($"drag_scroller: {fromPos} -> {toPos} ratio target via {ActionHelpers.ResolvePointerDriver(context)} button={button} modifiers={modifiers}");

            if (context?.SimulationSession?.PointerDriver != null)
            {
                await context.SimulationSession.PointerDriver.DragAsync(root, fromPos, toPos, delayMs, frameCount, button, modifiers, context);
            }
            else
            {
                ActionHelpers.DispatchMouseEvent(element, EventType.MouseDown, fromPos, Vector2.zero, button: (int)button, modifiers: modifiers);
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
                ActionHelpers.DispatchMouseEvent(element, EventType.MouseUp, toPos, Vector2.zero, button: (int)button, modifiers: modifiers);
            }

            context.Log($"drag_scroller: completed");
            await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
        }
    }
}
