// -----------------------------------------------------------------------
// UPilot Editor tests
// -----------------------------------------------------------------------

using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotSnapshotTests
    {
        private static readonly MethodInfo CreateJob = typeof(UPilotSnapshotService).GetMethod(
            "CreateJob",
            BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo ExecuteJob = typeof(UPilotSnapshotService).GetMethod(
            "ExecuteJob",
            BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo CreateAndSchedule = typeof(UPilotSnapshotService).GetMethod(
            "CreateAndSchedule",
            BindingFlags.NonPublic | BindingFlags.Instance);

        [Test]
        public void NewerTerminalManifestWinsOverOlderNonterminalState()
        {
            var state = PersistenceJob("snapshot-state", 4, false, "running");
            var manifest = PersistenceJob("snapshot-state", 5, true, "completed");
            var manifestBytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(manifest));

            var resolved = UPilotSnapshotService.ResolvePersistedSnapshotForTests(
                state, manifest, manifestBytes, out var stateNeedsRebuild);

            Assert.That(stateNeedsRebuild, Is.True);
            Assert.That(resolved, Is.Not.SameAs(state));
            Assert.That(resolved.terminal, Is.True);
            Assert.That(resolved.status, Is.EqualTo("completed"));
            Assert.That(resolved.persistenceStatus, Is.EqualTo("verified"));
            Assert.That(resolved.persistenceRecovered, Is.True);
            Assert.That(resolved.manifestBytes, Is.EqualTo(manifestBytes.LongLength));
            Assert.That(resolved.manifestSha256, Has.Length.EqualTo(64));
        }

        [Test]
        public void RecoveredCancellationKeepsBusinessOutcomeSeparateFromPersistence()
        {
            var state = PersistenceJob("snapshot-cancelled", 4, false, "running");
            var manifest = PersistenceJob("snapshot-cancelled", 5, true, "cancelled");
            manifest.success = false;
            var manifestBytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(manifest));

            var resolved = UPilotSnapshotService.ResolvePersistedSnapshotForTests(
                state, manifest, manifestBytes, out var stateNeedsRebuild);

            Assert.That(stateNeedsRebuild, Is.True);
            Assert.That(resolved.terminal, Is.True);
            Assert.That(resolved.status, Is.EqualTo("cancelled"));
            Assert.That(resolved.success, Is.False);
            Assert.That(resolved.persistenceStatus, Is.EqualTo("verified"));
            Assert.That(resolved.persistenceRecovered, Is.True);
        }

        [Test]
        public void NewerNonterminalManifestCannotRollbackTerminalState()
        {
            var state = PersistenceJob("snapshot-terminal", 7, true, "completed");
            var manifest = PersistenceJob("snapshot-terminal", 8, false, "running");

            var resolved = UPilotSnapshotService.ResolvePersistedSnapshotForTests(
                state, manifest, Encoding.UTF8.GetBytes(JsonUtility.ToJson(manifest)), out var stateNeedsRebuild);

            Assert.That(stateNeedsRebuild, Is.False);
            Assert.That(resolved, Is.SameAs(state));
            Assert.That(resolved.terminal, Is.True);
            Assert.That(resolved.persistenceStatus, Is.EqualTo("unverified"));
            Assert.That(resolved.persistenceError, Is.EqualTo("TERMINAL_STATE_ROLLBACK_REJECTED"));
        }

        [Test]
        public void EqualSequenceRequiresSameManifestProjection()
        {
            var state = PersistenceJob("snapshot-mismatch", 3, true, "completed");
            var manifest = PersistenceJob("snapshot-mismatch", 3, true, "failed");
            var manifestBytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(manifest));
            state.manifestBytes = manifestBytes.LongLength;
            state.manifestSha256 = Hash(manifestBytes);

            var resolved = UPilotSnapshotService.ResolvePersistedSnapshotForTests(
                state, manifest, manifestBytes, out var stateNeedsRebuild);

            Assert.That(stateNeedsRebuild, Is.False);
            Assert.That(resolved.persistenceStatus, Is.EqualTo("unverified"));
            Assert.That(resolved.persistenceError, Is.EqualTo("MANIFEST_STATE_MISMATCH"));
        }

        [Test]
        public void OlderManifestCannotReplaceANewerStateRecord()
        {
            // P2-WP-06-T05: a delayed writer must not roll a job back merely
            // because it eventually reaches disk after a newer state revision.
            var state = PersistenceJob("snapshot-late-manifest", 8, false, "running");
            var manifest = PersistenceJob("snapshot-late-manifest", 7, false, "queued");

            var resolved = UPilotSnapshotService.ResolvePersistedSnapshotForTests(
                state, manifest, Encoding.UTF8.GetBytes(JsonUtility.ToJson(manifest)), out var stateNeedsRebuild);

            Assert.That(stateNeedsRebuild, Is.False);
            Assert.That(resolved, Is.SameAs(state));
            Assert.That(resolved.persistenceStatus, Is.EqualTo("unverified"));
            Assert.That(resolved.persistenceError, Is.EqualTo("MANIFEST_SEQUENCE_STALE"));
        }

        [Test]
        public void RequestKeyMismatchIsNotAcceptedAsTheSamePersistedSnapshot()
        {
            // P2-WP-06-T06: a matching file name/snapshotId cannot substitute
            // for request identity; no recovery write is authorized.
            var state = PersistenceJob("snapshot-request-key", 3, true, "completed");
            var manifest = PersistenceJob("snapshot-request-key", 3, true, "completed");
            manifest.requestKey = "other-request";

            var resolved = UPilotSnapshotService.ResolvePersistedSnapshotForTests(
                state, manifest, Encoding.UTF8.GetBytes(JsonUtility.ToJson(manifest)), out var stateNeedsRebuild);

            Assert.That(stateNeedsRebuild, Is.False);
            Assert.That(resolved, Is.SameAs(state));
            Assert.That(resolved.persistenceStatus, Is.EqualTo("unverified"));
            Assert.That(resolved.persistenceError, Is.EqualTo("MANIFEST_STATE_MISMATCH"));
        }

        [Test]
        public void GeometryChangeCaptureExceptionIsReportedWithoutDowngradingItsFailureCode()
        {
            // P2-WP-04-T04: native capture supplies this diagnostic only after
            // its before/after identity-and-geometry check has rejected pixels.
            var exception = new EditorWindowCaptureException(
                "fixture geometry changed",
                new EditorWindowPixelCapture { originalError = "WINDOW_CHANGED_DURING_CAPTURE" });

            Assert.That(UPilotSnapshotService.CaptureFailureCode(exception),
                Is.EqualTo("WINDOW_CHANGED_DURING_CAPTURE"));
        }

        [Test]
        public void PersistedManifestPathMustRemainUnderTheDeclaredOutputDirectory()
        {
            var job = PersistenceJob("snapshot-path", 1, false, "queued");
            job.manifestPath = "../outside/manifest.json";

            Assert.That(UPilotSnapshotService.TryResolvePersistedManifestPathForTests(job, out var path), Is.False);
            Assert.That(path, Is.Null);

            job.manifestPath = job.outputDirectory + "/other.json";
            Assert.That(UPilotSnapshotService.TryResolvePersistedManifestPathForTests(job, out path), Is.False);
            Assert.That(path, Is.Null);
        }

        [Test]
        public void ManifestOnlyTerminalSnapshotRebuildsOnlyTheStateMetadata()
        {
            var manifest = PersistenceJob("snapshot-manifest-only", 9, true, "completed");
            var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(manifest));

            var resolved = UPilotSnapshotService.ResolvePersistedSnapshotForTests(
                null, manifest, bytes, out var stateNeedsRebuild);

            Assert.That(stateNeedsRebuild, Is.True);
            Assert.That(resolved.terminal, Is.True);
            Assert.That(resolved.persistenceStatus, Is.EqualTo("verified"));
            Assert.That(resolved.persistenceRecovered, Is.True);
            Assert.That(resolved.manifestSha256, Is.EqualTo(Hash(bytes)));
        }

        [Test]
        public void ManifestOnlyNonterminalSnapshotRemainsUnverifiedAndIsNotMistakenForACompletedCapture()
        {
            // P2-WP-06-T08: a complete manifest file does not prove that its
            // paired state transaction or the capture itself completed.
            var manifest = PersistenceJob("snapshot-manifest-running", 9, false, "running");
            var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(manifest));

            var resolved = UPilotSnapshotService.ResolvePersistedSnapshotForTests(
                null, manifest, bytes, out var stateNeedsRebuild);

            Assert.That(stateNeedsRebuild, Is.False);
            Assert.That(resolved.terminal, Is.False);
            Assert.That(resolved.status, Is.EqualTo("running"));
            Assert.That(resolved.persistenceStatus, Is.EqualTo("unverified"));
            Assert.That(resolved.persistenceError, Is.EqualTo("NONTERMINAL_MANIFEST_STATE_MISSING"));
        }

        [Test]
        public void StateOnlySnapshotStaysUnverifiedAndDoesNotRequestManifestRebuild()
        {
            var state = PersistenceJob("snapshot-state-only", 9, true, "completed");

            var resolved = UPilotSnapshotService.ResolvePersistedSnapshotForTests(
                state, null, null, out var stateNeedsRebuild);

            Assert.That(stateNeedsRebuild, Is.False);
            Assert.That(resolved, Is.SameAs(state));
            Assert.That(resolved.persistenceStatus, Is.EqualTo("unverified"));
            Assert.That(resolved.persistenceError, Is.EqualTo("MANIFEST_MISSING"));
        }

        [Test]
        public void UnknownPersistenceSchemaIsNeverInterpretedAsV2()
        {
            var state = PersistenceJob("snapshot-schema", 2, true, "completed");
            var manifest = PersistenceJob("snapshot-schema", 2, true, "completed");
            state.persistenceSchemaVersion = 3;
            manifest.persistenceSchemaVersion = 3;

            var resolved = UPilotSnapshotService.ResolvePersistedSnapshotForTests(
                state, manifest, Encoding.UTF8.GetBytes(JsonUtility.ToJson(manifest)), out var stateNeedsRebuild);

            Assert.That(stateNeedsRebuild, Is.False);
            Assert.That(resolved.persistenceStatus, Is.EqualTo("unverified"));
            Assert.That(resolved.persistenceError, Is.EqualTo("PERSISTENCE_SCHEMA_UNSUPPORTED"));
        }

        [Test]
        public void StateReplacementFailureDoesNotChangeBusinessResultOrPublishVerifiedHash()
        {
            var snapshotId = "snapshot-persist-fault-" + Guid.NewGuid().ToString("N");
            var job = PersistenceJob(snapshotId, 0, true, "completed");
            job.success = true;
            var manifest = ProjectAbsolute(job.manifestPath);
            var state = ProjectAbsolute("Library/UPilot/SnapshotJobs/" + snapshotId + ".json");
            try
            {
                UPilotSnapshotService.PersistenceWriteFaultForTests = stage =>
                    stage == "state" ? new IOException("expected state replacement failure") : null;
                new UPilotSnapshotService(null).PersistJobForTests(job);

                Assert.That(job.terminal, Is.True);
                Assert.That(job.success, Is.True);
                Assert.That(job.persistenceStatus, Is.EqualTo("unverified"));
                Assert.That(job.persistenceError, Is.EqualTo("STATE_REPLACE_FAILED"));
                Assert.That(job.manifestSha256, Is.Empty);
                Assert.That(File.Exists(manifest), Is.True);
                Assert.That(File.Exists(state), Is.False);
            }
            finally
            {
                UPilotSnapshotService.PersistenceWriteFaultForTests = null;
                if (File.Exists(manifest)) File.Delete(manifest);
                var output = ProjectAbsolute(job.outputDirectory);
                if (Directory.Exists(output)) Directory.Delete(output, true);
                if (File.Exists(state)) File.Delete(state);
            }
        }

        [Test]
        public void ManifestReplacementBoundaryKeepsOldPairAndReloadDoesNotRecapture()
        {
            var snapshotId = "snapshot-manifest-boundary-" + Guid.NewGuid().ToString("N");
            var job = PersistenceJob(snapshotId, 0, true, "completed");
            var manifestPath = ProjectAbsolute(job.manifestPath);
            var statePath = StatePath(snapshotId);
            try
            {
                var writer = new UPilotSnapshotService(null);
                writer.PersistJobForTests(job);
                var oldManifest = File.ReadAllBytes(manifestPath);
                var oldState = File.ReadAllBytes(statePath);

                job.updatedAtUtcMs++;
                UPilotSnapshotService.PersistenceWriteFaultForTests = stage =>
                    stage == "manifest-replace" ? new IOException("expected manifest replacement interruption") : null;
                writer.PersistJobForTests(job);
                var staged = Directory.GetFiles(
                    Path.GetDirectoryName(manifestPath),
                    Path.GetFileName(manifestPath) + ".*.tmp");

                Assert.That(File.ReadAllBytes(manifestPath), Is.EqualTo(oldManifest));
                Assert.That(File.ReadAllBytes(statePath), Is.EqualTo(oldState));
                Assert.That(staged, Has.Length.EqualTo(1),
                    "The interrupted manifest replacement must retain its own non-authoritative staging evidence.");
                Assert.That(job.persistenceStatus, Is.EqualTo("unverified"));
                Assert.That(job.persistenceError, Is.EqualTo("MANIFEST_REPLACE_FAILED"));

                UPilotSnapshotService.PersistenceWriteFaultForTests = null;
                var schedules = 0;
                var reloaded = new UPilotSnapshotService(null, _ => schedules++);
                var recovered = reloaded.ReadFromApi(snapshotId);

                Assert.That(recovered, Is.Not.Null);
                Assert.That(recovered.snapshotId, Is.EqualTo(snapshotId));
                Assert.That(recovered.terminal, Is.True);
                Assert.That(recovered.status, Is.EqualTo("completed"));
                Assert.That(recovered.persistenceStatus, Is.EqualTo("verified"));
                Assert.That(recovered.manifestSha256, Is.EqualTo(Hash(oldManifest)));
                Assert.That(schedules, Is.Zero, "Reload must observe the original snapshot instead of scheduling a recapture.");
            }
            finally
            {
                UPilotSnapshotService.PersistenceWriteFaultForTests = null;
                var directory = Path.GetDirectoryName(manifestPath);
                if (Directory.Exists(directory))
                    foreach (var temporary in Directory.GetFiles(directory, Path.GetFileName(manifestPath) + ".*.tmp"))
                        File.Delete(temporary);
                Cleanup(job);
            }
        }

        [Test]
        public void StateReplacementBoundaryRecoversCancelledSnapshotWithOriginalIdWithoutRecapture()
        {
            var snapshotId = "snapshot-state-boundary-" + Guid.NewGuid().ToString("N");
            var job = PersistenceJob(snapshotId, 0, false, "running");
            var manifestPath = ProjectAbsolute(job.manifestPath);
            var statePath = StatePath(snapshotId);
            try
            {
                var writer = new UPilotSnapshotService(null);
                writer.PersistJobForTests(job);
                var oldManifest = File.ReadAllBytes(manifestPath);
                var oldState = File.ReadAllBytes(statePath);

                // Model cancellation after the original observation is persisted.
                // The state fault is raised after the new manifest replacement.
                job.cancelRequested = true;
                job.terminal = true;
                job.success = false;
                job.status = "cancelled";
                job.phase = "cancelled";
                job.updatedAtUtcMs++;
                job.endedAtUtcMs = job.updatedAtUtcMs;
                UPilotSnapshotService.PersistenceWriteFaultForTests = stage =>
                    stage == "state-replace" ? new IOException("expected state replacement interruption") : null;
                writer.PersistJobForTests(job);
                var newManifest = File.ReadAllBytes(manifestPath);

                Assert.That(Hash(newManifest), Is.Not.EqualTo(Hash(oldManifest)));
                Assert.That(File.ReadAllBytes(statePath), Is.EqualTo(oldState));
                Assert.That(job.snapshotId, Is.EqualTo(snapshotId));
                Assert.That(job.terminal, Is.True);
                Assert.That(job.status, Is.EqualTo("cancelled"));
                Assert.That(job.success, Is.False);
                Assert.That(job.persistenceStatus, Is.EqualTo("unverified"));
                Assert.That(job.persistenceError, Is.EqualTo("STATE_REPLACE_FAILED"));
                Assert.That(job.manifestSha256, Is.Empty);

                UPilotSnapshotService.PersistenceWriteFaultForTests = null;
                var schedules = 0;
                var reloaded = new UPilotSnapshotService(null, _ => schedules++);
                var recovered = reloaded.ReadFromApi(snapshotId);
                var rebuiltState = JsonUtility.FromJson<SnapshotJobPayload>(File.ReadAllText(statePath));

                Assert.That(recovered, Is.Not.Null);
                Assert.That(recovered.snapshotId, Is.EqualTo(snapshotId));
                Assert.That(recovered.terminal, Is.True);
                Assert.That(recovered.status, Is.EqualTo("cancelled"));
                Assert.That(recovered.success, Is.False);
                Assert.That(recovered.persistenceStatus, Is.EqualTo("verified"));
                Assert.That(recovered.persistenceRecovered, Is.True);
                Assert.That(recovered.manifestSha256, Is.EqualTo(Hash(newManifest)));
                Assert.That(rebuiltState.snapshotId, Is.EqualTo(snapshotId));
                Assert.That(rebuiltState.persistenceStatus, Is.EqualTo("verified"));
                Assert.That(rebuiltState.manifestSha256, Is.EqualTo(Hash(newManifest)));
                Assert.That(schedules, Is.Zero, "Reload must not hide cancellation by scheduling another capture.");
            }
            finally
            {
                UPilotSnapshotService.PersistenceWriteFaultForTests = null;
                Cleanup(job);
            }
        }

        [Test]
        public void CameraDiscoveryIncludesInactiveSceneCameras()
        {
            var name = "UPilotSnapshotInactive_" + Guid.NewGuid().ToString("N");
            var gameObject = new GameObject(name);
            try
            {
                var camera = gameObject.AddComponent<Camera>();
                gameObject.SetActive(false);
                var find = typeof(UPilotSnapshotService).GetMethod(
                    "FindSceneCameras",
                    BindingFlags.NonPublic | BindingFlags.Static);

                var cameras = (Camera[])find.Invoke(null, Array.Empty<object>());

                Assert.That(cameras, Does.Contain(camera));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [UnityTest]
        public IEnumerator MultipleCameraColorArtifactsShareOneFrameAndRestoreState()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var firstObject = new GameObject("UPilotSnapshotFirst_" + suffix);
            var secondObject = new GameObject("UPilotSnapshotSecond_" + suffix);
            SnapshotJobPayload job = null;
            try
            {
                var first = ConfigureCamera(firstObject, Color.red);
                var second = ConfigureCamera(secondObject, Color.blue);
                var firstDynamicResolution = first.allowDynamicResolution;
                var secondDynamicResolution = second.allowDynamicResolution;
                var service = new UPilotSnapshotService(UPilotBridge.Instance);
                job = Start(service, new SnapshotCapturePayload
                {
                    outputDirectory = "Temp/UPilotSnapshotTests/" + suffix,
                    targets = new[]
                    {
                        CameraTarget("first", first.name, 96, 64),
                        CameraTarget("second", second.name, 96, 64),
                    },
                });

                for (var attempt = 0; attempt < 60 && !job.terminal; attempt++)
                    yield return null;

                Assert.That(job.terminal, Is.True);
                Assert.That(job.status, Is.EqualTo("completed"));
                Assert.That(job.success, Is.True);
                Assert.That(job.targets.Count, Is.EqualTo(2));
                Assert.That(job.targets.All(value => value.success), Is.True);
                Assert.That(job.artifacts.Count, Is.EqualTo(2));
                Assert.That(job.artifacts.All(value => value.acceptedAsEvidence), Is.True);
                Assert.That(job.artifacts.All(value => value.sha256.Length == 64), Is.True);
                Assert.That(job.artifacts.Select(value => value.path).Distinct().Count(), Is.EqualTo(2));
                Assert.That(job.frame.capturedAtUtcMs, Is.GreaterThan(0));
                Assert.That(first.targetTexture, Is.Null);
                Assert.That(second.targetTexture, Is.Null);
                Assert.That(first.allowDynamicResolution, Is.EqualTo(firstDynamicResolution));
                Assert.That(second.allowDynamicResolution, Is.EqualTo(secondDynamicResolution));

                foreach (var artifact in job.artifacts)
                {
                    var path = ProjectAbsolute(artifact.path);
                    Assert.That(File.Exists(path), Is.True, path);
                    Assert.That(new FileInfo(path).Length, Is.EqualTo(artifact.bytes));
                }
                Assert.That(File.Exists(ProjectAbsolute(job.manifestPath)), Is.True);
                Assert.That(job.manifestSha256, Has.Length.EqualTo(64));
                Assert.That(job.persistenceSchemaVersion, Is.EqualTo(2));
                Assert.That(job.snapshotSequence, Is.GreaterThan(0));
                Assert.That(job.persistenceStatus, Is.EqualTo("verified"));
                Assert.That(job.manifestBytes, Is.GreaterThan(0));
                var manifest = JsonUtility.FromJson<SnapshotJobPayload>(File.ReadAllText(ProjectAbsolute(job.manifestPath)));
                Assert.That(manifest.manifestSha256, Is.Empty);
                Assert.That(manifest.manifestBytes, Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(firstObject);
                UnityEngine.Object.DestroyImmediate(secondObject);
                Cleanup(job);
            }
        }

        [UnityTest]
        public IEnumerator DuplicateRequestKeySchedulesExactlyOneCapture()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var cameraObject = new GameObject("UPilotSnapshotDeduplicated_" + suffix);
            SnapshotJobPayload job = null;
            try
            {
                var camera = ConfigureCamera(cameraObject, Color.green);
                var schedules = 0;
                var service = new UPilotSnapshotService(UPilotBridge.Instance, _ => schedules++);
                var request = new SnapshotCapturePayload
                {
                    requestKey = "deduplicated-" + suffix,
                    outputDirectory = "Temp/UPilotSnapshotTests/" + suffix,
                    targets = new[] { CameraTarget("camera", camera.name, 64, 48) },
                };

                Assert.That(CreateAndSchedule, Is.Not.Null);
                job = (SnapshotJobPayload)CreateAndSchedule.Invoke(service, new object[] { request });
                var duplicate = (SnapshotJobPayload)CreateAndSchedule.Invoke(service, new object[] { request });
                Assert.That(duplicate.snapshotId, Is.EqualTo(job.snapshotId));

                for (var attempt = 0; attempt < 60 && !job.terminal; attempt++)
                    yield return null;
                yield return null;
                yield return null;

                Assert.That(job.status, Is.EqualTo("completed"));
                Assert.That(job.artifacts.Count, Is.EqualTo(1));
                Assert.That(job.targets.Count, Is.EqualTo(1));
                Assert.That(schedules, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
                Cleanup(job);
            }
        }

        [UnityTest]
        public IEnumerator BestEffortKeepsValidCameraAndReportsMissingTarget()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var gameObject = new GameObject("UPilotSnapshotValid_" + suffix);
            SnapshotJobPayload job = null;
            try
            {
                var camera = ConfigureCamera(gameObject, Color.green);
                var service = new UPilotSnapshotService(UPilotBridge.Instance);
                job = Start(service, new SnapshotCapturePayload
                {
                    completionPolicy = "bestEffort",
                    outputDirectory = "Temp/UPilotSnapshotTests/" + suffix,
                    targets = new[]
                    {
                        CameraTarget("valid", camera.name, 64, 64),
                        CameraTarget("missing", "Missing_" + suffix, 64, 64),
                    },
                });

                for (var attempt = 0; attempt < 60 && !job.terminal; attempt++)
                    yield return null;

                Assert.That(job.terminal, Is.True);
                Assert.That(job.status, Is.EqualTo("partial"));
                Assert.That(job.success, Is.False);
                Assert.That(job.targets.Count(value => value.success), Is.EqualTo(1));
                Assert.That(job.targets.Count(value => !value.success), Is.EqualTo(1));
                Assert.That(job.failures.Any(value => value.code == "CAMERA_NOT_FOUND"), Is.True);
                Assert.That(job.artifacts, Has.Count.EqualTo(1));
                Assert.That(job.artifacts[0].acceptedAsEvidence, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                Cleanup(job);
            }
        }

        [UnityTest]
        public IEnumerator AllOrNothingSkipsResolvableTargetsWhenAnySelectorFails()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var gameObject = new GameObject("UPilotSnapshotResolvable_" + suffix);
            SnapshotJobPayload job = null;
            try
            {
                var camera = ConfigureCamera(gameObject, Color.yellow);
                var service = new UPilotSnapshotService(UPilotBridge.Instance);
                job = Start(service, new SnapshotCapturePayload
                {
                    completionPolicy = "allOrNothing",
                    outputDirectory = "Temp/UPilotSnapshotTests/" + suffix,
                    targets = new[]
                    {
                        CameraTarget("valid", camera.name, 64, 64),
                        CameraTarget("missing", "Missing_" + suffix, 64, 64),
                    },
                });

                for (var attempt = 0; attempt < 60 && !job.terminal; attempt++)
                    yield return null;

                Assert.That(job.terminal, Is.True);
                Assert.That(job.status, Is.EqualTo("failed"));
                Assert.That(job.artifacts, Is.Empty);
                Assert.That(job.targets.Any(value => value.targetId == "valid" && value.status == "skipped"), Is.True);
                Assert.That(job.targets.Any(value => value.targetId == "missing" && value.status == "failed"), Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                Cleanup(job);
            }
        }

        [UnityTest]
        public IEnumerator CameraDepthCaptureWritesFloatExrStatisticsAndPreview()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var cameraObject = new GameObject("UPilotSnapshotDepthCamera_" + suffix);
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SnapshotJobPayload job = null;
            try
            {
                cube.name = "UPilotSnapshotDepthCube_" + suffix;
                cube.transform.position = new Vector3(0, 0, 3);
                var camera = ConfigureCamera(cameraObject, Color.black);
                camera.nearClipPlane = 0.3f;
                camera.farClipPlane = 20f;
                camera.depthTextureMode = DepthTextureMode.None;
                var service = new UPilotSnapshotService(UPilotBridge.Instance);
                var target = CameraTarget("depth", camera.name, 96, 64);
                target.channels = new[] { "color", "rawDepth", "linearDepth" };
                target.depthPreview = true;
                job = Start(service, new SnapshotCapturePayload
                {
                    outputDirectory = "Temp/UPilotSnapshotTests/" + suffix,
                    targets = new[] { target },
                });

                for (var attempt = 0; attempt < 60 && !job.terminal; attempt++)
                    yield return null;

                Assert.That(job.status, Is.EqualTo("completed"));
                Assert.That(job.artifacts.Select(value => value.role), Is.EquivalentTo(new[]
                {
                    "color", "rawDepth", "linearDepth", "linearDepthPreview",
                }));
                var raw = job.artifacts.Single(value => value.role == "rawDepth");
                var linear = job.artifacts.Single(value => value.role == "linearDepth");
                var preview = job.artifacts.Single(value => value.role == "linearDepthPreview");
                Assert.That(raw.encoding, Is.EqualTo("r32f"));
                Assert.That(raw.units, Is.EqualTo("device-depth-0-1"));
                Assert.That(raw.statistics.validPixelCount, Is.GreaterThan(0));
                Assert.That(raw.statistics.validPixelRatio, Is.InRange(0.0, 1.0));
                Assert.That(linear.units, Is.EqualTo("unity-world-unit"));
                Assert.That(linear.statistics.validPixelCount, Is.EqualTo(raw.statistics.validPixelCount));
                Assert.That(linear.statistics.min, Is.GreaterThan(camera.nearClipPlane));
                Assert.That(linear.statistics.max, Is.LessThanOrEqualTo(camera.farClipPlane + 0.1f));
                Assert.That(preview.acceptedAsEvidence, Is.False);
                Assert.That(camera.depthTextureMode, Is.EqualTo(DepthTextureMode.None));
                Assert.That(camera.targetTexture, Is.Null);

                foreach (var artifact in job.artifacts)
                {
                    Assert.That(artifact.width, Is.EqualTo(96));
                    Assert.That(artifact.height, Is.EqualTo(64));
                    Assert.That(File.Exists(ProjectAbsolute(artifact.path)), Is.True);
                }
                var exr = File.ReadAllBytes(ProjectAbsolute(raw.path));
                Assert.That(exr.Take(4).ToArray(), Is.EqualTo(new byte[] { 0x76, 0x2f, 0x31, 0x01 }));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(cube);
                Cleanup(job);
            }
        }

        [UnityTest]
        public IEnumerator BuiltInCameraDepthCaptureWritesVerifiedRawAndLinearExr()
        {
            var previousDefaultPipeline = GraphicsSettings.defaultRenderPipeline;
            var previousQualityPipeline = QualitySettings.renderPipeline;
            var suffix = Guid.NewGuid().ToString("N");
            var cameraObject = new GameObject("UPilotSnapshotBuiltInDepthCamera_" + suffix);
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Material temporaryMaterial = null;
            SnapshotJobPayload job = null;
            try
            {
                GraphicsSettings.defaultRenderPipeline = null;
                QualitySettings.renderPipeline = null;
                yield return null;
                Assert.That(GraphicsSettings.currentRenderPipeline, Is.Null);

                cube.name = "UPilotSnapshotBuiltInDepthCube_" + suffix;
                cube.transform.position = new Vector3(0, 0, 3);
                var surfaceShader = Shader.Find("Standard");
                Assert.That(surfaceShader, Is.Not.Null);
                temporaryMaterial = new Material(surfaceShader);
                cube.GetComponent<Renderer>().sharedMaterial = temporaryMaterial;
                var camera = ConfigureCamera(cameraObject, Color.black);
                camera.nearClipPlane = 0.3f;
                camera.farClipPlane = 20f;
                var service = new UPilotSnapshotService(UPilotBridge.Instance);
                var target = CameraTarget("built-in-depth", camera.name, 96, 64);
                target.channels = new[] { "color", "rawDepth", "linearDepth" };
                job = Start(service, new SnapshotCapturePayload
                {
                    outputDirectory = "Temp/UPilotSnapshotTests/" + suffix,
                    targets = new[] { target },
                });

                for (var attempt = 0; attempt < 60 && !job.terminal; attempt++)
                    yield return null;

                Assert.That(job.status, Is.EqualTo("completed"), job.failures.FirstOrDefault()?.message);
                Assert.That(job.targets.Single().provenance.captureApi,
                    Is.EqualTo("Camera.Render(targetTexture)+BuiltIn.onPostRenderDepth"));
                Assert.That(job.artifacts.Select(value => value.role),
                    Is.EquivalentTo(new[] { "color", "rawDepth", "linearDepth" }));
                Assert.That(job.artifacts.All(value => value.acceptedAsEvidence), Is.True);
                Assert.That(
                    job.artifacts
                        .Where(value => value.role != "color")
                        .All(value => value.encoding == "r32f"
                            && value.statistics.validPixelCount > 0),
                    Is.True);
            }
            finally
            {
                QualitySettings.renderPipeline = previousQualityPipeline;
                GraphicsSettings.defaultRenderPipeline = previousDefaultPipeline;
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(cube);
                if (temporaryMaterial != null) UnityEngine.Object.DestroyImmediate(temporaryMaterial);
                Cleanup(job);
            }
        }

        [Test]
        public void UnsupportedChannelFailsBeforeCreatingAJob()
        {
            var service = new UPilotSnapshotService(UPilotBridge.Instance);
            var request = new SnapshotCapturePayload
            {
                channels = new[] { "normals" },
                targets = new[]
                {
                    new SnapshotTargetRequestPayload
                    {
                        targetId = "unsupported",
                        kind = "camera",
                        exactName = "does-not-matter",
                    },
                },
            };

            var exception = Assert.Throws<TargetInvocationException>(() =>
                CreateJob.Invoke(service, new object[] { request }));

            Assert.That(exception.InnerException.Message, Does.Contain("Unsupported Snapshot channel"));
        }

        [Test]
        public void WindowContentRectRequirementIsExplicitAndDefaultsToCompatibilityMode()
        {
            var target = new SnapshotTargetRequestPayload();

            Assert.That(target.requireContentRect, Is.False);
            target.requireContentRect = true;
            Assert.That(target.requireContentRect, Is.True);
        }

        [Test]
        public void SceneViewSelectorResolvesExactInstanceId()
        {
            var sceneView = EditorWindow.GetWindow<SceneView>();
            var resolve = typeof(UPilotSnapshotService).GetMethod(
                "ResolveSceneView",
                BindingFlags.NonPublic | BindingFlags.Static);

            var resolved = resolve.Invoke(null, new object[]
            {
                new SnapshotTargetRequestPayload
                {
                    kind = "sceneView",
                    instanceId = UPilotEntityIds.ToWireId(sceneView).ToString(),
                },
            });

            Assert.That(resolved, Is.SameAs(sceneView));
        }

        [Test]
        public void SceneViewSelectorRejectsStaleDomainAndExplicitIdentityConflicts()
        {
            var sceneView = EditorWindow.GetWindow<SceneView>();
            var resolve = typeof(UPilotSnapshotService).GetMethod(
                "ResolveSceneView",
                BindingFlags.NonPublic | BindingFlags.Static);
            var target = new SnapshotTargetRequestPayload
            {
                kind = "sceneView",
                instanceId = UPilotEntityIds.ToWireId(sceneView).ToString(),
                domainGeneration = "stale-domain",
            };

            var domain = Assert.Throws<TargetInvocationException>(() =>
                resolve.Invoke(null, new object[] { target }));
            Assert.That(((UPilotSnapshotService.SnapshotRequestException)domain.InnerException).Code,
                Is.EqualTo("WINDOW_DOMAIN_MISMATCH"));

            target.domainGeneration = UPilotWindowDiagnostics.DomainReloadEpoch.ToString();
            target.fullTypeName = "Fixture.WrongWindow";
            var type = Assert.Throws<TargetInvocationException>(() =>
                resolve.Invoke(null, new object[] { target }));
            Assert.That(((UPilotSnapshotService.SnapshotRequestException)type.InnerException).Code,
                Is.EqualTo("EDITORWINDOW_TYPE_MISMATCH"));
        }

        [UnityTest]
        public IEnumerator SceneViewCaptureProducesVerifiedRepaintEvidence()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var sceneView = EditorWindow.GetWindow<SceneView>();
            SnapshotJobPayload job = null;
            try
            {
                sceneView.Show();
                sceneView.Repaint();
                var service = new UPilotSnapshotService(UPilotBridge.Instance);
                job = Start(service, new SnapshotCapturePayload
                {
                    outputDirectory = "Temp/UPilotSnapshotTests/" + suffix,
                    targets = new[]
                    {
                        new SnapshotTargetRequestPayload
                        {
                            targetId = "scene",
                            kind = "sceneView",
                            instanceId = UPilotEntityIds.ToWireId(sceneView).ToString(),
                            width = 320,
                            height = 180,
                        },
                    },
                });

                for (var attempt = 0; attempt < 300 && !job.terminal; attempt++)
                    yield return null;

                Assert.That(job.status, Is.EqualTo("completed"),
                    job.failures.FirstOrDefault()?.message);
                var target = job.targets.Single(value => value.targetId == "scene");
                Assert.That(target.provenance.pixelSourceVerified, Is.True);
                Assert.That(target.provenance.occlusionSensitive, Is.False);
                Assert.That(target.provenance.includesSceneGui, Is.True);
                Assert.That(target.provenance.includesHandles, Is.True);
                Assert.That(target.provenance.repaintSequence, Is.GreaterThan(0));
                Assert.That(target.provenance.repaintObservedAtUtcMs,
                    Is.GreaterThanOrEqualTo(target.provenance.repaintRequestedAtUtcMs));
                Assert.That(target.provenance.matchedInstanceId,
                    Is.EqualTo(UPilotEntityIds.ToWireId(sceneView).ToString()));
                Assert.That(job.artifacts.Single().acceptedAsEvidence, Is.True);
            }
            finally
            {
                Cleanup(job);
            }
        }

        [UnityTest]
        public IEnumerator GameViewTargetFailsExplicitlyOutsidePlayMode()
        {
            Assert.That(EditorApplication.isPlaying, Is.False);
            var suffix = Guid.NewGuid().ToString("N");
            SnapshotJobPayload job = null;
            try
            {
                var service = new UPilotSnapshotService(UPilotBridge.Instance);
                job = Start(service, new SnapshotCapturePayload
                {
                    outputDirectory = "Temp/UPilotSnapshotTests/" + suffix,
                    targets = new[]
                    {
                        new SnapshotTargetRequestPayload
                        {
                            targetId = "game",
                            kind = "gameView",
                            targetDisplay = 0,
                            width = 320,
                            height = 180,
                        },
                    },
                });

                for (var attempt = 0; attempt < 60 && !job.terminal; attempt++)
                    yield return null;

                Assert.That(job.status, Is.EqualTo("failed"));
                Assert.That(job.failures.Any(value => value.code == "SNAPSHOT_GAMEVIEW_REQUIRES_PLAYMODE"), Is.True);
                Assert.That(job.artifacts, Is.Empty);
            }
            finally
            {
                Cleanup(job);
            }
        }

        [UnityTest]
        public IEnumerator GameViewCaptureUsesDisplayZeroFinalComposite()
        {
            yield return new EnterPlayMode();

            var suffix = Guid.NewGuid().ToString("N");
            var backgroundObject = new GameObject("UPilotSnapshotGameBackground_" + suffix);
            SnapshotJobPayload job = null;
            try
            {
                var background = backgroundObject.AddComponent<Camera>();
                background.clearFlags = CameraClearFlags.SolidColor;
                background.backgroundColor = Color.red;
                background.depth = 0;
                background.targetDisplay = 0;

                yield return null;
                var service = new UPilotSnapshotService(UPilotBridge.Instance);
                job = Start(service, new SnapshotCapturePayload
                {
                    outputDirectory = "Temp/UPilotSnapshotTests/" + suffix,
                    targets = new[]
                    {
                        new SnapshotTargetRequestPayload
                        {
                            targetId = "game",
                            kind = "gameView",
                            targetDisplay = 0,
                            width = 320,
                            height = 180,
                        },
                    },
                });

                for (var attempt = 0; attempt < 300 && !job.terminal; attempt++)
                    yield return null;

                Assert.That(job.status, Is.EqualTo("completed"), job.failures.FirstOrDefault()?.message);
                var target = job.targets.Single();
                Assert.That(target.provenance.captureApi,
                    Is.EqualTo("WaitForEndOfFrame+ScreenCapture.CaptureScreenshotIntoRenderTexture"));
                Assert.That(target.provenance.pixelSourceVerified, Is.True);
                Assert.That(target.provenance.occlusionSensitive, Is.False);
                Assert.That(target.gameView.targetDisplay, Is.Zero);
                var artifact = job.artifacts.Single();
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                try
                {
                    Assert.That(texture.LoadImage(File.ReadAllBytes(ProjectAbsolute(artifact.path))), Is.True);
                    var center = texture.GetPixel(texture.width / 2, texture.height / 2);
                    Assert.That(center.r, Is.GreaterThan(center.b + 0.25f));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(backgroundObject);
                Cleanup(job);
            }

            yield return new ExitPlayMode();
        }

        private static Camera ConfigureCamera(GameObject gameObject, Color background)
        {
            var camera = gameObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = background;
            camera.allowDynamicResolution = true;
            return camera;
        }

        private static SnapshotTargetRequestPayload CameraTarget(string targetId, string exactName, int width, int height) =>
            new SnapshotTargetRequestPayload
            {
                targetId = targetId,
                kind = "camera",
                exactName = exactName,
                width = width,
                height = height,
            };

        private static SnapshotJobPayload Start(UPilotSnapshotService service, SnapshotCapturePayload request)
        {
            Assert.That(CreateJob, Is.Not.Null);
            Assert.That(ExecuteJob, Is.Not.Null);
            var job = (SnapshotJobPayload)CreateJob.Invoke(service, new object[] { request });
            ExecuteJob.Invoke(service, new object[] { job, request });
            return job;
        }

        private static SnapshotJobPayload PersistenceJob(string snapshotId, long sequence, bool terminal, string status) =>
            new SnapshotJobPayload
            {
                persistenceSchemaVersion = 2,
                snapshotId = snapshotId,
                requestKey = "request-" + snapshotId,
                requestHash = "hash-" + snapshotId,
                snapshotSequence = sequence,
                terminal = terminal,
                success = terminal,
                status = status,
                phase = status,
                outputDirectory = "Temp/UPilotSnapshotTests/" + snapshotId,
                manifestPath = "Temp/UPilotSnapshotTests/" + snapshotId + "/manifest.json",
            };

        private static string Hash(byte[] bytes)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string ProjectAbsolute(string relative) =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", relative.Replace('/', Path.DirectorySeparatorChar)));

        private static string StatePath(string snapshotId) =>
            ProjectAbsolute("Library/UPilot/SnapshotJobs/" + snapshotId + ".json");

        private static void Cleanup(SnapshotJobPayload job)
        {
            if (job == null) return;
            var output = ProjectAbsolute(job.outputDirectory);
            var expectedRoot = ProjectAbsolute("Temp/UPilotSnapshotTests")
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (output.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(output))
                Directory.Delete(output, true);

            var state = ProjectAbsolute("Library/UPilot/SnapshotJobs/" + job.snapshotId + ".json");
            if (File.Exists(state)) File.Delete(state);
        }
    }
}
