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
}
