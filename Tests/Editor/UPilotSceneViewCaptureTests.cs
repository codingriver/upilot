using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotSceneViewCaptureTests
    {
        private static SceneView activeCaptureWindow;
        private const string ReloadEvidenceKey = "UPilot.SceneViewCaptureTests.ReloadEvidence";
        private static readonly string ReloadDomain = Guid.NewGuid().ToString("N");
        private static TaskCompletionSource<UPilotScreenshotService.ScreenshotBytesResult> reloadCompletion;

        [Serializable]
        private sealed class ReloadEvidence
        {
            public string snapshotId, windowId, beforeDomain, afterDomain, captureError, manifestPath;
            public int beforeHandlers, pendingHandlers, afterHandlers, recoveredHandlers;
            public bool pendingBeforeReload, faultedBeforeReload, recoveredTerminal, recoveredSuccess;
            public long observedBeforeReloadAt, observedAfterReloadAt;
        }

        private static int CaptureHandlerCount()
        {
            var field = typeof(SceneView).GetField("duringSceneGui",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Cannot inspect SceneView subscription cleanup on this Unity version.");
            return ((Delegate)field.GetValue(null))?.GetInvocationList().Count(handler =>
                handler.Target?.GetType().DeclaringType == typeof(UPilotScreenshotService)) ?? 0;
        }

        private static void ObserveBeforeReload()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= ObserveBeforeReload;
            var evidence = JsonUtility.FromJson<ReloadEvidence>(SessionState.GetString(ReloadEvidenceKey, ""));
            evidence.faultedBeforeReload = reloadCompletion.Task.IsFaulted;
            evidence.captureError = reloadCompletion.Task.Exception?.GetBaseException().Message ?? "";
            evidence.afterHandlers = CaptureHandlerCount();
            evidence.observedBeforeReloadAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            SessionState.SetString(ReloadEvidenceKey, JsonUtility.ToJson(evidence));
        }

        private static void PreparePendingReloadCaptures()
        {
            var window = ScriptableObject.CreateInstance<SceneView>(); // Never shown: no target Repaint.
            window.titleContent = new GUIContent("UPilot owned Reload probe");
            var evidence = new ReloadEvidence
            {
                windowId = UPilotEntityIds.ToWireId(window).ToString(),
                beforeDomain = ReloadDomain,
                beforeHandlers = CaptureHandlerCount(),
            };
            // Persist ownership before starting either capture so teardown can always find the window.
            SessionState.SetString(ReloadEvidenceKey, JsonUtility.ToJson(evidence));
            reloadCompletion = new TaskCompletionSource<UPilotScreenshotService.ScreenshotBytesResult>();
            UPilotScreenshotService.CaptureSceneViewAfterRepaint(window, 300, 200, "png", 75, reloadCompletion);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var service = new UPilotSnapshotService(UPilotBridge.Instance);
            var request = new SnapshotCapturePayload
            {
                outputDirectory = "Log/UPilotSceneViewAcceptance/reload-" + Guid.NewGuid().ToString("N"),
                targets = new[] { new SnapshotTargetRequestPayload { kind = "sceneView", instanceId = evidence.windowId } },
            };
            var job = (SnapshotJobPayload)typeof(UPilotSnapshotService).GetMethod("CreateJob", flags)
                .Invoke(service, new object[] { request });
            evidence.snapshotId = job.snapshotId;
            evidence.manifestPath = job.manifestPath;
            typeof(UPilotSnapshotService).GetMethod("ExecuteJob", flags).Invoke(service, new object[] { job, request });
            evidence.pendingBeforeReload = !job.terminal && !reloadCompletion.Task.IsCompleted;
            evidence.pendingHandlers = CaptureHandlerCount();
            SessionState.SetString(ReloadEvidenceKey, JsonUtility.ToJson(evidence));
            AssemblyReloadEvents.beforeAssemblyReload += ObserveBeforeReload; // Observe after both product handlers.
            Assert.That(evidence.pendingBeforeReload, Is.True);
            Assert.That(evidence.pendingHandlers, Is.EqualTo(evidence.beforeHandlers + 2));
        }

        private static void CleanupReloadWindow()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= ObserveBeforeReload;
            var json = SessionState.GetString(ReloadEvidenceKey, "");
            if (string.IsNullOrEmpty(json)) return;
            var evidence = JsonUtility.FromJson<ReloadEvidence>(json);
            // Unity restores SceneView.titleContent during Reload. Ownership is the
            // exact ID persisted before creation of any capture, never the title.
            var window = Resources.FindObjectsOfTypeAll<SceneView>().FirstOrDefault(w =>
                UPilotEntityIds.ToWireId(w).ToString() == evidence.windowId);
            if (window != null) UnityEngine.Object.DestroyImmediate(window);
            Assert.That(Resources.FindObjectsOfTypeAll<SceneView>().Any(w =>
                UPilotEntityIds.ToWireId(w).ToString() == evidence.windowId), Is.False,
                "The owned Reload window was not released.");
            SessionState.EraseString(ReloadEvidenceKey);
        }

        private static void DrawProbe(SceneView current)
        {
            if (current != activeCaptureWindow) return;
            Handles.color = Color.yellow;
            Handles.DrawWireCube(Vector3.zero, Vector3.one);
            Handles.BeginGUI();
            EditorGUI.DrawRect(new Rect(45, 75, 90, 70), Color.magenta);
            Handles.EndGUI();
        }

        [UnityTearDown]
        public IEnumerator RestoreEditMode()
        {
            CleanupReloadWindow();
            if (EditorApplication.isPlaying) yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator PendingCaptureFailsAndUnsubscribesAcrossRealDomainReload()
        {
            PreparePendingReloadCaptures();
            yield return new EnterPlayMode();
            // No call-scoped Task/window references survive the real domain boundary.
            var evidence = JsonUtility.FromJson<ReloadEvidence>(SessionState.GetString(ReloadEvidenceKey, ""));
            evidence.afterDomain = ReloadDomain;
            evidence.recoveredHandlers = CaptureHandlerCount();
            evidence.observedAfterReloadAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var service = new UPilotSnapshotService(UPilotBridge.Instance);
            var job = (SnapshotJobPayload)typeof(UPilotSnapshotService).GetMethod("FindJob",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(service, new object[] { evidence.snapshotId });
            Assert.That(job, Is.Not.Null);
            evidence.recoveredTerminal = job.terminal;
            evidence.recoveredSuccess = job.success;
            var reportPath = Path.Combine(Application.dataPath, "..", job.outputDirectory, "reload-evidence.json");
            File.WriteAllText(reportPath, JsonUtility.ToJson(evidence, true));
            TestContext.WriteLine("Reload evidence: " + reportPath);
            Assert.That(evidence.afterDomain, Is.Not.EqualTo(evidence.beforeDomain), "No real Domain Reload occurred.");
            Assert.That(evidence.faultedBeforeReload, Is.True);
            Assert.That(evidence.captureError, Is.EqualTo("SCENEVIEW_DOMAIN_RELOAD"));
            Assert.That(evidence.afterHandlers, Is.EqualTo(evidence.beforeHandlers));
            Assert.That(evidence.recoveredHandlers, Is.Zero);
            Assert.That(job.terminal && !job.success, Is.True);
            Assert.That(job.status, Is.EqualTo("failed"));
            Assert.That(job.failures.Any(f => (f.code + " " + f.message).Contains("DOMAIN_RELOAD")), Is.True);
            Assert.That(job.artifacts, Is.Empty, "An interrupted no-Repaint request must not create a PNG.");
            yield return null;
            Assert.That(job.terminal && !job.success, Is.True, "Late callbacks changed the terminal result.");
        }

        [UnityTest]
        public IEnumerator SnapshotSingleSceneViewAcceptsTenPostRequestRepaintsInPlayMode()
        {
            yield return new EnterPlayMode();
            var window = ScriptableObject.CreateInstance<SceneView>();
            activeCaptureWindow = window;
            try
            {
                window.ShowUtility();
                window.position = new Rect(100, 100, 600, 450);
                SceneView.duringSceneGui += DrawProbe;
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                // Match snapshot.start: schedule from Editor.update, not the test
                // iterator's synchronization context after repeated Domain Reloads.
                var create = typeof(UPilotSnapshotService).GetMethod("CreateAndSchedule", flags);
                var service = new UPilotSnapshotService(UPilotBridge.Instance);
                var root = "Log/UPilotSceneViewAcceptance/snapshot-" + Guid.NewGuid().ToString("N");
                for (var i = 0; i < 10; i++)
                {
                    var request = new SnapshotCapturePayload
                    {
                        outputDirectory = root + "/" + i,
                        targets = new[] { new SnapshotTargetRequestPayload
                        {
                            kind = "sceneView", instanceId = UPilotEntityIds.ToWireId(window).ToString(),
                        }},
                    };
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var job = (SnapshotJobPayload)create.Invoke(service, new object[] { request });
                    while (!job.terminal && timer.Elapsed.TotalSeconds < 7) yield return null;
                    Assert.That(job.terminal && job.success, Is.True, JsonUtility.ToJson(job));
                    Assert.That(timer.Elapsed.TotalSeconds, Is.LessThan(5.5));
                    Assert.That(job.artifacts.Count, Is.EqualTo(1));
                    Assert.That(job.artifacts[0].acceptedAsEvidence, Is.True);
                    var provenance = job.targets[0].provenance;
                    Assert.That(provenance.includesHandles && provenance.includesSceneGui, Is.True);
                    Assert.That(provenance.pixelSourceVerified && !provenance.occlusionSensitive, Is.True);
                    Assert.That(provenance.repaintObservedAtUtcMs, Is.GreaterThanOrEqualTo(provenance.repaintRequestedAtUtcMs));
                    var texture = new Texture2D(2, 2);
                    try
                    {
                        Assert.That(texture.LoadImage(File.ReadAllBytes(Path.Combine(Application.dataPath, "..",
                            job.artifacts[0].path))), Is.True);
                        Assert.That(texture.GetPixels32().Count(p => p.r > 230 && p.b > 230 && p.g < 25),
                            Is.GreaterThan(3000));
                    }
                    finally { UnityEngine.Object.DestroyImmediate(texture); }
                }
                TestContext.WriteLine("Snapshot evidence: " + root);
            }
            finally
            {
                SceneView.duringSceneGui -= DrawProbe;
                activeCaptureWindow = null;
                if (window != null) window.Close();
            }
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator PlayModeCapturesTenFreshFramesWithSceneGuiPixels()
        {
            yield return new EnterPlayMode();
            var window = ScriptableObject.CreateInstance<SceneView>();
            activeCaptureWindow = window;
            try
            {
                window.titleContent = new GUIContent("UPilot SceneView capture probe");
                window.ShowUtility();
                window.position = new Rect(100, 100, 600, 450);
                SceneView.duringSceneGui += DrawProbe;
                var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Log",
                    "UPilotSceneViewAcceptance", Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(directory);
                long previousSequence = 0;
                for (var i = 0; i < 10; i++)
                {
                    var completion = new TaskCompletionSource<UPilotScreenshotService.ScreenshotBytesResult>();
                    var started = System.Diagnostics.Stopwatch.StartNew();
                    UPilotScreenshotService.CaptureSceneViewAfterRepaint(window, 600, 450, "png", 75, completion);
                    while (!completion.Task.IsCompleted && started.Elapsed.TotalSeconds < 7) yield return null;
                    Assert.That(completion.Task.IsCompleted, Is.True, "Capture did not reach a bounded terminal state.");
                    Assert.That(completion.Task.IsFaulted, Is.False, completion.Task.Exception?.ToString());
                    var capture = completion.Task.Result;
                    Assert.That(started.Elapsed.TotalSeconds, Is.LessThan(5.5));
                    Assert.That(capture, Is.Not.Null);
                    Assert.That(capture.PixelSourceVerified, Is.True);
                    Assert.That(capture.OcclusionSensitive, Is.False);
                    Assert.That(capture.Degraded, Is.False);
                    Assert.That(capture.IncludesHandles && capture.IncludesSceneGui, Is.True);
                    Assert.That(capture.MatchedFullTypeName, Is.EqualTo(typeof(SceneView).FullName));
                    Assert.That(capture.MatchedInstanceId, Is.EqualTo(UPilotEntityIds.ToWireId(window)));
                    Assert.That(capture.RepaintSequence, Is.GreaterThan(previousSequence));
                    Assert.That(capture.RepaintObservedAtUtcMs, Is.GreaterThanOrEqualTo(capture.RepaintRequestedAtUtcMs));
                    previousSequence = capture.RepaintSequence;
                    var texture = new Texture2D(2, 2);
                    try
                    {
                        Assert.That(texture.LoadImage(capture.Bytes), Is.True);
                        var magentaPixels = texture.GetPixels32().Count(p => p.r > 230 && p.b > 230 && p.g < 25);
                        Assert.That(magentaPixels, Is.GreaterThan(3000), "Scene GUI marker is absent from captured pixels.");
                    }
                    finally { UnityEngine.Object.DestroyImmediate(texture); }
                    var path = Path.Combine(directory, $"scene-{i:00}.png");
                    File.WriteAllBytes(path, capture.Bytes);
                    using (var sha = SHA256.Create())
                    {
                        var hash = BitConverter.ToString(sha.ComputeHash(capture.Bytes)).Replace("-", "").ToLowerInvariant();
                        File.WriteAllText(path + ".json", JsonUtility.ToJson(new Evidence
                        {
                            path = path, bytes = capture.Bytes.Length, sha256 = hash,
                            width = capture.Width, height = capture.Height, captureApi = capture.CaptureApi,
                            windowHandle = capture.WindowHandle, unityProcessId = capture.UnityProcessId,
                            repaintSequence = capture.RepaintSequence, requestedAt = capture.RepaintRequestedAtUtcMs,
                            observedAt = capture.RepaintObservedAtUtcMs, matchedInstanceId = capture.MatchedInstanceId.ToString(),
                            acceptedAsEvidence = capture.PixelSourceVerified && !capture.OcclusionSensitive && !capture.Degraded,
                            includesSceneGui = capture.IncludesSceneGui, includesHandles = capture.IncludesHandles,
                            pixelSourceVerified = capture.PixelSourceVerified, occlusionSensitive = capture.OcclusionSensitive,
                        }, true));
                    }
                }
                TestContext.WriteLine("SceneView evidence: " + directory);
            }
            finally
            {
                SceneView.duringSceneGui -= DrawProbe;
                activeCaptureWindow = null;
                if (window != null) window.Close();
            }
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator ClosedWindowTerminatesWithoutLateSuccess()
        {
            var window = ScriptableObject.CreateInstance<SceneView>();
            var completion = new TaskCompletionSource<UPilotScreenshotService.ScreenshotBytesResult>();
            try
            {
                UPilotScreenshotService.CaptureSceneViewAfterRepaint(window, 300, 200, "png", 75, completion);
                UnityEngine.Object.DestroyImmediate(window);
                var deadline = EditorApplication.timeSinceStartup + 2;
                while (!completion.Task.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.That(completion.Task.IsFaulted, Is.True);
                Assert.That(completion.Task.Exception.ToString(), Does.Contain("SCENEVIEW_CLOSED"));
                yield return null;
                Assert.That(completion.Task.IsFaulted, Is.True);
            }
            finally { if (window != null) UnityEngine.Object.DestroyImmediate(window); }
        }

        [UnityTest]
        public IEnumerator HiddenWindowWithoutRepaintTimesOutAndKeepsTerminal()
        {
            var window = ScriptableObject.CreateInstance<SceneView>();
            var completion = new TaskCompletionSource<UPilotScreenshotService.ScreenshotBytesResult>();
            try
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                UPilotScreenshotService.CaptureSceneViewAfterRepaint(window, 300, 200, "png", 75, completion);
                while (!completion.Task.IsCompleted && timer.Elapsed.TotalSeconds < 7) yield return null;
                Assert.That(completion.Task.IsFaulted, Is.True);
                Assert.That(completion.Task.Exception.GetBaseException(), Is.TypeOf<TimeoutException>());
                Assert.That(timer.Elapsed.TotalSeconds, Is.LessThan(6));
                window.ShowUtility();
                window.Repaint();
                yield return null;
                Assert.That(completion.Task.IsFaulted, Is.True, "Late Repaint must not overwrite timeout.");
            }
            finally { if (window != null) window.Close(); }
        }

        [Serializable]
        private sealed class Evidence
        {
            public string path, sha256, captureApi, matchedInstanceId;
            public int bytes, width, height, unityProcessId;
            public long windowHandle, repaintSequence, requestedAt, observedAt;
            public bool acceptedAsEvidence, includesSceneGui, includesHandles, pixelSourceVerified, occlusionSensitive;
        }
    }
}
