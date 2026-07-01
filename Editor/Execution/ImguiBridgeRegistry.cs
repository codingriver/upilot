using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using MonoHook;

namespace codingriver.upilot.UIFlow
{
    /// <summary>
    /// Centralized registry for IMGUI execution bridges.
    /// Ensures one bridge per window and handles MonoHook fallback for Unity 6000+.
    /// </summary>
    public static class ImguiBridgeRegistry
    {
        private static readonly Dictionary<EditorWindow, ImguiExecutionBridge> Bridges = new Dictionary<EditorWindow, ImguiExecutionBridge>();
        private static readonly Dictionary<Type, MethodHook> Hooks = new Dictionary<Type, MethodHook>();
        private static readonly Dictionary<EditorWindow, ImguiExecutionBridge> HookedWindows = new Dictionary<EditorWindow, ImguiExecutionBridge>();

        // GUILayoutUtility.DoGetRect hook for Unity 6000+ where GUILayoutUtility reflection no longer works
        private static MethodHook _getRectHook;
        // EditorGUILayout.Popup hook for Editor-only controls that bypass GUILayoutUtility
        private static MethodHook _editorPopupHook;
        // GUIStyle.Draw hook as additional fallback
        private static MethodHook _guiStyleDrawHook;
        private static EditorWindow _currentOnGUIWindow;
        private static readonly List<ImguiSnapshotEntry> _pendingDrawCalls = new List<ImguiSnapshotEntry>();
        private static int _onGUIRecursionDepth = 0;

        public static ImguiExecutionBridge GetOrCreateBridge(EditorWindow window)
        {
            if (window == null)
                return null;

            if (!Bridges.TryGetValue(window, out var bridge))
            {
                bridge = new ImguiExecutionBridge();
                bridge.Attach(window);
                Bridges[window] = bridge;
            }
            else if (!bridge.IsAttached && !bridge.UsesMonoHookFallback)
            {
                bridge.Attach(window);
            }
            return bridge;
        }

        public static void RemoveBridge(EditorWindow window)
        {
            if (window == null) return;
            if (Bridges.TryGetValue(window, out var bridge))
            {
                bridge.Detach();
                Bridges.Remove(window);
            }
        }

