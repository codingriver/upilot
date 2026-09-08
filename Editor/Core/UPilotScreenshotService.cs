// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    // Compatibility DTOs retained for serialized clients and focused contract tests.
    // Public screenshot tools are implemented exclusively as Snapshot wrappers.
    [Serializable]
    public class ScreenshotSavePayload
    {
        public string path = "";
        public string source = "gameView";
        public bool overwrite;
        public int width = 1280;
        public int height = 720;
        public string format = "png";
        public int quality = 75;
        public string cameraName = "";
        public string windowTitle = "Game";
        public bool allowOutsideProject;
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
    }

    /// <summary>
    /// Low-level pixel primitives shared by Snapshot capture.
    /// This type intentionally registers no Bridge commands: the public
    /// unity_screenshot_* surface is a thin wrapper over Snapshot schema v1.
    /// </summary>
    public static class UPilotScreenshotService
    {
        internal class ScreenshotBytesResult
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
            public int RequestedWidth;
            public int RequestedHeight;
            public int ActualColorWidth;
            public int ActualColorHeight;
            public int ActualDepthWidth;
            public int ActualDepthHeight;
            public int RenderRetryCount;
        }

        internal sealed class CameraRenderCapture
        {
            public byte[] Bytes;
            public int RequestedWidth;
            public int RequestedHeight;
            public int Width;
            public int Height;
            public int ColorWidth;
            public int ColorHeight;
            public int DepthWidth;
            public int DepthHeight;
            public int RetryCount;
        }

        private static long s_sceneViewRepaintSequence;

        internal static void CaptureSceneViewAfterRepaint(
            SceneView sceneView,
            int width,
            int height,
            string format,
            int quality,
            TaskCompletionSource<ScreenshotBytesResult> completion,
            bool allowCameraFallback = false)
        {
            if (sceneView == null)
            {
                completion.TrySetException(new ArgumentNullException(nameof(sceneView)));
                return;
            }

            var requestedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var deadline = EditorApplication.timeSinceStartup + 5.0;
            var captureScheduled = false;
            long observedAt = 0;
            long sequence = 0;

            Action<SceneView> onSceneGui = null;
            EditorApplication.CallbackFunction onUpdate = null;
            AssemblyReloadEvents.AssemblyReloadCallback onReload = null;
            Timer watchdog = null;
            Action cleanup = () =>
            {
                SceneView.duringSceneGui -= onSceneGui;
                EditorApplication.update -= onUpdate;
                AssemblyReloadEvents.beforeAssemblyReload -= onReload;
                watchdog?.Dispose();
            };

            onSceneGui = current =>
            {
                if (completion.Task.IsCompleted || captureScheduled || current == null || sceneView == null
                    || UPilotEntityIds.ToWireId(current) != UPilotEntityIds.ToWireId(sceneView))
                    return;
                if (Event.current == null || Event.current.type != EventType.Repaint)
                    return;
                captureScheduled = true;
                observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                sequence = Interlocked.Increment(ref s_sceneViewRepaintSequence);
            };

            onUpdate = () =>
            {
                if (completion.Task.IsCompleted) { cleanup(); return; }
                if (sceneView == null)
                {
                    cleanup();
                    completion.TrySetException(new InvalidOperationException("SCENEVIEW_CLOSED"));
                    return;
                }
                if (captureScheduled)
                {
                    try
                    {
                        var pixels = UPilotWindowDiagnostics.CaptureEditorWindowPixels(sceneView, allowCameraFallback);
                        ScreenshotBytesResult result = null;
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
                        else if (allowCameraFallback && sceneView.camera != null)
                        {
                            var rendered = RenderCamera(sceneView.camera, width, height, format, quality);
                            result = FromCameraRender(rendered, "sceneView-camera");
                            result.Degraded = true;
                            result.DegradeReason = "EditorWindow pixel capture unavailable; camera render excludes Handles and overlays.";
                            result.PixelSourceVerified = false;
                            result.RepaintRequestedAtUtcMs = requestedAt;
                            result.RepaintObservedAtUtcMs = observedAt;
                            result.RepaintSequence = sequence;
                            result.IncludesSceneGui = false;
                            result.IncludesHandles = false;
                            result.MatchedFullTypeName = sceneView.GetType().FullName;
                            result.MatchedInstanceId = UPilotEntityIds.ToWireId(sceneView);
                        }
                        completion.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                    finally { cleanup(); }
                    return;
                }

                if (EditorApplication.timeSinceStartup < deadline)
                    return;
                cleanup();
                completion.TrySetException(new TimeoutException(
                    $"SceneView {UPilotEntityIds.ToWireId(sceneView)} did not complete a Repaint event within 5 seconds."));
            };

            onReload = () =>
            {
                cleanup();
                completion.TrySetException(new InvalidOperationException("SCENEVIEW_DOMAIN_RELOAD"));
            };
            // Deadline observation cannot depend on Editor.update pumping.
            watchdog = new Timer(_ => completion.TrySetException(new TimeoutException("SCENEVIEW_REPAINT_TIMEOUT")),
                null, 5000, Timeout.Infinite);
            AssemblyReloadEvents.beforeAssemblyReload += onReload;
            SceneView.duringSceneGui += onSceneGui;
            EditorApplication.update += onUpdate;
            SceneView.RepaintAll();
            sceneView.Repaint();
        }

        internal static RenderTextureDescriptor BuildCameraRenderDescriptor(int width, int height)
        {
            return new RenderTextureDescriptor(
                Mathf.Max(1, width),
                Mathf.Max(1, height),
                RenderTextureFormat.ARGB32,
                24)
            {
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false,
                useDynamicScale = false,
                volumeDepth = 1,
            };
        }

        internal static CameraRenderCapture RenderCamera(
            Camera camera,
            int requestedWidth,
            int requestedHeight,
            string format,
            int quality)
        {
            if (camera == null)
                throw new ArgumentNullException(nameof(camera));

            var descriptor = BuildCameraRenderDescriptor(requestedWidth, requestedHeight);
            var renderTexture = new RenderTexture(descriptor);
            renderTexture.Create();
            if (!renderTexture.IsCreated()
                || renderTexture.width != descriptor.width
                || renderTexture.height != descriptor.height)
            {
                throw new InvalidOperationException(
                    $"Failed to create fixed screenshot render target {descriptor.width}x{descriptor.height}; "
                    + $"actual={renderTexture.width}x{renderTexture.height}.");
            }

            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var previousDynamicResolution = camera.allowDynamicResolution;
            try
            {
                camera.allowDynamicResolution = false;
                camera.targetTexture = renderTexture;
                camera.Render();

                RenderTexture.active = renderTexture;
                var texture = new Texture2D(descriptor.width, descriptor.height, TextureFormat.RGBA32, false);
                try
                {
                    texture.ReadPixels(new Rect(0, 0, descriptor.width, descriptor.height), 0, 0);
                    texture.Apply();
                    return new CameraRenderCapture
                    {
                        Bytes = string.Equals(format, "jpg", StringComparison.OrdinalIgnoreCase)
                            ? texture.EncodeToJPG(quality)
                            : texture.EncodeToPNG(),
                        RequestedWidth = requestedWidth,
                        RequestedHeight = requestedHeight,
                        Width = descriptor.width,
                        Height = descriptor.height,
                        ColorWidth = renderTexture.width,
                        ColorHeight = renderTexture.height,
                        DepthWidth = renderTexture.width,
                        DepthHeight = renderTexture.height,
                    };
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
            finally
            {
                camera.targetTexture = previousTarget;
                camera.allowDynamicResolution = previousDynamicResolution;
                RenderTexture.active = previousActive;
                renderTexture.Release();
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        private static ScreenshotBytesResult FromEditorWindowCapture(
            EditorWindowPixelCapture pixels,
            string source)
        {
            return new ScreenshotBytesResult
            {
                Bytes = Convert.FromBase64String(pixels.imageData),
                Width = pixels.width,
                Height = pixels.height,
                Source = source,
                Degraded = pixels.degraded,
                DegradeReason = pixels.degradeReason,
                CaptureApi = pixels.captureApi,
                WindowHandle = pixels.windowHandle,
                UnityProcessId = pixels.unityProcessId,
                Foreground = pixels.foreground,
                OcclusionSensitive = pixels.occlusionSensitive,
                PixelSourceVerified = pixels.pixelSourceVerified,
                RepaintRequestedAtUtcMs = pixels.repaintRequestedAtUtcMs,
                CapturedAtUtcMs = pixels.capturedAtUtcMs,
            };
        }

        private static ScreenshotBytesResult FromCameraRender(
            CameraRenderCapture rendered,
            string source)
        {
            return new ScreenshotBytesResult
            {
                Bytes = rendered.Bytes,
                Width = rendered.Width,
                Height = rendered.Height,
                Source = source,
                CaptureApi = "Camera.Render(RenderTextureDescriptor)",
                RequestedWidth = rendered.RequestedWidth,
                RequestedHeight = rendered.RequestedHeight,
                ActualColorWidth = rendered.ColorWidth,
                ActualColorHeight = rendered.ColorHeight,
                ActualDepthWidth = rendered.DepthWidth,
                ActualDepthHeight = rendered.DepthHeight,
                RenderRetryCount = rendered.RetryCount,
            };
        }
    }
}
