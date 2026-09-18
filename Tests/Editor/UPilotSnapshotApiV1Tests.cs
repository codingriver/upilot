using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public class UPilotSnapshotApiV1Tests
    {
        [Test]
        public void DepthShaderReadsCommandBufferTextureWithoutMaterialDefaultShadowing()
        {
            var shader = Shader.Find("Hidden/UPilot/SnapshotDepth");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            var source = new Texture2D(2, 2, TextureFormat.RFloat, false, true);
            var output = new RenderTexture(2, 2, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(2, 2, TextureFormat.RFloat, false, true);
            var command = new CommandBuffer();
            int inputId = Shader.PropertyToID("_UPilotDepthTexture");
            var previousInput = Shader.GetGlobalTexture(inputId);
            var previousActive = RenderTexture.active;
            try
            {
                for (int y = 0; y < 2; y++)
                    for (int x = 0; x < 2; x++)
                        source.SetPixel(x, y, new Color(0.25f, 0, 0, 1));
                source.Apply();
                output.Create();
                command.SetGlobalTexture(inputId, source);
                command.Blit(Texture2D.blackTexture, output, material, 0);
                Graphics.ExecuteCommandBuffer(command);
                RenderTexture.active = output;
                readback.ReadPixels(new Rect(0, 0, 2, 2), 0, 0);
                readback.Apply();
                Assert.That(readback.GetPixel(0, 0).r, Is.EqualTo(0.25f).Within(0.0001f));
            }
            finally
            {
                RenderTexture.active = previousActive;
                Shader.SetGlobalTexture(inputId, previousInput);
                command.Release();
                output.Release();
                UnityEngine.Object.DestroyImmediate(output);
                UnityEngine.Object.DestroyImmediate(readback);
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void CompatibilityEntryReturnsMigrationWithoutStartingHiddenJob()
        {
            Assert.That(UPilotScreenshotService.TrySaveScreenshot(new ScreenshotSavePayload(),
                out var result, out string code, out string message), Is.False);
            Assert.That(result, Is.Null);
            Assert.That(code, Is.EqualTo("SNAPSHOT_ASYNC_API_REQUIRED"));
            Assert.That(message, Does.Contain("no capture was started"));
        }

        [Test]
        public void InvalidRequestAndUnknownIdentityAreStructured()
        {
            Assert.That(UPilotSnapshotApiV1.Capabilities().available, Is.True);
            Assert.That(UPilotSnapshotApiV1.Start(new SnapshotCapturePayload()).ok, Is.False);
            Assert.That(UPilotSnapshotApiV1.Status("missing").errorCode, Is.EqualTo("SNAPSHOT_NOT_FOUND"));
        }

        [UnityTest]
        public IEnumerator StartReturnsQueuedIdentityAndCollectsExistingJob()
        {
            var target = new GameObject("P1 Snapshot API camera");
            string snapshotId = "";
            try
            {
                var camera = target.AddComponent<Camera>();
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.green;
                var request = new SnapshotCapturePayload
                {
                    requestKey = "api-test-" + Guid.NewGuid().ToString("N"),
                    targets = new[] { new SnapshotTargetRequestPayload
                    {
                        kind = "camera", instanceId = UPilotEntityIds.ToWireId(camera).ToString(), width = 64, height = 64,
                    } },
                };
                var started = UPilotSnapshotApiV1.Start(request);
                Assert.That(started.ok, Is.True, started.errorMessage);
                snapshotId = started.snapshotId;
                Assert.That(started.job.terminal, Is.False);
                Assert.That(UPilotSnapshotApiV1.Start(request).snapshotId, Is.EqualTo(snapshotId));
                double deadline = UnityEditor.EditorApplication.timeSinceStartup + 15;
                SnapshotApiResultV1 result;
                do
                {
                    yield return null;
                    result = UPilotSnapshotApiV1.Status(snapshotId);
                } while (!result.job.terminal && UnityEditor.EditorApplication.timeSinceStartup < deadline);
                Assert.That(result.job.terminal, Is.True);
                Assert.That(result.job.success, Is.True, JsonUtility.ToJson(result.job.failures));
                Assert.That(UPilotSnapshotApiV1.Collect(snapshotId).job.artifacts[0].acceptedAsEvidence, Is.True);
                Assert.That(UPilotSnapshotApiV1.Cancel(snapshotId).job.status, Is.EqualTo(result.job.status));
            }
            finally
            {
                if (!string.IsNullOrEmpty(snapshotId)) UPilotSnapshotApiV1.Cancel(snapshotId);
                UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }

    public class UPilotSnapshotBridgeV1Tests
    {
        private sealed class BusinessBridge
        {
            public string failureSignature = "P1_EXPECTED_BUSINESS_FAILURE";
            public string artifactDiagnostic;
            public string snapshotId;

            public SnapshotApiResultV1 Begin(SnapshotCapturePayload request)
            {
                var result = UPilotSnapshotApiV1.Start(request);
                snapshotId = result.snapshotId;
                if (!result.ok) artifactDiagnostic = result.errorCode + ": " + result.errorMessage;
                return result;
            }
        }

        private static SnapshotCapturePayload Request(string kind = "gameView") => new SnapshotCapturePayload
        {
            requestKey = Guid.NewGuid().ToString("N"),
            outputDirectory = "Log/P0P1/SnapshotBridge/" + Guid.NewGuid().ToString("N"),
            targets = new[] { new SnapshotTargetRequestPayload { kind = kind, width = 320, height = 180, targetDisplay = 0 } },
        };

        [Test]
        public void DisabledServicePreservesBusinessFailureAndCreatesNoJob()
        {
            var field = typeof(UPilotSnapshotApiV1).GetField("s_service", BindingFlags.NonPublic | BindingFlags.Static);
            var original = (UPilotSnapshotService)field.GetValue(null);
            try
            {
                UPilotSnapshotApiV1.Bind(null);
                var bridge = new BusinessBridge();
                var result = bridge.Begin(Request());
                Assert.That(UPilotSnapshotApiV1.Capabilities().available, Is.False);
                Assert.That(result.ok, Is.False);
                Assert.That(result.errorCode, Is.EqualTo("SNAPSHOT_UNAVAILABLE"));
                Assert.That(bridge.snapshotId, Is.Null.Or.Empty);
                Assert.That(bridge.artifactDiagnostic, Does.Contain("SNAPSHOT_UNAVAILABLE"));
                Assert.That(bridge.failureSignature, Is.EqualTo("P1_EXPECTED_BUSINESS_FAILURE"));
            }
            finally { UPilotSnapshotApiV1.Bind(original); }
        }

        [Test]
        public void LegacyMigrationDoesNotChangeJobInventory()
        {
            var service = typeof(UPilotSnapshotApiV1).GetField("s_service", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var jobs = (IDictionary)typeof(UPilotSnapshotService).GetField("_jobs", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(service);
            int before = jobs.Count;
            Assert.That(UPilotScreenshotService.TrySaveScreenshot(new ScreenshotSavePayload(),
                out _, out string code, out _), Is.False);
            Assert.That(code, Is.EqualTo("SNAPSHOT_ASYNC_API_REQUIRED"));
            Assert.That(jobs.Count, Is.EqualTo(before));
        }

        [UnityTest]
        public IEnumerator CancelBeforeFrameEndIsIdempotent()
        {
            var result = UPilotSnapshotApiV1.Start(Request());
            Assert.That(result.ok, Is.True, result.errorMessage);
            try
            {
                var cancelled = UPilotSnapshotApiV1.Cancel(result.snapshotId);
                Assert.That(cancelled.ok, Is.True);
                Assert.That(cancelled.job.cancelRequested, Is.True);
                UPilotSnapshotApiV1.Cancel(result.snapshotId);
                double deadline = UnityEditor.EditorApplication.timeSinceStartup + 5;
                do
                {
                    yield return null;
                    cancelled = UPilotSnapshotApiV1.Status(result.snapshotId);
                } while (!cancelled.job.terminal && UnityEditor.EditorApplication.timeSinceStartup < deadline);
                Assert.That(cancelled.job.terminal, Is.True);
                Assert.That(cancelled.job.status, Is.EqualTo("cancelled"));
                Assert.That(cancelled.job.success, Is.False);
                Assert.That(UPilotSnapshotApiV1.Cancel(result.snapshotId).job.status, Is.EqualTo(cancelled.job.status));
                Assert.That(cancelled.job.artifacts, Is.Empty);
            }
            finally { UPilotSnapshotApiV1.Cancel(result.snapshotId); }
        }

        [UnityTearDown]
        public IEnumerator RestoreEditMode()
        {
            if (UnityEditor.EditorApplication.isPlaying) yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator GameViewBridgeCollectsAsyncEvidenceBeforeBusinessCleanup()
        {
            yield return new EnterPlayMode();
            var target = new GameObject("P1 Snapshot Bridge Camera");
            var bridge = new BusinessBridge();
            try
            {
                var camera = target.AddComponent<Camera>();
                camera.depth = 10000;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.magenta;
                camera.targetDisplay = 0;
                yield return null;
                var started = bridge.Begin(Request());
                Assert.That(started.ok, Is.True, started.errorMessage);
                Assert.That(started.job.terminal, Is.False);
                double deadline = UnityEditor.EditorApplication.timeSinceStartup + 15;
                SnapshotApiResultV1 status;
                do
                {
                    yield return null;
                    status = UPilotSnapshotApiV1.Status(bridge.snapshotId);
                } while (status.ok && !status.job.terminal && UnityEditor.EditorApplication.timeSinceStartup < deadline);
                Assert.That(status.ok, Is.True, status.errorMessage);
                Assert.That(status.job.terminal, Is.True);
                Assert.That(status.job.success, Is.True, JsonUtility.ToJson(status.job));
                var collected = UPilotSnapshotApiV1.Collect(bridge.snapshotId).job;
                Assert.That(collected.artifacts.Count, Is.GreaterThan(0));
                Assert.That(collected.artifacts[0].acceptedAsEvidence, Is.True);
                Assert.That(collected.targets[0].provenance.pixelSourceVerified, Is.True);
                Assert.That(collected.targets[0].provenance.occlusionSensitive, Is.False);
                Assert.That(bridge.failureSignature, Is.EqualTo("P1_EXPECTED_BUSINESS_FAILURE"));
            }
            finally
            {
                if (!string.IsNullOrEmpty(bridge.snapshotId)) UPilotSnapshotApiV1.Cancel(bridge.snapshotId);
                UnityEngine.Object.Destroy(target);
            }
        }
    }
}
