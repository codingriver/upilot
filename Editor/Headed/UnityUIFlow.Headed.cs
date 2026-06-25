using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityUIFlow
{
    /// <summary>
    /// Event bus used by the headed UI.
    /// </summary>
    public static class HeadedRunEventBus
    {
        public static event Action<RuntimeController, string> RunAttached;
        public static event Action<ExecutableStep> StepStarted;
        public static event Action<ExecutableStep, StepResult, VisualElement> StepCompleted;
        public static event Action<ExecutableStep, VisualElement> HighlightedElementChanged;
        public static event Action<ExecutableStep, StepResult> Failure;
        public static event Action<TestResult> RunFinished;

        public static void PublishRunAttached(RuntimeController controller, string caseName) => RunAttached?.Invoke(controller, caseName);
        public static void PublishStepStarted(ExecutableStep step) => StepStarted?.Invoke(step);
        public static void PublishStepCompleted(ExecutableStep step, StepResult result, VisualElement element) => StepCompleted?.Invoke(step, result, element);
        public static void PublishHighlightedElement(ExecutableStep step, VisualElement element) => HighlightedElementChanged?.Invoke(step, element);
        public static void PublishFailure(ExecutableStep step, StepResult result) => Failure?.Invoke(step, result);
        public static void PublishRunFinished(TestResult result) => RunFinished?.Invoke(result);
    }

    /// <summary>
    /// Renders a highlight overlay above a target window.
    /// </summary>
    public sealed class HighlightOverlayRenderer
    {
        private EditorWindow _window;
        private VisualElement _overlayRoot;
        private VisualElement _marker;
        private Label _label;
        private IVisualElementScheduledItem _pulseAnimation;

        public void Attach(EditorWindow window)
        {
            if (_window == window && _overlayRoot != null)
            {
                return;
            }

            Detach();
            _window = window;
            if (_window == null || _window.rootVisualElement == null)
            {
                return;
            }

            _overlayRoot = new VisualElement
            {
                pickingMode = PickingMode.Ignore,
            };
            _overlayRoot.style.position = Position.Absolute;
            _overlayRoot.style.left = 0;
            _overlayRoot.style.top = 0;
            _overlayRoot.style.right = 0;
            _overlayRoot.style.bottom = 0;

            _marker = new VisualElement
            {
                pickingMode = PickingMode.Ignore,
            };
            _marker.style.position = Position.Absolute;
            _marker.style.backgroundColor = new Color(1f, 0.84f, 0f, 0.25f);
            _marker.style.borderBottomColor = new Color(1f, 0.65f, 0f, 1f);
            _marker.style.borderLeftColor = new Color(1f, 0.65f, 0f, 1f);
            _marker.style.borderRightColor = new Color(1f, 0.65f, 0f, 1f);
            _marker.style.borderTopColor = new Color(1f, 0.65f, 0f, 1f);
            _marker.style.borderBottomWidth = 2;
            _marker.style.borderLeftWidth = 2;
            _marker.style.borderRightWidth = 2;
            _marker.style.borderTopWidth = 2;
            _marker.style.display = DisplayStyle.None;

            _label = new Label
            {
                pickingMode = PickingMode.Ignore,
            };
            _label.style.position = Position.Absolute;
            _label.style.backgroundColor = new Color(0.9f, 0.25f, 0.1f, 0.95f);
            _label.style.color = Color.white;
            _label.style.fontSize = 11;
            _label.style.paddingLeft = 4;
            _label.style.paddingRight = 4;
            _label.style.paddingTop = 1;
            _label.style.paddingBottom = 1;
            _label.style.borderBottomLeftRadius = 2;
            _label.style.borderBottomRightRadius = 2;
            _label.style.borderTopLeftRadius = 2;
            _label.style.borderTopRightRadius = 2;
            _label.style.display = DisplayStyle.None;

            _overlayRoot.Add(_marker);
            _overlayRoot.Add(_label);
            _window.rootVisualElement.Add(_overlayRoot);
        }

        public void Highlight(VisualElement target)
        {
            Highlight(target, "");
        }

        public void Highlight(VisualElement target, string labelText)
        {
            if (target == null || target.panel == null || _marker == null)
            {
                Clear();
                return;
            }

            if (_window == null || _window.rootVisualElement == null)
            {
                Clear();
                return;
            }

            // Convert world-bound (panel coordinates) to rootVisualElement local space.
            // This fixes offset caused by rootVisualElement borders / toolbar insets.
            Rect worldBound = target.worldBound;
            Vector2 localTopLeft = _window.rootVisualElement.WorldToLocal(worldBound.position);

            _marker.style.left = localTopLeft.x;
            _marker.style.top = localTopLeft.y;
            // Use exact target size; border is drawn outside content box in UIToolkit default box model.
            _marker.style.width = worldBound.width;
            _marker.style.height = worldBound.height;
            _marker.style.display = DisplayStyle.Flex;

            if (!string.IsNullOrEmpty(labelText) && _label != null)
            {
                _label.text = labelText;
                _label.style.left = localTopLeft.x;
                _label.style.top = localTopLeft.y - 18;
                _label.style.display = DisplayStyle.Flex;
            }
            else if (_label != null)
            {
                _label.style.display = DisplayStyle.None;
            }

            StopPulseAnimation();
            StartPulseAnimation();
        }

        /// <summary>
        /// Highlights a IMGUI control by its window-local rect.
        /// The rect is in EditorWindow client-area coordinates.
        /// </summary>
        public void HighlightRect(Rect windowLocalRect, string labelText)
        {
            if (_marker == null)
            {
                Clear();
                return;
            }

            if (_window == null || _window.rootVisualElement == null)
            {
                Clear();
                return;
            }

            // Convert window-local coordinates to rootVisualElement local space.
            // Window-local (0,0) is the client-area top-left, which equals the panel origin.
            // rootVisualElement local space may be inset by borders.
            Vector2 panelPos = _window.rootVisualElement.LocalToWorld(Vector2.zero) + windowLocalRect.position;
            Vector2 localTopLeft = _window.rootVisualElement.WorldToLocal(panelPos);

            _marker.style.left = localTopLeft.x;
            _marker.style.top = localTopLeft.y;
            _marker.style.width = windowLocalRect.width;
            _marker.style.height = windowLocalRect.height;
            _marker.style.display = DisplayStyle.Flex;

            if (!string.IsNullOrEmpty(labelText) && _label != null)
            {
                _label.text = labelText;
                _label.style.left = localTopLeft.x;
                _label.style.top = localTopLeft.y - 18;
                _label.style.display = DisplayStyle.Flex;
            }
            else if (_label != null)
            {
                _label.style.display = DisplayStyle.None;
            }

            StopPulseAnimation();
            StartPulseAnimation();
        }

        private void StartPulseAnimation()
        {
            if (_marker == null) return;
            float baseAlpha = 0.25f;
            float t = 0f;
            _pulseAnimation = _marker.schedule.Execute(() =>
            {
                t += 0.15f;
                float pulse = Mathf.Abs(Mathf.Sin(t));
                float alpha = Mathf.Lerp(baseAlpha * 0.5f, baseAlpha * 1.5f, pulse);
                Color c = _marker.style.backgroundColor.value;
                _marker.style.backgroundColor = new Color(c.r, c.g, c.b, alpha);
            }).Every(50);
        }

        private void StopPulseAnimation()
        {
            if (_pulseAnimation != null)
            {
                _pulseAnimation.Pause();
                _pulseAnimation = null;
            }

            if (_marker != null)
            {
                Color c = _marker.style.backgroundColor.value;
                _marker.style.backgroundColor = new Color(c.r, c.g, c.b, 0.25f);
            }
        }

        public void Clear()
        {
            StopPulseAnimation();
            if (_marker != null)
            {
                _marker.style.display = DisplayStyle.None;
            }
            if (_label != null)
            {
                _label.style.display = DisplayStyle.None;
            }
        }

        public void ClearAfterDelay(int delayMs)
        {
            if (_marker == null) return;
            _marker.schedule.Execute(() => Clear()).StartingIn(delayMs);
        }

        public void Detach()
        {
            StopPulseAnimation();
            if (_overlayRoot != null && _overlayRoot.parent != null)
            {
                _overlayRoot.parent.Remove(_overlayRoot);
            }

            _overlayRoot = null;
            _marker = null;
            _label = null;
            _window = null;
        }
    }

    public static class StepHighlighter
    {
        private static readonly System.Collections.Generic.Dictionary<IPanel, HighlightOverlayRenderer> s_renderers = new();

        public static void Highlight(VisualElement element, string actionName, EditorWindow window)
        {
            if (element == null || window == null)
                return;

            IPanel panel = element.panel;
            if (panel == null)
                return;

            if (!s_renderers.TryGetValue(panel, out HighlightOverlayRenderer renderer))
            {
                renderer = new HighlightOverlayRenderer();
                s_renderers[panel] = renderer;
            }

            renderer.Attach(window);
            renderer.Highlight(element, actionName);
        }

        /// <summary>
        /// Highlights an IMGUI control by its window-local rect.
        /// </summary>
        public static void HighlightRect(Rect windowLocalRect, string labelText, EditorWindow window)
        {
            if (window == null)
                return;

            IPanel panel = window.rootVisualElement?.panel;
            if (panel == null)
                return;

            if (!s_renderers.TryGetValue(panel, out HighlightOverlayRenderer renderer))
            {
                renderer = new HighlightOverlayRenderer();
                s_renderers[panel] = renderer;
            }

            renderer.Attach(window);
            renderer.HighlightRect(windowLocalRect, labelText);
        }

        public static void Clear(VisualElement element)
        {
            if (element?.panel == null)
                return;

            if (s_renderers.TryGetValue(element.panel, out HighlightOverlayRenderer renderer))
            {
                renderer.Clear();
            }
        }

        public static void ClearAfterDelay(VisualElement element, int delayMs)
        {
            if (element?.panel == null)
                return;

            if (s_renderers.TryGetValue(element.panel, out HighlightOverlayRenderer renderer))
            {
                renderer.ClearAfterDelay(delayMs);
            }
        }

        /// <summary>
        /// Clears highlight for the given window's panel.
        /// </summary>
        public static void Clear(EditorWindow window)
        {
            if (window == null)
                return;

            IPanel panel = window.rootVisualElement?.panel;
            if (panel == null)
                return;

            if (s_renderers.TryGetValue(panel, out HighlightOverlayRenderer renderer))
            {
                renderer.Clear();
            }
        }
    }

    /// <summary>
    /// UIFlow menu bar items.
    /// </summary>
    public static class UnityUIFlowMenuItems
    {
        private const string PrefKey = "UnityUIFlow.VerboseLog";

        /// <summary>
        /// Returns whether verbose logging is currently enabled.
        /// </summary>
        public static bool IsVerboseLogEnabled => UnityUIFlowProjectSettingsUtility.IsVerboseLoggingEnabled(EditorPrefs.GetBool(PrefKey, false));
        

        [MenuItem("upilot/UIFlow/Settings", priority = 201)]
        private static void OpenSettingsMenu()
        {
            SettingsService.OpenProjectSettings(UnityUIFlowProjectSettingsUtility.SettingsPath);
        }
    }
}
