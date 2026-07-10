// -----------------------------------------------------------------------
// Upilot Editor — https://github.com/codingriver/upilot
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

namespace codingriver.upilot
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
    }

    [Serializable]
    public class EditorWindowScreenshotMessage
    {
        public EditorWindowScreenshotPayload payload;
    }

    [Serializable]
    public class EditorWindowScreenshotPayload
    {
        public string windowTitle = "upilot";
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
        }

    // ── Service ─────────────────────────────────────────────────────────────────

    public class UpilotScreenshotService
    {
        private readonly UpilotBridge _bridge;

        public UpilotScreenshotService(UpilotBridge bridge)
        {
            _bridge = bridge;
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
            var title = msg?.payload?.windowTitle ?? "upilot";

            var tcs = new TaskCompletionSource<string>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    string base64 = UpilotWindowDiagnostics.CaptureEditorWindowBase64(title);
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
                await _bridge.SendErrorAsync(id, "SCREENSHOT_FAILED", ex.Message, token, "screenshot.editorWindow");
                return;
            }

            if (result == null)
            {
                await _bridge.SendErrorAsync(id, "WINDOW_NOT_FOUND",
                    $"Editor window '{title}' not found or capture not supported on this platform.",
                    token, "screenshot.editorWindow");
                return;
            }

            var payload = new ScreenshotResultPayload { imageData = result, width = 0, height = 0, format = "png" };
            await _bridge.SendResultAsync(id, "screenshot.editorWindow", payload, token);
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

                    tcs.SetResult(new ScreenshotBytesResult
                    {
                        Bytes = RenderCameraToBytes(cam, w, h, fmt, qual),
                        Width = w,
                        Height = h
                    });
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

            var tcs = new TaskCompletionSource<string>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var sceneView = SceneView.lastActiveSceneView;
                    if (sceneView == null || sceneView.camera == null)
                    {
                        tcs.SetResult(null);
                        return;
                    }

                    string base64 = RenderCameraToBase64(sceneView.camera, w, h, fmt, qual);
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
                await _bridge.SendErrorAsync(id, "SCREENSHOT_FAILED", ex.Message, token, "screenshot.sceneView");
                return;
            }

            if (result == null)
            {
                await _bridge.SendErrorAsync(id, "NO_SCENE_VIEW", "No active Scene view found.", token, "screenshot.sceneView");
                return;
            }

            var payload = new ScreenshotResultPayload { imageData = result, width = w, height = h, format = fmt };
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
            try
            {
                capture = CaptureBytes(source, p.cameraName ?? "", p.windowTitle ?? "Game", w, h, fmt, qual);
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
                    overwritten = p.overwrite
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
                string base64 = UpilotWindowDiagnostics.CaptureEditorWindowBase64(windowTitle);
                if (string.IsNullOrEmpty(base64))
                {
                    return null;
                }

                return new ScreenshotBytesResult
                {
                    Bytes = Convert.FromBase64String(base64),
                    Width = 0,
                    Height = 0,
                    Source = "editorWindow"
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
                cam = sceneView != null ? sceneView.camera : null;
            }
            else if (source == "camera")
            {
                cam = FindCamera(cameraName);
            }

            if (cam == null)
            {
                return null;
            }

            return new ScreenshotBytesResult
            {
                Bytes = RenderCameraToBytes(cam, w, h, format, quality),
                Width = w,
                Height = h,
                Source = source == "gameView" ? "gameView-camera" : source
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
