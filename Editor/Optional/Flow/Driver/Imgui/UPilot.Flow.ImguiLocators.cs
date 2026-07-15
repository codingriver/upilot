using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CodingRiver.UPilot.Flow
{
    /// <summary>
    /// Captures IMGUI layout snapshots by reflecting into GUILayoutUtility internals.
    /// </summary>
    public static class ImguiSnapshotCapture
    {
        private static readonly BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly BindingFlags NonPublicStatic = BindingFlags.NonPublic | BindingFlags.Static;

        // Cached reflection members to avoid repeated lookups
        private static FieldInfo _guiLayoutUtilityCurrentField;
        private static FieldInfo _guiLayoutGroupEntriesField;
        private static FieldInfo _guiLayoutEntryRectField;
        private static FieldInfo _guiLayoutEntryStyleField;
        private static FieldInfo _guiLayoutEntryTextField;
        private static FieldInfo _guiLayoutGroupNameField;
        private static Type _guiLayoutUtilityType;
        private static Type _guiLayoutGroupType;
        private static Type _guiLayoutEntryType;

        private static bool _reflectionInitialized;

        /// <summary>
        /// Attempts to capture the current IMGUI layout state from the given window.
        /// Returns null if reflection fails or the window has no IMGUI content.
        /// </summary>
        public static ImguiSnapshot Capture(EditorWindow window)
        {
            if (window == null)
                return null;

            if (!_reflectionInitialized)
            {
                if (!InitializeReflection())
                    return null;
            }

            var snapshot = new ImguiSnapshot
            {
                CapturedAt = DateTimeOffset.UtcNow,
                SourceWindow = window,
            };

            try
            {
                object current = _guiLayoutUtilityCurrentField?.GetValue(null);
                if (current == null)
                    return snapshot; // empty snapshot

                object topLevel = current.GetType().GetField("topLevel", NonPublicInstance)?.GetValue(current);
                if (topLevel != null)
                {
                    TraverseLayoutGroup(topLevel, snapshot.Entries, depth: 0, groupName: "root");
                }
            }
            catch (Exception ex)
            {
                Codingriver.Logger.LogWarning($"[UPilot Flow] IMGUI snapshot capture failed: {ex.Message}");
            }

            // Assign global indices after full capture
            for (int i = 0; i < snapshot.Entries.Count; i++)
                snapshot.Entries[i].GlobalIndex = i;

            return snapshot;
        }

        private static void TraverseLayoutGroup(object group, List<ImguiSnapshotEntry> results, int depth, string groupName)
        {
            if (group == null) return;

            var entriesList = _guiLayoutGroupEntriesField?.GetValue(group) as IList;
            if (entriesList == null) return;

            // Try to read group-level name/style for grouping context
            string currentGroupName = groupName;
            var groupStyle = _guiLayoutEntryStyleField?.GetValue(group);
            if (groupStyle != null)
            {
                string styleName = groupStyle.GetType().GetProperty("name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(groupStyle) as string;
                if (!string.IsNullOrEmpty(styleName) && styleName != "null")
                    currentGroupName = styleName;
            }

            foreach (object entry in entriesList)
            {
                if (entry == null) continue;

                // Check if this entry is itself a group
                bool isGroup = _guiLayoutGroupType != null && _guiLayoutGroupType.IsAssignableFrom(entry.GetType());

                if (isGroup)
                {
                    TraverseLayoutGroup(entry, results, depth + 1, currentGroupName);
                }
                else
                {
                    var snapshotEntry = ExtractEntry(entry, depth, currentGroupName);
                    if (snapshotEntry != null)
                        results.Add(snapshotEntry);
                }
            }
        }

        private static ImguiSnapshotEntry ExtractEntry(object entry, int depth, string groupName)
        {
            try
            {
                var rect = (Rect)(_guiLayoutEntryRectField?.GetValue(entry) ?? new Rect());
                var style = _guiLayoutEntryStyleField?.GetValue(entry);
                string styleName = null;
                string text = null;

                if (style != null)
                {
                    styleName = style.GetType().GetProperty("name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(style) as string;

                    // Try to extract text from style or entry internals
                    // GUILayoutEntry does not expose text publicly, but some subclasses do
                    var textProp = entry.GetType().GetProperty("text", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                    if (textProp != null)
                    {
                        text = textProp.GetValue(entry) as string;
                    }
                    else
                    {
                        var textField = entry.GetType().GetField("text", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                        if (textField != null)
                            text = textField.GetValue(entry) as string;
                    }
                    if (string.IsNullOrEmpty(text))
                    {
                        var mTextField = entry.GetType().GetField("m_Text", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                        if (mTextField != null)
                            text = mTextField.GetValue(entry) as string;
                    }
                }

                return new ImguiSnapshotEntry
                {
                    Rect = rect,
                    StyleName = styleName ?? "unknown",
                    InferredType = InferControlType(styleName),
                    Text = text,
                    GroupName = groupName ?? "root",
                    Depth = depth,
                };
            }
            catch
            {
                return null;
            }
        }

        private static string InferControlType(string styleName)
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

        private static bool InitializeReflection()
        {
            try
            {
                _guiLayoutUtilityType = typeof(GUILayoutUtility);
                _guiLayoutUtilityCurrentField = _guiLayoutUtilityType.GetField("current", NonPublicStatic);

                if (_guiLayoutUtilityCurrentField == null)
                {
                    Codingriver.Logger.LogWarning("[UPilot Flow] GUILayoutUtility.current field not found via reflection. IMGUI automation unavailable.");
                    return false;
                }

                // Determine GUILayoutGroup and GUILayoutEntry types from the same assembly
                var guiLayoutUtilityAssembly = _guiLayoutUtilityType.Assembly;
                _guiLayoutGroupType = guiLayoutUtilityAssembly.GetType("UnityEngine.GUILayoutGroup");
                _guiLayoutEntryType = guiLayoutUtilityAssembly.GetType("UnityEngine.GUILayoutEntry");

                if (_guiLayoutGroupType == null || _guiLayoutEntryType == null)
                {
                    Codingriver.Logger.LogWarning("[UPilot Flow] GUILayoutGroup or GUILayoutEntry type not found. IMGUI automation unavailable.");
                    return false;
                }

                _guiLayoutGroupEntriesField = _guiLayoutGroupType.GetField("entries", NonPublicInstance);
                _guiLayoutEntryRectField = _guiLayoutEntryType.GetField("rect", NonPublicInstance);
                _guiLayoutEntryStyleField = _guiLayoutEntryType.GetField("style", NonPublicInstance);
                _guiLayoutEntryTextField = _guiLayoutEntryType.GetField("text", NonPublicInstance);
                _guiLayoutGroupNameField = _guiLayoutGroupType.GetField("name", NonPublicInstance);

                _reflectionInitialized = true;
                return true;
            }
            catch (Exception ex)
            {
                Codingriver.Logger.LogWarning($"[UPilot Flow] IMGUI reflection initialization failed: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Locates IMGUI controls from a captured snapshot.
    /// </summary>
    public static class ImguiElementLocator
    {
        /// <summary>
        /// Finds the first entry matching the selector from the most recent snapshot.
        /// Returns null if not found.
        /// </summary>
        public static ImguiSnapshotEntry Find(ImguiSnapshot snapshot, ImguiSelector selector)
        {
            if (snapshot == null || snapshot.Entries == null || snapshot.Entries.Count == 0)
                return null;

            if (selector.Focused)
            {
                return FindFocused(snapshot);
            }

            var candidates = new List<ImguiSnapshotEntry>();
            foreach (var entry in snapshot.Entries)
            {
                if (MatchesType(entry, selector.Type) &&
                    MatchesText(entry, selector.Text) &&
                    MatchesGroup(entry, selector.Group) &&
                    MatchesControlName(entry, selector.ControlName))
                {
                    candidates.Add(entry);
                }
            }

            if (candidates.Count == 0)
                return null;

            if (selector.Index >= 0 && selector.Index < candidates.Count)
                return candidates[selector.Index];

            return candidates[0];
        }

        /// <summary>
        /// Finds all entries matching the selector.
        /// </summary>
        public static List<ImguiSnapshotEntry> FindAll(ImguiSnapshot snapshot, ImguiSelector selector)
        {
            var results = new List<ImguiSnapshotEntry>();
            if (snapshot == null || snapshot.Entries == null)
                return results;

            foreach (var entry in snapshot.Entries)
            {
                if (MatchesType(entry, selector.Type) &&
                    MatchesText(entry, selector.Text) &&
                    MatchesGroup(entry, selector.Group) &&
                    MatchesControlName(entry, selector.ControlName))
                {
                    results.Add(entry);
                }
            }
            return results;
        }

        private static ImguiSnapshotEntry FindFocused(ImguiSnapshot snapshot)
        {
            int focusedControl = GUIUtility.keyboardControl;
            if (focusedControl <= 0)
                return null;

            // IMGUI does not expose a direct control-id-to-rect mapping publicly.
            // Best-effort: return the entry whose rect contains the current mouse position
            // if the focused control is a known interactive element.
            // This is a heuristic fallback.
            Vector2 mouse = Event.current?.mousePosition ?? GUIUtility.GUIToScreenPoint(Event.current?.mousePosition ?? Vector2.zero);
            foreach (var entry in snapshot.Entries)
            {
                if (entry.Rect.Contains(mouse) &&
                    (entry.InferredType == "textfield" ||
                     entry.InferredType == "button" ||
                     entry.InferredType == "toggle" ||
                     entry.InferredType == "dropdown"))
                {
                    return entry;
                }
            }
            return null;
        }

        private static bool MatchesType(ImguiSnapshotEntry entry, string type)
        {
            if (string.IsNullOrEmpty(type))
                return true;
            return string.Equals(entry.InferredType, type, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesText(ImguiSnapshotEntry entry, string text)
        {
            if (string.IsNullOrEmpty(text))
                return true;
            if (string.IsNullOrEmpty(entry.Text))
                return false;
            return entry.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool MatchesGroup(ImguiSnapshotEntry entry, string group)
        {
            if (string.IsNullOrEmpty(group))
                return true;
            if (string.IsNullOrEmpty(entry.GroupName))
                return false;
            return entry.GroupName.IndexOf(group, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool MatchesControlName(ImguiSnapshotEntry entry, string controlName)
        {
            if (string.IsNullOrEmpty(controlName))
                return true;
            // ControlName is not stored in GUILayoutEntry. This requires instrumentation.
            // Returning true here allows instrumentation to filter separately.
            return true;
        }
    }

    /// <summary>
    /// Bridges automation commands into the IMGUI repaint cycle.
    /// </summary>
    public sealed class ImguiExecutionBridge
    {
        private EditorWindow _window;
        private IMGUIContainer _imguiContainer;
        private Action _originalHandler;
        private readonly Queue<ImguiCommand> _pendingCommands = new Queue<ImguiCommand>();
        private ImguiSnapshot _lastSnapshot;
        private bool _useMonoHookFallback;
        private bool _isInDeferredExecution;
        private EditorApplication.CallbackFunction _deferredCallback;

        public bool IsAttached => _imguiContainer != null && _imguiContainer.onGUIHandler == OnGuiHook;
        public bool UsesMonoHookFallback => _useMonoHookFallback;
        public Vector2 WindowToContentOffset { get; set; }

        public void Attach(EditorWindow window)
        {
            if (_window == window && (IsAttached || _useMonoHookFallback))
                return;

            Detach();
            _window = window;
            if (window == null)
                return;

            _imguiContainer = window.rootVisualElement?.Q<IMGUIContainer>();
            if (_imguiContainer == null && window.rootVisualElement != null)
            {
                _imguiContainer = window.rootVisualElement.Query<IMGUIContainer>().First();
            }
            if (_imguiContainer == null)
            {
                // Fallback for Unity 6000+ where IMGUIContainer is not exposed in visual tree
                _useMonoHookFallback = true;
                ImguiBridgeRegistry.InstallOnGuiHook(window, this);
                return;
            }

            _originalHandler = _imguiContainer.onGUIHandler;
            _imguiContainer.onGUIHandler = OnGuiHook;
        }

        public void Detach()
        {
            if (_imguiContainer != null && _originalHandler != null)
            {
                try { _imguiContainer.onGUIHandler = _originalHandler; } catch { }
            }
            if (_useMonoHookFallback && _window != null)
            {
                ImguiBridgeRegistry.UninstallOnGuiHook(_window);
            }
            _imguiContainer = null;
            _originalHandler = null;
            _window = null;
            _useMonoHookFallback = false;
            _pendingCommands.Clear();
        }

        public void Enqueue(ImguiCommand command)
        {
            if (command == null) return;
            _pendingCommands.Enqueue(command);
            RequestRepaint();
        }

        public void Enqueue(IEnumerable<ImguiCommand> commands)
        {
            if (commands == null) return;
            foreach (var cmd in commands)
                _pendingCommands.Enqueue(cmd);
            RequestRepaint();
        }

        public ImguiSnapshot GetLastSnapshot() => _lastSnapshot;
        public void SetLastSnapshot(ImguiSnapshot snapshot) => _lastSnapshot = snapshot;

        /// <summary>
        /// Captures the IMGUI layout snapshot early, before OnGUI runs during Repaint.
        /// In Unity 6000+, GUILayoutUtility.current is valid during Repaint but cleared
        /// immediately after OnGUI returns. We capture at the start of Repaint while
        /// the layout from the preceding Layout event is still present.
        /// </summary>
        public void CaptureSnapshotEarly()
        {
            if (_window == null || !_useMonoHookFallback)
                return;

            _lastSnapshot = ImguiSnapshotCapture.Capture(_window);

            // Diagnostic
            var evtType = Event.current?.type.ToString() ?? "null";
            var count = _lastSnapshot?.Entries?.Count ?? -1;
            Codingriver.Logger.Log($"[UPilot Flow] CaptureSnapshotEarly: event={evtType}, entries={count}");
        }

        /// <summary>
        /// Called from the MonoHook replacement method after the original OnGUI completes.
        /// Defers command execution to EditorApplication.update to avoid reentrant OnGUI
        /// and ensure the snapshot is fresh before assert commands run.
        /// </summary>
        public void ExecutePostCommandsFromHook()
        {
            if (_window == null || !_useMonoHookFallback || _isInDeferredExecution)
                return;

            if (_pendingCommands.Count > 0)
            {
                ScheduleDeferredExecution();
            }
        }

        private void ScheduleDeferredExecution()
        {
            if (_deferredCallback != null)
                return; // Already scheduled

            _deferredCallback = () =>
            {
                EditorApplication.update -= _deferredCallback;
                _deferredCallback = null;
                _isInDeferredExecution = true;
                try
                {
                    Codingriver.Logger.Log($"[UPilot Flow] ScheduleDeferredExecution: _window={_window?.GetType().Name}@{_window?.GetHashCode()}, _pendingCommands={_pendingCommands.Count}");

                // Ensure we have a snapshot to work with
                if (_lastSnapshot == null)
                {
                    _lastSnapshot = ImguiSnapshotCapture.Capture(_window);
                }

                while (_pendingCommands.Count > 0)
                {
                    var cmd = _pendingCommands.Dequeue();
                    try
                    {
                        Codingriver.Logger.Log($"[UPilot Flow] Executing {cmd.GetType().Name}: window={_window?.GetType().Name}@{_window?.GetHashCode()}, snapshot={_lastSnapshot?.Entries?.Count}");
                        cmd.Execute(_window, _lastSnapshot);
                    }
                    catch (Exception ex)
                    {
                        Codingriver.Logger.LogWarning($"[UPilot Flow] IMGUI command failed: {ex}");
                    }

                    // After mutating commands, trigger repaint and break to let the window
                    // update before the next command (asserts need fresh snapshot).
                    if (cmd.RequiresRepaintWait && _pendingCommands.Count > 0)
                    {
                        _window?.Repaint();
                        ScheduleDeferredExecution();
                        return;
                    }
                }
                }
                finally
                {
                    _isInDeferredExecution = false;
                }
            };
            EditorApplication.update += _deferredCallback;
        }

        private void RequestRepaint()
        {
            _window?.Repaint();
        }

        private void OnGuiHook()
        {
            // Phase 1: Execute pre-commands (e.g., set focused control)
            ExecutePreCommands();

            // Phase 2: Run original OnGUI
            _originalHandler?.Invoke();

            // Phase 3: Capture snapshot immediately after layout
            _lastSnapshot = ImguiSnapshotCapture.Capture(_window);

            // Phase 4: Execute post-commands (click, type, assert)
            ExecutePostCommands();
        }

        private void ExecutePreCommands()
        {
            // Future: focus control before OnGUI if needed
        }

        private void ExecutePostCommands()
        {
            while (_pendingCommands.Count > 0)
            {
                var cmd = _pendingCommands.Dequeue();
                try
                {
                    cmd.Execute(_window, _lastSnapshot);
                }
                catch (Exception ex)
                {
                    Codingriver.Logger.LogWarning($"[UPilot Flow] IMGUI command failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Base class for queued IMGUI automation commands.
    /// </summary>
    public abstract class ImguiCommand
    {
        public abstract void Execute(EditorWindow window, ImguiSnapshot snapshot);

        /// <summary>
        /// When true, the bridge will trigger a repaint and defer remaining commands
        /// to the next EditorApplication.update cycle, giving the window time to
        /// refresh its snapshot before assert/read commands run.
        /// </summary>
        public virtual bool RequiresRepaintWait => false;
    }
}
