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
    public sealed class ScrollAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "scroll");
            string delta = ActionHelpers.Require(parameters, "scroll", "delta");
            string[] parts = delta.Split(',');
            if (parts.Length != 2 || !float.TryParse(parts[0], out float dx) || !float.TryParse(parts[1], out float dy))
            {
                throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, "scroll parameter 'delta' is invalid.");
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
}