        public static void InstallOnGuiHook(EditorWindow window, ImguiExecutionBridge bridge)
        {
            if (window == null || bridge == null) return;

            var windowType = window.GetType();
            var onguiMethod = windowType.GetMethod("OnGUI", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (onguiMethod == null)
            {
                Codingriver.Logger.LogWarning($"[UIFlow] Could not find OnGUI method on {windowType.Name}");
                return;
            }

            // Track which bridge owns this window instance
            HookedWindows[window] = bridge;

            // If we already have a hook for this type, just update the window mapping
            if (Hooks.ContainsKey(windowType))
                return;

            var replacementMethod = typeof(ImguiBridgeRegistry).GetMethod(nameof(OnGUIReplacement), BindingFlags.Static | BindingFlags.NonPublic);
            var proxyMethod = typeof(ImguiBridgeRegistry).GetMethod(nameof(OnGUIProxy), BindingFlags.Static | BindingFlags.NonPublic);

            try
            {
                var hook = new MethodHook(onguiMethod, replacementMethod, proxyMethod, windowType.FullName);
                hook.Install();
                Hooks[windowType] = hook;
                Codingriver.Logger.Log($"[UIFlow] MonoHook installed on {windowType.Name}.OnGUI");
            }
            catch (Exception ex)
            {
                Codingriver.Logger.LogWarning($"[UIFlow] Failed to install MonoHook on {windowType.Name}.OnGUI: {ex.Message}");
            }
        }

        public static void UninstallOnGuiHook(EditorWindow window)
        {
            if (window == null) return;
            HookedWindows.Remove(window);

            // Only uninstall the hook if no other windows of this type are being tracked
            var windowType = window.GetType();
            bool hasOtherWindowsOfType = false;
            foreach (var w in HookedWindows.Keys)
            {
                if (w.GetType() == windowType)
                {
                    hasOtherWindowsOfType = true;
                    break;
                }
            }

            if (!hasOtherWindowsOfType && Hooks.TryGetValue(windowType, out var hook))
            {
                try
                {
                    hook.Uninstall();
                    Hooks.Remove(windowType);
                    Codingriver.Logger.Log($"[UIFlow] MonoHook uninstalled from {windowType.Name}.OnGUI");
                }
                catch (Exception ex)
                {
                    Codingriver.Logger.LogWarning($"[UIFlow] Failed to uninstall MonoHook: {ex.Message}");
                }
            }
        }

        private static void OnGUIReplacement(EditorWindow __this)
        {
            if (__this == null)
            {
                OnGUIProxy(__this);
                return;
            }

            // Get or create bridge directly from the window instance passed to OnGUI.
            var bridge = GetOrCreateBridge(__this);

            // Calculate window-to-content offset for correct event injection coordinates.
            // EditorWindow.SendEvent expects window-space coordinates, but IMGUIContainer
            // transforms them to local space. Our snapshot rects are in local space, so
            // we need to add the offset when sending events.
            if (bridge != null && bridge.UsesMonoHookFallback)
            {
                try
                {
                    Vector2 screenPos = GUIUtility.GUIToScreenPoint(Vector2.zero);
                    Vector2 windowPos = __this.position.position;
                    bridge.WindowToContentOffset = screenPos - windowPos;
                }
                catch { }
            }

            // Install hooks on first use (fallback for Unity 6000+)
            InstallGetRectHook();
            InstallEditorPopupHook();
            InstallGuiStyleDrawHook();

            // Track recursion depth to handle nested OnGUI calls caused by SendEvent
            _onGUIRecursionDepth++;
            bool isOutermost = _onGUIRecursionDepth == 1;
            var previousWindow = _currentOnGUIWindow;
            _currentOnGUIWindow = __this;

            if (isOutermost)
            {
                _pendingDrawCalls.Clear();
            }

            // In Unity 6000+, GUILayoutUtility.current is cleared after OnGUI returns.
            // Capture the layout snapshot BEFORE OnGUI during Repaint, while the layout
            // built in the preceding Layout event is still valid.
            if (Event.current?.type == EventType.Repaint)
            {
                bridge?.CaptureSnapshotEarly();
            }

            // Call original OnGUI
            OnGUIProxy(__this);

            if (isOutermost)
            {
                // If snapshot from GUILayoutUtility is empty, use recorded draw calls as fallback
                var snapshot = bridge?.GetLastSnapshot();
                if ((snapshot == null || snapshot.Entries.Count == 0) && _pendingDrawCalls.Count > 0)
                {
                    var drawSnapshot = new ImguiSnapshot
                    {
                        CapturedAt = DateTimeOffset.UtcNow,
                        SourceWindow = __this,
                    };
                    drawSnapshot.Entries.AddRange(_pendingDrawCalls);
                    for (int i = 0; i < drawSnapshot.Entries.Count; i++)
                        drawSnapshot.Entries[i].GlobalIndex = i;
                    bridge?.SetLastSnapshot(drawSnapshot);
                }

                // Dispatch post-commands (deferred to EditorApplication.update)
                bridge?.ExecutePostCommandsFromHook();

                _currentOnGUIWindow = null;
                _pendingDrawCalls.Clear();
                _onGUIRecursionDepth = 0;
            }
            else
            {
                // Nested call: restore previous window context
                _currentOnGUIWindow = previousWindow;
            }
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void OnGUIProxy(EditorWindow __this)
        {
            // This will be patched by MonoHook to call the original OnGUI
            Codingriver.Logger.LogError("[UIFlow] OnGUIProxy should never be called directly!");
        }

        /// <summary>
        /// Invokes the original (unhooked) OnGUI on the given window with the current Event.current.
        /// Used by IMGUI action commands to process synthetic events.
        /// </summary>
        public static void InvokeOriginalOnGUI(EditorWindow window)
        {
            if (window == null) return;
            try
            {
                Codingriver.Logger.Log($"[UIFlow] InvokeOriginalOnGUI calling OnGUIProxy for {window.GetType().Name}, Event.current={Event.current?.type}");
                OnGUIProxy(window);
                Codingriver.Logger.Log($"[UIFlow] InvokeOriginalOnGUI OnGUIProxy returned for {window.GetType().Name}");
            }
            catch (Exception ex) when (ex.GetType().Name.Contains("ExitGUIException") || ex.GetType().Name.Contains("TargetInvocationException"))
            {
                Codingriver.Logger.Log($"[UIFlow] InvokeOriginalOnGUI caught {ex.GetType().Name} for {window.GetType().Name}");
            }
        }

        #region GUILayoutUtility.GetRect Hook (Unity 6000+ Primary)

        private static void InstallGetRectHook()
        {
            if (_getRectHook != null) return;

            var replacementMethod = typeof(ImguiBridgeRegistry).GetMethod(nameof(DoGetRectReplacement), BindingFlags.Static | BindingFlags.NonPublic);
            var proxyMethod = typeof(ImguiBridgeRegistry).GetMethod(nameof(DoGetRectProxy), BindingFlags.Static | BindingFlags.NonPublic);

            // Try to hook GetRect(GUIContent,GUIStyle,GUILayoutOption[]) first (public entry point)
            var getRectMethod = typeof(GUILayoutUtility).GetMethod("GetRect",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(GUIContent), typeof(GUIStyle), typeof(GUILayoutOption[]) },
                null);

            if (getRectMethod != null)
            {
                try
                {
                    _getRectHook = new MethodHook(getRectMethod, replacementMethod, proxyMethod, "GUILayoutUtility.GetRect");
                    _getRectHook.Install();
                    Codingriver.Logger.Log("[UIFlow] MonoHook installed on GUILayoutUtility.GetRect");
                    return;
                }
                catch (Exception ex)
                {
                    Codingriver.Logger.LogWarning($"[UIFlow] Failed to install MonoHook on GUILayoutUtility.GetRect: {ex.Message}");
                }
            }

            // Fallback: hook DoGetRect(GUIContent,GUIStyle,GUILayoutOption[])
            var doGetRectMethod = typeof(GUILayoutUtility).GetMethods(
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "DoGetRect" &&
                    m.GetParameters().Length == 3 &&
                    m.GetParameters()[0].ParameterType == typeof(GUIContent) &&
                    m.GetParameters()[1].ParameterType == typeof(GUIStyle) &&
                    m.GetParameters()[2].ParameterType == typeof(GUILayoutOption[]));

            if (doGetRectMethod == null)
            {
                Codingriver.Logger.LogWarning("[UIFlow] Could not find GUILayoutUtility.DoGetRect for hooking.");
                return;
            }

            try
            {
                _getRectHook = new MethodHook(doGetRectMethod, replacementMethod, proxyMethod, "GUILayoutUtility.DoGetRect");
                _getRectHook.Install();
                Codingriver.Logger.Log("[UIFlow] MonoHook installed on GUILayoutUtility.DoGetRect");
            }
            catch (Exception ex)
            {
                Codingriver.Logger.LogWarning($"[UIFlow] Failed to install MonoHook on GUILayoutUtility.DoGetRect: {ex.Message}");
            }
        }

        private static Rect DoGetRectReplacement(GUIContent content, GUIStyle style, GUILayoutOption[] options)
        {
            var result = DoGetRectProxy(content, style, options);

            if (_currentOnGUIWindow != null)
            {
                string styleName = style?.name ?? "unknown";
                Codingriver.Logger.Log($"[UIFlow] DoGetRectReplacement: style={styleName}, content={content?.text}, rect={result}");
                _pendingDrawCalls.Add(new ImguiSnapshotEntry
                {
                    Rect = result,
                    Text = content?.text,
                    StyleName = styleName,
                    InferredType = InferControlTypeFromStyle(styleName),
                    GroupName = "root",
                    Depth = 0,
                });
            }

            return result;
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static Rect DoGetRectProxy(GUIContent content, GUIStyle style, GUILayoutOption[] options)
        {
            // Will be patched by MonoHook
            return default;
        }

        private static void InstallEditorPopupHook()
        {
            if (_editorPopupHook != null) return;

            // EditorGUILayout.Popup(int, string[], GUILayoutOption[]) is used by IMGUI example window.
            // It may bypass GUILayoutUtility.GetRect and use its own layout path in Unity 6000+.
            var popupMethod = typeof(UnityEditor.EditorGUILayout).GetMethods(
                BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "Popup" &&
                    m.GetParameters().Length == 3 &&
                    m.GetParameters()[0].ParameterType == typeof(int) &&
                    m.GetParameters()[1].ParameterType == typeof(string[]) &&
                    m.GetParameters()[2].ParameterType == typeof(GUILayoutOption[]));

            if (popupMethod == null)
            {
                Codingriver.Logger.LogWarning("[UIFlow] Could not find EditorGUILayout.Popup for hooking.");
                return;
            }

            var replacementMethod = typeof(ImguiBridgeRegistry).GetMethod(nameof(EditorPopupReplacement), BindingFlags.Static | BindingFlags.NonPublic);
            var proxyMethod = typeof(ImguiBridgeRegistry).GetMethod(nameof(EditorPopupProxy), BindingFlags.Static | BindingFlags.NonPublic);

            try
            {
                _editorPopupHook = new MethodHook(popupMethod, replacementMethod, proxyMethod, "EditorGUILayout.Popup");
                _editorPopupHook.Install();
                Codingriver.Logger.Log("[UIFlow] MonoHook installed on EditorGUILayout.Popup");
            }
            catch (Exception ex)
            {
                Codingriver.Logger.LogWarning($"[UIFlow] Failed to install MonoHook on EditorGUILayout.Popup: {ex.Message}");
            }
        }

        private static int EditorPopupReplacement(int selectedIndex, string[] displayedOptions, GUILayoutOption[] options)
        {
            var result = EditorPopupProxy(selectedIndex, displayedOptions, options);

            if (_currentOnGUIWindow != null)
            {
                var rect = GUILayoutUtility.GetLastRect();
                _pendingDrawCalls.Add(new ImguiSnapshotEntry
                {
                    Rect = rect,
                    Text = displayedOptions?[selectedIndex],
                    StyleName = "popup",
                    InferredType = "dropdown",
                    GroupName = "root",
                    Depth = 0,
                });
            }

            return result;
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static int EditorPopupProxy(int selectedIndex, string[] displayedOptions, GUILayoutOption[] options)
        {
            return 0;
        }

        #endregion

        #region GUIStyle.Draw Hook (Unity 6000+ Secondary Fallback)

        private static void InstallGuiStyleDrawHook()
        {
            if (_guiStyleDrawHook != null) return;

            var drawMethod = typeof(GUIStyle).GetMethod("Draw",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(Rect), typeof(GUIContent), typeof(bool), typeof(bool), typeof(bool), typeof(bool) },
                null);

            if (drawMethod == null)
            {
                Codingriver.Logger.LogWarning("[UIFlow] Could not find GUIStyle.Draw for hooking.");
                return;
            }

            var replacementMethod = typeof(ImguiBridgeRegistry).GetMethod(nameof(DrawReplacement), BindingFlags.Static | BindingFlags.NonPublic);
            var proxyMethod = typeof(ImguiBridgeRegistry).GetMethod(nameof(DrawProxy), BindingFlags.Static | BindingFlags.NonPublic);

            try
            {
                _guiStyleDrawHook = new MethodHook(drawMethod, replacementMethod, proxyMethod, "GUIStyle.Draw");
                _guiStyleDrawHook.Install();
                Codingriver.Logger.Log("[UIFlow] MonoHook installed on GUIStyle.Draw");
            }
            catch (Exception ex)
            {
                Codingriver.Logger.LogWarning($"[UIFlow] Failed to install MonoHook on GUIStyle.Draw: {ex.Message}");
            }
        }

        private static void DrawReplacement(GUIStyle __this, Rect position, GUIContent content, bool isHover, bool isActive, bool on, bool hasKeyboardFocus)
        {
            if (_currentOnGUIWindow != null && position.width > 0 && position.height > 0)
            {
                string styleName = __this?.name ?? "unknown";
                bool isSystemStyle = styleName.Contains("Window") || styleName.Contains("PaneOptions")
                    || styleName.Contains("DropDownButton") || styleName == "ToolbarDropDown"
                    || styleName == "ToolbarButton" || styleName == "ToolbarTextField";

                if (!isSystemStyle)
                {
                    _pendingDrawCalls.Add(new ImguiSnapshotEntry
                    {
                        Rect = position,
                        Text = content?.text,
                        StyleName = styleName,
                        InferredType = InferControlTypeFromStyle(styleName),
                        GroupName = "root",
                        Depth = 0,
                    });
                }
            }
            DrawProxy(__this, position, content, isHover, isActive, on, hasKeyboardFocus);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static void DrawProxy(GUIStyle __this, Rect position, GUIContent content, bool isHover, bool isActive, bool on, bool hasKeyboardFocus)
        {
            Codingriver.Logger.LogError("[UIFlow] DrawProxy should never be called directly!");
        }

        #endregion

        private static string InferControlTypeFromStyle(string styleName)
        {
            if (string.IsNullOrEmpty(styleName))
                return "unknown";

            string s = styleName.ToLowerInvariant();
            if (s.Contains("button")) return "button";
            if (s.Contains("textfield") || s.Contains("text field")) return "textfield";
            if (s.Contains("toggle")) return "toggle";
            if (s.Contains("label")) return "label";
            if (s.Contains("popup") || s.Contains("dropdown") || s.Contains("pulldown")) return "dropdown";
            if (s.Contains("toolbar")) return "toolbar";
            if (s.Contains("slider") || s.Contains("minmaxslider")) return "slider";
            if (s.Contains("scrollview") || s.Contains("scrollview")) return "scroller";
            if (s.Contains("box") || s.Contains("group")) return "group";
            if (s.Contains("foldout")) return "foldout";
            return "unknown";
        }
    }
}
