// -----------------------------------------------------------------------
// UPilot Editor - https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CodingRiver.UPilot
{
    [Serializable]
    public sealed class SnapshotStartMessage { public SnapshotCapturePayload payload; }

    [Serializable]
    public sealed class SnapshotIdMessage { public SnapshotIdPayload payload; }

    [Serializable]
    public sealed class SnapshotIdPayload
    {
        public string snapshotId = string.Empty;
        public string detailLevel = "summary";
    }

    [Serializable]
    public sealed class SnapshotCapturePayload
    {
        public SnapshotTargetRequestPayload[] targets = Array.Empty<SnapshotTargetRequestPayload>();
        public string[] channels = { "color" };
        public string syncMode = "sameFrame";
        public string completionPolicy = "allOrNothing";
        public SnapshotCapturePolicyPayload capturePolicy = new SnapshotCapturePolicyPayload();
        public string outputDirectory = string.Empty;
        public string requestKey = string.Empty;
    }

    [Serializable]
    public sealed class SnapshotCapturePolicyPayload
    {
        public bool requireVerifiedPixels = true;
        public bool allowFallback;
        public bool allowOcclusionSensitive;
        public bool allowStaleFrame;
        public int maxStaleFrameAgeMs;
    }

    [Serializable]
    public sealed class SnapshotTargetRequestPayload
    {
        public string targetId = string.Empty;
        public string kind = "camera";
        public string instanceId = string.Empty;
        public string hierarchyPath = string.Empty;
        public string exactName = string.Empty;
        public string cameraName = string.Empty;
        public string fullTypeName = string.Empty;
        public string title = string.Empty;
        public int targetDisplay;
        public string[] channels = Array.Empty<string>();
        public bool depthPreview;
        public int width = 1280;
        public int height = 720;
    }

    [Serializable]
    public sealed class SnapshotCameraInfoPayload
    {
        public string instanceId;
        public string name;
        public string hierarchyPath;
        public string scenePath;
        public bool active;
        public bool enabled;
        public int targetDisplay;
        public float depth;
        public string renderPipeline;
        public string renderType;
    }

    [Serializable]
    public sealed class SnapshotCameraListPayload
    {
        public int count;
        public SnapshotCameraInfoPayload[] cameras = Array.Empty<SnapshotCameraInfoPayload>();
    }

    [Serializable]
    public sealed class SnapshotEnvironmentPayload
    {
        public string unityVersion;
        public string platform;
        public string graphicsApi;
        public string colorSpace;
        public string renderPipeline;
        public string renderPipelineVersion;
        public string editorWindowState = "unknown";
        public bool editorMinimized;
    }

    [Serializable]
    public sealed class SnapshotFramePayload
    {
        public int frameCount;
        public float fixedTime;
        public string playModeState;
        public long capturedAtUtcMs;
    }

    [Serializable]
    public sealed class SnapshotCameraEvidencePayload
    {
        public string instanceId;
        public string name;
        public string hierarchyPath;
        public string scenePath;
        public float nearClip;
        public float farClip;
        public bool orthographic;
        public float fieldOfView;
        public float orthographicSize;
        public float aspect;
        public int targetDisplay;
        public bool usesReversedZBuffer;
        public float[] viewMatrix = Array.Empty<float>();
        public float[] projectionMatrix = Array.Empty<float>();
        public float[] cameraToWorldMatrix = Array.Empty<float>();
    }

    [Serializable]
    public sealed class SnapshotEditorWindowEvidencePayload
    {
        public string instanceId;
        public string title;
        public string fullTypeName;
        public bool focused;
        public float x;
        public float y;
        public float width;
        public float height;
    }

    [Serializable]
    public sealed class SnapshotGameViewEvidencePayload
    {
        public int targetDisplay;
        public int screenWidth;
        public int screenHeight;
        public bool playMode;
        public bool runInBackgroundForced;
    }

    [Serializable]
    public sealed class SnapshotProvenancePayload
    {
        public string captureApi;
        public bool pixelSourceVerified;
        public bool occlusionSensitive;
        public bool degraded;
        public string degradeReason;
        public string originalError;
        public string[] fallbackChain = Array.Empty<string>();
        public string matchedFullTypeName;
        public string matchedInstanceId;
        public long windowHandle;
        public int unityProcessId;
        public bool foreground;
        public long repaintRequestedAtUtcMs;
        public long repaintObservedAtUtcMs;
        public long repaintSequence;
        public bool includesSceneGui;
        public bool includesHandles;
    }

    [Serializable]
    public sealed class SnapshotNumericStatisticsPayload
    {
        public long pixelCount;
        public long validPixelCount;
        public double validPixelRatio;
        public float min;
        public float max;
        public double mean;
    }

    [Serializable]
    public sealed class SnapshotArtifactPayload
    {
        public string artifactId;
        public string targetId;
        public string role;
        public string path;
        public string mimeType;
        public string encoding;
        public string units;
        public long bytes;
        public int width;
        public int height;
        public string sha256;
        public bool acceptedAsEvidence;
        public SnapshotNumericStatisticsPayload statistics;
    }

    [Serializable]
    public sealed class SnapshotFailurePayload
    {
        public EditorWindowPixelCapture captureDiagnostics;
        public string targetId;
        public string code;
        public string message;
        public string stage;
    }

    [Serializable]
    public sealed class SnapshotTargetResultPayload
    {
        public string targetId;
        public string kind;
        public string status;
        public bool success;
        public SnapshotTargetRequestPayload requested;
        public SnapshotCameraEvidencePayload camera;
        public SnapshotEditorWindowEvidencePayload editorWindow;
        public SnapshotGameViewEvidencePayload gameView;
        public SnapshotProvenancePayload provenance;
        public string[] artifactIds = Array.Empty<string>();
    }

    [Serializable]
    public sealed class SnapshotJobPayload
    {
        public int snapshotSchemaVersion = 1;
        public string snapshotId;
        public string status;
        public bool terminal;
        public bool success;
        public string requestHash;
        public string requestKey;
        public string phase;
        public string completionPolicy;
        public long startedAtUtcMs;
        public long updatedAtUtcMs;
        public long endedAtUtcMs;
        public bool cancelRequested;
        public SnapshotCapturePayload request;
        public SnapshotEnvironmentPayload environment;
        public SnapshotFramePayload frame;
        public List<SnapshotTargetResultPayload> targets = new List<SnapshotTargetResultPayload>();
        public List<SnapshotArtifactPayload> artifacts = new List<SnapshotArtifactPayload>();
        public List<SnapshotFailurePayload> failures = new List<SnapshotFailurePayload>();
        public string outputDirectory;
        public string manifestPath;
        public string manifestSha256;
    }

    /// <summary>
    /// Versioned, artifact-first Snapshot jobs. The first implementation keeps
    /// camera captures synchronous on one Editor frame while exposing an async
    /// job lifecycle to the MCP client.
    /// </summary>
    public sealed class UPilotSnapshotService
    {
        private const int MaxTargets = 16;
        private const long MaxPixels = 64L * 1024L * 1024L;
        private const int MaxRecoveredJobs = 512;
        private const string SnapshotStateDirectory = "Library/UPilot/SnapshotJobs";
        private readonly UPilotBridge _bridge;
        private readonly object _gate = new object();
        private readonly Dictionary<string, SnapshotJobPayload> _jobs =
            new Dictionary<string, SnapshotJobPayload>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _requestKeys =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _scheduledJobs =
            new HashSet<string>(StringComparer.Ordinal);

        public UPilotSnapshotService(UPilotBridge bridge)
        {
            _bridge = bridge;
            LoadPersistedJobs();
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register(new CommandDescriptor("snapshot.cameraList", "snapshot"), HandleCameraListAsync);
            _bridge.Router.Register(new CommandDescriptor("snapshot.start", "snapshot", idempotent: false), HandleStartAsync);
            _bridge.Router.Register(new CommandDescriptor("snapshot.status", "snapshot"), HandleStatusAsync);
            _bridge.Router.Register(new CommandDescriptor("snapshot.cancel", "snapshot", idempotent: false), HandleCancelAsync);
            _bridge.Router.Register(new CommandDescriptor("snapshot.collect", "snapshot"), HandleCollectAsync);
        }

        private async Task HandleCameraListAsync(string id, string json, CancellationToken token)
        {
            var completion = new TaskCompletionSource<SnapshotCameraListPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var cameras = FindSceneCameras()
                        .Select(BuildCameraInfo)
                        .OrderBy(value => value.scenePath, StringComparer.Ordinal)
                        .ThenBy(value => value.hierarchyPath, StringComparer.Ordinal)
                        .ThenBy(value => value.instanceId, StringComparer.Ordinal)
                        .ToArray();
                    completion.TrySetResult(new SnapshotCameraListPayload { count = cameras.Length, cameras = cameras });
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            });

            try { await _bridge.SendResultAsync(id, "snapshot.cameraList", await completion.Task, token); }
            catch (Exception ex) { await _bridge.SendErrorAsync(id, "SNAPSHOT_CAMERA_LIST_FAILED", ex.Message, token, "snapshot.cameraList"); }
        }

        private async Task HandleStartAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<SnapshotStartMessage>(json);
            var request = message != null && message.payload != null ? message.payload : new SnapshotCapturePayload();
            var accepted = new TaskCompletionSource<SnapshotJobPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try { accepted.TrySetResult(CreateAndSchedule(request)); }
                catch (Exception ex) { accepted.TrySetException(ex); }
            });

            try { await _bridge.SendResultAsync(id, "snapshot.start", await accepted.Task, token); }
            catch (SnapshotRequestException ex) { await _bridge.SendErrorAsync(id, ex.Code, ex.Message, token, "snapshot.start"); }
            catch (Exception ex) { await _bridge.SendErrorAsync(id, "SNAPSHOT_START_FAILED", ex.Message, token, "snapshot.start"); }
        }

        private async Task HandleStatusAsync(string id, string json, CancellationToken token)
        {
            var request = JsonUtility.FromJson<SnapshotIdMessage>(json)?.payload;
            var job = FindJob(request?.snapshotId);
            if (job == null)
            {
                await _bridge.SendErrorAsync(id, "SNAPSHOT_NOT_FOUND", $"Snapshot not found: {request?.snapshotId}", token, "snapshot.status");
                return;
            }
            await _bridge.SendResultAsync(id, "snapshot.status", job, token);
        }

        private async Task HandleCancelAsync(string id, string json, CancellationToken token)
        {
            var request = JsonUtility.FromJson<SnapshotIdMessage>(json)?.payload;
            var job = FindJob(request?.snapshotId);
            if (job == null)
            {
                await _bridge.SendErrorAsync(id, "SNAPSHOT_NOT_FOUND", $"Snapshot not found: {request?.snapshotId}", token, "snapshot.cancel");
                return;
            }
            lock (_gate)
            {
                if (!job.terminal)
                {
                    job.cancelRequested = true;
                    job.updatedAtUtcMs = UtcNowMs();
                    PersistJob(job);
                }
            }
            await _bridge.SendResultAsync(id, "snapshot.cancel", job, token);
        }

        private async Task HandleCollectAsync(string id, string json, CancellationToken token)
        {
            var request = JsonUtility.FromJson<SnapshotIdMessage>(json)?.payload;
            var job = FindJob(request?.snapshotId);
            if (job == null)
            {
                await _bridge.SendErrorAsync(id, "SNAPSHOT_NOT_FOUND", $"Snapshot not found: {request?.snapshotId}", token, "snapshot.collect");
                return;
            }
            await _bridge.SendResultAsync(id, "snapshot.collect", job, token);
        }

        private SnapshotJobPayload CreateAndSchedule(SnapshotCapturePayload request)
        {
            var job = CreateJob(request);
            var shouldSchedule = false;
            lock (_gate)
            {
                shouldSchedule = !job.terminal
                    && string.Equals(job.status, "queued", StringComparison.Ordinal)
                    && _scheduledJobs.Add(job.snapshotId);
            }
            if (shouldSchedule)
            {
                EditorApplication.CallbackFunction scheduled = null;
                scheduled = () =>
                {
                    EditorApplication.update -= scheduled;
                    lock (_gate) _scheduledJobs.Remove(job.snapshotId);
                    ExecuteJob(job, request);
                };
                EditorApplication.update += scheduled;
            }
            return job;
        }

        private SnapshotJobPayload CreateJob(SnapshotCapturePayload request)
        {
            ValidateRequest(request);
            var requestHash = Sha256(Encoding.UTF8.GetBytes(JsonUtility.ToJson(request)));
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(request.requestKey) && _requestKeys.TryGetValue(request.requestKey, out var existingId))
                {
                    var existing = _jobs[existingId];
                    if (!string.Equals(existing.requestHash, requestHash, StringComparison.Ordinal))
                        throw new SnapshotRequestException("SNAPSHOT_REQUEST_KEY_CONFLICT", "requestKey is already bound to a different Snapshot request.");
                    return existing;
                }

                var snapshotId = "snapshot-" + Guid.NewGuid().ToString("N");
                var output = ResolveOutputDirectory(request.outputDirectory, snapshotId);
                var now = UtcNowMs();
                var job = new SnapshotJobPayload
                {
                    snapshotId = snapshotId,
                    status = "queued",
                    terminal = false,
                    success = false,
                    requestHash = requestHash,
                    requestKey = request.requestKey ?? string.Empty,
                    phase = "queued",
                    completionPolicy = request.completionPolicy,
                    request = request,
                    startedAtUtcMs = now,
                    updatedAtUtcMs = now,
                    environment = BuildEnvironment(),
                    frame = new SnapshotFramePayload(),
                    outputDirectory = ToProjectRelative(output),
                    manifestPath = ToProjectRelative(Path.Combine(output, "manifest.json")),
                };
                _jobs.Add(snapshotId, job);
                if (!string.IsNullOrWhiteSpace(request.requestKey)) _requestKeys.Add(request.requestKey, snapshotId);
                PersistJob(job);
                return job;
            }
        }

        private async void ExecuteJob(SnapshotJobPayload job, SnapshotCapturePayload request)
        {
            try
            {
                UpdateJob(job, "running", "resolving", false, false);
                var resolved = ResolveTargets(request);
                RecordResolutionFailures(job, resolved);
                var allOrNothing = string.Equals(
                    request.completionPolicy,
                    "allOrNothing",
                    StringComparison.OrdinalIgnoreCase);
                if (allOrNothing && resolved.failures.Count > 0)
                {
                    foreach (var item in resolved.targets)
                    {
                        job.targets.Add(new SnapshotTargetResultPayload
                        {
                            targetId = item.request.targetId,
                            kind = item.request.kind,
                            status = "skipped",
                            success = false,
                            requested = item.request,
                            artifactIds = Array.Empty<string>(),
                        });
                    }
                    RejectArtifacts(job);
                    FinishJob(job, "failed", false);
                    return;
                }

                if (job.cancelRequested)
                {
                    FinishJob(job, "cancelled", false);
                    return;
                }

                UpdateJob(job, "running", "rendering", false, false);
                var hasGameView = resolved.targets.Any(value => value.kind == "gameview");
                // A single SceneView must wait for a new Repaint. Its capture frame,
                // not its request frame, is the sameFrame baseline. Multi-target
                // requests retain the strict existing cross-target frame check.
                var singleSceneView = resolved.targets.Count == 1 && resolved.targets[0].kind == "sceneview";
                var deferredFrame = hasGameView || singleSceneView;
                var frame = deferredFrame ? -1 : Time.frameCount;
                job.frame.frameCount = frame;
                job.frame.fixedTime = deferredFrame ? 0f : Time.fixedTime;
                job.frame.playModeState = EditorApplication.isPlaying ? "play" : "edit";

                var captureTargets = resolved.targets
                    .OrderBy(value => value.kind == "gameview" ? 0 : 1)
                    .ToArray();
                foreach (var item in captureTargets)
                {
                    if (job.cancelRequested)
                    {
                        FinishJob(job, "cancelled", false);
                        return;
                    }
                    try
                    {
                        var captureTask = CaptureTargetAsync(job, item, request.channels, request.capturePolicy);
                        if (item.kind == "sceneview")
                            await new SceneViewEditorCompletion(captureTask);
                        else
                            await captureTask;
                        if (frame < 0 && (item.kind == "gameview" || singleSceneView))
                        {
                            frame = Time.frameCount;
                            job.frame.frameCount = frame;
                            job.frame.fixedTime = Time.fixedTime;
                        }
                    }
                    catch (Exception ex)
                    {
                        var code = ex is SnapshotRequestException sre
                            ? sre.Code
                            : "SNAPSHOT_TARGET_CAPTURE_FAILED";
                        RecordTargetFailure(job, item.request, code, ex.Message, "rendering");
                        if (ex is EditorWindowCaptureException captureError)
                            job.failures[job.failures.Count - 1].captureDiagnostics = captureError.Diagnostics;
                        if (allOrNothing)
                        {
                            RejectArtifacts(job);
                            FinishJob(job, "failed", false);
                            return;
                        }
                    }
                    if (frame >= 0 && Time.frameCount != frame)
                    {
                        job.failures.Add(new SnapshotFailurePayload
                        {
                            targetId = item.request.targetId,
                            code = "FRAME_ADVANCED_DURING_CAPTURE",
                            message = "Unity advanced to another frame during sameFrame capture.",
                            stage = "rendering",
                        });
                        RejectArtifacts(job);
                        FinishJob(job, "failed", false);
                        return;
                    }
                }

                job.frame.capturedAtUtcMs = UtcNowMs();
                var successfulTargets = job.targets.Count(value => value.success);
                if (successfulTargets == 0)
                {
                    RejectArtifacts(job);
                    FinishJob(job, "failed", false);
                }
                else if (job.failures.Count > 0)
                {
                    FinishJob(job, "partial", false);
                }
                else
                {
                    FinishJob(job, "completed", true);
                }
            }
            catch (Exception ex)
            {
                job.failures.Add(new SnapshotFailurePayload
                {
                    targetId = string.Empty,
                    code = ex is SnapshotRequestException sre ? sre.Code : "SNAPSHOT_EXECUTION_FAILED",
                    message = ex.Message,
                    stage = job.phase,
                });
                RejectArtifacts(job);
                FinishJob(job, "failed", false);
            }
        }

        private Task CaptureTargetAsync(
            SnapshotJobPayload job,
            ResolvedSnapshotTarget target,
            string[] defaultChannels,
            SnapshotCapturePolicyPayload capturePolicy)
        {
            switch (target.kind)
            {
                case "camera":
                    CaptureCameraTarget(job, target.request, target.camera, defaultChannels, capturePolicy);
                    return Task.CompletedTask;
                case "sceneview":
                    return CaptureSceneViewTargetAsync(job, target.request, target.sceneView, defaultChannels, capturePolicy);
                case "gameview":
                    return CaptureGameViewTargetAsync(job, target.request, defaultChannels, capturePolicy);
                case "editorwindow":
                    CaptureEditorWindowTarget(job, target.request, target.editorWindow, defaultChannels, capturePolicy);
                    return Task.CompletedTask;
                default:
                    throw new SnapshotRequestException("SNAPSHOT_TARGET_KIND_NOT_IMPLEMENTED", $"Target kind is not implemented: {target.kind}");
            }
        }

        private void CaptureCameraTarget(
            SnapshotJobPayload job,
            SnapshotTargetRequestPayload requested,
            Camera camera,
            string[] defaultChannels,
            SnapshotCapturePolicyPayload capturePolicy)
        {
            var channelValues = requested.channels != null && requested.channels.Length > 0
                ? requested.channels
                : defaultChannels;
            var channels = NormalizeChannels(channelValues);

            var width = ClampDimension(requested.width, 1280);
            var height = ClampDimension(requested.height, 720);
            var safeTargetId = SafeFileName(requested.targetId) + "." + ShortHash(requested.targetId);
            var artifactIds = new List<string>();
            var requiresDepth = channels.Contains("rawdepth") || channels.Contains("lineardepth");
            string captureApi;
            if (requiresDepth)
            {
                var capture = RenderCameraChannels(
                    camera,
                    width,
                    height,
                    channels.Contains("color"),
                    channels.Contains("rawdepth"),
                    channels.Contains("lineardepth"),
                    requested.depthPreview);
                captureApi = capture.CaptureApi;
                if (capture.ColorBytes != null)
                    artifactIds.Add(AddArtifact(job, requested.targetId, safeTargetId, "color", "color.png",
                        capture.ColorBytes, "image/png", "rgba8", "", width, height, null, true));
                if (capture.RawDepthBytes != null)
                    artifactIds.Add(AddArtifact(job, requested.targetId, safeTargetId, "rawDepth", "raw-depth.exr",
                        capture.RawDepthBytes, "image/x-exr", "r32f", "device-depth-0-1", width, height,
                        capture.RawDepthStatistics, true));
                if (capture.LinearDepthBytes != null)
                    artifactIds.Add(AddArtifact(job, requested.targetId, safeTargetId, "linearDepth", "linear-depth.exr",
                        capture.LinearDepthBytes, "image/x-exr", "r32f", "unity-world-unit", width, height,
                        capture.LinearDepthStatistics, true));
                if (capture.LinearDepthPreviewBytes != null)
                    artifactIds.Add(AddArtifact(job, requested.targetId, safeTargetId, "linearDepthPreview", "linear-depth-preview.png",
                        capture.LinearDepthPreviewBytes, "image/png", "l8-preview", "normalized-preview", width, height,
                        null, false));
            }
            else
            {
                var capture = UPilotScreenshotService.RenderCamera(camera, width, height, "png", 75);
                captureApi = "Camera.Render(RenderTextureDescriptor)";
                artifactIds.Add(AddArtifact(job, requested.targetId, safeTargetId, "color", "color.png",
                    capture.Bytes, "image/png", "rgba8", "", capture.Width, capture.Height, null, true));
            }

            var targetResult = new SnapshotTargetResultPayload
            {
                targetId = requested.targetId,
                kind = "camera",
                status = "completed",
                success = true,
                requested = requested,
                camera = BuildCameraEvidence(camera),
                provenance = new SnapshotProvenancePayload
                {
                    captureApi = captureApi,
                    pixelSourceVerified = true,
                    occlusionSensitive = false,
                    degraded = false,
                    matchedInstanceId = WireId(camera),
                    unityProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
                },
                artifactIds = artifactIds.ToArray(),
            };
            ValidateEvidencePolicy(targetResult.provenance, capturePolicy);
            job.targets.Add(targetResult);
            UpdateJob(job, "running", "writing", false, false);
        }

        // SceneView work must resume on the Editor thread even when Unity's current
        // SynchronizationContext is not pumping (for example after a Runner Reload).
        // A watchdog may complete the source Task on a worker; never run Unity APIs there.
        private sealed class SceneViewEditorCompletion : System.Runtime.CompilerServices.INotifyCompletion
        {
            private readonly Task task;
            private bool interrupted;

            public SceneViewEditorCompletion(Task task) { this.task = task; }
            public SceneViewEditorCompletion GetAwaiter() => this;
            public bool IsCompleted => task.IsCompleted;
            public void GetResult()
            {
                if (interrupted && !task.IsCompleted)
                    throw new InvalidOperationException("SCENEVIEW_DOMAIN_RELOAD");
                task.GetAwaiter().GetResult();
            }

            public void OnCompleted(Action continuation)
            {
                EditorApplication.CallbackFunction update = null;
                AssemblyReloadEvents.AssemblyReloadCallback reload = null;
                var resumed = false;
                Action resume = () =>
                {
                    if (resumed) return;
                    resumed = true;
                    EditorApplication.update -= update;
                    AssemblyReloadEvents.beforeAssemblyReload -= reload;
                    continuation();
                };
                update = () => { if (task.IsCompleted) resume(); };
                reload = () => { interrupted = true; resume(); };
                EditorApplication.update += update;
                AssemblyReloadEvents.beforeAssemblyReload += reload;
            }
        }

        private async Task CaptureSceneViewTargetAsync(
            SnapshotJobPayload job,
            SnapshotTargetRequestPayload requested,
            SceneView sceneView,
            string[] defaultChannels,
            SnapshotCapturePolicyPayload capturePolicy)
        {
            var channelValues = requested.channels != null && requested.channels.Length > 0
                ? requested.channels
                : defaultChannels;
            var channels = NormalizeChannels(channelValues);
            if (channels.Count != 1 || !channels.Contains("color"))
                throw new SnapshotRequestException("SNAPSHOT_SCENEVIEW_CHANNEL_UNSUPPORTED", "SceneView Snapshot v1 supports only the color channel.");

            if (UPilotWindowDiagnostics.TryGetUnityEditorMinimized(out var editorMinimized, out var editorHandle) && editorMinimized)
                throw new SnapshotRequestException("SNAPSHOT_EDITOR_MINIMIZED", "SceneView capture requires a non-minimized Unity Editor window.");
            if (UPilotWindowDiagnostics.TryGetEditorWindowMinimized(sceneView, out var windowMinimized, out var windowHandle) && windowMinimized)
                throw new SnapshotRequestException("SNAPSHOT_EDITOR_MINIMIZED", "The requested SceneView window is minimized.");

            var width = ClampDimension(requested.width, 1280);
            var height = ClampDimension(requested.height, 720);
            var completion = new TaskCompletionSource<UPilotScreenshotService.ScreenshotBytesResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            UPilotScreenshotService.CaptureSceneViewAfterRepaint(
                sceneView,
                width,
                height,
                "png",
                75,
                completion,
                allowCameraFallback: false);
            await new SceneViewEditorCompletion(completion.Task);
            var capture = completion.Task.GetAwaiter().GetResult();
            if (capture == null || capture.Bytes == null || capture.Bytes.Length == 0)
                throw new SnapshotRequestException("SNAPSHOT_SCENEVIEW_CAPTURE_UNAVAILABLE", "The exact SceneView did not produce verified EditorWindow pixels.");

            var provenance = new SnapshotProvenancePayload
            {
                captureApi = capture.CaptureApi,
                pixelSourceVerified = capture.PixelSourceVerified,
                occlusionSensitive = capture.OcclusionSensitive,
                degraded = capture.Degraded,
                degradeReason = capture.DegradeReason ?? string.Empty,
                matchedFullTypeName = capture.MatchedFullTypeName,
                matchedInstanceId = capture.MatchedInstanceId.ToString(CultureInfo.InvariantCulture),
                windowHandle = capture.WindowHandle != 0 ? capture.WindowHandle : (windowHandle != 0 ? windowHandle : editorHandle),
                unityProcessId = capture.UnityProcessId,
                foreground = capture.Foreground,
                repaintRequestedAtUtcMs = capture.RepaintRequestedAtUtcMs,
                repaintObservedAtUtcMs = capture.RepaintObservedAtUtcMs,
                repaintSequence = capture.RepaintSequence,
                includesSceneGui = capture.IncludesSceneGui,
                includesHandles = capture.IncludesHandles,
            };
            ValidateEvidencePolicy(provenance, capturePolicy);
            if (!provenance.includesSceneGui || !provenance.includesHandles)
                throw new SnapshotRequestException("SNAPSHOT_SCENEVIEW_OVERLAYS_MISSING", "SceneView evidence must include Scene GUI and Handles.");

            var rect = sceneView.position;
            var safeTargetId = SafeFileName(requested.targetId) + "." + ShortHash(requested.targetId);
            var artifactId = AddArtifact(
                job,
                requested.targetId,
                safeTargetId,
                "color",
                "color.png",
                capture.Bytes,
                "image/png",
                "rgba8",
                string.Empty,
                capture.Width,
                capture.Height,
                null,
                true);
            job.targets.Add(new SnapshotTargetResultPayload
            {
                targetId = requested.targetId,
                kind = "sceneView",
                status = "completed",
                success = true,
                requested = requested,
                editorWindow = new SnapshotEditorWindowEvidencePayload
                {
                    instanceId = WireId(sceneView),
                    title = sceneView.titleContent?.text ?? string.Empty,
                    fullTypeName = sceneView.GetType().FullName,
                    focused = EditorWindow.focusedWindow == sceneView,
                    x = rect.x,
                    y = rect.y,
                    width = rect.width,
                    height = rect.height,
                },
                provenance = provenance,
                artifactIds = new[] { artifactId },
            });
            UpdateJob(job, "running", "writing", false, false);
        }

        private async Task CaptureGameViewTargetAsync(
            SnapshotJobPayload job,
            SnapshotTargetRequestPayload requested,
            string[] defaultChannels,
            SnapshotCapturePolicyPayload capturePolicy)
        {
            if (!EditorApplication.isPlaying)
                throw new SnapshotRequestException("SNAPSHOT_GAMEVIEW_REQUIRES_PLAYMODE", "True GameView composite capture is available only in PlayMode.");
            if (requested.targetDisplay != 0)
                throw new SnapshotRequestException("SNAPSHOT_GAMEVIEW_DISPLAY_UNSUPPORTED", "GameView Snapshot v1 supports only Display 0.");
            var channelValues = requested.channels != null && requested.channels.Length > 0
                ? requested.channels
                : defaultChannels;
            var channels = NormalizeChannels(channelValues);
            if (channels.Count != 1 || !channels.Contains("color"))
                throw new SnapshotRequestException("SNAPSHOT_GAMEVIEW_CHANNEL_UNSUPPORTED", "True GameView Snapshot v1 supports only the color channel.");

            var width = ClampDimension(requested.width, Screen.width > 0 ? Screen.width : 1280);
            var height = ClampDimension(requested.height, Screen.height > 0 ? Screen.height : 720);
            var capture = await GameViewCaptureRunner.CaptureAsync(width, height, 10.0);
            if (capture == null || capture.bytes == null || capture.bytes.Length == 0)
                throw new SnapshotRequestException("SNAPSHOT_GAMEVIEW_CAPTURE_FAILED", "ScreenCapture did not return a GameView composite.");

            var provenance = new SnapshotProvenancePayload
            {
                captureApi = "WaitForEndOfFrame+ScreenCapture.CaptureScreenshotIntoRenderTexture",
                pixelSourceVerified = true,
                occlusionSensitive = false,
                degraded = false,
                matchedFullTypeName = "UnityEditor.GameView(Display 0 final composite)",
                matchedInstanceId = "display:0",
                unityProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
            };
            ValidateEvidencePolicy(provenance, capturePolicy);
            var safeTargetId = SafeFileName(requested.targetId) + "." + ShortHash(requested.targetId);
            var artifactId = AddArtifact(
                job,
                requested.targetId,
                safeTargetId,
                "color",
                "color.png",
                capture.bytes,
                "image/png",
                "rgba8",
                string.Empty,
                capture.width,
                capture.height,
                null,
                true);
            job.targets.Add(new SnapshotTargetResultPayload
            {
                targetId = requested.targetId,
                kind = "gameView",
                status = "completed",
                success = true,
                requested = requested,
                gameView = new SnapshotGameViewEvidencePayload
                {
                    targetDisplay = 0,
                    screenWidth = Screen.width,
                    screenHeight = Screen.height,
                    playMode = true,
                    runInBackgroundForced = capture.runInBackgroundForced,
                },
                provenance = provenance,
                artifactIds = new[] { artifactId },
            });
            UpdateJob(job, "running", "writing", false, false);
        }

        private void CaptureEditorWindowTarget(
            SnapshotJobPayload job,
            SnapshotTargetRequestPayload requested,
            EditorWindow window,
            string[] defaultChannels,
            SnapshotCapturePolicyPayload capturePolicy)
        {
            var channelValues = requested.channels != null && requested.channels.Length > 0
                ? requested.channels
                : defaultChannels;
            var channels = NormalizeChannels(channelValues);
            if (channels.Count != 1 || !channels.Contains("color"))
                throw new SnapshotRequestException("SNAPSHOT_EDITORWINDOW_CHANNEL_UNSUPPORTED", "EditorWindow Snapshot v1 supports only the color channel.");
            if (UPilotWindowDiagnostics.TryGetUnityEditorMinimized(out var editorMinimized, out var editorHandle) && editorMinimized)
                throw new SnapshotRequestException("SNAPSHOT_EDITOR_MINIMIZED", "EditorWindow capture requires a non-minimized Unity Editor window.");
            if (UPilotWindowDiagnostics.TryGetEditorWindowMinimized(window, out var windowMinimized, out var windowHandle) && windowMinimized)
                throw new SnapshotRequestException("SNAPSHOT_EDITOR_MINIMIZED", "The requested EditorWindow is minimized.");

            var capture = UPilotWindowDiagnostics.CaptureEditorWindowPixels(window, capturePolicy.allowFallback);
            if (capture == null || string.IsNullOrWhiteSpace(capture.imageData))
                throw new SnapshotRequestException("SNAPSHOT_EDITORWINDOW_CAPTURE_UNAVAILABLE", "The exact EditorWindow did not produce pixels.");
            var bytes = Convert.FromBase64String(capture.imageData);
            var provenance = new SnapshotProvenancePayload
            {
                captureApi = capture.captureApi,
                originalError = capture.originalError,
                pixelSourceVerified = capture.pixelSourceVerified,
                occlusionSensitive = capture.occlusionSensitive,
                degraded = capture.degraded,
                degradeReason = capture.degradeReason ?? string.Empty,
                matchedFullTypeName = window.GetType().FullName,
                matchedInstanceId = WireId(window),
                windowHandle = capture.windowHandle != 0 ? capture.windowHandle : (windowHandle != 0 ? windowHandle : editorHandle),
                unityProcessId = capture.unityProcessId,
                foreground = capture.foreground,
                repaintRequestedAtUtcMs = capture.repaintRequestedAtUtcMs,
            };
            ValidateEvidencePolicy(provenance, capturePolicy);
            var rect = window.position;
            var safeTargetId = SafeFileName(requested.targetId) + "." + ShortHash(requested.targetId);
            var artifactId = AddArtifact(
                job,
                requested.targetId,
                safeTargetId,
                "color",
                "color.png",
                bytes,
                "image/png",
                "rgba8",
                string.Empty,
                capture.width,
                capture.height,
                null,
                true);
            job.targets.Add(new SnapshotTargetResultPayload
            {
                targetId = requested.targetId,
                kind = "editorWindow",
                status = "completed",
                success = true,
                requested = requested,
                editorWindow = new SnapshotEditorWindowEvidencePayload
                {
                    instanceId = WireId(window),
                    title = window.titleContent?.text ?? string.Empty,
                    fullTypeName = window.GetType().FullName,
                    focused = EditorWindow.focusedWindow == window,
                    x = rect.x,
                    y = rect.y,
                    width = rect.width,
                    height = rect.height,
                },
                provenance = provenance,
                artifactIds = new[] { artifactId },
            });
            UpdateJob(job, "running", "writing", false, false);
        }

        private static string AddArtifact(
            SnapshotJobPayload job,
            string targetId,
            string safeTargetId,
            string role,
            string suffix,
            byte[] bytes,
            string mimeType,
            string encoding,
            string units,
            int width,
            int height,
            SnapshotNumericStatisticsPayload statistics,
            bool acceptedAsEvidence)
        {
            var artifactId = "artifact-" + Guid.NewGuid().ToString("N");
            var targetPath = Path.Combine(ProjectAbsolute(job.outputDirectory), safeTargetId + "." + suffix);
            WriteAtomic(targetPath, bytes);
            job.artifacts.Add(new SnapshotArtifactPayload
            {
                artifactId = artifactId,
                targetId = targetId,
                role = role,
                path = ToProjectRelative(targetPath),
                mimeType = mimeType,
                encoding = encoding,
                units = units,
                bytes = bytes.LongLength,
                width = width,
                height = height,
                sha256 = Sha256(bytes),
                acceptedAsEvidence = acceptedAsEvidence,
                statistics = statistics,
            });
            return artifactId;
        }

        private static HashSet<string> NormalizeChannels(string[] values)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in values == null || values.Length == 0 ? new[] { "color" } : values)
            {
                var value = (raw ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
                if (value != "color" && value != "rawdepth" && value != "lineardepth")
                    throw new SnapshotRequestException("SNAPSHOT_CHANNEL_UNSUPPORTED", $"Unsupported Snapshot channel: {raw}");
                result.Add(value);
            }
            if (result.Count == 0) result.Add("color");
            return result;
        }

        private static SnapshotCameraChannelCapture RenderCameraChannels(
            Camera camera,
            int width,
            int height,
            bool includeColor,
            bool includeRawDepth,
            bool includeLinearDepth,
            bool includeDepthPreview)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            var color = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            {
                name = "UPilot Snapshot Color",
                hideFlags = HideFlags.HideAndDontSave,
                useMipMap = false,
                autoGenerateMips = false,
                useDynamicScale = false,
            };
            var rawDepth = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
            {
                name = "UPilot Snapshot Raw Depth",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
            };
            var linearDepth = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
            {
                name = "UPilot Snapshot Linear Depth",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
            };
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var previousDynamicResolution = camera.allowDynamicResolution;
            var previousDepthTextureMode = camera.depthTextureMode;
            var needsDepth = includeRawDepth || includeLinearDepth || includeDepthPreview;
            var urpDepthRequirement = needsDepth ? EnableUrpDepthTexture(camera) : null;
            var depthCopied = false;
            Camera.CameraCallback postRender = null;
            Material material = null;
            try
            {
                color.Create();
                rawDepth.Create();
                linearDepth.Create();
                if (!color.IsCreated() || !rawDepth.IsCreated() || !linearDepth.IsCreated())
                    throw new InvalidOperationException("Failed to create one or more Snapshot color/depth render targets.");
                if (color.width != width || color.height != height)
                    throw new InvalidOperationException("Snapshot color and depth surfaces do not have the requested dimensions.");

                if (needsDepth)
                {
                    var shader = Shader.Find("Hidden/UPilot/SnapshotDepth");
                    if (shader == null || !shader.isSupported)
                        throw new SnapshotRequestException("SNAPSHOT_DEPTH_SHADER_UNAVAILABLE", "Hidden/UPilot/SnapshotDepth is missing or unsupported.");
                    material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                }

                camera.allowDynamicResolution = false;
                if (needsDepth) camera.depthTextureMode |= DepthTextureMode.Depth;
                camera.targetTexture = color;

                var pipeline = GraphicsSettings.currentRenderPipeline;
                var captureApi = "Camera.Render(targetTexture)";
                if (!needsDepth)
                {
                    camera.Render();
                }
                else if (pipeline == null)
                {
                    postRender = renderedCamera =>
                    {
                        if (renderedCamera != camera || depthCopied) return;
                        var cameraDepth = Shader.GetGlobalTexture("_CameraDepthTexture");
                        if (cameraDepth == null) return;
                        material.SetTexture("_UPilotDepthTexture", cameraDepth);
                        Graphics.Blit(Texture2D.blackTexture, rawDepth, material, 0);
                        Graphics.Blit(Texture2D.blackTexture, linearDepth, material, 1);
                        depthCopied = true;
                    };
                    Camera.onPostRender += postRender;
                    camera.Render();
                    captureApi = "Camera.Render(targetTexture)+BuiltIn.onPostRenderDepth";
                }
                else if (IsUrpPipeline(pipeline))
                {
                    depthCopied = RenderUrpDepthAndColor(camera, rawDepth, linearDepth, material);
                    captureApi = "Camera.Render(targetTexture)+URP.ScriptableRenderPass.Depth";
                }
                else
                {
                    throw new SnapshotRequestException(
                        "SNAPSHOT_DEPTH_PIPELINE_UNSUPPORTED",
                        $"Depth Snapshot v1 supports Built-in and URP; active pipeline is {pipeline.GetType().FullName}.");
                }

                var result = new SnapshotCameraChannelCapture
                {
                    Width = width,
                    Height = height,
                    CaptureApi = captureApi,
                };
                if (includeColor) result.ColorBytes = ReadColorPng(color, width, height);

                if (needsDepth && !depthCopied)
                {
                    throw new SnapshotRequestException(
                        "SNAPSHOT_DEPTH_CAPTURE_FAILED",
                        "The active render pipeline did not expose a verified Camera depth surface during rendering.");
                }
                if (needsDepth)
                {
                    var rawReadback = ReadDepthExr(rawDepth, width, height, null);
                    var linearReadback = ReadDepthExr(linearDepth, width, height, rawReadback.Values);
                    if (includeRawDepth)
                    {
                        result.RawDepthBytes = rawReadback.Bytes;
                        result.RawDepthStatistics = rawReadback.Statistics;
                    }
                    if (includeLinearDepth)
                    {
                        result.LinearDepthBytes = linearReadback.Bytes;
                        result.LinearDepthStatistics = linearReadback.Statistics;
                    }
                    if (includeDepthPreview)
                        result.LinearDepthPreviewBytes = BuildLinearDepthPreview(
                            rawReadback.Values,
                            linearReadback.Values,
                            width,
                            height,
                            camera.nearClipPlane,
                            camera.farClipPlane);
                }
                return result;
            }
            finally
            {
                if (postRender != null) Camera.onPostRender -= postRender;
                camera.targetTexture = previousTarget;
                camera.allowDynamicResolution = previousDynamicResolution;
                camera.depthTextureMode = previousDepthTextureMode;
                RestoreUrpDepthTexture(urpDepthRequirement);
                RenderTexture.active = previousActive;
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                ReleaseAndDestroy(color);
                ReleaseAndDestroy(rawDepth);
                ReleaseAndDestroy(linearDepth);
            }
        }

        private static byte[] ReadColorPng(RenderTexture source, int width, int height)
        {
            var previous = RenderTexture.active;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = source;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply(false, false);
                return texture.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static SnapshotDepthReadback ReadDepthExr(
            RenderTexture source,
            int width,
            int height,
            float[] rawValidityValues)
        {
            var previous = RenderTexture.active;
            var texture = new Texture2D(width, height, TextureFormat.RFloat, false, true);
            try
            {
                RenderTexture.active = source;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply(false, false);
                var nativeValues = texture.GetRawTextureData<float>();
                var values = new float[nativeValues.Length];
                for (var index = 0; index < nativeValues.Length; index++) values[index] = nativeValues[index];
                return new SnapshotDepthReadback
                {
                    Bytes = texture.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat),
                    Values = values,
                    Statistics = BuildDepthStatistics(values, rawValidityValues ?? values),
                };
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static SnapshotNumericStatisticsPayload BuildDepthStatistics(float[] values, float[] rawValues)
        {
            var clearDepth = SystemInfo.usesReversedZBuffer ? 0f : 1f;
            var count = Math.Min(values?.Length ?? 0, rawValues?.Length ?? 0);
            long valid = 0;
            double sum = 0;
            var min = float.PositiveInfinity;
            var max = float.NegativeInfinity;
            for (var index = 0; index < count; index++)
            {
                var raw = rawValues[index];
                var value = values[index];
                if (float.IsNaN(raw) || float.IsInfinity(raw) || Math.Abs(raw - clearDepth) <= 0.000001f) continue;
                if (float.IsNaN(value) || float.IsInfinity(value)) continue;
                valid++;
                sum += value;
                if (value < min) min = value;
                if (value > max) max = value;
            }
            return new SnapshotNumericStatisticsPayload
            {
                pixelCount = count,
                validPixelCount = valid,
                validPixelRatio = count == 0 ? 0 : valid / (double)count,
                min = valid == 0 ? 0 : min,
                max = valid == 0 ? 0 : max,
                mean = valid == 0 ? 0 : sum / valid,
            };
        }

        private static byte[] BuildLinearDepthPreview(
            float[] rawValues,
            float[] linearValues,
            int width,
            int height,
            float nearClip,
            float farClip)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            try
            {
                var pixels = new Color32[width * height];
                var clearDepth = SystemInfo.usesReversedZBuffer ? 0f : 1f;
                var range = Math.Max(0.000001f, farClip - nearClip);
                for (var index = 0; index < pixels.Length; index++)
                {
                    var raw = rawValues[index];
                    var linear = linearValues[index];
                    if (float.IsNaN(raw) || float.IsInfinity(raw) || Math.Abs(raw - clearDepth) <= 0.000001f)
                    {
                        pixels[index] = new Color32(0, 0, 0, 255);
                        continue;
                    }
                    var normalized = Mathf.Clamp01((linear - nearClip) / range);
                    var value = (byte)Mathf.RoundToInt((1f - normalized) * 255f);
                    pixels[index] = new Color32(value, value, value, 255);
                }
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                return texture.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static void ReleaseAndDestroy(RenderTexture texture)
        {
            if (texture == null) return;
            if (texture.IsCreated()) texture.Release();
            UnityEngine.Object.DestroyImmediate(texture);
        }

        private static SnapshotUrpDepthRequirement EnableUrpDepthTexture(Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline == null) return null;
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(value => value.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData", false))
                .FirstOrDefault(value => value != null);
            if (type == null) return null;
            var component = camera.GetComponent(type);
            var added = false;
            if (component == null)
            {
                component = camera.gameObject.AddComponent(type);
                added = component != null;
            }
            var property = type.GetProperty("requiresDepthTexture", BindingFlags.Instance | BindingFlags.Public);
            if (component == null || property == null || !property.CanRead || !property.CanWrite)
            {
                if (added && component != null) UnityEngine.Object.DestroyImmediate(component);
                return null;
            }
            var previous = property.GetValue(component, null);
            property.SetValue(component, true, null);
            return new SnapshotUrpDepthRequirement
            {
                component = component,
                property = property,
                previousValue = previous,
                addedComponent = added,
            };
        }

        private static void RestoreUrpDepthTexture(SnapshotUrpDepthRequirement state)
        {
            if (state == null || state.component == null) return;
            try
            {
                if (!state.addedComponent && state.property != null && state.property.CanWrite)
                    state.property.SetValue(state.component, state.previousValue, null);
            }
            finally
            {
                if (state.addedComponent && state.component != null)
                    UnityEngine.Object.DestroyImmediate(state.component);
            }
        }

        private static bool IsUrpPipeline(RenderPipelineAsset pipeline)
        {
            if (pipeline == null) return false;
            var type = pipeline.GetType();
            return string.Equals(
                type.FullName,
                "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset",
                StringComparison.Ordinal);
        }

        private static bool RenderUrpDepthAndColor(
            Camera camera,
            RenderTexture rawDepth,
            RenderTexture linearDepth,
            Material depthMaterial)
        {
            var providerType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(value => value.GetType("CodingRiver.UPilot.UPilotSnapshotUrpDepthCapture", false))
                .FirstOrDefault(value => value != null);
            var method = providerType?.GetMethod(
                "RenderDepthAndColor",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(Camera), typeof(RenderTexture), typeof(RenderTexture), typeof(Material) },
                null);
            if (method == null)
            {
                throw new SnapshotRequestException(
                    "SNAPSHOT_URP_PROVIDER_UNAVAILABLE",
                    "The optional UPilot Snapshot URP provider is not compiled or could not be discovered.");
            }

            try
            {
                return method.Invoke(null, new object[] { camera, rawDepth, linearDepth, depthMaterial }) is bool recorded && recorded;
            }
            catch (TargetInvocationException ex)
            {
                throw new SnapshotRequestException(
                    "SNAPSHOT_URP_DEPTH_CAPTURE_FAILED",
                    ex.InnerException?.Message ?? ex.Message);
            }
        }

        private ResolvedTargets ResolveTargets(SnapshotCapturePayload request)
        {
            var result = new ResolvedTargets();
            var seenTargetIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in request.targets)
            {
                var target = raw ?? new SnapshotTargetRequestPayload();
                if (string.IsNullOrWhiteSpace(target.targetId)) target.targetId = "target-" + (result.targets.Count + 1).ToString(CultureInfo.InvariantCulture);
                if (!seenTargetIds.Add(target.targetId))
                {
                    result.failures.Add(new ResolvedFailure
                    {
                        request = target,
                        failure = Failure(target.targetId, "DUPLICATE_TARGET_ID", "targetId must be unique.", "resolving"),
                    });
                    continue;
                }
                var kind = (target.kind ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
                try
                {
                    switch (kind)
                    {
                        case "camera":
                            result.targets.Add(new ResolvedSnapshotTarget
                            {
                                request = target,
                                kind = kind,
                                camera = ResolveCamera(target),
                            });
                            break;
                        case "sceneview":
                            result.targets.Add(new ResolvedSnapshotTarget
                            {
                                request = target,
                                kind = kind,
                                sceneView = ResolveSceneView(target),
                            });
                            break;
                        case "gameview":
                            ValidateGameViewSelector(target);
                            result.targets.Add(new ResolvedSnapshotTarget { request = target, kind = kind });
                            break;
                        case "editorwindow":
                            result.targets.Add(new ResolvedSnapshotTarget
                            {
                                request = target,
                                kind = kind,
                                editorWindow = ResolveEditorWindow(target),
                            });
                            break;
                        default:
                            throw new SnapshotRequestException(
                                "SNAPSHOT_TARGET_KIND_UNSUPPORTED",
                                $"Unsupported Snapshot target kind: {target.kind}");
                    }
                }
                catch (SnapshotRequestException ex)
                {
                    result.failures.Add(new ResolvedFailure
                    {
                        request = target,
                        failure = Failure(target.targetId, ex.Code, ex.Message, "resolving"),
                    });
                }
            }
            return result;
        }

        private static SceneView ResolveSceneView(SnapshotTargetRequestPayload target)
        {
            if (string.IsNullOrWhiteSpace(target.instanceId))
                throw new SnapshotRequestException("SCENEVIEW_INSTANCE_ID_REQUIRED", "SceneView targets require the exact Unity instanceId returned by unity_editor_windows_list.");
            if (!ulong.TryParse(target.instanceId, NumberStyles.None, CultureInfo.InvariantCulture, out var wireId))
                throw new SnapshotRequestException("SCENEVIEW_INSTANCE_ID_INVALID", "SceneView instanceId must be an unsigned decimal string.");
            var sceneView = UPilotEntityIds.ObjectFromWireId(wireId) as SceneView;
            if (sceneView == null)
                throw new SnapshotRequestException("SCENEVIEW_NOT_FOUND", $"SceneView not found for instanceId={target.instanceId}.");
            return sceneView;
        }

        private static EditorWindow ResolveEditorWindow(SnapshotTargetRequestPayload target)
        {
            if (string.IsNullOrWhiteSpace(target.instanceId))
                throw new SnapshotRequestException("EDITORWINDOW_INSTANCE_ID_REQUIRED", "EditorWindow targets require the exact Unity instanceId returned by unity_editor_windows_list.");
            if (!ulong.TryParse(target.instanceId, NumberStyles.None, CultureInfo.InvariantCulture, out var wireId))
                throw new SnapshotRequestException("EDITORWINDOW_INSTANCE_ID_INVALID", "EditorWindow instanceId must be an unsigned decimal string.");
            var window = UPilotEntityIds.ObjectFromWireId(wireId) as EditorWindow;
            if (window == null)
                throw new SnapshotRequestException("EDITORWINDOW_NOT_FOUND", $"EditorWindow not found for instanceId={target.instanceId}.");
            if (window is SceneView)
                throw new SnapshotRequestException("EDITORWINDOW_KIND_MISMATCH", "Use kind=sceneView for UnityEditor.SceneView so repaint and Handles evidence can be verified.");
            return window;
        }

        private static void ValidateGameViewSelector(SnapshotTargetRequestPayload target)
        {
            if (!string.IsNullOrWhiteSpace(target.instanceId) ||
                !string.IsNullOrWhiteSpace(target.hierarchyPath) ||
                !string.IsNullOrWhiteSpace(target.exactName) ||
                !string.IsNullOrWhiteSpace(target.cameraName))
            {
                throw new SnapshotRequestException(
                    "GAMEVIEW_SELECTOR_INVALID",
                    "GameView targets identify the Display 0 final composite and do not accept Camera or window selectors.");
            }
            if (target.targetDisplay != 0)
                throw new SnapshotRequestException("SNAPSHOT_GAMEVIEW_DISPLAY_UNSUPPORTED", "GameView Snapshot v1 supports only Display 0.");
        }

        private static Camera ResolveCamera(SnapshotTargetRequestPayload target)
        {
            var selectorCount = 0;
            if (!string.IsNullOrWhiteSpace(target.instanceId)) selectorCount++;
            if (!string.IsNullOrWhiteSpace(target.hierarchyPath)) selectorCount++;
            var exactName = !string.IsNullOrWhiteSpace(target.exactName) ? target.exactName : target.cameraName;
            if (!string.IsNullOrWhiteSpace(exactName)) selectorCount++;
            if (selectorCount != 1)
                throw new SnapshotRequestException("CAMERA_SELECTOR_INVALID", "Camera target must provide exactly one of instanceId, hierarchyPath, or exactName.");

            if (!string.IsNullOrWhiteSpace(target.instanceId))
            {
                if (!ulong.TryParse(target.instanceId, NumberStyles.None, CultureInfo.InvariantCulture, out var wireId))
                    throw new SnapshotRequestException("CAMERA_INSTANCE_ID_INVALID", "Camera instanceId must be an unsigned decimal string.");
                var camera = UPilotEntityIds.ObjectFromWireId(wireId) as Camera;
                if (camera == null) throw new SnapshotRequestException("CAMERA_NOT_FOUND", $"Camera not found for instanceId={target.instanceId}.");
                return camera;
            }

            var cameras = FindSceneCameras();
            var matches = !string.IsNullOrWhiteSpace(target.hierarchyPath)
                ? cameras.Where(value => string.Equals(HierarchyPath(value.transform), target.hierarchyPath, StringComparison.Ordinal)).ToArray()
                : cameras.Where(value => string.Equals(value.name, exactName, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 0) throw new SnapshotRequestException("CAMERA_NOT_FOUND", "No Camera matched the exact selector.");
            if (matches.Length > 1) throw new SnapshotRequestException("AMBIGUOUS_CAMERA", $"Camera selector matched {matches.Length} instances; use instanceId or hierarchyPath.");
            return matches[0];
        }

        private static void ValidateRequest(SnapshotCapturePayload request)
        {
            if (request == null)
                throw new SnapshotRequestException("SNAPSHOT_REQUEST_REQUIRED", "Snapshot request is required.");
            if (request.targets == null || request.targets.Length == 0)
                throw new SnapshotRequestException("SNAPSHOT_TARGETS_REQUIRED", "targets must contain at least one target.");
            if (request.targets.Length > MaxTargets)
                throw new SnapshotRequestException("SNAPSHOT_TARGET_LIMIT_EXCEEDED", $"A Snapshot may contain at most {MaxTargets} targets.");
            if (!string.Equals(request.syncMode, "sameFrame", StringComparison.OrdinalIgnoreCase))
                throw new SnapshotRequestException("SNAPSHOT_SYNC_MODE_UNSUPPORTED", "v0.4 Snapshot supports only syncMode=sameFrame.");
            if (!string.Equals(request.completionPolicy, "allOrNothing", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(request.completionPolicy, "bestEffort", StringComparison.OrdinalIgnoreCase))
                throw new SnapshotRequestException("SNAPSHOT_COMPLETION_POLICY_INVALID", "completionPolicy must be allOrNothing or bestEffort.");
            request.capturePolicy = request.capturePolicy ?? new SnapshotCapturePolicyPayload();
            if (request.capturePolicy.maxStaleFrameAgeMs < 0)
                throw new SnapshotRequestException("SNAPSHOT_CAPTURE_POLICY_INVALID", "maxStaleFrameAgeMs cannot be negative.");
            var defaultChannels = NormalizeChannels(request.channels);
            long pixels = 0;
            var gameViewCount = 0;
            var hasEditorPixelTarget = false;
            foreach (var target in request.targets)
            {
                if (target == null) continue;
                var targetKind = (target.kind ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
                if (targetKind == "gameview") gameViewCount++;
                if (targetKind == "sceneview" || targetKind == "editorwindow") hasEditorPixelTarget = true;
                var channels = target.channels != null && target.channels.Length > 0
                    ? NormalizeChannels(target.channels)
                    : defaultChannels;
                if (target.depthPreview && !channels.Contains("lineardepth"))
                    throw new SnapshotRequestException(
                        "SNAPSHOT_DEPTH_PREVIEW_REQUIRES_LINEAR_DEPTH",
                        "depthPreview=true requires the linearDepth channel.");
                var outputCount = channels.Count + (target.depthPreview ? 1 : 0);
                pixels += (long)ClampDimension(target.width, 1280)
                          * ClampDimension(target.height, 720)
                          * outputCount;
            }
            if (gameViewCount > 1)
                throw new SnapshotRequestException("SNAPSHOT_GAMEVIEW_TARGET_LIMIT", "A Snapshot may contain at most one true GameView target.");
            if (gameViewCount > 0 && hasEditorPixelTarget)
                throw new SnapshotRequestException(
                    "SNAPSHOT_SYNC_TARGET_COMBINATION_UNSUPPORTED",
                    "A sameFrame Snapshot may combine GameView with Camera targets, but not with SceneView or EditorWindow repaint captures.");
            if (pixels > MaxPixels)
                throw new SnapshotRequestException("SNAPSHOT_PIXEL_BUDGET_EXCEEDED", $"Requested {pixels} pixels; maximum is {MaxPixels}.");
        }

        private static void ValidateEvidencePolicy(
            SnapshotProvenancePayload provenance,
            SnapshotCapturePolicyPayload policy)
        {
            policy = policy ?? new SnapshotCapturePolicyPayload();
            if (provenance == null)
                throw new SnapshotRequestException("SNAPSHOT_PROVENANCE_MISSING", "Capture did not produce provenance evidence.");
            if (policy.requireVerifiedPixels && !provenance.pixelSourceVerified)
                throw new SnapshotRequestException("SNAPSHOT_PIXELS_UNVERIFIED", "Capture pixels were not verified against the requested Unity target.");
            if (!policy.allowOcclusionSensitive && provenance.occlusionSensitive)
                throw new SnapshotRequestException("SNAPSHOT_OCCLUSION_SENSITIVE", "Capture depends on visible, unobstructed operating-system pixels.");
            if (!policy.allowFallback && (provenance.degraded || (provenance.fallbackChain?.Length ?? 0) > 0))
                throw new SnapshotRequestException("SNAPSHOT_FALLBACK_REJECTED", "Capture used a fallback while capturePolicy.allowFallback=false.");
        }

        private static void RecordResolutionFailures(SnapshotJobPayload job, ResolvedTargets resolved)
        {
            foreach (var item in resolved.failures)
            {
                job.failures.Add(item.failure);
                job.targets.Add(new SnapshotTargetResultPayload
                {
                    targetId = item.request.targetId,
                    kind = item.request.kind,
                    status = "failed",
                    success = false,
                    requested = item.request,
                    artifactIds = Array.Empty<string>(),
                });
            }
        }

        private static void RecordTargetFailure(
            SnapshotJobPayload job,
            SnapshotTargetRequestPayload request,
            string code,
            string message,
            string stage)
        {
            job.failures.Add(Failure(request.targetId, code, message, stage));
            job.targets.Add(new SnapshotTargetResultPayload
            {
                targetId = request.targetId,
                kind = request.kind,
                status = "failed",
                success = false,
                requested = request,
                artifactIds = job.artifacts
                    .Where(value => string.Equals(value.targetId, request.targetId, StringComparison.Ordinal))
                    .Select(value => value.artifactId)
                    .ToArray(),
            });
        }

        private static void RejectArtifacts(SnapshotJobPayload job)
        {
            foreach (var artifact in job.artifacts)
                artifact.acceptedAsEvidence = false;
        }

        private void UpdateJob(SnapshotJobPayload job, string status, string phase, bool terminal, bool success)
        {
            lock (_gate)
            {
                job.status = status;
                job.phase = phase;
                job.terminal = terminal;
                job.success = success;
                job.updatedAtUtcMs = UtcNowMs();
                PersistJob(job);
            }
        }

        private void FinishJob(SnapshotJobPayload job, string status, bool success)
        {
            lock (_gate)
            {
                job.status = status;
                job.phase = status;
                job.terminal = true;
                job.success = success;
                job.updatedAtUtcMs = UtcNowMs();
                job.endedAtUtcMs = job.updatedAtUtcMs;
                PersistJob(job);
                var manifest = ProjectAbsolute(job.manifestPath);
                if (File.Exists(manifest)) job.manifestSha256 = Sha256(File.ReadAllBytes(manifest));
            }
        }

        private SnapshotJobPayload FindJob(string snapshotId)
        {
            if (string.IsNullOrWhiteSpace(snapshotId)) return null;
            lock (_gate) return _jobs.TryGetValue(snapshotId, out var job) ? job : null;
        }

        private static SnapshotEnvironmentPayload BuildEnvironment()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;
            var windowStateKnown = UPilotWindowDiagnostics.TryGetUnityEditorMinimized(
                out var editorMinimized,
                out _);
            return new SnapshotEnvironmentPayload
            {
                unityVersion = Application.unityVersion,
                platform = Application.platform.ToString(),
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                colorSpace = QualitySettings.activeColorSpace.ToString(),
                renderPipeline = pipeline == null ? "BuiltIn" : pipeline.GetType().FullName,
                renderPipelineVersion = pipeline == null ? string.Empty : pipeline.GetType().Assembly.GetName().Version?.ToString() ?? string.Empty,
                editorWindowState = windowStateKnown ? (editorMinimized ? "minimized" : "normal") : "unknown",
                editorMinimized = windowStateKnown && editorMinimized,
            };
        }

        private static SnapshotCameraInfoPayload BuildCameraInfo(Camera camera)
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;
            return new SnapshotCameraInfoPayload
            {
                instanceId = WireId(camera),
                name = camera.name,
                hierarchyPath = HierarchyPath(camera.transform),
                scenePath = camera.gameObject.scene.path,
                active = camera.gameObject.activeInHierarchy,
                enabled = camera.enabled,
                targetDisplay = camera.targetDisplay,
                depth = camera.depth,
                renderPipeline = pipeline == null ? "BuiltIn" : pipeline.GetType().FullName,
                renderType = ReadUrpRenderType(camera),
            };
        }

        private static SnapshotCameraEvidencePayload BuildCameraEvidence(Camera camera) => new SnapshotCameraEvidencePayload
        {
            instanceId = WireId(camera),
            name = camera.name,
            hierarchyPath = HierarchyPath(camera.transform),
            scenePath = camera.gameObject.scene.path,
            nearClip = camera.nearClipPlane,
            farClip = camera.farClipPlane,
            orthographic = camera.orthographic,
            fieldOfView = camera.fieldOfView,
            orthographicSize = camera.orthographicSize,
            aspect = camera.aspect,
            targetDisplay = camera.targetDisplay,
            usesReversedZBuffer = SystemInfo.usesReversedZBuffer,
            viewMatrix = MatrixValues(camera.worldToCameraMatrix),
            projectionMatrix = MatrixValues(camera.projectionMatrix),
            cameraToWorldMatrix = MatrixValues(camera.cameraToWorldMatrix),
        };

        private static string ReadUrpRenderType(Camera camera)
        {
            var component = camera.GetComponent("UniversalAdditionalCameraData");
            if (component == null) return string.Empty;
            var property = component.GetType().GetProperty("renderType");
            return property?.GetValue(component, null)?.ToString() ?? string.Empty;
        }

        private static Camera[] FindSceneCameras() => Resources.FindObjectsOfTypeAll<Camera>()
            .Where(value => value != null && !EditorUtility.IsPersistent(value) && value.gameObject.scene.IsValid())
            .ToArray();

        private static string HierarchyPath(Transform transform)
        {
            var parts = new Stack<string>();
            for (var current = transform; current != null; current = current.parent) parts.Push(current.name);
            return string.Join("/", parts.ToArray());
        }

        private static float[] MatrixValues(Matrix4x4 matrix)
        {
            var values = new float[16];
            for (var row = 0; row < 4; row++)
                for (var column = 0; column < 4; column++)
                    values[row * 4 + column] = matrix[row, column];
            return values;
        }

        private static string ResolveOutputDirectory(string raw, string snapshotId)
        {
            var root = ProjectRoot();
            var value = string.IsNullOrWhiteSpace(raw)
                ? Path.Combine("Log", "UPilotSnapshots", DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture), snapshotId)
                : raw.Trim();
            var full = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
            var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new SnapshotRequestException("SNAPSHOT_PATH_OUTSIDE_PROJECT", "Snapshot outputDirectory must be under the current Unity project.");
            return full;
        }

        private static void PersistJob(SnapshotJobPayload job)
        {
            var target = ProjectAbsolute(job.manifestPath);
            var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(job, true));
            WriteAtomic(target, bytes);
            WriteAtomic(StatePath(job.snapshotId), bytes);
        }

        private void LoadPersistedJobs()
        {
            var stateRoot = ProjectAbsolute(SnapshotStateDirectory);
            if (!Directory.Exists(stateRoot)) return;

            foreach (var path in Directory.EnumerateFiles(stateRoot, "*.json", SearchOption.TopDirectoryOnly)
                         .Select(value => new FileInfo(value))
                         .OrderByDescending(value => value.LastWriteTimeUtc)
                         .Take(MaxRecoveredJobs)
                         .Select(value => value.FullName))
            {
                try
                {
                    var job = JsonUtility.FromJson<SnapshotJobPayload>(File.ReadAllText(path, Encoding.UTF8));
                    if (job == null || string.IsNullOrWhiteSpace(job.snapshotId)) continue;
                    if (!string.Equals(Path.GetFullPath(path), StatePath(job.snapshotId), StringComparison.OrdinalIgnoreCase)) continue;
                    job.targets = job.targets ?? new List<SnapshotTargetResultPayload>();
                    job.artifacts = job.artifacts ?? new List<SnapshotArtifactPayload>();
                    job.failures = job.failures ?? new List<SnapshotFailurePayload>();
                    if (!job.terminal)
                    {
                        job.failures.Add(Failure(
                            string.Empty,
                            "SNAPSHOT_INTERRUPTED_BY_DOMAIN_RELOAD",
                            "Snapshot execution was interrupted by a Unity Domain Reload or Bridge restart.",
                            job.phase));
                        RejectArtifacts(job);
                        job.status = "failed";
                        job.phase = "failed";
                        job.success = false;
                        job.terminal = true;
                        job.updatedAtUtcMs = UtcNowMs();
                        job.endedAtUtcMs = job.updatedAtUtcMs;
                        PersistJob(job);
                    }
                    var manifest = ProjectAbsolute(job.manifestPath);
                    if (File.Exists(manifest)) job.manifestSha256 = Sha256(File.ReadAllBytes(manifest));
                    _jobs[job.snapshotId] = job;
                    if (!string.IsNullOrWhiteSpace(job.requestKey) && !_requestKeys.ContainsKey(job.requestKey))
                        _requestKeys[job.requestKey] = job.snapshotId;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("SNAPSHOT", $"Ignored invalid persisted Snapshot state '{path}': {ex.Message}");
                }
            }
        }

        private static string StatePath(string snapshotId) =>
            ProjectAbsolute(Path.Combine(SnapshotStateDirectory, SafeFileName(snapshotId) + ".json"));

        private static void WriteAtomic(string target, byte[] bytes)
        {
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static string ProjectRoot() => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProjectAbsolute(string projectRelative) =>
            Path.GetFullPath(Path.Combine(ProjectRoot(), (projectRelative ?? string.Empty).Replace('/', Path.DirectorySeparatorChar)));

        private static string ToProjectRelative(string absolute)
        {
            var root = ProjectRoot().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? absolute.Substring(root.Length).Replace('\\', '/')
                : absolute.Replace('\\', '/');
        }

        private static string SafeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (value ?? "target").Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
            var result = new string(chars).Trim();
            return string.IsNullOrEmpty(result) ? "target" : result;
        }

        private static string ShortHash(string value) =>
            Sha256(Encoding.UTF8.GetBytes(value ?? string.Empty)).Substring(0, 8);

        private static int ClampDimension(int value, int fallback) => Mathf.Clamp(value <= 0 ? fallback : value, 1, 4096);
        private static long UtcNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private static string WireId(UnityEngine.Object value) => UPilotEntityIds.ToWireId(value).ToString(CultureInfo.InvariantCulture);

        private static SnapshotFailurePayload Failure(string targetId, string code, string message, string stage) =>
            new SnapshotFailurePayload { targetId = targetId, code = code, message = message, stage = stage };

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private sealed class ResolvedSnapshotTarget
        {
            public SnapshotTargetRequestPayload request;
            public string kind;
            public Camera camera;
            public SceneView sceneView;
            public EditorWindow editorWindow;
        }

        private sealed class SnapshotGameViewCapture
        {
            public byte[] bytes;
            public int width;
            public int height;
            public bool runInBackgroundForced;
        }

        private sealed class GameViewCaptureRunner : MonoBehaviour
        {
            private TaskCompletionSource<SnapshotGameViewCapture> _completion;
            private double _deadline;
            private bool _previousRunInBackground;
            private bool _runInBackgroundForced;
            private bool _finished;

            public static Task<SnapshotGameViewCapture> CaptureAsync(int width, int height, double timeoutSeconds)
            {
                var host = new GameObject("UPilot Snapshot GameView Capture")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                var runner = host.AddComponent<GameViewCaptureRunner>();
                return runner.Begin(width, height, timeoutSeconds);
            }

            private Task<SnapshotGameViewCapture> Begin(int width, int height, double timeoutSeconds)
            {
                _completion = new TaskCompletionSource<SnapshotGameViewCapture>();
                _deadline = EditorApplication.timeSinceStartup + Math.Max(1.0, timeoutSeconds);
                _previousRunInBackground = Application.runInBackground;
                _runInBackgroundForced = !_previousRunInBackground;
                Application.runInBackground = true;
                EditorApplication.update += CheckTimeout;
                StartCoroutine(CaptureAtEndOfFrame(width, height));
                return _completion.Task;
            }

            private IEnumerator CaptureAtEndOfFrame(int width, int height)
            {
                yield return new WaitForEndOfFrame();
                if (_finished) yield break;
                var target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                {
                    name = "UPilot Snapshot GameView Composite",
                    hideFlags = HideFlags.HideAndDontSave,
                    useDynamicScale = false,
                    useMipMap = false,
                    autoGenerateMips = false,
                };
                try
                {
                    target.Create();
                    if (!target.IsCreated())
                        throw new InvalidOperationException("Failed to allocate the GameView composite render target.");
                    ScreenCapture.CaptureScreenshotIntoRenderTexture(target);
                    var bytes = ReadColorPng(target, width, height);
                    Complete(new SnapshotGameViewCapture
                    {
                        bytes = bytes,
                        width = width,
                        height = height,
                        runInBackgroundForced = _runInBackgroundForced,
                    }, null);
                }
                catch (Exception ex)
                {
                    Complete(null, ex);
                }
                finally
                {
                    ReleaseAndDestroy(target);
                }
            }

            private void CheckTimeout()
            {
                if (_finished || EditorApplication.timeSinceStartup < _deadline) return;
                Complete(null, new TimeoutException(
                    "GameView did not reach an end-of-frame capture point within the Snapshot timeout."));
            }

            private void Complete(SnapshotGameViewCapture result, Exception error)
            {
                if (_finished) return;
                _finished = true;
                EditorApplication.update -= CheckTimeout;
                Application.runInBackground = _previousRunInBackground;
                if (error == null) _completion.TrySetResult(result);
                else _completion.TrySetException(error);
                if (this != null && gameObject != null)
                    UnityEngine.Object.Destroy(gameObject);
            }

            private void OnDestroy()
            {
                if (_finished) return;
                Complete(null, new OperationCanceledException("GameView Snapshot runner was destroyed before capture completed."));
            }
        }

        private sealed class SnapshotCameraChannelCapture
        {
            public int Width;
            public int Height;
            public string CaptureApi;
            public byte[] ColorBytes;
            public byte[] RawDepthBytes;
            public byte[] LinearDepthBytes;
            public byte[] LinearDepthPreviewBytes;
            public SnapshotNumericStatisticsPayload RawDepthStatistics;
            public SnapshotNumericStatisticsPayload LinearDepthStatistics;
        }

        private sealed class SnapshotDepthReadback
        {
            public byte[] Bytes;
            public float[] Values;
            public SnapshotNumericStatisticsPayload Statistics;
        }

        private sealed class SnapshotUrpDepthRequirement
        {
            public Component component;
            public PropertyInfo property;
            public object previousValue;
            public bool addedComponent;
        }

        private sealed class ResolvedTargets
        {
            public readonly List<ResolvedSnapshotTarget> targets = new List<ResolvedSnapshotTarget>();
            public readonly List<ResolvedFailure> failures = new List<ResolvedFailure>();
        }

        private sealed class ResolvedFailure
        {
            public SnapshotTargetRequestPayload request;
            public SnapshotFailurePayload failure;
        }

        private sealed class SnapshotRequestException : Exception
        {
            public string Code { get; }
            public SnapshotRequestException(string code, string message) : base(message) { Code = code; }
        }
    }
}
