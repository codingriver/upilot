// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable]
    public class EditorWindowInfo
    {
        public string windowHandle;
        public ulong instanceId;
        public string domainGeneration;
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

    public class EditorWindowResolveResult
    {
        public EditorWindow window;
        public EditorWindowInfo info;
        public bool multipleMatches;
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

    public sealed class UPilotWindowService
    {
        private const int SceneViewCommandObservationLimit = 128;
        private static readonly object SceneViewCommandObservationLock = new object();
        private static readonly Dictionary<string, EditorWindowCloseResultPayload> SceneViewCommandObservations = new Dictionary<string, EditorWindowCloseResultPayload>(StringComparer.Ordinal);
        private readonly UPilotBridge _bridge;

        public UPilotWindowService(UPilotBridge bridge) => _bridge = bridge;

        public void RegisterCommands()
        {
            _bridge.Router.Register("editor.windows.list", HandleWindowsListAsync);
            _bridge.Router.Register("editor.window.close", HandleWindowCloseAsync);
            _bridge.Router.Register("editor.window.setRect", HandleWindowSetRectAsync);
            _bridge.Router.Register("editor.window.history", HandleWindowHistoryAsync);
            _bridge.Router.Register("editor.window.open", HandleWindowOpenAsync);
            _bridge.Router.Register("editor.window.focus", HandleWindowFocusAsync);
            _bridge.Router.Register("sceneview.setMaximized", HandleSceneViewSetMaximizedAsync);
            _bridge.Router.Register("sceneview.commandStatus", HandleSceneViewCommandStatusAsync);
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

                result.windows.Add(BuildWindowInfo(w));
            }

            result.total = result.windows.Count;
            return result;
        }

        private async Task HandleWindowCloseAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<EditorWindowCloseMessage>(json);
            var payload = msg?.payload ?? new EditorWindowClosePayload();
            if (payload.closeMode != "requestUserClose" && payload.closeMode != "forceClose")
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", "closeMode must be requestUserClose or forceClose.", token, "editor.window.close");
                return;
            }

            var tcs = new TaskCompletionSource<EditorWindowCloseResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var result = string.IsNullOrWhiteSpace(payload.instanceId)
                        ? ResolveWindow(payload.windowTitle, payload.matchMode)
                        : ResolveWindowByInstanceId(payload.instanceId);
                    if (result.multipleMatches)
                        throw new InvalidOperationException("WINDOW_AMBIGUOUS: use the exact instanceId.");
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
                    var info = result.info ?? BuildWindowInfo(window);
                    if (!string.IsNullOrEmpty(payload.domainGeneration)
                        && !string.Equals(payload.domainGeneration, info.domainGeneration, StringComparison.Ordinal))
                    {
                        tcs.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            commandId = id,
                            instanceId = info.instanceId.ToString(),
                            domainGeneration = info.domainGeneration,
                            ok = false,
                            state = "denied",
                            deniedReason = "WINDOW_DOMAIN_MISMATCH",
                            matchedTitle = info.title,
                            matchedTypeName = info.typeName,
                            matchedFullTypeName = info.fullTypeName,
                        });
                        return;
                    }
                    if (!string.IsNullOrEmpty(payload.instanceId) && !string.IsNullOrEmpty(payload.windowTitle)
                        && !WindowTitleMatches(info.title, payload.windowTitle, payload.matchMode))
                    {
                        tcs.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            commandId = id,
                            instanceId = info.instanceId.ToString(),
                            domainGeneration = info.domainGeneration,
                            ok = false,
                            state = "denied",
                            deniedReason = "EDITORWINDOW_TITLE_MISMATCH",
                            matchedTitle = info.title,
                            matchedTypeName = info.typeName,
                            matchedFullTypeName = info.fullTypeName,
                            changed = false,
                            writeCount = 0,
                            commandSubmitted = false,
                            stateObserved = true,
                            sideEffectsMayHaveOccurred = false,
                        });
                        return;
                    }
                    var typeName = info.typeName;
                    var title = info.title;
                    if (window.docked)
                    {
                        tcs.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            ok = false,
                            state = "denied",
                            deniedReason = "WINDOW_DOCKED",
                            matchedTitle = title,
                            matchedTypeName = typeName,
                            matchedFullTypeName = info.fullTypeName,
                            windowWidth = info.width,
                            windowHeight = info.height,
                            multipleMatches = result.multipleMatches,
                        });
                        return;
                    }

                    var response = new EditorWindowCloseResultPayload
                    {
                        commandId = id, instanceId = info.instanceId.ToString(),
                        domainGeneration = info.domainGeneration,
                        discardRisk = payload.closeMode == "forceClose",
                        matchedTitle = title,
                        matchedTypeName = typeName,
                        matchedFullTypeName = info.fullTypeName,
                        windowWidth = info.width,
                        windowHeight = info.height,
                        multipleMatches = result.multipleMatches,
                    };
                    if (payload.closeMode == "forceClose")
                    {
                        window.Close();
                        response.closeVerified = window == null;
                        response.ok = response.closeVerified;
                        response.terminal = true;
                        response.state = response.closeVerified ? "closed" : "not_closed";
                        tcs.TrySetResult(response);
                        return;
                    }
                    if (!UPilotWindowDiagnostics.TryGetMappedWindowHandle(window, true, out var handle, out string mapping))
                    {
                        response.terminal = true;
                        response.state = "denied";
                        response.deniedReason = "WINDOW_MAPPING_UNVERIFIED";
                        response.mappingEvidence = mapping;
                        tcs.TrySetResult(response);
                        return;
                    }
                    response.windowHandle = handle.ToInt64();
                    response.mappingEvidence = mapping;
                    var watch = UPilotModalObserver.Arm(id, window);
                    if (!PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero))
                    {
                        watch.Complete(null, new InvalidOperationException("WM_CLOSE post failed."));
                        throw new InvalidOperationException("WM_CLOSE post failed: " + Marshal.GetLastWin32Error());
                    }
                    double deadline = EditorApplication.timeSinceStartup + 5;
                    EditorApplication.CallbackFunction verify = null;
                    verify = () =>
                    {
                        if (window != null && (EditorApplication.timeSinceStartup < deadline || watch.HasActiveModal())) return;
                        EditorApplication.update -= verify;
                        response.closeVerified = window == null;
                        response.ok = response.closeVerified;
                        response.terminal = true;
                        response.state = response.closeVerified ? "closed" : "not_closed";
                        watch.Complete(response);
                        tcs.TrySetResult(response);
                    };
                    EditorApplication.update += verify;
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                if (await Task.WhenAny(tcs.Task, Task.Delay(5200)).ConfigureAwait(false) == tcs.Task)
                    await _bridge.SendResultAsync(id, "editor.window.close", await tcs.Task, token);
                else
                {
                    var modal = UPilotModalObserver.Get(id);
                    await _bridge.SendResultAsync(id, "editor.window.close", new EditorWindowCloseResultPayload
                    {
                        commandId = id, instanceId = modal?.targetInstanceId ?? payload.instanceId,
                        windowHandle = modal?.ownerWindowHandle ?? 0,
                        waitingForModalUi = modal?.waitingForModalUi ?? false, terminal = false,
                        discardRisk = payload.closeMode == "forceClose",
                        state = modal?.waitingForModalUi == true ? "waitingForModalUi" : "close_pending",
                    }, token);
                }
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "WINDOW_CLOSE_FAILED", ex.Message, token, "editor.window.close");
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

        private async Task HandleWindowSetRectAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<EditorWindowSetRectMessage>(json);
            var payload = msg?.payload ?? new EditorWindowSetRectPayload();

            var tcs = new TaskCompletionSource<EditorWindowCloseResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var result = string.IsNullOrWhiteSpace(payload.instanceId)
                        ? ResolveWindow(payload.windowTitle, payload.matchMode)
                        : ResolveWindowByInstanceId(payload.instanceId);
                    if (result.multipleMatches)
                    {
                        tcs.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            ok = false,
                            state = "denied",
                            deniedReason = "WINDOW_AMBIGUOUS",
                            multipleMatches = true,
                        });
                        return;
                    }
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
                    var info = result.info ?? BuildWindowInfo(window);
                    if (!string.IsNullOrEmpty(payload.domainGeneration)
                        && !string.Equals(payload.domainGeneration, info.domainGeneration, StringComparison.Ordinal))
                    {
                        tcs.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            commandId = id,
                            instanceId = info.instanceId.ToString(),
                            domainGeneration = info.domainGeneration,
                            ok = false,
                            state = "denied",
                            deniedReason = "WINDOW_DOMAIN_MISMATCH",
                            matchedTitle = info.title,
                            matchedTypeName = info.typeName,
                            matchedFullTypeName = info.fullTypeName,
                        });
                        return;
                    }
                    if (!string.IsNullOrEmpty(payload.fullTypeName)
                        && !string.Equals(payload.fullTypeName, info.fullTypeName, StringComparison.Ordinal))
                    {
                        tcs.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            commandId = id,
                            instanceId = info.instanceId.ToString(),
                            domainGeneration = info.domainGeneration,
                            ok = false,
                            state = "denied",
                            deniedReason = "WINDOW_TYPE_MISMATCH",
                            matchedTitle = info.title,
                            matchedTypeName = info.typeName,
                            matchedFullTypeName = info.fullTypeName,
                            changed = false,
                            writeCount = 0,
                            commandSubmitted = false,
                            stateObserved = true,
                            sideEffectsMayHaveOccurred = false,
                        });
                        return;
                    }
                    // An explicit title is an identity constraint when callers
                    // also provide the exact instance. Do not resize an
                    // instance that no longer has the requested title merely
                    // because its wire ID is still live.
                    if (!string.IsNullOrEmpty(payload.instanceId) && !string.IsNullOrEmpty(payload.windowTitle)
                        && !WindowTitleMatches(info.title, payload.windowTitle, payload.matchMode))
                    {
                        tcs.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            commandId = id,
                            instanceId = info.instanceId.ToString(),
                            domainGeneration = info.domainGeneration,
                            ok = false,
                            state = "denied",
                            deniedReason = "EDITORWINDOW_TITLE_MISMATCH",
                            matchedTitle = info.title,
                            matchedTypeName = info.typeName,
                            matchedFullTypeName = info.fullTypeName,
                            changed = false,
                            writeCount = 0,
                            commandSubmitted = false,
                            stateObserved = true,
                            sideEffectsMayHaveOccurred = false,
                        });
                        return;
                    }
                    var typeName = info.typeName;
                    var title = info.title;
                    if (window.docked)
                    {
                        tcs.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            ok = false,
                            state = "denied",
                            deniedReason = "WINDOW_DOCKED",
                            matchedTitle = title,
                            matchedTypeName = typeName,
                            matchedFullTypeName = info.fullTypeName,
                            windowWidth = info.width,
                            windowHeight = info.height,
                            multipleMatches = result.multipleMatches,
                        });
                        return;
                    }

                    window.position = new Rect(payload.x, payload.y, Mathf.Max(100, payload.width), Mathf.Max(80, payload.height));
                    window.Repaint();
                    info = BuildWindowInfo(window);
                    tcs.TrySetResult(new EditorWindowCloseResultPayload
                    {
                        commandId = id,
                        instanceId = info.instanceId.ToString(),
                        domainGeneration = info.domainGeneration,
                        ok = true,
                        state = "rect_set",
                        matchedTitle = title,
                        matchedTypeName = typeName,
                        matchedFullTypeName = info.fullTypeName,
                        windowWidth = info.width,
                        windowHeight = info.height,
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

        private async Task HandleWindowHistoryAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<EditorWindowHistoryMessage>(json);
            var payload = message?.payload ?? new EditorWindowHistoryPayload();
            if (payload.afterSequence < 0 || payload.count < 1 || payload.count > 512)
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", "afterSequence must be non-negative and count must be between 1 and 512.", token, "editor.window.history");
                return;
            }

            var completion = new TaskCompletionSource<EditorWindowHistoryResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try { completion.TrySetResult(UPilotWindowHistory.Query(payload.instanceId, payload.afterSequence, payload.count)); }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
            try { await _bridge.SendResultAsync(id, "editor.window.history", await completion.Task, token); }
            catch (Exception ex) { await _bridge.SendErrorAsync(id, "WINDOW_HISTORY_FAILED", ex.Message, token, "editor.window.history"); }
        }

        private async Task HandleWindowOpenAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<EditorWindowOpenMessage>(json);
            var payload = message?.payload ?? new EditorWindowOpenPayload();
            if (string.IsNullOrWhiteSpace(payload.typeName))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", "typeName is required.", token, "editor.window.open");
                return;
            }

            var completion = new TaskCompletionSource<EditorWindowCloseResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    if (!UPilotSafeWindowProbe.IsAllowedTypeName(payload.typeName))
                    {
                        completion.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            commandId = id, ok = false, state = "denied", deniedReason = "WINDOW_TYPE_NOT_SAFE",
                        });
                        return;
                    }
                    var window = ScriptableObject.CreateInstance<UPilotSafeWindowProbe>();
                    window.titleContent = new GUIContent(UPilotSafeWindowProbe.Title);
                    window.ShowUtility();
                    var info = BuildWindowInfo(window);
                    completion.TrySetResult(new EditorWindowCloseResultPayload
                    {
                        commandId = id, instanceId = info.instanceId.ToString(), domainGeneration = info.domainGeneration,
                        ok = true, state = "opened", matchedTitle = info.title, matchedTypeName = info.typeName,
                        matchedFullTypeName = info.fullTypeName, windowWidth = info.width, windowHeight = info.height,
                    });
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
            try { await _bridge.SendResultAsync(id, "editor.window.open", await completion.Task, token); }
            catch (Exception ex) { await _bridge.SendErrorAsync(id, "WINDOW_OPEN_FAILED", ex.Message, token, "editor.window.open"); }
        }

        private async Task HandleWindowFocusAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<EditorWindowFocusMessage>(json);
            var payload = message?.payload ?? new EditorWindowFocusPayload();
            if (string.IsNullOrWhiteSpace(payload.instanceId))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", "instanceId is required.", token, "editor.window.focus");
                return;
            }

            var completion = new TaskCompletionSource<EditorWindowCloseResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var result = ResolveWindowByInstanceId(payload.instanceId);
                    if (result.multipleMatches || result.window == null)
                    {
                        completion.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            commandId = id, instanceId = payload.instanceId, ok = false,
                            state = result.multipleMatches ? "denied" : "not_found",
                            deniedReason = result.multipleMatches ? "WINDOW_AMBIGUOUS" : "WINDOW_NOT_FOUND",
                        });
                        return;
                    }
                    var info = result.info ?? BuildWindowInfo(result.window);
                    if (!string.IsNullOrEmpty(payload.domainGeneration)
                        && !string.Equals(payload.domainGeneration, info.domainGeneration, StringComparison.Ordinal))
                    {
                        completion.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            commandId = id, instanceId = info.instanceId.ToString(), domainGeneration = info.domainGeneration,
                            ok = false, state = "denied", deniedReason = "WINDOW_DOMAIN_MISMATCH",
                        });
                        return;
                    }
                    if (!(result.window is UPilotSafeWindowProbe))
                    {
                        completion.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            commandId = id, instanceId = info.instanceId.ToString(), domainGeneration = info.domainGeneration,
                            ok = false, state = "denied", deniedReason = "WINDOW_TYPE_NOT_SAFE",
                            matchedTypeName = info.typeName, matchedFullTypeName = info.fullTypeName,
                        });
                        return;
                    }
                    result.window.Focus();
                    completion.TrySetResult(new EditorWindowCloseResultPayload
                    {
                        commandId = id, instanceId = info.instanceId.ToString(), domainGeneration = info.domainGeneration,
                        ok = EditorWindow.focusedWindow == result.window, state = "focused",
                        matchedTitle = info.title, matchedTypeName = info.typeName, matchedFullTypeName = info.fullTypeName,
                    });
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
            try { await _bridge.SendResultAsync(id, "editor.window.focus", await completion.Task, token); }
            catch (Exception ex) { await _bridge.SendErrorAsync(id, "WINDOW_FOCUS_FAILED", ex.Message, token, "editor.window.focus"); }
        }

        private async Task HandleSceneViewSetMaximizedAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<SceneViewSetMaximizedMessage>(json);
            var payload = message?.payload ?? new SceneViewSetMaximizedPayload();
            if (!TryNormalizeSceneViewInstanceId(payload.instanceId, out var instanceId))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", "SceneView instanceId must be a non-negative integer.", token, "sceneview.setMaximized",
                    new ErrorDetailPayload
                    {
                        commandId = id,
                        commandName = "sceneview.setMaximized",
                        commandSubmitted = false,
                        stateObserved = false,
                        changed = false,
                        writeCount = 0,
                        stage = "preflight",
                        blockedReason = "INVALID_PAYLOAD",
                        nextAction = "Provide an exact non-negative SceneView instanceId.",
                        sideEffectsMayHaveOccurred = false,
                    });
                return;
            }
            // The legacy DTO transports IDs as strings, but SceneView mutation
            // accepts only the public integer identity. Canonicalizing here
            // prevents a direct Bridge request from bypassing that boundary.
            payload.instanceId = instanceId;
            RecordSceneViewCommandObservation(new EditorWindowCloseResultPayload
            {
                commandId = id,
                instanceId = instanceId,
                domainGeneration = payload.domainGeneration ?? string.Empty,
                observationStatus = "not_submitted",
                terminal = false,
                commandSubmitted = false,
                stateObserved = false,
                changed = false,
                writeCount = 0,
                sideEffectsMayHaveOccurred = false,
                nextAction = "Query sceneview.commandStatus with this original commandId; do not submit the setter again.",
            });

            if (token.IsCancellationRequested)
            {
                RecordSceneViewCommandObservation(new EditorWindowCloseResultPayload
                {
                    commandId = id,
                    instanceId = instanceId,
                    domainGeneration = payload.domainGeneration ?? string.Empty,
                    observationStatus = "not_submitted",
                    terminal = true,
                    commandSubmitted = false,
                    stateObserved = false,
                    changed = false,
                    writeCount = 0,
                    sideEffectsMayHaveOccurred = false,
                    nextAction = "The original command was cancelled before the setter; do not resubmit automatically.",
                });
                await _bridge.SendErrorAsync(id, "EXECUTION_CANCELLED", "SceneView request was cancelled before it reached the main thread.",
                    CancellationToken.None, "sceneview.setMaximized", new ErrorDetailPayload
                    {
                        commandId = id,
                        commandName = "sceneview.setMaximized",
                        commandSubmitted = false,
                        stateObserved = false,
                        changed = false,
                        writeCount = 0,
                        stage = "cancelled",
                        nextAction = "Inspect the current exact SceneView state; do not submit the cancelled request again.",
                        sideEffectsMayHaveOccurred = false,
                    });
                return;
            }

            var tcs = new TaskCompletionSource<EditorWindowCloseResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            var setterMayHaveRun = false;
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    if (token.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled(token);
                        return;
                    }
                    // The Server performs the first write gate, but a direct
                    // Bridge caller or an old Server must not turn a stale,
                    // compiling, or changing Editor snapshot into a late
                    // native window write.  This check and the setter run on
                    // the same main-thread queue action, so it cannot wait for
                    // another mode and then mutate a reused target.
                    var executionContext = _bridge.GetEditorExecutionContext("sceneview.setMaximized");
                    if (executionContext.blocked || !executionContext.authoritative || executionContext.isStale)
                    {
                        tcs.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            commandId = id,
                            instanceId = payload.instanceId,
                            domainGeneration = payload.domainGeneration ?? string.Empty,
                            ok = false,
                            state = "denied",
                            deniedReason = string.IsNullOrEmpty(executionContext.blockedReason)
                                ? "EDITOR_STATE_NOT_READY"
                                : executionContext.blockedReason,
                            commandSubmitted = false,
                            stateObserved = false,
                            changed = false,
                            writeCount = 0,
                            sideEffectsMayHaveOccurred = false,
                        });
                        return;
                    }
                    var result = ResolveWindowByInstanceId(payload.instanceId);
                    if (result.multipleMatches || result.window == null)
                    {
                        tcs.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            commandId = id, instanceId = payload.instanceId, ok = false,
                            state = result.multipleMatches ? "denied" : "not_found",
                            deniedReason = result.multipleMatches ? "WINDOW_AMBIGUOUS" : "WINDOW_NOT_FOUND",
                            multipleMatches = result.multipleMatches, changed = false, writeCount = 0,
                            commandSubmitted = false, stateObserved = false, sideEffectsMayHaveOccurred = false,
                        });
                        return;
                    }

                    var window = result.window;
                    var info = result.info ?? BuildWindowInfo(window);
                    if (!string.IsNullOrEmpty(payload.domainGeneration)
                        && !string.Equals(payload.domainGeneration, info.domainGeneration, StringComparison.Ordinal))
                    {
                        tcs.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            commandId = id, instanceId = info.instanceId.ToString(),
                            domainGeneration = info.domainGeneration, ok = false, state = "denied",
                            deniedReason = "WINDOW_DOMAIN_MISMATCH", matchedTypeName = info.typeName,
                            matchedFullTypeName = info.fullTypeName, maximized = window.maximized,
                            changed = false, writeCount = 0, commandSubmitted = false, stateObserved = true,
                            sideEffectsMayHaveOccurred = false,
                        });
                        return;
                    }
                    if (!string.Equals(info.fullTypeName, "UnityEditor.SceneView", StringComparison.Ordinal))
                    {
                        tcs.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            commandId = id, instanceId = info.instanceId.ToString(), domainGeneration = info.domainGeneration, ok = false, state = "denied",
                            deniedReason = "SCENEVIEW_REQUIRED", matchedTypeName = info.typeName,
                            matchedFullTypeName = info.fullTypeName, maximized = window.maximized,
                            changed = false, writeCount = 0, commandSubmitted = false, stateObserved = true,
                            sideEffectsMayHaveOccurred = false,
                        });
                        return;
                    }

                    if (payload.hasExpectedCurrentMaximized && window.maximized != payload.expectedCurrentMaximized)
                    {
                        tcs.TrySetResult(new EditorWindowCloseResultPayload
                        {
                            commandId = id, instanceId = info.instanceId.ToString(), domainGeneration = info.domainGeneration, ok = false, state = "denied",
                            deniedReason = "SCENEVIEW_STATE_CHANGED", matchedTypeName = info.typeName,
                            matchedFullTypeName = info.fullTypeName, maximized = window.maximized, changed = false, writeCount = 0,
                            commandSubmitted = false, stateObserved = true, sideEffectsMayHaveOccurred = false,
                        });
                        return;
                    }

                    var originalRect = ToWindowRect(window.position);
                    var changed = window.maximized != payload.maximized;
                    // This write-ahead observation is deliberately saved before
                    // the setter. If the response is lost after this point, a
                    // status query can distinguish an unknown setter outcome
                    // from a request that never reached the target.
                    RecordSceneViewCommandObservation(new EditorWindowCloseResultPayload
                    {
                        commandId = id,
                        instanceId = info.instanceId.ToString(),
                        domainGeneration = info.domainGeneration,
                        observationStatus = "submitted",
                        terminal = false,
                        commandSubmitted = true,
                        stateObserved = true,
                        changed = false,
                        writeCount = 0,
                        maximized = window.maximized,
                        sideEffectsMayHaveOccurred = changed,
                        requestedState = payload.maximized ? "maximized" : "restored",
                        observedState = window.maximized ? "maximized" : "restored",
                        originalRect = originalRect,
                        nextAction = "Query sceneview.commandStatus with this original commandId; do not submit the setter again.",
                    });
                    if (changed)
                    {
                        // From this point a property setter can have partially
                        // changed native layout even if it subsequently throws.
                        // Earlier resolution/preflight errors remain proven
                        // zero-side-effect failures instead of being promoted
                        // to an unknown mutation by the outer exception path.
                        setterMayHaveRun = true;
                        window.maximized = payload.maximized;
                        window.Repaint();
                    }
                    tcs.TrySetResult(new EditorWindowCloseResultPayload
                    {
                        commandId = id, instanceId = info.instanceId.ToString(), domainGeneration = info.domainGeneration, ok = true,
                        state = payload.maximized ? "maximized" : "restored", matchedTypeName = info.typeName,
                        matchedFullTypeName = info.fullTypeName, changed = changed, writeCount = changed ? 1 : 0,
                        maximized = window.maximized, commandSubmitted = true, stateObserved = true,
                        sideEffectsMayHaveOccurred = changed, requestedState = payload.maximized ? "maximized" : "restored",
                        observedState = window.maximized ? "maximized" : "restored", originalRect = originalRect,
                        observedRect = ToWindowRect(window.position),
                    });
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var response = await tcs.Task;
                var context = _bridge.GetEditorExecutionContext("sceneview.setMaximized");
                response.authoritative = context.authoritative;
                response.isStale = context.isStale;
                response.confirmed = response.ok && response.stateObserved && response.authoritative && !response.isStale;
                response.observationStatus = !response.commandSubmitted ? "not_submitted"
                    : response.confirmed ? "confirmed"
                    : response.stateObserved ? "observed" : "submitted";
                response.terminal = response.confirmed || (!response.commandSubmitted && !response.sideEffectsMayHaveOccurred);
                response.nextAction = response.terminal
                    ? string.Empty
                    : "Query sceneview.commandStatus with this original commandId; do not submit the setter again.";
                RecordSceneViewCommandObservation(response);
                await _bridge.SendResultAsync(id, "sceneview.setMaximized", response,
                    token.IsCancellationRequested ? CancellationToken.None : token);
            }
            catch (OperationCanceledException)
            {
                RecordSceneViewCommandObservation(new EditorWindowCloseResultPayload
                {
                    commandId = id,
                    instanceId = instanceId,
                    domainGeneration = payload.domainGeneration ?? string.Empty,
                    observationStatus = "not_submitted",
                    terminal = true,
                    commandSubmitted = false,
                    stateObserved = false,
                    changed = false,
                    writeCount = 0,
                    sideEffectsMayHaveOccurred = false,
                    nextAction = "The original command was cancelled before the setter; do not resubmit automatically.",
                });
                await _bridge.SendErrorAsync(id, "EXECUTION_CANCELLED", "SceneView request was cancelled before the maximized setter ran.",
                    CancellationToken.None, "sceneview.setMaximized", new ErrorDetailPayload
                    {
                        commandId = id,
                        commandName = "sceneview.setMaximized",
                        commandSubmitted = false,
                        stateObserved = false,
                        changed = false,
                        writeCount = 0,
                        stage = "cancelled",
                        nextAction = "Inspect the current exact SceneView state; do not submit the cancelled request again.",
                        sideEffectsMayHaveOccurred = false,
                    });
            }
            catch (Exception ex)
            {
                var failure = CreateSceneViewRuntimeFailureObservation(
                    id, instanceId, payload.domainGeneration, setterMayHaveRun);
                RecordSceneViewCommandObservation(failure);
                await _bridge.SendErrorAsync(id, "SCENEVIEW_SET_MAXIMIZED_FAILED", ex.Message,
                    token.IsCancellationRequested ? CancellationToken.None : token, "sceneview.setMaximized",
                    new ErrorDetailPayload
                    {
                        commandId = id,
                        commandName = "sceneview.setMaximized",
                        commandSubmitted = setterMayHaveRun,
                        stateObserved = false,
                        changed = false,
                        writeCount = 0,
                        stage = "runtime",
                        nextAction = setterMayHaveRun
                            ? "Inspect the original command and exact SceneView state; do not retry automatically."
                            : "The SceneView setter was not reached; inspect the rejection and correct the target before retrying.",
                        sideEffectsMayHaveOccurred = setterMayHaveRun,
                    });
            }
        }

        private async Task HandleSceneViewCommandStatusAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<SceneViewCommandStatusMessage>(json);
            var commandId = message?.payload?.commandId?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(commandId))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PAYLOAD", "sceneview commandId is required.", token, "sceneview.commandStatus");
                return;
            }
            if (!TryGetSceneViewCommandObservation(commandId, out var observation))
            {
                observation = CreateUnknownSceneViewCommandObservation(commandId);
            }
            await _bridge.SendResultAsync(id, "sceneview.commandStatus", observation, token);
        }

        internal static EditorWindowCloseResultPayload CreateUnknownSceneViewCommandObservation(string commandId)
        {
            // The in-process observation can disappear after Domain Reload or
            // bounded-history eviction even though the setter already ran.  An
            // unknown result must therefore be conservative and must never be
            // interpreted as proof that replay is safe.
            return new EditorWindowCloseResultPayload
            {
                commandId = commandId ?? string.Empty,
                observationStatus = "unknown",
                terminal = false,
                recoveryRequired = true,
                commandSubmitted = true,
                stateObserved = false,
                changed = false,
                writeCount = 0,
                sideEffectsMayHaveOccurred = true,
                nextAction = "The original SceneView command observation is unavailable after reload/restart; inspect the exact current SceneView state and do not resubmit automatically.",
            };
        }

        internal static EditorWindowCloseResultPayload CreateSceneViewRuntimeFailureObservation(
            string commandId, string instanceId, string domainGeneration, bool setterMayHaveRun)
        {
            return new EditorWindowCloseResultPayload
            {
                commandId = commandId ?? string.Empty,
                instanceId = instanceId ?? string.Empty,
                domainGeneration = domainGeneration ?? string.Empty,
                observationStatus = setterMayHaveRun ? "submitted" : "not_submitted",
                terminal = !setterMayHaveRun,
                recoveryRequired = setterMayHaveRun,
                commandSubmitted = setterMayHaveRun,
                stateObserved = false,
                changed = false,
                writeCount = 0,
                sideEffectsMayHaveOccurred = setterMayHaveRun,
                nextAction = setterMayHaveRun
                    ? "Query sceneview.commandStatus with this original commandId; do not submit the setter again."
                    : "The SceneView setter was not reached; correct the target before submitting a new request.",
            };
        }

        private static void RecordSceneViewCommandObservation(EditorWindowCloseResultPayload observation)
        {
            if (observation == null || string.IsNullOrWhiteSpace(observation.commandId)) return;
            observation.observationUpdatedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (SceneViewCommandObservationLock)
            {
                SceneViewCommandObservations[observation.commandId] = CloneSceneViewCommandObservation(observation);
                while (SceneViewCommandObservations.Count > SceneViewCommandObservationLimit)
                {
                    var oldest = SceneViewCommandObservations
                        .OrderBy(item => item.Value.observationUpdatedAtUtcMs)
                        .First();
                    SceneViewCommandObservations.Remove(oldest.Key);
                }
            }
        }

        private static bool TryGetSceneViewCommandObservation(string commandId, out EditorWindowCloseResultPayload observation)
        {
            lock (SceneViewCommandObservationLock)
            {
                if (SceneViewCommandObservations.TryGetValue(commandId, out var stored))
                {
                    observation = CloneSceneViewCommandObservation(stored);
                    return true;
                }
            }
            observation = null;
            return false;
        }

        private static EditorWindowCloseResultPayload CloneSceneViewCommandObservation(EditorWindowCloseResultPayload value)
        {
            return JsonUtility.FromJson<EditorWindowCloseResultPayload>(JsonUtility.ToJson(value));
        }

        internal static void ResetSceneViewCommandObservationsForTests()
        {
            lock (SceneViewCommandObservationLock) SceneViewCommandObservations.Clear();
        }

        internal static void RecordSceneViewCommandObservationForTests(EditorWindowCloseResultPayload observation)
        {
            RecordSceneViewCommandObservation(observation);
        }

        internal static bool TryGetSceneViewCommandObservationForTests(string commandId, out EditorWindowCloseResultPayload observation)
        {
            return TryGetSceneViewCommandObservation(commandId, out observation);
        }

        private static WindowRectPayload ToWindowRect(Rect value)
        {
            return new WindowRectPayload { x = value.x, y = value.y, width = value.width, height = value.height };
        }

        internal static bool WindowTitleMatches(string actual, string requested, string matchMode) =>
            string.Equals(matchMode, "contains", StringComparison.OrdinalIgnoreCase)
                ? (actual ?? string.Empty).IndexOf(requested ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0
                : string.Equals(actual ?? string.Empty, requested ?? string.Empty, StringComparison.Ordinal);

        internal static bool TryNormalizeSceneViewInstanceId(string value, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(value)
                || !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                return false;
            normalized = parsed.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        public static EditorWindowResolveResult ResolveWindow(string titleOrType, string matchMode)
        {
            if (string.IsNullOrWhiteSpace(titleOrType))
                return new EditorWindowResolveResult { window = null, info = null, multipleMatches = false };

            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            var exact = string.Equals(matchMode, "exact", StringComparison.OrdinalIgnoreCase);
            var matches = windows.Where(w =>
            {
                var t = w.titleContent?.text ?? "";
                var typeName = w.GetType().Name;
                var fullTypeName = w.GetType().FullName ?? typeName;
                return exact
                    ? string.Equals(t, titleOrType, StringComparison.Ordinal) ||
                      string.Equals(typeName, titleOrType, StringComparison.Ordinal) ||
                      string.Equals(fullTypeName, titleOrType, StringComparison.Ordinal)
                    : t.IndexOf(titleOrType, StringComparison.OrdinalIgnoreCase) >= 0 ||
                      typeName.IndexOf(titleOrType, StringComparison.OrdinalIgnoreCase) >= 0 ||
                      fullTypeName.IndexOf(titleOrType, StringComparison.OrdinalIgnoreCase) >= 0;
            }).ToList();

            var window = matches.FirstOrDefault();
            return new EditorWindowResolveResult
            {
                window = window,
                info = window == null ? null : BuildWindowInfo(window),
                multipleMatches = matches.Count > 1,
            };
        }

        public static EditorWindowResolveResult ResolveWindowByInstanceId(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
                return new EditorWindowResolveResult { window = null, info = null, multipleMatches = false };
            var matches = Resources.FindObjectsOfTypeAll<EditorWindow>()
                .Where(window => UPilotEntityIds.ToWireId(window).ToString() == instanceId)
                .ToList();
            var window = matches.FirstOrDefault();
            return new EditorWindowResolveResult
            {
                window = window,
                info = window == null ? null : BuildWindowInfo(window),
                multipleMatches = matches.Count > 1,
            };
        }

        public static EditorWindowInfo BuildWindowInfo(EditorWindow window)
        {
            if (window == null) return null;
            var typeName = window.GetType().Name;
            var fullTypeName = window.GetType().FullName ?? typeName;
            var rect = window.position;
            return new EditorWindowInfo
            {
                instanceId = UPilotEntityIds.ToWireId(window),
                domainGeneration = UPilotWindowDiagnostics.DomainReloadEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                windowHandle = UPilotWindowInputRegistry.Handle(window),
                typeName = typeName,
                fullTypeName = fullTypeName,
                title = window.titleContent?.text ?? "",
                posX = rect.x,
                posY = rect.y,
                width = rect.width,
                height = rect.height,
                hasFocus = EditorWindow.focusedWindow == window,
                docked = window.docked,
                hasUIToolkit = window.rootVisualElement != null,
            };
        }
    }
}
