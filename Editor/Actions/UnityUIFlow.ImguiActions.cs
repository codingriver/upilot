using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityUIFlow
{
    // ── Helper: shared IMGUI action infrastructure ──

    public static class ImguiActionHelper
    {
        public static EditorWindow ResolveWindow(ActionContext context, string actionName)
        {
            var window = context.Simulator as EditorWindow
                ?? context.SimulationSession?.HostEditorWindow
                ?? EditorWindow.focusedWindow
                ?? Resources.FindObjectsOfTypeAll<EditorWindow>().FirstOrDefault();
            if (window == null)
                throw new UnityUIFlowException(ErrorCodes.HostWindowOpenFailed, $"No active EditorWindow for {actionName}.");
            return window;
        }

        public static void EnsureWindowFocused(EditorWindow window)
        {
            if (window == null) return;
            window.Focus();
            window.Repaint();
        }

        public static async Task ExecuteCommandAsync(
            ImguiExecutionBridge bridge,
            System.Func<TaskCompletionSource<bool>, ImguiCommand> commandFactory,
            CancellationToken cancellationToken,
            int postDelayMs = 0)
        {
            var tcs = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(() => tcs.TrySetCanceled()))
            {
                bridge.Enqueue(commandFactory(tcs));
                await tcs.Task;
            }
            if (postDelayMs > 0)
                await Task.Delay(postDelayMs, cancellationToken);
        }

        public static bool TrySetFieldValue(EditorWindow window, string fieldName, object value)
        {
            if (window == null || string.IsNullOrWhiteSpace(fieldName))
                return false;

            var type = window.GetType();
            var field = type.GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (field != null)
            {
                var converted = Convert.ChangeType(value, field.FieldType);
                field.SetValue(window, converted);
                window.Repaint();
                return true;
            }

            var prop = type.GetProperty(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                var converted = Convert.ChangeType(value, prop.PropertyType);
                prop.SetValue(window, converted);
                window.Repaint();
                return true;
            }

            return false;
        }

        public static ImguiSnapshotEntry RequireEntry(Dictionary<string, string> parameters, ImguiSnapshot snapshot)
        {
            if (!parameters.TryGetValue("selector", out string selectorText))
                throw new UnityUIFlowException(ErrorCodes.SelectorInvalid, "IMGUI action requires 'selector' parameter.");

            var selector = ImguiSelectorCompiler.Compile(selectorText);
            var entry = ImguiElementLocator.Find(snapshot, selector);
            if (entry == null)
                throw new UnityUIFlowException(ErrorCodes.ElementNotFound,
                    $"IMGUI element not found for selector: {selectorText}");
            return entry;
        }

        public static Vector2 LocalToScreen(EditorWindow window, Vector2 localPos)
        {
            Vector2 screenPos = GUIUtility.GUIToScreenPoint(localPos);
            return screenPos;
        }

        public static void SendEvent(EditorWindow window, EventType eventType)
        {
            var evt = new Event { type = eventType };
            window.SendEvent(evt);
        }

        public static void SendMouseEvent(EditorWindow window, Vector2 localPos, EventType eventType, int button = 0)
        {
            var bridge = ImguiBridgeRegistry.GetOrCreateBridge(window);
            Vector2 offset = bridge?.WindowToContentOffset ?? Vector2.zero;
            Vector2 windowPos = localPos + offset;

            Codingriver.Logger.Log($"[UnityUIFlow] SendMouseEvent {eventType} at {localPos} (window={windowPos}, offset={offset}) for {window?.GetType().Name}");
            var evt = new Event
            {
                type = eventType,
                button = button,
                mousePosition = windowPos,
                modifiers = EventModifiers.None,
            };

            window.SendEvent(evt);
        }

        public static void Highlight(ImguiSnapshotEntry entry, string actionName, EditorWindow window)
        {
            if (entry == null || window == null)
                return;
            StepHighlighter.HighlightRect(entry.Rect, actionName, window);
        }
    }

    // ── imgui_click ──

    [ActionName("imgui_click")]
    public sealed class ImguiClickAction : IAction
    {
        public Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            return ExecuteAsyncInternal(context, parameters);
        }

        private async Task ExecuteAsyncInternal(ActionContext context, Dictionary<string, string> parameters)
        {
            var window = ImguiActionHelper.ResolveWindow(context, "imgui_click");
            ImguiActionHelper.EnsureWindowFocused(window);
            var bridge = GetOrCreateBridge(window);
            await ImguiActionHelper.ExecuteCommandAsync(
                bridge,
                tcs => new ImguiClickCommand(parameters, tcs),
                context.CancellationToken,
                postDelayMs: 50);
        }

        private static ImguiExecutionBridge GetOrCreateBridge(EditorWindow window) => ImguiBridgeRegistry.GetOrCreateBridge(window);

        private class ImguiClickCommand : ImguiCommand
        {
            private readonly Dictionary<string, string> _parameters;
            private readonly TaskCompletionSource<bool> _tcs;

            public ImguiClickCommand(Dictionary<string, string> parameters, TaskCompletionSource<bool> tcs)
            {
                _parameters = parameters;
                _tcs = tcs;
            }

            public override bool RequiresRepaintWait => true;

            public override void Execute(EditorWindow window, ImguiSnapshot snapshot)
            {
                try
                {
                    var entry = ImguiActionHelper.RequireEntry(_parameters, snapshot);
                    Vector2 center = entry.Rect.center;

                    // Process Layout first to ensure GUILayoutUtility cache is valid,
                    // then send MouseDown + MouseUp for the actual click.
                    ImguiActionHelper.SendEvent(window, EventType.Layout);
                    ImguiActionHelper.SendMouseEvent(window, center, EventType.MouseDown);
                    ImguiActionHelper.SendMouseEvent(window, center, EventType.MouseUp);

                    ImguiActionHelper.Highlight(entry, "imgui_click", window);
                    _tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                }
            }
        }
    }

    // ── imgui_type ──

    [ActionName("imgui_type")]
    public sealed class ImguiTypeAction : IAction
    {
        public Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            return ExecuteAsyncInternal(context, parameters);
        }

        private async Task ExecuteAsyncInternal(ActionContext context, Dictionary<string, string> parameters)
        {
            var window = ImguiActionHelper.ResolveWindow(context, "imgui_type");
            ImguiActionHelper.EnsureWindowFocused(window);

            if (!parameters.TryGetValue("text", out string text))
                throw new UnityUIFlowException(ErrorCodes.ActionParameterMissing, "imgui_type requires 'text' parameter.");

            var bridge = GetOrCreateBridge(window);
            await ImguiActionHelper.ExecuteCommandAsync(
                bridge,
                tcs => new ImguiTypeCommand(parameters, text, tcs),
                context.CancellationToken,
                postDelayMs: 50);
        }

        private static ImguiExecutionBridge GetOrCreateBridge(EditorWindow window) => ImguiBridgeRegistry.GetOrCreateBridge(window);

        private class ImguiTypeCommand : ImguiCommand
        {
            private readonly Dictionary<string, string> _parameters;
            private readonly string _text;
            private readonly TaskCompletionSource<bool> _tcs;

            public ImguiTypeCommand(Dictionary<string, string> parameters, string text, TaskCompletionSource<bool> tcs)
            {
                _parameters = parameters;
                _text = text;
                _tcs = tcs;
            }

            public override bool RequiresRepaintWait => true;

            public override void Execute(EditorWindow window, ImguiSnapshot snapshot)
            {
                try
                {
                    var entry = ImguiActionHelper.RequireEntry(_parameters, snapshot);
                    Vector2 center = entry.Rect.center;

                    // Click to focus the text field
                    ImguiActionHelper.SendMouseEvent(window, center, EventType.MouseDown);
                    ImguiActionHelper.SendMouseEvent(window, center, EventType.MouseUp);

                    // Type characters via Event queue
                    foreach (char c in _text)
                    {
                        var keyDown = new Event
                        {
                            type = EventType.KeyDown,
                            character = c,
                            keyCode = KeyCode.None,
                            modifiers = EventModifiers.None,
                        };
                        window.SendEvent(keyDown);
                    }

                    ImguiActionHelper.Highlight(entry, "imgui_type", window);
                    _tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                }
            }
        }
    }

    // ── imgui_assert_text ──

    [ActionName("imgui_assert_text")]
    public sealed class ImguiAssertTextAction : IAction
    {
        public Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            return ExecuteAsyncInternal(context, parameters);
        }

        private async Task ExecuteAsyncInternal(ActionContext context, Dictionary<string, string> parameters)
        {
            var window = ImguiActionHelper.ResolveWindow(context, "imgui_assert_text");
            ImguiActionHelper.EnsureWindowFocused(window);

            if (!parameters.TryGetValue("text", out string expectedText))
                throw new UnityUIFlowException(ErrorCodes.ActionParameterMissing, "imgui_assert_text requires 'text' parameter.");

            var bridge = GetOrCreateBridge(window);
            await ImguiActionHelper.ExecuteCommandAsync(
                bridge,
                tcs => new ImguiAssertTextCommand(parameters, expectedText, tcs),
                context.CancellationToken);
        }

        private static ImguiExecutionBridge GetOrCreateBridge(EditorWindow window) => ImguiBridgeRegistry.GetOrCreateBridge(window);

        private class ImguiAssertTextCommand : ImguiCommand
        {
            private readonly Dictionary<string, string> _parameters;
            private readonly string _expectedText;
            private readonly TaskCompletionSource<bool> _tcs;

            public ImguiAssertTextCommand(Dictionary<string, string> parameters, string expectedText, TaskCompletionSource<bool> tcs)
            {
                _parameters = parameters;
                _expectedText = expectedText;
                _tcs = tcs;
            }

            public override void Execute(EditorWindow window, ImguiSnapshot snapshot)
            {
                try
                {
                    if (!_parameters.TryGetValue("selector", out string selectorText))
                        throw new UnityUIFlowException(ErrorCodes.SelectorInvalid, "imgui_assert_text requires 'selector' parameter.");

                    var selector = ImguiSelectorCompiler.Compile(selectorText);
                    var candidates = ImguiElementLocator.FindAll(snapshot, selector);

                    // When control_name is used but not stored in snapshot entries, FindAll may
                    // return many candidates. Search for one whose text matches the expected value.
                    ImguiSnapshotEntry entry = null;
                    if (candidates.Count > 0)
                    {
                        entry = candidates.FirstOrDefault(e =>
                            string.Equals(e.Text ?? string.Empty, _expectedText, StringComparison.Ordinal));
                    }

                    if (entry == null)
                    {
                        // Provide detailed diagnostics: list ALL candidate texts
                        var candidateTexts = candidates.Select(e => $"[type={e.InferredType} text='{e.Text ?? "(null)"}' style={e.StyleName} rect={e.Rect}]").ToList();
                        string candidatesStr = candidateTexts.Count > 0
                            ? string.Join("; ", candidateTexts)
                            : "(no candidates)";
                        _tcs.TrySetException(new UnityUIFlowException(ErrorCodes.AssertionFailed,
                            $"imgui_assert_text failed. Expected: '{_expectedText}'. Candidates ({candidates.Count}): {candidatesStr}"));
                        return;
                    }

                    ImguiActionHelper.Highlight(entry, "imgui_assert_text", window);
                    _tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                }
            }
        }
    }

    // ── imgui_assert_visible ──

    [ActionName("imgui_assert_visible")]
    public sealed class ImguiAssertVisibleAction : IAction
    {
        public Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            return ExecuteAsyncInternal(context, parameters);
        }

        private async Task ExecuteAsyncInternal(ActionContext context, Dictionary<string, string> parameters)
        {
            var window = ImguiActionHelper.ResolveWindow(context, "imgui_assert_visible");
            ImguiActionHelper.EnsureWindowFocused(window);
            var bridge = GetOrCreateBridge(window);
            await ImguiActionHelper.ExecuteCommandAsync(
                bridge,
                tcs => new ImguiAssertVisibleCommand(parameters, tcs),
                context.CancellationToken);
        }

        private static ImguiExecutionBridge GetOrCreateBridge(EditorWindow window) => ImguiBridgeRegistry.GetOrCreateBridge(window);

        private class ImguiAssertVisibleCommand : ImguiCommand
        {
            private readonly Dictionary<string, string> _parameters;
            private readonly TaskCompletionSource<bool> _tcs;

            public ImguiAssertVisibleCommand(Dictionary<string, string> parameters, TaskCompletionSource<bool> tcs)
            {
                _parameters = parameters;
                _tcs = tcs;
            }

            public override void Execute(EditorWindow window, ImguiSnapshot snapshot)
            {
                try
                {
                    var entry = ImguiActionHelper.RequireEntry(_parameters, snapshot);

                    if (entry.Rect.width <= 0 || entry.Rect.height <= 0)
                    {
                        _tcs.TrySetException(new UnityUIFlowException(ErrorCodes.AssertionFailed,
                            "imgui_assert_visible failed: element has zero size."));
                        return;
                    }

                    ImguiActionHelper.Highlight(entry, "imgui_assert_visible", window);
                    _tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                }
            }
        }
    }

    // ── imgui_wait ──

    [ActionName("imgui_wait")]
    public sealed class ImguiWaitAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            var window = ImguiActionHelper.ResolveWindow(context, "imgui_wait");
            ImguiActionHelper.EnsureWindowFocused(window);

            if (!parameters.TryGetValue("selector", out string selectorText))
                throw new UnityUIFlowException(ErrorCodes.SelectorInvalid, "imgui_wait requires 'selector' parameter.");

            int timeoutMs = context.Options?.DefaultTimeoutMs ?? 3000;
            if (parameters.TryGetValue("timeout", out string timeoutLiteral))
                timeoutMs = DurationParser.ParseToMilliseconds(timeoutLiteral, "imgui_wait");

            var selector = ImguiSelectorCompiler.Compile(selectorText);
            using var cts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(
                context.CancellationToken,
                new System.Threading.CancellationTokenSource(timeoutMs).Token);

            try
            {
                var bridge = GetOrCreateBridge(window);
                var start = DateTimeOffset.UtcNow;

                while (!cts.Token.IsCancellationRequested)
                {
                    var snapshot = bridge.GetLastSnapshot();
                    if (snapshot != null)
                    {
                        var entry = ImguiElementLocator.Find(snapshot, selector);
                        if (entry != null)
                            return;
                    }

                    bridge.Enqueue(new ImguiNoOpCommand());
                    await Task.Delay(100, cts.Token);
                }

                // If we exited the loop because the token was cancelled but Task.Delay
                // happened to complete normally (race condition), we still need to treat
                // this as a timeout.
                throw new UnityUIFlowException(ErrorCodes.StepTimeout,
                    $"imgui_wait timed out after {timeoutMs}ms for selector: {selectorText}");
            }
            catch (TaskCanceledException)
            {
                throw new UnityUIFlowException(ErrorCodes.StepTimeout,
                    $"imgui_wait timed out after {timeoutMs}ms for selector: {selectorText}");
            }
        }

        private static ImguiExecutionBridge GetOrCreateBridge(EditorWindow window) => ImguiBridgeRegistry.GetOrCreateBridge(window);

        private class ImguiNoOpCommand : ImguiCommand
        {
            public override void Execute(EditorWindow window, ImguiSnapshot snapshot) { }
        }
    }

    // ── imgui_double_click ──

    [ActionName("imgui_double_click")]
    public sealed class ImguiDoubleClickAction : IAction
    {
        public Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            return ExecuteAsyncInternal(context, parameters);
        }

        private async Task ExecuteAsyncInternal(ActionContext context, Dictionary<string, string> parameters)
        {
            var window = ImguiActionHelper.ResolveWindow(context, "imgui_double_click");
            ImguiActionHelper.EnsureWindowFocused(window);
            var bridge = GetOrCreateBridge(window);
            await ImguiActionHelper.ExecuteCommandAsync(
                bridge,
                tcs => new ImguiDoubleClickCommand(parameters, tcs),
                context.CancellationToken,
                postDelayMs: 80);
        }

        private static ImguiExecutionBridge GetOrCreateBridge(EditorWindow window) => ImguiBridgeRegistry.GetOrCreateBridge(window);

        private class ImguiDoubleClickCommand : ImguiCommand
        {
            private readonly Dictionary<string, string> _parameters;
            private readonly TaskCompletionSource<bool> _tcs;

            public ImguiDoubleClickCommand(Dictionary<string, string> parameters, TaskCompletionSource<bool> tcs)
            {
                _parameters = parameters;
                _tcs = tcs;
            }

            public override bool RequiresRepaintWait => true;

            public override void Execute(EditorWindow window, ImguiSnapshot snapshot)
            {
                try
                {
                    var entry = ImguiActionHelper.RequireEntry(_parameters, snapshot);
                    Vector2 center = entry.Rect.center;

                    // Two rapid click sequences
                    for (int i = 0; i < 2; i++)
                    {
                        ImguiActionHelper.SendMouseEvent(window, center, EventType.MouseDown);
                        ImguiActionHelper.SendMouseEvent(window, center, EventType.MouseUp);
                    }

                    ImguiActionHelper.Highlight(entry, "imgui_double_click", window);
                    _tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                }
            }
        }
    }

    // ── imgui_focus ──

    [ActionName("imgui_focus")]
    public sealed class ImguiFocusAction : IAction
    {
        public Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            return ExecuteAsyncInternal(context, parameters);
        }

        private async Task ExecuteAsyncInternal(ActionContext context, Dictionary<string, string> parameters)
        {
            var window = ImguiActionHelper.ResolveWindow(context, "imgui_focus");
            ImguiActionHelper.EnsureWindowFocused(window);
            var bridge = GetOrCreateBridge(window);
            await ImguiActionHelper.ExecuteCommandAsync(
                bridge,
                tcs => new ImguiFocusCommand(parameters, tcs),
                context.CancellationToken,
                postDelayMs: 50);
        }

        private static ImguiExecutionBridge GetOrCreateBridge(EditorWindow window) => ImguiBridgeRegistry.GetOrCreateBridge(window);

        private class ImguiFocusCommand : ImguiCommand
        {
            private readonly Dictionary<string, string> _parameters;
            private readonly TaskCompletionSource<bool> _tcs;

            public ImguiFocusCommand(Dictionary<string, string> parameters, TaskCompletionSource<bool> tcs)
            {
                _parameters = parameters;
                _tcs = tcs;
            }

            public override bool RequiresRepaintWait => true;

            public override void Execute(EditorWindow window, ImguiSnapshot snapshot)
            {
                try
                {
                    var entry = ImguiActionHelper.RequireEntry(_parameters, snapshot);
                    Vector2 center = entry.Rect.center;

                    // Process Layout first to ensure GUILayoutUtility cache is valid,
                    // then send MouseDown + MouseUp for the actual click.
                    ImguiActionHelper.SendEvent(window, EventType.Layout);
                    ImguiActionHelper.SendMouseEvent(window, center, EventType.MouseDown);
                    ImguiActionHelper.SendMouseEvent(window, center, EventType.MouseUp);

                    ImguiActionHelper.Highlight(entry, "imgui_focus", window);
                    _tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                }
            }
        }
    }

    // ── imgui_scroll ──

    [ActionName("imgui_scroll")]
    public sealed class ImguiScrollAction : IAction
    {
        public Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            return ExecuteAsyncInternal(context, parameters);
        }

        private async Task ExecuteAsyncInternal(ActionContext context, Dictionary<string, string> parameters)
        {
            var window = ImguiActionHelper.ResolveWindow(context, "imgui_scroll");
            ImguiActionHelper.EnsureWindowFocused(window);

            // delta: positive = up, negative = down
            int delta = 120;
            if (parameters.TryGetValue("delta", out string deltaStr) && int.TryParse(deltaStr, out int parsedDelta))
                delta = parsedDelta;

            var bridge = GetOrCreateBridge(window);
            await ImguiActionHelper.ExecuteCommandAsync(
                bridge,
                tcs => new ImguiScrollCommand(parameters, delta, tcs),
                context.CancellationToken,
                postDelayMs: 50);
        }

        private static ImguiExecutionBridge GetOrCreateBridge(EditorWindow window) => ImguiBridgeRegistry.GetOrCreateBridge(window);

        private class ImguiScrollCommand : ImguiCommand
        {
            private readonly Dictionary<string, string> _parameters;
            private readonly int _delta;
            private readonly TaskCompletionSource<bool> _tcs;

            public ImguiScrollCommand(Dictionary<string, string> parameters, int delta, TaskCompletionSource<bool> tcs)
            {
                _parameters = parameters;
                _delta = delta;
                _tcs = tcs;
            }

            public override bool RequiresRepaintWait => true;

            public override void Execute(EditorWindow window, ImguiSnapshot snapshot)
            {
                try
                {
                    Vector2 scrollPos;
                    Rect highlightRect;
                    if (_parameters.TryGetValue("selector", out string selectorText))
                    {
                        var entry = ImguiActionHelper.RequireEntry(_parameters, snapshot);
                        scrollPos = entry.Rect.center;
                        highlightRect = entry.Rect;
                    }
                    else
                    {
                        // If no selector, scroll at current mouse position
                        scrollPos = Event.current?.mousePosition ?? new Vector2(window.position.width / 2, window.position.height / 2);
                        highlightRect = new Rect(scrollPos.x - 20, scrollPos.y - 20, 40, 40);
                    }

                    var bridge = ImguiBridgeRegistry.GetOrCreateBridge(window);
                    Vector2 offset = bridge?.WindowToContentOffset ?? Vector2.zero;
                    Vector2 windowPos = scrollPos + offset;

                    var evt = new Event
                    {
                        type = EventType.ScrollWheel,
                        mousePosition = windowPos,
                        delta = new Vector2(0, _delta / 120f),
                    };
                    window.SendEvent(evt);

                    ImguiActionHelper.Highlight(new ImguiSnapshotEntry { Rect = highlightRect, InferredType = "scroller" }, "imgui_scroll", window);
                    _tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                }
            }
        }
    }

    // ── imgui_select_option ──

    [ActionName("imgui_select_option")]
    public sealed class ImguiSelectOptionAction : IAction
    {
        public Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            return ExecuteAsyncInternal(context, parameters);
        }

        private async Task ExecuteAsyncInternal(ActionContext context, Dictionary<string, string> parameters)
        {
            var window = ImguiActionHelper.ResolveWindow(context, "imgui_select_option");
            ImguiActionHelper.EnsureWindowFocused(window);

            if (!parameters.TryGetValue("selector", out string selectorText))
                throw new UnityUIFlowException(ErrorCodes.SelectorInvalid, "imgui_select_option requires 'selector' parameter.");

            // option can be specified by index or text
            int optionIndex = -1;
            string optionText = null;
            if (parameters.TryGetValue("option", out string optionValue))
            {
                if (int.TryParse(optionValue, out int idx))
                    optionIndex = idx;
                else
                    optionText = optionValue;
            }
            else if (parameters.TryGetValue("index", out string indexValue) && int.TryParse(indexValue, out int idx2))
            {
                optionIndex = idx2;
            }

            if (optionIndex < 0 && string.IsNullOrEmpty(optionText))
                throw new UnityUIFlowException(ErrorCodes.ActionParameterMissing, "imgui_select_option requires 'option' or 'index' parameter.");

            int targetIndex = optionIndex >= 0 ? optionIndex : 0;

            // Primary path: when field_name is provided, bypass IMGUI event simulation entirely
            // and directly set the backing field via reflection. This is required because
            // EditorGUILayout.Popup opens a native OS menu that cannot receive SendEvent keyboard
            // events, and when Unity has no focus the EditorApplication.update frequency drops
            // to near-zero, causing the event-queue-based command to hang indefinitely.
            if (parameters.TryGetValue("field_name", out string fieldName))
            {
                if (!ImguiActionHelper.TrySetFieldValue(window, fieldName, targetIndex))
                {
                    throw new UnityUIFlowException(ErrorCodes.ActionExecutionFailed,
                        $"imgui_select_option: failed to set field '{fieldName}' to {targetIndex} via reflection on {window.GetType().Name}.");
                }

                // Best-effort highlight of the dropdown element using the latest snapshot
                var bridge = GetOrCreateBridge(window);
                var snapshot = bridge.GetLastSnapshot();
                if (snapshot != null)
                {
                    try
                    {
                        var selector = ImguiSelectorCompiler.Compile(selectorText);
                        var entry = ImguiElementLocator.Find(snapshot, selector);
                        if (entry != null)
                            ImguiActionHelper.Highlight(entry, "imgui_select_option", window);
                    }
                    catch { }
                }

                await Task.Delay(100, context.CancellationToken);
                return;
            }

            // Fallback path: attempt event simulation (best-effort, unreliable for EditorGUILayout.Popup)
            var bridge2 = GetOrCreateBridge(window);
            await ImguiActionHelper.ExecuteCommandAsync(
                bridge2,
                tcs => new ImguiSelectOptionCommand(parameters, optionIndex, optionText, tcs),
                context.CancellationToken,
                postDelayMs: 100);
        }

        private static ImguiExecutionBridge GetOrCreateBridge(EditorWindow window) => ImguiBridgeRegistry.GetOrCreateBridge(window);

        private class ImguiSelectOptionCommand : ImguiCommand
        {
            private readonly Dictionary<string, string> _parameters;
            private readonly int _optionIndex;
            private readonly string _optionText;
            private readonly TaskCompletionSource<bool> _tcs;

            public ImguiSelectOptionCommand(Dictionary<string, string> parameters, int optionIndex, string optionText, TaskCompletionSource<bool> tcs)
            {
                _parameters = parameters;
                _optionIndex = optionIndex;
                _optionText = optionText;
                _tcs = tcs;
            }

            public override bool RequiresRepaintWait => true;

            public override void Execute(EditorWindow window, ImguiSnapshot snapshot)
            {
                try
                {
                    var entry = ImguiActionHelper.RequireEntry(_parameters, snapshot);
                    Vector2 center = entry.Rect.center;

                    // Click to open the popup
                    ImguiActionHelper.SendMouseEvent(window, center, EventType.MouseDown);
                    ImguiActionHelper.SendMouseEvent(window, center, EventType.MouseUp);

                    // Navigate via keyboard since popup menu is not in the GUILayout tree
                    // Send Down arrow to open popup (first Down may expand), then navigate to target
                    int targetIndex = _optionIndex >= 0 ? _optionIndex : 0;
                    if (!string.IsNullOrEmpty(_optionText))
                    {
                        // When text is provided but index is not, we attempt to find by text in the snapshot
                        // This is best-effort; IMGUI popup menus are outside the tree
                        targetIndex = 0;
                    }

                    // Open popup with Down arrow
                    window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.DownArrow });

                    // Navigate to target index
                    for (int i = 0; i < targetIndex; i++)
                    {
                        window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.DownArrow });
                    }

                    // Confirm selection
                    window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Return });

                    ImguiActionHelper.Highlight(entry, "imgui_select_option", window);
                    _tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                }
            }
        }
    }

    // ── imgui_assert_value ──

    [ActionName("imgui_assert_value")]
    public sealed class ImguiAssertValueAction : IAction
    {
        public Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            return ExecuteAsyncInternal(context, parameters);
        }

        private async Task ExecuteAsyncInternal(ActionContext context, Dictionary<string, string> parameters)
        {
            var window = ImguiActionHelper.ResolveWindow(context, "imgui_assert_value");
            ImguiActionHelper.EnsureWindowFocused(window);

            if (!parameters.TryGetValue("expected", out string expectedValue))
                throw new UnityUIFlowException(ErrorCodes.ActionParameterMissing, "imgui_assert_value requires 'expected' parameter.");

            var bridge = GetOrCreateBridge(window);
            await ImguiActionHelper.ExecuteCommandAsync(
                bridge,
                tcs => new ImguiAssertValueCommand(parameters, expectedValue, tcs),
                context.CancellationToken);
        }

        private static ImguiExecutionBridge GetOrCreateBridge(EditorWindow window) => ImguiBridgeRegistry.GetOrCreateBridge(window);

        private class ImguiAssertValueCommand : ImguiCommand
        {
            private readonly Dictionary<string, string> _parameters;
            private readonly string _expectedValue;
            private readonly TaskCompletionSource<bool> _tcs;

            public ImguiAssertValueCommand(Dictionary<string, string> parameters, string expectedValue, TaskCompletionSource<bool> tcs)
            {
                _parameters = parameters;
                _expectedValue = expectedValue;
                _tcs = tcs;
            }

            public override void Execute(EditorWindow window, ImguiSnapshot snapshot)
            {
                try
                {
                    var entry = ImguiActionHelper.RequireEntry(_parameters, snapshot);
                    string actualValue = ResolveValue(entry);

                    if (!string.Equals(actualValue, _expectedValue, StringComparison.OrdinalIgnoreCase))
                    {
                        _tcs.TrySetException(new UnityUIFlowException(ErrorCodes.AssertionFailed,
                            $"imgui_assert_value failed. Expected: '{_expectedValue}', Actual: '{actualValue}', Type: {entry.InferredType}"));
                        return;
                    }

                    ImguiActionHelper.Highlight(entry, "imgui_assert_value", window);
                    _tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                }
            }

            private static string ResolveValue(ImguiSnapshotEntry entry)
            {
                // IMGUI GUILayoutEntry does not store typed values.
                // Best-effort resolution based on available metadata:
                // 1. Text content (labels, buttons with text)
                if (!string.IsNullOrEmpty(entry.Text))
                    return entry.Text;

                // 2. For toggle: inspect style name hints (Unity sometimes uses "Toggle" vs "ToggleMixed")
                if (entry.InferredType == "toggle")
                {
                    string style = entry.StyleName?.ToLowerInvariant() ?? "";
                    if (style.Contains("mixed"))
                        return "mixed";
                    // Without deeper reflection, we cannot reliably know on/off state
                    // Fallback: report that we cannot determine the value
                    return "unknown";
                }

                // 3. For slider: no value available in snapshot
                if (entry.InferredType == "slider")
                    return "unknown";

                // 4. For dropdown: text may contain current selection
                if (entry.InferredType == "dropdown")
                    return entry.Text ?? "unknown";

                return "unknown";
            }
        }
    }

    // ── imgui_read_value ──

    [ActionName("imgui_read_value")]
    public sealed class ImguiReadValueAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            var window = ImguiActionHelper.ResolveWindow(context, "imgui_read_value");
            ImguiActionHelper.EnsureWindowFocused(window);

            if (!parameters.TryGetValue("selector", out string selectorText))
                throw new UnityUIFlowException(ErrorCodes.SelectorInvalid, "imgui_read_value requires 'selector' parameter.");

            string bagKey = parameters.TryGetValue("bag_key", out string bk) ? bk : "imgui_value";

            var bridge = GetOrCreateBridge(window);
            // Force a repaint to ensure snapshot is fresh
            bridge.Enqueue(new ImguiReadValueRefreshCommand());
            await Task.Delay(50, context.CancellationToken);

            var snapshot = bridge.GetLastSnapshot();
            if (snapshot == null)
                throw new UnityUIFlowException(ErrorCodes.ElementNotFound, "IMGUI snapshot not available.");

            var selector = ImguiSelectorCompiler.Compile(selectorText);
            var entry = ImguiElementLocator.Find(snapshot, selector);
            if (entry == null)
                throw new UnityUIFlowException(ErrorCodes.ElementNotFound, $"IMGUI element not found: {selectorText}");

            string value = ResolveValue(entry);
            context.SharedBag[bagKey] = value;
            context.Log($"imgui_read_value: '{value}' stored to SharedBag['{bagKey}']");

            ImguiActionHelper.Highlight(entry, "imgui_read_value", window);
        }

        private static ImguiExecutionBridge GetOrCreateBridge(EditorWindow window) => ImguiBridgeRegistry.GetOrCreateBridge(window);

        private static string ResolveValue(ImguiSnapshotEntry entry)
        {
            if (!string.IsNullOrEmpty(entry.Text))
                return entry.Text;

            if (entry.InferredType == "toggle")
            {
                string style = entry.StyleName?.ToLowerInvariant() ?? "";
                if (style.Contains("mixed"))
                    return "mixed";
                return "unknown";
            }

            if (entry.InferredType == "dropdown")
                return entry.Text ?? "unknown";

            return "unknown";
        }

        private class ImguiReadValueRefreshCommand : ImguiCommand
        {
            public override void Execute(EditorWindow window, ImguiSnapshot snapshot) { }
        }
    }

    // ── imgui_right_click ──

    [ActionName("imgui_right_click")]
    public sealed class ImguiRightClickAction : IAction
    {
        public Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            return ExecuteAsyncInternal(context, parameters);
        }

        private async Task ExecuteAsyncInternal(ActionContext context, Dictionary<string, string> parameters)
        {
            var window = ImguiActionHelper.ResolveWindow(context, "imgui_right_click");
            ImguiActionHelper.EnsureWindowFocused(window);
            var bridge = GetOrCreateBridge(window);
            await ImguiActionHelper.ExecuteCommandAsync(
                bridge,
                tcs => new ImguiRightClickCommand(parameters, tcs),
                context.CancellationToken,
                postDelayMs: 50);
        }

        private static ImguiExecutionBridge GetOrCreateBridge(EditorWindow window) => ImguiBridgeRegistry.GetOrCreateBridge(window);

        private class ImguiRightClickCommand : ImguiCommand
        {
            private readonly Dictionary<string, string> _parameters;
            private readonly TaskCompletionSource<bool> _tcs;

            public ImguiRightClickCommand(Dictionary<string, string> parameters, TaskCompletionSource<bool> tcs)
            {
                _parameters = parameters;
                _tcs = tcs;
            }

            public override bool RequiresRepaintWait => true;

            public override void Execute(EditorWindow window, ImguiSnapshot snapshot)
            {
                try
                {
                    var entry = ImguiActionHelper.RequireEntry(_parameters, snapshot);
                    Vector2 center = entry.Rect.center;

                    // Right mouse button: button = 1
                    ImguiActionHelper.SendMouseEvent(window, center, EventType.MouseDown, button: 1);
                    ImguiActionHelper.SendMouseEvent(window, center, EventType.MouseUp, button: 1);

                    ImguiActionHelper.Highlight(entry, "imgui_right_click", window);
                    _tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                }
            }
        }
    }

    // ── imgui_hover ──

    [ActionName("imgui_hover")]
    public sealed class ImguiHoverAction : IAction
    {
        public Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            return ExecuteAsyncInternal(context, parameters);
        }

        private async Task ExecuteAsyncInternal(ActionContext context, Dictionary<string, string> parameters)
        {
            var window = ImguiActionHelper.ResolveWindow(context, "imgui_hover");
            ImguiActionHelper.EnsureWindowFocused(window);
            var bridge = GetOrCreateBridge(window);
            await ImguiActionHelper.ExecuteCommandAsync(
                bridge,
                tcs => new ImguiHoverCommand(parameters, tcs),
                context.CancellationToken,
                postDelayMs: 50);
        }

        private static ImguiExecutionBridge GetOrCreateBridge(EditorWindow window) => ImguiBridgeRegistry.GetOrCreateBridge(window);

        private class ImguiHoverCommand : ImguiCommand
        {
            private readonly Dictionary<string, string> _parameters;
            private readonly TaskCompletionSource<bool> _tcs;

            public ImguiHoverCommand(Dictionary<string, string> parameters, TaskCompletionSource<bool> tcs)
            {
                _parameters = parameters;
                _tcs = tcs;
            }

            public override void Execute(EditorWindow window, ImguiSnapshot snapshot)
            {
                try
                {
                    var entry = ImguiActionHelper.RequireEntry(_parameters, snapshot);
                    Vector2 center = entry.Rect.center;

                    // Send MouseMove (IMGUI uses MouseMove for hover, not MouseEnter)
                    var bridge = ImguiBridgeRegistry.GetOrCreateBridge(window);
                    Vector2 offset = bridge?.WindowToContentOffset ?? Vector2.zero;
                    Vector2 windowPos = center + offset;

                    var evt = new Event
                    {
                        type = EventType.MouseMove,
                        mousePosition = windowPos,
                    };
                    window.SendEvent(evt);

                    ImguiActionHelper.Highlight(entry, "imgui_hover", window);
                    _tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                }
            }
        }
    }

    // ── imgui_press_key ──

    [ActionName("imgui_press_key")]
    public sealed class ImguiPressKeyAction : IAction
    {
        public Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            return ExecuteAsyncInternal(context, parameters);
        }

        private async Task ExecuteAsyncInternal(ActionContext context, Dictionary<string, string> parameters)
        {
            var window = ImguiActionHelper.ResolveWindow(context, "imgui_press_key");
            ImguiActionHelper.EnsureWindowFocused(window);

            string key = ActionHelpers.Require(parameters, "imgui_press_key", "key");
            if (!Enum.TryParse(key, true, out KeyCode keyCode))
            {
                throw new UnityUIFlowException(ErrorCodes.ActionParameterInvalid, $"imgui_press_key parameter 'key' is invalid: '{key}'");
            }

            var bridge = GetOrCreateBridge(window);
            await ImguiActionHelper.ExecuteCommandAsync(
                bridge,
                tcs => new ImguiPressKeyCommand(parameters, keyCode, tcs),
                context.CancellationToken,
                postDelayMs: 50);
        }

        private static ImguiExecutionBridge GetOrCreateBridge(EditorWindow window) => ImguiBridgeRegistry.GetOrCreateBridge(window);

        private class ImguiPressKeyCommand : ImguiCommand
        {
            private readonly Dictionary<string, string> _parameters;
            private readonly KeyCode _keyCode;
            private readonly TaskCompletionSource<bool> _tcs;

            public ImguiPressKeyCommand(Dictionary<string, string> parameters, KeyCode keyCode, TaskCompletionSource<bool> tcs)
            {
                _parameters = parameters;
                _keyCode = keyCode;
                _tcs = tcs;
            }

            public override bool RequiresRepaintWait => true;

            public override void Execute(EditorWindow window, ImguiSnapshot snapshot)
            {
                try
                {
                    // Optionally focus a target element first
                    if (_parameters.TryGetValue("selector", out string selectorText))
                    {
                        var entry = ImguiActionHelper.RequireEntry(_parameters, snapshot);
                        Vector2 center = entry.Rect.center;
                        ImguiActionHelper.SendMouseEvent(window, center, EventType.MouseDown);
                        ImguiActionHelper.SendMouseEvent(window, center, EventType.MouseUp);
                        ImguiActionHelper.Highlight(entry, "imgui_press_key", window);
                    }

                    // Send KeyDown + KeyUp
                    window.SendEvent(new Event { type = EventType.KeyDown, keyCode = _keyCode });
                    window.SendEvent(new Event { type = EventType.KeyUp, keyCode = _keyCode });

                    _tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                }
            }
        }
    }

    // ── imgui_press_key_combination ──

    [ActionName("imgui_press_key_combination")]
    public sealed class ImguiPressKeyCombinationAction : IAction
    {
        public Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            return ExecuteAsyncInternal(context, parameters);
        }

        private async Task ExecuteAsyncInternal(ActionContext context, Dictionary<string, string> parameters)
        {
            var window = ImguiActionHelper.ResolveWindow(context, "imgui_press_key_combination");
            ImguiActionHelper.EnsureWindowFocused(window);

            string keys = ActionHelpers.Require(parameters, "imgui_press_key_combination", "keys");
            var parts = keys.Split('+').Select(p => p.Trim()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (parts.Count < 2)
            {
                throw new UnityUIFlowException(ErrorCodes.ActionParameterInvalid, "imgui_press_key_combination parameter 'keys' must contain at least two keys separated by '+'.");
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
                else
                {
                    throw new UnityUIFlowException(ErrorCodes.ActionParameterInvalid, $"Unknown key in combination: '{part}'");
                }
            }

            if (keyCodes.Count == 0)
                throw new UnityUIFlowException(ErrorCodes.ActionParameterInvalid, "imgui_press_key_combination must contain at least one non-modifier key.");

            var bridge = GetOrCreateBridge(window);
            await ImguiActionHelper.ExecuteCommandAsync(
                bridge,
                tcs => new ImguiPressKeyCombinationCommand(parameters, modifiers, keyCodes, tcs),
                context.CancellationToken,
                postDelayMs: 50);
        }

        private static ImguiExecutionBridge GetOrCreateBridge(EditorWindow window) => ImguiBridgeRegistry.GetOrCreateBridge(window);

        private class ImguiPressKeyCombinationCommand : ImguiCommand
        {
            private readonly Dictionary<string, string> _parameters;
            private readonly EventModifiers _modifiers;
            private readonly List<KeyCode> _keyCodes;
            private readonly TaskCompletionSource<bool> _tcs;

            public ImguiPressKeyCombinationCommand(Dictionary<string, string> parameters, EventModifiers modifiers, List<KeyCode> keyCodes, TaskCompletionSource<bool> tcs)
            {
                _parameters = parameters;
                _modifiers = modifiers;
                _keyCodes = keyCodes;
                _tcs = tcs;
            }

            public override bool RequiresRepaintWait => true;

            public override void Execute(EditorWindow window, ImguiSnapshot snapshot)
            {
                try
                {
                    if (_parameters.TryGetValue("selector", out string selectorText))
                    {
                        var entry = ImguiActionHelper.RequireEntry(_parameters, snapshot);
                        Vector2 center = entry.Rect.center;
                        ImguiActionHelper.SendMouseEvent(window, center, EventType.MouseDown);
                        ImguiActionHelper.SendMouseEvent(window, center, EventType.MouseUp);
                        ImguiActionHelper.Highlight(entry, "imgui_press_key_combination", window);
                    }

                    // Press all keys with modifiers held
                    foreach (var key in _keyCodes)
                    {
                        window.SendEvent(new Event { type = EventType.KeyDown, keyCode = key, modifiers = _modifiers });
                    }
                    // Release in reverse order
                    for (int i = _keyCodes.Count - 1; i >= 0; i--)
                    {
                        window.SendEvent(new Event { type = EventType.KeyUp, keyCode = _keyCodes[i], modifiers = _modifiers });
                    }

                    _tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                }
            }
        }
    }
}
