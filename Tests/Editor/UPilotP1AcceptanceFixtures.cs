using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public class UPilotP1AcceptanceFixtures
    {
        [UnityTest, Timeout(450000), Explicit("UP-011 targeted long-running acceptance only.")]
        public IEnumerator LongRunBeyondSixMinutes() => ProgressFor(370);

        [UnityTest, Explicit("Targeted Runner cancellation and timeout acceptance only.")]
        public IEnumerator CancelableProgressThirtySeconds() => ProgressFor(30);

        [Test, Explicit("Deliberate failure for UP-011 failure evidence.")]
        public void ExpectedBusinessFailure()
        {
            LogAssert.Expect(LogType.Error, "P1_EXPECTED_CONSOLE_ERROR");
            Debug.LogError("P1_EXPECTED_CONSOLE_ERROR");
            Assert.Fail("P1_EXPECTED_BUSINESS_FAILURE");
        }

        private static IEnumerator ProgressFor(double seconds)
        {
            double started = EditorApplication.timeSinceStartup;
            double nextProgress = started;
            try
            {
                while (EditorApplication.timeSinceStartup - started < seconds)
                {
                    if (EditorApplication.timeSinceStartup >= nextProgress)
                    {
                        TestContext.Progress.WriteLine("P1 progress: " + (EditorApplication.timeSinceStartup - started).ToString("F1"));
                        nextProgress += 5;
                    }
                    yield return null;
                }
                Assert.That(EditorApplication.timeSinceStartup - started, Is.GreaterThanOrEqualTo(seconds));
            }
            finally
            {
                TestContext.Progress.WriteLine("P1 fixture released at " + DateTime.UtcNow.ToString("O"));
            }
        }

        [UnityTest, Explicit("UP-012 Built-in pipeline acceptance with retained EXR artifacts.")]
        public IEnumerator BuiltInDepthEvidence() => CaptureDepth(false);

        [UnityTest, Explicit("UP-012 URP pipeline acceptance with retained EXR artifacts.")]
        public IEnumerator UrpDepthEvidence() => CaptureDepth(true);

        private static IEnumerator CaptureDepth(bool urp)
        {
            var previousDefault = GraphicsSettings.defaultRenderPipeline;
            var previousQuality = QualitySettings.renderPipeline;
            GameObject cameraObject = null;
            GameObject cube = null;
            RenderPipelineAsset temporaryPipeline = null;
            ScriptableObject temporaryRenderer = null;
            Material temporaryMaterial = null;
            string snapshotId = "";
            string directory = "Log/UPilotP1Depth/" + (urp ? "URP-" : "BuiltIn-") + Guid.NewGuid().ToString("N");
            try
            {
                RenderPipelineAsset pipeline = null;
                if (urp)
                {
                    var candidates = AssetDatabase.FindAssets("t:RenderPipelineAsset")
                        .Select(AssetDatabase.GUIDToAssetPath)
                        .Select(AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>)
                        .Where(asset => asset != null && asset.GetType().Name == "UniversalRenderPipelineAsset").ToArray();
                    pipeline = new[] { previousQuality, previousDefault }.FirstOrDefault(
                        asset => asset != null && asset.GetType().Name == "UniversalRenderPipelineAsset");
                    if (pipeline == null)
                    {
                        Assert.That(candidates.Length, Is.LessThanOrEqualTo(1), "An exact existing URP pipeline must be unambiguous.");
                        pipeline = candidates.SingleOrDefault();
                        if (pipeline == null)
                        {
                            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                            var assetType = assemblies.Select(assembly => assembly.GetType("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset"))
                                .FirstOrDefault(type => type != null);
                            var rendererType = assemblies.Select(assembly => assembly.GetType("UnityEngine.Rendering.Universal.UniversalRendererData"))
                                .FirstOrDefault(type => type != null);
                            Assert.That(assetType, Is.Not.Null, "URP must be installed in the authorized acceptance project.");
                            Assert.That(rendererType, Is.Not.Null);
                            temporaryRenderer = ScriptableObject.CreateInstance(rendererType);
                            var create = assetType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
                            Assert.That(create, Is.Not.Null, "The installed URP must expose its supported Editor Create API.");
                            pipeline = temporaryPipeline = (RenderPipelineAsset)create.Invoke(null, new object[] { temporaryRenderer });
                        }
                    }
                }
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;
                yield return null;
                Assert.That(GraphicsSettings.currentRenderPipeline, Is.EqualTo(pipeline));
                cameraObject = new GameObject("UPilot P1 depth camera");
                cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.position = new Vector3(0, 0, 3);
                var surfaceShader = Shader.Find(urp ? "Universal Render Pipeline/Lit" : "Standard");
                Assert.That(surfaceShader, Is.Not.Null);
                temporaryMaterial = new Material(surfaceShader);
                cube.GetComponent<Renderer>().sharedMaterial = temporaryMaterial;
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.nearClipPlane = 0.3f;
                camera.farClipPlane = 20;
                var started = UPilotSnapshotApiV1.Start(new SnapshotCapturePayload
                {
                    outputDirectory = directory, requestKey = directory,
                    targets = new[] { new SnapshotTargetRequestPayload
                    {
                        kind = "camera", instanceId = UPilotEntityIds.ToWireId(camera).ToString(),
                        width = 96, height = 64, channels = new[] { "rawDepth", "linearDepth" },
                    } },
                });
                Assert.That(started.ok, Is.True, started.errorMessage);
                snapshotId = started.snapshotId;
                double deadline = EditorApplication.timeSinceStartup + 30;
                SnapshotApiResultV1 result;
                do
                {
                    yield return null;
                    result = UPilotSnapshotApiV1.Status(snapshotId);
                    Assert.That(result.ok, Is.True, result.errorMessage);
                } while (!result.job.terminal && EditorApplication.timeSinceStartup < deadline);
                Assert.That(result.job.success, Is.True, JsonUtility.ToJson(result.job));
                var job = UPilotSnapshotApiV1.Collect(snapshotId).job;
                var provenance = job.targets.Single().provenance;
                Assert.That(provenance.pixelSourceVerified, Is.True);
                Assert.That(provenance.occlusionSensitive, Is.False);
                foreach (var artifact in job.artifacts)
                {
                    Assert.That(artifact.acceptedAsEvidence, Is.True);
                    Assert.That(artifact.width, Is.EqualTo(96));
                    Assert.That(artifact.height, Is.EqualTo(64));
                    Assert.That(artifact.encoding, Is.EqualTo("r32f"));
                    Assert.That(artifact.statistics.validPixelCount, Is.GreaterThan(0));
                    string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", artifact.path));
                    byte[] bytes = File.ReadAllBytes(path);
                    Assert.That(bytes.Take(4).ToArray(), Is.EqualTo(new byte[] { 0x76, 0x2f, 0x31, 0x01 }));
                    using var sha = SHA256.Create();
                    Assert.That(BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(),
                        Is.EqualTo(artifact.sha256.ToLowerInvariant()));
                }
                var linear = job.artifacts.Single(artifact => artifact.role == "linearDepth");
                Assert.That(linear.statistics.min, Is.InRange(2.3, 2.7));
                Assert.That(linear.statistics.max, Is.LessThanOrEqualTo(camera.farClipPlane + 0.1));
                var raw = job.artifacts.Single(artifact => artifact.role == "rawDepth");
                Assert.That(raw.statistics.min, Is.InRange(0, 1));
                Assert.That(raw.statistics.max, Is.InRange(0, 1));
                string output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", directory));
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "evidence.json"), JsonUtility.ToJson(job, true));
            }
            finally
            {
                if (!string.IsNullOrEmpty(snapshotId)) UPilotSnapshotApiV1.Cancel(snapshotId);
                if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
                if (cube != null) UnityEngine.Object.DestroyImmediate(cube);
                if (temporaryMaterial != null) UnityEngine.Object.DestroyImmediate(temporaryMaterial);
                QualitySettings.renderPipeline = previousQuality;
                GraphicsSettings.defaultRenderPipeline = previousDefault;
                if (temporaryPipeline != null) UnityEngine.Object.DestroyImmediate(temporaryPipeline);
                if (temporaryRenderer != null) UnityEngine.Object.DestroyImmediate(temporaryRenderer);
                Assert.That(QualitySettings.renderPipeline, Is.EqualTo(previousQuality));
                Assert.That(GraphicsSettings.defaultRenderPipeline, Is.EqualTo(previousDefault));
            }
        }
    }

    [Serializable]
    internal sealed class NativeWindowEvidence
    {
        public string scenario;
        public string value = "initial";
        public string lastAction = "opened";
        public int saveCalls;
        public int discardCalls;
        public int destroyCalls;
        public bool windowOpen;
        public string instanceId;
        public string title;
        public string evidencePath;
        public long evidenceBytes;
        public string evidenceSha256;
        public bool modalClickPosted;
        public string modalClickButton;
        public int processId;
    }

    public sealed class UPilotUnsavedWindowFixture : EditorWindow
    {
        [SerializeField] private string scenario;

        public static string Open(string scenarioId)
        {
            ValidateScenario(scenarioId);
            foreach (var existing in Resources.FindObjectsOfTypeAll<UPilotUnsavedWindowFixture>()
                         .Where(item => item != null && item.scenario == scenarioId).ToArray())
                existing.Close();

            var evidence = new NativeWindowEvidence
            {
                scenario = scenarioId,
                title = TitleFor(scenarioId),
                processId = System.Diagnostics.Process.GetCurrentProcess().Id,
            };
            WriteEvidence(scenarioId, evidence);

            var window = CreateInstance<UPilotUnsavedWindowFixture>();
            window.scenario = scenarioId;
            window.titleContent = new GUIContent(evidence.title);
            window.position = new Rect(160, 160, 420, 220);
            window.ShowUtility();
            window.hasUnsavedChanges = true;
            window.saveChangesMessage = "UPilot P1 unsaved fixture " + scenarioId;
            window.Focus();
            return Status(scenarioId);
        }

        public static string Status(string scenarioId)
        {
            ValidateScenario(scenarioId);
            var evidence = ReadEvidence(scenarioId);
            var window = Resources.FindObjectsOfTypeAll<UPilotUnsavedWindowFixture>()
                .SingleOrDefault(item => item != null && item.scenario == scenarioId);
            evidence.windowOpen = window != null;
            evidence.instanceId = window == null ? "" : UPilotEntityIds.ToWireId(window).ToString();
            evidence.title = TitleFor(scenarioId);
            evidence.processId = System.Diagnostics.Process.GetCurrentProcess().Id;
            string path = EvidencePath(scenarioId);
            evidence.evidencePath = RelativePath(path);
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                evidence.evidenceBytes = bytes.LongLength;
                using var sha = SHA256.Create();
                evidence.evidenceSha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
            string clickPath = Path.Combine(DirectoryFor(scenarioId), "modal-click.json");
            if (File.Exists(clickPath))
            {
                var click = JsonUtility.FromJson<NativeWindowEvidence>(File.ReadAllText(clickPath));
                evidence.modalClickPosted = click.modalClickPosted;
                evidence.modalClickButton = click.modalClickButton;
            }
            return JsonUtility.ToJson(evidence);
        }

        public static string QueueUnsavedDialogButton(string scenarioId, string button, string delayMilliseconds)
        {
            ValidateScenario(scenarioId);
            var window = Resources.FindObjectsOfTypeAll<UPilotUnsavedWindowFixture>()
                .Single(item => item != null && item.scenario == scenarioId);
            if (!UPilotWindowDiagnostics.TryGetMappedWindowHandle(window, true, out var owner, out string mapping))
                throw new InvalidOperationException("Fixture HWND mapping failed: " + mapping);
            int delay = Math.Max(0, Math.Min(10000, int.Parse(delayMilliseconds)));
            QueueModalButton(owner, TitleFor(scenarioId) + " - " + L10n.Tr("Unsaved Changes Detected"), button, delay,
                Path.Combine(DirectoryFor(scenarioId), "modal-click.json"), scenarioId);
            return JsonUtility.ToJson(new NativeWindowEvidence
            {
                scenario = scenarioId,
                instanceId = UPilotEntityIds.ToWireId(window).ToString(),
                title = window.titleContent.text,
                processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                lastAction = "modal-click-queued",
                modalClickButton = button,
            });
        }

        public override void SaveChanges()
        {
            var evidence = ReadEvidence(scenario);
            evidence.saveCalls++;
            evidence.value = "saved";
            evidence.lastAction = "save";
            WriteEvidence(scenario, evidence);
            hasUnsavedChanges = false;
            base.SaveChanges();
        }

        public override void DiscardChanges()
        {
            var evidence = ReadEvidence(scenario);
            evidence.discardCalls++;
            evidence.lastAction = "discard";
            WriteEvidence(scenario, evidence);
            hasUnsavedChanges = false;
            base.DiscardChanges();
        }

        private void OnDestroy()
        {
            if (string.IsNullOrEmpty(scenario)) return;
            var evidence = ReadEvidence(scenario);
            evidence.destroyCalls++;
            evidence.lastAction = evidence.lastAction == "opened" ? "force-close" : evidence.lastAction;
            WriteEvidence(scenario, evidence);
        }

        private void OnGUI()
        {
            GUILayout.Label("UPilot native unsaved-window acceptance fixture.");
        }

        private static void WriteEvidence(string scenarioId, NativeWindowEvidence evidence)
        {
            string path = EvidencePath(scenarioId);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(evidence, true));
        }

        private static NativeWindowEvidence ReadEvidence(string scenarioId)
        {
            string path = EvidencePath(scenarioId);
            return File.Exists(path)
                ? JsonUtility.FromJson<NativeWindowEvidence>(File.ReadAllText(path))
                : new NativeWindowEvidence { scenario = scenarioId };
        }

        private static string TitleFor(string scenarioId) => "UPilot Unsaved " + scenarioId;
        private static string DirectoryFor(string scenarioId) => Path.Combine(
            Directory.GetParent(Application.dataPath).FullName, "Log", "P0P1", "NativeWindow", scenarioId);
        private static string EvidencePath(string scenarioId) => Path.Combine(DirectoryFor(scenarioId), "state.json");
        private static string RelativePath(string path) => path.Substring(Directory.GetParent(Application.dataPath).FullName.Length + 1).Replace('\\', '/');

        private static void ValidateScenario(string scenarioId)
        {
            if (string.IsNullOrWhiteSpace(scenarioId) || scenarioId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("scenarioId must be a safe non-empty file name.", nameof(scenarioId));
        }

        internal static void QueueModalButton(IntPtr owner, string title, string button, int delay, string evidencePath, string scenarioId)
        {
            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            var thread = new Thread(() =>
            {
                Thread.Sleep(delay);
                long deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 15000;
                bool posted = false;
                while (!posted && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < deadline)
                {
                    IntPtr candidate = IntPtr.Zero;
                    IntPtr buttonHandle = IntPtr.Zero;
                    int matches = 0;
                    EnumWindows((handle, _) =>
                    {
                        GetWindowThreadProcessId(handle, out uint actualPid);
                        if (actualPid != (uint)pid || !IsWindowVisible(handle) || ClassName(handle) != "#32770" || WindowText(handle) != title)
                            return true;
                        if (owner != IntPtr.Zero && !BelongsToTargetOwner(handle, owner)) return true;
                        IntPtr found = IntPtr.Zero;
                        int buttonMatches = 0;
                        EnumChildWindows(handle, (child, __) =>
                        {
                            if (IsWindowVisible(child) && ClassName(child) == "Button" && WindowText(child) == button)
                            {
                                found = child;
                                buttonMatches++;
                            }
                            return true;
                        }, IntPtr.Zero);
                        if (buttonMatches == 1)
                        {
                            candidate = handle;
                            buttonHandle = found;
                            matches++;
                        }
                        return true;
                    }, IntPtr.Zero);
                    if (matches == 1 && BelongsToTargetOwner(candidate, owner))
                        posted = PostMessage(buttonHandle, 0x00F5, IntPtr.Zero, IntPtr.Zero);
                    if (!posted) Thread.Sleep(50);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(evidencePath));
                File.WriteAllText(evidencePath,
                    $"{{\"scenario\":\"{scenarioId}\",\"processId\":{pid}," +
                    $"\"modalClickPosted\":{posted.ToString().ToLowerInvariant()}," +
                    $"\"modalClickButton\":\"{button}\"," +
                    $"\"lastAction\":\"{(posted ? "modal-click-posted" : "modal-click-not-posted")}\"}}");
            }) { IsBackground = true, Name = "UPilot P1 modal fixture" };
            thread.Start();
        }

        private static bool IsOwnedBy(IntPtr handle, IntPtr owner)
        {
            if (owner == IntPtr.Zero || handle == owner) return false;
            for (int depth = 0; handle != IntPtr.Zero && depth < 16; depth++)
            {
                handle = GetWindow(handle, 4);
                if (handle == owner) return true;
            }
            return false;
        }

        private static bool BelongsToTargetOwner(IntPtr handle, IntPtr owner)
        {
            if (IsOwnedBy(handle, owner)) return true;
            IntPtr modalRoot = OwnerRoot(handle);
            IntPtr targetRoot = OwnerRoot(owner);
            return modalRoot != IntPtr.Zero && modalRoot == targetRoot;
        }

        private static IntPtr OwnerRoot(IntPtr handle)
        {
            IntPtr root = handle;
            for (int depth = 0; root != IntPtr.Zero && depth < 16; depth++)
            {
                IntPtr next = GetWindow(root, 4);
                if (next == IntPtr.Zero) return root;
                root = next;
            }
            return IntPtr.Zero;
        }

        private static string WindowText(IntPtr handle)
        {
            var value = new StringBuilder(512);
            SendMessageTimeout(handle, 0x000D, new IntPtr(value.Capacity), value, 0x0002, 100, out _);
            return value.ToString();
        }

        private static string ClassName(IntPtr handle)
        {
            var value = new StringBuilder(128);
            GetClassName(handle, value, value.Capacity);
            return value.ToString();
        }

        private delegate bool EnumProc(IntPtr handle, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr handle, EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder text, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr handle, uint message, IntPtr wParam, StringBuilder text, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    }

    public sealed class UPilotGenericMenuWindowFixture : EditorWindow
    {
        private static int selectedCount;

        public static string Open()
        {
            var window = CreateInstance<UPilotGenericMenuWindowFixture>();
            window.titleContent = new GUIContent("UPilot GenericMenu Fixture");
            window.position = new Rect(620, 160, 360, 220);
            window.ShowUtility();
            window.Focus();
            selectedCount = 0;
            return $"{{\"instanceId\":\"{UPilotEntityIds.ToWireId(window)}\",\"title\":\"{window.titleContent.text}\"}}";
        }

        public static string Status() => $"{{\"selectedCount\":{selectedCount},\"openCount\":{Resources.FindObjectsOfTypeAll<UPilotGenericMenuWindowFixture>().Count(item => item != null)}}}";

        private void OnGUI()
        {
            GUILayout.Label("Right-click inside this window to open the acceptance menu.");
            if (Event.current.type == EventType.MouseDown && Event.current.button == 1)
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Fixture/Select"), false, () => selectedCount++);
                menu.ShowAsContext();
                Event.current.Use();
            }
        }
    }

    public static class UPilotNativeModalFixture
    {
        private const string EvidenceKey = "UPilot.P1.NativeModal.Evidence";

        [MenuItem("Window/UPilot P1/Expected Modal")]
        private static void ExpectedModal()
        {
            int count = SessionState.GetInt(EvidenceKey + ".ExpectedCalls", 0) + 1;
            SessionState.SetInt(EvidenceKey + ".ExpectedCalls", count);
            bool accepted = EditorUtility.DisplayDialog("UPilot P1 Expected Modal", "Expected modal acceptance fixture.", "Proceed", "Cancel");
            SessionState.SetString(EvidenceKey + ".ExpectedResult", accepted ? "Proceed" : "Cancel");
        }

        [MenuItem("Window/UPilot P1/Unknown Modal")]
        private static void UnknownModal()
        {
            int count = SessionState.GetInt(EvidenceKey + ".UnknownCalls", 0) + 1;
            SessionState.SetInt(EvidenceKey + ".UnknownCalls", count);
            IntPtr owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            string path = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Log", "P0P1", "NativeModal", "unknown-click.json");
            UPilotUnsavedWindowFixture.QueueModalButton(owner, "UPilot P1 Unknown Modal", "Cancel", 15000, path, "unknown-modal");
            bool accepted = EditorUtility.DisplayDialog("UPilot P1 Unknown Modal", "Unknown modal must not be auto-confirmed.", "Proceed", "Cancel");
            SessionState.SetString(EvidenceKey + ".UnknownResult", accepted ? "Proceed" : "Cancel");
        }

        public static string Reset()
        {
            SessionState.SetInt(EvidenceKey + ".ExpectedCalls", 0);
            SessionState.SetInt(EvidenceKey + ".UnknownCalls", 0);
            SessionState.SetString(EvidenceKey + ".ExpectedResult", "");
            SessionState.SetString(EvidenceKey + ".UnknownResult", "");
            return Status();
        }

        public static string Status() =>
            $"{{\"expectedCalls\":{SessionState.GetInt(EvidenceKey + ".ExpectedCalls", 0)}," +
            $"\"unknownCalls\":{SessionState.GetInt(EvidenceKey + ".UnknownCalls", 0)}," +
            $"\"expectedResult\":\"{SessionState.GetString(EvidenceKey + ".ExpectedResult", "")}\"," +
            $"\"unknownResult\":\"{SessionState.GetString(EvidenceKey + ".UnknownResult", "")}\"}}";
    }

    public sealed class UPilotProfilerAcceptanceWindow : EditorWindow
    {
        internal string fixtureLabel;

        internal void SampleOnGuiTelemetry()
        {
            using (UPilotWindowTelemetry.OnGUI(this))
            {
                string label = fixtureLabel ?? "UPilot Profiler acceptance window";
                if (Event.current != null) GUILayout.Label(label);
                else GC.KeepAlive(label);
            }
        }

        private void OnGUI() => SampleOnGuiTelemetry();
    }

    public static class UPilotProfilerAcceptanceFixture
    {
        [Serializable]
        private sealed class FixtureStatus
        {
            public bool active;
            public string mode;
            public bool playing;
            public long startedAt;
            public double measurementElapsedSec;
            public int updateCount;
            public double updatesPerSec;
            public int frameCount;
            public double framesPerSec;
            public int requestedRepaints;
            public string[] windowInstanceIds = Array.Empty<string>();
        }

        private static readonly List<UPilotProfilerAcceptanceWindow> Windows = new();
        private static string s_mode = "";
        private static long s_startedAt;
        private static double s_measurementStartedAt;
        private static double s_lastRepaintAt;
        private static int s_updateCount;
        private static int s_measurementStartFrame;
        private static int s_requestedRepaints;
        private static double s_repaintIntervalSec = 0.1;
        private static bool s_active;

        public static string Begin(string mode, string repaintHzText)
        {
            StopInternal();
            string normalized = (mode ?? "").Trim();
            if (normalized != "idleWindow" && normalized != "lightPlayerLoop" &&
                normalized != "fixed20ms" && normalized != "dualWindow")
                throw new ArgumentException("mode must be idleWindow, lightPlayerLoop, fixed20ms or dualWindow.", nameof(mode));
            if ((normalized == "lightPlayerLoop" || normalized == "fixed20ms") && !EditorApplication.isPlaying)
                throw new InvalidOperationException(normalized + " requires PlayMode.");
            if ((normalized == "idleWindow" || normalized == "dualWindow") && EditorApplication.isPlaying)
                throw new InvalidOperationException(normalized + " requires EditMode.");

            double repaintHz = 10;
            if (!string.IsNullOrWhiteSpace(repaintHzText) &&
                !double.TryParse(repaintHzText, NumberStyles.Float, CultureInfo.InvariantCulture, out repaintHz))
                throw new ArgumentException("repaintHz must be numeric.", nameof(repaintHzText));
            repaintHz = Math.Max(1, Math.Min(60, repaintHz));
            s_repaintIntervalSec = 1d / repaintHz;
            s_mode = normalized;
            s_startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            s_lastRepaintAt = EditorApplication.timeSinceStartup;
            s_active = true;

            if (normalized == "idleWindow" || normalized == "dualWindow")
            {
                int count = normalized == "dualWindow" ? 2 : 1;
                for (int index = 0; index < count; index++)
                {
                    var window = EditorWindow.CreateWindow<UPilotProfilerAcceptanceWindow>();
                    window.fixtureLabel = normalized + " window " + (index + 1);
                    window.titleContent = new GUIContent("UPilot Profiler " + normalized + " " + (index + 1));
                    window.position = new Rect(180 + index * 400, 180, 360, 220);
                    window.Show();
                    window.Focus();
                    window.Repaint();
                    Windows.Add(window);
                }
            }

            ResetMeasurementInternal();
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            return Status();
        }

        public static string ResetMeasurement()
        {
            if (!s_active) throw new InvalidOperationException("Profiler acceptance fixture is not active.");
            ResetMeasurementInternal();
            return Status();
        }

        public static string Status()
        {
            double elapsed = s_active ? Math.Max(0, EditorApplication.timeSinceStartup - s_measurementStartedAt) : 0;
            int frameCount = s_active && EditorApplication.isPlaying
                ? Math.Max(0, Time.frameCount - s_measurementStartFrame) : 0;
            return JsonUtility.ToJson(new FixtureStatus
            {
                active = s_active,
                mode = s_mode,
                playing = EditorApplication.isPlaying,
                startedAt = s_startedAt,
                measurementElapsedSec = elapsed,
                updateCount = s_updateCount,
                updatesPerSec = elapsed > 0 ? s_updateCount / elapsed : 0,
                frameCount = frameCount,
                framesPerSec = elapsed > 0 ? frameCount / elapsed : 0,
                requestedRepaints = s_requestedRepaints,
                windowInstanceIds = Windows.Where(item => item != null)
                    .Select(item => UPilotEntityIds.ToWireId(item).ToString()).ToArray(),
            });
        }

        public static string Stop()
        {
            string result = Status();
            StopInternal();
            return result;
        }

        private static void Tick()
        {
            if (!s_active) return;
            if (s_mode == "fixed20ms") Thread.Sleep(20);
            s_updateCount++;
            double now = EditorApplication.timeSinceStartup;
            if (Windows.Count == 0 || now - s_lastRepaintAt < s_repaintIntervalSec) return;
            s_lastRepaintAt = now;
            foreach (var window in Windows)
            {
                if (window == null) continue;
                UPilotWindowTelemetry.RequestRepaint(window);
                window.SampleOnGuiTelemetry();
                s_requestedRepaints++;
            }
        }

        private static void ResetMeasurementInternal()
        {
            s_measurementStartedAt = EditorApplication.timeSinceStartup;
            s_measurementStartFrame = Time.frameCount;
            s_updateCount = 0;
            s_requestedRepaints = 0;
        }

        private static void StopInternal()
        {
            EditorApplication.update -= Tick;
            foreach (var window in Windows.Where(item => item != null).ToArray()) window.Close();
            Windows.Clear();
            s_mode = "";
            s_startedAt = 0;
            s_measurementStartedAt = 0;
            s_lastRepaintAt = 0;
            s_updateCount = 0;
            s_measurementStartFrame = 0;
            s_requestedRepaints = 0;
            s_active = false;
        }
    }

    public static class UPilotWriteBatchHangAcceptanceFixture
    {
        private static bool s_armed;
        private static double s_executeAt;
        private static int s_durationMs;
        private static string s_evidencePath;

        public static string Arm(string delayMilliseconds, string durationMilliseconds, string evidenceId)
        {
            if (s_armed)
                throw new InvalidOperationException("A write-batch Hang fixture is already armed.");
            if (!Guid.TryParse(evidenceId, out _))
                throw new ArgumentException("evidenceId must be a GUID.", nameof(evidenceId));
            int delayMs = int.Parse(delayMilliseconds, CultureInfo.InvariantCulture);
            int durationMs = int.Parse(durationMilliseconds, CultureInfo.InvariantCulture);
            if (delayMs < 3000 || delayMs > 30000 || durationMs < 5000 || durationMs > 30000)
                throw new ArgumentOutOfRangeException("The fixture requires delay 3-30s and duration 5-30s.");

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            s_evidencePath = Path.Combine(projectRoot, "Artifacts", "UnifiedAcceptance", "WriteBatchHang", evidenceId + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(s_evidencePath));
            s_executeAt = EditorApplication.timeSinceStartup + delayMs / 1000.0;
            s_durationMs = durationMs;
            s_armed = true;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            return Status();
        }

        public static string Status()
        {
            if (!string.IsNullOrEmpty(s_evidencePath) && File.Exists(s_evidencePath))
                return File.ReadAllText(s_evidencePath);
            return $"{{\"armed\":{s_armed.ToString().ToLowerInvariant()},\"durationMs\":{s_durationMs}}}";
        }

        private static void Tick()
        {
            if (!s_armed || EditorApplication.timeSinceStartup < s_executeAt)
                return;
            EditorApplication.update -= Tick;
            long startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            File.WriteAllText(s_evidencePath,
                $"{{\"armed\":true,\"startedAt\":{startedAt},\"endedAt\":0,\"durationMs\":{s_durationMs}}}");
            Thread.Sleep(s_durationMs);
            long endedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            File.WriteAllText(s_evidencePath,
                $"{{\"armed\":false,\"startedAt\":{startedAt},\"endedAt\":{endedAt},\"durationMs\":{s_durationMs}}}");
            s_armed = false;
        }
    }
}
