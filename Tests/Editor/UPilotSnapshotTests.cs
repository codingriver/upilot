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
    // An asymmetric final-composite fixture: the test records pixels, never corrects orientation.
    public sealed class UPilotGameViewOrientationProbe : MonoBehaviour
    {
        private void OnGUI()
        {
            GUI.depth = -10000;
            var original = GUI.color;
            var colors = new[] { Color.red, Color.green, Color.blue, Color.yellow };
            for (int i = 0; i < colors.Length; i++)
            {
                GUI.color = colors[i];
                GUI.DrawTexture(new Rect((i % 2) * Screen.width / 2f, (i / 2) * Screen.height / 2f,
                    Screen.width / 2f, Screen.height / 2f), Texture2D.whiteTexture);
            }
            GUI.color = Color.white;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 28, alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(0, 8, Screen.width, 44), "TOP / UP 123", style);
            GUI.Label(new Rect(0, Screen.height - 52, Screen.width, 44), "BOTTOM / DOWN 789", style);
            GUI.color = original;
        }
    }

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
        public void NativeTooltipRequiresExactIdentityAndCurrentDomain()
        {
            string domain = UPilotWindowDiagnostics.DomainReloadEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.That(() => UPilotWindowDiagnostics.ResolveNativeTooltip("1", ""), Throws.InvalidOperationException.With.Message.EqualTo("TOOLTIP_DOMAIN_MISMATCH"));
            Assert.That(() => UPilotWindowDiagnostics.ResolveNativeTooltip("1", "old-domain"), Throws.InvalidOperationException.With.Message.EqualTo("TOOLTIP_DOMAIN_MISMATCH"));
            Assert.That(() => UPilotWindowDiagnostics.ResolveNativeTooltip("0", domain), Throws.InvalidOperationException.With.Message.EqualTo("TOOLTIP_INSTANCE_ID_REQUIRED"));
            var unrelated = ScriptableObject.CreateInstance<EditorWindow>();
            try
            {
                Assert.That(() => UPilotWindowDiagnostics.ResolveNativeTooltip(UPilotEntityIds.ToWireId(unrelated).ToString(), domain),
                    Throws.InvalidOperationException.With.Message.EqualTo("TOOLTIP_NOT_FOUND_OR_WRONG_TYPE"));
            }
            finally { UnityEngine.Object.DestroyImmediate(unrelated); }
        }

        [Test]
        public void SnapshotRecoveryDoesNotAdoptAnotherServiceLifetime()
        {
            var job = PersistenceJob("snapshot-old-lifetime-" + Guid.NewGuid().ToString("N"), 0, true, "completed");
            job.serviceLifecycleId = "another-service";
            try
            {
                new UPilotSnapshotService(null).PersistJobForTests(job);
                Assert.That(new UPilotSnapshotService(null).ReadFromApi(job.snapshotId), Is.Null);
            }
            finally { Cleanup(job); }
        }

        [TestCase(1920, 1080, 960, 540, true, "landscape")]
        [TestCase(1920, 1080, 540, 960, false, "portrait")]
        [TestCase(0, 0, 32, 32, false, "square")]
        public void GameViewDiagnosticsDoNotAssertContentOrientation(int sw, int sh, int w, int h, bool matches, string layout)
        {
            Assert.That(UPilotSnapshotService.AspectMatches(sw, sh, w, h), Is.EqualTo(matches));
            Assert.That(UPilotSnapshotService.ImageLayout(w, h), Is.EqualTo(layout));
            var evidence = new SnapshotGameViewEvidencePayload();
            Assert.That(evidence.contentOrientation, Is.EqualTo("unverified"));
            Assert.That(evidence.compositionComplete, Is.EqualTo("unverified"));
            Assert.That(evidence.internalCrop, Is.EqualTo("unknown"));
        }

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
                    Assert.That(Hash(File.ReadAllBytes(path)), Is.EqualTo(artifact.sha256));
                }
                Assert.That(File.Exists(ProjectAbsolute(job.manifestPath)), Is.True);
                Assert.That(job.manifestSha256, Has.Length.EqualTo(64));
                Assert.That(job.persistenceSchemaVersion, Is.EqualTo(2));
                Assert.That(job.snapshotSequence, Is.GreaterThan(0));
                Assert.That(job.persistenceStatus, Is.EqualTo("verified"));
                Assert.That(job.manifestBytes, Is.EqualTo(new FileInfo(ProjectAbsolute(job.manifestPath)).Length));
                Assert.That(job.manifestSha256, Is.EqualTo(Hash(File.ReadAllBytes(ProjectAbsolute(job.manifestPath)))));
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
        public IEnumerator CancellationDuringLastSceneViewRepaintDoesNotPublishSuccess() =>
            VerifyCancellationDuringSceneViewRepaint("allOrNothing", false);

        [UnityTest]
        public IEnumerator BestEffortCancellationDuringLastSceneViewRepaintDoesNotPublishSuccess() =>
            VerifyCancellationDuringSceneViewRepaint("bestEffort", false);

        [UnityTest]
        public IEnumerator CancellationDuringSceneViewRepaintDoesNotCaptureNextTarget() =>
            VerifyCancellationDuringSceneViewRepaint("allOrNothing", true);

        private static IEnumerator VerifyCancellationDuringSceneViewRepaint(string completionPolicy, bool includeNext)
        {
            var sceneView = ScriptableObject.CreateInstance<SceneView>();
            SnapshotJobPayload job = null;
            try
            {
                sceneView.Show();
                sceneView.Repaint();
                yield return null;
                var service = new UPilotSnapshotService(UPilotBridge.Instance);
                var target = new SnapshotTargetRequestPayload
                {
                    targetId = "cancel-last-scene",
                    kind = "sceneView",
                    instanceId = UPilotEntityIds.ToWireId(sceneView).ToString(),
                    width = 320,
                    height = 180,
                };
                var nextTarget = new SnapshotTargetRequestPayload
                {
                    targetId = "must-not-capture",
                    kind = "sceneView",
                    instanceId = target.instanceId,
                    width = 320,
                    height = 180,
                };
                job = Start(service, new SnapshotCapturePayload
                {
                    outputDirectory = "Log/Stage4/SnapshotCancellation/" + Guid.NewGuid().ToString("N"),
                    completionPolicy = completionPolicy,
                    targets = includeNext ? new[] { target, nextTarget } : new[] { target },
                });
                Assert.That(job.terminal, Is.False, "The cancellation must occur during the real repaint wait.");
                Assert.That(job.phase, Is.EqualTo("rendering"));
                Assert.That(service.CancelJob(job.snapshotId), Is.SameAs(job));
                Assert.That(job.cancelRequested, Is.True);
                var deadline = EditorApplication.timeSinceStartup + 20.0;
                while (!job.terminal && EditorApplication.timeSinceStartup < deadline)
                    yield return null;

                File.WriteAllText(ProjectAbsolute(job.outputDirectory + "/observed-state.json"),
                    JsonUtility.ToJson(job, true));
                Assert.That(job.terminal, Is.True, "The in-flight capture must finish within its existing timeout.");
                Assert.That(job.targets.Count, Is.EqualTo(1), "Cancellation must not dispatch the next target.");
                Assert.That(job.targets.Single().provenance.repaintSequence, Is.GreaterThan(0),
                    "This scenario requires real captured pixels, not an early validation failure.");
                Assert.That(job.status, Is.EqualTo("cancelled"));
                Assert.That(job.success, Is.False);
                Assert.That(job.artifacts.All(value => !value.acceptedAsEvidence), Is.True);
                Assert.That(job.persistenceStatus, Is.EqualTo("verified"));
                var manifest = File.ReadAllBytes(ProjectAbsolute(job.manifestPath));
                Assert.That(job.manifestBytes, Is.EqualTo(manifest.LongLength));
                Assert.That(job.manifestSha256, Is.EqualTo(Hash(manifest)));
                var frozen = JsonUtility.ToJson(job);
                for (var i = 0; i < 5; i++) yield return null;
                Assert.That(JsonUtility.ToJson(service.ReadFromApi(job.snapshotId)), Is.EqualTo(frozen));
                Assert.That(File.ReadAllBytes(ProjectAbsolute(job.manifestPath)), Is.EqualTo(manifest));
            }
            finally
            {
                sceneView.Close();
                // Keep only this test's captured PNG/manifest/observation as acceptance evidence.
                Cleanup(job);
            }
        }

        [UnityTest]
        public IEnumerator ExternalTerminalFreezesLateSceneViewCompletion() =>
            VerifyExternalTerminalDuringSceneViewRepaint("allOrNothing", false);

        [UnityTest]
        public IEnumerator ExternalTerminalFreezesLateSceneViewFailure() =>
            VerifyExternalTerminalDuringSceneViewRepaint("allOrNothing", true);

        [UnityTest]
        public IEnumerator BestEffortExternalTerminalFreezesLateSceneViewCompletion() =>
            VerifyExternalTerminalDuringSceneViewRepaint("bestEffort", false);

        [UnityTest]
        public IEnumerator BestEffortExternalTerminalFreezesLateSceneViewFailure() =>
            VerifyExternalTerminalDuringSceneViewRepaint("bestEffort", true);

        private static IEnumerator VerifyExternalTerminalDuringSceneViewRepaint(string policy, bool closeDuringCapture)
        {
            var sceneView = ScriptableObject.CreateInstance<SceneView>();
            SnapshotJobPayload job = null;
            try
            {
                sceneView.Show();
                sceneView.Repaint();
                yield return null;
                // Never reset the Bridge's registered service or another task's Snapshot.
                var service = new UPilotSnapshotService(UPilotBridge.Instance);
                var request = new SnapshotCapturePayload
                {
                    outputDirectory = "Log/Stage4/SnapshotLateCompletion/" + Guid.NewGuid().ToString("N"),
                    completionPolicy = policy,
                    channels = new[] { "color" },
                    targets = new[]
                    {
                        new SnapshotTargetRequestPayload
                        {
                            targetId = "late-scene", kind = "sceneView",
                            instanceId = UPilotEntityIds.ToWireId(sceneView).ToString(),
                            width = 320, height = 180,
                        },
                        new SnapshotTargetRequestPayload
                        {
                            targetId = "must-not-start", kind = "sceneView",
                            instanceId = UPilotEntityIds.ToWireId(sceneView).ToString(),
                            width = 320, height = 180,
                        },
                    },
                };
                job = (SnapshotJobPayload)CreateJob.Invoke(service, new object[] { request });
                var execution = (System.Threading.Tasks.Task)ExecuteJob.Invoke(service, new object[] { job, request });
                Assert.That(execution, Is.Not.Null);
                Assert.That(execution.IsCompleted, Is.False, "Require an actual pending repaint, not an early validation failure.");
                Assert.That(job.phase, Is.EqualTo("rendering"));
                Assert.That(job.terminal, Is.False);
                service.ResetActive();
                Assert.That(job.status, Is.EqualTo("aborted"));
                Assert.That(job.terminal, Is.True);
                Assert.That(job.success, Is.False);
                Assert.That(job.persistenceStatus, Is.EqualTo("verified"));
                var frozen = JsonUtility.ToJson(job);
                var output = ProjectAbsolute(job.outputDirectory);
                var manifest = File.ReadAllBytes(ProjectAbsolute(job.manifestPath));
                var state = File.ReadAllBytes(StatePath(job.snapshotId));
                File.WriteAllText(Path.Combine(output, "frozen-job.json"), frozen);
                File.WriteAllBytes(Path.Combine(output, "frozen-state.json"), state);
                File.WriteAllBytes(Path.Combine(output, "frozen-manifest.json"), manifest);
                Assert.That(job.manifestBytes, Is.EqualTo(manifest.LongLength));
                Assert.That(job.manifestSha256, Is.EqualTo(Hash(manifest)));
                if (closeDuringCapture) sceneView.Close();
                else sceneView.Repaint();
                var deadline = EditorApplication.timeSinceStartup + 20;
                while (!execution.IsCompleted && EditorApplication.timeSinceStartup < deadline)
                    yield return null;
                File.WriteAllText(Path.Combine(output, "after-job.json"), JsonUtility.ToJson(job));
                File.WriteAllBytes(Path.Combine(output, "after-state.json"), File.ReadAllBytes(StatePath(job.snapshotId)));
                File.WriteAllText(Path.Combine(output, "execution.txt"),
                    "policy=" + policy + "\nclosed=" + closeDuringCapture + "\nstatus=" + execution.Status
                    + "\nexception=" + execution.Exception);
                Assert.That(execution.IsCompleted, Is.True, "Observe the original continuation; a fixed frame delay is insufficient.");
                Assert.That(execution.IsFaulted, Is.False, execution.Exception?.ToString());
                Assert.That(job.targets, Is.Empty, "Late success/failure must not append targets or dispatch the second target.");
                Assert.That(job.artifacts, Is.Empty, "No artifact may be accepted after external termination.");
                Assert.That(job.failures, Is.Empty, "Late failure must not replace the frozen external outcome.");
                Assert.That(Directory.GetFiles(output, "*.png", SearchOption.AllDirectories), Is.Empty,
                    "Late callbacks must not leave unregistered PNG files.");
                Assert.That(JsonUtility.ToJson(job), Is.EqualTo(frozen));
                Assert.That(File.ReadAllBytes(ProjectAbsolute(job.manifestPath)), Is.EqualTo(manifest));
                Assert.That(File.ReadAllBytes(StatePath(job.snapshotId)), Is.EqualTo(state));
                Assert.That(service.ReadFromApi(job.snapshotId), Is.Null, "Late work must not reinsert reset activity.");
            }
            finally
            {
                if (sceneView != null) sceneView.Close();
                Cleanup(job);
            }
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

        [UnityTest]
        public IEnumerator GameViewCancellationWhileWaitingForFrameDoesNotPublishSuccess() =>
            RunGameViewLifecycleCase("allOrNothing", "cancel", false);

        [UnityTest]
        public IEnumerator BestEffortGameViewCancellationWhileWaitingForFrameDoesNotPublishSuccess() =>
            RunGameViewLifecycleCase("bestEffort", "cancel", false);

        [UnityTest]
        public IEnumerator GameViewCancellationDoesNotCaptureNextTarget() =>
            RunGameViewLifecycleCase("allOrNothing", "cancel", true);

        [UnityTest]
        public IEnumerator ExternalTerminalFreezesLateGameViewCompletion() =>
            RunGameViewLifecycleCase("allOrNothing", "abort", true);

        [UnityTest]
        public IEnumerator BestEffortExternalTerminalFreezesLateGameViewCompletion() =>
            RunGameViewLifecycleCase("bestEffort", "abort", true);

        [UnityTest]
        public IEnumerator ExternalTerminalFreezesLateGameViewFailure() =>
            RunGameViewLifecycleCase("allOrNothing", "abortDestroy", true);

        [UnityTest]
        public IEnumerator BestEffortExternalTerminalFreezesLateGameViewFailure() =>
            RunGameViewLifecycleCase("bestEffort", "abortDestroy", true);

        private static IEnumerator RunGameViewLifecycleCase(string policy, string action, bool includeNext)
        {
            yield return new EnterPlayMode();
            Exception failure = null;
            var scenario = VerifyGameViewLifecycle(policy, action, includeNext);
            try
            {
                // The scenario only yields frames. Capture assertion failures so ExitPlayMode
                // still runs before rethrowing; do not leave the user's Editor in PlayMode.
                while (true)
                {
                    bool more;
                    object current;
                    try { more = scenario.MoveNext(); current = more ? scenario.Current : null; }
                    catch (Exception ex) { failure = ex; break; }
                    if (!more) break;
                    yield return current;
                }
            }
            finally { (scenario as IDisposable)?.Dispose(); }
            if (EditorApplication.isPlaying) yield return new ExitPlayMode();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static IEnumerator VerifyGameViewLifecycle(string policy, string action, bool includeNext)
        {
            var runnerType = typeof(UPilotSnapshotService).GetNestedType("GameViewCaptureRunner", BindingFlags.NonPublic);
            Assert.That(runnerType, Is.Not.Null);
            var existingRunners = Resources.FindObjectsOfTypeAll(runnerType);
            Assert.That(existingRunners, Is.Empty, "Do not overlap or destroy another task's GameView capture.");
            var previousBackground = Application.runInBackground;
            var outputRoot = "Log/Stage4/GameViewLifecycle/" + Guid.NewGuid().ToString("N");
            var root = new GameObject("UPilotGameViewLifecycleProbe");
            Component ownedRunner = null;
            SnapshotJobPayload job = null;
            try
            {
                var camera = root.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.red;
                camera.cullingMask = 0;
                camera.depth = 1000;
                camera.targetDisplay = 0;
                yield return null;
                var target = new SnapshotTargetRequestPayload
                {
                    targetId = "game", kind = "gameView", targetDisplay = 0, width = 320, height = 180,
                };
                var next = new SnapshotTargetRequestPayload
                {
                    targetId = "must-not-start", kind = "camera", exactName = root.name, width = 320, height = 180,
                };
                var request = new SnapshotCapturePayload
                {
                    outputDirectory = outputRoot + "/snapshot",
                    completionPolicy = policy,
                    channels = new[] { "color" },
                    targets = includeNext ? new[] { target, next } : new[] { target },
                };
                var service = new UPilotSnapshotService(UPilotBridge.Instance);
                job = (SnapshotJobPayload)CreateJob.Invoke(service, new object[] { request });
                var execution = (System.Threading.Tasks.Task)ExecuteJob.Invoke(service, new object[] { job, request });
                Assert.That(job.terminal, Is.False);
                Assert.That(job.phase, Is.EqualTo("rendering"));
                Assert.That(execution.IsCompleted, Is.False, "Require actual pending end-of-frame work.");
                // Synchronous creation since the last yield identifies this test's exact runner.
                ownedRunner = Resources.FindObjectsOfTypeAll(runnerType).Cast<Component>().Single();
                var ownedRunnerId = UPilotEntityIds.ToWireId(ownedRunner);
                var completion = runnerType.GetField("_completion", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(ownedRunner);
                var rawTask = (System.Threading.Tasks.Task)completion.GetType().GetProperty("Task").GetValue(completion);
                Assert.That(rawTask.IsCompleted, Is.False);
                var output = ProjectAbsolute(outputRoot);
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "before-job.json"), JsonUtility.ToJson(job));
                string frozen = null;
                byte[] frozenManifest = null, frozenState = null;
                if (action == "cancel")
                {
                    service.CancelJob(job.snapshotId);
                    Assert.That(job.cancelRequested, Is.True);
                    Assert.That(job.terminal, Is.False, "Cancellation must await the existing capture.");
                }
                else
                {
                    service.ResetActive(); // Isolated service, never the Bridge's global service.
                    Assert.That(job.status, Is.EqualTo("aborted"));
                    Assert.That(job.terminal, Is.True);
                    frozen = JsonUtility.ToJson(job);
                    frozenManifest = File.ReadAllBytes(ProjectAbsolute(job.manifestPath));
                    frozenState = File.ReadAllBytes(StatePath(job.snapshotId));
                    File.WriteAllText(Path.Combine(output, "frozen-job.json"), frozen);
                    File.WriteAllBytes(Path.Combine(output, "frozen-manifest.json"), frozenManifest);
                    File.WriteAllBytes(Path.Combine(output, "frozen-state.json"), frozenState);
                    if (action == "abortDestroy") UnityEngine.Object.DestroyImmediate(ownedRunner.gameObject);
                }
                var deadline = EditorApplication.timeSinceStartup + 20;
                while ((!execution.IsCompleted || ownedRunner != null) && EditorApplication.timeSinceStartup < deadline)
                    yield return null;
                File.WriteAllText(Path.Combine(output, "after-job.json"), JsonUtility.ToJson(job));
                File.WriteAllBytes(Path.Combine(output, "after-state.json"), File.ReadAllBytes(StatePath(job.snapshotId)));
                var rawError = rawTask.Exception?.GetBaseException();
                File.WriteAllText(Path.Combine(output, "execution.txt"),
                    "policy=" + policy + "\naction=" + action + "\nincludeNext=" + includeNext
                    + "\nexecution=" + execution.Status + "\nrawCapture=" + rawTask.Status
                    + "\nrawError=" + rawError?.GetType().FullName
                    + "\nrunnerId=" + ownedRunnerId + "\nrunnerDestroyed=" + (ownedRunner == null)
                    + "\nbackgroundBefore=" + previousBackground + "\nbackgroundAfter=" + Application.runInBackground);
                Assert.That(execution.Status, Is.EqualTo(System.Threading.Tasks.TaskStatus.RanToCompletion));
                Assert.That(ownedRunner == null, Is.True, "The exact owned runner must be destroyed.");
                Assert.That(Application.runInBackground, Is.EqualTo(previousBackground));
                Assert.That(job.terminal, Is.True);
                Assert.That(job.success, Is.False);
                Assert.That(job.persistenceStatus, Is.EqualTo("verified"));
                var manifest = File.ReadAllBytes(ProjectAbsolute(job.manifestPath));
                Assert.That(job.manifestBytes, Is.EqualTo(manifest.LongLength));
                Assert.That(job.manifestSha256, Is.EqualTo(Hash(manifest)));
                if (action == "abortDestroy")
                {
                    Assert.That(rawTask.IsFaulted, Is.True);
                    Assert.That(rawError, Is.TypeOf<OperationCanceledException>());
                }
                else
                {
                    Assert.That(rawTask.Status, Is.EqualTo(System.Threading.Tasks.TaskStatus.RanToCompletion), rawError?.ToString());
                    var raw = rawTask.GetType().GetProperty("Result").GetValue(rawTask);
                    var bytes = (byte[])raw.GetType().GetField("bytes").GetValue(raw);
                    Assert.That(bytes, Is.Not.Empty);
                    // Test-owned witness outside the job directory: NOT a product artifact.
                    File.WriteAllBytes(Path.Combine(output, "witness-readback.png"), bytes);
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    try
                    {
                        Assert.That(texture.LoadImage(bytes), Is.True);
                        Assert.That(texture.width, Is.EqualTo(320));
                        Assert.That(texture.height, Is.EqualTo(180));
                        var center = texture.GetPixel(160, 90);
                        Assert.That(center.r, Is.GreaterThan(center.b + 0.25f), "Require the real owned camera's composite.");
                    }
                    finally { UnityEngine.Object.DestroyImmediate(texture); }
                    if (action == "cancel")
                    {
                        Assert.That(job.status, Is.EqualTo("cancelled"));
                        Assert.That(job.targets.Count, Is.EqualTo(1));
                        Assert.That(job.targets.Single().provenance.pixelSourceVerified, Is.True);
                        Assert.That(job.targets.Single().gameView.playMode, Is.True);
                        var artifact = job.artifacts.Single();
                        Assert.That(artifact.acceptedAsEvidence, Is.False);
                        Assert.That(File.ReadAllBytes(ProjectAbsolute(artifact.path)), Is.EqualTo(bytes));
                        Assert.That(artifact.sha256, Is.EqualTo(Hash(bytes)));
                    }
                }
                Assert.That(job.failures, Is.Empty);
                if (action != "cancel")
                {
                    Assert.That(JsonUtility.ToJson(job), Is.EqualTo(frozen));
                    Assert.That(job.targets, Is.Empty);
                    Assert.That(job.artifacts, Is.Empty);
                    Assert.That(Directory.GetFiles(ProjectAbsolute(job.outputDirectory), "*.png", SearchOption.AllDirectories), Is.Empty);
                    Assert.That(manifest, Is.EqualTo(frozenManifest));
                    Assert.That(File.ReadAllBytes(StatePath(job.snapshotId)), Is.EqualTo(frozenState));
                    Assert.That(service.ReadFromApi(job.snapshotId), Is.Null);
                }
                else
                {
                    var terminal = JsonUtility.ToJson(job);
                    var state = File.ReadAllBytes(StatePath(job.snapshotId));
                    service.CancelJob(job.snapshotId);
                    yield return null;
                    Assert.That(JsonUtility.ToJson(job), Is.EqualTo(terminal));
                    Assert.That(File.ReadAllBytes(ProjectAbsolute(job.manifestPath)), Is.EqualTo(manifest));
                    Assert.That(File.ReadAllBytes(StatePath(job.snapshotId)), Is.EqualTo(state));
                }
            }
            finally
            {
                // Only the runner created synchronously by this test is an owned cleanup target.
                if (ownedRunner != null) UnityEngine.Object.DestroyImmediate(ownedRunner.gameObject);
                UnityEngine.Object.Destroy(root);
                Cleanup(job);
            }
        }

        [UnityTest]
        public IEnumerator AsymmetricGameViewReportsNativeScaledAndMismatchedOutputWithoutCorrection()
        {
            yield return new EnterPlayMode();
            var root = new GameObject("UPilotGameViewOrientationProbe");
            root.AddComponent<UPilotGameViewOrientationProbe>();
            var directory = "Log/Stage4/AsymmetricGameView/" + Guid.NewGuid().ToString("N");
            try
            {
                yield return null;
                int sw = Screen.width, sh = Screen.height;
                Assert.That(sw, Is.GreaterThan(0));
                Assert.That(sh, Is.GreaterThan(0));
                var sizes = new[] { new Vector2Int(sw, sh), new Vector2Int(sw * 2, sh * 2),
                    new Vector2Int(sh == sw ? sh / 2 : sh, sw) };
                for (int index = 0; index < sizes.Length; index++)
                {
                    var size = sizes[index];
                    var service = new UPilotSnapshotService(UPilotBridge.Instance);
                    var job = Start(service, new SnapshotCapturePayload
                    {
                        outputDirectory = directory + "/" + index,
                        targets = new[] { new SnapshotTargetRequestPayload {
                            kind = "gameView", targetId = "asymmetric-" + index, targetDisplay = 0,
                            width = size.x, height = size.y } },
                    });
                    double deadline = EditorApplication.timeSinceStartup + 30;
                    while (!job.terminal && EditorApplication.timeSinceStartup < deadline) yield return null;
                    Assert.That(job.terminal, Is.True, "Observe the original job; do not recapture on timeout.");
                    Assert.That(job.status, Is.EqualTo("completed"), JsonUtility.ToJson(job));
                    var target = job.targets.Single();
                    Assert.That(target.provenance.pixelSourceVerified, Is.True);
                    var diagnostic = target.gameView;
                    Assert.That(diagnostic.screenWidth, Is.EqualTo(sw));
                    Assert.That(diagnostic.screenHeight, Is.EqualTo(sh));
                    Assert.That(diagnostic.requestedWidth, Is.EqualTo(size.x));
                    Assert.That(diagnostic.requestedHeight, Is.EqualTo(size.y));
                    Assert.That(diagnostic.outputWidth, Is.EqualTo(size.x));
                    Assert.That(diagnostic.outputHeight, Is.EqualTo(size.y));
                    Assert.That(diagnostic.capturedAt, Is.GreaterThan(0));
                    Assert.That(diagnostic.aspectRatioMatches, Is.EqualTo(index != 2));
                    Assert.That(string.IsNullOrEmpty(diagnostic.aspectWarning), Is.EqualTo(index != 2));
                    Assert.That(diagnostic.contentOrientation, Is.EqualTo("unverified"));
                    Assert.That(diagnostic.compositionComplete, Is.EqualTo("unverified"));
                    Assert.That(diagnostic.internalCrop, Is.EqualTo("unknown"));
                    var png = job.artifacts.Single(a => a.path.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                    var texture = new Texture2D(2, 2);
                    try
                    {
                        Assert.That(texture.LoadImage(File.ReadAllBytes(ProjectAbsolute(png.path))), Is.True);
                        Assert.That(texture.width, Is.EqualTo(size.x));
                        Assert.That(texture.height, Is.EqualTo(size.y));
                    }
                    finally { UnityEngine.Object.DestroyImmediate(texture); }
                    // Keep these self-owned manifests/PNGs for visual orientation and hash inspection.
                    File.WriteAllText(ProjectAbsolute(directory + "/" + index + "/observed-state.json"),
                        JsonUtility.ToJson(job, true));
                }
            }
            finally { UnityEngine.Object.Destroy(root); }
            yield return new ExitPlayMode();
        }

#if UNITY_EDITOR_WIN
        [UnityTest]
        public IEnumerator NativeWindowSnapshotSurvivesRepeatedMinimizeRestore() => VerifyNativeWindowLifecycle(false);

        [UnityTest]
        public IEnumerator NativeWindowSnapshotFailureCleansUpAndAllowsFollowingCapture() => VerifyNativeWindowLifecycle(true);

        [UnityTest]
        public IEnumerator FloatingWindowSnapshotSurvivesRepeatedMinimizeRestore() => VerifyNativeWindowLifecycle(false, true);

        [UnityTest]
        public IEnumerator FloatingWindowSnapshotFailureCleansUpAndAllowsFollowingCapture() => VerifyNativeWindowLifecycle(true, true);

        [Test]
        public void NativeWindowMinimizedProbeRejectsUnshownWindow()
        {
            var window = ScriptableObject.CreateInstance<UPilotWindowGeometryMarkerProbe>();
            try
            {
                Assert.That(UPilotWindowDiagnostics.TryGetEditorWindowMinimized(window, out var minimized, out var handle), Is.False);
                Assert.That(minimized, Is.False);
                Assert.That(handle, Is.Zero);
                Assert.That(UPilotWindowDiagnostics.TryGetMappedWindowHandle(window, true, out var mapped, out _), Is.False);
                Assert.That(mapped, Is.EqualTo(IntPtr.Zero));
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        private EditorWindow nativeLifecycleWindow;
        private string nativeLifecycleDirectory;
        private NativeWindowLifecycleObservation nativeLifecycleCleanup;

        [UnityTearDown]
        public IEnumerator SettleNativeWindowLifecycleCleanup()
        {
            if (nativeLifecycleDirectory == null) yield break;
            try
            {
                var cleanup = nativeLifecycleCleanup;
                var handle = new IntPtr(cleanup.windowHandle);
                var main = new IntPtr(cleanup.mainWindowHandle);
                if (nativeLifecycleWindow != null) nativeLifecycleWindow.Close();
                double started = EditorApplication.timeSinceStartup;
                int frames = 0;
                while (handle != IntPtr.Zero && IsWindow(handle) && EditorApplication.timeSinceStartup - started < 3)
                {
                    yield return null;
                    frames++;
                }
                cleanup.stage = "cleanup-settled";
                cleanup.observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                cleanup.waitFrames = frames;
                cleanup.waitMs = (EditorApplication.timeSinceStartup - started) * 1000;
                cleanup.exists = handle != IntPtr.Zero && IsWindow(handle);
                cleanup.windowReleased = nativeLifecycleWindow == null;
                cleanup.mainIconic = IsIconic(main);
                cleanup.seamsRestored = UPilotWindowDiagnostics.RejectNativeCaptureForTesting == null
                    && UPilotWindowDiagnostics.AfterNativeCaptureForTesting == null;
                File.WriteAllText(Path.Combine(nativeLifecycleDirectory, "cleanup-settled.json"), JsonUtility.ToJson(cleanup, true));
                Assert.That(cleanup.windowReleased, Is.True);
                Assert.That(cleanup.exists, Is.False, "The original test HWND must disappear within the bounded frame wait.");
                Assert.That(cleanup.seamsRestored, Is.True);
                Assert.That(cleanup.mainIconic, Is.False);
            }
            finally
            {
                nativeLifecycleWindow = null;
                nativeLifecycleDirectory = null;
                nativeLifecycleCleanup = null;
            }
        }

        [Serializable]
        private sealed class NativeWindowLifecycleObservation
        {
            public string stage, domain, instanceId;
            public long observedAt, windowHandle, mainWindowHandle, minimizedProbeHandle;
            public uint processId;
            public int waitFrames;
            public double waitMs;
            public bool exists, iconic, mainIconic, minimizedProbeFound, minimizedProbeValue, windowReleased, seamsRestored;
            public Rect position;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr handle);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr handle);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr handle, int command);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr handle, uint flags);

        private IEnumerator VerifyNativeWindowLifecycle(bool injectFailure, bool floating = false)
        {
            Assert.That(UPilotSnapshotApiV1.Capabilities().available, Is.True);
            var reject = UPilotWindowDiagnostics.RejectNativeCaptureForTesting;
            var after = UPilotWindowDiagnostics.AfterNativeCaptureForTesting;
            Assert.That(reject, Is.Null, "Do not replace another test's native capture seam.");
            Assert.That(after, Is.Null, "Do not replace another test's native capture seam.");
            var directory = ProjectAbsolute("Log/Stage4/WindowLifecycle/" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var processId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var main = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            Assert.That(main, Is.Not.EqualTo(IntPtr.Zero));
            Assert.That(IsIconic(main), Is.False, "The test must not restore or minimize the main Editor.");
            var window = ScriptableObject.CreateInstance<UPilotWindowGeometryMarkerProbe>();
            IntPtr handle = IntPtr.Zero;
            var jobs = new System.Collections.Generic.List<SnapshotJobPayload>();
            Assert.That(nativeLifecycleDirectory, Is.Null);
            nativeLifecycleDirectory = directory;
            nativeLifecycleWindow = window;
            nativeLifecycleCleanup = new NativeWindowLifecycleObservation { mainWindowHandle = main.ToInt64(), processId = processId };
            try
            {
                window.titleContent = new GUIContent("UPilot V17 " + Guid.NewGuid().ToString("N"));
                window.position = new Rect(160, 160, 320, 220);
                if (floating) window.Show();
                else window.ShowUtility();
                window.Repaint();
                yield return null;
                yield return null;
                Assert.That(UPilotWindowDiagnostics.TryGetMappedWindowHandle(window, true, out handle, out var mapping), Is.True, mapping);
                AssertOwnedNativeWindow(handle, main, processId);
                nativeLifecycleCleanup.windowHandle = handle.ToInt64();
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    var prefix = cycle.ToString("D2");
                    SnapshotJobPayload job = null;
                    RecordNativeWindowState(directory, prefix + "-normal", window, handle, main);
                    yield return CaptureLifecycleWindow(window, directory, prefix + "-normal", value => { job = value; jobs.Add(value); });
                    VerifyLifecycleWindowPixels(job, handle);
                    if (injectFailure)
                    {
                        if (cycle == 1)
                            UPilotWindowDiagnostics.AfterNativeCaptureForTesting = target =>
                            { if (target == window) target.position = new Rect(target.position.x + 12, target.position.y + 9, target.position.width, target.position.height); };
                        else UPilotWindowDiagnostics.RejectNativeCaptureForTesting = target => target == window;
                    }
                    else
                    {
                        AssertOwnedNativeWindow(handle, main, processId);
                        ShowWindow(handle, 6); // SW_MINIMIZE: only this verified, exclusive test HWND.
                        yield return null;
                        Assert.That(IsIconic(handle), Is.True);
                    }
                    RecordNativeWindowState(directory, prefix + "-failure", window, handle, main);
                    yield return CaptureLifecycleWindow(window, directory, prefix + "-failure", value => { job = value; jobs.Add(value); });
                    Assert.That(job.status, Is.EqualTo("failed"));
                    Assert.That(job.success, Is.False);
                    Assert.That(job.failures.Count, Is.EqualTo(1));
                    Assert.That(job.failures[0].code, Is.EqualTo(!injectFailure ? "SNAPSHOT_EDITOR_MINIMIZED"
                        : cycle == 1 ? "WINDOW_CHANGED_DURING_CAPTURE" : "SNAPSHOT_TARGET_CAPTURE_FAILED"));
                    Assert.That(job.artifacts.Any(a => a.path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)), Is.False);
                    UPilotWindowDiagnostics.RejectNativeCaptureForTesting = reject;
                    UPilotWindowDiagnostics.AfterNativeCaptureForTesting = after;
                    if (!injectFailure)
                    {
                        AssertOwnedNativeWindow(handle, main, processId);
                        ShowWindow(handle, 4); // SW_SHOWNOACTIVATE: restore without focusing the main Editor.
                        yield return null;
                        Assert.That(IsIconic(handle), Is.False);
                    }
                    window.Repaint();
                    yield return null;
                    RecordNativeWindowState(directory, prefix + "-restored", window, handle, main);
                    yield return CaptureLifecycleWindow(window, directory, prefix + "-restored", value => { job = value; jobs.Add(value); });
                    VerifyLifecycleWindowPixels(job, handle);
                    foreach (var completed in jobs)
                    {
                        var observed = UPilotSnapshotApiV1.Collect(completed.snapshotId);
                        Assert.That(observed.ok, Is.True, observed.errorMessage);
                        Assert.That(JsonUtility.ToJson(observed.job), Is.EqualTo(JsonUtility.ToJson(completed)), "Later work must not mutate an earlier terminal job.");
                    }
                }
            }
            finally
            {
                UPilotWindowDiagnostics.RejectNativeCaptureForTesting = reject;
                UPilotWindowDiagnostics.AfterNativeCaptureForTesting = after;
                foreach (var job in jobs.Where(value => !value.terminal)) UPilotSnapshotApiV1.Cancel(job.snapshotId);
                if (handle != IntPtr.Zero && IsWindow(handle) && IsIconic(handle))
                {
                    GetWindowThreadProcessId(handle, out var owner);
                    if (owner == processId && handle != main) ShowWindow(handle, 4);
                }
                if (window != null) window.Close();
                var cleanup = nativeLifecycleCleanup = new NativeWindowLifecycleObservation
                {
                    stage = "cleanup", observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    windowHandle = handle.ToInt64(), mainWindowHandle = main.ToInt64(), processId = processId,
                    exists = handle != IntPtr.Zero && IsWindow(handle), mainIconic = IsIconic(main),
                    windowReleased = window == null, seamsRestored = UPilotWindowDiagnostics.RejectNativeCaptureForTesting == reject
                        && UPilotWindowDiagnostics.AfterNativeCaptureForTesting == after,
                };
                File.WriteAllText(Path.Combine(directory, "cleanup.json"), JsonUtility.ToJson(cleanup, true));
            }
            Assert.That(window == null, Is.True);
            Assert.That(IsIconic(main), Is.False);
        }

        private static void AssertOwnedNativeWindow(IntPtr handle, IntPtr main, uint processId)
        {
            Assert.That(IsWindow(handle), Is.True);
            Assert.That(handle, Is.Not.EqualTo(main), "Never operate on the main Editor HWND.");
            GetWindowThreadProcessId(handle, out var owner);
            Assert.That(owner, Is.EqualTo(processId));
            Assert.That(GetAncestor(handle, 2), Is.EqualTo(handle)); // GA_ROOT
        }

        private static void RecordNativeWindowState(string directory, string stage, EditorWindow window, IntPtr handle, IntPtr main)
        {
            GetWindowThreadProcessId(handle, out var owner);
            var found = UPilotWindowDiagnostics.TryGetEditorWindowMinimized(window, out var minimized, out var probeHandle);
            File.WriteAllText(Path.Combine(directory, stage + "-native.json"), JsonUtility.ToJson(new NativeWindowLifecycleObservation
            {
                stage = stage, observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                domain = UPilotWindowDiagnostics.DomainReloadEpoch.ToString(), instanceId = UPilotEntityIds.ToWireId(window).ToString(),
                position = window.position, processId = owner, windowHandle = handle.ToInt64(), mainWindowHandle = main.ToInt64(),
                exists = IsWindow(handle), iconic = IsIconic(handle), mainIconic = IsIconic(main),
                minimizedProbeFound = found, minimizedProbeValue = minimized, minimizedProbeHandle = probeHandle,
            }, true));
            Assert.That(found, Is.True, "An exact mapping to the test's original HWND is required.");
            Assert.That(probeHandle, Is.EqualTo(handle.ToInt64()));
            Assert.That(minimized, Is.EqualTo(IsIconic(handle)));
            Assert.That(IsIconic(main), Is.False);
        }

        private static IEnumerator CaptureLifecycleWindow(EditorWindow window, string directory, string stage, Action<SnapshotJobPayload> observe)
        {
            var response = UPilotSnapshotApiV1.Start(new SnapshotCapturePayload
            {
                targets = new[] { new SnapshotTargetRequestPayload { targetId = "window", kind = "editorWindow",
                    instanceId = UPilotEntityIds.ToWireId(window).ToString(), requireContentRect = true } },
                outputDirectory = Path.Combine(directory, stage), requestKey = Guid.NewGuid().ToString("N"),
            });
            Assert.That(response.ok, Is.True, response.errorCode + ":" + response.errorMessage);
            File.WriteAllText(Path.Combine(directory, stage + "-start.json"), JsonUtility.ToJson(response, true));
            var deadline = EditorApplication.timeSinceStartup + 12;
            while (!response.job.terminal && EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
                response = UPilotSnapshotApiV1.Status(response.snapshotId);
                Assert.That(response.ok, Is.True, response.errorMessage);
            }
            observe(response.job);
            File.WriteAllText(Path.Combine(directory, stage + "-observed.json"), JsonUtility.ToJson(response, true));
            Assert.That(response.job.terminal, Is.True, "Observe the original Snapshot, never restart it.");
            Assert.That(response.job.persistenceStatus, Is.EqualTo("verified"));
            var bytes = File.ReadAllBytes(ProjectAbsolute(response.job.manifestPath));
            Assert.That(bytes.LongLength, Is.EqualTo(response.job.manifestBytes));
            Assert.That(Hash(bytes), Is.EqualTo(response.job.manifestSha256));
        }

        private static void VerifyLifecycleWindowPixels(SnapshotJobPayload job, IntPtr handle)
        {
            Assert.That(job.status, Is.EqualTo("completed"));
            Assert.That(job.success, Is.True);
            var provenance = job.targets.Single().provenance;
            Assert.That(provenance.windowHandle, Is.EqualTo(handle.ToInt64()));
            Assert.That(provenance.pixelSourceVerified && provenance.contentRectVerified && provenance.cropComplete, Is.True);
            var artifact = job.artifacts.Single(a => a.path.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
            var bytes = File.ReadAllBytes(ProjectAbsolute(artifact.path));
            Assert.That(artifact.acceptedAsEvidence, Is.True);
            Assert.That(bytes.LongLength, Is.EqualTo(artifact.bytes));
            Assert.That(Hash(bytes), Is.EqualTo(artifact.sha256));
            var texture = new Texture2D(2, 2);
            try
            {
                Assert.That(texture.LoadImage(bytes), Is.True);
                Assert.That(texture.width, Is.EqualTo(artifact.width));
                Assert.That(texture.height, Is.EqualTo(artifact.height));
                var inset = Mathf.Max(1, Mathf.RoundToInt(UPilotWindowGeometryMarkerProbe.MarkerThickness * provenance.pixelsPerPoint / 2f));
                var left = texture.GetPixel(inset, texture.height / 2);
                var right = texture.GetPixel(texture.width - 1 - inset, texture.height / 2);
                var top = texture.GetPixel(texture.width / 2, inset);
                var bottom = texture.GetPixel(texture.width / 2, texture.height - 1 - inset);
                Assert.That(left.r > .8f && left.g < .2f && right.g > .8f && right.r < .2f, Is.True);
                Assert.That((top.b > .8f && bottom.r > .8f && bottom.g > .8f)
                    || (bottom.b > .8f && top.r > .8f && top.g > .8f), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }
#endif

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
                serviceLifecycleId = UPilotServiceLifetime.Id,
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
