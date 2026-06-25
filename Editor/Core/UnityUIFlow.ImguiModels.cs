using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityUIFlow
{
    /// <summary>
    /// Parsed selector for an IMGUI control.
    /// </summary>
    [Serializable]
    public sealed class ImguiSelector
    {
        public string Type;           // button, textfield, toggle, label, dropdown, toolbar, slider, scroller, group
        public string Text;           // text match (exact or partial)
        public int Index = -1;        // index match, -1 means unused
        public string Group;          // parent GUILayout group name
        public string ControlName;    // GUI.SetNextControlName value
        public bool Focused;          // match currently focused control
    }

    /// <summary>
    /// A single captured entry from a GUILayout layout batch.
    /// </summary>
    [Serializable]
    public sealed class ImguiSnapshotEntry
    {
        public Rect Rect;
        public string InferredType;   // button, textfield, toggle, label, dropdown, toolbar, slider, scroller, group, unknown
        public string Text;           // text content if available
        public string StyleName;      // GUIStyle.name
        public string GroupName;      // owning GUILayoutGroup identifier
        public int Depth;             // nesting depth in the layout tree
        public int GlobalIndex;       // index in the flattened snapshot list

        public override string ToString()
        {
            return $"[{GlobalIndex}] {InferredType} '{Text}' rect={Rect} style={StyleName} group={GroupName}";
        }
    }

    /// <summary>
    /// A full snapshot of IMGUI controls captured after OnGUI completes.
    /// </summary>
    public sealed class ImguiSnapshot
    {
        public List<ImguiSnapshotEntry> Entries = new List<ImguiSnapshotEntry>();
        public DateTimeOffset CapturedAt;
        public EditorWindow SourceWindow;

        public ImguiSnapshotEntry FindFirst(ImguiSelector selector)
        {
            foreach (var entry in Entries)
            {
                if (Matches(entry, selector))
                    return entry;
            }
            return null;
        }

        public List<ImguiSnapshotEntry> FindAll(ImguiSelector selector)
        {
            var results = new List<ImguiSnapshotEntry>();
            foreach (var entry in Entries)
            {
                if (Matches(entry, selector))
                    results.Add(entry);
            }
            return results;
        }

        private static bool Matches(ImguiSnapshotEntry entry, ImguiSelector selector)
        {
            if (selector.Focused)
            {
                // Focused matching is checked externally against GUIUtility.keyboardControl
                return false; // placeholder; real check done in locator
            }

            if (!string.IsNullOrEmpty(selector.Type))
            {
                if (!string.Equals(entry.InferredType, selector.Type, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (!string.IsNullOrEmpty(selector.Text))
            {
                if (entry.Text == null ||
                    entry.Text.IndexOf(selector.Text, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            if (selector.Index >= 0)
            {
                // Index is applied by the locator after filtering by type
                // This method only checks type/text/group; index applied externally
            }

            if (!string.IsNullOrEmpty(selector.Group))
            {
                if (entry.GroupName == null ||
                    entry.GroupName.IndexOf(selector.Group, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            return true;
        }
    }
}
