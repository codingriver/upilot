// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable]
    public class ScreenshotMessage
    {
        public ScreenshotPayload payload;
    }

    [Serializable]
    public class ScreenshotPayload
    {
        public int    width   = 1280;
        public int    height  = 720;
        public string format  = "png";
        public int    quality = 75;
    }

    [Serializable]
    public class ScreenshotCameraMessage
    {
        public ScreenshotCameraPayload payload;
    }

    [Serializable]
    public class ScreenshotCameraPayload
    {
        public string cameraName = "";
        public int    width      = 1280;
        public int    height     = 720;
        public string format     = "png";
        public int    quality    = 75;
    }

    [Serializable]
    public class ScreenshotResultPayload
    {
        public string imageData;   // Base64
        public int    width;
        public int    height;
        public string format;
        public string source;
        public bool   degraded;
        public string degradeLevel;
        public string degradeReason;
        public string matchedTitle;
        public string matchedTypeName;
        public string matchedFullTypeName;
        public ulong  matchedInstanceId;
        public bool   multipleMatches;
        public string captureApi;
        public long   windowHandle;
        public int    unityProcessId;
        public bool   foreground;
        public bool   occlusionSensitive;
        public bool   pixelSourceVerified;
        public long   repaintRequestedAtUtcMs;
        public long   repaintObservedAtUtcMs;
        public long   repaintSequence;
        public bool   includesSceneGui;
        public bool   includesHandles;
        public long   capturedAtUtcMs;
    }

    [Serializable]
    public class EditorWindowScreenshotMessage
    {
        public EditorWindowScreenshotPayload payload;
    }

    [Serializable]
    public class EditorWindowScreenshotPayload
    {
        public string windowTitle = "UPilot";
    }

    [Serializable]
    public class ScreenshotSaveMessage
    {
        public ScreenshotSavePayload payload;
    }

    [Serializable]
    public class ScreenshotSavePayload
    {
        public string path = "";
        public string source = "gameView";
        public bool overwrite = false;
        public int width = 1280;
        public int height = 720;
        public string format = "png";
        public int quality = 75;
        public string cameraName = "";
        public string windowTitle = "Game";
        public bool allowOutsideProject = false;
        public string degrade = "none";
        public string[] fallbackSources;
    }

    [Serializable]
        public class ScreenshotSaveResultPayload
        {
            public string path;
            public string source;
            public long bytes;
        public int width;
        public int height;
            public string format;
            public string sha256;
            public bool overwritten;
            public bool degraded;
            public string degradeReason;
            public string requestedSource;
            public string captureApi;
            public long windowHandle;
            public int unityProcessId;
            public bool foreground;
            public bool occlusionSensitive;
            public bool pixelSourceVerified;
            public long repaintRequestedAtUtcMs;
            public long repaintObservedAtUtcMs;
            public long repaintSequence;
            public bool includesSceneGui;
            public bool includesHandles;
            public string matchedFullTypeName;
            public ulong matchedInstanceId;
            public long capturedAtUtcMs;
        }

    // ── Service ─────────────────────────────────────────────────────────────────

    public class UPilotScreenshotService
    {
        private readonly UPilotBridge _bridge;
        private static readonly object RecentCaptureLock = new object();
        private static ScreenshotBytesResult s_recentGameViewCapture;

        public UPilotScreenshotService(UPilotBridge bridge)
        {
            _bridge = bridge;
            EditorApplication.playModeStateChanged -= CacheGameViewBeforeExit;
            EditorApplication.playModeStateChanged += CacheGameViewBeforeExit;
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("screenshot.gameView",      HandleGameViewAsync);
            _bridge.Router.Register("screenshot.sceneView",     HandleSceneViewAsync);
            _bridge.Router.Register("screenshot.camera",        HandleCameraAsync);
            _bridge.Router.Register("screenshot.editorWindow",  HandleEditorWindowAsync);
            _bridge.Router.Register("screenshot.save",          HandleSaveAsync);
        }

        // ── screenshot.editorWindow ───────────────────────────────────────────

        private async Task HandleEditorWindowAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<EditorWindowScreenshotMessage>(json);
            var title = msg?.payload?.windowTitle ?? "UPilot";

            var tcs = new TaskCompletionSource<EditorWindowCaptureResult>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var match = UPilotWindowService.ResolveWindow(title, "contains");
                    if (match.window == null)
                    {
                        tcs.SetResult(new EditorWindowCaptureResult
                        {
                            Found = false,
                            FailureReason = "WINDOW_NOT_FOUND",
                        });
                        return;
                    }

                    var info = match.info ?? UPilotWindowService.BuildWindowInfo(match.window);
                    var capture = UPilotWindowDiagnostics.CaptureEditorWindowPixels(match.window);
                    if (capture == null || string.IsNullOrEmpty(capture.imageData))
                    {
                        tcs.SetResult(new EditorWindowCaptureResult
                        {
                            Found = true,
                            Captured = false,
                            FailureReason = "EDITOR_WINDOW_CAPTURE_UNAVAILABLE",
                            Payload = BuildEditorWindowScreenshotPayload(null, info, match.multipleMatches),
                        });
                        return;
                    }

                    tcs.SetResult(new EditorWindowCaptureResult
                    {
                        Found = true,
                        Captured = true,
                        Payload = BuildEditorWindowScreenshotPayload(capture, info, match.multipleMatches),
                    });
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            EditorWindowCaptureResult result;
            try
            {
                result = await tcs.Task;
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCREENSHOT_FAILED", ex.Message, token, "screenshot.editorWindow");
                return;
            }

            if (result == null || !result.Found)
            {
                await _bridge.SendErrorAsync(id, "WINDOW_NOT_FOUND",
                    $"Editor window '{title}' not found or capture not supported on this platform.",
                    token, "screenshot.editorWindow");
                return;
            }

            if (!result.Captured)
            {
                await _bridge.SendErrorAsync(id, result.FailureReason ?? "SCREENSHOT_UNAVAILABLE",
                    $"Editor window '{title}' was found but could not be captured.",
                    token, "screenshot.editorWindow");
                return;
            }

            await _bridge.SendResultAsync(id, "screenshot.editorWindow", result.Payload, token);
        }

        private sealed class EditorWindowCaptureResult
        {
            public bool Found;
            public bool Captured;
            public string FailureReason;
            public ScreenshotResultPayload Payload;
        }

        private static ScreenshotResultPayload BuildEditorWindowScreenshotPayload(
            EditorWindowPixelCapture capture,
            EditorWindowInfo info,
            bool multipleMatches)
        {
            return new ScreenshotResultPayload
            {
                imageData = capture?.imageData ?? "",
                width = capture?.width ?? (info == null ? 0 : Mathf.Max(1, Mathf.RoundToInt(info.width))),
                height = capture?.height ?? (info == null ? 0 : Mathf.Max(1, Mathf.RoundToInt(info.height))),
                format = "png",
                source = "editorWindow",
                degraded = capture?.degraded ?? true,
                degradeLevel = capture != null && capture.degraded ? "occlusion_sensitive_fallback" : "",
                degradeReason = capture?.degradeReason ?? "",
                matchedTitle = info?.title ?? "",
                matchedTypeName = info?.typeName ?? "",
                matchedFullTypeName = info?.fullTypeName ?? "",
                multipleMatches = multipleMatches,
                captureApi = capture?.captureApi ?? "",
                windowHandle = capture?.windowHandle ?? 0,
                unityProcessId = capture?.unityProcessId ?? 0,
                foreground = capture?.foreground ?? false,
                occlusionSensitive = capture?.occlusionSensitive ?? true,
                pixelSourceVerified = capture?.pixelSourceVerified ?? false,
                repaintRequestedAtUtcMs = capture?.repaintRequestedAtUtcMs ?? 0,
                capturedAtUtcMs = capture?.capturedAtUtcMs ?? 0,
            };
        }

        // ── screenshot.gameView ─────────────────────────────────────────────────

        private async Task HandleGameViewAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ScreenshotMessage>(json);
            var p   = msg?.payload ?? new ScreenshotPayload();

            int w = Clamp(p.width,  1, 4096, 1280);
            int h = Clamp(p.height, 1, 4096, 720);
            string fmt = NormalizeFormat(p.format);
            int qual   = Clamp(p.quality, 1, 100, 75);

            var tcs = new TaskCompletionSource<ScreenshotBytesResult>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var cam = GetGameViewCamera();
                    if (cam == null)
                    {
                        tcs.SetResult(null);
                        return;
                    }

                    var capture = new ScreenshotBytesResult
                    {
                        Bytes = RenderCameraToBytes(cam, w, h, fmt, qual),
                        Width = w,
                        Height = h
                    };
                    CacheRecentGameView(capture);
                    tcs.SetResult(capture);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            ScreenshotBytesResult result;
            try
            {
                result = await tcs.Task;
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCREENSHOT_FAILED", ex.Message, token, "screenshot.gameView");
                return;
            }

            if (result == null)
            {
                await _bridge.SendErrorAsync(id, "NO_CAMERA", "No camera found for Game view capture.", token, "screenshot.gameView");
                return;
            }

            var payload = new ScreenshotResultPayload
            {
                imageData = Convert.ToBase64String(result.Bytes),
                width = result.Width,
                height = result.Height,
                format = fmt
            };
            await _bridge.SendResultAsync(id, "screenshot.gameView", payload, token);
        }

        // ── screenshot.sceneView ────────────────────────────────────────────────

        private async Task HandleSceneViewAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ScreenshotMessage>(json);
            var p   = msg?.payload ?? new ScreenshotPayload();

            int w = Clamp(p.width,  1, 4096, 1280);
            int h = Clamp(p.height, 1, 4096, 720);
            string fmt = NormalizeFormat(p.format);
            int qual   = Clamp(p.quality, 1, 100, 75);

            var tcs = new TaskCompletionSource<ScreenshotBytesResult>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var sceneView = SceneView.lastActiveSceneView;
                    if (sceneView == null)
                    {
                        tcs.SetResult(null);
                        return;
                    }

                    CaptureSceneViewAfterRepaint(sceneView, w, h, fmt, qual, tcs);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            ScreenshotBytesResult result;
            try
            {
                result = await tcs.Task;
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCREENSHOT_FAILED", ex.Message, token, "screenshot.sceneView");
                return;
            }

            if (result == null)
            {
                await _bridge.SendErrorAsync(id, "NO_SCENE_VIEW", "No active Scene view found.", token, "screenshot.sceneView");
                return;
            }

            var payload = BuildScreenshotPayload(result, fmt);
            await _bridge.SendResultAsync(id, "screenshot.sceneView", payload, token);
        }

        // ── screenshot.camera ───────────────────────────────────────────────────

        private async Task HandleCameraAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ScreenshotCameraMessage>(json);
            var p   = msg?.payload ?? new ScreenshotCameraPayload();

            string camName = p.cameraName ?? "";
            int w = Clamp(p.width,  1, 4096, 1280);
            int h = Clamp(p.height, 1, 4096, 720);
            string fmt = NormalizeFormat(p.format);
            int qual   = Clamp(p.quality, 1, 100, 75);

            var tcs = new TaskCompletionSource<string>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    Camera cam = null;
                    if (!string.IsNullOrEmpty(camName))
                    {
                        // Find camera by name in the scene
                        foreach (var c in Camera.allCameras)
                        {
                            if (string.Equals(c.name, camName, StringComparison.OrdinalIgnoreCase))
                            {
                                cam = c;
                                break;
                            }
                        }
                        if (cam == null)
                        {
                            // Also check disabled cameras via FindObjectsOfType(true)
                            foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>(true))
                            {
                                if (string.Equals(c.name, camName, StringComparison.OrdinalIgnoreCase))
                                {
                                    cam = c;
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        cam = Camera.main;
                    }

                    if (cam == null)
                    {
                        tcs.SetResult(null);
                        return;
                    }

                    string base64 = RenderCameraToBase64(cam, w, h, fmt, qual);
                    tcs.SetResult(base64);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            string result;
            try
            {
                result = await tcs.Task;
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCREENSHOT_FAILED", ex.Message, token, "screenshot.camera");
                return;
            }

            if (result == null)
            {
                string errMsg = string.IsNullOrEmpty(camName)
                    ? "No main camera found in the scene."
                    : $"Camera '{camName}' not found.";
                await _bridge.SendErrorAsync(id, "CAMERA_NOT_FOUND", errMsg, token, "screenshot.camera");
                return;
            }

            var payload = new ScreenshotResultPayload { imageData = result, width = w, height = h, format = fmt };
            await _bridge.SendResultAsync(id, "screenshot.camera", payload, token);
        }

        // ── screenshot.save ─────────────────────────────────────────────────────

        private async Task HandleSaveAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ScreenshotSaveMessage>(json);
            var p = msg?.payload ?? new ScreenshotSavePayload();

            if (NormalizeSource(p.source) == "sceneView")
            {
                await HandleSceneViewSaveAsync(id, p, token);
                return;
            }

            var tcs = new TaskCompletionSource<ScreenshotSaveResultPayload>();
            string errorCode = string.Empty;
            string errorMessage = string.Empty;
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    if (TrySaveScreenshot(p, out var result, out errorCode, out errorMessage))
                    {
                        tcs.SetResult(result);
                    }
                    else
                    {
                        tcs.SetResult(null);
                    }
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            ScreenshotSaveResultPayload payload;
            try
            {
                payload = await tcs.Task;
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCREENSHOT_FAILED", ex.Message, token, "screenshot.save");
                return;
            }

            if (payload == null)
            {
                await _bridge.SendErrorAsync(id,
                    string.IsNullOrEmpty(errorCode) ? "SCREENSHOT_FAILED" : errorCode,
                    string.IsNullOrEmpty(errorMessage) ? "Screenshot save failed." : errorMessage,
                    token,
                    "screenshot.save");
                return;
            }

            await _bridge.SendResultAsync(id, "screenshot.save", payload, token);
        }

        private async Task HandleSceneViewSaveAsync(string id, ScreenshotSavePayload payload, CancellationToken token)
        {
            var captureCompletion = new TaskCompletionSource<ScreenshotBytesResult>();
            _bridge.EnqueueTracked(id, () =>
            {
                var sceneView = SceneView.lastActiveSceneView;
                if (sceneView == null)
                {
                    captureCompletion.TrySetResult(null);
                    return;
                }
                CaptureSceneViewAfterRepaint(
                    sceneView,
                    Clamp(payload.width, 1, 4096, 1280),
                    Clamp(payload.height, 1, 4096, 720),
                    "png",
                    Clamp(payload.quality, 1, 100, 75),
                    captureCompletion);
            });

            try
            {
                ScreenshotBytesResult capture = await captureCompletion.Task;
                if (capture == null)
                {
                    await _bridge.SendErrorAsync(id, "NO_SCENE_VIEW", "No active SceneView could be captured.", token, "screenshot.save");
                    return;
                }
                if (!TryWriteCapturedScreenshot(payload, capture, out var result, out string errorCode, out string errorMessage))
                {
                    await _bridge.SendErrorAsync(id, errorCode, errorMessage, token, "screenshot.save");
                    return;
                }
                await _bridge.SendResultAsync(id, "screenshot.save", result, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCREENSHOT_FAILED", ex.Message, token, "screenshot.save");
            }
        }

        private static bool TryWriteCapturedScreenshot(
            ScreenshotSavePayload payload, ScreenshotBytesResult capture,
            out ScreenshotSaveResultPayload result, out string errorCode, out string errorMessage)
        {
            result = null;
            errorCode = string.Empty;
            errorMessage = string.Empty;
            if (NormalizeFormat(payload.format) != "png")
            {
                errorCode = "INVALID_SCREENSHOT_FORMAT";
                errorMessage = "screenshot.save only supports format=png.";
                return false;
            }
            string targetPath = ResolveSavePath(payload.path, payload.allowOutsideProject, out string pathError);
            if (!string.IsNullOrEmpty(pathError))
            {
                errorCode = "INVALID_SCREENSHOT_PATH";
                errorMessage = pathError;
                return false;
            }
            if (File.Exists(targetPath) && !payload.overwrite)
            {
                errorCode = "FILE_EXISTS";
                errorMessage = $"Screenshot target already exists: {targetPath}";
                return false;
            }
            try
            {
                string directory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                string temporaryPath = Path.Combine(directory ?? "", $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
                File.WriteAllBytes(temporaryPath, capture.Bytes);
                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.Move(temporaryPath, targetPath);
                var info = new FileInfo(targetPath);
                result = new ScreenshotSaveResultPayload
                {
                    path = targetPath, source = capture.Source, bytes = info.Length,
                    width = capture.Width, height = capture.Height, format = "png", sha256 = ComputeSha256(capture.Bytes),
                    overwritten = payload.overwrite, degraded = capture.Degraded, degradeReason = capture.DegradeReason,
                    requestedSource = "sceneView", captureApi = capture.CaptureApi, windowHandle = capture.WindowHandle,
                    unityProcessId = capture.UnityProcessId, foreground = capture.Foreground,
                    occlusionSensitive = capture.OcclusionSensitive, pixelSourceVerified = capture.PixelSourceVerified,
                    repaintRequestedAtUtcMs = capture.RepaintRequestedAtUtcMs,
                    repaintObservedAtUtcMs = capture.RepaintObservedAtUtcMs, repaintSequence = capture.RepaintSequence,
                    includesSceneGui = capture.IncludesSceneGui, includesHandles = capture.IncludesHandles,
                    matchedFullTypeName = capture.MatchedFullTypeName, matchedInstanceId = capture.MatchedInstanceId,
                    capturedAtUtcMs = capture.CapturedAtUtcMs,
                };
                return true;
            }
            catch (Exception ex)
            {
                errorCode = "SCREENSHOT_WRITE_FAILED";
                errorMessage = ex.Message;
                return false;
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Save a Unity screenshot to disk.
        /// This public API is intended for Editor automation that needs a deterministic file path.
        /// </summary>
        public static bool TrySaveScreenshot(
            ScreenshotSavePayload payload,
            out ScreenshotSaveResultPayload result,
            out string errorCode,
            out string errorMessage)
        {
            result = null;
            errorCode = string.Empty;
            errorMessage = string.Empty;

            ScreenshotSavePayload p = payload ?? new ScreenshotSavePayload();
            string fmt = NormalizeFormat(p.format);
            if (fmt != "png")
            {
                errorCode = "INVALID_SCREENSHOT_FORMAT";
                errorMessage = "screenshot.save only supports format=png.";
                return false;
            }

            string targetPath = ResolveSavePath(p.path, p.allowOutsideProject, out string pathError);
            if (!string.IsNullOrEmpty(pathError))
            {
                errorCode = "INVALID_SCREENSHOT_PATH";
                errorMessage = pathError;
                return false;
            }

            if (File.Exists(targetPath) && !p.overwrite)
            {
                errorCode = "FILE_EXISTS";
                errorMessage = $"Screenshot target already exists: {targetPath}";
                return false;
            }

            int w = Clamp(p.width, 1, 4096, 1280);
            int h = Clamp(p.height, 1, 4096, 720);
            int qual = Clamp(p.quality, 1, 100, 75);
            string source = NormalizeSource(p.source);

            ScreenshotBytesResult capture;
            string requestedSource = source;
            string degradeReason = string.Empty;
            bool degraded = false;
            try
            {
                capture = CaptureBytes(source, p.cameraName ?? "", p.windowTitle ?? "Game", w, h, fmt, qual);
                if (capture == null && !string.Equals(p.degrade, "none", StringComparison.OrdinalIgnoreCase))
                {
                    var fallbacks = p.fallbackSources != null && p.fallbackSources.Length > 0
                        ? p.fallbackSources
                        : new[] { "recentGameView", "camera", "sceneView", "editorWindow" };
                    foreach (var fallback in fallbacks)
                    {
                        capture = CaptureFallbackBytes(fallback, p.cameraName ?? "", p.windowTitle ?? "Game", w, h, fmt, qual);
                        if (capture == null) continue;
                        degraded = true;
                        degradeReason = $"{requestedSource} unavailable; used {capture.Source}";
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                errorCode = "SCREENSHOT_FAILED";
                errorMessage = ex.Message;
                return false;
            }

            if (capture == null || capture.Bytes == null || capture.Bytes.Length == 0)
            {
                errorCode = "SCREENSHOT_FAILED";
                errorMessage = $"No screenshot data captured for source={source}.";
                return false;
            }

            try
            {
                string dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tempPath = Path.Combine(dir ?? "", $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
                File.WriteAllBytes(tempPath, capture.Bytes);
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                File.Move(tempPath, targetPath);

                var info = new FileInfo(targetPath);
                result = new ScreenshotSaveResultPayload
                {
                    path = targetPath,
                    source = string.IsNullOrEmpty(capture.Source) ? source : capture.Source,
                    bytes = info.Length,
                    width = capture.Width,
                    height = capture.Height,
                    format = "png",
                    sha256 = ComputeSha256(capture.Bytes),
                    overwritten = p.overwrite,
                    degraded = degraded || capture.Degraded,
                    degradeReason = !string.IsNullOrEmpty(degradeReason) ? degradeReason : capture.DegradeReason,
                    requestedSource = requestedSource,
                    captureApi = capture.CaptureApi,
                    windowHandle = capture.WindowHandle,
                    unityProcessId = capture.UnityProcessId,
                    foreground = capture.Foreground,
                    occlusionSensitive = capture.OcclusionSensitive,
                    pixelSourceVerified = capture.PixelSourceVerified,
                    repaintRequestedAtUtcMs = capture.RepaintRequestedAtUtcMs,
                    repaintObservedAtUtcMs = capture.RepaintObservedAtUtcMs,
                    repaintSequence = capture.RepaintSequence,
                    includesSceneGui = capture.IncludesSceneGui,
                    includesHandles = capture.IncludesHandles,
                    matchedFullTypeName = capture.MatchedFullTypeName,
                    matchedInstanceId = capture.MatchedInstanceId,
                    capturedAtUtcMs = capture.CapturedAtUtcMs,
                };
                return true;
            }
            catch (Exception ex)
            {
                errorCode = "SCREENSHOT_WRITE_FAILED";
                errorMessage = ex.Message;
                return false;
            }
        }

        private class ScreenshotBytesResult
        {
            public byte[] Bytes;
            public int Width;
            public int Height;
            public string Source;
            public bool Degraded;
            public string DegradeReason;
            public string CaptureApi;
            public long WindowHandle;
            public int UnityProcessId;
            public bool Foreground;
            public bool OcclusionSensitive;
            public bool PixelSourceVerified;
            public long RepaintRequestedAtUtcMs;
            public long RepaintObservedAtUtcMs;
            public long RepaintSequence;
            public bool IncludesSceneGui;
            public bool IncludesHandles;
            public string MatchedFullTypeName;
            public ulong MatchedInstanceId;
            public long CapturedAtUtcMs;
        }

        private static long s_sceneViewRepaintSequence;

        private static void CaptureSceneViewAfterRepaint(
            SceneView sceneView, int width, int height, string format, int quality,
            TaskCompletionSource<ScreenshotBytesResult> completion)
        {
            long requestedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            double deadline = EditorApplication.timeSinceStartup + 5.0;
            bool captureScheduled = false;

            Action<SceneView> onSceneGui = null;
            EditorApplication.CallbackFunction onUpdate = null;
            Action cleanup = () =>
            {
                SceneView.duringSceneGui -= onSceneGui;
                EditorApplication.update -= onUpdate;
            };

            onSceneGui = current =>
            {
                if (captureScheduled || current == null
                    || UPilotEntityIds.ToWireId(current) != UPilotEntityIds.ToWireId(sceneView))
                    return;
                if (Event.current == null || Event.current.type != EventType.Repaint)
                    return;
                captureScheduled = true;
                long observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long sequence = Interlocked.Increment(ref s_sceneViewRepaintSequence);
                EditorApplication.delayCall += () =>
                {
                    cleanup();
                    try
                    {
                        var pixels = UPilotWindowDiagnostics.CaptureEditorWindowPixels(sceneView);
                        ScreenshotBytesResult result;
                        if (pixels != null && !string.IsNullOrEmpty(pixels.imageData))
                        {
                            result = FromEditorWindowCapture(pixels, "sceneView");
                            result.RepaintRequestedAtUtcMs = requestedAt;
                            result.RepaintObservedAtUtcMs = observedAt;
                            result.RepaintSequence = sequence;
                            result.IncludesSceneGui = true;
                            result.IncludesHandles = true;
                            result.MatchedFullTypeName = sceneView.GetType().FullName;
                            result.MatchedInstanceId = UPilotEntityIds.ToWireId(sceneView);
                        }
                        else if (sceneView.camera != null)
                        {
                            result = new ScreenshotBytesResult
                            {
                                Bytes = RenderCameraToBytes(sceneView.camera, width, height, format, quality),
                                Width = width, Height = height, Source = "sceneView-camera", Degraded = true,
                                DegradeReason = "EditorWindow pixel capture unavailable; camera render excludes Handles and overlays.",
                                CaptureApi = "Camera.Render", PixelSourceVerified = false,
                                RepaintRequestedAtUtcMs = requestedAt, RepaintObservedAtUtcMs = observedAt,
                                RepaintSequence = sequence, IncludesSceneGui = false, IncludesHandles = false,
                                MatchedFullTypeName = sceneView.GetType().FullName,
                                MatchedInstanceId = UPilotEntityIds.ToWireId(sceneView),
                            };
                        }
                        else result = null;
                        completion.TrySetResult(result);
                    }
                    catch (Exception ex) { completion.TrySetException(ex); }
                };
            };
            onUpdate = () =>
            {
                if (captureScheduled || EditorApplication.timeSinceStartup < deadline)
                    return;
                cleanup();
                completion.TrySetException(new TimeoutException(
                    $"SceneView {UPilotEntityIds.ToWireId(sceneView)} did not complete a Repaint event within 5 seconds."));
            };

            SceneView.duringSceneGui += onSceneGui;
            EditorApplication.update += onUpdate;
            SceneView.RepaintAll();
            sceneView.Repaint();
        }

        private static void CacheRecentGameView(ScreenshotBytesResult capture)
        {
            if (capture == null || capture.Bytes == null || capture.Bytes.Length == 0) return;
            lock (RecentCaptureLock)
            {
                s_recentGameViewCapture = new ScreenshotBytesResult
                {
                    Bytes = (byte[])capture.Bytes.Clone(),
                    Width = capture.Width,
                    Height = capture.Height,
                    Source = "recentGameView",
                };
            }
        }

        private static void CacheGameViewBeforeExit(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingPlayMode) return;
            try
            {
                var cam = GetGameViewCamera();
                if (cam == null) return;
                CacheRecentGameView(new ScreenshotBytesResult
                {
                    Bytes = RenderCameraToBytes(cam, 640, 360, "png", 75),
                    Width = 640,
                    Height = 360,
                    Source = "recentGameView",
                });
            }
            catch (Exception ex)
            {
                Logger.LogWarning("SCREENSHOT", $"Failed to cache GameView before PlayMode exit: {ex.Message}");
            }
        }

        private static ScreenshotBytesResult CaptureFallbackBytes(string fallback, string cameraName, string windowTitle, int w, int h, string format, int quality)
        {
            var value = (fallback ?? string.Empty).Trim();
            if (string.Equals(value, "recentGameView", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "recent", StringComparison.OrdinalIgnoreCase))
            {
                lock (RecentCaptureLock)
                {
                    if (s_recentGameViewCapture == null) return null;
                    return new ScreenshotBytesResult
                    {
                        Bytes = (byte[])s_recentGameViewCapture.Bytes.Clone(),
                        Width = s_recentGameViewCapture.Width,
                        Height = s_recentGameViewCapture.Height,
                        Source = "recentGameView",
                    };
                }
            }
            return CaptureBytes(NormalizeSource(value), cameraName, windowTitle, w, h, format, quality);
        }

        /// <summary>Render a camera to RenderTexture → Texture2D → Base64 string.</summary>
        private static string RenderCameraToBase64(Camera cam, int w, int h, string format, int quality)
        {
            return Convert.ToBase64String(RenderCameraToBytes(cam, w, h, format, quality));
        }

        private static byte[] RenderCameraToBytes(Camera cam, int w, int h, string format, int quality)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 1;

            var prevRT  = cam.targetTexture;
            var prevActive = RenderTexture.active;

            try
            {
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();

                byte[] bytes;
                if (format == "jpg")
                    bytes = tex.EncodeToJPG(quality);
                else
                    bytes = tex.EncodeToPNG();

                UnityEngine.Object.DestroyImmediate(tex);
                return bytes;
            }
            finally
            {
                cam.targetTexture  = prevRT;
                RenderTexture.active = prevActive;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        /// <summary>Get the main camera used for the Game view.</summary>
        private static Camera GetGameViewCamera()
        {
            // Prefer Camera.main, then fall back to first enabled camera
            if (Camera.main != null) return Camera.main;
            var allCams = Camera.allCameras;
            return allCams.Length > 0 ? allCams[0] : null;
        }

        private static ScreenshotBytesResult CaptureBytes(string source, string cameraName, string windowTitle, int w, int h, string format, int quality)
        {
            if (source == "editorWindow")
            {
                var match = UPilotWindowService.ResolveWindow(windowTitle, "contains");
                if (match.window == null) return null;
                var pixels = UPilotWindowDiagnostics.CaptureEditorWindowPixels(match.window);
                if (pixels == null || string.IsNullOrEmpty(pixels.imageData))
                {
                    return null;
                }

                return new ScreenshotBytesResult
                {
                    Bytes = Convert.FromBase64String(pixels.imageData), Width = pixels.width, Height = pixels.height,
                    Source = "editorWindow", Degraded = pixels.degraded, DegradeReason = pixels.degradeReason,
                    CaptureApi = pixels.captureApi, WindowHandle = pixels.windowHandle, UnityProcessId = pixels.unityProcessId,
                    Foreground = pixels.foreground, OcclusionSensitive = pixels.occlusionSensitive,
                    PixelSourceVerified = pixels.pixelSourceVerified, RepaintRequestedAtUtcMs = pixels.repaintRequestedAtUtcMs,
                    CapturedAtUtcMs = pixels.capturedAtUtcMs,
                };
            }

            Camera cam = null;
            if (source == "gameView")
            {
                cam = GetGameViewCamera();
            }
            else if (source == "sceneView")
            {
                var sceneView = SceneView.lastActiveSceneView;
                if (sceneView != null)
                {
                    SceneView.RepaintAll();
                    var pixels = UPilotWindowDiagnostics.CaptureEditorWindowPixels(sceneView);
                    if (pixels != null && !string.IsNullOrEmpty(pixels.imageData)) return FromEditorWindowCapture(pixels, "sceneView");
                    cam = sceneView.camera;
                }
            }
            else if (source == "camera")
            {
                cam = FindCamera(cameraName);
            }

            if (cam == null)
            {
                return null;
            }

            var result = new ScreenshotBytesResult
            {
                Bytes = RenderCameraToBytes(cam, w, h, format, quality),
                Width = w,
                Height = h,
                Source = source == "gameView" ? "gameView-camera" : (source == "sceneView" ? "sceneView-camera" : source),
                Degraded = source == "sceneView",
                DegradeReason = source == "sceneView" ? "EditorWindow pixel capture unavailable; camera render excludes Handles and overlays." : "",
                CaptureApi = "Camera.Render",
                PixelSourceVerified = source != "sceneView",
            };
            if (source == "gameView") CacheRecentGameView(result);
            return result;
        }

        private static ScreenshotBytesResult FromEditorWindowCapture(EditorWindowPixelCapture pixels, string source)
        {
            return new ScreenshotBytesResult
            {
                Bytes = Convert.FromBase64String(pixels.imageData), Width = pixels.width, Height = pixels.height, Source = source,
                Degraded = pixels.degraded, DegradeReason = pixels.degradeReason, CaptureApi = pixels.captureApi,
                WindowHandle = pixels.windowHandle, UnityProcessId = pixels.unityProcessId, Foreground = pixels.foreground,
                OcclusionSensitive = pixels.occlusionSensitive, PixelSourceVerified = pixels.pixelSourceVerified,
                RepaintRequestedAtUtcMs = pixels.repaintRequestedAtUtcMs, CapturedAtUtcMs = pixels.capturedAtUtcMs,
            };
        }

        private static ScreenshotResultPayload BuildScreenshotPayload(ScreenshotBytesResult result, string format)
        {
            return new ScreenshotResultPayload
            {
                imageData = Convert.ToBase64String(result.Bytes), width = result.Width, height = result.Height, format = format,
                source = result.Source, degraded = result.Degraded, degradeReason = result.DegradeReason,
                degradeLevel = result.Degraded ? "capture_fallback" : "", captureApi = result.CaptureApi,
                windowHandle = result.WindowHandle, unityProcessId = result.UnityProcessId, foreground = result.Foreground,
                occlusionSensitive = result.OcclusionSensitive, pixelSourceVerified = result.PixelSourceVerified,
                repaintRequestedAtUtcMs = result.RepaintRequestedAtUtcMs, capturedAtUtcMs = result.CapturedAtUtcMs,
                repaintObservedAtUtcMs = result.RepaintObservedAtUtcMs, repaintSequence = result.RepaintSequence,
                includesSceneGui = result.IncludesSceneGui, includesHandles = result.IncludesHandles,
                matchedFullTypeName = result.MatchedFullTypeName, matchedInstanceId = result.MatchedInstanceId,
            };
        }

        private static Camera FindCamera(string camName)
        {
            if (string.IsNullOrEmpty(camName))
            {
                return Camera.main;
            }

            foreach (var c in Camera.allCameras)
            {
                if (string.Equals(c.name, camName, StringComparison.OrdinalIgnoreCase))
                {
                    return c;
                }
            }

            foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>(true))
            {
                if (string.Equals(c.name, camName, StringComparison.OrdinalIgnoreCase))
                {
                    return c;
                }
            }

            return null;
        }

        private static string NormalizeSource(string source)
        {
            string value = string.IsNullOrEmpty(source) ? "gameView" : source.Trim().ToLowerInvariant();
            if (value == "gameview" || value == "game_view" || value == "game") return "gameView";
            if (value == "sceneview" || value == "scene_view" || value == "scene") return "sceneView";
            if (value == "editorwindow" || value == "editor_window" || value == "window") return "editorWindow";
            if (value == "camera") return "camera";
            return "gameView";
        }

        private static string ResolveSavePath(string rawPath, bool allowOutsideProject, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrEmpty(rawPath))
            {
                error = "path is required.";
                return string.Empty;
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string targetPath = rawPath;
            if (!Path.IsPathRooted(targetPath))
            {
                targetPath = Path.Combine(projectRoot, targetPath);
            }

            targetPath = Path.GetFullPath(targetPath);
            if (!string.Equals(Path.GetExtension(targetPath), ".png", StringComparison.OrdinalIgnoreCase))
            {
                error = "screenshot.save only writes .png files.";
                return string.Empty;
            }

            if (!allowOutsideProject)
            {
                string rootWithSeparator = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (!targetPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"path must be under current Unity project: {projectRoot}";
                    return string.Empty;
                }
            }

            return targetPath;
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string NormalizeFormat(string fmt)
        {
            if (string.IsNullOrEmpty(fmt)) return "png";
            fmt = fmt.ToLowerInvariant().Trim();
            return fmt == "jpg" || fmt == "jpeg" ? "jpg" : "png";
        }

        private static int Clamp(int value, int min, int max, int defaultValue)
        {
            if (value <= 0) return defaultValue;
            return value < min ? min : (value > max ? max : value);
        }
    }
}
