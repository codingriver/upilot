using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable]
    public sealed class WindowInputEvidence
    {
        public string windowInstanceId;
        public string windowHandle;
        public string text;
        public string controlType;
        public Rect localRect;
        public Rect clipRect;
        public bool enabled;
        public string matchSource;
        public long layoutSequence;
        public bool dispatched;
        public bool businessEffectVerified;
    }

    /// <summary>Opt-in IMGUI mappings. Rectangles are in window-local coordinates, after scroll/GUI transforms.</summary>
    public static class UPilotWindowInputRegistry
    {
        private static readonly string Epoch = Guid.NewGuid().ToString("N");
        private static readonly Dictionary<ulong, Frame> Frames = new Dictionary<ulong, Frame>();
        private static long sequence;

        public static string Handle(EditorWindow window) => window == null ? "" : "window:" + Epoch + ":" + UPilotEntityIds.ToWireId(window);

        public static EditorWindow ResolveHandle(string handle)
        {
            var parts = (handle ?? "").Split(':');
            if (parts.Length != 3 || parts[0] != "window" || parts[1] != Epoch || !ulong.TryParse(parts[2], out var id))
                return null;
            return Resolve(id.ToString(), "");
        }

        public static EditorWindow Resolve(string instanceId, string legacyTarget)
        {
            if (string.IsNullOrEmpty(instanceId)) return UPilotPlayInputService.FindTargetWindow(legacyTarget);
            if (!ulong.TryParse(instanceId, out var id)) return null;
            return Resources.FindObjectsOfTypeAll<EditorWindow>().FirstOrDefault(w => UPilotEntityIds.ToWireId(w) == id);
        }

        public static Frame BeginFrame(EditorWindow window)
        {
            if (window == null || Event.current == null || Event.current.type != EventType.Repaint)
                throw new InvalidOperationException("BeginFrame requires the target window's Repaint callback.");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic;
            var currentView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GUIView")
                ?.GetProperty("current", flags | BindingFlags.Static)?.GetValue(null);
            var parent = typeof(EditorWindow).GetField("m_Parent", flags | BindingFlags.Instance)?.GetValue(window);
            if (currentView == null || !ReferenceEquals(parent, currentView))
                throw new InvalidOperationException("IMGUI_FRAME_OWNER_UNVERIFIED");
            foreach (var key in Frames.Where(p => p.Value.Window == null).Select(p => p.Key).ToArray()) Frames.Remove(key);
            if (Frames.Count >= 128 && !Frames.ContainsKey(UPilotEntityIds.ToWireId(window)))
                throw new InvalidOperationException("IMGUI window mapping limit reached.");
            var frame = new Frame(window, ++sequence);
            Frames[UPilotEntityIds.ToWireId(window)] = frame;
            return frame;
        }

        public sealed class Frame : IDisposable
        {
            internal readonly EditorWindow Window;
            internal readonly List<WindowInputEvidence> Controls = new List<WindowInputEvidence>();
            internal readonly Rect Position;
            internal readonly double CapturedAt;
            internal bool Complete;
            private readonly long Sequence;
            internal Frame(EditorWindow window, long sequence)
            {
                Window = window; Position = window.position; Sequence = sequence;
                CapturedAt = EditorApplication.timeSinceStartup;
            }
            public void Add(string text, string controlType, Rect localRect, Rect clipRect, bool enabled)
            {
                if (Complete || Controls.Count >= 4096) throw new InvalidOperationException("IMGUI mapping is complete or full.");
                Controls.Add(new WindowInputEvidence
                {
                    windowInstanceId = UPilotEntityIds.ToWireId(Window).ToString(), windowHandle = Handle(Window),
                    text = text, controlType = controlType, localRect = localRect, clipRect = clipRect,
                    enabled = enabled, layoutSequence = Sequence, matchSource = "imgui-registered",
                });
            }
            public void Dispose() { Complete = true; }
        }

        internal static bool TryFind(EditorWindow window, string text, int index, out WindowInputEvidence evidence, out string error)
        {
            evidence = null;
            error = "IMGUI_LOCATOR_UNSUPPORTED";
            if (!Frames.TryGetValue(UPilotEntityIds.ToWireId(window), out var frame)) return false;
            if (!frame.Complete || frame.Position != window.position || EditorApplication.timeSinceStartup - frame.CapturedAt > 1)
            { error = "IMGUI_LAYOUT_STALE"; return false; }
            var matches = frame.Controls.Where(c => string.IsNullOrEmpty(text) || c.text == text).ToArray();
            if (matches.Length == 0) { error = "ELEMENT_NOT_FOUND"; return false; }
            if (index < 0 && matches.Length != 1) { error = "ELEMENT_AMBIGUOUS"; return false; }
            index = index < 0 ? 0 : index;
            if (index >= matches.Length) { error = "ELEMENT_INDEX_OUT_OF_RANGE"; return false; }
            evidence = matches[index];
            if (!evidence.enabled) { error = "ELEMENT_DISABLED"; return false; }
            var rect = evidence.localRect;
            if (rect.width <= 0 || rect.height <= 0 || !evidence.clipRect.Contains(rect.center)
                || !new Rect(Vector2.zero, window.position.size).Contains(rect.center))
            { error = "ELEMENT_NOT_VISIBLE"; return false; }
            // Per-call evidence must not mutate the stored layout.
            evidence = JsonUtility.FromJson<WindowInputEvidence>(JsonUtility.ToJson(evidence));
            error = "";
            return true;
        }
    }
}
