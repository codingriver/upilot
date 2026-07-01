// -----------------------------------------------------------------------
// Upilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace codingriver.upilot
{
    [Serializable]
    public class EditorWindowInfo
    {
        public ulong instanceId;
        public string typeName;
        public string fullTypeName;
        public string title;
        public float posX;
        public float posY;
        public float width;
        public float height;
        public bool hasFocus;
        public bool docked;
        public bool hasUIToolkit;
    }

    [Serializable]
    public class EditorWindowsListPayload
    {
        public List<EditorWindowInfo> windows = new();
        public int total;
    }

    [Serializable]
    public class EditorWindowsListMessage
    {
        public string id;
        public string type;
        public string name;
        public EditorWindowsListFilterPayload payload;
        public long timestamp;
        public string sessionId;
        public string protocolVersion;
    }

    [Serializable]
    public class EditorWindowsListFilterPayload
    {
        public string typeFilter;
        public string titleFilter;
    }

    public sealed class UpilotWindowService
    {
        private readonly UpilotBridge _bridge;

        public UpilotWindowService(UpilotBridge bridge) => _bridge = bridge;

        public void RegisterCommands()
        {
            _bridge.Router.Register("editor.windows.list", HandleWindowsListAsync);
            _bridge.Router.Register("editor.window.close", HandleWindowCloseAsync);
            _bridge.Router.Register("editor.window.setRect", HandleWindowSetRectAsync);
        }

        private async Task HandleWindowsListAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<EditorWindowsListMessage>(json);
            var filter = msg?.payload ?? new EditorWindowsListFilterPayload();

            var tcs = new TaskCompletionSource<EditorWindowsListPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try { tcs.TrySetResult(ListWindows(filter)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "editor.windows.list", result, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "INTERNAL_ERROR", $"枚举窗口失败：{ex.Message}", token, "editor.windows.list");
            }
        }

        private static EditorWindowsListPayload ListWindows(EditorWindowsListFilterPayload filter)
        {
            var result = new EditorWindowsListPayload();
            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();

            foreach (var w in windows)
            {
                var typeName = w.GetType().Name;
                var fullTypeName = w.GetType().FullName ?? typeName;
                var title = w.titleContent?.text ?? "";

                if (!string.IsNullOrEmpty(filter.typeFilter) &&
                    typeName.IndexOf(filter.typeFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    fullTypeName.IndexOf(filter.typeFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (!string.IsNullOrEmpty(filter.titleFilter) &&
                    title.IndexOf(filter.titleFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var rect = w.position;
                result.windows.Add(new EditorWindowInfo
                {
                    instanceId = UpilotEntityIds.ToWireId(w),
                    typeName = typeName,
                    fullTypeName = fullTypeName,
                    title = title,
                    posX = rect.x,
                    posY = rect.y,
                    width = rect.width,
                    height = rect.height,
                    hasFocus = w.hasFocus,
                    docked = w.docked,
                    hasUIToolkit = w.rootVisualElement != null,
                });
            }

            result.total = result.windows.Count;
            return result;
        }

        private async Task HandleWindowCloseAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<EditorWindowCloseMessage>(json);
            var payload = msg?.payload ?? new EditorWindowClosePayload();

            var tcs = new TaskCompletionSource<EditorWindowCloseResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var result = ResolveWindow(payload.windowTitle, payload.matchMode);
                    if (result.window == null)
                    {
                        tcs.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            ok = false,
                            state = "not_found",
                            deniedReason = "WINDOW_NOT_FOUND",
                            multipleMatches = result.multipleMatches,
                        });
                        return;
                    }

                    var window = result.window;
                    var typeName = window.GetType().Name;
                    var title = window.titleContent?.text ?? "";
                    if (window.docked)
                    {
                        tcs.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            ok = false,
                            state = "denied",
                            deniedReason = "WINDOW_DOCKED",
                            matchedTitle = title,
                            matchedTypeName = typeName,
                            multipleMatches = result.multipleMatches,
                        });
                        return;
                    }

                    window.Close();
                    tcs.TrySetResult(new EditorWindowCloseResultPayload
                    {
                        ok = true,
                        state = "closed",
                        matchedTitle = title,
                        matchedTypeName = typeName,
                        multipleMatches = result.multipleMatches,
                    });
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                await _bridge.SendResultAsync(id, "editor.window.close", await tcs.Task, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "WINDOW_CLOSE_FAILED", ex.Message, token, "editor.window.close");
            }
        }

        private async Task HandleWindowSetRectAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<EditorWindowSetRectMessage>(json);
            var payload = msg?.payload ?? new EditorWindowSetRectPayload();

            var tcs = new TaskCompletionSource<EditorWindowCloseResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var result = ResolveWindow(payload.windowTitle, payload.matchMode);
                    if (result.window == null)
                    {
                        tcs.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            ok = false,
                            state = "not_found",
                            deniedReason = "WINDOW_NOT_FOUND",
                            multipleMatches = result.multipleMatches,
                        });
                        return;
                    }

                    var window = result.window;
                    var typeName = window.GetType().Name;
                    var title = window.titleContent?.text ?? "";
                    if (window.docked)
                    {
                        tcs.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            ok = false,
                            state = "denied",
                            deniedReason = "WINDOW_DOCKED",
                            matchedTitle = title,
                            matchedTypeName = typeName,
                            multipleMatches = result.multipleMatches,
                        });
                        return;
                    }

                    window.position = new Rect(payload.x, payload.y, Mathf.Max(100, payload.width), Mathf.Max(80, payload.height));
                    window.Repaint();
                    tcs.TrySetResult(new EditorWindowCloseResultPayload
                    {
                        ok = true,
                        state = "rect_set",
                        matchedTitle = title,
                        matchedTypeName = typeName,
                        multipleMatches = result.multipleMatches,
                    });
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                await _bridge.SendResultAsync(id, "editor.window.setRect", await tcs.Task, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "WINDOW_SET_RECT_FAILED", ex.Message, token, "editor.window.setRect");
            }
        }

        private static (EditorWindow window, bool multipleMatches) ResolveWindow(string title, string matchMode)
        {
            if (string.IsNullOrWhiteSpace(title))
                return (null, false);

            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            var exact = string.Equals(matchMode, "exact", StringComparison.OrdinalIgnoreCase);
            var matches = windows.Where(w =>
            {
                var t = w.titleContent?.text ?? "";
                return exact
                    ? string.Equals(t, title, StringComparison.Ordinal)
                    : t.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0;
            }).ToList();

            return (matches.FirstOrDefault(), matches.Count > 1);
        }
    }
}
